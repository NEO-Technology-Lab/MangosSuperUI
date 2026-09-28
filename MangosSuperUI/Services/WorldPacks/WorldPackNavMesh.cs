using System.Numerics;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// The server's navmesh of one map as the verifier sees it: vmangos <c>.mmtile</c> files (Detour v7 tile data behind
/// the 20-byte MmapTileHeader) read without Detour, every walkable poly united into WALK-CONNECTED components through
/// its internal neighbours and the portal edges that join tiles (what Detour's connectExtLinks joins). Two points on
/// different components have no path: <c>.mmap path</c> answers INCOMPLETE, a creature there EVADES "target
/// unreachable", a player's group can never walk to it. Found by map 801 (2026-09-27): Baron Ashbury and Lord Walden
/// stood in a walled ward that was its own island - every boss trial had teleported the group in.
/// WoW (x north, y west, z up) = recast (X = y, Y = z, Z = x). Proven against the live server: the offline component
/// of every probe point matched <c>.mmap path</c> (complete = same component, incomplete = different).
/// </summary>
public sealed class WorldPackNavMesh
{
    public const ushort Ground = 0x01, Water = 0x08, Steep = 0x10;
    private const ushort ExtLink = 0x8000;
    private const float Cell = 8f;                        // spatial index cell (yd)

    private sealed class Tile
    {
        public int Tx, Ty, Base;
        public float Climb;
        public Vector3[] Verts = [];
        public (ushort[] V, ushort[] N, ushort Flags, int Type)[] Polys = [];
    }

    private readonly List<Tile> _tiles = new();
    private int[] _parent = [];
    private readonly Dictionary<int, float> _area = new();
    private readonly Dictionary<(int, int), List<(Tile t, int i)>> _index = new();

    public int Tiles => _tiles.Count;
    public int Polys => _parent.Length;

    /// <summary>Every .mmtile of the map in <paramref name="dir"/> (<c>{map:D3}{gx:D2}{gy:D2}.mmtile</c>), or only the
    /// grid cells named (plus their neighbours) - a continent has ~1000 tiles; a pack needs the ones it stands on.</summary>
    public static WorldPackNavMesh? Load(string dir, int map, IEnumerable<(int gx, int gy)>? cells = null)
    {
        if (!Directory.Exists(dir)) return null;
        IEnumerable<string> files;
        if (cells is null) files = Directory.EnumerateFiles(dir, $"{map:D3}*.mmtile").Where(f => Path.GetFileName(f).Length == 14);
        else
        {
            var want = new HashSet<(int, int)>();
            foreach (var (gx, gy) in cells)
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++) want.Add((gx + dx, gy + dy));
            files = want.Select(c => Path.Combine(dir, $"{map:D3}{c.Item1:D2}{c.Item2:D2}.mmtile")).Where(File.Exists);
        }
        var nav = new WorldPackNavMesh();
        foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
            if (ReadTile(File.ReadAllBytes(f)) is { } t) nav._tiles.Add(t);
        if (nav._tiles.Count == 0) return null;
        nav.Build();
        return nav;
    }

    /// <summary>The grid cell (vmangos GridMap / mmtile name) of a world point.</summary>
    public static (int gx, int gy) CellOf(float x, float y) => ((int)MathF.Floor(32f - x / 533.33333f), (int)MathF.Floor(32f - y / 533.33333f));

    internal static WorldPackNavMesh FromTiles(IEnumerable<byte[]> tiles)
    {
        var nav = new WorldPackNavMesh();
        foreach (var b in tiles) if (ReadTile(b) is { } t) nav._tiles.Add(t);
        nav.Build();
        return nav;
    }

    private static Tile? ReadTile(byte[] d)
    {
        if (d.Length < 120) return null;
        int o = 20;                                               // MmapTileHeader: magic, dtVersion, mmapVersion, size, usesLiquids
        int I(int k) => BitConverter.ToInt32(d, o + 4 * k);
        float Fl(int k) => BitConverter.ToSingle(d, o + 4 * k);
        if (I(0) != ('D' << 24 | 'N' << 16 | 'A' << 8 | 'V') || I(1) != 7) return null;
        var t = new Tile { Tx = I(2), Ty = I(3), Climb = Fl(17) };
        int polyCount = I(6), vertCount = I(7);
        o += 100;                                                 // dtMeshHeader
        t.Verts = new Vector3[vertCount];
        for (int i = 0; i < vertCount; i++, o += 12)
            t.Verts[i] = new Vector3(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));
        t.Polys = new (ushort[], ushort[], ushort, int)[polyCount];
        for (int i = 0; i < polyCount; i++, o += 32)            // dtPoly: firstLink, verts[6], neis[6], flags, vertCount, areaAndType
        {
            int vc = Math.Min((int)d[o + 30], 6);
            var v = new ushort[vc]; var n = new ushort[vc];
            for (int k = 0; k < vc; k++) { v[k] = BitConverter.ToUInt16(d, o + 4 + 2 * k); n[k] = BitConverter.ToUInt16(d, o + 16 + 2 * k); }
            t.Polys[i] = (v, n, BitConverter.ToUInt16(d, o + 28), d[o + 31] >> 6);
        }
        return t;
    }

    private int Find(int a)
    {
        while (_parent[a] != a) { _parent[a] = _parent[_parent[a]]; a = _parent[a]; }
        return a;
    }

    private void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _parent[a] = b; }

    private void Build()
    {
        int total = 0;
        foreach (var t in _tiles) { t.Base = total; total += t.Polys.Length; }
        _parent = Enumerable.Range(0, total).ToArray();
        var ext = new Dictionary<(int, int, int), List<(int g, Vector3 a, Vector3 b, float climb)>>();
        foreach (var t in _tiles)
            for (int i = 0; i < t.Polys.Length; i++)
            {
                var (v, n, _, type) = t.Polys[i];
                if (type != 0) continue;
                for (int j = 0; j < n.Length; j++)
                {
                    if (n[j] == 0) continue;
                    if ((n[j] & ExtLink) != 0)
                    {
                        int side = n[j] & 0xff;
                        if (!ext.TryGetValue((t.Tx, t.Ty, side), out var list)) ext[(t.Tx, t.Ty, side)] = list = new();
                        list.Add((t.Base + i, t.Verts[v[j]], t.Verts[v[(j + 1) % v.Length]], t.Climb));
                    }
                    else Union(t.Base + i, t.Base + n[j] - 1);
                }
            }
        // Portal edges: side 0 = +X neighbour, 2 = +Z, 4 = -X, 6 = -Z (recast axes); overlapping, height within climb.
        foreach (var ((tx, ty, side), edges) in ext)
        {
            var (dx, dy) = side switch { 0 => (1, 0), 2 => (0, 1), 4 => (-1, 0), 6 => (0, -1), _ => (0, 0) };
            if ((dx, dy) == (0, 0) || !ext.TryGetValue((tx + dx, ty + dy, (side + 4) & 7), out var other)) continue;
            bool alongZ = side is 0 or 4;
            foreach (var (g, a0, a1, climb) in edges)
            {
                float A(Vector3 p) => alongZ ? p.Z : p.X;
                float lo = MathF.Min(A(a0), A(a1)), hi = MathF.Max(A(a0), A(a1));
                foreach (var (g2, b0, b1, _) in other)
                {
                    float olo = MathF.Max(lo, MathF.Min(A(b0), A(b1))), ohi = MathF.Min(hi, MathF.Max(A(b0), A(b1)));
                    if (ohi - olo < 0.01f) continue;
                    static float H(Vector3 p0, Vector3 p1, float s, Func<Vector3, float> ax)
                    {
                        float den = ax(p1) - ax(p0);
                        float f = MathF.Abs(den) < 1e-6f ? 0f : (s - ax(p0)) / den;
                        return p0.Y + (p1.Y - p0.Y) * f;
                    }
                    if (MathF.Abs(H(a0, a1, olo, A) - H(b0, b1, olo, A)) <= climb + 0.5f && MathF.Abs(H(a0, a1, ohi, A) - H(b0, b1, ohi, A)) <= climb + 0.5f)
                        Union(g, g2);
                }
            }
        }
        foreach (var t in _tiles)
            for (int i = 0; i < t.Polys.Length; i++)
            {
                var v = t.Polys[i].V;
                float s = 0f, minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                for (int k = 0; k < v.Length; k++)
                {
                    var p = t.Verts[v[k]]; var q = t.Verts[v[(k + 1) % v.Length]];
                    s += p.X * q.Z - q.X * p.Z;
                    minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X); minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
                }
                int r = Find(t.Base + i);
                _area[r] = _area.GetValueOrDefault(r) + MathF.Abs(s) / 2f;
                if (v.Length == 0) continue;
                for (int cx = (int)MathF.Floor(minX / Cell); cx <= (int)MathF.Floor(maxX / Cell); cx++)
                    for (int cz = (int)MathF.Floor(minZ / Cell); cz <= (int)MathF.Floor(maxZ / Cell); cz++)
                    {
                        if (!_index.TryGetValue((cx, cz), out var bucket)) _index[(cx, cz)] = bucket = new();
                        bucket.Add((t, i));
                    }
            }
    }

    /// <summary>Walkable area (sq yd) of a component.</summary>
    public float Area(int component) => _area.GetValueOrDefault(Find(component));

    /// <summary>
    /// The component under a WoW point: the walkable poly (ground or water, not steep) whose footprint holds (x, y), the one
    /// nearest <paramref name="z"/> in height (or the highest); none under it = the nearest poly centre within
    /// <paramref name="reach"/> yd (a spawn stands on a poly's edge). Null = no navmesh here at all.
    /// </summary>
    public (int Component, float Height, ushort Flags)? At(float x, float y, float? z = null, float reach = 2.5f)
    {
        float X = y, Z = x;
        var hits = new List<(int g, float h, ushort f)>();
        if (_index.TryGetValue(((int)MathF.Floor(X / Cell), (int)MathF.Floor(Z / Cell)), out var bucket))
            foreach (var (t, i) in bucket)
            {
                var (v, _, flags, type) = t.Polys[i];
                if (type != 0 || (flags & (Ground | Water)) == 0 || (flags & Steep) != 0) continue;
                bool inside = false; float h = 0f;
                for (int k = 0, j = v.Length - 1; k < v.Length; j = k++)
                {
                    var a = t.Verts[v[k]]; var b = t.Verts[v[j]];
                    h += a.Y;
                    if ((a.Z > Z) != (b.Z > Z) && X < (b.X - a.X) * (Z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
                }
                if (inside) hits.Add((Find(t.Base + i), h / v.Length, flags));
            }
        if (hits.Count == 0)
        {
            (float d, int g, float h, ushort f)? best = null;
            int r = (int)MathF.Ceiling(reach / Cell);
            for (int cx = -r; cx <= r; cx++)
                for (int cz = -r; cz <= r; cz++)
                {
                    if (!_index.TryGetValue(((int)MathF.Floor(X / Cell) + cx, (int)MathF.Floor(Z / Cell) + cz), out var b)) continue;
                    foreach (var (t, i) in b)
                    {
                        var (v, _, flags, type) = t.Polys[i];
                        if (type != 0 || (flags & (Ground | Water)) == 0 || (flags & Steep) != 0 || v.Length == 0) continue;
                        var c = Vector3.Zero;
                        foreach (var k in v) c += t.Verts[k];
                        c /= v.Length;
                        float d = MathF.Sqrt((c.X - X) * (c.X - X) + (c.Z - Z) * (c.Z - Z));
                        if (d > reach || (z is float zz && MathF.Abs(c.Y - zz) > 6f)) continue;
                        if (best is null || d < best.Value.d) best = (d, Find(t.Base + i), c.Y, flags);
                    }
                }
            if (best is null) return null;
            return (best.Value.g, best.Value.h, best.Value.f);
        }
        var pick = z is float zq ? hits.MinBy(h => MathF.Abs(h.h - zq)) : hits.MaxBy(h => h.h);
        return (pick.g, pick.h, pick.f);
    }
}
