using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class FarSurfaceChecks
{
    internal static bool Run()
    {
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var bounds=projection.Bounds(20);
        var site=new GeoFeature{Id="s",Polygons=new(){new(){new(){projection.ToGeo(-2,-2),projection.ToGeo(2,-2),projection.ToGeo(2,2),projection.ToGeo(-2,2)}}}};
        var near=EditRegion.Create(site,projection,bounds,3);
        var field=TerrainField.FromSamples(bounds,projection,4,4,Enumerable.Range(0,25)
            .Select(i=>100+.4*(-20+i%5*10)-.2*(-20+i/5*10)+.008*(-20+i%5*10)*(-20+i/5*10)).ToArray());
        var road=ContourPaths.Rectangle(-20,3,20,6);
        foreach(bool nurbs in new[]{false,true})
        foreach(bool separate in new[]{false,true})
        foreach(double scale in new[]{1d,1000d})
        {
            var roads=RoadGeometry.Build(Array.Empty<GeoFeature>(),projection,bounds) with {Outlines=road};
            var all=TerrainSurface.Build(field,roads,.05,scale,.001,nurbs,separate,near);
            var far=TerrainSurface.Build(field,roads,.05,scale,.001,nurbs,separate,near,includeNear:false);
            try
            {
                var expected=all.Pieces.Where(p=>p.Zone=="Far").ToArray();
                Require(far.Pieces.All(p=>p.Zone=="Far" && p.Geometry.IsValid),"only valid Far geometry is emitted");
                Require(Math.Abs(far.PlanAreaSquareMetres-all.PlanAreaSquareMetres)<1e-9 && far.PlanAreaResidualSquareMetres<.01,
                    "complete plane coverage is independent of output selection");
                foreach(string role in expected.Select(p=>p.Role).Distinct())
                {
                    double area=expected.Where(p=>p.Role==role).Sum(p=>Area(p.Geometry));
                    double actual=far.Pieces.Where(p=>p.Role==role).Sum(p=>Area(p.Geometry));
                    Require(Math.Abs(area-actual)/scale/scale<.001,"Far role surfaces and road sides retain their area");
                }
                Require(expected.Length==far.Pieces.Count,"same Far parts and road sides");
            }
            finally
            { foreach(var piece in all.Pieces.Concat(far.Pieces))piece.Geometry.Dispose(); }
        }
        CellRegionsAndDrape();
        LocalSiteWindow();
        return true;
    }

    private static void LocalSiteWindow()
    {
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var bounds=projection.Bounds(200);
        var field=TerrainField.FromSamples(bounds,projection,20,20,Enumerable.Range(0,441)
            .Select(i=>{double x=-200+i%21*20,y=-200+i/21*20;return 100+.1*x+.2*y+.001*x*y;}).ToArray());
        var footprint=ContourPaths.Difference(ContourPaths.Rectangle(-10,-10,10,10),
            ContourPaths.Rectangle(-2,-2,2,2));
        var feature=new GeoFeature{Id="local-site",Polygons=new(){footprint.Select(r=>r
            .Append(r[0]).Select(p=>projection.ToGeo(p.x,p.y)).ToList()).ToList()}};
        var site=SiteSelection.Create(new[]{feature},projection).Components.Single();
        foreach(double scale in new[]{1d,1000d})
        {
            var bodies=SiteGeometry.Build(field,site,70,scale,.001*scale);
            try
            {
                double volume=0;
                foreach(var body in bodies)
                {
                    Require(body.IsValid&&body.IsSolid&&body.IsManifold,"local site window retains a closed manifold NURBS body");
                    using var mass=VolumeMassProperties.Compute(body);
                    volume+=mass.Volume/scale/scale/scale;
                    var box=body.GetBoundingBox(true);
                    Require(Math.Abs(box.Min.Z/scale-70)<1e-7&&Math.Abs(box.Min.X/scale+10)<1e-7&&
                        Math.Abs(box.Max.X/scale-10)<1e-7,"local site window preserves the source XY and bottom");
                }
                Require(Math.Abs(volume-384*30)<1e-4,"local site window preserves bilinear terrain volume and courtyard");
            }
            finally{foreach(var body in bodies)body.Dispose();}
        }
    }

    private static void CellRegionsAndDrape()
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var bounds = projection.Bounds(30);
        // Unequal row/column counts expose incorrect cell keys. The road frame,
        // its courtyard and the crossing strip create several regions per cell.
        var field = TerrainField.FromSamples(bounds, projection, 6, 4, Enumerable.Repeat(100d, 35).ToArray());
        var frame = ContourPaths.Difference(ContourPaths.Rectangle(-18, -15, 18, 15),
            ContourPaths.Rectangle(-12, -9, 12, 9));
        var outlines = ContourPaths.Union(new PathsD(frame.Concat(ContourPaths.Rectangle(-20, -1, 20, 1))));
        foreach (bool rotated in new[] { false, true })
        foreach (bool nurbs in new[] { false, true })
        foreach (bool separate in new[] { false, true })
        foreach (double scale in new[] { 1d, 1000d })
        {
            PointD Move(PointD p) => rotated ? new(-p.y + 3, p.x - 2) : p;
            var road = new PathsD(outlines.Select(ring => new PathD(ring.Select(Move))));
            if (rotated) foreach (var ring in road) ring.Reverse();
            // RoadArea carries normalized shells/holes, unlike raw source rings.
            road = ContourPaths.Union(road);
            var roads = RoadGeometry.Build(Array.Empty<GeoFeature>(), projection, bounds) with { Outlines = road };
            var surface = TerrainSurface.Build(field, roads, .2, scale, .001, nurbs, separate);
            try
            {
                var boundary = new[] { new PointD(-10, -6), new PointD(10, -6), new PointD(10, 6),
                    new PointD(-10, 6), new PointD(-10, -6) }.Select(Move).ToArray();
                if (rotated) Array.Reverse(boundary);
                var draped = surface.Drape(boundary.Select(p => projection.ToGeo(p.x, p.y)).ToArray(), scale);
                Require(draped.Count > 4 && draped[0].DistanceTo(draped[^1]) / scale < 1e-8,
                    "multi-cell projected boundary remains closed");
                int verticalSteps = 0;
                foreach (var point in draped)
                {
                    double x = point.X / scale, y = point.Y / scale;
                    if (rotated) (x, y) = (y + 2, 3 - x);
                    Require(Math.Min(Math.Abs(Math.Abs(x) - 10), Math.Abs(Math.Abs(y) - 6)) < 1e-8,
                        "draping preserves the source boundary in XY");
                    Require(Math.Min(Math.Abs(point.Z / scale - 100), Math.Abs(point.Z / scale - (separate ? 99.8 : 100))) < 1e-8,
                        "draping retains the land and road heights");
                }
                for (int i = 1; i < draped.Count; i++)
                {
                    var a = draped[i - 1] / scale; var b = draped[i] / scale;
                    if (Math.Abs(a.Z - b.Z) < 1e-8) continue;
                    Require(new Point2d(a.X, a.Y).DistanceTo(new(b.X, b.Y)) < 1e-8 && Math.Abs(Math.Abs(a.Z - b.Z) - .2) < 1e-8,
                        "road crossings retain a vertical step without a diagonal shortcut");
                    verticalSteps++;
                }
                Require(verticalSteps == (separate ? 4 : 0), "all four road crossings are represented once");
                Require(surface.PlanAreaResidualSquareMetres < .01, "multi-region cell coverage is preserved");
            }
            finally { foreach (var piece in surface.Pieces) piece.Geometry.Dispose(); }

            if (separate) continue;
            var clip = ContourPaths.Difference(ContourPaths.Rectangle(-15, -15, 15, 15),
                ContourPaths.Rectangle(-2, -2, 2, 2));
            var clipped = TerrainSurface.Build(field, roads, 0, scale, .001, nurbs, false, clip: clip);
            try
            {
                double area = clipped.Pieces.Where(p => p.Role == "Land").Sum(p => Area(p.Geometry)) / scale / scale;
                Require(Math.Abs(area - 884) < 1e-6 && clipped.PlanAreaResidualSquareMetres < .01,
                    "clipped cells preserve the analytical site area and courtyard");
            }
            finally { foreach (var piece in clipped.Pieces) piece.Geometry.Dispose(); }
        }
    }
    private static double Area(GeometryBase geometry)
    {
        using var mass=geometry is Brep brep?AreaMassProperties.Compute(brep):AreaMassProperties.Compute((Mesh)geometry);
        return mass?.Area??throw new InvalidOperationException("Far surface area unavailable");
    }
    private static void Require(bool value,string message)
    {if(!value)throw new InvalidOperationException("Far-only preparation: "+message);}
}
