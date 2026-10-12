using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class PlanarPartitionChecks
{
    internal static int Run()
    {
        int count = 0;
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var sw = projection.ToGeo(-20,-20); var ne = projection.ToGeo(20,20);
        var field = TerrainField.FromSamples(new(sw.Lon,sw.Lat,ne.Lon,ne.Lat), projection, 8,8,
            Enumerable.Repeat(100d,81).ToArray());
        var circle = new PathD(Enumerable.Range(0,128).Select(i => new PointD(
            10*Math.Cos(2*Math.PI*i/128),10*Math.Sin(2*Math.PI*i/128))));
        var near = new PathsD { circle };
        var footprint = new PathsD { Rectangle(-3,7,3,9.97) };
        var original = new PathsD(footprint.Select(path=>new PathD(path)));
        var partition = PlanarPartition.Build(field,new(),near,clip:Clipper.Difference(new PathsD {Rectangle(-20,-20,20,20)},footprint,FillRule.NonZero,6));
        var cap = PlanarPartition.Merge(partition.Regions.Where(r=>r.Near))
            .Where(p=>p.EnvelopeInternal.MinY>9.9e6).ToArray();
        Check(cap.Length==1 && cap[0].EnvelopeInternal.Height/1e6<=.030001
            && cap[0].Buffer(-49999).IsEmpty,
            "exact semantic intersection contains the small top cap used by the regression");
        var patches = partition.Patches(2,true,false,out _);
        Check(patches.SelectMany(p=>p.Regions).Count()==partition.Regions.Count
            && patches.SelectMany(p=>p.Regions).Distinct().Count()==partition.Regions.Count,
            "required trim remains covered once inside the connected terrain role");
        Check(Clipper.Area(Clipper.Xor(original,footprint,FillRule.NonZero,6))==0
            && footprint[0].SequenceEqual(original[0]), "patch support never moves building or source boundaries");
        Check(Math.Abs(partition.PlanAreaSquareMetres - (1600 - 6*2.97)) < 1e-8,
            "protected clipping preserves its exact area without changing source boundaries");

        foreach (var road in new[] { Rectangle(-5,-.02,5,.02), Rectangle(-.02,-.02,.02,.02) })
        {
            var thin = PlanarPartition.Build(field,new(){road},near);
            thin.Patches(2,true,false,out _);
            Check(thin.ReassignedRoleAreaSquareMetres > 0 && !thin.Regions.Any(region => region.Road),
                "a subminimum independent road is reassigned on the common graph without deleting coverage");
        }
        var completeNear = new PathsD{Rectangle(-20,-20,20,20)};
        var crossing = PlanarPartition.Build(field,new(){Rectangle(-.03,-8,8,8)},completeNear);
        var merged = crossing.Patches(2,true,true,out int mergedCount);
        Check(mergedCount>0 && merged.Any(p=>p.Blocks.Count>1),
            "incidental sample-block slivers still borrow neighbouring support");
        var outputSw = projection.ToGeo(-13, -11); var outputNe = projection.ToGeo(17, 14);
        var output = new GeoBounds(outputSw.Lon, outputSw.Lat, outputNe.Lon, outputNe.Lat);
        var clipped = PlanarPartition.Build(field, new() { Rectangle(-20,-2,20,2) }, near, output);
        Check(Math.Abs(clipped.Regions.Sum(region=>region.Polygon.Area)/1e12 - 750) < 1e-10 &&
            clipped.Regions.All(region=>region.Polygon.EnvelopeInternal.MinX>=-13e6 &&
                region.Polygon.EnvelopeInternal.MaxX<=17e6 && region.Polygon.EnvelopeInternal.MinY>=-11e6 &&
                region.Polygon.EnvelopeInternal.MaxY<=14e6),
            "acquired terrain larger than output still partitions exactly the requested rectangle");
        Check(Math.Abs(clipped.Regions.Where(region=>region.Road).Sum(region=>region.Polygon.Area)/1e12 - 120) < 1e-10,
            "roads crossing output are clipped once without exterior regions");
        Check(clipped.Regions.All(region=>
        {
            var sample = PlanarPartition.InteriorSample(region.Polygon);
            return field.ModelCell(sample.X/1e6,sample.Y/1e6)==(region.Column,region.Row) &&
                clipped.CellAt(sample.X/1e6,sample.Y/1e6)==(region.Column,region.Row);
        }), "clipped polygons retain original source grid indices and height support");
        bool outsideRejected=false;
        try { clipped.CellAt(18, 0); }
        catch(ArgumentOutOfRangeException) { outsideRejected=true; }
        Check(outsideRejected,"surface lookup rejects acquired-only coordinates outside the output rectangle");
        var aligned = PlanarPartition.Build(field, new(), new(), outputBounds: projection.Bounds(10));
        Check(aligned.CellCount == 16 && aligned.CellAt(10,10) == (5,5) && aligned.CellAt(-10,-10) == (2,2),
            "an output boundary on an interior source grid line belongs to the last emitted cell, not acquired-only cells");
        return count;

        void Check(bool valid,string name) { count++; if(!valid)throw new InvalidOperationException("Planar partition: "+name); }
    }
    private static PathD Rectangle(double x0,double y0,double x1,double y1) => new(){new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1)};
}
