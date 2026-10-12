using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class AreaIntegralChecks
{
    internal static void Run()
    {
        foreach (double scale in new[] { 1d, 1000d })
        foreach (double offset in new[] { 0d, 100000d })
        {
            // Rectangle plus a quadratic arch: its exact doubled area is
            // 2*w*h + 4*w*b/3. A tiny adjacent line reproduces the original
            // unequal knot intervals without relying on a geographic location.
            const double w = 100, h = 40, b = 6;
            var origin = new Point3d(offset * scale, -offset * scale, 0);
            Point3d P(double x, double y) => origin + new Vector3d(x * scale, y * scale, 0);
            using var outline = new PolyCurve();
            var points = new[] { P(0, 0), P(50, 0), P(50.0000004, 0), P(w, 0), P(w, h) };
            for (int i = 1; i < points.Length; i++)
            {
                using var line = new LineCurve(points[i - 1], points[i]);
                outline.AppendSegment(line);
            }
            using var arch = new BezierCurve(new[] { P(w, h), P(2 * w / 3, h + 4 * b / 3), P(w / 3, h + 4 * b / 3), P(0, h) });
            using var archCurve = arch.ToNurbsCurve();
            outline.AppendSegment(archCurve);
            using var last = new LineCurve(P(0, h), P(0, 0));
            outline.AppendSegment(last);
            using var curve = outline.ToNurbsCurve();
            double expected = (2 * w * h + 4 * w * b / 3) * scale * scale;
            for (int direction = 0; direction < 2; direction++)
            {
                double actual = NearTerrainCap.SignedAreaIntegral(curve, origin);
                if (!double.IsFinite(actual) || Math.Abs(actual - expected) > Math.Abs(expected) * 1e-10)
                    throw new InvalidOperationException("Area integration changed with units, translation or boundary orientation.");
                curve.Reverse(); expected = -expected;
            }
            CheckSegmentedCap(outline, P(20, 20), expected / 2, scale);
        }
    }

    private static void CheckSegmentedCap(PolyCurve outline, Point3d holeCenter, double outerArea, double scale)
    {
        const double holeRadius = 4;
        double bottom = -30 * scale;
        var segments = outline.DuplicateSegments();
        using var hole = new Circle(holeCenter, holeRadius * scale).ToNurbsCurve();
        hole.Reverse();
        var sources = segments.Append<Curve>(hole).ToArray();
        try
        {
            foreach (var source in sources) source.Translate(0, 0, bottom);
            var before = sources.ToDictionary(source => source, source => source.DataCRC(0));
            // Input order differs from Brep's outer-first order; the returned
            // map must retain the caller's loop and source-profile identities.
            IReadOnlyList<Curve>[] loops = { new Curve[] { hole }, segments };
            using var cap = NearTerrainCap.CreateSegmentedPlanarCap(loops, bottom, .001 * scale, out var edgeIndices);
            if (!cap.IsValid || cap.Faces.Count != 1 || cap.Loops.Count != 2 || cap.Edges.Count != sources.Length
                || cap.Vertices.Count != sources.Length)
                throw new InvalidOperationException("Segmented cap lost its shared closed-loop topology.");
            if (edgeIndices.Length != loops.Length || edgeIndices.SelectMany(loop => loop).Distinct().Count() != sources.Length)
                throw new InvalidOperationException("Segmented cap lost its input-to-edge mapping.");
            for (int loopIndex = 0; loopIndex < loops.Length; loopIndex++)
            {
                var loop = loops[loopIndex];
                if (edgeIndices[loopIndex].Length != loop.Count)
                    throw new InvalidOperationException("Cap edge mapping does not match the source loop.");
                for (int i = 0; i < loop.Count; i++)
                {
                    var edge = cap.Edges[edgeIndices[loopIndex][i]];
                    var next = cap.Edges[edgeIndices[loopIndex][(i + 1) % loop.Count]];
                    if (edge.EndVertex.VertexIndex != next.StartVertex.VertexIndex)
                        throw new InvalidOperationException("Cap segments do not reuse their adjacent seam vertex.");
                    if (loop[i].DataCRC(0) != before[loop[i]] || edge.EdgeCurve.DataCRC(0) != before[loop[i]])
                        throw new InvalidOperationException("Cap boundary changed or mismatched a source wall profile instead of copying it.");
                }
            }
            var normal = cap.Faces[0].NormalAt(cap.Faces[0].Domain(0).Mid, cap.Faces[0].Domain(1).Mid);
            if (normal.Z > -.999999)
                throw new InvalidOperationException("Cap orientation must point down.");
            using var metric = cap.DuplicateBrep();
            metric.Translate(-holeCenter.X, -holeCenter.Y, -bottom);
            metric.Transform(Transform.Scale(Point3d.Origin, 1 / scale));
            using var area = AreaMassProperties.Compute(metric);
            double expected = outerArea / (scale * scale) - Math.PI * holeRadius * holeRadius;
            if (area is null || Math.Abs(area.Area - expected) > expected * 1e-10)
                throw new InvalidOperationException("Segmented cap area changed with units, translation or a hole.");
            var atHole = new Point3d(holeCenter.X, holeCenter.Y, bottom);
            if (!cap.Faces[0].ClosestPoint(atHole, out double u, out double v)
                || cap.Faces[0].IsPointOnFace(u, v) != PointFaceRelation.Exterior)
                throw new InvalidOperationException("Segmented cap filled the source inner loop.");
        }
        finally { foreach (var segment in segments) segment.Dispose(); }
    }
}
