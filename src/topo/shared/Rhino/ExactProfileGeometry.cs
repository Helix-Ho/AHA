using Rhino.Geometry;
namespace Aha.Topo;

/// <summary>Converts owned analytic profile rings without changing their topology.</summary>
internal static class ExactProfileGeometry
{
    internal static IReadOnlyList<Curve> Curves(ExactNearRegions.Component component)
    {
        var result=new List<Curve>();
        try
        {
            foreach(var ring in component.Rings)
            {
                var edges=OutputEdges(ring).ToArray();
                Curve loop=edges.All(edge=>edge.Source.Circle is null)
                    ?StraightCurve(edges):MixedCurve(edges);
                result.Add(loop);
                if(!loop.IsClosed||!loop.IsValid)throw new InvalidOperationException("정확 프로파일의 폐합이 보존되지 않았습니다.");
                double expected=Math.Abs(ring.SignedArea),allowed=Math.Max(1e-8,expected*1e-8);
                using var area=AreaMassProperties.Compute(loop);
                double measured=area?.Area??double.NaN;
                // Native curve integration exposes no accuracy argument. When its
                // discrepancy exceeds the verification bound within its reported error, integrate the same
                // unit-height profile with explicit mass-property accuracy instead.
                if(area is not null&&Math.Abs(measured-expected)>allowed&&area.AreaError>allowed)
                {
                    using var extrusion=Extrusion.Create(loop,Plane.WorldXY,1,true);
                    using var brep=extrusion?.ToBrep();
                    using var mass=brep is null?null:VolumeMassProperties.Compute(brep,true,false,false,false,1e-11,1e-11);
                    measured=mass is null?double.NaN:Math.Abs(mass.Volume);
                }
                if(!double.IsFinite(measured)||measured<=0||Math.Abs(measured-expected)>allowed)
                    throw new InvalidOperationException("정확 프로파일의 직선·원호 면적이 Rhino 출력에서 보존되지 않았습니다.");
            }
            return result;
        }
        catch{foreach(var curve in result)curve.Dispose();throw;}
    }
    private static Curve StraightCurve(IReadOnlyList<ExactNearRegions.Edge> edges)
    {
        var points=edges.Select(edge=>new Point3d(edge.Start.X,edge.Start.Y,0)).ToList();
        points.Add(new(edges[^1].End.X,edges[^1].End.Y,0));
        using var polyline=new PolylineCurve(points);
        return polyline.ToNurbsCurve();
    }
    // Preserve the analytic path, but not redundant subdivisions of one straight
    // line. Native LineCurve validity rejects sub-nanometre segments even when
    // the enclosing profile has a large positive area. Core provenance stays intact.
    private static IEnumerable<ExactNearRegions.Edge> OutputEdges(ExactNearRegions.Ring ring)
    {
        var edges=ring.Edges;int start=0;
        while(start<edges.Count&&Collinear(edges[(start+edges.Count-1)%edges.Count],edges[start]))start++;
        if(start==edges.Count)throw new InvalidOperationException("프로파일의 모든 경계가 같은 직선입니다.");
        var run=edges[start];
        for(int i=1;i<edges.Count;i++)
        {
            var next=edges[(start+i)%edges.Count];
            if(Collinear(run,next))run=run with{End=next.End,ExactEnd=next.ExactEnd};
            else {yield return run;run=next;}
        }
        yield return run;
    }
    private static bool Collinear(ExactNearRegions.Edge a,ExactNearRegions.Edge b)
    {
        if(a.Source.Circle is not null||b.Source.Circle is not null||a.End!=b.Start)return false;
        var direction=a.Source.End-a.Source.Start;
        return EditBoundaryMath.Cross(direction,b.Source.Start-a.Source.Start)==0&&
            EditBoundaryMath.Cross(direction,b.Source.End-a.Source.Start)==0&&
            EditBoundaryMath.Dot(a.End-a.Start,b.End-b.Start)>0;
    }
    private static Curve MixedCurve(IReadOnlyList<ExactNearRegions.Edge> edges)
    {
        var arcs=new Curve?[edges.Count];
        var parts=new List<(List<ExactNearRegions.Edge> Edges,Curve Curve)>();
        try
        {
            for(int i=0;i<edges.Count;i++)
            {
                if(edges[i].Source.Circle is null)continue;
                var arc=ArcCurve(edges[i]);
                if(arc.IsValid)arcs[i]=arc;else arc.Dispose();
            }
            int start=Array.FindIndex(arcs,c=>c is not null);
            if(start<0)return RationalCurve(edges);
            for(int i=0;i<edges.Count;)
            {
                int index=(start+i)%edges.Count;
                if(arcs[index] is {} arc)
                {
                    parts.Add((new(){edges[index]},arc));arcs[index]=null;i++;
                }
                else
                {
                    var chain=new List<ExactNearRegions.Edge>();
                    do{chain.Add(edges[(start+i++)%edges.Count]);}
                    while(i<edges.Count&&arcs[(start+i)%edges.Count] is null);
                    parts.Add((chain,chain.All(e=>e.Source.Circle is null)?StraightCurve(chain):RationalCurve(chain)));
                }
            }
            // A tiny isolated interval may need its neighbouring span to form
            // a native curve. Combine representations without deleting an edge.
            while(parts.Count>1)
            {
                int index=parts.FindIndex(p=>!p.Curve.IsValid);
                if(index<0)break;
                int previous=(index+parts.Count-1)%parts.Count;
                var chain=parts[previous].Edges.Concat(parts[index].Edges).ToList();
                var joined=RationalCurve(chain);
                parts[previous].Curve.Dispose();parts[index].Curve.Dispose();
                parts[previous]=(chain,joined);parts.RemoveAt(index);
            }
            if(parts.Count==1)return parts[0].Curve.DuplicateCurve();
            var result=new PolyCurve();
            try
            {
                foreach(var part in parts)
                    if(!result.Append(part.Curve))throw new InvalidOperationException("공유 교점의 정확 프로파일을 연결할 수 없습니다.");
                return result;
            }
            catch{result.Dispose();throw;}
        }
        finally
        {
            foreach(var arc in arcs)arc?.Dispose();
            foreach(var part in parts)part.Curve.Dispose();
        }
    }
    private static Curve ArcCurve(ExactNearRegions.Edge edge)
    {
        var c=edge.Source.Circle!;
        double start=edge.Source.StartAngle+edge.Source.SweepRadians*Math.Min(edge.From,edge.To);
        double sweep=Math.Abs(edge.Sweep);
        var circle=new Circle(new Point3d(c.CenterX,c.CenterY,0),c.RadiusMetres);
        var curve=sweep>=2*Math.PI-1e-14?new ArcCurve(circle):new ArcCurve(new Arc(circle,new Interval(start,start+sweep)));
        if(edge.To<edge.From)curve.Reverse();
        return curve;
    }
    // A rational quadratic represents the analytic chain. Native ArcCurve
    // rejects very short angles even when the enclosing ring is valid. Uniform
    // span parameters retain those intervals without rounding knots by length.
    private static Curve RationalCurve(IReadOnlyList<ExactNearRegions.Edge> edges)
    {
        var controls=new List<(EditBoundaryPoint Point,double Weight)>();
        foreach(var edge in edges)
        {
            int count=edge.Source.Circle is null?1:Math.Max(1,(int)Math.Ceiling(Math.Abs(edge.Sweep)/(Math.PI/2)));
            for(int i=0;i<count;i++)
            {
                var a=edge.At((double)i/count);var b=edge.At((double)(i+1)/count);
                var middle=(a+b)*.5;double weight=1;
                if(edge.Source.Circle is {} circle)
                {
                    weight=Math.Cos(edge.Sweep/count/2);
                    var centre=new EditBoundaryPoint(circle.CenterX,circle.CenterY);
                    middle=centre+(middle-centre)*(1/(weight*weight));
                }
                controls.Add((a,1));controls.Add((middle,weight));
            }
        }
        controls.Add((edges[^1].End,1));
        var curve=new NurbsCurve(3,true,3,controls.Count);
        for(int i=0;i<controls.Count;i++)
        {
            var (p,w)=controls[i];
            curve.Points.SetPoint(i,p.X*w,p.Y*w,0,w);
        }
        for(int i=0;i<curve.Knots.Count;i++)curve.Knots[i]=i/2;
        return curve;
    }
}
