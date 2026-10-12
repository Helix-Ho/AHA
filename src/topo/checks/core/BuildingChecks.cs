using System;
using Aha.Topo;

namespace Aha.Topo.Checks;

internal static class BuildingChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL: " + label);
            count++;
            Console.WriteLine("PASS: " + label);
        }
        void Invalid(ModelOptions options, string label)
        {
            var failed = false;
            try { BuildingRules.Validate(options); }
            catch (ArgumentException) { failed = true; }
            Check(failed, label);
        }

        var defaults = new ModelOptions();
        Check(defaults.UseNearRegion, "topography defaults to an editable Near partition");
        Check(!defaults.Contour && !defaults.EffectiveContour && defaults.ContourIntervalMetres == 1,
            "Contour is optional with a one metre default interval");
        Check(!defaults.SeparateFoundations, "building bodies and foundations default to one object");
        Check(defaults.Parapets && defaults.EffectiveParapets && defaults.ParapetWidthMetres == .3 && defaults.ParapetHeightMetres == .3,
            "Near parapets default to 300mm wide and 300mm high");
        Check(!new ModelOptions(UseNearRegion: false).EffectiveParapets && !new ModelOptions(Parapets: false).EffectiveParapets,
            "Parapets require Near and their own switch");
        foreach (double value in new[] { 0d, -1, double.NaN, double.PositiveInfinity })
        {
            Invalid(new ModelOptions(ParapetWidthMetres: value), "Parapet width must be positive and finite");
            Invalid(new ModelOptions(Parapets: false, ParapetHeightMetres: value), "Parapet height validation is shared even while disabled");
        }
        Check(new ModelOptions(Contour: true).EffectiveContour, "Contour is active for Near modelling");
        Invalid(new ModelOptions(UseNearRegion: false, Contour: true), "Contour requires Near modelling");
        Invalid(new ModelOptions(Contour: true, ContourIntervalMetres: 0), "Contour interval must be positive");
        Invalid(new ModelOptions(Contour: true, ContourIntervalMetres: double.NaN), "Contour interval must be finite");
        Invalid(new ModelOptions(Contour: true, ContourIntervalMetres: double.PositiveInfinity), "infinite Contour interval is rejected");
        Invalid(new ModelOptions(Contour: true, NearDepthMetres: 2), "Contour depth must exceed two support plates");
        Invalid(new ModelOptions(Contour: true, ContourIntervalMetres: double.MaxValue), "unrepresentable support depth is rejected");
        BuildingRules.Validate(new ModelOptions(Contour: true, NearDepthMetres: 2.001));
        BuildingRules.Validate(new ModelOptions(ContourIntervalMetres: double.NaN));
        Check(true, "Contour accepts a positive base depth and ignores unused interval");
        Check(!defaults.TerrainAsNurbs && !defaults.BuildingsAsNurbs, "terrain and buildings default independently to direct Mesh");
        Check(defaults.SeparateRoads && defaults.LowerRoads && defaults.EffectiveRoadDrop == .2, "roads default to split surfaces with a 0.2 metre step");
        Check(new ModelOptions(TerrainAsNurbs:true).BuildingsAsNurbs == false && new ModelOptions(BuildingsAsNurbs:true).TerrainAsNurbs == false,
            "terrain and building representation switches are independent");
        Check(new ModelOptions(LowerRoads:false,RoadDropMetres:.7).EffectiveRoadDrop == 0,
            "disabled road lowering keeps split road at the ground height");
        Check(new ModelOptions(SeparateRoads:false,LowerRoads:true,RoadDropMetres:.7).EffectiveRoadDrop == 0,
            "continuous terrain ignores road lowering and keeps road overlay at the same height");
        BuildingRules.Validate(new ModelOptions(LowerRoads:false,RoadDropMetres:double.NaN));
        BuildingRules.Validate(new ModelOptions(SeparateRoads:false,RoadDropMetres:double.NaN));
        Check(true, "inactive road drop is not validated or applied");
        BuildingRules.Validate(new ModelOptions(NearRatio: double.NaN, UseNearRegion: false));
        Check(true, "disabled near partition does not validate unused editing ratio");
        var measured = new GeoFeature { Height = 17.5, Floors = 100 };
        var measuredDecision = BuildingRules.Resolve(measured, defaults);
        Check(measuredDecision == new HeightDecision(17.5, "source_height"),
            "source height takes priority over floor estimate");
        Check(BuildingRules.Resolve(measured, new ModelOptions(9, 4, 10)) == measuredDecision,
            "changing fallbacks preserves known source height");

        var onlyFloors = new GeoFeature { Height = 0, Floors = 5 };
        Check(BuildingRules.Resolve(onlyFloors, defaults) == new HeightDecision(15, "floor_estimate"),
            "zero source height uses positive floors at default floor height");
        Check(BuildingRules.Resolve(onlyFloors, new ModelOptions(FloorHeight: 4)) == new HeightDecision(20, "floor_estimate"),
            "floor height change from 3 to 4 metres changes estimate");
        Check(BuildingRules.Resolve(new GeoFeature { Height = null, Floors = 5 }, defaults) ==
              new HeightDecision(15, "floor_estimate"), "missing source height uses floor estimate");

        var unknown = new GeoFeature { Height = 0, Floors = 0 };
        Check(BuildingRules.Resolve(unknown, defaults) == new HeightDecision(6, "default_estimate"),
            "zero height and zero floors use unknown-building default");
        Check(BuildingRules.Resolve(unknown, new ModelOptions(UnknownHeight: 9)) == new HeightDecision(9, "default_estimate"),
            "unknown-building default change from 6 to 9 metres is applied");
        Check(BuildingRules.Resolve(new GeoFeature(), defaults) == new HeightDecision(6, "default_estimate"),
            "missing height and floors remain labelled as an estimate");

        var projection = new LocalProjection(new(127, 37.5));
        var bounds = projection.Bounds(20);
        GeoFeature Footprint(double width, double height) => new()
        {
            Polygons = new() { new() { new() { projection.ToGeo(0, 0), projection.ToGeo(width, 0),
                projection.ToGeo(width, height), projection.ToGeo(0, height), projection.ToGeo(0, 0) } } }
        };
        void InvalidFootprint(GeoFeature feature, string label)
        {
            bool rejected = false;
            try { BuildingRules.IsEligibleFootprint(feature, projection, bounds, 0); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, label);
        }
        Check(!BuildingRules.IsEligibleFootprint(Footprint(9.999, 1), projection, bounds, defaults.MinArea),
            "footprint below 10 square metres is excluded");
        var thresholdFootprint = Footprint(10, 1);
        Check(BuildingRules.IsEligibleFootprint(thresholdFootprint, projection, bounds, Geo.Area(thresholdFootprint, projection)),
            "footprint exactly at the computed whole-area threshold is included");
        Check(BuildingRules.IsEligibleFootprint(Footprint(.001, 1), projection, bounds, 0),
            "zero minimum area keeps every positive-area footprint");
        InvalidFootprint(Footprint(0, 1),
            "zero minimum area still rejects degenerate zero-area geometry");
        var nonFiniteFootprint = Footprint(10, 1);
        nonFiniteFootprint.Polygons[0][0][1] = new(double.NaN, 37.5);
        InvalidFootprint(nonFiniteFootprint, "non-finite footprint geometry is rejected");

        Invalid(new ModelOptions(UnknownHeight: 0), "zero unknown-building height is rejected");
        Invalid(new ModelOptions(FloorHeight: 0), "zero floor height is rejected");
        Invalid(new ModelOptions(MinArea: -1), "negative area cutoff is rejected");
        Invalid(new ModelOptions(UnknownHeight: double.NaN), "NaN unknown-building height is rejected");
        Invalid(new ModelOptions(FloorHeight: double.PositiveInfinity), "infinite floor height is rejected");
        Invalid(new ModelOptions(MinArea: double.NaN), "NaN area cutoff is rejected");
        BuildingRules.Validate(new ModelOptions(NearRatio: 1.5, RoadDropMetres: 0, UseNearRegion: true));
        Check(true, "minimum editing ratio and zero road step are allowed");
        Invalid(new ModelOptions(NearRatio: 0, UseNearRegion: true), "zero editing ratio is rejected");
        Invalid(new ModelOptions(NearRatio: 1.49, UseNearRegion: true), "editing ratio below 1.5 is rejected");
        Invalid(new ModelOptions(NearRatio: -1, UseNearRegion: true), "negative editing ratio is rejected");
        Invalid(new ModelOptions(NearRatio: double.PositiveInfinity, UseNearRegion: true), "infinite editing ratio is rejected");
        Invalid(new ModelOptions(NearRatio: double.NaN, UseNearRegion: true), "NaN editing ratio is rejected");
        Check(new ModelOptions().NearDepthMetres==30, "near solid depth defaults to 30 metres");
        BuildingRules.Validate(new ModelOptions(NearDepthMetres: .01, UseNearRegion: true));
        Check(true,"positive near solid depth is allowed");
        Invalid(new ModelOptions(NearDepthMetres: 0, UseNearRegion: true),"zero solid depth is rejected");
        Invalid(new ModelOptions(NearDepthMetres: -1, UseNearRegion: true),"negative solid depth is rejected");
        Invalid(new ModelOptions(NearDepthMetres: double.NaN, UseNearRegion: true),"NaN solid depth is rejected");
        Invalid(new ModelOptions(NearDepthMetres: double.PositiveInfinity, UseNearRegion: true),"infinite solid depth is rejected");
        BuildingRules.Validate(new ModelOptions(NearDepthMetres: double.NaN, UseNearRegion: false));
        Check(true,"topography ignores unused near solid depth");
        Invalid(new ModelOptions(RoadDropMetres: -0.01), "negative road step is rejected");
        Invalid(new ModelOptions(RoadDropMetres: double.NaN), "NaN road step is rejected");
        return count;
    }
}
