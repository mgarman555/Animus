using GameAssetExplorer.Core.Interfaces;
using GameAssetExplorer.Core.Models;
using System.Globalization;
using System.Text;

namespace GameAssetExplorer.Exporters.ModelExporter;

/// <summary>
/// Self-contained FBX 7.4 ASCII exporter (no external SDK — AssimpNet can't write FBX).
/// Emits one Geometry+Model+Material per submesh, with positions, per-vertex normals and UVs,
/// validated by re-importing through Assimp.
///
/// Coordinate system matches the OBJ exporter: when <see cref="ExportSettings.ApplyBlenderBoneCorrection"/>
/// is set, UE (left-handed, Z-up) is converted to Y-up (x, z, -y); the file is tagged Y-up / cm.
///
/// Not yet emitted: skin weights (our <see cref="LodData"/> doesn't carry them), so the mesh isn't
/// bound to a skeleton. Geometry, UVs, normals and per-submesh materials are all present.
/// </summary>
public class FbxModelExporter : IExporter
{
    public string ExporterName => "FBX Model Exporter";
    public IReadOnlyList<AssetType> SupportedTypes => new[] { AssetType.StaticMesh, AssetType.SkeletalMesh };
    public IReadOnlyList<string> OutputExtensions => new[] { ".fbx" };

    public async Task<ExportResult> ExportAsync(
        AssetData assetData, string outputDirectory, ExportSettings settings,
        IProgress<ExportProgress>? progress = null)
    {
        if (assetData is not MeshAssetData mesh)
            return Fail(assetData, "FBX exporter only handles MeshAssetData.");
        if (mesh.Lods.Count == 0 || mesh.Lods[0].VertexBuffer == null)
            return Fail(assetData, "Mesh has no geometry data.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var meshName = SanitizeFileName(mesh.Info.Name);
            var fbxPath  = Path.Combine(outputDirectory, meshName + ".fbx");

            int lodIdx = settings.ModelLodLevel >= 0 && settings.ModelLodLevel < mesh.Lods.Count
                ? settings.ModelLodLevel : 0;
            var lod = mesh.Lods[lodIdx];

            await Task.Run(() => File.WriteAllText(fbxPath, BuildFbx(meshName, mesh, lod, settings)));

            sw.Stop();
            return new ExportResult
            {
                Success = true, OutputPath = fbxPath, SourceAsset = assetData.Info,
                FileSizeBytes = new FileInfo(fbxPath).Length, Duration = sw.Elapsed
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

    // ─── FBX 7.4 ASCII writer ───────────────────────────────────────────────────

    private static string BuildFbx(string meshName, MeshAssetData mesh, LodData lod, ExportSettings settings)
    {
        var vb = lod.VertexBuffer!;
        var ib = lod.IndexBuffer!;
        int ni = ib.Length / 4;
        bool hasUv = lod.UvBuffer != null;
        float scale = settings.ModelScaleFactor <= 0 ? 1f : settings.ModelScaleFactor;
        bool convert = settings.ApplyBlenderBoneCorrection;

        var submeshes = lod.Submeshes.Count > 0
            ? lod.Submeshes
            : new List<SubmeshInfo> { new() { Name = meshName, VertexStart = 0, VertexCount = lod.VertexCount, IndexStart = 0, IndexCount = ni } };

        long nextId = 2000000;
        var objs = new StringBuilder();
        var cons = new StringBuilder();
        int geom = 0, model = 0, matc = 0;

        for (int s = 0; s < submeshes.Count; s++)
        {
            var sub = submeshes[s];
            if (sub.VertexCount <= 0 || sub.IndexCount <= 0) continue;
            int vc = sub.VertexCount;

            // Positions (converted + scaled)
            var pos = new double[vc * 3];
            for (int v = 0; v < vc; v++)
            {
                int off = (sub.VertexStart + v) * 12;
                if (off + 12 > vb.Length) { vc = v; break; }
                float x = BitConverter.ToSingle(vb, off) * scale;
                float y = BitConverter.ToSingle(vb, off + 4) * scale;
                float z = BitConverter.ToSingle(vb, off + 8) * scale;
                if (convert) { pos[v * 3] = x; pos[v * 3 + 1] = z; pos[v * 3 + 2] = -y; }
                else { pos[v * 3] = x; pos[v * 3 + 1] = y; pos[v * 3 + 2] = z; }
            }

            // Faces (local, rebased) + normal accumulation
            var faces = new List<int>(sub.IndexCount);
            var nrm = new double[vc * 3];
            int end = sub.IndexStart + sub.IndexCount;
            for (int i = sub.IndexStart; i + 2 < end && i + 2 < ni; i += 3)
            {
                int a = BitConverter.ToInt32(ib, i * 4) - sub.VertexStart;
                int b = BitConverter.ToInt32(ib, (i + 1) * 4) - sub.VertexStart;
                int c = BitConverter.ToInt32(ib, (i + 2) * 4) - sub.VertexStart;
                if ((uint)a >= (uint)vc || (uint)b >= (uint)vc || (uint)c >= (uint)vc) continue;
                faces.Add(a); faces.Add(b); faces.Add(c);
                AccumNormal(pos, nrm, a, b, c);
            }
            if (faces.Count == 0) continue;
            NormalizeAll(nrm);

            // UVs (per vertex, V flipped to match OBJ convention)
            double[]? uv = null;
            if (hasUv)
            {
                uv = new double[vc * 2];
                for (int v = 0; v < vc; v++)
                {
                    int o = (sub.VertexStart + v) * 8;
                    if (o + 8 > lod.UvBuffer!.Length) break;
                    uv[v * 2] = BitConverter.ToSingle(lod.UvBuffer, o);
                    uv[v * 2 + 1] = 1f - BitConverter.ToSingle(lod.UvBuffer, o + 4);
                }
            }

            long geomId = ++nextId, modelId = ++nextId, matId = ++nextId;
            string nm = SanitizeName(sub.Name, meshName, s);
            string matName = SanitizeName(s < mesh.MaterialSlots.Count ? mesh.MaterialSlots[s].MaterialName : null, nm + "_mat", s);

            // Geometry
            objs.Append($"\tGeometry: {geomId}, \"Geometry::{nm}\", \"Mesh\" {{\n");
            objs.Append($"\t\tVertices: *{vc * 3} {{\n\t\t\ta: ").Append(JoinNums(pos)).Append("\n\t\t}\n");
            objs.Append($"\t\tPolygonVertexIndex: *{faces.Count} {{\n\t\t\ta: ").Append(JoinPolyIndex(faces)).Append("\n\t\t}\n");
            objs.Append("\t\tGeometryVersion: 124\n");
            objs.Append("\t\tLayerElementNormal: 0 {\n\t\t\tVersion: 102\n\t\t\tName: \"\"\n\t\t\tMappingInformationType: \"ByVertice\"\n\t\t\tReferenceInformationType: \"Direct\"\n");
            objs.Append($"\t\t\tNormals: *{vc * 3} {{\n\t\t\t\ta: ").Append(JoinNums(nrm)).Append("\n\t\t\t}\n\t\t}\n");
            if (uv != null)
            {
                objs.Append("\t\tLayerElementUV: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"UVMap\"\n\t\t\tMappingInformationType: \"ByVertice\"\n\t\t\tReferenceInformationType: \"Direct\"\n");
                objs.Append($"\t\t\tUV: *{vc * 2} {{\n\t\t\t\ta: ").Append(JoinNums(uv)).Append("\n\t\t\t}\n\t\t}\n");
            }
            objs.Append("\t\tLayerElementMaterial: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"\"\n\t\t\tMappingInformationType: \"AllSame\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n\t\t\tMaterials: *1 {\n\t\t\t\ta: 0\n\t\t\t}\n\t\t}\n");
            objs.Append("\t\tLayer: 0 {\n\t\t\tVersion: 100\n\t\t\tLayerElement: {\n\t\t\t\tType: \"LayerElementNormal\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n");
            if (uv != null) objs.Append("\t\t\tLayerElement: {\n\t\t\t\tType: \"LayerElementUV\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n");
            objs.Append("\t\t\tLayerElement: {\n\t\t\t\tType: \"LayerElementMaterial\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n\t\t}\n\t}\n");

            // Model
            objs.Append($"\tModel: {modelId}, \"Model::{nm}\", \"Mesh\" {{\n\t\tVersion: 232\n\t\tProperties70: {{\n\t\t\tP: \"DefaultAttributeIndex\", \"int\", \"Integer\", \"\",0\n\t\t}}\n\t\tShading: Y\n\t\tCulling: \"CullingOff\"\n\t}}\n");

            // Material
            objs.Append($"\tMaterial: {matId}, \"Material::{matName}\", \"\" {{\n\t\tVersion: 102\n\t\tShadingModel: \"phong\"\n\t\tMultiLayer: 0\n\t\tProperties70: {{\n\t\t\tP: \"DiffuseColor\", \"Color\", \"\", \"A\",0.8,0.8,0.8\n\t\t}}\n\t}}\n");

            cons.Append($"\tC: \"OO\",{modelId},0\n");
            cons.Append($"\tC: \"OO\",{geomId},{modelId}\n");
            cons.Append($"\tC: \"OO\",{matId},{modelId}\n");
            geom++; model++; matc++;
        }

        int defCount = 1 + geom + model + matc;
        var sb = new StringBuilder(objs.Length + 4096);
        sb.Append("; FBX 7.4.0 project file\n; Created by Game Asset Explorer\n;-----------------------------------\n\n");
        sb.Append("FBXHeaderExtension:  {\n\tFBXHeaderVersion: 1003\n\tFBXVersion: 7400\n\tCreator: \"Game Asset Explorer\"\n}\n");
        sb.Append("GlobalSettings:  {\n\tVersion: 1000\n\tProperties70:  {\n");
        sb.Append("\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1\n\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1\n");
        sb.Append("\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2\n\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1\n");
        sb.Append("\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0\n\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1\n");
        sb.Append("\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",1\n\t}\n}\n\n");
        sb.Append($"Definitions:  {{\n\tVersion: 100\n\tCount: {defCount}\n");
        sb.Append("\tObjectType: \"GlobalSettings\" {\n\t\tCount: 1\n\t}\n");
        if (geom > 0) sb.Append($"\tObjectType: \"Geometry\" {{\n\t\tCount: {geom}\n\t}}\n");
        if (model > 0) sb.Append($"\tObjectType: \"Model\" {{\n\t\tCount: {model}\n\t}}\n");
        if (matc > 0) sb.Append($"\tObjectType: \"Material\" {{\n\t\tCount: {matc}\n\t}}\n");
        sb.Append("}\n\n");
        sb.Append("Objects:  {\n").Append(objs).Append("}\n\n");
        sb.Append("Connections:  {\n").Append(cons).Append("}\n");
        return sb.ToString();
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private static void AccumNormal(double[] pos, double[] nrm, int a, int b, int c)
    {
        double ax=pos[a*3],ay=pos[a*3+1],az=pos[a*3+2];
        double ux=pos[b*3]-ax, uy=pos[b*3+1]-ay, uz=pos[b*3+2]-az;
        double vx=pos[c*3]-ax, vy=pos[c*3+1]-ay, vz=pos[c*3+2]-az;
        double nx=uy*vz-uz*vy, ny=uz*vx-ux*vz, nz=ux*vy-uy*vx;
        nrm[a*3]+=nx; nrm[a*3+1]+=ny; nrm[a*3+2]+=nz;
        nrm[b*3]+=nx; nrm[b*3+1]+=ny; nrm[b*3+2]+=nz;
        nrm[c*3]+=nx; nrm[c*3+1]+=ny; nrm[c*3+2]+=nz;
    }

    private static void NormalizeAll(double[] nrm)
    {
        for (int i = 0; i < nrm.Length; i += 3)
        {
            double len = Math.Sqrt(nrm[i]*nrm[i] + nrm[i+1]*nrm[i+1] + nrm[i+2]*nrm[i+2]);
            if (len > 1e-9) { nrm[i]/=len; nrm[i+1]/=len; nrm[i+2]/=len; }
            else { nrm[i]=0; nrm[i+1]=1; nrm[i+2]=0; }
        }
    }

    private static string JoinNums(double[] a)
    {
        var sb = new StringBuilder(a.Length * 8);
        for (int i = 0; i < a.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(a[i].ToString("G7", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // Each polygon's last index is one's-complemented (~i = -i-1) to mark the polygon boundary.
    private static string JoinPolyIndex(List<int> faces)
    {
        var sb = new StringBuilder(faces.Count * 6);
        for (int i = 0; i < faces.Count; i += 3)
        {
            if (i > 0) sb.Append(',');
            sb.Append(faces[i]).Append(',').Append(faces[i + 1]).Append(',').Append(-faces[i + 2] - 1);
        }
        return sb.ToString();
    }

    private static string SanitizeName(string? primary, string? fallback, int index)
    {
        var n = !string.IsNullOrWhiteSpace(primary) ? primary
              : !string.IsNullOrWhiteSpace(fallback) ? fallback : $"submesh_{index}";
        return n!.Replace(' ', '_').Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace('"', '_');
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static ExportResult Fail(AssetData asset, string message) =>
        new() { Success = false, ErrorMessage = message, SourceAsset = asset.Info };
}
