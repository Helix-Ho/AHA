using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class ContourWallChecks
{
    internal static void Verify(Brep wall, EditRegion near, TerrainField field, PathsD roads,
        double drop, double bottom, double scale)
    {
        Require(wall.IsValid && !wall.IsSolid && wall.Faces.Count == 1, "one valid open face without caps");
        var face = wall.Faces[0];
        Require(face.UnderlyingSurface() is NurbsSurface && face.Degree(1) == 1, "vertical ruled NURBS");
        var parts = new ContourBoundary(roads).Split(near.Boundaries.SelectMany(b => b.Segments), false);
        var spans = face.GetSpanVector(0);
        for (int i = 1; i < spans.Length; i++)
        for (int j = 0; j <= 4; j++)
        {
            double u = spans[i - 1] + (spans[i] - spans[i - 1]) * j / 4;
            var low = face.PointAt(u, face.Domain(1).Min) / scale;
            var high = face.PointAt(u, face.Domain(1).Max) / scale;
            Require(Math.Abs(low.Z - bottom) < 1e-7 && high.Z > low.Z, "positive wall reaches base top");
            Require(Math.Abs(low.X - high.X) < 1e-7 && Math.Abs(low.Y - high.Y) < 1e-7, "vertical generator");
            var matches = parts.Where(p => EditBoundaryMath.OnSegment(new(high.X, high.Y), p.Edge, 1e-7)).ToArray();
            Require(matches.Length > 0, "exact Near arc or tangent");
            Require(matches.Any(p => Math.Abs(high.Z - field.SampleModelLocal(high.X, high.Y) + (p.Filled ? drop : 0)) < .001), $"continuous Far height and road drop at {high.X:R},{high.Y:R}: wall={high.Z:R}, field={field.SampleModelLocal(high.X,high.Y):R}, drops={string.Join(",",matches.Select(p=>p.Filled?drop:0))}");
        }
    }

    internal static bool LiftRegression()
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var sw = projection.ToGeo(-20, -20); var ne = projection.ToGeo(20, 20);
        var bounds = new GeoBounds(sw.Lon, sw.Lat, ne.Lon, ne.Lat);
        double Height(double x, double y) => 100 + .13*x - .27*y + .017*x*y;
        var field = TerrainField.FromSamples(bounds, projection, 4, 4,
            Enumerable.Range(0, 25).Select(i => Height(-20 + (i%5)*10, -20 + (i/5)*10)).ToArray());
        foreach (var circles in new[] {
            new[] { new EditCircle("one",0,0,7) },
            new[] { new EditCircle("a",-4,0,6),new EditCircle("b",4,0,4) },
            new[] { new EditCircle("a",-10,0,3),new EditCircle("b",10,0,3) },
            new[] { new EditCircle("a",-4,0,6),new EditCircle("b",4,0,5),new EditCircle("c",0,5,5) } })
        foreach (var edge in EditBoundaryHull.Create(circles).SelectMany(b => b.Segments))
        {
            using var curve = TerrainBoundaryGeometry.Lift(edge, field, .2);
            using var wall = TerrainBoundaryGeometry.VerticalWall(curve, _ => 70);
            Require(curve.IsValid && wall.IsValid && !wall.IsSolid, "lift and uncapped wall are valid");
            for (int i = 0; i <= 100; i++)
            {
                var point = curve.PointAt(curve.Domain.ParameterAt(i / 100d));
                Require(EditBoundaryMath.OnSegment(new(point.X,point.Y), edge, 1e-8), "rational lift retains planar arc/tangent");
                Require(Math.Abs(point.Z - Height(point.X, point.Y) + .2) < 1e-7, "independent bilinear polynomial matches lifted curve");
            }
        }
        return true;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Contour wall: " + message); }
}
