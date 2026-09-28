using System.Net.Http;
using System.Text.Json;
using MangosSuperUI.Services.Mpq;
using MangosSuperUI.Services.WorldPacks;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>
/// Scratch probe: run the World Pack Verifier's static audit OFFLINE against the patch-7.MPQ an
/// MSUIClient downloaded (GameData/Data) with docs/placements fetched from the live web app
/// (MSUI_WEBAPP, default http://192.168.0.2:5000). No stock DB facts. Skips when either is absent.
/// </summary>
public class WorldPackVerifyProbe
{
    private readonly ITestOutputHelper _out;
    public WorldPackVerifyProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public async Task Audit()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? data = null;
        while (dir != null && data == null)
        {
            var c = Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data");
            if (File.Exists(Path.Combine(c, "patch-7.MPQ"))) data = c;
            dir = dir.Parent;
        }
        if (data == null) return;
        string web = Environment.GetEnvironmentVariable("MSUI_WEBAPP") ?? "http://192.168.0.2:5000";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        JsonElement docsJson, packsJson;
        try
        {
            docsJson = JsonDocument.Parse(await http.GetStringAsync($"{web}/WorldPacks/Docs")).RootElement;
            packsJson = JsonDocument.Parse(await http.GetStringAsync($"{web}/WorldPacks/Packs")).RootElement;
        }
        catch (HttpRequestException) { return; }

        var enabled = packsJson.GetProperty("packs").EnumerateArray().Where(p => p.GetProperty("enabled").GetBoolean())
            .Select(p => p.GetProperty("id").GetInt32()).ToHashSet();
        var docs = docsJson.GetProperty("docs").EnumerateArray()
            .Where(d => enabled.Contains(d.GetProperty("packId").GetInt32()))
            .Select(d => new DocRow
            {
                PackId = d.GetProperty("packId").GetInt32(), Kind = d.GetProperty("kind").GetString()!,
                DocKey = d.GetProperty("docKey").GetString()!, Body = d.GetProperty("body").GetRawText(),
            }).Where(d => d.Body != "null").ToList();
        var placements = new List<PlacementRow>();
        foreach (int map in new[] { 0, 1, 800, 801 })
        {
            var st = JsonDocument.Parse(await http.GetStringAsync($"{web}/WorldPacks/State?mapId={map}")).RootElement;
            placements.AddRange(st.GetProperty("placements").Deserialize<List<PlacementRow>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                .Where(p => !p.Deleted && enabled.Contains(p.PackId)));
        }

        using var stock = new VanillaArchiveSet(data);
        using var patch = MpqArchive.Open(Path.Combine(data, "patch-7.MPQ"))!;
        var mapDirs = WorldPackBuildService.MapDirectories(stock);
        foreach (var d in docs.Where(d => d.Kind == "map"))
        {
            var m = JsonDocument.Parse(d.Body).RootElement;
            mapDirs[m.GetProperty("mapId").GetInt32()] = m.GetProperty("directory").GetString()!;
        }
        var findings = new WorldPackAudit(new WorldPackAudit.AuditInput
        {
            Stock = stock.ReadFile,
            Built = p => patch.ReadFile(p) ?? stock.ReadFile(p),
            MapDirs = mapDirs, Docs = docs, Placements = placements,
        }).Run();
        foreach (var f in findings.OrderBy(f => f.Severity == "error" ? 0 : f.Severity == "warn" ? 1 : 2).ThenBy(f => f.Check))
            _out.WriteLine($"{f.Severity,-5} {f.Check,-5} {f.Subject}: {f.Message}" + (f.X is { } x ? $"  @{f.Map} ({x:F0}, {f.Y:F0}, {f.Z:F0})" : ""));
        _out.WriteLine($"{findings.Count(f => f.Severity == "error")} error(s), {findings.Count(f => f.Severity == "warn")} warning(s), {findings.Count} total");
    }
}
