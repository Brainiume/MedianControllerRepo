using System;
using System.Collections.Generic;
using Colossal.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private sealed class EndpointMeshState
        {
            internal Entity Source;
            internal Entity Variant;
            internal List<NetCompositionPiece> Pieces;
        }

        private sealed class EndpointStylePlan
        {
            internal Entity Source;
            internal int SectionIndex;
            internal List<NetCompositionPiece> Pieces;
        }

        private readonly struct StylePlanKey : IEquatable<StylePlanKey>
        {
            private readonly RoadGrassMedianAnchorKey m_Anchor;
            private readonly Entity m_Source;
            private readonly Entity m_EdgeComposition;
            private readonly MedianSavedCap m_Cap;
            private readonly bool m_Extend;
            internal StylePlanKey(RoadGrassResolvedMedianAnchor anchor, Entity source,
                MedianSavedCap cap, bool extend)
            {
                m_Anchor = anchor.Key;
                m_Source = source;
                m_EdgeComposition = anchor.SourceComposition;
                m_Cap = cap;
                m_Extend = extend;
            }
            public bool Equals(StylePlanKey other) => m_Anchor.Equals(other.m_Anchor) &&
                m_Source == other.m_Source && m_EdgeComposition == other.m_EdgeComposition &&
                m_Cap == other.m_Cap && m_Extend == other.m_Extend;
            public override bool Equals(object obj) => obj is StylePlanKey other && Equals(other);
            public override int GetHashCode() => ((m_Anchor.GetHashCode() * 397 ^ m_Source.GetHashCode()) * 397 ^
                m_EdgeComposition.GetHashCode()) * 397 ^ (int)m_Cap;
        }

        private readonly Dictionary<EndpointCacheKey, EndpointMeshState> m_EndpointMeshes = new();
        // Includes rejected plans so hovering an unsupported prefab does not rebuild every frame.
        private readonly Dictionary<StylePlanKey, EndpointStylePlan> m_StylePlans = new();
        private readonly Dictionary<(RoadGrassMedianAnchorKey, Entity, MedianSavedSurface), RoadGrassResolvedMedianAnchor>
            m_SurfaceContinuations = new();
        private readonly Dictionary<RoadGrassMedianAnchorKey, RoadGrassSavedEndpointStyle> m_Styles = new();
        private readonly HashSet<Entity> m_StyledEdges = new();
        private readonly Dictionary<Entity, EdgeRuntimeStamp> m_StyledEdgeStamps = new();
        private readonly List<Entity> m_PendingEndpointMeshes = new();
        private bool m_StylesLoaded;

        internal void ClearStylePreviewCache()
        {
            ClearTaperPlans(false);
            m_SegmentAnchors.Clear();
            m_SegmentPlans.Clear();
            m_StylePlans.Clear();
            m_SurfaceContinuations.Clear();
            m_ResolveCache.Clear();
        }

        private bool TryGetEndpointStyles(bool create, out DynamicBuffer<RoadGrassSavedEndpointStyle> styles)
        {
            styles = default;
            if (m_CityQuery.IsEmptyIgnoreFilter) return false;
            Entity city = m_CityQuery.GetSingletonEntity();
            if (!EntityManager.HasBuffer<RoadGrassSavedEndpointStyle>(city))
            {
                if (!create) return false;
                EntityManager.AddBuffer<RoadGrassSavedEndpointStyle>(city);
            }
            styles = EntityManager.GetBuffer<RoadGrassSavedEndpointStyle>(city);
            return true;
        }

        private Entity GetOriginalEndpoint(RoadGrassMedianAnchorKey key)
        {
            if (!EntityManager.TryGetComponent(key.Edge, out Composition composition)) return Entity.Null;
            Entity source = key.End ? composition.m_EndNode : composition.m_StartNode;
            if (m_EndpointMeshes.TryGetValue(new EndpointCacheKey(key.Edge, key.End), out var state) &&
                source == state.Variant) return state.Source;
            return source;
        }

        private bool TryResolveStyleAnchor(RoadGrassResolvedMedianAnchor anchor, CompositionFlags flags,
            out RoadGrassResolvedMedianAnchor result)
        {
            result = null;
            if (!RoadGrassMedianAnchorResolver.TryResolveAll(EntityManager, anchor.Key.Edge, anchor.Key.End,
                GetBufferLookup<NetSubSection>(true), GetBufferLookup<NetSectionPiece>(true),
                GetComponentLookup<NetPieceData>(true), GetComponentLookup<NetVertexMatchData>(true),
                out var candidates, flags) ||
                !RoadGrassMedianAnchorResolver.TryMatch(candidates, anchor.Data, out result)) return false;
            // A style must not move/widen the divider or select a neighbouring ordinal.
            return result.Data.RootSection == anchor.Data.RootSection &&
                result.Key.Ordinal == anchor.Key.Ordinal &&
                math.abs(result.Center - anchor.Center) < 0.01f &&
                math.abs(result.Width - anchor.Width) < 0.01f;
        }

        private static CompositionFlags WithSurface(CompositionFlags flags, MedianSavedSurface surface)
        {
            if (surface == MedianSavedSurface.None) return flags;
            if (surface == MedianSavedSurface.Grass)
            {
                flags.m_General |= CompositionFlags.General.PrimaryMiddleBeautification;
                flags.m_Left |= CompositionFlags.Side.PrimaryBeautification;
                flags.m_Right |= CompositionFlags.Side.PrimaryBeautification;
            }
            else
            {
                flags.m_General &= ~CompositionFlags.General.PrimaryMiddleBeautification;
                flags.m_Left &= ~CompositionFlags.Side.PrimaryBeautification;
                flags.m_Right &= ~CompositionFlags.Side.PrimaryBeautification;
            }
            return flags;
        }

        private bool TryPlanEndpoint(RoadGrassResolvedMedianAnchor anchor, MedianSavedCap cap,
            out EndpointStylePlan plan, bool extend = false)
        {
            plan = null;
            if (anchor == null || !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle) return false;
            Entity source = GetOriginalEndpoint(anchor.Key);
            var key = new StylePlanKey(anchor, source, cap, extend);
            if (m_StylePlans.TryGetValue(key, out plan)) return plan != null;
            m_StylePlans[key] = null;
            if (source == Entity.Null || !EntityManager.Exists(source) ||
                !EntityManager.TryGetComponent(source, out NetCompositionData sourceData) ||
                !EntityManager.HasBuffer<NetCompositionPiece>(source) ||
                !EntityManager.HasComponent<NetCompositionMeshRef>(source)) return false;

            // Keep node caps and connection continuations distinct. An edge piece
            // inserted as a flat node cap stretches over the node's fan geometry.
            if (!TryResolveStyleAnchor(anchor, sourceData.m_Flags, out var originalCap)) return false;
            CompositionFlags capFlags = MedianEndpointProfiles.GetCapFlags(sourceData.m_Flags, cap);
            if (!TryResolveStyleAnchor(anchor, anchor.RoadComposition.m_Flags, out var continuation) ||
                !TryResolveStyleAnchor(anchor, capFlags, out var endpointCap)) return false;
            if (!extend && cap != MedianSavedCap.None &&
                CompositionPiecesEqual(endpointCap.Pieces, continuation.Pieces))
                return false;
            if (!extend && cap == MedianSavedCap.Straight)
            {
                // A prefab that ignores Crosswalk has not supplied a distinct flat
                // cap. Reject it instead of accepting its ordinary rounded end.
                CompositionFlags withoutCrosswalk = capFlags;
                withoutCrosswalk.m_General &= ~CompositionFlags.General.Crosswalk;
                if (!TryResolveStyleAnchor(anchor, withoutCrosswalk, out var ordinaryCap) ||
                    CompositionPiecesEqual(endpointCap.Pieces, ordinaryCap.Pieces)) return false;
            }
            int sectionIndex = originalCap.Pieces[0].m_SectionIndex;
            var sourcePieces = EntityManager.GetBuffer<NetCompositionPiece>(source, true);
            var original = new List<NetCompositionPiece>();
            for (int i = 0; i < sourcePieces.Length; i++)
            {
                if (sourcePieces[i].m_SectionIndex != sectionIndex) continue;
                var piece = sourcePieces[i];
                piece.m_Offset.x -= originalCap.Center;
                original.Add(piece);
            }
            // Reject ambiguous mappings rather than removing a different road section.
            if (!CompositionPiecesEqual(original, originalCap.Pieces)) return false;
            var replacement = new List<NetCompositionPiece>();
            foreach (var piece in endpointCap.Pieces)
            {
                // Keep shared pavement; replace only this median's curb/cap.
                if (extend && (piece.m_PieceFlags & (NetPieceFlags.PreserveShape | NetPieceFlags.BlockTraffic)) != 0) continue;
                var placed = MedianEndpointProfiles.GetCapPiece(piece, cap);
                placed.m_SectionIndex = sectionIndex;
                placed.m_Offset.x += originalCap.Center;
                replacement.Add(placed);
            }
            if (extend && replacement.Count == original.Count) return false;
            plan = new EndpointStylePlan
            {
                Source = source, SectionIndex = sectionIndex, Pieces = replacement
            };
            m_StylePlans[key] = plan;
            return true;
        }

        internal bool CanEditEndpoint(RoadGrassResolvedMedianAnchor anchor, MedianCapChoice cap, MedianSurfaceChoice surface, bool extendIntoIntersection = false)
        {
            if ((uint)cap > 1 || (uint)surface > 1 || m_CityQuery.IsEmptyIgnoreFilter) return false;
            var savedCap = cap == MedianCapChoice.Rounded ? MedianSavedCap.Rounded : MedianSavedCap.Straight;
            return (!extendIntoIntersection || (anchor != null && !m_ActivePairs.ContainsKey(anchor.Key) && TryGetTaperStart(anchor,out _))) &&
                TryPlanEndpoint(anchor, extendIntoIntersection ? MedianSavedCap.None : savedCap, out _, extendIntoIntersection) &&
                CanEditSegmentSurface(GetSegmentForEndpoint(anchor), surface);
        }

        internal bool TryEditEndpoint(RoadGrassResolvedMedianAnchor anchor, MedianCapChoice cap, MedianSurfaceChoice surface, bool extendIntoIntersection = false)
        {
            // Extension placement needs the second, explicitly selected point.
            if (extendIntoIntersection) return false;
            if (!CanEditEndpoint(anchor, cap, surface, extendIntoIntersection) || !TryGetEndpointStyles(true, out _)) return false;
            StoreEndpointStyle(new RoadGrassSavedEndpointStyle
            {
                Edge = anchor.Key.Edge, Node = anchor.Node, Anchor = anchor.Data,
                Cap = cap == MedianCapChoice.Rounded ? MedianSavedCap.Rounded : MedianSavedCap.Straight,
                Surface = MedianSavedSurface.None,
                ExtendIntoIntersection = extendIntoIntersection
            });
            StoreSegmentSurface(GetSegmentForEndpoint(anchor), surface == MedianSurfaceChoice.Grass ? MedianSavedSurface.Grass : MedianSavedSurface.Plain);
            return true;
        }

        private RoadGrassSavedEndpointStyle GetResetStyle(RoadGrassResolvedMedianAnchor anchor, MedianResetFilters filters)
        {
            m_Styles.TryGetValue(anchor.Key, out var style);
            style.Edge = anchor.Key.Edge;
            style.Node = anchor.Node;
            style.Anchor = anchor.Data;
            if ((filters & MedianResetFilters.EndCap) != 0)
            {
                style.Cap = MedianSavedCap.None;
                style.ExtendIntoIntersection = false;
                style.HasTaperEnd = false;
                style.TaperEndOffset = default;
            }
            if ((filters & MedianResetFilters.Surface) != 0) style.Surface = MedianSavedSurface.None;
            return style;
        }

        internal bool CanResetEndpoint(RoadGrassResolvedMedianAnchor anchor, MedianResetFilters filters)
        {
            if (anchor == null || filters == MedianResetFilters.None || (filters & ~MedianResetFilters.All) != 0 ||
                !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle) return false;
            var style = GetResetStyle(anchor, filters);
            return style.ExtendIntoIntersection || style.Cap == MedianSavedCap.None || TryPlanEndpoint(anchor, style.Cap, out _);
        }

        internal bool TryResetEndpoint(RoadGrassResolvedMedianAnchor anchor, MedianResetFilters filters)
        {
            if (!CanResetEndpoint(anchor, filters)) return false;
            var style = GetResetStyle(anchor, filters);
            // Validation above is complete before either category changes.
            if ((filters & MedianResetFilters.Connection) != 0) RemoveAnchorLinks(anchor.Key);
            if ((filters & MedianResetFilters.Surface) != 0)
            {
                var segment = GetSegmentForEndpoint(anchor);
                if (segment != null) StoreSegmentSurface(segment, MedianSavedSurface.None);
            }
            StoreEndpointStyle(style);
            return true;
        }

        internal bool HasExtension(RoadGrassResolvedMedianAnchor anchor) => anchor != null &&
            m_Styles.TryGetValue(anchor.Key, out var style) && style.Node == anchor.Node && style.ExtendIntoIntersection;

        internal bool TryRemoveExtension(RoadGrassResolvedMedianAnchor anchor)
        {
            if (anchor == null || !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle ||
                !m_Styles.TryGetValue(anchor.Key, out var style) || style.Node != anchor.Node ||
                !style.ExtendIntoIntersection) return false;
            style.ExtendIntoIntersection = false;
            style.HasTaperEnd = false;
            style.TaperEndOffset = default;
            StoreEndpointStyle(style);
            return true;
        }

        private void StoreEndpointStyle(RoadGrassSavedEndpointStyle style)
        {
            ClearTaperPlans();
            bool empty = style.Cap == MedianSavedCap.None && style.Surface == MedianSavedSurface.None && !style.ExtendIntoIntersection;
            if (!TryGetEndpointStyles(!empty, out var styles))
            {
                m_Dirty = true;
                return;
            }
            for (int i = styles.Length - 1; i >= 0; i--)
                if (styles[i].Key.Equals(style.Key)) styles.RemoveAt(i);
            if (empty) m_Styles.Remove(style.Key);
            else
            {
                styles.Add(style);
                m_Styles[style.Key] = style;
                TrackStyledEdge(style.Edge);
            }
            m_Dirty = true;
        }

        private void TrackStyledEdge(Entity edge)
        {
            m_StyledEdges.Add(edge);
            if (!m_StyledEdgeStamps.ContainsKey(edge) && TryCaptureTaperStamp(edge, out var stamp))
                m_StyledEdgeStamps[edge] = stamp;
            if (EntityManager.Exists(edge) && !EntityManager.HasComponent<RoadGrassStyledEdge>(edge))
                EntityManager.AddComponent<RoadGrassStyledEdge>(edge);
        }

        private void HydrateEndpointStyles()
        {
            if (m_StylesLoaded) return;
            m_StylesLoaded = true;
            m_Styles.Clear();
            if (!TryGetEndpointStyles(false, out var styles)) return;
            var copy = new List<RoadGrassSavedEndpointStyle>();
            for (int i = 0; i < styles.Length; i++) copy.Add(styles[i]);
            styles.Clear();
            foreach (var saved in copy)
            {
                if (!EntityManager.Exists(saved.Edge) || EntityManager.HasComponent<Deleted>(saved.Edge) ||
                    (saved.Cap == MedianSavedCap.None && saved.Surface == MedianSavedSurface.None && !saved.ExtendIntoIntersection)) continue;
                var style = saved;
                if (TryResolveSavedAnchor(saved.Edge, saved.Anchor, out var anchor))
                {
                    if (anchor.Node != saved.Node) continue;
                    style.Anchor = anchor.Data;
                }
                // A generated prefab may still be unavailable after loading. Preserve its
                // record until a real road update can resolve or prune it; never drop city
                // data just because rendering is not ready in this frame.
                // Canonical dictionary also removes duplicate loaded records.
                m_Styles[style.Key] = style;
            }
            foreach (var entry in m_Styles.Values)
            {
                // Structural writes invalidate DynamicBuffer handles: reacquire after markers.
                TrackStyledEdge(entry.Edge);
                EntityManager.GetBuffer<RoadGrassSavedEndpointStyle>(m_CityQuery.GetSingletonEntity()).Add(entry);
            }
        }

        private void SynchronizeEndpointMeshes()
        {
            var desired = new Dictionary<EndpointCacheKey, List<EndpointStylePlan>>();
            // Only explicit cap edits own road endpoint meshes. Connections and
            // surface choices belong exclusively to the standalone median instance.
            foreach (var entry in m_Styles)
            {
                var key = entry.Key;
                var style = entry.Value;
                if ((!style.ExtendIntoIntersection && style.Cap == MedianSavedCap.None) ||
                    !TryResolveSavedAnchor(style.Edge, style.Anchor, out var anchor) ||
                    anchor.Node != style.Node || !anchor.Key.Equals(key)) continue;
                bool extend = style.ExtendIntoIntersection && m_RenderPairs.TryGetValue(key, out var paired) &&
                    paired.Equals(key) && IsTaperMeshReady(key);
                // A temporarily unavailable taper keeps the saved ordinary cap.
                if (!extend && style.Cap == MedianSavedCap.None) continue;
                if (!TryPlanEndpoint(anchor, extend ? MedianSavedCap.None : style.Cap, out var plan, extend)) continue;
                var endpoint = new EndpointCacheKey(key.Edge, key.End);
                if (!desired.TryGetValue(endpoint, out var plans)) desired.Add(endpoint, plans = new List<EndpointStylePlan>());
                plans.Add(plan);
            }
            foreach (var median in m_ManualMedians.Values)
            {
                if (median.Points.Count >= 3)
                {
                    if (TryGetPolygonCapPlan(median, out var polygonPlan))
                    {
                        var polygonEndpoint = new EndpointCacheKey(median.Saved.Edge, median.Saved.Anchor.End);
                        if (!desired.TryGetValue(polygonEndpoint, out var polygonPlans)) desired.Add(polygonEndpoint, polygonPlans = new List<EndpointStylePlan>());
                        polygonPlans.Add(polygonPlan);
                    }
                    continue;
                }
                var key = median.Saved.Key;
                if (!m_RenderPairs.TryGetValue(key, out var pair) || !pair.Equals(key) || !IsTaperMeshReady(key) ||
                    !TryResolveManual(median, out var anchor, out _) ||
                    !TryPlanEndpoint(anchor, MedianSavedCap.None, out var plan, true)) continue;
                var endpoint = new EndpointCacheKey(key.Edge, key.End);
                if (!desired.TryGetValue(endpoint, out var plans)) desired.Add(endpoint, plans = new List<EndpointStylePlan>());
                plans.Add(plan);
            }
            var obsolete = new List<EndpointCacheKey>();
            foreach (var key in m_EndpointMeshes.Keys) if (!desired.ContainsKey(key)) obsolete.Add(key);
            foreach (var key in obsolete) RestoreEndpointMesh(key);
            foreach (var entry in desired)
            {
                TrackStyledEdge(entry.Key.Edge);
                Entity source = entry.Value[0].Source;
                var sourcePieces = EntityManager.GetBuffer<NetCompositionPiece>(source, true);
                var pieces = new List<NetCompositionPiece>();
                var replacements = new Dictionary<int, EndpointStylePlan>();
                foreach (var plan in entry.Value) replacements[plan.SectionIndex] = plan;
                var emitted = new HashSet<int>();
                for (int i = 0; i < sourcePieces.Length; i++)
                {
                    var piece = sourcePieces[i];
                    if (!replacements.TryGetValue(piece.m_SectionIndex, out var plan)) pieces.Add(piece);
                    else if (emitted.Add(piece.m_SectionIndex)) pieces.AddRange(plan.Pieces);
                }
                if (m_EndpointMeshes.TryGetValue(entry.Key, out var current) && current.Source == source &&
                    EntityManager.Exists(current.Variant) && CompositionPiecesEqual(current.Pieces, pieces))
                {
                    var existing = EntityManager.GetComponentData<Composition>(entry.Key.Edge);
                    if ((entry.Key.End ? existing.m_EndNode : existing.m_StartNode) != current.Variant)
                    {
                        if (entry.Key.End) existing.m_EndNode = current.Variant;
                        else existing.m_StartNode = current.Variant;
                        EntityManager.SetComponentData(entry.Key.Edge, existing);
                        MarkEndpointBatches(entry.Key.Edge);
                    }
                    continue;
                }
                RestoreEndpointMesh(entry.Key);
                Entity variant = EntityManager.Instantiate(source);
                var buffer = EntityManager.GetBuffer<NetCompositionPiece>(variant);
                buffer.Clear();
                foreach (var piece in pieces) buffer.Add(piece);
                if (!EntityManager.HasComponent<Created>(variant)) EntityManager.AddComponent<Created>(variant);
                m_PendingEndpointMeshes.Add(variant);
                var composition = EntityManager.GetComponentData<Composition>(entry.Key.Edge);
                if (entry.Key.End) composition.m_EndNode = variant;
                else composition.m_StartNode = variant;
                EntityManager.SetComponentData(entry.Key.Edge, composition);
                MarkEndpointBatches(entry.Key.Edge);
                m_EndpointMeshes[entry.Key] = new EndpointMeshState { Source = source, Variant = variant, Pieces = pieces };
            }
            var retained = new HashSet<Entity>();
            foreach (var style in m_Styles.Values) retained.Add(style.Edge);
            foreach (var surface in m_SegmentSurfaces.Values) retained.Add(surface.Edge);
            foreach (var endpoint in m_EndpointMeshes.Keys) retained.Add(endpoint.Edge);
            foreach (var edge in new List<Entity>(m_StyledEdges))
            {
                if (retained.Contains(edge)) continue;
                if (EntityManager.Exists(edge) && EntityManager.HasComponent<RoadGrassStyledEdge>(edge))
                    EntityManager.RemoveComponent<RoadGrassStyledEdge>(edge);
                m_StyledEdges.Remove(edge);
                m_StyledEdgeStamps.Remove(edge);
            }
        }

        private void MarkEndpointBatches(Entity edge)
        {
            if (EntityManager.Exists(edge) && !EntityManager.HasComponent<BatchesUpdated>(edge))
                EntityManager.AddComponent<BatchesUpdated>(edge);
        }

        private void RestoreEndpointMesh(EndpointCacheKey key)
        {
            if (!m_EndpointMeshes.TryGetValue(key, out var state)) return;
            if (EntityManager.Exists(key.Edge) && EntityManager.TryGetComponent(key.Edge, out Composition composition))
            {
                bool replaced = key.End ? composition.m_EndNode == state.Variant : composition.m_StartNode == state.Variant;
                if (replaced)
                {
                    if (key.End) composition.m_EndNode = state.Source;
                    else composition.m_StartNode = state.Source;
                    EntityManager.SetComponentData(key.Edge, composition);
                    MarkEndpointBatches(key.Edge);
                }
            }
            if (EntityManager.Exists(state.Variant)) EntityManager.DestroyEntity(state.Variant);
            m_EndpointMeshes.Remove(key);
            m_PendingEndpointMeshes.Remove(state.Variant);
        }

        private void RestoreAllEndpointMeshes()
        {
            RestoreAllSegmentMeshes();
            foreach (var key in new List<EndpointCacheKey>(m_EndpointMeshes.Keys)) RestoreEndpointMesh(key);
            m_StylePlans.Clear();
            m_SurfaceContinuations.Clear();
        }

        private void ClearEndpointStyles()
        {
            RestoreAllEndpointMeshes();
            if (TryGetSegmentSurfaces(false, out var surfaces)) surfaces.Clear();
            m_SegmentSurfaces.Clear();
            m_PendingSegmentSurfaces = false;
            if (TryGetEndpointStyles(false, out var styles)) styles.Clear();
            foreach (var edge in m_StyledEdges)
                if (EntityManager.Exists(edge) && EntityManager.HasComponent<RoadGrassStyledEdge>(edge))
                    EntityManager.RemoveComponent<RoadGrassStyledEdge>(edge);
            m_StyledEdges.Clear();
            m_StyledEdgeStamps.Clear();
            m_Styles.Clear();
        }

        private RoadGrassResolvedMedianAnchor GetStyledContinuation(RoadGrassResolvedMedianAnchor anchor)
        {
            var segment = GetSegmentForEndpoint(anchor);
            MedianSavedSurface surface = MedianSavedSurface.None;
            if (segment != null && m_SegmentSurfaces.TryGetValue((segment.Key.Edge, segment.Key.Ordinal), out var style))
                surface = style.Surface;
            else if (m_Styles.TryGetValue(anchor.Key, out var legacy)) surface = legacy.Surface;
            if (surface != MedianSavedSurface.None &&
                TryResolveSurfaceContinuation(anchor, surface, out var continuation)) return continuation;
            return anchor;
        }

        private bool TryResolveSurfaceContinuation(RoadGrassResolvedMedianAnchor anchor,
            MedianSavedSurface surface, out RoadGrassResolvedMedianAnchor continuation)
        {
            continuation = null;
            if (anchor == null || !m_GameReady || m_SuspendedForSave || m_WaitingForLoadSettle) return false;
            var key = (anchor.Key, anchor.SourceComposition, surface);
            if (m_SurfaceContinuations.TryGetValue(key, out continuation)) return continuation != null;
            m_SurfaceContinuations[key] = null;
            if (!TryResolveStyleAnchor(anchor, WithSurface(anchor.RoadComposition.m_Flags, surface),
                out continuation)) return false;
            if (surface != MedianSavedSurface.None)
            {
                var opposite = surface == MedianSavedSurface.Grass ? MedianSavedSurface.Plain : MedianSavedSurface.Grass;
                if (!TryResolveStyleAnchor(anchor, WithSurface(anchor.RoadComposition.m_Flags, opposite),
                    out var other) || CompositionPiecesEqual(continuation.Pieces, other.Pieces)) return false;
            }
            // The existing standalone renderer uses these pieces and composition;
            // no road-owned composition is replaced to change the generated surface.
            continuation.Data = anchor.Data;
            continuation.Key = anchor.Key;
            m_SurfaceContinuations[key] = continuation;
            return true;
        }

        internal void InvalidateEndpointStyles(Entity edge)
        {
            bool deleted = !EntityManager.Exists(edge) || EntityManager.HasComponent<Deleted>(edge);
            if (!deleted)
            {
                if (!TryCaptureTaperStamp(edge, out var current)) return;
                if (m_StyledEdgeStamps.TryGetValue(edge, out var saved) && saved.Equals(current))
                {
                    // Native composition replacement may need the cap/surface
                    // reapplied, but must not invalidate any polygon geometry.
                    if (EntityManager.TryGetComponent(edge, out Composition composition) &&
                        ((m_SegmentMeshes.TryGetValue(edge, out var segment) && composition.m_Edge != segment.Variant) ||
                         (m_EndpointMeshes.TryGetValue(new EndpointCacheKey(edge, false), out var start) && composition.m_StartNode != start.Variant) ||
                         (m_EndpointMeshes.TryGetValue(new EndpointCacheKey(edge, true), out var end) && composition.m_EndNode != end.Variant)))
                        m_Dirty = true;
                    return;
                }
                m_StyledEdgeStamps[edge] = current;
            }
            else m_StyledEdgeStamps.Remove(edge);
            ClearTaperPlans();
            InvalidateTransitions(edge);
            m_SegmentAnchors.Remove(edge);
            m_SegmentPlans.Clear();
            m_StylePlans.Clear();
            m_SurfaceContinuations.Clear();
            m_ResolveCache.Clear();
            if (deleted) RemoveSegmentRecords(edge);
            if (TryGetEndpointStyles(false, out var styles))
            {
                for (int i = styles.Length - 1; i >= 0; i--)
                {
                    var saved = styles[i];
                    if (saved.Edge != edge) continue;
                    if (deleted || !TryResolveSavedAnchor(edge, saved.Anchor, out var anchor) ||
                        saved.Node != anchor.Node || !saved.Key.Equals(anchor.Key))
                    {
                        m_Styles.Remove(saved.Key);
                        styles.RemoveAt(i);
                    }
                }
            }
            m_Dirty = true;
        }
    }
}
