using Game.Prefabs;

namespace RoadGrass
{
    internal static class MedianEndpointProfiles
    {
        internal static NetCompositionPiece GetCapPiece(NetCompositionPiece piece, MedianSavedCap cap)
        {
            const NetPieceFlags crosswalkCap = NetPieceFlags.PreserveShape | NetPieceFlags.BlockTraffic |
                NetPieceFlags.Surface | NetPieceFlags.DisableTiling | NetPieceFlags.AsymmetricMeshZ | NetPieceFlags.HasMesh;
            if (cap == MedianSavedCap.Straight &&
                (piece.m_PieceFlags & (crosswalkCap | NetPieceFlags.BlockCrosswalk | NetPieceFlags.SkipBottomHalf)) == crosswalkCap &&
                (piece.m_SectionFlags & (NetSectionFlags.Median | NetSectionFlags.FlipMesh)) == NetSectionFlags.Median)
            {
                // Native node meshing keeps raw Z >= -0.01: the road-side curb.
                // PreserveShape keeps its placement/UVs; the detached far curb is clipped.
                // HalfLength participates in native mesh identity, unlike SkipBottomHalf.
                piece.m_SectionFlags |= NetSectionFlags.HalfLength;
            }
            return piece;
        }

        internal static CompositionFlags GetCapFlags(CompositionFlags nodeFlags, MedianSavedCap cap)
        {
            // CompositionSelectSystem selects Crosswalk at sidewalk intersections.
            // Ask the prefab for that closed, transverse median cap, not an edge
            // continuation (which the node mesh would stretch into the junction).
            // These flags are only inputs to piece selection; they are never
            // written to the road, its lanes, or its original composition.
            if (cap == MedianSavedCap.Straight)
                nodeFlags.m_General |= CompositionFlags.General.Crosswalk;
            return nodeFlags;
        }
    }
}
