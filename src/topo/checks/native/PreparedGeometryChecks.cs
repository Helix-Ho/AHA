using System.Diagnostics;
using Rhino;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class PreparedGeometryChecks
{
    internal static Dictionary<string, object?> Run()
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        doc.ModelUnitSystem = UnitSystem.Meters;
        doc.ModelAbsoluteTolerance = .001;
        doc.UndoRecordingEnabled = true;
        using var originalParameters = new MeshingParameters { GridMinCount = 8, GridMaxCount = 64 };
        doc.SetCustomMeshingParameters(originalParameters);
        doc.MeshingParameterStyle = MeshingParameterStyle.Custom;
        var context = ModelContext.Capture(doc);
        using var changedParameters = new MeshingParameters(originalParameters) { GridMinCount = 32 };
        doc.SetCustomMeshingParameters(changedParameters);
        using var capturedParameters = MeshingParameters.FromEncodedString(context.RenderMeshingParameters)!;
        bool copiedParameters = capturedParameters.GridMinCount == 8 && capturedParameters.GridMaxCount == 64
            && capturedParameters.Equals(originalParameters);
        doc.SetCustomMeshingParameters(originalParameters);

        using var outer = new Circle(Plane.WorldXY, 5).ToNurbsCurve();
        using var hole = new Circle(Plane.WorldXY, 2).ToNurbsCurve();
        hole.Reverse();
        using var extrusion = Extrusion.Create(outer, Plane.WorldXY, 4, true)
            ?? throw new InvalidOperationException("Cannot create the prepared-mesh extrusion input.");
        if (!extrusion.AddInnerProfile(hole)) throw new InvalidOperationException("Cannot add the prepared-mesh hole.");
        using var brep = new Box(Plane.WorldXY, new Interval(12, 16), new Interval(0, 3), new Interval(0, 2)).ToBrep();
        using var face = brep.Faces[0].DuplicateFace(false);
        var faceMeshes = Mesh.CreateFromBrep(face, capturedParameters);
        using var custom = new Mesh();
        try { foreach (var mesh in faceMeshes) custom.Append(mesh); }
        finally { foreach (var mesh in faceMeshes) mesh.Dispose(); }
        custom.VertexColors.CreateMonotoneMesh(System.Drawing.Color.CornflowerBlue);
        if (!brep.Faces[0].SetMesh(MeshType.Render, custom)) throw new InvalidOperationException("Cannot attach custom face mesh.");
        uint cachedCrc = brep.Faces[0].GetMesh(MeshType.Render)!.DataCRC(0);
        using var standalone = Mesh.CreateFromBox(new BoundingBox(20, 0, 0, 21, 1, 1), 1, 1, 1);
        uint standaloneCrc = standalone.DataCRC(0);
        bool initiallyMissing = brep.Faces.Skip(1).Any(f => f.GetMesh(MeshType.Render) is null);
        var objects = new[]
        {
            new ModelDocumentObject(extrusion, "Near::Buildings", "annular extrusion", new Dictionary<string, string> { ["AHA.Role"] = "Building" }),
            new ModelDocumentObject(brep, "Far::Buildings", "brep with one custom cache", new Dictionary<string, string> { ["AHA.Role"] = "Building" }),
            new ModelDocumentObject(standalone, "Far::Buildings", "standalone mesh", new Dictionary<string, string> { ["AHA.Role"] = "Building" })
        };
        var timer = Stopwatch.StartNew();
        using var prepared = new PreparedModel<ModelResult>(context, new ModelResult { LayerName = ModelCoordinates.NewLayerName() }, objects,
            new[] { "Near::Buildings", "Far::Buildings" }, new Dictionary<string, string>(), value => value);
        timer.Stop();
        double displayPreparationSeconds = timer.Elapsed.TotalSeconds;
        bool meshReady = brep.Faces.All(f => f.GetMesh(MeshType.Render) is { IsValid: true, Faces.Count: > 0 });
        bool preservedCaches = brep.Faces[0].GetMesh(MeshType.Render)!.DataCRC(0) == cachedCrc
            && standalone.DataCRC(0) == standaloneCrc;
        using var annularBrep = extrusion.ToBrep();
        using var volume = VolumeMassProperties.Compute(annularBrep);
        bool exactVolume = volume is not null && Math.Abs(volume.Volume - Math.PI * 21 * 4) < 1e-4;
        var committed = prepared.Commit(doc);
        prepared.Dispose();
        bool released = extrusion.Disposed && brep.Disposed && standalone.Disposed;
        var live = committed.ObjectIds.Select(id => doc.Objects.FindId(id)!).ToArray();
        var liveBrep = live.Single(item => item.Geometry is Brep);
        bool addedCaches = ((Brep)liveBrep.Geometry).Faces.All(f => f.GetMesh(MeshType.Render) is { IsValid: true, Faces.Count: > 0 })
            && ((Brep)liveBrep.Geometry).Faces[0].GetMesh(MeshType.Render)!.DataCRC(0) == cachedCrc;
        var liveExtrusion = live.Single(item => item.Geometry is Extrusion);
        // Rhino's AddExtrusion discards detached caches. Verify its own first
        // display path independently, without adding it to the creation cost.
        timer.Restart();
        int nativeCount = liveExtrusion.CreateMeshes(MeshType.Render, capturedParameters, false);
        timer.Stop();
        double nativeDisplaySeconds = timer.Elapsed.TotalSeconds;
        var nativeMeshes = liveExtrusion.GetMeshes(MeshType.Render);
        bool nativeValid = nativeCount > 0 && nativeMeshes.Length > 0
            && nativeMeshes.All(mesh => mesh.IsValid && mesh.Faces.Count > 0);
        bool holePreserved = nativeValid && nativeMeshes.All(mesh =>
            Rhino.Geometry.Intersect.Intersection.MeshLine(mesh,
                new Line(new Point3d(0, 0, -1), new Point3d(0, 0, 5))) is { Length: 0 });
        bool saved = false;
        string path = Path.Combine(Path.GetTempPath(), "aha-prepared-geometry-" + Guid.NewGuid().ToString("N") + ".3dm");
        try
        {
            if (!doc.WriteFile(path, new Rhino.FileIO.FileWriteOptions { IncludeRenderMeshes = true }))
                throw new InvalidOperationException("Cannot save the prepared geometry check.");
            using var reopened = Rhino.FileIO.File3dm.Read(path)
                ?? throw new InvalidOperationException("Cannot reopen the prepared geometry check.");
            var stored = reopened.Objects.Single(item => item.Attributes.Name == "annular extrusion").Geometry as Extrusion;
            using var storedBrep = stored?.ToBrep();
            using var storedVolume = storedBrep is null ? null : VolumeMassProperties.Compute(storedBrep);
            saved = stored is { IsValid: true, IsSolid: true, ProfileCount: 2 }
                && storedVolume is not null && Math.Abs(storedVolume.Volume - Math.PI * 21 * 4) < 1e-4
                && stored.GetMesh(MeshType.Render) is { IsValid: true, Faces.Count: > 0 };
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        using var cancelledGeometry = new Box(Plane.WorldXY, new Interval(0, 2), new Interval(0, 2), new Interval(0, 2)).ToBrep();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool cancelled = false;
        try
        {
            using var cancelledResult = new PreparedModel<ModelResult>(context, new ModelResult(),
                new[] { new ModelDocumentObject(cancelledGeometry, "Buildings", "cancelled", new Dictionary<string, string>()) },
                Array.Empty<string>(), new Dictionary<string, string>(), value => value, ct: cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = cancelledGeometry.Faces.All(face => face.GetMesh(MeshType.Render) is null); }
        using var retraced=new PolyCurve();
        retraced.Append(new LineCurve(new Point3d(0,0,0),new Point3d(10,0,0)));
        retraced.Append(new LineCurve(new Point3d(10,0,0),new Point3d(0,0,0)));
        using var degenerate=Extrusion.Create(retraced,Plane.WorldXY,1,true);
        bool degenerateRejected=degenerate is null;
        if(degenerate is not null)
            try { ModelDocumentWriter.ValidateSolid(degenerate,"retraced regression"); }
            catch(InvalidOperationException) { degenerateRejected=true; }
        bool wrongVolumeRejected = false;
        bool missingHoleRejected = false;
        using (var square = new PolylineCurve(new[] { new Point3d(0,0,0), new Point3d(2,0,0), new Point3d(2,2,0), new Point3d(0,2,0), new Point3d(0,0,0) }))
        using (var wrongHeight = Extrusion.Create(square, 2, true))
            try { ModelDocumentWriter.ValidateSolid(wrongHeight, "wrong height", new SolidExpectation(4, 1e-6)); }
            catch (InvalidOperationException) { wrongVolumeRejected = true; }
        using (var missingHole = Extrusion.Create(outer, 4, true))
            try { ModelDocumentWriter.ValidateSolid(missingHole, "missing courtyard", new SolidExpectation(Math.PI * 21 * 4, 1e-4)); }
            catch (InvalidOperationException) { missingHoleRejected = true; }
        return new()
        {
            ["wrong_valid_solid_volume_rejected"] = wrongVolumeRejected,
            ["missing_inner_profile_rejected"] = missingHoleRejected,
            ["retraced_capped_profile_rejected"] = degenerateRejected,
            ["geometry_checks_passed"] = missingHoleRejected && wrongVolumeRejected && degenerateRejected && copiedParameters && initiallyMissing && meshReady && preservedCaches && exactVolume && nativeValid && holePreserved && saved && released && addedCaches && cancelled,
            ["meshing_parameters_copied_from_document"] = copiedParameters,
            ["missing_brep_render_meshes_ready_before_creation"] = initiallyMissing && meshReady,
            ["existing_custom_and_standalone_meshes_preserved"] = preservedCaches,
            ["extrusion_analytical_volume_preserved"] = exactVolume,
            ["native_extrusion_render_mesh_preserves_hole"] = nativeValid && holePreserved,
            ["saved_extrusion_geometry_and_render_mesh_preserved"] = saved,
            ["prepared_geometry_released_after_creation"] = released,
            ["document_brep_render_meshes_survive_prepared_disposal"] = addedCaches,
            ["cancelled_display_preparation_does_not_mesh"] = cancelled,
            ["display_preparation_seconds"] = displayPreparationSeconds,
            ["native_extrusion_display_seconds"] = nativeDisplaySeconds
        };
    }
}
