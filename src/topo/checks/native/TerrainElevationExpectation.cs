using Rhino.Geometry;

namespace Aha.Topo.Checks;

/// <summary>Continuous terrain oracle shared by surface and Near solid checks.</summary>
internal sealed class TerrainElevationExpectation(TerrainField field)
{
    internal double Error(Point3d pointMetres, double roadDrop) =>
        Math.Abs(pointMetres.Z - field.SampleModelLocal(pointMetres.X, pointMetres.Y) + roadDrop);
}
