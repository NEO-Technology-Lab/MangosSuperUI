using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using MangosSuperUI.Services;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

public class WorldPackWorldMapTests
{
    // Exactly two ADTs: columns 28..29, rows 34..35. Four hover cells per ADT.
    private static JsonObject Body(int area = 7001, string directory = "Gilneas") => JsonNode.Parse(
        $$"""{"map":0,"area":{{area}},"directory":"{{directory}}","polygon":[[-1066.6667,2133.3333],[-1066.6667,1066.6667],[-2133.3333,1066.6667],[-2133.3333,2133.3333]]}""")!.AsObject();

    private static List<DocRow> Docs() => new()
    {
        new() { PackId = 1, Kind = "worldmap", DocKey = "0:7001", Body = Body().ToJsonString() },
        new() { PackId = 1, Kind = "dbc:AreaTable", DocKey = "7001", Body = """{"fields":{"1":0,"2":0,"11":"Gilneas"}}""" },
    };

    [Fact]
    public void Zmp_WorldAxesAndHalfTileOwnership_AreExactAndPreserveEveryOutsideCell()
    {
        var region = WorldPackWorldMap.Parse(Body());
        var stock = new byte[128 * 128 * 4];
        for (int i = 0; i < 128 * 128; i++) BinaryPrimitives.WriteUInt32LittleEndian(stock.AsSpan(i * 4), (uint)(100 + i));
        byte[] built = WorldPackWorldMap.PatchZoneMap(stock, new[] { region });
        int changed = 0;
        for (int row = 0; row < 128; row++) for (int col = 0; col < 128; col++)
        {
            int i = row * 128 + col;
            bool owned = col is >= 56 and <= 59 && row is >= 68 and <= 71;
            Assert.Equal(owned ? 7001U : (uint)(100 + i), BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(i * 4)));
            Assert.Equal((uint)(100 + i), BinaryPrimitives.ReadUInt32LittleEndian(stock.AsSpan(i * 4)));
            if (owned) changed++;
        }
        Assert.Equal(16, changed);
    }

    [Fact]
    public void Zmp_ConcaveOutline_DoesNotFillTheBoundingRectangle()
    {
        var region = new WorldPackWorldMap.Region(0, 7001, "Test", new[] {
            new Vector2(0, 0), new Vector2(0, 1600), new Vector2(-1600,1600),
            new Vector2(-1600, 800), new Vector2(-800,800), new Vector2(-800,0),
        });
        byte[] built = WorldPackWorldMap.PatchZoneMap(new byte[128 * 128 * 4], new[] { region });
        Assert.Equal(7001U, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan((65 * 128 + 63) * 4)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan((69 * 128 + 63) * 4)));
        Assert.Equal(7001U, BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan((69 * 128 + 59) * 4)));
    }

    [Fact]
    public void Zmp_OverlappingZonesAreRejectedInsteadOfDependingOnBuildOrder()
    {
        var a = WorldPackWorldMap.Parse(Body());
        var b = a with { Area = 7002, Directory = "AnotherZone" };
        Assert.Throws<InvalidOperationException>(() => WorldPackWorldMap.PatchZoneMap(new byte[128 * 128 * 4], new[] { a, b }));
    }

    [Fact]
    public void Bounds_AndHighlightUseClientWorldMapProjectionAndVerticalCrop()
    {
        var r = WorldPackWorldMap.Parse(Body());
        var b = r.Bounds;
        Assert.InRange(b.Width / b.Height, 1.49999f, 1.50001f);
        Assert.Equal(85, b.PixelHeight);
        Assert.Equal(128, b.FileHeight);
        Assert.True(r.Polygon.All(p => p.X < b.Top && p.X > b.Bottom && p.Y < b.Left && p.Y > b.Right));
        var pixels = WorldPackWorldMap.HighlightPixels(r);
        int visible = 0;
        for (int y = 0; y < b.FileHeight; y++) for (int x = 0; x < 128; x++)
        {
            int offset = (y * 128 + x) * 4;
            Assert.Equal(255, pixels[offset + 3]);
            // Independent inversion of the client UMax=1, VMax=85/128 texture crop.
            float wx = b.Top - (y + .5f) / 85 * b.Height, wy = b.Left - (x + .5f) / 128 * b.Width;
            bool expected = y < 85 && wx is < -1066.6667f and > -2133.3333f && wy is < 2133.3333f and > 1066.6667f;
            Assert.Equal(expected, pixels[offset] > 0);
            if (expected) visible++;
        }
        Assert.True(visible > 6000);
    }

    [Fact]
    public void Region_ContainsAndDistanceSupportCoastShaping()
    {
        var r = WorldPackWorldMap.Parse(Body());
        Assert.True(r.Contains(new(-1600, 1600)));
        Assert.False(r.Contains(new(-2600, 1600)));
        Assert.InRange(r.DistanceToBoundary(new(-1600, 1600)), 533.32f, 533.35f);
        Assert.InRange(r.DistanceToBoundary(new(-2600, 1600)), 466.65f, 466.68f);
    }

    [Theory]
    [InlineData("[[0,0],[0,1600],[-1600,0],[-1600,1600]]")]
    [InlineData("[[0,0],[0,1600],[0,800]]")]
    [InlineData("[[0,0],[0,10],[10,0]]")]
    [InlineData("[[0,0],[0,20000],[1000,0]]")]
    public void InvalidOrUnhoverablePolygonsAreRejected(string polygon)
    {
        var body = Body(); body["polygon"] = JsonNode.Parse(polygon);
        Assert.Throws<ArgumentException>(() => WorldPackWorldMap.Parse(body));
    }

    [Fact]
    public void Region_ExplicitClosureIsAcceptedAndForeignMetadataPreserved()
    {
        var body = Body(); var polygon = body["polygon"]!.AsArray(); polygon.Add(polygon[0]!.DeepClone());
        body["terrain"] = new JsonObject { ["coastWidth"] = 120 };
        Assert.Equal(4, WorldPackWorldMap.Parse(body).Polygon.Length);
        Assert.Equal(120, (int)body["terrain"]!["coastWidth"]!);
    }

    [Fact]
    public void GeneratedWorldMapArea_UsesPolygonBoundsAndRejectsConflictingExplicitRows()
    {
        var docs = Docs();
        var row = Assert.Single(WorldPackWorldMap.AreaDocuments(docs));
        var fields = JsonNode.Parse(row.Body)!["fields"]!;
        Assert.Equal("Gilneas", (string?)fields["3"]);
        Assert.Equal(7001, (int)fields["2"]!);
        Assert.InRange((float)fields["4"]!["f"]!, 2425, 2427);
        docs.Add(row);
        Assert.Throws<InvalidOperationException>(() => WorldPackWorldMap.AreaDocuments(docs));
    }

    [Fact]
    public void AreaNameAndContinentMustBeExplicitAndConsistent()
    {
        var docs = Docs(); docs[1].Body = """{"fields":{"1":1,"2":0,"11":"Gilneas"}}""";
        Assert.Throws<InvalidOperationException>(() => WorldPackWorldMap.AreaDocuments(docs));
        docs[1].Body = """{"fields":{"1":0,"2":7000,"11":"Gilneas"}}""";
        Assert.Throws<InvalidOperationException>(() => WorldPackWorldMap.AreaDocuments(docs));
    }

    [Fact]
    public void BuildAssets_BlpRoundTripAndPublishedAuditDetectCorruptedOwnershipAndBounds()
    {
        var docs = Docs();
        var stock = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) {
            [WorldPackWorldMap.AreaPath] = Dbc(8, new uint[] { 0, 0, 0, 1, F(18166), F(-18166), F(12111), F(-12111) }, "\0Azeroth\0"),
            [@"Interface\WorldMap\Azeroth.zmp"] = new byte[128 * 128 * 4],
        };
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        WorldPackWorldMap.BuildAssets(p => stock.GetValueOrDefault(p), docs, files);
        Assert.Equal(0, files[@"Interface\WorldMap\Gilneas\GilneasHighlight.blp"][9]); // RGB-on-black ADD art, no alpha channel.
        var b = WorldPackWorldMap.Parse(Body()).Bounds;
        var areaDbc = DbcWriterService.ReadDbc(stock[WorldPackWorldMap.AreaPath], "test");
        areaDbc.AddRow(new uint[] { 7001, 0, 7001, areaDbc.AddString("Gilneas"), F(b.Left), F(b.Right), F(b.Top), F(b.Bottom) });
        files[WorldPackWorldMap.AreaPath] = areaDbc.Write();
        var name = new uint[28]; name[0] = 7001; name[11] = 1;
        files[WorldPackWorldMap.NamesPath] = Dbc(28, name, "\0Gilneas\0");
        List<AuditFinding> Verify() => WorldPackWorldMap.Verify(docs, p => stock.GetValueOrDefault(p), p => files.GetValueOrDefault(p)).ToList();
        Assert.DoesNotContain(Verify(), f => f.Severity == "error");
        files[@"Interface\WorldMap\Azeroth.zmp"][0] = 1; // unrelated stock territory must survive.
        Assert.Contains(Verify(), f => f.Severity == "error" && f.Subject.EndsWith(".zmp"));
        files[@"Interface\WorldMap\Azeroth.zmp"][0] = 0;
        areaDbc.PatchRowFloat(7001, 4, b.Left + 40); files[WorldPackWorldMap.AreaPath] = areaDbc.Write();
        Assert.Contains(Verify(), f => f.Severity == "error" && f.Message.Contains("WorldMapArea"));
        files.Remove(@"Interface\WorldMap\Gilneas\GilneasHighlight.blp");
        Assert.Contains(Verify(), f => f.Severity == "error" && f.Message.Contains("BLP missing"));
    }

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);
    private static byte[] Dbc(int fieldCount, uint[] row, string strings)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        byte[] text = Encoding.UTF8.GetBytes(strings);
        writer.Write(Encoding.ASCII.GetBytes("WDBC")); writer.Write(1); writer.Write(fieldCount); writer.Write(fieldCount * 4); writer.Write(text.Length);
        foreach (uint value in row) writer.Write(value);
        writer.Write(text); return stream.ToArray();
    }
}
