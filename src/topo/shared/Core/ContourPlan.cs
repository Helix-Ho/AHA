using Clipper2Lib;

namespace Aha.Topo;

public sealed record ContourBuilding(string Id, PathsD Footprint, double DefaultBottom, bool Excavate);
public sealed record ContourPlate(string Role, PathsD Profiles, PathsD SourceProfiles, double Bottom, double Top)
{
    internal IReadOnlyList<ExactNearRegions.Component>? Exact { get; init; }
    internal IReadOnlyList<ExactNearRegions.Component> Excluded { get; init; } = Array.Empty<ExactNearRegions.Component>();
    // Exact sections already own their components; straight profiles normalize on demand.
    private IReadOnlyList<PathsD>? components;
    internal IReadOnlyList<PathsD> Components => components??=ContourPaths.Components(Profiles);
}
public sealed record ContourExposure(string Role, PathsD Profiles, PathsD SourceProfiles, double Bottom, double Top)
{
    internal ContourRegions Regions { get; init; } = new();
    internal ExactNearRegions? Boundary { get; init; }
    internal IReadOnlyList<ExactNearRegions.Component>? Exact { get; init; }
    internal IReadOnlyList<ExactNearRegions.Component> Excluded { get; init; } = Array.Empty<ExactNearRegions.Component>();
    private RectD? bounds;
    internal RectD Bounds => bounds??=Exact is {Count:>0}
        ?new RectD(Exact.Min(c=>c.Bounds.MinX),Exact.Min(c=>c.Bounds.MinY),Exact.Max(c=>c.Bounds.MaxX),Exact.Max(c=>c.Bounds.MaxY))
        :Clipper.GetBounds(Profiles);
    internal bool Meets(PathsD paths, RectD query)
    {
        if(query.right<=Bounds.left||query.left>=Bounds.right||query.bottom<=Bounds.top||query.top>=Bounds.bottom)return false;
        if(Exact is not null&&Boundary is not null)
        {
            var normalized=Regions.Union(paths);
            var envelope=new NetTopologySuite.Geometries.Envelope(query.left,query.right,query.top,query.bottom);
            return Exact.Any(component=>component.Bounds.Intersects(envelope)&&Boundary.Intersects(normalized,component));
        }
        return ContourPaths.OverlayGeometry(Regions.Region(paths),Regions.Region(Profiles),
            NetTopologySuite.Operation.Overlay.SpatialFunction.Intersection).Area>0;
    }
}

/// <summary>Horizontal profiles and elevations; no Rhino geometry is constructed here.</summary>
public sealed class ContourPlan
{
    public IReadOnlyList<ContourPlate> Plates { get; }
    public IReadOnlyList<ContourExposure> Exposures { get; }
    public IReadOnlyList<ContourExposure> GroundExposures { get; }
    internal IReadOnlyList<ContourExposure> RoadOverlays { get; }
    internal PathsD OverlayRoleChanges { get; }
    public IReadOnlyDictionary<string, double> BuildingBottoms { get; }
    public PathsD RemovedDetail { get; }
    public double DetailLevel { get; }
    public double BaseTop { get; }
    public double BaseBottom { get; }
    public double Interval { get; }
    public EditRegion Near { get; }
    public bool SeparateRoads { get; }
    public PathsD Roads { get; }
    internal PathsD FarRoads { get; }
    public double RoadDrop { get; }
    public double ApproximationHeightError { get; }
    public int PredictedSolidCount { get; }

    private ContourPlan(List<ContourPlate> plates, List<ContourExposure> exposures,
        Dictionary<string,double> bottoms, PathsD removed, double level, double interval,
        double depth, EditRegion near, bool separateRoads, PathsD roads, PathsD farRoads, double roadDrop, double heightError,
        IReadOnlyList<ContourExposure> groundExposures, int predictedSolidCount,
        IReadOnlyList<ContourExposure> roadOverlays, PathsD overlayRoleChanges)
    {
        Plates=plates; Exposures=exposures; BuildingBottoms=bottoms; RemovedDetail=removed;
        DetailLevel=level; Interval=interval; BaseTop=level-2*interval; BaseBottom=level-depth;
        Near=near; SeparateRoads=separateRoads; Roads=roads; FarRoads=farRoads; RoadDrop=roadDrop; ApproximationHeightError=heightError;
        GroundExposures=groundExposures; PredictedSolidCount=predictedSolidCount;
        RoadOverlays=roadOverlays; OverlayRoleChanges=overlayRoleChanges;
    }

    /// <summary>Minimum exposed terrace height before foundation cuts, excluding the continuous design site.</summary>
    public double? MinimumExposedElevation(PathsD footprint)
    {
        var bounds=Clipper.GetBounds(footprint);
        foreach(var exposure in Exposures)
            if(exposure.Meets(footprint,bounds))
                return exposure.Top;
        return null;
    }

    /// <summary>Lowest actual model ground, including full-profile foundation cuts.</summary>
    public double? MinimumGroundElevation(PathsD footprint)
    {
        var bounds=Clipper.GetBounds(footprint);
        foreach(var exposure in GroundExposures)
            if(exposure.Meets(footprint,bounds))
                return exposure.Top;
        return null;
    }

    public static ContourPlan Build(TerrainField field, PathsD roads, EditRegion near, PathsD sites,
        double interval, double roadDrop, double depth, bool separateRoads,
        IReadOnlyList<ContourBuilding>? buildings=null, PathsD? planarNearBoundary=null, IReadOnlyList<string[]>? overlapGroups=null, CancellationToken ct=default, IProgress<string>? progress=null)
    {
        var regions=new ContourRegions();
        ArgumentNullException.ThrowIfNull(field); ArgumentNullException.ThrowIfNull(near);
        if(!double.IsFinite(interval)||interval<=0 || !double.IsFinite(depth)||depth<=2*interval ||
            !double.IsFinite(roadDrop)||roadDrop<0)
            throw new ArgumentException("Contour 간격은 양수이며 입체 깊이는 간격의 두 배보다 커야 합니다.");
        roadDrop=separateRoads?roadDrop:0;
        buildings??=Array.Empty<ContourBuilding>();
        if(buildings.Select(b=>b.Id).Distinct(StringComparer.Ordinal).Count()!=buildings.Count ||
            buildings.Any(b=>!double.IsFinite(b.DefaultBottom)))
            throw new ArgumentException("건물 식별자 또는 기초 높이가 유효하지 않습니다.");
        var farRoads=roads; // Far classification uses the unchanged resolved partition, before contour-domain clipping.
        var nearPaths=regions.Union(planarNearBoundary??near.Paths);
        var exactNear=new ExactNearRegions(near,ct);
        // Contour owns only Near. Keep a small analytic-boundary restoration
        // margin, rather than carrying unrelated distant road/detail polygons.
        const double boundaryMargin=.002;
        var domain=regions.Union(ContourPaths.Rectangle(
            Math.Max(field.ModelX(0),near.Circles.Min(c=>c.CenterX-c.RadiusMetres)-boundaryMargin),
            Math.Max(field.ModelY(0),near.Circles.Min(c=>c.CenterY-c.RadiusMetres)-boundaryMargin),
            Math.Min(field.ModelX(field.Columns),near.Circles.Max(c=>c.CenterX+c.RadiusMetres)+boundaryMargin),
            Math.Min(field.ModelY(field.Rows),near.Circles.Max(c=>c.CenterY+c.RadiusMetres)+boundaryMargin)));
        sites=regions.Union(sites); roads=regions.Intersect(regions.Union(roads),domain);
        var roadSource=separateRoads?new ExactNearRegions.SourceRegion(roads):null;
        var outsideSite=regions.Difference(domain,sites);
        var land=separateRoads?regions.Difference(outsideSite,roads):outsideSite;
        var road=separateRoads?regions.Intersect(outsideSite,roads):new PathsD();
        progress?.Report("등고 단면을 계산하고 있습니다…");
        var threshold=new ContourLevels(field,interval,nearPaths,ct);
        double minimum=threshold.Minimum, maximum=threshold.Maximum;
        if(!double.IsFinite((maximum-minimum+roadDrop)/interval) ||
            !double.IsFinite(maximum/interval)||Math.Max(Math.Abs(maximum/interval),Math.Abs((minimum-roadDrop)/interval))>9e15)
            throw new ArgumentException("Contour 간격으로 현재 높이 범위를 표현할 수 없습니다. 간격을 조정하세요.");
        long first=Level(minimum-roadDrop,interval)-1, last=Level(maximum,interval);
        long sourceFirst=Level(minimum,interval)-1;
        var levelAreas=new Dictionary<long,PathsD>();
        var removed=new PathsD();var levelOmissions=new ContourOmissions(regions);
        for(long k=sourceFirst;k<=last+1;k++)
        {
            ct.ThrowIfCancellationRequested();
            var raw=k<=sourceFirst?domain:regions.Union(threshold.At(k));
            var loss=regions.DetailOmissions(raw);
            levelOmissions.Add(loss);
            removed.AddRange(loss);
            levelAreas[k]=levelOmissions.Subtract(raw);
        }
        PathsD At(long k)=>k<=sourceFirst?domain:k>last?new PathsD():levelAreas[k];
        PathsD ClipNear(PathsD paths)=>regions.Intersect(paths,nearPaths);
        progress?.Report("등고판의 도로·대지 영역을 정리하고 있습니다…");
        var roleAreas=new Dictionary<(string Role,long Level),PathsD>();
        var roleRemoved=new Dictionary<(string Role,long Level),PathsD>();
        foreach(var item in new[]{(Role:"Land",Domain:land),(Role:"Road",Domain:road)})
        {
            var roleOmissions=new ContourOmissions(regions);
            roleOmissions.Add(regions.NewDetailOmissions(item.Domain,sites));
            for(long k=sourceFirst;k<=last+1;k++)
            {
                ct.ThrowIfCancellationRequested();
                var source=regions.Intersect(At(k),item.Domain);
                var remaining=roleOmissions.Subtract(source);
                var newOmission=regions.NewDetailOmissions(remaining,At(k));
                roleOmissions.Add(newOmission);
                var omittedRole=roleOmissions.Masks(source);
                var clean=regions.Difference(source,omittedRole);
                roleRemoved[(item.Role,k)]=omittedRole;
                removed.AddRange(ClipNear(omittedRole));
                roleAreas[(item.Role,k)]=clean;
            }
        }
        PathsD RoleRemoved(string role,long k)=>roleRemoved.GetValueOrDefault((role,Math.Max(k,sourceFirst)))??new PathsD();
        PathsD RoleAt(string role,long k)=>k>last?new PathsD():roleAreas[(role,Math.Max(k,sourceFirst))];
        var exactRegions=new System.Runtime.CompilerServices.ConditionalWeakTable<PathsD,ExactRegionValue>();
        IReadOnlyList<ExactNearRegions.Component> Exact(PathsD p)=>p.Count==0?Array.Empty<ExactNearRegions.Component>():
            exactRegions.GetValue(p,x=>new(exactNear.Split(regions.Union(x),includeFar:false).Near)).Regions;
        var siteMasks=Exact(sites);
        var cutMasks=buildings.Where(b=>b.Excavate).ToDictionary(b=>b.Id,b=>Exact(b.Footprint),StringComparer.Ordinal);
        var precut=new Dictionary<(string Role,long Level),IReadOnlyList<ExactNearRegions.Component>>();
        IReadOnlyList<ExactNearRegions.Component> Precut(string role,long k)
        {
            if(!precut.TryGetValue((role,k),out var material))
                precut[(role,k)]=material=exactNear.Split(At(k),siteMasks.Concat(Exact(RoleRemoved(role,k))).ToArray(),
                    rolePaths:separateRoads?roads:null,road:role=="Road",includeFar:false,roleSource:roadSource).Near;
            return material;
        }
        var exposures=new List<ContourExposure>();
        for(long k=sourceFirst;k<=last;k++)
        {
            ct.ThrowIfCancellationRequested();
            Expose("Land",k,k*interval);
            if(separateRoads)Expose("Road",k,k*interval-roadDrop);
        }
        void Expose(string role,long k,double top)
        {
            var exact=ContourExposureCache.SubtractCovered(Precut(role,k),Precut(role,k+1),exactNear);
            if(exact.Count==0)return;
            var profiles=new PathsD(exact.SelectMany(c=>c.Sample()));
            double bottom=(Math.Ceiling(top/interval-1e-10)-1)*interval;
            exposures.Add(new(role,profiles,profiles,bottom,top){Regions=regions,Boundary=exactNear,Exact=exact});
        }
        precut.Clear();
        exposures=exposures.OrderBy(e=>e.Top).ThenBy(e=>e.Role,StringComparer.Ordinal).ToList();
        var bottoms=new Dictionary<string,double>(StringComparer.Ordinal);
        foreach(var building in buildings)
        {
            double? floor=null;double ordinary=building.DefaultBottom;
            var bounds=Clipper.GetBounds(building.Footprint);
            foreach(var exposure in exposures)
                if(exposure.Meets(building.Footprint,bounds))
                {
                    if(building.Excavate)floor=exposure.Bottom;
                    ordinary=Math.Min(ordinary,exposure.Top-1);
                    break;
                }
            bottoms[building.Id]=floor??ordinary;
        }
        // Positive-area overlap shares a foundation elevation, never deletes source buildings.
        var byId=buildings.ToDictionary(b=>b.Id,StringComparer.Ordinal);
        overlapGroups??=BuildingOverlapGroups.Create(buildings.Select(b=>(b.Id,b.Footprint)));
        var buildingBounds=buildings.ToDictionary(b=>b.Id,b=>Clipper.GetBounds(b.Footprint),StringComparer.Ordinal);
        var groups=overlapGroups.Select(group=>
        {
            var members=group.Select(id=>byId[id]).ToArray();
            var bounds=Clipper.GetBounds(new PathsD(members.SelectMany(b=>b.Footprint)));
            return (Members:members,Bounds:bounds,Excavates:members.Any(b=>b.Excavate));
        }).ToArray();
        foreach(var group in groups)
        {
            var members=group.Members;
            double bottom=members.Min(b=>bottoms[b.Id]);
            if(members.Any(b=>b.Excavate))bottom=Level(bottom,interval)*interval;
            foreach(var b in members)bottoms[b.Id]=bottom;
        }
        double lowest=exposures.Count==0?minimum:exposures.Min(e=>e.Top);
        if(sites.Count>0)lowest=Math.Min(lowest,BuildingPlacement.Statistics(field,sites,new(),0).Minimum);
        foreach(var b in buildings.Where(b=>b.Excavate))lowest=Math.Min(lowest,bottoms[b.Id]);
        double level=Level(lowest,interval)*interval;
        long baseLevel=Level(level,interval);
        var protectedCourtyards=regions.Union(new PathsD(buildings.Where(b=>b.Excavate)
            .SelectMany(b=>b.Footprint).Where(path=>Clipper.Area(path)<0)
            .Select(path=>new PathD(path.AsEnumerable().Reverse()))));
        long dropWhole=(long)Math.Floor(roadDrop/interval+1e-10);
        double remainder=roadDrop-dropWhole*interval;
        if(remainder<1e-10)remainder=0;
        // Sources do not change when foundation floors move.
        var thinRoads=new Dictionary<long,PathsD>();
        var thinTerrain=new Dictionary<long,PathsD>();
        if(separateRoads&&remainder>0)
            for(long k=baseLevel+1;k<=last;k++)
                {
                thinRoads[k]=regions.Difference(RoleAt("Road",k+dropWhole),RoleAt("Road",k+dropWhole+1));
                thinTerrain[k]=regions.Difference(At(k+dropWhole),At(k+dropWhole+1));
                }
        var emptyHoles=new PathsD();
        var holeCache=new Dictionary<string,PathsD>(StringComparer.Ordinal);
        var steps=new Dictionary<(string Role,double Bottom,double Top),ContourPlateStep>();
        var initialStates=new Dictionary<string,object>(StringComparer.Ordinal)
            {["Land"]=new(),["Road"]=new(),["Base"]=new()};
        var exposureCache=new ContourExposureCache();
        IReadOnlyList<ContourExposure>? previousGround=null;
        int pass=0;
        List<ContourPlate> plates;
        IReadOnlyList<ContourExposure> ground;
        PathsD omitted;
        int predicted;
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            pass++;
            progress?.Report(pass==1?"등고판과 기초 구멍을 계산하고 있습니다…":$"기초 변경 영역을 갱신하고 있습니다… ({pass})");
            plates=MakePlates(out omitted);
            predicted=plates.Sum(p=>p.Exact?.Count??p.Components.Count);
            ground=exposureCache.Build(plates,sites,exactNear,regions);
            var changedGround=new NetTopologySuite.Index.Strtree.STRtree<ContourExposure>();
            if(previousGround is not null)
            {
                var prior=new HashSet<ContourExposure>(previousGround,ReferenceEqualityComparer.Instance);
                var current=new HashSet<ContourExposure>(ground,ReferenceEqualityComparer.Instance);
                foreach(var exposure in previousGround.Where(e=>!current.Contains(e)).Concat(ground.Where(e=>!prior.Contains(e))))
                {
                    var b=exposure.Bounds;
                    changedGround.Insert(new(b.left,b.right,b.top,b.bottom),exposure);
                }
            }
            changedGround.Build();
            bool changedCut=false;
            foreach(var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                if(previousGround is not null&&changedGround.Query(new(group.Bounds.left,group.Bounds.right,group.Bounds.top,group.Bounds.bottom)).Count==0)continue;
                var exposure=ground.FirstOrDefault(e=>group.Members.Any(member=>e.Meets(member.Footprint,buildingBounds[member.Id])));
                if(exposure is null)continue;
                double current=bottoms[group.Members[0].Id];
                // A cut group rests on its cut floor: subtracting another metre
                // would deepen its own hole forever. Ordinary groups instead
                // remain one metre below actual post-cleanup terrain.
                double required=group.Excavates?Level(exposure.Top,interval)*interval:exposure.Top-1;
                double resolved=Math.Min(current,required);
                if(resolved>=current)continue;
                foreach(var member in group.Members)bottoms[member.Id]=resolved;
                changedCut|=group.Excavates;
            }
            previousGround=ground;
            if(!changedCut)break;
            // Cut floors only decrease through existing contour levels. The
            // intact support top L bounds them below; non-cut groups do not
            // alter plates. Therefore this reaches a fixed point without an
            // arbitrary iteration limit or a self-deepening depth adjustment.
        }
        removed.AddRange(omitted);
        (IReadOnlyList<ContourExposure> Surfaces, PathsD Changed) overlays=separateRoads
            ? (Array.Empty<ContourExposure>(),new PathsD()) : OverlayTops(ground,roads,exactNear,ct,regions);
        return new(plates,exposures,bottoms,ClipNear(regions.Union(removed)),level,interval,depth,near,
            separateRoads,roads,farRoads,roadDrop,threshold.MaximumHeightError,ground,predicted,
            overlays.Surfaces,overlays.Changed);

        List<ContourPlate> MakePlates(out PathsD omittedDetail)
        {
            var output=new List<ContourPlate>();
            var changed=new PathsD();
            var omissions=new Dictionary<string,ContourOmissions>(StringComparer.Ordinal);
            var states=new Dictionary<string,object>(initialStates,StringComparer.Ordinal);
            var nextSteps=new Dictionary<(string Role,double Bottom,double Top),ContourPlateStep>();
            var nextHoles=new Dictionary<string,PathsD>(StringComparer.Ordinal);
            // Floors are fixed during this pass. The same active cut set serves
            // Land, Road and every level between two successive floor events.
            var cutEvents=buildings.Where(b=>b.Excavate).GroupBy(b=>bottoms[b.Id])
                .OrderBy(group=>group.Key).Select(group=>(Bottom:group.Key,Members:group.ToArray())).ToArray();
            var passHoles=new Dictionary<int,PathsD>{[0]=emptyHoles};
            PathsD Holes(double top)
            {
                int count=0;
                while(count<cutEvents.Length && top>cutEvents[count].Bottom+1e-10)count++;
                if(passHoles.TryGetValue(count,out var found))return found;
                // Sorted identifiers make unchanged active sets reusable even
                // when their floor-event positions change between passes.
                for(int i=1;i<=count;i++)
                {
                    if(passHoles.ContainsKey(i))continue;
                    var ids=cutEvents.Take(i).SelectMany(e=>e.Members).Select(b=>b.Id).Order(StringComparer.Ordinal);
                    string key=System.Text.Json.JsonSerializer.Serialize(ids);
                    if(!holeCache.TryGetValue(key,out var holes))
                        holes=regions.Union(passHoles[i-1],
                            new PathsD(cutEvents[i-1].Members.SelectMany(b=>b.Footprint)));
                    nextHoles[key]=holes;passHoles[i]=holes;
                }
                return passHoles[count];
            }
            for(long k=baseLevel+1;k<=last;k++)
            {
                Add("Land",RoleAt("Land",k),(k-1)*interval,k*interval,true,At(k),RoleRemoved("Land",k));
                if(!separateRoads)continue;
                Add("Road",RoleAt("Road",k+dropWhole+(remainder>0?1:0)),(k-1)*interval,k*interval,true,At(k+dropWhole+(remainder>0?1:0)),RoleRemoved("Road",k+dropWhole+(remainder>0?1:0)));
                if(remainder>0)
                    Add("Road",thinRoads[k],
                        (k-1)*interval,k*interval-remainder,true,
                        thinTerrain[k],RoleRemoved("Road",k+dropWhole));
            }
            // Both supports preserve the site void; only the base continues
            // beneath the continuous site body. Support courses are never cut.
            for(int i=0;i<2;i++)
            {
                Add("Land",land,level-(i+1)*interval,level-i*interval,false);
                if(separateRoads)Add("Road",road,level-(i+1)*interval,level-i*interval,false);
            }
            Add("Base",domain,level-depth,level-2*interval,false);
            steps=nextSteps;holeCache=nextHoles;
            omittedDetail=changed;
            return output;

            void Add(string role,PathsD source,double bottom,double top,bool cut,PathsD? terrain=null,PathsD? roleOmitted=null)
            {
                ct.ThrowIfCancellationRequested();
                if(top<=bottom||source.Count==0)return;
                if(!omissions.TryGetValue(role,out var cumulative))
                    omissions[role]=cumulative=new(regions);
                var input=source;
                var holes=cut?Holes(top):emptyHoles;
                var before=cut?states[role]:initialStates[role];
                var key=(role,bottom,top);
                if(steps.TryGetValue(key,out var cached)&&ReferenceEquals(cached.Input,input)&&
                    ReferenceEquals(cached.Holes,holes)&&ReferenceEquals(cached.Before,before))
                {
                    Replay(cached);return;
                }
                var omitted=new PathsD();
                var exactOmitted=new List<ExactNearRegions.Component>();
                if(cut)
                {
                    if(holes.Count>0)source=regions.Difference(source,holes);
                    var beforeOmissions=source;
                    source=cumulative.Subtract(source);
                    var appliedOmissions=regions.Difference(beforeOmissions,source);
                    omitted=regions.NewDetailOmissions(source,holes,appliedOmissions);
                    var protectedMaterial=regions.Intersect(source,protectedCourtyards);
                    if(protectedMaterial.Count>0)
                        omitted=regions.Difference(omitted,protectedMaterial);
                }
                // Width decisions use fixed-grid regions. Output keeps the
                // original terrain, role and cutter boundaries as operands,
                // instead of reinterpreting their rounded intersections.
                terrain??=domain;
                var masks=new List<ExactNearRegions.Component>();
                if(role!="Base")masks.AddRange(siteMasks);
                if(cut)
                {
                    foreach(var building in buildings.Where(b=>b.Excavate&&top>bottoms[b.Id]+1e-10))masks.AddRange(cutMasks[building.Id]);
                    masks.AddRange(Exact(roleOmitted??emptyHoles));
                    masks.AddRange(cumulative.MaterialMasks(terrain,exactNear));
                    masks.AddRange(Exact(omitted));
                    masks.AddRange(cumulative.Query(terrain));
                }
                var excluded=masks.ToArray();
                var exact=exactNear.Split(terrain,excluded,rolePaths:separateRoads&&role!="Base"?roads:null,
                    road:role=="Road",includeFar:false,roleSource:roadSource).Near;
                if(cut)
                {
                    var kept=new List<ExactNearRegions.Component>();
                    foreach(var component in exact)
                    {
                        var proxy=component.Sample();
                        bool protectedMaterial=ContourPaths.Area(regions.Intersect(proxy,protectedCourtyards))>0;
                        if(!protectedMaterial&&(component.Area<ModelPlanPolicy.Default.MinimumAreaSquareMetres||!ContourPaths.HasMinimumWidth(proxy)))
                            exactOmitted.Add(component);
                        else kept.Add(component);
                    }
                    exact=kept;
                }
                var clipped=new PathsD(exact.SelectMany(c=>c.Sample()));
                ContourPlate? plate=clipped.Count>0?new(role,clipped,source,bottom,top)
                    {Exact=exact,Excluded=excluded.Concat(exactOmitted).ToArray()}:null;
                object after=omitted.Count==0&&exactOmitted.Count==0?before:new object();
                // A changed cut need not change the propagated omission state.
                if(cached is not null&&ReferenceEquals(cached.Before,before)&&
                    exactOmitted.Count==0&&cached.ExactOmitted.Count==0&&
                    regions.Region(omitted).EqualsTopologically(regions.Region(cached.Omitted)))after=cached.After;
                Replay(new(input,holes,before,after,omitted,exactOmitted,plate));

                void Replay(ContourPlateStep step)
                {
                    nextSteps[key]=step;
                    if(cut)
                    {
                        cumulative.Add(step.Omitted);changed.AddRange(step.Omitted);
                        foreach(var component in step.ExactOmitted)
                        {cumulative.Add(component);changed.AddRange(component.Sample());}
                        states[role]=step.After;
                    }
                    if(step.Plate is not null)output.Add(step.Plate);
                }
            }
        }
    }

    internal static (IReadOnlyList<ContourExposure> Surfaces, PathsD Changed) OverlayTops(
        IReadOnlyList<ContourExposure> ground, PathsD roads, ExactNearRegions exactNear, CancellationToken ct=default, ContourRegions? regions=null)
    {
        regions??=new ContourRegions();
        var result=new List<ContourExposure>(); var changes=new PathsD();
        foreach(var exposure in ground.Where(e=>e.Role=="Land"))
        {
            ct.ThrowIfCancellationRequested();
            var source=regions.Intersect(exposure.SourceProfiles,roads);
            var material=exposure.Exact??exactNear.Split(exposure.SourceProfiles,exposure.Excluded,includeFar:false).Near;
            var roadParts=new List<ExactNearRegions.Component>();
            foreach(var component in material)
            {
                var box=component.Bounds;
                var domain=ContourPaths.Rectangle(box.MinX-1,box.MinY-1,box.MaxX+1,box.MaxY+1);
                var partition=NearRoadGeometry.ResolveExact(exactNear,domain,roads,includeFar:false,ct:ct,included:component);
                foreach(var change in partition.Changes)changes.AddRange(change.Region.Sample());
                roadParts.AddRange(partition.Road.Near);
            }
            // An entire exposed strip can be narrower than the detail
            // threshold. It has no same-level land neighbour, but the land
            // plate already covers it: omit only the independent road paint.
            var kept=new List<ExactNearRegions.Component>();
            foreach(var component in roadParts)
            {
                var sample=component.Sample();
                if(component.Area<ModelPlanPolicy.Default.MinimumAreaSquareMetres||!ContourPaths.HasMinimumWidth(sample))
                    changes.AddRange(sample);
                else kept.Add(component);
            }
            var profiles=new PathsD(kept.SelectMany(c=>c.Sample()));
            if(profiles.Count>0)result.Add(new("Road",profiles,source,exposure.Bottom,exposure.Top)
                {Exact=kept,Excluded=exposure.Excluded,Regions=regions,Boundary=exactNear});
        }
        return (result,regions.Union(changes));
    }

    private static long Level(double height,double interval)=>checked((long)Math.Floor(height/interval+1e-10));
}
