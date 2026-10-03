using System.Collections.Generic;
using Unity.Mathematics;

namespace RoadGrass
{
    // Pure contour stages shared by preview and placed medians. Native profiles own curb/height.
    internal static class RoadGrassPolygonShape
    {
        internal sealed class Loop
        {
            internal readonly List<float3> Points = new();
            internal readonly List<bool> Opening = new();
        }

        internal static List<Loop> Build(IReadOnlyList<float3> points, IReadOnlyList<bool> opening, float4 settings)
        {
            var seed = new Loop();
            for (int i = 0; i < points.Count; i++)
            { seed.Points.Add(points[i]); seed.Opening.Add(opening != null && opening[i]); }
            if (math.all(settings == 0)) return new List<Loop> { seed };
            var result = new List<Loop>();
            foreach (var simple in Split(seed))
            {
                if (math.abs(Area(simple.Points)) < .0001f) continue;
                var moved = Offset(simple, settings.x, settings.y);
                foreach (var part in Split(moved))
                {
                    if (Area(part.Points) * Area(simple.Points) <= .0001f) continue;
                    // An inset edge that runs backwards is a collapsed fragment, not a new island.
                    bool collapsed = false;
                    for (int i = 0; i < part.Points.Count && !collapsed; i++)
                    {
                        var middle = (part.Points[i].xz + part.Points[(i + 1) % part.Points.Count].xz) * .5f;
                        if (!Inside(middle, simple.Points)) collapsed = true;
                        float best = float.MaxValue, required = 0;
                        for (int j = 0; j < simple.Points.Count; j++)
                        {
                            float2 a = simple.Points[j].xz, d = simple.Points[(j + 1) % simple.Points.Count].xz - a;
                            float t = math.saturate(math.dot(middle - a, d) / math.max(1e-8f, math.lengthsq(d)));
                            float distance = math.distance(middle, a + d * t);
                            if (distance < best) { best = distance; required = simple.Opening[j] ? settings.y : settings.x; }
                        }
                        if (best + .002f < required) collapsed = true;
                    }
                    if (!collapsed)
                    {
                        var rounded = Round(part, settings.z, settings.w, 1);
                        for (int scale = 2; rounded.Points.Count > 128 && scale <= 4; scale *= 2)
                            rounded = Round(part, settings.z, settings.w, scale);
                        result.Add(rounded);
                    }
                }
            }
            return result;
        }

        internal static Loop Offset(Loop input, float line, float median)
        {
            var result = new Loop();
            float sign = math.sign(Area(input.Points));
            for (int i = 0; i < input.Points.Count; i++)
            {
                int previous = (i + input.Points.Count - 1) % input.Points.Count;
                float3 p = input.Points[i];
                float2 before = math.normalizesafe(p.xz - input.Points[previous].xz);
                float2 after = math.normalizesafe(input.Points[(i + 1) % input.Points.Count].xz - p.xz);
                float a = input.Opening[previous] ? median : line, b = input.Opening[i] ? median : line;
                float2 x = p.xz + new float2(-before.y, before.x) * a * sign;
                float2 y = p.xz + new float2(-after.y, after.x) * b * sign;
                float determinant = Cross(before, after);
                float2 joint = math.abs(determinant) > .00001f ? x + before * (Cross(y - x, after) / determinant) : (x + y) * .5f;
                float2 delta = joint - p.xz;
                if (math.length(delta) > 100) joint = p.xz + math.normalizesafe(delta) * 100;
                result.Points.Add(new float3(joint.x, p.y, joint.y)); result.Opening.Add(input.Opening[i]);
            }
            return result;
        }

        private static Loop Round(Loop input, float line, float median, int scale)
        {
            var result = new Loop(); int count = input.Points.Count;
            var trim = new float[count]; var angles = new float[count];
            for (int i = 0; i < count; i++)
            {
                float2 u = input.Points[(i + count - 1) % count].xz - input.Points[i].xz;
                float2 v = input.Points[(i + 1) % count].xz - input.Points[i].xz;
                float angle = math.acos(math.clamp(math.dot(math.normalizesafe(u), math.normalizesafe(v)), -1, 1));
                float radius = input.Opening[i] || input.Opening[(i + count - 1) % count] ? median : line;
                float t = radius / math.tan(angle * .5f);
                if (radius > 0 && angle > .001f && angle < math.PI - .001f && t <= math.min(math.length(u), math.length(v)))
                { trim[i] = t; angles[i] = angle; }
            }
            // Each edge has two consumers. Leave both sharp if their requested fillets cannot fit.
            var fits = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int previous = (i + count - 1) % count, next = (i + 1) % count;
                fits[i] = trim[i] > 0 && trim[i] + trim[previous] <= math.distance(input.Points[i].xz, input.Points[previous].xz) + .0001f &&
                    trim[i] + trim[next] <= math.distance(input.Points[i].xz, input.Points[next].xz) + .0001f;
            }
            for (int i = 0; i < count; i++)
            {
                int previous = (i + count - 1) % count, next = (i + 1) % count;
                float3 p = input.Points[i];
                if (!fits[i]) { result.Points.Add(p); result.Opening.Add(input.Opening[i]); continue; }
                float radius = input.Opening[i] || input.Opening[previous] ? median : line;
                float2 u = math.normalize(input.Points[previous].xz - p.xz), v = math.normalize(input.Points[next].xz - p.xz);
                float2 centre = p.xz + math.normalize(u + v) * (radius / math.sin(angles[i] * .5f));
                float2 start = p.xz + u * trim[i] - centre, end = p.xz + v * trim[i] - centre;
                float turn = math.atan2(Cross(start, end), math.dot(start, end));
                // ponytail: bounded chord sampling; native section budget is checked by the caller.
                int samples = math.max(2, (int)math.ceil(math.abs(turn) / math.radians(5 * scale)));
                samples = math.max(samples, (int)math.ceil(math.abs(turn) * radius / (10 * scale)));
                float startY = math.lerp(p.y, input.Points[previous].y, trim[i] / math.distance(p.xz, input.Points[previous].xz));
                float endY = math.lerp(p.y, input.Points[next].y, trim[i] / math.distance(p.xz, input.Points[next].xz));
                samples = math.max(samples, (int)math.ceil(math.abs(endY - startY) / 3));
                for (int j = 0; j <= samples; j++)
                {
                    float t = j / (float)samples, a = turn * t;
                    float2 point = centre + new float2(start.x * math.cos(a) - start.y * math.sin(a), start.x * math.sin(a) + start.y * math.cos(a));
                    result.Points.Add(new float3(point.x, math.lerp(startY, endY, t), point.y));
                    result.Opening.Add(j == samples ? input.Opening[i] : input.Opening[i] || input.Opening[previous]);
                }
            }
            return result;
        }

        private static List<Loop> Split(Loop seed)
        {
            var pending = new List<Loop> { seed }; var result = new List<Loop>();
            while (pending.Count > 0)
            {
                var loop = pending[pending.Count - 1]; pending.RemoveAt(pending.Count - 1);
                for (int k = loop.Points.Count - 1; k >= 0 && loop.Points.Count > 1; k--)
                    if (math.distancesq(loop.Points[k], loop.Points[(k + 1) % loop.Points.Count]) < .000001f)
                    { loop.Points.RemoveAt(k); loop.Opening.RemoveAt(k); }
                int count = loop.Points.Count; bool split = false;
                if (count < 3) continue;
                for (int i = 0; i < count && !split; i++) for (int j = i + 2; j < count && !split; j++)
                {
                    if (i == 0 && j == count - 1) continue;
                    float3 a = loop.Points[i], b = loop.Points[j];
                    float2 u = loop.Points[(i + 1) % count].xz - a.xz, v = loop.Points[(j + 1) % count].xz - b.xz;
                    float d = Cross(u, v); if (math.abs(d) < 1e-7f) continue;
                    float t = Cross(b.xz - a.xz, v) / d, s = Cross(b.xz - a.xz, u) / d;
                    if (t < -.00001f || t > 1.00001f || s < -.00001f || s > 1.00001f) continue;
                    // Insets meet at T junctions when a narrow neck collapses. Ignoring endpoint
                    // intersections leaves the two islands joined by an inverted strip.
                    if ((t <= .00001f || t >= .99999f) && (s <= .00001f || s >= .99999f)) continue;
                    var hit = math.lerp(a, loop.Points[(i + 1) % count], t);
                    var first = new Loop(); first.Points.Add(hit); first.Opening.Add(loop.Opening[i]);
                    for (int k = i + 1; k <= j; k++) { first.Points.Add(loop.Points[k]); first.Opening.Add(loop.Opening[k]); }
                    var second = new Loop(); second.Points.Add(hit); second.Opening.Add(loop.Opening[j]);
                    for (int k = j + 1; k <= count + i; k++) { second.Points.Add(loop.Points[k % count]); second.Opening.Add(loop.Opening[k % count]); }
                    pending.Add(first); pending.Add(second); split = true;
                }
                if (!split) result.Add(loop);
                if (pending.Count + result.Count > 256) return new List<Loop>();
            }
            return result;
        }

        private static bool Inside(float2 p, List<float3> points)
        {
            bool inside = false;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                float2 a = points[i].xz, b = points[j].xz, d = b - a;
                if (math.distance(p, a + d * math.saturate(math.dot(p - a, d) / math.max(1e-8f, math.lengthsq(d)))) < .002f) return true;
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }
        private static float Area(List<float3> points)
        { float a = 0; for (int i = 0; i < points.Count; i++) a += Cross(points[i].xz, points[(i + 1) % points.Count].xz); return a * .5f; }
        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
    }
}
