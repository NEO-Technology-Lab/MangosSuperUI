using MangosSuperUI.Services.WorldPacks;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>Scratch survey: the WMOs stamped into Greymane Fortress (map 801, from Stromgarde
/// Azeroth 35..36 × 34..35 → 31..32 × 31..32) with their positions in the NEW map's world coords.</summary>
public class WorldPackFortressProbe
{
    private readonly ITestOutputHelper _out;
    public WorldPackFortressProbe(ITestOutputHelper o) => _out = o;

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
        var seen = new HashSet<uint>();
        // Gilneas (map 800) = Silverpine 27..30 × 29..33 → 30..33 × 30..34 (buildings dropped): where were they?
        foreach (var (sc, sr) in new[] { (28, 32), (29, 32), (28, 33), (29, 33), (30, 31), (29, 31), (30, 32), (30, 33) })
        {
            var doc = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", sc, sr))!, sc, sr);
            doc.Relocate(sc + 3, sr + 1, true, true, uid => uid);
            foreach (var (path, pos, uid) in doc.WmoPlacements())
            {
                if (!seen.Add(uid)) continue;
                var w = WorldCoords.PlacementToWorld(pos);
                _out.WriteLine($"GILNEAS {Path.GetFileName(path),-40} world ({w.X,7:F0}, {w.Y,7:F0}, {w.Z,5:F0})");
            }
        }
        seen.Clear();
        foreach (var (sc, sr, dc, dr) in new[] { (35, 34, 31, 31), (36, 34, 32, 31), (35, 35, 31, 32), (36, 35, 32, 32) })
        {
            var doc = AdtDocument.Parse(set.ReadFile(WorldCoords.AdtPath("Azeroth", sc, sr))!, sc, sr);
            doc.Relocate(dc, dr, true, true, uid => uid);
            foreach (var (path, pos, uid) in doc.WmoPlacements())
            {
                if (!seen.Add(uid)) continue;
                var w = WorldCoords.PlacementToWorld(pos);
                _out.WriteLine($"{Path.GetFileName(path),-40} world ({w.X,7:F0}, {w.Y,7:F0}, {w.Z,5:F0})");
            }
        }
    }
}
