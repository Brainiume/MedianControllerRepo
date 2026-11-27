using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace RoadGrass
{
    /// <summary>
    /// Resolves persistent median anchors, creates median-section-only vanilla
    /// composition meshes, and supplies one shared pair-only strip to rendering.
    /// It never changes the real edge or node composition references.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassJoinCompositionSystem : GameSystemBase
    {
        // Keep the render-only connection just above the original vanilla node
        // median.  Two centimetres is enough to make the custom strip win the
        // depth test without producing a visibly floating median.
        private const float ConnectionRenderLift = 0.01f;

        internal sealed class VariantState
        {
            internal RoadGrassResolvedMedianAnchor Anchor;
            internal Entity MedianComposition;
            internal Entity Carrier;
            internal int Signature;
            internal SharedVariantState Shared;
            internal RoadGrassTransitionMesh.Half Transition;
        }

        internal sealed class SharedVariantState
        {
            internal Entity MedianComposition;
            internal NetCompositionData CompositionData;
            internal List<NetCompositionPiece> Pieces;
            internal int Signature;
            internal int References;
        }

        internal readonly struct MedianRenderState
        {
            internal readonly RoadGrassMedianAnchorKey Paired;
            internal readonly BatchDataHelpers.CompositionParameters Parameters;
            internal readonly Bounds3 Bounds;
            internal readonly List<MedianRenderState> Spans;
            internal readonly EdgeGeometry? ConnectionGeometry;
            internal readonly List<RoadGrassTaperGeometry.Lane> Crosswalks;

            internal MedianRenderState(
                RoadGrassMedianAnchorKey paired,
                BatchDataHelpers.CompositionParameters parameters,
                Bounds3 bounds, List<MedianRenderState> spans = null, EdgeGeometry? connectionGeometry = null,
                List<RoadGrassTaperGeometry.Lane> crosswalks = null)
            {
                Paired = paired;
                Parameters = parameters;
                Bounds = bounds;
                Spans = spans;
                ConnectionGeometry = connectionGeometry;
                Crosswalks = crosswalks;
            }
        }

        private readonly struct EdgeRuntimeStamp : IEquatable<EdgeRuntimeStamp>
        {
            internal readonly Entity Start;
            internal readonly Entity End;
            internal readonly uint Geometry;

            internal EdgeRuntimeStamp(
                Entity start,
                Entity end,
                uint geometry)
            {
                Start = start;
                End = end;
                Geometry = geometry;
            }

            public bool Equals(EdgeRuntimeStamp other)
            {
                return Start == other.Start && End == other.End &&
                    Geometry == other.Geometry;
            }
        }

        private EntityQuery m_LinkedEdgesQuery;
        private EntityQuery m_AllLinkedEdgesQuery;
        private EntityQuery m_AllLegacyEdgesQuery;
        private EntityQuery m_LegacyEdgesQuery;
        private EntityQuery m_CityQuery;
        private readonly Dictionary<RoadGrassMedianAnchorKey, VariantState> m_Variants = new();
        private readonly Dictionary<int, List<SharedVariantState>> m_SharedVariants = new();
        private readonly Dictionary<RoadGrassMedianAnchorKey, RoadGrassMedianAnchorKey> m_ActivePairs = new();
        private readonly Dictionary<RoadGrassMedianAnchorKey, RoadGrassResolvedMedianAnchor> m_Resolved = new();
        private readonly Dictionary<RoadGrassMedianAnchorKey, MedianRenderState> m_RenderStates = new();
        private readonly Dictionary<Entity, EdgeRuntimeStamp> m_EdgeStamps = new();
        private readonly Dictionary<ulong, (RoadGrassResolvedMedianAnchor A, RoadGrassResolvedMedianAnchor B)> m_LastResolvedPairs = new();
        private readonly HashSet<ulong> m_PendingPairIds = new();
        private readonly HashSet<RoadGrassMedianAnchorKey> m_RetainedPairAnchors = new();
        private readonly Dictionary<EndpointCacheKey, List<RoadGrassResolvedMedianAnchor>> m_ResolveCache = new();
        private readonly Dictionary<EndpointCacheKey, (Composition Composition, Entity Node, Entity Prefab)> m_EmptyResolveSources = new();
        private readonly List<RoadGrassMedianAnchorKey> m_RemoveKeys = new();
        private readonly List<RoadGrassMedianAnchorKey> m_PendingFinalize = new();
        private ulong m_NextPairId = 1;
        private readonly HashSet<ulong> m_CrosswalkGapPairs = new();
        private const int LoadSettleQuietFrames = 3;
        private const int LoadSettleMinimumFrames = 30;
        private const int LoadSettleMaximumFrames = 240;
        private bool m_SuspendedForSave;
        private readonly List<Entity> m_SaveExcludedCarriers = new();
        private bool m_GameReady;
        private bool m_PersistenceInitialized;
        private bool m_WaitingForLoadSettle;
        private bool m_BatchHydrationPending;
        private bool m_Dirty = true;
        private readonly HashSet<Entity> m_EditEdges = new();
        private readonly HashSet<int> m_EditPolygons = new();

        private void QueueMedianEdit(Entity edge) => m_EditEdges.Add(edge);
        private void QueuePolygonEdit(int id) => m_EditPolygons.Add(id);
        private int m_LastPendingPairCount = -1;
        private int m_RenderRevision = 1;
        private int m_LoadSettleQuietFrames;
        private int m_LoadSettleStartFrame;
        private int m_LoadSettleLastFrame;

        internal static RoadGrassJoinCompositionSystem Instance { get; private set; }
        internal int RenderRevision => m_RenderRevision;

        private readonly struct EndpointCacheKey : IEquatable<EndpointCacheKey>
        {
            internal readonly Entity Edge;
            internal readonly bool End;
            internal readonly bool IncludeRoadsideGrass;

            internal EndpointCacheKey(Entity edge, bool end, bool includeRoadsideGrass = false)
            {
                Edge = edge;
                End = end;
                IncludeRoadsideGrass = includeRoadsideGrass;
            }

            public bool Equals(EndpointCacheKey other) => Edge == other.Edge && End == other.End && IncludeRoadsideGrass == other.IncludeRoadsideGrass;
            public override bool Equals(object obj) => obj is EndpointCacheKey other && Equals(other);
            public override int GetHashCode() => (Edge.GetHashCode() * 397) ^ End.GetHashCode() ^ (IncludeRoadsideGrass ? 7919 : 0);
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            m_LinkedEdgesQuery = GetEntityQuery(
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Composition>(),
                ComponentType.ReadWrite<RoadGrassMedianPieceLink>());
            m_AllLinkedEdgesQuery = GetEntityQuery(
                ComponentType.ReadWrite<RoadGrassMedianPieceLink>());
            m_AllLegacyEdgesQuery = GetEntityQuery(
                ComponentType.ReadWrite<RoadGrassMedianJoin>());
            m_LegacyEdgesQuery = GetEntityQuery(
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Composition>(),
                ComponentType.ReadWrite<RoadGrassMedianJoin>());
            m_CityQuery = GetEntityQuery(ComponentType.ReadOnly<Game.City.City>());
        }

        [Preserve]
        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_CrosswalkGapPairs.Clear();
            m_PersistenceInitialized = false;
            m_CrosswalkCaps.Clear();
            m_TerrainMedianFills.Clear();
            RestoreAllEndpointMeshes();
            m_Styles.Clear();
            m_StyledEdges.Clear();
            m_StylesLoaded = false;
            ResetManualRuntime();
            m_SegmentSurfaces.Clear();
            m_SegmentSurfacesLoaded = false;
            m_LastPendingPairCount = -1;
            m_SuspendedForSave = false;
            m_SaveExcludedCarriers.Clear();
            m_GameReady = false;
            m_WaitingForLoadSettle = false;
            m_BatchHydrationPending = false;
            m_Dirty = true;
            m_LoadSettleQuietFrames = 0;
            m_LoadSettleStartFrame = 0;
            m_LoadSettleLastFrame = -1;
            DestroyAllVariants();
            // The composition dictionary is the normal owner of these handles,
            // but a renderer rebuild can invalidate a native instance without
            // first invalidating its runtime VariantState. Clear any residual
            // handles explicitly when changing cities.
            RoadGrassJoinMedianBatchSystem.Instance?.ResetForGameLoad();
        }

        [Preserve]
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // RoadBuilder recreates generated prefabs during loading complete.
            // Re-run hydration after every load and let unresolved anchors
            // remain pending until their generated composition is available.
            m_PersistenceInitialized = false;
            m_LastPendingPairCount = -1;
            m_GameReady = true;
            m_WaitingForLoadSettle = true;
            m_BatchHydrationPending = true;
            m_Dirty = true;
            m_LoadSettleQuietFrames = 0;
            m_LoadSettleStartFrame = UnityEngine.Time.frameCount;
            m_LoadSettleLastFrame = -1;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Also restore exclusion tags if serialization aborted before its reapply system ran.
            if (m_SuspendedForSave) ResumeAfterSerialization();
            if (!m_GameReady)
            {
                return;
            }

            // Vanilla and RoadBuilder may still replace compositions and
            // rebuild EdgeGeometry for several updates after loading-complete.
            // Building the runtime medians before that work finishes captures
            // a temporary mesh/geometry snapshot which is then discarded. The
            // gate is bounded and only checks the roads named by saved pairs.
            if (m_WaitingForLoadSettle && !AdvanceLoadSettleGate())
            {
                return;
            }

            // Missing resources wait for load, user edits or network/texture changes; never poll/rebuild on a timer.
            if (!m_Dirty)
            {
                if (m_EditEdges.Count == 0 && m_EditPolygons.Count == 0) return;
                var edges = new HashSet<Entity>(m_EditEdges);
                foreach (var pair in m_ActivePairs)
                    if (m_EditEdges.Contains(pair.Key.Edge) || m_EditEdges.Contains(pair.Value.Edge))
                    { edges.Add(pair.Key.Edge); edges.Add(pair.Value.Edge); }
                if (m_EditEdges.Count != 0)
                {
                    SynchronizeSegmentMeshes(edges);
                    SynchronizeRenderStates(edges);
                    SynchronizeIntersectionTapers(edges);
                }
                foreach (int id in m_EditPolygons)
                    if (m_ManualMedians.TryGetValue(id, out var median)) edges.Add(median.Saved.Edge);
                SynchronizeManualMedians(edges, m_EditPolygons);
                SynchronizeVariants(edges);
                SynchronizeEndpointMeshes(edges);
                if (m_EditEdges.Count != 0) SynchronizeTerrainMedianFills(edges);
                m_EditEdges.Clear(); m_EditPolygons.Clear();
                BeginPendingBatchHydration();
                return;
            }
            m_Dirty = false;
            m_EditEdges.Clear(); m_EditPolygons.Clear();

            m_ResolveCache.Clear();
            m_EmptyResolveSources.Clear();
            m_InvalidMedianEdges.Clear();
            HydrateEndpointStyles();
            HydrateManualMedians();
            HydrateSegmentSurfaces();
            SynchronizeSegmentMeshes();
            RestorePersistentPairMirrors();
            MigrateLegacyJoins();
            CollectActivePairs();
            SynchronizeRenderStates();
            SynchronizeIntersectionTapers();
            SynchronizeManualMedians();
            SynchronizeVariants();
            SynchronizeEndpointMeshes();
            SynchronizeTerrainMedianFills();
            BeginPendingBatchHydration();
        }

        internal bool TryGetSelectableAnchors(
            Entity edge,
            bool end,
            out List<RoadGrassResolvedMedianAnchor> anchors,
            bool includeRoadsideGrass = false)
        {
            var key = new EndpointCacheKey(edge, end, includeRoadsideGrass);
            if (m_ResolveCache.TryGetValue(key, out anchors))
            {
                if (anchors.Count == 0 && m_EmptyResolveSources.TryGetValue(key, out var empty) &&
                    !EntityManager.HasComponent<Updated>(edge) && !EntityManager.HasComponent<Created>(edge) &&
                    !EntityManager.HasComponent<Deleted>(edge) && EntityManager.TryGetComponent(edge, out Composition composition) &&
                    composition.m_Edge == empty.Composition.m_Edge && composition.m_StartNode == empty.Composition.m_StartNode &&
                    composition.m_EndNode == empty.Composition.m_EndNode && EntityManager.TryGetComponent(edge, out Edge road) &&
                    (end ? road.m_End : road.m_Start) == empty.Node && EntityManager.TryGetComponent(edge, out PrefabRef prefab) && prefab.m_Prefab == empty.Prefab)
                    return false;
                if (anchors.Count > 0 && EntityManager.Exists(edge) &&
                    !EntityManager.HasComponent<Deleted>(edge) &&
                    EntityManager.TryGetComponent(edge, out Composition current) &&
                    current.m_Edge == anchors[0].SourceComposition &&
                    EntityManager.TryGetComponent(edge, out Edge edgeData) &&
                    (end ? edgeData.m_End : edgeData.m_Start) == anchors[0].Node)
                    return true;
                m_ResolveCache.Remove(key);
            }

            bool result = RoadGrassMedianAnchorResolver.TryResolveAll(
                EntityManager,
                edge,
                end,
                GetBufferLookup<NetSubSection>(true),
                GetBufferLookup<NetSectionPiece>(true),
                GetComponentLookup<NetPieceData>(true),
                GetComponentLookup<NetVertexMatchData>(true),
                out anchors, out bool invalidData, includeRoadsideGrass: includeRoadsideGrass,
                prefabs: includeRoadsideGrass ? World.GetExistingSystemManaged<PrefabSystem>() : null);
            if (invalidData && !EntityManager.HasComponent<Updated>(edge)) m_InvalidMedianEdges.Add(edge);
            m_ResolveCache[key] = anchors;
            if (anchors.Count == 0 && EntityManager.TryGetComponent(edge, out Composition emptyComposition) &&
                EntityManager.HasComponent<NetCompositionData>(emptyComposition.m_Edge) && EntityManager.HasBuffer<NetCompositionPiece>(emptyComposition.m_Edge) &&
                EntityManager.TryGetComponent(edge, out Edge emptyRoad) && EntityManager.TryGetComponent(edge, out PrefabRef emptyPrefab))
                m_EmptyResolveSources[key] = (emptyComposition, end ? emptyRoad.m_End : emptyRoad.m_Start, emptyPrefab.m_Prefab);
            else m_EmptyResolveSources.Remove(key);
            return result;
        }

        internal bool TryResolveAnchor(
            RoadGrassMedianAnchorKey key,
            out RoadGrassResolvedMedianAnchor anchor)
        {
            anchor = null;
            if (!TryGetSelectableAnchors(key.Edge, key.End, out var anchors, key.IsRoadsideGrass))
            {
                return false;
            }

            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i].Key.Equals(key))
                {
                    anchor = anchors[i];
                    return true;
                }
            }
            return false;
        }

        internal List<RoadGrassResolvedMedianAnchor> GetNodeAnchors(Entity node, bool includeRoadsideGrass = false,
            List<RoadGrassResolvedMedianAnchor> result = null, bool append = false)
        {
            result ??= new List<RoadGrassResolvedMedianAnchor>();
            if (!append) result.Clear();
            if (node == Entity.Null || !EntityManager.Exists(node) ||
                !EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                return result;
            }

            DynamicBuffer<ConnectedEdge> connected = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < connected.Length; i++)
            {
                Entity edgeEntity = connected[i].m_Edge;
                if (!EntityManager.TryGetComponent(edgeEntity, out Edge edge))
                {
                    continue;
                }
                bool end = edge.m_End == node;
                if (TryGetSelectableAnchors(edgeEntity, end, out var anchors, includeRoadsideGrass))
                {
                    result.AddRange(anchors);
                }
            }
            return result;
        }

        internal bool IsAnchorLinked(RoadGrassMedianAnchorKey key)
        {
            if (!EntityManager.Exists(key.Edge) ||
                !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(key.Edge))
            {
                return false;
            }
            DynamicBuffer<RoadGrassMedianPieceLink> links =
                EntityManager.GetBuffer<RoadGrassMedianPieceLink>(key.Edge, true);
            for (int i = 0; i < links.Length; i++)
            {
                if (links[i].Local.End == key.End && links[i].Local.Ordinal == key.Ordinal)
                {
                    return true;
                }
            }
            return false;
        }

        internal bool AddLink(
            RoadGrassResolvedMedianAnchor a,
            RoadGrassResolvedMedianAnchor b, bool crosswalkGaps = false)
        {
            if (a == null || b == null || a.Node == Entity.Null || a.Node != b.Node ||
                a.Key.Edge == b.Key.Edge ||
                !TryBuildPairPreview(a, b, out EdgeGeometry aGeometry, out EdgeGeometry bGeometry))
            {
                return false;
            }

            RemoveAnchorLinks(a.Key);
            RemoveAnchorLinks(b.Key);
            ulong pairId = AllocatePairId();
            AddLinkEntry(
                a.Key.Edge,
                new RoadGrassMedianPieceLink(pairId, b.Key.Edge, a.Data, b.Data));
            AddLinkEntry(
                b.Key.Edge,
                new RoadGrassMedianPieceLink(pairId, a.Key.Edge, b.Data, a.Data));
            UpsertPersistentPair(
                new RoadGrassSavedMedianPair(
                    pairId,
                    a.Key.Edge,
                    a.Data,
                    b.Key.Edge,
                    b.Data, crosswalkGaps));
            CachePairGeometry(a, b, aGeometry, bGeometry);
            m_ActivePairs[a.Key] = b.Key; m_ActivePairs[b.Key] = a.Key;
            m_Resolved[a.Key] = a; m_Resolved[b.Key] = b;
            m_LastResolvedPairs[pairId] = (a, b);
            QueueMedianEdit(a.Key.Edge); QueueMedianEdit(b.Key.Edge);
            Mod.TryInfo(
                $"Linked pair {pairId} selected vanilla median pieces " +
                $"{a.Key} ({a.Width:F2}m) and " +
                $"{b.Key} ({b.Width:F2}m) through node {a.Node}");
            return true;
        }

        internal bool TryBuildPairPreview(
            RoadGrassResolvedMedianAnchor a,
            RoadGrassResolvedMedianAnchor b,
            out EdgeGeometry aGeometry,
            out EdgeGeometry bGeometry)
        {
            aGeometry = default;
            bGeometry = default;
            if (a == null || b == null || a.Key.IsRoadsideGrass != b.Key.IsRoadsideGrass ||
                (!a.Key.IsRoadsideGrass && RoadGrassTransitionMesh.IsBlockedWidthPair(a.Width, b.Width))) return false;
            return TryBuildPairGeometry(a, b, out aGeometry, out bGeometry);
        }

        internal void RemoveAnchorLinks(RoadGrassMedianAnchorKey key)
        {
            m_TaperPlans.Remove(key);
            QueueMedianEdit(key.Edge);
            if (!EntityManager.Exists(key.Edge) ||
                !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(key.Edge))
            {
                RemoveVariant(key);
                return;
            }

            DynamicBuffer<RoadGrassMedianPieceLink> links =
                EntityManager.GetBuffer<RoadGrassMedianPieceLink>(key.Edge, true);
            var matches = new List<RoadGrassMedianPieceLink>();
            for (int i = 0; i < links.Length; i++)
            {
                RoadGrassMedianPieceLink link = links[i];
                if (link.Local.End != key.End || link.Local.Ordinal != key.Ordinal)
                {
                    continue;
                }
                matches.Add(link);
            }
            for (int i = 0; i < matches.Count; i++)
            {
                RoadGrassMedianPieceLink link = matches[i];
                if (link.PairId != 0)
                {
                    RemovePairById(link.PairId);
                }
                else
                {
                    RemoveLegacyPair(key.Edge, key, link);
                }
            }
            if (matches.Count == 0)
            {
                RemoveVariant(key);
            }
        }

        internal void RestoreEdgeToVanilla(Entity edge)
        {
            if (edge == Entity.Null)
            {
                return;
            }
            m_Dirty = true;

            if (EntityManager.Exists(edge) &&
                EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge))
            {
                DynamicBuffer<RoadGrassMedianPieceLink> buffer =
                    EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge, true);
                var copy = new List<RoadGrassMedianPieceLink>(buffer.Length);
                for (int i = 0; i < buffer.Length; i++)
                {
                    copy.Add(buffer[i]);
                }
                var removedPairIds = new HashSet<ulong>();
                for (int i = 0; i < copy.Count; i++)
                {
                    RoadGrassMedianPieceLink link = copy[i];
                    if (link.PairId != 0 && removedPairIds.Add(link.PairId))
                    {
                        RemovePairById(link.PairId);
                    }
                    else if (link.PairId == 0)
                    {
                        RemoveLegacyPair(
                            edge,
                            new RoadGrassMedianAnchorKey(
                                edge,
                                link.Local.End,
                                link.Local.Ordinal),
                            link);
                    }
                }
            }

            if (EntityManager.Exists(edge) && EntityManager.HasBuffer<RoadGrassMedianJoin>(edge))
            {
                EntityManager.RemoveComponent<RoadGrassMedianJoin>(edge);
            }

            m_RemoveKeys.Clear();
            foreach (RoadGrassMedianAnchorKey key in m_Variants.Keys)
            {
                if (key.Edge == edge)
                {
                    m_RemoveKeys.Add(key);
                }
            }
            for (int i = 0; i < m_RemoveKeys.Count; i++)
            {
                RemoveVariant(m_RemoveKeys[i]);
            }
            Mod.TryInfo($"Restored vanilla rendering and removed median-piece links for {edge}");
        }

        internal void ClearAllData()
        {
            ClearManualMedians();
            ClearEndpointStyles();
            // Remove the durable city records first, then the old per-edge
            // format and its runtime mirrors so a later save cannot import
            // the links back into the city buffer.
            if (TryGetPersistentPairs(create: false, out var pairs))
            {
                pairs.Clear();
            }
            using (NativeArray<Entity> edges = m_AllLinkedEdgesQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    if (EntityManager.Exists(edges[i]))
                    {
                        EntityManager.RemoveComponent<RoadGrassMedianPieceLink>(edges[i]);
                    }
                }
            }
            using (NativeArray<Entity> edges = m_AllLegacyEdgesQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    if (EntityManager.Exists(edges[i]))
                    {
                        EntityManager.RemoveComponent<RoadGrassMedianJoin>(edges[i]);
                    }
                }
            }
            RoadGrassJoinMedianBatchSystem.Instance?.RemoveAll();
            DestroyAllVariants();
            m_PersistenceInitialized = true;
            m_WaitingForLoadSettle = false;
            m_BatchHydrationPending = false;
            m_LastPendingPairCount = 0;
            m_NextPairId = 1;
            m_CrosswalkGapPairs.Clear();
            RoadGrassPolygonPrototypeBatchSystem.Instance?.Clear();
            m_CrosswalkCaps.Clear();
            m_TerrainMedianFills.Clear();
            m_Dirty = false;
        }

        internal void SyncMedianBatches(RoadGrassJoinMedianBatchSystem batchSystem)
        {
            PruneCrosswalkCaps();
            if (m_SuspendedForSave)
            {
                return;
            }
            else
            {
                batchSystem.Sync(m_Variants, m_RenderPairs, m_RenderRevision);
            }
        }

        internal void UpdateMedianBatchData(RoadGrassJoinMedianBatchDataSystem batchDataSystem)
        {
            if (!m_SuspendedForSave)
            {
                batchDataSystem.UpdateData(m_Variants, m_RenderPairs);
            }
        }

        internal bool TryGetMedianRenderState(
            RoadGrassMedianAnchorKey key,
            RoadGrassMedianAnchorKey pairedKey,
            out BatchDataHelpers.CompositionParameters parameters,
            out Bounds3 bounds, int span = 0)
        {
            parameters = default;
            bounds = default;
            if (!m_RenderStates.TryGetValue(key, out MedianRenderState state) ||
                !state.Paired.Equals(pairedKey))
            {
                return false;
            }

            if (state.Spans != null)
            {
                if ((uint)span >= state.Spans.Count) return false;
                state = state.Spans[span];
            }
            parameters = state.Parameters;
            bounds = state.Bounds;
            return true;
        }

        internal int GetMedianRenderSpanCount(RoadGrassMedianAnchorKey key) =>
            m_RenderStates.TryGetValue(key, out var state) ? state.Spans?.Count ?? 1 : 0;

        internal void FinalizePendingVariants()
        {
            foreach (var variant in m_PendingEndpointMeshes)
            {
                if (EntityManager.Exists(variant) && EntityManager.HasComponent<Created>(variant))
                    EntityManager.RemoveComponent<Created>(variant);
            }
            m_PendingEndpointMeshes.Clear();
            for (int i = 0; i < m_PendingFinalize.Count; i++)
            {
                RoadGrassMedianAnchorKey key = m_PendingFinalize[i];
                if (!m_Variants.TryGetValue(key, out VariantState state))
                {
                    continue;
                }
                // Native Node mesh identity is now resolved. Replace the source
                // cap on the next update only if this mesh is actually usable.
                if (m_RenderPairs.TryGetValue(key, out var paired) && paired.Equals(key)) m_Dirty = true;
                if (EntityManager.Exists(state.MedianComposition) &&
                    EntityManager.HasComponent<Created>(state.MedianComposition))
                {
                    EntityManager.RemoveComponent<Created>(state.MedianComposition);
                }
                if (EntityManager.Exists(state.Carrier) &&
                    !EntityManager.HasComponent<BatchesUpdated>(state.Carrier))
                {
                    EntityManager.AddComponent<BatchesUpdated>(state.Carrier);
                }
            }
            m_PendingFinalize.Clear();
        }

        internal void SuspendForSerialization()
        {
            if (m_SuspendedForSave)
            {
                return;
            }
            CapturePersistentPairs();
            m_SuspendedForSave = true;
            // SerializerSystem already excludes compositions; Temp excludes only our render carriers for this save.
            foreach (var state in m_Variants.Values)
            {
                if (!EntityManager.Exists(state.Carrier) || EntityManager.HasComponent<Game.Tools.Temp>(state.Carrier)) continue;
                EntityManager.AddComponent<Game.Tools.Temp>(state.Carrier);
                m_SaveExcludedCarriers.Add(state.Carrier);
            }
        }

        internal void ResumeAfterSerialization()
        {
            if (!m_SuspendedForSave) return;
            foreach (Entity carrier in m_SaveExcludedCarriers)
                if (EntityManager.Exists(carrier) && EntityManager.HasComponent<Game.Tools.Temp>(carrier))
                    EntityManager.RemoveComponent<Game.Tools.Temp>(carrier);
            m_SaveExcludedCarriers.Clear();
            m_SuspendedForSave = false;
        }

        private void BeginPendingBatchHydration()
        {
            if (!m_BatchHydrationPending)
            {
                return;
            }

            if (m_Variants.Count == 0)
            {
                // Keep the request alive while saved RoadBuilder anchors are
                // still pending. A save with no joins needs no renderer pass.
                if (m_LastPendingPairCount == 0)
                {
                    m_BatchHydrationPending = false;
                }
                return;
            }

            RoadGrassJoinMedianBatchSystem batchSystem =
                RoadGrassJoinMedianBatchSystem.Instance;
            if (batchSystem == null)
            {
                m_Dirty = true;
                return;
            }

            batchSystem.BeginHydrationDelay();
            m_BatchHydrationPending = false;
            Mod.TryInfo($"Preparing {m_Variants.Count} joined-median endpoint render instance(s) after city load");
        }

        private bool AdvanceLoadSettleGate()
        {
            if (m_CityQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            Entity city = m_CityQuery.GetSingletonEntity();
            var pairs = EntityManager.HasBuffer<RoadGrassSavedMedianPair>(city)
                ? EntityManager.GetBuffer<RoadGrassSavedMedianPair>(city, true) : default;
            var styles = EntityManager.HasBuffer<RoadGrassSavedEndpointStyle>(city)
                ? EntityManager.GetBuffer<RoadGrassSavedEndpointStyle>(city, true) : default;
            var surfaces = EntityManager.HasBuffer<RoadGrassSavedSegmentSurface>(city)
                ? EntityManager.GetBuffer<RoadGrassSavedSegmentSurface>(city, true) : default;
            int pairCount = pairs.IsCreated ? pairs.Length : 0;
            int styleCount = styles.IsCreated ? styles.Length : 0;
            int surfaceCount = surfaces.IsCreated ? surfaces.Length : 0;
            if (pairCount == 0 && styleCount == 0 && surfaceCount == 0)
            {
                m_WaitingForLoadSettle = false;
                return true;
            }

            int frame = UnityEngine.Time.frameCount;
            if (frame == m_LoadSettleLastFrame)
            {
                return false;
            }
            m_LoadSettleLastFrame = frame;
            bool changing = false;
            for (int i = 0; i < pairCount && !changing; i++)
            {
                RoadGrassSavedMedianPair pair = pairs[i];
                changing = IsSavedEdgeStillLoading(pair.EdgeA) ||
                    IsSavedEdgeStillLoading(pair.EdgeB);
            }

            for (int i = 0; i < styleCount && !changing; i++)
                changing = IsSavedEdgeStillLoading(styles[i].Edge);
            for (int i = 0; i < surfaceCount && !changing; i++)
                changing = IsSavedEdgeStillLoading(surfaces[i].Edge);

            if (changing)
            {
                m_LoadSettleQuietFrames = 0;
            }
            else
            {
                m_LoadSettleQuietFrames++;
            }

            int elapsedFrames = frame - m_LoadSettleStartFrame;
            if ((elapsedFrames < LoadSettleMinimumFrames ||
                 m_LoadSettleQuietFrames < LoadSettleQuietFrames) &&
                elapsedFrames < LoadSettleMaximumFrames)
            {
                return false;
            }

            m_WaitingForLoadSettle = false;
            return true;
        }

        private bool IsSavedEdgeStillLoading(Entity edge)
        {
            if (edge == Entity.Null || !EntityManager.Exists(edge))
            {
                // Entity remapping and generated compositions are handled by
                // the existing pending-pair retry path after this bounded gate.
                return false;
            }

            return EntityManager.HasComponent<Updated>(edge) ||
                !EntityManager.HasComponent<Edge>(edge) ||
                !EntityManager.HasComponent<Composition>(edge) ||
                !EntityManager.HasComponent<EdgeGeometry>(edge);
        }

        private void MigrateLegacyJoins()
        {
            if (m_LegacyEdgesQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            using NativeArray<Entity> edges = m_LegacyEdgesQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i];
                if (!EntityManager.Exists(edge) ||
                    !EntityManager.HasBuffer<RoadGrassMedianJoin>(edge))
                {
                    continue;
                }
                DynamicBuffer<RoadGrassMedianJoin> old =
                    EntityManager.GetBuffer<RoadGrassMedianJoin>(edge, true);
                var copy = new List<RoadGrassMedianJoin>(old.Length);
                for (int j = 0; j < old.Length; j++)
                {
                    copy.Add(old[j]);
                }
                EntityManager.RemoveComponent<RoadGrassMedianJoin>(edge);

                for (int j = 0; j < copy.Count; j++)
                {
                    RoadGrassMedianJoin join = copy[j];
                    if (TryGetSelectableAnchors(edge, join.LocalEnd, out var local) &&
                        local.Count == 1 &&
                        TryGetSelectableAnchors(join.PairedEdge, join.PairedEnd, out var paired) &&
                        paired.Count == 1 && local[0].Node == paired[0].Node)
                    {
                        AddLink(local[0], paired[0]);
                    }
                    else
                    {
                        Mod.TryInfo(
                            $"Discarded ambiguous endpoint-only median join on {edge} during v2 migration");
                    }
                }
            }
        }

        private bool TryGetPersistentPairs(
            bool create,
            out DynamicBuffer<RoadGrassSavedMedianPair> pairs)
        {
            pairs = default;
            if (m_CityQuery.IsEmptyIgnoreFilter)
            {
                return false;
            }

            Entity city = m_CityQuery.GetSingletonEntity();
            if (!EntityManager.HasBuffer<RoadGrassSavedMedianPair>(city))
            {
                if (!create)
                {
                    return false;
                }
                EntityManager.AddBuffer<RoadGrassSavedMedianPair>(city);
            }
            pairs = EntityManager.GetBuffer<RoadGrassSavedMedianPair>(city);
            return true;
        }

        private void RestorePersistentPairMirrors()
        {
            if (!TryGetPersistentPairs(create: true, out var pairs))
            {
                return;
            }

            if (!m_PersistenceInitialized)
            {
                // Import the old per-edge format once, allowing saves made by
                // v6 and earlier to acquire the durable city-level store.
                ImportEdgeLinksIntoPersistentBuffer(pairs);
                m_PersistenceInitialized = true;
                Mod.TryInfo(
                    $"Loaded {pairs.Length} persistent median pair(s) from the city save buffer");
            }

            // Copy before adding missing edge buffers. Any AddBuffer call is a
            // structural change and would invalidate the city's DynamicBuffer
            // handle while it is being enumerated.
            var savedPairs = new List<RoadGrassSavedMedianPair>(pairs.Length);
            for (int i = pairs.Length - 1; i >= 0; i--)
            {
                RoadGrassSavedMedianPair pair = pairs[i];
                if (pair.PairId == 0)
                {
                    pair.PairId = AllocatePairId();
                    pairs[i] = pair;
                }
                if (pair.PairId >= m_NextPairId)
                {
                    m_NextPairId = pair.PairId + 1;
                }
                savedPairs.Add(pair);
                if (pair.CrosswalkGaps) m_CrosswalkGapPairs.Add(pair.PairId);
            }

            for (int i = 0; i < savedPairs.Count; i++)
            {
                RoadGrassSavedMedianPair pair = savedPairs[i];
                if (pair.EdgeA == Entity.Null || pair.EdgeB == Entity.Null || pair.EdgeA == pair.EdgeB ||
                    !pair.AnchorA.IsValid || !pair.AnchorB.IsValid)
                {
                    RemovePairById(pair.PairId);
                    continue;
                }

                bool aExists = EntityManager.Exists(pair.EdgeA);
                bool bExists = EntityManager.Exists(pair.EdgeB);
                if (IsParentRoadDeleted(pair.EdgeA) || IsParentRoadDeleted(pair.EdgeB))
                {
                    RemovePairById(pair.PairId);
                    Mod.TryInfo(
                        $"Removed persistent median pair {pair.PairId} because a road was deleted");
                    continue;
                }

                // Missing entities or Edge components can be transient during
                // deserialization.  Do not discard the player's saved pair.
                if (!aExists || !bExists ||
                    !EntityManager.HasComponent<Edge>(pair.EdgeA) ||
                    !EntityManager.HasComponent<Edge>(pair.EdgeB))
                {
                    continue;
                }

                AddLinkEntry(
                    pair.EdgeA,
                    new RoadGrassMedianPieceLink(
                        pair.PairId,
                        pair.EdgeB,
                        pair.AnchorA,
                        pair.AnchorB));
                AddLinkEntry(
                    pair.EdgeB,
                    new RoadGrassMedianPieceLink(
                        pair.PairId,
                        pair.EdgeA,
                        pair.AnchorB,
                        pair.AnchorA));
            }
        }

        private void CapturePersistentPairs()
        {
            if (TryGetPersistentPairs(create: true, out var pairs))
            {
                ImportEdgeLinksIntoPersistentBuffer(pairs);
                Mod.TryInfo(
                    $"Saving {pairs.Length} persistent joined-median pair(s)");
            }
        }

        private void ImportEdgeLinksIntoPersistentBuffer(
            DynamicBuffer<RoadGrassSavedMedianPair> pairs)
        {
            if (m_AllLinkedEdgesQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            using NativeArray<Entity> edges =
                m_AllLinkedEdgesQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i];
                if (!EntityManager.Exists(edge) ||
                    !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge))
                {
                    continue;
                }
                DynamicBuffer<RoadGrassMedianPieceLink> links =
                    EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge);
                for (int j = 0; j < links.Length; j++)
                {
                    RoadGrassMedianPieceLink link = links[j];
                    if (link.PairId == 0 || link.PairedEdge == Entity.Null)
                    {
                        continue;
                    }
                    UpsertPersistentPair(
                        pairs,
                        new RoadGrassSavedMedianPair(
                            link.PairId,
                            edge,
                            link.Local,
                            link.PairedEdge,
                            link.Paired));
                }
            }
        }

        private void UpsertPersistentPair(RoadGrassSavedMedianPair pair)
        {
            if (pair.PairId == 0 ||
                !TryGetPersistentPairs(create: true, out var pairs))
            {
                return;
            }
            UpsertPersistentPair(pairs, pair);
            if (pair.CrosswalkGaps) m_CrosswalkGapPairs.Add(pair.PairId);
        }

        private static void UpsertPersistentPair(
            DynamicBuffer<RoadGrassSavedMedianPair> pairs,
            RoadGrassSavedMedianPair pair)
        {
            for (int i = 0; i < pairs.Length; i++)
            {
                if (pairs[i].PairId == pair.PairId)
                {
                    pair.CrosswalkGaps |= pairs[i].CrosswalkGaps;
                    pairs[i] = pair;
                    return;
                }
            }
            pairs.Add(pair);
        }

        private void RemovePersistentPair(ulong pairId)
        {
            m_CrosswalkGapPairs.Remove(pairId);
            m_LastResolvedPairs.Remove(pairId);
            m_PendingPairIds.Remove(pairId);
            if (pairId == 0 ||
                !TryGetPersistentPairs(create: false, out var pairs))
            {
                return;
            }
            for (int i = pairs.Length - 1; i >= 0; i--)
            {
                if (pairs[i].PairId == pairId)
                {
                    pairs.RemoveAt(i);
                }
            }
        }

        private void RemovePairMirrors(ulong pairId)
        {
            if (pairId == 0 || m_AllLinkedEdgesQuery.IsEmptyIgnoreFilter)
            {
                return;
            }
            using NativeArray<Entity> edges =
                m_AllLinkedEdgesQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i];
                if (!EntityManager.Exists(edge) ||
                    !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge))
                {
                    continue;
                }
                DynamicBuffer<RoadGrassMedianPieceLink> links =
                    EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge);
                for (int j = links.Length - 1; j >= 0; j--)
                {
                    if (links[j].PairId == pairId)
                    {
                        links.RemoveAt(j);
                    }
                }
                if (links.Length == 0)
                {
                    EntityManager.RemoveComponent<RoadGrassMedianPieceLink>(edge);
                }
            }
        }

        private void ReportPendingPersistentPairs()
        {
            int pending = 0;
            if (TryGetPersistentPairs(create: false, out var pairs))
            {
                pending = math.max(m_PendingPairIds.Count, pairs.Length - m_ActivePairs.Count / 2);
            }
            if (pending != m_LastPendingPairCount)
            {
                if (pending > 0)
                {
                    Mod.TryInfo(
                        $"Waiting to rebuild {pending} saved median pair(s) from road geometry");
                }
                else if (m_LastPendingPairCount > 0)
                {
                    Mod.TryInfo("All saved median pairs have been rebuilt");
                }
                m_LastPendingPairCount = pending;
            }
        }

        internal void ProcessChangedLinkedEdges(NativeArray<Entity> changed)
        {
            if (changed.Length == 0)
            {
                return;
            }

            var invalid = new List<Entity>();
            for (int i = 0; i < changed.Length; i++)
            {
                Entity edge = changed[i];
                if (edge == Entity.Null || !EntityManager.Exists(edge) ||
                    EntityManager.HasComponent<Deleted>(edge))
                {
                    m_Dirty = true;
                    InvalidateTransitions(edge);
                    invalid.Add(edge);
                    continue;
                }

                // A missing stamp means this is a freshly loaded/pending pair.
                // Its immutable snapshot is captured only after its geometry has
                // successfully produced the connection.
                if (!m_EdgeStamps.TryGetValue(edge, out EdgeRuntimeStamp saved))
                {
                    if (TryCaptureEdgeStamp(edge, out _)) m_Dirty = true;
                    continue;
                }

                // Traffic controls rebuild composition entities without changing
                // road geometry. Missing geometry during a rebuild is not deletion.
                if (TryCaptureEdgeStamp(edge, out EdgeRuntimeStamp current) &&
                    !saved.Equals(current))
                {
                    m_Dirty = true;
                    InvalidateTransitions(edge);
                    invalid.Add(edge);
                }
                else if (m_LastPendingPairCount > 0)
                {
                    // A generated composition may have become available even
                    // though its road curves did not change. Retry on this event.
                    m_Dirty = true;
                }
                else
                {
                    if (m_CrosswalkGapPairs.Count > 0) RefreshCrosswalkClearance(edge);
                    foreach (var median in m_ManualMedians.Values)
                        if (median.Saved.CrosswalkGaps && (median.Saved.Edge == edge ||
                            (EntityManager.TryGetComponent(edge, out Edge road) &&
                             (road.m_Start == median.Saved.Node || road.m_End == median.Saved.Node))))
                        { m_Dirty = true; break; }
                }
            }

            for (int i = 0; i < invalid.Count; i++)
            {
                if (RoadGrassSettings.AutomaticMedianDeletionDisabled && !IsParentRoadDeleted(invalid[i])) continue;
                Entity edge = invalid[i];
                Mod.TryInfo(
                    $"Removing median joins from {edge}: source road changed or was deleted");
                RemoveGeneratedMediansForGeometry(edge);
                m_EdgeStamps.Remove(edge);
            }
        }

        private void SynchronizeRenderStates(HashSet<Entity> edges = null)
        {
            m_RemoveKeys.Clear();
            foreach (KeyValuePair<RoadGrassMedianAnchorKey, MedianRenderState> entry in m_RenderStates)
            {
                if (edges != null && !edges.Contains(entry.Key.Edge)) continue;
                if (!m_ActivePairs.TryGetValue(entry.Key, out RoadGrassMedianAnchorKey paired) ||
                    !entry.Value.Paired.Equals(paired))
                {
                    m_RemoveKeys.Add(entry.Key);
                }
            }
            for (int i = 0; i < m_RemoveKeys.Count; i++)
            {
                m_RenderStates.Remove(m_RemoveKeys[i]);
            }

            foreach (KeyValuePair<RoadGrassMedianAnchorKey, RoadGrassMedianAnchorKey> pair in m_ActivePairs)
            {
                if (edges != null && !edges.Contains(pair.Key.Edge)) continue;
                if (m_RetainedPairAnchors.Contains(pair.Key)) continue;
                if (m_RenderStates.TryGetValue(pair.Key, out MedianRenderState existing) &&
                    existing.Paired.Equals(pair.Value) &&
                    m_Resolved.TryGetValue(pair.Key, out var existingA) &&
                    m_Resolved.TryGetValue(pair.Value, out var existingB) &&
                    math.abs(existingA.Width - existingB.Width) <= 0.01f)
                {
                    if (existing.ConnectionGeometry.HasValue && m_RenderStates.TryGetValue(pair.Value, out var opposite) &&
                        opposite.ConnectionGeometry.HasValue)
                        CachePairGeometry(existingA, existingB, existing.ConnectionGeometry.Value, opposite.ConnectionGeometry.Value);
                    continue;
                }
                if (!m_Resolved.TryGetValue(pair.Key, out RoadGrassResolvedMedianAnchor a) ||
                    !m_Resolved.TryGetValue(pair.Value, out RoadGrassResolvedMedianAnchor b) ||
                    !TryBuildPairGeometry(a, b, out EdgeGeometry aGeometry, out EdgeGeometry bGeometry))
                {
                    m_RenderStates.Remove(pair.Key);
                    continue;
                }
                CachePairGeometry(a, b, aGeometry, bGeometry);
            }
        }

        private bool CachePairGeometry(
            RoadGrassResolvedMedianAnchor a,
            RoadGrassResolvedMedianAnchor b,
            EdgeGeometry aGeometry,
            EdgeGeometry bGeometry)
        {
            BatchDataHelpers.CompositionParameters aParameters, bParameters;
            RoadGrassTransitionMesh.Half aHalf = null, bHalf = null;
            if (math.abs(a.Width - b.Width) > 0.01f)
            {
                if (!TryPlanTransition(a, b, aGeometry, bGeometry, out var transition)) return false;
                aHalf = transition.A; bHalf = transition.B;
                BatchDataHelpers.CalculateNodeParameters(transition.A.Geometry, transition.A.Composition, out aParameters);
                BatchDataHelpers.CalculateNodeParameters(transition.B.Geometry, transition.B.Composition, out bParameters);
            }
            else
            {
                BatchDataHelpers.CalculateEdgeParameters(aGeometry, isRotated: false, out aParameters);
                BatchDataHelpers.CalculateEdgeParameters(bGeometry, isRotated: false, out bParameters);
            }
            var crosswalks = HasCrosswalkGaps(a.Key) ? GetConnectionCrosswalks(a.Node) : new List<RoadGrassTaperGeometry.Lane>();
            bool sameGeometry = m_RenderStates.TryGetValue(a.Key, out var oldA) && oldA.Paired.Equals(b.Key) &&
                m_RenderStates.TryGetValue(b.Key, out var oldB) && oldB.Paired.Equals(a.Key) &&
                oldA.Parameters.Equals(aParameters) && oldB.Parameters.Equals(bParameters) &&
                oldA.Bounds.Equals(aGeometry.m_Bounds) && oldB.Bounds.Equals(bGeometry.m_Bounds);
            if (sameGeometry && CrosswalksEqual(oldA.Crosswalks, crosswalks)) return true;
            RemoveCrosswalkCaps(a.Key);
            RemoveCrosswalkCaps(b.Key);
            var aSpans = CrosswalkRenderSpans(a, b.Key, aGeometry, crosswalks, aHalf);
            var bSpans = CrosswalkRenderSpans(b, a.Key, bGeometry, crosswalks, bHalf);
            bool changed = !sameGeometry || !RenderSpansEqual(oldA.Spans, aSpans) ||
                !RenderSpansEqual(m_RenderStates[b.Key].Spans, bSpans);
            m_RenderStates[a.Key] = new MedianRenderState(
                b.Key,
                aParameters,
                aGeometry.m_Bounds, aSpans, aGeometry, crosswalks);
            m_RenderStates[b.Key] = new MedianRenderState(
                a.Key,
                bParameters,
                bGeometry.m_Bounds, bSpans, bGeometry, crosswalks);
            CaptureEdgeStamp(a.Key.Edge);
            CaptureEdgeStamp(b.Key.Edge);
            if (changed) AdvanceRenderRevision();
            return true;
        }

        private void CaptureEdgeStamp(Entity edge)
        {
            if (TryCaptureEdgeStamp(edge, out EdgeRuntimeStamp stamp))
            {
                m_EdgeStamps[edge] = stamp;
            }
        }

        private bool TryCaptureEdgeStamp(Entity edge, out EdgeRuntimeStamp stamp)
        {
            stamp = default;
            if (edge == Entity.Null || !EntityManager.Exists(edge) ||
                EntityManager.HasComponent<Deleted>(edge) ||
                !EntityManager.TryGetComponent(edge, out Edge edgeData))
            {
                return false;
            }
            uint hash;
            if (EntityManager.TryGetComponent(edge, out Game.Net.Curve curve))
            {
                // Signal/crosswalk updates rebuild derived junction geometry and lanes.
                // Only the road's authored curve/prefab identifies a physical road edit.
                hash = 2166136261u;
                AccumulateStamp(ref hash, curve.m_Bezier.a); AccumulateStamp(ref hash, curve.m_Bezier.b);
                AccumulateStamp(ref hash, curve.m_Bezier.c); AccumulateStamp(ref hash, curve.m_Bezier.d);
            }
            else return false; // Missing authored geometry is temporary unavailability, not a road edit.
            if (EntityManager.TryGetComponent(edge, out PrefabRef prefab))
                hash = (hash ^ (uint)prefab.m_Prefab.GetHashCode()) * 16777619u;
            stamp = new EdgeRuntimeStamp(edgeData.m_Start, edgeData.m_End, hash);
            return true;
        }

        private static void AccumulateStamp(ref uint hash, float3 value)
        {
            // EdgeGeometry can be rebuilt to values differing only by floating
            // point noise after loading. Quantizing to one millimetre keeps
            // those equivalent rebuilds from deleting a durable median while
            // still detecting any meaningful road move or curve edit.
            int3 millimetres = (int3)math.round(value * 1000f);
            hash = (hash ^ (uint)millimetres.x) * 16777619u;
            hash = (hash ^ (uint)millimetres.y) * 16777619u;
            hash = (hash ^ (uint)millimetres.z) * 16777619u;
        }

        private static void AccumulateStamp(ref uint hash, float2 value)
        {
            int2 millimetres = (int2)math.round(value * 1000f);
            hash = (hash ^ (uint)millimetres.x) * 16777619u;
            hash = (hash ^ (uint)millimetres.y) * 16777619u;
        }

        private readonly HashSet<Entity> m_InvalidMedianEdges = new();

        private void CollectActivePairs()
        {
            m_ActivePairs.Clear();
            m_Resolved.Clear();
            m_PendingPairIds.Clear();
            m_RetainedPairAnchors.Clear();
            if (RoadGrassSettings.AutomaticMedianDeletionDisabled)
                foreach (var pairId in m_LastResolvedPairs.Keys) RetainPendingPair(pairId);
            if (m_AllLinkedEdgesQuery.IsEmptyIgnoreFilter)
            {
                ReportPendingPersistentPairs();
                return;
            }

            SeedPairIdAllocator();
            var invalidPairs = new HashSet<ulong>();
            // Composition may be absent during a rebuild; its absence must not
            // exclude durable links before their cached geometry can be retained.
            using NativeArray<Entity> edges = m_AllLinkedEdgesQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i];
                if (!EntityManager.Exists(edge) ||
                    !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge))
                {
                    continue;
                }

                // Counterpart restoration can add buffers; never keep a live ECS buffer across it.
                using var links = EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge).ToNativeArray(Allocator.Temp);
                for (int j = links.Length - 1; j >= 0; j--)
                {
                    RoadGrassMedianPieceLink link = links[j];
                    if (link.PairId == 0)
                    {
                        link.PairId = FindCounterpartPairId(edge, link);
                        if (link.PairId == 0)
                        {
                            link.PairId = AllocatePairId();
                        }
                        AddLinkEntry(edge, link);
                    }
                    if (link.PairedEdge == Entity.Null || link.PairedEdge == edge || !link.Local.IsValid || !link.Paired.IsValid ||
                        IsParentRoadDeleted(edge) || IsParentRoadDeleted(link.PairedEdge))
                    { invalidPairs.Add(link.PairId); continue; }
                    if (!TryResolveSavedAnchor(edge, link.Local, out var local) ||
                        !TryResolveSavedAnchor(link.PairedEdge, link.Paired, out var paired))
                    {
                        // A generated RoadBuilder prefab and its composition can
                        // become available several updates after save entities
                        // have deserialized.  Keep the durable pair and its
                        // mirrors intact so it can be rematched later.
                        if (m_InvalidMedianEdges.Contains(edge) || m_InvalidMedianEdges.Contains(link.PairedEdge))
                            invalidPairs.Add(link.PairId);
                        else RetainPendingPair(link.PairId);
                        continue;
                    }

                    if (local.Node == Entity.Null || local.Node != paired.Node ||
                        local.Key.Edge == paired.Key.Edge)
                    {
                        // Generated anchors can disagree while compositions are rebuilding.
                        // Delete only when the roads themselves confirm a disconnected junction.
                        bool disconnected = edge == link.PairedEdge;
                        if (EntityManager.TryGetComponent(edge, out Edge aEdge) &&
                            EntityManager.TryGetComponent(link.PairedEdge, out Edge bEdge))
                        {
                            Entity aNode = link.Local.End ? aEdge.m_End : aEdge.m_Start;
                            Entity bNode = link.Paired.End ? bEdge.m_End : bEdge.m_Start;
                            disconnected |= aNode != Entity.Null && bNode != Entity.Null && aNode != bNode;
                        }
                        if (!disconnected || RoadGrassSettings.AutomaticMedianDeletionDisabled)
                        { RetainPendingPair(link.PairId); continue; }
                        invalidPairs.Add(link.PairId);
                        Mod.TryWarn(
                            $"Removed saved median pair {link.PairId}: its roads no longer share the saved junction");
                        continue;
                    }
                    bool hasCachedGeometry =
                        m_RenderStates.TryGetValue(local.Key, out MedianRenderState cached) &&
                        cached.Paired.Equals(paired.Key);
                    EdgeGeometry localGeometry = default;
                    EdgeGeometry pairedGeometry = default;
                    if (!hasCachedGeometry && !TryBuildPairGeometry(
                            local,
                            paired,
                            out localGeometry,
                            out pairedGeometry))
                    {
                        // EdgeGeometry is also generated asynchronously while
                        // loading.  A failed visual calculation is not proof
                        // that the player's saved link is invalid.
                        RetainPendingPair(link.PairId);
                        continue;
                    }
                    if ((m_ActivePairs.TryGetValue(local.Key, out var existing) &&
                         !existing.Equals(paired.Key)) ||
                        (m_ActivePairs.TryGetValue(paired.Key, out var pairedExisting) &&
                         !pairedExisting.Equals(local.Key)))
                    {
                        // Ambiguous rematching is not an explicit removal request.
                        RetainPendingPair(link.PairId);
                        continue;
                    }
                    link.Local = local.Data;
                    link.Paired = paired.Data;
                    AddLinkEntry(edge, link);
                    if (!hasCachedGeometry && !CachePairGeometry(
                            local, paired, localGeometry, pairedGeometry))
                    {
                        RetainPendingPair(link.PairId);
                        continue;
                    }
                    m_ActivePairs[local.Key] = paired.Key;
                    m_ActivePairs[paired.Key] = local.Key;
                    m_Resolved[local.Key] = local;
                    m_Resolved[paired.Key] = paired;
                    m_LastResolvedPairs[link.PairId] = (local, paired);
                    m_PendingPairIds.Remove(link.PairId);
                    EnsureCounterpart(edge, link);
                    UpsertPersistentPair(
                        new RoadGrassSavedMedianPair(
                            link.PairId,
                            edge,
                            local.Data,
                            link.PairedEdge,
                            paired.Data));
                }

                if (EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge) &&
                    EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge).Length == 0)
                {
                    EntityManager.RemoveComponent<RoadGrassMedianPieceLink>(edge);
                }
            }

            foreach (ulong pairId in invalidPairs) RemovePairById(pairId);
            PruneTransitionPlans();
            ReportPendingPersistentPairs();
        }

        private void RetainPendingPair(ulong pairId)
        {
            m_PendingPairIds.Add(pairId);
            if (!m_LastResolvedPairs.TryGetValue(pairId, out var pair) ||
                !CanRetain(pair.A, pair.B.Key) || !CanRetain(pair.B, pair.A.Key) ||
                (m_ActivePairs.TryGetValue(pair.A.Key, out var a) && !a.Equals(pair.B.Key)) ||
                (m_ActivePairs.TryGetValue(pair.B.Key, out var b) && !b.Equals(pair.A.Key))) return;
            m_ActivePairs[pair.A.Key] = pair.B.Key;
            m_ActivePairs[pair.B.Key] = pair.A.Key;
            m_Resolved[pair.A.Key] = pair.A;
            m_Resolved[pair.B.Key] = pair.B;
            m_RetainedPairAnchors.Add(pair.A.Key);
            m_RetainedPairAnchors.Add(pair.B.Key);

            bool CanRetain(RoadGrassResolvedMedianAnchor anchor, RoadGrassMedianAnchorKey paired)
            {
                Entity edge = anchor.Key.Edge;
                if (IsParentRoadDeleted(edge)) return false;
                if (RoadGrassSettings.AutomaticMedianDeletionDisabled)
                    return m_RenderStates.TryGetValue(anchor.Key, out var retained) && retained.Paired.Equals(paired);
                if (!EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge) ||
                    !EntityManager.TryGetComponent(edge, out Edge road) ||
                    (anchor.Key.End ? road.m_End : road.m_Start) != anchor.Node ||
                    !m_RenderStates.TryGetValue(anchor.Key, out var rendered) || !rendered.Paired.Equals(paired) ||
                    !m_EdgeStamps.TryGetValue(edge, out var saved)) return false;
                // Missing generated geometry is temporary; changed geometry must be rebuilt.
                return road.m_Start == saved.Start && road.m_End == saved.End &&
                    (!TryCaptureEdgeStamp(edge, out var current) || saved.Equals(current));
            }
        }

        private bool TryResolveSavedAnchor(
            Entity edge,
            RoadGrassMedianAnchorData saved,
            out RoadGrassResolvedMedianAnchor anchor)
        {
            anchor = null;
            return TryGetSelectableAnchors(edge, saved.End, out var anchors, saved.Ordinal <= int.MinValue / 2) &&
                RoadGrassMedianAnchorResolver.TryMatch(anchors, saved, out anchor);
        }

        private void SynchronizeVariants(HashSet<Entity> edges = null)
        {
            m_RemoveKeys.Clear();
            foreach (RoadGrassMedianAnchorKey key in m_Variants.Keys)
            {
                if (edges != null && !edges.Contains(key.Edge)) continue;
                if (!m_RenderPairs.ContainsKey(key) || !m_Resolved.ContainsKey(key) || !m_RenderStates.ContainsKey(key))
                {
                    m_RemoveKeys.Add(key);
                }
            }
            for (int i = 0; i < m_RemoveKeys.Count; i++)
            {
                var key = m_RemoveKeys[i];
                if (m_RenderPairs.ContainsKey(key)) RemoveVariantMesh(key);
                else RemoveVariant(key);
            }

            foreach (KeyValuePair<RoadGrassMedianAnchorKey, RoadGrassMedianAnchorKey> pair in m_RenderPairs)
            {
                if (edges != null && !edges.Contains(pair.Key.Edge)) continue;
                // Preserve the styled mesh too while its source composition is rebuilding.
                if (m_RetainedPairAnchors.Contains(pair.Key) &&
                    m_Variants.TryGetValue(pair.Key, out var retained) &&
                    EntityManager.Exists(retained.Carrier) && EntityManager.Exists(retained.MedianComposition))
                {
                    EnsureCarrierRequested(retained);
                    continue;
                }
                RoadGrassResolvedMedianAnchor anchor = m_ManualKeys.ContainsKey(pair.Key) || m_PolygonRenderKeys.Contains(pair.Key)
                    ? m_Resolved[pair.Key] : GetStyledContinuation(m_Resolved[pair.Key]);
                if (!m_RenderStates.ContainsKey(pair.Key)) continue;
                var transition = pair.Key.Equals(pair.Value) && m_TaperPlans.TryGetValue(pair.Key, out var taper)
                    ? taper.Half : math.abs(anchor.Width - m_Resolved[pair.Value].Width) > 0.01f
                        ? GetTransitionHalf(pair.Key) : null;
                if (math.abs(anchor.Width - m_Resolved[pair.Value].Width) > 0.01f && transition == null) continue;
                if (m_Variants.TryGetValue(pair.Key, out VariantState state))
                {
                    if (state.Signature == anchor.Signature &&
                        ReferenceEquals(state.Transition, transition) &&
                        EntityManager.Exists(state.MedianComposition))
                    {
                        state.Anchor = anchor;
                        EnsureCarrierRequested(state);
                        continue;
                    }
                    // A mesh/style refresh must retain the pair and its geometry.
                    RoadGrassJoinMedianBatchSystem.Instance?.RemoveAnchor(pair.Key);
                    if (EntityManager.Exists(state.Carrier)) EntityManager.DestroyEntity(state.Carrier);
                    ReleaseSharedVariant(state.Shared);
                    m_Variants.Remove(pair.Key);
                    m_PendingFinalize.Remove(pair.Key);
                }
                BuildVariant(anchor, transition);
            }
        }

        private void BuildVariant(RoadGrassResolvedMedianAnchor anchor, RoadGrassTransitionMesh.Half transition = null)
        {
            if (!EntityManager.Exists(anchor.Key.Edge) ||
                !EntityManager.TryGetComponent(anchor.Key.Edge, out Composition composition))
            {
                return;
            }
            Entity source = anchor.Key.End ? composition.m_EndNode : composition.m_StartNode;
            if (source == Entity.Null || !EntityManager.Exists(source) ||
                !EntityManager.HasBuffer<NetCompositionPiece>(source) ||
                !EntityManager.HasComponent<NetCompositionData>(source) ||
                !EntityManager.HasComponent<NetCompositionMeshRef>(source))
            {
                return;
            }

            if (transition != null) anchor = CreateTransitionAnchor(transition);
            SharedVariantState shared = FindSharedVariant(anchor);
            bool createdShared = shared == null;
            if (createdShared)
            {
                Entity medianComposition = EntityManager.Instantiate(source);
                DynamicBuffer<NetCompositionPiece> buffer =
                    EntityManager.GetBuffer<NetCompositionPiece>(medianComposition);
                buffer.Clear();
                for (int i = 0; i < anchor.Pieces.Count; i++)
                {
                    buffer.Add(anchor.Pieces[i]);
                }
                EntityManager.SetComponentData(medianComposition, anchor.StandaloneComposition);
                if (!EntityManager.HasComponent<Created>(medianComposition))
                {
                    EntityManager.AddComponent<Created>(medianComposition);
                }
                shared = new SharedVariantState
                {
                    MedianComposition = medianComposition,
                    CompositionData = anchor.StandaloneComposition,
                    Pieces = new List<NetCompositionPiece>(anchor.Pieces),
                    Signature = anchor.Signature,
                    References = 0
                };
                if (!m_SharedVariants.TryGetValue(
                        anchor.Signature,
                        out List<SharedVariantState> variants))
                {
                    variants = new List<SharedVariantState>();
                    m_SharedVariants.Add(anchor.Signature, variants);
                }
                variants.Add(shared);
            }

            Entity carrier = CreateCarrier(anchor.Key.Edge, shared.MedianComposition);
            if (carrier == Entity.Null)
            {
                if (createdShared)
                {
                    DestroySharedVariant(shared);
                }
                return;
            }
            shared.References++;

            m_Variants[anchor.Key] = new VariantState
            {
                Anchor = anchor,
                MedianComposition = shared.MedianComposition,
                Carrier = carrier,
                Signature = anchor.Signature,
                Shared = shared,
                Transition = transition
            };
            AdvanceRenderRevision();
            if (EntityManager.HasComponent<Created>(shared.MedianComposition))
            {
                m_PendingFinalize.Add(anchor.Key);
            }
        }

        private SharedVariantState FindSharedVariant(RoadGrassResolvedMedianAnchor anchor)
        {
            if (!m_SharedVariants.TryGetValue(
                    anchor.Signature,
                    out List<SharedVariantState> variants))
            {
                return null;
            }
            for (int i = variants.Count - 1; i >= 0; i--)
            {
                SharedVariantState candidate = variants[i];
                if (!EntityManager.Exists(candidate.MedianComposition))
                {
                    variants.RemoveAt(i);
                    continue;
                }
                if (CompositionDataEquals(
                        candidate.CompositionData,
                        anchor.StandaloneComposition) &&
                    CompositionPiecesEqual(candidate.Pieces, anchor.Pieces))
                {
                    return candidate;
                }
            }
            if (variants.Count == 0)
            {
                m_SharedVariants.Remove(anchor.Signature);
            }
            return null;
        }

        private static bool CompositionDataEquals(
            NetCompositionData a,
            NetCompositionData b)
        {
            return math.all(a.m_SyncVertexOffsetsLeft == b.m_SyncVertexOffsetsLeft) &&
                math.all(a.m_SyncVertexOffsetsRight == b.m_SyncVertexOffsetsRight) &&
                a.m_HeightRange.min == b.m_HeightRange.min &&
                a.m_HeightRange.max == b.m_HeightRange.max &&
                a.m_SurfaceHeight.min == b.m_SurfaceHeight.min &&
                a.m_SurfaceHeight.max == b.m_SurfaceHeight.max &&
                math.all(a.m_EdgeHeights == b.m_EdgeHeights) &&
                math.all(a.m_SideConnectionOffset == b.m_SideConnectionOffset) &&
                a.m_Flags == b.m_Flags && a.m_State == b.m_State &&
                a.m_Width == b.m_Width && a.m_MiddleOffset == b.m_MiddleOffset &&
                a.m_WidthOffset == b.m_WidthOffset && a.m_NodeOffset == b.m_NodeOffset &&
                a.m_MinLod == b.m_MinLod;
        }

        private static bool CompositionPiecesEqual(
            List<NetCompositionPiece> a,
            List<NetCompositionPiece> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                NetCompositionPiece left = a[i];
                NetCompositionPiece right = b[i];
                if (left.m_Piece != right.m_Piece ||
                    !math.all(left.m_Offset == right.m_Offset) ||
                    !math.all(left.m_Size == right.m_Size) ||
                    left.m_SectionFlags != right.m_SectionFlags ||
                    left.m_PieceFlags != right.m_PieceFlags ||
                    left.m_SectionIndex != right.m_SectionIndex)
                {
                    return false;
                }
            }
            return true;
        }

        private void ReleaseSharedVariant(SharedVariantState shared)
        {
            if (shared == null)
            {
                return;
            }
            shared.References = math.max(0, shared.References - 1);
            if (shared.References == 0)
            {
                DestroySharedVariant(shared);
            }
        }

        private void DestroySharedVariant(SharedVariantState shared)
        {
            if (shared == null)
            {
                return;
            }
            if (EntityManager.Exists(shared.MedianComposition))
            {
                EntityManager.DestroyEntity(shared.MedianComposition);
            }
            if (m_SharedVariants.TryGetValue(
                    shared.Signature,
                    out List<SharedVariantState> variants))
            {
                variants.Remove(shared);
                if (variants.Count == 0)
                {
                    m_SharedVariants.Remove(shared.Signature);
                }
            }
        }

        private Entity CreateCarrier(Entity edge, Entity medianComposition)
        {
            if (!EntityManager.Exists(edge) || !EntityManager.Exists(medianComposition) ||
                !EntityManager.TryGetComponent(edge, out PrefabRef prefabRef))
            {
                return Entity.Null;
            }
            Entity carrier = EntityManager.CreateEntity(
                ComponentType.ReadWrite<PrefabRef>(),
                ComponentType.ReadWrite<Orphan>(),
                ComponentType.ReadWrite<Game.Tools.Hidden>(),
                ComponentType.ReadWrite<BatchesUpdated>());
            EntityManager.SetComponentData(carrier, prefabRef);
            EntityManager.SetComponentData(
                carrier,
                new Orphan { m_Composition = medianComposition });
            EntityManager.AddBuffer<MeshBatch>(carrier);
            return carrier;
        }

        private void EnsureCarrierRequested(VariantState state)
        {
            if (!EntityManager.Exists(state.Carrier) ||
                !EntityManager.Exists(state.MedianComposition))
            {
                return;
            }
            bool ready = EntityManager.TryGetComponent(
                    state.MedianComposition,
                    out NetCompositionMeshRef meshRef) &&
                meshRef.m_Mesh != Entity.Null && EntityManager.Exists(meshRef.m_Mesh) &&
                EntityManager.HasBuffer<BatchGroup>(meshRef.m_Mesh) &&
                EntityManager.GetBuffer<BatchGroup>(meshRef.m_Mesh, true).Length > 0;
            if (!ready && !EntityManager.HasComponent<BatchesUpdated>(state.Carrier))
            {
                EntityManager.AddComponent<BatchesUpdated>(state.Carrier);
            }
        }

        private void RemoveVariant(RoadGrassMedianAnchorKey key)
        {
            RemoveVariant(key, removeBatch: true);
        }

        private void RemoveVariant(RoadGrassMedianAnchorKey key, bool removeBatch)
        {
            RemoveCrosswalkCaps(key);
            if (removeBatch)
            {
                RoadGrassJoinMedianBatchSystem.Instance?.RemoveAnchor(key);
            }
            bool removed = m_Variants.TryGetValue(key, out VariantState state);
            if (removed)
            {
                if (EntityManager.Exists(state.Carrier))
                {
                    EntityManager.DestroyEntity(state.Carrier);
                }
                ReleaseSharedVariant(state.Shared);
            }
            m_Variants.Remove(key);
            m_ActivePairs.Remove(key);
            m_RetainedPairAnchors.Remove(key);
            m_RenderPairs.Remove(key);
            m_Resolved.Remove(key);
            m_RenderStates.Remove(key);
            m_PendingFinalize.Remove(key);
            if (removed)
            {
                AdvanceRenderRevision();
            }
        }

        private void RemoveVariantMesh(RoadGrassMedianAnchorKey key)
        {
            RoadGrassJoinMedianBatchSystem.Instance?.RemoveAnchor(key);
            if (m_Variants.TryGetValue(key, out var state))
            {
                if (EntityManager.Exists(state.Carrier)) EntityManager.DestroyEntity(state.Carrier);
                ReleaseSharedVariant(state.Shared);
                m_Variants.Remove(key);
                m_PendingFinalize.Remove(key);
                AdvanceRenderRevision();
            }
        }

        private void AdvanceRenderRevision()
        {
            unchecked
            {
                m_RenderRevision++;
            }
            if (m_RenderRevision == 0)
            {
                m_RenderRevision = 1;
            }
        }

        private void RemovePairById(ulong pairId)
        {
            if (pairId == 0)
            {
                return;
            }

            bool known = m_LastResolvedPairs.TryGetValue(pairId, out var previous);
            RemovePersistentPair(pairId);
            var keys = new HashSet<RoadGrassMedianAnchorKey>();
            using NativeArray<Entity> edges = known
                ? new NativeArray<Entity>(new[] { previous.A.Key.Edge, previous.B.Key.Edge }, Allocator.Temp)
                : m_AllLinkedEdgesQuery.ToEntityArray(Allocator.Temp);
            if (!known) m_Dirty = true; // Unresolved/corrupt mirrors still require a complete reconciliation.
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i];
                if (!EntityManager.Exists(edge) ||
                    !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge))
                {
                    continue;
                }
                DynamicBuffer<RoadGrassMedianPieceLink> links =
                    EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge);
                for (int j = links.Length - 1; j >= 0; j--)
                {
                    RoadGrassMedianPieceLink link = links[j];
                    if (link.PairId != pairId)
                    {
                        continue;
                    }
                    keys.Add(new RoadGrassMedianAnchorKey(
                        edge,
                        link.Local.End,
                        link.Local.Ordinal));
                    keys.Add(new RoadGrassMedianAnchorKey(
                        link.PairedEdge,
                        link.Paired.End,
                        link.Paired.Ordinal));
                    links.RemoveAt(j);
                }
                if (links.Length == 0)
                {
                    EntityManager.RemoveComponent<RoadGrassMedianPieceLink>(edge);
                }
            }

            RoadGrassJoinMedianBatchSystem.Instance?.RemoveAnchors(keys);
            foreach (RoadGrassMedianAnchorKey key in keys)
            {
                QueueMedianEdit(key.Edge);
                RemoveVariant(key, removeBatch: false);
            }
            foreach (RoadGrassMedianAnchorKey key in keys)
            {
                if (!EntityManager.Exists(key.Edge) ||
                    !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(key.Edge))
                {
                    m_EdgeStamps.Remove(key.Edge);
                }
            }
            Mod.TryInfo($"Removed complete median pair {pairId}");
        }

        private void RemoveLegacyPair(
            Entity localEdge,
            RoadGrassMedianAnchorKey localKey,
            RoadGrassMedianPieceLink link)
        {
            QueueMedianEdit(localEdge); QueueMedianEdit(link.PairedEdge);
            RemoveCounterpart(localEdge, link);
            if (EntityManager.Exists(localEdge) &&
                EntityManager.HasBuffer<RoadGrassMedianPieceLink>(localEdge))
            {
                DynamicBuffer<RoadGrassMedianPieceLink> links =
                    EntityManager.GetBuffer<RoadGrassMedianPieceLink>(localEdge);
                for (int i = links.Length - 1; i >= 0; i--)
                {
                    if (links[i].Local.End == localKey.End &&
                        links[i].Local.Ordinal == localKey.Ordinal)
                    {
                        links.RemoveAt(i);
                    }
                }
                if (links.Length == 0)
                {
                    EntityManager.RemoveComponent<RoadGrassMedianPieceLink>(localEdge);
                }
            }
            RoadGrassMedianAnchorKey pairedKey = new RoadGrassMedianAnchorKey(
                link.PairedEdge,
                link.Paired.End,
                link.Paired.Ordinal);
            RoadGrassJoinMedianBatchSystem.Instance?.RemoveAnchors(
                new[] { localKey, pairedKey });
            RemoveVariant(localKey, removeBatch: false);
            RemoveVariant(pairedKey, removeBatch: false);
        }

        private void DestroyAllVariants()
        {
            ClearTaperPlans();
            m_RenderPairs.Clear();
            m_RemoveKeys.Clear();
            m_RemoveKeys.AddRange(m_Variants.Keys);
            for (int i = 0; i < m_RemoveKeys.Count; i++)
            {
                RemoveVariant(m_RemoveKeys[i]);
            }
            m_ActivePairs.Clear();
            m_Resolved.Clear();
            m_RenderStates.Clear();
            m_TransitionPlans.Clear();
            m_EdgeStamps.Clear();
            m_StyledEdgeStamps.Clear();
            m_LastResolvedPairs.Clear();
            m_PendingPairIds.Clear();
            m_RetainedPairAnchors.Clear();
            m_SharedVariants.Clear();
            m_PendingFinalize.Clear();
        }

        private void AddLinkEntry(Entity edge, RoadGrassMedianPieceLink link)
        {
            if (!EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge))
            {
                EntityManager.AddBuffer<RoadGrassMedianPieceLink>(edge);
            }
            DynamicBuffer<RoadGrassMedianPieceLink> links =
                EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge);
            for (int i = 0; i < links.Length; i++)
            {
                if ((link.PairId != 0 && links[i].PairId == link.PairId) ||
                    (links[i].Local.End == link.Local.End &&
                    links[i].Local.Ordinal == link.Local.Ordinal &&
                    links[i].PairedEdge == link.PairedEdge &&
                    links[i].Paired.End == link.Paired.End &&
                    links[i].Paired.Ordinal == link.Paired.Ordinal))
                {
                    links[i] = link;
                    return;
                }
            }
            links.Add(link);
        }

        private void RemoveCounterpart(Entity localEdge, RoadGrassMedianPieceLink local)
        {
            if (local.PairedEdge == Entity.Null || !EntityManager.Exists(local.PairedEdge) ||
                !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(local.PairedEdge))
            {
                return;
            }
            DynamicBuffer<RoadGrassMedianPieceLink> paired =
                EntityManager.GetBuffer<RoadGrassMedianPieceLink>(local.PairedEdge);
            for (int i = paired.Length - 1; i >= 0; i--)
            {
                RoadGrassMedianPieceLink candidate = paired[i];
                if ((local.PairId != 0 && candidate.PairId == local.PairId) ||
                    (candidate.PairedEdge == localEdge &&
                    candidate.Local.End == local.Paired.End &&
                    candidate.Local.Ordinal == local.Paired.Ordinal &&
                    candidate.Paired.End == local.Local.End &&
                    candidate.Paired.Ordinal == local.Local.Ordinal))
                {
                    paired.RemoveAt(i);
                }
            }
            if (paired.Length == 0)
            {
                EntityManager.RemoveComponent<RoadGrassMedianPieceLink>(local.PairedEdge);
            }
        }

        private void EnsureCounterpart(Entity localEdge, RoadGrassMedianPieceLink local)
        {
            if (local.PairedEdge == Entity.Null || !EntityManager.Exists(local.PairedEdge))
            {
                return;
            }
            AddLinkEntry(
                local.PairedEdge,
                new RoadGrassMedianPieceLink(
                    local.PairId,
                    localEdge,
                    local.Paired,
                    local.Local));
        }

        private ulong AllocatePairId()
        {
            if (m_NextPairId == 0)
            {
                m_NextPairId = 1;
            }
            return m_NextPairId++;
        }

        private void SeedPairIdAllocator()
        {
            using NativeArray<Entity> edges = m_AllLinkedEdgesQuery.ToEntityArray(Allocator.Temp);
            ulong next = m_NextPairId == 0 ? 1UL : m_NextPairId;
            for (int i = 0; i < edges.Length; i++)
            {
                Entity edge = edges[i];
                if (!EntityManager.Exists(edge) ||
                    !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(edge))
                {
                    continue;
                }
                DynamicBuffer<RoadGrassMedianPieceLink> links =
                    EntityManager.GetBuffer<RoadGrassMedianPieceLink>(edge, true);
                for (int j = 0; j < links.Length; j++)
                {
                    if (links[j].PairId >= next)
                    {
                        next = links[j].PairId + 1;
                    }
                }
            }
            if (TryGetPersistentPairs(create: false, out var pairs))
            {
                for (int i = 0; i < pairs.Length; i++)
                {
                    if (pairs[i].PairId >= next)
                    {
                        next = pairs[i].PairId + 1;
                    }
                }
            }
            m_NextPairId = next == 0 ? 1 : next;
        }

        private ulong FindCounterpartPairId(
            Entity localEdge,
            RoadGrassMedianPieceLink local)
        {
            if (local.PairedEdge == Entity.Null || !EntityManager.Exists(local.PairedEdge) ||
                !EntityManager.HasBuffer<RoadGrassMedianPieceLink>(local.PairedEdge))
            {
                return 0;
            }
            DynamicBuffer<RoadGrassMedianPieceLink> paired =
                EntityManager.GetBuffer<RoadGrassMedianPieceLink>(local.PairedEdge, true);
            for (int i = 0; i < paired.Length; i++)
            {
                RoadGrassMedianPieceLink candidate = paired[i];
                if (candidate.PairedEdge == localEdge &&
                    candidate.Local.End == local.Paired.End &&
                    candidate.Local.Ordinal == local.Paired.Ordinal &&
                    candidate.Paired.End == local.Local.End &&
                    candidate.Paired.Ordinal == local.Local.Ordinal)
                {
                    return candidate.PairId;
                }
            }
            return 0;
        }

        private bool TryBuildPairGeometry(
            RoadGrassResolvedMedianAnchor a,
            RoadGrassResolvedMedianAnchor b,
            out EdgeGeometry aGeometry,
            out EdgeGeometry bGeometry)
        {
            aGeometry = default;
            bGeometry = default;
            if (a == null || b == null || a.Node == Entity.Null || a.Node != b.Node ||
                !TryGetApproachStrip(a, out Segment aApproach) ||
                !TryGetApproachStrip(b, out Segment bApproach))
            {
                return false;
            }

            Segment bTarget = RoadGrassMedianGeometry.InvertSegment(bApproach);
            float startHalfWidth = math.distance(
                aApproach.m_Left.d.xz,
                aApproach.m_Right.d.xz) * 0.5f;
            float endHalfWidth = math.distance(
                bTarget.m_Left.a.xz,
                bTarget.m_Right.a.xz) * 0.5f;
            if (!math.isfinite(startHalfWidth) || !math.isfinite(endHalfWidth) ||
                startHalfWidth < 0.01f || endHalfWidth < 0.01f)
            {
                return false;
            }

            // Share one native center path; rotate its width with the local tangent.
            Bezier4x3 centerApproach = MathUtils.Lerp(
                aApproach.m_Left,
                aApproach.m_Right,
                0.5f);
            Bezier4x3 centerTarget = MathUtils.Lerp(
                bTarget.m_Left,
                bTarget.m_Right,
                0.5f);
            Bezier4x3 fullCenter = FitConnectionCurve(
                centerApproach,
                centerTarget);
            // Use one transverse division for both materials. Separate left
            // and right division positions create a diagonal width step even
            // when the two halves otherwise share their boundary curves.
            float seam = math.clamp(
                NetUtils.FindMiddleTangentPos(fullCenter.xz, new float2(0f, 1f)),
                0.2f,
                0.8f);

            float3 startWidth = (aApproach.m_Right.d - aApproach.m_Left.d) * .5f;
            float3 endWidth = (bTarget.m_Right.a - bTarget.m_Left.a) * .5f;
            RoadGrassTransitionMesh.BuildPairBoundaries(fullCenter, startWidth, endWidth, ref seam,
                out var aLeft, out var aRight, out var bLeft, out var bRight);
            aLeft = LiftCurve(aLeft, ConnectionRenderLift); aRight = LiftCurve(aRight, ConnectionRenderLift);
            bLeft = LiftCurve(bLeft, ConnectionRenderLift); bRight = LiftCurve(bRight, ConnectionRenderLift);
            if (!ValidateBoundaryPair(aLeft, aRight) || !ValidateBoundaryPair(bLeft, bRight)) return false;
            aGeometry = BuildEdgeGeometry(aLeft, aRight);
            bGeometry = BuildEdgeGeometry(
                MathUtils.Invert(bRight),
                MathUtils.Invert(bLeft));
            float stretchFactor = RoadGrassSettings.GetStretchFactor(a.Width, b.Width);
            if (math.abs(a.Width - b.Width) > 0.01f && stretchFactor != 1f)
            {
                if (a.Width < b.Width)
                    aGeometry = RoadGrassTransitionMesh.ScaleSeam(aGeometry, stretchFactor);
                else
                    bGeometry = RoadGrassTransitionMesh.ScaleSeam(bGeometry, stretchFactor);
            }
            return ValidateEdgeGeometry(aGeometry) && ValidateEdgeGeometry(bGeometry) &&
                (math.abs(a.Width - b.Width) <= 0.01f || TryPlanTransition(a, b, aGeometry, bGeometry, out _));
        }

        private bool TryGetApproachStrip(
            RoadGrassResolvedMedianAnchor anchor,
            out Segment result)
        {
            return RoadGrassMedianGeometry.TryGetOrientedApproach(
                EntityManager,
                anchor,
                out result);
        }

        private static Bezier4x3 FitConnectionCurve(
            Bezier4x3 approach,
            Bezier4x3 target)
        {
            float3 startDirection = MathUtils.EndTangent(approach);
            float3 endDirection = MathUtils.StartTangent(target);
            startDirection = MathUtils.Normalize(startDirection, startDirection.xz);
            endDirection = MathUtils.Normalize(endDirection, endDirection.xz);
            startDirection.y = math.clamp(startDirection.y, -1f, 1f);
            endDirection.y = math.clamp(endDirection.y, -1f, 1f);
            return NetUtils.FitCurve(
                approach.d,
                startDirection,
                endDirection,
                target.a);
        }

        private static Bezier4x3 LiftCurve(Bezier4x3 curve, float height)
        {
            curve.a.y += height;
            curve.b.y += height;
            curve.c.y += height;
            curve.d.y += height;
            return curve;
        }

        internal static EdgeGeometry BuildEdgeGeometry(
            Bezier4x3 left,
            Bezier4x3 right)
        {
            // Edge meshes expect two longitudinal curve segments.  Dividing
            // both boundaries at the same parameter keeps the grass and curb
            // vertices synchronized and produces one visually continuous half.
            MathUtils.Divide(left, out Bezier4x3 startLeft, out Bezier4x3 endLeft, 0.5f);
            MathUtils.Divide(right, out Bezier4x3 startRight, out Bezier4x3 endRight, 0.5f);
            EdgeGeometry result = new EdgeGeometry
            {
                m_Start = CreateSegment(startLeft, startRight),
                m_End = CreateSegment(endLeft, endRight),
                m_Bounds = new Bounds3(float.MaxValue, float.MinValue)
            };
            result.m_Bounds |= MathUtils.TightBounds(startLeft);
            result.m_Bounds |= MathUtils.TightBounds(startRight);
            result.m_Bounds |= MathUtils.TightBounds(endLeft);
            result.m_Bounds |= MathUtils.TightBounds(endRight);
            return result;
        }

        private static Segment CreateSegment(
            Bezier4x3 left,
            Bezier4x3 right)
        {
            return new Segment
            {
                m_Left = left,
                m_Right = right,
                m_Length = new float2(
                    MathUtils.Length(left),
                    MathUtils.Length(right))
            };
        }

        private static bool ValidateEdgeGeometry(EdgeGeometry geometry)
        {
            return math.all(geometry.m_Start.m_Length > 0.01f) &&
                math.all(geometry.m_End.m_Length > 0.01f) &&
                ValidateBoundaryPair(
                    geometry.m_Start.m_Left,
                    geometry.m_Start.m_Right) &&
                ValidateBoundaryPair(
                    geometry.m_End.m_Left,
                    geometry.m_End.m_Right);
        }

        private static bool ValidateBoundaryPair(
            Bezier4x3 leftCurve,
            Bezier4x3 rightCurve)
        {
            if (!IsFinite(leftCurve) || !IsFinite(rightCurve))
            {
                return false;
            }

            Bezier4x3 middle = MathUtils.Lerp(leftCurve, rightCurve, 0.5f);
            float expectedSign = 0f;
            for (int i = 0; i <= 16; i++)
            {
                float t = i / 16f;
                float3 left = MathUtils.Position(leftCurve, t);
                float3 right = MathUtils.Position(rightCurve, t);
                float3 center = (left + right) * 0.5f;
                float3 tangent = MathUtils.Tangent(middle, t);
                float2 lateral = right.xz - left.xz;
                float sign = tangent.x * lateral.y - tangent.z * lateral.x;
                if (!math.all(math.isfinite(center)) || math.length(lateral) < 0.02f ||
                    math.abs(sign) < 0.0001f)
                {
                    return false;
                }
                if (expectedSign == 0f)
                {
                    expectedSign = math.sign(sign);
                }
                else if (math.sign(sign) != expectedSign)
                {
                    return false;
                }
            }
            return !CurveSelfIntersects(leftCurve) &&
                !CurveSelfIntersects(rightCurve) &&
                !CurvesIntersect(leftCurve, rightCurve);
        }

        private static bool CurveSelfIntersects(Bezier4x3 curve)
        {
            const int samples = 16;
            for (int i = 0; i < samples; i++)
            {
                float2 a = MathUtils.Position(curve, i / (float)samples).xz;
                float2 b = MathUtils.Position(curve, (i + 1) / (float)samples).xz;
                for (int j = i + 2; j < samples; j++)
                {
                    float2 c = MathUtils.Position(curve, j / (float)samples).xz;
                    float2 d = MathUtils.Position(curve, (j + 1) / (float)samples).xz;
                    if (SegmentsIntersect(a, b, c, d))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool CurvesIntersect(Bezier4x3 aCurve, Bezier4x3 bCurve)
        {
            const int samples = 16;
            for (int i = 0; i < samples; i++)
            {
                float2 a = MathUtils.Position(aCurve, i / (float)samples).xz;
                float2 b = MathUtils.Position(aCurve, (i + 1) / (float)samples).xz;
                for (int j = 0; j < samples; j++)
                {
                    float2 c = MathUtils.Position(bCurve, j / (float)samples).xz;
                    float2 d = MathUtils.Position(bCurve, (j + 1) / (float)samples).xz;
                    if (SegmentsIntersect(a, b, c, d))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(float2 a, float2 b, float2 c, float2 d)
        {
            float abC = Cross(b - a, c - a);
            float abD = Cross(b - a, d - a);
            float cdA = Cross(d - c, a - c);
            float cdB = Cross(d - c, b - c);
            const float epsilon = 0.0001f;
            return abC * abD < -epsilon && cdA * cdB < -epsilon;
        }

        private static float Cross(float2 a, float2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static bool IsFinite(Bezier4x3 curve)
        {
            return math.all(math.isfinite(curve.a)) && math.all(math.isfinite(curve.b)) &&
                math.all(math.isfinite(curve.c)) && math.all(math.isfinite(curve.d));
        }

        [Preserve]
        protected override void OnDestroy()
        {
            RestoreAllEndpointMeshes();
            RoadGrassJoinMedianBatchSystem.Instance?.RemoveAll();
            DestroyAllVariants();
            if (Instance == this)
            {
                Instance = null;
            }
            base.OnDestroy();
        }
    }

    /// <summary>
    /// Invalidates immutable joins only after vanilla has finished rebuilding
    /// EdgeGeometry. This is event-filtered by Updated/Deleted and does no work
    /// for unchanged roads.
    /// </summary>
    [Preserve]
    internal partial class RoadGrassJoinGeometryInvalidationSystem : GameSystemBase
    {
        private EntityQuery m_ChangedLinkedEdgesQuery;
        private EntityQuery m_ChangedStyledEdgesQuery;
        private EntityQuery m_ChangedTaperGeometryQuery;
        private NativeParallelHashMap<Entity, byte> m_TrackedParents;
        private int m_TrackedRevision = -1;
        [BurstCompile]
        private struct GatherChangedParentsJob : IJobChunk
        {
            [ReadOnly] internal EntityTypeHandle Entities;
            [ReadOnly] internal ComponentLookup<Owner> Owners;
            [ReadOnly] internal NativeParallelHashMap<Entity, byte> Tracked;
            internal NativeParallelHashMap<Entity, byte>.ParallelWriter Changed;
            public void Execute(in ArchetypeChunk chunk, int index, bool enabledMask, in v128 mask)
            {
                var entities = chunk.GetNativeArray(Entities);
                foreach (var entity in entities)
                {
                    Entity parent = entity;
                    if (!Tracked.ContainsKey(parent) && Owners.HasComponent(entity)) parent = Owners[entity].m_Owner;
                    if (Tracked.ContainsKey(parent)) Changed.TryAdd(parent, 0);
                }
            }
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_TrackedParents = new NativeParallelHashMap<Entity, byte>(1, Allocator.Persistent);
            _ = GetComponentLookup<Owner>(true);
            m_ChangedLinkedEdgesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<RoadGrassMedianPieceLink>()
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Deleted>()
                }
            });
            m_ChangedStyledEdgesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<RoadGrassStyledEdge>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>() }
            });
            var events = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Created>() };
            m_ChangedTaperGeometryQuery = GetEntityQuery(
                new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Edge>() }, Any = events, None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() } },
                new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Node>() }, Any = events, None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() } },
                new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<Game.Net.Lane>() }, Any = events, None = new[] { ComponentType.ReadOnly<Game.Tools.Temp>() } });
        }

        protected override void OnUpdate()
        {
            var manager = RoadGrassJoinCompositionSystem.Instance;
            if (manager != null && manager.HasTaperPlans && !m_ChangedTaperGeometryQuery.IsEmptyIgnoreFilter)
            {
                if (m_TrackedRevision != manager.TaperDependencyRevision)
                {
                    m_TrackedParents.Clear();
                    if (m_TrackedParents.Capacity < manager.TaperDependencyCount) m_TrackedParents.Capacity = manager.TaperDependencyCount;
                    foreach (var entity in manager.TaperDependencies) m_TrackedParents.TryAdd(entity, 0);
                    m_TrackedRevision = manager.TaperDependencyRevision;
                }
                using var changedParents = new NativeParallelHashMap<Entity, byte>(math.max(1, manager.TaperDependencyCount), Allocator.TempJob);
                Dependency = new GatherChangedParentsJob { Entities = GetEntityTypeHandle(), Owners = GetComponentLookup<Owner>(true),
                    Tracked = m_TrackedParents, Changed = changedParents.AsParallelWriter() }.ScheduleParallel(m_ChangedTaperGeometryQuery, Dependency);
                Dependency.Complete();
                using var geometry = changedParents.GetKeyArray(Allocator.Temp);
                manager.InvalidateTaperGeometry(geometry);
            }
            if (m_ChangedLinkedEdgesQuery.IsEmptyIgnoreFilter && m_ChangedStyledEdgesQuery.IsEmptyIgnoreFilter) return;
            using NativeArray<Entity> changed =
                m_ChangedLinkedEdgesQuery.ToEntityArray(Allocator.Temp);
            RoadGrassJoinCompositionSystem.Instance?.ProcessChangedLinkedEdges(changed);
            using NativeArray<Entity> styled = m_ChangedStyledEdgesQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < styled.Length; i++)
                RoadGrassJoinCompositionSystem.Instance?.InvalidateEndpointStyles(styled[i]);
        }
        protected override void OnDestroy()
        {
            Dependency.Complete();
            if (m_TrackedParents.IsCreated) m_TrackedParents.Dispose();
            base.OnDestroy();
        }
    }

    [Preserve]
    internal partial class RoadGrassJoinCompositionFinalizeSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            RoadGrassJoinCompositionSystem.Instance?.FinalizePendingVariants();
        }
    }

    [Preserve]
    internal partial class RoadGrassJoinSaveRestoreSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            RoadGrassOverridePersistenceSystem.Instance?.SuspendForSerialization();
            RoadGrassJoinCompositionSystem.Instance?.SuspendForSerialization();
        }
    }

    [Preserve]
    internal partial class RoadGrassJoinSaveReapplySystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            RoadGrassJoinCompositionSystem.Instance?.ResumeAfterSerialization();
            RoadGrassOverridePersistenceSystem.Instance?.ResumeAfterSerialization();
        }
    }
}
