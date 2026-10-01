using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal readonly struct RoadGrassMedianAnchorKey : IEquatable<RoadGrassMedianAnchorKey>
    {
        internal readonly Entity Edge;
        internal readonly bool End;
        internal readonly int Ordinal;

        internal RoadGrassMedianAnchorKey(Entity edge, bool end, int ordinal)
        {
            Edge = edge;
            End = end;
            Ordinal = ordinal;
        }

        public bool Equals(RoadGrassMedianAnchorKey other)
        {
            return Edge == other.Edge && End == other.End && Ordinal == other.Ordinal;
        }

        public override bool Equals(object obj)
        {
            return obj is RoadGrassMedianAnchorKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Edge.GetHashCode();
                hash = (hash * 397) ^ End.GetHashCode();
                return (hash * 397) ^ Ordinal;
            }
        }

        public override string ToString()
        {
            return $"{Edge}:{(End ? "end" : "start")}:median-{Ordinal}";
        }
    }

    internal sealed class RoadGrassResolvedMedianAnchor
    {
        internal RoadGrassMedianAnchorKey Key;
        internal Entity Node;
        internal RoadGrassMedianAnchorData Data;
        internal Entity SourceComposition;
        internal NetCompositionData RoadComposition;
        internal NetCompositionData StandaloneComposition;
        internal List<NetCompositionPiece> Pieces;
        internal float Center;
        internal float Width;
        internal float Left;
        internal float Right;
        internal int Signature;
    }

    internal static class RoadGrassMedianAnchorResolver
    {
        internal static bool TryResolveAll(
            EntityManager entityManager,
            Entity edgeEntity,
            bool end,
            BufferLookup<NetSubSection> subSections,
            BufferLookup<NetSectionPiece> sectionPieces,
            ComponentLookup<NetPieceData> pieceData,
            ComponentLookup<NetVertexMatchData> vertexMatchData,
            out List<RoadGrassResolvedMedianAnchor> anchors,
            CompositionFlags? requestedFlags = null)
        {
            anchors = new List<RoadGrassResolvedMedianAnchor>();
            if (edgeEntity == Entity.Null || !entityManager.Exists(edgeEntity) ||
                entityManager.HasComponent<Deleted>(edgeEntity) ||
                !entityManager.TryGetComponent(edgeEntity, out Edge edge) ||
                !entityManager.TryGetComponent(edgeEntity, out Composition composition) ||
                !entityManager.TryGetComponent(composition.m_Edge, out NetCompositionData edgeData) ||
                !entityManager.TryGetComponent(composition.m_Edge, out PrefabRef compositionPrefab) ||
                !entityManager.HasBuffer<NetGeometrySection>(compositionPrefab.m_Prefab))
            {
                return false;
            }

            Entity node = end ? edge.m_End : edge.m_Start;
            CompositionFlags flags = requestedFlags ?? GetEndpointMedianFlags(edgeData.m_Flags, end);

            DynamicBuffer<NetGeometrySection> geometrySections =
                entityManager.GetBuffer<NetGeometrySection>(compositionPrefab.m_Prefab, true);
            var pieces = new NativeList<NetCompositionPiece>(Allocator.Temp);
            NetCompositionHelpers.GetCompositionPieces(
                pieces,
                geometrySections.AsNativeArray(),
                flags,
                subSections,
                sectionPieces);

            NetCompositionData resolvedData = edgeData;
            resolvedData.m_Flags = flags;
            NetCompositionHelpers.CalculateCompositionData(
                ref resolvedData,
                pieces.AsArray(),
                pieceData,
                vertexMatchData);

            var activeRoots = GetActiveRoots(
                geometrySections,
                flags,
                subSections,
                sectionPieces);
            int ordinal = 0;
            int index = 0;
            while (index < pieces.Length)
            {
                int sectionIndex = pieces[index].m_SectionIndex;
                Entity root = sectionIndex >= 0 && sectionIndex < activeRoots.Count
                    ? activeRoots[sectionIndex]
                    : Entity.Null;
                int endIndex = index + 1;
                while (endIndex < pieces.Length &&
                       pieces[endIndex].m_SectionIndex == sectionIndex)
                {
                    endIndex++;
                }

                bool blockingDivider = false;
                bool containsVehicleLane = false;
                bool visibleMedianMesh = false;
                Entity representative = Entity.Null;
                int representativeIndex = -1;
                var group = new List<NetCompositionPiece>(endIndex - index);
                for (int i = index; i < endIndex; i++)
                {
                    NetCompositionPiece piece = pieces[i];
                    group.Add(piece);
                    blockingDivider |=
                        (piece.m_PieceFlags & NetPieceFlags.BlockTraffic) != 0;
                    containsVehicleLane |=
                        (piece.m_PieceFlags &
                         (NetPieceFlags.HasRoadLanes |
                          NetPieceFlags.HasBicycleLanes)) != 0;
                    bool visibleMesh =
                        (piece.m_PieceFlags & NetPieceFlags.HasMesh) != 0 &&
                        (piece.m_SectionFlags &
                         (NetSectionFlags.Hidden | NetSectionFlags.Underground |
                          NetSectionFlags.Overhead)) == 0;
                    if (!visibleMesh)
                    {
                        continue;
                    }

                    visibleMedianMesh = true;
                    // Prefer the physical divider mesh as the stable identity.
                    // Grass/top meshes can be swapped by composition flags,
                    // while the traffic-blocking curb/divider is what both
                    // vanilla and RoadBuilder use to identify a real median.
                    if (representative == Entity.Null ||
                        ((piece.m_PieceFlags & NetPieceFlags.BlockTraffic) != 0 &&
                         (group[representativeIndex].m_PieceFlags &
                          NetPieceFlags.BlockTraffic) == 0))
                    {
                        representative = piece.m_Piece;
                        representativeIndex = i - index;
                    }
                }

                bool prefabBlockingDivider =
                    HasBlockingDividerDefinition(root, sectionPieces);
                // RoadBuilder's generated 1 m / 2 m / 5 m median sections do
                // not consistently set NetSectionInfo.m_Median, and their
                // active node variants do not consistently retain
                // BlockTraffic. Its own IsMedian implementation instead scans
                // the complete section prefab for a blocking divider. Mirror
                // that identity here, then reject anything that actually
                // contains road or bicycle lanes.
                bool physicalMedian = blockingDivider || prefabBlockingDivider;
                if (physicalMedian && !containsVehicleLane && visibleMedianMesh &&
                    representative != Entity.Null && representativeIndex >= 0)
                {
                    // CalculateCompositionData has already placed every piece
                    // in full-road coordinates. All alternatives in one
                    // section share this section centre; using it directly is
                    // reliable for narrow and asymmetric RoadBuilder medians.
                    float center = group[0].m_Offset.x;
                    var standalonePieces = new NativeArray<NetCompositionPiece>(
                        group.Count,
                        Allocator.Temp);
                    for (int i = 0; i < group.Count; i++)
                    {
                        NetCompositionPiece standalonePiece = group[i];
                        // The full-road calculation has already translated the
                        // section to its lateral road position. A standalone
                        // median composition must begin at the section's local
                        // origin or that translation is applied a second time,
                        // which is especially visible on asymmetric roads.
                        standalonePiece.m_Offset.x -= center;
                        standalonePieces[i] = standalonePiece;
                    }

                    NetCompositionData standalone = edgeData;
                    standalone.m_Flags = flags;
                    NetCompositionHelpers.CalculateCompositionData(
                        ref standalone,
                        standalonePieces,
                        pieceData,
                        vertexMatchData);
                    float width = standalone.m_Width;
                    group.Clear();
                    for (int i = 0; i < standalonePieces.Length; i++)
                    {
                        group.Add(standalonePieces[i]);
                    }
                    standalonePieces.Dispose();

                    if (!math.isfinite(center) || !math.isfinite(width) || width <= 0.01f)
                    {
                        index = endIndex;
                        continue;
                    }
                    var data = new RoadGrassMedianAnchorData(
                        end,
                        ordinal,
                        root,
                        representative,
                        center / math.max(0.01f, resolvedData.m_Width),
                        width);
                    anchors.Add(new RoadGrassResolvedMedianAnchor
                    {
                        Key = new RoadGrassMedianAnchorKey(edgeEntity, end, ordinal),
                        Node = node,
                        Data = data,
                        SourceComposition = composition.m_Edge,
                        RoadComposition = resolvedData,
                        StandaloneComposition = standalone,
                        Pieces = group,
                        Center = center,
                        Width = width,
                        Left = center - width * 0.5f,
                        Right = center + width * 0.5f,
                        Signature = CalculateSignature(data, group)
                    });
                    ordinal++;
                }

                index = endIndex;
            }

            pieces.Dispose();
            return anchors.Count > 0;
        }

        internal static bool TryMatch(
            List<RoadGrassResolvedMedianAnchor> anchors,
            RoadGrassMedianAnchorData saved,
            out RoadGrassResolvedMedianAnchor result)
        {
            result = null;
            if (saved.Ordinal >= 0 && saved.Ordinal < anchors.Count)
            {
                RoadGrassResolvedMedianAnchor ordinal = anchors[saved.Ordinal];
                bool rootMatches = saved.RootSection == Entity.Null ||
                    ordinal.Data.RootSection == saved.RootSection;
                bool pieceMatches = saved.RepresentativePiece == Entity.Null ||
                    ordinal.Data.RepresentativePiece == saved.RepresentativePiece;
                if (rootMatches && pieceMatches)
                {
                    result = ordinal;
                    return true;
                }
            }

            if (TryFindNearest(anchors, saved, requirePrefab: true, out result))
            {
                return true;
            }

            // Generated RoadBuilder prefab entities may be reconstructed on
            // load. In that case match only when lateral placement, width and
            // ordinal identify a unique median section.
            if (TryFindNearest(anchors, saved, requirePrefab: false, out result))
            {
                return true;
            }

            result = null;
            return false;
        }

        private static bool TryFindNearest(
            List<RoadGrassResolvedMedianAnchor> anchors,
            RoadGrassMedianAnchorData saved,
            bool requirePrefab,
            out RoadGrassResolvedMedianAnchor result)
        {
            result = null;
            float bestScore = float.MaxValue;
            bool ambiguous = false;
            for (int i = 0; i < anchors.Count; i++)
            {
                RoadGrassResolvedMedianAnchor candidate = anchors[i];
                bool prefabMatch =
                    (saved.RootSection != Entity.Null &&
                     candidate.Data.RootSection == saved.RootSection) ||
                    (saved.RepresentativePiece != Entity.Null &&
                     candidate.Data.RepresentativePiece == saved.RepresentativePiece);
                if (requirePrefab && !prefabMatch)
                {
                    continue;
                }

                float centerDelta = math.abs(
                    candidate.Data.NormalizedCenter - saved.NormalizedCenter);
                float widthDelta = math.abs(candidate.Width - saved.Width) /
                    math.max(0.25f, saved.Width);
                float ordinalDelta = math.abs(candidate.Data.Ordinal - saved.Ordinal);
                float score = centerDelta * 4f + widthDelta + ordinalDelta * 0.25f;
                if (score + 0.01f < bestScore)
                {
                    bestScore = score;
                    result = candidate;
                    ambiguous = false;
                }
                else if (math.abs(score - bestScore) <= 0.01f)
                {
                    ambiguous = true;
                }
            }

            float maximumScore = requirePrefab ? 0.75f : 0.5f;
            if (ambiguous || result == null || bestScore > maximumScore)
            {
                result = null;
                return false;
            }
            return true;
        }

        internal static CompositionFlags GetEndpointMedianFlags(
            CompositionFlags edgeFlags,
            bool end)
        {
            CompositionFlags flags = edgeFlags;
            const CompositionFlags.Side stops = CompositionFlags.Side.PrimaryStop |
                CompositionFlags.Side.SecondaryStop | CompositionFlags.Side.TertiaryStop;
            flags.m_Left &= ~stops;
            flags.m_Right &= ~stops;
            if (!end)
            {
                flags = NetCompositionHelpers.InvertCompositionFlags(flags);
                flags.m_General ^= CompositionFlags.General.Invert;
            }
            return flags;
        }

        private static List<Entity> GetActiveRoots(
            DynamicBuffer<NetGeometrySection> sections,
            CompositionFlags flags,
            BufferLookup<NetSubSection> subSections,
            BufferLookup<NetSectionPiece> sectionPieces)
        {
            var roots = new List<Entity>();
            CompositionFlags invertedFlags =
                NetCompositionHelpers.InvertCompositionFlags(flags);
            invertedFlags.m_Left =
                (invertedFlags.m_Left & ~CompositionFlags.Side.AbruptEnd) |
                (flags.m_Left & CompositionFlags.Side.AbruptEnd);
            invertedFlags.m_Right =
                (invertedFlags.m_Right & ~CompositionFlags.Side.AbruptEnd) |
                (flags.m_Right & CompositionFlags.Side.AbruptEnd);

            bool invertOrder =
                (flags.m_General & CompositionFlags.General.Invert) != 0;
            for (int j = 0; j < sections.Length; j++)
            {
                int i = invertOrder ? sections.Length - j - 1 : j;
                NetGeometrySection section = sections[i];
                if (invertOrder)
                {
                    section.m_Flags ^=
                        NetSectionFlags.Invert | NetSectionFlags.FlipLanes;
                    if ((section.m_Flags & NetSectionFlags.Left) != 0)
                    {
                        section.m_Flags =
                            (section.m_Flags & ~NetSectionFlags.Left) |
                            NetSectionFlags.Right;
                    }
                    else if ((section.m_Flags & NetSectionFlags.Right) != 0)
                    {
                        section.m_Flags =
                            (section.m_Flags & ~NetSectionFlags.Right) |
                            NetSectionFlags.Left;
                    }
                }

                if ((flags.m_General & CompositionFlags.General.Flip) != 0)
                {
                    section.m_Flags ^= NetSectionFlags.FlipLanes;
                }

                CompositionFlags sectionCompositionFlags =
                    (section.m_Flags & NetSectionFlags.Invert) != 0
                        ? invertedFlags
                        : flags;
                if (!NetCompositionHelpers.TestSectionFlags(
                        section,
                        sectionCompositionFlags))
                {
                    continue;
                }

                NetSectionFlags sectionFlags =
                    (section.m_Flags & NetSectionFlags.Invert) != 0
                        ? NetCompositionHelpers.InvertSectionFlags(section.m_Flags)
                        : section.m_Flags;
                // Preserve the logical section identity. RoadBuilder's own
                // IsMedian check is made against this prefab, not against the
                // endpoint-specific subsection selected for rendering.
                Entity definitionRoot = section.m_Section;

                // GetCompositionPieces follows subsection matches repeatedly;
                // RoadBuilder median node/stop variants can be nested. Mirror
                // that traversal so m_SectionIndex and this identity list stay
                // aligned with the pieces returned by vanilla.
                for (int depth = 0; depth < 16 &&
                     subSections.HasBuffer(section.m_Section); depth++)
                {
                    DynamicBuffer<NetSubSection> candidates =
                        subSections[section.m_Section];
                    bool matched = false;
                    for (int k = 0; k < candidates.Length; k++)
                    {
                        NetSubSection candidate = candidates[k];
                        if (NetCompositionHelpers.TestSubSectionFlags(
                                candidate,
                                sectionCompositionFlags,
                                sectionFlags))
                        {
                            section.m_Section = candidate.m_SubSection;
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        break;
                    }
                }

                if (!sectionPieces.HasBuffer(section.m_Section))
                {
                    continue;
                }

                DynamicBuffer<NetSectionPiece> definitions =
                    sectionPieces[section.m_Section];
                bool producesPieces = false;
                for (int k = 0; k < definitions.Length; k++)
                {
                    NetSectionPiece definition = definitions[k];
                    if (NetCompositionHelpers.TestPieceFlags(
                            definition,
                            sectionCompositionFlags,
                            sectionFlags) ||
                        NetCompositionHelpers.TestPieceFlags2(
                            definition,
                            sectionCompositionFlags,
                            sectionFlags))
                    {
                        producesPieces = true;
                        break;
                    }
                }
                if (producesPieces)
                {
                    roots.Add(definitionRoot);
                }
            }
            return roots;
        }

        private static bool HasBlockingDividerDefinition(
            Entity root,
            BufferLookup<NetSectionPiece> sectionPieces)
        {
            if (root == Entity.Null || !sectionPieces.HasBuffer(root))
            {
                return false;
            }

            DynamicBuffer<NetSectionPiece> definitions = sectionPieces[root];
            for (int i = 0; i < definitions.Length; i++)
            {
                NetSectionPiece definition = definitions[i];
                // This mirrors RoadBuilder's NetSectionPrefab.IsMedian:
                // only an unconditional divider definition establishes the
                // logical section as a physical median. Conditional node or
                // lane pieces must not turn an ordinary drive section into a
                // selectable median.
                if ((definition.m_Flags & NetPieceFlags.BlockTraffic) != 0 &&
                    definition.m_CompositionAll == default &&
                    definition.m_CompositionAny == default &&
                    definition.m_SectionAll == 0 &&
                    definition.m_SectionAny == 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static int CalculateSignature(
            RoadGrassMedianAnchorData data,
            List<NetCompositionPiece> pieces)
        {
            unchecked
            {
                int hash = data.RootSection.GetHashCode();
                hash = (hash * 397) ^ data.RepresentativePiece.GetHashCode();
                hash = (hash * 397) ^ (int)math.round(data.Width * 1000f);
                for (int i = 0; i < pieces.Count; i++)
                {
                    NetCompositionPiece piece = pieces[i];
                    hash = (hash * 397) ^ piece.m_Piece.GetHashCode();
                    hash = (hash * 397) ^ (int)piece.m_SectionFlags;
                    hash = (hash * 397) ^ (int)piece.m_PieceFlags;
                }
                return hash;
            }
        }
    }

    /// <summary>
    /// Converts an endpoint median's logical composition interval into the
    /// exact node-facing strip cut from the road's rendered edge geometry.
    /// Selection, preview, and final rendering all use this one orientation.
    /// </summary>
    internal static class RoadGrassMedianGeometry
    {
        internal static bool TryGetSegmentStrip(EntityManager entityManager,
            RoadGrassResolvedMedianAnchor anchor, out EdgeGeometry strip)
        {
            strip = default;
            if (anchor == null || !entityManager.TryGetComponent(anchor.Key.Edge, out EdgeGeometry road)) return false;
            float width = math.max(0.01f, anchor.RoadComposition.m_Width);
            float left = math.clamp(anchor.Left / width + 0.5f, 0f, 1f);
            float right = math.clamp(anchor.Right / width + 0.5f, 0f, 1f);
            if (right - left < 0.001f) return false;
            strip.m_Start.m_Left = MathUtils.Lerp(road.m_Start.m_Left, road.m_Start.m_Right, left);
            strip.m_Start.m_Right = MathUtils.Lerp(road.m_Start.m_Left, road.m_Start.m_Right, right);
            strip.m_End.m_Left = MathUtils.Lerp(road.m_End.m_Left, road.m_End.m_Right, left);
            strip.m_End.m_Right = MathUtils.Lerp(road.m_End.m_Left, road.m_End.m_Right, right);
            return true;
        }

        internal static bool TryGetOrientedApproach(
            EntityManager entityManager,
            RoadGrassResolvedMedianAnchor anchor,
            out Segment result)
        {
            result = default;
            if (anchor == null || anchor.Key.Edge == Entity.Null ||
                !entityManager.Exists(anchor.Key.Edge) ||
                !entityManager.TryGetComponent(anchor.Key.Edge, out EdgeGeometry geometry))
            {
                return false;
            }

            Segment road = anchor.Key.End
                ? geometry.m_End
                : InvertSegment(geometry.m_Start);
            float roadWidth = math.max(0.01f, anchor.RoadComposition.m_Width);
            float leftT = math.clamp(anchor.Left / roadWidth + 0.5f, 0f, 1f);
            float rightT = math.clamp(anchor.Right / roadWidth + 0.5f, 0f, 1f);
            if (rightT - leftT < 0.001f)
            {
                return false;
            }

            result.m_Left = MathUtils.Lerp(road.m_Left, road.m_Right, leftT);
            result.m_Right = MathUtils.Lerp(road.m_Left, road.m_Right, rightT);
            result.m_Length = new float2(
                MathUtils.Length(result.m_Left),
                MathUtils.Length(result.m_Right));
            return math.all(result.m_Length > 0.01f);
        }

        internal static Segment InvertSegment(Segment segment)
        {
            return new Segment
            {
                m_Left = MathUtils.Invert(segment.m_Right),
                m_Right = MathUtils.Invert(segment.m_Left),
                m_Length = segment.m_Length.yx
            };
        }
    }
}
