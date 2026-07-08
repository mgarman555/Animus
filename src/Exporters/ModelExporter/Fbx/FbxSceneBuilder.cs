using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Exporters.ModelExporter.Fbx;

/// <summary>
/// Builds an FBX document node tree (for <see cref="FbxBinaryWriter"/>) from our asset model.
///
/// Layout is the standard FBX 7.4 scene: FBXHeaderExtension, GlobalSettings, Definitions, Objects
/// (one Geometry+Model+Material per submesh), Connections. Geometry carries positions, per-vertex
/// normals (from <see cref="LodData.Normals"/> when present, else accumulated from faces), and UV0.
///
/// Coordinate handling mirrors the OBJ/legacy-FBX exporters: when
/// <see cref="ExportSettings.ApplyBlenderBoneCorrection"/> is set, UE Z-up is baked to Y-up as
/// (x, z, -y) and the GlobalSettings axes are declared Y-up; otherwise data is left UE-native Z-up
/// and the axes are declared Z-up (so the file never lies about its own orientation).
///
/// Skinning (1c) and animation (1d) extend this via <see cref="AddSkin"/> and the anim builder.
/// </summary>
public static class FbxSceneBuilder
{
    // Object IDs must be unique int64s. Start high to avoid colliding with the root (0).
    private sealed class IdGen { private long _n = 1_000_000; public long Next() => ++_n; }

    public static List<FbxNode> Build(MeshAssetData mesh, LodData lod, ExportSettings settings)
    {
        var ids = new IdGen();
        bool convert = settings.ApplyBlenderBoneCorrection;
        float scale = settings.ModelScaleFactor <= 0 ? 1f : settings.ModelScaleFactor;

        var objects = new FbxNode("Objects");
        var connections = new FbxNode("Connections");

        var submeshes = lod.Submeshes.Count > 0
            ? lod.Submeshes
            : new List<SubmeshInfo>
              {
                  new() { Name = mesh.Info.Name, VertexStart = 0, VertexCount = lod.VertexCount,
                          IndexStart = 0, IndexCount = (lod.IndexBuffer?.Length ?? 0) / 4 }
              };

        int geomCount = 0, modelCount = 0, matCount = 0;
        int ni = (lod.IndexBuffer?.Length ?? 0) / 4;

        for (int s = 0; s < submeshes.Count; s++)
        {
            var built = BuildSubmesh(mesh, lod, submeshes[s], s, ni, convert, scale, ids, objects, connections);
            if (built) { geomCount++; modelCount++; matCount++; }
        }

        var top = new List<FbxNode>
        {
            BuildHeader(),
            BuildGlobalSettings(convert),
            BuildDefinitions(geomCount, modelCount, matCount),
            objects,
            connections,
        };
        return top;
    }

    private static bool BuildSubmesh(
        MeshAssetData mesh, LodData lod, SubmeshInfo sub, int s, int ni,
        bool convert, float scale, IdGen ids, FbxNode objects, FbxNode connections)
    {
        int vc = sub.VertexCount;
        if (vc <= 0 || sub.IndexCount <= 0 || lod.VertexBuffer == null || lod.IndexBuffer == null)
            return false;
        var vb = lod.VertexBuffer;
        var ib = lod.IndexBuffer;

        // Positions (rebased to submesh-local, converted + scaled).
        var pos = new double[vc * 3];
        for (int v = 0; v < vc; v++)
        {
            int off = (sub.VertexStart + v) * 12;
            if (off + 12 > vb.Length) { vc = v; break; }
            float x = BitConverter.ToSingle(vb, off) * scale;
            float y = BitConverter.ToSingle(vb, off + 4) * scale;
            float z = BitConverter.ToSingle(vb, off + 8) * scale;
            if (convert) { pos[v * 3] = x; pos[v * 3 + 1] = z; pos[v * 3 + 2] = -y; }
            else         { pos[v * 3] = x; pos[v * 3 + 1] = y; pos[v * 3 + 2] = z; }
        }
        Array.Resize(ref pos, vc * 3);

        // Faces (rebased) + polygon-vertex index with last-index one's-complement.
        var polyIndex = new List<int>(sub.IndexCount);
        var faceTris = new List<(int a, int b, int c)>(sub.IndexCount / 3);
        int end = sub.IndexStart + sub.IndexCount;
        for (int i = sub.IndexStart; i + 2 < end && i + 2 < ni; i += 3)
        {
            int a = BitConverter.ToInt32(ib, i * 4) - sub.VertexStart;
            int b = BitConverter.ToInt32(ib, (i + 1) * 4) - sub.VertexStart;
            int c = BitConverter.ToInt32(ib, (i + 2) * 4) - sub.VertexStart;
            if ((uint)a >= (uint)vc || (uint)b >= (uint)vc || (uint)c >= (uint)vc) continue;
            polyIndex.Add(a); polyIndex.Add(b); polyIndex.Add(~c); // ~c marks polygon end
            faceTris.Add((a, b, c));
        }
        if (polyIndex.Count == 0) return false;

        // Normals: prefer source channel, else accumulate from faces.
        var normals = ExtractOrComputeNormals(lod, sub, vc, convert, pos, faceTris);

        // UV0 (V flipped to FBX convention).
        double[]? uv = null;
        if (lod.UvBuffer != null)
        {
            uv = new double[vc * 2];
            for (int v = 0; v < vc; v++)
            {
                int o = (sub.VertexStart + v) * 8;
                if (o + 8 > lod.UvBuffer.Length) break;
                uv[v * 2] = BitConverter.ToSingle(lod.UvBuffer, o);
                uv[v * 2 + 1] = 1f - BitConverter.ToSingle(lod.UvBuffer, o + 4);
            }
        }

        long geomId = ids.Next(), modelId = ids.Next(), matId = ids.Next();
        string nm = FbxName.Sanitize(sub.Name, mesh.Info.Name, s);
        string matName = FbxName.Sanitize(
            s < mesh.MaterialSlots.Count ? mesh.MaterialSlots[s].MaterialName : sub.MaterialName, nm + "_mat", s);

        objects.Add(BuildGeometry(geomId, nm, pos, polyIndex, normals, uv));
        objects.Add(BuildMeshModel(modelId, nm));
        objects.Add(BuildPhongMaterial(matId, matName));

        connections.Add("C", "OO", modelId, 0L);      // model → scene root
        connections.Add("C", "OO", geomId, modelId);  // geometry → model
        connections.Add("C", "OO", matId, modelId);   // material → model
        return true;
    }

    private static double[] ExtractOrComputeNormals(
        LodData lod, SubmeshInfo sub, int vc, bool convert, double[] pos, List<(int a, int b, int c)> faces)
    {
        var nrm = new double[vc * 3];
        if (lod.Normals != null && lod.Normals.Length >= (sub.VertexStart + vc) * 3)
        {
            for (int v = 0; v < vc; v++)
            {
                int o = (sub.VertexStart + v) * 3;
                double nx = lod.Normals[o], ny = lod.Normals[o + 1], nz = lod.Normals[o + 2];
                if (convert) { nrm[v * 3] = nx; nrm[v * 3 + 1] = nz; nrm[v * 3 + 2] = -ny; }
                else         { nrm[v * 3] = nx; nrm[v * 3 + 1] = ny; nrm[v * 3 + 2] = nz; }
            }
            return nrm;
        }

        // Face-accumulated fallback (same math as the legacy exporter).
        foreach (var (a, b, c) in faces)
        {
            double ax = pos[a * 3], ay = pos[a * 3 + 1], az = pos[a * 3 + 2];
            double ux = pos[b * 3] - ax, uy = pos[b * 3 + 1] - ay, uz = pos[b * 3 + 2] - az;
            double vx = pos[c * 3] - ax, vy = pos[c * 3 + 1] - ay, vz = pos[c * 3 + 2] - az;
            double gx = uy * vz - uz * vy, gy = uz * vx - ux * vz, gz = ux * vy - uy * vx;
            nrm[a * 3] += gx; nrm[a * 3 + 1] += gy; nrm[a * 3 + 2] += gz;
            nrm[b * 3] += gx; nrm[b * 3 + 1] += gy; nrm[b * 3 + 2] += gz;
            nrm[c * 3] += gx; nrm[c * 3 + 1] += gy; nrm[c * 3 + 2] += gz;
        }
        for (int i = 0; i < nrm.Length; i += 3)
        {
            double len = Math.Sqrt(nrm[i] * nrm[i] + nrm[i + 1] * nrm[i + 1] + nrm[i + 2] * nrm[i + 2]);
            if (len > 1e-9) { nrm[i] /= len; nrm[i + 1] /= len; nrm[i + 2] /= len; }
            else { nrm[i] = 0; nrm[i + 1] = 1; nrm[i + 2] = 0; }
        }
        return nrm;
    }

    // ─── node builders ──────────────────────────────────────────────────────────

    private static FbxNode BuildGeometry(long id, string name, double[] pos, List<int> polyIndex, double[] normals, double[]? uv)
    {
        var geo = new FbxNode("Geometry", id, $"Geometry::{name}", "Mesh");
        geo.Add("Vertices").Prop(pos);
        geo.Add("PolygonVertexIndex").Prop(polyIndex.ToArray());
        geo.Add("GeometryVersion", 124);

        var nrm = geo.Add("LayerElementNormal", 0);
        nrm.Add("Version", 102);
        nrm.Add("Name", "");
        nrm.Add("MappingInformationType", "ByVertice");
        nrm.Add("ReferenceInformationType", "Direct");
        nrm.Add("Normals").Prop(normals);

        if (uv != null)
        {
            var uvn = geo.Add("LayerElementUV", 0);
            uvn.Add("Version", 101);
            uvn.Add("Name", "UVMap");
            uvn.Add("MappingInformationType", "ByVertice");
            uvn.Add("ReferenceInformationType", "Direct");
            uvn.Add("UV").Prop(uv);
        }

        var mat = geo.Add("LayerElementMaterial", 0);
        mat.Add("Version", 101);
        mat.Add("Name", "");
        mat.Add("MappingInformationType", "AllSame");
        mat.Add("ReferenceInformationType", "IndexToDirect");
        mat.Add("Materials").Prop(new[] { 0 });

        var layer = geo.Add("Layer", 0);
        layer.Add("Version", 100);
        AddLayerElement(layer, "LayerElementNormal", 0);
        if (uv != null) AddLayerElement(layer, "LayerElementUV", 0);
        AddLayerElement(layer, "LayerElementMaterial", 0);
        return geo;
    }

    private static void AddLayerElement(FbxNode layer, string type, int index)
    {
        var le = layer.Add("LayerElement");
        le.Add("Type", type);
        le.Add("TypedIndex", index);
    }

    private static FbxNode BuildMeshModel(long id, string name)
    {
        var model = new FbxNode("Model", id, $"Model::{name}", "Mesh");
        model.Add("Version", 232);
        var p70 = model.Add("Properties70");
        p70.Add(P("DefaultAttributeIndex", "int", "Integer", "", 0));
        model.Add("Shading", true);
        model.Add("Culling", "CullingOff");
        return model;
    }

    private static FbxNode BuildPhongMaterial(long id, string name)
    {
        var m = new FbxNode("Material", id, $"Material::{name}", "");
        m.Add("Version", 102);
        m.Add("ShadingModel", "phong");
        m.Add("MultiLayer", 0);
        var p70 = m.Add("Properties70");
        p70.Add(P("DiffuseColor", "Color", "", "A", 0.8, 0.8, 0.8));
        return m;
    }

    private static FbxNode BuildHeader()
    {
        var h = new FbxNode("FBXHeaderExtension");
        h.Add("FBXHeaderVersion", 1003);
        h.Add("FBXVersion", 7400);
        h.Add("Creator", "Game Asset Explorer");
        return h;
    }

    private static FbxNode BuildGlobalSettings(bool yUp)
    {
        var gs = new FbxNode("GlobalSettings");
        gs.Add("Version", 1000);
        var p = gs.Add("Properties70");
        // yUp: UpAxis=Y(1), FrontAxis=Z(2), CoordAxis=X(0).  Z-up: UpAxis=Z(2), FrontAxis=-Y, CoordAxis=X(0).
        if (yUp)
        {
            p.Add(P("UpAxis", "int", "Integer", "", 1));
            p.Add(P("UpAxisSign", "int", "Integer", "", 1));
            p.Add(P("FrontAxis", "int", "Integer", "", 2));
            p.Add(P("FrontAxisSign", "int", "Integer", "", 1));
        }
        else
        {
            p.Add(P("UpAxis", "int", "Integer", "", 2));
            p.Add(P("UpAxisSign", "int", "Integer", "", 1));
            p.Add(P("FrontAxis", "int", "Integer", "", 1));
            p.Add(P("FrontAxisSign", "int", "Integer", "", -1));
        }
        p.Add(P("CoordAxis", "int", "Integer", "", 0));
        p.Add(P("CoordAxisSign", "int", "Integer", "", 1));
        p.Add(P("UnitScaleFactor", "double", "Number", "", 1.0));
        return gs;
    }

    private static FbxNode BuildDefinitions(int geom, int model, int mat)
    {
        var d = new FbxNode("Definitions");
        d.Add("Version", 100);
        d.Add("Count", 1 + geom + model + mat);
        d.Add("ObjectType", "GlobalSettings").Add("Count", 1);
        if (geom > 0)  d.Add("ObjectType", "Geometry").Add("Count", geom);
        if (model > 0) d.Add("ObjectType", "Model").Add("Count", model);
        if (mat > 0)   d.Add("ObjectType", "Material").Add("Count", mat);
        return d;
    }

    /// <summary>Builds a Properties70 "P" record: name, type, subtype, flags, then value properties.</summary>
    internal static FbxNode P(string name, string type, string sub, string flags, params object[] values)
    {
        var p = new FbxNode("P", name, type, sub, flags);
        foreach (var v in values) p.Prop(v);
        return p;
    }
}
