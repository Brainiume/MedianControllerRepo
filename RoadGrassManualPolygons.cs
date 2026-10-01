using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private readonly HashSet<RoadGrassMedianAnchorKey> m_PolygonRenderKeys = new();
        private List<float3> m_PolygonPreviewPoints;
        private RoadGrassMedianAnchorKey m_PolygonPreviewKey;
        private MedianSavedSurface m_PolygonPreviewSurface;
        private List<RoadGrassTransitionMesh.Half> m_PolygonPreviewHalves;
        private bool m_PolygonPreviewValid;
        private string m_PolygonPreviewReason;
        private readonly Dictionary<Entity, List<RoadGrassTaperGeometry.Lane>> m_PolygonGuides = new();
        private readonly Dictionary<(Entity, float3, float3), List<float3>> m_PolygonGuideEdges = new();

        internal List<float3> PolygonGuideEdge(Entity node, float3 start, float3 end)
        {
            var key = (node, start, end);
            if (m_PolygonGuideEdges.TryGetValue(key, out var path)) return path;
            if (!m_PolygonGuides.TryGetValue(node, out var lanes))
            {
                lanes = new List<RoadGrassTaperGeometry.Lane>(GetMarkingGuides(node));
                if (EntityManager.HasBuffer<Game.Net.SubLane>(node))
                    foreach (var sub in EntityManager.GetBuffer<Game.Net.SubLane>(node, true))
                    {
                        Entity lane = sub.m_SubLane;
                        if (!EntityManager.HasComponent<Game.Net.CarLane>(lane) ||
                            !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve) ||
                            !EntityManager.TryGetComponent(lane, out PrefabRef prefab) ||
                            !EntityManager.TryGetComponent(prefab.m_Prefab, out NetLaneData data)) continue;
                        lanes.Add(new RoadGrassTaperGeometry.Lane { Curve = curve.m_Bezier, Width = data.m_Width });
                    }
                m_PolygonGuides[node] = lanes;
            }
            path = RoadGrassLaneGuideResolver.FollowEdge(start, end, lanes);
            m_PolygonGuideEdges[key] = path;
            return path;
        }

        internal bool CanDrawPolygon(RoadGrassResolvedMedianAnchor anchor) => anchor != null && m_GameReady &&
            !m_SuspendedForSave && !m_WaitingForLoadSettle && !m_CityQuery.IsEmptyIgnoreFilter;

        internal bool TryPolygonPreview(RoadGrassResolvedMedianAnchor anchor, IReadOnlyList<float3> points,
            MedianSavedSurface surface, out List<RoadGrassTransitionMesh.Half> halves)
        {
            halves = null;
            if (!CanDrawPolygon(anchor)) return false;
            bool same = m_PolygonPreviewPoints != null && m_PolygonPreviewKey.Equals(anchor.Key) &&
                m_PolygonPreviewSurface == surface && m_PolygonPreviewPoints.Count == points.Count;
            for (int i = 0; same && i < points.Count; i++) same = m_PolygonPreviewPoints[i].Equals(points[i]);
            if (!same)
            {
                m_PolygonPreviewPoints = new List<float3>(points);
                m_PolygonPreviewKey = anchor.Key; m_PolygonPreviewSurface = surface;
                m_PolygonPreviewValid = TryCreatePolygon(anchor, points, surface, out m_PolygonPreviewHalves, out m_PolygonPreviewReason);
            }
            halves = m_PolygonPreviewHalves;
            ManualMessage = m_PolygonPreviewValid ? "" : m_PolygonPreviewReason;
            return m_PolygonPreviewValid;
        }

        private bool TryCreatePolygon(RoadGrassResolvedMedianAnchor anchor, IReadOnlyList<float3> points,
            MedianSavedSurface surface, out List<RoadGrassTransitionMesh.Half> halves, out string reason)
        {
            if (!TryPlanPolygon(anchor, points, default, out var sections, out _, out reason))
            { halves = null; return false; }
            return BindPolygonSurface(anchor, sections, surface, out halves, out reason);
        }

        private bool TryPlanPolygon(RoadGrassResolvedMedianAnchor anchor, IReadOnlyList<float3> points,
            float4 shape, out List<RoadGrassPolygonSections.Section> sections, out bool attached, out string reason)
        {
            sections = new List<RoadGrassPolygonSections.Section>(); attached = false;
            reason = "Choose at least three polygon corners";
            if (!CanDrawPolygon(anchor) || points.Count < 3) return false;
            var kinds = PolygonOpeningEdges(anchor, points);
            var loops = RoadGrassPolygonShape.Build(points, kinds, shape);
            foreach (var loop in loops)
            {
                float2? direction = null;
                if (TryPolygonAttachment(anchor, loop.Points, out var opening))
                {
                    attached = true;
                    float2 across = math.normalizesafe((opening.m_Right.d - opening.m_Left.d).xz);
                    float2 forward = new float2(-across.y, across.x);
                    if (math.dot(forward, (opening.m_Left.d - opening.m_Left.c).xz) < 0) forward = -forward;
                    direction = forward;
                }
                if (!RoadGrassPolygonSections.TryBuild(loop.Points, out var part, out reason, direction)) return false;
                sections.AddRange(part);
                if (sections.Count >= 256) { reason = "Polygon needs too many native sections"; return false; }
            }
            reason = loops.Count == 0 ? "Shape collapses" : loops.Count > 1 ? $"Split into {loops.Count} islands" : "";
            return true;
        }

        private bool[] PolygonOpeningEdges(RoadGrassResolvedMedianAnchor anchor, IReadOnlyList<float3> points)
        {
            var kinds = new bool[points.Count];
            if (!RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var opening)) return kinds;
            for (int i = 0; i < points.Count; i++)
            {
                float3 a = points[i], b = points[(i + 1) % points.Count];
                kinds[i] = (math.distance(a, opening.m_Left.d) < .01f && math.distance(b, opening.m_Right.d) < .01f) ||
                    (math.distance(b, opening.m_Left.d) < .01f && math.distance(a, opening.m_Right.d) < .01f);
            }
            return kinds;
        }

        private bool BindPolygonSurface(RoadGrassResolvedMedianAnchor anchor, List<RoadGrassPolygonSections.Section> sections,
            MedianSavedSurface surface, out List<RoadGrassTransitionMesh.Half> halves, out string reason)
        {
            halves = new List<RoadGrassTransitionMesh.Half>();
            if (!TryManualSurface(anchor, surface, out var styled)) { reason = "Native median surface unavailable"; return false; }
            float2 borders = RoadGrassTransitionMesh.ReadBorderWidths(EntityManager, styled);
            foreach (var section in sections)
            {
                var geometry = BuildEdgeGeometry(section.Left, section.Right);
                float endWidth = math.distance(section.Left.d, section.Right.d);
                var half = RoadGrassTransitionMesh.Create(styled, geometry, borders, math.min(borders, new float2(endWidth * .5f)));
                PreservePolygonTipCurb(half, borders);
                ConformPolygonHeight(half, anchor);
                halves.Add(half);
            }
            reason = "";
            return true;
        }

        private bool TryPolygonAttachment(RoadGrassResolvedMedianAnchor anchor, IReadOnlyList<float3> points, out Segment opening)
        {
            if (!RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out opening)) return false;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                if ((math.distance(a, opening.m_Left.d) < .01f && math.distance(b, opening.m_Right.d) < .01f) ||
                    (math.distance(b, opening.m_Left.d) < .01f && math.distance(a, opening.m_Right.d) < .01f)) return true;
            }
            return false;
        }

        private static void PreservePolygonTipCurb(RoadGrassTransitionMesh.Half half, float2 borders)
        {
            var node = half.Geometry;
            var left = node.m_Left.m_Left; var right = node.m_Right.m_Right;
            float2 inset = math.max(borders, new float2(.12f));
            float t = RoadGrassPolygonSections.InteriorEnd(left, right, inset);
            if (t >= .9999f) return;
            var middle = node.m_Middle;
            middle.d = (MathUtils.Position(left, t) + MathUtils.Position(right, t)) * .5f;
            middle.c = math.lerp(middle.b, middle.d, .75f);
            node.m_Middle = middle;
            node.m_Left.m_Right = middle; node.m_Right.m_Left = middle;
            node.m_Left.m_Length.y = node.m_Right.m_Length.x = MathUtils.Length(middle);
            float radius = math.max(math.distance((left.d + right.d) * .5f, middle.d), math.cmax(inset));
            node.m_SyncVertexTargetsLeft = new float4(0, 0, 0, math.min(1, inset.x / radius));
            node.m_SyncVertexTargetsRight = new float4(math.max(0, 1 - inset.y / radius), 1, 1, 1);
            half.Geometry = node;
        }

        private bool TryGetPolygonCapPlan(ManualMedian median, out EndpointStylePlan plan)
        {
            plan = null;
            if (!TryResolvePolygon(median, out var source, out _) || !median.Attached || median.Sections == null || median.Sections.Count == 0) return false;
            bool any = false;
            for (int i = 0; i < 256; i++)
            {
                var key = new RoadGrassMedianAnchorKey(source.Key.Edge, source.Key.End, -(median.Saved.Id * 256 + i));
                if (!m_PolygonRenderKeys.Contains(key)) break;
                if (!IsTaperMeshReady(key)) return false;
                any = true;
            }
            return any && TryPlanEndpoint(source, MedianSavedCap.None, out plan, true);
        }

        internal bool PlacePolygon(RoadGrassResolvedMedianAnchor anchor, IReadOnlyList<float3> points, MedianSavedSurface surface, IReadOnlyList<bool> snapped = null)
        {
            if (!TryPolygonPreview(anchor, points, surface, out _)) return false;
            int id = 1;
            foreach (int existing in m_ManualMedians.Keys) id = math.max(id, existing + 1);
            if (id >= int.MaxValue / 256) { ManualMessage = "Too many saved medians"; return false; }
            var saved = new RoadGrassSavedManualMedian { Id = id, Node = anchor.Node, Edge = anchor.Key.Edge, Anchor = anchor.Data, Surface = surface };
            var median = new ManualMedian { Saved = saved };
            float3 origin = EntityManager.GetComponentData<Node>(anchor.Node).m_Position;
            for (int i = 0; i < points.Count; i++)
            {
                median.Points.Add(points[i] - origin);
                median.References.Add(CapturePolygonReference(id, anchor, points[i], snapped != null && snapped[i]));
            }
            Entity city = m_CityQuery.GetSingletonEntity();
            if (!EntityManager.HasBuffer<RoadGrassSavedManualMedian>(city)) EntityManager.AddBuffer<RoadGrassSavedManualMedian>(city);
            if (!EntityManager.HasBuffer<RoadGrassSavedManualMedianVertex>(city)) EntityManager.AddBuffer<RoadGrassSavedManualMedianVertex>(city);
            EntityManager.GetBuffer<RoadGrassSavedManualMedian>(city).Add(saved);
            var vertices = EntityManager.GetBuffer<RoadGrassSavedManualMedianVertex>(city);
            foreach (var vertex in median.References) vertices.Add(vertex);
            m_ManualMedians.Add(id, median);
            SelectedManualMedianId = id;
            ManualMessage = "";
            ClearTaperPlans(); m_Dirty = true;
            return true;
        }

        private bool TryResolvePolygon(ManualMedian median, out RoadGrassResolvedMedianAnchor anchor, out List<float3> points)
        {
            points = new List<float3>();
            anchor = null;
            if (median.Points.Count < 3 || median.Points.Count > 128 ||
                !TryResolveSavedAnchor(median.Saved.Edge, median.Saved.Anchor, out anchor) || anchor.Node != median.Saved.Node ||
                !EntityManager.TryGetComponent(anchor.Node, out Node node)) return false;
            for (int i = 0; i < median.Points.Count; i++)
            {
                var p = median.Points[i];
                if (!math.all(math.isfinite(p))) return false;
                if (median.References.Count == median.Points.Count)
                {
                    // Guides assist placement; losing an optional guide must not erase the polygon.
                    // Parent median loss is handled separately by SynchronizeManualMedians.
                    points.Add(ResolvePolygonReference(anchor, median.References[i], out var resolved) ? resolved : node.m_Position + p);
                }
                else points.Add(node.m_Position + p);
            }
            return true;
        }

        internal IEnumerable<EdgeGeometry> GetManualOutlines(int id)
        {
            if (!m_ManualMedians.TryGetValue(id, out var median)) yield break;
            if (median.Points.Count == 2)
            {
                if (GetManualOutline(id, out var outline)) yield return outline;
                yield break;
            }
            for (int i = 0; i < 256; i++)
            {
                var key = new RoadGrassMedianAnchorKey(median.Saved.Edge, median.Saved.Anchor.End, -(id * 256 + i));
                if (!m_TaperPlans.TryGetValue(key, out var plan) || plan.Half == null) yield break;
                yield return BuildEdgeGeometry(plan.Half.Geometry.m_Left.m_Left, plan.Half.Geometry.m_Right.m_Right);
            }
        }

        private void SynchronizePolygon(int id, ManualMedian median)
        {
            // Shape edits can reduce the section count to zero; old plans must not remain pickable.
            for (int i = 0; i < 256; i++)
                m_TaperPlans.Remove(new RoadGrassMedianAnchorKey(median.Saved.Edge, median.Saved.Anchor.End, -(id * 256 + i)));
            if (!EnsurePolygonPlan(median, out var source)) return;
            if (median.Halves == null && !BindPolygonSurface(source, median.Sections, median.Saved.Surface, out median.Halves, out var error))
            { if (id == SelectedManualMedianId) ManualMessage = error; return; }
            var halves = median.Halves;
            for (int i = 0; i < halves.Count; i++)
            {
                var half = halves[i];
                var original = half.Source;
                var key = new RoadGrassMedianAnchorKey(source.Key.Edge, source.Key.End, -(id * 256 + i));
                half.Source = new RoadGrassResolvedMedianAnchor
                {
                    Key = key, Node = original.Node, Data = original.Data, SourceComposition = original.SourceComposition,
                    RoadComposition = original.RoadComposition, StandaloneComposition = original.StandaloneComposition,
                    Pieces = original.Pieces, Center = original.Center, Width = original.Width,
                    Left = original.Left, Right = original.Right, Signature = original.Signature
                };
                if (m_Variants.TryGetValue(key, out var existing) && existing.Transition != null &&
                    existing.Transition.Geometry.Equals(half.Geometry) && CompositionDataEquals(existing.Transition.Composition, half.Composition) &&
                    CompositionPiecesEqual(existing.Transition.Pieces, half.Pieces)) half = existing.Transition;
                m_PolygonRenderKeys.Add(key);
                m_TaperPlans[key] = new TaperPlan { Source = half.Source, Half = half, Manual = true };
                m_RenderPairs[key] = key; m_Resolved[key] = half.Source;
                BatchDataHelpers.CalculateNodeParameters(half.Geometry, half.Composition, out var parameters);
                m_RenderStates[key] = new MedianRenderState(key, parameters, half.Geometry.m_Bounds);
            }
        }
    }

    internal static class RoadGrassPolygonSections
    {
        internal struct Section { internal Bezier4x3 Left, Right; }

        internal static bool RayHit(Segment strip, Line3.Segment ray, out float hit)
        {
            hit = float.MaxValue;
            float3 left = strip.m_Left.a, right = strip.m_Right.a;
            for (int i = 1; i <= 16; i++)
            {
                float3 nextLeft = MathUtils.Position(strip.m_Left, i / 16f), nextRight = MathUtils.Position(strip.m_Right, i / 16f);
                if (RayTriangle(ray, left, right, nextLeft, out float a)) hit = math.min(hit, a);
                if (RayTriangle(ray, right, nextRight, nextLeft, out float b)) hit = math.min(hit, b);
                left = nextLeft; right = nextRight;
            }
            return hit < float.MaxValue;
        }

        private static bool RayTriangle(Line3.Segment ray, float3 a, float3 b, float3 c, out float t)
        {
            t = 0;
            float3 direction = ray.b - ray.a, e = b - a, f = c - a;
            float3 p = math.cross(direction, f);
            float determinant = math.dot(e, p);
            if (math.abs(determinant) < .000001f) return false;
            float3 offset = ray.a - a;
            float u = math.dot(offset, p) / determinant;
            float3 q = math.cross(offset, e);
            float v = math.dot(direction, q) / determinant;
            t = math.dot(f, q) / determinant;
            return u >= -.0001f && v >= -.0001f && u + v <= 1.0001f && t >= 0 && t <= 1;
        }

        internal static float InteriorEnd(Bezier4x3 left, Bezier4x3 right, float2 curb)
        {
            bool Fits(float t)
            {
                float2 l = MathUtils.Position(left, t).xz, r = MathUtils.Position(right, t).xz;
                float2 middle = (l + r) * .5f;
                float2 lt = math.normalizesafe(MathUtils.Tangent(left, t).xz);
                float2 rt = math.normalizesafe(MathUtils.Tangent(right, t).xz);
                return math.abs(Cross(middle - l, lt)) >= curb.x && math.abs(Cross(middle - r, rt)) >= curb.y;
            }
            if (Fits(1)) return 1;
            float a = 0, b = 1;
            for (int i = 0; i < 24; i++) { float t = (a + b) * .5f; if (Fits(t)) a = t; else b = t; }
            return a;
        }

        // Split at vertex heights: each native section has two straight polygon boundary
        // edges and open cross-sections. Concave polygons can have multiple spans per slice.
        internal static bool TryBuild(IReadOnlyList<float3> points, out List<Section> sections, out string reason, float2? attachmentForward = null)
        {
            sections = new List<Section>(); reason = "Polygon needs at least three corners (128 outline samples maximum)";
            int count = points.Count;
            if (count < 3 || count > 128) return false;
            reason = "Polygon contains a non-finite position";
            for (int i = 0; i < count; i++)
            {
                float2 a = points[i].xz, b = points[(i + 1) % count].xz;
                if (!math.all(math.isfinite(points[i]))) return false;
            }
            float2 forward = default, across = default;
            var heights = new float[count];
            bool found = false;
            if (attachmentForward.HasValue)
            {
                forward = attachmentForward.Value; across = new float2(forward.y, -forward.x);
                float minimum = float.MaxValue;
                for (int i = 0; i < count; i++)
                { heights[i] = math.dot(points[i].xz - points[0].xz, forward); minimum = math.min(minimum, heights[i]); }
                found = true;
            }
            for (int attempt = 0; attempt < 64 && !found; attempt++)
            {
                float angle = .137f + attempt * 2.3999632f;
                forward = new float2(math.cos(angle), math.sin(angle));
                across = new float2(forward.y, -forward.x);
                found = true;
                for (int i = 0; i < count; i++)
                {
                    heights[i] = math.dot(points[i].xz - points[0].xz, forward);
                    for (int j = 0; j < i; j++) if (math.abs(heights[i] - heights[j]) < .01f) found = false;
                }
            }
            var levels = new List<float>(heights);
            // Crossed outlines use even-odd filling. Split where boundaries exchange order.
            for (int i = 0; i < count; i++) for (int j = i + 1; j < count; j++)
            {
                float2 a = points[i].xz, u = points[(i + 1) % count].xz - a;
                float2 b = points[j].xz, v = points[(j + 1) % count].xz - b;
                float determinant = Cross(u, v);
                if (math.abs(determinant) < .000001f) continue;
                float t = Cross(b - a, v) / determinant, s = Cross(b - a, u) / determinant;
                if (t > 0 && t < 1 && s > 0 && s < 1) levels.Add(math.dot(a + u * t - points[0].xz, forward));
            }
            levels.Sort();
            for (int i = levels.Count - 1; i > 0; i--) if (math.abs(levels[i] - levels[i-1]) < .0001f) levels.RemoveAt(i);
            for (int slice = 0; slice < levels.Count - 1; slice++)
            {
                float low = levels[slice], high = levels[slice + 1], middle = (low + high) * .5f;
                var edges = new List<int>();
                for (int i = 0; i < count; i++)
                    if (middle > math.min(heights[i], heights[(i + 1) % count]) && middle < math.max(heights[i], heights[(i + 1) % count])) edges.Add(i);
                edges.Sort((a, b) => math.dot(At(a, middle).xz, across).CompareTo(math.dot(At(b, middle).xz, across)));
                if ((edges.Count & 1) != 0) return false;
                for (int i = 0; i < edges.Count; i += 2)
                {
                    float3 la = At(edges[i], low), ld = At(edges[i], high);
                    float3 ra = At(edges[i + 1], low), rd = At(edges[i + 1], high);
                    // Start at the wider end so a triangular slice uses the native taper tip.
                    if (math.distance(la, ra) < math.distance(ld, rd))
                    { var a = la; var b = ra; la = rd; ra = ld; ld = b; rd = a; }
                    sections.Add(new Section { Left = Line(la, ld), Right = Line(ra, rd) });
                    if (sections.Count >= 256) { reason = "Polygon needs too many native sections"; return false; }
                }
            }
            reason = ""; return sections.Count > 0;

            float3 At(int edge, float height)
            {
                int next = (edge + 1) % count;
                float t = math.saturate((height - heights[edge]) / (heights[next] - heights[edge]));
                return math.lerp(points[edge], points[next], t);
            }
        }

        private static Bezier4x3 Line(float3 a, float3 b) => new Bezier4x3(a, math.lerp(a, b, 1f / 3), math.lerp(a, b, 2f / 3), b);
        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
        private static bool Crosses(float2 a, float2 b, float2 c, float2 d)
        {
            if (math.any(math.max(a, b) < math.min(c, d)) || math.any(math.max(c, d) < math.min(a, b))) return false;
            return Cross(b - a, c - a) * Cross(b - a, d - a) <= 0 && Cross(d - c, a - c) * Cross(d - c, b - c) <= 0;
        }
    }
}
