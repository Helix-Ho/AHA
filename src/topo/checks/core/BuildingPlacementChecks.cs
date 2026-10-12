using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class BuildingPlacementChecks
{
    internal static int Run()
    {
        int count = 0;
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        // Fixed seven-vertex footprint crossing a 64 x 53 DEM grid. Rounding each
        // clipped intersection previously lost 4.74046e-6 m² and rejected the whole model.
        var source = new PathD { new(-24.416043,-4.769943), new(-23.516673,-11.800037),
            new(-10.776580,-9.801169), new(-11.015702,-.511243), new(-16.345678,-.330686),
            new(-23.895705,-1.489999), new(-23.526033,-4.650038) };
        foreach (var shift in new[] { (X:0d,Y:0d), (X:137.123456,Y:-98.765432), (X:10000d,Y:-20000d) })
        foreach (bool rotate in new[] { false, true })
        {
            var path = new PathD(source.Select(point => new PointD(
                (rotate ? -point.y : point.x) + shift.X, (rotate ? point.x : point.y) + shift.Y)));
            var bounds = Bounds(shift.X - 400, shift.Y - 400, shift.X + 400, shift.Y + 400);
            var flat = Field(bounds, 64, 53, (_, _) => 17);
            var flatResult = BuildingPlacement.Statistics(flat, new() { path }, new(), 0);
            Assert(Math.Abs(flatResult.Minimum - 17) < 1e-10 && Math.Abs(flatResult.Mean - 17) < 1e-10,
                "fractional cell intersection preserves the complete constant-height footprint");
            double Plane(double x, double y) => 100 + .13 * (x - shift.X) + .21 * (y - shift.Y);
            var slope = Field(bounds, 64, 53, Plane);
            var slopedResult = BuildingPlacement.Statistics(slope, new() { path }, new(), 0);
            var centroid = Centroid(path);
            Assert(Math.Abs(slopedResult.Minimum - path.Min(point => Plane(point.x, point.y))) < 1e-8
                && Math.Abs(slopedResult.Mean - Plane(centroid.x, centroid.y)) < 1e-8,
                "linear field mean equals the independent polygon centroid height after rotation/translation");
        }
        {
            var bounds = Bounds(-100.123456, -100.654321, 100.876543, 100.345678);
            var flat = Field(bounds, 7, 9, (_, _) => 100);
            var footprint = new PathsD { Rectangle(-20,-15,20,15) };
            var roads = new PathsD { Rectangle(3.14159,-70,8.76543,70) };
            var result = BuildingPlacement.Statistics(flat, footprint, roads, .2);
            double roadFraction = (8.76543 - 3.14159) / 40;
            Assert(Math.Abs(result.Minimum - 99.8) < 1e-10 && Math.Abs(result.Mean - (100 - .2 * roadFraction)) < 1e-10,
                "road drop uses exact clipped area without repeated grid rounding");
            var hole = Rectangle(-10,-10,-5,10); hole.Reverse(); footprint.Add(hole);
            result = BuildingPlacement.Statistics(flat, footprint, roads, .2);
            double expectedArea = 40 * 30 - 5 * 20;
            Assert(Math.Abs(result.Minimum - 99.8) < 1e-10
                && Math.Abs(result.Mean - (100 - .2 * ((8.76543 - 3.14159) * 30 / expectedArea))) < 1e-10,
                "courtyard area is excluded from the denominator and remains outside the road integral");
            var noDrop = BuildingPlacement.Statistics(flat, footprint, roads, 0);
            Assert(Math.Abs(noDrop.Minimum - 100) < 1e-10 && Math.Abs(noDrop.Mean - 100) < 1e-10,
                "disabled drop keeps the complete land statistic");
        }
        {
            // Sparse contexts retain exact road rings, including holes, while
            // height integration visits only components that can meet a building.
            var bounds = Bounds(-600,-600,600,600);
            var field = Field(bounds,13,17,(_,_)=>23);
            GeoFeature Feature(string id,params PathD[] rings)=>new() {Id=id,
                Polygons=new(){rings.Select(ring=>ring.Select(p=>projection.ToGeo(p.x,p.y)).ToList()).ToList()}};
            var sourceRoads=new PathsD{Rectangle(-30,-30,30,30)};
            var roadHole=Rectangle(-8,-8,8,8);roadHole.Reverse();sourceRoads.Add(roadHole);
            sourceRoads.Add(Rectangle(6,0,7,1));
            sourceRoads.Add(Rectangle(-4,-2,-2,2)); // Lies wholly inside the building courtyard.
            for(int i=0;i<100;i++)sourceRoads.Add(Rectangle(200+i%10*12,200+i/10*12,204+i%10*12,204+i/10*12));
            var roads=RoadGeometry.Build(Array.Empty<GeoFeature>(),projection,bounds) with
                {Outlines=Clipper.BooleanOp(ClipType.Union,sourceRoads,null,FillRule.NonZero,EditRegion.ClipPrecision)};
            var candidates=new[]{Feature("courtyard",Rectangle(-20,-20,20,20),Rectangle(-5,-5,5,5)),
                Feature("road-hole",Rectangle(-7,0,-6,1)),Feature("remote-road",Rectangle(201,201,202,202))};
            foreach(var mode in new[]{(Separate:true,Drop:.2),(Separate:true,Drop:0d),(Separate:false,Drop:.2)})
            {
                var batch=BuildingPlacement.Prepare(field,roads,mode.Drop,mode.Separate,candidates);
                Assert(batch.Rejections.Count==0&&batch.Placements.Count==3,"sparse road context retains all valid buildings");
                foreach(var item in batch.Placements)
                {
                    double drop=mode.Separate?mode.Drop:0;
                    var unfiltered=BuildingPlacement.Statistics(field,item.Footprint,roads.Outlines,drop);
                    Assert(Math.Abs(item.MinimumElevationMetres-unfiltered.Minimum)<1e-10
                        &&Math.Abs(item.MeanElevationMetres-unfiltered.Mean)<1e-10,
                        "indexed road candidates preserve the unfiltered whole-footprint minimum and mean");
                    double fraction=item.SourceFeatureId switch {"courtyard"=>1345d/1500,"remote-road"=>1,_=>0};
                    Assert(Math.Abs(item.MeanElevationMetres-(23-drop*fraction))<1e-10
                        &&Math.Abs(item.MinimumElevationMetres-(23-(fraction>0?drop:0)))<1e-10,
                        "road holes, building courtyards and remote components preserve independent clipped-area statistics");
                }
            }
        }
        {
            // A raw grid break and its model knot differ by less than 0.5µm.
            // The floor must meet the very same V-shaped surface minimum as
            // the output terrain, rather than a distinct raw-grid interpolation.
            var bounds = Bounds(-20.123456, -20.123456, 20.654321, 20.654321);
            var layout = Field(bounds, 13, 17, (_, _) => 0);
            double valley = layout.ModelY(7);
            double Height(double x, double y) => 80 + .08 * x + Math.Abs(y - valley);
            var field = Field(bounds, 13, 17, Height);
            var originalHeights = field.Heights.ToArray();
            var footprint = new PathsD { new PathD { new(-7,-4),new(13,0),new(5,8) } };
            var result = BuildingPlacement.Statistics(field, footprint, new(), 0);
            double minimumX = valley - 3;
            // The lower triangle has area fraction (valley+4)^2/48 and
            // centroid Y=(-4+2*valley)/3. This directly integrates |Y-valley|.
            double expectedMean = 80 + .08 * (11.0 / 3) + 4.0 / 3 - valley + Math.Pow(valley + 4, 3) / 72;
            Assert(valley > -4 && valley < 0 && Math.Abs(result.Minimum - Height(minimumX, valley)) < 1e-10,
                "fractional grid valley gives the exact footprint minimum at the model knot");
            Assert(Math.Abs(result.Mean - expectedMean) < 1e-10,
                "nonuniform rounded cells preserve the independent V-field area mean");
            Assert(Math.Abs(field.SampleModelLocal(minimumX, valley) - result.Minimum) < 1e-10 &&
                Math.Abs(field.SampleLocal(minimumX, valley) - result.Minimum) > 1e-9,
                "floor uses model interpolation rather than the distinct raw grid interpolation");
            Assert(field.Heights.SequenceEqual(originalHeights), "model interpolation does not modify source node elevations");
            var partition = PlanarPartition.Build(field, new(), new());
            Assert(partition.Regions.All(region =>
                region.Polygon.EnvelopeInternal.MinX == Math.Round(field.ModelX(region.Column) * 1e6) &&
                region.Polygon.EnvelopeInternal.MaxX == Math.Round(field.ModelX(region.Column + 1) * 1e6) &&
                region.Polygon.EnvelopeInternal.MinY == Math.Round(field.ModelY(region.Row) * 1e6) &&
                region.Polygon.EnvelopeInternal.MaxY == Math.Round(field.ModelY(region.Row + 1) * 1e6)),
                "planar graph and floor statistics use the same model cell boundaries");
        }
        {
            var bounds = Bounds(-50,-50,50,50);
            var field = Field(bounds,10,10,(x,y) => 100 + .2*x + Math.Abs(y));
            GeoFeature Feature(string id, params PathD[] rings) => new() { Id = id,
                Polygons = new() { rings.Select(ring => ring.Select(p => projection.ToGeo(p.x,p.y)).ToList()).ToList() } };
            var a = Feature("a", Rectangle(-10,-5,0,5));
            var b = Feature("b", Rectangle(-1,-5,9,5));
            var c = Feature("c", Rectangle(8,-5,18,5));
            var touch = Feature("touch", Rectangle(18,-5,28,5));
            var roads = RoadGeometry.Build(Array.Empty<GeoFeature>(),projection,bounds);
            var batch = BuildingPlacement.Prepare(field,roads,0,true,new[] { a,b,c,touch });
            Assert(batch.Rejections.Count == 0 && batch.Placements.Count == 4,
                "positive-area overlap and edge contact retain every valid building");
            var first = batch.Placements.Single(p => p.SourceFeatureId == "a");
            Assert(Math.Abs(first.MinimumElevationMetres - 98) < 1e-7 &&
                Math.Abs(first.BaseElevationMetres - 97) < 1e-7 &&
                Math.Abs(first.MeanElevationMetres - 101.5) < 1e-7,
                "all-footprint ground minimum determines the one-metre foundation; the mean independently determines the body datum");
            var shared = BuildingPlacement.ShareLowestFloors(batch.Placements);
            Assert(shared.Take(3).All(p => Math.Abs(p.BaseElevationMetres - 97) < 1e-7) &&
                Math.Abs(shared[3].BaseElevationMetres - batch.Placements[3].BaseElevationMetres) < 1e-12,
                "transitive positive-area overlap shares the lowest floor but edge contact does not");
            Assert(shared.Select(p => p.MeanElevationMetres).SequenceEqual(batch.Placements.Select(p => p.MeanElevationMetres)),
                "sharing foundations never changes individual body and roof reference elevations");
            var nearOnly = BuildingPlacement.ShareLowestFloors(batch.Placements, p => p.SourceFeatureId != "a");
            Assert(Math.Abs(nearOnly[1].BaseElevationMetres - batch.Placements[1].BaseElevationMetres) < 1e-12 &&
                Math.Abs(nearOnly[2].BaseElevationMetres - nearOnly[1].BaseElevationMetres) < 1e-12,
                "excluded Far buildings do not lower a Near overlap group");
            var courtyard = Feature("court", Rectangle(-10,-10,10,10), Rectangle(-.02,-.02,.02,.02));
            var court = BuildingPlacement.Prepare(field,roads,0,true,new[] {courtyard}).Placements.Single();
            Assert(court.Footprint.Count == 2 && court.Feature.Polygons.Single().Count == 2 &&
                Math.Abs(Clipper.Area(court.Footprint) - (400-.0016)) < 1e-5,
                "a small source courtyard is retained rather than filled as a disposable model feature");
            var reversed = BuildingPlacement.ShareLowestFloors(batch.Placements.Reverse().ToArray());
            Assert(shared.All(p => reversed.Single(r => r.SourceFeatureId == p.SourceFeatureId).BaseElevationMetres == p.BaseElevationMetres),
                "overlap floors do not depend on source order");
        }
        {
            var inputs = new[] {
                (Id:"a",Footprint:ContourPaths.Rectangle(0,0,1,1)),
                (Id:"overlap",Footprint:ContourPaths.Rectangle(.999999,0,2,1)),
                (Id:"edge",Footprint:ContourPaths.Rectangle(2,0,3,1)),
                (Id:"point",Footprint:ContourPaths.Rectangle(3,1,4,2)),
                (Id:"court",Footprint:ContourPaths.Difference(ContourPaths.Rectangle(5,0,9,4),ContourPaths.Rectangle(6,1,8,3))),
                (Id:"inside-hole",Footprint:ContourPaths.Rectangle(6.5,1.5,7.5,2.5)) };
            foreach (var order in new[] { inputs, inputs.Reverse().ToArray() })
            {
                var groups=BuildingOverlapGroups.Create(order);
                Assert(groups.Count==5 && groups.Single(g=>g.Contains("a")).Order().SequenceEqual(new[]{"a","overlap"}),
                    "one-micrometre positive overlap joins floors; point, edge and courtyard contacts do not, in either input order");
            }
        }
        return count;

        void Assert(bool condition, string name) { count++; if (!condition) throw new InvalidOperationException("Building placement: " + name); }
        GeoBounds Bounds(double x0, double y0, double x1, double y1)
        { var sw = projection.ToGeo(x0,y0); var ne = projection.ToGeo(x1,y1); return new(sw.Lon,sw.Lat,ne.Lon,ne.Lat); }
        TerrainField Field(GeoBounds bounds, int columns, int rows, Func<double,double,double> sample)
        {
            var sw = projection.ToLocal(new(bounds.West,bounds.South)); var ne = projection.ToLocal(new(bounds.East,bounds.North));
            var heights = new double[(columns + 1) * (rows + 1)];
            for(int row=0;row<=rows;row++) for(int col=0;col<=columns;col++)
                heights[row*(columns+1)+col] = sample(Math.Round(sw.X+(ne.X-sw.X)*col/columns, 6),
                    Math.Round(sw.Y+(ne.Y-sw.Y)*row/rows, 6));
            return TerrainField.FromSamples(bounds,projection,columns,rows,heights);
        }
    }

    private static PathD Rectangle(double x0,double y0,double x1,double y1) => new() { new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1) };
    private static PointD Centroid(PathD path)
    {
        var origin = path[0]; double twiceArea = 0, x = 0, y = 0;
        for(int i=0;i<path.Count;i++)
        {
            var a=path[i];var b=path[(i+1)%path.Count];
            double ax=a.x-origin.x, ay=a.y-origin.y, bx=b.x-origin.x, by=b.y-origin.y;
            double cross=ax*by-ay*bx;twiceArea+=cross;x+=(ax+bx)*cross;y+=(ay+by)*cross;
        }
        return new(origin.x+x/(3*twiceArea),origin.y+y/(3*twiceArea));
    }
}
