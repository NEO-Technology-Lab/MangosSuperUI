using System.Buffers.Binary;
using System.Numerics;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// An oriented box in MODF placement space — one WMO group's MOGI bounds under its placement's
/// transform. The audit's unit of solid matter: two buildings clip when their group boxes
/// interpenetrate, a prop is "inside a wall" when its origin sits inside one.
/// </summary>
public readonly struct Obb
{
    public Vector3 Center { get; init; }
    public Vector3 AxisX { get; init; }
    public Vector3 AxisY { get; init; }
    public Vector3 AxisZ { get; init; }
    public Vector3 Half { get; init; }

    public static Obb FromLocal(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        var c = (min + max) * 0.5f;
        return new Obb
        {
            Center = Vector3.Transform(c, m),
            AxisX = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, m)),
            AxisY = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, m)),
            AxisZ = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, m)),
            Half = (max - min) * 0.5f,
        };
    }

    /// <summary>How deep <paramref name="p"/> is inside (positive) or how far outside (negative).</summary>
    public float Depth(Vector3 p)
    {
        var d = p - Center;
        float x = Half.X - MathF.Abs(Vector3.Dot(d, AxisX));
        float y = Half.Y - MathF.Abs(Vector3.Dot(d, AxisY));
        float z = Half.Z - MathF.Abs(Vector3.Dot(d, AxisZ));
        return MathF.Min(x, MathF.Min(y, z));
    }

    private float Radius(Vector3 axis) =>
        Half.X * MathF.Abs(Vector3.Dot(AxisX, axis)) + Half.Y * MathF.Abs(Vector3.Dot(AxisY, axis)) +
        Half.Z * MathF.Abs(Vector3.Dot(AxisZ, axis));

    /// <summary>Separating-axis penetration depth of two boxes (≤ 0 = apart).</summary>
    public static float Penetration(in Obb a, in Obb b)
    {
        Span<Vector3> axes = stackalloc Vector3[15];
        axes[0] = a.AxisX; axes[1] = a.AxisY; axes[2] = a.AxisZ;
        axes[3] = b.AxisX; axes[4] = b.AxisY; axes[5] = b.AxisZ;
        int n = 6;
        foreach (var u in new[] { a.AxisX, a.AxisY, a.AxisZ })
            foreach (var v in new[] { b.AxisX, b.AxisY, b.AxisZ })
            {
                var cr = Vector3.Cross(u, v);
                if (cr.LengthSquared() > 1e-6f) axes[n++] = Vector3.Normalize(cr);
            }
        float best = float.MaxValue;
        var t = b.Center - a.Center;
        for (int i = 0; i < n; i++)
        {
            float overlap = a.Radius(axes[i]) + b.Radius(axes[i]) - MathF.Abs(Vector3.Dot(t, axes[i]));
            if (overlap < best) best = overlap;
            if (best <= 0) return best;
        }
        return best;
    }

    /// <summary>A grid over the box's top and bottom faces (placement space), for ground sampling.</summary>
    public IEnumerable<Vector3> SamplePoints(int steps)
    {
        for (int i = 0; i <= steps; i++)
            for (int j = 0; j <= steps; j++)
                for (int k = 0; k <= 1; k++)
                {
                    var local = new Vector3(Half.X * (2f * i / steps - 1f), Half.Y * (2f * j / steps - 1f), Half.Z * (2f * k - 1f));
                    yield return Center + AxisX * local.X + AxisY * local.Y + AxisZ * local.Z;
                }
    }
}

public static class WorldPackGeometry
{
    /// <summary>MOGI group boxes (local WMO space) of a WMO root, with the group flags.</summary>
    public static List<(uint flags, Vector3 min, Vector3 max)> WmoGroups(byte[]? root)
    {
        var list = new List<(uint, Vector3, Vector3)>();
        if (root == null) return list;
        int at = 0;
        while (at + 8 <= root.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(at + 4));
            if (size < 0 || at + 8 + size > root.Length) break;
            if (root[at] == 'I' && root[at + 1] == 'G' && root[at + 2] == 'O' && root[at + 3] == 'M')
            {
                for (int e = at + 8; e + 32 <= at + 8 + size; e += 32)
                    list.Add((BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(e)), V(root, e + 4), V(root, e + 16)));
                break;
            }
            at += 8 + size;
        }
        return list;
    }

    private static Vector3 V(byte[] d, int at) => new(
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 4)),
        BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(at + 8)));

    /// <summary>WMO local → placement space; the same composition as <see cref="ModelBounds.WmoExtents"/>
    /// (and MSUIClient WmoRenderer.BuildPlacement).</summary>
    public static Matrix4x4 WmoMatrix(Vector3 pos, Vector3 rotDeg)
    {
        const float deg = MathF.PI / 180f;
        var basis = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        return basis
             * Matrix4x4.CreateRotationX(rotDeg.Z * deg)
             * Matrix4x4.CreateRotationZ(-rotDeg.X * deg)
             * Matrix4x4.CreateRotationY((rotDeg.Y - 90f) * deg)
             * Matrix4x4.CreateTranslation(pos);
    }

    /// <summary>Placement space (C−Y, Z, C−X) → world (X north, Y west, Z up).</summary>
    public static Vector3 ToWorld(Vector3 placement) => WorldCoords.PlacementToWorld(placement);

    /// <summary>Is this texture a road/path/street surface? Name-based (vanilla tilesets name them).</summary>
    public static bool IsRoadTexture(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).ToLowerInvariant();
        return n.Contains("road") || n.Contains("path") || n.Contains("cobble") || n.Contains("street") ||
               n.Contains("brick") || n.Contains("trail");
    }
}
