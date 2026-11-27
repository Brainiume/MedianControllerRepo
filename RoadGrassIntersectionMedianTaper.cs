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
        private readonly Dictionary<Entity, EdgeRuntimeStamp> m_TaperDependencyStamps = new();
        internal bool HasTaperPlans => m_TaperPlans.Count != 0 || m_TaperSnapPoints.Count != 0 || m_ManualMedians.Count != 0;
        internal int TaperDependencyRevision { get; private set; }
        internal int TaperDependencyCount => m_TaperDependencies.Count;
        internal IEnumerable<Entity> TaperDependencies => m_TaperDependencies;

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

        private void ClearTaperPlans(bool geometryChanged = true)
        {
            m_ManualPreviewSet = false;
            m_PolygonPreviewPoints = null;
            m_TaperPlans.Clear();
            // Adding/editing a median does not change road geometry. Keep the
            // existing polygons' sections, profiles and renderer resources.
            if (!geometryChanged) return;
            m_PolygonPlanEpoch++;
            ClearRoadGuideCaches();
            m_PolygonProfiles.Clear();
            m_TaperDependencies.Clear();
            TaperDependencyRevision++;
            m_TaperDependencyStamps.Clear();
        }

        private int m_PolygonGuideEpoch;
        private void ClearRoadGuideCaches()
        {
            m_PolygonGuideEpoch++;
            m_PolygonReferenceGuides.Clear();
            m_TaperSnapPoints.Clear(); m_ContourGraphs.Clear();
            foreach (var builder in m_TaperSnapBuilders.Values) builder.Dispose();
            m_TaperSnapBuilders.Clear();
            m_PolygonGuides.Clear(); m_PolygonGuideEdges.Clear();
            m_PolygonRoadSurfaces.Clear();
            m_MarkingGuides.Clear();
        }

        private void TrackTaperDependency(Entity entity)
        {
            if (!EntityManager.HasComponent<Edge>(entity) && !EntityManager.HasComponent<Node>(entity) &&
                EntityManager.TryGetComponent(entity, out Owner owner)) entity = owner.m_Owner;
            if (m_TaperDependencies.Add(entity)) TaperDependencyRevision++;
            if (!m_TaperDependencyStamps.ContainsKey(entity) && TryCaptureTaperStamp(entity, out var stamp))
                m_TaperDependencyStamps[entity] = stamp;
        }

        private bool TryCaptureTaperStamp(Entity entity, out EdgeRuntimeStamp stamp)
        {
            stamp = default;
            if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity)) return false;
            uint hash = 2166136261u;
            Entity start = Entity.Null, end = Entity.Null;
            if (EntityManager.HasComponent<Edge>(entity))
            {
                if (!TryCaptureEdgeStamp(entity, out var edge)) return false;
                start = edge.Start; end = edge.End; hash = edge.Geometry;
            }
            else if (EntityManager.TryGetComponent(entity, out Node node))
            {
                AccumulateStamp(ref hash, node.m_Position);
                uint edges = 0;
                if (EntityManager.HasBuffer<ConnectedEdge>(entity))
                    foreach (var connected in EntityManager.GetBuffer<ConnectedEdge>(entity, true))
                        edges += (uint)connected.m_Edge.GetHashCode();
                hash = (hash ^ edges) * 16777619u;
            }
            else return false;
            if (EntityManager.TryGetComponent(entity, out PrefabRef prefab))
                hash = (hash ^ (uint)prefab.m_Prefab.GetHashCode()) * 16777619u;
            // Generated lanes/junction cutbacks can change when traffic lights are edited.
            // They must not delete generated medians or trigger polygon remeshing.
            stamp = new EdgeRuntimeStamp(start, end, hash);
            return true;
        }

        internal void InvalidateTaperGeometry(NativeArray<Entity> changed)
        {
            var checkedOwners = new HashSet<Entity>();
            foreach (var entity in changed)
            {
                Entity dependency = entity;
                if (!m_TaperDependencies.Contains(dependency) && EntityManager.TryGetComponent(entity, out Owner owner))
                    dependency = owner.m_Owner;
                if (!m_TaperDependencies.Contains(dependency) || !checkedOwners.Add(dependency)) continue;
                bool deleted = !EntityManager.Exists(dependency) || EntityManager.HasComponent<Deleted>(dependency);
                if (deleted || (TryCaptureTaperStamp(dependency, out var current) &&
                    (!m_TaperDependencyStamps.TryGetValue(dependency, out var saved) || !saved.Equals(current))))
                {
                    RemoveGeneratedMediansForGeometry(dependency);
                    if (deleted)
                    {
                        if (m_TaperDependencies.Remove(dependency)) TaperDependencyRevision++;
                        m_TaperDependencyStamps.Remove(dependency);
                    }
                    else if (TryCaptureTaperStamp(dependency, out var updated))
                        m_TaperDependencyStamps[dependency] = updated;
                }
            }
        }

        private void RemoveGeneratedMediansForGeometry(Entity dependency)
        {
            if (RoadGrassSettings.AutomaticMedianDeletionDisabled && !IsParentRoadDeleted(dependency)) return;
            // Include deleted nodes whose Node component may already be gone.
            var nodes = new HashSet<Entity> { dependency };
            if (EntityManager.TryGetComponent(dependency, out Edge road))
            { nodes.Add(road.m_Start); nodes.Add(road.m_End); }
            if (m_TaperDependencyStamps.TryGetValue(dependency, out var previous) ||
                m_EdgeStamps.TryGetValue(dependency, out previous))
            { nodes.Add(previous.Start); nodes.Add(previous.End); }
            nodes.Remove(Entity.Null);

            bool Affected(Entity edge, Entity node) => RoadGrassSettings.AutomaticMedianDeletionDisabled
                ? IsParentRoadDeleted(edge) : edge == dependency || nodes.Contains(node);
            bool AffectedAnchor(Entity edge, RoadGrassMedianAnchorData anchor) => RoadGrassSettings.AutomaticMedianDeletionDisabled
                ? IsParentRoadDeleted(edge) : edge == dependency ||
                (EntityManager.TryGetComponent(edge, out Edge data) && nodes.Contains(anchor.End ? data.m_End : data.m_Start));

            var pairIds = new List<ulong>();
            if (TryGetPersistentPairs(false, out var pairs))
                for (int i = 0; i < pairs.Length; i++)
                    if (AffectedAnchor(pairs[i].EdgeA, pairs[i].AnchorA) || AffectedAnchor(pairs[i].EdgeB, pairs[i].AnchorB))
                        pairIds.Add(pairs[i].PairId);
            foreach (var id in pairIds) RemovePairById(id);
            if (EntityManager.HasComponent<Edge>(dependency) || m_EdgeStamps.ContainsKey(dependency))
                RestoreEdgeToVanilla(dependency);

            var manualIds = new List<int>();
            foreach (var entry in m_ManualMedians)
            {
                var median = entry.Value;
                bool remove = median.Points.Count >= 3
                    ? HasDeletedManualParent(median)
                    : Affected(median.Saved.Edge, median.Saved.Node);
                if (remove) manualIds.Add(entry.Key);
            }
            foreach (var id in manualIds) RemoveManualMedian(id);
            foreach (var style in new List<RoadGrassSavedEndpointStyle>(m_Styles.Values))
            {
                if (!style.ExtendIntoIntersection || !Affected(style.Edge, style.Node)) continue;
                var ordinary = style;
                ordinary.ExtendIntoIntersection = false;
                ordinary.HasTaperEnd = false;
                ordinary.TaperEndOffset = default;
                StoreEndpointStyle(ordinary);
                RemoveVariant(style.Key);
            }
            // New drawing needs fresh guides; surviving polygons keep their mesh/material resources.
            ClearRoadGuideCaches();
            m_Dirty = true;
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
            TrackTaperDependency(anchor.Key.Edge);
            TrackTaperDependency(anchor.Node);
            if (!RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, anchor, out var approach) ||
                !EntityManager.HasBuffer<ConnectedEdge>(anchor.Node)) return false;

            var lanes = new List<RoadGrassTaperGeometry.Lane>();
            var seen = new HashSet<Entity>();
            bool hasCars = false;
            bool AddLanes(Entity owner)
            {
                TrackTaperDependency(owner);
                if (!EntityManager.HasBuffer<SubLane>(owner)) return false;
                foreach (var item in EntityManager.GetBuffer<SubLane>(owner, true))
                {
                    Entity lane = item.m_SubLane;
                    TrackTaperDependency(lane);
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

        private void SynchronizeIntersectionTapers(HashSet<Entity> edges = null)
        {
            if (edges == null) m_RenderPairs.Clear();
            else
            {
                m_RemoveKeys.Clear();
                foreach (var key in m_RenderPairs.Keys) if (edges.Contains(key.Edge)) m_RemoveKeys.Add(key);
                foreach (var key in m_RemoveKeys) m_RenderPairs.Remove(key);
            }
            foreach (var pair in m_ActivePairs)
                if (edges == null || edges.Contains(pair.Key.Edge)) m_RenderPairs[pair.Key] = pair.Value;
            foreach (var style in m_Styles.Values)
            {
                if (edges != null && !edges.Contains(style.Edge)) continue;
                if (!style.ExtendIntoIntersection) continue;
                if (!TryResolveSavedAnchor(style.Edge, style.Anchor, out var anchor) ||
                    anchor.Node != style.Node || !TryPlanIntersectionTaper(anchor, out var half) ||
                    !TryPlanEndpoint(anchor, MedianSavedCap.None, out _, true))
                { RetainProtectedVariant(style.Key); continue; }
                m_RenderPairs[anchor.Key] = anchor.Key;
                m_Resolved[anchor.Key] = half.Source;
                BatchDataHelpers.CalculateNodeParameters(half.Geometry, half.Composition, out var parameters);
                m_RenderStates[anchor.Key] = new MedianRenderState(anchor.Key, parameters, half.Geometry.m_Bounds);
            }
        }

        private void RetainProtectedVariant(RoadGrassMedianAnchorKey key)
        {
            if (!RoadGrassSettings.AutomaticMedianDeletionDisabled ||
                IsParentRoadDeleted(key.Edge) ||
                !m_Variants.TryGetValue(key, out var variant) || variant.Transition == null ||
                !EntityManager.Exists(variant.Carrier) || !EntityManager.Exists(variant.MedianComposition)) return;
            var half = variant.Transition;
            m_RenderPairs[key] = key;
            m_Resolved[key] = variant.Anchor;
            BatchDataHelpers.CalculateNodeParameters(half.Geometry, half.Composition, out var parameters);
            m_RenderStates[key] = new MedianRenderState(key, parameters, half.Geometry.m_Bounds);
            m_RetainedPairAnchors.Add(key);
        }
    }
}
