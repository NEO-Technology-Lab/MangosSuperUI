using System.Buffers.Binary;
using System.Numerics;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>Local-space bounding boxes of WMO roots (MOHD) and M2s (header 0xB4), for MODF
/// extents and MCRF overlap. Pure; reads through a caller-supplied file reader.</summary>
public static class ModelBounds
{
    public static (Vector3 min, Vector3 max)? Wmo(byte[]? root)
    {
        if (root == null) return null;
        int at = 0;
        while (at + 8 <= root.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(at + 4));
            if (root[at] == 'D' && root[at + 1] == 'H' && root[at + 2] == 'O' && root[at + 3] == 'M' && size >= 0x3C)
            {
                int d = at + 8;
                return (Vec(root, d + 0x24), Vec(root, d + 0x30));
            }
            if (size < 0) break;
            at += 8 + size;
        }
        return null;
    }

    public static (Vector3 min, Vector3 max)? M2(byte[]? model)
    {
        if (model == null || model.Length < 0xCC || model[0] != 'M' || model[1] != 'D') return null;
        return (Vec(model, 0xB4), Vec(model, 0xC0));
    }

    /// <summary>
    /// Height (model space, z up) of the largest upward-facing walkable surface of an M2's COLLISION mesh
    /// (vanilla header: nBoundingTriangles/ofs at 0xEC/0xF0 as u16 indices, nBoundingVertices/ofs at
    /// 0xF4/0xF8). For a dock that is the deck - its posts and bounding box top are not. Null when the
    /// model has no upward-facing collision. Coplanar faces are grouped by their oriented plane;
    /// the selected surface's area-weighted actual height is returned.
    /// A sloping surface needs per-point triangle sampling to assess its shore connection.
    /// </summary>
    public static float? M2WalkableTop(byte[]? m) => M2WalkableSurface(m)?.Center.Z;

    public sealed record WalkableSurface(Vector3 Center, Vector3 Normal, float Area, Vector3[] Vertices)
    {
        public float MinimumHeight => Vertices.Min(v => v.Z);
        public float MaximumHeight => Vertices.Max(v => v.Z);
        public float SlopeDegrees => MathF.Acos(Math.Clamp(Normal.Z, -1, 1)) * 180f / MathF.PI;
    }

    /// <summary>Dominant upward collision plane, including its real vertices for transformed span/slope audits.</summary>
    public static WalkableSurface? M2WalkableSurface(byte[]? m)
    {
        if (m == null || m.Length < 0x100 || m[0] != 'M' || m[1] != 'D') return null;
        int nTri = BitConverter.ToInt32(m, 0xEC), ofsTri = BitConverter.ToInt32(m, 0xF0);
        int nVert = BitConverter.ToInt32(m, 0xF4), ofsVert = BitConverter.ToInt32(m, 0xF8);
        if (nTri <= 0 || nVert <= 0 || ofsTri + nTri * 2 > m.Length || ofsVert + nVert * 12 > m.Length) return null;
        var planes = new Dictionary<(int nx, int ny, int nz, int d),
            List<(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float area)>>();
        for (int t = 0; t + 2 < nTri; t += 3)
        {
            Vector3 P(int k) { int i = BitConverter.ToUInt16(m, ofsTri + (t + k) * 2); return i < nVert ? Vec(m, ofsVert + i * 12) : Vector3.Zero; }
            var a = P(0); var b = P(1); var c = P(2);
            var n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            // Raw M2 collision vertices use Z up and outward triangle winding. Taking Abs(n.Z)
            // counts a dock's underside, which can have exactly the same area as its deck.
            if (len < 1e-5f || n.Z / len < 0.9f) continue;
            var normal = n / len;
            // Two triangles on a ramp have different centroid heights but belong to one plane.
            var key = ((int)MathF.Round(normal.X * 1000), (int)MathF.Round(normal.Y * 1000),
                (int)MathF.Round(normal.Z * 1000), (int)MathF.Round(Vector3.Dot(normal, a) * 100));
            if (!planes.TryGetValue(key, out var faces)) planes[key] = faces = [];
            faces.Add((a, b, c, normal, len * .5f));
        }
        return planes.Values.Select(faces =>
        {
            float area = faces.Sum(f => f.area);
            var center = faces.Aggregate(Vector3.Zero, (sum, f) => sum + (f.a + f.b + f.c) * (f.area / 3)) / area;
            var normal = Vector3.Normalize(faces.Aggregate(Vector3.Zero, (sum, f) => sum + f.normal * f.area));
            return new WalkableSurface(center, normal, area, faces.SelectMany(f => new[] { f.a, f.b, f.c }).ToArray());
        }).OrderByDescending(s => s.Area).ThenByDescending(s => s.Center.Z).FirstOrDefault();
    }

    private static Vector3 Vec(byte[] d, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 8)));

    /// <summary>
    /// Placement-space AABB of a WMO placed with MODF rotation <paramref name="rotDeg"/> at
    /// <paramref name="pos"/>. Mirrors MSUIClient WmoRenderer.BuildPlacement: model basis
    /// (x,y,z)→(x,z,−y), then RotX(rotZ)·RotZ(−rotX)·RotY(rotY−90) (row-vector order).
    /// </summary>
    public static (Vector3 min, Vector3 max) WmoExtents(Vector3 localMin, Vector3 localMax, Vector3 pos, Vector3 rotDeg)
    {
        const float deg = MathF.PI / 180f;
        var basis = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var m = basis
              * Matrix4x4.CreateRotationX(rotDeg.Z * deg)
              * Matrix4x4.CreateRotationZ(-rotDeg.X * deg)
              * Matrix4x4.CreateRotationY((rotDeg.Y - 90f) * deg)
              * Matrix4x4.CreateTranslation(pos);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? localMin.X : localMax.X,
                (c & 2) == 0 ? localMin.Y : localMax.Y,
                (c & 4) == 0 ? localMin.Z : localMax.Z);
            var p = Vector3.Transform(corner, m);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }
}
