using MangosSuperUI.Services.WorldPacks;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>Scratch survey (not an assertion): which stock Azeroth tiles exist around Gilneas and what
/// they hold. Run explicitly: dotnet test --filter WorldPackSurveyProbe.</summary>
public class WorldPackSurveyProbe
{
    private readonly ITestOutputHelper _out;
    public WorldPackSurveyProbe(ITestOutputHelper o) => _out = o;

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
        foreach (var (col, row, what) in new[] { (35, 35, "Stromgarde?"), (36, 35, "Stromgarde E?"), (35, 34, "Arathi N?"), (36, 29, "Caer Darrow?"), (29, 32, "SFK tile"), (28, 32, "Pyrewood?") })
        {
            var b = set.ReadFile(WorldCoords.AdtPath("Azeroth", col, row));
            if (b == null) { _out.WriteLine($"{what} {col},{row}: none"); continue; }
            var doc = AdtDocument.Parse(b, col, row);
            _out.WriteLine($"{what} {col},{row}: area {doc.ChunkInfo(136).area} wmos: {string.Join(", ", doc.WmoNames.Select(Path.GetFileName))}");
        }
        for (int row = 26; row <= 40; row++)
        {
            var line = new System.Text.StringBuilder($"row {row:D2}: ");
            for (int col = 20; col <= 32; col++)
            {
                var b = set.ReadFile(WorldCoords.AdtPath("Azeroth", col, row));
                if (b == null) { line.Append("  .......  "); continue; }
                var doc = AdtDocument.Parse(b, col, row);
                float min = float.MaxValue, max = float.MinValue;
                for (int gr = 0; gr <= 128; gr += 16)
                    for (int gc = 0; gc <= 128; gc += 16)
                    {
                        float h = doc.OuterHeight(gr, gc);
                        min = Math.Min(min, h); max = Math.Max(max, h);
                    }
                var areas = Enumerable.Range(0, 256).Select(i => doc.ChunkInfo(i).area).GroupBy(a => a).OrderByDescending(g => g.Count()).First().Key;
                line.Append($" {col}:{areas,4}/{(int)min,4}..{(int)max,-4}w{doc.WmoCount}d{doc.DoodadCount,-4}");
            }
            _out.WriteLine(line.ToString());
        }
    }
}
