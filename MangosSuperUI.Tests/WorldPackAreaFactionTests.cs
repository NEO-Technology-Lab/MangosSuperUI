using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// Verifier C10, area faction: AreaTable field 20 (FactionGroupMask) is chosen per pack area, and the client's
/// value agrees with the server's area_template.team. Gilneas cloned Silverpine and inherited its Horde 4, so the
/// minimap painted Duskhaven red for Alliance players while the server said 0 (2026-09-27).
/// </summary>
public class WorldPackAreaFactionTests
{
    private static List<AuditFinding> Audit(string areaFields, int team) =>
        new WorldPackAudit(new WorldPackAudit.AuditInput
        {
            Stock = _ => null, Built = _ => null, MapDirs = new(), Placements = new(),
            Docs = new()
            {
                new DocRow { PackId = 1, Kind = "dbc:AreaTable", DocKey = "7002",
                    Body = $"{{\"cloneFrom\":130,\"fields\":{{\"1\":0,\"2\":7001,\"11\":\"Duskhaven\"{areaFields}}}}}" },
                new DocRow { PackId = 1, Kind = "dbrow:area_template", DocKey = "7002",
                    Body = $"{{\"entry\":7002,\"map_id\":0,\"zone_id\":7001,\"name\":\"Duskhaven\",\"team\":{team}}}" },
            },
        }).RunContent().Where(f => f.Check == "C10" && f.Subject == "area 7002").ToList();

    [Fact]
    public void InheritedFaction_IsAWarning()
    {
        var f = Assert.Single(Audit("", 0));
        Assert.Equal("warn", f.Severity);
        Assert.Contains("inherited from clone source 130", f.Message);
    }

    [Fact]
    public void ClientAndServerFactionsDisagree_IsAnError()
    {
        var f = Assert.Single(Audit(",\"20\":4", 2));
        Assert.Equal("error", f.Severity);
    }

    [Fact]
    public void ChosenAndAgreeing_IsClean() => Assert.Empty(Audit(",\"20\":2", 2));
}
