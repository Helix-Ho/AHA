using Clipper2Lib;

namespace Aha.Topo;

/// <summary>Separate material fans at a touching profile vertex through its empty angular sectors.</summary>
internal static class ProfileContact
{
    internal static IReadOnlyList<PathsD> Split(PathsD paths)
    {
        // Source-only geometry retains its original coordinates when no exact
        // contact exists; do not round a valid small feature just to test it.
        var vertices = new HashSet<(double, double)>();
        if (!paths.SelectMany(path => path).Any(p => !vertices.Add((p.x, p.y)))) return new[] { paths };
        var pending = new Queue<PathsD>(ContourPaths.Components(paths));
        var result = new List<PathsD>();
        while (pending.TryDequeue(out var component))
        {
            if (!TryFind(component, out var contact)) { result.Add(component); continue; }
            var pieces = Domains(component, contact).SelectMany(domain =>
                ContourPaths.Components(ContourPaths.Intersect(component, domain))).ToArray();
            if (pieces.Length < 2 || Math.Abs(pieces.Sum(ContourPaths.Area) - ContourPaths.Area(component)) >
                Math.Max(1e-8, ContourPaths.Area(component) * 1e-9))
                throw new InvalidOperationException("접촉 프로파일의 분할 면적이 보존되지 않았습니다.");
            foreach (var piece in pieces) pending.Enqueue(piece);
        }
        return result;
    }

    internal static PathsD[] Domains(PathsD component, PointD contact)
    {
        // Contact is topological equality, not two distinct intersections
        // rounding to the same grid point. Directed ring edges already tell
        // which angular sectors contain material; no triangulation is needed.
        bool Same(PointD p)=>p.x==contact.x&&p.y==contact.y;
        var events=component.SelectMany(path=>Enumerable.Range(0,path.Count).Where(i=>Same(path[i]))
            .SelectMany(i=>new[]{(Angle:Angle(path[(i+1)%path.Count]),Delta:1),
                (Angle:Angle(path[(i+path.Count-1)%path.Count]),Delta:-1)}))
            .GroupBy(e=>e.Angle).OrderBy(g=>g.Key).Select(g=>(Angle:g.Key,Delta:g.Sum(e=>e.Delta))).ToArray();
        var angles=events.Select(e=>e.Angle).ToArray();
        int winding=0,minimum=0;
        foreach(var e in events){winding+=e.Delta;minimum=Math.Min(minimum,winding);}
        winding=-minimum;
        var cuts = new List<double>();
        for (int i = 0; i < angles.Length; i++)
        {
            double end = angles[(i + 1) % angles.Length]; if (end <= angles[i]) end += 2 * Math.PI;
            double middle = (angles[i] + end) / 2;
            winding+=events[i].Delta;
            if (winding==0) cuts.Add(middle % (2 * Math.PI));
        }
        cuts.Sort();
        if (cuts.Count < 2) throw new InvalidOperationException("프로파일 접촉 정점의 재료 부채꼴을 구분할 수 없습니다.");
        double radius = 2 * (1 + component.SelectMany(p => p).Max(p => Math.Sqrt(Math.Pow(p.x - contact.x, 2) + Math.Pow(p.y - contact.y, 2))));
        return Enumerable.Range(0, cuts.Count).Select(i =>
        {
            double start = cuts[i], end = cuts[(i + 1) % cuts.Count]; if (end <= start) end += 2 * Math.PI;
            int count = (int)Math.Ceiling((end - start) / (Math.PI / 4)); var path = new PathD { contact };
            for (int j = 0; j <= count; j++) { double a = start + (end - start) * j / count; path.Add(new(contact.x + radius * Math.Cos(a), contact.y + radius * Math.Sin(a))); }
            return new PathsD { path };
        }).ToArray();
        double Angle(PointD p) { double a = Math.Atan2(p.y - contact.y, p.x - contact.x); return a < 0 ? a + 2 * Math.PI : a; }
    }

    internal static bool TryFind(PathsD paths, out PointD point)
    {
        var points = new HashSet<(double, double)>();
        foreach (var path in paths) foreach (var p in path)
                if (!points.Add((p.x,p.y))) { point = p; return true; }
        point = default; return false;
    }
}
