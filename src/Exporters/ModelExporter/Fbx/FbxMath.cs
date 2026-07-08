using GameAssetExplorer.Core.Models;

namespace GameAssetExplorer.Exporters.ModelExporter.Fbx;

/// <summary>
/// 4×4 double matrix in the exact convention FBX uses on disk: column-vector transforms
/// (p' = M·p) stored column-major. That means <see cref="M"/> is already the 16-double array an
/// FBX Transform/TransformLink node expects — no transpose games at write time.
///
/// Local bone transform is composed as T·R·S (scale applied first to a point), and a bone's global
/// bind matrix is ParentGlobal·Local. Rotation eulers use FBX EulerXYZ order (Rx·Ry·Rz,
/// column-vector — Z applied first), matched by <see cref="QuatToEulerXyzDeg"/> so the emitted
/// Lcl Rotation and the cluster TransformLink describe the *same* orientation (rest pose == bind
/// pose, so a skinned mesh sits undeformed at rest).
/// </summary>
internal readonly struct Mat4
{
    public readonly double[] M; // column-major, length 16

    private Mat4(double[] m) => M = m;

    public static Mat4 Identity() => new(new double[]
    {
        1,0,0,0,  0,1,0,0,  0,0,1,0,  0,0,0,1
    });

    private static double Get(double[] m, int row, int col) => m[col * 4 + row];

    public static Mat4 Translation(double x, double y, double z)
    {
        var m = Identity().M;
        m[12] = x; m[13] = y; m[14] = z;
        return new Mat4(m);
    }

    public static Mat4 Scale(double x, double y, double z)
    {
        var m = Identity().M;
        m[0] = x; m[5] = y; m[10] = z;
        return new Mat4(m);
    }

    /// <summary>Rotation about X (column-vector).</summary>
    public static Mat4 RotX(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        var m = Identity().M;
        m[5] = c;  m[9] = -s;
        m[6] = s;  m[10] = c;
        return new Mat4(m);
    }

    public static Mat4 RotY(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        var m = Identity().M;
        m[0] = c;  m[8] = s;
        m[2] = -s; m[10] = c;
        return new Mat4(m);
    }

    public static Mat4 RotZ(double rad)
    {
        double c = Math.Cos(rad), s = Math.Sin(rad);
        var m = Identity().M;
        m[0] = c; m[4] = -s;
        m[1] = s; m[5] = c;
        return new Mat4(m);
    }

    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        var r = new double[16];
        for (int col = 0; col < 4; col++)
            for (int row = 0; row < 4; row++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                    sum += Get(a.M, row, k) * Get(b.M, k, col);
                r[col * 4 + row] = sum;
            }
        return new Mat4(r);
    }

    /// <summary>Rotation from EulerXYZ degrees: R = Rx·Ry·Rz (column-vector).</summary>
    public static Mat4 RotationEulerXyzDeg(double xDeg, double yDeg, double zDeg)
    {
        const double d2r = Math.PI / 180.0;
        return RotX(xDeg * d2r) * RotY(yDeg * d2r) * RotZ(zDeg * d2r);
    }

    /// <summary>Local bone matrix T·R·S from a <see cref="BoneInfo"/> (euler derived from its quat).</summary>
    public static Mat4 LocalFromBone(BoneInfo b)
    {
        var (rx, ry, rz) = QuatToEulerXyzDeg(b.Rotation[0], b.Rotation[1], b.Rotation[2], b.Rotation[3]);
        var t = Translation(b.Position[0], b.Position[1], b.Position[2]);
        var r = RotationEulerXyzDeg(rx, ry, rz);
        var sx = b.Scale[0] == 0 && b.Scale[1] == 0 && b.Scale[2] == 0 ? 1 : b.Scale[0];
        var sy = b.Scale[1] == 0 ? 1 : b.Scale[1];
        var sz = b.Scale[2] == 0 ? 1 : b.Scale[2];
        return t * r * Scale(sx, sy, sz);
    }

    /// <summary>
    /// Quaternion → EulerXYZ degrees, matched to <see cref="RotationEulerXyzDeg"/> so that
    /// recomposing the returned angles reproduces the quaternion's rotation matrix (validated by
    /// tests). Uses the standard Rx·Ry·Rz extraction with a gimbal-lock guard at |sy|→1.
    /// </summary>
    public static (double x, double y, double z) QuatToEulerXyzDeg(double x, double y, double z, double w)
    {
        // Normalize (defensive; source quats should already be unit).
        double n = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (n < 1e-12) return (0, 0, 0);
        x /= n; y /= n; z /= n; w /= n;

        // Rotation matrix elements (column-vector) for R = Rx·Ry·Rz.
        // m02 = sin(Y).
        double m02 = 2 * (x * z + w * y);
        m02 = Math.Clamp(m02, -1.0, 1.0);
        double ry = Math.Asin(m02);

        double rx, rz;
        if (Math.Abs(m02) < 0.9999999)
        {
            // m12 = -sx*cy, m22 = cx*cy  → rx = atan2(-m12, m22)
            double m12 = 2 * (y * z - w * x);
            double m22 = 1 - 2 * (x * x + y * y);
            rx = Math.Atan2(-m12, m22);
            // m01 = -cy*sz, m00 = cy*cz  → rz = atan2(-m01, m00)
            double m01 = 2 * (x * y - w * z);
            double m00 = 1 - 2 * (y * y + z * z);
            rz = Math.Atan2(-m01, m00);
        }
        else
        {
            // Gimbal lock: fix rz = 0, solve rx from remaining terms.
            double m10 = 2 * (x * y + w * z);
            double m11 = 1 - 2 * (x * x + z * z);
            rx = Math.Atan2(m10, m11);
            rz = 0;
        }

        const double r2d = 180.0 / Math.PI;
        return (rx * r2d, ry * r2d, rz * r2d);
    }

    public Mat4 Inverse()
    {
        // General 4×4 inverse (cofactor method). Bind matrices are well-conditioned.
        var m = M;
        var inv = new double[16];

        inv[0] = m[5]*m[10]*m[15] - m[5]*m[11]*m[14] - m[9]*m[6]*m[15] + m[9]*m[7]*m[14] + m[13]*m[6]*m[11] - m[13]*m[7]*m[10];
        inv[4] = -m[4]*m[10]*m[15] + m[4]*m[11]*m[14] + m[8]*m[6]*m[15] - m[8]*m[7]*m[14] - m[12]*m[6]*m[11] + m[12]*m[7]*m[10];
        inv[8] = m[4]*m[9]*m[15] - m[4]*m[11]*m[13] - m[8]*m[5]*m[15] + m[8]*m[7]*m[13] + m[12]*m[5]*m[11] - m[12]*m[7]*m[9];
        inv[12] = -m[4]*m[9]*m[14] + m[4]*m[10]*m[13] + m[8]*m[5]*m[14] - m[8]*m[6]*m[13] - m[12]*m[5]*m[10] + m[12]*m[6]*m[9];

        inv[1] = -m[1]*m[10]*m[15] + m[1]*m[11]*m[14] + m[9]*m[2]*m[15] - m[9]*m[3]*m[14] - m[13]*m[2]*m[11] + m[13]*m[3]*m[10];
        inv[5] = m[0]*m[10]*m[15] - m[0]*m[11]*m[14] - m[8]*m[2]*m[15] + m[8]*m[3]*m[14] + m[12]*m[2]*m[11] - m[12]*m[3]*m[10];
        inv[9] = -m[0]*m[9]*m[15] + m[0]*m[11]*m[13] + m[8]*m[1]*m[15] - m[8]*m[3]*m[13] - m[12]*m[1]*m[11] + m[12]*m[3]*m[9];
        inv[13] = m[0]*m[9]*m[14] - m[0]*m[10]*m[13] - m[8]*m[1]*m[14] + m[8]*m[2]*m[13] + m[12]*m[1]*m[10] - m[12]*m[2]*m[9];

        inv[2] = m[1]*m[6]*m[15] - m[1]*m[7]*m[14] - m[5]*m[2]*m[15] + m[5]*m[3]*m[14] + m[13]*m[2]*m[7] - m[13]*m[3]*m[6];
        inv[6] = -m[0]*m[6]*m[15] + m[0]*m[7]*m[14] + m[4]*m[2]*m[15] - m[4]*m[3]*m[14] - m[12]*m[2]*m[7] + m[12]*m[3]*m[6];
        inv[10] = m[0]*m[5]*m[15] - m[0]*m[7]*m[13] - m[4]*m[1]*m[15] + m[4]*m[3]*m[13] + m[12]*m[1]*m[7] - m[12]*m[3]*m[5];
        inv[14] = -m[0]*m[5]*m[14] + m[0]*m[6]*m[13] + m[4]*m[1]*m[14] - m[4]*m[2]*m[13] - m[12]*m[1]*m[6] + m[12]*m[2]*m[5];

        inv[3] = -m[1]*m[6]*m[11] + m[1]*m[7]*m[10] + m[5]*m[2]*m[11] - m[5]*m[3]*m[10] - m[9]*m[2]*m[7] + m[9]*m[3]*m[6];
        inv[7] = m[0]*m[6]*m[11] - m[0]*m[7]*m[10] - m[4]*m[2]*m[11] + m[4]*m[3]*m[10] + m[8]*m[2]*m[7] - m[8]*m[3]*m[6];
        inv[11] = -m[0]*m[5]*m[11] + m[0]*m[7]*m[9] + m[4]*m[1]*m[11] - m[4]*m[3]*m[9] - m[8]*m[1]*m[7] + m[8]*m[3]*m[5];
        inv[15] = m[0]*m[5]*m[10] - m[0]*m[6]*m[9] - m[4]*m[1]*m[10] + m[4]*m[2]*m[9] + m[8]*m[1]*m[6] - m[8]*m[2]*m[5];

        double det = m[0]*inv[0] + m[1]*inv[4] + m[2]*inv[8] + m[3]*inv[12];
        if (Math.Abs(det) < 1e-18) return Identity();
        double invDet = 1.0 / det;
        for (int i = 0; i < 16; i++) inv[i] *= invDet;
        return new Mat4(inv);
    }

    /// <summary>Translation column (world position) — handy for the armature Null and tests.</summary>
    public (double x, double y, double z) TranslationPart() => (M[12], M[13], M[14]);
}
