using Aha.Topo;
using Clipper2Lib;
using System.Text.Json;

namespace Aha.Topo.Checks;

internal static class RoadGeometryChecks
{
    public static int Run()
    {
        var projection = new LocalProjection(new GeoPoint(126.98, 37.56));
        var bounds = projection.Bounds(200);
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("road geometry: " + name);
            count++;
        }
        void Fails(Action action, string name)
        {
            bool failed = false;
            try { action(); } catch (ArgumentException) { failed = true; } catch (InvalidOperationException) { failed = true; }
            Check(failed, name);
        }
        List<GeoPoint> Ring(params (double X, double Y)[] points) => points.Select(p => projection.ToGeo(p.X, p.Y)).ToList();
        GeoFeature Parcel(string label, params (double X, double Y)[] outer) => new()
        {
            Id = label, ParcelLabel = label, LandCategoryCode = ParcelLandCategory.ParseLabel(label),
            Polygons = new() { new() { Ring(outer) } }
        };
        var road = Parcel("12도", (-50,-2), (50,-2), (50,2), (-50,2));
        var straight = RoadGeometry.BuildSource(new[] { road }, projection, bounds);
        Check(Math.Abs(straight.AreaSquareMetres - 400) < 1e-6, "source parcel outline determines road area without round caps or width inference");
        Check(!Contains(straight.Outlines,new(50.001,0)), "road does not extend beyond source parcel endpoints");
        var cross = RoadGeometry.BuildSource(new[] { road, Parcel("13도",(-2,-50),(2,-50),(2,50),(-2,50)) },projection,bounds);
        Check(Math.Abs(cross.AreaSquareMetres-784)<1e-6,"road parcels union their intersection once");
        var duplicate=RoadGeometry.BuildSource(new[]{road,road},projection,bounds);
        Check(duplicate.AreaSquareMetres==straight.AreaSquareMetres,"duplicate road parcels do not duplicate area");
        var empty=RoadGeometry.BuildSource(Array.Empty<GeoFeature>(),projection,bounds);
        Check(empty.AreaSquareMetres==0 && empty.Outlines.Count==0,"empty cadastre does not fabricate roads");
        var unknown=Parcel("12",(-50,-2),(50,-2),(50,2),(-50,2));
        Check(RoadGeometry.BuildSource(new[]{unknown},projection,bounds).AreaSquareMetres==0,"unknown category is not inferred from shape or PNU");
        var clipped=RoadGeometry.BuildSource(new[]{road},projection,projection.Bounds(25));
        Check(Math.Abs(clipped.AreaSquareMetres-200)<1e-6,"road clips to requested extent");
        Check(clipped.Outlines.SelectMany(p=>p).All(p=>Math.Abs(p.x)<=25.000001 && Math.Abs(p.y)<=25.000001),"road vertices remain in AOI");
        var holed=new GeoFeature {Id="hole",ParcelLabel="14도",LandCategoryCode=14,Polygons=new(){new(){
            Ring((-20,-20),(20,-20),(20,20),(-20,20)),Ring((-10,-10),(10,-10),(10,10),(-10,10))}}};
        var holeArea=RoadGeometry.BuildSource(new[]{holed},projection,bounds);
        Check(Math.Abs(holeArea.AreaSquareMetres-1200)<1e-6 && !Contains(holeArea.Outlines,new(0,0)),"GeoJSON hole is subtractive even with same winding as shell");
        Check(holeArea.Outlines.Count(path=>Clipper.Area(path)>0)==1 && holeArea.Outlines.Count(path=>Clipper.Area(path)<0)==1,"output shell is CCW and hole CW");
        var reversed=new GeoFeature{Id="reverse",LandCategoryCode=14,Polygons=holed.Polygons.Select(p=>p.Select(r=>r.AsEnumerable().Reverse().ToList()).ToList()).ToList()};
        Check(Math.Abs(RoadGeometry.BuildSource(new[]{reversed},projection,bounds).AreaSquareMetres-holeArea.AreaSquareMetres)<1e-6,"ring direction does not change classified road area");
        var island=Parcel("15도",(-5,-5),(5,-5),(5,5),(-5,5));
        var islandArea=RoadGeometry.BuildSource(new[]{holed,island},projection,bounds);
        Check(Math.Abs(islandArea.AreaSquareMetres-1300)<1e-6 && Contains(islandArea.Outlines,new(0,0)),"independent road island inside a hole survives union");
        var land=Parcel("20대",(-5,-10),(5,-10),(5,10),(-5,10));
        var conflict=RoadGeometry.BuildSource(new[]{road,land},projection,bounds);
        Check(Math.Abs(conflict.AreaSquareMetres-360)<1e-6 && !Contains(conflict.Outlines,new(0,0)),"non-road cadastral land takes precedence at source overlap");
        Check(Math.Abs(conflict.Diagnostics.ConflictAreaSquareMetres-40)<1e-6 && conflict.Diagnostics.RoadUnionAreaSquareMetres==400,"source overlap area is reported independently of final road area");
        var uncertain=new GeoFeature{Id="uncertain",Polygons=land.Polygons};
        var uncertainArea=RoadGeometry.BuildSource(new[]{road,uncertain},projection,bounds);
        Check(uncertainArea.AreaSquareMetres==conflict.AreaSquareMetres && uncertainArea.Diagnostics.UnknownCategoryCount==1,"unknown overlapping parcel stays land and remains visible in diagnostics");
        var bowtie=Parcel("30도",(0,0),(10,10),(0,10),(10,0));
        var fixedArea=RoadGeometry.BuildSource(new[]{bowtie},projection,bounds);
        Check(Math.Abs(fixedArea.AreaSquareMetres-50)<1e-6 && fixedArea.Diagnostics.RepairedParcelCount==1,"self-crossing source ring is noded into its two lobes without buffering");
        Check(Math.Abs(fixedArea.Diagnostics.RegularizationAreaChangeSquareMetres-50)<1e-6,"ambiguous algebraic versus regularized area is reported");
        var crossingHole=new GeoFeature {Id="crossing-hole",LandCategoryCode=14,Polygons=new(){new(){
            Ring((0,0),(10,0),(10,10),(0,10)),Ring((5,5),(15,5),(15,15),(5,15))}}};
        var cut=RoadGeometry.BuildSource(new[]{crossingHole},projection,bounds);
        Check(Math.Abs(cut.AreaSquareMetres-75)<1e-6 && cut.Diagnostics.RepairedParcelCount==1,"crossing hole is clipped out and never promoted to invented road land");
        var narrow=Parcel("40도",(-50,0),(50,0),(50,.0001),(-50,.0001));
        Check(Math.Abs(RoadGeometry.BuildSource(new[]{narrow},projection,bounds).AreaSquareMetres-.01)<1e-6,"real sub-millimetre strip is not deleted by width heuristics");
        var parallel=RoadGeometry.BuildSource(new[]{road,Parcel("41도",(-50,3),(50,3),(50,7),(-50,7))},projection,bounds);
        Check(!Contains(parallel.Outlines,new(0,2.5)),"independent parcels retain real gaps");
        var touching=RoadGeometry.BuildSource(new[]{Parcel("50도",(0,0),(10,0),(10,10),(0,10)),Parcel("51도",(10,0),(20,0),(20,10),(10,10))},projection,bounds);
        Check(touching.Outlines.Count==1 && Math.Abs(touching.AreaSquareMetres-200)<1e-6,"adjacent parcels dissolve shared edge");
        var before=JsonSerializer.Serialize(new[]{road,holed,land,bowtie});
        RoadGeometry.BuildSource(new[]{road,holed,land,bowtie},projection,bounds);
        Check(before==JsonSerializer.Serialize(new[]{road,holed,land,bowtie}),"preprocessing does not mutate source labels or coordinates");
        Fails(()=>RoadGeometry.BuildSource(new[]{Parcel("60도",(0,0),(1,0),(2,0))},projection,bounds),"collapsed source shell fails instead of inventing surface");
        Fails(()=>RoadGeometry.BuildSource(new[]{new GeoFeature{Id="bad",LandCategoryCode=14,Polygons=new(){new(){new(){new(200,0),new(201,0),new(201,1)}}}}},projection,bounds),"invalid source coordinate fails instead of discarding vertices");
        var coverage = projection.Bounds(80);
        var output = projection.Bounds(50);
        var boundaryRoad = Parcel("70도", (49.96,-40), (50.96,-40), (50.96,40), (49.96,40));
        var coverageRoad = RoadGeometry.Build(new[] { boundaryRoad }, projection, coverage);
        var outputRoad = RoadGeometry.Build(new[] { boundaryRoad }, projection, coverage, output);
        Check(Contains(coverageRoad.Outlines, new(49.98,0)) && !Contains(outputRoad.Outlines, new(49.98,0)),
            "output cut removes the 40mm road sliver even when the source road has a valid full-width core outside output");
        Check(Contains(outputRoad.Outlines, new(50.5,0)) && !Contains(outputRoad.Outlines, new(49.8,0)),
            "output boundary cleanup preserves acquired-only road context and cannot grow into adjacent land");
        Check(Math.Abs(outputRoad.Diagnostics.ModelCleanup!.SourceAreaSquareMetres-3.2)<1e-5 &&
            outputRoad.Diagnostics.ModelCleanup.ModelAreaSquareMetres==0,
            "model cleanup diagnostics describe the actual output domain");
        var field = TerrainField.FromSamples(coverage, projection, 8, 8, Enumerable.Repeat(20d,81).ToArray());
        var partition = PlanarPartition.Build(field, outputRoad.Outlines, new PathsD(), outputBounds:output);
        partition.Patches(8, true, false, out _);
        Check(partition.Regions.All(region=>!region.Road), "clipped terrain partition has no rejected narrow independent road");
        Fails(()=>RoadGeometry.Build(new[] { boundaryRoad },projection,output,coverage),
            "road output cannot exceed acquired calculation coverage");
        // Near is also an output/representation choice. When its boundary needs
        // no road correction, enabling it must not re-round shape or diagnostics.
        var obliqueRoads=Enumerable.Range(0,7).Select(index=>
        {
            double angle=.371+index*.617,cx=25*Math.Cos(index*2*Math.PI/7),cy=25*Math.Sin(index*2*Math.PI/7);
            (double X,double Y) Move(double x,double y)=>(cx+x*Math.Cos(angle)-y*Math.Sin(angle),
                cy+x*Math.Sin(angle)+y*Math.Cos(angle));
            return Parcel($"{80+index}도",Move(-7,-1),Move(7,-1),Move(7,1),Move(.015,1),
                Move(.015,5),Move(-.015,5),Move(-.015,1),Move(-7,1));
        }).ToArray();
        var nearTarget=Parcel("81대",(-3,-4),(3,-4),(3,4),(-3,4));
        var near=EditRegion.Create(nearTarget,projection,output,2);
        var plainModel=RoadGeometry.Build(obliqueRoads,projection,coverage,output);
        var nearModel=RoadGeometry.Build(obliqueRoads,projection,coverage,output,near);
        Check(nearModel.Diagnostics.ModelCleanup!.NearResolvedComponents==0,
            "separate oblique roads require no Near correction after source cleanup");
        Check(plainModel.AreaSquareMetres==nearModel.AreaSquareMetres &&
            Math.Abs(Clipper.Area(Clipper.Xor(plainModel.Outlines,nearModel.Outlines,FillRule.NonZero,6)))==0,
            "enabling a no-op Near partition preserves road geometry and its reported area");
        Check(plainModel.Diagnostics==nearModel.Diagnostics,
            "enabling a no-op Near partition preserves source and cleanup diagnostics without recalculation drift");
        return count;
    }

    private static bool Contains(PathsD paths,PointD point)
    {
        int winding=0;
        foreach(var path in paths)
        {
            var location=Clipper.PointInPolygon(point,path,RoadGeometry.ClipPrecision);
            if(location==PointInPolygonResult.IsOn)return true;
            if(location==PointInPolygonResult.IsInside)winding+=Clipper.Area(path)>0?1:-1;
        }
        return winding!=0;
    }
}
