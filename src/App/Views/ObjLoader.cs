using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using WpfPoint = System.Windows.Point;

namespace GameAssetExplorer.App.Views;

/// <summary>
/// Minimal Wavefront OBJ parser.
/// Handles v / vn / vt / f lines; triangulates quads and n-gons via fan.
/// </summary>
public static class ObjLoader
{
    public static MeshGeometry3D? Load(string path)
    {
        var rawPos = new List<Point3D>();
        var rawNrm = new List<Vector3D>();
        var rawUV  = new List<WpfPoint>();

        var outPos  = new Point3DCollection();
        var outNrm  = new Vector3DCollection();
        var outUV   = new PointCollection();
        var outIdx  = new Int32Collection();

        // Deduplicate (v, vt, vn) triplets so the index buffer stays compact
        var vertexCache = new Dictionary<(int, int, int), int>();

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var p = Split(line);
                if (p.Length >= 4)
                    rawPos.Add(new Point3D(F(p[1]), F(p[2]), F(p[3])));
            }
            else if (line.StartsWith("vn ", StringComparison.Ordinal))
            {
                var p = Split(line);
                if (p.Length >= 4)
                    rawNrm.Add(new Vector3D(F(p[1]), F(p[2]), F(p[3])));
            }
            else if (line.StartsWith("vt ", StringComparison.Ordinal))
            {
                var p = Split(line);
                if (p.Length >= 3)
                    rawUV.Add(new WpfPoint(F(p[1]), 1.0 - F(p[2]))); // flip V for D3D convention
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var p    = Split(line);
                var face = new int[p.Length - 1];

                for (int i = 0; i < face.Length; i++)
                {
                    var tok = p[i + 1].Split('/');
                    int vi  = tok.Length > 0 && tok[0].Length > 0 ? int.Parse(tok[0]) - 1 : 0;
                    int vti = tok.Length > 1 && tok[1].Length > 0 ? int.Parse(tok[1]) - 1 : -1;
                    int vni = tok.Length > 2 && tok[2].Length > 0 ? int.Parse(tok[2]) - 1 : -1;

                    var key = (vi, vti, vni);
                    if (!vertexCache.TryGetValue(key, out int cached))
                    {
                        cached = outPos.Count;
                        vertexCache[key] = cached;

                        outPos.Add(vi < rawPos.Count ? rawPos[vi] : default);

                        if (vni >= 0 && vni < rawNrm.Count)
                            outNrm.Add(rawNrm[vni]);

                        if (vti >= 0 && vti < rawUV.Count)
                            outUV.Add(rawUV[vti]);
                    }
                    face[i] = cached;
                }

                // Fan-triangulate the polygon
                for (int i = 1; i < face.Length - 1; i++)
                {
                    outIdx.Add(face[0]);
                    outIdx.Add(face[i]);
                    outIdx.Add(face[i + 1]);
                }
            }
        }

        if (outPos.Count == 0) return null;

        var geo = new MeshGeometry3D
        {
            Positions       = outPos,
            TriangleIndices = outIdx,
        };
        if (outNrm.Count == outPos.Count) geo.Normals             = outNrm;
        if (outUV.Count  == outPos.Count) geo.TextureCoordinates  = outUV;

        return geo;
    }

    private static string[] Split(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static double F(string s) =>
        double.Parse(s, CultureInfo.InvariantCulture);
}
