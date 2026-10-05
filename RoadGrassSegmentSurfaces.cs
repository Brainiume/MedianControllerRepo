using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private readonly Dictionary<(Entity, int), RoadGrassSavedSegmentSurface> m_SegmentSurfaces = new();
        private readonly Dictionary<Entity, EndpointMeshState> m_SegmentMeshes = new();
        private readonly Dictionary<Entity, List<RoadGrassResolvedMedianAnchor>> m_SegmentAnchors = new();
        private readonly Dictionary<(Entity, int, Entity, MedianSavedSurface), EndpointStylePlan> m_SegmentPlans = new();
        private bool m_SegmentSurfacesLoaded;
        private bool m_PendingSegmentSurfaces;

        private Entity GetOriginalSegment(Entity edge)
        {
            if (!EntityManager.TryGetComponent(edge, out Composition c)) return Entity.Null;
            return m_SegmentMeshes.TryGetValue(edge, out var state) && c.m_Edge == state.Variant
                ? state.Source : c.m_Edge;
        }

        internal List<RoadGrassResolvedMedianAnchor> GetSegmentAnchors(Entity edge)
        {
            Entity source = GetOriginalSegment(edge);
            if (source == Entity.Null || !EntityManager.Exists(source) ||
                EntityManager.HasComponent<Deleted>(edge)) return new List<RoadGrassResolvedMedianAnchor>();
            if (m_SegmentAnchors.TryGetValue(edge, out var cached) && cached.Count > 0 &&
                cached[0].SourceComposition == source) return cached;
            if (!EntityManager.TryGetComponent(source, out NetCompositionData data) ||
                !EntityManager.HasBuffer<NetCompositionPiece>(source) ||
                !RoadGrassMedianAnchorResolver.TryResolveAll(EntityManager, edge, false,
                    GetBufferLookup<NetSubSection>(true), GetBufferLookup<NetSectionPiece>(true),
                    GetComponentLookup<NetPieceData>(true), GetComponentLookup<NetVertexMatchData>(true),
                    out var anchors, data.m_Flags)) return new List<RoadGrassResolvedMedianAnchor>();
            var pieces = EntityManager.GetBuffer<NetCompositionPiece>(source, true);
            // A center divider must have road lanes on both sides. This excludes
            // outer verges even on generated prefabs without Median section flags.
            anchors.RemoveAll(a => !IsInteriorMedian(a, pieces));
            foreach (var anchor in anchors) anchor.SourceComposition = source;
            m_SegmentAnchors[edge] = anchors;
            return anchors;
        }

        private static bool IsInteriorMedian(RoadGrassResolvedMedianAnchor anchor, DynamicBuffer<NetCompositionPiece> pieces)
        {
            bool left = false, right = false;
            for (int i = 0; i < pieces.Length; i++)
            {
                var piece = pieces[i];
                if ((piece.m_PieceFlags & (NetPieceFlags.HasRoadLanes | NetPieceFlags.HasBicycleLanes)) == 0) continue;
                left |= piece.m_Offset.x < anchor.Left;
                right |= piece.m_Offset.x > anchor.Right;
            }
            return left && right;
        }

        private bool TryGetSegmentAnchor(Entity edge, int ordinal, out RoadGrassResolvedMedianAnchor anchor)
        {
            anchor = GetSegmentAnchors(edge).Find(a => a.Key.Ordinal == ordinal);
            return anchor != null;
        }

        internal RoadGrassResolvedMedianAnchor GetSegmentForEndpoint(RoadGrassResolvedMedianAnchor endpoint)
        {
            if (endpoint == null) return null;
            // Start endpoint geometry is inverted, including its ordinal order.
            // Match the physical divider in canonical edge coordinates instead.
            float center = endpoint.Key.End ? endpoint.Center : -endpoint.Center;
            return GetSegmentAnchors(endpoint.Key.Edge).Find(a =>
                a.Data.RootSection == endpoint.Data.RootSection &&
                math.abs(a.Center - center) < 0.01f && math.abs(a.Width - endpoint.Width) < 0.01f);
        }

        private bool TryPlanSegment(RoadGrassResolvedMedianAnchor endpoint, MedianSavedSurface surface,
            out EndpointStylePlan plan)
        {
            plan = null;
            if (endpoint == null || !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle ||
                !TryGetSegmentAnchor(endpoint.Key.Edge, endpoint.Key.Ordinal, out var anchor)) return false;
            var key = (anchor.Key.Edge, anchor.Key.Ordinal, anchor.SourceComposition, surface);
            if (m_SegmentPlans.TryGetValue(key, out plan)) return plan != null;
            m_SegmentPlans[key] = null;
            if (!EntityManager.HasComponent<NetCompositionMeshRef>(anchor.SourceComposition)) return false;
            // Reuse the original grass repair's removal of stop flags for piece
            // selection only. Neither road flags nor outer-edge pieces are changed.
            var flags = WithSurface(RoadGrassMeshInjectSystem.RemoveStopFlags(anchor.RoadComposition.m_Flags), surface);
            if (!TryResolveStyleAnchor(anchor, flags, out var target)) return false;
            var opposite = surface == MedianSavedSurface.Grass ? MedianSavedSurface.Plain : MedianSavedSurface.Grass;
            if (!TryResolveStyleAnchor(anchor, WithSurface(flags, opposite), out var other) ||
                CompositionPiecesEqual(target.Pieces, other.Pieces)) return false;
            int section = anchor.Pieces[0].m_SectionIndex;
            var source = EntityManager.GetBuffer<NetCompositionPiece>(anchor.SourceComposition, true);
            var original = new List<NetCompositionPiece>();
            for (int i = 0; i < source.Length; i++)
            {
                var piece = source[i];
                if (piece.m_SectionIndex != section) continue;
                piece.m_Offset.x -= anchor.Center;
                original.Add(piece);
            }
            if (!CompositionPiecesEqual(original, anchor.Pieces)) return false;
            var replacement = new List<NetCompositionPiece>();
            foreach (var piece in target.Pieces)
            {
                var placed = piece;
                placed.m_SectionIndex = section;
                placed.m_Offset.x += anchor.Center;
                replacement.Add(placed);
            }
            plan = new EndpointStylePlan { Source = anchor.SourceComposition, SectionIndex = section, Pieces = replacement };
            m_SegmentPlans[key] = plan;
            return true;
        }

        internal bool CanEditSegmentSurface(RoadGrassResolvedMedianAnchor anchor, MedianSurfaceChoice choice) =>
            (uint)choice <= 1 && !m_CityQuery.IsEmptyIgnoreFilter && TryPlanSegment(anchor,
                choice == MedianSurfaceChoice.Grass ? MedianSavedSurface.Grass : MedianSavedSurface.Plain, out _);

        internal bool TryEditSegmentSurface(RoadGrassResolvedMedianAnchor anchor, MedianSurfaceChoice choice)
        {
            if (!CanEditSegmentSurface(anchor, choice)) return false;
            StoreSegmentSurface(anchor, choice == MedianSurfaceChoice.Grass ? MedianSavedSurface.Grass : MedianSavedSurface.Plain);
            return true;
        }

        internal bool CanResetSegmentSurface(RoadGrassResolvedMedianAnchor anchor) =>
            anchor != null && m_GameReady && !m_SuspendedForSave && !m_WaitingForLoadSettle &&
            !m_CityQuery.IsEmptyIgnoreFilter;

        internal bool TryResetSegmentSurface(RoadGrassResolvedMedianAnchor anchor)
        {
            if (!CanResetSegmentSurface(anchor)) return false;
            StoreSegmentSurface(anchor, MedianSavedSurface.None);
            return true;
        }

        private bool TryGetSegmentSurfaces(bool create, out DynamicBuffer<RoadGrassSavedSegmentSurface> records)
        {
            records = default;
            if (m_CityQuery.IsEmptyIgnoreFilter) return false;
            var city = m_CityQuery.GetSingletonEntity();
            if (!EntityManager.HasBuffer<RoadGrassSavedSegmentSurface>(city))
            {
                if (!create) return false;
                EntityManager.AddBuffer<RoadGrassSavedSegmentSurface>(city);
            }
            records = EntityManager.GetBuffer<RoadGrassSavedSegmentSurface>(city);
            return true;
        }

        private void StoreSegmentSurface(RoadGrassResolvedMedianAnchor anchor, MedianSavedSurface surface)
        {
            var identity = anchor.Data;
            identity.End = false;
            if (TryGetSegmentAnchor(anchor.Key.Edge, anchor.Key.Ordinal, out var segment)) identity = segment.Data;
            var record = new RoadGrassSavedSegmentSurface { Edge = anchor.Key.Edge, Median = identity, Surface = surface };
            if (m_SegmentSurfaces.TryGetValue(record.Key, out var current)
                ? current.Surface == surface && current.Median.Equals(identity)
                : surface == MedianSavedSurface.None) return;
            InvalidateTransitions(anchor.Key.Edge);
            if (surface == MedianSavedSurface.None) m_SegmentSurfaces.Remove(record.Key);
            else { m_SegmentSurfaces[record.Key] = record; TrackStyledEdge(record.Edge); }
            if (TryGetSegmentSurfaces(surface != MedianSavedSurface.None, out var records))
            {
                for (int i = records.Length - 1; i >= 0; i--)
                    if (records[i].Key.Equals(record.Key)) records.RemoveAt(i);
                if (surface != MedianSavedSurface.None) records.Add(record);
            }
            m_Dirty = true;
        }

        private void HydrateSegmentSurfaces()
        {
            DynamicBuffer<RoadGrassSavedSegmentSurface> records;
            if (!m_SegmentSurfacesLoaded)
            {
                m_SegmentSurfacesLoaded = true;
                if (TryGetSegmentSurfaces(false, out records))
                    for (int i = 0; i < records.Length; i++)
                        if (records[i].Surface != MedianSavedSurface.None) m_SegmentSurfaces[records[i].Key] = records[i];
                foreach (var record in m_SegmentSurfaces.Values) TrackStyledEdge(record.Edge);
            }
            // Older versions stored surfaces at endpoints. Preserve the saved
            // choice in segment space; an existing segment choice wins, otherwise
            // the start endpoint wins if both ends had conflicting choices.
            var legacy = new List<RoadGrassSavedEndpointStyle>(m_Styles.Values);
            legacy.Sort((a, b) => a.Anchor.End.CompareTo(b.Anchor.End));
            foreach (var style in legacy)
            {
                if (style.Surface == MedianSavedSurface.None) continue;
                if (!TryResolveSavedAnchor(style.Edge, style.Anchor, out var endpoint)) continue;
                var segment = GetSegmentForEndpoint(endpoint);
                if (segment == null) continue;
                var key = (style.Edge, segment.Key.Ordinal);
                if (!m_SegmentSurfaces.ContainsKey(key))
                {
                    var identity = segment.Data;
                    var record = new RoadGrassSavedSegmentSurface { Edge = style.Edge, Median = identity, Surface = style.Surface };
                    m_SegmentSurfaces[key] = record;
                    if (TryGetSegmentSurfaces(true, out records)) records.Add(record);
                    TrackStyledEdge(style.Edge);
                }
                var capOnly = style;
                capOnly.Surface = MedianSavedSurface.None;
                StoreEndpointStyle(capOnly);
            }
        }

        private void SynchronizeSegmentMeshes()
        {
            var desired = new Dictionary<Entity, List<EndpointStylePlan>>();
            m_PendingSegmentSurfaces = false;
            foreach (var style in m_Styles.Values)
                m_PendingSegmentSurfaces |= style.Surface != MedianSavedSurface.None;
            foreach (var record in new List<RoadGrassSavedSegmentSurface>(m_SegmentSurfaces.Values))
            {
                if (EntityManager.Exists(record.Edge) && EntityManager.HasComponent<Deleted>(record.Edge))
                { RemoveSegmentRecords(record.Edge); continue; }
                var anchors = GetSegmentAnchors(record.Edge);
                if (anchors.Count == 0)
                { m_PendingSegmentSurfaces = true; continue; }
                // Never silently move a persisted override to a different divider.
                // Reuse the saved-anchor rematcher for regenerated Road Builder
                // prefabs, then require the same ordinal and physical interval.
                if (!RoadGrassMedianAnchorResolver.TryMatch(anchors, record.Median, out var anchor) ||
                    anchor.Key.Ordinal != record.Median.Ordinal ||
                    math.abs(anchor.Data.NormalizedCenter - record.Median.NormalizedCenter) > 0.001f ||
                    math.abs(anchor.Data.Width - record.Median.Width) > 0.01f) continue;
                if (!TryPlanSegment(anchor, record.Surface, out var plan)) continue;
                if (!desired.TryGetValue(record.Edge, out var plans)) desired[record.Edge] = plans = new();
                plans.Add(plan);
            }
            foreach (var edge in new List<Entity>(m_SegmentMeshes.Keys))
                if (!desired.ContainsKey(edge)) RestoreSegmentMesh(edge);
            foreach (var entry in desired)
            {
                var source = entry.Value[0].Source;
                var pieces = new List<NetCompositionPiece>();
                var replacements = new Dictionary<int, EndpointStylePlan>();
                foreach (var plan in entry.Value) replacements[plan.SectionIndex] = plan;
                var emitted = new HashSet<int>();
                var original = EntityManager.GetBuffer<NetCompositionPiece>(source, true);
                for (int i = 0; i < original.Length; i++)
                {
                    var piece = original[i];
                    if (!replacements.TryGetValue(piece.m_SectionIndex, out var plan)) pieces.Add(piece);
                    else if (emitted.Add(piece.m_SectionIndex)) pieces.AddRange(plan.Pieces);
                }
                if (m_SegmentMeshes.TryGetValue(entry.Key, out var current) && current.Source == source &&
                    EntityManager.Exists(current.Variant) && CompositionPiecesEqual(current.Pieces, pieces))
                {
                    var actual = EntityManager.GetComponentData<Composition>(entry.Key);
                    if (actual.m_Edge != current.Variant)
                    {
                        actual.m_Edge = current.Variant;
                        EntityManager.SetComponentData(entry.Key, actual);
                        MarkEndpointBatches(entry.Key);
                    }
                    continue;
                }
                RestoreSegmentMesh(entry.Key);
                var variant = EntityManager.Instantiate(source);
                var buffer = EntityManager.GetBuffer<NetCompositionPiece>(variant);
                buffer.Clear();
                foreach (var piece in pieces) buffer.Add(piece);
                if (!EntityManager.HasComponent<Created>(variant)) EntityManager.AddComponent<Created>(variant);
                m_PendingEndpointMeshes.Add(variant);
                var composition = EntityManager.GetComponentData<Composition>(entry.Key);
                composition.m_Edge = variant;
                EntityManager.SetComponentData(entry.Key, composition);
                MarkEndpointBatches(entry.Key);
                m_SegmentMeshes[entry.Key] = new EndpointMeshState { Source = source, Variant = variant, Pieces = pieces };
            }
        }

        private void RestoreSegmentMesh(Entity edge)
        {
            if (!m_SegmentMeshes.TryGetValue(edge, out var state)) return;
            if (EntityManager.Exists(edge) && EntityManager.TryGetComponent(edge, out Composition composition) &&
                composition.m_Edge == state.Variant)
            {
                composition.m_Edge = state.Source;
                EntityManager.SetComponentData(edge, composition);
                MarkEndpointBatches(edge);
            }
            if (EntityManager.Exists(state.Variant)) EntityManager.DestroyEntity(state.Variant);
            m_PendingEndpointMeshes.Remove(state.Variant);
            m_SegmentMeshes.Remove(edge);
        }

        private void RestoreAllSegmentMeshes()
        {
            foreach (var edge in new List<Entity>(m_SegmentMeshes.Keys)) RestoreSegmentMesh(edge);
            m_SegmentAnchors.Clear();
            m_SegmentPlans.Clear();
        }

        private void RemoveSegmentRecords(Entity edge)
        {
            foreach (var key in new List<(Entity, int)>(m_SegmentSurfaces.Keys))
                if (key.Item1 == edge) m_SegmentSurfaces.Remove(key);
            if (TryGetSegmentSurfaces(false, out var records))
                for (int i = records.Length - 1; i >= 0; i--)
                    if (records[i].Edge == edge) records.RemoveAt(i);
        }
    }
}
