using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Mathematics;
using UnityEngine;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private readonly Dictionary<RoadGrassMedianAnchorKey, int> m_CrosswalkCaps = new();
        private int m_NextCrosswalkCapId = -1;
        private int m_LastCrosswalkCapRevision = -1;

        private void RemoveCrosswalkCaps(RoadGrassMedianAnchorKey key)
        {
            if (!m_CrosswalkCaps.TryGetValue(key, out int id)) return;
            RoadGrassPolygonPrototypeBatchSystem.Instance?.Remove(id);
            m_CrosswalkCaps.Remove(key);
        }

        private void PruneCrosswalkCaps()
        {
            if (m_LastCrosswalkCapRevision == m_RenderRevision) return;
            m_LastCrosswalkCapRevision = m_RenderRevision;
            foreach (var key in new List<RoadGrassMedianAnchorKey>(m_CrosswalkCaps.Keys))
                if (!m_RenderStates.ContainsKey(key) || !m_RenderPairs.ContainsKey(key)) RemoveCrosswalkCaps(key);
        }

        private List<float2> CapCrosswalkSpans(RoadGrassResolvedMedianAnchor source, EdgeGeometry geometry, List<float2> ranges)
        {
            if (ranges.Count == 0) return ranges;
            var renderer = RoadGrassPolygonPrototypeBatchSystem.Instance;
            if (renderer == null || !renderer.TryInitialize(out _)) return ranges;
            Mesh mesh = null;
            Material pavement = null, fill = null;
            List<Colossal.IO.AssetDatabase.TextureAsset> textures = null;
            List<RenderTexture> tiles = null;
            try
            {
                if (!TryPolygonRenderProfile(source, MedianSavedSurface.None, out var styled, out var curbSource,
                    out var profile, out bool grass)) return ranges;
                var loops = new List<RoadGrassPolygonShape.Loop>();
                var body = new List<float2>();
                float length = math.max(.001f, math.cmax(geometry.m_Start.m_Length + geometry.m_End.m_Length));
                float depth = math.max(.1f, profile.CurbWidth * 2) / length;
                float2 inset=PolygonProfile(source).Insets/math.max(.001f,source.Width);
                foreach (var range in ranges)
                {
                    bool start = range.x > 0, end = range.y < 1;
                    if (!start && !end) { body.Add(range); continue; }
                    float from = start ? math.min(range.y,range.x+depth) : range.x;
                    float to = end ? math.max(range.x,range.y-depth) : range.y;
                    if (from >= to) AddBand(range,start,end);
                    else
                    {
                        if(start) AddBand(new float2(range.x,from),true,false);
                        if(end) AddBand(new float2(to,range.y),false,true);
                        body.Add(new float2(from,to));
                    }
                }
                if (loops.Count == 0) return ranges;
                var triangles = new List<RoadGrassPolygonSurface.Triangle>();
                RoadGrassPolygonSurface.Add(geometry.m_Start,triangles);
                RoadGrassPolygonSurface.Add(geometry.m_End,triangles);
                float3 origin = geometry.m_Start.m_Left.a;
                float3 Project(float3 point)
                { point.y = RoadGrassPolygonSurface.Height(point,triangles)+profile.BaseHeight; return point; }
                mesh = RoadGrassPolygonPrototypeMesh.Build(loops,origin,profile,Project,grass);
                if(mesh == null) return ranges;
                renderer.CopyMedianMaterials(EntityManager,
                    World.GetExistingSystemManaged<PrefabSystem>(),styled,curbSource,profile,grass,
                    out pavement,out fill,out textures,out tiles);
                int id = m_NextCrosswalkCapId--;
                renderer.Submit(id,loops,MedianSavedSurface.None,mesh,origin,grass,new List<Line3.Segment>(),
                    pavement,fill,textures,tiles);
                mesh=null; pavement=null; fill=null; textures=null; tiles=null;
                m_CrosswalkCaps[source.Key]=id;
                return body;

                void AddBand(float2 range,bool closeStart,bool closeEnd)
                {
                    var slice = RoadGrassTransitionMesh.Slice(geometry,range);
                    loops.Add(RoadGrassTransitionMesh.SurfaceLoop(slice,inset,!closeStart,!closeEnd,4));
                }
            }
            catch(System.Exception error) { Mod.TryWarn("Crosswalk endcap unavailable: "+error.Message); return ranges; }
            finally
            {
                if(mesh!=null) Object.Destroy(mesh);
                RoadGrassPolygonPrototypeResources.ReleaseMaterial(pavement);
                RoadGrassPolygonPrototypeResources.ReleaseMaterial(fill);
                if(tiles!=null) foreach(var tile in tiles) Object.Destroy(tile);
                if(textures!=null) foreach(var texture in textures) texture.Unload();
            }
        }
    }
}
