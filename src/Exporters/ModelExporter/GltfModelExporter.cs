using Assimp;
using GameAssetExplorer.Core.Animation;
using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using GameAssetExplorer.Core.Services;
using AiMesh = Assimp.Mesh;
using AiMaterial = Assimp.Material;
using AiBone = Assimp.Bone;
using AiAnimation = Assimp.Animation;
using AiQuaternion = Assimp.Quaternion;
using NumMatrix = System.Numerics.Matrix4x4;
using NumVector3 = System.Numerics.Vector3;

namespace GameAssetExplorer.Exporters.ModelExporter;

/// <summary>
/// glTF model exporter (binary .glb) built on AssimpNet — self-contained, and both Blender and
/// UE5 import it natively with full mesh / UVs / normals / per-submesh materials. This is the
/// default model format: Assimp can write glTF (unlike FBX), and .glb is a single embedded file.
///
/// Coordinate system matches the OBJ/FBX exporters: when
/// <see cref="ExportSettings.ApplyBlenderBoneCorrection"/> is set, UE (Z-up) is converted to the
/// Y-up convention glTF expects (x, z, -y).
///
/// Skinning and animation are exported so that what plays in the viewport is what appears in
/// Blender and UE. The coordinate conversion is a similarity transform <c>Xf</c>, so every
/// matrix — bone locals, inverse binds, animation keys — is carried across as
/// <c>Xf⁻¹ · M · Xf</c> rather than having its translation converted in isolation. Converting
/// only the translation (which is what this exporter used to do for bones) yields a skeleton
/// that looks right in bind pose and deforms wrongly the moment it moves.
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

        // Native → export space. Applied to points as p · Xf, and to any matrix by conjugation.
        var xf = BuildExportTransform(scale, convert);
        if (!NumMatrix.Invert(xf, out var xfInv)) xfInv = NumMatrix.Identity;

        var submeshes = lod.Submeshes.Count > 0
            ? lod.Submeshes
            : new List<SubmeshInfo> { new() { Name = meshName, VertexStart = 0, VertexCount = nv, IndexStart = 0, IndexCount = ni } };

        var meshNode = new Node(meshName);
        // Which AiMesh came from which submesh — the skin pass needs the mapping to translate
        // LOD-space vertex indices into per-mesh ones.
        var built = new List<(AiMesh Mesh, SubmeshInfo Sub)>();

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
                var p = NumVector3.Transform(new NumVector3(
                    BitConverter.ToSingle(vb, off),
                    BitConverter.ToSingle(vb, off + 4),
                    BitConverter.ToSingle(vb, off + 8)), xf);
                aiMesh.Vertices.Add(new Vector3D(p.X, p.Y, p.Z));
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
            built.Add((aiMesh, sub));
        }

        scene.RootNode.Children.Add(meshNode);

        if (mesh.Skeleton is { Bones.Count: > 0 } skel)
        {
            // Local bind transforms, conjugated into export space once and reused by the node
            // hierarchy, the inverse binds and the animation channels.
            var localExport = new NumMatrix[skel.Bones.Count];
            for (int i = 0; i < skel.Bones.Count; i++)
                localExport[i] = xfInv * SkeletonMath.LocalMatrix(skel.Bones[i]) * xf;

            scene.RootNode.Children.Add(BuildSkeleton(skel, localExport, out var boneNames));

            if (settings.ExportSkinWeights && lod.Skin is { } skin)
                AttachSkin(built, skin, skel, boneNames, localExport, settings, mesh.Info.Name);

            if (settings.ExportAnimations && mesh.Animations.Count > 0)
                AttachAnimations(scene, mesh, skel, boneNames, xf, xfInv);
        }

        return scene;
    }

    /// <summary>
    /// Native → export space as a row-vector matrix: uniform scale, plus the Z-up → Y-up
    /// rotation (x, y, z) → (x, z, −y) when the Blender/UE correction is enabled.
    /// </summary>
    private static NumMatrix BuildExportTransform(float scale, bool convert)
    {
        if (!convert) return NumMatrix.CreateScale(scale);
        var m = new NumMatrix
        {
            M11 = scale, M12 = 0,     M13 = 0,
            M21 = 0,     M22 = 0,     M23 = -scale,
            M31 = 0,     M32 = scale, M33 = 0,
            M44 = 1,
        };
        return m;
    }

    /// <summary>Build the bone node tree with full local transforms (not translation only).</summary>
    private static Node BuildSkeleton(SkeletonData skel, NumMatrix[] localExport, out string[] boneNames)
    {
        var armature = new Node("Armature");
        var nodes = new Node[skel.Bones.Count];
        boneNames = new string[skel.Bones.Count];

        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < skel.Bones.Count; i++)
        {
            // glTF channels and skin joints are matched by node name, so duplicates in the
            // source skeleton have to be made unique or several bones collapse into one.
            string name = string.IsNullOrEmpty(skel.Bones[i].Name) ? $"bone_{i}" : skel.Bones[i].Name;
            if (!used.Add(name))
            {
                string candidate;
                int n = 1;
                do { candidate = $"{name}_{n++}"; } while (!used.Add(candidate));
                name = candidate;
            }
            boneNames[i] = name;
            nodes[i] = new Node(name) { Transform = ToAssimp(localExport[i]) };
        }

        for (int i = 0; i < skel.Bones.Count; i++)
        {
            int p = skel.Bones[i].ParentIndex;
            if (p >= 0 && p < nodes.Length && p != i) nodes[p].Children.Add(nodes[i]);
            else armature.Children.Add(nodes[i]);
        }
        return armature;
    }

    /// <summary>
    /// Bind each exported submesh to the skeleton: one <see cref="AiBone"/> per influencing
    /// joint, carrying that joint's inverse-bind matrix and its per-vertex weights.
    /// </summary>
    private static void AttachSkin(
        List<(AiMesh Mesh, SubmeshInfo Sub)> built, SkinBinding skin, SkeletonData skel,
        string[] boneNames, NumMatrix[] localExport, ExportSettings settings, string label)
    {
        // Inverse binds must come from the EXPORT-space locals so they invert the same
        // hierarchy the node tree describes.
        var worldExport = new NumMatrix[skel.Bones.Count];
        var resolved = new bool[skel.Bones.Count];
        for (int i = 0; i < skel.Bones.Count; i++)
            ResolveWorld(i, skel, localExport, worldExport, resolved, 0);

        var inverseBind = new NumMatrix[skel.Bones.Count];
        for (int i = 0; i < skel.Bones.Count; i++)
            inverseBind[i] = NumMatrix.Invert(worldExport[i], out var inv) ? inv : NumMatrix.Identity;

        int cap = Math.Clamp(settings.MaxInfluencesPerVertex, 1, 12);
        int inf = Math.Max(skin.InfluencesPerVertex, 1);
        double droppedWeight = 0;
        int droppedCount = 0;

        // Scratch rows, allocated once: stack-allocating these per vertex would blow the stack
        // on a 50k-vertex mesh.
        var idx = new int[12];
        var wgt = new float[12];

        foreach (var (aiMesh, sub) in built)
        {
            var bones = new Dictionary<int, AiBone>();

            for (int v = 0; v < aiMesh.VertexCount; v++)
            {
                int gi = sub.VertexStart + v;
                if (gi >= skin.VertexCount) continue;
                int row = gi * inf;

                // Keep the heaviest `cap` influences and renormalise, so a vertex still sums
                // to 1 after truncation instead of shrinking toward the origin.
                int n = 0;
                for (int k = 0; k < inf && k < 12; k++)
                {
                    float w = skin.BoneWeights[row + k];
                    if (w <= 0f) continue;
                    idx[n] = skin.BoneIndices[row + k];
                    wgt[n] = w;
                    n++;
                }
                if (n == 0) continue;

                // Selection sort by descending weight — n is at most 12.
                for (int a = 0; a < n - 1; a++)
                {
                    int best = a;
                    for (int b = a + 1; b < n; b++) if (wgt[b] > wgt[best]) best = b;
                    if (best == a) continue;
                    (wgt[a], wgt[best]) = (wgt[best], wgt[a]);
                    (idx[a], idx[best]) = (idx[best], idx[a]);
                }

                int keep = Math.Min(n, cap);
                float kept = 0;
                for (int k = 0; k < keep; k++) kept += wgt[k];
                for (int k = keep; k < n; k++)
                {
                    if (wgt[k] > 0.05f) { droppedWeight += wgt[k]; droppedCount++; }
                }
                if (kept <= 1e-6f) continue;

                for (int k = 0; k < keep; k++)
                {
                    int bone = idx[k];
                    if (bone >= skel.Bones.Count) continue;
                    if (!bones.TryGetValue(bone, out var aiBone))
                    {
                        aiBone = new AiBone
                        {
                            Name         = boneNames[bone],
                            OffsetMatrix = ToAssimp(inverseBind[bone]),
                        };
                        bones[bone] = aiBone;
                    }
                    aiBone.VertexWeights.Add(new VertexWeight(v, wgt[k] / kept));
                }
            }

            foreach (var b in bones.Values) aiMesh.Bones.Add(b);
        }

        if (droppedCount > 0)
            Log.Warn($"GltfModelExporter[{label}]: capped skinning at {cap} influences/vertex — " +
                     $"dropped {droppedCount} influence(s) with weight > 0.05 " +
                     $"(total weight {droppedWeight:F2}). Raise ExportSettings.MaxInfluencesPerVertex " +
                     "if the deformation differs from the viewport.");
    }

    private static void ResolveWorld(int i, SkeletonData skel, NumMatrix[] local,
                                     NumMatrix[] world, bool[] done, int depth)
    {
        if (done[i]) return;
        done[i] = true;
        int p = skel.Bones[i].ParentIndex;
        if (p >= 0 && p < world.Length && p != i && depth < 256)
        {
            ResolveWorld(p, skel, local, world, done, depth + 1);
            world[i] = local[i] * world[p];
        }
        else world[i] = local[i];
    }

    /// <summary>
    /// Bake every clip into Assimp animation channels. Keys are sampled per frame from the
    /// same <see cref="AnimationSampler"/> the viewport uses, so the exported motion is the
    /// motion that was previewed — not a second interpretation of the source data.
    /// </summary>
    private static void AttachAnimations(Scene scene, MeshAssetData mesh, SkeletonData skel,
                                         string[] boneNames, NumMatrix xf, NumMatrix xfInv)
    {
        foreach (var clip in mesh.Animations)
        {
            if (clip.FrameCount <= 0 || clip.Tracks.Count == 0) continue;

            var sampler = new AnimationSampler(skel, clip);
            if (sampler.BoundTrackCount == 0) continue;

            float fps = clip.FrameRate > 0.01f ? clip.FrameRate : 30f;
            int frames = Math.Max(clip.FrameCount, 1);

            var anim = new AiAnimation
            {
                Name           = string.IsNullOrEmpty(clip.ClipName) ? clip.Info.Name : clip.ClipName,
                TicksPerSecond = fps,
                DurationInTicks = frames - 1,
            };

            var channels = new NodeAnimationChannel[skel.Bones.Count];
            for (int b = 0; b < skel.Bones.Count; b++)
                channels[b] = new NodeAnimationChannel { NodeName = boneNames[b] };

            var scratch = new NumMatrix[skel.Bones.Count];
            for (int f = 0; f < frames; f++)
            {
                var local = sampler.SampleLocal(f / fps, scratch);
                for (int b = 0; b < skel.Bones.Count && b < local.Length; b++)
                {
                    var m = xfInv * local[b] * xf;
                    if (!NumMatrix.Decompose(m, out var sc, out var rot, out var tr))
                    {
                        sc  = NumVector3.One;
                        rot = System.Numerics.Quaternion.Identity;
                        tr  = m.Translation;
                    }
                    channels[b].PositionKeys.Add(new VectorKey(f, new Vector3D(tr.X, tr.Y, tr.Z)));
                    channels[b].RotationKeys.Add(new QuaternionKey(f, new AiQuaternion(rot.W, rot.X, rot.Y, rot.Z)));
                    channels[b].ScalingKeys.Add(new VectorKey(f, new Vector3D(sc.X, sc.Y, sc.Z)));
                }
            }

            foreach (var ch in channels)
                if (ch.PositionKeyCount > 0) anim.NodeAnimationChannels.Add(ch);

            if (anim.NodeAnimationChannelCount > 0) scene.Animations.Add(anim);
        }
    }

    /// <summary>
    /// System.Numerics (row-vector, translation in the fourth ROW) → Assimp (column-vector,
    /// translation in the fourth COLUMN). That is exactly a transpose.
    /// </summary>
    private static Matrix4x4 ToAssimp(NumMatrix m) => new(
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44);

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
