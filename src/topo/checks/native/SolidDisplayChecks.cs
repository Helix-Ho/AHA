using Rhino.Geometry;

namespace Aha.Topo.Checks;

internal sealed record SolidDisplayAudit(int NakedEdges,int NonmanifoldEdges,int SameDirectionEdges,int DegenerateTriangles,int MissingFaceCaches,int Triangles,
    IReadOnlyList<object> DegenerateSamples, IReadOnlyList<object> NakedSamples,int ConnectedComponents)
{
    public bool Passed=>NakedEdges==0&&NonmanifoldEdges==0&&SameDirectionEdges==0&&DegenerateTriangles==0&&MissingFaceCaches==0&&Triangles>0;
}

internal static class SolidDisplayChecks
{
    // Smaller than the one-micrometre source precision: retain short real edges
    // while identifying only floating-point copies of the same shared vertex.
    internal const double WeldToleranceMetres=1e-8;
    internal static SolidDisplayAudit Check(Brep solid,double scale)=>
        CheckMeshes(solid.Faces.Select(face=>face.GetMesh(MeshType.Render)),scale);

    internal static SolidDisplayAudit CheckMeshes(IEnumerable<Mesh?> meshes,double scale)
    {
        var points=new List<Point3d>();
        var parents=new List<int>();
        var buckets=new Dictionary<(long X,long Y,long Z),List<int>>();
        var edges=new Dictionary<(int A,int B),(int Count,int Direction,int FirstTriangle)>();
        int degenerate=0,missing=0,triangles=0;
        var degenerateSamples = new List<object>();
        foreach(var mesh in meshes)
        {
            if(mesh is null||!mesh.IsValid){missing++;continue;}
            var vertexIds=mesh.Vertices.ToPoint3dArray().Select(p=>Vertex(p/scale)).ToArray();
            foreach(var f in mesh.Faces)
            {
                Triangle(vertexIds[f.A],vertexIds[f.B],vertexIds[f.C]);
                Sample(f.A,f.B,f.C);
                if(f.IsQuad)Triangle(vertexIds[f.A],vertexIds[f.C],vertexIds[f.D]);
                if(f.IsQuad)Sample(f.A,f.C,f.D);
            }
            void Sample(int a,int b,int c)
            {
                if(degenerateSamples.Count>=8)return;
                if(vertexIds[a]==vertexIds[b]||vertexIds[b]==vertexIds[c]||vertexIds[c]==vertexIds[a])
                    degenerateSamples.Add(new {a=(mesh.Vertices.Point3dAt(a)/scale).ToString(),
                        b=(mesh.Vertices.Point3dAt(b)/scale).ToString(),c=(mesh.Vertices.Point3dAt(c)/scale).ToString()});
            }
        }
        int components=Enumerable.Range(0,parents.Count).Select(Root).Distinct().Count();
        return new(edges.Values.Count(e=>e.Count==1),edges.Values.Count(e=>e.Count>2),edges.Values.Count(e=>e.Count==2&&e.Direction!=0),degenerate,missing,triangles,degenerateSamples,edges.Where(p=>p.Value.Count==1).Take(16).Select(p=>(object)new {a=points[p.Key.A].ToString(),b=points[p.Key.B].ToString()}).ToArray(),components);
        int Vertex(Point3d p)
        {
            long x=(long)Math.Floor(p.X/WeldToleranceMetres),y=(long)Math.Floor(p.Y/WeldToleranceMetres),z=(long)Math.Floor(p.Z/WeldToleranceMetres);
            int found=-1;double closest=WeldToleranceMetres;
            for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)for(int dz=-1;dz<=1;dz++)
                if(buckets.TryGetValue((x+dx,y+dy,z+dz),out var candidates))foreach(int id in candidates)
                {
                    double distance=points[id].DistanceTo(p);
                    if(distance<=closest){found=id;closest=distance;}
                }
            if(found>=0)return found;
            int index=points.Count;points.Add(p);
            if(!buckets.TryGetValue((x,y,z),out var group))buckets[(x,y,z)]=group=new();
            group.Add(index);return index;
        }
        void Triangle(int a,int b,int c)
        {
            triangles++;
            if(a==b||b==c||c==a){degenerate++;return;}
            int index=parents.Count;parents.Add(index);
            Add(a,b,index);Add(b,c,index);Add(c,a,index);
        }
        int Root(int index)
        {
            while(parents[index]!=index){parents[index]=parents[parents[index]];index=parents[index];}
            return index;
        }
        void Join(int a,int b)
        {
            int x=Root(a),y=Root(b);if(x!=y)parents[y]=x;
        }
        void Add(int a,int b,int triangle)
        {
            var key=a<b?(a,b):(b,a);var old=edges.GetValueOrDefault(key);
            // Edge adjacency, rather than a shared point, matches the native
            // face connectivity and keeps point-touching shells distinct.
            if(old.Count>0)Join(triangle,old.FirstTriangle);
            edges[key]=(old.Count+1,old.Direction+(a<b?1:-1),old.Count>0?old.FirstTriangle:triangle);
        }
    }
}
