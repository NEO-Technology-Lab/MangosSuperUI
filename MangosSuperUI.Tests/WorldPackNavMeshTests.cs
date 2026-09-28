using System.Numerics;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// G15 reachability reads the server navmesh (vmangos .mmtile = MmapTileHeader + Detour v7 tile data) without Detour:
/// polys joined through internal neighbours and through the portal edges between tiles are ONE walk-connected
/// component; a walled-in courtyard is its own (map 801's Ashbury ward, 2026-09-27). Tiles here are built by hand in
/// the file layout; WoW (x, y) = recast (Z, X).
/// </summary>
public class WorldPackNavMeshTests
{
    private const ushort Ext = 0x8000;

    private static byte[] Tile(int tx, int ty, Vector3[] verts, params (ushort[] v, ushort[] n)[] polys)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (uint u in new uint[] { 0x4d4d4150, 7, 13, 0, 0 }) w.Write(u);            // MmapTileHeader
        int[] ints = { 'D' << 24 | 'N' << 16 | 'A' << 8 | 'V', 7, tx, ty, 0, 0, polys.Length, verts.Length, 0, 0, 0, 0, 0, 0, 0 };
        foreach (int i in ints) w.Write(i);
        foreach (float f in new[] { 2f, 0.3f, 1.8f, 0f, 0f, 0f, 0f, 0f, 0f, 1f }) w.Write(f);   // height, radius, climb, bmin, bmax, quant
        foreach (var v in verts) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        foreach (var (v, n) in polys)
        {
            w.Write(0u);
            for (int k = 0; k < 6; k++) w.Write(k < v.Length ? v[k] : (ushort)0);
            for (int k = 0; k < 6; k++) w.Write(k < n.Length ? n[k] : (ushort)0);
            w.Write((ushort)WorldPackNavMesh.Ground); w.Write((byte)v.Length); w.Write((byte)0);
        }
        return ms.ToArray();
    }

    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void Components_JoinNeighbours_AndKeepAWalledCourtyardApart()
    {
        // A | B share the recast edge X = 10; C stands alone at X 40-50 (a courtyard behind walls).
        var verts = new[] { V(0, 0, 0), V(10, 0, 0), V(10, 0, 10), V(0, 0, 10), V(20, 0, 0), V(20, 0, 10), V(40, 0, 0), V(50, 0, 0), V(50, 0, 10), V(40, 0, 10) };
        var nav = WorldPackNavMesh.FromTiles(new[] { Tile(0, 0, verts,
            (new ushort[] { 0, 1, 2, 3 }, new ushort[] { 0, 2, 0, 0 }),
            (new ushort[] { 1, 4, 5, 2 }, new ushort[] { 0, 0, 0, 1 }),
            (new ushort[] { 6, 7, 8, 9 }, new ushort[] { 0, 0, 0, 0 })) });
        var a = nav.At(5, 5)!.Value;          // WoW x = recast Z, WoW y = recast X
        var b = nav.At(5, 15)!.Value;
        var c = nav.At(5, 45)!.Value;
        Assert.Equal(a.Component, b.Component);
        Assert.NotEqual(a.Component, c.Component);
        Assert.Equal(200f, nav.Area(a.Component), 1);
        Assert.Equal(100f, nav.Area(c.Component), 1);
        Assert.Null(nav.At(5, 30));             // no navmesh between them
    }

    [Fact]
    public void PortalEdges_JoinTiles_OnlyWithinClimb()
    {
        // Tile (0,0): X 0-10 with its X = 10 edge a portal to +X (side 0); tile (1,0): X 10-20 with the X = 10 edge to -X (4).
        byte[] left = Tile(0, 0, new[] { V(0, 0, 0), V(10, 0, 0), V(10, 0, 10), V(0, 0, 10) },
            (new ushort[] { 0, 1, 2, 3 }, new ushort[] { 0, Ext | 0, 0, 0 }));
        byte[] Right(float h) => Tile(1, 0, new[] { V(10, h, 0), V(20, h, 0), V(20, h, 10), V(10, h, 10) },
            (new ushort[] { 0, 1, 2, 3 }, new ushort[] { 0, 0, 0, Ext | 4 }));
        var level = WorldPackNavMesh.FromTiles(new[] { left, Right(0f) });
        Assert.Equal(level.At(5, 5)!.Value.Component, level.At(5, 15)!.Value.Component);
        var cliff = WorldPackNavMesh.FromTiles(new[] { left, Right(6f) });    // 6 yd step > climb 1.8
        Assert.NotEqual(cliff.At(5, 5)!.Value.Component, cliff.At(5, 15)!.Value.Component);
    }

    [Fact]
    public void CellOf_IsTheMmtileGrid()
    {
        // 801's city (x 100, y 300) lies in 8013131.mmtile; x < 0 moves to grid 32.
        Assert.Equal((31, 31), WorldPackNavMesh.CellOf(100f, 300f));
        Assert.Equal((32, 31), WorldPackNavMesh.CellOf(-55f, 330f));
    }
}
