using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class BuildingContactChecks
{
    internal static void Run()
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var paths = new PathsD(ContourPaths.Rectangle(-10, -10, 10, 10)
            .Concat(ContourPaths.Rectangle(-4, -4, 0, 0)).Concat(ContourPaths.Rectangle(0, 0, 4, 4)));
        var feature = new GeoFeature
        {
            Id = "touching-courtyards",
            Polygons = new() {
            paths.Select(path=>path.Select(p=>projection.ToGeo(p.x,p.y)).ToList()).ToList() }
        };
        foreach (double scale in new[] { 1d, 1000d })
        {
            var objects = BuildingGeometry.CreateEditable(feature, projection, -1, 10, scale, .001 * scale);
            try
            {
                double volume = 0;
                foreach (var geometry in objects)
                {
                    if (geometry is not Extrusion extrusion || !extrusion.IsValid || !extrusion.IsSolid)
                        throw new InvalidOperationException("Building point contact must retain closed lightweight extrusions");
                    using var mass = VolumeMassProperties.Compute(extrusion);
                    volume += mass?.Volume ?? throw new InvalidOperationException("Building contact volume unavailable");
                }
                if (objects.Count < 2 || Math.Abs(volume / Math.Pow(scale, 3) - 3680) > 1e-5)
                    throw new InvalidOperationException("Building contact split changed material or courtyard volume");
            }
            finally { foreach (var geometry in objects) geometry.Dispose(); }
            using var mesh = BuildingGeometry.CreateMesh(feature, projection, -1, 10, scale);
            using var meshMass = VolumeMassProperties.Compute(mesh);
            if (!mesh.IsValid || !mesh.IsClosed || meshMass is null ||
                Math.Abs(meshMass.Volume / Math.Pow(scale, 3) - 3680) > 1e-5)
                throw new InvalidOperationException("Building point contact mesh changed material or courtyard volume");
        }
    }
}
