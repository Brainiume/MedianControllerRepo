using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
namespace RoadGrass
{
    internal partial class RoadGrassJoinToolSystem
    {
        private uint m_ContourDisplayHash;
        private List<float3> m_ContourDisplayPoints;
        private bool PickPolygonSnap(List<float3> points, Line3.Segment ray, bool hasRay, float fovTan,
            ref NativeList<OverlayCommand> commands, out float3 destination, System.Func<float3, bool> connected = null)
        {
            uint hash=0;
            foreach(var p in points)hash=unchecked(hash*397^math.hash(p)^(connected==null||connected(p)?1u:0u));
            if(m_ContourDisplayPoints==null||hash!=m_ContourDisplayHash)
            {
                m_ContourDisplayPoints=RoadGrassContourGraph.MergeDisplayPoints(points,connected);
                m_ContourDisplayHash=hash;
            }
            points=m_ContourDisplayPoints;
            destination = default;
            float nearest = float.MaxValue;
            float3 delta = ray.b - ray.a;
            foreach (var p in points)
            {
                if (!hasRay) break;
                float t = math.dot(p - ray.a, delta) / math.max(.0001f, math.lengthsq(delta));
                if (t < 0 || t > 1) continue;
                float distance = math.distance(p, ray.a + delta * t);
                float tolerance = math.clamp(math.distance(ray.a, p) * fovTan * .006f, .15f, .5f);
                float score = distance / tolerance;
                if (score < 1 && score < nearest) { nearest = score; destination = p; }
            }
            bool snapped = nearest < float.MaxValue;
            foreach (var p in points)
            {
                bool hovered = snapped && p.Equals(destination), follows = connected == null || connected(p);
                Color color = hovered ? (follows ? new Color(.42f,.93f,.68f,1) : new Color(.8f,.84f,.86f,1)) :
                    follows ? new Color(.98f,.39f,.41f,1) : new Color(.54f,.58f,.6f,1);
                float radius = math.clamp(math.distance(ray.a,p) * fovTan * .0025f, .08f, .22f) * (hovered ? 1.6f : 1);
                for (int i = 0; i < 12; i++)
                {
                    float a = i * math.PI * 2 / 12, b = (i + 1) * math.PI * 2 / 12;
                    AddLine(ref commands, color, new Line3.Segment(p + new float3(math.cos(a),0,math.sin(a))*radius,
                        p + new float3(math.cos(b),0,math.sin(b))*radius), radius*.8f);
                }
            }
            return snapped;
        }
    }
}
