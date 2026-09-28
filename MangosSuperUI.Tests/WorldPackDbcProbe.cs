using MangosSuperUI.Services;
using MangosSuperUI.Services.WorldPacks;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>Scratch survey of the stock DBCs a World Pack extends. Not an assertion.</summary>
public class WorldPackDbcProbe
{
    private readonly ITestOutputHelper _out;
    public WorldPackDbcProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Survey()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? data = null;
        while (dir != null && data == null)
        {
            var c = Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data");
            if (File.Exists(Path.Combine(c, "terrain.MPQ"))) data = c;
            dir = dir.Parent;
        }
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        void Dump(string name, params uint[] ids)
        {
            var dbc = DbcWriterService.ReadDbc(set.ReadFile($"DBFilesClient\\{name}.dbc")!);
            _out.WriteLine($"{name}: {dbc.RecordCount} rows, {dbc.FieldCount} fields, max id {dbc.GetMaxId()}");
            foreach (var id in ids)
            {
                var r = dbc.GetRow(id);
                if (r == null) { _out.WriteLine($"  {id}: none"); continue; }
                _out.WriteLine($"  {id}: " + string.Join(" ", r.Select((v, i) =>
                {
                    string s = v > 0 && v < 2_000_000 ? dbc.ReadString(v) : "";
                    return s.Length > 1 && s.All(ch => ch >= 32 && ch < 127) ? $"[{i}]'{s}'" : $"[{i}]{v}";
                })));
            }
            if (name == "AreaTable")
                _out.WriteLine($"  max AreaBit (field 3): {dbc.GetAllRows().Max(r => r[3])}");
        }
        var light = DbcWriterService.ReadDbc(set.ReadFile("DBFilesClient\\Light.dbc")!);
        _out.WriteLine($"Light: {light.RecordCount} rows, {light.FieldCount} fields, max id {light.GetMaxId()}");
        foreach (var r in light.GetAllRows().Where(r => r[1] == 0 || r[1] == 33 || r[1] == 309).Take(400))
        {
            float x = DbcWriterService.UintToFloat(r[2]) / 36f, y = DbcWriterService.UintToFloat(r[3]) / 36f;
            float fs = DbcWriterService.UintToFloat(r[5]) / 36f, fe = DbcWriterService.UintToFloat(r[6]) / 36f;
            bool near = r[1] != 0 || (x is > -2000 and < 2500 && y is > -2000 and < 3000) || fe == 0;
            if (near) _out.WriteLine($"  light {r[0]} map {r[1]} pos/36 ({x:F0},{y:F0}) falloff/36 {fs:F0}..{fe:F0} params {string.Join(",", r.Skip(7))}");
        }
        Dump("Map", 0, 1, 33, 309);
        Dump("AreaTable", 130, 209, 204);
        Dump("AreaTrigger", 145, 194);
        Dump("LoadingScreens", 1, 20);
        Dump("WorldMapArea", 21, 25);
    }
}
