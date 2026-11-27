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

        internal static void BuildPairBoundaries(Bezier4x3 center, float3 startHalfWidth, float3 endHalfWidth,
            ref float seam, out Bezier4x3 aLeft, out Bezier4x3 aRight, out Bezier4x3 bLeft, out Bezier4x3 bRight)
        {
            float aError = BuildWidthBoundaries(center, startHalfWidth, endHalfWidth, out aLeft, out aRight, 0, seam);
            float bError = BuildWidthBoundaries(center, startHalfWidth, endHalfWidth, out bLeft, out bRight, seam, 1);
            if (math.max(aError, bError) <= .01f || math.abs(seam - .5f) < .001f) return;
            // An off-center material join can leave almost the entire tight turn in one cubic.
            seam = .5f;
            BuildWidthBoundaries(center, startHalfWidth, endHalfWidth, out aLeft, out aRight, 0, seam);
            BuildWidthBoundaries(center, startHalfWidth, endHalfWidth, out bLeft, out bRight, seam, 1);
        }

        private static float BuildWidthBoundaries(Bezier4x3 center, float3 startHalfWidth, float3 endHalfWidth,
            out Bezier4x3 left, out Bezier4x3 right, float from = 0, float to = 1)
        {
            MathUtils.Divide(center, out var prefix, out _, to);
            MathUtils.Divide(prefix, out _, out var segment, from / to);
            float3 start = segment.b - segment.a, end = segment.d - segment.c;
            start /= math.max(.000001f, math.length(start.xz));
            end /= math.max(.000001f, math.length(end.xz));
            CrossSection(from, out var first, out var firstDerivative);
            CrossSection(to, out var last, out var lastDerivative);
            float span = to - from;
            float3 bOffset = first + (firstDerivative - start * math.dot(firstDerivative.xz, start.xz)) * (span / 3);
            float3 cOffset = last - (lastDerivative - end * math.dot(lastDerivative.xz, end.xz)) * (span / 3);
            float aa = 0, ab = 0, bb = 0, ar = 0, br = 0;
            // Rotate width with the local tangent; constrain endpoint transverse slopes so both halves meet smoothly.
            for (int i = 1; i < 32; i++)
            {
                float t = i / 32f, u = 1 - t, b = 3 * u * u * t, c = 3 * u * t * t;
                CrossSection(math.lerp(from, to, t), out var target, out _);
                float2 baseline = first.xz * (u*u*u) + bOffset.xz * b + cOffset.xz * c + last.xz * (t*t*t);
                float2 a = start.xz * b, d = end.xz * c, residual = target.xz - baseline;
                float weight = 1 / math.max(.0001f, math.lengthsq(target.xz));
                aa += math.dot(a, a) * weight; ab += math.dot(a, d) * weight; bb += math.dot(d, d) * weight;
                ar += math.dot(a, residual) * weight; br += math.dot(d, residual) * weight;
            }
            float determinant = aa * bb - ab * ab;
            if (determinant > .000001f)
            {
                bOffset += start * ((ar * bb - br * ab) / determinant);
                cOffset += end * ((br * aa - ar * ab) / determinant);
            }
            left = new Bezier4x3(segment.a - first, segment.b - bOffset, segment.c - cOffset, segment.d - last);
            right = new Bezier4x3(segment.a + first, segment.b + bOffset, segment.c + cOffset, segment.d + last);
            float error = 0;
            var offset = new Bezier4x3(first, bOffset, cOffset, last);
            for (int i = 1; i < 32; i++)
            {
                float t = i / 32f;
                CrossSection(math.lerp(from, to, t), out var target, out _);
                error = math.max(error, math.abs(math.length(MathUtils.Position(offset, t).xz) / math.max(.001f, math.length(target.xz)) - 1));
            }
            return error;

            void CrossSection(float t, out float3 width, out float3 derivative)
            {
                float2 startTangent = math.normalizesafe((center.b-center.a).xz);
                float2 endTangent = math.normalizesafe((center.d-center.c).xz);
                float2 startNormal = new float2(startTangent.y, -startTangent.x), endNormal = new float2(endTangent.y, -endTangent.x);
                float2 lateral = new float2(math.dot(startHalfWidth.xz,startNormal), math.dot(endHalfWidth.xz,endNormal));
                float2 skew = new float2(math.dot(startHalfWidth.xz,startTangent), math.dot(endHalfWidth.xz,endTangent));
                float blend = t*t*(3-2*t), slope = 6*t*(1-t);
                float2 velocity = MathUtils.Tangent(center,t).xz;
                float speed = math.max(.000001f,math.length(velocity));
                float2 tangent = math.normalizesafe(velocity,startTangent), normal = new float2(tangent.y,-tangent.x);
                float2 acceleration = (6*(1-t)*(center.c-2*center.b+center.a) + 6*t*(center.d-2*center.c+center.b)).xz;
                float2 tangentDerivative = (acceleration - tangent * math.dot(acceleration,tangent)) / speed;
                float2 normalDerivative = new float2(tangentDerivative.y,-tangentDerivative.x);
                float l = math.lerp(lateral.x,lateral.y,blend), s = math.lerp(skew.x,skew.y,blend);
                width = new float3(0,math.lerp(startHalfWidth.y,endHalfWidth.y,blend),0);
                width.xz = normal*l + tangent*s;
                if(t==0) width=startHalfWidth;
                if(t==1) width=endHalfWidth;
                derivative = new float3(0,(endHalfWidth.y-startHalfWidth.y)*slope,0);
                derivative.xz = normalDerivative*l + normal*((lateral.y-lateral.x)*slope) +
                    tangentDerivative*s + tangent*((skew.y-skew.x)*slope);
            }
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

        internal static List<float2> CrosswalkRanges(EdgeGeometry geometry, IReadOnlyList<RoadGrassTaperGeometry.Lane> lanes)
        {
            var left = Join(geometry.m_Start.m_Left, geometry.m_End.m_Left);
            var right = Join(geometry.m_Start.m_Right, geometry.m_End.m_Right);
            var ranges = new List<float2>();
            if (lanes.Count == 0) { ranges.Add(new float2(0, 1)); return ranges; }
            int steps = math.clamp((int)math.ceil(math.max(MathUtils.Length(left), MathUtils.Length(right)) / .25f), 1, 512);
            var blocked = new bool[steps];
            foreach (var lane in lanes)
            {
                int samples = math.clamp((int)math.ceil(MathUtils.Length(lane.Curve) / .5f), 1, 128);
                for (int segment = 0; segment < samples; segment++)
                {
                    float3 a = MathUtils.Position(lane.Curve, segment / (float)samples);
                    float3 b = MathUtils.Position(lane.Curve, (segment + 1f) / samples);
                    float radius = math.max(.25f, lane.Width * .5f) + .15f;
                    for (int i = 0; i < steps; i++)
                    {
                        float t = (i + .5f) / steps;
                        float3 l = MathUtils.Position(left, t), r = MathUtils.Position(right, t);
                        if (math.abs((l.y + r.y - a.y - b.y) * .5f) > 1 ||
                            RoadGrassTaperGeometry.Distance(l.xz, r.xz, a.xz, b.xz) > radius) continue;
                        // Expand one sample so a crossing between samples cannot leave a raised sliver.
                        for (int j = math.max(0, i - 1); j <= math.min(steps - 1, i + 1); j++) blocked[j] = true;
                    }
                }
            }
            for (int i = 0; i < steps;)
            {
                if (blocked[i]) { i++; continue; }
                int first = i++;
                while (i < steps && !blocked[i]) i++;
                ranges.Add(new float2(first / (float)steps, i / (float)steps));
            }
            return ranges;
        }

        internal static EdgeGeometry Slice(EdgeGeometry geometry, float2 range)
        {
            Bezier4x3 Cut(Bezier4x3 curve)
            {
                MathUtils.Divide(curve, out var prefix, out _, range.y);
                MathUtils.Divide(prefix, out _, out var result, range.x / range.y);
                return result;
            }
            var left = Cut(Join(geometry.m_Start.m_Left, geometry.m_End.m_Left));
            var right = Cut(Join(geometry.m_Start.m_Right, geometry.m_End.m_Right));
            MathUtils.Divide(left, out var sl, out var el, .5f);
            MathUtils.Divide(right, out var sr, out var er, .5f);
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

        internal static RoadGrassPolygonShape.Loop SurfaceLoop(EdgeGeometry geometry, float2 inset, bool openStart, bool openEnd, int samples, float2? range = null)
        {
            var loop = new RoadGrassPolygonShape.Loop();
            float2 span = range ?? new float2(0, 1);
            // Native edge segments need not be subdivisions of one cubic. Retain their seam exactly.
            var parameters = new List<float>();
            for (int i = 0; i <= samples; i++) parameters.Add(math.lerp(span.x, span.y, i / (float)samples));
            if (span.x < .5f && span.y > .5f && !parameters.Contains(.5f)) { parameters.Add(.5f); parameters.Sort(); }
            for (int i = 0; i < parameters.Count - 1;)
            {
                float a = parameters[i], b = parameters[i + 1]; bool split = false;
                for (int side = 0; side < 2; side++)
                {
                    float3 start = At(a, side == 1), end = At(b, side == 1);
                    split |= math.distance(start, end) > 1;
                    for (int j = 1; j < 4; j++)
                    {
                        float t = j / 4f;
                        split |= math.distance(At(math.lerp(a,b,t),side == 1), math.lerp(start,end,t)) > .005f;
                    }
                }
                if (split && b - a > .000001f) parameters.Insert(i + 1, (a + b) * .5f); else i++;
            }
            for (int i = 0; i < parameters.Count; i++)
            { loop.Points.Add(At(parameters[i],false)); loop.Opening.Add(i == parameters.Count - 1 && openEnd); }
            for (int i = parameters.Count - 1; i >= 0; i--)
            { loop.Points.Add(At(parameters[i],true)); loop.Opening.Add(i == 0 && openStart); }
            return loop;
            float3 At(float t, bool right)
            {
                var half = t <= .5f ? geometry.m_Start : geometry.m_End;
                float u = t <= .5f ? t * 2 : (t - .5f) * 2;
                float3 l = MathUtils.Position(half.m_Left,u), r = MathUtils.Position(half.m_Right,u);
                return math.lerp(l,r,right ? 1-inset.y : inset.x);
            }
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
