"""Derives and verifies the GTA V -> Unreal transform numerically.

This exists because the failure mode is invisible. A wrong handedness flip or a wrong quaternion
sign does not crash: the city loads, every asset is in a plausible spot, and the whole region is
mirrored. It reads like a rotation bug for a day. So the convention gets pinned here, by matrix
math, and everything else in the pipeline quotes this file.

The chain being verified:

  GTA world space          right-handed, Z up, metres
    -> glTF                right-handed, Y up      (what GltfModelExporter writes: x, z, -y)
    -> Unreal              left-handed,  Z up, cm  (what UE's glTF import applies)

Composed, that lands at UE = (gy, gx, gz) * 100. Placements in region.json must use the SAME
composition, or the meshes and their transforms disagree.
"""

import itertools
import math
import sys

# ── small matrix/quaternion helpers (no numpy dependency) ─────────────────────

def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]

def mat_vec(m, v):
    return [sum(m[i][k] * v[k] for k in range(3)) for i in range(3)]

def det3(m):
    return (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1])
          - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0])
          + m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0]))

def transpose(m):
    return [[m[j][i] for j in range(3)] for i in range(3)]

def quat_to_mat(q):
    """q = (x, y, z, w), unit. Returns the rotation matrix."""
    x, y, z, w = q
    return [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w),     2 * (x * z + y * w)],
        [2 * (x * y + z * w),     1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w),     2 * (y * z + x * w),     1 - 2 * (x * x + y * y)],
    ]

def mat_to_quat(m):
    """Shepperd's method. Returns (x, y, z, w) with w >= 0 for a canonical comparison."""
    t = m[0][0] + m[1][1] + m[2][2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        w, x, y, z = 0.25 * s, (m[2][1] - m[1][2]) / s, (m[0][2] - m[2][0]) / s, (m[1][0] - m[0][1]) / s
    elif m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]) * 2
        w, x, y, z = (m[2][1] - m[1][2]) / s, 0.25 * s, (m[0][1] + m[1][0]) / s, (m[0][2] + m[2][0]) / s
    elif m[1][1] > m[2][2]:
        s = math.sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]) * 2
        w, x, y, z = (m[0][2] - m[2][0]) / s, (m[0][1] + m[1][0]) / s, 0.25 * s, (m[1][2] + m[2][1]) / s
    else:
        s = math.sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]) * 2
        w, x, y, z = (m[1][0] - m[0][1]) / s, (m[0][2] + m[2][0]) / s, (m[1][2] + m[2][1]) / s, 0.25 * s
    return canonical((x, y, z, w))

def canonical(q):
    """q and -q are the same rotation. Pick the one with w >= 0 so they compare equal."""
    return tuple(-c for c in q) if q[3] < 0 else tuple(q)

def quat_close(a, b, eps=1e-9):
    return all(abs(u - v) < eps for u, v in zip(canonical(a), canonical(b)))

def normalize(q):
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q)


# ── the transforms under test ─────────────────────────────────────────────────

# What GltfModelExporter does to a vertex: (x, y, z) -> (x, z, -y)
M_GLTF = [[1, 0, 0],
          [0, 0, 1],
          [0, -1, 0]]

# What UE's glTF import does: UE.X = -gltf.Z, UE.Y = gltf.X, UE.Z = gltf.Y
M_UE_IMPORT = [[0, 0, -1],
               [1, 0, 0],
               [0, 1, 0]]

M_TOTAL = mat_mul(M_UE_IMPORT, M_GLTF)


def transform_position(p):
    """The convention the manifest must use. Metres in, centimetres out."""
    return [p[1] * 100.0, p[0] * 100.0, p[2] * 100.0]


def transform_quaternion(stored):
    """
    CEntityDef.rotation is stored as the CONJUGATE of the entity's rotation, so it is inverted
    first. Then the rotation is re-expressed in UE's basis.

    Under an improper basis change (det = -1) a rotation R becomes M R M^-1, and in quaternion
    terms the vector part is carried by M while the scalar part changes sign. For this particular
    M, that sign change cancels the conjugate's negation, which is why the result looks like a
    bare component swap. That cancellation is the whole reason this file exists: it is very easy
    to write by accident and impossible to distinguish from a bug by eye.
    """
    sx, sy, sz, sw = stored
    return (sy, sx, sz, sw)


def transform_scale(scale_xy, scale_z):
    """GTA scale is never uniform: (scaleXY, scaleXY, scaleZ). It rides through the axis swap."""
    return [scale_xy, scale_xy, scale_z]


# ── verification ──────────────────────────────────────────────────────────────

def main():
    ok = True

    def check(label, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"  {'PASS' if cond else 'FAIL'}  {label}" + (f"   {detail}" if detail and not cond else ""))

    print("Basis matrices")
    check("glTF export step is a pure rotation (det +1), NOT a handedness flip",
          abs(det3(M_GLTF) - 1) < 1e-12, f"det = {det3(M_GLTF)}")
    check("UE glTF import step flips handedness (det -1)",
          abs(det3(M_UE_IMPORT) + 1) < 1e-12, f"det = {det3(M_UE_IMPORT)}")
    check("composed GTA->UE flips handedness exactly once (det -1)",
          abs(det3(M_TOTAL) + 1) < 1e-12, f"det = {det3(M_TOTAL)}")
    check("composed map is the XY swap, UE = (gy, gx, gz)",
          M_TOTAL == [[0, 1, 0], [1, 0, 0], [0, 0, 1]], f"got {M_TOTAL}")

    print("\nPositions")
    for p in [(1, 0, 0), (0, 1, 0), (0, 0, 1), (-1663.97, -1126.74, 29.32)]:
        expect = [round(v * 100, 6) for v in mat_vec(M_TOTAL, p)]
        got = [round(v, 6) for v in transform_position(p)]
        check(f"position {p}", got == expect, f"got {got}, matrix says {expect}")

    print("\nRotations: transform_quaternion must equal M R M^-1 for the inverted stored rotation")
    Minv = transpose(M_TOTAL)   # the swap is its own inverse, and orthogonal either way
    tested = 0
    for i, (ax, ay, az) in enumerate(itertools.product([-1, 0, 1], repeat=3)):
        if (ax, ay, az) == (0, 0, 0):
            continue
        n = math.sqrt(ax * ax + ay * ay + az * az)
        axis = (ax / n, ay / n, az / n)
        for deg in (17.0, 90.0, 143.5, 231.0):
            th = math.radians(deg)
            s = math.sin(th / 2)
            # the entity's true rotation
            q_true = normalize((axis[0] * s, axis[1] * s, axis[2] * s, math.cos(th / 2)))
            # what the ymap actually stores: its conjugate
            q_stored = (-q_true[0], -q_true[1], -q_true[2], q_true[3])

            # ground truth: express the true rotation in UE's basis via matrices
            R = quat_to_mat(q_true)
            R_ue = mat_mul(mat_mul(M_TOTAL, R), Minv)
            expect = mat_to_quat(R_ue)

            got = transform_quaternion(q_stored)
            tested += 1
            if not quat_close(got, expect):
                check(f"quat axis={axis} angle={deg}", False,
                      f"got {tuple(round(c,6) for c in canonical(got))}, "
                      f"expected {tuple(round(c,6) for c in expect)}")
                return 1

    check(f"all {tested} axis/angle combinations match the matrix-derived result", True)

    print("\nRound trip: a rotated, non-uniformly scaled point lands where the matrices say")
    q_true = normalize((0.2, -0.5, 0.3, 0.78))
    q_stored = (-q_true[0], -q_true[1], -q_true[2], q_true[3])
    local = (2.0, 5.0, 1.0)                 # a point on the model, GTA space, metres
    sxy, sz = 1.5, 0.75
    origin = (-1663.97, -1126.74, 29.32)

    # GTA: scale, rotate, translate
    scaled = (local[0] * sxy, local[1] * sxy, local[2] * sz)
    world_gta = [o + c for o, c in zip(origin, mat_vec(quat_to_mat(q_true), scaled))]
    expect_ue = [round(v * 100, 6) for v in mat_vec(M_TOTAL, world_gta)]

    # UE: same operations using only what the manifest carries
    ue_scale = transform_scale(sxy, sz)
    ue_local = mat_vec(M_TOTAL, local)                      # the mesh went through the same map
    ue_scaled = [c * s for c, s in zip(ue_local, ue_scale)]
    ue_rot = quat_to_mat(transform_quaternion(q_stored))
    got_ue = [o + c * 100 for o, c in zip(transform_position(origin), mat_vec(ue_rot, ue_scaled))]
    got_ue = [round(v, 6) for v in got_ue]

    check("transformed vertex matches", all(abs(a - b) < 1e-6 for a, b in zip(got_ue, expect_ue)),
          f"got {got_ue}, expected {expect_ue}")

    print("\nSanity: the two conventions people mix up are genuinely different")
    p = (100.0, 25.0, 5.0)
    swap_xy = transform_position(p)
    negate_y = [p[0] * 100, -p[1] * 100, p[2] * 100]
    check("swap-XY and negate-Y disagree, so picking the wrong one is not harmless",
          swap_xy != negate_y, f"{swap_xy} vs {negate_y}")

    print("\nALL PASS" if ok else "\nFAILURES ABOVE")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
