using Aha.Topo;
using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class RoadGradingChecks
{
    public static int Run()
    {
        Action[] checks = { NoEligibleRoadLeavesSource, StraightGradeIsPreserved, NarrowCrossSlope,
            DiagonalGrade, LongitudinalBumps, CrossingIsOrderIndependent, ParallelGradesStaySeparate,
            HairpinRetainsDifferentLegElevations, SubcellParallelRoadsKeepTheirGrades,
            SingleCellMatrix, BoundedSteepSlope, LongSourceRouteIsClipped, BroadAreaBudget,
            HoleAndDisjointSectionsStayInRoad, JunctionDoesNotInventDirection, BoundaryOrderIndependent, Cancellation, RoadSolverChecks.Run };
        foreach (var check in checks) check();
        return checks.Length;
    }

    private static readonly LocalProjection Projection = new(new GeoPoint(126.98, 37.56));
    private static TerrainField Field(double extent, int cells, Func<double, double, double> height)
    {
        var bounds = Projection.Bounds(extent / 2);
        var heights = new double[(cells + 1) * (cells + 1)];
        for (int row = 0; row <= cells; row++)
        for (int col = 0; col <= cells; col++) heights[row * (cells + 1) + col] = height(
            extent * (col / (double)cells - 0.5), extent * (row / (double)cells - 0.5));
        return TerrainField.FromSamples(bounds, Projection, cells, cells, heights);
    }
    private static RoadArea Road(string id, double? width, params (double X, double Y)[] points)
    {
        if(width is not >0)return new(new PathsD(),0);
        var paths=Clipper.InflatePaths(new PathsD{new PathD(points.Select(p=>new PointD(p.X,p.Y)))},width.Value/2,JoinType.Round,EndType.Butt,2,6,.03);
        return new(paths,Math.Abs(Clipper.Area(paths)));
    }
    private static RoadGradingResult Apply(TerrainField field,IReadOnlyList<RoadArea> roads)
    {
        var all=new PathsD(roads.SelectMany(r=>r.Outlines));
        var aoi=new PathsD{new PathD{new(field.X0,field.Y0),new(field.X1,field.Y0),new(field.X1,field.Y1),new(field.X0,field.Y1)}};
        var paths=Clipper.Intersect(Clipper.Union(all,FillRule.NonZero),aoi,FillRule.NonZero,6);
        return RoadGrading.Apply(field,new(paths,Math.Abs(Clipper.Area(paths))));
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("road grading: " + message);
    }
    private static void Near(double actual, double expected, double tolerance, string message) =>
        Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance,
            $"{message}: expected {expected:g6}, received {actual:g6}, tolerance {tolerance:g6}");
    private static bool SameSamples(TerrainField a, TerrainField b) => a.Columns == b.Columns && a.Rows == b.Rows &&
        a.X0 == b.X0 && a.Y0 == b.Y0 && a.X1 == b.X1 && a.Y1 == b.Y1 && a.Heights.SequenceEqual(b.Heights);
    private static void Cancellation()
    {
        using var token = new CancellationTokenSource(); token.Cancel();
        try { RoadGrading.Apply(Field(20, 2, (x,y)=>0), new(new PathsD(),0), token.Token); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("road grading: cancelled requests cannot succeed, including empty roads");
    }
    private static void NoEligibleRoadLeavesSource()
    {
        var field = Field(300, 20, (x, y) => 100 + x * 0.03 + y * 0.2);
        var empty = Apply(field, Array.Empty<RoadArea>());
        Require(SameSamples(field, empty.Field) && empty.AdjustedNodes == 0, "no roads retains exact field");
        var missing = Apply(field, new[] { Road("missing", null, (-100, 0), (100, 0)) });
        Require(SameSamples(field, missing.Field) && missing.CrossSectionSamples == 0, "an empty area creates no assumed road grading");
        var outside = Apply(field, new[] { Road("outside", 5, (-100, 250), (100, 250)) });
        Require(SameSamples(field, outside.Field), "outside road creates no extrapolated terrain");
    }
    private static void StraightGradeIsPreserved()
    {
        var field = Field(300, 20, (x, y) => 100 + x * 0.08);
        var result = Apply(field, new[] { Road("grade", 6, (-140, 3), (140, 3)) });
        Require(result.Converged && result.MaximumAdjustmentMetres < 1e-8,
            "a plane aligned with road grade must remain unchanged, including profile ends");
    }
    private static void NarrowCrossSlope()
    {
        var field = Field(300, 20, (x, y) => 100 + x * 0.08 + y * 0.3);
        var copy = (double[])field.Heights.Clone();
        var result = Apply(field, new[] { Road("narrow", 4, (-130, 3), (130, 3)) });
        Require(result.Converged && result.Field.Columns == field.Columns && result.Field.Rows == field.Rows,
            "a narrow road does not invent higher source resolution");
        Require(field.Heights.SequenceEqual(copy), "input field is not mutated");
        double before = Math.Abs(field.SampleLocal(0, 5) - field.SampleLocal(0, 1));
        double after = Math.Abs(result.Field.SampleLocal(0, 5) - result.Field.SampleLocal(0, 1));
        Require(after < before * 0.2, $"4m road on 15m cells reduces transverse slope within terrain fidelity budget: {before:g6} -> {after:g6}");
        Near((result.Field.SampleLocal(80, 3) - result.Field.SampleLocal(-80, 3)) / 160, 0.08, 0.001,
            "longitudinal grade retained on cross-slope hill");
        Near(result.Field.SampleLocal(0, 100), field.SampleLocal(0, 100), 0, "outside influence terrain unchanged");
        Require(result.ReferenceHeightRmsMetres < 0.25, "road reference elevations remain close to their local longitudinal profile");
        Console.WriteLine($"Road grading narrow: cross {before:g4} -> {after:g4} m; reference RMS {result.ReferenceHeightRmsMetres:g4} m, {result.Iterations} iterations.");
    }
    private static void DiagonalGrade()
    {
        var field = Field(300, 20, (x, y) => 100 + 0.08 * (x + y) / Math.Sqrt(2));
        var result = Apply(field, new[] { Road("diagonal", 7, (-130, -130), (130, 130)) });
        Require(result.MaximumAdjustmentMetres < 1e-7, "a diagonal road's longitudinal plane is preserved");
    }
    private static void LongitudinalBumps()
    {
        var field = Field(600, 40, (x, y) => 100 + 0.1 * x + 2 * Math.Cos(x * Math.PI / 30));
        var result = Apply(field, new[] { Road("bumps", 8, (-280, 0), (280, 0)) });
        double before = 0, after = 0;
        for (int x = -180; x <= 180; x += 15)
        {
            double a = field.SampleLocal(x - 15, 0) - 2 * field.SampleLocal(x, 0) + field.SampleLocal(x + 15, 0);
            double b = result.Field.SampleLocal(x - 15, 0) - 2 * result.Field.SampleLocal(x, 0) + result.Field.SampleLocal(x + 15, 0);
            before += a * a; after += b * b;
        }
        Require(after < before * 0.8, "local longitudinal bumps attenuate without flattening the full road");
        Near((result.Field.SampleLocal(180, 0) - result.Field.SampleLocal(-180, 0)) / 360, 0.1, 0.001,
            "overall uphill grade survives smoothing");
    }
    private static void CrossingIsOrderIndependent()
    {
        var field = Field(300, 20, (x, y) => 100 + 0.04 * x + 0.08 * y + Math.Sin(x / 18) * Math.Cos(y / 21));
        var roads = new[] { Road("a", 6, (-140, 0), (140, 0)), Road("b", 6, (0, -140), (0, 140)) };
        var first = Apply(field, roads);
        var second = Apply(field, roads.Reverse().ToArray());
        Require(first.Converged && first.CrossSlopeRmsAfter < first.CrossSlopeRmsBefore, "intersecting constraints reduce transverse slope together");
        for (int i = 0; i < field.Heights.Length; i++) Near(first.Field.Heights[i], second.Field.Heights[i], 1e-10,
            "crossing road result independent of API feature order");
        Require(first.Field.Heights.All(double.IsFinite), "intersection is a single-valued finite field without a folded strip");
    }
    private static void ParallelGradesStaySeparate()
    {
        var field = Field(300, 20, (x, y) => 100 + 0.05 * x + 0.2 * y);
        var roads = new[] { Road("lower", 4, (-130, -20), (130, -20)), Road("upper", 4, (-130, 20), (130, 20)) };
        var result = Apply(field, roads);
        double separation = result.Field.SampleLocal(0, 20) - result.Field.SampleLocal(0, -20);
        Require(separation > 7, $"parallel road grades are not averaged into one elevation: {separation:g6}");
        Require(result.CrossSlopeRmsAfter < result.CrossSlopeRmsBefore * 0.2, "parallel roads become transversely flatter");
    }
    private static void HairpinRetainsDifferentLegElevations()
    {
        var field = Field(360, 24, (x, y) => 100 + 0.04 * x + 0.22 * y);
        var road = Road("hairpin", 5, (-150, -25), (110, -25), (130, -15), (140, 0), (130, 15), (110, 25), (-150, 25));
        var result = Apply(field, new[] { road });
        Require(result.Converged && result.Field.SampleLocal(-80, 25) - result.Field.SampleLocal(-80, -25) > 10,
            "hairpin legs retain separate grades, without nearest-segment averaging");
        Near(result.Field.SampleLocal(-80, -25) - result.Field.SampleLocal(-130, -25), 2, 0.15,
            "hairpin straight leg keeps its direction of slope");
    }
    private static void LongSourceRouteIsClipped()
    {
        var field = Field(300, 20, (x, y) => 100 + 0.04 * x + 0.2 * y);
        var result = Apply(field, new[] { Road("long", 6, (-500_000, 3), (500_000, 3)) });
        Require(result.CrossSectionSamples < 100 && result.CrossSectionSamples > 35 && result.Converged,
            "long synthetic corridor clips before boundary sampling");
    }
    private static void SubcellParallelRoadsKeepTheirGrades()
    {
        var field = Field(300, 20, (x, y) => 100 + 0.05 * x + 0.4 * y);
        var roads = new[] { Road("near-a", 3, (-130, 22), (130, 22)), Road("near-b", 3, (-130, 28), (130, 28)) };
        var result = Apply(field, roads);
        double original = field.SampleLocal(0, 28) - field.SampleLocal(0, 22);
        double adjusted = result.Field.SampleLocal(0, 28) - result.Field.SampleLocal(0, 22);
        Require(result.Converged && adjusted > original * 0.85,
            $"unresolvable close parallel grades take priority over transverse flattening: {original:g4} -> {adjusted:g4}");
        Console.WriteLine($"Road grading sub-cell parallel: {original:g4} -> {adjusted:g4}m elevation separation.");
    }
    private static void SingleCellMatrix()
    {
        var field = Field(15, 1, (x, y) => 100 + 0.05 * x + 0.2 * y);
        var result = Apply(field, new[] { Road("one-cell", 3, (-7, 1), (7, 1)) });
        Require(result.Converged && result.CrossSlopeRmsAfter < result.CrossSlopeRmsBefore * 0.05,
            $"single-cell grid has the same valid neighbour topology: {result.CrossSlopeRmsBefore:g6} -> {result.CrossSlopeRmsAfter:g6}, converged {result.Converged}");
        Near((result.Field.SampleLocal(5, 1) - result.Field.SampleLocal(-5, 1)) / 10, 0.05, 0.001,
            "single-cell road retains grade");
    }
    private static void BroadAreaBudget()
    {
        var field = Field(3000, 200, (x, y) => 100 + 0.03 * x + 0.07 * y + 6 * Math.Sin(x / 100) * Math.Cos(y / 120));
        var roads = new List<RoadArea>();
        for (int i = -14; i <= 14; i++)
        {
            roads.Add(Road("h" + i, 6, (-1490, i * 100), (1490, i * 100)));
            roads.Add(Road("v" + i, 8, (i * 100, -1490), (i * 100, 1490)));
        }
        var result = Apply(field, roads);
        Require(result.Converged && result.Field.Heights.Length == field.Heights.Length,
            $"3km network remains bounded: {result.ElapsedSeconds:g4}s/{result.Iterations} iterations");
        Require(result.CrossSlopeRmsAfter < result.CrossSlopeRmsBefore, "broad terrain improves measured transverse slope");
        Console.WriteLine($"Road grading broad: {result.CrossSectionSamples} samples, {result.ElapsedSeconds:g4}s, {result.Iterations} iterations, cross RMS {result.CrossSlopeRmsBefore:g4} -> {result.CrossSlopeRmsAfter:g4}.");
    }
    private static void BoundedSteepSlope()
    {
        var field = Field(300, 20, (x, y) => 400 + 0.04 * x + 2 * y);
        var result = Apply(field, new[] { Road("steep", 8, (-135, 3), (135, 3)) });
        Console.WriteLine($"Road grading bounded: {result.Iterations} iterations.");
        Require(result.Converged && result.LimitedNodes > 0, "steep hillside converges with active terrain trust limits");
        Require(result.MaximumAdjustmentMetres <= RoadGrading.MaximumNodeAdjustmentMetres + 1e-10,
            "no node exceeds the global context-modeling deformation budget");
        for (double x = -140; x <= 140; x += 9.7)
        for (double y = -140; y <= 140; y += 11.3)
            Require(Math.Abs(result.Field.SampleLocal(x, y) - field.SampleLocal(x, y)) <= RoadGrading.MaximumNodeAdjustmentMetres + 1e-9,
                "bounded bilinear nodes bound the continuous surface, not only sampled road locations");
        Require(result.CrossSlopeRmsAfter < result.CrossSlopeRmsBefore, "extreme slope receives partial flattening instead of invented large cut and fill");
        Near((result.Field.SampleLocal(80, 3) - result.Field.SampleLocal(-80, 3)) / 160, 0.04, 0.002,
            "bounded correction preserves longitudinal grade");
    }

    private static void HoleAndDisjointSectionsStayInRoad()
    {
        var field=Field(300,20,(x,y)=>100+.04*x+.2*y);
        var outer=new PathD{new(-100,-45),new(100,-45),new(100,45),new(-100,45)};
        var hole=new PathD{new(-94,-39),new(94,-39),new(94,39),new(-94,39)};
        var island=new PathD{new(-100,80),new(100,80),new(100,86),new(-100,86)};
        var paths=Clipper.Union(Clipper.Difference(new PathsD{outer},new PathsD{hole},FillRule.NonZero,6),new PathsD{island},FillRule.NonZero,6);
        var area=new RoadArea(paths,Math.Abs(Clipper.Area(paths)));
        var samples=RoadCrossSections.Build(field,area);
        Require(samples.Count>80,"road ring and disconnected corridor both supply supported sections");
        foreach(var sample in samples)
        {
            Require(sample.Width<6.000001,"a boundary ray stops at the first road boundary, not across an island or adjacent road");
            for(int i=1;i<10;i++)
            {
                double t=i/10d-.5;var point=new PointD(sample.X+t*sample.Width*sample.Nx,sample.Y+t*sample.Width*sample.Ny);
                int winding=paths.Sum(p=>Clipper.PointInPolygon(point,p,6)==PointInPolygonResult.IsInside?(Clipper.Area(p)>0?1:-1):0);
                Require(winding>0,"all interior points of each cross-section lie inside the cadastral road union");
            }
        }
        var result=RoadGrading.Apply(field,area);
        Require(result.Converged&&result.Field.SampleLocal(0,83)-result.Field.SampleLocal(0,42)>7.5,
            "separate road elevations survive a road island and an adjacent road");
    }

    private static void JunctionDoesNotInventDirection()
    {
        var field=Field(300,20,(x,y)=>100+.04*x+.08*y);
        var a=Road("horizontal",8,(-130,0),(130,0));var b=Road("vertical",8,(0,-130),(0,130));
        var paths=Clipper.BooleanOp(ClipType.Union,new PathsD(a.Outlines.Concat(b.Outlines)),null,FillRule.NonZero,6);
        var samples=RoadCrossSections.Build(field,new(paths,Math.Abs(Clipper.Area(paths))));
        Require(samples.Count>50&&samples.All(s=>s.Width<8.000001),
            "a junction does not turn an end-to-end length into a road width");
        Require(samples.All(s=>Math.Abs(s.X)>4||Math.Abs(s.Y)>4),
            "ambiguous junction centre is left to the common terrain solve, without an invented crossfall direction");
    }

    private static void BoundaryOrderIndependent()
    {
        var field=Field(300,20,(x,y)=>100+.04*x+.2*y);
        var paths=new PathsD{new PathD{new(-130,-23),new(130,-23),new(130,-17),new(-130,-17)},
            new PathD{new(-130,17),new(130,17),new(130,23),new(-130,23)}};
        var rotated=new PathsD(paths.AsEnumerable().Reverse().Select(p=>new PathD(p.Skip(2).Concat(p.Take(2)))));
        var first=RoadGrading.Apply(field,new(paths,Math.Abs(Clipper.Area(paths))));
        var second=RoadGrading.Apply(field,new(rotated,Math.Abs(Clipper.Area(rotated))));
        Require(first.CrossSectionSamples==second.CrossSectionSamples,"ring start and part order retain section counts");
        for(int i=0;i<field.Heights.Length;i++)Near(first.Field.Heights[i],second.Field.Heights[i],1e-10,
            "cadastral ring start and part order do not change grading");
    }
}
