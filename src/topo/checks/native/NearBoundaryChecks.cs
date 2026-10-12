using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal static class NearBoundaryChecks
{
    internal sealed record BoundaryAudit(bool Passed,int Arcs,int TangentLines,int Samples,double MaximumArcError,double MaximumGap);
    internal static BoundaryAudit AuditBoundary(EditRegion region,IEnumerable<Brep> nearGeometry,double scale=1,double toleranceMetres=1e-6,bool requireAnalyticArcs=true)
    {
        var expectedLines=region.Boundaries.SelectMany(loop=>loop.Segments).Where(s=>s.Circle is null).ToArray();
        var curves=new List<(Curve Curve,BoundingBox Bounds)>();
        int arcs=0,lines=0,samplesCount=0;double arcError=0,boundaryError=0;
        foreach(var face in nearGeometry.SelectMany(b=>b.Faces))
        {
            if(face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid).Z<=1e-8)continue;
            foreach(var edge in face.Loops.SelectMany(loop=>loop.Trims).Select(t=>t.Edge).Where(e=>e is not null).Distinct())
            {
                var trim=edge.DuplicateCurve();
                trim.Transform(Transform.Scale(Point3d.Origin,1/scale));
                trim.Transform(Transform.PlanarProjection(Plane.WorldXY));
                curves.Add((trim,trim.GetBoundingBox(true)));
                var samples=new[]{trim.PointAtStart,trim.PointAt(trim.Domain.Mid),trim.PointAtEnd};
                if(trim.TryGetArc(out Arc arc,toleranceMetres)&&samples.All(p=>HullChecks.DistanceToBoundary(new(p.X,p.Y),region.Boundaries)<toleranceMetres))
                {
                    double error=region.Circles.Min(c=>Math.Max(Math.Abs(arc.Radius-c.RadiusMetres),Math.Sqrt(Math.Pow(arc.Center.X-c.CenterX,2)+Math.Pow(arc.Center.Y-c.CenterY,2))));
                    arcs++;arcError=Math.Max(arcError,error);
                }
                if(trim.IsLinear(1e-8)&&expectedLines.Any(line=>samples.All(p=>HullChecks.DistanceToSegment(new(p.X,p.Y),line)<1e-6)))lines++;
            }
        }
        // Compare every authoritative arc/tangent with actual near-top boundary curves,
        // independently of their display mesh and the solid-construction pathway.
        foreach(var segment in region.Boundaries.SelectMany(loop=>loop.Segments))
        {
        int divisions=segment.Circle is {} circle?Math.Max(32,(int)Math.Ceiling(Math.Abs(segment.SweepRadians)/
            (2*Math.Acos(Math.Clamp(1-toleranceMetres/4/circle.RadiusMetres,-1,1))))):32;
        for(int i=0;i<=divisions;i++)
        {
            var exact=segment.PointAt(i/(double)divisions);var point=new Point3d(exact.X,exact.Y,0);
            double error=double.PositiveInfinity;
            foreach(var candidate in curves)
            {
                var box=candidate.Bounds;
                if(point.X<box.Min.X-2*toleranceMetres||point.X>box.Max.X+2*toleranceMetres||point.Y<box.Min.Y-2*toleranceMetres||point.Y>box.Max.Y+2*toleranceMetres)continue;
                if(candidate.Curve.ClosestPoint(point,out double parameter))error=Math.Min(error,point.DistanceTo(candidate.Curve.PointAt(parameter)));
            }
            boundaryError=Math.Max(boundaryError,double.IsFinite(error)?error:1e20);samplesCount++;
        }
        }
        foreach(var entry in curves)entry.Curve.Dispose();
        return new(samplesCount>0&&boundaryError<=toleranceMetres&&(!requireAnalyticArcs||arcs>0&&arcError<toleranceMetres&&(expectedLines.Length==0||lines>0)),arcs,lines,samplesCount,arcError,boundaryError);
    }

}
