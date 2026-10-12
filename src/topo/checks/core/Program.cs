using Aha.Topo.Checks;

try
{
    var terrainCount = TerrainChecks.Run();
    var contourCount = ExactNearRegionsChecks.Run() + ContourChecks.Run()+ContourPrecisionChecks.Run()+ContourSectionChecks.Run()+ProfileContactChecks.Run() + ExactContourProfileChecks.Run();
    if (FixtureData.Recorded) contourCount += RecordedExactNearRegionsChecks.Run();
    var dataCount = await DataChecks.Run();
    var buildingCount = BuildingChecks.Run() + ParapetProfileChecks.Run();
    var placementCount = BuildingPlacementChecks.Run();
    var coverageCount = TerrainCoverageChecks.Run();
    var partitionCount = PlanarPartitionChecks.Run();
    var boundarySourcesCount = PlanBoundarySourcesChecks.Run();
    var buildingSourceCount = await BuildingSourceChecks.Run();
    var objectInfoCount = ObjectInfoChecks.Run();
    var roadGeometryCount = RoadGeometryChecks.Run();
    var modelPlanCount = ModelPlanChecks.Run();
    var nearRoadCount = NearRoadChecks.Run();
    var editRegionCount = EditRegionChecks.Run();
    var roadGradingCount = RoadGradingChecks.Run();
    Console.WriteLine($"PASS: contour {contourCount}, terrain {terrainCount}, data/projection {dataCount}, building settings {buildingCount}, placement statistics {placementCount}, terrain coverage {coverageCount}, planar partition {partitionCount}, boundary sources {boundarySourcesCount}, building source {buildingSourceCount}, object info {objectInfoCount}, cadastral road geometry {roadGeometryCount}, model plan {modelPlanCount}, Near road cleanup {nearRoadCount}, site selection/editing region {editRegionCount}, road grading {roadGradingCount} checks.");
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error);
    Environment.ExitCode = 1;
}
