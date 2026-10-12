namespace Aha.Topo.Checks;

internal static class RoadSolverChecks
{
    internal static void Run()
    {
        foreach(int size in new[]{21,301})
        foreach(double target in new[]{1d,10d})
        {
            int at=size/2;
            var sparse=new RoadGrading.GridMatrix(size,1);var touched=new bool[size];
            sparse.Add(new(at,at+1,at,at+1,1,0,0,0),target,2048,touched);
            sparse.Regularize(touched,.025,.15);
            var solved=sparse.Solve(CancellationToken.None);
            double expected=Math.Min(3,2048*target/(2048+.025+2*.15));
            if(!solved.Converged||Math.Abs(solved.Solution[at]-expected)>1e-9||
                solved.Solution.Where((_,i)=>i!=at).Any(value=>value!=0))
                throw new InvalidOperationException("Compact road support changed the anchored solution or untouched nodes");
        }
        // Mid-cell height constraints create a high-contrast positive-definite
        // tridiagonal system. Its solution is independently obtained by Thomas
        // elimination, not by repeating the iterative product algorithm.
        const int n=500;
        var matrix=new RoadGrading.GridMatrix(n,1);
        var used=new bool[n]; var rhs=new double[n];
        double previousTarget=0;
        for(int i=0;i<n-1;i++)
        {
            double target=(Math.Sin(i*1.37)+Math.Cos(i*.131))/1024-previousTarget;
            previousTarget=target;
            matrix.Add(new(i,i+1,i,i+1,.5,.5,0,0),target,2048,used);
            rhs[i]+=1024*target;rhs[i+1]+=1024*target;
        }
        matrix.Regularize(used,.025,.15);
        var diagonal=Enumerable.Repeat(1024+.325,n).ToArray();
        diagonal[0]=diagonal[^1]=512+.175;
        const double neighbour=512-.15;
        var upper=Enumerable.Repeat(neighbour,n-1).ToArray();
        var solvedRhs=(double[])rhs.Clone();var pivots=(double[])diagonal.Clone();
        for(int i=1;i<n;i++)
        {double factor=neighbour/pivots[i-1];pivots[i]-=factor*upper[i-1];solvedRhs[i]-=factor*solvedRhs[i-1];}
        var exact=new double[n];exact[^1]=solvedRhs[^1]/pivots[^1];
        for(int i=n-2;i>=0;i--)exact[i]=(solvedRhs[i]-upper[i]*exact[i+1])/pivots[i];
        var result=matrix.Solve(CancellationToken.None);
        double residual=0,norm=rhs.Sum(x=>x*x),error=0;
        for(int i=0;i<n;i++)
        {
            double actual=diagonal[i]*result.Solution[i]+(i>0?neighbour*result.Solution[i-1]:0)+
                (i+1<n?neighbour*result.Solution[i+1]:0)-rhs[i];
            residual+=actual*actual;error+=Math.Pow(result.Solution[i]-exact[i],2);
        }
        // The .025 anchor bounds the inverse norm. Check both actual residual
        // and the independent solution with that mathematical error bound.
        if(!result.Converged||residual>norm*1e-12||Math.Sqrt(error)>Math.Sqrt(residual)/.025+1e-9)
            throw new InvalidOperationException("Road solver did not converge to the constrained energy solution");
        Console.WriteLine($"Road grading high-contrast system: {result.Iterations} iterations, relative residual {Math.Sqrt(residual/norm):g4}.");
    }
}
