using System.Numerics;
using System.Text.Json.Nodes;
using MangosSuperUI.Services.WorldPacks;
using Xunit;

namespace MangosSuperUI.Tests;

public class WorldPackCoastTests
{
    private static string? ClientRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.GetFullPath(Path.Combine(dir.FullName, "..", "MSUIClient"));
            if (File.Exists(Path.Combine(candidate, "GameData", "Data", "terrain.MPQ"))) return candidate;
        }
        return null;
    }

    private static WorldPackCoast Square(float? north = null) => new(
        new(0, 7001, "Test", [new(-1000,1000), new(-1000,2200), new(-2400,2200), new(-2400,1000)]),
        [(28,34)], 0, 515, 180, 6, north, 80);

    private static List<DocRow> GilneasDocs(string root, params (int col, int row)[] tiles)
    {
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tools", "worldpack", "examples", "gilneas", "worldmap-outline.json")))!.AsObject();
        var tileArray = new JsonArray();
        foreach (var (col,row) in tiles) tileArray.Add(new JsonArray(col,row));
        body["terrain"] = new JsonObject { ["tiles"] = tileArray, ["seaLevel"] = 0, ["seaDepth"] = 515,
            ["coastWidth"] = 180, ["minimumLand"] = 6, ["joinNorth"] = WorldCoords.VertexWorldX(34,0), ["joinWidth"] = 80 };
        return [new() { PackId = 1, Kind = "worldmap", DocKey = "0:7001", Body = body.ToJsonString() }];
    }

    private static AdtDocument Stock(VanillaArchiveSet stock, int col, int row) =>
        AdtDocument.Parse(stock.ReadFile(WorldCoords.AdtPath("Azeroth",col,row)) ?? throw new InvalidOperationException($"missing stock {col},{row}"), col,row);

    private static AdtDocument Relocated(VanillaArchiveSet stock, int sourceCol, int sourceRow, int col, int row)
    {
        var adt = Stock(stock, sourceCol, sourceRow);
        adt.Relocate(col,row,keepObjects:true,uid => uid);
        return adt;
    }

    [Fact]
    public void Height_KeepsInteriorDryAndReturnsExteriorToDeclaredSeaFloor()
    {
        var coast = Square();
        Assert.Equal(6, coast.Height(new(-1600,1600),-515));
        Assert.Equal(80, coast.Height(new(-1600,1600),80));
        Assert.InRange(coast.Height(new(-1600,2199),80), .4f,.6f);
        Assert.Equal(240,coast.Height(new(-1600,2000),240)); // inland relief is unchanged past the coast band
        Assert.InRange(coast.Height(new(-1600,2201),80),-.06f,0);
        Assert.Equal(-515,coast.Height(new(-1600,2500),80));
        var raisedSea = coast with { SeaLevel = 12 };
        Assert.Equal(18,raisedSea.Height(new(-1600,1600),-515));
        Assert.Equal(-503,raisedSea.Height(new(-1600,2500),80));
    }

    [Fact]
    public void NorthJoin_PreservesSeamAndDoesNotTreatLandBorderAsBeach()
    {
        var coast = Square(-1000);
        Assert.Equal(37,coast.Height(new(-1000,1600),37));
        Assert.Equal(37,coast.Height(new(-980,1600),37));
        Assert.InRange(coast.CoastDistance(new(-1001,1600)),599.99f,600.01f);
        Assert.InRange(coast.Height(new(-1040,1600),-515),-254.501f,-254.499f);
        Assert.Equal(6,coast.Height(new(-1080,1600),-515));
        Assert.True(coast.ClearsProp(WorldCoords.WorldToPlacement(new(-1020,1600,20))));
        Assert.True(coast.ClearsBuilding(WorldCoords.WorldToPlacement(new(-1020,1600,20))));
        Assert.False(coast.ClearsProp(WorldCoords.WorldToPlacement(new(-1020,2600,20)),identity:true));
    }

    [Fact]
    public void CoastClearance_RemovesTreesNearBeachButPreservesBuildingsFartherInland()
    {
        var coast = Square();
        var village = WorldCoords.WorldToPlacement(new(-1700,2050,25));
        Assert.True(coast.ClearsProp(village));
        Assert.False(coast.ClearsBuilding(village));
        Assert.True(coast.ClearsBuilding(WorldCoords.WorldToPlacement(new(-1700,2190,25))));
        Assert.True(coast.ClearsBuilding(WorldCoords.WorldToPlacement(new(-1700,2300,25))));
    }

    [Fact]
    public void RealOutline_AdjacentRelocatedTilesKeepSharedHeightsAfterSerialization()
    {
        string? root = ClientRoot(); if (root is null) return;
        using var stock = new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var docs = GilneasDocs(root,(28,35),(29,35),(28,36),(29,36));
        var built = new Dictionary<(int map,int col,int row),AdtDocument>();
        for(int col=28;col<=29;col++) for(int row=35;row<=36;row++)
            built[(0,col,row)] = Relocated(stock,col,row-6,col,row);
        WorldPackCoast.Apply(docs,stock,new() { [0]="Azeroth" },built,[],_=>{});
        foreach(var key in built.Keys.ToArray()) built[key] = AdtDocument.Parse(built[key].Write(),key.col,key.row);
        float worst=0;
        for(int k=0;k<=128;k++)
        {
            for(int row=35;row<=36;row++) worst=Math.Max(worst,Math.Abs(built[(0,28,row)].OuterHeight(k,128)-built[(0,29,row)].OuterHeight(k,0)));
            for(int col=28;col<=29;col++) worst=Math.Max(worst,Math.Abs(built[(0,col,35)].OuterHeight(128,k)-built[(0,col,36)].OuterHeight(0,k)));
        }
        Assert.InRange(worst,0,.01f);
        var bytes=built.ToDictionary(kv=>WorldCoords.AdtPath("Azeroth",kv.Key.col,kv.Key.row),kv=>kv.Value.Write());
        var input = new WorldPackAudit.AuditInput { Stock=stock.ReadFile, Built=p=>bytes.GetValueOrDefault(p),
            MapDirs=new() { [0]="Azeroth" }, Docs=docs, Placements=[] };
        Assert.DoesNotContain(WorldPackCoast.Verify(input),f=>f.Severity=="error");
        var coast=Assert.Single(WorldPackCoast.Read(docs));
        int outside=0,inside=0;
        foreach(var (key,adt) in built)
        {
            var original=Stock(stock,key.col,key.row);
            for(int i=0;i<256;i++)
            {
                var (ix,iy,ox,oy,area)=adt.ChunkInfo(i);
                var p=new Vector2(ox-WorldCoords.Chunk/2,oy-WorldCoords.Chunk/2);
                if(!coast.Region.Contains(p))
                {
                    Assert.Equal(original.ChunkInfo(original.ChunkIndex(ix,iy)).area,area);outside++;
                }
                else if(coast.CoastDistance(p)>10)
                {
                    float ground=adt.OuterHeight(iy*8+4,ix*8+4);
                    if(adt.LiquidLevel(i)is float water)Assert.True(water<=ground,$"interior water {water} above ground {ground} at {p}");
                    inside++;
                }
            }
        }
        Assert.True(outside>0&&inside>0,"the real coastline fixture must exercise both sides");
    }

    [Fact]
    public void RealOutline_NorthTileEdgeRemainsExactlyItsPreCoastHeight()
    {
        string? root = ClientRoot(); if (root is null) return;
        using var stock = new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var doc = Relocated(stock,28,29,28,34);
        var before = Enumerable.Range(0,129).Select(c=>doc.OuterHeight(0,c)).ToArray();
        var built = new Dictionary<(int map,int col,int row),AdtDocument> { [(0,28,34)]=doc };
        WorldPackCoast.Apply(GilneasDocs(root,(28,34)),stock,new() { [0]="Azeroth" },built,[],_=>{});
        var re=AdtDocument.Parse(doc.Write(),28,34);
        for(int c=0;c<=128;c++) Assert.Equal(before[c],re.OuterHeight(0,c));
    }

    [Fact]
    public void ForceLiquid_ReplacesSourceLakeWithDestinationSeaEvenUnderRaisedLand()
    {
        string? root = ClientRoot(); if (root is null) return;
        using var stock = new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var destination=Stock(stock,28,35);
        var source=Stock(stock,30,29);
        int sourceLiquids=Enumerable.Range(0,256).Count(i=>source.LiquidLevel(i) is float z && Math.Abs(z)>1);
        Assert.True(sourceLiquids>0,"the source fixture must include a non-sea-level lake");
        source.Relocate(28,35,true,uid=>uid);
        var h=source.OuterHeights();
        source.ApplySculpt(Enumerable.Range(0,h.Length).ToDictionary(i=>i,i=>100-h[i]));
        Assert.True(source.CarryLiquidFrom(destination,replaceExisting:true)>0);
        var re=AdtDocument.Parse(source.Write(),28,35);
        int checkedChunks=0;
        for(int i=0;i<256;i++)
        {
            var (ix,iy,_,_,_)=re.ChunkInfo(i);
            float? expected=destination.LiquidLevel(destination.ChunkIndex(ix,iy));
            if(expected is null)continue;
            Assert.Equal(expected,re.LiquidLevel(i)); checkedChunks++;
        }
        Assert.Equal(256,checkedChunks);
    }

    [Fact]
    public void RealOutline_IdentityApronOutsideCoastBandPreservesStockTileByteForByte()
    {
        string? root = ClientRoot(); if (root is null) return;
        using var stock = new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        byte[] original=stock.ReadFile(WorldCoords.AdtPath("Azeroth",31,34))!;
        var adt=AdtDocument.Parse(original,31,34);
        Assert.True(adt.OuterHeights().Any(h=>h>50),"fixture must include existing Hillsbrad land");
        var docs=GilneasDocs(root,(31,34));
        docs.Add(new() { PackId=1,Kind="tile",DocKey="0:31:34",Body="""
            {"map":0,"col":31,"row":34,"sourceMap":"Azeroth","sourceCol":31,"sourceRow":34,"keepDoodads":true,"keepWmos":true,"stitch":0}
            """ });
        var built=new Dictionary<(int map,int col,int row),AdtDocument> { [(0,31,34)]=adt };
        WorldPackCoast.Apply(docs,stock,new() { [0]="Azeroth" },built,[],_=>{});
        Assert.Equal(original,adt.Write());
    }

    [Fact]
    public void RealOutline_OffshoreStampReturnsToVariableStockSeabedAndUntouchedNeighbourEdges()
    {
        string? root = ClientRoot(); if (root is null) return;
        using var stock = new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var baseline=Stock(stock,31,35);
        Assert.True(baseline.OuterHeights().Max()-baseline.OuterHeights().Min()>200,
            "fixture must have a varying sea floor, not a flat -515 plane");
        var adt=Relocated(stock,30,29,31,35);
        var docs=GilneasDocs(root,(31,35));
        docs.Add(new() { PackId=1,Kind="tile",DocKey="0:31:35",Body="""
            {"map":0,"col":31,"row":35,"sourceMap":"Azeroth","sourceCol":30,"sourceRow":29,"keepDoodads":true,"keepWmos":true,"stitch":120}
            """ });
        var built=new Dictionary<(int map,int col,int row),AdtDocument> { [(0,31,35)]=adt };
        WorldPackCoast.Apply(docs,stock,new() { [0]="Azeroth" },built,[],_=>{});
        var re=AdtDocument.Parse(adt.Write(),31,35);
        var east=Stock(stock,32,35);var south=Stock(stock,31,36);
        for(int r=0;r<=128;r++)for(int c=0;c<=128;c++)
            Assert.InRange(Math.Abs(re.OuterHeight(r,c)-baseline.OuterHeight(r,c)),0,.001f);
        for(int r=0;r<128;r++)for(int c=0;c<128;c++)
            Assert.InRange(Math.Abs(re.InnerHeight(r,c)-baseline.InnerHeight(r,c)),0,.001f);
        for(int i=0;i<256;i++)
        {
            var (ix,iy,_,_,_)=re.ChunkInfo(i);
            Assert.Equal(baseline.NormalsOf(baseline.ChunkIndex(ix,iy)),re.NormalsOf(i));
        }
        for(int k=0;k<=128;k++)
        {
            Assert.InRange(Math.Abs(re.OuterHeight(k,128)-east.OuterHeight(k,0)),0,.01f);
            Assert.InRange(Math.Abs(re.OuterHeight(128,k)-south.OuterHeight(0,k)),0,.01f);
        }
    }

    [Fact]
    public void BaselineSurfaceRestore_KeepsSculptedNormalsAndRestoresOnlyUnchangedSurface()
    {
        string? root=ClientRoot();if(root is null)return;
        using var stock=new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var baseline=Stock(stock,31,34);var adt=Stock(stock,31,34);
        adt.ApplySculpt(new Dictionary<int,float> { [64*129+64]=30 });
        // Simulate the baked-normal drift that a fitted stamp leaves away from its changed terrain.
        for(int i=0;i<256;i++)adt.MarkNormalsDirty(i);
        adt.RecomputeDirtyNormals();
        int changedChunk=adt.ChunkIndex(8,8),farChunk=adt.ChunkIndex(0,0);
        var sculpted=adt.NormalsOf(changedChunk);
        Assert.False(adt.NormalsOf(farChunk).SequenceEqual(baseline.NormalsOf(farChunk)),
            "fixture must have baked normals distinct from recomputed normals");
        float inner=adt.InnerHeight(64,64);
        adt.RestoreUnchangedSurfaceFrom(baseline);
        var re=AdtDocument.Parse(adt.Write(),31,34);
        Assert.Equal(baseline.OuterHeight(64,64)+30,re.OuterHeight(64,64));
        Assert.Equal(inner,re.InnerHeight(64,64));
        Assert.Equal(baseline.NormalsOf(farChunk),re.NormalsOf(farChunk));
        var normals=re.NormalsOf(changedChunk);
        // Outer vertex (64,65) slopes towards the moved vertex, and its cell center changed too.
        foreach(int vertex in new[] { 1,9 })
            Assert.Equal(sculpted.AsSpan(vertex*3,3).ToArray(),normals.AsSpan(vertex*3,3).ToArray());
        Assert.False(normals.AsSpan(3,3).SequenceEqual(baseline.NormalsOf(changedChunk).AsSpan(3,3)));
    }

    [Fact]
    public void RealOutline_RemovedNorthernBuildingsLeaveNoExposedTerrainHoles()
    {
        string? root=ClientRoot();if(root is null)return;
        using var stock=new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var docs=GilneasDocs(root,(29,34));
        var coast=Assert.Single(WorldPackCoast.Read(docs));
        var adt=Relocated(stock,30,30,29,34);
        var northHoles=Enumerable.Range(0,256).Where(i=>
        {
            var (_,_,ox,oy,_)=adt.ChunkInfo(i);
            var p=new Vector2(ox-WorldCoords.Chunk/2,oy-WorldCoords.Chunk/2);
            return adt.HolesOf(i)!=0 && p.X>=coast.JoinNorth!.Value-150 &&
                coast.Region.Contains(p) && coast.CoastDistance(p)>coast.CoastWidth;
        }).ToArray();
        Assert.NotEmpty(northHoles);
        var built=new Dictionary<(int map,int col,int row),AdtDocument> { [(0,29,34)]=adt };
        WorldPackCoast.Apply(docs,stock,new() { [0]="Azeroth" },built,[],_=>{});
        var re=AdtDocument.Parse(adt.Write(),29,34);
        foreach(int i in northHoles)Assert.Equal((ushort)0,re.HolesOf(i));
    }

    [Fact]
    public void RealOutline_RemovesImportedExteriorLakeButPreservesInteriorPondAndOcean()
    {
        string? root=ClientRoot();if(root is null)return;
        using var stock=new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var outside=Relocated(stock,31,30,30,34);
        var inside=Relocated(stock,29,30,28,34);
        var pond=Assert.Single(inside.LiquidCells().Where(c=>c.Row==31&&c.Col==127));
        Assert.InRange(pond.H00,32.93f,32.94f);
        Assert.Equal((byte)0x44,pond.Flags);
        Assert.Contains(outside.LiquidCells(),c=>c.Row==41&&c.Col==127);
        Assert.DoesNotContain(Stock(stock,30,34).LiquidCells(),c=>c.Row==41&&c.Col==127);
        Assert.DoesNotContain(Stock(stock,28,34).LiquidCells(),c=>c.Row==31&&c.Col==127);
        var docs=GilneasDocs(root,(30,34),(28,34));
        var built=new Dictionary<(int map,int col,int row),AdtDocument> { [(0,30,34)]=outside,[(0,28,34)]=inside };
        WorldPackCoast.Apply(docs,stock,new() { [0]="Azeroth" },built,[],_=>{});
        outside=AdtDocument.Parse(outside.Write(),30,34);inside=AdtDocument.Parse(inside.Write(),28,34);
        Assert.DoesNotContain(outside.LiquidCells(),c=>c.Row==41&&c.Col==127);
        Assert.Equal(pond,Assert.Single(inside.LiquidCells().Where(c=>c.Row==31&&c.Col==127)));
        var coast=Assert.Single(WorldPackCoast.Read(docs));
        var stockOcean=Stock(stock,30,34).LiquidCells().Where(c=>
            !coast.Region.Contains(new(WorldCoords.VertexWorldX(34,c.Row)-WorldCoords.Unit/2,
                WorldCoords.VertexWorldY(30,c.Col)-WorldCoords.Unit/2))).ToArray();
        Assert.NotEmpty(stockOcean);
        var outsideCells=outside.LiquidCells().ToHashSet();
        foreach(var cell in stockOcean)Assert.Contains(cell,outsideCells);
        var bytes=new Dictionary<string,byte[]> { [WorldCoords.AdtPath("Azeroth",30,34)]=outside.Write(),
            [WorldCoords.AdtPath("Azeroth",28,34)]=inside.Write() };
        var input=new WorldPackAudit.AuditInput { Stock=stock.ReadFile,Built=p=>bytes.GetValueOrDefault(p),
            MapDirs=new(){[0]="Azeroth"},Docs=docs,Placements=[] };
        Assert.DoesNotContain(WorldPackCoast.Verify(input),f=>f.Severity=="error");
        // Reintroduce source liquid over unchanged baseline terrain: the audit must catch it.
        var bad=Stock(stock,30,34);bad.CarryLiquidFrom(Stock(stock,31,30),replaceExisting:true);
        bytes[WorldCoords.AdtPath("Azeroth",30,34)]=bad.Write();
        Assert.Contains(WorldPackCoast.Verify(input),f=>f.Severity=="error"&&f.Message.Contains("exterior liquid cells"));
    }

    [Fact]
    public void LiquidCellRemoval_PreservesOtherCellsAndDropsEmptyPayloadAfterSerialization()
    {
        string? root=ClientRoot();if(root is null)return;
        using var stock=new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var adt=Stock(stock,31,30);var before=adt.LiquidCells().ToArray();
        var mixed=before.GroupBy(c=>(c.Row/8,c.Col/8)).First(g=>g.Count()>1).ToArray();
        var target=mixed[0];
        adt.RemoveLiquidCells((r,c)=>r==target.Row&&c==target.Col);
        var re=AdtDocument.Parse(adt.Write(),31,30);
        Assert.Equal(before.Where(c=>c.Row!=target.Row||c.Col!=target.Col).ToArray(),re.LiquidCells().ToArray());
        Assert.Contains(mixed[1],re.LiquidCells());
        re.RemoveLiquidCells((_,_)=>true);
        var bytes=re.Write();var empty=AdtDocument.Parse(bytes,31,30);
        Assert.Empty(empty.LiquidCells());
        foreach(var chunk in before.Select(c=>(iy:c.Row/8,ix:c.Col/8)).Distinct())
            Assert.Null(empty.LiquidLevel(empty.ChunkIndex(chunk.ix,chunk.iy)));
        Assert.Equal(bytes,empty.Write());
    }

    [Fact]
    public void Minimap_SeesRemovedWaterCellsInsideAnOtherwiseWetChunk()
    {
        string? root=ClientRoot();if(root is null)return;
        using var stock=new VanillaArchiveSet(Path.Combine(root,"GameData","Data"));
        var adt=Stock(stock,31,30);
        var mixed=adt.LiquidCells().Where(c=>c.H00>1).GroupBy(c=>(c.Row/8,c.Col/8)).First(g=>g.Count()>1).ToArray();
        var target=mixed[0];var retained=mixed[1];
        var h=adt.OuterHeights();adt.ApplySculpt(Enumerable.Range(0,h.Length).ToDictionary(i=>i,i=>-10-h[i]));
        var before=WorldPackMinimap.Sample(adt,_=>new Vector3(100,100,100));
        adt.RemoveLiquidCells((r,c)=>r==target.Row&&c==target.Col);
        var re=AdtDocument.Parse(adt.Write(),31,30);
        Assert.NotNull(re.LiquidLevel(re.ChunkIndex(target.Col/8,target.Row/8)));
        var after=WorldPackMinimap.Sample(re,_=>new Vector3(100,100,100));
        var changed=WorldPackMinimap.Changed(before,after);
        for(int dy=0;dy<2;dy++)for(int dx=0;dx<2;dx++)
        {
            int pixel=(target.Row*2+dy)*256+target.Col*2+dx;
            Assert.True(before.Wet(pixel));Assert.True(float.IsNaN(after.Water[pixel]));Assert.True(changed[pixel]);
        }
        int other=(retained.Row*2)*256+retained.Col*2;
        Assert.Equal(before.Water[other],after.Water[other]);Assert.True(after.Wet(other));
        Assert.Equal(4,changed.Count(c=>c)); // only the removed liquid cell, without a terrain change
    }
}
