using System.Diagnostics;

namespace Aha.Topo;

/// <summary>One shared, resolution-limited terrain adjustment; this is not a surveyed road profile.</summary>
public sealed record RoadGradingResult(TerrainField Field, int CrossSectionSamples, int AdjustedNodes,
    int Iterations, bool Converged, double ElapsedSeconds, double MaximumAdjustmentMetres,
    double RmsAdjustmentMetres, double CrossSlopeRmsBefore, double CrossSlopeRmsAfter,
    double ReferenceHeightRmsMetres, double InfluenceDistanceMetres, int LimitedNodes = 0);

/// <summary>
/// Fit cadastral road cross-sections to the existing bilinear DEM without adding geometry or source samples.
/// Opposing-boundary midpoints retain their local longitudinal trend; cross derivatives approach zero.
/// A sparse, regularized least-squares solve handles intersections and overlapping influences together,
/// avoiding nearest-road switches. Land and road subsequently use this same corrected field.
/// </summary>
public static class RoadGrading
{
    // This preview adjustment must not turn a coarse hillside DEM into major invented earthworks.
    // Bilinear interpolation is a convex combination, so this node bound also bounds every surface point.
    public const double MaximumNodeAdjustmentMetres = 3;
    // Elevation continuity takes priority when closely spaced roads cannot both be flattened on the
    // source grid. This prevents a sub-cell pair of hairpin legs being collapsed to their mean height.
    private const double HeightWeight = 2048;
    private const double CrossWeight = 8;
    private const double AnchorWeight = 0.025;
    private const double BlendWeight = 0.15;
    private const int BlendRings = 2;

    public static RoadGradingResult Apply(TerrainField source, RoadArea roads, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(roads);
        ct.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var crossSections = RoadCrossSections.Build(source, roads);
        if (crossSections.Count == 0) return new(source, 0, 0, 0, true, timer.Elapsed.TotalSeconds, 0, 0, 0, 0, 0, 0);
        int stride = source.Columns + 1, count = source.Heights.Length;
        var matrix = new GridMatrix(stride, source.Rows + 1);
        var used = new bool[count];
        var crossRows = new List<(Stencil Stencil, double Original, double Weight)>();
        var referenceRows = new List<(Stencil Stencil, double Target)>();
        double scale = Math.Sqrt(source.Dx * source.Dy);
        foreach (var sample in crossSections)
        {
            ct.ThrowIfCancellationRequested();
            var center = Stencil.At(source, sample.X, sample.Y);
            double original = center.Evaluate(source.Heights);
            referenceRows.Add((center, sample.Height));
            matrix.Add(center, sample.Height - original, HeightWeight * sample.Weight, used);
            // Wide roads constrain several sections. Narrow roads still constrain the whole bilinear
            // cell's transverse derivative; no false fine-resolution geometry is invented.
            var sideA = new Point(sample.X - sample.Nx * sample.Width / 2, sample.Y - sample.Ny * sample.Width / 2);
            var sideB = new Point(sample.X + sample.Nx * sample.Width / 2, sample.Y + sample.Ny * sample.Width / 2);
            if (!Clip(source, sideA, sideB, out var crossA, out var crossB)) continue;
            int sections = Math.Max(1, (int)Math.Ceiling(crossA.Distance(crossB) / Math.Min(source.Dx, source.Dy)));
            for (int section = 0; section <= sections; section++)
            {
                double t = section / (double)sections;
                double x = crossA.X + t * (crossB.X - crossA.X), y = crossA.Y + t * (crossB.Y - crossA.Y);
                var derivative = Stencil.Derivative(source, x, y, sample.Nx, sample.Ny);
                double initial = derivative.Evaluate(source.Heights);
                double weight = sample.Weight / (sections + 1);
                crossRows.Add((derivative, initial, weight));
                matrix.Add(derivative.Scale(scale), -initial * scale, CrossWeight * weight, used);
            }
        }
        // Correction has compact support: untouched nodes remain exactly unchanged. Two surrounding
        // rings regularize the height change into existing land, with no separate transition strip.
        for (int ring = 0; ring < BlendRings; ring++)
        {
            var expanded = (bool[])used.Clone();
            for (int row = 0; row <= source.Rows; row++)
            for (int col = 0; col <= source.Columns; col++)
            {
                int i = row * stride + col;
                if (!used[i]) continue;
                if (col > 0) expanded[i - 1] = true;
                if (col < source.Columns) expanded[i + 1] = true;
                if (row > 0) expanded[i - stride] = true;
                if (row < source.Rows) expanded[i + stride] = true;
            }
            used = expanded;
        }
        matrix.Regularize(used, AnchorWeight, BlendWeight);
        var (correction, iterations, converged, limitedNodes) = matrix.Solve(ct);
        double maximum = 0, squared = 0; int changed = 0;
        var heights = (double[])source.Heights.Clone();
        for (int i = 0; i < count; i++)
        {
            if (!double.IsFinite(correction[i])) throw new InvalidOperationException("도로 지형 보정값이 유한하지 않습니다.");
            if (!used[i]) continue;
            heights[i] += correction[i];
            maximum = Math.Max(maximum, Math.Abs(correction[i]));
            squared += correction[i] * correction[i];
            if (Math.Abs(correction[i]) > 1e-8) changed++;
        }
        double before = 0, after = 0, crossWeight = 0, referenceError = 0;
        foreach (var row in crossRows)
        {
            before += row.Weight * row.Original * row.Original;
            double derivative = row.Stencil.Evaluate(heights);
            after += row.Weight * derivative * derivative; crossWeight += row.Weight;
        }
        foreach (var row in referenceRows)
        {
            double residual = row.Stencil.Evaluate(heights) - row.Target;
            referenceError += residual * residual;
        }
        var field = maximum < 1e-10 ? source : TerrainField.FromSamples(source.Bounds,
            source.Projection, source.Columns, source.Rows, heights);
        return new(field, crossSections.Count, changed, iterations, converged, timer.Elapsed.TotalSeconds,
            maximum, Math.Sqrt(squared / count), Math.Sqrt(before / Math.Max(crossWeight, 1e-20)),
            Math.Sqrt(after / Math.Max(crossWeight, 1e-20)), Math.Sqrt(referenceError / crossSections.Count),
            (BlendRings + 2) * Math.Sqrt(source.Dx * source.Dx + source.Dy * source.Dy), limitedNodes);
    }

    private readonly record struct Point(double X, double Y)
    {
        public double Distance(Point other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
    }

    private static bool Clip(TerrainField field, Point a, Point b, out Point from, out Point to)
    {
        double low = 0, high = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 1e-20) return q >= 0;
            double r = q / p;
            if (p < 0) low = Math.Max(low, r); else high = Math.Min(high, r);
            return low <= high;
        }
        bool intersects = Edge(-dx, a.X - field.X0) && Edge(dx, field.X1 - a.X) &&
            Edge(-dy, a.Y - field.Y0) && Edge(dy, field.Y1 - a.Y);
        from = new(Math.Clamp(a.X + low * dx, field.X0, field.X1), Math.Clamp(a.Y + low * dy, field.Y0, field.Y1));
        to = new(Math.Clamp(a.X + high * dx, field.X0, field.X1), Math.Clamp(a.Y + high * dy, field.Y0, field.Y1));
        return intersects;
    }

    internal readonly record struct Stencil(int I0, int I1, int I2, int I3, double C0, double C1, double C2, double C3)
    {
        public double Evaluate(double[] values) => C0 * values[I0] + C1 * values[I1] + C2 * values[I2] + C3 * values[I3];
        public Stencil Scale(double scale) => this with { C0 = C0 * scale, C1 = C1 * scale, C2 = C2 * scale, C3 = C3 * scale };
        public static Stencil At(TerrainField field, double x, double y) => Create(field, x, y, 0, 0, false);
        public static Stencil Derivative(TerrainField field, double x, double y, double nx, double ny) => Create(field, x, y, nx, ny, true);
        private static Stencil Create(TerrainField field, double x, double y, double nx, double ny, bool derivative)
        {
            var (col, row) = field.Cell(x, y);
            double u = Math.Clamp((x - field.X0) / field.Dx - col, 0, 1), v = Math.Clamp((y - field.Y0) / field.Dy - row, 0, 1);
            int i = row * (field.Columns + 1) + col, stride = field.Columns + 1;
            return derivative ? new(i, i + 1, i + stride, i + stride + 1,
                -(1 - v) * nx / field.Dx - (1 - u) * ny / field.Dy,
                (1 - v) * nx / field.Dx - u * ny / field.Dy,
                -v * nx / field.Dx + (1 - u) * ny / field.Dy,
                v * nx / field.Dx + u * ny / field.Dy) :
                new(i, i + 1, i + stride, i + stride + 1, (1 - u) * (1 - v), u * (1 - v), (1 - u) * v, u * v);
        }
    }

    /// <summary>
    /// Symmetric nine-point grid matrix, with Jacobi-preconditioned conjugate gradients.
    /// Positive anchors make the sum of weighted squared constraints strictly positive definite.
    /// Method: https://www.netlib.org/linalg/html_templates/node20.html
    /// </summary>
    internal sealed class GridMatrix
    {
        private readonly int stride, rows, count;
        private readonly double[] diagonal, right, up, upRight, upLeft, rhs;
        private int[] active = Array.Empty<int>();
        public GridMatrix(int stride, int rows)
        {
            this.stride = stride; this.rows = rows; count = stride * rows;
            diagonal = new double[count]; right = new double[count]; up = new double[count];
            upRight = new double[count]; upLeft = new double[count]; rhs = new double[count];
        }
        public void Add(Stencil stencil, double target, double weight, bool[] used)
        {
            Span<int> indices = stackalloc int[] { stencil.I0, stencil.I1, stencil.I2, stencil.I3 };
            Span<double> coefficients = stackalloc double[] { stencil.C0, stencil.C1, stencil.C2, stencil.C3 };
            for (int a = 0; a < 4; a++)
            {
                int i = indices[a]; double c = coefficients[a];
                if (Math.Abs(c) < 1e-16) continue;
                used[i] = true; diagonal[i] += weight * c * c; rhs[i] += weight * c * target;
                for (int b = a + 1; b < 4; b++)
                {
                    double entry = weight * c * coefficients[b];
                    // Topological corner pairs, not index differences: on a one-cell-wide grid,
                    // the NW diagonal and a horizontal neighbour both have index difference one.
                    if ((a == 0 && b == 1) || (a == 2 && b == 3)) right[i] += entry;
                    else if ((a == 0 && b == 2) || (a == 1 && b == 3)) up[i] += entry;
                    else if (a == 0 && b == 3) upRight[i] += entry;
                    else if (a == 1 && b == 2) upLeft[i] += entry;
                    else throw new InvalidOperationException("도로 보정 행렬의 이웃 관계가 올바르지 않습니다.");
                }
            }
        }
        public void Regularize(bool[] used, double anchor, double blend)
        {
            // Unused nodes have only a unit diagonal and zero right-hand side.
            // Keep their exact zero solution without visiting them every iteration.
            active = Enumerable.Range(0, count).Where(i => used[i]).ToArray();
            for (int row = 0; row < rows; row++)
            for (int col = 0; col < stride; col++)
            {
                int i = row * stride + col;
                diagonal[i] += used[i] ? anchor : 1;
                if (col + 1 < stride) Edge(i, i + 1, right);
                if (row + 1 < rows) Edge(i, i + stride, up);
            }
            void Edge(int i, int j, double[] entries)
            {
                if (used[i]) diagonal[i] += blend;
                if (used[j]) diagonal[j] += blend;
                if (used[i] && used[j]) entries[i] -= blend;
            }
        }
        private void Multiply(double[] x, double[] y)
        {
            foreach (int i in active) y[i] = diagonal[i] * x[i];
            foreach (int i in active)
            {
                int row = i / stride, col = i % stride;
                if (col + 1 < stride) Pair(i, i + 1, right[i]);
                if (row + 1 >= rows) continue;
                Pair(i, i + stride, up[i]);
                if (col + 1 < stride) Pair(i, i + stride + 1, upRight[i]);
                if (col > 0) Pair(i, i + stride - 1, upLeft[i]);
            }
            void Pair(int i, int j, double value) { y[i] += value * x[j]; y[j] += value * x[i]; }
        }
        public (double[] Solution, int Iterations, bool Converged, int LimitedNodes) Solve(CancellationToken ct)
        {
            var (x, iterations, converged) = SolveUnconstrained(ct);
            if (active.All(i => Math.Abs(x[i]) <= MaximumNodeAdjustmentMetres)) return (x, iterations, converged, 0);
            // Feasible initial iterate only. The final answer is the minimizer of the *bounded* energy,
            // not a clipped unconstrained result: neighbouring land and the road are solved together.
            foreach (int i in active) x[i] = Math.Clamp(x[i], -MaximumNodeAdjustmentMetres, MaximumNodeAdjustmentMetres);
            var gradient = new double[count];
            RecomputeGradient();
            for (int sweep = 1; ; sweep++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (int i in active) Update(i);
                for (int at = active.Length - 1; at >= 0; at--) Update(active[at]);
                if (sweep % 25 == 0) RecomputeGradient();
                double residual = 0;
                foreach (int i in active) residual = Math.Max(residual, Math.Abs(x[i] - Math.Clamp(
                    x[i] - gradient[i] / diagonal[i], -MaximumNodeAdjustmentMetres, MaximumNodeAdjustmentMetres)));
                if (residual < 1e-6)
                {
                    RecomputeGradient();
                    double actual = 0;
                    foreach (int i in active) actual = Math.Max(actual, Math.Abs(x[i] - Math.Clamp(
                        x[i] - gradient[i] / diagonal[i], -MaximumNodeAdjustmentMetres, MaximumNodeAdjustmentMetres)));
                    if (actual < 1e-6) return Finish(iterations + sweep, true);
                }
            }

            void RecomputeGradient()
            {
                Multiply(x, gradient);
                foreach (int i in active) gradient[i] -= rhs[i];
            }
            void Update(int i)
            {
                double next = Math.Clamp(x[i] - gradient[i] / diagonal[i], -MaximumNodeAdjustmentMetres, MaximumNodeAdjustmentMetres);
                double change = next - x[i];
                if (Math.Abs(change) < 1e-16) return;
                x[i] = next; gradient[i] += diagonal[i] * change;
                int col = i % stride, row = i / stride;
                if (col > 0) gradient[i - 1] += right[i - 1] * change;
                if (col + 1 < stride) gradient[i + 1] += right[i] * change;
                if (row > 0)
                {
                    gradient[i - stride] += up[i - stride] * change;
                    if (col > 0) gradient[i - stride - 1] += upRight[i - stride - 1] * change;
                    if (col + 1 < stride) gradient[i - stride + 1] += upLeft[i - stride + 1] * change;
                }
                if (row + 1 < rows)
                {
                    gradient[i + stride] += up[i] * change;
                    if (col + 1 < stride) gradient[i + stride + 1] += upRight[i] * change;
                    if (col > 0) gradient[i + stride - 1] += upLeft[i] * change;
                }
            }
            (double[], int, bool, int) Finish(int total, bool success) =>
                (x, total, success, active.Count(i => Math.Abs(x[i]) >= MaximumNodeAdjustmentMetres - 1e-7));
        }
        private (double[] Solution, int Iterations, bool Converged) SolveUnconstrained(CancellationToken ct)
        {
            var x = new double[count]; var r = (double[])rhs.Clone(); var p = new double[count]; var ap = new double[count];
            double norm = Dot(rhs, rhs), rz = 0;
            if (norm < 1e-20) return (x, 0, true);
            foreach (int i in active) { p[i] = r[i] / diagonal[i]; rz += r[i] * p[i]; }
            for (int iteration = 1; ; iteration++)
            {
                ct.ThrowIfCancellationRequested();
                Multiply(p, ap);
                double denominator = Dot(p, ap);
                if (!(denominator > 0)) throw new InvalidOperationException("도로 보정 계산의 양의 정부호 조건을 만족하지 못했습니다.");
                double alpha = rz / denominator;
                foreach (int i in active) { x[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
                bool refresh = iteration % 64 == 0 || Dot(r, r) <= norm * 1e-12;
                if (refresh)
                {
                    // Recursive CG residual can drift on high-contrast road weights.
                    Multiply(x, ap);
                    foreach (int i in active) r[i] = rhs[i] - ap[i];
                    if (Dot(r, r) <= norm * 1e-12) return (x, iteration, true);
                }
                double next = 0;
                foreach (int i in active) next += r[i] * r[i] / diagonal[i];
                double beta = refresh ? 0 : next / rz;
                foreach (int i in active) p[i] = r[i] / diagonal[i] + beta * p[i];
                rz = next;
            }
        }
        private double Dot(double[] a, double[] b)
        {
            double sum = 0; foreach (int i in active) sum += a[i] * b[i]; return sum;
        }
    }
}
