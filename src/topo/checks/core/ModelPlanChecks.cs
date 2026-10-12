using Aha.Topo;
using Clipper2Lib;
using System.Text.Json;

namespace Aha.Topo.Checks;

internal static class ModelPlanChecks
{
    public static int Run()
    {
        int count=0;
        void Check(bool passed,string name)
        {if(!passed)throw new InvalidOperationException("model plan: "+name);count++;}
        var aoi=Rectangle(-20,-20,20,20);
        // Public model policy is physical metres, independent of document units.
        var policy=ModelPlanPolicy.Default;
        double width=policy.MinimumWidthMetres,change=policy.MaximumBoundaryChangeMetres;
        Check(width==.1&&policy.MinimumAreaSquareMetres==.01,"confirmed 100mm model policy");
        var fixtures=new (string Name,PathsD Input,PointD Probe,bool ContainsProbe)[]
        {
            ("normal 0.5m lane",Rectangle(-10,-.25,10,.25),new(0,0),true),
            ("minimum width lane",Rectangle(-10,-width/2,10,width/2),new(0,0),true),
            ("long sub-width lane",Rectangle(-10,-width/8,10,width/8),new(0,0),false),
            ("tiny island",Rectangle(-width/8,-width/8,width/8,width/8),new(0,0),false),
            ("tiny internal hole",Clipper.Difference(Rectangle(-5,-5,5,5),Rectangle(-width/8,-width/8,width/8,width/8),FillRule.NonZero,6),new(0,0),true),
            ("normal internal hole",Clipper.Difference(Rectangle(-5,-5,5,5),Rectangle(-1,-1,1,1),FillRule.NonZero,6),new(0,0),false),
            ("thin source triangle",new(){new(){new(16.963092,30.655929-20),new(16.963092,30.655930-20),new(16.895646,30.684567-20)}},new(16.94061,10.66548),false),
            ("AOI clipped normal lane",Rectangle(-30,-.5,30,.5),new(19,0),true)
        };
        foreach(var fixture in fixtures)
        {
            var source=JsonSerializer.Serialize(fixture.Input,new JsonSerializerOptions{IncludeFields=true});
            var result=ModelPlanGeometry.Build(fixture.Input,aoi);
            Check(JsonSerializer.Serialize(fixture.Input,new JsonSerializerOptions{IncludeFields=true})==source,fixture.Name+" preserves raw coordinates");
            Check(Contains(result.Roads,fixture.Probe)==fixture.ContainsProbe,fixture.Name+" keeps only model scale features");
            Check(Area(Clipper.Difference(result.Roads,aoi,FillRule.NonZero,6))<1e-9,fixture.Name+" stays in AOI");
            var land=Clipper.Difference(aoi,result.Roads,FillRule.NonZero,6);
            Check(Math.Abs(Area(land)+Area(result.Roads)-1600)<1e-7,fixture.Name+" road and land cover AOI once");
            Check(Area(Clipper.Intersect(result.Roads,land,FillRule.NonZero,6))<1e-9,fixture.Name+" road and land have no overlap");
            var reference=Clipper.Intersect(fixture.Input,aoi,FillRule.NonZero,6);
            // Independent area budget from the original perimeter and the
            // declared movement band, not from product-reported diagnostics.
            double perimeter=reference.Sum(p=>Enumerable.Range(0,p.Count).Sum(i=>Distance(p[i],p[(i+1)%p.Count])));
            double changed=Area(Clipper.Xor(reference,result.Roads,FillRule.NonZero,6));
            Check(changed<=2*perimeter*change+Math.PI*change*change*reference.Sum(p=>p.Count)+1e-6,fixture.Name+" has bounded changed area");
            var again=ModelPlanGeometry.Build(fixture.Input,aoi);
            Check(JsonSerializer.Serialize(result.Roads,new JsonSerializerOptions{IncludeFields=true})
                ==JsonSerializer.Serialize(again.Roads,new JsonSerializerOptions{IncludeFields=true}),fixture.Name+" same raw source reproduces exact model coordinates");
            var reordered=new PathsD(fixture.Input.AsEnumerable().Reverse().Select(path=>new PathD(path.AsEnumerable().Reverse())));
            var orderResult=ModelPlanGeometry.Build(reordered,aoi);
            Check(Area(Clipper.Xor(result.Roads,orderResult.Roads,FillRule.NonZero,6))<1e-7,fixture.Name+" is independent of input order and winding");
        }
        var normal=Rectangle(-5,-.25,5,.25);
        var spur=Clipper.BooleanOp(ClipType.Union,new PathsD(normal.Concat(Rectangle(-width/8,.25,width/8,5))),null,FillRule.NonZero,6);
        Check(!Contains(ModelPlanGeometry.Build(spur,aoi).Roads,new(0,4)),"narrow protrusion is removed from a wide road");
        var separated=Clipper.BooleanOp(ClipType.Union,new PathsD(Rectangle(-5,-1,-.5,1).Concat(Rectangle(.5,-1,5,1))),null,FillRule.NonZero,6);
        Check(!Contains(ModelPlanGeometry.Build(separated,aoi).Roads,new(0,0)),"normal gap is not bridged");
        foreach(bool contact in new[]{false,true})
        {
            var fixture=ModelPlanFixtures.Create(contact);
            var original=JsonSerializer.Serialize(fixture.Raw,new JsonSerializerOptions{IncludeFields=true});
            var model=ModelPlanGeometry.Build(fixture.Raw,fixture.Domain);
            Check(JsonSerializer.Serialize(fixture.Raw,new JsonSerializerOptions{IncludeFields=true})==original,"source contact coordinates stay unchanged");
            var combined=Clipper.BooleanOp(ClipType.Union,new PathsD(model.Roads.Concat(model.Land)),null,FillRule.NonZero,6);
            Check(Area(Clipper.Xor(combined,fixture.Domain,FillRule.NonZero,6))<1e-6,"one contact plan covers the complete AOI");
            Check(Area(Clipper.Intersect(model.Roads,model.Land,FillRule.NonZero,6))<1e-6,"one contact plan contains no role overlap");
        }
        // A subminimum boundary fragment outside the 50 mm band is assigned
        // to the adjoining road role without changing source geometry.
        var cutDomain=Rectangle(-2,-2,2,0);
        var cutLand=new PathsD{new PathD(FixtureData.Witness("model_cut").EnumerateArray()
            .Select(p => new PointD(p[0].GetDouble(),p[1].GetDouble())))};
        var cutRoad=Clipper.Difference(cutDomain,cutLand,FillRule.NonZero,6);
        var jsonOptions=new JsonSerializerOptions{IncludeFields=true};
        string rawBefore=JsonSerializer.Serialize(cutRoad,jsonOptions),domainBefore=JsonSerializer.Serialize(cutDomain,jsonOptions);
        var cutModel=ModelPlanGeometry.Build(cutRoad,cutDomain);
        Check(Area(cutLand)<.01,"AOI boundary fixture reproduces a subminimum land component");
        Check(rawBefore==JsonSerializer.Serialize(cutRoad,jsonOptions)&&domainBefore==JsonSerializer.Serialize(cutDomain,jsonOptions),
            "AOI boundary cleanup preserves raw road and domain coordinates");
        Check(cutModel.Land.All(p=>Clipper.Area(p)<=0||Clipper.Area(p)>=.01-1e-8),
            "AOI boundary leaves no subminimum independent land component");
        Check(cutModel.Land.Count == 0 && cutModel.Diagnostics.ReassignedComponents == 1,
            "AOI micro island is assigned to its edge-connected neighbour");
        var cutCombined=Clipper.BooleanOp(ClipType.Union,new PathsD(cutModel.Roads.Concat(cutModel.Land)),null,FillRule.NonZero,6);
        Check(Area(Clipper.Xor(cutCombined,cutDomain,FillRule.NonZero,6))<1e-8,
            "AOI boundary model covers the unchanged domain");
        Check(Area(Clipper.Intersect(cutModel.Roads,cutModel.Land,FillRule.NonZero,6))<1e-8,
            "AOI boundary model roles do not overlap");
        var cutBand=Clipper.Difference(
            Clipper.InflatePaths(cutRoad,.05,JoinType.Round,EndType.Polygon,2,6,.0001),
            Clipper.InflatePaths(cutRoad,-.05,JoinType.Round,EndType.Polygon,2,6,.0001),FillRule.NonZero,6);
        Check(Area(Clipper.Difference(Clipper.Xor(cutRoad,cutModel.Roads,FillRule.NonZero,6),cutBand,FillRule.NonZero,6))>1e-7 &&
            cutModel.Diagnostics.ReassignedAreaSquareMetres > 0 &&
            Math.Abs(cutModel.Diagnostics.ChangedAreaSquareMetres-Area(cutLand))<1e-7,
            "residual reassignment reports its area and is not rejected by the 50mm source band");
        var protectedRoad=Clipper.InflatePaths(cutRoad,-.049999,JoinType.Miter,EndType.Polygon,2,6);
        Check(Area(Clipper.Difference(protectedRoad,cutModel.Roads,FillRule.NonZero,6))<1e-7,
            "AOI boundary cleanup preserves the useful road interior");
        Check(JsonSerializer.Serialize(cutModel.Roads,jsonOptions)==JsonSerializer.Serialize(ModelPlanGeometry.Build(cutRoad,cutDomain).Roads,jsonOptions),
            "AOI boundary raw input reproduces the same model");
        return count;
    }

    internal static PathsD Rectangle(double x0,double y0,double x1,double y1)=>
        new(){new(){new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1)}};
    internal static double Area(PathsD paths)=>Math.Abs(Clipper.Area(paths));
    internal static bool Contains(PathsD paths,PointD point)
    {
        int winding=0;
        foreach(var path in paths)
        {
            var result=Clipper.PointInPolygon(point,path,6);
            if(result==PointInPolygonResult.IsOn)return true;
            if(result==PointInPolygonResult.IsInside)winding+=Clipper.Area(path)>0?1:-1;
        }
        return winding!=0;
    }
    private static double Distance(PointD a,PointD b)=>Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y));
}
