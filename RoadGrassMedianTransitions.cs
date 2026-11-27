using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Entities;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using SubLane = Game.Net.SubLane;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private sealed class TransitionPair
        {
            internal RoadGrassResolvedMedianAnchor SourceA, SourceB;
            internal Entity NodeA, NodeB;
            internal EdgeGeometry GeometryA, GeometryB;
            internal RoadGrassTransitionMesh.Half A, B;
        }

        private readonly Dictionary<(RoadGrassMedianAnchorKey, RoadGrassMedianAnchorKey), TransitionPair>
            m_TransitionPlans = new();

        private List<RoadGrassTaperGeometry.Lane> GetConnectionCrosswalks(Entity node)
        {
            var result = new List<RoadGrassTaperGeometry.Lane>();
            var seen = new HashSet<Entity>();
            void Read(Entity owner)
            {
                if (!EntityManager.HasBuffer<SubLane>(owner)) return;
                foreach (var item in EntityManager.GetBuffer<SubLane>(owner, true))
                {
                    var lane = item.m_SubLane;
                    if (!seen.Add(lane) || EntityManager.HasComponent<Game.Common.Deleted>(lane) ||
                        EntityManager.HasComponent<Game.Tools.Temp>(lane) ||
                        !EntityManager.TryGetComponent(lane, out Game.Net.PedestrianLane pedestrian) ||
                        (pedestrian.m_Flags & Game.Net.PedestrianLaneFlags.Crosswalk) == 0 ||
                        !EntityManager.TryGetComponent(lane, out Game.Net.Curve curve) ||
                        !EntityManager.TryGetComponent(lane, out PrefabRef prefab) ||
                        !EntityManager.TryGetComponent(prefab.m_Prefab, out NetLaneData data) ||
                        !math.isfinite(data.m_Width) || !math.all(math.isfinite(curve.m_Bezier.a)) ||
                        !math.all(math.isfinite(curve.m_Bezier.b)) || !math.all(math.isfinite(curve.m_Bezier.c)) ||
                        !math.all(math.isfinite(curve.m_Bezier.d))) continue;
                    result.Add(new RoadGrassTaperGeometry.Lane { Curve = curve.m_Bezier, Width = data.m_Width });
                }
            }
            Read(node);
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
                foreach (var edge in EntityManager.GetBuffer<ConnectedEdge>(node, true)) Read(edge.m_Edge);
            return result;
        }

        private List<MedianRenderState> CrosswalkRenderSpans(RoadGrassResolvedMedianAnchor source,
            RoadGrassMedianAnchorKey paired, EdgeGeometry geometry, List<RoadGrassTaperGeometry.Lane> crosswalks,
            RoadGrassTransitionMesh.Half transition)
        {
            if (crosswalks.Count == 0) return null;
            var ranges = RoadGrassTransitionMesh.CrosswalkRanges(geometry, crosswalks);
            if (ranges.Count == 1 && math.all(ranges[0] == new float2(0, 1))) return null;
            ranges = CapCrosswalkSpans(transition?.Source ?? GetStyledContinuation(source), geometry, ranges);
            var result = new List<MedianRenderState>();
            foreach (var range in ranges)
            {
                var piece = RoadGrassTransitionMesh.Slice(geometry, range);
                BatchDataHelpers.CompositionParameters parameters;
                if (transition == null) BatchDataHelpers.CalculateEdgeParameters(piece, false, out parameters);
                else
                {
                    float2 border = RoadGrassTransitionMesh.ReadBorderWidths(EntityManager, transition.Source);
                    var half = RoadGrassTransitionMesh.Create(transition.Source, piece, border, border);
                    if (range.y == 1)
                    {
                        half.Geometry.m_SyncVertexTargetsLeft = transition.Geometry.m_SyncVertexTargetsLeft;
                        half.Geometry.m_SyncVertexTargetsRight = transition.Geometry.m_SyncVertexTargetsRight;
                    }
                    BatchDataHelpers.CalculateNodeParameters(half.Geometry, transition.Composition, out parameters);
                }
                result.Add(new MedianRenderState(paired, parameters, piece.m_Bounds));
            }
            return result;
        }

        private static bool RenderSpansEqual(List<MedianRenderState> a, List<MedianRenderState> b)
        {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Parameters.Equals(b[i].Parameters) || !a[i].Bounds.Equals(b[i].Bounds)) return false;
            return true;
        }

        private static bool CrosswalksEqual(List<RoadGrassTaperGeometry.Lane> a, List<RoadGrassTaperGeometry.Lane> b)
        {
            if (a == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!a[i].Curve.Equals(b[i].Curve) || a[i].Width != b[i].Width) return false;
            return true;
        }

        internal void RefreshCrosswalkClearance(Entity edge = default)
        {
            foreach (var key in new List<RoadGrassMedianAnchorKey>(m_RenderStates.Keys))
            {
                var state = m_RenderStates[key];
                if (key.Equals(state.Paired) || (edge != Entity.Null && key.Edge != edge && state.Paired.Edge != edge) ||
                    !state.ConnectionGeometry.HasValue || !m_RenderStates.TryGetValue(state.Paired, out var opposite) ||
                    !opposite.ConnectionGeometry.HasValue || !m_Resolved.TryGetValue(key, out var a) ||
                    !m_Resolved.TryGetValue(state.Paired, out var b)) continue;
                CachePairGeometry(a, b, state.ConnectionGeometry.Value, opposite.ConnectionGeometry.Value);
            }
        }

        private ulong LinkedPairId(RoadGrassMedianAnchorKey key)
        {
            if (EntityManager.HasBuffer<RoadGrassMedianPieceLink>(key.Edge))
                foreach (var link in EntityManager.GetBuffer<RoadGrassMedianPieceLink>(key.Edge, true))
                    if (link.Local.End == key.End && link.Local.Ordinal == key.Ordinal) return link.PairId;
            return 0;
        }
        private bool HasCrosswalkGaps(RoadGrassMedianAnchorKey key) => m_CrosswalkGapPairs.Contains(LinkedPairId(key));
        internal void SetLinkedCrosswalkGaps(RoadGrassMedianAnchorKey key, bool value)
        {
            ulong id = LinkedPairId(key);
            if (id == 0 || !TryGetPersistentPairs(false, out var pairs)) return;
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].PairId != id) continue;
                var pair = pairs[i];
                if (pair.CrosswalkGaps == value) return;
                pair.CrosswalkGaps = value; pairs[i] = pair;
                if (value) m_CrosswalkGapPairs.Add(id); else m_CrosswalkGapPairs.Remove(id);
                RefreshCrosswalkClearance(key.Edge);
                m_Dirty = true;
                return;
            }
        }

        internal void RefreshTransitionCalibration()
        {
            m_TransitionPlans.Clear();
            m_RenderStates.Clear();
            m_Dirty = true;
            AdvanceRenderRevision();
        }

        private void InvalidateTransitions(Entity edge)
        {
            var remove = new List<(RoadGrassMedianAnchorKey, RoadGrassMedianAnchorKey)>();
            foreach (var key in m_TransitionPlans.Keys)
                if (key.Item1.Edge == edge || key.Item2.Edge == edge) remove.Add(key);
            foreach (var key in remove) m_TransitionPlans.Remove(key);
        }

        private void PruneTransitionPlans()
        {
            if (m_TransitionPlans.Count <= 32) return;
            var remove = new List<(RoadGrassMedianAnchorKey, RoadGrassMedianAnchorKey)>();
            foreach (var entry in m_TransitionPlans.Keys)
                if (!m_ActivePairs.TryGetValue(entry.Item1, out var paired) || !paired.Equals(entry.Item2)) remove.Add(entry);
            foreach (var entry in remove) m_TransitionPlans.Remove(entry);
        }

        private bool TryPlanTransition(RoadGrassResolvedMedianAnchor a, RoadGrassResolvedMedianAnchor b,
            EdgeGeometry ga, EdgeGeometry gb, out TransitionPair plan)
        {
            a = GetStyledContinuation(a);
            b = GetStyledContinuation(b);
            var key = (a.Key, b.Key);
            Entity na = GetOriginalEndpoint(a.Key), nb = GetOriginalEndpoint(b.Key);
            if (m_TransitionPlans.TryGetValue(key, out plan) && plan.NodeA == na && plan.NodeB == nb &&
                plan.SourceA.SourceComposition == a.SourceComposition && plan.SourceB.SourceComposition == b.SourceComposition &&
                plan.GeometryA.Equals(ga) && plan.GeometryB.Equals(gb) &&
                CompositionDataEquals(plan.SourceA.StandaloneComposition, a.StandaloneComposition) &&
                CompositionDataEquals(plan.SourceB.StandaloneComposition, b.StandaloneComposition) &&
                CompositionPiecesEqual(plan.SourceA.Pieces, a.Pieces) && CompositionPiecesEqual(plan.SourceB.Pieces, b.Pieces))
                return true;
            m_TransitionPlans.Remove(key);
            m_TransitionPlans.Remove((b.Key, a.Key));
            plan = null;
            float2 bordersA = RoadGrassTransitionMesh.ReadBorderWidths(EntityManager, a);
            float2 bordersB = RoadGrassTransitionMesh.ReadBorderWidths(EntityManager, b);
            float2 seamBorders = RoadGrassTransitionMesh.MatchSeamBorders(bordersA, bordersB.yx);
            float seamWidth = math.min(math.distance(ga.m_End.m_Left.d, ga.m_End.m_Right.d),
                math.distance(gb.m_End.m_Left.d, gb.m_End.m_Right.d));
            if (!math.isfinite(seamWidth) || seamWidth <= math.csum(seamBorders) + 0.01f) return false;
            var ha = RoadGrassTransitionMesh.Create(a, ga, bordersA, seamBorders);
            var hb = RoadGrassTransitionMesh.Create(b, gb, bordersB, seamBorders.yx);
            plan = new TransitionPair { SourceA = a, SourceB = b, NodeA = na, NodeB = nb,
                GeometryA = ga, GeometryB = gb, A = ha, B = hb };
            m_TransitionPlans[key] = plan;
            m_TransitionPlans[(b.Key, a.Key)] = new TransitionPair { SourceA = b, SourceB = a,
                NodeA = nb, NodeB = na, GeometryA = gb, GeometryB = ga, A = hb, B = ha };
            return true;
        }

        private RoadGrassTransitionMesh.Half GetTransitionHalf(RoadGrassMedianAnchorKey key) =>
            m_ActivePairs.TryGetValue(key, out var paired) && m_TransitionPlans.TryGetValue((key, paired), out var plan)
                ? plan.A : null;

        private RoadGrassResolvedMedianAnchor CreateTransitionAnchor(RoadGrassTransitionMesh.Half half)
        {
            var source = half.Source;
            return new RoadGrassResolvedMedianAnchor { Key = source.Key, Node = source.Node, Data = source.Data,
                SourceComposition = source.SourceComposition, RoadComposition = source.RoadComposition,
                StandaloneComposition = half.Composition, Pieces = half.Pieces, Center = source.Center, Width = source.Width,
                Left = source.Left, Right = source.Right, Signature = source.Signature };
        }
    }
}
