using System.Diagnostics;
using System.Text.Json;
using Clipper2Lib;
using Rhino.Geometry;

namespace Aha.Topo.Checks;

/// <summary>Independent planar reduction and local-boundary error regressions.</summary>
public static class MeshEfficiencyChecks
{
    public static string Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var result=Check();
        var path=Path.Combine(directory,"mesh-efficiency-checks.json");
        File.WriteAllText(path,JsonSerializer.Serialize(new{passed=result["geometry_checks_passed"],cases=result["mesh_efficiency_cases"]},new JsonSerializerOptions{WriteIndented=true}));
        return path;
    }

    internal static Dictionary<string,object?> Check()
    {
        var cases=new List<Dictionary<string,object?>>();
        foreach(double scale in new[]{1d,1000d})
        {
            using var a=new Mesh();a.Vertices.UseDoublePrecisionVertices=true;
            a.Vertices.Add(181.49*scale,-155.99*scale,20*scale);
            a.Vertices.Add(181.490001*scale,-155.99*scale,20*scale);
            a.Vertices.Add(182*scale,-154*scale,20*scale);a.Faces.AddFace(0,1,2);
            using var b=new Mesh();b.Vertices.UseDoublePrecisionVertices=true;
            b.Vertices.Add(a.Vertices.Point3dAt(0));b.Vertices.Add(a.Vertices.Point3dAt(1));
            b.Vertices.Add(181*scale,-157*scale,20*scale);b.Faces.AddFace(1,0,2);
            var builder=new TerrainSurface.DisplayMeshBuilder();using var joined=builder.Mesh;
            builder.Append(a);builder.Append(b);
            cases.Add(new(){["case"]="exact-double-merge",["scale"]=scale,
                ["passed"]=joined.IsValid&&joined.Vertices.Count==4&&joined.Faces.Count==2&&
                    joined.Faces.All(f=>f.A!=f.B&&f.B!=f.C&&f.C!=f.A)});
        }
        foreach(string kind in new[]{"flat","tilted","saddle","two-planes","hole","point-touch"})
        {
            using var source=Grid(kind);uint crc=source.DataCRC(0);var timer=Stopwatch.StartNew();
            var reduced=CoplanarMeshReduction.Reduce(source);timer.Stop();
            try
            {
                double maxDistance=0;
                foreach(var p in source.Vertices.ToPoint3dArray())
                {maxDistance=Math.Max(maxDistance,VerticalDistance(reduced,p));}
                bool boundary=Boundary(source).SetEquals(Boundary(reduced));
                double areaError=Math.Abs(Area(source)-Area(reduced));
                bool reductionExpected=kind is "flat" or "tilted" or "hole" or "point-touch";
                bool passed=source.DataCRC(0)==crc&&reduced.IsValid&&boundary&&maxDistance<=2e-9&&areaError<=1e-8
                    &&(reductionExpected?reduced.Faces.Count<source.Faces.Count/2:true);
                cases.Add(new(){["case"]=kind,["passed"]=passed,["before_triangles"]=source.Faces.Count,["after_triangles"]=reduced.Faces.Count,
                    ["boundary_edges_unchanged"]=boundary,["source_unchanged"]=source.DataCRC(0)==crc,["max_source_height_error_m"]=maxDistance,
                    ["plan_area_error_m2"]=areaError,["seconds"]=timer.Elapsed.TotalSeconds});
            }
            finally{if(!ReferenceEquals(source,reduced))reduced.Dispose();}
        }
        foreach(double scale in new[]{1d,1000d})cases.Add(LocalBoundary(scale));
        return new(){["geometry_checks_passed"]=cases.All(c=>(bool)c["passed"]!),["mesh_efficiency_cases"]=cases};
    }

    private static Dictionary<string,object?> LocalBoundary(double scale)
    {
        var projection=new LocalProjection(new GeoPoint(127,37.5));var bounds=projection.Bounds(40);
        var flat=Enumerable.Repeat(20d,9*9).ToArray();var distant=(double[])flat.Clone();distant[0]=520;
        var target=new GeoFeature{Id="target",Pnu="9900000000100010000",Polygons=new(){new(){new(){projection.ToGeo(-2,-2),projection.ToGeo(2,-2),projection.ToGeo(2,2),projection.ToGeo(-2,2),projection.ToGeo(-2,-2)}}}};
        var near=EditRegion.Create(SiteSelection.Create(new[]{target},projection),projection,bounds,2);
        var roads=RoadGeometry.Build(Array.Empty<GeoFeature>(),projection,bounds);
        TerrainSurface? a=null,b=null;
        try
        {
            a=TerrainSurface.Build(TerrainField.FromSamples(bounds,projection,8,8,flat),roads,0,scale,.001,false,true,near,30);
            var field=TerrainField.FromSamples(bounds,projection,8,8,distant);
            b=TerrainSurface.Build(field,roads,0,scale,.001,false,true,near,30);
            var asolid=(Brep)a.Pieces.Single(p=>p.Zone=="Near"&&p.Role=="Land").Geometry;
            var bsolid=(Brep)b.Pieces.Single(p=>p.Zone=="Near"&&p.Role=="Land").Geometry;
            // This fixture's Near top is horizontal. A vertical wall can have
            // a roundoff-sized positive Z normal; it is not a top face.
            bool IsTop(BrepFace face) => face.NormalAt(face.Domain(0).Mid,face.Domain(1).Mid).Z > .999999;
            HashSet<string> TopBoundary(Brep solid)
            {using var mesh=new Mesh();foreach(var f in solid.Faces)if(IsTop(f))mesh.Append(f.GetMesh(MeshType.Render));return Boundary(mesh);}
            bool same=TopBoundary(asolid).SetEquals(TopBoundary(bsolid));
            var check=NearGeometryChecks.Check(b.Pieces,near,field,0,scale,true,30,b.BottomElevationMetres);
            var display=SolidDisplayChecks.Check(bsolid,scale);
            var top=bsolid.Faces.Where(IsTop).Sum(f=>f.GetMesh(MeshType.Render).Faces.Count);
            var bottom=bsolid.Faces.Where(f=>f.NormalAt(f.Domain(0).Mid,f.Domain(1).Mid).Z<-.999999).Sum(f=>f.GetMesh(MeshType.Render).Faces.Count);
            return new(){["case"]="distant-steep-cell-local-boundary",["scale"]=scale,["passed"]=same&&display.Passed&&(bool)check["near_geometry_checks_passed"]!,
                ["far_steep_cell_does_not_densify_near_boundary"]=same,["near_geometry"]=check,["closed_display"]=display,["near_top_triangles"]=top,["near_bottom_triangles"]=bottom};
        }
        finally{if(a!=null)foreach(var p in a.Pieces)p.Geometry.Dispose();if(b!=null)foreach(var p in b.Pieces)p.Geometry.Dispose();}
    }
    private static Mesh Grid(string kind)
    {
        var mesh=new Mesh();mesh.Vertices.UseDoublePrecisionVertices=true;
        var points=new Dictionary<(int,int),int>();
        int Vertex(int x,int y)
        {if(points.TryGetValue((x,y),out int id))return id;double z=kind switch{"tilted"=>20+.1*x-.2*y,"saddle"=>20+.005*x*y,"two-planes"=>20+.05*Math.Abs(x-16),_=>20};id=mesh.Vertices.Add(x,y,z);points.Add((x,y),id);return id;}
        for(int y=0;y<32;y++)for(int x=0;x<32;x++)
        {
            if(kind=="hole"&&x>=12&&x<20&&y>=12&&y<20)continue;
            if(kind=="point-touch"&&((x<16)!=(y<16)))continue;
            int a=Vertex(x,y),b=Vertex(x+1,y),c=Vertex(x+1,y+1),d=Vertex(x,y+1);mesh.Faces.AddFace(a,b,c);mesh.Faces.AddFace(a,c,d);
        }
        mesh.Normals.ComputeNormals();return mesh;
    }
    private static double VerticalDistance(Mesh mesh,Point3d point)
    {
        // Rhino's closest-mesh query can use its single-precision accelerator.
        // Evaluate the actual double-precision triangle plane at source XY.
        double best=double.PositiveInfinity;
        foreach(var face in mesh.Faces)
        {
            var a=mesh.Vertices.Point3dAt(face.A);var b=mesh.Vertices.Point3dAt(face.B);var c=mesh.Vertices.Point3dAt(face.C);
            double dx=point.X-a.X,dy=point.Y-a.Y,bx=b.X-a.X,by=b.Y-a.Y,cx=c.X-a.X,cy=c.Y-a.Y;
            double den=bx*cy-by*cx,u=(dx*cy-dy*cx)/den,v=(bx*dy-by*dx)/den;
            if(u < -1e-12||v < -1e-12||u+v > 1+1e-12)continue;
            best=Math.Min(best,Math.Abs(point.Z-(a.Z+u*(b.Z-a.Z)+v*(c.Z-a.Z))));
        }
        return best;
    }
    private static double Area(Mesh mesh)
    {double area=0;foreach(var f in mesh.Faces){var a=mesh.Vertices.Point3dAt(f.A);var b=mesh.Vertices.Point3dAt(f.B);var c=mesh.Vertices.Point3dAt(f.C);area+=((b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X))/2;}return area;}
    private static HashSet<string> Boundary(Mesh mesh)
    {
        var edges=new Dictionary<string,int>();var points=mesh.Vertices.ToPoint3dArray();
        string Key(Point3d p)=>$"{p.X:R},{p.Y:R},{p.Z:R}";
        foreach(var f in mesh.Faces){Add(f.A,f.B);Add(f.B,f.C);if(f.IsQuad){Add(f.C,f.D);Add(f.D,f.A);}else Add(f.C,f.A);}
        return edges.Where(p=>p.Value!=0).Select(p=>p.Key+":"+p.Value).ToHashSet();
        void Add(int a,int b){string x=Key(points[a]),y=Key(points[b]);bool forward=string.CompareOrdinal(x,y)<0;string key=forward?x+"|"+y:y+"|"+x;edges[key]=edges.GetValueOrDefault(key)+(forward?1:-1);}
    }
}
