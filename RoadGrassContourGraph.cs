using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;
namespace RoadGrass
{
    // Same sampled-guide graph and bounded merge rules as the contour lab.
    internal sealed class RoadGrassContourGraph
    {
        internal sealed class Vertex
        {
            internal float3 Position;
            internal readonly Dictionary<int,float> Refs = new();
            internal readonly List<Vertex> Members = new();
        }
        internal readonly List<Vertex> Vertices = new();
        internal readonly List<float3> Points = new();
        private readonly List<RoadGrassTaperGeometry.Lane> m_Guides;
        private readonly List<float3[]> m_Samples = new();
        internal bool Ready;
        private bool m_PublishEndpoints = true;
        private readonly Dictionary<int2,List<Vertex>> m_VertexCells = new();
        internal readonly List<float3> ExtraPoints = new();
        internal RoadGrassContourGraph(List<RoadGrassTaperGeometry.Lane> guides) { m_Guides = guides; }
        internal static List<float3> MergeDisplayPoints(IReadOnlyList<float3> points, System.Func<float3,bool> connected)
        {
            var groups=new List<List<float3>>();var representatives=new List<float3>();var follows=new List<bool>();
            var cells=new Dictionary<int2,List<int>>();
            foreach(var p in points)
            {
                int2 cell=(int2)math.floor(p.xz/.35f);int best=int.MaxValue;
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)
                    if(cells.TryGetValue(cell+new int2(x,y),out var nearby))foreach(int i in nearby)
                        if(i<best&&groups[i].TrueForAll(q=>math.distance(p.xz,q.xz)<=.35f&&math.abs(p.y-q.y)<=.15f))best=i;
                bool guided=connected==null||connected(p);
                if(best==int.MaxValue)
                {
                    if(!cells.TryGetValue(cell,out var bucket))cells[cell]=bucket=new List<int>();bucket.Add(groups.Count);
                    groups.Add(new List<float3>{p});representatives.Add(p);follows.Add(guided);
                }
                else
                {
                    groups[best].Add(p);
                    // Keep the point on the active contour, never an average between contours.
                    if(guided&&!follows[best]){representatives[best]=p;follows[best]=true;}
                }
            }
            return representatives;
        }
        private float3 At(int g,float t)
        {
            float f=math.clamp(t*64,0,64);int i=math.min(63,(int)f);
            return math.lerp(m_Samples[g][i],m_Samples[g][i+1],f-i);
        }
        private void Add(float3 p,int g,float t)
        {
            int2 cell=(int2)math.floor(p.xz/.035f);Vertex v=null;
            for(int x=-1;x<=1&&v==null;x++)for(int y=-1;y<=1&&v==null;y++)
                if(m_VertexCells.TryGetValue(cell+new int2(x,y),out var nearby))
                    v=nearby.Find(n=>math.distancesq(n.Position.xz,p.xz)<.035f*.035f&&math.abs(n.Position.y-p.y)<.15f);
            if(v==null)
            {
                v=new Vertex{Position=p};Vertices.Add(v);if(m_PublishEndpoints)Points.Add(p);
                if(!m_VertexCells.TryGetValue(cell,out var bucket))m_VertexCells[cell]=bucket=new List<Vertex>();bucket.Add(v);
            }
            if(!v.Refs.ContainsKey(g))v.Refs[g]=t;
        }
        private bool EndpointJoin(Vertex a,Vertex b)
        {
            float d=math.distance(a.Position.xz,b.Position.xz);
            if(d<.035f||d>.4f||math.abs(a.Position.y-b.Position.y)>.15f)return false;
            foreach(var r in a.Refs)foreach(var s in b.Refs)
            {
                if(r.Key==s.Key||!m_Guides[r.Key].Marking||!m_Guides[s.Key].Marking||
                    (r.Value!=0&&r.Value!=1)||(s.Value!=0&&s.Value!=1))continue;
                float2 gap=(b.Position-a.Position).xz/d;
                float2 u=math.normalizesafe((At(r.Key,r.Value==0?1f/64:63f/64)-a.Position).xz);
                float2 v=math.normalizesafe((At(s.Key,s.Value==0?1f/64:63f/64)-b.Position).xz);
                if(math.abs(math.dot(u,gap))>.95f&&math.abs(math.dot(v,gap))>.95f)return true;
            }
            return false;
        }
        private bool Fits(List<Vertex> a,List<Vertex> b)
        {
            bool connected=false;
            foreach(var m in a)foreach(var n in b)
            {
                if(math.distance(m.Position.xz,n.Position.xz)>.4f)return false;
                bool endpoint=EndpointJoin(m,n);
                if(math.abs(m.Position.y-n.Position.y)>.15f||(math.distance(m.Position.xz,n.Position.xz)>.35f&&!endpoint))return false;
                if(endpoint)connected=true;
                foreach(var r in m.Refs)if(n.Refs.ContainsKey(r.Key))connected=true;
            }
            return connected;
        }
        internal IEnumerator<bool> Build(float3 centre)
        {
            var lines=new List<Line3.Segment>();var ids=new List<int>();var steps=new List<int>();
            for(int g=0;g<m_Guides.Count;g++)
            {
                var samples=new float3[65];for(int i=0;i<=64;i++)samples[i]=MathUtils.Position(m_Guides[g].Curve,i/64f);
                m_Samples.Add(samples);
                for(int i=0;i<64;i++)
                {
                    bool insideA=math.distance(samples[i].xz,centre.xz)<=65,insideB=math.distance(samples[i+1].xz,centre.xz)<=65;
                    if(!insideA&&!insideB)continue;
                    if(i==0||math.distance(samples[i-1].xz,centre.xz)>65)Add(samples[i],g,i/64f);
                    if(i==63||math.distance(samples[math.min(i+2,64)].xz,centre.xz)>65)Add(samples[i+1],g,(i+1)/64f);
                    lines.Add(new Line3.Segment(samples[i],samples[i+1]));ids.Add(g);steps.Add(i);
                }
                yield return true;
            }
            m_PublishEndpoints=false;
            int work=0;
            foreach(var pair in RoadGrassSnapCandidates.Pairs(lines, 4))
            {
                if((++work&63)==0)yield return true;
                int i=pair.x,j=pair.y,g=ids[i],h=ids[j];
                if(g==h)continue;
                var family=m_Guides[g].Family;
                if(!string.IsNullOrEmpty(family)&&family==m_Guides[h].Family)continue;
                var a=lines[i];var b=lines[j];
                if(math.any(math.max(a.a.xz,a.b.xz)<math.min(b.a.xz,b.b.xz))||math.any(math.max(b.a.xz,b.b.xz)<math.min(a.a.xz,a.b.xz)))continue;
                float2 u=(a.b-a.a).xz,v=(b.b-b.a).xz,w=(b.a-a.a).xz;
                float det=u.x*v.y-u.y*v.x;if(math.abs(det)<1e-8f)continue;
                float t=(w.x*v.y-w.y*v.x)/det,s=(w.x*u.y-w.y*u.x)/det;
                if(t<0||t>1||s<0||s>1)continue;
                float3 p=math.lerp(a.a,a.b,t),q=math.lerp(b.a,b.b,s);
                if(math.abs(p.y-q.y)>.5f)continue;
                Add(p,g,(steps[i]+t)/64f);Add(p,h,(steps[j]+s)/64f);
            }
            var clusters=new List<List<Vertex>>();var initialCells=new Dictionary<int2,List<int>>();
            foreach(var n in Vertices)
            {
                var one=new List<Vertex>{n};int2 cell=(int2)math.floor(n.Position.xz/.4f);int best=int.MaxValue;
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)
                    if(initialCells.TryGetValue(cell+new int2(x,y),out var nearby))
                        foreach(int index in nearby)if(index<best&&Fits(clusters[index],one))best=index;
                if(best==int.MaxValue)
                {
                    if(!initialCells.TryGetValue(cell,out var bucket))initialCells[cell]=bucket=new List<int>();bucket.Add(clusters.Count);clusters.Add(one);
                }
                else clusters[best].Add(n);
                yield return true;
            }
            var cells=new Dictionary<int2,List<int>>();var active=new HashSet<int>();
            for(int i=0;i<clusters.Count;i++)
            {
                int2 key=(int2)math.floor(clusters[i][0].Position.xz/.4f);
                if(!cells.TryGetValue(key,out var bucket))cells[key]=bucket=new List<int>();
                bucket.Add(i);active.Add(i);
            }
            for(int i=0;i<clusters.Count;i++)
            {
                if(!active.Contains(i))continue;
                int2 key=(int2)math.floor(clusters[i][0].Position.xz/.4f);var nearby=new List<int>();
                for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)
                    if(cells.TryGetValue(key+new int2(x,y),out var bucket))foreach(int j in bucket)if(j>i&&active.Contains(j))nearby.Add(j);
                nearby.Sort();
                foreach(int j in nearby)
                {
                    if((++work&63)==0)yield return true;
                    if(!Fits(clusters[i],clusters[j]))continue;
                    clusters[i].AddRange(clusters[j]);active.Remove(j);i=-1;break;
                }
            }
            var remaining=new List<List<Vertex>>();for(int i=0;i<clusters.Count;i++)if(active.Contains(i))remaining.Add(clusters[i]);
            clusters=remaining;
            Vertices.Clear();Points.Clear();
            foreach(var cluster in clusters)
            {
                Vertex representative=cluster[0];foreach(var n in cluster)if(n.Refs.Count>representative.Refs.Count)representative=n;
                var merged=new Vertex{Position=representative.Position};merged.Members.AddRange(cluster);
                foreach(var n in cluster)foreach(var r in n.Refs)
                    if(!merged.Refs.TryGetValue(r.Key,out float old)||math.distance(At(r.Key,r.Value),merged.Position)<math.distance(At(r.Key,old),merged.Position))merged.Refs[r.Key]=r.Value;
                Vertices.Add(merged);Points.Add(merged.Position);
            }
            foreach (var point in ExtraPoints) if (math.distance(point.xz,centre.xz)<=65 && !Points.Exists(p=>math.distance(p,point)<.035f))
            { var v=new Vertex{Position=point};Vertices.Add(v);Points.Add(point); }
            m_VertexCells.Clear();Ready=true;
        }
        internal List<float3> Edge(float3 start,float3 end)
        {
            if(!Ready)return null;
            var a=Vertices.Find(n=>math.distance(n.Position,start)<.035f||n.Members.Exists(m=>math.distance(m.Position,start)<.035f));var b=Vertices.Find(n=>math.distance(n.Position,end)<.035f||n.Members.Exists(m=>math.distance(m.Position,end)<.035f));
            if(a==null||b==null||a==b)return null;
            foreach(var r in a.Refs)
            {
                if(!b.Refs.TryGetValue(r.Key,out float to))continue;
                // Crossings remain selectable, but do not interrupt a continuous contour.
                int count=math.max(2,(int)math.ceil(math.abs(to-r.Value)*64));var path=new List<float3>{start};
                for(int i=1;i<count;i++)path.Add(At(r.Key,math.lerp(r.Value,to,i/(float)count)));
                path.Add(end);
                // Preserve the sampled curve within 1.5cm while respecting the existing polygon point budget.
                for(int i=1;i<path.Count-1;)
                {
                    float3 d=path[i+1]-path[i-1];float t=math.saturate(math.dot(path[i]-path[i-1],d)/math.max(.0001f,math.lengthsq(d)));
                    if(math.distance(path[i],path[i-1]+d*t)<.015f)path.RemoveAt(i);else i++;
                }
                return path;
            }
            return null;
        }
    }
}
