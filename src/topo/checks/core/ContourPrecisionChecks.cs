using Clipper2Lib;

namespace Aha.Topo.Checks;

internal static class ContourPrecisionChecks
{
    private static PathsD CleanDetails(PathsD input)
    {
        var regions=new ContourRegions();
        return regions.Difference(input,regions.DetailOmissions(input));
    }

    internal static int Run()
    {
        int count=SiteSplitDetails()+ExposureUpdates();
        {
            var projection=new LocalProjection(new GeoPoint(127,37.5));
            var sw=projection.ToGeo(-200,-100);var ne=projection.ToGeo(200,100);
            var bounds=new GeoBounds(sw.Lon,sw.Lat,ne.Lon,ne.Lat);
            GeoFeature Site(double x)=>new(){Id=$"site-{x}",Polygons=new(){new(){new(){
                projection.ToGeo(x-2,-2),projection.ToGeo(x+2,-2),projection.ToGeo(x+2,2),
                projection.ToGeo(x-2,2),projection.ToGeo(x-2,-2)}}}};
            var selection=SiteSelection.Create(new[]{Site(-150),Site(150)},projection);
            var near=EditRegion.Create(selection,projection,bounds,3);
            ContourPlan Model(double farHeight)
            {
                var heights=Enumerable.Range(0,11).SelectMany(y=>Enumerable.Range(0,21)
                    .Select(x=>x>=8&&x<=12?farHeight:100.25)).ToArray();
                return ContourPlan.Build(TerrainField.FromSamples(bounds,projection,20,10,heights),
                    new(),near,new(),.01,0,30,true);
            }
            var flat=Model(100.25);
            foreach(double farHeight in new[]{350.25,-150.25})
            {
                var distant=Model(farHeight);
                Assert(flat.DetailLevel==distant.DetailLevel&&flat.Plates.Count==distant.Plates.Count&&
                    flat.Plates.Zip(distant.Plates).All(p=>p.First.Bottom==p.Second.Bottom&&p.First.Top==p.Second.Top&&
                        Same(p.First.Profiles,p.Second.Profiles)),
                    "Far relief between independent Near domains does not change contour levels, base or plates");
            }
        }
        {
            var projection=new LocalProjection(new GeoPoint(127,37.5));
            var bounds=projection.Bounds(50);
            GeoFeature Site(double x)=>new(){Id=$"support-{x}",Polygons=new(){new(){new(){
                projection.ToGeo(x-2,-2),projection.ToGeo(x+2,-2),projection.ToGeo(x+2,2),
                projection.ToGeo(x-2,2),projection.ToGeo(x-2,-2)}}}};
            var near=EditRegion.Create(SiteSelection.Create(new[]{Site(-30),Site(30)},projection),projection,bounds,3);
            var heights=Enumerable.Range(0,11).SelectMany(y=>Enumerable.Range(0,11)
                .Select(x=>100+.05*(-50+10*y)*Math.Sin((-50+10*x)/10d))).ToArray();
            var field=TerrainField.FromSamples(bounds,projection,10,10,heights);
            var box=Clipper.GetBounds(near.Paths);
            var full=new ContourLevels(field,.1,ContourPaths.Rectangle(box.left,box.top,box.right,box.bottom));
            var local=new ContourLevels(field,.1,near.Paths);
            for(long level=985;level<=1015;level++)
            {
                var expected=ContourPaths.Intersect(full.At(level),near.Paths);
                var actual=ContourPaths.Intersect(local.At(level),near.Paths);
                Assert(ContourPaths.Area(ContourPaths.Difference(expected,actual))<1e-10&&
                    ContourPaths.Area(ContourPaths.Difference(actual,expected))<1e-10,
                    "Near section retains neighbouring subdivision stations when unrelated Far triangles are omitted");
            }
        }
        {
            var rectangle=new PathsD{new PathD{new(54.899801,-11.717707),new(-36.871313,42.329647),
                new(-54.899801,11.717707),new(36.871313,-42.329647)}};
            var originalRectangle=ContourPaths.Union(rectangle);
            var current=originalRectangle;
            for(int repeat=0;repeat<10;repeat++)
            {
                current=CleanDetails(current);
                Assert(Same(originalRectangle,current),"detail cleanup preserves a broad rotated rectangle without accumulating boundary loss");
            }
        }
        {
            var regions=new ContourRegions();var omissions=new ContourOmissions(regions);
            omissions.Add(ContourPaths.Rectangle(0,0,1,1));
            omissions.Add(ContourPaths.Rectangle(100,100,102,102));
            var near=omissions.Subtract(ContourPaths.Rectangle(-1,-1,2,2));
            Assert(Math.Abs(ContourPaths.Area(near)-8)<1e-10,"omission lookup subtracts only intersecting material");
            omissions.Add(ContourPaths.Rectangle(.5,0,1.5,1));
            near=omissions.Subtract(ContourPaths.Rectangle(-1,-1,2,2));
            Assert(Math.Abs(ContourPaths.Area(near)-7.5)<1e-10,"overlapping omissions merge without double subtraction");
            var later=omissions.Subtract(ContourPaths.Rectangle(99,99,103,103));
            Assert(Math.Abs(ContourPaths.Area(later)-12)<1e-10,"omission outside an earlier plate remains available to a later one");
        }
        {
            var cornerSource=ContourPaths.Union(new PathsD{new(){new(-101.011275,122.972756),
                new(-111.725539,127.536734),new(-111.424338,126.672752),new(-100,115),new(-98,120)}});
            var regions=new ContourRegions();
            var loss=regions.DetailOmissions(cornerSource);
            Assert(loss.Count>0&&Clipper.GetBounds(loss).right<-111,
                "removing an acute detail does not create an omission tail along its long retained edge");
            Assert(ContourPaths.Area(loss)>0&&ContourPaths.Area(loss)<.01,
                "a real small corner is retained as an omission without an area rejection threshold");
            var cut=ContourPaths.Rectangle(-111.725539,125,-90,130);
            var directLoss=regions.NewDetailOmissions(cornerSource,cut);
            var cornerResult=regions.Difference(cornerSource,directLoss);
            Assert(Clipper.GetBounds(directLoss).right<-111&&
                Same(cornerResult,ContourPaths.Difference(cornerSource,directLoss)),
                "cut cleanup returns its original local omission rather than subtracting rounded result boundaries again");
            var originalArea=ContourPaths.Area(cornerResult);
            for(int repeat=0;repeat<10;repeat++)cornerResult=regions.Difference(cornerResult,regions.NewDetailOmissions(cornerResult,cut));
            Assert(Math.Abs(ContourPaths.Area(cornerResult)-originalArea)<1e-10,
                "repeating resolved corner cleanup does not accumulate removal");
        }
        {
            var clip=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("zone",0,0,10)}));
            var left=clip.Split(ContourPaths.Rectangle(-3,-2,0,2)).Near.Single();
            Assert(!clip.Intersects(ContourPaths.Rectangle(0,-1,2,1),left),"exact foundation query rejects a shared edge");
            Assert(!clip.Intersects(ContourPaths.Rectangle(0,2,2,3),left),"exact foundation query rejects point contact");
            Assert(clip.Intersects(ContourPaths.Rectangle(-.000001,-1,2,1),left),"exact foundation query retains a real narrow positive intersection");
            Assert(!clip.Intersects(ContourPaths.Rectangle(1,1,2,2),left),"exact foundation query rejects disjoint domains");
        }
        {
            var corner=ContourPaths.Union(new(){new(){new(83.267652,177.506214),new(96.421237,179.107511),
                new(96.440906,179.168252),new(93.834717,181.583972),new(80.966063,180.104479)}});
            var regions=new ContourRegions();
            var distantCut=ContourPaths.Rectangle(0,0,1,1);
            var cornerClean=regions.Difference(corner,regions.NewDetailOmissions(corner,distantCut));
            Assert(Same(corner,cornerClean),"a remote cut does not reopen an existing road corner or rotate its long shared edge");
            var strip=ContourPaths.Rectangle(0,0,.03,5);
            var cut=ContourPaths.Rectangle(.03,0,2,5);
            Assert(regions.Difference(strip,regions.NewDetailOmissions(strip,cut)).Count==0,"a newly cut 30mm strip is still omitted");
        }
        {
            var boundary=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("zone",0,0,10)}));
            var actual=boundary.Split(ContourPaths.Rectangle(-20,-20,20,20),includeFar:false).Near;
            var sample=new PathsD(actual.SelectMany(c=>c.Sample()));
            var plate=new ContourPlate("Land",sample,sample,0,1){Exact=actual};
            var exposure=new ContourExposureCache().Build(new[]{plate},new(),boundary,new ContourRegions());
            Assert(Math.Abs(exposure.Sum(e=>e.Exact!.Sum(c=>c.Area))-100*Math.PI)<1e-9,
                "ground visibility uses the owned exact plate rather than reinterpreting its sampled profile");
            var upper=plate with {Bottom=1,Top=2};
            exposure=new ContourExposureCache().Build(new[]{plate,upper},new(),boundary,new ContourRegions());
            Assert(exposure.Count==1&&exposure[0].Top==2,
                "coincident exact courses fully cover their shared top without sampled residual strips");
        }
        {
            var boundary=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("zone",0,0,Math.Sqrt(72))}));
            var query=ContourPaths.Rectangle(-10,-6,0,0);
            var cut=boundary.Split(ContourPaths.Rectangle(-6,-6,-4,0),includeFar:false).Near;
            var forward=boundary.Split(query,cut,includeFar:false).Near;
            var reverse=boundary.Split(ContourPaths.Union(new PathsD(query.Select(r=>new PathD(r.AsEnumerable().Reverse())))),cut,includeFar:false).Near;
            Assert(forward.All(c=>c.Area>0)&&Math.Abs(forward.Sum(c=>c.Area)-reverse.Sum(c=>c.Area))<1e-10,
                "a known circle endpoint shares one event with all collinear source segments");
        }
        using(var fixture=System.Text.Json.JsonDocument.Parse(File.ReadAllText(FixtureData.PathFor("flat-winding-region.json"))))
        {
            var flat=new PathsD(fixture.RootElement.EnumerateArray().Select(r=>new PathD(r.EnumerateArray().Select(p=>new PointD(p[0].GetDouble(),p[1].GetDouble())))));
            var box=ContourPaths.Rectangle(-1000,-1000,1000,1000);
            var region=ContourPaths.Intersect(flat,box);
            Assert(PlanarTriangulation.ToGridGeometry(region).IsValid,"raw NonZero rings are normalized before hole ownership");
            Assert(Same(region,ContourPaths.Union(flat)),"clipping to a containing domain preserves the full winding region");
        }
        var collapsedWitness=FixtureData.Witness("collapsed_hole");
        PathD ReadPath(string name)=>new(collapsedWitness.GetProperty(name).EnumerateArray()
            .Select(p=>new PointD(p[0].GetDouble(),p[1].GetDouble())));
        var outer=ReadPath("outer");
        var collapsedHole=ReadPath("hole");
        var source=new PathsD{outer,collapsedHole};
        var original=source.Select(path=>path.ToArray()).ToArray();
        var normalized=ContourPaths.Union(source);
        Assert(normalized.Count==1,"sub-grid triangular hole collapses within the established planar precision");
        Assert(source.Select((path,index)=>path.SequenceEqual(original[index])).All(x=>x),"normalization does not modify original profile input");
        for(int repeat=0;repeat<5;repeat++)
        {
            var again=ContourPaths.Union(normalized);
            Assert(Same(normalized,again),"computed-profile normalization is idempotent");
            normalized=again;
        }
        var court=ContourPaths.Rectangle(-.015,3.5,.015,5.5);
        var courtyard=ContourPaths.Difference(ContourPaths.Rectangle(-3,3,3,6),court);
        var clean=ContourPaths.Union(courtyard);
        Assert(clean.Count==2&&ContourPaths.Area(ContourPaths.Intersect(clean,court))<1e-12,
            "real 30mm courtyard remains empty after normalization");
        var contact=new PathsD{new PathD{new(0,0),new(10,0),new(10,5)},
            new PathD{new(0,0),new(8,2),new(8,1)}};
        clean=ContourPaths.Union(contact);
        Assert(clean.Count==2&&Math.Abs(ContourPaths.Area(clean)-ContourPaths.Area(contact))<1e-12,
            "valid acute-corner point contact retains its area and hole");
        var site=ContourPaths.Rectangle(-2,-2,2,2);
        var touchingHole=ContourPaths.Rectangle(-4,2,0,6);
        var land=ContourPaths.Difference(ContourPaths.Rectangle(-10,-10,10,10),touchingHole);
        var combined=ContourPaths.Union(new PathsD(land.Concat(site)));
        Assert(Math.Abs(ContourPaths.Area(combined)-384)<1e-12&&ContourPaths.Intersect(combined,touchingHole).Count==0,
            "a hole touching a smaller shell belongs to the shell containing its entire area");
        var duplicate=ContourPaths.Union(new PathsD(land.Concat(land)));
        Assert(Math.Abs(ContourPaths.Area(duplicate)-384)<1e-12&&ContourPaths.Intersect(duplicate,touchingHole).Count==0,
            "duplicate compound polygons keep their original negative hole winding");
        Assert(ContourPaths.Difference(normalized,normalized).Count==0,"identical canonical polygons subtract to empty");
        {
            var regions=new ContourRegions();var owned=regions.Union(duplicate);
            Assert(regions.Intersect(owned,new()).Count==0&&regions.Intersect(new(),owned).Count==0&&
                Same(regions.Difference(owned,new()),owned)&&regions.Difference(new(),owned).Count==0,
                "empty operands preserve owned region area and hole semantics without additional geometry");
            var added=regions.Union(ContourPaths.Rectangle(20,0,23,4));
            Assert(Same(regions.Union(owned,added),ContourPaths.Union(new PathsD(owned.Concat(added))))&&
                regions.Intersect(regions.Union(owned,added),touchingHole).Count==0,
                "union of owned regions preserves disjoint shells and the original courtyard");
        }
        {
            var rings=new PathsD();
            for(int i=0;i<64;i++)
            {
                double x=(i%8)*20,y=(i/8)*20;
                rings.AddRange(ContourPaths.Rectangle(x,y,x+10,y+10));
                foreach(double offset in new[]{1d,6d})
                {var hole=ContourPaths.Rectangle(x+offset,y+1,x+offset+2,y+3)[0];hole.Reverse();rings.Add(hole);}
            }
            foreach(var ordered in new[]{rings,new PathsD(rings.AsEnumerable().Reverse())})
            {
                var geometry=PlanarTriangulation.ToGridGeometry(ordered);
                Assert(geometry.NumGeometries==64 &&
                    Enumerable.Range(0,64).Sum(i=>((NetTopologySuite.Geometries.Polygon)geometry.GetGeometryN(i)).NumInteriorRings)==128 &&
                    Math.Abs(geometry.Area/1e12-64*92)<1e-9,
                    "multiple normalized shells retain every hole and area independent of input order");
            }
        }
        var cutter=ContourPaths.Rectangle(80,54,90,58);
        var partition=ContourPaths.Union(new PathsD(ContourPaths.Difference(normalized,cutter).Concat(ContourPaths.Intersect(normalized,cutter))));
        Assert(ContourPaths.Difference(normalized,partition).Count==0&&ContourPaths.Difference(partition,normalized).Count==0,
            "fixed-precision intersection and difference reconstruct their source region");
        {
            var projection=new LocalProjection(new GeoPoint(127,37.5));
            var sw=projection.ToGeo(-20,-20);var ne=projection.ToGeo(20,20);
            var bounds=new GeoBounds(sw.Lon,sw.Lat,ne.Lon,ne.Lat);
            var feature=new GeoFeature{Id="site",Polygons=new(){new(){new(){projection.ToGeo(-2,-2),projection.ToGeo(2,-2),
                projection.ToGeo(2,2),projection.ToGeo(-2,2),projection.ToGeo(-2,-2)}}}};
            var near=EditRegion.Create(feature,projection,bounds,3);
            var heights=Enumerable.Range(0,5).SelectMany(y=>Enumerable.Range(0,5).Select(x=>x==0||x==4||y==0||y==4?1000d:100d)).ToArray();
            var field=TerrainField.FromSamples(bounds,projection,4,4,heights);
            var contour=ContourPlan.Build(field,new(),near,SiteSelection.FeaturePaths(feature,projection),.001,0,30,true);
            Assert(contour.DetailLevel==100&&contour.Plates.All(plate=>plate.Top<=100),
                "far mountains do not create levels or reject valid flat Near contour detail");
        }
        {
            var exact=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("zone",0,0,10)}));
            var thresholds=new[]{-12d,-7,-3,0,2,5,7,9};
            var plates=thresholds.Select((x,i)=>
            {
                var source=ContourPaths.Rectangle(x,-15,15,15);
                var components=exact.Split(source,includeFar:false).Near;
                return new ContourPlate("Land",new PathsD(components.SelectMany(c=>c.Sample())),source,i,i+1)
                    {Exact=components};
            }).ToArray();
            var exposures=new ContourExposureCache().Build(plates,new(),exact,new ContourRegions());
            Assert(exposures.Count>1&&exposures.All(exposure=>Same(exposure.Profiles,
                new PathsD(exact.Split(exposure.SourceProfiles,exposure.Excluded,includeFar:false).Near.SelectMany(c=>c.Sample())))),
                "visible plate tops derive every displayed region from the same authoritative source profile");
            for(int i=0;i<thresholds.Length;i++)
            {
                double expected=CapArea(thresholds[i])-(i+1<thresholds.Length?CapArea(thresholds[i+1]):0);
                var exposure=exposures.Single(e=>e.Top==i+1);
                Assert(Math.Abs(exposure.Exact!.Sum(c=>c.Area)-expected)<1e-8,
                    "only uncovered material is exposed between exactly clipped nested courses");
            }
            static double CapArea(double x)=>x<=-10?100*Math.PI:100*Math.Acos(x/10)-x*Math.Sqrt(100-x*x);
        }
        {
            var circle=EditBoundaryHull.Create(new[]{new EditCircle("one",0,0,10)});
            var stadium=EditBoundaryHull.Create(new[]{new EditCircle("left",-3,0,4),new EditCircle("right",3,0,4)});
            var outsideRoad=new ContourBoundary(ContourPaths.Rectangle(-10,4,10,5));
            var far=outsideRoad.Split(stadium.SelectMany(loop=>loop.Segments),false);
            Assert(Math.Abs(Length(far.Where(part=>part.Filled).Select(part=>part.Edge))-6)<1e-8,
                "Far road ownership uses the opposite side when the Near tangent borders land");
            var split=new ContourBoundary(ContourPaths.Rectangle(-20,0,20,20)).Split(circle.SelectMany(loop=>loop.Segments),false);
            Assert(Math.Abs(Length(split.Where(part=>part.Filled).Select(part=>part.Edge))-10*Math.PI)<1e-8&&
                Math.Abs(Length(split.Where(part=>!part.Filled).Select(part=>part.Edge))-10*Math.PI)<1e-8,
                "an outer wall interval splits at Far road transitions while preserving the complete exact boundary");
        }
        {
            var profiles=ContourPaths.Union(new PathsD(
                ContourPaths.Difference(ContourPaths.Rectangle(0,0,10,10),ContourPaths.Rectangle(2,2,4,4))
                .Concat(ContourPaths.Rectangle(20,0,23,4))));
            var plate=new ContourPlate("Land",profiles,profiles,10,11);
            Assert(plate.Components.Count==2 && plate.Components.Sum(c=>c.Count)==3 &&
                Math.Abs(plate.Components.Sum(ContourPaths.Area)-108)<1e-10,
                "prepared plate components preserve independent exterior areas and a courtyard for counting and output");
        }
        return count;
        void Assert(bool success,string name){count++;if(!success)throw new InvalidOperationException("Contour precision: "+name);}
        double Length(IEnumerable<EditBoundarySegment> segments)=>segments.Sum(edge=>edge.Circle is {} circle?circle.RadiusMetres*edge.SweepRadians:edge.Start.DistanceTo(edge.End));
        bool Same(PathsD a,PathsD b)=>a.Select(Canonical).Order(StringComparer.Ordinal).SequenceEqual(b.Select(Canonical).Order(StringComparer.Ordinal));
        string Canonical(PathD path)
        {
            var points=path.Select(PlanarTriangulation.GridKey).ToArray();
            int start=Enumerable.Range(0,points.Length).OrderBy(i=>points[i].X).ThenBy(i=>points[i].Y).First();
            return string.Join(";",Enumerable.Range(0,points.Length).Select(i=>points[(start+i)%points.Length]));
        }
    }

    private static int SiteSplitDetails()
    {
        int count=0;
        var projection=new LocalProjection(new GeoPoint(127,37.5));
        var sw=projection.ToGeo(-20,-20);var ne=projection.ToGeo(20,20);
        var bounds=new GeoBounds(sw.Lon,sw.Lat,ne.Lon,ne.Lat);
        foreach(var transform in new[]{(Turn:false,Move:false,Reverse:false),(Turn:false,Move:false,Reverse:true),
            (Turn:true,Move:false,Reverse:false),(Turn:false,Move:true,Reverse:false)})
        foreach(double gap in new[]{.03,.5})
        {
            PointD Map(PointD p)=>new((transform.Turn?-p.y:p.x)+(transform.Move?3:0),
                (transform.Turn?p.x:p.y)+(transform.Move?-2:0));
            PathsD MapPaths(PathsD paths)=>new(paths.Select(path=>new PathD(
                (transform.Reverse?path.AsEnumerable().Reverse():path).Select(Map))));
            var sites=MapPaths(ContourPaths.Rectangle(-2,-2,2,2));
            var roads=MapPaths(ContourPaths.Rectangle(2+gap,-4,4,4));
            var original=sites.Concat(roads).Select(path=>path.ToArray()).ToArray();
            var feature=new GeoFeature{Id="site",Polygons=new(){sites.Select(path=>path.Append(path[0])
                .Select(p=>projection.ToGeo(p.x,p.y)).ToList()).ToList()}};
            var near=EditRegion.Create(feature,projection,bounds,3);
            var grid=TerrainField.FromSamples(bounds,projection,4,4,new double[25]);
            var heights=Enumerable.Range(0,5).SelectMany(y=>Enumerable.Range(0,5).Select(x=>
                100+.1*(transform.Turn?grid.ModelY(y):grid.ModelX(x)-(transform.Move?3:0)))).ToArray();
            var field=TerrainField.FromSamples(bounds,projection,4,4,heights);
            var plan=ContourPlan.Build(field,roads,near,sites,1,0,30,true);
            var probe=ContourPaths.Union(MapPaths(ContourPaths.Rectangle(2.001,.1,2.029,1.9)));
            var boundary=new ExactNearRegions(near);
            double Material(ContourPlate plate)=>plate.Exact!.Where(c=>boundary.Intersects(probe,c))
                .Sum(c=>boundary.Split(probe,included:c,includeFar:false).Near.Sum(p=>p.Area));
            double expected=.028*1.8;
            var top=plan.Plates.Single(p=>p.Role=="Land"&&p.Bottom==99&&p.Top==100);
            Assert(Math.Abs(Material(top)-(gap<.1?0:expected))<1e-9,
                "Site cut omits a connected 30mm neck and preserves a broad connection in exact output");
            Assert(plan.Plates.Where(p=>p.Role=="Land"&&p.Top>100).All(p=>Material(p)<1e-9),
                "higher detail courses do not retain an unsupported Site neck");
            Assert(plan.Plates.Where(p=>p.Role=="Land"&&p.Bottom>=plan.BaseTop&&p.Top<=plan.DetailLevel)
                .All(p=>Math.Abs(Material(p)-expected)<1e-9),"support courses retain material below the omitted neck");
            Assert(sites.Concat(roads).Select((path,i)=>path.SequenceEqual(original[i])).All(x=>x),
                "Site and road source coordinates remain unchanged");
        }
        {
            var sites=ContourPaths.Rectangle(-2,-2,2,2);
            var feature=new GeoFeature{Id="site",Polygons=new(){sites.Select(path=>path.Append(path[0])
                .Select(p=>projection.ToGeo(p.x,p.y)).ToList()).ToList()}};
            var near=EditRegion.Create(feature,projection,bounds,3);
            var field=TerrainField.FromSamples(bounds,projection,4,4,Enumerable.Repeat(100.35,25).ToArray());
            var roads=ContourPaths.Rectangle(2.03,-4,4,4);
            var plan=ContourPlan.Build(field,roads,near,sites,1,3,30,true);
            var probe=ContourPaths.Rectangle(2.001,.1,2.029,1.9);var boundary=new ExactNearRegions(near);
            var low=plan.Plates.Where(p=>p.Role=="Land"&&p.Top>plan.DetailLevel&&p.Top<99).ToArray();
            Assert(low.Length>0&&low.All(p=>p.Exact!.All(c=>!boundary.Intersects(probe,c))),
                "Site omissions persist in detail courses below the first terrain source level");
        }
        return count;
        void Assert(bool ok,string name){count++;if(!ok)throw new InvalidOperationException("Contour Site split: "+name);}
    }

    private static int ExposureUpdates()
    {
        int count=0;
        var boundary=new ExactNearRegions(EditBoundaryHull.Create(new[]{new EditCircle("zone",0,0,10)}));
        var regions=new ContourRegions();var cache=new ContourExposureCache();
        ContourPlate Plate(double left,double right,double bottom,double top)
        {
            var source=ContourPaths.Rectangle(left,-2,right,2);var exact=boundary.Split(source,includeFar:false).Near;
            return new("Land",new PathsD(exact.SelectMany(c=>c.Sample())),source,bottom,top){Exact=exact};
        }
        var lower=Plate(-8,-5,0,1);var distant=Plate(5,8,1,2);
        IReadOnlyList<ContourExposure> Build(params ContourPlate[] plates)=>cache.Build(plates,new(),boundary,regions);
        double Area(IReadOnlyList<ContourExposure> exposed,double top)=>exposed.Where(e=>e.Top==top).Sum(e=>e.Exact!.Sum(c=>c.Area));
        var first=Build(lower,distant);var replaced=Build(Plate(5,8,1,2),lower);
        Assert(Math.Abs(Area(first,1)-12)<1e-10&&Math.Abs(Area(replaced,1)-12)<1e-10,
            "a disjoint replacement and input order preserve the lower exposed material");
        var covering=Build(lower,Plate(-8,-6,1,2));
        Assert(Math.Abs(Area(covering,1)-4)<1e-10&&Math.Abs(Area(covering,2)-8)<1e-10,
            "a moved cover updates only the area it actually covers");
        var query=ContourPaths.Rectangle(-7.5,-1,-6.5,1);
        Assert(covering.Where(e=>e.Meets(query,Clipper.GetBounds(query))).Min(e=>e.Top)==2,
            "the covered ground query uses the new upper elevation");
        var removed=Build(lower);
        Assert(Math.Abs(Area(removed,1)-12)<1e-10&&removed.Single().Top==1,
            "removing a relevant cover restores the complete lower top");
        var contact=Build(lower,Plate(-5,-4,1,2));
        Assert(Math.Abs(Area(contact,1)-12)<1e-10,"edge contact does not remove positive material");
        var narrow=Build(lower,Plate(-5.000001,-4,1,2));
        Assert(Math.Abs(Area(narrow,1)-(12-.000004))<1e-9,
            "a small positive overlap remains a real covering area");
        return count;
        void Assert(bool ok,string name){count++;if(!ok)throw new InvalidOperationException("Contour exposure update: "+name);}
    }
}
