using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

namespace Aha.Topo;

internal sealed record RoadCrossSection(double X,double Y,double Nx,double Ny,double Width,double Height,double Weight);

/// <summary>Local opposing sides of cadastral road areas, without a separately sourced centreline.</summary>
internal static class RoadCrossSections
{
    private const double GeometryTolerance=0.000002;
    // Only a nearly shortest, opposing-wall section has a supported road direction.
    // Junctions, large squares and road end-to-end spans are not forced into a crossfall.
    private const double MinimumMedialRatio=.95;
    private const double MinimumOpposingCosine=.95;
    private sealed record Edge(double Ax,double Ay,double Bx,double By,double Dx,double Dy,double Length);

    internal static List<RoadCrossSection> Build(TerrainField field,RoadArea area)
    {
        var edges=new List<Edge>();var index=new STRtree<Edge>();
        foreach(var ring in area.Outlines)
        for(int i=0;i<ring.Count;i++)
        {
            var a=ring[i];var b=ring[(i+1)%ring.Count];double dx=b.x-a.x,dy=b.y-a.y,length=Math.Sqrt(dx*dx+dy*dy);
            if(length<=GeometryTolerance)continue;
            var edge=new Edge(a.x,a.y,b.x,b.y,dx/length,dy/length,length);edges.Add(edge);
            index.Insert(new Envelope(a.x,b.x,a.y,b.y),edge);
        }
        index.Build();
        var result=new List<RoadCrossSection>();
        double step=Math.Min(field.Dx,field.Dy)/2,halfWindow=2*Math.Max(field.Dx,field.Dy);
        double diagonal=Math.Sqrt(Math.Pow(field.X1-field.X0,2)+Math.Pow(field.Y1-field.Y0,2));
        foreach(var edge in edges)
        {
            int count=Math.Max(1,(int)Math.Ceiling(edge.Length/step));
            for(int i=0;i<count;i++)
            {
                double along=edge.Length*(i+.5)/count,x=edge.Ax+along*edge.Dx,y=edge.Ay+along*edge.Dy;
                double nx=-edge.Dy,ny=edge.Dx;
                var (width,opposite)=FirstBoundary(x,y,nx,ny,diagonal);
                if(opposite is null||width<=GeometryTolerance||edge.Dx*opposite.Dx+edge.Dy*opposite.Dy>-MinimumOpposingCosine)continue;
                double mx=x+nx*width/2,my=y+ny*width/2,radius=width/2;
                double support=radius*MinimumMedialRatio;
                if(index.Query(new Envelope(mx-support,mx+support,my-support,my+support)).Any(other=>Distance(mx,my,other)<support-GeometryTolerance))continue;
                double height=LocalLongitudinalHeight(mx,my,edge.Dx,edge.Dy);
                result.Add(new(mx,my,nx,ny,width,height,edge.Length/count/step/2));
            }
        }
        // Deterministic assembly independent of cadastral feature/ring enumeration.
        return result.OrderBy(s=>s.X).ThenBy(s=>s.Y).ThenBy(s=>s.Nx).ThenBy(s=>s.Ny).ThenBy(s=>s.Width).ToList();

        (double Distance,Edge? Edge) FirstBoundary(double x,double y,double dx,double dy,double maximum)
        {
            double radius=Math.Min(Math.Max(step*2,GeometryTolerance*2),maximum);
            while(radius>0)
            {
                var envelope=new Envelope(x,x+dx*radius,y,y+dy*radius);envelope.ExpandBy(GeometryTolerance);
                double nearest=double.PositiveInfinity;Edge? found=null;
                foreach(var other in index.Query(envelope))
                {
                    double ex=other.Bx-other.Ax,ey=other.By-other.Ay,den=dx*ey-dy*ex;
                    if(Math.Abs(den)<1e-14)continue;
                    double ox=other.Ax-x,oy=other.Ay-y;
                    double t=(ox*ey-oy*ex)/den,u=(ox*dy-oy*dx)/den;
                    if(t>GeometryTolerance&&t<=radius+GeometryTolerance&&u>=-1e-10&&u<=1+1e-10&&t<nearest){nearest=t;found=other;}
                }
                if(found is not null)return(nearest,found);
                if(radius>=maximum)return(maximum,null);
                radius=Math.Min(maximum,radius*2);
            }
            return(maximum,null);
        }
        double LocalLongitudinalHeight(double x,double y,double tx,double ty)
        {
            double forward=FirstBoundary(x,y,tx,ty,halfWindow).Distance;
            double backward=FirstBoundary(x,y,-tx,-ty,halfWindow).Distance;
            double w0=0,w1=0,w2=0,z0=0,z1=0;
            int samples=(int)Math.Ceiling(halfWindow/step);
            for(int j=-samples;j<=samples;j++)
            {
                double s=j*step,ratio=Math.Abs(s)/halfWindow;
                if(ratio>=1||s>=forward-GeometryTolerance||-s>=backward-GeometryTolerance)continue;
                double px=x+s*tx,py=y+s*ty;
                if(px<field.X0||px>field.X1||py<field.Y0||py>field.Y1)continue;
                double weight=Math.Pow(1-ratio*ratio*ratio,3),z=field.SampleLocal(px,py);
                w0+=weight;w1+=weight*s;w2+=weight*s*s;z0+=weight*z;z1+=weight*z*s;
            }
            double determinant=w0*w2-w1*w1;
            return determinant>1e-12?(z0*w2-z1*w1)/determinant:field.SampleLocal(x,y);
        }
    }

    private static double Distance(double x,double y,Edge edge)
    {
        double t=Math.Clamp((x-edge.Ax)*edge.Dx+(y-edge.Ay)*edge.Dy,0,edge.Length);
        return Math.Sqrt(Math.Pow(x-edge.Ax-t*edge.Dx,2)+Math.Pow(y-edge.Ay-t*edge.Dy,2));
    }
}
