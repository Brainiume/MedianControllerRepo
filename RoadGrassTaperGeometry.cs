using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Mathematics;

namespace RoadGrass
{
    // Read-only geometry, shared by previews and committed taper instances.
    internal static class RoadGrassTaperGeometry
    {
        internal const float Inset = 0.12f;
        private const float Error = 0.015f;
        internal struct Lane { internal Bezier4x3 Curve; internal float Width; }
        private struct Block { internal float2 A, B; internal float Radius; }

        internal static bool TryBuildManual(Segment approach, float3 tip, float radius,
            out EdgeGeometry geometry, out Bezier4x3 middle)
        {
            geometry = default;
            middle = default;
            float3 start = (approach.m_Left.d + approach.m_Right.d) * 0.5f;
            float3 direction = math.normalizesafe(new float3(tip.x - start.x, 0, tip.z - start.z));
            float3 forward = math.normalizesafe(new float3(
                approach.m_Left.d.x - approach.m_Left.c.x + approach.m_Right.d.x - approach.m_Right.c.x, 0,
                approach.m_Left.d.z - approach.m_Left.c.z + approach.m_Right.d.z - approach.m_Right.c.z));
            float distance = math.distance(start.xz, tip.xz);
            if (!math.all(math.isfinite(tip)) || !math.isfinite(distance) ||
                distance < math.max(0.75f, radius * 3) || distance > 100 || math.dot(direction, forward) < 0.15f) return false;
            float3 across = new float3(-direction.z, 0, direction.x);
            if (math.dot(across, approach.m_Right.d - approach.m_Left.d) < 0) across = -across;
            float handle = math.min(distance * 0.4f, distance - radius * 2);
            float3 rise = new float3(0,(tip.y - start.y) / 3,0);
            // Opposed tangents meet smoothly at the nose, rather than forming a cusp.
            float3 leftHandle=approach.m_Left.d+forward*handle+rise, rightHandle=approach.m_Right.d+forward*handle+rise;
            float halfWidth=math.distance(approach.m_Left.d.xz,approach.m_Right.d.xz)*0.5f;
            // A quarter-circle handle is wrong for a full-length cubic. Derive
            // the endpoint tangent from its curvature radius instead.
            float leftNose=math.min(halfWidth,math.sqrt(2f/3f*radius*math.max(radius,math.dot(tip-leftHandle,direction))));
            float rightNose=math.min(halfWidth,math.sqrt(2f/3f*radius*math.max(radius,math.dot(tip-rightHandle,direction))));
            var left = new Bezier4x3(approach.m_Left.d,leftHandle,tip-across*leftNose,tip);
            var right = new Bezier4x3(approach.m_Right.d,rightHandle,tip+across*rightNose,tip);
            middle = new Bezier4x3(start, start + forward * handle + rise,
                tip - direction * (radius + distance * 0.2f), tip - direction * radius);
            // Structural guards only. Manual placement does not shorten the user's
            // chosen destination or veto it against automatically inferred lane gaps.
            for (int i = 0; i < 32; i++)
            {
                float t = i / 32f;
                if (math.dot(MathUtils.Position(right,t)-MathUtils.Position(left,t),across) < -0.01f) return false;
            }
            MathUtils.Divide(left,out var sl,out var el,0.5f);
            MathUtils.Divide(right,out var sr,out var er,0.5f);
            var bounds = MathUtils.TightBounds(left) | MathUtils.TightBounds(right) | MathUtils.TightBounds(middle);
            bounds.min.y -= 0.5f; bounds.max.y += 0.5f;
            geometry = new EdgeGeometry {
                m_Start = new Segment { m_Left=sl,m_Right=sr,m_Length=new float2(MathUtils.Length(sl),MathUtils.Length(sr)) },
                m_End = new Segment { m_Left=el,m_Right=er,m_Length=new float2(MathUtils.Length(el),MathUtils.Length(er)) },
                m_Bounds=bounds
            };
            return true;
        }

        internal static bool TryBuild(Segment approach, EdgeNodeGeometry node, IReadOnlyList<Lane> lanes,
            out EdgeGeometry geometry, out string reason)
        {
            geometry = default;
            reason = "No safe lane gap";
            float3 origin = (approach.m_Left.d + approach.m_Right.d) * 0.5f;
            float3 delta = approach.m_Right.d - approach.m_Left.d;
            float width = math.length(delta.xz);
            if (!math.isfinite(width) || width < 0.5f || lanes.Count == 0) return false;
            float2 across = delta.xz / width;
            float2 forward = new float2(-across.y, across.x);
            float2 tangent = (approach.m_Left.d - approach.m_Left.c + approach.m_Right.d - approach.m_Right.c).xz;
            if (math.dot(forward, tangent) < 0) forward = -forward;
            if (!math.all(math.isfinite(origin)) || math.dot(forward, tangent) < 0.001f) return false;
            float2 Local(float3 p) => new float2(math.dot(p.xz - origin.xz, across), math.dot(p.xz - origin.xz, forward));
            // Retain the native longitudinal height profile and the source crossfall.
            if (!Flatten(node.m_Middle, new List<float3>())) return false;
            float Height(float distance)
            {
                float3 previous = node.m_Middle.a;
                for (int i = 1; i <= 64; i++)
                {
                    float3 next = MathUtils.Position(node.m_Middle, i / 64f);
                    float a = math.dot(previous.xz - node.m_Middle.a.xz, forward);
                    float b = math.dot(next.xz - node.m_Middle.a.xz, forward);
                    if (b >= distance && b > a)
                        return math.lerp(previous.y, next.y, math.saturate((distance - a) / (b - a))) - node.m_Middle.a.y;
                    previous = next;
                }
                return node.m_Middle.d.y - node.m_Middle.a.y;
            }
            float3 World(float2 p) => new float3(origin.x + across.x * p.x + forward.x * p.y,
                origin.y + delta.y * p.x / width + Height(p.y), origin.z + across.y * p.x + forward.y * p.y);

            var blocks = new List<Block>();
            var points = new List<float3>();
            foreach (var lane in lanes)
            {
                if (!math.isfinite(lane.Width) || lane.Width <= 0 || !Flatten(lane.Curve, points)) return false;
                for (int i = 1; i < points.Count; i++)
                    blocks.Add(new Block { A = Local(points[i - 1]), B = Local(points[i]), Radius = lane.Width * 0.5f + Error });
            }
            // Preserve the source section's existing lane setback. Its continuation
            // pieces also retain their native curb/pavement shoulder inside this outline.
            float clearance = float.MaxValue;
            foreach (var b in blocks)
                clearance = math.min(clearance, math.min(PointDistance(Local(approach.m_Left.d), b.A, b.B),
                    PointDistance(Local(approach.m_Right.d), b.A, b.B)) - b.Radius + Error);
            float padding = math.max(Inset, clearance);
            float sourcePadding = math.max(0, clearance);
            // Native node fan perimeter. The two inner fan ends and middle end
            // connect the outer boundaries across the junction, not world axes.
            var pavement = new List<float2>();
            if (!Flatten(node.m_Left.m_Left, points)) return false;
            foreach (var p in points) pavement.Add(Local(p));
            pavement.Add(Local(node.m_Left.m_Right.d));
            pavement.Add(Local(node.m_Middle.d));
            pavement.Add(Local(node.m_Right.m_Left.d));
            if (!Flatten(node.m_Right.m_Right, points)) return false;
            for (int i = points.Count - 1; i >= 0; i--) pavement.Add(Local(points[i]));
            float maxDistance = 0;
            foreach (var p in pavement) maxDistance = math.max(maxDistance, p.y);
            maxDistance = math.min(maxDistance, 50f);
            if (!math.isfinite(maxDistance) || maxDistance < 1f) return false;

            // Intersect lane envelopes with transverse slices of this approach.
            // Short capsule AABBs deliberately overestimate occupied lane space.
            var sections = new List<float3>();
            sections.Add(new float3(-width * 0.5f, width * 0.5f, 0));
            float center = 0, previousWidth = width;
            for (float s = 0.25f; s <= maxDistance; s += 0.25f)
            {
                var intervals = new List<float2>();
                float inset = math.lerp(sourcePadding, padding, math.saturate(s / 2f));
                foreach (var b in blocks)
                {
                    float r = b.Radius + inset;
                    if (s < math.min(b.A.y, b.B.y) - r || s > math.max(b.A.y, b.B.y) + r) continue;
                    intervals.Add(new float2(math.min(b.A.x, b.B.x) - r, math.max(b.A.x, b.B.x) + r));
                }
                intervals.Sort((a,b) => a.x.CompareTo(b.x));
                float lo = -width, hi = width;
                foreach (var interval in intervals)
                {
                    if (interval.y <= center) lo = math.max(lo, interval.y);
                    else if (interval.x >= center) hi = math.min(hi, interval.x);
                    else { lo = hi; break; }
                }
                if (hi - lo < 0.08f) break;
                // Stay contiguous with the source median and never widen.
                float half = math.min(previousWidth, hi - lo) * 0.5f;
                center = math.clamp(center, lo + half, hi - half);
                lo = center - half; hi = center + half;
                if (!Inside(new float2(lo,s), pavement) || !Inside(new float2(hi,s), pavement)) break;
                sections.Add(new float3(lo,hi,s));
                previousWidth = hi - lo;
            }
            reason = "Lane corridor ends after " + (sections.Count - 1) * 0.25f + "m";
            // Try the longest proven gap first. A rejected fit never mutates the
            // source road; shorter fits can avoid a turning lane's curved envelope.
            for (int end = sections.Count - 1; end >= 4; end--)
            {
                float3 last = sections[end];
                if (last.z < 1f) break;
                float2 tip = new float2((last.x + last.y) * 0.5f, last.z);
                Bezier4x3 left = Fit(approach.m_Left, tip, sections, end, false, World, Local);
                Bezier4x3 right = Fit(approach.m_Right, tip, sections, end, true, World, Local);
                float previousSpan = width + 0.025f, previousLeft = -0.025f, previousRight = -0.025f;
                bool monotone = true;
                for (int i = 0; i <= 64; i++)
                {
                    float2 l = Local(MathUtils.Position(left, i / 64f)), r = Local(MathUtils.Position(right, i / 64f));
                    float span = r.x - l.x;
                    if (span < -0.001f || span > previousSpan + 0.001f || l.y < previousLeft - 0.001f || r.y < previousRight - 0.001f)
                    { monotone = false; break; }
                    previousSpan = span; previousLeft = l.y; previousRight = r.y;
                }
                if (!monotone) { reason = "Taper widens or reverses"; continue; }
                var polygon = new List<float2>();
                if (!Flatten(left, points)) continue;
                foreach (var p in points) polygon.Add(Local(p));
                if (!Flatten(right, points)) continue;
                for (int i = points.Count - 1; i >= 0; i--) polygon.Add(Local(points[i]));
                if (!Safe(polygon, pavement, blocks, sourcePadding, padding, out reason)) continue;
                MathUtils.Divide(left, out var sl, out var el, 0.5f);
                MathUtils.Divide(right, out var sr, out var er, 0.5f);
                var bounds = MathUtils.TightBounds(left) | MathUtils.TightBounds(right);
                bounds.min.y -= 0.5f; bounds.max.y += 0.5f;
                geometry = new EdgeGeometry {
                    m_Start = new Segment { m_Left = sl, m_Right = sr, m_Length = new float2(MathUtils.Length(sl),MathUtils.Length(sr)) },
                    m_End = new Segment { m_Left = el, m_Right = er, m_Length = new float2(MathUtils.Length(el),MathUtils.Length(er)) },
                    m_Bounds = bounds
                };
                reason = null;
                return true;
            }
            return false;
        }

        internal static bool ValidateGuide(Segment approach, EdgeNodeGeometry node,
            Bezier4x3 left, Bezier4x3 right, out string reason)
        {
            reason = "Invalid guide geometry";
            float3 origin = (approach.m_Left.d + approach.m_Right.d) * .5f;
            float width = math.distance(approach.m_Left.d.xz, approach.m_Right.d.xz);
            if (width < .5f) return false;
            float2 across = math.normalizesafe((approach.m_Right.d - approach.m_Left.d).xz);
            float2 forward = new float2(-across.y, across.x);
            if (math.dot(forward, (approach.m_Left.d - approach.m_Left.c).xz) < 0) forward = -forward;
            float2 Local(float3 p) => new float2(math.dot(p.xz - origin.xz, across), math.dot(p.xz - origin.xz, forward));
            var samples = new List<float3>();
            var pavement = new List<float2>();
            if (!Flatten(node.m_Left.m_Left, samples)) return false;
            foreach (var p in samples) pavement.Add(Local(p));
            pavement.Add(Local(node.m_Left.m_Right.d)); pavement.Add(Local(node.m_Middle.d)); pavement.Add(Local(node.m_Right.m_Left.d));
            if (!Flatten(node.m_Right.m_Right, samples)) return false;
            for (int i = samples.Count - 1; i >= 0; i--) pavement.Add(Local(samples[i]));
            var outline = new List<float2>();
            if (!Flatten(left, samples)) return false;
            foreach (var p in samples) outline.Add(Local(p));
            if (!Flatten(right, samples)) return false;
            for (int i = samples.Count - 2; i >= 0; i--) outline.Add(Local(samples[i]));
            float previousAlong = 0;
            for (int i = 1; i <= 64; i++)
            {
                float t = i / 64f;
                float2 l = Local(MathUtils.Position(left, t)), r = Local(MathUtils.Position(right, t));
                float along = (l.y + r.y) * .5f;
                if (!math.all(math.isfinite(l)) || !math.all(math.isfinite(r)) || along < previousAlong - .01f ||
                    r.x < l.x - .01f) return false;
                previousAlong = along;
            }
            // Manual placement may overlap lanes; automatic tapers retain their clearance checks.
            return Safe(outline, pavement, new List<Block>(), 0, 0, out reason);
        }

        private static Bezier4x3 Fit(Bezier4x3 approach, float2 tip, List<float3> sections, int end, bool right,
            System.Func<float2,float3> world, System.Func<float3,float2> local)
        {
            float2 a = local(approach.d);
            float2 direction = math.normalizesafe(local(approach.d) - local(approach.c), new float2(0,1));
            float2 b = a + direction * (tip.y / 3f);
            float2 sum = 0; float weight = 0;
            for (int i = 1; i < end; i++)
            {
                float t = sections[i].z / tip.y, u = 1 - t, coefficient = 3 * u * t * t;
                // Ease inside the measured envelope so the two sides meet at a
                // point, instead of ending as a rounded or full-width cap.
                float x = right ? sections[i].y : sections[i].x;
                float middle = (sections[i].x + sections[i].y) * 0.5f;
                x = math.lerp(x, middle, t * t);
                sum += coefficient * (new float2(x,sections[i].z) - u*u*u*a - 3*u*u*t*b - t*t*t*tip);
                weight += coefficient * coefficient;
            }
            float2 c = weight > 0 ? sum / weight : math.lerp(b,tip,0.5f);
            return new Bezier4x3(approach.d, world(b), world(c), world(tip));
        }

        private static bool Safe(List<float2> polygon, List<float2> pavement, List<Block> blocks, float sourcePadding, float padding, out string reason)
        {
            reason = "Outside pavement";
            foreach (var p in polygon)
                if (!math.all(math.isfinite(p)) || p.y < -0.02f || (p.y > 0.05f && !Inside(p,pavement))) return false;
            // Non-adjacent outline crossings reject reversed/self-intersecting fits.
            for (int i = 0; i < polygon.Count; i++)
            {
                float2 a=polygon[i], b=polygon[(i+1)%polygon.Count];
                for (int j = 0; j < pavement.Count; j++)
                    if (math.min(a.y,b.y) > 0.05f && Crosses(a,b,pavement[j],pavement[(j+1)%pavement.Count])) return false;
                for (int j=i+2;j<polygon.Count;j++)
                {
                    if (i==0 && j==polygon.Count-1) continue;
                    float2 c=polygon[j], d=polygon[(j+1)%polygon.Count];
                    if (Crosses(a,b,c,d)) { reason = "Outline crossing"; return false; }
                }
                foreach (var lane in blocks)
                {
                    float along = math.min(a.y,b.y);
                    // At the attachment use the existing road clearance. The
                    // additional inset ramps up over the first two metres.
                    float radius = lane.Radius + math.lerp(sourcePadding, padding, math.saturate(along / 2f));
                    // Captured native attachment boundaries differ by up to 2cm.
                    // This tolerance expires within 0.5m; it never relaxes the tip inset.
                    if (Distance(a,b,lane.A,lane.B) < radius - Error - 0.02f * (1 - math.saturate(along / 0.5f)))
                    { reason = "Insufficient lane clearance"; return false; }
                }
            }
            foreach(var lane in blocks) if(Inside(lane.A,polygon)||Inside(lane.B,polygon)) return false;
            return true;
        }
        private static float Cross(float2 a,float2 b)=>a.x*b.y-a.y*b.x;
        internal static bool Overlaps(EdgeNodeGeometry a, EdgeNodeGeometry b)
        {
            List<float2> Outline(EdgeNodeGeometry node)
            {
                var result = new List<float2>();
                var samples = new List<float3>();
                if (!Flatten(node.m_Left.m_Left, samples)) return result;
                foreach (var p in samples) result.Add(p.xz);
                if (!Flatten(node.m_Right.m_Right, samples)) return new List<float2>();
                for (int i = samples.Count - 1; i >= 0; i--) result.Add(samples[i].xz);
                return result;
            }
            var x = Outline(a); var y = Outline(b);
            if (x.Count < 3 || y.Count < 3) return true;
            foreach (var p in x) if (Inside(p,y)) return true;
            foreach (var p in y) if (Inside(p,x)) return true;
            for (int i = 0; i < x.Count; i++)
                for (int j = 0; j < y.Count; j++)
                    if (Crosses(x[i],x[(i+1)%x.Count],y[j],y[(j+1)%y.Count])) return true;
            return false;
        }
        private static bool Crosses(float2 a,float2 b,float2 c,float2 d)=>
            Cross(b-a,c-a)*Cross(b-a,d-a)<-0.000001f && Cross(d-c,a-c)*Cross(d-c,b-c)<-0.000001f;
        private static float PointDistance(float2 p,float2 a,float2 b)=>math.distance(p,math.lerp(a,b,math.saturate(math.dot(p-a,b-a)/math.max(1e-9f,math.lengthsq(b-a)))));
        private static float Distance(float2 a,float2 b,float2 c,float2 d)=>Crosses(a,b,c,d)?0:
            math.min(math.min(PointDistance(a,c,d),PointDistance(b,c,d)),math.min(PointDistance(c,a,b),PointDistance(d,a,b)));
        private static bool Inside(float2 p,List<float2> polygon)
        {
            bool inside=false;
            for(int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
            {
                var a=polygon[i];var b=polygon[j];
                if(PointDistance(p,a,b)<0.001f)return true;
                if((a.y>p.y)!=(b.y>p.y) && p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x)inside=!inside;
            }
            return inside;
        }
        private static bool Flatten(Bezier4x3 curve,List<float3> points)
        {
            points.Clear();
            if(!math.all(math.isfinite(curve.a))||!math.all(math.isfinite(curve.b))||!math.all(math.isfinite(curve.c))||!math.all(math.isfinite(curve.d)))return false;
            points.Add(curve.a);
            return Flatten(curve,points,0);
        }
        private static bool Flatten(Bezier4x3 c,List<float3> points,int depth)
        {
            if(math.distance(c.a.xz,c.d.xz)<=0.5f && PointDistance(c.b.xz,c.a.xz,c.d.xz)<=Error && PointDistance(c.c.xz,c.a.xz,c.d.xz)<=Error)
            {points.Add(c.d);return true;}
            if(depth>=14)return false;
            MathUtils.Divide(c,out var a,out var b,0.5f);
            return Flatten(a,points,depth+1)&&Flatten(b,points,depth+1);
        }
    }
}
