using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class ContourSectionChecks
{
    internal static int Run()
    {
        int count = 0;
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var bounds = projection.Bounds(200);
        var template = TerrainField.FromSamples(bounds, projection, 17, 15, new double[18 * 16]);
        var region = ContourPaths.Rectangle(template.ModelX(0), template.ModelY(0), template.ModelX(17), template.ModelY(15));
        double area = ContourPaths.Area(region);
        foreach (var (name, f) in new (string, Func<double, double, double>)[]
        {
            ("flat", (x,y) => 0), ("plane", (x,y) => .02*x),
            ("saddle", (x,y) => .001*x*y), ("ridge", (x,y) => .02*Math.Abs(x)),
            ("critical vertices", (x,y) => .001*Math.Abs(x)*Math.Abs(y)),
            ("unequal edge subdivisions", (x,y) => (x<0?.0003:.0011)*x*y)
        })
        {
            var field = TerrainField.FromSamples(bounds, projection, 17, 15, Enumerable.Range(0,18*16)
                .Select(i => f(template.ModelX(i%18), template.ModelY(i/18))).ToArray());
            var levels = new ContourLevels(field, 1, region);
            PathsD? previous = null;
            for (long k = -5; k <= 5; k++)
            {
                var section = levels.At(k);
                Require(PlanarTriangulation.ToGridGeometry(section).IsValid, name + " closed planar topology");
                if (previous is not null) Require(ContourPaths.Area(ContourPaths.Difference(section,previous)) < 1e-7, name + " nested superlevels");
                if (name == "flat") Require(Math.Abs(ContourPaths.Area(section)-(k<=0?area:0)) < 1e-6, "flat inclusive level");
                if (name == "plane")
                {
                    double width = Math.Clamp(template.ModelX(17)-k/.02,0,template.ModelX(17)-template.ModelX(0));
                    Require(Math.Abs(ContourPaths.Area(section)-width*(template.ModelY(15)-template.ModelY(0))) < .001,
                        "linear slope analytic area including negative elevations");
                }
                previous = section;
            }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { _ = new ContourLevels(template,1,region,cancelled.Token); throw new Exception("uncancelled preparation"); }
        catch (OperationCanceledException) { count++; }
        var prepared = new ContourLevels(template,1,region,CancellationToken.None);
        Require(prepared.At(0).Count>0, "flat preparation is reusable for sequential levels");
        using var processing = new CancellationTokenSource();
        var cancellable = new ContourLevels(template,1,region,processing.Token);
        processing.Cancel();
        try { cancellable.At(0); throw new Exception("uncancelled extraction"); }
        catch (OperationCanceledException) { count++; }
        foreach (double invalid in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
        {
            try { _ = new ContourLevels(template,invalid,region); throw new Exception("invalid interval accepted"); }
            catch (ArgumentException) { count++; }
        }
        return count;

        void Require(bool value,string message)
        { if(!value) throw new InvalidOperationException("contour section: "+message); count++; }
    }
}
