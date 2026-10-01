using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    // Use the same vertex matching contract as NetCompositionHelpers and
    // BatchDataHelpers.CalculateNodeParameters, on an isolated median only.
    internal static class RoadGrassTransitionMesh
    {
        internal static bool IsBlockedWidthPair(float a, float b) =>
            math.abs(math.min(a, b) - 1f) < 0.01f && math.abs(math.max(a, b) - 5f) < 0.01f;

        internal sealed class Half
        {
            internal RoadGrassResolvedMedianAnchor Source;
            internal NetCompositionData Composition;
            internal List<NetCompositionPiece> Pieces;
            internal EdgeNodeGeometry Geometry;
        }

        internal static float2 ReadBorderWidths(EntityManager manager, RoadGrassResolvedMedianAnchor anchor)
        {
            float half = anchor.Width * 0.5f;
            float left = -half, right = half;
            foreach (var piece in anchor.Pieces)
            {
                if (!manager.HasComponent<NetVertexMatchData>(piece.m_Piece)) continue;
                float3 offsets = manager.GetComponentData<NetVertexMatchData>(piece.m_Piece).m_Offsets;
                if (!math.all(math.isfinite(offsets.xy))) continue;
                float2 interval = new float2(offsets.x, math.isnan(offsets.z) ? offsets.y : offsets.z);
                if ((piece.m_SectionFlags & NetSectionFlags.Invert) != 0) interval = -interval.yx;
                interval += piece.m_Offset.x;
                // Physical borders, in this median's local coordinates. Other
                // medians and the outer road edges are not present in Pieces.
                if (interval.x > -half && interval.x < 0) left = math.max(left, interval.x);
                if (interval.y < half && interval.y > 0) right = math.min(right, interval.y);
            }
            return new float2(left + half, half - right);
        }

        internal static void SetSyncOffsets(ref NetCompositionData composition, float2 borders)
        {
            float half = composition.m_Width * 0.5f;
            composition.m_SyncVertexOffsetsLeft = new float4(0, 0, 0, borders.x / half);
            composition.m_SyncVertexOffsetsRight = new float4(1 - borders.y / half, 1, 1, 1);
        }

        internal static float2 MatchSeamBorders(float2 a, float2 opposite)
        {
            // GeometrySystem.CalculateVertexSyncTarget merges coincident sync
            // points in BOTH profiles before interpolation. If either border
            // coincides with the fixed outer edge, averaging in a nonzero inset
            // moves only the other mesh's border and leaves a step at the seam.
            return math.select((a + opposite) * 0.5f, float2.zero, (a == 0) | (opposite == 0));
        }

        internal static Half Create(RoadGrassResolvedMedianAnchor source, EdgeGeometry geometry,
            float2 sourceBorders, float2 seamBorders)
        {
            var composition = source.StandaloneComposition;
            composition.m_Flags.m_General &= ~(CompositionFlags.General.Edge | CompositionFlags.General.Roundabout);
            composition.m_Flags.m_General |= CompositionFlags.General.Node;
            composition.m_MiddleOffset = 0;
            SetSyncOffsets(ref composition, sourceBorders);
            var pieces = new List<NetCompositionPiece>(source.Pieces.Count);
            foreach (var original in source.Pieces)
            {
                var piece = original;
                piece.m_SectionFlags |= NetSectionFlags.Median;
                // These are open EDGE continuation assets. Let the native node
                // generator clip and deform their half, rather than preserve a
                // fixed-size whole piece or select an endpoint cap asset.
                piece.m_PieceFlags &= ~(NetPieceFlags.PreserveShape | NetPieceFlags.SkipBottomHalf);
                pieces.Add(piece);
            }
            var left = Join(geometry.m_Start.m_Left, geometry.m_End.m_Left);
            var right = Join(geometry.m_Start.m_Right, geometry.m_End.m_Right);
            var middle = new Bezier4x3((left.a + right.a) * 0.5f, (left.b + right.b) * 0.5f,
                (left.c + right.c) * 0.5f, (left.d + right.d) * 0.5f);
            float seamHalf = math.distance(left.d, right.d) * 0.5f;
            var node = new EdgeNodeGeometry
            {
                m_Left = new Segment { m_Left = left, m_Right = middle,
                    m_Length = new float2(MathUtils.Length(left), MathUtils.Length(middle)) },
                m_Right = new Segment { m_Left = middle, m_Right = right,
                    m_Length = new float2(MathUtils.Length(middle), MathUtils.Length(right)) },
                m_Middle = middle,
                m_Bounds = geometry.m_Bounds,
                m_SyncVertexTargetsLeft = new float4(0, 0, 0, seamHalf > 0.0001f ? seamBorders.x / seamHalf : 0),
                m_SyncVertexTargetsRight = new float4(seamHalf > 0.0001f ? 1 - seamBorders.y / seamHalf : 1, 1, 1, 1)
            };
            return new Half { Source = source, Composition = composition, Pieces = pieces, Geometry = node };
        }

        // BuildEdgeGeometry subdivides at 0.5; recover its original curve exactly.
        internal static EdgeGeometry ScaleSeam(EdgeGeometry geometry, float factor)
        {
            var left = Join(geometry.m_Start.m_Left, geometry.m_End.m_Left);
            var right = Join(geometry.m_Start.m_Right, geometry.m_End.m_Right);
            float3 centerC = (left.c + right.c) * 0.5f, centerD = (left.d + right.d) * 0.5f;
            left.c = centerC + (left.c - centerC) * factor;
            right.c = centerC + (right.c - centerC) * factor;
            left.d = centerD + (left.d - centerD) * factor;
            right.d = centerD + (right.d - centerD) * factor;
            MathUtils.Divide(left, out var sl, out var el, 0.5f);
            MathUtils.Divide(right, out var sr, out var er, 0.5f);
            var bounds = MathUtils.TightBounds(left) | MathUtils.TightBounds(right);
            bounds.min.y = math.min(bounds.min.y, geometry.m_Bounds.min.y);
            bounds.max.y = math.max(bounds.max.y, geometry.m_Bounds.max.y);
            return new EdgeGeometry
            {
                m_Start = new Segment { m_Left = sl, m_Right = sr, m_Length = new float2(MathUtils.Length(sl), MathUtils.Length(sr)) },
                m_End = new Segment { m_Left = el, m_Right = er, m_Length = new float2(MathUtils.Length(el), MathUtils.Length(er)) },
                m_Bounds = bounds
            };
        }

        private static Bezier4x3 Join(Bezier4x3 start, Bezier4x3 end) =>
            new Bezier4x3(start.a, 2 * start.b - start.a, 2 * end.c - end.d, end.d);
    }
}
