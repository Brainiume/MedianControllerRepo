using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private readonly Dictionary<Entity, List<RoadGrassPolygonSurface.Triangle>> m_PolygonRoadSurfaces = new();

        private void ConformPolygonHeight(RoadGrassTransitionMesh.Half half, RoadGrassResolvedMedianAnchor source)
        {
            var point = GetPolygonHeightProjection(source);
            if (point == null) return;
            var node = half.Geometry;
            node.m_Left.m_Left = Project(node.m_Left.m_Left); node.m_Left.m_Right = Project(node.m_Left.m_Right);
            node.m_Right.m_Left = Project(node.m_Right.m_Left); node.m_Right.m_Right = Project(node.m_Right.m_Right);
            node.m_Middle = Project(node.m_Middle);
            node.m_Left.m_Length = new float2(MathUtils.Length(node.m_Left.m_Left), MathUtils.Length(node.m_Left.m_Right));
            node.m_Right.m_Length = new float2(MathUtils.Length(node.m_Right.m_Left), MathUtils.Length(node.m_Right.m_Right));
            node.m_Bounds = MathUtils.TightBounds(node.m_Left.m_Left) | MathUtils.TightBounds(node.m_Right.m_Right) | MathUtils.TightBounds(node.m_Middle);
            half.Geometry = node;

            Bezier4x3 Project(Bezier4x3 curve) => new Bezier4x3(point(curve.a), point(curve.b), point(curve.c), point(curve.d));
        }

        private Func<float3, float3> GetPolygonHeightProjection(RoadGrassResolvedMedianAnchor source)
        {
            if (!RoadGrassMedianGeometry.TryGetOrientedApproach(EntityManager, source, out var opening)) return null;
            if (!m_PolygonRoadSurfaces.TryGetValue(source.Node, out var triangles))
            {
                triangles = new List<RoadGrassPolygonSurface.Triangle>();
                m_PolygonRoadSurfaces[source.Node] = triangles;
                if (EntityManager.HasBuffer<ConnectedEdge>(source.Node))
                    foreach (var connected in EntityManager.GetBuffer<ConnectedEdge>(source.Node, true))
                    {
                        Entity edge = connected.m_Edge;
                        TrackTaperDependency(edge);
                        if (!EntityManager.TryGetComponent(edge, out Edge data)) continue;
                        if (data.m_Start == source.Node && EntityManager.TryGetComponent(edge, out StartNodeGeometry start)) Add(start.m_Geometry);
                        if (data.m_End == source.Node && EntityManager.TryGetComponent(edge, out EndNodeGeometry end)) Add(end.m_Geometry);
                    }
            }
            if (triangles.Count == 0) return null;
            float3 l = opening.m_Left.d, r = opening.m_Right.d;
            float leftOffset = l.y - RoadGrassPolygonSurface.Height(l, triangles);
            float rightOffset = r.y - RoadGrassPolygonSurface.Height(r, triangles);
            return Point;
            float3 Point(float3 p)
            {
                float2 across = r.xz - l.xz;
                float t = math.saturate(math.dot(p.xz - l.xz, across) / math.max(.0001f, math.lengthsq(across)));
                p.y = RoadGrassPolygonSurface.Height(p, triangles) + math.lerp(leftOffset, rightOffset, t);
                return p;
            }
            void Add(EdgeNodeGeometry geometry)
            {
                RoadGrassPolygonSurface.Add(geometry.m_Left, triangles);
                RoadGrassPolygonSurface.Add(geometry.m_Right, triangles);
            }
        }
    }

    internal static class RoadGrassPolygonSurface
    {
        internal struct Triangle
        {
            internal float3 A, B, C;
            internal float2 U, V, Min, Max;
            internal float Determinant;
            internal bool Prepared;
            internal Triangle(float3 a, float3 b, float3 c)
            {
                A = a; B = b; C = c; U = b.xz - a.xz; V = c.xz - a.xz;
                Min = math.min(a.xz, math.min(b.xz, c.xz)); Max = math.max(a.xz, math.max(b.xz, c.xz));
                Determinant = Cross(U, V); Prepared = true;
            }
        }

        internal static void Add(Segment strip, List<Triangle> triangles)
        {
            float3 left = strip.m_Left.a, right = strip.m_Right.a;
            for (int i = 1; i <= 16; i++)
            {
                float3 a = MathUtils.Position(strip.m_Left, i / 16f), b = MathUtils.Position(strip.m_Right, i / 16f);
                triangles.Add(new Triangle(left, right, a));
                triangles.Add(new Triangle(right, b, a));
                left = a; right = b;
            }
        }

        internal static float Height(float3 p, IReadOnlyList<Triangle> triangles)
        {
            float best = float.MaxValue, height = p.y, vertical = float.MaxValue;
            foreach (var triangle in triangles)
            {
                var data = triangle.Prepared ? triangle : new Triangle(triangle.A, triangle.B, triangle.C);
                if (math.distancesq(p.xz, math.clamp(p.xz, data.Min, data.Max)) > best) continue;
                float3 a = data.A, b = data.B, c = data.C;
                float2 u = data.U, v = data.V, w = p.xz - a.xz;
                float determinant = data.Determinant;
                if (math.abs(determinant) < .000001f) continue;
                float x = Cross(w, v) / determinant, y = Cross(u, w) / determinant;
                if (x >= 0 && y >= 0 && x + y <= 1) Consider(0, a.y + x * (b.y - a.y) + y * (c.y - a.y));
                else { Edge(a, b); Edge(b, c); Edge(c, a); }
            }
            return height;
            void Edge(float3 a, float3 b)
            {
                float2 delta = b.xz - a.xz;
                float t = math.saturate(math.dot(p.xz - a.xz, delta) / math.max(.000001f, math.lengthsq(delta)));
                Consider(math.distancesq(p.xz, math.lerp(a.xz, b.xz, t)), math.lerp(a.y, b.y, t));
            }
            void Consider(float distance, float h)
            {
                float d = math.abs(h - p.y);
                if (!math.isfinite(h) || distance > best || (distance == best && d >= vertical)) return;
                best = distance; height = h; vertical = d;
            }
        }
        private static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
    }
}
