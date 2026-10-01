using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private struct PolygonGuide
        {
            internal RoadGrassPolygonGuideReference Reference;
            internal Bezier4x3 Curve;
            internal float Width;
            internal float3 Point(float t)
            {
                float3 p = MathUtils.Position(Curve, t);
                float2 d = math.normalizesafe(MathUtils.Tangent(Curve, t).xz);
                p.xz += new float2(-d.y, d.x) * (Reference.Side * Width * .5f);
                return p;
            }
        }
        private readonly Dictionary<Entity, List<PolygonGuide>> m_PolygonReferenceGuides = new();
        private List<PolygonGuide> PolygonReferenceGuides(Entity node)
        {
            if (m_PolygonReferenceGuides.TryGetValue(node, out var result)) return result;
            result = new List<PolygonGuide>(); var seen = new HashSet<Entity>();
            float3 origin = EntityManager.GetComponentData<Node>(node).m_Position;
            var marking = GetMarkingGuides(node);
            Read(node);
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
                foreach (var edge in EntityManager.GetBuffer<ConnectedEdge>(node, true)) Read(edge.m_Edge);
            m_PolygonReferenceGuides[node] = result; return result;
            void Read(Entity owner)
            {
                if (!EntityManager.HasBuffer<Game.Net.SubLane>(owner)) return;
                var lanes = EntityManager.GetBuffer<Game.Net.SubLane>(owner, true);
                for (int i = 0; i < lanes.Length; i++)
                {
                    var entity = lanes[i].m_SubLane;
                    if (!seen.Add(entity) || EntityManager.HasComponent<Deleted>(entity) || EntityManager.HasComponent<Game.Tools.Temp>(entity) ||
                        !EntityManager.TryGetComponent(entity, out Curve curve) || !EntityManager.TryGetComponent(entity, out PrefabRef prefab)) continue;
                    bool car = EntityManager.HasComponent<Game.Net.CarLane>(entity), decal = false;
                    foreach (var guide in marking) if (guide.Curve.Equals(curve.m_Bezier)) { decal = true; break; }
                    if (!car && !decal) continue;
                    float width = car && EntityManager.TryGetComponent(prefab.m_Prefab, out NetLaneData data) ? data.m_Width : 0;
                    for (int side = width == 0 ? 0 : -1; side <= 1; side += 2)
                    {
                        result.Add(new PolygonGuide { Curve = curve.m_Bezier, Width = width,
                            Reference = new RoadGrassPolygonGuideReference { Owner = owner, Prefab = prefab.m_Prefab, Ordinal = i, Side = side,
                                Start = curve.m_Bezier.a - origin, End = curve.m_Bezier.d - origin } });
                        if (width == 0) break;
                    }
                }
            }
        }

        private RoadGrassSavedManualMedianVertex CapturePolygonReference(int id, RoadGrassResolvedMedianAnchor anchor, float3 p, bool snapped)
        {
            float3 origin = EntityManager.GetComponentData<Node>(anchor.Node).m_Position;
            var vertex = new RoadGrassSavedManualMedianVertex { Id = id, Offset = p - origin };
            if (!snapped) return vertex;
            if (RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var opening))
            {
                if (math.distance(p, opening.m_Left.d) < .01f) { vertex.Kind = 1; return vertex; }
                if (math.distance(p, opening.m_Right.d) < .01f) { vertex.Kind = 2; return vertex; }
            }
            float best = .2f, second = .2f;
            foreach (var guide in PolygonReferenceGuides(anchor.Node))
            {
                float distance = float.MaxValue, parameter = 0;
                float3 previous = guide.Point(0);
                for (int i = 1; i <= 64; i++)
                {
                    float3 next = guide.Point(i / 64f), d = next - previous;
                    float t = math.saturate(math.dot(p - previous, d) / math.max(.000001f, math.lengthsq(d)));
                    float error = math.distance(p, math.lerp(previous, next, t));
                    if (error < distance) { distance = error; parameter = (i - 1 + t) / 64f; }
                    previous = next;
                }
                var reference = guide.Reference; reference.T = parameter;
                if (distance < best)
                { second = best; vertex.RefB = vertex.RefA; best = distance; vertex.RefA = reference; }
                else if (distance < second) { second = distance; vertex.RefB = reference; }
            }
            if (best < .2f) vertex.Kind = second < .2f ? 4 : 3;
            // Proximity to two guides does not prove they intersect (parallel lane borders often overlap).
            // A reference must reproduce the previewed point before it is allowed into the save.
            if (vertex.Kind != 0 && (!ResolvePolygonReference(anchor, vertex, out var resolved) || math.distance(resolved, p) > .01f))
            { vertex.Kind = 0; vertex.RefA = default; vertex.RefB = default; }
            return vertex;
        }

        private bool ResolvePolygonReference(RoadGrassResolvedMedianAnchor anchor, RoadGrassSavedManualMedianVertex vertex, out float3 p)
        {
            float3 origin = EntityManager.GetComponentData<Node>(anchor.Node).m_Position;
            p = origin + vertex.Offset;
            if (vertex.Kind == 0) return math.all(math.isfinite(p));
            if (vertex.Kind == 1 || vertex.Kind == 2)
            {
                if (!RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var opening)) return false;
                p = vertex.Kind == 1 ? opening.m_Left.d : opening.m_Right.d; return true;
            }
            if (!Find(vertex.RefA, out var a)) return false;
            if (vertex.Kind == 3) { p = a.Point(vertex.RefA.T); return true; }
            if (!Find(vertex.RefB, out var b)) return false;
            float best = float.MaxValue; int matches = 0; float3 hit = default;
            // Only rebuilt after a network event; endpoints and guide identity narrow the search.
            for (int i = 0; i < 32; i++) for (int j = 0; j < 32; j++)
            {
                float3 x = a.Point(i / 32f), y = b.Point(j / 32f);
                float3 u = a.Point((i + 1) / 32f) - x, v = b.Point((j + 1) / 32f) - y;
                float d = Cross(u.xz, v.xz); if (math.abs(d) < 1e-7f) continue;
                float t = Cross((y - x).xz, v.xz) / d, s = Cross((y - x).xz, u.xz) / d;
                if (t < 0 || t > 1 || s < 0 || s > 1 || math.abs((x + u * t).y - (y + v * s).y) > .5f) continue;
                float3 found = x + u * t; float score = math.distance(found, origin + vertex.Offset);
                if (score > 8) continue;
                if (score < best - .05f) { best = score; hit = found; matches = 1; }
                else if (math.abs(score - best) < .05f && math.distance(found, hit) > .05f) matches++;
            }
            if (matches != 1) return false;
            p = hit; return true;

            bool Find(RoadGrassPolygonGuideReference reference, out PolygonGuide selected)
            {
                selected = default; int exact = 0, fallback = 0; PolygonGuide near = default;
                if (!math.isfinite(reference.T) || reference.T < 0 || reference.T > 1 ||
                    !math.all(math.isfinite(reference.Start)) || !math.all(math.isfinite(reference.End))) return false;
                foreach (var guide in PolygonReferenceGuides(anchor.Node))
                {
                    var r = guide.Reference;
                    if (r.Owner != reference.Owner || r.Side != reference.Side) continue;
                    float distance = math.max(math.distance(r.Start, reference.Start), math.distance(r.End, reference.End));
                    if (r.Prefab == reference.Prefab && r.Ordinal == reference.Ordinal && distance < 8) { selected = guide; exact++; }
                    if (distance < 2) { near = guide; fallback++; }
                }
                if (exact == 1) return true;
                if (exact == 0 && fallback == 1) { selected = near; return true; }
                return false;
            }
        }
    }
}
