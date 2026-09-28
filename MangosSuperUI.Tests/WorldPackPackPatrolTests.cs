using System.Text.Json.Nodes;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// Linked packs (creature_groups) and patrols (movement_type 2 + creature_movement) in World Content Packs:
/// only pack spawns may be linked, and verifier C11 catches the setups that silently misbehave in game
/// (a patrol with no route, a member far from its leader, a leader without its own row).
/// </summary>
public class WorldPackPackPatrolTests
{
    private static DocRow Spawn(uint guid, float x, float y, int movement = 0) => new()
    {
        PackId = 1, Kind = "dbrow:creature", DocKey = guid.ToString(),
        Body = $"{{\"guid\":{guid},\"id\":7000300,\"map\":801,\"position_x\":{x},\"position_y\":{y},\"position_z\":68,\"movement_type\":{movement}}}",
    };
    private static DocRow Link(uint leader, uint member, int flags = 14) => new()
    {
        PackId = 1, Kind = "dbrow:creature_groups", DocKey = member.ToString(),
        Body = $"{{\"leader_guid\":{leader},\"member_guid\":{member},\"dist\":0,\"angle\":0,\"flags\":{flags}}}",
    };
    private static DocRow Point(uint guid, int point, float x, float y) => new()
    {
        PackId = 1, Kind = "dbrow:creature_movement", DocKey = $"{guid}|{point}",
        Body = $"{{\"id\":{guid},\"point\":{point},\"position_x\":{x},\"position_y\":{y},\"position_z\":68,\"waittime\":0}}",
    };

    private static List<AuditFinding> C11(params DocRow[] docs) =>
        new WorldPackAudit(new WorldPackAudit.AuditInput
        {
            Stock = _ => null, Built = _ => null, MapDirs = new(), Placements = new(), Docs = docs.ToList(),
        }).RunContent().Where(f => f.Check == "C11").ToList();

    [Fact]
    public void RowKey_GroupsLinkOnlyPackSpawns()
    {
        Assert.Equal("1500201", WorldPackContent.RowKey("creature_groups",
            JsonNode.Parse("{\"leader_guid\":1500200,\"member_guid\":1500201,\"dist\":3,\"angle\":1,\"flags\":14}")!.AsObject()));
        Assert.Throws<ArgumentException>(() => WorldPackContent.RowKey("creature_groups",
            JsonNode.Parse("{\"leader_guid\":51427,\"member_guid\":1500201,\"dist\":3,\"angle\":1,\"flags\":14}")!.AsObject()));
    }

    [Fact]
    public void LinkedPack_WithLeaderRow_IsClean() =>
        Assert.Empty(C11(Spawn(1500200, 0, 0), Spawn(1500201, 3, 0), Spawn(1500202, 0, 3),
            Link(1500200, 1500200), Link(1500200, 1500201), Link(1500200, 1500202)));

    [Fact]
    public void FarMember_AndMissingLeaderRow_AreWarnings()
    {
        var f = C11(Spawn(1500200, 0, 0), Spawn(1500201, 60, 0), Link(1500200, 1500201));
        Assert.Contains(f, x => x.Severity == "warn" && x.Message.Contains("from its leader"));
        Assert.Contains(f, x => x.Severity == "warn" && x.Message.Contains("no row of its own"));
    }

    [Fact]
    public void Patrol_WithoutRoute_IsAnError_AndARouteIsClean()
    {
        Assert.Contains(C11(Spawn(1500300, 0, 0, movement: 2)), x => x.Severity == "error" && x.Message.Contains("no waypoints"));
        Assert.Empty(C11(Spawn(1500300, 0, 0, movement: 2), Point(1500300, 1, 0, 0), Point(1500300, 2, 30, 0), Point(1500300, 3, 30, 30)));
    }

    [Fact]
    public void LongLeg_AndIgnoredWaypoints_AreWarnings()
    {
        Assert.Contains(C11(Spawn(1500300, 0, 0, movement: 2), Point(1500300, 1, 0, 0), Point(1500300, 2, 120, 0)),
            x => x.Severity == "warn" && x.Message.Contains("long legs"));
        Assert.Contains(C11(Spawn(1500300, 0, 0), Point(1500300, 1, 0, 0), Point(1500300, 2, 20, 0)),
            x => x.Severity == "warn" && x.Message.Contains("ignored"));
    }

    [Fact]
    public void FitsColumn_UnsignedFloatsRefuseNegatives()
    {
        // Build #28: creature_groups.angle is FLOAT UNSIGNED; an Atan2 angle of -1.9 passed the integer-only pre-check
        // and MySQL refused the row half-way through the (MyISAM, non-transactional) install.
        Assert.False(WorldPackBuildService.FitsColumn("float unsigned", -1.9));
        Assert.True(WorldPackBuildService.FitsColumn("float unsigned", 4.38));
        Assert.True(WorldPackBuildService.FitsColumn("float", -1.9));
        Assert.False(WorldPackBuildService.FitsColumn("double unsigned", -0.01));
        Assert.False(WorldPackBuildService.FitsColumn("int(11) unsigned", -1));
        Assert.False(WorldPackBuildService.FitsColumn("smallint(5) unsigned", 70000));
        Assert.True(WorldPackBuildService.FitsColumn("bigint(20) unsigned", 5e9));
        Assert.False(WorldPackBuildService.FitsColumn("bigint(20) unsigned", -5));
    }
}
