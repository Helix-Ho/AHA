namespace Aha.Topo;

/// <summary>Regular source samples in local metres. Bilinear evaluation is shared by both output forms.</summary>
public sealed class TerrainField
{
    public GeoBounds Bounds { get; }
    public LocalProjection Projection { get; }
    public int Columns { get; }
    public int Rows { get; }
    public double[] Heights { get; }
    public double X0 { get; }
    public double Y0 { get; }
    public double Dx { get; }
    public double Dy { get; }
    public double X1 => X0 + Columns * Dx;
    public double Y1 => Y0 + Rows * Dy;
    public double CellLongitudeDegrees => (Bounds.East - Bounds.West) / Columns;
    public double CellLatitudeDegrees => (Bounds.North - Bounds.South) / Rows;
    private readonly double[] modelXs, modelYs;

    private TerrainField(GeoBounds bounds, LocalProjection projection, int columns, int rows, double[] heights)
    {
        bounds.Validate();
        if (columns < 1 || rows < 1 || (long)(columns + 1) * (rows + 1) > 1_000_000 ||
            heights.Length != (columns + 1) * (rows + 1) || heights.Any(z => !double.IsFinite(z)))
            throw new ArgumentException("지형 격자의 크기 또는 높이가 올바르지 않습니다.");
        Bounds = bounds; Projection = projection; Columns = columns; Rows = rows; Heights = (double[])heights.Clone();
        var sw = projection.ToLocal(new(bounds.West, bounds.South));
        var ne = projection.ToLocal(new(bounds.East, bounds.North));
        X0 = sw.X; Y0 = sw.Y; Dx = (ne.X - sw.X) / columns; Dy = (ne.Y - sw.Y) / rows;
        modelXs = Enumerable.Range(0, columns + 1).Select(i => Math.Round(X0 + i * Dx, EditRegion.ClipPrecision)).ToArray();
        modelYs = Enumerable.Range(0, rows + 1).Select(i => Math.Round(Y0 + i * Dy, EditRegion.ClipPrecision)).ToArray();
        if (modelXs.Zip(modelXs.Skip(1)).Any(pair => pair.First >= pair.Second) ||
            modelYs.Zip(modelYs.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new ArgumentException("모델 정밀도에서 지형 격자 간격을 구분할 수 없습니다.");
    }

    public static TerrainField FromSamples(GeoBounds bounds, LocalProjection projection, int columns, int rows, double[] elevations)
        => new(bounds, projection, columns, rows, elevations);

    public static TerrainField FromGrid(GeoBounds bounds, LocalProjection projection, TerrainGrid terrain)
    {
        int columns = Math.Max(1, (int)Math.Ceiling((bounds.East - bounds.West) / terrain.CellLongitudeDegrees));
        int rows = Math.Max(1, (int)Math.Ceiling((bounds.North - bounds.South) / terrain.CellLatitudeDegrees));
        if ((long)(columns + 1) * (rows + 1) > 1_000_000) throw new ArgumentException("지형 격자가 너무 큽니다.");
        var heights = new double[(columns + 1) * (rows + 1)];
        for (int row = 0; row <= rows; row++)
        for (int col = 0; col <= columns; col++)
            heights[row * (columns + 1) + col] = terrain.Sample(new(
                bounds.West + (bounds.East - bounds.West) * col / columns,
                bounds.South + (bounds.North - bounds.South) * row / rows));
        return new(bounds, projection, columns, rows, heights);
    }

    public double Node(int column, int row) => Heights[row * (Columns + 1) + column];
    // Model curves, surface knots and floor statistics use this one planar
    // grid. Heights remain source samples; rounded node spacing is allowed to
    // vary by one precision unit instead of mixing raw and model interpolation.
    public double ModelX(int column) => modelXs[column];
    public double ModelY(int row) => modelYs[row];
    public (int Column, int Row) ModelCell(double x, double y)
    {
        Cell(x, y); // Preserve the extent contract, including its coordinate precision.
        return (Index(modelXs, x), Index(modelYs, y));
        static int Index(double[] nodes, double value)
        {
            int index = Array.BinarySearch(nodes, value);
            return Math.Clamp(index < 0 ? ~index - 1 : index, 0, nodes.Length - 2);
        }
    }
    public double SampleModelLocal(double x, double y)
    {
        var (col, row) = ModelCell(x, y);
        double u = Math.Clamp((x - ModelX(col)) / (ModelX(col + 1) - ModelX(col)), 0, 1);
        double v = Math.Clamp((y - ModelY(row)) / (ModelY(row + 1) - ModelY(row)), 0, 1);
        return (1-v)*((1-u)*Node(col,row)+u*Node(col+1,row)) +
            v*((1-u)*Node(col,row+1)+u*Node(col+1,row+1));
    }
    public (double X, double Y, double Z) ModelNormalLocal(double x, double y)
    {
        var (col, row) = ModelCell(x, y);
        double sx=0, sy=0; int count=0;
        for (int r=Math.Max(0,row-1);r<=Math.Min(Rows-1,row+1);r++)
        for (int c=Math.Max(0,col-1);c<=Math.Min(Columns-1,col+1);c++)
        {
            double west=ModelX(c), east=ModelX(c+1), south=ModelY(r), north=ModelY(r+1);
            if (x<west-2e-6 || x>east+2e-6 || y<south-2e-6 || y>north+2e-6) continue;
            double u=Math.Clamp((x-west)/(east-west),0,1), v=Math.Clamp((y-south)/(north-south),0,1);
            sx+=((1-v)*(Node(c+1,r)-Node(c,r))+v*(Node(c+1,r+1)-Node(c,r+1)))/(east-west);
            sy+=((1-u)*(Node(c,r+1)-Node(c,r))+u*(Node(c+1,r+1)-Node(c+1,r)))/(north-south);
            count++;
        }
        sx/=count;sy/=count;double length=Math.Sqrt(sx*sx+sy*sy+1);
        return (-sx/length,-sy/length,1/length);
    }
    public IEnumerable<double> ModelSegmentCuts(double ax, double ay, double bx, double by)
    {
        var cuts=new SortedSet<double>{0,1};
        Add(modelXs,ax,bx);Add(modelYs,ay,by);return cuts;
        void Add(IEnumerable<double> nodes,double a,double b)
        {
            if(a==b)return;
            foreach(double node in nodes)
            {double t=(node-a)/(b-a);if(t>1e-12&&t<1-1e-12)cuts.Add(t);}
        }
    }
    public (int Column, int Row) Cell(double x, double y)
    {
        if (x < X0 - 0.000002 || x > X1 + 0.000002 || y < Y0 - 0.000002 || y > Y1 + 0.000002)
            throw new ArgumentOutOfRangeException(nameof(x), "모델 지형 범위 밖의 높이를 요청했습니다.");
        return (Math.Clamp((int)Math.Floor((x - X0) / Dx), 0, Columns - 1),
            Math.Clamp((int)Math.Floor((y - Y0) / Dy), 0, Rows - 1));
    }
    public double SampleLocal(double x, double y)
    {
        var (col, row) = Cell(x, y);
        var u = Math.Clamp((x - X0) / Dx - col, 0, 1);
        var v = Math.Clamp((y - Y0) / Dy - row, 0, 1);
        return (1 - v) * ((1 - u) * Node(col, row) + u * Node(col + 1, row)) +
            v * ((1 - u) * Node(col, row + 1) + u * Node(col + 1, row + 1));
    }
}
