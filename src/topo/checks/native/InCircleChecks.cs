using NetTopologySuite.Geometries;

namespace Aha.Topo.Checks;

internal static class InCircleChecks
{
    internal static void Run()
    {
        if (FixtureData.Recorded)
        {
            var points = FixtureData.Witness("incircle").EnumerateArray()
                .Select(point => new Coordinate(point[0].GetDouble(), point[1].GetDouble())).ToArray();
            for (int i = 0; i < 4; i++)
                Require(!PlanarSurfaceMesh.InCircle(points[i], points[(i+1)%4], points[(i+2)%4], points[(i+3)%4]),
                    "Recorded cocircular input must preserve either diagonal.");
        }
        foreach (double offset in new[] { 0d, 100_000d })
        {
            var a = new Coordinate(offset, offset); var b = new Coordinate(offset + 1, offset);
            var c = new Coordinate(offset + 1, offset + 1);
            Require(!PlanarSurfaceMesh.InCircle(a, b, c, new(offset, offset + 1)), "Square is cocircular.");
            Require(PlanarSurfaceMesh.InCircle(a, b, c, new(offset, Math.BitDecrement(offset + 1))), "One ULP inside must be inside.");
            Require(!PlanarSurfaceMesh.InCircle(a, b, c, new(offset, Math.BitIncrement(offset + 1))), "One ULP outside must be outside.");
            Require(PlanarSurfaceMesh.InCircle(a, b, c, new(offset + .5, offset + .5)), "Centre is inside.");
        }
    }
    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
