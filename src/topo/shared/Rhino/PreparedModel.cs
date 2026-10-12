using Rhino;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Copied on the document thread; calculation never consults a live document.</summary>
public sealed record ModelContext(UnitSystem Units, double AbsoluteTolerance, double RelativeTolerance,
    double AngleToleranceRadians, double UnitsPerMetre, string RenderMeshingParameters)
{
    public static ModelContext Capture(RhinoDoc doc)
    {
        using var meshing = doc.GetMeshingParameters(doc.MeshingParameterStyle);
        return new(doc.ModelUnitSystem, doc.ModelAbsoluteTolerance, doc.ModelRelativeTolerance,
            doc.ModelAngleToleranceRadians, ModelCoordinates.MetresToDocumentUnits(doc), meshing.ToEncodedString());
    }

    public bool Matches(RhinoDoc doc) => Units == doc.ModelUnitSystem &&
        AbsoluteTolerance == doc.ModelAbsoluteTolerance && RelativeTolerance == doc.ModelRelativeTolerance &&
        AngleToleranceRadians == doc.ModelAngleToleranceRadians;
}

/// <summary>Owns detached geometry until it is committed or discarded. No document or UI references.</summary>
public sealed class PreparedModel<T> : IDisposable
{
    private readonly ModelResult model;
    private readonly IReadOnlyList<ModelDocumentObject> objects;
    private readonly IReadOnlyList<string> paths;
    private readonly IReadOnlyDictionary<string, string> tags;
    private readonly Func<ModelResult, T> result;
    private readonly bool hideXY;
    private bool disposed;
    private bool committed;

    internal PreparedModel(ModelContext context, ModelResult model, IReadOnlyList<ModelDocumentObject> objects,
        IReadOnlyList<string> paths, IReadOnlyDictionary<string, string> tags, Func<ModelResult, T> result,
        bool hideXY = true, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        ct.ThrowIfCancellationRequested();
        ModelDocumentWriter.Validate(objects, ct);
        progress?.Report("화면 표시용 Mesh 준비 중…");
        PrepareDisplayMeshes(context, objects, ct);
        Context = context; this.model = model; this.objects = objects; this.paths = paths;
        this.tags = tags; this.result = result; this.hideXY = hideXY;
    }

    private static void PrepareDisplayMeshes(ModelContext context, IReadOnlyList<ModelDocumentObject> objects,
        CancellationToken ct)
    {
        MeshingParameters? parameters = null;
        try
        {
            MeshingParameters Parameters() => parameters ??= MeshingParameters.FromEncodedString(context.RenderMeshingParameters)
                ?? throw new ArgumentException("저장된 Rhino 표시망 설정을 읽을 수 없습니다.");
            foreach (var item in objects)
            {
                ct.ThrowIfCancellationRequested();
                // Rhino discards incoming Extrusion render caches on AddExtrusion.
                // Prepare only caches that the document can preserve; Rhino owns
                // the extrusion's first-display mesh after object registration.
                if (item.Geometry is Brep brep)
                {
                    foreach (var face in brep.Faces)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (face.GetMesh(MeshType.Render) is not null) continue;
                        // Preserve custom terrain caches. Mesh only a face that has none.
                        using var single = face.DuplicateFace(false);
                        using var mesh = MeshBrep(single, Parameters(), ct);
                        if (!mesh.IsValid || mesh.Faces.Count == 0 || !face.SetMesh(MeshType.Render, mesh))
                            throw new InvalidOperationException("준비한 NURBS 면의 표시망을 만들지 못했습니다: " + item.Name);
                    }
                }
            }
        }
        finally { parameters?.Dispose(); }
    }

    private static Mesh MeshBrep(Brep brep, MeshingParameters parameters, CancellationToken ct)
    {
        var parts = Mesh.CreateFromBrep(brep, parameters);
        var mesh = new Mesh();
        try
        {
            ct.ThrowIfCancellationRequested();
            if (parts is not null) foreach (var part in parts) mesh.Append(part);
            return mesh;
        }
        catch { mesh.Dispose(); throw; }
        finally { if (parts is not null) foreach (var part in parts) part.Dispose(); }
    }

    public ModelContext Context { get; }
    public int ObjectCount => objects.Count;
    public IReadOnlyDictionary<string, int> ObjectCounts => objects.GroupBy(item => item.Tags.GetValueOrDefault("AHA.Role", "Reference"))
        .ToDictionary(group => group.Key, group => group.Count());
    public T Details => result(model);
    internal IReadOnlyList<ModelDocumentObject> Objects => objects;

    public T Commit(RhinoDoc doc)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (committed) return Details;
        if (!Context.Matches(doc)) throw new InvalidOperationException("연산 이후 대상 Rhino 문서의 단위 또는 허용오차가 변경되었습니다. 다시 연산하세요.");
        ModelDocumentWriter.Commit(doc, model, objects, paths, tags, hideXY, validated: true);
        committed = true;
        return Details;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (GeometryBase geometry in objects.Select(item => item.Geometry).Distinct()) geometry.Dispose();
    }
}
