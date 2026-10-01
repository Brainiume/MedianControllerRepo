using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace RoadGrass
{
    internal static class RoadGrassLaneGuideResolver
    {
        internal static List<float3> FollowEdge(float3 start, float3 end, IReadOnlyList<RoadGrassTaperGeometry.Lane> lanes)
        {
            List<float3> best = null;
            float score = 1.5f;
            foreach (var lane in lanes)
            for (int side = -1; side <= 1; side += 2)
            {
                var samples = new List<float3>();
                int from = 0, to = 0; float a = float.MaxValue, b = float.MaxValue;
                for (int i = 0; i <= 64; i++)
                {
                    float t = i / 64f;
                    float3 p = MathUtils.Position(lane.Curve, t);
                    float2 tangent = math.normalizesafe(MathUtils.Tangent(lane.Curve, t).xz);
                    p.xz += new float2(-tangent.y, tangent.x) * (side * lane.Width * .5f);
                    samples.Add(p);
                    float da = math.distance(start, p), db = math.distance(end, p);
                    if (da < a) { a = da; from = i; }
                    if (db < b) { b = db; to = i; }
                }
                if (a > .75f || b > .75f || a + b >= score || from == to) continue;
                int direction = to > from ? 1 : -1;
                var path = new List<float3> { start };
                for (int i = from + direction; i != to; i += direction)
                {
                    float t = (float)(i - from) / (to - from);
                    path.Add(samples[i] + math.lerp(start - samples[from], end - samples[to], t));
                }
                path.Add(end);
                // Keep only samples needed for a 5cm curved outline approximation.
                for (int i = 1; i < path.Count - 1;)
                {
                    float3 d = path[i + 1] - path[i - 1];
                    float t = math.saturate(math.dot(path[i] - path[i - 1], d) / math.max(.0001f, math.lengthsq(d)));
                    if (math.distance(path[i], path[i - 1] + d * t) < .05f) path.RemoveAt(i);
                    else i++;
                }
                score = a + b; best = path;
            }
            return best ?? new List<float3> { start, end };
        }
        // A supported contour is one forward lane-guided strip. Arbitrary closed
        // polygons and guide switches are rejected rather than rendered approximately.
        internal static bool TryResolve(float3 start, float3 forward, float width, float3 destination,
            IReadOnlyList<RoadGrassTaperGeometry.Lane> lanes, out List<float3> guide, out string reason)
        {
            guide = null;
            reason = "No lane guide connects this median to the selected point";
            float best = float.MaxValue;
            foreach (var lane in lanes)
            for (int side = -1; side <= 1; side += 2)
            {
                var samples = new List<float3>();
                for (int i = 0; i <= 64; i++)
                {
                    float t = i / 64f;
                    float3 p = MathUtils.Position(lane.Curve, t);
                    float2 tangent = math.normalizesafe(MathUtils.Tangent(lane.Curve, t).xz);
                    p.xz += new float2(-tangent.y, tangent.x) * (side * lane.Width * .5f);
                    samples.Add(p);
                }
                int from = 0, to = 0;
                float startDistance = float.MaxValue, endDistance = float.MaxValue;
                for (int i = 0; i < samples.Count; i++)
                {
                    float a = math.distancesq(start.xz, samples[i].xz), b = math.distancesq(destination.xz, samples[i].xz);
                    if (a < startDistance) { startDistance = a; from = i; }
                    if (b < endDistance) { endDistance = b; to = i; }
                }
                if (math.sqrt(startDistance) > width * .5f + 2 || endDistance > 1 || math.abs(to - from) < 3) continue;
                int direction = to > from ? 1 : -1;
                float3 initial = samples[from + direction] - samples[from];
                if (math.dot(math.normalizesafe(initial.xz), forward.xz) < .4f) continue;
                float score = startDistance + endDistance * 4;
                if (score >= best) continue;
                float2 offset = start.xz - samples[from].xz;
                var candidate = new List<float3> { start };
                bool valid = true;
                for (int i = from + direction; i != to + direction; i += direction)
                {
                    float t = (float)(i - from) / (to - from);
                    float3 p = samples[i];
                    // Preserve the median's initial lane setback; converge to the
                    // chosen guide intersection with a small inset at the nose.
                    p.xz += offset * (1 - t) + math.normalizesafe(offset) * RoadGrassTaperGeometry.Inset * t;
                    p.y = math.lerp(start.y, destination.y, t);
                    if (!math.all(math.isfinite(p)) || math.dot(p.xz - candidate[candidate.Count - 1].xz, forward.xz) <= 0)
                    { valid = false; break; }
                    candidate.Add(p);
                }
                if (!valid || candidate.Count < 4) continue;
                best = score;
                guide = candidate;
            }
            return guide != null;
        }

        internal static bool TryFit(IReadOnlyList<float3> points, out Bezier4x3 curve)
        {
            curve = default;
            if (points.Count < 4) return false;
            float total = 0;
            var distances = new float[points.Count];
            for (int i = 1; i < points.Count; i++) distances[i] = total += math.distance(points[i - 1], points[i]);
            if (total < 1 || total > 100) return false;
            float3 a = points[0], d = points[points.Count - 1];
            float aa = 0, ab = 0, bb = 0;
            float3 x = 0, y = 0;
            for (int i = 1; i < points.Count - 1; i++)
            {
                float t = distances[i] / total, u = 1 - t, b = 3 * u * u * t, c = 3 * u * t * t;
                float3 residual = points[i] - a * u * u * u - d * t * t * t;
                aa += b * b; ab += b * c; bb += c * c; x += b * residual; y += c * residual;
            }
            float determinant = aa * bb - ab * ab;
            if (determinant < .00001f) return false;
            curve = new Bezier4x3(a, (x * bb - y * ab) / determinant, (y * aa - x * ab) / determinant, d);
            for (int i = 0; i < points.Count; i++)
                if (math.distance(MathUtils.Position(curve, distances[i] / total), points[i]) > .2f) return false;
            return true;
        }
    }
}
