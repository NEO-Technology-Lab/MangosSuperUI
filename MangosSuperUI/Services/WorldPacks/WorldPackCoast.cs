using System.Numerics;
using System.Text.Json.Nodes;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>Fits authored terrain to the SAME world-coordinate polygon used by the continent map.
/// Explicit tile scope keeps the neighbouring stock world unchanged. A northern land join preserves
/// the stitched stock boundary; the remaining polygon edges become coast, at the declared sea level.</summary>
public sealed record WorldPackCoast(WorldPackWorldMap.Region Region, HashSet<(int col, int row)> Tiles,
    float SeaLevel, float SeaDepth, float CoastWidth, float MinimumLand, float? JoinNorth, float JoinWidth)
{
    public static List<WorldPackCoast> Read(IEnumerable<DocRow> docs)
    {
        var result = new List<WorldPackCoast>();
        foreach (var d in docs.Where(d => d.Kind == "worldmap"))
        {
            var b = JsonNode.Parse(d.Body)!.AsObject();
            if (b["terrain"] is not JsonObject t) continue;
            var region = WorldPackWorldMap.Parse(b);
            var tiles = (t["tiles"] as JsonArray ?? throw new ArgumentException("coast terrain needs explicit tiles"))
                .Select(v => ((int)v![0]!, (int)v[1]!)).ToHashSet();
            if (tiles.Count == 0 || tiles.Any(v => v.Item1 is < 0 or > 63 || v.Item2 is < 0 or > 63))
                throw new ArgumentException("coast tiles outside the map");
            float level = (float?)t["seaLevel"] ?? 0, depth = (float?)t["seaDepth"] ?? 515;
            float width = (float?)t["coastWidth"] ?? 180, land = (float?)t["minimumLand"] ?? 6;
            float? join = (float?)t["joinNorth"]; float joinWidth = (float?)t["joinWidth"] ?? 80;
            if (!float.IsFinite(level) || !float.IsFinite(depth) || !float.IsFinite(width) || !float.IsFinite(land) ||
                depth <= 0 || width < 20 || land <= 0 || joinWidth < 1 || !float.IsFinite(joinWidth) ||
                (join.HasValue && !float.IsFinite(join.Value))) throw new ArgumentException("invalid coast terrain parameters");
            result.Add(new(region, tiles, level, depth, width, land, join, joinWidth));
        }
        return result;
    }

    public float CoastDistance(Vector2 p)
    {
        float best = float.MaxValue;
        var polygon = Region.Polygon;
        for (int i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            // The straight northern closure is a land border, not an ocean beach.
            if (JoinNorth is float north && a.X >= north - 1 && b.X >= north - 1) continue;
            var ab = b - a;
            float t = ab.LengthSquared() < 1e-8f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            best = Math.Min(best, Vector2.Distance(p, a + t * ab));
        }
        return best;
    }

    private static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    public float Height(Vector2 p, float original, float? baseline = null)
    {
        float distance = CoastDistance(p);
        bool inside = Region.Contains(p);
        float land = Math.Max(original - SeaLevel, MinimumLand);
        float beach = Math.Min(land, distance * 0.45f);
        float target = inside
            ? SeaLevel + beach + (land - beach) * Smooth(distance / CoastWidth)
            : SeaLevel + ((baseline ?? (SeaLevel - SeaDepth)) - SeaLevel) * Smooth(distance / CoastWidth);
        // Shared northern vertices remain exactly the stock/stamp seam, including the Greymane approach.
        float weight = JoinNorth is float north ? Smooth((north - p.X) / JoinWidth) : 1;
        return original + (target - original) * weight;
    }

    public bool AffectsBaseline(Vector2 p) => Region.Contains(p) || CoastDistance(p) < CoastWidth;

    public bool ClearsProp(Vector3 placement, bool identity = false)
    {
        var w = WorldCoords.PlacementToWorld(placement); var p = new Vector2(w.X, w.Y);
        return ClearsSurface(p, identity);
    }

    public bool ClearsSurface(Vector2 p, bool identity = false)
    {
        if (identity && !AffectsBaseline(p)) return false;
        if (JoinNorth is float north && p.X >= north - Math.Max(150, JoinWidth)) return true;
        // Trees and other source props cannot remain suspended over a reshaped beach or sea floor.
        return !Region.Contains(p) || CoastDistance(p) < CoastWidth;
    }

    public bool ClearsBuilding(Vector3 placement, bool identity = false)
    {
        var w = WorldCoords.PlacementToWorld(placement); var p = new Vector2(w.X, w.Y);
        if (identity && !AffectsBaseline(p)) return false;
        if (JoinNorth is float north && p.X >= north - Math.Max(150, JoinWidth)) return true;
        return !Region.Contains(p) || CoastDistance(p) < 40;
    }

    public static void Apply(List<DocRow> docs, VanillaArchiveSet stock, Dictionary<int, string> mapDirs,
        Dictionary<(int map, int col, int row), AdtDocument> built, List<PlacementRow> placements, Action<string> log)
    {
        var placedPositions = placements.Where(p => !p.Deleted).Select(p => WorldCoords.WorldToPlacement(new(p.PosX, p.PosY, p.PosZ))).ToHashSet();
        foreach (var coast in Read(docs))
        {
            int changed = 0, removed = 0, removedWater = 0;
            foreach (var (col, row) in coast.Tiles)
            {
                if (!built.TryGetValue((coast.Region.Map, col, row), out var adt))
                    throw new InvalidOperationException($"coast tile {coast.Region.Map}:{col},{row} must have a tile document");
                var tile = docs.Where(d => d.Kind == "tile").Select(d => JsonNode.Parse(d.Body)!.AsObject())
                    .FirstOrDefault(t => (int?)t["map"] == coast.Region.Map && (int?)t["col"] == col && (int?)t["row"] == row);
                bool identity = tile is not null && (string?)tile["sourceMap"] == mapDirs[coast.Region.Map]
                    && (int?)tile["sourceCol"] == col && (int?)tile["sourceRow"] == row;
                var original = stock.ReadFile(WorldCoords.AdtPath(mapDirs[coast.Region.Map], col, row))
                    ?? throw new InvalidOperationException($"coast tile {col},{row} has no stock terrain baseline");
                var baseline = AdtDocument.Parse(original, col, row);
                var heights = adt.OuterHeights(); var deltas = new Dictionary<int, float>();
                for (int r = 0; r <= 128; r++) for (int c = 0; c <= 128; c++)
                {
                    int i = r * 129 + c;
                    var p = new Vector2(WorldCoords.VertexWorldX(row, r), WorldCoords.VertexWorldY(col, c));
                    float dz = coast.Height(p, heights[i], baseline.OuterHeight(r, c)) - heights[i];
                    if (Math.Abs(dz) > 0.0001f) deltas[i] = dz;
                }
                // Center vertices are actual terrain vertices too. Averaging only corner deltas
                // would carry the source tile's bumps/depressions into the new beach and seabed.
                var innerDeltas = new Dictionary<int, float>();
                for (int r = 0; r < 128; r++) for (int c = 0; c < 128; c++)
                {
                    var p = new Vector2(WorldCoords.VertexWorldX(row, r) - WorldCoords.Unit / 2,
                        WorldCoords.VertexWorldY(col, c) - WorldCoords.Unit / 2);
                    float h = adt.InnerHeight(r, c);
                    float dz = coast.Height(p, h, baseline.InnerHeight(r, c)) - h;
                    if (Math.Abs(dz) > 0.0001f) innerDeltas[r * 128 + c] = dz;
                }
                changed += adt.ApplySculpt(deltas, innerDeltas);
                removed += adt.DropDoodads((_, p) => !placedPositions.Contains(p) && coast.ClearsProp(p, identity));
                removed += adt.DropWmos((_, p) => !placedPositions.Contains(p) && coast.ClearsBuilding(p, identity));
                for (int i = 0; i < 256; i++)
                {
                    var info = adt.ChunkInfo(i);
                    var p = new Vector2(info.ox - WorldCoords.Chunk / 2, info.oy - WorldCoords.Chunk / 2);
                    // Imported buildings in the northern seam band are removed too; their old
                    // courtyard/cave holes must not remain open after the terrain is fitted.
                    if (coast.ClearsSurface(p, identity)) adt.ClearHoles(i);
                    if (!coast.Region.Contains(p))
                        adt.PaintArea(baseline.ChunkInfo(baseline.ChunkIndex(info.ix, info.iy)).area, p.X, p.Y, 1);
                }
                adt.CarryLiquidFrom(baseline, replaceExisting: true);
                var stockWet = baseline.LiquidCells().Select(c => (c.Row, c.Col)).ToHashSet();
                removedWater += adt.RemoveLiquidCells((r, c) =>
                {
                    var p = new Vector2(WorldCoords.VertexWorldX(row, r) - WorldCoords.Unit / 2,
                        WorldCoords.VertexWorldY(col, c) - WorldCoords.Unit / 2);
                    return !stockWet.Contains((r, c)) && !coast.Region.Contains(p) &&
                        (coast.JoinNorth is not float north || p.X <= north - coast.JoinWidth);
                });
                adt.RestoreUnchangedSurfaceFrom(baseline);
            }
            log($"coast area {coast.Region.Area}: {coast.Tiles.Count} tiles fitted, {changed} heights, {removed} coastal props cleared, {removedWater} exterior source-water cells removed");
        }
    }

    public static IEnumerable<AuditFinding> Verify(WorldPackAudit.AuditInput input)
    {
        foreach (var coast in Read(input.Docs))
        {
            int dryOutside = 0, wetInside = 0, checkedVertices = 0, liquidDifferences = 0, checkedLiquid = 0;
            foreach (var (col, row) in coast.Tiles)
            {
                var bytes = input.Built(WorldCoords.AdtPath(input.MapDirs[coast.Region.Map], col, row));
                if (bytes is null) { yield return new("G17", "error", "coast", $"tile {col},{row} missing", coast.Region.Map); continue; }
                var adt = AdtDocument.Parse(bytes, col, row);
                var stockBytes = input.Stock(WorldCoords.AdtPath(input.MapDirs[coast.Region.Map], col, row));
                var baseline = stockBytes is null ? null : AdtDocument.Parse(stockBytes, col, row);
                var actualWater = adt.LiquidCells().ToLookup(c => (c.Row, c.Col));
                var stockWater = (baseline?.LiquidCells() ?? []).ToLookup(c => (c.Row, c.Col));
                foreach (var key in actualWater.Select(g => g.Key).Union(stockWater.Select(g => g.Key)))
                {
                    var p = new Vector2(WorldCoords.VertexWorldX(row, key.Row) - WorldCoords.Unit / 2,
                        WorldCoords.VertexWorldY(col, key.Col) - WorldCoords.Unit / 2);
                    if (coast.Region.Contains(p) || (coast.JoinNorth is float north && p.X > north - coast.JoinWidth)) continue;
                    checkedLiquid++;
                    var a = actualWater[key].ToArray(); var b = stockWater[key].ToArray();
                    if (a.Length != b.Length || a.Zip(b).Any(pair => pair.First.Flags != pair.Second.Flags ||
                        Math.Abs(pair.First.H00 - pair.Second.H00) > .01f || Math.Abs(pair.First.H01 - pair.Second.H01) > .01f ||
                        Math.Abs(pair.First.H10 - pair.Second.H10) > .01f || Math.Abs(pair.First.H11 - pair.Second.H11) > .01f)) liquidDifferences++;
                }
                // Both the outer grid and the center of every cell participate in collision.
                // Source inner-height residuals can protrude even when the four corners are dry.
                for (int inner = 0; inner <= 1; inner++)
                for (int r = 0; r <= 128 - inner; r++) for (int c = 0; c <= 128 - inner; c++)
                {
                    var p = new Vector2(WorldCoords.VertexWorldX(row, r) - inner * WorldCoords.Unit / 2,
                        WorldCoords.VertexWorldY(col, c) - inner * WorldCoords.Unit / 2);
                    if (coast.JoinNorth is float north && p.X > north - coast.JoinWidth) continue;
                    if (coast.CoastDistance(p) < 10) continue;
                    float h = inner == 0 ? adt.OuterHeight(r, c) : adt.InnerHeight(r, c); checkedVertices++;
                    float stockHeight = baseline is null ? float.NegativeInfinity :
                        inner == 0 ? baseline.OuterHeight(r, c) : baseline.InnerHeight(r, c);
                    if (!coast.Region.Contains(p) && h > coast.SeaLevel + 0.1f &&
                        stockHeight <= coast.SeaLevel + 0.1f) dryOutside++;
                    if (coast.Region.Contains(p) && h < coast.SeaLevel + 0.1f) wetInside++;
                }
            }
            yield return new("G17", dryOutside + wetInside + liquidDifferences == 0 ? "info" : "error", $"coast area {coast.Region.Area}",
                $"{checkedVertices} terrain samples: {dryOutside} new dry land outside painted outline, {wetInside} submerged inside; {checkedLiquid} exterior liquid cells: {liquidDifferences} differ from stock; northern land join excluded", coast.Region.Map);
        }
    }
}
