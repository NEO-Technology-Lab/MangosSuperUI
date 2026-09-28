using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>One authored world polygon drives terrain, continent hover ownership and its additive glow.
/// ZMP uses the vanilla 5875 world grid, not pixels in the painted map; highlight placement uses WorldMapArea.</summary>
public static class WorldPackWorldMap
{
    public const int GridEdge = 128;
    public const int HighlightWidth = 128;
    public const float WorldToGrid = 2.9296876e-5f;
    public const string AreaPath = @"DBFilesClient\WorldMapArea.dbc";
    public const string NamesPath = @"DBFilesClient\AreaTable.dbc";

    public sealed record Region(int Map, int Area, string Directory, Vector2[] Polygon)
    {
        public string Key => $"{Map}:{Area}";
        public string HighlightPath => $@"Interface\WorldMap\{Directory}\{Directory}Highlight.blp";
        public bool Contains(Vector2 point) => WorldPackWorldMap.Contains(Polygon, point);
        public float DistanceToBoundary(Vector2 point) => WorldPackWorldMap.DistanceToBoundary(Polygon, point);
        public MapBounds Bounds => FitBounds(Polygon);
    }

    // WoW world X is north, Y west. DBC left/right are world Y; top/bottom are world X.
    public readonly record struct MapBounds(float Left, float Right, float Top, float Bottom)
    {
        public float Width => Left - Right;
        public float Height => Top - Bottom;
        public int PixelHeight => (int)(Height * HighlightWidth / Width);
        public int FileHeight { get { int n = 1; while (n < PixelHeight) n <<= 1; return n; } }
        public Vector2 PixelWorld(float column, float row) =>
            new(Top - row / PixelHeight * Height, Left - column / HighlightWidth * Width);
    }

    public static Region Parse(JsonObject body)
    {
        int map = (int?)body["map"] ?? -1, area = (int?)body["area"] ?? -1;
        string directory = (string?)body["directory"] ?? "";
        if (map is not (0 or 1)) throw new ArgumentException("worldmap needs a stock continent map 0 or 1");
        if (area < WorldPackContent.AreaIdBase) throw new ArgumentException("worldmap area must be a pack area (7000+)");
        if (directory.Length is 0 or > 40 || !directory.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("worldmap directory must be 1-40 ASCII letters/digits");
        if (body["polygon"] is not JsonArray points || points.Count is < 3 or > 2048)
            throw new ArgumentException("worldmap needs polygon [[worldX,worldY],...] with 3-2048 vertices");
        var polygon = points.Select(p =>
        {
            if (p is not JsonArray { Count: 2 } pair) throw new ArgumentException("worldmap polygon vertices need [worldX,worldY]");
            var v = new Vector2((float?)pair[0] ?? float.NaN, (float?)pair[1] ?? float.NaN);
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || Math.Abs(v.X) >= 17066 || Math.Abs(v.Y) >= 17066)
                throw new ArgumentException("worldmap polygon vertices must be finite world coordinates inside the continent grid");
            return v;
        }).ToArray();
        // Accept an explicitly closed ring, but store only its distinct closing vertex.
        if (polygon[0] == polygon[^1]) polygon = polygon[..^1];
        if (polygon.Length < 3 || polygon.Distinct().Count() != polygon.Length)
            throw new ArgumentException("worldmap polygon needs at least three distinct vertices and no repeated vertices");
        double twiceArea = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            twiceArea += (double)a.X * b.Y - (double)b.X * a.Y;
            for (int j = i + 2; j < polygon.Length; j++)
            {
                if (i == 0 && j == polygon.Length - 1) continue;
                if (SegmentsIntersect(a, b, polygon[j], polygon[(j + 1) % polygon.Length]))
                    throw new ArgumentException("worldmap polygon must not cross or touch itself");
            }
        }
        if (Math.Abs(twiceArea) < 2) throw new ArgumentException("worldmap polygon must enclose at least one square yard");
        var region = new Region(map, area, directory, polygon);
        if (!Enumerable.Range(0, GridEdge * GridEdge).Any(i => region.Contains(CellCentre(i))))
            throw new ArgumentException("worldmap polygon is smaller than the continent hover grid (no cell centres inside)");
        return region;
    }

    public static List<Region> ReadRegions(IEnumerable<DocRow> docs)
    {
        var regions = docs.Where(d => d.Kind == "worldmap").Select(d =>
        {
            var r = Parse(JsonNode.Parse(d.Body)!.AsObject());
            if (d.DocKey != r.Key) throw new ArgumentException($"worldmap key {d.DocKey} must be {r.Key}");
            return r;
        }).ToList();
        if (regions.Select(r => r.Area).Distinct().Count() != regions.Count ||
            regions.Select(r => r.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != regions.Count)
            throw new ArgumentException("each worldmap must have a unique area and highlight directory across enabled packs");
        foreach (var group in regions.GroupBy(r => r.Map))
            if (group.Count() > 1) _ = PatchZoneMap(new byte[GridEdge * GridEdge * 4], group.ToArray());
        return regions;
    }

    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static float DistanceToBoundary(IReadOnlyList<Vector2> polygon, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < polygon.Count; i++)
        {
            Vector2 a = polygon[i], d = polygon[(i + 1) % polygon.Count] - a;
            float t = Math.Clamp(Vector2.Dot(p - a, d) / d.LengthSquared(), 0, 1);
            best = Math.Min(best, Vector2.Distance(p, a + t * d));
        }
        return best;
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static double Cross(Vector2 p, Vector2 q, Vector2 r) => ((double)q.X - p.X) * (r.Y - p.Y) - ((double)q.Y - p.Y) * (r.X - p.X);
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
            Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y)) return false;
        return Cross(a, b, c) * Cross(a, b, d) <= 0 && Cross(c, d, a) * Cross(c, d, b) <= 0;
    }

    public static MapBounds FitBounds(IReadOnlyList<Vector2> polygon)
    {
        float top = polygon.Max(p => p.X), bottom = polygon.Min(p => p.X);
        float left = polygon.Max(p => p.Y), right = polygon.Min(p => p.Y);
        // Two source pixels around the shape keep the glow away from a clipped texture edge.
        float width = left - right, height = top - bottom;
        width = Math.Max(width, height * 1.5f) * (128f / 124f);
        height = width / 1.5f;
        return new((left + right + width) / 2, (left + right - width) / 2,
            (top + bottom + height) / 2, (top + bottom - height) / 2);
    }

    /// <summary>Inverse of WorldMapZoneMap.TryCellIndex; centres avoid truncation ambiguities at half-tile edges.</summary>
    public static Vector2 CellCentre(int index) => new(
        (0.5f - (index / GridEdge + 0.5f) / GridEdge) / WorldToGrid,
        (0.5f - (index % GridEdge + 0.5f) / GridEdge) / WorldToGrid);

    public static byte[] PatchZoneMap(byte[] stock, IReadOnlyList<Region> regions)
    {
        if (stock.Length != GridEdge * GridEdge * 4) throw new InvalidDataException("continent ZMP must contain 128x128 uint32 cells");
        var result = (byte[])stock.Clone();
        for (int i = 0; i < GridEdge * GridEdge; i++)
        {
            var owners = regions.Where(r => r.Contains(CellCentre(i))).ToArray();
            if (owners.Length > 1) throw new InvalidOperationException($"worldmap areas {string.Join(",", owners.Select(r => r.Area))} overlap at hover cell {i % GridEdge},{i / GridEdge}");
            if (owners.Length == 1) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), (uint)owners[0].Area);
        }
        return result;
    }

    public static List<DocRow> AreaDocuments(List<DocRow> docs)
    {
        var output = new List<DocRow>();
        foreach (var region in ReadRegions(docs))
        {
            if (docs.Any(d => d.Kind == "dbc:WorldMapArea" &&
                (d.DocKey == region.Area.ToString() || (int?)JsonNode.Parse(d.Body)?["fields"]?["2"] == region.Area)))
                throw new InvalidOperationException($"worldmap {region.Key} generates WorldMapArea; remove the conflicting explicit dbc:WorldMapArea doc");
            var source = docs.SingleOrDefault(d => d.Kind == "dbc:AreaTable" && d.DocKey == region.Area.ToString())
                ?? throw new InvalidOperationException($"worldmap {region.Key} requires its pack AreaTable document");
            var f = JsonNode.Parse(source.Body)?["fields"];
            if ((int?)f?["1"] != region.Map || (int?)f?["2"] != 0 || string.IsNullOrWhiteSpace((string?)f?["11"]))
                throw new InvalidOperationException($"worldmap {region.Key} needs AreaTable map={region.Map}, zone=0 and a non-empty name");
            MapBounds b = region.Bounds;
            output.Add(new DocRow { PackId = source.PackId, Kind = "dbc:WorldMapArea", DocKey = region.Area.ToString(),
                Body = new JsonObject { ["fields"] = new JsonObject {
                    ["1"] = region.Map, ["2"] = region.Area, ["3"] = region.Directory,
                    ["4"] = new JsonObject { ["f"] = b.Left }, ["5"] = new JsonObject { ["f"] = b.Right },
                    ["6"] = new JsonObject { ["f"] = b.Top }, ["7"] = new JsonObject { ["f"] = b.Bottom },
                }}.ToJsonString() });
        }
        return output;
    }

    public static byte[] HighlightPixels(Region region)
    {
        MapBounds b = region.Bounds;
        var pixels = new byte[HighlightWidth * b.FileHeight * 4];
        for (int y = 0; y < b.FileHeight; y++) for (int x = 0; x < HighlightWidth; x++)
        {
            int i = (y * HighlightWidth + x) * 4;
            pixels[i + 3] = 255; // Vanilla's ADD-authored art has opaque alpha; black contributes no light.
            if (y >= b.PixelHeight) continue;
            Vector2 point = b.PixelWorld(x + 0.5f, y + 0.5f);
            if (!region.Contains(point)) continue;
            float distance = region.DistanceToBoundary(point) / (b.Width / HighlightWidth);
            byte glow = (byte)(56 + 96 * Math.Exp(-distance * distance / 8));
            pixels[i] = pixels[i + 1] = pixels[i + 2] = glow;
        }
        return pixels;
    }

    public static void BuildAssets(Func<string, byte[]?> stock, List<DocRow> docs,
        Dictionary<string, byte[]> files, Action<string>? log = null)
    {
        var regions = ReadRegions(docs);
        if (regions.Count == 0) return;
        var areas = DbcWriterService.ReadDbc(stock(AreaPath) ?? throw new InvalidDataException("stock WorldMapArea missing"), AreaPath);
        foreach (var region in regions)
            if (areas.GetAllRows().Any(r => r[0] == region.Area ||
                areas.ReadString(r[3]).Equals(region.Directory, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"worldmap {region.Key} would replace a stock WorldMapArea row or highlight directory");
        foreach (var group in regions.GroupBy(r => r.Map))
        {
            uint[] continent = areas.GetAllRows().SingleOrDefault(r => r[1] == group.Key && r[2] == 0)
                ?? throw new InvalidDataException($"no stock continent WorldMapArea for map {group.Key}");
            string path = $@"Interface\WorldMap\{areas.ReadString(continent[3])}.zmp";
            files[path] = PatchZoneMap(stock(path) ?? throw new InvalidDataException($"missing stock hover map {path}"), group.ToArray());
            log?.Invoke($"worldmap: {path}, {group.Count()} polygon(s); all other hover cells preserved");
        }
        foreach (var region in regions)
        {
            files[region.HighlightPath] = EncodeHighlight(region);
            log?.Invoke($"worldmap: {region.Key} {region.HighlightPath}, bounds {region.Bounds}");
        }
    }

    public static byte[] EncodeHighlight(Region region)
    {
        using var bitmap = new SKBitmap(HighlightWidth, region.Bounds.FileHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        byte[] pixels = HighlightPixels(region);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        byte[] encoded = new BlpWriterService().EncodeBitmapToBlpUncompressed(bitmap)
            ?? throw new InvalidDataException($"could not encode {region.HighlightPath}");
        // The shared lossless writer emits alpha-depth 8. ADD art must declare NO alpha channel:
        // GameplayArt.AdditiveHandle derives coverage from RGB only for alpha-depth 0 (just like stock).
        // Strip each opaque alpha plane, retaining the palette and full mip chain losslessly.
        const int headerLength = 1172;
        byte[] header = encoded[..headerLength];
        header[9] = 0;
        using var output = new MemoryStream();
        output.Write(header);
        for (int level = 0; level < 16; level++)
        {
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(20 + level * 4));
            if (offset == 0) continue;
            int count = Math.Max(1, HighlightWidth >> level) * Math.Max(1, region.Bounds.FileHeight >> level);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20 + level * 4), (uint)output.Position);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(84 + level * 4), (uint)count);
            output.Write(encoded, offset, count);
        }
        output.Position = 0;
        output.Write(header);
        return output.ToArray();
    }

    /// <summary>G16 checks the published files, including outside-polygon ownership and the client texture crop.</summary>
    public static IEnumerable<AuditFinding> Verify(List<DocRow> docs, Func<string, byte[]?> stock, Func<string, byte[]?> built)
    {
        var regions = ReadRegions(docs);
        if (regions.Count == 0) yield break;
        _ = AreaDocuments(docs); // Missing names, mismatched map/parent and conflicting authoring are build errors too.
        var areaBytes = built(AreaPath) ?? throw new InvalidDataException("published WorldMapArea missing");
        var areas = DbcWriterService.ReadDbc(areaBytes, AreaPath);
        var names = DbcWriterService.ReadDbc(built(NamesPath) ?? throw new InvalidDataException("published AreaTable missing"), NamesPath);
        foreach (var group in regions.GroupBy(r => r.Map))
        {
            var continent = areas.GetAllRows().SingleOrDefault(r => r[1] == group.Key && r[2] == 0)
                ?? throw new InvalidDataException($"no continent WorldMapArea for map {group.Key}");
            string path = $@"Interface\WorldMap\{areas.ReadString(continent[3])}.zmp";
            byte[] expected = PatchZoneMap(stock(path) ?? throw new InvalidDataException($"missing stock {path}"), group.ToArray());
            byte[]? actual = built(path);
            if (actual is null || !actual.AsSpan().SequenceEqual(expected))
                yield return new("G16", "error", path, "hover cells do not match polygon ownership with stock cells preserved outside", group.Key);
        }
        foreach (var region in regions)
        {
            uint[]? row = areas.GetRow((uint)region.Area), name = names.GetRow((uint)region.Area);
            var b = region.Bounds;
            if (row is null || row[1] != region.Map || row[2] != region.Area || areas.ReadString(row[3]) != region.Directory ||
                Math.Abs(BitConverter.UInt32BitsToSingle(row[4]) - b.Left) > .01f ||
                Math.Abs(BitConverter.UInt32BitsToSingle(row[5]) - b.Right) > .01f ||
                Math.Abs(BitConverter.UInt32BitsToSingle(row[6]) - b.Top) > .01f ||
                Math.Abs(BitConverter.UInt32BitsToSingle(row[7]) - b.Bottom) > .01f)
                yield return new("G16", "error", region.Key, "WorldMapArea bounds/directory do not align the highlight with its polygon", region.Map);
            if (name is null || name[1] != region.Map || name[2] != 0 || string.IsNullOrWhiteSpace(names.ReadString(name[11])))
                yield return new("G16", "error", region.Key, "AreaTable does not supply a named top-level zone on the continent", region.Map);
            byte[]? blp = built(region.HighlightPath);
            if (blp is null) { yield return new("G16", "error", region.Key, "continent highlight BLP missing", region.Map); continue; }
            byte[] pixels = BlpDecoder.GetPixels(blp, 0, out int width, out int height);
            if (blp[9] != 0 || width != HighlightWidth || height != b.FileHeight || !pixels.AsSpan().SequenceEqual(HighlightPixels(region)))
                yield return new("G16", "error", region.Key, "highlight pixels/crop differ from the polygon in WorldMapArea coordinates", region.Map);
            else yield return new("G16", "info", region.Key, $"polygon highlight verified ({width}x{height}, visible height {b.PixelHeight}); named zone {names.ReadString(name?[11] ?? 0)}", region.Map);
        }
    }
}
