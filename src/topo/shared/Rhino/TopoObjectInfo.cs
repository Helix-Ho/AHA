using System.Text.Json.Nodes;
using Aha.Info;

namespace Aha.Topo;

internal static class TopoObjectInfo
{
    public static string Parcel(IEnumerable<GeoFeature> parcels, bool site = false, bool detailed = false)
    {
        var items = new JsonArray();
        foreach (var p in parcels)
        {
            var item = new JsonObject();
            if (p.Pnu.Length > 0) item["pnu"] = p.Pnu;
            if ((site || detailed) && p.Address.Length > 0) item["address"] = p.Address;
            if (p.ParcelLabel.Length > 0) item["label"] = p.ParcelLabel;
            if (p.LandCategoryCode is { } category) item["land_category_code"] = category;
            items.Add(item);
        }
        return InfoData.Create("parcel", site, new JsonObject { ["parcels"] = items }).ToJsonString();
    }

    public static string Building(GeoFeature feature, double? height = null, string? heightSource = null, bool detailed = false)
    {
        var data = new JsonObject();
        if (feature.BuildingId.Length > 0) data["building_id"] = feature.BuildingId;
        var parcel = new JsonObject();
        if (feature.Pnu.Length > 0) parcel["pnu"] = feature.Pnu;
        if (detailed && feature.Address.Length > 0) parcel["address"] = feature.Address;
        if (parcel.Count > 0) data["parcels"] = new JsonArray(parcel);
        if (feature.Floors is { } floors) data["floors"] = floors;
        if (height is { } h) data["height_m"] = h;
        if (!string.IsNullOrWhiteSpace(heightSource)) data["height_source"] = heightSource;
        data["classification"] = feature.BuildingClassification.ToString();
        if (detailed)
        {
            if (feature.UndergroundFloors is { } undergroundFloors) data["underground_floors"] = undergroundFloors;
            AddText(data, "building_position_code", feature.BuildingPositionCode);
            if (feature.BuildingDetails is { } details) AddBuildingDetails(data, details);
        }
        return InfoData.Create("building", false, data).ToJsonString();
    }

    private static void AddBuildingDetails(JsonObject data, BuildingDetails details)
    {
        AddText(data, "building_name", details.Name);
        AddText(data, "building_part_name", details.PartName);
        AddText(data, "building_name_en", details.EnglishName);
        AddText(data, "road_name", details.RoadName);
        if (details.RoadAddress.Length > 0)
            data["road_address"] = details.RoadAddress;
        else
        {
            AddText(data, "address_province", details.Province);
            AddText(data, "address_city_district", details.CityDistrict);
            AddText(data, "address_town", details.Town);
            AddText(data, "building_number", details.BuildingNumber);
        }
    }

    private static void AddText(JsonObject data, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) data[key] = value;
    }

    public static string Kind(string kind) => InfoData.Create(kind, false, new()).ToJsonString();
    public static string WithSite(string info, bool site)
    {
        var data = InfoData.Parse(info); data["site"] = site; return data.ToJsonString();
    }
}
