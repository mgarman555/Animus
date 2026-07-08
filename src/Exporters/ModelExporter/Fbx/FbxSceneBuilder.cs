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
        float scale = settings.ModelScaleFactor <= 0 ? 1f : settings.ModelScaleFactor;

        // Skinned meshes are exported UE-native (Z-up, no vertex bake) so bones and vertices share
        // one space; the importer applies its own axis conversion from GlobalSettings. The
        // ApplyBlenderBoneCorrection vertex-bake is therefore honoured only for unrigged meshes.
        bool skinned = settings.ExportSkeleton
                       && mesh.Skeleton != null && mesh.Skeleton.Bones.Count > 0
                       && lod.BoneIndices != null && lod.BoneWeights != null;
        bool bake = settings.ApplyBlenderBoneCorrection && !skinned;

        var objects = new FbxNode("Objects");
        var connections = new FbxNode("Connections");

        var submeshes = lod.Submeshes.Count > 0
            ? lod.Submeshes
            : new List<SubmeshInfo>
              {
                  new() { Name = mesh.Info.Name, VertexStart = 0, VertexCount = lod.VertexCount,
                          IndexStart = 0, IndexCount = (lod.IndexBuffer?.Length ?? 0) / 4 }
              };

        // Skeleton (shared across submeshes) built once so every cluster references the same bones.
        Skeleton? skel = skinned ? BuildSkeleton(mesh.Skeleton!, ids, objects, connections) : null;

        int geomCount = 0, modelCount = 1 /*armature Null when skinned*/, matCount = 0;
        if (!skinned) modelCount = 0;
        int boneModels = skel?.BoneIds.Length ?? 0;
        int deformerCount = 0, subDeformerCount = 0, poseCount = skinned ? 1 : 0, attrCount = boneModels;
        int ni = (lod.IndexBuffer?.Length ?? 0) / 4;

        for (int s = 0; s < submeshes.Count; s++)
        {
            long geomId = BuildSubmesh(mesh, lod, submeshes[s], s, ni, bake, scale, ids, objects, connections);
            if (geomId == 0) continue;
            geomCount++; modelCount++; matCount++;

            if (skel != null)
            {
                int subs = BuildSkin(lod, submeshes[s], geomId, skel, ids, objects, connections);
                if (subs > 0) { deformerCount++; subDeformerCount += subs; }
            }
        }

        var top = new List<FbxNode>
        {
            BuildHeader(),
            BuildGlobalSettings(bake),
            BuildDefinitions(geomCount, modelCount, matCount, boneModels, deformerCount, subDeformerCount, poseCount, attrCount),
            objects,
            connections,
        };
        return top;
    }

    /// <summary>
    /// Builds an FBX document containing a skeleton plus one animation take: the bone nodes (so the
    /// clip is self-contained and retargetable in Blender) and an AnimationStack/Layer with T/R/S
    /// curves per animated bone. Rotation keys are quaternions in the source and are converted to
    /// EulerXYZ per frame with unrolling so curves stay continuous across the ±180° wrap.
    /// </summary>
    public static List<FbxNode> BuildAnimation(SkeletonData skeleton, AnimationAssetData anim, ExportSettings settings)
    {
        var ids = new IdGen();
        var objects = new FbxNode("Objects");
        var connections = new FbxNode("Connections");

        var skel = BuildSkeleton(skeleton, ids, objects, connections);
        var boneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < skel.Bones.Count; i++) boneByName[skel.Bones[i].Name] = i;

        float fps = anim.FrameRate > 0 ? anim.FrameRate : 30f;
        var (stackNodes, curveNodeCount, curveCount) = BuildTake(anim, skel, boneByName, fps, ids, objects, connections);

        var top = new List<FbxNode>
        {
            BuildHeader(),
            BuildGlobalSettings(false),
            BuildAnimDefinitions(skel.BoneIds.Length, curveNodeCount, curveCount),
            objects,
            connections,
        };
        // AnimationStack/Layer live in Objects (added by BuildTake); nothing else to place.
        _ = stackNodes;
        return top;
    }

    // One FBX "KTime" tick = 1/46186158000 s (FbxTime resolution).
    private const long KTimePerSecond = 46_186_158_000L;

    private static (int stacks, int curveNodes, int curves) BuildTake(
        AnimationAssetData anim, Skeleton skel, Dictionary<string, int> boneByName, float fps,
        IdGen ids, FbxNode objects, FbxNode connections)
    {
        long stackId = ids.Next();
        var stack = new FbxNode("AnimationStack", stackId, $"AnimStack::{FbxName.Sanitize(anim.Info.Name, "Take", 0)}", "");
        int frameCount = Math.Max(anim.FrameCount, 1);
        long stopKt = (long)Math.Round((frameCount - 1) / (double)fps * KTimePerSecond);
        var sp70 = stack.Add("Properties70");
        sp70.Add(P("LocalStart", "KTime", "Time", "", 0L));
        sp70.Add(P("LocalStop", "KTime", "Time", "", stopKt));
        objects.Add(stack);

        long layerId = ids.Next();
        var layer = new FbxNode("AnimationLayer", layerId, "AnimLayer::BaseLayer", "");
        objects.Add(layer);
        connections.Add("C", "OO", layerId, stackId); // layer → stack

        int curveNodes = 0, curves = 0;
        foreach (var track in anim.Tracks)
        {
            if (!boneByName.TryGetValue(track.BoneName, out int bi)) continue;
            long boneId = skel.BoneIds[bi];

            if (track.PositionKeys.Count > 0)
                EmitChannel(track.PositionKeys, "Lcl Translation", boneId, layerId, fps, ids, objects, connections, ref curveNodes, ref curves);

            if (track.RotationKeys.Count > 0)
            {
                var euler = QuatKeysToEuler(track.RotationKeys);
                EmitChannel(euler, "Lcl Rotation", boneId, layerId, fps, ids, objects, connections, ref curveNodes, ref curves);
            }

            if (track.ScaleKeys.Count > 0)
                EmitChannel(track.ScaleKeys, "Lcl Scaling", boneId, layerId, fps, ids, objects, connections, ref curveNodes, ref curves);
        }
        return (1, curveNodes, curves);
    }

    /// <summary>Emits one AnimationCurveNode (X/Y/Z) for a bone property, plus three AnimationCurves.</summary>
    private static void EmitChannel(
        IReadOnlyList<float[]> keys, string property, long boneId, long layerId, float fps,
        IdGen ids, FbxNode objects, FbxNode connections, ref int curveNodes, ref int curves)
    {
        long nodeId = ids.Next();
        double d0 = keys.Count > 0 ? keys[0][0] : 0, d1 = keys.Count > 0 ? keys[0][1] : 0, d2 = keys.Count > 0 ? keys[0][2] : 0;
        var cn = new FbxNode("AnimationCurveNode", nodeId, $"AnimCurveNode::{ChannelTag(property)}", "");
        var p = cn.Add("Properties70");
        p.Add(P("d|X", "Number", "", "A", d0));
        p.Add(P("d|Y", "Number", "", "A", d1));
        p.Add(P("d|Z", "Number", "", "A", d2));
        objects.Add(cn);
        connections.Add("C", "OP", nodeId, boneId, property); // curve node → bone property
        connections.Add("C", "OO", nodeId, layerId);          // curve node → layer
        curveNodes++;

        var times = new long[keys.Count];
        for (int f = 0; f < keys.Count; f++)
            times[f] = (long)Math.Round(f / (double)fps * KTimePerSecond);

        for (int axis = 0; axis < 3; axis++)
        {
            var values = new float[keys.Count];
            for (int f = 0; f < keys.Count; f++) values[f] = keys[f].Length > axis ? keys[f][axis] : 0f;
            long curveId = EmitCurve(times, values, ids, objects);
            connections.Add("C", "OP", curveId, nodeId, axis == 0 ? "d|X" : axis == 1 ? "d|Y" : "d|Z");
            curves++;
        }
    }

    private static long EmitCurve(long[] times, float[] values, IdGen ids, FbxNode objects)
    {
        long id = ids.Next();
        var c = new FbxNode("AnimationCurve", id, "AnimCurve::", "");
        c.Add("Default", 0.0);
        c.Add("KeyVer", 4008);
        c.Add("KeyTime").Prop(times);
        c.Add("KeyValueFloat").Prop(values);
        // Linear interpolation flag for every key; minimal attr arrays the importers expect.
        c.Add("KeyAttrFlags").Prop(new[] { 0x0000010C });
        c.Add("KeyAttrDataFloat").Prop(new float[] { 0, 0, 0, 0 });
        c.Add("KeyAttrRefCount").Prop(new[] { times.Length });
        objects.Add(c);
        return id;
    }

    private static string ChannelTag(string property) => property switch
    {
        "Lcl Translation" => "T",
        "Lcl Rotation" => "R",
        "Lcl Scaling" => "S",
        _ => "X"
    };

    /// <summary>Quaternion rotation keys → EulerXYZ degrees with per-frame unrolling for continuity.</summary>
    private static List<float[]> QuatKeysToEuler(IReadOnlyList<float[]> quatKeys)
    {
        var outKeys = new List<float[]>(quatKeys.Count);
        double px = 0, py = 0, pz = 0;
        for (int f = 0; f < quatKeys.Count; f++)
        {
            var q = quatKeys[f];
            var (x, y, z) = Mat4.QuatToEulerXyzDeg(q[0], q[1], q[2], q.Length > 3 ? q[3] : 1f);
            if (f > 0)
            {
                x = Unroll(px, x);
                y = Unroll(py, y);
                z = Unroll(pz, z);
            }
            px = x; py = y; pz = z;
            outKeys.Add(new[] { (float)x, (float)y, (float)z });
        }
        return outKeys;
    }

    /// <summary>Shifts <paramref name="cur"/> by 360° multiples to sit within ±180° of <paramref name="prev"/>.</summary>
    private static double Unroll(double prev, double cur)
    {
        while (cur - prev > 180) cur -= 360;
        while (cur - prev < -180) cur += 360;
        return cur;
    }

    private static FbxNode BuildAnimDefinitions(int boneModels, int curveNodes, int curves)
    {
        var d = new FbxNode("Definitions");
        d.Add("Version", 100);
        d.Add("Count", 1 + boneModels + boneModels /*attrs*/ + 1 /*pose*/ + 1 /*armature*/ + 1 /*stack*/ + 1 /*layer*/ + curveNodes + curves);
        d.Add("ObjectType", "GlobalSettings").Add("Count", 1);
        if (boneModels > 0)
        {
            d.Add("ObjectType", "Model").Add("Count", boneModels + 1); // + armature Null
            d.Add("ObjectType", "NodeAttribute").Add("Count", boneModels);
            d.Add("ObjectType", "Pose").Add("Count", 1);
        }
        d.Add("ObjectType", "AnimationStack").Add("Count", 1);
        d.Add("ObjectType", "AnimationLayer").Add("Count", 1);
        if (curveNodes > 0) d.Add("ObjectType", "AnimationCurveNode").Add("Count", curveNodes);
        if (curves > 0)     d.Add("ObjectType", "AnimationCurve").Add("Count", curves);
        return d;
    }

    /// <summary>
    /// Builds an FBX document for a standalone skeleton (armature only, no geometry). This is the
    /// "skeleton exporter" — same bone nodes + bind pose the skinned path emits, minus meshes.
    /// </summary>
    public static List<FbxNode> BuildSkeletonOnly(SkeletonData skeleton, ExportSettings settings)
    {
        var ids = new IdGen();
        var objects = new FbxNode("Objects");
        var connections = new FbxNode("Connections");

        var skel = BuildSkeleton(skeleton, ids, objects, connections);

        var top = new List<FbxNode>
        {
            BuildHeader(),
            BuildGlobalSettings(false), // native Z-up; importer converts
            BuildDefinitions(0, 1 /*armature Null*/, 0, skel.BoneIds.Length, 0, 0, 1, skel.BoneIds.Length),
            objects,
            connections,
        };
        return top;
    }

    /// <summary>Emits Geometry+Model+Material for one submesh. Returns the geometry object id, or 0 if skipped.</summary>
    private static long BuildSubmesh(
        MeshAssetData mesh, LodData lod, SubmeshInfo sub, int s, int ni,
        bool convert, float scale, IdGen ids, FbxNode objects, FbxNode connections)
    {
        int vc = sub.VertexCount;
        if (vc <= 0 || sub.IndexCount <= 0 || lod.VertexBuffer == null || lod.IndexBuffer == null)
            return 0;
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
        if (polyIndex.Count == 0) return 0;

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
        return geomId;
    }

    // ─── skeleton + skinning ──────────────────────────────────────────────────────

    /// <summary>Resolved skeleton: per-bone object ids + global bind matrices, in bone-list order.</summary>
    private sealed class Skeleton
    {
        public long[] BoneIds = Array.Empty<long>();
        public Mat4[] GlobalBind = Array.Empty<Mat4>();
        public IReadOnlyList<BoneInfo> Bones = Array.Empty<BoneInfo>();
    }

    /// <summary>
    /// Emits the armature Null, one LimbNode Model + NodeAttribute per bone, the bone hierarchy
    /// connections, and a BindPose. Returns per-bone ids and global bind matrices for the clusters.
    /// </summary>
    private static Skeleton BuildSkeleton(SkeletonData skeleton, IdGen ids, FbxNode objects, FbxNode connections)
    {
        var bones = skeleton.Bones;
        int n = bones.Count;
        var boneIds = new long[n];
        var attrIds = new long[n];
        var global = new Mat4[n];

        long armatureId = ids.Next();
        var armature = new FbxNode("Model", armatureId, "Model::Armature", "Null");
        armature.Add("Version", 232);
        armature.Add("Properties70");
        objects.Add(armature);
        connections.Add("C", "OO", armatureId, 0L); // armature → scene root

        for (int i = 0; i < n; i++)
        {
            boneIds[i] = ids.Next();
            attrIds[i] = ids.Next();

            var b = bones[i];
            var local = Mat4.LocalFromBone(b);
            global[i] = b.ParentIndex >= 0 && b.ParentIndex < i ? global[b.ParentIndex] * local : local;

            var (rx, ry, rz) = Mat4.QuatToEulerXyzDeg(b.Rotation[0], b.Rotation[1], b.Rotation[2], b.Rotation[3]);
            double sx = b.Scale[0] == 0 ? 1 : b.Scale[0];
            double sy = b.Scale[1] == 0 ? 1 : b.Scale[1];
            double sz = b.Scale[2] == 0 ? 1 : b.Scale[2];

            var model = new FbxNode("Model", boneIds[i], $"Model::{FbxName.Sanitize(b.Name, "bone", i)}", "LimbNode");
            model.Add("Version", 232);
            var p70 = model.Add("Properties70");
            p70.Add(P("Lcl Translation", "Lcl Translation", "", "A", (double)b.Position[0], (double)b.Position[1], (double)b.Position[2]));
            p70.Add(P("Lcl Rotation", "Lcl Rotation", "", "A", rx, ry, rz));
            p70.Add(P("Lcl Scaling", "Lcl Scaling", "", "A", sx, sy, sz));
            objects.Add(model);

            var attr = new FbxNode("NodeAttribute", attrIds[i], "NodeAttribute::", "LimbNode");
            attr.Add("TypeFlags", "Skeleton");
            objects.Add(attr);

            connections.Add("C", "OO", attrIds[i], boneIds[i]); // attribute → bone model
            long parentId = b.ParentIndex >= 0 && b.ParentIndex < n ? boneIds[b.ParentIndex] : armatureId;
            connections.Add("C", "OO", boneIds[i], parentId);   // bone → parent bone (or armature)
        }

        // BindPose: global bind matrix per bone (+ armature at identity).
        var pose = new FbxNode("Pose", ids.Next(), "Pose::BIND_POSES", "BindPose");
        pose.Add("Type", "BindPose");
        pose.Add("Version", 100);
        pose.Add("NbPoseNodes", n + 1);
        AddPoseNode(pose, armatureId, Mat4.Identity());
        for (int i = 0; i < n; i++) AddPoseNode(pose, boneIds[i], global[i]);
        objects.Add(pose);

        return new Skeleton { BoneIds = boneIds, GlobalBind = global, Bones = bones };
    }

    private static void AddPoseNode(FbxNode pose, long nodeId, Mat4 m)
    {
        var pn = pose.Add("PoseNode");
        pn.Add("Node", nodeId);
        pn.Add("Matrix").Prop(m.M);
    }

    /// <summary>
    /// Builds a Skin deformer on one submesh's geometry plus a Cluster per influencing bone,
    /// mapping submesh-local vertex indices to bones via the LOD's parallel skin arrays.
    /// Returns the number of clusters emitted (0 if the submesh has no weighted vertices).
    /// </summary>
    private static int BuildSkin(LodData lod, SubmeshInfo sub, long geomId, Skeleton skel, IdGen ids, FbxNode objects, FbxNode connections)
    {
        if (lod.BoneIndices == null || lod.BoneWeights == null) return 0;
        int inf = Math.Max(lod.InfluencesPerVertex, 1);

        // bone → (local vertex indices, weights)
        var byBone = new Dictionary<int, (List<int> idx, List<double> wt)>();
        for (int v = 0; v < sub.VertexCount; v++)
        {
            int gv = sub.VertexStart + v;
            for (int k = 0; k < inf; k++)
            {
                int si = gv * inf + k;
                if (si >= lod.BoneWeights.Length) break;
                double w = lod.BoneWeights[si];
                if (w <= 0) continue;
                int bone = lod.BoneIndices[si];
                if (bone < 0 || bone >= skel.BoneIds.Length) continue;
                if (!byBone.TryGetValue(bone, out var lst)) { lst = (new List<int>(), new List<double>()); byBone[bone] = lst; }
                lst.idx.Add(v);
                lst.wt.Add(w);
            }
        }
        if (byBone.Count == 0) return 0;

        long skinId = ids.Next();
        var skin = new FbxNode("Deformer", skinId, "Deformer::Skin", "Skin");
        skin.Add("Version", 101);
        objects.Add(skin);
        connections.Add("C", "OO", skinId, geomId); // skin → geometry

        int clusters = 0;
        foreach (var (bone, data) in byBone)
        {
            long clusterId = ids.Next();
            var cl = new FbxNode("Deformer", clusterId, "SubDeformer::Cluster", "Cluster");
            cl.Add("Version", 100);
            cl.Add("Indexes").Prop(data.idx.ToArray());
            cl.Add("Weights").Prop(data.wt.ToArray());
            // Transform = mesh global at bind (identity — mesh model carries no Lcl transform).
            cl.Add("Transform").Prop(Mat4.Identity().M);
            // TransformLink = bone global bind matrix.
            cl.Add("TransformLink").Prop(skel.GlobalBind[bone].M);
            objects.Add(cl);

            connections.Add("C", "OO", clusterId, skinId);            // cluster → skin
            connections.Add("C", "OO", skel.BoneIds[bone], clusterId); // bone model → cluster
            clusters++;
        }
        return clusters;
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

    private static FbxNode BuildDefinitions(
        int geom, int model, int mat, int boneModels = 0,
        int deformers = 0, int subDeformers = 0, int poses = 0, int nodeAttrs = 0)
    {
        int totalModels = model + boneModels;
        var d = new FbxNode("Definitions");
        d.Add("Version", 100);
        d.Add("Count", 1 + geom + totalModels + mat + deformers + subDeformers + poses + nodeAttrs);
        d.Add("ObjectType", "GlobalSettings").Add("Count", 1);
        if (geom > 0)          d.Add("ObjectType", "Geometry").Add("Count", geom);
        if (totalModels > 0)   d.Add("ObjectType", "Model").Add("Count", totalModels);
        if (nodeAttrs > 0)     d.Add("ObjectType", "NodeAttribute").Add("Count", nodeAttrs);
        if (mat > 0)           d.Add("ObjectType", "Material").Add("Count", mat);
        if (deformers + subDeformers > 0)
                               d.Add("ObjectType", "Deformer").Add("Count", deformers + subDeformers);
        if (poses > 0)         d.Add("ObjectType", "Pose").Add("Count", poses);
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
