using MangosSuperUI.Services.WorldPacks;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>Scratch: vanilla M2 collision header fields of dock/boat models, to pin the offsets
/// ModelBounds.M2WalkableTop reads (nBoundingTriangles/ofs, nBoundingVertices/ofs).</summary>
public class WorldPackM2CollisionProbe
{
    private readonly ITestOutputHelper _out;
    public WorldPackM2CollisionProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Docks()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? data = null;
        while (dir != null && data == null)
        {
            var c = Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data");
            if (File.Exists(Path.Combine(c, "model.MPQ"))) data = c;
            dir = dir.Parent;
        }
        if (data == null) return;
        using var set = new VanillaArchiveSet(data);
        foreach (var path in new[] { @"World\Lordaeron\SilverPine\PassiveDoodads\Docks\SilverPineDocks01.m2",
                                     @"World\Lordaeron\SilverPine\PassiveDoodads\Docks\SilverPineDocks02.m2",
                                     @"World\AZEROTH\REDRIDGE\PASSIVEDOODADS\RowBoat\RowBoat01.m2" })
        {
            var m = set.ReadFile(path)!;
            _out.WriteLine($"{Path.GetFileName(path)} len {m.Length} version {BitConverter.ToUInt32(m, 4)}");
            for (int o = 0xC8; o <= 0x104; o += 4)
                _out.WriteLine($"  0x{o:X3}: u32 {BitConverter.ToUInt32(m, o),10}  f {BitConverter.ToSingle(m, o),12:F3}");
            var top = ModelBounds.M2WalkableTop(m);
            _out.WriteLine($"  walkable top: {top?.ToString("F2") ?? "none"}  bounds {ModelBounds.M2(m)}");
        }
    }
}
