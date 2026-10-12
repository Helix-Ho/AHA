using System.Numerics;

namespace Aha.Topo;

/// <summary>Exact events of finite integer-grid lines. Coordinates stay rational;
/// conversion to metres occurs only for the geometry representation.</summary>
internal sealed record PlanarRationalPoint(BigInteger X,BigInteger Y,BigInteger W)
{
    internal static PlanarRationalPoint Grid(long x,long y)=>new(x,y,1);
    private static PlanarRationalPoint Make(BigInteger x,BigInteger y,BigInteger w)
    {
        if(w.Sign<0){x=-x;y=-y;w=-w;}
        var gcd=BigInteger.GreatestCommonDivisor(BigInteger.GreatestCommonDivisor(BigInteger.Abs(x),BigInteger.Abs(y)),w);
        return new(x/gcd,y/gcd,w/gcd);
    }
    internal static PlanarRationalPoint Mid(PlanarRationalPoint a,PlanarRationalPoint b)
        =>Make(a.X*b.W+b.X*a.W,a.Y*b.W+b.Y*a.W,2*a.W*b.W);
    internal EditBoundaryPoint Point=>new(Coordinate(X),Coordinate(Y));
    private double Coordinate(BigInteger value)
    {
        // Preserve the original representation of grid-aligned coordinates.
        // Multiplying a quotient by 1e-6 can place a horizontal event on the
        // other side of its own endpoint in a later floating ray query.
        var whole=BigInteger.DivRem(value,W,out var remainder);
        return ((double)whole+(double)remainder/(double)W)/(1/ModelPlanPolicy.PrecisionMetres);
    }
    internal static PlanarRationalPoint? Crossing(PlanarRationalPoint a,PlanarRationalPoint b,PlanarRationalPoint c,PlanarRationalPoint d)
    {
        var rx=b.X-a.X;var ry=b.Y-a.Y;var sx=d.X-c.X;var sy=d.Y-c.Y;
        var den=rx*sy-ry*sx;if(den.IsZero)return null;
        var num=(c.X-a.X)*sy-(c.Y-a.Y)*sx;
        return Make(a.X*den+rx*num,a.Y*den+ry*num,den);
    }
    internal double Cross(PlanarRationalPoint a,PlanarRationalPoint b)
    {
        var ax=a.X*W-X*a.W;var ay=a.Y*W-Y*a.W;
        var bx=b.X*W-X*b.W;var by=b.Y*W-Y*b.W;
        return (double)(ax*by-ay*bx)/(double)(W*W*a.W*b.W)*
            ModelPlanPolicy.PrecisionMetres*ModelPlanPolicy.PrecisionMetres;
    }
    internal int CompareY(PlanarRationalPoint b)=>(Y*b.W-b.Y*W).Sign;
    internal int CompareX(PlanarRationalPoint b)=>(X*b.W-b.X*W).Sign;
    internal int Orientation(PlanarRationalPoint a,PlanarRationalPoint b)
        =>((b.X*a.W-a.X*b.W)*(Y*a.W-a.Y*W)-(b.Y*a.W-a.Y*b.W)*(X*a.W-a.X*W)).Sign;
}
