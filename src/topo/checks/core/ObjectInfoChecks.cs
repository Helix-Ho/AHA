using System.Text;
using System.Text.Json.Nodes;
using Aha.Info;
using Aha.Topo;

namespace Aha.Topo.Checks;

internal static class ObjectInfoChecks
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        const string pnu = "9900000000100310000";
        const string address = "가상시 예시구 예시동 1-1";
        var parcel = new GeoFeature { Pnu = pnu, Address = address, ParcelLabel = "31대", LandCategoryCode = 8 };
        var road = new GeoFeature { Pnu = "9900000000100320000", Address = "가상시 예시구 예시동 1-2", ParcelLabel = "32도", LandCategoryCode = 14 };
        foreach (var source in new[] { parcel, road })
        {
            var low = InfoData.Parse(TopoObjectInfo.Parcel(new[] { source }));
            var item = low["data"]!["parcels"]!.AsArray().Single()!.AsObject();
            Check(!low["site"]!.GetValue<bool>() && item["pnu"]!.GetValue<string>() == source.Pnu &&
                item["label"]!.GetValue<string>() == source.ParcelLabel && !item.ContainsKey("address"),
                "low-density parcel keeps source identity and category without address: " + source.LandCategoryCode);
            var high = InfoData.Parse(TopoObjectInfo.Parcel(new[] { source }, detailed: true));
            Check(high["data"]!["parcels"]![0]!["address"]!.GetValue<string>() == source.Address &&
                !high["site"]!.GetValue<bool>(), "near parcel address does not automatically designate a site: " + source.LandCategoryCode);
        }
        var selected = TopoObjectInfo.Parcel(new[] { parcel, road }, site: true);
        var xy = InfoData.Parse(TopoObjectInfo.WithSite(selected, false));
        Check(InfoData.Parse(selected)["site"]!.GetValue<bool>() && !xy["site"]!.GetValue<bool>() &&
            JsonNode.DeepEquals(InfoData.Parse(selected)["data"], xy["data"]) &&
            xy["data"]!["parcels"]![1]!["address"]!.GetValue<string>() == road.Address,
            "selected union and XY copy retain each source parcel address independently of surrounding density");
        var noAddress = InfoData.Parse(TopoObjectInfo.Parcel(new[] { new GeoFeature { Pnu = pnu, ParcelLabel = "31대" } }, detailed: true));
        Check(!noAddress["data"]!["parcels"]![0]!.AsObject().ContainsKey("address"),
            "a cadastral label or PNU never fabricates a full address");

        var details = new BuildingDetails("청사", "본관", "City Hall", "가상시", "예시구", "", "모형로", "110");
        var building = new GeoFeature
        {
            BuildingId = "9900000000100310000012345", Pnu = pnu, Address = address, Floors = 3,
            BuildingDetails = details, UndergroundFloors = 0, BuildingPositionCode = "0",
            BuildingClassification = BuildingClassificationStatus.Verified
        };
        var lowBuilding = InfoData.Parse(TopoObjectInfo.Building(building, 9, "floor_estimate"))["data"]!.AsObject();
        var highBuilding = InfoData.Parse(TopoObjectInfo.Building(building, 9, "floor_estimate", detailed: true))["data"]!.AsObject();
        Check(lowBuilding["parcels"]![0]!["pnu"]!.GetValue<string>() == pnu &&
            !lowBuilding["parcels"]![0]!.AsObject().ContainsKey("address") &&
            !lowBuilding.ContainsKey("building_name") && !lowBuilding.ContainsKey("road_address") &&
            lowBuilding["building_id"]!.GetValue<string>() == building.BuildingId && lowBuilding["floors"]!.GetValue<double>() == 3 &&
            lowBuilding["height_m"]!.GetValue<double>() == 9 && lowBuilding["height_source"]!.GetValue<string>() == "floor_estimate" &&
            lowBuilding["classification"]!.GetValue<string>() == "Verified",
            "far building retains identity, floors, model height and estimate basis without detailed source text");
        Check(highBuilding["parcels"]![0]!["address"]!.GetValue<string>() == address &&
            highBuilding["road_address"]!.GetValue<string>() == "가상시 예시구 모형로 110" &&
            highBuilding["road_name"]!.GetValue<string>() == "모형로" &&
            highBuilding["building_name"]!.GetValue<string>() == "청사" &&
            highBuilding["building_part_name"]!.GetValue<string>() == "본관" &&
            highBuilding["underground_floors"]!.GetValue<double>() == 0,
            "near building retains acquired details and distinguishes parcel address from road address, including explicit zero floors");
        Check(!highBuilding.ContainsKey("address_province") && !highBuilding.ContainsKey("building_number"),
            "complete road address avoids duplicating all its source components");
        var partialDetails = new BuildingDetails("", "", "", "가상시", "", "", "모형로", "110");
        var partial = InfoData.Parse(TopoObjectInfo.Building(new GeoFeature { BuildingDetails = partialDetails }, detailed: true))["data"]!.AsObject();
        Check(!partial.ContainsKey("road_address") && !partial.ContainsKey("parcels") &&
            !partial.ContainsKey("building_name") && partial["address_province"]!.GetValue<string>() == "가상시" &&
            partial["building_number"]!.GetValue<string>() == "110",
            "incomplete road address preserves available components without inventing a parcel or full address");
        var addressOnly = InfoData.Parse(TopoObjectInfo.Building(new GeoFeature { Address = address }, detailed: true))["data"]!["parcels"]![0]!.AsObject();
        Check(addressOnly["address"]!.GetValue<string>() == address && !addressOnly.ContainsKey("pnu"),
            "acquired parcel address remains available without a PNU in detailed building info");
        var managementOnly = InfoData.Parse(TopoObjectInfo.Building(new GeoFeature { BuildingId = building.BuildingId }, detailed: true))["data"]!.AsObject();
        Check(!managementOnly.ContainsKey("parcels") && !managementOnly.ContainsKey("underground_floors") &&
            !managementOnly.ContainsKey("building_position_code"), "management number and missing values never fabricate building or parcel information");
        foreach (var kind in new[] { "terrain", "road" })
        {
            var info = InfoData.Parse(TopoObjectInfo.Kind(kind));
            Check(info["kind"]!.GetValue<string>() == kind && info["data"]!.AsObject().Count == 0,
                "merged surface carries only its AHA kind: " + kind);
        }
        return passed;
    }
}
