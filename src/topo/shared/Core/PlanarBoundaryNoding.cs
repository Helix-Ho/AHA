using Clipper2Lib;
using NetTopologySuite.Geometries;
using NetTopologySuite.Noding;
using NetTopologySuite.Noding.Snapround;

namespace Aha.Topo;

/// <summary>Nodes adjacent sampled boundaries together while retaining each
/// directed ring's owner and source-edge order.</summary>
internal static class PlanarBoundaryNoding
{
    private sealed record Source(int Owner,int Ring,int Edge,Coordinate A,Coordinate B);
    internal static IReadOnlyList<PathsD> Node(IReadOnlyList<PathsD> regions,PathsD constraints)
    {
        var lines=new List<ISegmentString>();
        for(int owner=0;owner<regions.Count;owner++)
        for(int ring=0;ring<regions[owner].Count;ring++)Add(regions[owner][ring],owner,ring,true);
        foreach(var path in constraints)Add(path,-1,0,false);
        var noder=new SnapRoundingNoder(new PrecisionModel(1));noder.ComputeNodes(lines);
        var ordered=noder.GetNodedSubstrings().Where(line=>((Source)line.Context).Owner>=0)
            .SelectMany(line=>Enumerable.Range(1,line.Coordinates.Length-1).Select(i=>
                (Source:(Source)line.Context,A:line.Coordinates[i-1],B:line.Coordinates[i])))
            .GroupBy(e=>(e.Source.Owner,e.Source.Ring))
            .ToDictionary(g=>g.Key,g=>g.OrderBy(e=>e.Source.Edge).ThenBy(e=>
                (e.A.X-e.Source.A.X)*(e.Source.B.X-e.Source.A.X)+(e.A.Y-e.Source.A.Y)*(e.Source.B.Y-e.Source.A.Y)).ToArray());
        var result=new List<PathsD>();
        for(int owner=0;owner<regions.Count;owner++)
        {
            var paths=new PathsD();
            for(int ring=0;ring<regions[owner].Count;ring++)
            {
                if(!ordered.TryGetValue((owner,ring),out var edges))continue;
                var path=new PathD();
                for(int i=0;i<edges.Length;i++)
                {
                    var edge=edges[i];
                    if(!edge.B.Equals2D(edges[(i+1)%edges.Length].A))
                        throw new InvalidOperationException("공통 평면 노딩에서 원본 고리의 연결이 유실됐습니다.");
                    path.Add(new(edge.A.X/(1/ModelPlanPolicy.PrecisionMetres),edge.A.Y/(1/ModelPlanPolicy.PrecisionMetres)));
                }
                if(path.Count>=3)paths.Add(path);
            }
            result.Add(paths);
        }
        return result;

        void Add(PathD path,int owner,int ring,bool closed)
        {
            for(int i=0;i<(closed?path.Count:path.Count-1);i++)
            {
                var p=PlanarTriangulation.GridKey(path[i]);var q=PlanarTriangulation.GridKey(path[(i+1)%path.Count]);
                if(p==q)continue;
                var a=new Coordinate(p.X,p.Y);var b=new Coordinate(q.X,q.Y);
                lines.Add(new NodedSegmentString(new[]{a,b},new Source(owner,ring,i,a,b)));
            }
        }
    }
}
