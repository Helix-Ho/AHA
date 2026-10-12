using Clipper2Lib;
using Aha.Topo.Checks;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Aha.Topo;

/// <summary>Development checks of the actual terrain/site output, shared by live and saved-input checks.</summary>
internal static class ModelGeometryChecks
{
    // Hidden XY references are part of the saved model and its hand-off contract.
    internal static RhinoObject[] AllObjects(RhinoDoc doc) => doc.Objects.GetObjectList(new ObjectEnumeratorSettings
    {
        NormalObjects = true, LockedObjects = true, HiddenObjects = true,
        ActiveObjects = true, ReferenceObjects = false, DeletedObjects = false
    }).ToArray();

    internal static Dictionary<string, object?> Run(RhinoDoc doc, ModelResult result,
        GeoBounds bounds, LocalProjection projection, TerrainField field,
        IReadOnlyList<GeoFeature> buildings, ModelOptions options, Action<string>? progress = null, IReadOnlyList<GeoFeature>? parcels = null, EditRegion? nearRegion = null)
    {
        double scale = RhinoMath.UnitScale(UnitSystem.Meters, doc.ModelUnitSystem);
        // Existing user geometry is outside this generation's audit scope.
        // Resolve every emitted ID so a missing output is an explicit failure.
        var objects = result.ObjectIds.Select(id => doc.Objects.FindId(id)
            ?? throw new InvalidOperationException("생성한 Topo 객체가 문서에서 누락되었습니다.")).ToArray();
        var source = buildings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var buildingObjects = objects.Where(obj => obj.Attributes.GetUserString("AHA.Role") == "Building")
            .ToLookup(obj => obj.Attributes.GetUserString("AHA.FeatureId"));
        var foundationObjects = objects.Where(obj => obj.Attributes.GetUserString("AHA.Role") == "Foundation")
            .ToLookup(obj => obj.Attributes.GetUserString("AHA.FeatureId"));
        var builtIds = buildingObjects.Select(group => group.Key!).ToHashSet(StringComparer.Ordinal);
        var expectedRejections = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<GeoFeature>();
        foreach (var building in buildings)
        {
            try
            {
                SiteSelection.ValidateFeature(building, projection);
                double sourceArea = Geo.Area(building, projection);
                if (!double.IsFinite(sourceArea) || sourceArea <= 0) throw new ArgumentException("invalid_footprint_area");
                if (TerrainCoverage.IsGenerated(building, projection, bounds, options.MinArea)) candidates.Add(building);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { expectedRejections.Add(building.Id); }
        }
        // Analytic full-footprint statistics are independently protected in BuildingPlacementChecks.
        var expectedRoads = RoadGeometry.Build(options.IncludeRoads ? parcels ?? Array.Empty<GeoFeature>() : Array.Empty<GeoFeature>(), projection, field.Bounds, bounds, nearRegion);
        var placements = BuildingPlacement.Prepare(field, expectedRoads, options.EffectiveRoadDrop, options.SeparateRoads, candidates);
        foreach (var rejected in placements.Rejections) expectedRejections.Add(rejected.FeatureId);
        var placementById = BuildingPlacement.ShareLowestFloors(placements.Placements, item =>
                nearRegion?.ContainsStrict(source[item.SourceFeatureId], projection) == true && nearRegion.ContainsStrict(item.Footprint))
            .ToDictionary(item => item.SourceFeatureId, StringComparer.Ordinal);
        var expectedHeight = new TerrainElevationExpectation(field);
        bool rejectedBuildingsPreserveXY = placements.Rejections.All(rejected => !builtIds.Contains(rejected.FeatureId)
            && objects.Any(obj => obj.Attributes.GetUserString("AHA.Role") == "BuildingFootprint"
                && obj.Attributes.GetUserString("AHA.FeatureId") == rejected.FeatureId));
        bool expectedRejectedIds = expectedRejections.SetEquals(result.InvalidBuildingIds)
            && result.InvalidBuildings == expectedRejections.Count;
        int invalid = 0, nonSolid = 0, wrongHeight = 0, wrongRepresentation = 0;
        int nearBuildings = 0, farBuildings = 0, allBuildings = 0, surfaceSamples = 0, displaySamples = 0, nonUpward = 0;
        int nearSurfaces = 0, farSurfaces = 0, allSurfaces = 0, nurbsSurfaces = 0, meshSurfaces = 0, sideObjects = 0, missingDisplayMeshes = 0;
        double landArea = 0, roadArea = 0, surfaceError = 0, displayError = 0, volumeError = 0;
        long bytes = 0, displayTriangles = 0;
        double displayAreaError = 0;
        double boundaryChordError = 0;
        var boundaryEdges = new Dictionary<string, List<(Point3d A, Point3d B, string Role, string Zone)>>();
        var overlayEdges = new Dictionary<string, List<(Point3d A, Point3d B, string Role, string Zone)>>();
        double effectiveDrop = options.EffectiveRoadDrop;
        var sideEdges = new Dictionary<string, List<(Point3d A, Point3d B)>>();
        var sideNormals = new Dictionary<string, List<Vector3d>>();
        int missingSideDisplayMeshes = 0, sideNormalSamples = 0, inconsistentSideNormals = 0;
        var checkedIds = new HashSet<string>();
        int visitedObjects = 0;
        foreach (var obj in objects)
        {
            var geometry = obj.Geometry;
            var role = obj.Attributes.GetUserString("AHA.Role");
            var zone = obj.Attributes.GetUserString("AHA.Zone");
            if (visitedObjects++ % 1000 == 0) progress?.Invoke($"auditing object {visitedObjects}/{objects.Length}");
            if (!geometry.IsValid) invalid++;
            bytes += geometry.MemoryEstimate();
            if (role == "Building")
            {
                var id = obj.Attributes.GetUserString("AHA.FeatureId") ?? "";
                if (!source.TryGetValue(id, out var feature)) throw new InvalidOperationException("Building identity was lost.");
                if (BuildingRules.IsUnderground(feature)) throw new InvalidOperationException("An underground facility was emitted as an above-ground building.");
                checkedIds.Add(id);
                bool solid = geometry switch { Mesh m => m.IsClosed, Brep b => b.IsSolid, Extrusion e => e.IsSolid, _ => false };
                if (!solid) nonSolid++;
                if (zone == "Near") nearBuildings++;
                else if (zone == "Far") farBuildings++;
                else if (zone == "All") allBuildings++;
                else wrongRepresentation++;
                bool editableBuilding=options.BuildingsAsNurbs || options.UseNearRegion && zone == "Near";
                if (editableBuilding ? geometry is not Extrusion : geometry is not Mesh) wrongRepresentation++;
                if (placementById.TryGetValue(id, out var classified))
                {
                    bool expectedNear = nearRegion?.ContainsStrict(feature, projection) == true && nearRegion.ContainsStrict(classified.Footprint);
                    if (zone != (options.UseNearRegion ? expectedNear ? "Near" : "Far" : "All")) wrongRepresentation++;
                }
                if (options.UseNearRegion ? zone is not ("Near" or "Far") : zone != "All") wrongRepresentation++;
                var box = geometry.GetBoundingBox(true);
                var expected = BuildingRules.Resolve(feature, options);
                if (!placementById.TryGetValue(id, out var placement)) { wrongHeight++; continue; }
                double bottom = options.SeparateFoundations ? placement.MeanElevationMetres : placement.BaseElevationMetres;
                double physicalHeight = expected.Height + placement.MeanElevationMetres - bottom;
                if (Math.Abs(box.Min.Z / scale - bottom) > .0001
                    || Math.Abs(box.Max.Z / scale - placement.MeanElevationMetres - expected.Height) > .0001) wrongHeight++;
                if (Math.Abs((box.Max.Z - box.Min.Z) / scale - physicalHeight) > 0.0001
                    || obj.Attributes.GetUserString("AHA.HeightSource") != expected.Source
                    || !double.TryParse(obj.Attributes.GetUserString("AHA.HeightMetres"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double declaredHeight)
                    || Math.Abs(declaredHeight - expected.Height) > .0001) wrongHeight++;
                // Multipart features may produce several objects; aggregate independently below.
            }
            if (role == "Foundation")
            {
                var id = obj.Attributes.GetUserString("AHA.FeatureId") ?? "";
                if (!options.SeparateFoundations || !placementById.TryGetValue(id, out var placement)) { wrongRepresentation++; continue; }
                bool editable = zone == "Near" || options.BuildingsAsNurbs;
                if (editable ? geometry is not Extrusion { IsSolid:true } : geometry is not Mesh { IsClosed:true }) wrongRepresentation++;
                var box=geometry.GetBoundingBox(true);
                if (Math.Abs(box.Min.Z/scale-placement.BaseElevationMetres)>.0001
                    || Math.Abs(box.Max.Z/scale-placement.MeanElevationMetres)>.0001) wrongHeight++;
                if (!buildingObjects[id].Any(body=>body.Attributes.GetUserString("AHA.Zone")==zone
                    &&body.Attributes.GetUserString(Aha.Info.InfoData.Key)==obj.Attributes.GetUserString(Aha.Info.InfoData.Key))) wrongRepresentation++;
            }
            if (role == "RoadSide")
            {
                sideObjects++;
                bool nurbsSide=options.TerrainAsNurbs || options.UseNearRegion && zone == "Near";
                if ((nurbsSide ? geometry is not Brep singleSide || singleSide.Faces.Count != 1 || singleSide.Faces[0].UnderlyingSurface() is not NurbsSurface : geometry is not Mesh)
                    || (options.UseNearRegion ? zone is not ("Near" or "Far") : zone != "All"))
                    wrongRepresentation++;
                if (geometry is Brep sideBrep)
                {
                    foreach (var face in sideBrep.Faces)
                    {
                        var render = face.GetMesh(MeshType.Render);
                        if (render is null || !render.IsValid || render.Faces.Count == 0) missingSideDisplayMeshes++;
                        else
                        {
                            CheckSideBoundary(render);
                            if (face.UnderlyingSurface() is NurbsSurface basis)
                            {
                                // Match the exact boundary segment, not the spatially
                                // closest triangle: micron-sized turns and adjacent
                                // opposing curbs can be closer than mesh search precision.
                                var displaySegments = render.Faces.Select(f =>
                                {
                                    var a=render.Vertices.Point3dAt(f.A); var b=render.Vertices.Point3dAt(f.B); var c=render.Vertices.Point3dAt(f.C);
                                    var normal=Vector3d.CrossProduct(b-a,c-a);
                                    var end=a.X != b.X || a.Y != b.Y ? b : c;
                                    return (A:a, B:end, Normal:normal);
                                }).ToArray();
                                var spans = Enumerable.Range(0, basis.KnotsU.Count-1)
                                    .Where(i => basis.KnotsU[i+1]-basis.KnotsU[i] > 1e-10)
                                    .Select(i => (Start:basis.KnotsU[i], End:basis.KnotsU[i+1])).ToArray();
                                foreach (int index in new[] { 0, spans.Length/2, spans.Length-1 }.Distinct())
                                {
                                    if (index < 0 || index >= spans.Length) continue;
                                    double u=(spans[index].Start+spans[index].End)/2, v=face.Domain(1).Mid;
                                    var start=face.PointAt(spans[index].Start,v); var end=face.PointAt(spans[index].End,v);
                                    var normal=basis.NormalAt(u,v); if (face.OrientationIsReversed) normal.Reverse();
                                    double toleranceSquared=Math.Pow(1e-8*scale,2);
                                    bool SameXY(Point3d a,Point3d b) => (a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y) <= toleranceSquared;
                                    var matches=displaySegments.Where(edge => SameXY(start,edge.A) && SameXY(end,edge.B)
                                        || SameXY(start,edge.B) && SameXY(end,edge.A)).ToArray();
                                    if (matches.Length == 0) inconsistentSideNormals++;
                                    else foreach (var match in matches)
                                    {
                                        var expected=match.Normal;
                                        if (!normal.Unitize() || !expected.Unitize() || normal*expected < 0.99999) { inconsistentSideNormals++; break; }
                                    }
                                    sideNormalSamples++;
                                }
                            }
                        }
                    }
                }
                else if (geometry is Mesh sideMesh) CheckSideBoundary(sideMesh);
                else wrongRepresentation++;
                continue;
            }
            if (role is not ("Land" or "Road")) continue;
            if (zone == "Near") nearSurfaces++;
            else if (zone == "Far") farSurfaces++;
            else if (zone == "All") allSurfaces++;
            else wrongRepresentation++;
            bool expectNurbs=options.TerrainAsNurbs || options.UseNearRegion && zone == "Near";
            if ((expectNurbs ? geometry is not Brep singleSurface || (zone != "Near" && singleSurface.Faces.Count != 1) || singleSurface.Faces.Where(IsUpward).Any(face=>face.UnderlyingSurface() is not NurbsSurface) : geometry is not Mesh)
                || (options.UseNearRegion ? zone is not ("Near" or "Far") : zone != "All")) wrongRepresentation++;
            if (geometry is Brep) nurbsSurfaces++; else if (geometry is Mesh) meshSurfaces++;
            double area = ProjectedArea(geometry, scale) / (scale * scale);
            if (role == "Land") landArea += area; else roadArea += area;
            double drop = role == "Road" ? effectiveDrop : 0;
            if (geometry is Brep brep)
            {
                double displayArea = 0;
                foreach (var face in brep.Faces)
                {
                    var render = face.GetMesh(MeshType.Render);
                    if (render is null || !render.IsValid || render.Faces.Count == 0) missingDisplayMeshes++;
                    else if(IsUpward(face))
                    {
                        displayTriangles += render.Faces.Count;
                        displayArea += ProjectedArea(render, scale) / (scale * scale);
                        CheckSurfaceMesh(render, drop, role, zone!);
                    }
                }
                displayAreaError += Math.Abs(displayArea - area);
                foreach (var face in brep.Faces.Where(IsUpward))
                for (int u = 1; u <= 3; u++) for (int v = 1; v <= 3; v++)
                {
                    double pu = face.Domain(0).ParameterAt(u / 4.0), pv = face.Domain(1).ParameterAt(v / 4.0);
                    if (face.IsPointOnFace(pu, pv) == PointFaceRelation.Exterior) continue;
                    CheckElevation(face.PointAt(pu, pv), drop, true);
                }
                foreach(var face in brep.Faces.Where(IsUpward))
                foreach(var edge in face.Loops.SelectMany(loop=>loop.Trims).Select(trim=>trim.Edge).Where(edge=>edge is not null).Distinct())
                foreach(double t in new[]{edge.Domain.Min,edge.Domain.Mid,edge.Domain.Max})CheckElevation(edge.PointAt(t),drop,true);
            }
            else if (geometry is Mesh directMesh) CheckSurfaceMesh(directMesh, drop, role, zone!);
        }
        int visitedBuildings = 0;
        progress?.Invoke("auditing building volumes");
        foreach (var id in checkedIds)
        {
            if (visitedBuildings++ % 1000 == 0) progress?.Invoke($"auditing building volume {visitedBuildings}/{checkedIds.Count}");
            var feature = source[id];
            double height = BuildingRules.Resolve(feature, options).Height;
            var modelFeature = feature;
            if (placementById.TryGetValue(id, out var placement))
            { modelFeature = placement.Feature; if (!options.SeparateFoundations) height += placement.MeanElevationMetres - placement.BaseElevationMetres; }
            double expectedVolume = Geo.Area(modelFeature, projection) * height;
            double volume = 0;
            foreach (var obj in buildingObjects[id])
            {
                using var mass = obj.Geometry switch
                {
                    Mesh m => VolumeMassProperties.Compute(m), Brep b => VolumeMassProperties.Compute(b),
                    Extrusion e => VolumeMassProperties.Compute(e), _ => null
                };
                if (mass is null) { nonSolid++; continue; }
                volume += Math.Abs(mass.Volume) / Math.Pow(scale, 3);
            }
            volumeError = Math.Max(volumeError, Math.Abs(volume - expectedVolume) / Math.Max(1, expectedVolume));
            var foundations=foundationObjects[id].ToArray();
            if (options.SeparateFoundations)
            {
                if (foundations.Length!=buildingObjects[id].Count() || !placementById.TryGetValue(id,out var foundationPlacement)) wrongRepresentation++;
                else
                {
                    double foundationVolume=0;
                    foreach(var obj in foundations)
                    {
                        using var mass=obj.Geometry switch {Mesh m=>VolumeMassProperties.Compute(m),Extrusion e=>VolumeMassProperties.Compute(e),_=>null};
                        if(mass is null){nonSolid++;continue;}
                        foundationVolume+=Math.Abs(mass.Volume)/Math.Pow(scale,3);
                    }
                    double expectedFoundation=Geo.Area(foundationPlacement.Feature,projection)*(foundationPlacement.MeanElevationMetres-foundationPlacement.BaseElevationMetres);
                    volumeError=Math.Max(volumeError,Math.Abs(foundationVolume-expectedFoundation)/Math.Max(1,expectedFoundation));
                }
            }
            else if(foundations.Length!=0)wrongRepresentation++;
        }
        var sw = projection.ToLocal(new GeoPoint(bounds.West, bounds.South));
        var ne = projection.ToLocal(new GeoPoint(bounds.East, bounds.North));
        double expectedArea = (ne.X - sw.X) * (ne.Y - sw.Y);
        progress?.Invoke("auditing all surface and road-side boundary incidences");
        double areaError = Math.Abs(landArea + (options.SeparateRoads ? roadArea : 0) - expectedArea);
        double roadAreaError = Math.Abs(roadArea - result.RoadAreaSquareMetres);
        int unmatchedOverlayEdges=overlayEdges.Values.Count(edges => edges.Count is not (1 or 2));
        bool overlayBoundary=true;
        if (!options.SeparateRoads && parcels is not null)
        {
            var outputDomain = new PathsD { new PathD { new(sw.X,sw.Y), new(ne.X,sw.Y), new(ne.X,ne.Y), new(sw.X,ne.Y) } };
            var outputRoads = Clipper.Intersect(expectedRoads.Outlines, outputDomain, FillRule.NonZero, EditRegion.ClipPrecision);
            var expectedSegments=outputRoads.SelectMany(path => Enumerable.Range(0,path.Count)
                .Select(i => (A:new Point3d(path[i].x,path[i].y,0), B:new Point3d(path[(i+1)%path.Count].x,path[(i+1)%path.Count].y,0)))).ToArray();
            var actualSegments=overlayEdges.Values.Where(edges => edges.Count==1).Select(edges => (edges[0].A,edges[0].B)).ToArray();
            overlayBoundary=BoundaryCoverage(expectedSegments,actualSegments) && BoundaryCoverage(actualSegments,expectedSegments);
        }
        int unmatchedBoundaryEdges = boundaryEdges.Values.Count(e => e.Count != 2 && !(e.Count == 1 && OnOuter(e[0].A, e[0].B)));
        int expectedStepEdges = 0, missingStepEdges = 0, extraSideEdges = 0;
        int inwardSideTriangles = 0;
        double sideEdgeGap = 0;
        foreach (var (key, edges) in boundaryEdges)
        {
            bool step = edges.Count == 2 && edges.Any(e => e.Role == "Land") && edges.Any(e => e.Role == "Road")
                && EndpointsDistance(edges[0].A, edges[0].B, edges[1].A, edges[1].B) > 1e-10;
            if (!step || effectiveDrop == 0 || edges.Any(e=>e.Zone=="Near")) continue;
            expectedStepEdges++;
            if (!sideEdges.TryGetValue(key, out var sides) || sides.Count != 2) { missingStepEdges++; continue; }
            foreach (var edge in edges)
                sideEdgeGap = Math.Max(sideEdgeGap, sides.Min(s => EndpointsDistance(edge.A, edge.B, s.A, s.B)));
            var land = edges.Single(e => e.Role == "Land");
            var direction = land.B - land.A;
            var towardRoad = new Vector3d(direction.Y, -direction.X, 0);
            if (sideNormals.TryGetValue(key, out var normals))
                inwardSideTriangles += normals.Count(n => n * towardRoad <= 0);
        }
        foreach (var (key, edges) in sideEdges)
            if (effectiveDrop == 0 || !boundaryEdges.TryGetValue(key, out var surfaces) || surfaces.Count != 2
                || !surfaces.Any(e => e.Role == "Land") || !surfaces.Any(e => e.Role == "Road")) extraSideEdges += edges.Count;
        int accounted = result.KnownBuildings + result.FloorEstimatedBuildings + result.DefaultEstimatedBuildings
            + result.FilteredBuildings + result.OutsideBuildings + result.InvalidBuildings + result.UndergroundBuildings;
        int editableBoundaries = objects.Count(o => o.Attributes.GetUserString("AHA.Role") == "EditableBoundary");
        bool regionPartition = options.UseNearRegion
            ? nearSurfaces > 0 && allSurfaces == 0 && editableBoundaries == 0
            : nearSurfaces == 0 && farSurfaces == 0 && allSurfaces > 0 && editableBoundaries == 0
                && !doc.Layers.Any(layer => !layer.IsDeleted && layer.ParentLayerId == result.ParentLayerId
                    && layer.Name is "Near" or "Far" or "EditableBoundary");
        bool surfaceCounts = result.SurfaceCount == nearSurfaces + farSurfaces + allSurfaces
            && result.NearSurfaces == nearSurfaces && result.FarSurfaces == farSurfaces;
        bool parapetsValid = ParapetGeometryChecks.Audit(doc, objects, options, scale);
        bool passed = parapetsValid && regionPartition && surfaceCounts && invalid == 0 && nonSolid == 0 && wrongHeight == 0 && wrongRepresentation == 0
            && expectedRejectedIds && rejectedBuildingsPreserveXY && accounted == buildings.Count && nonUpward == 0
            && volumeError <= 0.0001 && areaError <= Math.Max(0.1, expectedArea * 0.000002)
            && roadAreaError <= Math.Max(0.1, result.RoadAreaSquareMetres * 0.000002) && unmatchedOverlayEdges == 0 && overlayBoundary
            && surfaceError <= 0.0011 && displayError <= 0.051 && (nurbsSurfaces == 0 || surfaceSamples > 0) && displaySamples > 0
            && missingDisplayMeshes == 0 && displayAreaError <= Math.Max(0.1, expectedArea * 0.000002)
            && unmatchedBoundaryEdges == 0 && boundaryChordError <= 0.0011
            && missingSideDisplayMeshes == 0 && missingStepEdges == 0 && extraSideEdges == 0 && sideEdgeGap <= 0.000001
            && inwardSideTriangles == 0 && inconsistentSideNormals == 0
            && (effectiveDrop != 0 || sideObjects == 0);
        return new()
        {
            ["geometry_checks_passed"] = passed, ["invalid_objects"] = invalid, ["object_count"] = objects.Length,
            ["parapets_valid"] = parapetsValid,
            ["native_geometry_bytes"] = bytes,
            ["near_building_objects"] = nearBuildings, ["far_building_objects"] = farBuildings, ["unpartitioned_building_objects"] = allBuildings,
            ["checked_building_features"] = checkedIds.Count, ["non_solid_buildings"] = nonSolid,
            ["building_height_mismatches"] = wrongHeight, ["building_max_relative_volume_error"] = volumeError,
            ["expected_building_rejections"] = expectedRejections.ToArray(), ["building_rejections_match"] = expectedRejectedIds,
            ["rejected_buildings_preserve_xy"] = rejectedBuildingsPreserveXY,
            ["wrong_representation"] = wrongRepresentation, ["building_feature_accounting_complete"] = accounted == buildings.Count,
            ["near_surface_objects"] = nearSurfaces, ["far_surface_objects"] = farSurfaces, ["unpartitioned_surface_objects"] = allSurfaces,
            ["nurbs_surface_objects"] = nurbsSurfaces, ["mesh_surface_objects"] = meshSurfaces,
            ["optional_region_partition_passed"] = regionPartition, ["surface_count_accounting_passed"] = surfaceCounts,
            ["editable_boundary_objects"] = editableBoundaries,
            ["missing_surface_render_meshes"] = missingDisplayMeshes, ["surface_render_triangles"] = displayTriangles,
            ["surface_render_plan_area_error_m2"] = displayAreaError,
            ["unmatched_surface_partition_edges"] = unmatchedBoundaryEdges,
            ["max_actual_boundary_chord_error_m"] = boundaryChordError,
            ["expected_road_step_edges"] = expectedStepEdges, ["missing_road_step_side_edges"] = missingStepEdges,
            ["extra_road_side_edges"] = extraSideEdges, ["missing_road_side_render_meshes"] = missingSideDisplayMeshes,
            ["max_road_side_to_surface_edge_gap_m"] = sideEdgeGap,
            ["inward_road_side_triangles"] = inwardSideTriangles,
            ["road_side_nurbs_normal_samples"] = sideNormalSamples, ["inconsistent_road_side_nurbs_normals"] = inconsistentSideNormals,
            ["road_side_objects"] = sideObjects, ["land_plan_area_m2"] = landArea,
            ["road_plan_area_m2"] = roadArea, ["aoi_plan_area_m2"] = expectedArea,
            ["surface_plan_area_error_m2"] = areaError, ["road_plan_area_error_m2"] = roadAreaError,
            ["overlay_boundary_matches_source"] = overlayBoundary, ["unmatched_overlay_edges"] = unmatchedOverlayEdges, ["surface_elevation_samples"] = surfaceSamples,
            ["max_nurbs_surface_field_error_m"] = surfaceError, ["max_display_mesh_field_error_m"] = displayError, ["display_elevation_samples"] = displaySamples,
            ["non_upward_surface_triangles"] = nonUpward,
            ["model_counts"] = result, ["model_options"] = options,
            ["coverage_check_scope"] = "Primary ground projected coverage and naked-edge incidences; split mode Land+Road=AOI, overlay mode Land=AOI with independent source-road boundary/area check",
            ["elevation_check_scope"] = "Continuous graded terrain: upward display vertices/centroids and Brep 3x3/edge samples with role-specific road drop",
            ["active_document_changed"] = false
        };

        double ElevationError(Point3d point, double drop) => expectedHeight.Error(point / scale, drop);
        void CheckElevation(Point3d point, double drop, bool nurbs)
        {
            double error = ElevationError(point, drop);
            if (nurbs) { surfaceError = Math.Max(surfaceError, error); surfaceSamples++; }
            else { displayError = Math.Max(displayError, error); displaySamples++; }
        }
        void CheckSurfaceMesh(Mesh mesh, double drop, string role, string zone)
        {
            CheckBoundary(mesh, drop, role, zone);
            foreach (var point in mesh.Vertices.ToPoint3dArray()) CheckElevation(point, drop, false);
            foreach (var face in mesh.Faces)
            {
                var a = mesh.Vertices.Point3dAt(face.A); var b = mesh.Vertices.Point3dAt(face.B); var c = mesh.Vertices.Point3dAt(face.C);
                if (Vector3d.CrossProduct(b - a, c - a).Z <= 0) nonUpward++;
                CheckElevation((a + b + c) / 3, drop, false);
                if (face.IsQuad) { var d = mesh.Vertices.Point3dAt(face.D); CheckElevation((a + c + d) / 3, drop, false); }
            }
        }
        void CheckBoundary(Mesh mesh, double drop, string role, string zone)
        {
            foreach (var edge in NakedSegments(mesh))
            {
                var a = edge.A / scale; var b = edge.B / scale; var midpoint = (a + b) / 2;
                boundaryChordError = Math.Max(boundaryChordError, ElevationError(midpoint * scale, drop));
                string key = EdgeKey(a, b);
                var target = !options.SeparateRoads && role == "Road" ? overlayEdges : boundaryEdges;
                if (!target.TryGetValue(key, out var edges)) target[key] = edges = new();
                edges.Add((a, b, role, zone));
            }
        }
        void CheckSideBoundary(Mesh mesh)
        {
            foreach (var face in mesh.Faces)
            {
                var a = mesh.Vertices.Point3dAt(face.A) / scale; var b = mesh.Vertices.Point3dAt(face.B) / scale;
                var c = mesh.Vertices.Point3dAt(face.C) / scale;
                string key = a.X == b.X && a.Y == b.Y ? EdgeKey(a,c) : EdgeKey(a,b);
                if (!sideNormals.TryGetValue(key, out var normals)) sideNormals[key] = normals = new();
                normals.Add(Vector3d.CrossProduct(b-a,c-a));
            }
            foreach (var edge in NakedSegments(mesh))
            {
                var a = edge.A / scale; var b = edge.B / scale;
                if (a.X == b.X && a.Y == b.Y) continue;
                string key = EdgeKey(a, b);
                if (!sideEdges.TryGetValue(key, out var edges)) sideEdges[key] = edges = new();
                edges.Add((a, b));
            }
        }
        static string EdgeKey(Point3d a, Point3d b)
        {
            string Key(Point3d p) => (p.X == 0 ? 0 : p.X).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                + "," + (p.Y == 0 ? 0 : p.Y).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string ka = Key(a), kb = Key(b);
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + ":" + kb : kb + ":" + ka;
        }
        static double EndpointsDistance(Point3d a, Point3d b, Point3d c, Point3d d) =>
            Math.Min(Math.Max(a.DistanceTo(c), b.DistanceTo(d)), Math.Max(a.DistanceTo(d), b.DistanceTo(c)));
        bool OnOuter(Point3d a, Point3d b) =>
            Math.Abs(a.X - sw.X) < 0.000002 && Math.Abs(b.X - sw.X) < 0.000002 ||
            Math.Abs(a.X - ne.X) < 0.000002 && Math.Abs(b.X - ne.X) < 0.000002 ||
            Math.Abs(a.Y - sw.Y) < 0.000002 && Math.Abs(b.Y - sw.Y) < 0.000002 ||
            Math.Abs(a.Y - ne.Y) < 0.000002 && Math.Abs(b.Y - ne.Y) < 0.000002;
    }

    internal static bool BoundaryCoverage(IReadOnlyList<(Point3d A,Point3d B)> actual,
        IReadOnlyList<(Point3d A,Point3d B)> expected)
    {
        if (actual.Count == 0 || expected.Count == 0) return actual.Count == expected.Count;
        const double tolerance=0.000002;
        var tree=new NetTopologySuite.Index.Strtree.STRtree<(Point3d A,Point3d B)>();
        foreach (var edge in expected)
            tree.Insert(new NetTopologySuite.Geometries.Envelope(edge.A.X,edge.B.X,edge.A.Y,edge.B.Y),edge);
        tree.Build();
        foreach (var edge in actual)
        foreach (var point in new[] {edge.A,(edge.A+edge.B)/2,edge.B})
        {
            var envelope=new NetTopologySuite.Geometries.Envelope(point.X-tolerance,point.X+tolerance,point.Y-tolerance,point.Y+tolerance);
            bool found=false;
            foreach(var candidate in tree.Query(envelope))
            {
                var direction=candidate.B-candidate.A;
                double length=direction.X*direction.X+direction.Y*direction.Y;
                double t=length==0?0:Math.Clamp(((point.X-candidate.A.X)*direction.X+(point.Y-candidate.A.Y)*direction.Y)/length,0,1);
                if (Math.Pow(point.X-candidate.A.X-t*direction.X,2)+Math.Pow(point.Y-candidate.A.Y-t*direction.Y,2)<=tolerance*tolerance)
                {found=true;break;}
            }
            if(!found)return false;
        }
        return true;
    }

    private static IEnumerable<(Point3d A, Point3d B)> NakedSegments(Mesh mesh)
    {
        // GetNakedEdges joins segments into curves and becomes quadratic for a
        // large disconnected road network. Incidence needs only the segments.
        // Weld exact saved double positions in the audit, including vertices
        // duplicated for sharp normals, without modifying the output mesh.
        var vertices = mesh.Vertices.ToPoint3dArray();
        var positions = new Dictionary<Point3d,int>();
        var points = new List<Point3d>();
        var map = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            if (!positions.TryGetValue(vertices[i], out int id))
            { id = points.Count; positions.Add(vertices[i], id); points.Add(vertices[i]); }
            map[i] = id;
        }
        var counts = new Dictionary<(int A, int B),(int Count, int A, int B)>();
        foreach (var face in mesh.Faces)
        {
            Add(face.A,face.B); Add(face.B,face.C);
            if (face.IsQuad) { Add(face.C,face.D); Add(face.D,face.A); }
            else Add(face.C,face.A);
        }
        foreach (var edge in counts.Values)
            if (edge.Count == 1) yield return (points[edge.A],points[edge.B]);
        void Add(int first, int second)
        {
            int a = map[first], b = map[second];
            if (a == b) throw new InvalidOperationException("Surface mesh contains a collapsed edge.");
            var key = a < b ? (a,b) : (b,a);
            if (counts.TryGetValue(key, out var previous))
            {
                if (previous.Count > 1 || previous.A == a)
                    throw new InvalidOperationException("Surface mesh has a non-manifold or equally directed shared edge.");
                counts[key] = (2, previous.A, previous.B);
            }
            else counts[key] = (1,a,b);
        }
    }

    private static bool IsUpward(BrepFace face)=>face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid).Z>1e-8;

    private static double ProjectedArea(GeometryBase geometry, double scale)
    {
        if (geometry is Mesh mesh)
        {
            double sum = 0;
            foreach (var f in mesh.Faces)
            {
                var a = mesh.Vertices.Point3dAt(f.A); var b = mesh.Vertices.Point3dAt(f.B); var c = mesh.Vertices.Point3dAt(f.C);
                sum += Math.Abs(Vector3d.CrossProduct(b - a, c - a).Z) / 2;
                if (f.IsQuad) sum += Math.Abs(Vector3d.CrossProduct(c - a, mesh.Vertices.Point3dAt(f.D) - a).Z) / 2;
            }
            return sum;
        }
        if (geometry is Brep brep)
        {
            double sum=0;
            foreach(var face in brep.Faces.Where(IsUpward))
            {
                using var planar=face.DuplicateFace(false);
                if(!planar.Transform(Transform.PlanarProjection(Plane.WorldXY)))throw new InvalidOperationException("Upper face projection failed.");
                planar.Transform(Transform.Scale(Point3d.Origin,1/scale));
                using var mass=AreaMassProperties.Compute(planar,true,false,false,false)??throw new InvalidOperationException("Upper face projected area is unavailable.");
                sum+=mass.Area*scale*scale;
            }
            return sum;
        }
        throw new InvalidOperationException("Unexpected surface representation.");
    }
}
