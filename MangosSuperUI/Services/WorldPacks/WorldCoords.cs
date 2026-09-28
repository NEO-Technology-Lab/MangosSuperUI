using System.Numerics;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// The one coordinate law of the World Builder (MSUIClient shared_docs/WORLD_BUILDER.md §3).
///
/// Wire and database positions are WoW WORLD coordinates (X north, Y west, Z up). An ADT file
/// <c>World\Maps\{map}\{map}_{col}_{row}.adt</c> covers world
///   X ∈ ((32 - row - 1) * TILE, (32 - row) * TILE],  Y ∈ ((32 - col - 1) * TILE, (32 - col) * TILE]
/// and its 129×129 outer vertex grid (V9, <c>vertex = gridRow * 129 + gridCol</c>) sits at
///   worldX = (32 - row) * TILE - gridRow * UNIT,  worldY = (32 - col) * TILE - gridCol * UNIT
/// — the mapping MSUIClient's TerrainTile proved against server heights.
///
/// MODF/MDDF placement space is (C - worldY, worldZ, C - worldX) with C = 32 * TILE.
/// </summary>
public static class WorldCoords
{
    public const float Tile = 533.33333f;
    public const float Unit = Tile / 128f;          // one outer-vertex step
    public const float Chunk = Tile / 16f;          // one MCNK
    public const float Corner = 32f * Tile;          // 17066.666

    public static int TileCol(float worldY) => (int)MathF.Floor(32f - worldY / Tile);
    public static int TileRow(float worldX) => (int)MathF.Floor(32f - worldX / Tile);

    public static Vector3 WorldToPlacement(Vector3 world) =>
        new(Corner - world.Y, world.Z, Corner - world.X);

    public static Vector3 PlacementToWorld(Vector3 p) =>
        new(Corner - p.Z, Corner - p.X, p.Y);

    public static float VertexWorldX(int row, int gridRow) => (32 - row) * Tile - gridRow * Unit;
    public static float VertexWorldY(int col, int gridCol) => (32 - col) * Tile - gridCol * Unit;

    public static string AdtPath(string mapDir, int col, int row) =>
        $"World\\Maps\\{mapDir}\\{mapDir}_{col}_{row}.adt";

    public static string WdtPath(string mapDir) => $"World\\Maps\\{mapDir}\\{mapDir}.wdt";
}
