using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
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
