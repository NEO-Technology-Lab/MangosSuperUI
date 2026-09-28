using System.Globalization;
using System.Text.Json.Nodes;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// Move a pack's region: everything the pack placed on <see cref="FromMap"/> moves to <see cref="ToMap"/>
/// shifted by a whole number of ADT tiles (<see cref="DCol"/>, <see cref="DRow"/>), so stamped tiles land on
/// tile boundaries and every coordinate moves by the same world offset. Built for Gilneas 2026-09-27: a zone
/// had been a separate map behind a teleport at the Greymane Wall; zones in WoW are seamless continent land.
/// </summary>
public sealed record RelocateSpec(int FromMap, int ToMap, int DCol, int DRow, bool ToStockMap)
{
    /// <summary>World X is north (row grows southward), world Y is west (col grows eastward).</summary>
    public float Dx => -DRow * WorldCoords.Tile;
    public float Dy => -DCol * WorldCoords.Tile;
}

/// <summary>
/// The pure doc half of a region move (MangosSuperUI.Tests: WorldPackRelocationTests). One doc in → the
/// doc changes out: a modified body, a delete, or a delete + create when the key itself carries the
/// location (tile docs: "map:col:row"). Docs that do not reference <see cref="RelocateSpec.FromMap"/>
/// are untouched. Rotations never change (a translation, not a turn).
/// </summary>
public static class WorldPackRelocation
{
    public readonly record struct Change(string Kind, string Key, string? Body);

    public static List<Change> Transform(string kind, string key, JsonObject? body, RelocateSpec r)
    {
        var none = new List<Change>();
        if (body is null) return none;
        var b = (JsonObject)body.DeepClone();
        bool changed = false;

        switch (kind)
        {
            case "tile":
                if (I(b["map"]) != r.FromMap) return none;
                b["map"] = r.ToMap;
                b["col"] = I(b["col"]) + r.DCol;
                b["row"] = I(b["row"]) + r.DRow;
                ShiftPoints(b["healHoles"] as JsonArray, r);
                ShiftPoints(b["areaPaint"] as JsonArray, r);
                string newKey = $"{r.ToMap}:{I(b["col"])}:{I(b["row"])}";
                return newKey == key
                    ? new() { new(kind, key, b.ToJsonString()) }
                    : new() { new(kind, key, null), new(kind, newKey, b.ToJsonString()) };

            case "map":
                // A pack map that becomes continent land stops existing as a map.
                return I(b["mapId"]) == r.FromMap && r.ToStockMap ? new() { new(kind, key, null) } : none;

            case "dbc:Map":
                return key == r.FromMap.ToString(CultureInfo.InvariantCulture) && r.ToStockMap ? new() { new(kind, key, null) } : none;

            case "dbrow:map_template":
                if (I(b["entry"]) == r.FromMap && r.ToStockMap) return new() { new(kind, key, null) };
                if (I(b["ghost_entrance_map"]) == r.FromMap)
                {
                    b["ghost_entrance_map"] = r.ToMap;
                    Shift(b, "ghost_entrance_x", "ghost_entrance_y", r);
                    changed = true;
                }
                break;

            case "dbrow:creature":
            case "dbrow:gameobject":
                if (I(b["map"]) == r.FromMap) { b["map"] = r.ToMap; Shift(b, "position_x", "position_y", r); changed = true; }
                break;

            case "dbrow:areatrigger_template":
                if (I(b["map_id"]) == r.FromMap) { b["map_id"] = r.ToMap; Shift(b, "x", "y", r); changed = true; }
                break;

            case "dbrow:areatrigger_teleport":
                if (I(b["target_map"]) == r.FromMap) { b["target_map"] = r.ToMap; Shift(b, "target_position_x", "target_position_y", r); changed = true; }
                break;

            case "dbrow:area_template":
                if (I(b["map_id"]) == r.FromMap) { b["map_id"] = r.ToMap; changed = true; }
                break;

            case "dbc:AreaTrigger":
            case "dbc:WorldSafeLocs":
                if (b["fields"] is JsonObject f && I(f["1"]) == r.FromMap)
                {
                    f["1"] = r.ToMap;
                    f["2"] = new JsonObject { ["f"] = D(f["2"]) + r.Dx };
                    f["3"] = new JsonObject { ["f"] = D(f["3"]) + r.Dy };
                    changed = true;
                }
                break;

            case "dbc:AreaTable":
                if (b["fields"] is JsonObject at && I(at["1"]) == r.FromMap) { at["1"] = r.ToMap; changed = true; }
                break;

            case "dbc:Light":
                // A pack map clones the continent's global light; a continent already has its own.
                if (b["fields"] is JsonObject lf && I(lf["1"]) == r.FromMap)
                {
                    if (r.ToStockMap) return new() { new(kind, key, null) };
                    lf["1"] = r.ToMap; changed = true;
                }
                break;
        }
        return changed ? new() { new(kind, key, b.ToJsonString()) } : none;
    }

    private static void Shift(JsonObject b, string xKey, string yKey, RelocateSpec r)
    {
        b[xKey] = Math.Round(D(b[xKey]) + r.Dx, 3);
        b[yKey] = Math.Round(D(b[yKey]) + r.Dy, 3);
    }

    private static void ShiftPoints(JsonArray? points, RelocateSpec r)
    {
        foreach (var p in points?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            Shift(p, "x", "y", r);
    }

    /// <summary>Numbers arrive as JSON numbers, numeric strings or DBC floats <c>{ "f": x }</c>.</summary>
    public static double D(JsonNode? n) => n switch
    {
        JsonObject o when o["f"] is JsonNode f => D(f),
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) => s,
        _ => 0d,
    };

    public static int I(JsonNode? n) => (int)Math.Round(D(n));
}
