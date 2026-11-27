using Game.Prefabs;

namespace RoadGrass
{
    internal static class MedianEndpointProfiles
    {
        internal static NetCompositionPiece GetCapPiece(NetCompositionPiece piece, MedianSavedCap cap)
        {
            // The section also contains the pavement fan shared by the junction.
            // Clipping or fixing its shape tears the surrounding node geometry.
            const NetPieceFlags capFlags = NetPieceFlags.PreserveShape |
                NetPieceFlags.BlockTraffic | NetPieceFlags.BlockCrosswalk;
            if (cap == MedianSavedCap.Straight &&
                (piece.m_PieceFlags & NetPieceFlags.HasMesh) != 0 &&
                (piece.m_PieceFlags & capFlags) != 0)
            {
                // This is only called for the selected median's replacement pieces.
                // Normalize the cap orientation so the retained positive-Z half is
                // at the road boundary, including originally flipped cap templates.
                piece.m_SectionFlags = (piece.m_SectionFlags & ~NetSectionFlags.FlipMesh) | NetSectionFlags.HalfLength;
                piece.m_PieceFlags |= NetPieceFlags.PreserveShape;
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
