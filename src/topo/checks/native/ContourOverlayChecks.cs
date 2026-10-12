using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class ContourOverlayChecks
{
    internal static void Run()
    {
        using(var input=File.OpenRead(FixtureData.PathFor("profile-grid-rounding.json")))
        {
            var coordinates=System.Text.Json.JsonSerializer.Deserialize<double[][][]>(input!)!;
            var original=new PathsD(coordinates.Select(r=>new PathD(r.Select(p=>new PointD(p[0],p[1])))));
            double originalArea=ContourPaths.Area(original);
            double perimeter=original.Sum(r=>Enumerable.Range(0,r.Count).Sum(i=>
                Math.Sqrt(Math.Pow(r[i].x-r[(i+1)%r.Count].x,2)+Math.Pow(r[i].y-r[(i+1)%r.Count].y,2))));
            foreach(double scale in new[]{1d,1000d})
            {
                using var mesh=ContourGeometry.OverlayMesh(original,12,scale);
                using var area=AreaMassProperties.Compute(mesh);
                if(!mesh.IsValid||area is null||Math.Abs(area.Area/scale/scale-originalArea)>perimeter*ModelPlanPolicy.PrecisionMetres||
                    mesh.GetNakedEdges().Any(edge=>!edge.IsClosed))
                    throw new InvalidOperationException("Profile sampling must normalize grid crossings before assigning shells and holes.");
            }
        }
        // Two legitimate boundary vertices are 1µm apart.
        var witness = FixtureData.Witness("overlay");
        var source = new PathD(witness.GetProperty("source").EnumerateArray()
            .Select(p=>new PointD(p[0].GetDouble(),p[1].GetDouble())));
        double expected = Math.Abs(Clipper.Area(source));
        foreach (double scale in new[] { 1d, 1000d })
        foreach (double shift in new[] { 0d, 10000d, -10000d })
        foreach (bool reverse in new[] { false, true })
        {
            var path = new PathD(source.Select(p => new PointD(p.x + shift, p.y - shift)));
            if (reverse) path.Reverse();
            // ContourPlan supplies normalized outer/hole orientation.
            var domain = ContourPaths.Union(new PathsD { path });
            using var mesh = ContourGeometry.OverlayMesh(domain, 25, scale);
            using var area = AreaMassProperties.Compute(mesh);
            if (!mesh.IsValid || mesh.Vertices.Count != 6 || mesh.Faces.Count != 4 ||
                area is null || Math.Abs(area.Area / (scale * scale) - expected) > 1e-7 ||
                mesh.GetNakedEdges().Length != 1 || !mesh.GetNakedEdges()[0].IsClosed)
                throw new InvalidOperationException("Contour overlay must preserve source vertex identities, area and closed boundary.");
        }
        foreach (var path in new[] { source,
            new PathD(witness.GetProperty("thin").EnumerateArray()
                .Select(p=>new PointD(p[0].GetDouble(),p[1].GetDouble()))),
            new PathD { new(20,-150), new(21,-150), new(21,-149.999999), new(20,-149.999999) } })
        {
            using var loop = new PolylineCurve(path.Append(path[0]).Select(p => new Point3d(p.x, p.y, 0)));
            using var surface = ContourGeometry.OverlaySurface(new[] { loop }, 25, .001);
            if (!surface.IsValid || surface.Faces.Count != 1 || surface.Loops.Count != 1 ||
                surface.GetBoundingBox(true).Min.Z != 25 || surface.GetBoundingBox(true).Max.Z != 25)
                throw new InvalidOperationException("Contour overlay must retain known thin planar profiles.");
        }
    }
}
