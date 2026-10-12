using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class ProfileContactChecks
{
    internal static int Run()
    {
        int checks = 0;
        foreach (int turn in Enumerable.Range(0, 8))
        foreach (double offset in new[] { 0d, 10000d })
        {
            double angle = turn * Math.PI / 4;
            PathsD Transform(PathsD paths) => new(paths.Select(p => new PathD(p.Select(v => new PointD(
                Math.Round(offset + v.x * Math.Cos(angle) - v.y * Math.Sin(angle), 6),
                Math.Round(offset + v.x * Math.Sin(angle) + v.y * Math.Cos(angle), 6))))));
            var outer = Transform(ContourPaths.Rectangle(-10, -10, 10, 10));
            var holes = Transform(new(ContourPaths.Rectangle(-4, -4, 0, 0).Concat(ContourPaths.Rectangle(0, 0, 4, 4))));
            var input = ContourPaths.Difference(outer, holes);
            var queue = new Queue<PathsD>(ContourPaths.Components(input));
            var output = new PathsD();
            while (queue.TryDequeue(out var component))
            {
                if (!ProfileContact.TryFind(component, out var point)) { output.AddRange(component); continue; }
                foreach (var domain in ProfileContact.Domains(component, point))
                foreach (var part in ContourPaths.Components(ContourPaths.Intersect(component, domain)))
                {
                    // Splitting through void sectors must resolve the contact,
                    // not repeatedly bisect the same material fan.
                    if (ProfileContact.TryFind(part, out var remaining) && PlanarTriangulation.GridKey(remaining) == PlanarTriangulation.GridKey(point))
                        throw new InvalidOperationException("profile contact survives its angular partition");
                    queue.Enqueue(part);
                }
            }
            var union = ContourPaths.Union(output);
            double difference = ContourPaths.Area(ContourPaths.Difference(union, input)) + ContourPaths.Area(ContourPaths.Difference(input, union));
            if (difference > 1e-8 || Math.Abs(ContourPaths.Area(union) - ContourPaths.Area(input)) > 1e-7)
                throw new InvalidOperationException("profile contact partition loses material or fills its holes");
            checks++;
        }
        return checks;
    }
}
