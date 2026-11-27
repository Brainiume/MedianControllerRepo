using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace RoadGrass
{
    internal static class RoadGrassCrosswalkGeometry
    {
        internal struct Cut
        {
            internal float2 A, B, C, D, Min, Max;
            internal float Height;
        }
        internal struct Vertex
        {
            internal float3 Position, Weights;
        }

        // Rebuild the surviving outline before meshing so cut edges receive the same curb as any other edge.
        internal static List<RoadGrassPolygonShape.Loop> CutLoops(IReadOnlyList<RoadGrassPolygonShape.Loop> loops,
            IReadOnlyList<RoadGrassTaperGeometry.Lane> lanes)
        {
            var result = new List<RoadGrassPolygonShape.Loop>();
            var cuts = BuildCuts(ContinueAcrossMedian(loops,lanes));
            foreach (var loop in loops)
            {
                if (loop.Points.Count < 3) continue;
                float3 origin = loop.Points[0];
                var active = new List<Cut>();
                float2 min = origin.xz, max = min; float height = 0;
                foreach (var p in loop.Points) { min = math.min(min,p.xz); max = math.max(max,p.xz); height += p.y; }
                height /= loop.Points.Count;
                foreach (var cut in cuts)
                    if (!math.any(max < cut.Min) && !math.any(min > cut.Max) && math.abs(height-cut.Height) <= 1) active.Add(cut);
                if (active.Count == 0) { result.Add(loop); continue; }
                var edges = new List<(float3 A, float3 B, bool Open)>();
                for (int i=0;i<loop.Points.Count;i++)
                    edges.Add((loop.Points[i]-origin,loop.Points[(i+1)%loop.Points.Count]-origin,loop.Opening[i]));
                foreach (var cut in active)
                {
                    var corners = new[] { cut.A,cut.B,cut.C,cut.D };
                    for (int i=0;i<4;i++)
                    {
                        float2 a=corners[i]-origin.xz,b=corners[(i+1)%4]-origin.xz;
                        edges.Add((new float3(a.x,0,a.y),new float3(b.x,0,b.y),false));
                    }
                }
                var localCuts = new List<float2[]>();
                foreach(var cut in active) localCuts.Add(new[]{cut.A-origin.xz,cut.B-origin.xz,cut.C-origin.xz,cut.D-origin.xz});
                bool Kept(float2 p)
                {
                    bool inside=false;
                    for(int i=0,j=loop.Points.Count-1;i<loop.Points.Count;j=i++)
                    {
                        float2 a=loop.Points[i].xz-origin.xz,b=loop.Points[j].xz-origin.xz;
                        if((a.y>p.y)!=(b.y>p.y) && p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x) inside=!inside;
                    }
                    if(!inside) return false;
                    foreach(var c in localCuts)
                    {
                        bool covered=true;
                        for(int i=0;i<4;i++) if(Cross(c[(i+1)%4]-c[i],p-c[i])<0) {covered=false;break;}
                        if(covered) return false;
                    }
                    return true;
                }
                var boundary=new List<(float3 A,float3 B,bool Open)>();
                foreach(var edge in edges)
                {
                    float2 a=edge.A.xz,d=edge.B.xz-a;
                    float length=math.length(d); if(length<.0001f) continue;
                    var splits=new List<float>{0,1};
                    foreach(var other in edges)
                    {
                        float2 b=other.A.xz,e=other.B.xz-b; float determinant=Cross(d,e);
                        if(math.abs(determinant)>1e-7f)
                        {
                            float t=Cross(b-a,e)/determinant,u=Cross(b-a,d)/determinant;
                            if(t>0 && t<1 && u>=-.00001f && u<=1.00001f) splits.Add(t);
                        }
                        else if(math.abs(Cross(d,b-a))/length<.0001f)
                        {
                            foreach(var p in new[]{b,b+e}) {float t=math.dot(p-a,d)/math.lengthsq(d);if(t>0 && t<1) splits.Add(t);}
                        }
                    }
                    splits.Sort();
                    for(int i=1;i<splits.Count;i++)
                    {
                        float from=splits[i-1],to=splits[i];if((to-from)*length<.0001f) continue;
                        float2 middle=a+d*((from+to)*.5f),normal=new float2(-d.y,d.x)/length*.00001f;
                        bool left=Kept(middle+normal),right=Kept(middle-normal);if(left==right) continue;
                        float3 start=math.lerp(edge.A,edge.B,left?from:to),end=math.lerp(edge.A,edge.B,left?to:from);
                        if(!boundary.Exists(x=>math.distancesq(x.A.xz,start.xz)<1e-8f && math.distancesq(x.B.xz,end.xz)<1e-8f))
                            boundary.Add((start,end,edge.Open));
                    }
                }
                var contours = new List<RoadGrassPolygonShape.Loop>();
                while(boundary.Count>0)
                {
                    var seed=boundary[boundary.Count-1];boundary.RemoveAt(boundary.Count-1);
                    var contour=new RoadGrassPolygonShape.Loop();var current=seed;
                    bool closed=false;
                    while(true)
                    {
                        contour.Points.Add(current.A+origin);contour.Opening.Add(current.Open);
                        if(math.distancesq(current.B.xz,seed.A.xz)<1e-7f){closed=true;break;}
                        int next=boundary.FindIndex(x=>math.distancesq(x.A.xz,current.B.xz)<1e-7f);
                        if(next<0) break; current=boundary[next];boundary.RemoveAt(next);
                    }
                    if(!closed) throw new System.InvalidOperationException("Crosswalk curb outline did not close");
                    if(contour.Points.Count>=3) contours.Add(contour);
                }
                // Doubled open bridges preserve enclosed gaps under the filler's even-odd rule.
                var holes=contours.FindAll(x=>Area(x)<0);
                contours.RemoveAll(x=>Area(x)<0);
                holes.Sort((a,b)=>Left(a).x.CompareTo(Left(b).x));
                foreach(var hole in holes)
                {
                    float3 left=Left(hole); int start=hole.Points.IndexOf(left),edge=-1;
                    float nearest=float.NegativeInfinity; RoadGrassPolygonShape.Loop owner=null; float3 hit=default;
                    foreach(var outer in contours)
                    for(int i=0;i<outer.Points.Count;i++)
                    {
                        float3 a=outer.Points[i],b=outer.Points[(i+1)%outer.Points.Count];
                        if((a.z>left.z)==(b.z>left.z)) continue;
                        float3 p=math.lerp(a,b,(left.z-a.z)/(b.z-a.z));
                        if(p.x<=left.x && p.x>nearest) {nearest=p.x;edge=i;owner=outer;hit=p;}
                    }
                    if(owner==null) throw new System.InvalidOperationException("Crosswalk gap has no enclosing median outline");
                    var points=new List<float3>{hit};var opening=new List<bool>{true};
                    for(int i=0;i<hole.Points.Count;i++) {int j=(start+i)%hole.Points.Count;points.Add(hole.Points[j]);opening.Add(hole.Opening[j]);}
                    points.Add(left);opening.Add(true);points.Add(hit);opening.Add(owner.Opening[edge]);
                    owner.Points.InsertRange(edge+1,points);owner.Opening.InsertRange(edge+1,opening);
                }
                result.AddRange(contours);
            }
            return result;
        }

        private static float3 Left(RoadGrassPolygonShape.Loop loop)
        {
            float3 p=loop.Points[0];foreach(var v in loop.Points) if(v.x<p.x) p=v;return p;
        }

        private static float Area(RoadGrassPolygonShape.Loop loop)
        {
            float area=0;float2 origin=loop.Points[0].xz;
            for(int i=0;i<loop.Points.Count;i++) area+=Cross(loop.Points[i].xz-origin,loop.Points[(i+1)%loop.Points.Count].xz-origin);
            return area*.5f;
        }

        private static float Cross(float2 a,float2 b)=>a.x*b.y-a.y*b.x;

        private static List<RoadGrassTaperGeometry.Lane> ContinueAcrossMedian(IReadOnlyList<RoadGrassPolygonShape.Loop> loops,
            IReadOnlyList<RoadGrassTaperGeometry.Lane> lanes)
        {
            var result=new List<RoadGrassTaperGeometry.Lane>(lanes);
            var ends=new List<(int Lane,float3 Position,float3 Outward)>();
            for(int i=0;i<lanes.Count;i++)
            {
                var curve=lanes[i].Curve;
                ends.Add((i,curve.a,Outward(curve.a-curve.b,curve.a-curve.d)));
                ends.Add((i,curve.d,Outward(curve.d-curve.c,curve.d-curve.a)));
            }
            var nearest=new int[ends.Count];var distances=new float[ends.Count];
            for(int i=0;i<ends.Count;i++){nearest[i]=-1;distances[i]=float.MaxValue;}
            for(int i=0;i<ends.Count;i++)
            for(int j=i+1;j<ends.Count;j++)
            {
                var a=ends[i];var b=ends[j];
                if(a.Lane==b.Lane || math.abs(a.Position.y-b.Position.y)>1) continue;
                float2 delta=b.Position.xz-a.Position.xz;float distance=math.length(delta);
                if(distance<.01f) continue;
                float2 direction=delta/distance;
                if(math.dot(a.Outward.xz,direction)<.9f || math.dot(b.Outward.xz,-direction)<.9f) continue;
                bool crosses=false;
                foreach(var loop in loops)
                {
                    if(loop.Points.Count<3) continue;
                    float2 min=loop.Points[0].xz,max=min;float height=0;
                    foreach(var p in loop.Points){min=math.min(min,p.xz);max=math.max(max,p.xz);height+=p.y;}
                    if(distance>math.length(max-min)+math.max(lanes[a.Lane].Width,lanes[b.Lane].Width)*2 ||
                        math.abs(height/loop.Points.Count-(a.Position.y+b.Position.y)*.5f)>1) continue;
                    if(Inside((a.Position.xz+b.Position.xz)*.5f,loop.Points)){crosses=true;break;}
                    for(int edge=0;edge<loop.Points.Count;edge++)
                    {
                        float2 p=loop.Points[edge].xz,q=loop.Points[(edge+1)%loop.Points.Count].xz-p;
                        float determinant=Cross(delta,q);if(math.abs(determinant)<1e-7f) continue;
                        float t=Cross(p-a.Position.xz,q)/determinant,u=Cross(p-a.Position.xz,delta)/determinant;
                        if(t>=0 && t<=1 && u>=0 && u<=1){crosses=true;break;}
                    }
                    if(crosses) break;
                }
                if(!crosses) continue;
                if(distance<distances[i]){nearest[i]=j;distances[i]=distance;}
                if(distance<distances[j]){nearest[j]=i;distances[j]=distance;}
            }
            // Native crosswalks stop at the original island. Join only mutually nearest, facing ends across this polygon.
            for(int i=0;i<ends.Count;i++)
            {
                int j=nearest[i];if(j<=i || nearest[j]!=i) continue;
                var a=ends[i];var b=ends[j];float handle=distances[i]/3;
                result.Add(new RoadGrassTaperGeometry.Lane {Curve=new Bezier4x3(a.Position,a.Position+a.Outward*handle,
                    b.Position+b.Outward*handle,b.Position),Width=math.min(lanes[a.Lane].Width,lanes[b.Lane].Width)});
            }
            return result;

            float3 Outward(float3 tangent,float3 fallback)
            {
                if(math.lengthsq(tangent.xz)<1e-8f) tangent=fallback;
                return tangent/math.max(.00001f,math.length(tangent.xz));
            }
        }

        private static bool Inside(float2 p,IReadOnlyList<float3> points)
        {
            bool inside=false;
            for(int i=0,j=points.Count-1;i<points.Count;j=i++)
            {
                float2 a=points[i].xz,b=points[j].xz;
                if((a.y>p.y)!=(b.y>p.y) && p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x) inside=!inside;
            }
            return inside;
        }

        internal static List<Cut> BuildCuts(IReadOnlyList<RoadGrassTaperGeometry.Lane> lanes)
        {
            var cuts = new List<Cut>();
            foreach (var lane in lanes)
            {
                float2 chord = math.normalizesafe(lane.Curve.d.xz-lane.Curve.a.xz);
                float2 across = new float2(-chord.y,chord.x);
                bool straight = math.lengthsq(chord) > 0 && math.abs(math.dot(lane.Curve.b.xz-lane.Curve.a.xz,across)) < .01f &&
                    math.abs(math.dot(lane.Curve.c.xz-lane.Curve.a.xz,across)) < .01f && math.abs(lane.Curve.d.y-lane.Curve.a.y) < .5f;
                int samples = straight ? 1 : math.clamp((int)math.ceil(MathUtils.Length(lane.Curve) / .5f), 1, 128);
                float radius = math.max(.25f, lane.Width * .5f) + .15f;
                for (int i = 0; i < samples; i++)
                {
                    float3 start = MathUtils.Position(lane.Curve, i / (float)samples);
                    float3 end = MathUtils.Position(lane.Curve, (i + 1f) / samples);
                    float2 direction = math.normalizesafe(end.xz - start.xz);
                    if (math.lengthsq(direction) == 0) continue;
                    float2 normal = new float2(-direction.y, direction.x) * radius;
                    float2 a = start.xz - direction * radius - normal, b = end.xz + direction * radius - normal;
                    float2 c = b + 2 * normal, d = a + 2 * normal;
                    cuts.Add(new Cut { A = a, B = b, C = c, D = d,
                        Min = math.min(math.min(a,b),math.min(c,d)), Max = math.max(math.max(a,b),math.max(c,d)), Height = (start.y + end.y) * .5f });
                }
            }
            return cuts;
        }
        // Barycentric weights keep UVs, normals and material ownership unchanged at every new cut vertex.
        internal static List<List<Vertex>> ClipTriangle(float3 a, float3 b, float3 c, IReadOnlyList<Cut> cuts)
        {
            var polygons = new List<List<Vertex>> { new List<Vertex> {
                new Vertex { Position = a, Weights = new float3(1,0,0) },
                new Vertex { Position = b, Weights = new float3(0,1,0) },
                new Vertex { Position = c, Weights = new float3(0,0,1) } } };
            float2 min = math.min(a.xz,math.min(b.xz,c.xz)), max = math.max(a.xz,math.max(b.xz,c.xz));
            foreach (var cut in cuts)
            {
                if (math.any(max < cut.Min) || math.any(min > cut.Max) || math.abs((a.y+b.y+c.y)/3 - cut.Height) > 1) continue;
                var outside = new List<List<Vertex>>();
                foreach (var polygon in polygons)
                {
                    var remainder = polygon;
                    var corners = new[] { cut.A, cut.B, cut.C, cut.D };
                    for (int edge = 0; edge < 4 && remainder.Count >= 3; edge++)
                    {
                        var rejected = Clip(remainder,corners[edge],corners[(edge+1)%4],false);
                        if (rejected.Count >= 3) outside.Add(rejected);
                        remainder = Clip(remainder,corners[edge],corners[(edge+1)%4],true);
                    }
                }
                polygons = outside;
                if (polygons.Count == 0) break;
            }
            return polygons;
        }
        private static List<Vertex> Clip(List<Vertex> input, float2 start, float2 end, bool inside)
        {
            var result = new List<Vertex>();
            float Distance(Vertex v)
            {
                float2 d = end - start, p = v.Position.xz - start;
                return (d.x*p.y-d.y*p.x) * (inside ? 1 : -1);
            }
            var previous = input[input.Count-1]; float previousDistance = Distance(previous);
            foreach (var current in input)
            {
                float distance = Distance(current);
                if ((distance >= 0) != (previousDistance >= 0))
                {
                    float t = previousDistance / (previousDistance - distance);
                    result.Add(new Vertex { Position = math.lerp(previous.Position,current.Position,t), Weights = math.lerp(previous.Weights,current.Weights,t) });
                }
                if (distance >= 0) result.Add(current);
                previous = current; previousDistance = distance;
            }
            return result;
        }
    }
}
