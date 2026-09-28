using System.Numerics;
using System.Text.Json.Nodes;
using MangosSuperUI.Services;
using MangosSuperUI.Services.Mpq;
using MangosSuperUI.Services.WorldPacks;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace MangosSuperUI.Tests;

/// <summary>
/// Minimaps of pack-changed tiles (WorldPackMinimap): only pixels whose published ground differs from the ground the
/// Blizzard image shows are re-rendered; everything else stays the Blizzard image. Gilneas' pass (land raised out of
/// the sea in Azeroth 29,34) showed open sea until 2026-09-27.
/// </summary>
public class WorldPackMinimapTests
{
    private const int N = WorldPackMinimap.Size;

    private static WorldPackMinimap.Ground Flat(float height, float water, Vector3 albedo)
    {
        var g = new WorldPackMinimap.Ground();
        for (int p = 0; p < N * N; p++) { g.Height[p] = height; g.Water[p] = water; g.Albedo[p] = albedo; g.Light[p] = 0.8f; }
        return g;
    }

    [Fact]
    public void Changed_OnlyWhereTheGroundDiffers()
    {
        var sea = Flat(-20f, 0f, new(90, 80, 60));
        var raised = Flat(-20f, 0f, new(90, 80, 60));
        for (int y = 0; y < N; y++) for (int x = 0; x < 64; x++) raised.Height[y * N + x] = 12f;   // a strip of new land
        var m = WorldPackMinimap.Changed(sea, raised);
        Assert.Equal(64 * N, m.Count(c => c));
        Assert.True(m[10 * N + 5] && !m[10 * N + 200]);
        Assert.Equal(0, WorldPackMinimap.Changed(sea, Flat(-20.5f, 0f, new(1, 2, 3))).Count(c => c));   // within 1.5 yd: same
        Assert.All(WorldPackMinimap.Changed(null, sea), Assert.True);                                    // no source ground: all
    }

    [Fact]
    public void Compose_KeepsTheBlizzardImageWhereNothingChanged()
    {
        var sea = Flat(-20f, 0f, new(90, 80, 60));
        var land = Flat(-20f, 0f, new(90, 80, 60));
        for (int y = 0; y < N; y++) for (int x = 0; x < 64; x++) land.Height[y * N + x] = 12f;
        var image = new byte[N * N * 4];
        for (int p = 0; p < N * N; p++) { image[p * 4] = 70; image[p * 4 + 1] = 40; image[p * 4 + 2] = 10; image[p * 4 + 3] = 255; }
        var changed = WorldPackMinimap.Changed(sea, land);
        var fit = WorldPackMinimap.Fit(image, sea, changed, WorldPackMinimap.Calibration.Default);
        Assert.True(fit.WaterSamples >= 400);
        Assert.Equal(10f, fit.Shallow.X, 1);                                   // the sea colour is learned from the image
        var outPx = WorldPackMinimap.Compose(image, land, changed, fit, Array.Empty<(Vector2, float)>(), Array.Empty<(float, float, float, float)>());
        int far = 10 * N + 200, strip = 10 * N + 5;
        Assert.Equal(image[far * 4 + 2], outPx[far * 4 + 2]);                 // untouched sea: byte-identical
        Assert.NotEqual(image[strip * 4 + 1], outPx[strip * 4 + 1]);          // the new land is no longer sea-coloured
    }

    [Fact]
    public void Feather_IsOneInsideAndFadesOutside()
    {
        var m = new bool[N * N];
        WorldPackMinimap.Mark(m, 100, 100, 110, 110);
        var a = WorldPackMinimap.Feather(m);
        Assert.Equal(1f, a[105 * N + 105]);
        Assert.InRange(a[105 * N + 113], 0.01f, 0.99f);
        Assert.Equal(0f, a[105 * N + 140]);
    }
}

/// <summary>
/// Scratch probe: the minimap pass over the PUBLISHED Gilneas tiles (client GameData patch-7 over stock, tile docs from
/// the web app MSUI_WEBAPP, default http://192.168.0.2:5000): writes before/after PNGs to MSUI_MINIMAP_OUT (default
/// %TEMP%\minimap-probe) and prints the changed pixels per tile. Skipped when either is unavailable.
/// </summary>
public class WorldPackMinimapProbe
{
    private const int N = WorldPackMinimap.Size;
    private readonly ITestOutputHelper _out;
    public WorldPackMinimapProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void GilneasTiles()
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
        JsonArray? docs;
        try { docs = JsonNode.Parse(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }.GetStringAsync($"{web}/WorldPacks/Docs?kind=tile").Result)?["docs"] as JsonArray; }
        catch { return; }
        if (docs == null) return;
        string outDir = Environment.GetEnvironmentVariable("MSUI_MINIMAP_OUT") ?? Path.Combine(Path.GetTempPath(), "minimap-probe");
        Directory.CreateDirectory(outDir);
        using var stock = new VanillaArchiveSet(data);
        using var patch = MpqArchive.Open(Path.Combine(data, "patch-7.MPQ"))!;
        var dirs = WorldPackBuildService.MapDirectories(stock);
        dirs[801] = "GreymaneFortress";
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in System.Text.Encoding.UTF8.GetString(stock.ReadFile(WorldPackBuildService.MinimapTrsPath)!).Split('\n'))
            if (line.Trim().Split('\t') is [var k, var v]) hashes[k] = v;
        var colours = new Dictionary<string, Vector3?>(StringComparer.OrdinalIgnoreCase);
        Vector3? Colour(string path)
        {
            if (colours.TryGetValue(path, out var c)) return c;
            byte[]? blp = stock.ReadFile(path);
            if (blp == null) return colours[path] = null;
            byte[] px = BlpDecoder.GetPixels(blp, 0, out _, out _);
            double r = 0, g = 0, b = 0; int n = 0;
            for (int i = 0; i + 3 < px.Length; i += 28) { b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
            return colours[path] = new Vector3((float)(r / n), (float)(g / n), (float)(b / n));
        }
        byte[]? Image(string key) => hashes.TryGetValue(key, out var h) && stock.ReadFile(@"textures\Minimap\" + h) is { } blp
            ? BlpDecoder.GetPixels(blp, 0, out int w, out int hh) is var px && w == N && hh == N ? px : null : null;
        void Png(byte[] bgra, string file)
        {
            using var bmp = new SKBitmap(N, N, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
            using var fs = File.Create(Path.Combine(outDir, file));
            bmp.Encode(fs, SKEncodedImageFormat.Png, 100);
        }
        var tiles = new List<(string name, byte[]? image, WorldPackMinimap.Tile tile)>();
        var pool = new List<(float, Vector3)>();
        var touched = new HashSet<(int, int, int)>();
        foreach (var d in docs.OfType<JsonObject>())
        {
            var t = d["body"] as JsonObject;
            if (t == null) continue;
            int map = (int)t["map"]!, col = (int)t["col"]!, row = (int)t["row"]!;
            if (!dirs.TryGetValue(map, out var mapDir) || patch.ReadFile(WorldCoords.AdtPath(mapDir, col, row)) is not { } pub) continue;
            string sd = (string)t["sourceMap"]!; int sc = (int)t["sourceCol"]!, sr = (int)t["sourceRow"]!;
            byte[]? srcAdt = stock.ReadFile(WorldCoords.AdtPath(sd, sc, sr));
            byte[]? image = Image($@"{sd}\map{sc}_{sr}.blp");
            var tile = WorldPackMinimap.Prepare(AdtDocument.Parse(pub, col, row), col, row, srcAdt == null ? null : AdtDocument.Parse(srcAdt, sc, sr),
                sc, sr, image, stock.ReadFile, Colour, null, pool);
            tiles.Add(($"{mapDir}_{col}_{row}", image, tile));
            touched.Add((map, col, row));
        }
        foreach (var (map, col, row) in touched.Where(k => k.Item1 < 800)
                     .SelectMany(k => new[] { (k.Item1, k.Item2 - 1, k.Item3), (k.Item1, k.Item2 + 1, k.Item3), (k.Item1, k.Item2, k.Item3 - 1), (k.Item1, k.Item2, k.Item3 + 1) })
                     .Distinct().Where(k => !touched.Contains(k)))
        {
            if (stock.ReadFile(WorldCoords.AdtPath(dirs[map], col, row)) is not { } adt || Image($@"{dirs[map]}\map{col}_{row}.blp") is not { } img) continue;
            WorldPackMinimap.Fit(img, WorldPackMinimap.Sample(AdtDocument.Parse(adt, col, row), Colour), new bool[N * N], WorldPackMinimap.Calibration.Default, pool);
        }
        var mean = WorldPackMinimap.Mean(tiles.Select(t => t.tile.Fit), pool);
        foreach (var (name, image, tile) in tiles)
        {
            _out.WriteLine($"{name}: {tile.ChangedPixels} px changed; fit ground {tile.Fit?.GroundSamples ?? 0} water {tile.Fit?.WaterSamples ?? 0}");
            if (image != null) Png(image, $"{name}-before.png");
            if (tile.ChangedPixels > 0) Png(WorldPackMinimap.Render(tile, image, mean), $"{name}-after.png");
        }
        _out.WriteLine($"mean fit gain {mean.Gain} offset {mean.Offset} shallow {mean.Shallow} deep {mean.Deep}; PNGs in {outDir}");
    }
}
