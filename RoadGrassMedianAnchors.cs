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
        // Keep roadside identities separate from both median ordinals and polygon render keys.
        internal bool IsRoadsideGrass => Ordinal <= int.MinValue / 2;

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
            out bool invalidData,
            CompositionFlags? requestedFlags = null,
            bool includeRoadsideGrass = false,
            PrefabSystem prefabs = null)
        {
            anchors = new List<RoadGrassResolvedMedianAnchor>();
            invalidData = false;
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
            using var pieces = new NativeList<NetCompositionPiece>(Allocator.Temp);
            if (!TryGetCompositionPieces(geometrySections, flags, subSections, sectionPieces, pieceData, pieces, out var activeRoots))
            { invalidData = true; return false; }

            NetCompositionData resolvedData = edgeData;
            resolvedData.m_Flags = flags;
            NetCompositionHelpers.CalculateCompositionData(
                ref resolvedData,
                pieces.AsArray(),
                pieceData,
                vertexMatchData);

            using var plainPieces = includeRoadsideGrass
                ? new NativeList<NetCompositionPiece>(Allocator.Temp) : default;
            List<Entity> plainRoots = null;
            if (includeRoadsideGrass)
            {
                CompositionFlags plainFlags = flags;
                plainFlags.m_Left &= ~CompositionFlags.Side.PrimaryBeautification;
                plainFlags.m_Right &= ~CompositionFlags.Side.PrimaryBeautification;
                if (!TryGetCompositionPieces(geometrySections, plainFlags, subSections, sectionPieces, pieceData, plainPieces, out plainRoots))
                { invalidData = true; return false; }
            }
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
                bool roadsideGrass = false;
                if (includeRoadsideGrass && !physicalMedian && !containsVehicleLane)
                {
                    // Select the native grass replacement, not the wider sidewalk section.
                    int grassIndex = -1;
                    for (int i = 0; i < group.Count; i++)
                    {
                        var candidate = group[i];
                        if ((candidate.m_SectionFlags & (NetSectionFlags.Left | NetSectionFlags.Right)) == 0 ||
                            (candidate.m_SectionFlags & (NetSectionFlags.Hidden | NetSectionFlags.Underground | NetSectionFlags.Overhead)) != 0 ||
                            (candidate.m_PieceFlags & NetPieceFlags.HasMesh) == 0) continue;
                        bool unchanged = false;
                        for (int j = 0; j < plainPieces.Length; j++)
                            if (plainPieces[j].m_SectionIndex >= 0 && plainPieces[j].m_SectionIndex < plainRoots.Count &&
                                plainRoots[plainPieces[j].m_SectionIndex] == root &&
                                plainPieces[j].m_Piece == candidate.m_Piece) { unchanged = true; break; }
                        bool? nativeGrass = HasNativeGrassSurface(entityManager, prefabs, candidate.m_Piece);
                        if (nativeGrass == false || (nativeGrass != true && unchanged)) continue;
                        if (grassIndex < 0 || candidate.m_Size.x > group[grassIndex].m_Size.x)
                            grassIndex = i;
                    }
                    if (grassIndex >= 0)
                    {
                        var grass = group[grassIndex];
                        // Keep matching native curb pieces, excluding pavement outside this strip.
                        group.RemoveAll(p => (p.m_PieceFlags & NetPieceFlags.HasMesh) == 0 ||
                            (p.m_SectionFlags & (NetSectionFlags.Hidden | NetSectionFlags.Underground | NetSectionFlags.Overhead)) != 0 ||
                            math.abs(p.m_Offset.x - grass.m_Offset.x) + p.m_Size.x * 0.5f > grass.m_Size.x * 0.5f + 0.01f);
                        representative = grass.m_Piece;
                        representativeIndex = group.FindIndex(p => p.m_Piece == representative);
                        roadsideGrass = representativeIndex >= 0;
                    }
                }
                if ((physicalMedian || roadsideGrass) && !containsVehicleLane && visibleMedianMesh &&
                    representative != Entity.Null && representativeIndex >= 0)
                {
                    // CalculateCompositionData has already placed every piece
                    // in full-road coordinates. All alternatives in one
                    // section share this section centre; using it directly is
                    // reliable for narrow and asymmetric RoadBuilder medians.
                    float2 grassInterval = roadsideGrass
                        ? GetRoadsideGrassInterval(entityManager, prefabs, group[representativeIndex]) : default;
                    float center = roadsideGrass ? math.csum(grassInterval) * 0.5f : group[0].m_Offset.x;
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
                        if (roadsideGrass)
                            standalonePiece.m_SectionFlags = (standalonePiece.m_SectionFlags &
                                ~(NetSectionFlags.Left | NetSectionFlags.Right | NetSectionFlags.AlignCenter)) |
                                NetSectionFlags.Median;
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
                    if (roadsideGrass)
                    {
                        // This carrier contains one strip, not either half of the original road.
                        width = grassInterval.y - grassInterval.x;
                        standalone.m_Width = width;
                        standalone.m_MiddleOffset = 0f;
                        for (int i = 0; i < group.Count; i++)
                        {
                            var piece = standalonePieces[i];
                            piece.m_Offset.x = group[i].m_Offset.x - center;
                            standalonePieces[i] = piece;
                        }
                    }
                    group.Clear();
                    for (int i = 0; i < standalonePieces.Length; i++)
                    {
                        group.Add(standalonePieces[i]);
                    }

                    if (roadsideGrass && width > 0.01f)
                        RoadGrassTransitionMesh.SetSyncOffsets(ref standalone,
                            RoadGrassTransitionMesh.ReadBorderWidths(entityManager,
                                new RoadGrassResolvedMedianAnchor { Width = width, Pieces = group }));
                    standalonePieces.Dispose();

                    if (!math.isfinite(center) || !math.isfinite(width) || width <= 0.01f)
                    {
                        index = endIndex;
                        continue;
                    }
                    var data = new RoadGrassMedianAnchorData(
                        end,
                        roadsideGrass ? int.MinValue + sectionIndex : ordinal,
                        root,
                        representative,
                        center / math.max(0.01f, resolvedData.m_Width),
                        width);
                    anchors.Add(new RoadGrassResolvedMedianAnchor
                    {
                        Key = new RoadGrassMedianAnchorKey(edgeEntity, end, data.Ordinal),
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
                    if (!roadsideGrass) ordinal++;
                }

                index = endIndex;
            }

            return anchors.Count > 0;
        }

        private static float2 GetRoadsideGrassInterval(EntityManager manager, PrefabSystem prefabs,
            NetCompositionPiece piece)
        {
            float2 local = new float2(-piece.m_Size.x * 0.5f, piece.m_Size.x * 0.5f);
            if (manager.HasComponent<MeshData>(piece.m_Piece))
            {
                var bounds = manager.GetComponentData<MeshData>(piece.m_Piece).m_Bounds;
                if (math.isfinite(bounds.max.x - bounds.min.x) && bounds.max.x - bounds.min.x > 0.01f)
                    local = new float2(bounds.min.x, bounds.max.x);
            }
            // Cached native grass submesh indices identify its visible footprint, including
            // asymmetric sidewalk assets. Reading them avoids loading geometry assets on hover.
            if (prefabs != null && manager.HasBuffer<MeshVertex>(piece.m_Piece) &&
                manager.HasBuffer<MeshIndex>(piece.m_Piece) && manager.HasBuffer<MeshMaterial>(piece.m_Piece) &&
                prefabs.TryGetPrefab<RenderPrefab>(piece.m_Piece, out var prefab))
            {
                var vertices = manager.GetBuffer<MeshVertex>(piece.m_Piece, true);
                var indices = manager.GetBuffer<MeshIndex>(piece.m_Piece, true);
                var materials = manager.GetBuffer<MeshMaterial>(piece.m_Piece, true);
                float min = float.MaxValue, max = float.MinValue;
                for (int i = 0; i < materials.Length; i++)
                {
                    var surface = prefab.GetSurfaceAsset(i);
                    if (surface == null || (surface.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) < 0 &&
                        surface.name.IndexOf("lawn", StringComparison.OrdinalIgnoreCase) < 0)) continue;
                    var material = materials[i];
                    int end = math.min(indices.Length, material.m_StartIndex + material.m_IndexCount);
                    for (int j = math.max(0, material.m_StartIndex); j < end; j++)
                    {
                        int vertex = indices[j].m_Index;
                        if ((uint)vertex >= vertices.Length) continue;
                        float x = vertices[vertex].m_Vertex.x;
                        if (!math.isfinite(x)) continue;
                        min = math.min(min, x);
                        max = math.max(max, x);
                    }
                }
                if (max - min > 0.01f && math.isfinite(max - min)) local = new float2(min, max);
            }
            // Match BatchMeshHelpers.GenerateCompositionMesh: native X is -1 without Invert, +1 with it.
            if ((piece.m_SectionFlags & NetSectionFlags.Invert) == 0) local = -local.yx;
            return local + piece.m_Offset.x;
        }

        private static bool? HasNativeGrassSurface(EntityManager manager, PrefabSystem prefabs, Entity piece)
        {
            // Read material metadata only; never load textures or mesh vertices during picking.
            if (prefabs == null || !manager.HasBuffer<MeshMaterial>(piece) ||
                !prefabs.TryGetPrefab<RenderPrefab>(piece, out var prefab)) return null;
            var materials = manager.GetBuffer<MeshMaterial>(piece, true);
            bool knownSurface = false;
            for (int i = 0; i < materials.Length; i++)
            {
                var surface = prefab.GetSurfaceAsset(i);
                if (surface == null) continue;
                knownSurface = true;
                if (surface.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    surface.name.IndexOf("lawn", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return knownSurface ? false : (bool?)null;
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
                bool rootMatches = ordinal.Key.Ordinal == saved.Ordinal && (saved.RootSection == Entity.Null ||
                    ordinal.Data.RootSection == saved.RootSection);
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
                if (candidate.Key.IsRoadsideGrass != (saved.Ordinal <= int.MinValue / 2)) continue;
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

        internal static bool TryGetCompositionPieces(
            DynamicBuffer<NetGeometrySection> sections,
            CompositionFlags flags,
            BufferLookup<NetSubSection> subSections,
            BufferLookup<NetSectionPiece> sectionPieces,
            ComponentLookup<NetPieceData> pieceData,
            NativeList<NetCompositionPiece> pieces,
            out List<Entity> roots)
        {
            roots = new List<Entity>();
            if (sections.Length == 0) return false;
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
                var visited = new HashSet<Entity>();
                while (subSections.HasBuffer(section.m_Section))
                {
                    if (!visited.Add(section.m_Section)) return false;
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
                    return false;
                }

                DynamicBuffer<NetSectionPiece> definitions =
                    sectionPieces[section.m_Section];
                int start = pieces.Length;
                NetPieceFlags hidden = 0;
                if ((section.m_Flags & NetSectionFlags.HiddenSurface) != 0) hidden |= NetPieceFlags.Surface;
                if ((section.m_Flags & NetSectionFlags.HiddenBottom) != 0) hidden |= NetPieceFlags.Bottom;
                if ((section.m_Flags & NetSectionFlags.HiddenTop) != 0) hidden |= NetPieceFlags.Top;
                if ((section.m_Flags & NetSectionFlags.HiddenSide) != 0) hidden |= NetPieceFlags.Side;
                for (int k = 0; k < definitions.Length; k++)
                {
                    NetSectionPiece definition = definitions[k];
                    var pieceFlags = definition.m_Flags;
                    if (!NetCompositionHelpers.TestPieceFlags(definition, sectionCompositionFlags, sectionFlags))
                    {
                        if (!NetCompositionHelpers.TestPieceFlags2(definition, sectionCompositionFlags, sectionFlags)) continue;
                        pieceFlags |= NetPieceFlags.SkipBottomHalf;
                    }
                    if (definition.m_Piece == Entity.Null || !pieceData.HasComponent(definition.m_Piece) ||
                        !math.all(math.isfinite(section.m_Offset + definition.m_Offset))) return false;
                    var data = pieceData[definition.m_Piece];
                    if (!math.isfinite(data.m_Width) || data.m_Width < 0 || !math.isfinite(data.m_Length) || data.m_Length < 0 ||
                        !math.isfinite(data.m_HeightRange.min) || !math.isfinite(data.m_HeightRange.max)) return false;
                    pieces.Add(new NetCompositionPiece { m_Piece = definition.m_Piece, m_SectionIndex = roots.Count,
                        m_SectionFlags = section.m_Flags | ((hidden & pieceFlags) != 0 ? NetSectionFlags.Hidden : 0),
                        m_PieceFlags = pieceFlags, m_Offset = section.m_Offset + definition.m_Offset });
                }
                if (pieces.Length != start)
                {
                    roots.Add(definitionRoot);
                }
            }
            return pieces.Length > 0;
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
