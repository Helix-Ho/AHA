namespace Aha.Topo;

public readonly record struct EditBoundaryPoint(double X, double Y)
{
    public static EditBoundaryPoint operator +(EditBoundaryPoint a, EditBoundaryPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static EditBoundaryPoint operator -(EditBoundaryPoint a, EditBoundaryPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static EditBoundaryPoint operator *(EditBoundaryPoint p, double scale) => new(p.X * scale, p.Y * scale);
    public double LengthSquared => X * X + Y * Y;
    public double DistanceTo(EditBoundaryPoint other) => Math.Sqrt((this - other).LengthSquared);
}

/// <summary>One CCW exact circular arc, or one straight common tangent when Circle is null.</summary>
public sealed record EditBoundarySegment(EditBoundaryPoint Start, EditBoundaryPoint End,
    EditCircle? Circle = null, double StartAngle = 0, double SweepRadians = 0)
{
    public bool IsArc => Circle != null;
    public EditBoundaryPoint PointAt(double fraction) => Circle is { } circle
        ? EditBoundaryMath.CirclePoint(circle, StartAngle + SweepRadians * fraction)
        : Start + (End - Start) * fraction;
}

/// <summary>A convex exact boundary; separate loops describe separate near regions.</summary>
public sealed class EditBoundaryLoop
{
    public IReadOnlyList<EditCircle> Circles { get; }
    public IReadOnlyList<EditBoundarySegment> Segments { get; }
    internal EditBoundaryLoop(IReadOnlyList<EditCircle> circles, IReadOnlyList<EditBoundarySegment> segments)
    { Circles = circles; Segments = segments; }

    public bool Contains(EditBoundaryPoint point, double tolerance = .000001)
    {
        foreach (var segment in Segments)
        {
            if (segment.Circle is not { } circle)
            {
                var direction = segment.End - segment.Start;
                if (EditBoundaryMath.Cross(direction, point - segment.Start) < -tolerance * Math.Sqrt(direction.LengthSquared)) return false;
                continue;
            }
            var delta = point - new EditBoundaryPoint(circle.CenterX, circle.CenterY);
            double maximum = Math.Max(EditBoundaryMath.Dot(delta, EditBoundaryMath.Unit(segment.StartAngle)),
                EditBoundaryMath.Dot(delta, EditBoundaryMath.Unit(segment.StartAngle + segment.SweepRadians)));
            double angle = Math.Atan2(delta.Y, delta.X);
            if (EditBoundaryMath.AngleOnArc(angle, segment)) maximum = Math.Sqrt(delta.LengthSquared);
            if (maximum > circle.RadiusMetres + tolerance) return false;
        }
        return true;
    }

    public bool Intersects(EditBoundaryLoop other, double tolerance = .000001) =>
        Contains(other.Segments[0].Start, tolerance) || other.Contains(Segments[0].Start, tolerance) ||
        Segments.Any(a => other.Segments.Any(b => EditBoundaryMath.Intersections(a, b, tolerance).Count != 0));

    public bool IntersectsSegment(EditBoundaryPoint a, EditBoundaryPoint b, double tolerance = .000001) =>
        Contains(a, tolerance) || Contains(b, tolerance) ||
        Segments.Any(segment => EditBoundaryMath.Intersections(segment, new(a, b), tolerance).Count != 0);
}

/// <summary>Convex envelopes of overlapping disks, merged again if the envelopes meet.</summary>
public static class EditBoundaryHull
{
    public static IReadOnlyList<EditBoundaryLoop> Create(IReadOnlyList<EditCircle> input)
    {
        if (input.Count == 0 || input.Any(circle => !double.IsFinite(circle.CenterX) || !double.IsFinite(circle.CenterY) ||
                !double.IsFinite(circle.RadiusMetres) || circle.RadiusMetres <= 0))
            throw new ArgumentException("근접 영역의 원이 유효하지 않습니다.");
        var groups = input.OrderBy(circle => circle.CenterX).ThenBy(circle => circle.CenterY)
            .ThenBy(circle => circle.RadiusMetres).ThenBy(circle => circle.SiteComponentId, StringComparer.Ordinal)
            .Select(circle => new List<EditCircle> { circle }).ToList();
        Merge((a, b) => a.Any(x => b.Any(y => new EditBoundaryPoint(x.CenterX, x.CenterY)
            .DistanceTo(new(y.CenterX, y.CenterY)) <= x.RadiusMetres + y.RadiusMetres + .000001)));
        while (true)
        {
            var hulls = groups.Select(Build).ToArray();
            bool changed = false;
            for (int a = 0; a < hulls.Length && !changed; a++)
            for (int b = a + 1; b < hulls.Length; b++)
                if (hulls[a].Intersects(hulls[b]))
                { groups[a].AddRange(groups[b]); groups.RemoveAt(b); changed = true; break; }
            if (!changed) return hulls;
        }

        void Merge(Func<IReadOnlyList<EditCircle>, IReadOnlyList<EditCircle>, bool> touches)
        {
            bool changed;
            do
            {
                changed = false;
                for (int a = 0; a < groups.Count && !changed; a++)
                for (int b = a + 1; b < groups.Count; b++)
                    if (touches(groups[a], groups[b]))
                    { groups[a].AddRange(groups[b]); groups.RemoveAt(b); changed = true; break; }
            } while (changed);
        }
    }

    private static EditBoundaryLoop Build(IReadOnlyList<EditCircle> originals)
    {
        var circles = originals.GroupBy(circle => (circle.CenterX, circle.CenterY, circle.RadiusMetres))
            .Select(group => group.First()).ToArray();
        var events = new SortedSet<double> { 0, 2 * Math.PI };
        for (int a = 0; a < circles.Length; a++)
        for (int b = a + 1; b < circles.Length; b++)
        {
            var delta = new EditBoundaryPoint(circles[b].CenterX - circles[a].CenterX, circles[b].CenterY - circles[a].CenterY);
            double distance = Math.Sqrt(delta.LengthSquared), radiusDifference = circles[a].RadiusMetres - circles[b].RadiusMetres;
            if (distance <= Math.Abs(radiusDifference)) continue;
            double angle = Math.Atan2(delta.Y, delta.X), offset = Math.Acos(Math.Clamp(radiusDifference / distance, -1, 1));
            events.Add(EditBoundaryMath.PositiveAngle(angle - offset)); events.Add(EditBoundaryMath.PositiveAngle(angle + offset));
        }
        var angles = new List<double>();
        foreach (double angle in events)
            if (angles.Count == 0 || angle - angles[^1] > 1e-12) angles.Add(angle);
        if (2 * Math.PI - angles[^1] > 1e-12) angles.Add(2 * Math.PI); else angles[^1] = 2 * Math.PI;
        var arcs = new List<(EditCircle Circle, double Start, double End)>();
        for (int index = 1; index < angles.Count; index++)
        {
            var normal = EditBoundaryMath.Unit((angles[index - 1] + angles[index]) / 2);
            var winner = circles.OrderByDescending(circle => circle.CenterX * normal.X + circle.CenterY * normal.Y + circle.RadiusMetres).First();
            if (arcs.Count != 0 && arcs[^1].Circle == winner)
                arcs[^1] = (winner, arcs[^1].Start, angles[index]);
            else arcs.Add((winner, angles[index - 1], angles[index]));
        }
        if (arcs.Count > 1 && arcs[0].Circle == arcs[^1].Circle)
        {
            var wrap = (arcs[0].Circle, arcs[^1].Start, arcs[0].End + 2 * Math.PI);
            arcs.RemoveAt(arcs.Count - 1); arcs.RemoveAt(0); arcs.Add(wrap);
        }
        var segments = new List<EditBoundarySegment>();
        for (int index = 0; index < arcs.Count; index++)
        {
            var arc = arcs[index]; var next = arcs[(index + 1) % arcs.Count];
            var start = EditBoundaryMath.CirclePoint(arc.Circle, arc.Start); var end = EditBoundaryMath.CirclePoint(arc.Circle, arc.End);
            segments.Add(new(start, end, arc.Circle, arc.Start, arc.End - arc.Start));
            var nextStart = EditBoundaryMath.CirclePoint(next.Circle, next.Start);
            if (end.DistanceTo(nextStart) > 1e-10) segments.Add(new(end, nextStart));
        }
        return new(originals.ToArray(), segments);
    }
}

internal static class EditBoundaryMath
{
    internal static double Dot(EditBoundaryPoint a, EditBoundaryPoint b) => a.X * b.X + a.Y * b.Y;
    internal static double Cross(EditBoundaryPoint a, EditBoundaryPoint b) => a.X * b.Y - a.Y * b.X;
    internal static EditBoundaryPoint Unit(double angle)
    {
        // Preserve exact axis contacts instead of manufacturing a microscopic
        // tangent segment from sin(π) or cos(3π/2) round-off.
        double quadrant=angle/(Math.PI/2);
        if(quadrant==Math.Truncate(quadrant))return (((int)(quadrant%4)+4)%4) switch
        {0=>new(1,0),1=>new(0,1),2=>new(-1,0),_=>new(0,-1)};
        return new(Math.Cos(angle), Math.Sin(angle));
    }
    internal static EditBoundaryPoint CirclePoint(EditCircle circle, double angle) =>
        new EditBoundaryPoint(circle.CenterX, circle.CenterY) + Unit(angle) * circle.RadiusMetres;
    internal static double PositiveAngle(double angle)
    { angle %= 2 * Math.PI; return angle < 0 ? angle + 2 * Math.PI : angle; }
    internal static bool AngleOnArc(double angle, EditBoundarySegment arc, double tolerance = 1e-12)
    {
        double offset = PositiveAngle(angle - arc.StartAngle);
        return arc.SweepRadians >= 2 * Math.PI - tolerance || offset <= arc.SweepRadians + tolerance ||
            offset >= 2 * Math.PI - tolerance;
    }

    internal static IReadOnlyList<EditBoundaryPoint> Intersections(EditBoundarySegment a, EditBoundarySegment b, double tolerance)
        => SupportIntersections(a, b, tolerance).Where(point => OnSegment(point, a, tolerance) && OnSegment(point, b, tolerance)).Distinct().ToArray();

    internal static IReadOnlyList<EditBoundaryPoint> SupportIntersections(EditBoundarySegment a, EditBoundarySegment b, double tolerance)
    {
        var candidates = new List<EditBoundaryPoint>();
        if (a.Circle is { } ac && b.Circle is { } bc)
        {
            var delta = new EditBoundaryPoint(bc.CenterX - ac.CenterX, bc.CenterY - ac.CenterY); double distance = Math.Sqrt(delta.LengthSquared);
            if (distance <= tolerance && Math.Abs(ac.RadiusMetres - bc.RadiusMetres) <= tolerance)
                candidates.AddRange(new[] { a.Start, a.End, b.Start, b.End });
            else if (distance > 0 && distance <= ac.RadiusMetres + bc.RadiusMetres + tolerance &&
                     distance >= Math.Abs(ac.RadiusMetres - bc.RadiusMetres) - tolerance)
            {
                double along = (ac.RadiusMetres * ac.RadiusMetres - bc.RadiusMetres * bc.RadiusMetres + distance * distance) / (2 * distance);
                double perpendicular = Math.Sqrt(Math.Max(0, ac.RadiusMetres * ac.RadiusMetres - along * along));
                var midpoint = new EditBoundaryPoint(ac.CenterX, ac.CenterY) + delta * (along / distance);
                var offset = new EditBoundaryPoint(-delta.Y, delta.X) * (perpendicular / distance);
                candidates.Add(midpoint + offset); candidates.Add(midpoint - offset);
            }
        }
        else if (a.Circle != null || b.Circle != null)
        {
            var circle = a.Circle ?? b.Circle!; var line = a.Circle == null ? a : b;
            var delta = line.End - line.Start; double length = Math.Sqrt(delta.LengthSquared);
            if (length <= 1e-12) candidates.Add(line.Start);
            else
            {
                var direction = delta * (1 / length); var centre = new EditBoundaryPoint(circle.CenterX, circle.CenterY);
                var foot = line.Start + direction * Dot(centre - line.Start, direction);
                double square = circle.RadiusMetres * circle.RadiusMetres - (foot - centre).LengthSquared;
                if (square >= -tolerance * (2 * circle.RadiusMetres + tolerance))
                {
                    var offset = direction * Math.Sqrt(Math.Max(0, square));
                    candidates.Add(foot - offset); candidates.Add(foot + offset);
                }
            }
        }
        else
        {
            var da = a.End - a.Start; var db = b.End - b.Start; double cross = Cross(da, db);
            if (Math.Abs(cross) > 1e-18) candidates.Add(a.Start + da * (Cross(b.Start - a.Start, db) / cross));
            else candidates.AddRange(new[] { a.Start, a.End, b.Start, b.End });
        }
        return candidates;
    }

    internal static bool OnSegment(EditBoundaryPoint point, EditBoundarySegment segment, double tolerance)
    {
        if (segment.Circle is { } circle)
            return Math.Abs(point.DistanceTo(new(circle.CenterX, circle.CenterY)) - circle.RadiusMetres) <= tolerance &&
                AngleOnArc(Math.Atan2(point.Y - circle.CenterY, point.X - circle.CenterX), segment, tolerance / circle.RadiusMetres);
        var delta = segment.End - segment.Start; double lengthSquared = delta.LengthSquared;
        double t = lengthSquared == 0 ? 0 : Math.Clamp(Dot(point - segment.Start, delta) / lengthSquared, 0, 1);
        return point.DistanceTo(segment.Start + delta * t) <= tolerance;
    }
}
