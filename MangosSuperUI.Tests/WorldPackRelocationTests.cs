using System.Text.Json.Nodes;
using MangosSuperUI.Services.Mpq;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// Region move (2026-09-27): Gilneas was its own map behind a teleport at the Greymane Wall; zones in WoW are
/// seamless continent land. WorldPackRelocation moves every doc of a region by whole ADT tiles to another map.
/// </summary>
public class WorldPackRelocationTests
{
    // Gilneas: map 800 cols 30-33 rows 30-34 → Eastern Kingdoms (map 0) cols 27-30 rows 35-39.
    private static readonly RelocateSpec Spec = new(800, 0, -3, +5, ToStockMap: true);

    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Offset_IsWholeTiles_XSouthYWest()
    {
        Assert.Equal(-5 * WorldCoords.Tile, Spec.Dx, 3);   // five rows south = world X down
        Assert.Equal(+3 * WorldCoords.Tile, Spec.Dy, 3);   // three cols west = world Y up
    }

    [Fact]
    public void TileDoc_MovesKeyGridAndPointFields()
    {
        var c = WorldPackRelocation.Transform("tile", "800:32:34",
            J("""{"map":800,"col":32,"row":34,"sourceMap":"Azeroth","sourceCol":29,"sourceRow":33,"healHoles":[{"x":-21.0,"y":46.0}],"areaPaint":[{"areaId":7007,"x":-1300,"y":-66,"radius":150}]}"""), Spec);
        Assert.Equal(2, c.Count);
        Assert.Equal(("tile", "800:32:34", (string?)null), (c[0].Kind, c[0].Key, c[0].Body));
        Assert.Equal("0:29:39", c[1].Key);
        var b = J(c[1].Body!);
        Assert.Equal(0, (int)b["map"]!); Assert.Equal(29, (int)b["col"]!); Assert.Equal(39, (int)b["row"]!);
        Assert.Equal("Azeroth", (string)b["sourceMap"]!);                     // the SOURCE never moves
        Assert.Equal(-21.0 + Spec.Dx, (double)b["healHoles"]![0]!["x"]!, 2);
        Assert.Equal(-66.0 + Spec.Dy, (double)b["areaPaint"]![0]!["y"]!, 2);
    }

    [Fact]
    public void Spawn_MovesMapAndPosition_KeepsOrientationAndZ()
    {
        var c = WorldPackRelocation.Transform("dbrow:creature", "1500000",
            J("""{"guid":1500000,"id":7000100,"map":800,"position_x":"-921.5","position_y":-56,"position_z":17.4,"orientation":1.2}"""), Spec);
        var b = J(Assert.Single(c).Body!);
        Assert.Equal(0, (int)b["map"]!);
        Assert.Equal(-921.5 + Spec.Dx, (double)b["position_x"]!, 2);
        Assert.Equal(-56 + Spec.Dy, (double)b["position_y"]!, 2);
        Assert.Equal(17.4, (double)b["position_z"]!, 3);
        Assert.Equal(1.2, (double)b["orientation"]!, 3);
    }

    [Fact]
    public void OtherMapsDocs_AreUntouched()
    {
        Assert.Empty(WorldPackRelocation.Transform("dbrow:creature", "1500300",
            J("""{"guid":1500300,"map":801,"position_x":1,"position_y":2}"""), Spec));   // the instance stays
        Assert.Empty(WorldPackRelocation.Transform("dbrow:creature_template", "7000100", J("""{"entry":7000100}"""), Spec));
    }

    [Fact]
    public void Dbc_TriggerAndGraveyard_MoveWithFloatFields_AreaTableChangesContinent()
    {
        var trig = J(Assert.Single(WorldPackRelocation.Transform("dbc:AreaTrigger", "7012",
            J("""{"fields":{"1":800,"2":{"f":-767},"3":{"f":-39},"4":{"f":62},"5":{"f":7}}}"""), Spec)).Body!);
        Assert.Equal(0, (int)trig["fields"]!["1"]!);
        Assert.Equal(-767 + Spec.Dx, (double)trig["fields"]!["2"]!["f"]!, 2);
        Assert.Equal(62, (double)trig["fields"]!["4"]!["f"]!, 3);                // z untouched
        var area = J(Assert.Single(WorldPackRelocation.Transform("dbc:AreaTable", "7001",
            J("""{"cloneFrom":130,"fields":{"1":800,"2":7001,"11":"Duskhaven"}}"""), Spec)).Body!);
        Assert.Equal(0, (int)area["fields"]!["1"]!);
    }

    [Fact]
    public void Teleport_TargetMoves_PackMapAndItsLightAreDeleted()
    {
        var tp = J(Assert.Single(WorldPackRelocation.Transform("dbrow:areatrigger_teleport", "7013|0",
            J("""{"id":7013,"target_map":800,"target_position_x":-745,"target_position_y":-60,"target_position_z":49.3}"""), Spec)).Body!);
        Assert.Equal(0, (int)tp["target_map"]!);
        Assert.Equal(-60 + Spec.Dy, (double)tp["target_position_y"]!, 2);
        Assert.Null(Assert.Single(WorldPackRelocation.Transform("map", "800", J("""{"mapId":800,"directory":"Gilneas"}"""), Spec)).Body);
        Assert.Null(Assert.Single(WorldPackRelocation.Transform("dbc:Map", "800", J("""{"fields":{"1":"Gilneas"}}"""), Spec)).Body);
        Assert.Null(Assert.Single(WorldPackRelocation.Transform("dbrow:map_template", "800|0", J("""{"entry":800,"ghost_entrance_map":-1}"""), Spec)).Body);
        Assert.Null(Assert.Single(WorldPackRelocation.Transform("dbc:Light", "7001", J("""{"cloneFrom":1,"fields":{"1":800}}"""), Spec)).Body);
        Assert.Empty(WorldPackRelocation.Transform("dbc:Light", "7002", J("""{"cloneFrom":1,"fields":{"1":801}}"""), Spec));
    }

    [Fact]
    public void InstanceGhostEntrance_OnTheMovedMap_Follows()
    {
        var mt = J(Assert.Single(WorldPackRelocation.Transform("dbrow:map_template", "801|0",
            J("""{"entry":801,"ghost_entrance_map":800,"ghost_entrance_x":-767,"ghost_entrance_y":-39}"""), Spec)).Body!);
        Assert.Equal(0, (int)mt["ghost_entrance_map"]!);
        Assert.Equal(-39 + Spec.Dy, (double)mt["ghost_entrance_y"]!, 2);
    }

    /// <summary>A land stamp over open sea, sunk below sea level, takes the stock tile's ocean where it dips.</summary>
    [Fact]
    public void CarryLiquid_FillsOnlyChunksBelowTheStockSea()
    {
        var data = DataDir();
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        var sea = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", 29, 36))!, 29, 36);   // open sea, z -515
        var land = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", 29, 32))!, 29, 32);  // Silverpine
        land.Relocate(29, 36, keepDoodads: false, keepWmos: false, uid => uid);
        int before = Enumerable.Range(0, 256).Count(i => land.LiquidLevel(i) != null);
        // Sink the west half of the tile far under the sea; the east half stays dry land.
        var deltas = new Dictionary<int, float>();
        for (int r = 0; r < 129; r++) for (int c = 0; c < 60; c++) deltas[r * 129 + c] = -400f;
        land.ApplySculpt(deltas);
        int filled = land.CarryLiquidFrom(sea);
        Assert.True(filled >= 16 * 7, $"only {filled} chunk(s) took the sea");
        Assert.True(filled <= 256 - 16 * 7, "chunks above the sea must stay dry");
        var reparsed = AdtDocument.Parse(land.Write(), 29, 36);
        int wet = Enumerable.Range(0, 256).Count(i => reparsed.LiquidLevel(i) is float l && MathF.Abs(l) < 0.01f);
        Assert.Equal(before + filled, Enumerable.Range(0, 256).Count(i => reparsed.LiquidLevel(i) != null));
        Assert.True(wet >= filled);
        Assert.Equal(land.OuterHeights(), reparsed.OuterHeights());
    }

    private static string? DataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var c = Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data");
            if (File.Exists(Path.Combine(c, "terrain.MPQ"))) return c;
            dir = dir.Parent;
        }
        return null;
    }
}
