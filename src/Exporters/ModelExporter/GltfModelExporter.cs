using Assimp;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using AiMesh = Assimp.Mesh;
using AiMaterial = Assimp.Material;

namespace GameAssetExplorer.Exporters.ModelExporter;

/// <summary>
/// glTF model exporter (binary .glb) built on AssimpNet — self-contained, and both Blender and
/// UE5 import it natively with full mesh / UVs / normals / per-submesh materials. This is the
/// default model format: Assimp can write glTF (unlike FBX), and .glb is a single embedded file.
///
/// Coordinate system matches the OBJ/FBX exporters: when
/// <see cref="ExportSettings.ApplyBlenderBoneCorrection"/> is set, UE (Z-up) is converted to the
/// Y-up convention glTF expects (x, z, -y). Skeleton nodes are emitted for skeletal meshes;
/// skin weights aren't carried by <see cref="LodData"/> yet, so the mesh isn't bound to them.
/// </summary>
public class GltfModelExporter : IExporter
{
    public string ExporterName => "glTF Model Exporter";
    public IReadOnlyList<AssetType> SupportedTypes => new[] { AssetType.StaticMesh, AssetType.SkeletalMesh };
    public IReadOnlyList<string> OutputExtensions => new[] { ".gltf", ".bin" };

    public async Task<ExportResult> ExportAsync(
        AssetData assetData, string outputDirectory, ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        if (assetData is not MeshAssetData mesh)
            return Fail(assetData, "glTF exporter only handles MeshAssetData.");
        if (mesh.Lods.Count == 0 || mesh.Lods[0].VertexBuffer == null)
            return Fail(assetData, "Mesh has no geometry data.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var meshName = SanitizeFileName(mesh.Info.Name);
            // glTF 2.0 (JSON .gltf + .bin buffer). Assimp's "glb" is glTF 1.0, which Blender
            // rejects ("GLB version must be 2"); "gltf2" is the 2.0 exporter Blender/UE accept.
            var outPath  = Path.Combine(outputDirectory, meshName + ".gltf");

            int lodIdx = settings.ModelLodLevel >= 0 && settings.ModelLodLevel < mesh.Lods.Count
                ? settings.ModelLodLevel : 0;
            var lod = mesh.Lods[lodIdx];

            await Task.Run(() =>
            {
                var scene = BuildScene(meshName, mesh, lod, settings);
                using var ctx = new AssimpContext();
                if (!ctx.ExportFile(scene, outPath, "gltf2"))
                    throw new Exception("Assimp glTF2 export returned false.");
            });

            sw.Stop();
            return new ExportResult
            {
                Success = true, OutputPath = outPath, SourceAsset = assetData.Info,
                FileSizeBytes = new FileInfo(outPath).Length, Duration = sw.Elapsed
            };
        }
        catch (Exception ex) { return Fail(assetData, ex.Message); }
    }

    public async Task<IReadOnlyList<ExportResult>> ExportBatchAsync(
        IReadOnlyList<AssetData> assets, string outputDirectory, ExportSettings settings,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<ExportResult>();
        for (int i = 0; i < assets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ExportProgress { Total = assets.Count, Completed = i, CurrentAsset = assets[i].Info.Name });
            results.Add(await ExportAsync(assets[i], outputDirectory, settings));
        }
        return results;
    }

    private static Scene BuildScene(string meshName, MeshAssetData mesh, LodData lod, ExportSettings settings)
    {
        var scene = new Scene { RootNode = new Node("RootNode") };
        var vb = lod.VertexBuffer!;
        var ib = lod.IndexBuffer;
        int nv = lod.VertexCount;
        int ni = ib != null ? ib.Length / 4 : 0;
        bool hasUv = lod.UvBuffer != null;
        float scale = settings.ModelScaleFactor <= 0 ? 1f : settings.ModelScaleFactor;
        bool convert = settings.ApplyBlenderBoneCorrection;

        var submeshes = lod.Submeshes.Count > 0
            ? lod.Submeshes
            : new List<SubmeshInfo> { new() { Name = meshName, VertexStart = 0, VertexCount = nv, IndexStart = 0, IndexCount = ni } };

        var meshNode = new Node(meshName);

        for (int s = 0; s < submeshes.Count; s++)
        {
            var sub = submeshes[s];
            if (sub.VertexCount <= 0 || sub.IndexCount <= 0) continue;

            string matName = SanitizeName(s < mesh.MaterialSlots.Count ? mesh.MaterialSlots[s].MaterialName : null, sub.Name, s);
            int matIndex = scene.MaterialCount;
            scene.Materials.Add(new AiMaterial { Name = matName });

            var aiMesh = new AiMesh(SanitizeName(sub.Name, meshName, s), PrimitiveType.Triangle) { MaterialIndex = matIndex };

            for (int v = 0; v < sub.VertexCount; v++)
            {
                int gi = sub.VertexStart + v, off = gi * 12;
                if (off + 12 > vb.Length) break;
                float x = BitConverter.ToSingle(vb, off) * scale;
                float y = BitConverter.ToSingle(vb, off + 4) * scale;
                float z = BitConverter.ToSingle(vb, off + 8) * scale;
                aiMesh.Vertices.Add(convert ? new Vector3D(x, z, -y) : new Vector3D(x, y, z));
                if (hasUv && lod.UvBuffer!.Length >= (gi + 1) * 8)
                {
                    float u = BitConverter.ToSingle(lod.UvBuffer, gi * 8);
                    float t = BitConverter.ToSingle(lod.UvBuffer, gi * 8 + 4);
                    aiMesh.TextureCoordinateChannels[0].Add(new Vector3D(u, 1f - t, 0f));
                }
            }
            if (hasUv) aiMesh.UVComponentCount[0] = 2;

            var normals = new Vector3D[aiMesh.VertexCount];
            int end = sub.IndexStart + sub.IndexCount;
            for (int i = sub.IndexStart; i + 2 < end && i + 2 < ni; i += 3)
            {
                int a = BitConverter.ToInt32(ib!, i * 4) - sub.VertexStart;
                int b = BitConverter.ToInt32(ib!, (i + 1) * 4) - sub.VertexStart;
                int c = BitConverter.ToInt32(ib!, (i + 2) * 4) - sub.VertexStart;
                if ((uint)a >= (uint)aiMesh.VertexCount || (uint)b >= (uint)aiMesh.VertexCount || (uint)c >= (uint)aiMesh.VertexCount) continue;
                aiMesh.Faces.Add(new Face(new[] { a, b, c }));
                var fn = Cross(Sub(aiMesh.Vertices[b], aiMesh.Vertices[a]), Sub(aiMesh.Vertices[c], aiMesh.Vertices[a]));
                normals[a] = Add(normals[a], fn); normals[b] = Add(normals[b], fn); normals[c] = Add(normals[c], fn);
            }
            if (aiMesh.FaceCount == 0) continue;
            foreach (var n in normals) aiMesh.Normals.Add(Normalize(n));

            meshNode.MeshIndices.Add(scene.MeshCount);
            scene.Meshes.Add(aiMesh);
        }

        scene.RootNode.Children.Add(meshNode);
        if (mesh.Skeleton is { Bones.Count: > 0 } skel)
            scene.RootNode.Children.Add(BuildSkeleton(skel, scale, convert));
        return scene;
    }

    private static Node BuildSkeleton(SkeletonData skel, float scale, bool convert)
    {
        var armature = new Node("Armature");
        var nodes = new Node[skel.Bones.Count];
        for (int i = 0; i < skel.Bones.Count; i++)
        {
            var b = skel.Bones[i];
            float x = b.Position[0] * scale, y = b.Position[1] * scale, z = b.Position[2] * scale;
            var t = convert ? new Vector3D(x, z, -y) : new Vector3D(x, y, z);
            nodes[i] = new Node(string.IsNullOrEmpty(b.Name) ? $"bone_{i}" : b.Name) { Transform = Matrix4x4.FromTranslation(t) };
        }
        for (int i = 0; i < skel.Bones.Count; i++)
        {
            int p = skel.Bones[i].ParentIndex;
            if (p >= 0 && p < nodes.Length) nodes[p].Children.Add(nodes[i]);
            else armature.Children.Add(nodes[i]);
        }
        return armature;
    }

    private static Vector3D Sub(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vector3D Add(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Vector3D Cross(Vector3D a, Vector3D b) => new(a.Y*b.Z - a.Z*b.Y, a.Z*b.X - a.X*b.Z, a.X*b.Y - a.Y*b.X);
    private static Vector3D Normalize(Vector3D v)
    {
        float len = (float)Math.Sqrt(v.X*v.X + v.Y*v.Y + v.Z*v.Z);
        return len > 1e-8f ? new Vector3D(v.X/len, v.Y/len, v.Z/len) : new Vector3D(0, 1, 0);
    }

    private static string SanitizeName(string? primary, string? fallback, int index)
    {
        var n = !string.IsNullOrWhiteSpace(primary) ? primary : !string.IsNullOrWhiteSpace(fallback) ? fallback : $"submesh_{index}";
        return n!.Replace(' ', '_').Replace('/', '_').Replace('\\', '_').Replace(':', '_');
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static ExportResult Fail(AssetData asset, string message) =>
        new() { Success = false, ErrorMessage = message, SourceAsset = asset.Info };
}
