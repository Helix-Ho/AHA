namespace Aha.Topo;

internal readonly record struct PlanarMeshTriangle(int A, int B, int C);

/// <summary>Boundary cycles follow triangle fans, preserving point-touching components and holes.</summary>
internal static class PlanarMeshTopology
{
    private readonly record struct Edge(int A, int B)
    {
        internal static Edge Undirected(int a, int b) => a < b ? new(a, b) : new(b, a);
    }
    internal static List<List<int>> BoundaryCycles(IReadOnlyList<PlanarMeshTriangle> triangles)
    {
        var boundaryTriangles = new Dictionary<Edge, int>();
        var adjacentTriangles = new Dictionary<Edge, (int First, int Second)>();
        var incidence = new Dictionary<Edge, (int A, int B, int Triangle, int Count)>();
        for (int i = 0; i < triangles.Count; i++)
        {
            var t = triangles[i];
            if (t.A == t.B || t.B == t.C || t.C == t.A)
                throw new InvalidOperationException("평면 메시 삼각형의 꼭짓점이 겹칩니다.");
            foreach (var edge in new[] { new Edge(t.A, t.B), new Edge(t.B, t.C), new Edge(t.C, t.A) })
            {
                var key = Edge.Undirected(edge.A, edge.B);
                if (!incidence.TryGetValue(key, out var previous))
                    incidence.Add(key, (edge.A, edge.B, i, 1));
                else
                {
                    if (previous.Count != 1 || previous.A != edge.B || previous.B != edge.A)
                        throw new InvalidOperationException("평면 메시의 공통 경계가 올바르지 않습니다.");
                    adjacentTriangles.Add(key, (previous.Triangle, i));
                    incidence[key] = (previous.A, previous.B, previous.Triangle, 2);
                }
            }
        }
        foreach (var pair in incidence)
            if (pair.Value.Count == 1) boundaryTriangles.Add(pair.Key, pair.Value.Triangle);
        // A coordinate can belong to several boundary fans when cadastral
        // components meet at one point. Follow incident triangle half-edges,
        // never choose a successor from XY position or a turn-angle heuristic.
        var next = new Dictionary<Edge, Edge>();
        foreach (var pair in boundaryTriangles)
        {
            var triangle = triangles[pair.Value];
            var directed = new[] { new Edge(triangle.A, triangle.B), new Edge(triangle.B, triangle.C), new Edge(triangle.C, triangle.A) }
                .Single(edge => Edge.Undirected(edge.A, edge.B) == pair.Key);
            int currentTriangle=pair.Value;var incoming=directed;var visitedFan=new HashSet<int>();
            while(true)
            {
                if(!visitedFan.Add(currentTriangle))
                    throw new InvalidOperationException("상면 표시 경계의 삼각형 연결이 순환합니다.");
                var current=triangles[currentTriangle];
                int third=current.A!=incoming.A&&current.A!=incoming.B?current.A:
                    current.B!=incoming.A&&current.B!=incoming.B?current.B:current.C;
                var outgoing=new Edge(incoming.B,third);var key=Edge.Undirected(outgoing.A,outgoing.B);
                if(boundaryTriangles.ContainsKey(key)){next.Add(directed,outgoing);break;}
                if(!adjacentTriangles.TryGetValue(key,out var neighbours))
                    throw new InvalidOperationException("상면 표시 경계의 인접 삼각형이 없습니다.");
                currentTriangle=neighbours.First==currentTriangle?neighbours.Second:neighbours.First;
                incoming=new(outgoing.B,outgoing.A);
            }
        }
        if(next.Values.Distinct().Count()!=next.Count)
            throw new InvalidOperationException("상면 표시 경계의 삼각형 고리가 서로 겹칩니다.");
        var cycles = new List<List<int>>(); var visited = new HashSet<Edge>();
        foreach (var first in next.Keys)
        {
            if (visited.Contains(first)) continue;
            var cycle = new List<int>(); var edge = first;
            do
            {
                if (!visited.Add(edge) || !next.TryGetValue(edge, out var following))
                    throw new InvalidOperationException("상면 표시 경계가 닫히지 않습니다.");
                cycle.Add(edge.A); edge = following;
            } while (edge != first);
            cycles.Add(cycle);
        }
        return cycles;
    }

}
