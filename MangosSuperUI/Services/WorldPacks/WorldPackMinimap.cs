using System.Numerics;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// Minimap images for the tiles a pack changed (verifier G11; MSUIClient shared_docs/WORLD_BUILDER.md §7).
///
/// A stamped tile used to show its SOURCE tile's Blizzard minimap and a reshaped stock tile its stock one - so Gilneas'
/// pass (land raised out of the sea in Azeroth 29,34) showed open sea, a stamp's edges stitched down into the sea
/// showed forest, and buildings a pack dropped were still drawn. Now every touched tile is compared with the ground its
/// image was drawn from, pixel by pixel (256 x 256, 2.08 yd each): where the published ground differs (height, water,
/// a dropped or placed building) the pixel is re-rendered from the published tile - its texture layers' colours,
/// hillshade, water by depth, placed buildings as roofs, trees as shade - with colours FITTED to the Blizzard image's
/// unchanged pixels of the same tile, and feathered in. Unchanged pixels stay Blizzard's art. Pure: callers supply
/// the tiles, the source image and texture colours.
/// </summary>
public static class WorldPackMinimap
{
    public const int Size = 256;

    /// <summary>The ground of one tile at minimap resolution.</summary>
    public sealed class Ground
    {
        public readonly float[] Height = new float[Size * Size];
        public readonly float[] Water = new float[Size * Size];        // liquid surface, NaN = none
        public readonly Vector3[] Albedo = new Vector3[Size * Size];   // texture blend colour, 0..255
        public readonly float[] Light = new float[Size * Size];        // sun (north-west) lambert, 0..1

        public bool Wet(int p) => !float.IsNaN(Water[p]) && Water[p] > Height[p] + 0.2f;
    }

    /// <summary>Colour model fitted to Blizzard minimaps: ground = gain * albedo * (0.45 + 0.75 light) + offset (per tile);
    /// water = shallow..deep along <see cref="DepthScale"/> (one model for all tiles - the sea is one colour).</summary>
    public sealed record Calibration(Vector3 Gain, Vector3 Offset, Vector3 Shallow, Vector3 Deep, int GroundSamples, int WaterSamples)
    {
        public static readonly Calibration Default = new(new(0.62f, 0.62f, 0.62f), new(4f, 6f, 4f),
            new(52f, 96f, 98f), new(12f, 38f, 52f), 0, 0);
    }

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(-0.6f, -0.6f, 1f));   // image x east, y south, z up

    /// <summary>Water depth on a log scale, 0 at the shore .. 1 at 300 yd: a coastal shallow and the open ocean
    /// (hundreds of yd) both fit one line (a linear 0..40 yd scale painted the open sea in shallow-coast grey).</summary>
    public static float DepthScale(float depth) => Math.Clamp(MathF.Log(1f + MathF.Max(0f, depth)) / MathF.Log(301f), 0f, 1f);

    public static Ground Sample(AdtDocument adt, Func<string, Vector3?> textureColor)
    {
        var g = new Ground();
        float[] h = adt.OuterHeights();
        var liquid = adt.LiquidCells().ToLookup(c => (c.Row, c.Col));
        float Water(float gr, float gc)
        {
            int r = Math.Clamp((int)gr, 0, 127), c = Math.Clamp((int)gc, 0, 127);
            float fr = gr - r, fc = gc - c, top = float.NaN;
            foreach (var cell in liquid[(r, c)])
            {
                float height = (cell.H00 * (1 - fc) + cell.H01 * fc) * (1 - fr) +
                    (cell.H10 * (1 - fc) + cell.H11 * fc) * fr;
                if (float.IsNaN(top) || height > top) top = height;
            }
            return top;
        }
        float H(float gr, float gc)
        {
            gr = Math.Clamp(gr, 0f, 128f); gc = Math.Clamp(gc, 0f, 128f);
            int r0 = Math.Min((int)gr, 127), c0 = Math.Min((int)gc, 127);
            float fr = gr - r0, fc = gc - c0;
            float a = h[r0 * 129 + c0], b = h[r0 * 129 + c0 + 1], c = h[(r0 + 1) * 129 + c0], d = h[(r0 + 1) * 129 + c0 + 1];
            return (a * (1 - fc) + b * fc) * (1 - fr) + (c * (1 - fc) + d * fc) * fr;
        }
        const float step = Size / 128f;   // minimap pixels per grid unit (2)
        for (int i = 0; i < 256; i++)
        {
            var (ix, iy, _, _, _) = adt.ChunkInfo(i);
            if (ix is < 0 or > 15 || iy is < 0 or > 15) continue;
            var layers = adt.ChunkLayers(i);
            var colours = layers.Select(l => textureColor(l.texture) ?? new Vector3(110, 100, 80)).ToArray();
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                {
                    int px = ix * 16 + x, py = iy * 16 + y, p = py * Size + px;
                    float gr = (py + 0.5f) / step, gc = (px + 0.5f) / step;
                    g.Height[p] = H(gr, gc);
                    // MCLQ can contain both wet and hidden cells within this same chunk.
                    g.Water[p] = Water(gr, gc);
                    int a = (y * 4 + 2) * 64 + x * 4 + 2;
                    var col = Vector3.Zero;
                    for (int k = 0; k < layers.Count; k++) col += colours[k] * layers[k].alpha[a];
                    g.Albedo[p] = layers.Count == 0 ? new Vector3(110, 100, 80) : col;
                    float east = (H(gr, gc + 0.5f) - H(gr, gc - 0.5f)) / WorldCoords.Unit;
                    float south = (H(gr + 0.5f, gc) - H(gr - 0.5f, gc)) / WorldCoords.Unit;
                    g.Light[p] = MathF.Max(0f, Vector3.Dot(Vector3.Normalize(new Vector3(-east, -south, 1f)), Sun));
                }
        }
        return g;
    }

    /// <summary>Pixels whose published ground differs from the ground the image was drawn from.</summary>
    public static bool[] Changed(Ground? source, Ground published, float tolerance = 1.5f)
    {
        var m = new bool[Size * Size];
        for (int p = 0; p < m.Length; p++)
        {
            if (source is null) { m[p] = true; continue; }
            bool ws = source.Wet(p), wp = published.Wet(p);
            // Water in both: only the seabed moved, the surface looks the same from above - keep Blizzard's sea
            // (repainting it left a band of a slightly different blue across open-sea tiles).
            m[p] = ws != wp || (!wp && MathF.Abs(source.Height[p] - published.Height[p]) > tolerance);
        }
        return m;
    }

    /// <summary>
    /// Water in both, but the seabed dropped far (a stamp's lake shore stitched down into the open ocean): 0 below 15 yd
    /// deeper, 1 at 75 yd, blurred - the weight the render takes over the source art there, so a light lake colour
    /// does not border the navy ocean of the next tile (Gilneas' west coast, 2026-09-27).
    /// </summary>
    public static float[] Deepening(Ground source, Ground published)
    {
        var w = new float[Size * Size];
        for (int p = 0; p < w.Length; p++)
            if (source.Wet(p) && published.Wet(p))
                w[p] = Math.Clamp((source.Height[p] - published.Height[p] - 15f) / 60f, 0f, 1f);
        var outW = new float[w.Length];
        const int r = 4;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float sum = 0; int n = 0;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, Size - 1), yy = Math.Clamp(y + dy, 0, Size - 1);
                        sum += w[yy * Size + xx]; n++;
                    }
                outW[y * Size + x] = published.Wet(y * Size + x) ? sum / n : 0f;
            }
        return outW;
    }

    /// <summary>Mark a box (tile pixel coordinates) as changed - a dropped or a placed building.</summary>
    public static void Mark(bool[] m, float x0, float y0, float x1, float y1)
    {
        for (int y = Math.Max(0, (int)MathF.Floor(y0)); y <= Math.Min(Size - 1, (int)MathF.Ceiling(y1)); y++)
            for (int x = Math.Max(0, (int)MathF.Floor(x0)); x <= Math.Min(Size - 1, (int)MathF.Ceiling(x1)); x++)
                m[y * Size + x] = true;
    }

    /// <summary>Fit the colour model to the Blizzard image's UNCHANGED pixels of the same tile (its own ground).</summary>
    public static Calibration Fit(byte[] imageBgra, Ground source, bool[] changed, Calibration fallback,
        List<(float d, Vector3 colour)>? waterPool = null)
    {
        var gx = new List<(Vector3 x, Vector3 y)>();
        var wx = new List<(float d, Vector3 y)>();
        for (int p = 0; p < Size * Size; p += 3)
        {
            if (changed[p]) continue;
            var y = new Vector3(imageBgra[p * 4 + 2], imageBgra[p * 4 + 1], imageBgra[p * 4]);
            if (source.Wet(p)) wx.Add((DepthScale(source.Water[p] - source.Height[p]), y));
            else gx.Add((source.Albedo[p] * (0.45f + 0.75f * source.Light[p]), y));
        }
        Vector3 gain = fallback.Gain, offset = fallback.Offset, shallow = fallback.Shallow, deep = fallback.Deep;
        if (gx.Count >= 400)
            for (int c = 0; c < 3; c++)
            {
                var (a, b) = Line(gx.Select(v => (v.x[c], v.y[c])));
                gain[c] = Math.Clamp(a, 0.2f, 3f);
                offset[c] = Math.Clamp(b, -60f, 120f);
            }
        if (wx.Count >= 400)
            for (int c = 0; c < 3; c++)
            {
                var (a, b) = Line(wx.Select(v => (v.d, v.y[c])));
                shallow[c] = Math.Clamp(b, 0f, 255f);
                deep[c] = Math.Clamp(b + a, 0f, 255f);
            }
        waterPool?.AddRange(wx);
        return new Calibration(gain, offset, shallow, deep, gx.Count, wx.Count);
    }

    /// <summary>One water model (shallow, deep) from every sampled water pixel of every image (touched tiles and the
    /// stock sea around them); the default when there are too few.</summary>
    public static (Vector3 shallow, Vector3 deep) FitWater(List<(float d, Vector3 colour)> pool)
    {
        if (pool.Count < 400) return (Calibration.Default.Shallow, Calibration.Default.Deep);
        Vector3 shallow = default, deep = default;
        for (int c = 0; c < 3; c++)
        {
            var (a, b) = Line(pool.Select(v => (v.d, v.colour[c])));
            shallow[c] = Math.Clamp(b, 0f, 255f);
            deep[c] = Math.Clamp(b + a, 0f, 255f);
        }
        return (shallow, deep);
    }

    private static (float slope, float intercept) Line(IEnumerable<(float x, float y)> pts)
    {
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in pts) { n++; sx += x; sy += y; sxx += x * x; sxy += x * y; }
        double den = n * sxx - sx * sx;
        // No spread in x (every sample at one depth / one brightness): no slope to learn, the mean is the answer.
        if (n < 2 || sxx / n - (sx / n) * (sx / n) < 1e-6) return (0f, (float)(n > 0 ? sy / n : 0));
        double a = (n * sxy - sx * sy) / den;
        return ((float)a, (float)((sy - a * sx) / n));
    }

    /// <summary>
    /// The published tile's minimap (BGRA, 256 x 256): <paramref name="source"/> (the Blizzard image, or null for a tile
    /// that had none) where nothing changed, the fitted render where it did, feathered over 3 px. Placed buildings
    /// (<paramref name="roofs"/>, tile pixel boxes) are drawn as roofs; trees (<paramref name="trees"/>, pixel centre
    /// + radius) as shade, only inside re-rendered pixels (Blizzard's art already has its own).
    /// </summary>
    public static byte[] Compose(byte[]? source, Ground published, bool[] changed, Calibration cal,
        IEnumerable<(Vector2 centre, float radius)> trees, IEnumerable<(float x0, float y0, float x1, float y1)> roofs,
        float[]? deepened = null)
    {
        var alpha = Feather(changed);
        if (deepened != null)
            for (int p = 0; p < alpha.Length; p++) alpha[p] = MathF.Max(alpha[p], deepened[p]);
        var gen = new Vector3[Size * Size];
        for (int p = 0; p < gen.Length; p++)
        {
            if (alpha[p] <= 0f && source is not null) continue;
            if (published.Wet(p))
            {
                gen[p] = Vector3.Lerp(cal.Shallow, cal.Deep, DepthScale(published.Water[p] - published.Height[p]));
            }
            else gen[p] = cal.Gain * (published.Albedo[p] * (0.45f + 0.75f * published.Light[p])) + cal.Offset;
        }
        foreach (var (centre, radius) in trees)
        {
            float r = MathF.Max(radius, 1.2f);
            for (int y = (int)(centre.Y - r - 1); y <= (int)(centre.Y + r + 1); y++)
                for (int x = (int)(centre.X - r - 1); x <= (int)(centre.X + r + 1); x++)
                {
                    if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                    int p = y * Size + x;
                    if ((alpha[p] <= 0f && source is not null) || published.Wet(p)) continue;
                    float t = 1f - Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), centre) / r;
                    if (t > 0f) gen[p] *= 1f - 0.45f * MathF.Min(1f, t * 1.6f);
                }
        }
        foreach (var (x0, y0, x1, y1) in roofs)
            for (int y = Math.Max(0, (int)y0); y <= Math.Min(Size - 1, (int)y1); y++)
                for (int x = Math.Max(0, (int)x0); x <= Math.Min(Size - 1, (int)x1); x++)
                {
                    bool edge = y == (int)y0 || y == (int)y1 || x == (int)x0 || x == (int)x1;
                    gen[y * Size + x] = cal.Gain * (edge ? new Vector3(200, 190, 175) : new Vector3(150, 138, 125)) + cal.Offset;
                }
        var outBgra = new byte[Size * Size * 4];
        for (int p = 0; p < gen.Length; p++)
        {
            float a = source is null ? 1f : alpha[p];
            var src = source is null ? Vector3.Zero : new Vector3(source[p * 4 + 2], source[p * 4 + 1], source[p * 4]);
            var c = src * (1f - a) + gen[p] * a;
            outBgra[p * 4] = (byte)Math.Clamp(c.Z, 0f, 255f);
            outBgra[p * 4 + 1] = (byte)Math.Clamp(c.Y, 0f, 255f);
            outBgra[p * 4 + 2] = (byte)Math.Clamp(c.X, 0f, 255f);
            outBgra[p * 4 + 3] = 255;
        }
        return outBgra;
    }

    /// <summary>One touched tile, ready to render: its published ground, what differs from the ground its image shows,
    /// the buildings the pack placed (roofs), its trees, and the colour fit of its own Blizzard image (null: none).</summary>
    public sealed record Tile(Ground Published, bool[] Changed, List<(float x0, float y0, float x1, float y1)> Roofs,
        List<(Vector2 centre, float radius)> Trees, Calibration? Fit, float[]? Deepened = null)
    {
        public int ChangedPixels => Changed.Count(c => c) + (Deepened?.Count(w => w > 0.05f) ?? 0);
    }

    /// <summary>
    /// Compare a published tile with the tile its image was drawn from (a stamp's SOURCE tile, else the stock tile at the
    /// same place; null when there was no terrain - open sea): heights, water, and buildings - one the image shows that
    /// is gone, or one the pack placed. <paramref name="image"/> (BGRA 256 x 256) is that tile's Blizzard minimap.
    /// </summary>
    public static Tile Prepare(AdtDocument published, int col, int row, AdtDocument? source, int sourceCol, int sourceRow,
        byte[]? image, Func<string, byte[]?> readFile, Func<string, Vector3?> textureColour,
        Dictionary<string, (Vector3, Vector3)?>? wmoSizeCache = null, List<(float d, Vector3 colour)>? waterPool = null)
    {
        var sizes = wmoSizeCache ?? new(StringComparer.OrdinalIgnoreCase);
        List<(string path, float x0, float y0, float x1, float y1)> Footprints(AdtDocument adt, int c, int r)
        {
            var list = new List<(string, float, float, float, float)>();
            const float k = Size / WorldCoords.Tile;
            foreach (var (path, _, pos, rot) in adt.WmoPlacementsFull())
            {
                if (!sizes.TryGetValue(path, out var b)) sizes[path] = b = ModelBounds.Wmo(readFile(path));
                if (b == null) continue;
                var (min, max) = ModelBounds.WmoExtents(b.Value.Item1, b.Value.Item2, pos, rot);
                list.Add((path, (min.X - c * WorldCoords.Tile) * k, (min.Z - r * WorldCoords.Tile) * k,
                                (max.X - c * WorldCoords.Tile) * k, (max.Z - r * WorldCoords.Tile) * k));
            }
            return list;
        }
        var src = source == null ? null : Sample(source, textureColour);
        var pub = Sample(published, textureColour);
        var changed = Changed(src, pub);
        var before = source == null ? new() : Footprints(source, sourceCol, sourceRow);
        var after = Footprints(published, col, row);
        static bool Same((string p, float x0, float y0, float x1, float y1) a, (string p, float x0, float y0, float x1, float y1) b) =>
            string.Equals(a.p, b.p, StringComparison.OrdinalIgnoreCase) && MathF.Abs(a.x0 - b.x0) < 1f && MathF.Abs(a.y0 - b.y0) < 1f;
        foreach (var w in before.Where(w => !after.Any(a => Same(w, a)))) Mark(changed, w.x0, w.y0, w.x1, w.y1);
        var roofs = after.Where(a => !before.Any(w => Same(w, a))).Select(a => (a.x0, a.y0, a.x1, a.y1)).ToList();
        foreach (var r in roofs) Mark(changed, r.Item1, r.Item2, r.Item3, r.Item4);
        const float kk = Size / WorldCoords.Tile;
        var trees = published.DoodadPlacements().Select(d => (
            new Vector2((d.pos.X - col * WorldCoords.Tile) * kk, (d.pos.Z - row * WorldCoords.Tile) * kk), 1.3f * MathF.Max(0.5f, d.scale))).ToList();
        var fit = image != null && src != null ? Fit(image, src, changed, Calibration.Default, waterPool) : null;
        return new Tile(pub, changed, roofs, trees, fit, src == null ? null : Deepening(src, pub));
    }

    /// <summary>The tile's minimap (BGRA): its own fit where its image had enough ground/water, else <paramref name="mean"/>.</summary>
    public static byte[] Render(Tile t, byte[]? image, Calibration mean)
    {
        var cal = t.Fit is { GroundSamples: >= 400 } f ? f with { Shallow = mean.Shallow, Deep = mean.Deep } : mean;
        return Compose(image, t.Published, t.Changed, cal, t.Trees, t.Roofs, t.Deepened);
    }

    /// <summary>The average ground fit of several tiles' images (for a tile without enough ground of its own) with the
    /// shared water model (<see cref="FitWater"/>) - the calibration <see cref="Render"/> takes.</summary>
    public static Calibration Mean(IEnumerable<Calibration?> fits, List<(float d, Vector3 colour)>? waterPool = null)
    {
        var all = fits.Where(f => f != null).Select(f => f!).ToList();
        var g = all.Where(f => f.GroundSamples >= 400).ToList();
        var w = all.Where(f => f.WaterSamples >= 400).ToList();
        var d = Calibration.Default;
        static Vector3 Avg(List<Calibration> l, Func<Calibration, Vector3> f) =>
            new(l.Average(x => f(x).X), l.Average(x => f(x).Y), l.Average(x => f(x).Z));
        var (shallow, deep) = waterPool != null ? FitWater(waterPool)
            : (w.Count > 0 ? Avg(w, f => f.Shallow) : d.Shallow, w.Count > 0 ? Avg(w, f => f.Deep) : d.Deep);
        return new Calibration(g.Count > 0 ? Avg(g, f => f.Gain) : d.Gain, g.Count > 0 ? Avg(g, f => f.Offset) : d.Offset,
            shallow, deep, g.Count, waterPool?.Count ?? w.Count);
    }

    /// <summary>Changed mask grown by 2 px, then a 7 x 7 box blur: 1 inside a change, fading out at its rim.</summary>
    public static float[] Feather(bool[] changed)
    {
        var grown = new float[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                if (!changed[y * Size + x]) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && yy >= 0 && xx < Size && yy < Size) grown[yy * Size + xx] = 1f;
                    }
            }
        var outA = new float[Size * Size];
        const int r = 3;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float sum = 0; int n = 0;
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, Size - 1), yy = Math.Clamp(y + dy, 0, Size - 1);
                        sum += grown[yy * Size + xx]; n++;
                    }
                outA[y * Size + x] = changed[y * Size + x] ? 1f : sum / n;
            }
        return outA;
    }
}
