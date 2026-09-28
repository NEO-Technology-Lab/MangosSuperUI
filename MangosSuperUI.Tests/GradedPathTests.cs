using System.Numerics;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>
/// The graded-path law (doc kind "path", shared_docs/WORLD_BUILDER.md): the ground takes the path's height
/// inside half the width, blends back over the falloff, and the lane is clear of props.
/// </summary>
public class GradedPathTests
{
    // An east-west road 12 yd wide on Azeroth tile 29,34 (x north, y west): from y=1400 to y=1300 at x=-1000.
    private static readonly GradedPath Road = new("road", 0,
        new List<Vector3> { new(-1000, 1400, 50), new(-1000, 1300, 60) }, Width: 12, Falloff: 20);

    [Fact]
    public void Nearest_InterpolatesHeightAlongTheSegment()
    {
        var (d, z) = Road.Nearest(new Vector2(-1003, 1350));
        Assert.Equal(3f, d, 3);
        Assert.Equal(55f, z, 3);
    }

    [Fact]
    public void InLane_OnlyWithinHalfTheWidth()
    {
        Assert.True(Road.InLane(WorldCoords.WorldToPlacement(new Vector3(-1005, 1350, 55))));
        Assert.False(Road.InLane(WorldCoords.WorldToPlacement(new Vector3(-1007, 1350, 55))));
        Assert.False(Road.InLane(WorldCoords.WorldToPlacement(new Vector3(-1000, 1420, 55))));   // past the end
    }

    [Fact]
    public void Deltas_TakeThePathHeightOnTheLaneAndFadeToNothingAtTheReach()
    {
        int col = WorldCoords.TileCol(1350), row = WorldCoords.TileRow(-1000);
        var flat = new float[129 * 129];   // ground at z 0 everywhere
        var deltas = Road.Deltas(col, row, flat);
        float unit = WorldCoords.Tile / 128f;
        float x0 = (32 - row) * WorldCoords.Tile, y0 = (32 - col) * WorldCoords.Tile;
        int Index(float x, float y) => (int)MathF.Round((x0 - x) / unit) * 129 + (int)MathF.Round((y0 - y) / unit);
        // On the centreline the vertex becomes the path height (55 at the middle, grid-snapped).
        int mid = Index(-1000, 1350);
        Assert.InRange(deltas[mid], 54f, 56f);
        // Beyond half the width + the falloff nothing moves.
        Assert.False(deltas.ContainsKey(Index(-1000 - 6 - 20 - unit, 1350)));
    }
}
