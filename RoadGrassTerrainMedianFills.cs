using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RoadGrass
{
    internal partial class RoadGrassJoinCompositionSystem
    {
        private sealed class TerrainMedianFill
        {
            internal int Id, Epoch;
            internal float Width;
            internal List<NetCompositionPiece> Pieces;
            internal EdgeGeometry Geometry;
        }
        private readonly Dictionary<(Entity Edge, int Ordinal, int Part), TerrainMedianFill> m_TerrainMedianFills = new();

        private void ClearTerrainMedianFills()
        {
            foreach (var fill in m_TerrainMedianFills.Values) RoadGrassPolygonPrototypeBatchSystem.Instance?.Remove(fill.Id);
            m_TerrainMedianFills.Clear();
        }

        private void SynchronizeTerrainMedianFills(HashSet<Entity> edges = null)
        {
            var desired = new HashSet<(Entity, int, int)>();
            foreach (var record in m_SegmentSurfaces.Values)
            {
                if (edges != null && !edges.Contains(record.Edge)) continue;
                if (record.TerrainLayer == PolygonTerrainLayer.None || IsParentRoadDeleted(record.Edge)) continue;
                var anchors = GetSegmentAnchors(record.Edge);
                if (!RoadGrassMedianAnchorResolver.TryMatch(anchors,record.Median,out var source) ||
                    source.Key.Ordinal != record.Median.Ordinal || math.abs(source.Data.NormalizedCenter-record.Median.NormalizedCenter) > .001f ||
                    math.abs(source.Width-record.Median.Width) > .01f || !TryPlanSegment(source,record.Surface,out _)) continue;
                if (RoadGrassMedianGeometry.TryGetSegmentStrip(EntityManager,source,out var geometry))
                    Queue((record.Edge,record.Median.Ordinal,0),source,geometry,record.TerrainLayer,record.EffectiveTextureTiling,null);
            }
            foreach (var pair in m_RenderStates)
            {
                if (edges != null && !edges.Contains(pair.Key.Edge)) continue;
                if (!pair.Value.ConnectionGeometry.HasValue || !m_Resolved.TryGetValue(pair.Key,out var source)) continue;
                var segment = GetSegmentForEndpoint(source);
                if (segment == null || !m_SegmentSurfaces.TryGetValue((segment.Key.Edge,segment.Key.Ordinal),out var record) ||
                    record.TerrainLayer == PolygonTerrainLayer.None || !TryPlanSegment(segment,record.Surface,out _)) continue;
                Queue((record.Edge,record.Median.Ordinal,pair.Key.End?2:1),source,pair.Value.ConnectionGeometry.Value,
                    record.TerrainLayer,record.EffectiveTextureTiling,pair.Value.Crosswalks);
            }
            foreach (var key in new List<(Entity,int,int)>(m_TerrainMedianFills.Keys))
                if ((edges == null || edges.Contains(key.Item1)) && !desired.Contains(key))
                { RoadGrassPolygonPrototypeBatchSystem.Instance?.Remove(m_TerrainMedianFills[key].Id); m_TerrainMedianFills.Remove(key); }

            void Queue((Entity,int,int) key,RoadGrassResolvedMedianAnchor source,EdgeGeometry geometry,
                PolygonTerrainLayer layer,float textureTiling,List<RoadGrassTaperGeometry.Lane> crossings)
            {
                desired.Add(key);
                TryQueueTerrainMedianFill(key,source,geometry,layer,textureTiling,crossings);
            }
        }

        private bool TryQueueTerrainMedianFill((Entity,int,int) key,RoadGrassResolvedMedianAnchor source,EdgeGeometry geometry,
            PolygonTerrainLayer layer,float textureTiling,List<RoadGrassTaperGeometry.Lane> crossings)
        {
            var renderer = RoadGrassPolygonPrototypeBatchSystem.Instance;
            if (renderer == null || !RoadGrassTerrainTextures.TryGet(layer,out _,out _) || !renderer.TryInitialize(out _)) return false;
            m_TerrainMedianFills.TryGetValue(key,out var previous);
            if (previous != null && previous.Width == source.Width && CompositionPiecesEqual(previous.Pieces,source.Pieces) &&
                previous.Epoch == m_PolygonPlanEpoch && previous.Geometry.Equals(geometry) &&
                renderer.Matches(previous.Id,previous,MedianSavedSurface.Grass,out bool ready,out _,layer,textureTiling,crossings)) return ready;
            Mesh mesh=null; Material pavement=null,fill=null;
            List<Colossal.IO.AssetDatabase.TextureAsset> textures=null; List<RenderTexture> tiles=null;
            try
            {
                if (!TryPolygonRenderProfile(source,MedianSavedSurface.Grass,out var styled,out var curb,out var profile,out _)) return false;
                profile.TerrainScale=RoadGrassTerrainTextures.Scale(layer);
                profile.InteriorTiling=textureTiling;
                // Check the cached material before building geometry, including cached failures.
                renderer.CopyMedianMaterials(EntityManager,World.GetExistingSystemManaged<PrefabSystem>(),
                    styled,curb,profile,true,out pavement,out fill,out textures,out tiles);
                if(!renderer.TryTerrainMaterial(pavement,layer,TerrainUsesLegacyNormals(layer),out var terrain)) return false;
                RoadGrassPolygonPrototypeResources.ReleaseMaterial(fill);fill=terrain;
                var ranges=crossings == null ? new List<float2>{new float2(0,1)} : RoadGrassTransitionMesh.CrosswalkRanges(geometry,crossings);
                var loops=new List<RoadGrassPolygonShape.Loop>();
                float2 inset=PolygonProfile(source).Insets/math.max(.001f,source.Width);
                foreach(var range in ranges)
                {
                    loops.Add(RoadGrassTransitionMesh.SurfaceLoop(geometry,inset,range.x==0,range.y==1,4,range));
                }
                var triangles=new List<RoadGrassPolygonSurface.Triangle>();
                RoadGrassPolygonSurface.Add(geometry.m_Start,triangles);RoadGrassPolygonSurface.Add(geometry.m_End,triangles);
                float3 origin=geometry.m_Start.m_Left.a;
                // Only an opaque inner fill is drawn, 3mm above native grass; the original curb remains untouched.
                float3 Project(float3 p){p.y=RoadGrassPolygonSurface.Height(p,triangles)+profile.BaseHeight+.003f;return p;}
                mesh=RoadGrassPolygonPrototypeMesh.Build(loops,origin,profile,Project,true);
                if(mesh==null) mesh=new Mesh{subMeshCount=2};
                mesh.SetTriangles(Array.Empty<int>(),0);
                var entry=new TerrainMedianFill {Id=previous?.Id ?? m_NextCrosswalkCapId--, Width=source.Width,
                    Pieces=new List<NetCompositionPiece>(source.Pieces),Epoch=m_PolygonPlanEpoch,Geometry=geometry};
                renderer.Submit(entry.Id,entry,MedianSavedSurface.Grass,mesh,origin,true,new List<Line3.Segment>(),
                    pavement,fill,textures,tiles,layer,textureTiling,crossings);
                m_TerrainMedianFills[key]=entry;
                mesh=null;pavement=null;fill=null;textures=null;tiles=null;
                return true;
            }
            catch(Exception error){Mod.TryWarn("Median terrain surface unavailable: "+error.Message);return false;}
            finally
            {
                if(mesh!=null) UnityEngine.Object.Destroy(mesh);
                RoadGrassPolygonPrototypeResources.ReleaseMaterial(pavement);
                RoadGrassPolygonPrototypeResources.ReleaseMaterial(fill);
                if(tiles!=null) foreach(var tile in tiles) UnityEngine.Object.Destroy(tile);
                if(textures!=null) foreach(var texture in textures) texture.Unload();
            }
        }
    }
}
