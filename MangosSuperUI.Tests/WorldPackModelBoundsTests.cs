using System.Buffers.Binary;
using System.Numerics;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

public class WorldPackModelBoundsTests
{
    private const string Dock = @"World\Lordaeron\SilverPine\PassiveDoodads\Docks\SilverPineDocks01.m2";

    private static string? DataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.GetFullPath(Path.Combine(dir.FullName, "..", "MSUIClient", "GameData", "Data"));
            if (File.Exists(Path.Combine(path, "terrain.MPQ"))) return path;
        }
        return null;
    }

    private static byte[] Collision(params (Vector3 a, Vector3 b, Vector3 c)[] triangles)
    {
        int count = triangles.Length * 3, verticesAt = 0x100 + count * 2;
        var bytes = new byte[verticesAt + count * 12];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'D';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xEC), count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xF0), 0x100);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xF4), count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0xF8), verticesAt);
        int i = 0;
        foreach (var triangle in triangles)
            foreach (var v in new[] { triangle.a, triangle.b, triangle.c })
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x100 + i * 2), (ushort)i);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(verticesAt + i * 12), v.X);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(verticesAt + i * 12 + 4), v.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(verticesAt + i * 12 + 8), v.Z);
                i++;
            }
        return bytes;
    }

    [Fact]
    public void WalkableTop_RejectsLargeUndersideAndSmallHighPostsAndKeepsExactHeight()
    {
        const float deck = 5.92202425f;
        var model = Collision(
            (new(0, 0, 2.859f), new(0, 30, 2.859f), new(30, 0, 2.859f)), // larger downward face
            (new(0, 0, deck), new(20, 0, deck), new(0, 20, deck)),
            (new(0, 0, 8), new(1, 0, 8), new(0, 1, 8))); // small upward post cap
        Assert.Equal(deck, ModelBounds.M2WalkableTop(model)!.Value, 5);
        Assert.Null(ModelBounds.M2WalkableTop(Collision(
            (new(0, 0, 2), new(0, 10, 2), new(10, 0, 2)))));
    }

    [Fact]
    public void RealSilverpineDock_UsesUpwardDeckAt5922RatherThanUndersideAt2859()
    {
        string? data = DataDir(); if (data is null) return;
        using var stock = new VanillaArchiveSet(data);
        byte[] model = stock.ReadFile(Dock)!;
        Assert.NotNull(model);
        float deck = ModelBounds.M2WalkableTop(model)!.Value;
        Assert.InRange(deck, 5.9220f, 5.9221f);
        Assert.InRange(deck + .2f, 6.1220f, 6.1221f);
        Assert.InRange(ModelBounds.M2WalkableSurface(model)!.SlopeDegrees, 0, .01f);
        Assert.True(ModelBounds.M2(model)!.Value.max.Z > deck + 1,
            "the fixture must have posts above the walking deck");
    }

    [Fact]
    public void RealSilverpineRamp_GroupsItsTwoSlopingTrianglesAndReportsTheirFullSpan()
    {
        string? data = DataDir(); if (data is null) return;
        using var stock = new VanillaArchiveSet(data);
        var surface = ModelBounds.M2WalkableSurface(stock.ReadFile(Dock.Replace("01.m2", "02.m2")))!;
        Assert.NotNull(surface);
        Assert.InRange(surface.MinimumHeight, -.417f, -.416f);
        Assert.InRange(surface.MaximumHeight, 7.172f, 7.173f);
        Assert.InRange(surface.SlopeDegrees, 21.7f, 21.8f);
        Assert.InRange(surface.Center.Z, 3.377f, 3.379f);
        Assert.Equal(6, surface.Vertices.Length); // both triangles, not one centroid's Z bucket
    }

    [Fact]
    public void WaterAudit_ScaledRampReportsSpanAndRequiresWalkingProof()
    {
        string? data = DataDir(); if (data is null) return;
        using var stock = new VanillaArchiveSet(data);
        var input = new WorldPackAudit.AuditInput
        {
            Stock = stock.ReadFile, Built = stock.ReadFile, MapDirs = new() { [0] = "Azeroth" }, Docs = [],
            Placements = [new() { Id = 12, MapId = 0, Kind = "m2", ModelPath = Dock.Replace("01.m2", "02.m2"),
                PosX = -1468, PosY = 2596.424f, PosZ = 3.225f, RotY = 180, Scale = .4f }]
        };
        var finding = Assert.Single(new WorldPackAudit(input).Run().Where(f => f.Check == "G10"));
        Assert.Equal("info", finding.Severity);
        Assert.Contains("sloped walkable surface spans 3.1–6.1 yd", finding.Message);
        Assert.Contains("21.7 degrees", finding.Message);
        Assert.Contains("entry/exit walking proof required", finding.Message);
    }

    [Theory]
    [InlineData(.2f, "warn", "deck 6.1")]
    [InlineData(-2.5f, "info", "deck 3.4")]
    public void WaterAudit_UsesActualDockDeckForItsHeightWarning(float z, string severity, string message)
    {
        string? data = DataDir(); if (data is null) return;
        using var stock = new VanillaArchiveSet(data);
        var input = new WorldPackAudit.AuditInput
        {
            Stock = stock.ReadFile, Built = stock.ReadFile, MapDirs = new() { [0] = "Azeroth" }, Docs = [],
            Placements = [new() { Id = 8, MapId = 0, Kind = "m2", ModelPath = Dock,
                PosX = -1468, PosY = 2610.33f, PosZ = z, RotY = 90, Scale = 1 }]
        };
        var finding = Assert.Single(new WorldPackAudit(input).Run().Where(f => f.Check == "G10"));
        Assert.Equal(severity, finding.Severity);
        Assert.Contains(message, finding.Message);
    }
}
