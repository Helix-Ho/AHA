using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>One owned piece of directly generated terrain geometry.</summary>
public sealed record SurfacePiece(GeometryBase Geometry, string Layer, string Role, string Zone);

public sealed record NearSolidResult(IReadOnlyList<SurfacePiece> Pieces, double BottomElevationMetres, int SolidCount);
