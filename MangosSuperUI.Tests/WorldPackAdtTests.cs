using System.Numerics;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// World Builder ADT writer (MSUIClient shared_docs/WORLD_BUILDER.md). Runs against the stock
/// archives in the sibling MSUIClient checkout (GameData\Data) and is a no-op where they are absent.
/// </summary>
public class WorldPackAdtTests
{
    private static string? DataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data");
            if (File.Exists(Path.Combine(candidate, "terrain.MPQ"))) return Path.GetFullPath(candidate);
            dir = dir.Parent;
        }
        return null;
    }

    // Northshire Valley: Azeroth_32_48.adt.
    private const int Col = 32, Row = 48;

    [Fact]
    public void UntouchedAdt_RoundTripsByteForByte()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        foreach (var (col, row) in new[] { (32, 48), (31, 48), (38, 34), (40, 34) })
        {
            var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", col, row))
                        ?? set.ReadFile(WorldCoords.AdtPath("Kalimdor", col, row));
            Assert.NotNull(bytes);
            var doc = AdtDocument.Parse(bytes!, col, row);
            Assert.Equal(bytes, doc.Write());
        }
    }

    [Fact]
    public void RecomputedNormals_MatchStockNormals()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!;
        var doc = AdtDocument.Parse(bytes, Col, Row);
        double sumDot = 0; int n = 0;
        for (int i = 0; i < 256; i += 17)
        {
            var before = doc.NormalsOf(i);
            doc.MarkNormalsDirty(i);
            doc.RecomputeDirtyNormals();
            var after = doc.NormalsOf(i);
            for (int v = 0; v < 145; v++)
            {
                var a = Vector3.Normalize(new Vector3((sbyte)before[v * 3], (sbyte)before[v * 3 + 1], (sbyte)before[v * 3 + 2]));
                var b = Vector3.Normalize(new Vector3((sbyte)after[v * 3], (sbyte)after[v * 3 + 1], (sbyte)after[v * 3 + 2]));
                sumDot += Vector3.Dot(a, b); n++;
            }
        }
        // Same convention ⇒ mean cosine close to 1 (a flipped axis would drag it well below).
        Assert.True(sumDot / n > 0.97, $"mean normal cosine {sumDot / n:F4}");
    }

    [Fact]
    public void Sculpt_MovesTheVertexAndSurvivesReparse()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!;
        var doc = AdtDocument.Parse(bytes, Col, Row);
        float before = doc.OuterHeight(64, 64);
        float edge = doc.OuterHeight(64, 72);   // a chunk-border vertex (shared by 2 MCNKs)
        doc.ApplySculpt(new Dictionary<int, float> { [64 * 129 + 64] = 5f, [64 * 129 + 72] = -3f });
        var re = AdtDocument.Parse(doc.Write(), Col, Row);
        Assert.Equal(before + 5f, re.OuterHeight(64, 64), 3);
        Assert.Equal(edge - 3f, re.OuterHeight(64, 72), 3);
    }

    [Fact]
    public void ChunkOrigins_FollowTheProvenMapping_AndRelocate()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var doc = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!, Col, Row);
        for (int i = 0; i < 256; i += 37)
        {
            var c = doc.ChunkInfo(i);
            Assert.Equal((32 - Row) * WorldCoords.Tile - c.iy * WorldCoords.Chunk, c.ox, 1);
            Assert.Equal((32 - Col) * WorldCoords.Tile - c.ix * WorldCoords.Chunk, c.oy, 1);
        }
        int wmos = doc.WmoCount;
        doc.Relocate(30, 31, keepObjects: true, uid => uid + 1);
        doc.SetAreaId(7001);
        var re = AdtDocument.Parse(doc.Write(), 30, 31);
        var moved = re.ChunkInfo(17);
        Assert.Equal((32 - 31) * WorldCoords.Tile - moved.iy * WorldCoords.Chunk, moved.ox, 1);
        Assert.Equal((32 - 30) * WorldCoords.Tile - moved.ix * WorldCoords.Chunk, moved.oy, 1);
        Assert.Equal(7001u, moved.area);
        Assert.Equal(wmos, re.WmoCount);

        var bare = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!, Col, Row);
        bare.Relocate(30, 31, keepObjects: false, uid => uid);
        var bareRe = AdtDocument.Parse(bare.Write(), 30, 31);
        Assert.Equal(0, bareRe.WmoCount);
        Assert.Equal(0, bareRe.DoodadCount);
    }

    [Fact]
    public void DropWmos_RemovesOnlyTheNamedBuilding()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        // Silverpine 29,32 holds Pyrewood's houses AND the Shadowfang Keep exterior.
        var doc = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", 29, 32))!, 29, 32);
        int before = doc.WmoCount;
        Assert.Contains(doc.WmoNames, n => n.EndsWith("ld_shadowfang.wmo", StringComparison.OrdinalIgnoreCase));
        int removed = doc.DropWmos(p => p.EndsWith("ld_shadowfang.wmo", StringComparison.OrdinalIgnoreCase));
        Assert.True(removed >= 1);
        var re = AdtDocument.Parse(doc.Write(), 29, 32);
        Assert.Equal(before - removed, re.WmoCount);
        Assert.DoesNotContain(re.WmoNames, n => n.EndsWith("ld_shadowfang.wmo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(re.WmoNames, n => n.EndsWith("duskwood_townhall_nowall.wmo", StringComparison.OrdinalIgnoreCase));
        // Every surviving placement keeps ITS model at its position (names are renumbered, not shuffled).
        var want = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", 29, 32))!, 29, 32).WmoPlacementsFull()
            .Where(w => !w.path.EndsWith("ld_shadowfang.wmo", StringComparison.OrdinalIgnoreCase))
            .Select(w => (w.path.ToLowerInvariant(), w.pos, w.rot)).ToList();
        Assert.Equal(want, re.WmoPlacementsFull().Select(w => (w.path.ToLowerInvariant(), w.pos, w.rot)).ToList());
    }

    [Fact]
    public void Sculpt_LeavesEveryNormalOutsideItsStencilByteIdentical()
    {
        // Owner report 2026-09-26 ("the ground is different colours"): a sculpt used to rewrite every
        // normal of every touched chunk (+1 ring) with our formula, drifting up to 4/127 from Blizzard's
        // baked normals chunk-wide. Only normals that depend on a moved height may change now.
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!;
        var stock = AdtDocument.Parse(bytes, Col, Row);
        var doc = AdtDocument.Parse(bytes, Col, Row);
        doc.ApplySculpt(new Dictionary<int, float> { [70 * 129 + 36] = 3f, [70 * 129 + 37] = 2f, [71 * 129 + 36] = 2f });
        var re = AdtDocument.Parse(doc.Write(), Col, Row);
        int changedOutside = 0, changedInside = 0;
        for (int i = 0; i < 256; i++)
        {
            var (ix, iy, _, _, _) = stock.ChunkInfo(i);
            byte[] a = stock.NormalsOf(i), b = re.NormalsOf(re.ChunkIndex(ix, iy));
            for (int v = 0; v < 145; v++)
            {
                bool innerV = v % 17 >= 9;
                int r = v / 17, c = innerV ? v % 17 - 9 : v % 17;
                int gr = iy * 8 + r, gc = ix * 8 + c;
                bool near = Math.Abs(gr - 70) <= 3 && Math.Abs(gc - 36) <= 3;
                bool diff = a[v * 3] != b[v * 3] || a[v * 3 + 1] != b[v * 3 + 1] || a[v * 3 + 2] != b[v * 3 + 2];
                if (diff && !near) changedOutside++;
                if (diff && near) changedInside++;
            }
        }
        Assert.Equal(0, changedOutside);
        Assert.True(changedInside > 0);
    }

    [Fact]
    public void DropDoodads_RemovesSelectedAndKeepsTheRestInPlace()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", 29, 32))!;
        var doc = AdtDocument.Parse(bytes, 29, 32);
        var before = doc.DoodadPlacements().ToList();
        bool Tree(string p) => p.Contains("tree", StringComparison.OrdinalIgnoreCase);
        int removed = doc.DropDoodads((p, _) => Tree(p));
        Assert.Equal(before.Count(d => Tree(d.path)), removed);
        Assert.True(removed > 0);
        var re = AdtDocument.Parse(doc.Write(), 29, 32);
        Assert.Equal(before.Where(d => !Tree(d.path)).Select(d => (d.path.ToLowerInvariant(), d.pos, d.rot, d.scale)),
            re.DoodadPlacements().Select(d => (d.path.ToLowerInvariant(), d.pos, d.rot, d.scale)));
        Assert.Equal(0, AdtDocument.Parse(bytes, 29, 32).DropDoodads((_, _) => false));
    }

    [Fact]
    public void HealHoleAt_ClosesExactlyThatSquare()
    {
        // 2026-09-27: stock hole squares a building leaves partly open (the Arathi-style entrance copied
        // into Greymane Fortress) - healing one must clear that one bit and touch nothing else.
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", 36, 34))!;
        var doc = AdtDocument.Parse(bytes, 36, 34);
        int chunk = Enumerable.Range(0, 256).First(i => doc.HolesOf(i) != 0);
        ushort before = doc.HolesOf(chunk);
        int bit = Enumerable.Range(0, 16).First(b => (before & (1 << b)) != 0);
        var (_, _, ox, oy, _) = doc.ChunkInfo(chunk);
        float x = ox - (bit / 4 + 0.5f) * WorldCoords.Chunk / 4f, y = oy - (bit % 4 + 0.5f) * WorldCoords.Chunk / 4f;   // G12's centre
        Assert.True(doc.HealHoleAt(x, y));
        Assert.Equal((ushort)(before & ~(1 << bit)), doc.HolesOf(chunk));
        Assert.False(doc.HealHoleAt(x, y));                     // already closed
        var after = doc.Write();
        Assert.Equal(bytes.Length, after.Length);
        Assert.True(Enumerable.Range(0, bytes.Length).Count(i => bytes[i] != after[i]) is 1 or 2);   // the one mask word
        Assert.False(doc.HealHoleAt(x + 5000f, y));            // off this tile
    }

    [Fact]
    public void ReplaceArea_RetagsOnlyThatAreasChunks()
    {
        // 2026-09-27: the Gilneas pass (tile 29,34) is land a pack raised out of the sea, and every chunk still
        // said 2397 "The Great Sea". areaReplace re-tags exactly those chunks and nothing else.
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", 30, 33))!;   // Silverpine/Hillsbrad + some Great Sea
        var doc = AdtDocument.Parse(bytes, 30, 33);
        uint AreaOf(int i) => doc.ChunkInfo(i).Item5;
        var before = Enumerable.Range(0, 256).Select(AreaOf).ToArray();
        int sea = before.Count(a => a == 2397);
        Assert.True(sea > 0);
        Assert.Equal(sea, doc.ReplaceArea(2397, 7001));
        for (int i = 0; i < 256; i++)
            Assert.Equal(before[i] == 2397 ? 7001u : before[i], AreaOf(i));
        Assert.Equal(0, doc.ReplaceArea(2397, 7001));
        Assert.Equal(bytes.Length, doc.Write().Length);
    }

    [Fact]
    public void DropWmos_NoMatch_LeavesTheTileByteIdentical()
    {
        // Regression 2026-09-26: a tile WITHOUT the dropped WMO had its MODF name indices renumbered
        // but kept the old name table — Gilneas' farmhouses, barns and the shipwreck swapped models.
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", 29, 29))!;
        var doc = AdtDocument.Parse(bytes, 29, 29);
        Assert.True(doc.WmoCount >= 3);
        Assert.Equal(0, doc.DropWmos(p => p.EndsWith("ld_shadowfang.wmo", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(bytes, doc.Write());
    }

    [Fact]
    public void Wdt_MarksTilesRowMajor()
    {
        var wdt = AdtDocument.BuildWdt(new[] { (30, 31), (31, 31) });
        // MVER(12) + MPHD(8+32) → MAIN data starts at 52 + 8.
        int main = 12 + 40 + 8;
        Assert.Equal(1, BitConverter.ToInt32(wdt, main + (31 * 64 + 30) * 8));
        Assert.Equal(1, BitConverter.ToInt32(wdt, main + (31 * 64 + 31) * 8));
        Assert.Equal(0, BitConverter.ToInt32(wdt, main + (30 * 64 + 31) * 8));
    }

    [Fact]
    public void Placements_AppendModfMddfAndReparse()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var bytes = set.ReadFile(WorldCoords.AdtPath("Azeroth", Col, Row))!;
        var doc = AdtDocument.Parse(bytes, Col, Row);
        int wmos = doc.WmoCount, doodads = doc.DoodadCount;
        var world = new Vector3(-8914f, -135f, 80f);            // Northshire Abbey courtyard
        var p = WorldCoords.WorldToPlacement(world);
        Assert.Equal(Col, (int)(p.X / WorldCoords.Tile));
        Assert.Equal(Row, (int)(p.Z / WorldCoords.Tile));
        doc.AddWmo(@"World\wmo\Azeroth\Buildings\Human_Farm\Farm.wmo", 7_000_001, p, new Vector3(0, 45, 0),
            p - new Vector3(20), p + new Vector3(20), 0);
        doc.AddDoodad(@"World\Azeroth\Elwynn\PassiveDoodads\Trees\ElwynnTreeCanopy01.mdx", 7_000_002, p,
            Vector3.Zero, 1.5f, 12f);
        var re = AdtDocument.Parse(doc.Write(), Col, Row);
        Assert.Equal(wmos + 1, re.WmoCount);
        Assert.Equal(doodads + 1, re.DoodadCount);
        // And a second write of the re-parsed document is stable.
        Assert.Equal(doc.Write(), re.Write());
    }
}
