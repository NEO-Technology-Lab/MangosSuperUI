using MangosSuperUI.Services.Mpq;
using MangosSuperUI.Services.WorldPacks;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>Scratch: per chunk of a published sculpted tile — how many heights changed vs how many
/// normals changed (and by how much) against stock.</summary>
public class WorldPackNormalProbe
{
    private readonly ITestOutputHelper _out;
    public WorldPackNormalProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Northshire()
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
        using var stock = new VanillaArchiveSet(data);
        using var patch = MpqArchive.Open(Path.Combine(data, "patch-7.MPQ"))!;
        foreach (var (col, row) in new[] { (32, 48), (32, 49), (31, 48), (31, 49) })
        {
            var path = WorldCoords.AdtPath("Azeroth", col, row);
            var p = patch.ReadFile(path);
            if (p == null) continue;
            var a = AdtDocument.Parse(stock.ReadFile(path)!, col, row);
            var b = AdtDocument.Parse(p, col, row);
            _out.WriteLine($"== {col},{row}");
            for (int i = 0; i < 256; i++)
            {
                var (ix, iy, _, _, _) = a.ChunkInfo(i);
                int hChanged = 0;
                for (int r = 0; r <= 8; r++) for (int c = 0; c <= 8; c++)
                    if (MathF.Abs(a.OuterHeight(iy * 8 + r, ix * 8 + c) - b.OuterHeight(iy * 8 + r, ix * 8 + c)) > 0.01f) hChanged++;
                var na = a.NormalsOf(i); var nb = b.NormalsOf(b.ChunkIndex(ix, iy));
                int nChanged = 0, maxd = 0;
                for (int k = 0; k < na.Length; k++)
                {
                    int d = Math.Abs((sbyte)na[k] - (sbyte)nb[k]);
                    if (d > 2) nChanged++;
                    maxd = Math.Max(maxd, d);
                }
                if (hChanged > 0 || nChanged > 0) _out.WriteLine($"chunk ({ix,2},{iy,2}) heights changed {hChanged,3}  normal bytes changed {nChanged,3}  max byte diff {maxd}");
            }
        }
    }
}
