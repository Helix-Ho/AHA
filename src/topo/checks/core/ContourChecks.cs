using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class ContourChecks
{
    private static PathsD CleanDetails(PathsD input)
    {
        var regions=new ContourRegions();
        return regions.Difference(input,regions.DetailOmissions(input));
    }

    internal static int Run()
    {
        int count=0;var projection=new LocalProjection(new GeoPoint(127,37.5));
        var bounds=Bounds(-20,-20,20,20);
        var site=Feature(ContourPaths.Rectangle(-2,-2,2,2));
        var near=EditRegion.Create(site,projection,bounds,3);
        var sites=SiteSelection.FeaturePaths(site,projection);
        var field=Field((x,y)=>100);
        {
            var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true);
            Assert(plan.Plates.Count>0&&plan.BaseTop==98&&plan.Plates.All(p=>p.Top>p.Bottom),
                "flat superlevel preserves plate heights and the two-course support base");
        }
        {
            var ground=ContourPaths.Rectangle(0,0,100,100);
            var road=ContourPaths.Rectangle(10,10,20,20);
            var sliver=new PathsD { new PathD(FixtureData.Witness("overlay").GetProperty("thin")
                .EnumerateArray().Select(p=>new PointD(p[0].GetDouble(),p[1].GetDouble()))) };
            var source=ContourPaths.Union(new PathsD(road.Concat(sliver)));
            var exact=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("zone",50,50,100)}));
            var result=ContourPlan.OverlayTops(new[]{new ContourExposure("Land",ground,ground,26,27)},source,exact);
            Assert(result.Surfaces.Count==1 && ContourPaths.Area(ContourPaths.Difference(result.Surfaces[0].Profiles,road))<1e-10,
                $"post-contour overlay fragments use the shared minimum-detail reassignment: surfaces={result.Surfaces.Count}, area={result.Surfaces.Sum(s=>ContourPaths.Area(s.Profiles)):R}, changed={ContourPaths.Area(result.Changed):R}");
            Assert(Math.Abs(ContourPaths.Area(result.Changed)-ContourPaths.Area(sliver))<1e-10,
                "overlay reassignment records the actual changed planar area");
            Assert(Math.Abs(ContourPaths.Area(source)-100-ContourPaths.Area(sliver))<1e-9 && ContourPaths.Area(ground)==10000,
                "overlay cleanup preserves source roads and terrain profiles");
        }
        {
            var strip=ContourPaths.Rectangle(-10,0,-9.999999,4);
            var exact=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("zone",0,0,30)}));
            var exposure=new ContourExposure("Land",strip,strip,4,5);
            var overlay=ContourPlan.OverlayTops(new[]{exposure},strip,exactNear:exact);
            Assert(overlay.Surfaces.Count==0&&Math.Abs(ContourPaths.Area(overlay.Changed)-.000004)<1e-10,
                "isolated sub-detail road paint is assigned to the existing land below it");
            Assert(exposure.Profiles==strip&&ContourPaths.Area(strip)>0,
                "omitting road paint does not remove or distort its supporting terrain");
        }
        for(int rotation=1;rotation<=7;rotation++)
        {
            double angle=rotation*.137;
            var road=ContourPaths.Union(new(){new PathD(ContourPaths.Rectangle(-20,3,20,6)[0].Select(p=>
                new PointD(Math.Round(p.x*Math.Cos(angle)-p.y*Math.Sin(angle),6),Math.Round(p.x*Math.Sin(angle)+p.y*Math.Cos(angle),6))))});
            var plan=ContourPlan.Build(field,road,near,sites,1,.2,30,true);
            Assert(plan.GroundExposures.All(p=>p.Exact!.All(c=>c.Area>0)),"rotated coincident source boundaries retain owned exposure regions");
        }
        foreach(double drop in new[]{0d,.05,.2,1d,1.2,2d})
        {
            var roads=ContourPaths.Rectangle(-20,3,20,6);
            var building=new ContourBuilding("road-building",ContourPaths.Rectangle(-1,3.5,1,5),98,true);
            var plan=ContourPlan.Build(field,roads,near,sites,1,drop,30,true,new[]{building});
            double top=100-drop,bottom=Math.Ceiling(top-1e-10)-1;
            Assert(Math.Abs(plan.BuildingBottoms[building.Id]-bottom)<1e-9,"exposed road plate determines one-course excavation");
            Assert(plan.Plates.All(p=>p.Top>p.Bottom),"no zero-thickness objects after complete road-course removal");
            Assert(plan.Plates.Where(p=>p.Role!="Base").All(p=>ContourPaths.Area(ContourPaths.Intersect(p.Profiles,sites))<1e-9),"site void continues through both supports");
            Assert(plan.Plates.Where(p=>p.Role=="Base").Sum(p=>ContourPaths.Area(ContourPaths.Intersect(p.Profiles,sites)))>15.99,"base supports selected site");
            Assert(plan.Plates.Where(p=>p.Top>bottom+1e-9).All(p=>ContourPaths.Area(ContourPaths.Intersect(p.Profiles,building.Footprint))<1e-9),"foundation passes completely through each intersecting upper plate");
            Assert(Math.Abs(plan.MinimumExposedElevation(building.Footprint)!.Value-top)<1e-9,"exposed query ignores later excavation");
            var crossing=ContourPaths.Rectangle(-2,3.5,2,5);
            Assert(Math.Abs(plan.MinimumGroundElevation(crossing)!.Value-bottom)<1e-9,"ordinary crossing building sees actual excavated ground floor");
            Assert(Math.Abs(plan.BaseTop-(plan.DetailLevel-2))<1e-9&&Math.Abs(plan.BaseBottom-(plan.DetailLevel-30))<1e-9,"two support courses lie within specified depth");
        }
        {
            var a=new ContourBuilding("a",ContourPaths.Rectangle(-4,3,-1,6),99,true);
            var b=new ContourBuilding("b",ContourPaths.Rectangle(-2,4,1,7),97.4,false);
            var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,new[]{a,b});
            Assert(plan.BuildingBottoms["a"]==97&&plan.BuildingBottoms["b"]==97,"overlap group retains both buildings and one grid-aligned foundation");
            Assert(plan.Plates.Any(p=>p.Top>97&&ContourPaths.Area(ContourPaths.Intersect(p.Profiles,ContourPaths.Difference(b.Footprint,a.Footprint)))>0),"non-excavated building does not cut its exclusive footprint");
        }
        {
            var plan=ContourPlan.Build(Field((x,y)=>100+.2*x-.1*y),new(),near,sites,1,0,30,true);
            foreach(var exposure in plan.Exposures)
            {
                var point=PlanarTriangulation.ToGridGeometry(exposure.Profiles).InteriorPoint.Coordinate;
                double source=100+.2*point.X/1e6-.1*point.Y/1e6;
                Assert(exposure.Top<=source+1e-5&&source-exposure.Top<=1.011,"planar slope terraces floor source elevation");
            }
            foreach(var plate in plan.Plates.Where(p=>p.Bottom>=plan.DetailLevel))
            {
                var below=plan.Plates.Where(p=>Math.Abs(p.Top-plate.Bottom)<1e-9).SelectMany(p=>p.Exact!).ToArray();
                var unsupported=new ExactNearRegions(near).Split(plate.SourceProfiles,plate.Excluded.Concat(below).ToArray()).Near;
                Assert(unsupported.Sum(p=>p.Area)<1e-7,"every upper plate is supported by lower material");
            }
        }
        {
            var opening=ContourPaths.Rectangle(-3,3,-1,5);
            var outer=ContourPaths.Rectangle(-4,2,0,6);var footprint=ContourPaths.Difference(outer,opening);
            var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,new[]{new ContourBuilding("courtyard",footprint,99,true)});
            Assert(plan.Plates.Any(p=>p.Bottom==99&&ContourPaths.Area(ContourPaths.Intersect(p.Profiles,opening))>3.99),"building courtyard remains unexcavated");
            var overlay=ContourPlan.Build(field,outer,near,sites,1,.2,30,false);
            Assert(overlay.Plates.All(p=>p.Role!="Road")&&overlay.RoadDrop==0,"overlay roads do not alter continuous land plates");
        }
        {
            var spike=ContourPaths.Rectangle(0,0,4,4);spike.AddRange(ContourPaths.Rectangle(1.98,4,2.02,5));
            var clean=CleanDetails(ContourPaths.Union(spike));
            Assert(ContourPaths.Area(ContourPaths.Intersect(clean,ContourPaths.Rectangle(1.98,4.01,2.02,5)))<1e-9,"sub-100mm positive spike is omitted rather than merged as a protrusion");
            var hole=ContourPaths.Rectangle(1.98,1,2.02,3);
            clean=CleanDetails(ContourPaths.Difference(ContourPaths.Rectangle(0,0,4,4),hole));
            Assert(ContourPaths.Area(ContourPaths.Intersect(clean,hole))<1e-9,"narrow negative courtyard is not filled");
        }
        {
            var saddle=Field((x,y)=>100+.04*x*y);
            var plan=ContourPlan.Build(saddle,new(),near,new(),1,0,30,true);
            Assert(plan.Plates.Count>0&&plan.Plates.All(p=>p.Top>p.Bottom),"saddle threshold vertices and crossing branches produce positive plates");
            foreach(var exposure in plan.Exposures)
            {
                var seed=PlanarTriangulation.ToGridGeometry(exposure.Profiles).InteriorPoint.Coordinate;
                double actual=100+.04*(seed.X/1e6)*(seed.Y/1e6);
                Assert(actual-exposure.Top>=-plan.ApproximationHeightError-1e-5,"bilinear saddle contour respects height approximation bound");
            }
            var fixedBottom=new ContourBuilding("ordinary",ContourPaths.Rectangle(-4,3,-1,6),98.7,false);
            var coarse=ContourPlan.Build(Field((x,y)=>100.3),new(),near,sites,3,0,30,true,new[]{fixedBottom});
            Assert(coarse.BuildingBottoms["ordinary"]==98,"nonexcavated foundation stays one metre below coarse actual terrace");
        }
        {
            var cuts=new[]{new ContourBuilding("left",ContourPaths.Rectangle(-4,3,-.015,6),99,true),
                new ContourBuilding("right",ContourPaths.Rectangle(.015,3,4,6),99,true)};
            var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,cuts);
            var sliver=ContourPaths.Rectangle(-.014,3.2,.014,5.8);
            Assert(plan.Plates.Where(p=>p.Bottom>=99).All(p=>ContourPaths.Area(ContourPaths.Intersect(p.Profiles,sliver))<1e-8),
                "two cutters do not leave a 30mm positive sliver in the exposed plate");
            Assert(plan.Plates.Any(p=>p.Top==99&&ContourPaths.Area(ContourPaths.Intersect(p.Profiles,sliver))>.07),
                "omitted cutter sliver exposes the intact lower support plate");
            Assert(plan.MinimumGroundElevation(sliver)==99&&plan.MinimumExposedElevation(sliver)==100,
                "actual ground includes post-cut omitted positive detail while placement input keeps precut terraces");
            Assert(plan.GroundExposures.Where(e=>e.Top>99).All(e=>ContourPaths.Area(ContourPaths.Intersect(e.Profiles,sliver))<1e-8),
                "road overlays and ground queries never use removed upper material as exposed ground");
            var narrowCourt=ContourPaths.Rectangle(-.015,3.5,.015,5.5);
            var shell=ContourPaths.Difference(ContourPaths.Rectangle(-3,3,3,6),narrowCourt);
            plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,new[]{new ContourBuilding("court",shell,99,true)});
            Assert(plan.Plates.Any(p=>p.Bottom==99&&ContourPaths.Area(ContourPaths.Intersect(p.Profiles,narrowCourt))>.05999),
                "post-cut positive cleanup preserves even a narrow source courtyard");
        }
        {
            // The cutter and this Site-touching building share only an edge.
            // Removing the 30mm land strip exposes z=96 rather than z=99.
            var cut=new ContourBuilding("cut",ContourPaths.Rectangle(-4,3,-.03,6),99.3,true);
            var ordinary=new ContourBuilding("ordinary",ContourPaths.Rectangle(-.03,1.5,1,5),99.3,false);
            var road=ContourPaths.Rectangle(0,-20,20,20);
            var plan=ContourPlan.Build(Field((x,y)=>100.3),road,near,sites,3,0,30,true,new[]{cut,ordinary});
            Assert(plan.MinimumExposedElevation(ordinary.Footprint)==99&&plan.MinimumGroundElevation(ordinary.Footprint)==96,
                "postcut role-strip removal changes the actual ground below a noncut building");
            Assert(plan.BuildingBottoms["ordinary"]==95&&plan.BuildingBottoms["cut"]==96,
                "noncut Near foundation uses actual ground minus one metre without deepening its neighbour's cut");
            var reversed=ContourPlan.Build(Field((x,y)=>100.3),road,near,sites,3,0,30,true,new[]{ordinary,cut});
            Assert(plan.BuildingBottoms.All(item=>reversed.BuildingBottoms[item.Key]==item.Value),
                "actual-ground foundation resolution is independent of building order");
        }
        {
            var a=new ContourBuilding("a",ContourPaths.Rectangle(-5.5,3,-4,5),100.5,true);
            var b=new ContourBuilding("b",ContourPaths.Rectangle(-1,-6,-.03,6),96,true);
            var multi=new PathsD(ContourPaths.Rectangle(-5,4,-4.5,4.5).Concat(ContourPaths.Rectangle(-.03,3.5,1,5)));
            var ordinary=new ContourBuilding("member",multi,100.75,false);
            var road=ContourPaths.Rectangle(0,-20,20,20);
            var plan=ContourPlan.Build(Field((x,y)=>100+.5*y),road,near,new(),1,0,30,true,new[]{a,b,ordinary});
            Assert(plan.BuildingBottoms["a"]==96&&plan.BuildingBottoms["member"]==96&&plan.BuildingBottoms["b"]==96,
                "lower actual ground propagates to an overlapping cut group's shared floor without subtracting one metre from its own cut");
            Assert(plan.BuildingBottoms["a"]==96&&
                plan.Plates.Where(p=>p.Top>96).All(p=>ContourPaths.Area(ContourPaths.Intersect(p.Profiles,a.Footprint))<1e-9),
                "a changed shared cut floor rebuilds the matching full-profile plate holes");
            Assert(plan.MinimumGroundElevation(a.Footprint)==96&&plan.Plates.Any(p=>p.Top==96&&ContourPaths.Area(ContourPaths.Intersect(p.Profiles,a.Footprint))>2.99),
                "the settled cut floor has supporting material rather than a self-deepening loop");
            var reversed=ContourPlan.Build(Field((x,y)=>100+.5*y),road,near,new(),1,0,30,true,new[]{ordinary,b,a});
            Assert(plan.BuildingBottoms.All(item=>reversed.BuildingBottoms[item.Key]==item.Value),
                "cut-group fixed point is independent of input order");
        }
        {
            var originalSites=new PathsD(sites.Select(p=>new PathD(p)));
            var nearSource=new PathsD(near.Paths.Select(p=>new PathD(p)));
            ContourPlan? first=null;
            for(int run=0;run<3;run++)
            {
                double angle=run==1?.63:.29;
                var footprint=new PathsD{new PathD(ContourPaths.Rectangle(-2,4,2,7)[0].Select(p=>
                    new PointD(Math.Round(p.x*Math.Cos(angle)-p.y*Math.Sin(angle),6),Math.Round(p.x*Math.Sin(angle)+p.y*Math.Cos(angle),6))))};
                var building=new ContourBuilding("rotated",footprint,99,true);
                var plan=ContourPlan.Build(field,new(),near,sites,1,0,30,true,new[]{building});
                Assert(plan.BuildingBottoms[building.Id]==99&&plan.MinimumGroundElevation(footprint)==99,
                    "rotated foundation sees its intended supporting plate without self-excavation drift");
                Assert(plan.Plates.Where(p=>p.Top>99).All(p=>ContourPaths.Area(ContourPaths.Intersect(p.Profiles,footprint))<1e-9),
                    "rotated footprint is completely absent from the cut upper plate");
                if(run==0)first=plan;
                if(run==2)
                    Assert(plan.Plates.Select(p=>(p.Role,p.Bottom,p.Top,Area:Math.Round(p.Exact!.Sum(c=>c.Area),9)))
                        .SequenceEqual(first!.Plates.Select(p=>(p.Role,p.Bottom,p.Top,Area:Math.Round(p.Exact!.Sum(c=>c.Area),9)))),
                        "A-B-A preparations do not leak omissions, cut floors or cached regions");
            }
            Assert(sites.Select((p,i)=>p.SequenceEqual(originalSites[i])).All(x=>x)&&near.Paths.Select((p,i)=>p.SequenceEqual(nearSource[i])).All(x=>x),
                "foundation cleanup preserves the supplied site and Near source vertices");
        }
        return count;
        void Assert(bool condition,string name){count++;if(!condition)throw new InvalidOperationException("Contour: "+name);}
        GeoBounds Bounds(double x0,double y0,double x1,double y1){var a=projection.ToGeo(x0,y0);var b=projection.ToGeo(x1,y1);return new(a.Lon,a.Lat,b.Lon,b.Lat);}
        GeoFeature Feature(PathsD paths)=>new(){Id="site",Polygons=new(){paths.Select(p=>p.Append(p[0]).Select(v=>projection.ToGeo(v.x,v.y)).ToList()).ToList()}};
        TerrainField Field(Func<double,double,double> f)
        {
            var empty=TerrainField.FromSamples(bounds,projection,4,4,new double[25]);
            return TerrainField.FromSamples(bounds,projection,4,4,Enumerable.Range(0,5).SelectMany(y=>Enumerable.Range(0,5).Select(x=>f(empty.ModelX(x),empty.ModelY(y)))).ToArray());
        }
    }
}
