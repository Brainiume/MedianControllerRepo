using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Mathematics;

namespace RoadGrass
{
    // Immutable bounds tree, rebuilt only when its owning geometry changes.
    internal sealed class RoadGrassMedianPickIndex
    {
        private struct Node { internal Bounds3 Bounds; internal int Start, Count, Left, Right; }
        private readonly List<Node> m_Nodes = new();
        private readonly Bounds3[] m_Bounds;
        private readonly int[] m_Items;

        internal RoadGrassMedianPickIndex(Bounds3[] bounds)
        {
            m_Bounds = bounds; m_Items = new int[bounds.Length];
            for (int i = 0; i < bounds.Length; i++) m_Items[i] = i;
            if (bounds.Length != 0) Build(0, bounds.Length);
        }

        private int Build(int start, int count)
        {
            var bounds = m_Bounds[m_Items[start]];
            for (int i = start + 1; i < start + count; i++) bounds |= m_Bounds[m_Items[i]];
            int index = m_Nodes.Count;
            m_Nodes.Add(new Node { Bounds = bounds, Start = start, Count = count });
            if (count <= 8) return index;
            float3 size = bounds.max - bounds.min;
            int axis = size.x > size.z ? (size.x > size.y ? 0 : 1) : (size.z > size.y ? 2 : 1);
            float middle = bounds.min[axis] + bounds.max[axis];
            int split = start;
            for (int i = start; i < start + count; i++)
                if (m_Bounds[m_Items[i]].min[axis] + m_Bounds[m_Items[i]].max[axis] < middle)
                { int item = m_Items[i]; m_Items[i] = m_Items[split]; m_Items[split++] = item; }
            int half = split - start;
            if (half < count / 4 || half > count * 3 / 4) half = count / 2;
            int left = Build(start, half), right = Build(start + half, count - half);
            m_Nodes[index] = new Node { Bounds = bounds, Left = left, Right = right };
            return index;
        }

        internal float Query(Line3.Segment ray, Func<int, float, float> intersect, float limit = 1)
        {
            float best = limit;
            if (m_Nodes.Count != 0) Visit(0);
            return best;
            void Visit(int index)
            {
                var node = m_Nodes[index];
                if (!Hit(node.Bounds, ray, best, out _)) return;
                if (node.Count != 0)
                {
                    for (int i = node.Start; i < node.Start + node.Count; i++)
                        if (Hit(m_Bounds[m_Items[i]], ray, best, out _)) best = math.min(best, intersect(m_Items[i], best));
                    return;
                }
                bool a = Hit(m_Nodes[node.Left].Bounds, ray, best, out float nearA);
                bool b = Hit(m_Nodes[node.Right].Bounds, ray, best, out float nearB);
                if (a && b)
                { if (nearA <= nearB) { Visit(node.Left); Visit(node.Right); } else { Visit(node.Right); Visit(node.Left); } }
                else if (a) Visit(node.Left);
                else if (b) Visit(node.Right);
            }
        }

        internal static bool Hit(Bounds3 bounds, Line3.Segment ray, float limit, out float near)
        {
            float far = limit; near = 0;
            float3 delta = ray.b - ray.a;
            for (int axis = 0; axis < 3; axis++)
            {
                if (math.abs(delta[axis]) < .000001f)
                { if (ray.a[axis] < bounds.min[axis] || ray.a[axis] > bounds.max[axis]) return false; }
                else
                {
                    float a = (bounds.min[axis] - ray.a[axis]) / delta[axis], b = (bounds.max[axis] - ray.a[axis]) / delta[axis];
                    near = math.max(near, math.min(a, b)); far = math.min(far, math.max(a, b));
                    if (near > far) return false;
                }
            }
            return true;
        }
    }
}
