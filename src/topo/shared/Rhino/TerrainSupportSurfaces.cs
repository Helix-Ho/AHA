using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Unlowered support surfaces borrowed within one model preparation.</summary>
internal sealed class TerrainSupportSurfaces(TerrainField field) : IDisposable
{
    private readonly Dictionary<(int Column,int Row,int Columns,int Rows),NurbsSurface> surfaces = new();
    private bool disposed;

    internal TerrainField Field { get; } = field;

    /// <summary>The caller must not transform or dispose the borrowed support.</summary>
    internal NurbsSurface Get(int column, int row, int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var key = (column,row,columns,rows);
        if (!surfaces.TryGetValue(key, out var surface))
        {
            surface = TerrainSurfaceGeometry.CreatePatch(Field,column,row,columns,rows);
            surfaces.Add(key,surface);
        }
        return surface;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var surface in surfaces.Values) surface.Dispose();
        surfaces.Clear();
    }
}
