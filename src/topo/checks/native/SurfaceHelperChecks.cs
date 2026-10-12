using Clipper2Lib;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using System.Text.Json;

namespace Aha.Topo.Checks;

internal static class SurfaceHelperChecks
{
    internal static Dictionary<string, object?> Run(string directory)
    {
        AreaIntegralChecks.Run();
        InCircleChecks.Run();
        BuildingContactChecks.Run();
        ContourOverlayChecks.Run();
        int buildingChecks = BuildingMeshChecks.CheckSynthetic();
        int sourcePrecisionChecks = BuildingMeshChecks.CheckSourcePrecision();
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var field = TerrainField.FromSamples(projection.Bounds(10), projection, 1, 1, new double[] { 0, 0, 0, 10 });
        using var surface = TerrainSurfaceGeometry.CreatePatch(field, 0, 0, 1, 1);
        double fieldError = 0, xyError = 0;
        for (int x = 0; x <= 10; x++) for (int y = 0; y <= 10; y++)
        {
            double px = field.ModelX(0) + (field.ModelX(1) - field.ModelX(0)) * x / 10.0,
                py = field.ModelY(0) + (field.ModelY(1) - field.ModelY(0)) * y / 10.0;
            var point = surface.PointAt(px, py);
            fieldError = Math.Max(fieldError, Math.Abs(point.Z - field.SampleModelLocal(px, py)));
            xyError = Math.Max(xyError, Math.Max(Math.Abs(px - point.X), Math.Abs(py - point.Y)));
        }
        var regions = new PathsD
        {
            new() { new(-8,-8), new(8,-8), new(8,8), new(-8,8) },
            new() { new(-2,-2), new(-2,2), new(2,2), new(2,-2) }
        };
        var trimmed = TerrainSurfaceGeometry.Trim(regions, surface, 0.001);
        bool holeRetained = false, meshCachePersisted = false;
        try
        {
            var brep = trimmed.Single();
            holeRetained = brep.IsValid && brep.Loops.Count == 2
                && brep.Faces[0].IsPointOnFace(0, 0) == PointFaceRelation.Exterior
                && brep.Faces[0].IsPointOnFace(4, 4) == PointFaceRelation.Interior;
            var triangles = PlanarTriangulation.Triangulate(regions);
            using var mesh = new Mesh(); mesh.Vertices.UseDoublePrecisionVertices = true;
            foreach (var tri in triangles)
            {
                int start = mesh.Vertices.Count;
                foreach (var p in tri) mesh.Vertices.Add(p.x, p.y, field.SampleModelLocal(p.x, p.y));
                mesh.Faces.AddFace(start, start + 1, start + 2);
            }
            int expectedRenderFaces = mesh.Faces.Count;
            TerrainSurfaceGeometry.AttachDisplayMesh(brep, mesh);
            string path = Path.Combine(directory, "helper-trimmed-cache.3dm");
            // Use the same document object-copy and writer path as generated site models.
            using (var doc = RhinoDoc.CreateHeadless(null))
            {
                doc.ModelUnitSystem = UnitSystem.Meters;
                if (doc.Objects.AddBrep(brep, null, null, false, false) == Guid.Empty || !doc.Write3dmFile(path,
                    new FileWriteOptions { FileVersion = 8, SuppressDialogBoxes = true, IncludeRenderMeshes = true }))
                    throw new IOException("Helper cache file write failed.");
            }
            using var read = File3dm.Read(path) ?? throw new InvalidDataException("Helper cache file could not be read back.");
            // Rhino 8.35's indexed/CopyTo path returned a null slot for this valid
            // one-object archive. Native enumeration preserves the actual record.
            var records = new List<File3dmObject>();
            foreach (var record in read.Objects) records.Add(record);
            if (records.Count != 1 || records[0] is null)
                throw new InvalidDataException($"Helper cache archive has {records.Count} records or a null record.");
            var restored = records[0].Geometry as Brep ?? throw new InvalidDataException("Helper cache geometry is not a Brep.");
            var restoredMesh = restored.Faces[0].GetMesh(MeshType.Render);
            meshCachePersisted = restored.IsValid && restoredMesh is not null && restoredMesh.Faces.Count == expectedRenderFaces;
        }
        finally { foreach (var brep in trimmed) brep.Dispose(); }
        Point3d At(double x, double y) => new(x, y, field.SampleModelLocal(x, y));
        using var side = TerrainSurfaceGeometry.CreateRoadSide(new[] { (At(-8, -6), At(0, 0), At(8, 6)) }, 0.15);
        double sideMidError = 0;
        int curvedSideEdges = 0;
        foreach (var edge in side.Edges)
        {
            var a = edge.PointAtStart; var b = edge.PointAtEnd;
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 1e-8) continue;
            // Read the actual NURBS top/bottom curves in world coordinates.
            // Inspect the actual top/bottom boundary curves in world coordinates.
            for (int i = 0; i <= 4; i++)
            {
                var point = edge.PointAt(edge.Domain.ParameterAt(i / 4.0));
                double difference = point.Z - field.SampleModelLocal(point.X, point.Y);
                sideMidError = Math.Max(sideMidError, Math.Min(Math.Abs(difference), Math.Abs(difference + 0.15)));
            }
            curvedSideEdges++;
        }
        var planar = ConformingPartition(field);
        var regressions = ConformingRegressions();
        var polygonRegressions = PolygonRegressions();
        var kinkedCaches = KinkedCachePersistence(directory, projection);
        var failedBuildingContracts = FailedBuildingContracts();
        var touchingDisplayLoops = TouchingDisplayLoops();
        var thinInteriorSample = ThinInteriorSample();
        var modelRegularization = ModelPlanNativeChecks.Run();
        var constrainedNormals = ConstrainedNormals();
        var solidOrientations = SolidOrientationRegressions();
        var curvedBoundaries = CurvedBoundaryAccuracy();
        var patchSupports = PatchSupports();
        bool sharedSupports = SharedSupportOwnership();
        bool passed = buildingChecks == 4 && sourcePrecisionChecks == 2 && fieldError < 1e-8 && xyError < 1e-8
            && holeRetained && meshCachePersisted && side.IsValid && curvedSideEdges == 2 && sideMidError < 1e-7 && planar.Passed
            && regressions.All(r => (bool)r["passed"]!) && polygonRegressions.All(r => (bool)r["passed"]!)
            && (bool)kinkedCaches["passed"]!
            && failedBuildingContracts.All(r=>(bool)r["passed"]!)
            && touchingDisplayLoops.All(r=>(bool)r["passed"]!)
            && (bool)thinInteriorSample["passed"]!
            && modelRegularization.All(r=>(bool)r["passed"]!)
            && constrainedNormals.All(r=>(bool)r["passed"]!)
            && solidOrientations.All(r=>(bool)r["passed"]!)
            && curvedBoundaries.All(r=>(bool)r["passed"]!)
            && patchSupports.All(r=>(bool)r["passed"]!)
            && sharedSupports
            ;
        return new()
        {
            ["geometry_checks_passed"] = passed,
            ["building_courtyard_multipart_m_mm_checks"] = buildingChecks,
            ["building_narrow_ear_source_precision_m_mm_checks"] = sourcePrecisionChecks,
            ["nurbs_max_field_error_m"] = fieldError, ["nurbs_max_xy_uv_error_m"] = xyError,
            ["trim_hole_retained"] = holeRetained, ["brep_render_mesh_survived_file_roundtrip"] = meshCachePersisted,
            ["road_side_midpoint_error_m"] = sideMidError,
            ["conforming_partition_passed"] = planar.Passed, ["conforming_partition_triangles"] = planar.Triangles,
            ["conforming_partition_max_field_error_m"] = planar.Error, ["unmatched_interior_edges"] = planar.UnmatchedEdges,
            ["conforming_real_cell_regressions"] = regressions, ["polygon_triangulation_regressions"] = polygonRegressions,
            ["kinked_brep_cache_persistence"] = kinkedCaches,
            ["failed_building_contracts"] = failedBuildingContracts,
            ["point_touching_display_boundary_regressions"] = touchingDisplayLoops,
            ["area_units_translation_parameterization_passed"] = true,
            ["thin_polygon_interior_sample_regression"] = thinInteriorSample,
            ["model_regularization_regressions"] = modelRegularization,
            ["almost_collinear_bilinear_normals"] = constrainedNormals,
            ["independent_native_display_orientation"] = solidOrientations,
            ["curved_boundary_accuracy"] = curvedBoundaries,
            ["nurbs_patch_support_preservation"] = patchSupports,
            ["shared_support_survives_output_transforms_and_releases_independently"] = sharedSupports,
        };
    }

    private static bool SharedSupportOwnership()
    {
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var field=TerrainField.FromSamples(projection.Bounds(10),projection,1,1,new[]{0d,2,4,9});
        var supports=new TerrainSupportSurfaces(field);
        var first=TerrainSurfaceGeometry.Trim(ContourPaths.Rectangle(-8,-8,8,8),supports.Get(0,0,1,1),.001).Single();
        try
        {
            first.Translate(0,0,-.2);first.Transform(Transform.Scale(Point3d.Origin,1000));
            var second=TerrainSurfaceGeometry.Trim(ContourPaths.Rectangle(-6,-6,6,6),supports.Get(0,0,1,1),.001).Single();
            try
            {
                double expected=field.SampleModelLocal(2,3);
                double actual=second.Faces[0].PointAt(2,3).Z;
                supports.Dispose();
                bool rejected=false;
                try{supports.Get(0,0,1,1);}catch(ObjectDisposedException){rejected=true;}
                return rejected&&first.IsValid&&second.IsValid&&Math.Abs(actual-expected)<1e-9&&
                    Math.Abs(second.Faces[0].PointAt(2,3).Z-expected)<1e-9;
            }
            finally{second.Dispose();}
        }
        finally{first.Dispose();supports.Dispose();}
    }

    private static List<Dictionary<string,object?>> SolidOrientationRegressions()
    {
        var results=new List<Dictionary<string,object?>>();
        foreach(double scale in new[]{1d,1000d})
        {
            const double step=1e-6;
            Point3d At(double x,double y)=>new(x*scale,y*scale,30*scale);
            var source=new[]{At(0,0),At(2+.6*step,5+.7*step),At(-2,4.95),At(-5,-2),At(0,-2),At(0,0)};
            using var outline=new PolylineCurve(source);
            var tops=Brep.CreatePlanarBreps(outline,.001*scale);
            if(tops is not {Length:1})throw new InvalidOperationException("Corner orientation fixture needs one planar top.");
            using var top=tops[0];
            using var display=new Mesh();display.Vertices.UseDoublePrecisionVertices=true;
            // A source corner and its fixed-grid display connector have different
            // local tangents even though they describe the same outward boundary.
            foreach(var point in new[]{At(0,0),At(2,5),At(2,5+step),At(-2,4.95),At(-5,-2),At(0,-2)})
                display.Vertices.Add(point);
            for(int i=1;i<5;i++)display.Faces.AddFace(0,i,i+1);
            TerrainSurfaceGeometry.AttachDisplayMesh(top,display);
            var generated=NearTerrainSolid.Build(new[]{new SurfacePiece(top,"Near::Surfaces","Land","Near")},30,scale,.001,true);
            try
            {
                var solid=(Brep)generated.Pieces.Single().Geometry;
                var normal=NearSolidOrientationChecks.Check(solid,scale);
                int nonmatchingTangents=0;
                foreach(var face in solid.Faces)
                {
                    if(Math.Abs(face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid).Z)>1e-8)continue;
                    var mesh=face.GetMesh(MeshType.Render)!;
                    foreach(var triangle in mesh.Faces)
                    {
                        Compare(triangle.A,triangle.B,triangle.C);
                        if(triangle.IsQuad)Compare(triangle.A,triangle.C,triangle.D);
                    }
                    void Compare(int ia,int ib,int ic)
                    {
                        var a=mesh.Vertices.Point3dAt(ia);var b=mesh.Vertices.Point3dAt(ib);var c=mesh.Vertices.Point3dAt(ic);
                        var cross=Vector3d.CrossProduct(b-a,c-a);
                        if(face.ClosestPoint((a+b+c)/3,out double u,out double v)&&face.IsPointOnFace(u,v)==PointFaceRelation.Interior
                            &&cross*face.NormalAt(u,v)<=0)nonmatchingTangents++;
                    }
                }
                using var flippedWall=solid.DuplicateBrep();
                var wall=flippedWall.Faces.First(f=>Math.Abs(f.NormalAt(f.Domain(0).Mid,f.Domain(1).Mid).Z)<1e-8);
                using var reversed=wall.GetMesh(MeshType.Render)!.DuplicateMesh();
                reversed.Flip(true,true,true);
                if(!wall.SetMesh(MeshType.Render,reversed))throw new InvalidOperationException("Cannot set orientation control cache.");
                var wallAudit=NearSolidOrientationChecks.Check(flippedWall,scale);
                using var flippedNative=solid.DuplicateBrep();flippedNative.Flip();
                var nativeAudit=NearSolidOrientationChecks.Check(flippedNative,scale);
                using var flippedDisplay=solid.DuplicateBrep();
                foreach(var face in flippedDisplay.Faces)
                {
                    using var mesh=face.GetMesh(MeshType.Render)!.DuplicateMesh();mesh.Flip(true,true,true);
                    if(!face.SetMesh(MeshType.Render,mesh))throw new InvalidOperationException("Cannot set reversed display control.");
                }
                var displayAudit=NearSolidOrientationChecks.Check(flippedDisplay,scale);
                using var disconnected=solid.DuplicateBrep();
                var carrier=disconnected.Faces[wall.FaceIndex];
                using var joined=carrier.GetMesh(MeshType.Render)!.DuplicateMesh();
                using var island=Mesh.CreateFromBox(new BoundingBox(new Point3d(20*scale,20*scale,10*scale),new Point3d(21*scale,21*scale,11*scale)),1,1,1);
                joined.Append(island);
                if(!carrier.SetMesh(MeshType.Render,joined))throw new InvalidOperationException("Cannot set disconnected display control.");
                var disconnectedTopology=SolidDisplayChecks.Check(disconnected,scale);
                var disconnectedAudit=NearSolidOrientationChecks.Check(disconnected,scale,disconnectedTopology);
                results.Add(new(){["passed"]=normal.Passed&&nonmatchingTangents>0&&!wallAudit.Passed&&!nativeAudit.Passed&&!displayAudit.Passed
                        &&disconnectedTopology.Passed&&disconnectedTopology.ConnectedComponents==2&&!disconnectedAudit.Passed,
                    ["scale"]=scale,["normal"]=normal,["different_corner_tangents"]=nonmatchingTangents,
                    ["reversed_wall_rejected"]=!wallAudit.Passed,["reversed_native_rejected"]=!nativeAudit.Passed,
                    ["reversed_display_rejected"]=!displayAudit.Passed,["disconnected_display_rejected"]=!disconnectedAudit.Passed,
                    ["disconnected_closed_components"]=disconnectedTopology.ConnectedComponents});
            }
            finally { foreach(var piece in generated.Pieces)piece.Geometry.Dispose(); }
        }
        return results;
    }

    private static List<Dictionary<string, object?>> PatchSupports()
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var bounds = projection.Bounds(15);
        var grid = TerrainField.FromSamples(bounds, projection, 6, 6, new double[49]);
        var cases = new (string Name, Func<double, double, double> Height, double? AreaScale)[]
        {
            ("horizontal", (x,y) => 20, 1),
            ("slope", (x,y) => 20 + .25*x - .5*y, Math.Sqrt(1 + .25*.25 + .5*.5)),
            ("bilinear", (x,y) => 20 + .25*x - .5*y + .03125*x*y, null),
            ("kinked", (x,y) => 20 + .25*x - .5*y + .03125*x*y + .75*Math.Abs(x-5) + .5*Math.Abs(y+5), null)
        };
        var results = new List<Dictionary<string, object?>>();
        foreach (var item in cases)
        foreach (var range in new[] { (Column: 0, Row: 0, Columns: 6, Rows: 6), (Column: 1, Row: 2, Columns: 4, Rows: 3) })
        {
            var heights = Enumerable.Range(0, 7).SelectMany(row => Enumerable.Range(0, 7)
                .Select(column => item.Height(grid.ModelX(column), grid.ModelY(row)))).ToArray();
            var field = TerrainField.FromSamples(bounds, projection, 6, 6, heights);
            using var patch = TerrainSurfaceGeometry.CreatePatch(field, range.Column, range.Row, range.Columns, range.Rows);
            double west = field.ModelX(range.Column), east = field.ModelX(range.Column + range.Columns);
            double south = field.ModelY(range.Row), north = field.ModelY(range.Row + range.Rows);
            double heightError = 0, xyError = 0;
            int samples = 0;
            for (int i = 0; i <= 24; i++)
            for (int j = 0; j <= 24; j++)
            {
                double x = west + (east-west)*i/24, y = south + (north-south)*j/24;
                var actual = patch.PointAt(x,y);
                heightError = Math.Max(heightError, Math.Abs(actual.Z-item.Height(x,y)));
                xyError = Math.Max(xyError, Math.Max(Math.Abs(actual.X-x), Math.Abs(actual.Y-y)));
                samples++;
            }
            bool preserved = field.Heights.SequenceEqual(heights) && patch.Domain(0) == new Interval(west,east) &&
                patch.Domain(1) == new Interval(south,north);
            double areaError = 0, areaBudget = 0;
            if (range.Column == 0 && item.AreaScale is { } factor)
            {
                using var circle = new Circle(Plane.WorldXY, 10).ToNurbsCurve();
                using var hole = new PolylineCurve(new[] { new Point3d(-4,-4,0),new Point3d(-4,4,0),
                    new Point3d(4,4,0),new Point3d(4,-4,0),new Point3d(-4,-4,0) });
                using var trimmed = TerrainSurfaceGeometry.Trim(new Curve[] { circle,hole }, patch,.001);
                using var mass = AreaMassProperties.Compute(trimmed,true,false,false,false);
                if (mass is null) areaError = double.PositiveInfinity;
                else areaError = Math.Abs(mass.Area-(100*Math.PI-64)*factor);
                areaBudget = (20*Math.PI+32)*ModelPlanPolicy.PrecisionMetres*factor;
            }
            results.Add(new() { ["case"] = item.Name, ["partial_range"] = range.Column != 0,
                ["passed"] = patch.IsValid && preserved && samples > 0 && heightError < 1e-8 && xyError < 1e-8 && areaError <= areaBudget,
                ["samples"] = samples, ["height_error_m"] = heightError, ["xy_uv_error_m"] = xyError,
                ["source_and_domain_preserved"] = preserved,
                ["native_trim_area_error_m2"] = double.IsFinite(areaError) ? areaError : null,
                ["native_trim_area_budget_m2"] = areaBudget });
        }
        return results;
    }

    private static List<Dictionary<string,object?>> CurvedBoundaryAccuracy()
    {
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        var bounds = projection.Bounds(100);
        var grid = TerrainField.FromSamples(bounds, projection, 4, 5, new double[30]);
        // Absolute-value kinks lie on grid lines. This analytic function is
        // bilinear in each cell and supplies an independent exact UV lift.
        double Height(double x, double y) => 45 + .03*x + .02*y + .002*x*y
            + .65*Math.Abs(x-grid.ModelX(2)) + .45*Math.Abs(y-grid.ModelY(2));
        var heights = Enumerable.Range(0, 6).SelectMany(row => Enumerable.Range(0, 5)
            .Select(col => Height(grid.ModelX(col), grid.ModelY(row)))).ToArray();
        var field = TerrainField.FromSamples(bounds, projection, 4, 5, heights);
        using var patch = TerrainSurfaceGeometry.CreatePatch(field, 0, 0, 4, 5);
        const double centerX = 2.3, centerY = -1.7, radius = 73.4;
        using var circle = new Circle(new Point3d(centerX, centerY, 0), radius).ToNurbsCurve();
        uint patchBefore = patch.DataCRC(0), circleBefore = circle.DataCRC(0);
        using var source = TerrainSurfaceGeometry.Trim(new Curve[] { circle }, patch, .001);
        var results = new List<Dictionary<string,object?>>();
        foreach (double scale in new[] { 1d, 1000d })
        {
            using var actual = source.DuplicateBrep();
            if (!actual.Transform(Transform.Scale(Point3d.Origin, scale)))
                throw new InvalidOperationException("Curved boundary fixture scaling failed.");
            double maximumLiftError = 0, maximumCoverageError = 0, maximumPlanError = 0;
            int samples = 0;
            foreach (var edge in actual.Edges)
            for (int span = 0; span < edge.SpanCount; span++)
            for (int sample = 0; sample <= 16; sample++)
            {
                var point = edge.PointAt(edge.SpanDomain(span).ParameterAt(sample/16d))/scale;
                double angle = Math.Atan2(point.Y-centerY, point.X-centerX);
                var exact = At(angle);
                maximumLiftError = Math.Max(maximumLiftError, point.DistanceTo(exact));
                maximumPlanError = Math.Max(maximumPlanError,
                    Math.Abs(Math.Sqrt(Math.Pow(point.X-centerX,2)+Math.Pow(point.Y-centerY,2))-radius));
                samples++;
            }
            // Check the reverse direction too: an omitted arc must not pass
            // merely because the retained boundary points lie on the surface.
            for (int i = 0; i < 512; i++)
            {
                var exact = At(2*Math.PI*i/512)*scale;
                double error = actual.Edges.Min(edge => edge.ClosestPoint(exact, out double parameter)
                    ? exact.DistanceTo(edge.PointAt(parameter))/scale : double.PositiveInfinity);
                maximumCoverageError = Math.Max(maximumCoverageError, error);
            }
            bool unchanged = patch.DataCRC(0)==patchBefore && circle.DataCRC(0)==circleBefore
                && field.Heights.SequenceEqual(heights);
            results.Add(new() { ["scale"]=scale,
                ["passed"]=actual.IsValid && actual.Faces.Count==1 && actual.Loops.Count==1 && samples>0 && unchanged
                    && maximumLiftError<=.001 && maximumCoverageError<=.001 && maximumPlanError<=.001,
                ["actual_edge_samples"]=samples, ["edge_to_exact_lift_error_m"]=maximumLiftError,
                ["exact_boundary_to_edge_error_m"]=maximumCoverageError, ["plan_circle_error_m"]=maximumPlanError,
                ["source_unchanged"]=unchanged });
        }
        return results;
        Point3d At(double angle)
        {
            double x=centerX+radius*Math.Cos(angle), y=centerY+radius*Math.Sin(angle);
            return new Point3d(x,y,Height(x,y));
        }
    }



    private static List<Dictionary<string,object?>> ConstrainedNormals()
    {
        var witness = FixtureData.Witness("normals");
        static PointD P(JsonElement p) => new(p[0].GetDouble(),p[1].GetDouble());
        var a=P(witness.GetProperty("a"));var b=P(witness.GetProperty("b"));var c=P(witness.GetProperty("c"));
        var box=witness.GetProperty("box").EnumerateArray().Select(P).ToArray();
        var paths=new PathsD{new(){c,a,b,box[0],box[1]}};
        string source=JsonSerializer.Serialize(paths,new JsonSerializerOptions{IncludeFields=true});
        var result=new List<Dictionary<string,object?>>();
        double Height(PointD p)=>20+.03*p.x+.07*p.y-.01*p.x*p.y;
        Vector3d Normal(PointD p)=>new(-(.03-.01*p.y),-(.07-.01*p.x),1);
        foreach(double scale in new[]{1d,1000d})
        {
            Point3d Lift(PointD p)=>new(p.x*scale,p.y*scale,Height(p)*scale);
            var oldCross=Vector3d.CrossProduct(Lift(b)-Lift(a),Lift(c)-Lift(a))/(scale*scale);
            double oldDot=oldCross*Normal(new((a.x+b.x+c.x)/3,(a.y+b.y+c.y)/3));
            var triangles=PlanarTriangulation.Triangulate(paths);double minimumDot=double.PositiveInfinity;
            foreach(var triangle in triangles)
            {
                var cross=Vector3d.CrossProduct(Lift(triangle[1])-Lift(triangle[0]),Lift(triangle[2])-Lift(triangle[0]))/(scale*scale);
                // The bilinear field normal is affine in XY: checking all three
                // vertices proves positive dot throughout the triangle interior.
                foreach(var point in triangle)minimumDot=Math.Min(minimumDot,cross*Normal(point));
            }
            double areaError=Math.Abs(Math.Abs(Clipper.Area(paths))-triangles.Sum(t=>Math.Abs(Clipper.Area(t))));
            bool unchanged=source==JsonSerializer.Serialize(paths,new JsonSerializerOptions{IncludeFields=true});
            bool vertices=triangles.SelectMany(t=>t).All(p=>paths[0].Any(q=>p.x==q.x&&p.y==q.y));
            result.Add(new(){["passed"]=oldDot<0&&minimumDot>0&&areaError<1e-8&&unchanged&&vertices,
                ["scale"]=scale,["old_needle_ear_dot"]=oldDot,["minimum_triangle_field_normal_dot"]=minimumDot,
                ["area_error_m2"]=areaError,["source_unchanged"]=unchanged,["only_original_vertices"]=vertices,["triangles"]=triangles.Count});
        }
        return result;
    }

    private static Dictionary<string,object?> ThinInteriorSample()
    {
        // A one-grid-unit narrow end must retain an interior classification point.
        var factory=new NetTopologySuite.Geometries.GeometryFactory(new NetTopologySuite.Geometries.PrecisionModel(1));
        var points=FixtureData.Witness("thin_interior").EnumerateArray()
            .Select(p=>new NetTopologySuite.Geometries.Coordinate(p[0].GetDouble(),p[1].GetDouble())).ToArray();
        var polygon=factory.CreatePolygon(points.Append(points[0]).ToArray());
        var bytes=polygon.AsBinary();double area=polygon.Area;
        var rounded=polygon.InteriorPoint;
        var actual=PlanarPartition.InteriorSample(polygon);
        bool unchanged=bytes.SequenceEqual(polygon.AsBinary())&&polygon.Area==area;
        bool passed=polygon.IsValid&&area>0&&polygon.Contains(actual)&&unchanged&&
            (FixtureData.Recorded ? !polygon.Contains(rounded)&&area==33723 : true);
        return new(){["passed"]=passed,["source_unchanged"]=unchanged,["area_grid_units_squared"]=area,
            ["rounded_point"]=new[]{rounded.X,rounded.Y},["rounded_point_inside"]=polygon.Contains(rounded),
            ["unrounded_point"]=new[]{actual.X,actual.Y},["unrounded_point_inside"]=polygon.Contains(actual)};
    }



    private static List<Dictionary<string,object?>> TouchingDisplayLoops()
    {
        var records=new List<Dictionary<string,object?>>();
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var field=TerrainField.FromSamples(projection.Bounds(10),projection,1,1,new[]{20d,20d,20d,20d});
        foreach(bool hole in new[]{false,true})foreach(double scale in new[]{1d,1000d})
        {
            var left=new PathsD{new(){new(-8,-8),new(0,-8),new(0,0),new(-8,0)}};
            if(hole)left.Add(new(){new(-6,-6),new(-6,-2),new(-2,-2),new(-2,-6)});
            var right=new PathsD{new(){new(0,0),new(8,0),new(8,8),new(0,8)}};
            using var top=new Brep();using var patch=TerrainSurfaceGeometry.CreatePatch(field,0,0,1,1);
            var sourceCaches=new List<Mesh>();
            foreach(var paths in new[]{left,right})
            {
                using var piece=TerrainSurfaceGeometry.Trim(paths,patch,.001).Single();
                if(!piece.Transform(Transform.Scale(Point3d.Origin,scale)))throw new InvalidOperationException("Point-touch fixture scale failed.");
                top.Append(piece);
                using var mesh=new Mesh();mesh.Vertices.UseDoublePrecisionVertices=true;
                foreach(var triangle in PlanarTriangulation.Triangulate(paths))
                {int first=mesh.Vertices.Count;foreach(var p in triangle)mesh.Vertices.Add(p.x*scale,p.y*scale,20*scale);mesh.Faces.AddFace(first,first+1,first+2);}
                // Append intentionally keeps coincident-but-distinct BREP vertices.
                // Shared XY in its display cache must still retain separate fans.
                sourceCaches.Add(mesh.DuplicateMesh());
            }
            // Brep.Append invalidates existing face caches; attach only after
            // the fixture's complete BREP topology has been assembled.
            for(int i=0;i<sourceCaches.Count;i++)top.Faces[i].SetMesh(MeshType.Render,sourceCaches[i]);
            var boundaries=new List<NearCapBoundary>();
            try
            {
                foreach(var edge in top.Edges.Where(e=>e.Valence==EdgeAdjacency.Naked))
                {
                    var trim=top.Trims[edge.TrimIndices().Single()];var curve=trim.DuplicateCurve();
                    if(!curve.ChangeDimension(3)||!curve.Transform(Transform.Scale(Point3d.Origin,scale)))throw new InvalidOperationException("Point-touch boundary conversion failed.");
                    boundaries.Add(trim.IsReversed()?new(edge.EdgeIndex,edge.EndVertex.VertexIndex,edge.StartVertex.VertexIndex,curve):
                        new(edge.EdgeIndex,edge.StartVertex.VertexIndex,edge.EndVertex.VertexIndex,curve));
                }
                using var display=NearTerrainDisplay.Build(top,boundaries,-10*scale,scale);
                using var bottom=AreaMassProperties.Compute(display.Bottom);
                double bottomArea=bottom!.Area/(scale*scale),wallArea=0;
                foreach(var wall in display.Walls.Values){using var mass=AreaMassProperties.Compute(wall);wallArea+=mass!.Area/(scale*scale);}
                var wallChecks=display.Walls.Values.Select(mesh=>SolidDisplayChecks.CheckMeshes(new[]{mesh},scale)).ToArray();
                bool passed=display.Tops.Count==2&&display.Walls.Count==(hole?3:2)&&Math.Abs(bottomArea-(hole?112:128))<1e-8&&
                    Math.Abs(wallArea-(hole?80:64)*30)<1e-7&&wallChecks.All(a=>a.NonmanifoldEdges==0&&a.SameDirectionEdges==0&&a.DegenerateTriangles==0);
                records.Add(new(){["hole"]=hole,["scale"]=scale,["passed"]=passed,["loops"]=display.Walls.Count,
                    ["bottom_area_m2"]=bottomArea,["wall_area_m2"]=wallArea,["wall_topology"]=wallChecks.Select(OpenMeshReport).ToArray()});
            }
            finally{foreach(var boundary in boundaries)boundary.Profile.Dispose();foreach(var cache in sourceCaches)cache.Dispose();}
        }
        return records;
    }


    private static object OpenMeshReport(SolidDisplayAudit audit)=>new
    {
        audit.NakedEdges,audit.NonmanifoldEdges,audit.SameDirectionEdges,audit.DegenerateTriangles,
        audit.MissingFaceCaches,audit.Triangles,
        OpenSurfaceTopologyPassed=audit.NonmanifoldEdges==0&&audit.SameDirectionEdges==0&&audit.DegenerateTriangles==0&&audit.MissingFaceCaches==0
    };


    private static List<Dictionary<string,object?>> FailedBuildingContracts()
    {
        var records=new List<Dictionary<string,object?>>();
        var origin=new GeoPoint(127,37.5);var projection=new LocalProjection(origin);var bounds=projection.Bounds(50);
        var field=TerrainField.FromSamples(bounds,projection,2,2,Enumerable.Repeat(20d,9).ToArray());
        int parcelNumber=0;
        GeoFeature Rectangle(string id,double x,double y)=>new() {Id=id,Pnu=$"99000000001{++parcelNumber:D4}0000",Floors=3,
            Polygons=new(){new(){new(){projection.ToGeo(x,y),projection.ToGeo(x+4,y),projection.ToGeo(x+4,y+4),projection.ToGeo(x,y+4),projection.ToGeo(x,y)}}}};
        var target=Rectangle("target",-2,-2);var normal=Rectangle("normal",-1,-1);
        var bad=Rectangle("invalid-second-part",-10,-10);
        // Its first polygon is valid and has positive area, so selection and
        // ground sampling succeed. A later degenerate polygon fails construction.
        bad.Polygons.Add(new(){new(){projection.ToGeo(20,20),projection.ToGeo(21,20),projection.ToGeo(22,20),projection.ToGeo(20,20)}});
        foreach(var unit in new[]{UnitSystem.Meters,UnitSystem.Millimeters})
        foreach(bool editable in new[]{false,true})
        foreach(bool near in new[]{false,true})
        {
            using var control=RhinoDoc.CreateHeadless(null);using var doc=RhinoDoc.CreateHeadless(null);
            foreach(var d in new[]{control,doc}){d.ModelUnitSystem=unit;d.ModelAbsoluteTolerance=unit==UnitSystem.Millimeters ? 1 : .001;}
            var options=new ModelOptions(MinArea:0,BuildingsAsNurbs:editable,UseNearRegion:near);
            CheckModel.Build(control,origin,bounds,new[]{target},new[]{target},new[]{normal},field,options);
            var result=CheckModel.Build(doc,origin,bounds,new[]{target},new[]{target},new[]{normal,bad},field,options);
            var normalControl=ModelGeometryChecks.AllObjects(control).Single(o=>o.Attributes.Name=="Building_normal");
            var normalActual=ModelGeometryChecks.AllObjects(doc).Single(o=>o.Attributes.Name=="Building_normal");
            bool noResidual=ModelGeometryChecks.AllObjects(doc).All(o=>o.Attributes.GetUserString("AHA.FeatureId")!=bad.Id)
                ;
            bool normalUnchanged=normalActual.Geometry.DataCRC(0)==normalControl.Geometry.DataCRC(0)
                && normalActual.Geometry.GetBoundingBox(true).Equals(normalControl.Geometry.GetBoundingBox(true));
            bool excluded=result.InvalidBuildings==1 && result.InvalidBuildingIds.SequenceEqual(new[]{bad.Id});
            records.Add(new(){["unit"]=unit.ToString(),["building_nurbs"]=editable,["near_enabled"]=near,
                ["passed"]=noResidual && normalUnchanged && excluded,
                ["invalid_building_excluded"]=excluded,["no_partial_objects"]=noResidual,
                ["normal_building_unchanged"]=normalUnchanged});
        }
        return records;
    }

    private static Dictionary<string, object?> KinkedCachePersistence(string directory, LocalProjection projection)
    {
        var field = TerrainField.FromSamples(projection.Bounds(10), projection, 2, 2,
            new double[] { 0, 10, 0, 10, 0, 10, 0, 10, 0 });
        using var patch = TerrainSurfaceGeometry.CreatePatch(field, 0, 0, 2, 2);
        var trimmed = TerrainSurfaceGeometry.Trim(new PathsD { new() { new(-10,-10), new(10,-10), new(10,10), new(-10,10) } }, patch, 0.001);
        using var brep = trimmed.Single();
        using var mesh = new Mesh(); mesh.Vertices.UseDoublePrecisionVertices = true;
        for (int row = 0; row <= 2; row++) for (int col = 0; col <= 2; col++)
            mesh.Vertices.Add(field.ModelX(col), field.ModelY(row), field.Node(col, row));
        for (int row = 0; row < 2; row++) for (int col = 0; col < 2; col++)
        { int a = row * 3 + col; mesh.Faces.AddFace(a, a + 1, a + 4); mesh.Faces.AddFace(a, a + 4, a + 3); }
        int patchFaces = mesh.Faces.Count;
        TerrainSurfaceGeometry.AttachDisplayMesh(brep, mesh);
        Point3d At(Point2d p) => new(p.X, p.Y, field.SampleModelLocal(p.X, p.Y));
        var points = new[] { new Point2d(-8,-6), new Point2d(0,-6), new Point2d(0,0), new Point2d(0,8) };
        var segments = points.Zip(points.Skip(1), (a,b) => (At(a), At((a+b)/2), At(b))).ToArray();
        using var side = TerrainSurfaceGeometry.CreateRoadSide(segments, 0.15);
        using var sideMesh = new Mesh(); sideMesh.Vertices.UseDoublePrecisionVertices = true;
        foreach (var (a, _, b) in segments)
        {
            int i = sideMesh.Vertices.Count;
            foreach (var p in new[] { a, b, b - new Vector3d(0,0,.15), a - new Vector3d(0,0,.15) }) sideMesh.Vertices.Add(p);
            sideMesh.Faces.AddFace(i,i+1,i+2); sideMesh.Faces.AddFace(i,i+2,i+3);
        }
        int sideFaces = sideMesh.Faces.Count;
        TerrainSurfaceGeometry.AttachDisplayMesh(side, sideMesh);
        using var doc = RhinoDoc.CreateHeadless(null); doc.ModelUnitSystem = UnitSystem.Meters;
        var expected = new Dictionary<string,int> { ["multicell-patch"] = patchFaces, ["cornered-road-side"] = sideFaces };
        int beforeWrite = 0, afterWrite = 0;
        foreach (var (name, geometry) in new[] { ("multicell-patch", brep), ("cornered-road-side", side) })
        {
            var id = doc.Objects.AddBrep(geometry, new Rhino.DocObjects.ObjectAttributes { Name = name }, null, false, false);
            if (id == Guid.Empty) throw new InvalidOperationException("Kinked cache test could not add a Brep.");
            if (Preserved(doc.Objects.FindId(id).Geometry as Brep, expected[name])) beforeWrite++;
        }
        string path = Path.Combine(directory, "helper-kinked-caches.3dm");
        if (!doc.Write3dmFile(path, new FileWriteOptions { FileVersion = 8, SuppressDialogBoxes = true, IncludeRenderMeshes = true }))
            throw new IOException("Kinked cache archive could not be written.");
        using (var saved = File3dm.Read(path) ?? throw new InvalidDataException("Kinked cache archive could not be read."))
        foreach (var record in saved.Objects)
            if (expected.TryGetValue(record.Attributes.Name, out int faces) && Preserved(record.Geometry as Brep, faces)) afterWrite++;
        // Diagnostic control only: host defaults can legitimately change later.
        using var control = RhinoDoc.CreateHeadless(null);
        var defaultId = control.Objects.AddBrep(brep);
        int defaultFaces = (control.Objects.FindId(defaultId)?.Geometry as Brep)?.Faces.Count ?? 0;
        return new()
        {
            ["passed"] = beforeWrite == 2 && afterWrite == 2,
            ["single_face_with_cache_before_write"] = beforeWrite, ["single_face_with_cache_after_readback"] = afterWrite,
            ["default_add_brep_control_face_count"] = defaultFaces, ["expected_patch_render_faces"] = patchFaces,
            ["expected_side_render_faces"] = sideFaces
        };
        static bool Preserved(Brep? value, int count) => value is { IsValid: true } && value.Faces.Count == 1
            && value.Faces[0].GetMesh(MeshType.Render) is { IsValid: true } render && render.Faces.Count == count;
    }

    private static (bool Passed, int Triangles, double Error, int UnmatchedEdges) ConformingPartition(TerrainField field)
    {
        Point2d P(double x, double y) => new(x, y);
        var inputs = new[]
        {
            new SurfaceTriangle(P(-10,-10), P(0,-10), P(0,10), 0),
            new SurfaceTriangle(P(-10,-10), P(0,10), P(-10,10), 0),
            new SurfaceTriangle(P(0,-10), P(10,-10), P(0,0), 1),
            new SurfaceTriangle(P(10,-10), P(10,0), P(0,0), 1),
            new SurfaceTriangle(P(0,0), P(10,0), P(0,10), 1),
            new SurfaceTriangle(P(10,0), P(10,10), P(0,10), 1)
        };
        var output = PlanarSurfaceMesh.ConformAndRefine(inputs, field);
        return Audit(output, field, 400);
    }

    private static List<Dictionary<string, object?>> ConformingRegressions()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FixtureData.PathFor("conforming-regressions.json")));
        var result = new List<Dictionary<string, object?>>();
        var projection = new LocalProjection(new GeoPoint(127, 37.5));
        foreach (var fixture in json.RootElement.EnumerateArray())
        {
            double x0 = fixture.GetProperty("x0").GetDouble(), y0 = fixture.GetProperty("y0").GetDouble();
            var sw = projection.ToGeo(x0, y0);
            var ne = projection.ToGeo(x0 + fixture.GetProperty("dx").GetDouble(), y0 + fixture.GetProperty("dy").GetDouble());
            var field = TerrainField.FromSamples(new GeoBounds(sw.Lon, sw.Lat, ne.Lon, ne.Lat), projection, 1, 1,
                fixture.GetProperty("heights").EnumerateArray().Select(v => v.GetDouble()).ToArray());
            var input = fixture.GetProperty("triangles").EnumerateArray().Select(t =>
            {
                Point2d P(string key) { var p = t.GetProperty(key); return new(p[0].GetDouble(), p[1].GetDouble()); }
                return new SurfaceTriangle(P("a"), P("b"), P("c"), t.GetProperty("part").GetInt32());
            }).ToArray();
            double area = input.Sum(t => Math.Abs((t.B.X-t.A.X)*(t.C.Y-t.A.Y)-(t.B.Y-t.A.Y)*(t.C.X-t.A.X))/2);
            var refined = PlanarSurfaceMesh.ConformAndRefine(input, field);
            var check = Audit(refined, field, area);
            result.Add(new() { ["case"] = fixture.GetProperty("case").GetString(), ["passed"] = check.Passed,
                ["triangles"] = check.Triangles, ["max_field_error_m"] = check.Error, ["unmatched_interior_edges"] = check.UnmatchedEdges });
        }
        return result;
    }

    private static List<Dictionary<string, object?>> PolygonRegressions()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FixtureData.PathFor("polygon-regressions.json")));
        var result = new List<Dictionary<string, object?>>();
        foreach (var fixture in json.RootElement.EnumerateArray())
        foreach (var region in fixture.GetProperty("polygons").EnumerateArray())
        {
            var paths = new PathsD(region.GetProperty("paths").EnumerateArray().Select(ring => new PathD(ring.EnumerateArray()
                .Select(p => new PointD(p[0].GetDouble(), p[1].GetDouble())))));
            var triangles = PlanarTriangulation.Triangulate(paths);
            double area = Math.Abs(Clipper.Area(paths)), triangleArea = triangles.Sum(t => Math.Abs(Clipper.Area(t)));
            double difference = Math.Abs(area - triangleArea);
            result.Add(new() { ["case"] = fixture.GetProperty("name").GetString(), ["part"] = region.GetProperty("part").GetInt32(),
                ["passed"] = difference <= Math.Max(1e-8, area * 1e-9), ["polygon_area_m2"] = area,
                ["triangle_area_m2"] = triangleArea, ["triangles"] = triangles.Count });
        }
        return result;
    }

    private static (bool Passed, int Triangles, double Error, int UnmatchedEdges) Audit(
        List<SurfaceTriangle> output, TerrainField field, double expectedArea)
    {
        var edges = new Dictionary<string, (int Count, Point2d A, Point2d B)>();
        double maxError = 0, area = 0;
        foreach (var triangle in output)
        {
            var a = triangle.A; var b = triangle.B; var c = triangle.C;
            area += Math.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
            double x = (a.X + b.X + c.X) / 3, y = (a.Y + b.Y + c.Y) / 3;
            maxError = Math.Max(maxError, Math.Abs((field.SampleModelLocal(a.X,a.Y) + field.SampleModelLocal(b.X,b.Y) + field.SampleModelLocal(c.X,c.Y))/3 - field.SampleModelLocal(x,y)));
            Add(a,b); Add(b,c); Add(c,a);
        }
        int unmatched = edges.Values.Count(e => e.Count != 2 && !Outer(e.A,e.B));
        return (maxError <= 0.051 && Math.Abs(area - expectedArea) < 1e-6 && unmatched == 0, output.Count, maxError, unmatched);

        void Add(Point2d a, Point2d b)
        {
            string Ka(Point2d p) => (p.X == 0 ? 0 : p.X).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + "," + (p.Y == 0 ? 0 : p.Y).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string x = Ka(a), y = Ka(b); string key = string.CompareOrdinal(x,y) <= 0 ? x + ":" + y : y + ":" + x;
            edges[key] = edges.TryGetValue(key, out var e) ? (e.Count + 1, a, b) : (1, a, b);
        }
        bool Outer(Point2d a, Point2d b) => Math.Abs(a.X-b.X) < 1e-8
                && (Math.Abs(a.X-field.ModelX(0)) < 0.000002 || Math.Abs(a.X-field.ModelX(field.Columns)) < 0.000002)
            || Math.Abs(a.Y-b.Y) < 1e-8
                && (Math.Abs(a.Y-field.ModelY(0)) < 0.000002 || Math.Abs(a.Y-field.ModelY(field.Rows)) < 0.000002);
    }
}

internal sealed record NearSolidOrientationAudit(bool Passed,string NativeOrientation,bool OrientedManifold,
    bool Connected,bool DisplayOrientedManifold,bool DisplayConnected,int TopFaces,int BottomFaces,int TopNormalSamples,int InvalidNormals,
    double? NativeVolumeMetres3,double? NativeVolumeUncertaintyMetres3,double? DisplaySignedVolumeMetres3,
    IReadOnlyList<object> NormalFailures);

/// <summary>Independent checks for generated height-field solids; never trusts an orientation flag or flips geometry.</summary>
internal static class NearSolidOrientationChecks
{
    internal static NearSolidOrientationAudit Check(Brep solid,double scale,SolidDisplayAudit? displayAudit=null)
    {
        bool manifold=solid.IsOrientedManifold(out bool oriented,out bool boundary)&&oriented&&!boundary;
        var display=displayAudit??SolidDisplayChecks.Check(solid,scale);
        bool displayManifold=display.Passed,displayConnected=display.ConnectedComponents==1;
        var visited=new HashSet<int>();var pending=new Stack<int>();
        if(solid.Faces.Count>0)pending.Push(0);
        while(pending.TryPop(out int index))
        {
            if(!visited.Add(index))continue;
            foreach(int neighbour in solid.Faces[index].AdjacentFaces())pending.Push(neighbour);
        }
        bool connected=visited.Count==solid.Faces.Count&&visited.Count>0;
        using var metric=solid.DuplicateBrep();
        metric.Transform(Transform.Scale(Point3d.Origin,1/scale));
        using var mass=VolumeMassProperties.Compute(metric, true, false, false, false);
        double unit=1;
        double? volume=Finite(mass?.Volume/unit),uncertainty=Finite(mass?.VolumeError/unit);
        bool positive=volume is >0&&uncertainty is {} error&&volume>Math.Abs(error);
        double bottom=solid.Vertices.Min(v=>v.Location.Z);
        var origin=solid.Vertices[0].Location/scale;
        double displayVolume=0,compensation=0;int topFaces=0,bottomFaces=0,samples=0,invalidNormals=0;
        var normalFailures=new List<object>();
        foreach(var face in solid.Faces)
        {
            var mesh=face.GetMesh(MeshType.Render);
            if(mesh is null||mesh.Faces.Count==0){invalidNormals++;continue;}
            var faceNormal=face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid);
            bool cap=face.TryGetPlane(out var capPlane,1e-7*scale)&&faceNormal.Z<-.999999
                &&Math.Abs(capPlane.Normal.Z)>1-1e-10;
            bool top=faceNormal.Z>1e-8;int faceSamples=0;
            foreach(var triangle in mesh.Faces)
            {
                Inspect(triangle.A,triangle.B,triangle.C);
                if(triangle.IsQuad)Inspect(triangle.A,triangle.C,triangle.D);
            }
            if(cap)
            {
                bottomFaces++;
                // A horizontal cap has one constant normal, including outside
                // its trim; its underlying domain midpoint is safe here.
                var normal=face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid);
                if(!face.TryGetPlane(out var plane,1e-7*scale)||Math.Abs(plane.Normal.Z)<1-1e-10||normal.Z>-.999999
                    ||Math.Abs(plane.Origin.Z-bottom)>1e-7*scale)invalidNormals++;
            }
            else if(top)
            {
                topFaces++;
                if(faceSamples==0)invalidNormals++;
            }

            void Inspect(int ia,int ib,int ic)
            {
                var a=mesh.Vertices.Point3dAt(ia)/scale;var b=mesh.Vertices.Point3dAt(ib)/scale;var c=mesh.Vertices.Point3dAt(ic)/scale;
                var aa=a-origin;var bb=b-origin;var cc=c-origin;
                double term=(aa*Vector3d.CrossProduct(bb,cc))/6-compensation;
                double next=displayVolume+term;compensation=(next-displayVolume)-term;displayVolume=next;
                var cross=Vector3d.CrossProduct(b-a,c-a);
                if(!cross.IsValid||cross.SquareLength<=0){invalidNormals++;return;}
                if(cap){if(cross.Z>=0)invalidNormals++;return;}
                if(top&&cross.Z<0)invalidNormals++;
                // Native walls retain exact corners; the display boundary has
                // fixed-grid connectors. Its local tangent need not agree with
                // either native corner span. Each closed representation instead
                // proves consistent orientation through shared edges and signed
                // volume. Top face normals still follow the common height field.
                if(!top)return;
                var centroid=(a+b+c)/3*scale;
                if(!face.ClosestPoint(centroid,out double u,out double v)||face.IsPointOnFace(u,v)!=PointFaceRelation.Interior)return;
                var normal=face.NormalAt(u,v);
                if(!normal.IsValid||Vector3d.Multiply(cross,normal)<=0)
                {
                    invalidNormals++;
                    normalFailures.Add(new { face=face.FaceIndex, triangle=new[]{ia,ib,ic}, top, a,b,c,
                        u,v,closest=face.PointAt(u,v)/scale,normal,cross });
                }
                if(top){samples++;faceSamples++;if(normal.Z<=0)invalidNormals++;}
            }
        }
        bool passed=solid.IsValid&&solid.IsSolid&&manifold&&connected&&displayManifold&&displayConnected&&positive&&double.IsFinite(displayVolume)&&displayVolume>0
            &&topFaces>0&&bottomFaces>0&&samples>=topFaces&&invalidNormals==0;
        return new(passed,solid.SolidOrientation.ToString(),manifold,connected,displayManifold,displayConnected,topFaces,bottomFaces,samples,invalidNormals,
            volume,uncertainty,Finite(displayVolume),normalFailures);
        static double? Finite(double? value)=>value is {} number&&double.IsFinite(number)?number:null;
    }
}
