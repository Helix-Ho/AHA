using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class EditRegionChecks
{
    public static int Run()
    {
        int count = 0;
        var projection = new LocalProjection(new GeoPoint(126.98, 37.56));
        var bounds = projection.Bounds(500);
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("site selection/editing region: " + name);
            count++;
        }
        List<GeoPoint> Ring(params (double X, double Y)[] points) =>
            points.Select(p => projection.ToGeo(p.X, p.Y)).ToList();
        GeoFeature Rect(string id, double x1, double y1, double x2, double y2) => new()
        {
            Id = id, Pnu = id,
            Polygons = new() { new() { Ring((x1,y1),(x2,y1),(x2,y2),(x1,y2),(x1,y1)) } }
        };
        void Invalid(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, name);
        }

        var selected = Rect("single", -50,-10,50,10);
        var single = SiteSelection.Create(new[] { selected }, projection);
        Check(single.Parcels.Count == 1 && single.Components.Count == 1, "single parcel remains one site component");
        Check(Math.Abs(Geo.Area(single.Boundary, projection)-2000) < 1e-5, "selection union preserves source area");
        var centre = projection.ToLocal(single.Components[0].Centre);
        Check(Math.Abs(centre.X) < 1e-6 && Math.Abs(centre.Y) < 1e-6, "component centre is the area centroid");
        Check(Math.Abs(single.Components[0].RadiusMetres-Math.Sqrt(2600)) < 1e-6, "component radius reaches its farthest exterior vertex");
        var minimum = EditRegion.Create(single, projection, bounds, 1.5);
        var region = EditRegion.Create(single, projection, bounds, 2);
        double radius = 2*Math.Sqrt(2600);
        Check(minimum.Circles.Count == 1 && Math.Abs(minimum.Circles[0].RadiusMetres - 1.5*Math.Sqrt(2600)) < 1e-6,
            "minimum ratio 1.5 defines a circle radius from the site centre");
        Check(region.Circles[0].SiteComponentId == single.Components[0].Boundary.Id &&
            Math.Abs(region.Circles[0].CenterX) < 1e-6 && Math.Abs(region.Circles[0].CenterY) < 1e-6 &&
            Math.Abs(region.Circles[0].RadiusMetres-radius)<1e-6, "exact local circle retains its source component");
        Check(region.Intersects(Rect("inside",radius-2,-1,radius-1,1),projection), "circle includes interior buildings");
        Check(!region.Intersects(Rect("former-buffer",120,-1,122,1),projection), "circle does not retain the former boundary-offset area");
        Check(region.Intersects(Rect("tangent",radius,-1,radius+2,1),projection), "source intersection includes exact circle tangency");
        Check(!region.Intersects(Rect("outside",radius+.01,-1,radius+2,1),projection), "circle excludes buildings beyond its radius");
        Check(region.Intersects(Rect("enclosing",-250,-250,250,250),projection), "building enclosing the near circle intersects the region");
        Check(!region.Intersects(Rect("corner",radius-1,radius-1,radius,radius),projection), "circle excludes bounding-box corners");
        Check(region.ContainsStrict(Rect("strict-inside", radius-2,-1,radius-1,1),projection), "whole interior building is Near");
        Check(!region.ContainsStrict(Rect("strict-tangent", radius-2,-1,radius,1),projection), "circle tangent or crossing building is Far");
        Check(!region.ContainsStrict(Rect("strict-outside", radius+.01,-1,radius+2,1),projection), "outside building is Far");
        Check(!region.ContainsStrict(Rect("strict-enclosing", -250,-250,250,250),projection), "building surrounding Near is Far");
        Check(region.ContainsStrict(new PathsD { new PathD { new(-1,-1), new(1,-1),new(1,1),new(-1,1) } }),
            "cleaned local model paths use the same strict classification");
        var touchingVertex = new GeoFeature { Id="vertex-contact", Polygons = new() { new() {
            Ring((radius,0),(radius-2,1),(radius-2,-1),(radius,0)) } } };
        Check(!region.ContainsStrict(touchingVertex,projection), "one exact circle contact vertex is sufficient for Far classification");
        Check(region.Rings.All(r=>r[0]==r[^1]), "preview receives closed geographic rings");
        var preview = region.Paths.Single();
        double maxChordError = Enumerable.Range(0, preview.Count).Max(i =>
        {
            var a=preview[i]; var b=preview[(i+1)%preview.Count];
            return radius-Math.Sqrt(Math.Pow((a.x+b.x)/2,2)+Math.Pow((a.y+b.y)/2,2));
        });
        Check(maxChordError <= EditRegion.MaximumChordErrorMetres + 1e-8, "preview chords deviate by at most one millimetre");
        Check(preview.All(p=>Math.Abs(Math.Sqrt(p.x*p.x+p.y*p.y)-radius)<1e-6), "preview vertices lie on the exact circle within clipping precision");
        var larger = EditRegion.Create(single,projection,bounds,3);
        Check(Clipper.Area(larger.Paths)>Clipper.Area(region.Paths), "larger ratio expands the circle");
        var delegated = EditRegion.Create(selected,projection,bounds,2);
        Check(Math.Abs(Clipper.Area(delegated.Paths)-Clipper.Area(region.Paths))<1e-6, "single-feature overload uses the selection contract");

        var parcels = new[] {
            Rect("left-a",-40,-10,-20,10), Rect("left-b",-20,-10,0,10),
            Rect("island-small",20,-5,30,5), Rect("island-large",150,-10,170,10)
        };
        var selection = SiteSelection.Create(parcels,projection);
        Check(selection.Parcels.Select(p=>p.Id).Order().SequenceEqual(parcels.Select(p=>p.Id).Order()), "source parcel identities survive");
        Check(selection.Components.Count==3, "adjacent parcels dissolve before computing a circle");
        Check(Math.Abs(Geo.Area(selection.Boundary,projection)-1300)<1e-5, "adjacent union removes its internal boundary without adding area");
        var radii = selection.Components.Select(c=>c.RadiusMetres).Order().ToArray();
        Check(Math.Abs(radii[0]-Math.Sqrt(50))<1e-6 && Math.Abs(radii[1]-Math.Sqrt(200))<1e-6 && Math.Abs(radii[2]-Math.Sqrt(500))<1e-6,
            "each independent site retains its own R");
        var perSite = EditRegion.Create(selection,projection,bounds,2);
        Check(perSite.Circles.Count == 3, "one exact circle is exposed for each independent site");
        Check(perSite.Intersects(Rect("large-radius",-64,-1,-63,1),projection), "large site has a larger circle");
        Check(!perSite.Intersects(Rect("small-radius-limit",42,-1,43,1),projection), "small site does not inherit another site's radius");
        Check(perSite.Paths.Count(p=>Clipper.Area(p)>0)==2, "overlapping circles unite while a distant circle remains independent");
        var reversed = SiteSelection.Create(parcels.Reverse().ToArray(),projection);
        var reverseRegion = EditRegion.Create(reversed,projection,bounds,2);
        Check(Math.Abs(Clipper.Area(reverseRegion.Paths)-Clipper.Area(perSite.Paths))<1e-6, "selection order does not change the circle union");

        var overlap = SiteSelection.Create(new[] { Rect("overlap-a",0,0,20,20),Rect("overlap-b",10,0,30,20) },projection);
        Check(overlap.Components.Count==1 && Math.Abs(Geo.Area(overlap.Boundary,projection)-600)<1e-5, "overlapping parcels are unioned");
        var courtyard = Rect("courtyard",-40,-40,40,40);
        courtyard.Polygons[0].Add(Ring((-20,-20),(20,-20),(20,20),(-20,20),(-20,-20)));
        var holedSelection = SiteSelection.Create(new[] { courtyard },projection);
        Check(Math.Abs(Geo.Area(holedSelection.Boundary,projection)-4800)<1e-5, "declared site holes survive union regardless of winding");
        Check(EditRegion.Create(holedSelection,projection,bounds,2).Intersects(Rect("inside-hole",-2,-2,2,2),projection), "centroid circle includes the site's courtyard");
        var aroundCircle = Rect("building-courtyard",-250,-250,250,250);
        aroundCircle.Polygons[0].Add(Ring((-150,-150),(150,-150),(150,150),(-150,150),(-150,-150)));
        Check(!region.Intersects(aroundCircle,projection), "building courtyard containing the entire circle does not intersect");
        aroundCircle.Polygons[0][1] = Ring((-80,-80),(80,-80),(80,80),(-80,80),(-80,-80));
        Check(region.Intersects(aroundCircle,projection), "circle crossing a courtyard boundary intersects the building");

        Invalid(()=>EditRegion.Create(single,projection,projection.Bounds(100),2), "exact circle outside AOI is rejected");
        // An extremum between polygon vertices must also be rejected; AOI checks use exact circles.
        var exactBounds = new GeoBounds(projection.ToGeo(-radius+.01,-radius-1).Lon,
            projection.ToGeo(-radius-1,-radius-1).Lat,projection.ToGeo(radius+1,radius+1).Lon,projection.ToGeo(radius+1,radius+1).Lat);
        Invalid(()=>EditRegion.Create(single,projection,exactBounds,2), "AOI validation uses exact circle extents");
        Invalid(()=>EditRegion.Create(single,projection,bounds,1.49), "ratio below 1.5 is rejected");
        Invalid(()=>EditRegion.Create(single,projection,bounds,0), "zero ratio is rejected");
        Invalid(()=>EditRegion.Create(single,projection,bounds,-1), "negative ratio is rejected");
        Invalid(()=>EditRegion.Create(single,projection,bounds,double.NaN), "non-finite ratio is rejected");
        Invalid(()=>SiteSelection.Create(Array.Empty<GeoFeature>(),projection), "empty site selection is rejected");
        Invalid(()=>SiteSelection.Create(new[] { new GeoFeature() },projection), "invalid selected parcel is rejected");
        return count+HullChecks.Run();
    }
}
