using System;
using System.Collections.Generic;
using Colossal.Entities;
using Colossal.UI.Binding;
using Game.Prefabs;
using Game.Rendering;
using Game.UI;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

namespace RoadGrass
{
    internal static class RoadGrassTerrainTextures
    {
        private static readonly List<Action> s_Completions = new();
        internal static void CompleteOnMainThread(Action action) { lock (s_Completions) s_Completions.Add(action); }
        internal static void PumpCompletions()
        {
            Action[] ready;
            lock (s_Completions) { if (s_Completions.Count == 0) return; ready = s_Completions.ToArray(); s_Completions.Clear(); }
            foreach (var action in ready) action();
        }
        internal static float Scale(PolygonTerrainLayer layer)
        {
            if (MedianTextureIdentity.IsCustom((int)layer))
                return RoadGrassCustomTextures.TryGet(layer, out var custom) ? 1 / custom.Definition.tileSizeMeters : 1;
            string group = layer >= PolygonTerrainLayer.Extra1 ? "Extra" : layer.ToString();
            float tiling = Shader.GetGlobalVector("_Terrain" + group + "TextureTiling").x;
            // Native terrain UVs normalize world metres against the shader's 16km reference extent.
            return tiling > 0 && !float.IsInfinity(tiling) ? tiling / 16000f : .15f;
        }
        internal static bool TryGet(PolygonTerrainLayer layer, out Texture diffuse, out Texture normal)
        {
            if (MedianTextureIdentity.IsCustom((int)layer))
            {
                bool available = RoadGrassCustomTextures.TryGet(layer, out var custom);
                diffuse = available ? custom.Base : null;
                normal = available ? custom.Normal : null;
                return available;
            }
            string name = layer.ToString();
            diffuse = layer == PolygonTerrainLayer.None ? null : Shader.GetGlobalTexture("colossal_Terrain" + name + "Diffuse");
            normal = layer == PolygonTerrainLayer.None ? null : Shader.GetGlobalTexture("colossal_Terrain" + name + "Normal");
            return diffuse != null && diffuse.width > 4 && diffuse.height > 4;
        }

        internal static bool IsChoice(int value) => value >= 11 && MedianTextureIdentity.IsValid(value - 10) &&
            (value <= 17 || RoadGrassCustomTextures.Contains(value - 10));

        internal static void ReadAsync(Texture texture, int size, Vector2 scale, Vector2 offset, bool linear, Action<Color32[]> completed, int height = 0)
        {
            if (height == 0) height = size;
            var target = RenderTexture.GetTemporary(size, height, 0, RenderTextureFormat.ARGB32,
                linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            bool previousSRGB = GL.sRGBWrite;
            bool transferred = false;
            try
            {
                GL.sRGBWrite = !linear;
                Graphics.Blit(texture, target, scale, offset);
                RenderTexture.active = target;
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    AsyncGPUReadback.Request(target, 0, TextureFormat.RGBA32, request =>
                    {
                        Color32[] pixels = null;
                        try { if (!request.hasError) pixels = request.GetData<Color32>().ToArray(); }
                        catch { pixels = null; }
                        finally { RenderTexture.ReleaseTemporary(target); }
                        completed(pixels);
                    });
                    transferred = true;
                }
                else
                {
                    // Compatibility for GPUs without async transfer; this CPU-only readback is never uploaded again.
                    var image = new Texture2D(size, height, TextureFormat.RGBA32, false, linear);
                    try { image.ReadPixels(new Rect(0, 0, size, height), 0, 0, false); completed(image.GetPixels32()); }
                    finally { UnityEngine.Object.Destroy(image); }
                }
            }
            finally
            {
                RenderTexture.active = previous; GL.sRGBWrite = previousSRGB;
                if (!transferred) RenderTexture.ReleaseTemporary(target);
            }
        }

        internal static void Preview(Texture texture, Vector2 scale, Vector2 offset, Action<string> completed)
        {
            ReadAsync(texture, 128, scale, offset, false, pixels =>
            {
                if (pixels == null) { completed(""); return; }
                var image = new Texture2D(128, 128, TextureFormat.RGBA32, false, false);
                try
                {
                // Native diffuse alpha stores surface data, not preview opacity.
                for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
                image.SetPixels32(pixels);
                completed("data:image/png;base64," + Convert.ToBase64String(image.EncodeToPNG()));
                }
                catch { completed(""); }
                finally { UnityEngine.Object.Destroy(image); }
            });
        }
    }

    internal partial class RoadGrassJoinCompositionSystem
    {
        private bool TerrainUsesLegacyNormals(PolygonTerrainLayer layer)
        {
            if (layer >= PolygonTerrainLayer.Extra1) return false;
            var terrain = World.GetExistingSystemManaged<TerrainMaterialSystem>();
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            return terrain != null && prefabs.TryGetPrefab<Game.Prefabs.Terrain.TerrainRenderSettingsPrefab>(terrain.currentTerrainRenderSettings, out var settings) &&
                settings.isLegacyFormat;
        }
        private sealed class PolygonSurfaceOption
        {
            internal int Id;
            internal string Name, NameKey, Icon;
            internal bool Available;
        }

        private readonly List<PolygonSurfaceOption> m_PolygonSurfaceOptions = new();
        private int m_PolygonSourceTexture = 1;
        private readonly Dictionary<string, string> m_SurfacePreviews = new();
        private int m_SurfacePreviewGeneration;
        private string RequestSurfacePreview(string key, Texture texture, Vector2 scale, Vector2 offset)
        {
            if (m_SurfacePreviews.TryGetValue(key, out string preview)) return preview;
            m_SurfacePreviews[key] = "";
            int generation = m_SurfacePreviewGeneration;
            RoadGrassTerrainTextures.Preview(texture, scale, offset, value =>
            {
                if (generation != m_SurfacePreviewGeneration) return;
                m_SurfacePreviews[key] = value; m_SurfaceOptionsSelected = -1;
            });
            return "";
        }
        private int m_SurfaceOptionsSelected = -1, m_SurfaceOptionsEpoch = -1;
        private int m_CustomSurfaceVersion = -1;
        private int m_CustomPreviewVersion = -1;
        private Entity m_SurfaceOptionsTerrain;
        private readonly int[] m_SurfaceOptionTextures = new int[14];

        private void ResetPolygonSurfaceOptions()
        {
            m_SurfaceOptionsSelected = m_SurfaceOptionsEpoch = -1;
            m_PolygonSurfaceOptions.Clear(); m_SurfacePreviews.Clear();
            m_SurfacePreviewGeneration++;
            m_PolygonSourceTexture = 1;
            Array.Clear(m_SurfaceOptionTextures, 0, m_SurfaceOptionTextures.Length);
        }

        internal int SelectedMedianTexture
        {
            get
            {
                if (!m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median)) return 1;
                if (median.Points.Count >= 3 && median.Saved.TerrainLayer != PolygonTerrainLayer.None)
                    return 10 + (int)median.Saved.TerrainLayer;
                if (median.Saved.Surface != MedianSavedSurface.None) return (int)median.Saved.Surface;
                if (median.Points.Count >= 3) return m_PolygonSourceTexture;
                return m_Resolved.TryGetValue(median.Saved.Key, out var source) &&
                    (source.StandaloneComposition.m_Flags.m_General & CompositionFlags.General.PrimaryMiddleBeautification) == 0 ? 2 : 1;
            }
        }

        internal void SetMedianTexture(int value)
        {
            if (value >= 0 && value <= 2)
            { SetManualSurface(SelectedManualMedianId, (MedianSavedSurface)value); return; }
            if (!RoadGrassTerrainTextures.IsChoice(value) || !SelectedIsPolygon || !CanEditSelectedManualMedian ||
                !RoadGrassTerrainTextures.TryGet((PolygonTerrainLayer)(value - 10), out _, out _) ||
                !m_ManualMedians.TryGetValue(SelectedManualMedianId, out var median)) return;
            var layer = (PolygonTerrainLayer)(value - 10);
            if (median.Saved.TerrainLayer == layer) return;
            if (!SetManualSurface(SelectedManualMedianId, MedianSavedSurface.Grass)) return;
            median.Saved.TerrainLayer = layer;
            var records = EntityManager.GetBuffer<RoadGrassSavedManualMedian>(m_CityQuery.GetSingletonEntity());
            for (int i = 0; i < records.Length; i++) if (records[i].Id == median.Saved.Id) records[i] = median.Saved;
            QueuePolygonEdit(median.Saved.Id);
        }

        internal bool RefreshPolygonSurfaceOptions()
        {
            RoadGrassCustomTextures.EnsureLoaded();
            RoadGrassCustomTextures.PumpPreviews();
            var terrain = World.GetExistingSystemManaged<TerrainMaterialSystem>();
            Entity settings = terrain?.currentTerrainRenderSettings ?? Entity.Null;
            bool changed = m_SurfaceOptionsSelected != SelectedManualMedianId || m_SurfaceOptionsEpoch != m_PolygonPlanEpoch ||
                m_SurfaceOptionsTerrain != settings || m_CustomSurfaceVersion != RoadGrassCustomTextures.Version || m_CustomPreviewVersion != RoadGrassCustomTextures.PreviewVersion;
            bool texturesChanged = m_CustomSurfaceVersion != RoadGrassCustomTextures.Version;
            for (int i = 1; i <= 7; i++)
            {
                RoadGrassTerrainTextures.TryGet((PolygonTerrainLayer)i, out var diffuse, out var normal);
                int id = diffuse != null ? diffuse.GetInstanceID() : 0;
                int normalId = normal != null ? normal.GetInstanceID() : 0;
                texturesChanged |= m_SurfaceOptionTextures[i - 1] != id;
                texturesChanged |= m_SurfaceOptionTextures[i + 6] != normalId;
                m_SurfaceOptionTextures[i - 1] = id;
                m_SurfaceOptionTextures[i + 6] = normalId;
            }
            changed |= texturesChanged;
            if (!changed) return false;
            if (texturesChanged)
            {
                foreach (var median in m_ManualMedians.Values)
                    if (median.Saved.TerrainLayer != PolygonTerrainLayer.None) { m_Dirty = true; break; }
                foreach (var record in m_SegmentSurfaces.Values)
                    if (record.TerrainLayer != PolygonTerrainLayer.None) { m_Dirty = true; break; }
            }
            m_SurfaceOptionsSelected = SelectedManualMedianId; m_SurfaceOptionsEpoch = m_PolygonPlanEpoch;
            m_SurfaceOptionsTerrain = settings;
            m_CustomSurfaceVersion = RoadGrassCustomTextures.Version;
            m_CustomPreviewVersion = RoadGrassCustomTextures.PreviewVersion;
            m_PolygonSurfaceOptions.Clear();
            m_PolygonSurfaceOptions.Add(new PolygonSurfaceOption { Id = 1, Name = "Grass", NameKey = "RoadGrass.MANUAL[Grass]", Available = true });
            m_PolygonSurfaceOptions.Add(new PolygonSurfaceOption { Id = 2, Name = "Pavement", NameKey = "RoadGrass.MANUAL[Plain]", Available = true });
            if (SelectedIsPolygon && TryGetSelectedPolygonPoints(out var source, out _))
            {
                m_PolygonSourceTexture = (source.StandaloneComposition.m_Flags.m_General & CompositionFlags.General.PrimaryMiddleBeautification) != 0 ? 1 : 2;
                foreach (var option in m_PolygonSurfaceOptions)
                    option.Icon = NativeSurfacePreview(source, (MedianSavedSurface)option.Id);
            }
            for (int i = 1; i <= 7; i++)
            {
                var layer = (PolygonTerrainLayer)i;
                bool available = RoadGrassTerrainTextures.TryGet(layer, out var diffuse, out _);
                string preview = "";
                if (available)
                {
                    string key = "terrain:" + diffuse.GetInstanceID();
                    preview = RequestSurfacePreview(key, diffuse, Vector2.one, Vector2.zero);
                }
                string name = layer switch { PolygonTerrainLayer.Extra1 => "Dry", PolygonTerrainLayer.Extra2 => "Lush",
                    PolygonTerrainLayer.Extra3 => "Dirt", PolygonTerrainLayer.Extra4 => "Sand", _ => "Terrain " + layer };
                m_PolygonSurfaceOptions.Add(new PolygonSurfaceOption { Id = 10 + i, Available = available, Icon = preview,
                    NameKey = "RoadGrass.MANUAL[Terrain" + layer + "]", Name = name });
            }
            foreach (var custom in RoadGrassCustomTextures.Entries)
            {
                RoadGrassCustomTextures.RequestPreview(custom);
                bool available = custom.Preview != "";
                m_PolygonSurfaceOptions.Add(new PolygonSurfaceOption { Id = 10 + custom.Definition.id,
                    Name = custom.Definition.displayName, NameKey = "RoadGrass.CUSTOM[" + custom.Definition.id + "]",
                    Available = available, Icon = available ? custom.Preview : "" });
            }
            return true;
        }


        private string NativeSurfacePreview(RoadGrassResolvedMedianAnchor source, MedianSavedSurface surface)
        {
            if (!TryPolygonRenderProfile(source, surface, out var fill, out _, out var profile, out bool grass)) return "";
            var prefabs = World.GetExistingSystemManaged<PrefabSystem>();
            foreach (var piece in fill.Pieces)
            {
                if (!EntityManager.HasBuffer<MeshMaterial>(piece.m_Piece) || !prefabs.TryGetPrefab<RenderPrefab>(piece.m_Piece, out var prefab)) continue;
                int count = EntityManager.GetBuffer<MeshMaterial>(piece.m_Piece, true).Length;
                for (int material = 0; material < count; material++)
                {
                    var asset = prefab.GetSurfaceAsset(material);
                    if (asset == null) continue;
                    bool isGrass = asset.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0 || asset.name.IndexOf("lawn", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (grass ? !isGrass : piece.m_Piece != profile.PavementPiece || material != profile.PavementMaterial) continue;
                    string key = $"native:{piece.m_Piece}:{material}:{profile.PavementU}";
                    if (m_SurfacePreviews.TryGetValue(key, out string preview)) return preview;
                    try
                    {
                        asset.LoadProperties(false);
                        foreach (var map in asset.textures)
                        {
                            if (!RoadGrassPolygonPrototypeResources.IsPrimaryMap(map.Key, "base") || map.Value == null) continue;
                            var texture = map.Value.Load();
                            try
                            {
                                if (texture != null) return RequestSurfacePreview(key, texture,
                                    grass ? Vector2.one : new Vector2(profile.PavementU.y - profile.PavementU.x, 1),
                                    grass ? Vector2.zero : new Vector2(profile.PavementU.x, 0));
                            }
                            finally { map.Value.Unload(); }
                        }
                    }
                    catch { return ""; }
                    finally { asset.UnloadProperties(); }
                }
            }
            return "";
        }

        internal void SurfaceMaterialsReady()
        {
            foreach (var median in m_ManualMedians.Values)
                if (median.Saved.TerrainLayer != PolygonTerrainLayer.None) QueuePolygonEdit(median.Saved.Id);
            foreach (var record in m_SegmentSurfaces.Values)
                if (record.TerrainLayer != PolygonTerrainLayer.None) QueueMedianEdit(record.Edge);
        }

        internal void WritePolygonSurfaceOptions(IJsonWriter writer)
        {
            writer.ArrayBegin(m_PolygonSurfaceOptions.Count);
            foreach (var option in m_PolygonSurfaceOptions)
            {
                writer.TypeBegin("RoadGrass.PolygonSurfaceOption");
                writer.PropertyName("id"); writer.Write(option.Id);
                writer.PropertyName("name"); writer.Write(option.Name);
                writer.PropertyName("nameKey"); writer.Write(option.NameKey);
                writer.PropertyName("icon"); writer.Write(option.Icon ?? "");
                writer.PropertyName("available"); writer.Write(option.Available);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }
    }
}
