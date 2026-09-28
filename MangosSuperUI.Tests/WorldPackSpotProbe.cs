using System.Numerics;
using MangosSuperUI.Services.Mpq;
using MangosSuperUI.Services.WorldPacks;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>Scratch: what is under one world point of a pack map - terrain height and every WMO/doodad
/// whose origin is within 12 yd (MSUI_SPOT="map,x,y", default the Keel Harbor dock - Gilneas is continent land on
/// map 0 since 2026-09-27; map 800 no longer exists). A spot on a tile patch-7 does not carry is skipped.</summary>
public class WorldPackSpotProbe
{
    private readonly ITestOutputHelper _out;
    public WorldPackSpotProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Spot()
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
        var spot = (Environment.GetEnvironmentVariable("MSUI_SPOT") ?? "0,-2229.2,1899.1").Split(',');
        int map = int.Parse(spot[0]); float x = float.Parse(spot[1]), y = float.Parse(spot[2]);
        using var stock = new VanillaArchiveSet(data);
        using var patch = MpqArchive.Open(Path.Combine(data, "patch-7.MPQ"))!;
        var dirs = WorldPackBuildService.MapDirectories(stock);
        dirs[800] = "Gilneas";
        dirs[801] = "GreymaneFortress";
        var audit = new WorldPackAudit(new WorldPackAudit.AuditInput
        {
            Stock = stock.ReadFile, Built = p => patch.ReadFile(p) ?? stock.ReadFile(p), MapDirs = dirs, Docs = new(), Placements = new(),
        });
        _out.WriteLine($"terrain at ({x}, {y}) = {audit.Ground(map, x, y)}");
        int col = WorldCoords.TileCol(y), row = WorldCoords.TileRow(x);
        if (!dirs.ContainsKey(map) || patch.ReadFile(WorldCoords.AdtPath(dirs[map], col, row)) is not { } spotBytes) return;
        var adt = AdtDocument.Parse(spotBytes, col, row);
        foreach (var d in adt.DoodadPlacements())
        {
            var w = WorldCoords.PlacementToWorld(d.pos);
            if (Vector2.Distance(new(w.X, w.Y), new(x, y)) < 12)
                _out.WriteLine($"doodad {Path.GetFileName(d.path),-34} at ({w.X:F1}, {w.Y:F1}, {w.Z:F1}) scale {d.scale:F2}");
        }
        // Terrain holes on every Gilneas tile: where are they, and does any WMO stand over them?
        foreach (var key in Enumerable.Range(27, 4).SelectMany(c => Enumerable.Range(35, 5).Select(r => (c, r))))   // the continent block
        {
            var bytes = patch.ReadFile(WorldCoords.AdtPath(dirs[map], key.Item1, key.Item2));
            if (bytes == null) continue;
            var t = AdtDocument.Parse(bytes, key.Item1, key.Item2);
            for (int i = 0; i < 256; i++)
            {
                ushort holes = t.HolesOf(i);
                if (holes == 0) continue;
                var (ix, iy, ox, oy, _) = t.ChunkInfo(i);
                var cx = ox - WorldCoords.Chunk / 2; var cy = oy - WorldCoords.Chunk / 2;
                bool covered = t.WmoPlacementsFull().Any(w => { var p = WorldCoords.PlacementToWorld(w.pos); return Vector2.Distance(new(p.X, p.Y), new(cx, cy)) < 60; });
                _out.WriteLine($"HOLE tile {key.Item1},{key.Item2} chunk ({ix},{iy}) world ({cx:F0}, {cy:F0}) mask 0x{holes:X4} wmo-nearby={covered}");
            }
        }
        foreach (var wmo in adt.WmoPlacementsFull())
        {
            var w = WorldCoords.PlacementToWorld(wmo.pos);
            if (Vector2.Distance(new(w.X, w.Y), new(x, y)) < 60)
            {
                _out.WriteLine($"wmo {Path.GetFileName(wmo.path),-34} at ({w.X:F1}, {w.Y:F1}, {w.Z:F1})");
                // Every MOGI group box in world space that stands over the spot: does anything have a floor here?
                var m = WorldPackGeometry.WmoMatrix(wmo.pos, wmo.rot);
                int g = 0;
                foreach (var (flags, lo, hi) in WorldPackGeometry.WmoGroups(stock.ReadFile(wmo.path) ?? patch.ReadFile(wmo.path)))
                {
                    var o = Obb.FromLocal(lo, hi, m);
                    Vector3 wlo = new(float.MaxValue), whi = new(float.MinValue);
                    for (int c = 0; c < 8; c++)
                    {
                        var pc = WorldCoords.PlacementToWorld(Vector3.Transform(new Vector3((c & 1) != 0 ? hi.X : lo.X, (c & 2) != 0 ? hi.Y : lo.Y, (c & 4) != 0 ? hi.Z : lo.Z), m));
                        wlo = Vector3.Min(wlo, pc); whi = Vector3.Max(whi, pc);
                    }
                    bool over = x >= wlo.X && x <= whi.X && y >= wlo.Y && y <= whi.Y;
                    _out.WriteLine($"   group {g++,2} flags 0x{flags:X8} world X {wlo.X:F0}..{whi.X:F0} Y {wlo.Y:F0}..{whi.Y:F0} Z {wlo.Z:F0}..{whi.Z:F0}{(over ? "  <- over the spot" : "")}");
                }
            }
        }
    }
}
