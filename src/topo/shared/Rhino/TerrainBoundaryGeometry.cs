using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Direct boundary lifting and vertical ruled walls; no temporary terrain faces.</summary>
internal static class TerrainBoundaryGeometry
{
    internal static NurbsCurve Lift(EditBoundarySegment edge, TerrainField field, double drop)
    {
        var cuts = new SortedSet<double> { 0, 1 };
        if (edge.Circle is { } circle)
        {
            int spans = Math.Max(1, (int)Math.Ceiling(edge.SweepRadians / (Math.PI / 2)));
            for (int i = 1; i < spans; i++) cuts.Add((double)i / spans);
        }
        for (int col = 1; col < field.Columns; col++)
            AddCuts(new(new(field.ModelX(col), field.ModelY(0)), new(field.ModelX(col), field.ModelY(field.Rows))));
        for (int row = 1; row < field.Rows; row++)
            AddCuts(new(new(field.ModelX(0), field.ModelY(row)), new(field.ModelX(field.Columns), field.ModelY(row))));
        using var composite = new PolyCurve();
        var stops = cuts.ToArray();
        for (int i = 1; i < stops.Length; i++)
        {
            double from = stops[i - 1], to = stops[i];
            if (to - from <= 1e-14) continue;
            var mid = edge.PointAt((from + to) / 2);
            var cell = field.ModelCell(mid.X, mid.Y);
            using var span = edge.Circle is { } arc
                ? LiftArc(arc, edge.StartAngle + edge.SweepRadians * from, edge.SweepRadians * (to - from), field, cell.Column, cell.Row, drop)
                : TerrainSurfaceGeometry.BilinearEdge(Point(from), Point((from + to) / 2), Point(to));
            if (!composite.AppendSegment(span)) throw new InvalidOperationException("지형 외벽의 연속 경계를 연결할 수 없습니다.");
        }
        return composite.ToNurbsCurve() ?? throw new InvalidOperationException("지형 외벽의 NURBS 경계를 만들 수 없습니다.");

        Point3d Point(double t)
        { var p = edge.PointAt(t); return new(p.X, p.Y, field.SampleModelLocal(p.X, p.Y) - drop); }
        void AddCuts(EditBoundarySegment grid)
        {
            foreach (var p in EditBoundaryMath.Intersections(edge, grid, 1e-9))
            {
                double t;
                if (p.DistanceTo(edge.Start) <= 1e-9) t = 0;
                else if (p.DistanceTo(edge.End) <= 1e-9) t = 1;
                else if (edge.Circle is { } arc)
                    t = EditBoundaryMath.PositiveAngle(Math.Atan2(p.Y - arc.CenterY, p.X - arc.CenterX) - edge.StartAngle) / edge.SweepRadians;
                else
                { var d = edge.End - edge.Start; t = EditBoundaryMath.Dot(p - edge.Start, d) / d.LengthSquared; }
                if (t > 0 && t < 1) cuts.Add(t);
            }
        }
    }

    // A rational quadratic XY arc composed with a bilinear height has degree four:
    // X'=XW, Y'=YW, W'=W², Z'=z00W²+duUW+dvVW+crossUV.
    // Thus both the exact planar circle and the cell height are retained without fitting.
    private static NurbsCurve LiftArc(EditCircle circle, double start, double sweep,
        TerrainField field, int col, int row, double drop)
    {
        using var arc = new ArcCurve(new Arc(new Circle(new Point3d(circle.CenterX, circle.CenterY, 0), circle.RadiusMetres),
            new Interval(start, start + sweep)));
        using var planar = arc.ToNurbsCurve();
        if (planar.Points.Count != 3) throw new InvalidOperationException("원호 구간의 차수가 예상과 다릅니다.");
        double x0 = field.ModelX(col), y0 = field.ModelY(row);
        double dx = field.ModelX(col + 1) - x0, dy = field.ModelY(row + 1) - y0;
        double z00 = field.Node(col, row) - drop, du = field.Node(col + 1, row) - drop - z00,
            dv = field.Node(col, row + 1) - drop - z00,
            cross = field.Node(col, row) - field.Node(col + 1, row) - field.Node(col, row + 1) + field.Node(col + 1, row + 1);
        var p = planar.Points.ToArray();
        var w = p.Select(v => v.Weight).ToArray();
        var x = p.Select(v => v.Location.X * v.Weight).ToArray();
        var y = p.Select(v => v.Location.Y * v.Weight).ToArray();
        var u = Enumerable.Range(0, 3).Select(i => (x[i] - x0 * w[i]) / dx).ToArray();
        var v = Enumerable.Range(0, 3).Select(i => (y[i] - y0 * w[i]) / dy).ToArray();
        var curve = new NurbsCurve(3, true, 5, 5);
        for (int i = 0; i < 4; i++) { curve.Knots[i] = 0; curve.Knots[i + 4] = 1; }
        for (int k = 0; k < 5; k++)
        {
            double weight = Product(w, w, k);
            curve.Points.SetPoint(k, new Point3d(Product(x, w, k) / weight, Product(y, w, k) / weight,
                (z00 * weight + du * Product(u, w, k) + dv * Product(v, w, k) + cross * Product(u, v, k)) / weight), weight);
        }
        return curve;
        static double Product(double[] a, double[] b, int k)
        {
            int[] c2 = { 1, 2, 1 }, c4 = { 1, 4, 6, 4, 1 };
            double value = 0;
            for (int i = Math.Max(0, k - 2); i <= Math.Min(2, k); i++) value += c2[i] * c2[k - i] * a[i] * b[k - i] / c4[k];
            return value;
        }
    }

    internal static Brep VerticalWall(NurbsCurve upper, Func<double, double> lowerHeight)
    {
        using var surface = NurbsSurface.Create(3, upper.IsRational, upper.Order, 2, upper.Points.Count, 2)
            ?? throw new InvalidOperationException("수직 NURBS 외벽을 만들 수 없습니다.");
        for (int i = 0; i < upper.Knots.Count; i++) surface.KnotsU[i] = upper.Knots[i];
        surface.KnotsV[0] = 0; surface.KnotsV[1] = 1;
        for (int i = 0; i < upper.Points.Count; i++)
        {
            var point = upper.Points[i];
            surface.Points.SetPoint(i, 0, new Point3d(point.Location.X, point.Location.Y, lowerHeight(point.Location.Z)), point.Weight);
            surface.Points.SetControlPoint(i, 1, point);
        }
        var result = surface.ToBrep();
        if (result is null || !result.IsValid)
        { result?.Dispose(); throw new InvalidOperationException("수직 NURBS 외벽이 유효하지 않습니다."); }
        return result;
    }
}
