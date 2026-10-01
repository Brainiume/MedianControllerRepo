using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using CarLane = Game.Net.CarLane;
using PedestrianLane = Game.Net.PedestrianLane;
using TrackLane = Game.Net.TrackLane;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private sealed class TaperPlan
        {
            internal RoadGrassResolvedMedianAnchor Source;
            internal Entity NodeComposition;
            internal EdgeGeometry Edge;
            internal EdgeNodeGeometry Node;
            internal RoadGrassTransitionMesh.Half Half;
            internal bool? Clear;
            internal bool Manual;
            internal float3 Destination;
        }

        // Self entries exist only in the render map, never in saved connection pairs.
        private readonly Dictionary<RoadGrassMedianAnchorKey, RoadGrassMedianAnchorKey> m_RenderPairs = new();
        private readonly Dictionary<RoadGrassMedianAnchorKey, TaperPlan> m_TaperPlans = new();
        private readonly HashSet<Entity> m_TaperDependencies = new();
        internal bool HasTaperPlans => m_TaperPlans.Count != 0 || m_TaperSnapPoints.Count != 0 || m_ManualMedians.Count != 0;

        internal bool TryBuildTaperPreview(RoadGrassResolvedMedianAnchor anchor, out EdgeGeometry geometry)
        {
            geometry = default;
            if (!TryPlanIntersectionTaper(anchor, out var half) || !TryPlanEndpoint(anchor, MedianSavedCap.None, out _, true)) return false;
            geometry = BuildEdgeGeometry(half.Geometry.m_Left.m_Left, half.Geometry.m_Right.m_Right);
            return true;
        }

        private bool IsTaperMeshReady(RoadGrassMedianAnchorKey key)
        {
            if (!m_Variants.TryGetValue(key, out var state) || state.Transition == null ||
                !EntityManager.TryGetComponent(state.MedianComposition, out NetCompositionMeshRef mesh) ||
                !EntityManager.Exists(mesh.m_Mesh) || !EntityManager.HasBuffer<BatchGroup>(mesh.m_Mesh) ||
                EntityManager.GetBuffer<BatchGroup>(mesh.m_Mesh, true).Length == 0) return false;
            return !EntityManager.TryGetComponent(GetOriginalEndpoint(key), out NetCompositionMeshRef original) || original.m_Mesh != mesh.m_Mesh;
        }

        private void ClearTaperPlans()
        {
            m_PolygonPlanEpoch++;
            m_PolygonReferenceGuides.Clear();
            m_ManualPreviewSet = false;
            m_TaperSnapPoints.Clear();
            foreach (var builder in m_TaperSnapBuilders.Values) builder.Dispose();
            m_TaperSnapBuilders.Clear();
            m_PolygonPreviewPoints = null;
            m_PolygonGuides.Clear(); m_PolygonGuideEdges.Clear();
            m_PolygonRoadSurfaces.Clear();
            m_MarkingGuides.Clear();
            m_TaperPlans.Clear();
            m_TaperDependencies.Clear();
        }

        internal void InvalidateTaperGeometry(NativeArray<Entity> changed)
        {
            foreach (var entity in changed)
                if (m_TaperDependencies.Contains(entity) ||
                    (EntityManager.TryGetComponent(entity, out Owner owner) && m_TaperDependencies.Contains(owner.m_Owner)))
                {
                    ClearTaperPlans();
                    m_Dirty = true;
                    return;
                }
        }

        private bool TryPlanIntersectionTaper(RoadGrassResolvedMedianAnchor anchor,
            out RoadGrassTransitionMesh.Half half)
        {
            if (anchor != null && m_Styles.TryGetValue(anchor.Key,out var manual) && manual.HasTaperEnd)
                return TryPlanManualTaper(anchor,manual,out half);
            if (!TryPlanTaperGeometry(anchor, out half)) return false;
            var plan = m_TaperPlans[anchor.Key];
            if (plan.Clear.HasValue) return plan.Clear.Value;
            plan.Clear = false;
            foreach (var style in m_Styles.Values)
            {
                if (!style.ExtendIntoIntersection || style.Node != anchor.Node || style.Key.Equals(anchor.Key)) continue;
                if (TryResolveSavedAnchor(style.Edge, style.Anchor, out var other) &&
                    TryPlanTaperGeometry(other, out var otherHalf) && RoadGrassTaperGeometry.Overlaps(half.Geometry, otherHalf.Geometry)) return false;
            }
            foreach (var pair in m_ActivePairs)
            {
                if (!m_Resolved.TryGetValue(pair.Key, out var a) || a.Node != anchor.Node ||
                    !m_Resolved.TryGetValue(pair.Value, out var b)) continue;
                if (!TryBuildPairGeometry(a,b,out var ga,out var gb)) return false;
                var other = RoadGrassTransitionMesh.Create(a,ga,float2.zero,float2.zero);
                if (RoadGrassTaperGeometry.Overlaps(half.Geometry,other.Geometry)) return false;
            }
            plan.Clear = true;
            return true;
        }

        private bool TryPlanTaperGeometry(RoadGrassResolvedMedianAnchor anchor,
            out RoadGrassTransitionMesh.Half half)
        {
            if (anchor != null && m_Styles.TryGetValue(anchor.Key,out var manual) && manual.HasTaperEnd)
                return TryPlanManualTaper(anchor,manual,out half);
            half = null;
            if (anchor == null || !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle ||
                m_ActivePairs.ContainsKey(anchor.Key) || !EntityManager.Exists(anchor.Node) ||
                EntityManager.HasComponent<Deleted>(anchor.Node) || EntityManager.HasComponent<Game.Tools.Temp>(anchor.Key.Edge) ||
                !EntityManager.TryGetComponent(anchor.Key.Edge, out EdgeGeometry edge)) return false;
            Entity source = GetOriginalEndpoint(anchor.Key);
            if (!EntityManager.TryGetComponent(source, out NetCompositionData data)) return false;
            const CompositionFlags.General unsupported = CompositionFlags.General.Roundabout | CompositionFlags.General.Elevated | CompositionFlags.General.Tunnel;
            if ((data.m_Flags.m_General & CompositionFlags.General.Intersection) == 0 ||
                (data.m_Flags.m_General & unsupported) != 0) return false;
            EdgeNodeGeometry node;
            if (anchor.Key.End && EntityManager.TryGetComponent(anchor.Key.Edge, out EndNodeGeometry end)) node = end.m_Geometry;
            else if (!anchor.Key.End && EntityManager.TryGetComponent(anchor.Key.Edge, out StartNodeGeometry start)) node = start.m_Geometry;
            else return false;
            anchor = GetStyledContinuation(anchor);
            if (m_TaperPlans.TryGetValue(anchor.Key, out var cached) && cached.NodeComposition == source &&
                cached.Source.SourceComposition == anchor.SourceComposition && cached.Edge.Equals(edge) && cached.Node.Equals(node) &&
                CompositionDataEquals(cached.Source.StandaloneComposition, anchor.StandaloneComposition) &&
                CompositionPiecesEqual(cached.Source.Pieces, anchor.Pieces))
            {
                half = cached.Half;
                return half != null;
            }
            // Preview failures are cached too. Only road/node/lane events discard them.
            var plan = new TaperPlan { Source = anchor, NodeComposition = source, Edge = edge, Node = node };
            m_TaperPlans[anchor.Key] = plan;
            m_TaperDependencies.Add(anchor.Key.Edge);
            m_TaperDependencies.Add(anchor.Node);
            if (!RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var approach) ||
                !EntityManager.HasBuffer<ConnectedEdge>(anchor.Node)) return false;

            var lanes = new List<RoadGrassTaperGeometry.Lane>();
            var seen = new HashSet<Entity>();
            bool hasCars = false;
            bool AddLanes(Entity owner)
            {
                m_TaperDependencies.Add(owner);
                if (!EntityManager.HasBuffer<SubLane>(owner)) return false;
                foreach (var item in EntityManager.GetBuffer<SubLane>(owner, true))
                {
                    Entity lane = item.m_SubLane;
                    m_TaperDependencies.Add(lane);
                    if (!seen.Add(lane) || EntityManager.HasComponent<Deleted>(lane) || EntityManager.HasComponent<Game.Tools.Temp>(lane)) continue;
                    bool car = EntityManager.HasComponent<CarLane>(lane);
                    if (!car && !EntityManager.HasComponent<PedestrianLane>(lane) && !EntityManager.HasComponent<TrackLane>(lane)) continue;
                    if (!EntityManager.TryGetComponent(lane, out Game.Net.Curve curve) ||
                        !EntityManager.TryGetComponent(lane, out PrefabRef prefab) ||
                        !EntityManager.TryGetComponent(prefab.m_Prefab, out NetLaneData laneData)) return false;
                    hasCars |= car;
                    lanes.Add(new RoadGrassTaperGeometry.Lane { Curve = curve.m_Bezier, Width = laneData.m_Width });
                }
                return true;
            }
            if (!AddLanes(anchor.Node)) return false;
            foreach (var connected in EntityManager.GetBuffer<ConnectedEdge>(anchor.Node, true))
            {
                Entity road = connected.m_Edge;
                if (EntityManager.HasComponent<Deleted>(road) || EntityManager.HasComponent<Game.Tools.Temp>(road) ||
                    !EntityManager.TryGetComponent(road, out Edge roadData) || !AddLanes(road)) return false;
                // Other median approaches are obstacles as well as the live lanes.
                if (TryGetSelectableAnchors(road, roadData.m_End == anchor.Node, out var medians))
                    foreach (var median in medians)
                        if (!median.Key.Equals(anchor.Key) && RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, median, out var strip))
                            lanes.Add(new RoadGrassTaperGeometry.Lane { Curve = MathUtils.Lerp(strip.m_Left, strip.m_Right, 0.5f), Width = median.Width });
            }
            if (!hasCars || !RoadGrassTaperGeometry.TryBuild(approach, node, lanes, out var geometry, out _)) return false;
            var borders = RoadGrassTransitionMesh.ReadBorderWidths(EntityManager, anchor);
            half = plan.Half = RoadGrassTransitionMesh.Create(anchor, geometry, borders, float2.zero);
            return true;
        }

        private void SynchronizeIntersectionTapers()
        {
            m_RenderPairs.Clear();
            foreach (var pair in m_ActivePairs) m_RenderPairs.Add(pair.Key, pair.Value);
            foreach (var style in m_Styles.Values)
            {
                if (!style.ExtendIntoIntersection || !TryResolveSavedAnchor(style.Edge, style.Anchor, out var anchor) ||
                    anchor.Node != style.Node || !TryPlanIntersectionTaper(anchor, out var half) ||
                    !TryPlanEndpoint(anchor, MedianSavedCap.None, out _, true)) continue;
                m_RenderPairs[anchor.Key] = anchor.Key;
                m_Resolved[anchor.Key] = half.Source;
                BatchDataHelpers.CalculateNodeParameters(half.Geometry, half.Composition, out var parameters);
                m_RenderStates[anchor.Key] = new MedianRenderState(anchor.Key, parameters, half.Geometry.m_Bounds);
            }
        }
    }
}
