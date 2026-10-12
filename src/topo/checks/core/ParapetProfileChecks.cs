using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class ParapetProfileChecks
{
    internal sealed record Case(string Name, PathsD Roof, double Width, double Area);
    internal static IEnumerable<Case> Cases()
    {
        yield return new("rectangle", ContourPaths.Rectangle(0, 0, 10, 8), .3, 10.44);
        var courtyard = ContourPaths.Rectangle(3, 3, 7, 7); courtyard[0].Reverse();
        yield return new("courtyard", new(ContourPaths.Rectangle(0, 0, 10, 10).Concat(courtyard)), .3, 16.8);
        // A courtyard touching the exterior is split for building output. The
        // roof-edge band must not grow along those artificial split seams.
        var touchingHole = new PathD { new(3, 0), new(1, 2), new(3, 4), new(5, 2) };
        double radius = 2 + Math.Round(.3 * Math.Sqrt(2), 6);
        yield return new("touching-courtyard", new() {
            new() { new(0, 0), new(3, 0), new(6, 0), new(6, 6), new(0, 6) }, touchingHole }, .3,
            28 - 5.4 * 5.4 + 2 * radius * radius - Math.Pow(radius - 1.7, 2));
        yield return new("L", new() { new() { new(0, 0), new(6, 0), new(6, 2), new(2, 2), new(2, 6), new(0, 6) } }, .3, 6.84);
        yield return new("narrow", ContourPaths.Rectangle(0, 0, 4, .4), .3, 1.6);
        yield return new("whole-roof", ContourPaths.Rectangle(0, 0, 10, 8), 20, 80);
        yield return new("thin-band", ContourPaths.Rectangle(0, 0, 10, 8), .01, .3596);
        yield return new("point-contact", new(ContourPaths.Rectangle(0, 0, 2, 2)
            .Concat(ContourPaths.Rectangle(2, 2, 4, 4))), .3, 4.08);
        yield return new("disconnected", new(ContourPaths.Rectangle(0, 0, 2, 2)
            .Concat(ContourPaths.Rectangle(4, 0, 6, 2))), .3, 4.08);
        yield return new("split-inset", new() { new() { new(0, 0), new(3, 0), new(3, 1.3), new(5, 1.3),
            new(5, 0), new(8, 0), new(8, 3), new(5, 3), new(5, 1.7), new(3, 1.7), new(3, 3), new(0, 3) } }, .3, 7.28);
    }

    internal static int Run()
    {
        int checks = 0;
        foreach (var example in Cases())
        foreach (bool reverse in new[] { false, true })
        foreach (bool rotate in new[] { false, true })
        foreach (double shift in new[] { 0d, 10000d })
        {
            var roof = new PathsD(example.Roof.Select(path => new PathD(path.Select(p =>
                rotate ? new PointD(shift - p.y, shift + p.x) : new PointD(shift + p.x, shift + p.y)))));
            if (reverse) foreach (var path in roof) path.Reverse();
            var before = roof.SelectMany(path => path).Select(p => (p.x, p.y)).ToArray();
            var profiles = ParapetProfiles.Create(roof, example.Width);
            var output = new PathsD(profiles.SelectMany(profile => profile));
            Require(Math.Abs(ContourPaths.Area(output) - example.Area) < 1e-7, example.Name + " analytic area");
            Require(ContourPaths.Area(ContourPaths.Difference(output, roof)) < 1e-8, example.Name + " stays inside roof and outside courtyards");
            Require(profiles.All(profile => profile.Count > 0 && Clipper.Area(profile[0]) > 0
                && profile.Skip(1).All(hole => Clipper.Area(hole) < 0) && !ProfileContact.TryFind(profile, out _)), example.Name + " simple owned profiles");
            Require(before.SequenceEqual(roof.SelectMany(path => path).Select(p => (p.x, p.y))), "roof input is not mutated");
        }
        // A wrong width changes the independently known result; the area oracle
        // must not simply accept every valid band.
        Require(Math.Abs(ContourPaths.Area(new(ParapetProfiles.Create(ContourPaths.Rectangle(0, 0, 10, 8), .2)
            .SelectMany(profile => profile))) - 10.44) > 1, "different width is detected by the area oracle");
        return checks;
        void Require(bool value, string label) { if (!value) throw new Exception("Parapet: " + label); checks++; }
    }
}
