using System.Text.Json;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

// Regression measurements come from .local JSON at execution time; no acquired coordinates are compiled in.
internal static class RecordedContourGeometryChecks
{
    internal static Dictionary<string,object?> Run()
    {
        using var document=JsonDocument.Parse(File.ReadAllText(FixtureData.PathFor("recorded-contour-profiles.json")));
        var input=document.RootElement;
        var checks=new Dictionary<string,object?>();
        static EditBoundaryPoint Point(JsonElement value)=>new(value[0].GetDouble(),value[1].GetDouble());

        var shortIntersection=input.GetProperty("short_intersection");
        var points=shortIntersection.GetProperty("points").EnumerateArray().Select(Point).ToArray();
        foreach(double scale in new[]{1d,1000d})
        {
            var edges=points.Select((p,i)=>new ExactNearRegions.Edge(new(p,points[(i+1)%points.Length]),
                i,0,1,p,points[(i+1)%points.Length])).ToArray();
            var curves=ExactProfileGeometry.Curves(new(new(edges),Array.Empty<ExactNearRegions.Ring>()));
            try
            {
                var curve=curves.Single();curve.Transform(Transform.Scale(Point3d.Origin,scale));
                using var extrusion=Extrusion.Create(curve,scale,true);
                using var brep=extrusion?.ToBrep();
                using var volume=brep is null?null:VolumeMassProperties.Compute(brep,true,false,false,false,1e-12,1e-12*Math.Pow(scale,3));
                checks[$"scale_{scale}_short_intersection_span_preserves_solid"]=curve.IsValid&&curve.IsClosed&&
                    extrusion is {IsValid:true}&&brep is {IsValid:true,IsSolid:true,IsManifold:true}&&
                    volume is not null&&Math.Abs(volume.Volume/Math.Pow(scale,3)-shortIntersection.GetProperty("expected_volume").GetDouble())<1e-8;
            }
            finally{foreach(var curve in curves)curve.Dispose();}
        }

        var shortArc=input.GetProperty("short_arc");
        var sourceEdges=shortArc.GetProperty("edges").EnumerateArray().Select((item,id)=>
        {
            var endpoints=item.GetProperty("segment");
            EditBoundarySegment segment;
            if(item.TryGetProperty("circle",out var circle))
            {
                var angles=item.GetProperty("angles");
                segment=new(Point(endpoints[0]),Point(endpoints[1]),
                    new EditCircle("profile",circle[0].GetDouble(),circle[1].GetDouble(),circle[2].GetDouble()),
                    angles[0].GetDouble(),angles[1].GetDouble());
            }
            else segment=new(Point(endpoints[0]),Point(endpoints[1]));
            var parameters=item.GetProperty("parameters");var ends=item.GetProperty("ends");
            return new ExactNearRegions.Edge(segment,id,parameters[0].GetDouble(),parameters[1].GetDouble(),
                Point(ends[0]),Point(ends[1]));
        }).ToArray();
        foreach(double scale in new[]{1d,1000d})
        foreach(bool reverse in new[]{false,true})
        foreach(int start in new[]{0,4,7})
        {
            var ordered=Enumerable.Range(0,sourceEdges.Length).Select(i=>sourceEdges[(i+start)%sourceEdges.Length]);
            var edges=(reverse?ordered.Reverse().Select(e=>e.Reverse()):ordered).ToArray();
            var curves=ExactProfileGeometry.Curves(new(new(edges),Array.Empty<ExactNearRegions.Ring>()));
            try
            {
                var curve=curves.Single();curve.Transform(Transform.Scale(Point3d.Origin,scale));
                using var extrusion=Extrusion.Create(curve,scale,true);
                using var brep=extrusion?.ToBrep();
                using var volume=brep is null?null:VolumeMassProperties.Compute(brep,true,false,false,false,1e-12,1e-12*Math.Pow(scale,3));
                checks[$"scale_{scale}_reverse_{reverse}_start_{start}_short_arc_preserves_solid"]=curve.IsValid&&curve.IsClosed&&
                    brep is {IsValid:true,IsSolid:true,IsManifold:true}&&volume is not null&&
                    Math.Abs(volume.Volume/Math.Pow(scale,3)-shortArc.GetProperty("expected_volume").GetDouble())<1e-8;
            }
            finally{foreach(var curve in curves)curve.Dispose();}
        }
        checks["geometry_checks_passed"]=checks.Values.OfType<bool>().All(x=>x);
        if(!(bool)checks["geometry_checks_passed"]!)throw new InvalidOperationException("Contour recorded: "+
            string.Join(", ",checks.Where(p=>p.Value is false).Select(p=>p.Key)));
        return checks;
    }
}
