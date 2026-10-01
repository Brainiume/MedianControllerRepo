using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace RoadGrass
{
    internal partial class RoadGrassJoinToolSystem
    {
        private sealed class PolygonSnapGroup
        {
            internal readonly List<float3> Points = new();
            internal float3 Centre;
        }
        private readonly List<PolygonSnapGroup> m_PolygonSnapGroups = new();
        private int m_PolygonSnapCount = -1, m_PolygonHoverGroup = -1;
        private float3 m_PolygonSnapOrigin;
        private uint m_PolygonSnapHash;
        private float3? m_PolygonSnapTarget;
        private bool PickPolygonSnap(List<float3> points, Line3.Segment ray, bool hasRay, float fovTan,
            ref NativeList<OverlayCommand> commands, out float3 destination)
        {
            destination = default;
            uint hash = 0; foreach (var point in points) hash = unchecked(hash * 397 ^ math.hash(point));
            if (hash != m_PolygonSnapHash || points.Count != m_PolygonSnapCount || (points.Count > 0 && !points[0].Equals(m_PolygonSnapOrigin)))
            {
                m_PolygonSnapGroups.Clear(); m_PolygonHoverGroup = -1;
                // Spatial buckets keep grouping linear for large marking candidate sets.
                var cells = new Dictionary<int2, List<int>>();
                foreach (var p in points)
                {
                    int2 cell = (int2)math.floor(p.xz / .75f); int group = -1;
                    for (int x = -1; x <= 1 && group < 0; x++) for (int y = -1; y <= 1 && group < 0; y++)
                        if (cells.TryGetValue(cell + new int2(x,y), out var nearby)) foreach (int index in nearby)
                            if (math.distance(m_PolygonSnapGroups[index].Points[0], p) < .75f) { group = index; break; }
                    if (group < 0)
                    {
                        group = m_PolygonSnapGroups.Count; m_PolygonSnapGroups.Add(new PolygonSnapGroup());
                        if (!cells.TryGetValue(cell, out var bucket)) cells[cell] = bucket = new List<int>();
                        bucket.Add(group);
                    }
                    var target = m_PolygonSnapGroups[group]; target.Points.Add(p); target.Centre += p;
                }
                foreach (var group in m_PolygonSnapGroups) group.Centre /= group.Points.Count;
                m_PolygonSnapCount = points.Count; if (points.Count > 0) m_PolygonSnapOrigin = points[0];
                m_PolygonSnapHash = hash;
            }
            float3 delta = ray.b - ray.a;
            float Distance(float3 p)
            {
                if (!hasRay) return float.MaxValue;
                float t = math.dot(p - ray.a, delta) / math.max(.0001f, math.lengthsq(delta));
                return t < 0 || t > 1 ? float.MaxValue : math.distance(p, ray.a + delta * t);
            }
            float Radius(PolygonSnapGroup group) => math.max(.65f, group.Points.Count * .5f / (2 * math.PI));
            if (m_PolygonHoverGroup >= 0)
            {
                var active = m_PolygonSnapGroups[m_PolygonHoverGroup];
                if (Distance(active.Centre) > Radius(active) + .7f) m_PolygonHoverGroup = -1;
            }
            if (m_PolygonHoverGroup < 0)
            {
                float nearest = .65f;
                for (int i = 0; i < m_PolygonSnapGroups.Count; i++)
                { float d = Distance(m_PolygonSnapGroups[i].Centre); if (d < nearest) { nearest = d; m_PolygonHoverGroup = i; } }
            }
            bool snapped = false;
            float tolerance = .35f;
            if (m_PolygonSnapTarget.HasValue && points.Contains(m_PolygonSnapTarget.Value) && Distance(m_PolygonSnapTarget.Value) < tolerance * 1.4f)
            { destination = m_PolygonSnapTarget.Value; snapped = true; }
            if (!snapped)
            {
                float best = tolerance;
                foreach (var point in points)
                { float distance = Distance(point); if (distance < best) { best = distance; destination = point; snapped = true; } }
            }
            m_PolygonSnapTarget = snapped ? destination : (float3?)null;
            for (int i = 0; i < m_PolygonSnapGroups.Count; i++)
            {
                var group = m_PolygonSnapGroups[i]; bool expanded = i == m_PolygonHoverGroup && group.Points.Count > 1;
                int count = expanded ? group.Points.Count : 1;
                for (int j = 0; j < count; j++)
                {
                    // Expand in place: moving markers makes the preview jump between real and display coordinates.
                    float3 p = expanded ? group.Points[j] : snapped && group.Points.Contains(destination) ? destination : group.Points[0];
                    bool hovered = snapped && p.Equals(destination);
                    if (hovered) for (int k = 0; k < 12; k++)
                    {
                        float a = k * 2 * math.PI / 12, b = (k + 1) * 2 * math.PI / 12;
                        AddLine(ref commands, Color.white, new Line3.Segment(p + new float3(math.cos(a),0,math.sin(a)) * .28f,
                            p + new float3(math.cos(b),0,math.sin(b)) * .28f), .05f);
                    }
                    AddLine(ref commands, new Color(1,.2f,.2f,.8f), new Line3.Segment(p - new float3(.12f,0,0), p + new float3(.12f,0,0)), .24f);
                }
            }
            return snapped;

        }
    }
}
