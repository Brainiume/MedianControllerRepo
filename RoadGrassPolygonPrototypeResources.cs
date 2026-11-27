using System;
using UnityEngine;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Unity.Entities;
using System.Collections.Generic;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Experimental.Rendering;

namespace RoadGrass
{
    // Runtime renderer cache only. No resource handles belong in saved city data.
    internal sealed class RoadGrassPolygonPrototypeResources : IDisposable
    {
        internal Material Pavement { get; private set; }
        internal Material Grass { get; private set; }
        private sealed class MedianMaterials
        {
            internal Material Pavement, Fill;
            internal List<TextureAsset> Textures;
            internal List<RenderTexture> Tiles;
        }
        private readonly Dictionary<(int Source, int Curb, Entity CurbPiece, int CurbMaterial, Entity FillPiece, int FillMaterial, Unity.Mathematics.float2 U, bool Grass), MedianMaterials> m_MedianMaterials = new();
        private static readonly HashSet<Material> s_SharedMaterials = new();
        internal static void ReleaseMaterial(Material material)
        {
            if (material != null && !s_SharedMaterials.Contains(material)) UnityEngine.Object.Destroy(material);
        }

        internal void CopyMedianMaterials(EntityManager manager, PrefabSystem prefabs,
            RoadGrassResolvedMedianAnchor source, RoadGrassResolvedMedianAnchor curbSource,
            RoadGrassPolygonPrototypeMesh.Profile profile, bool useGrass,
            out Material pavement, out Material grass, out List<TextureAsset> textures, out List<RenderTexture> fillTextures)
        {
            var key = (source.Signature, curbSource.Signature, profile.CurbPiece, profile.CurbMaterial,
                profile.PavementPiece, profile.PavementMaterial, profile.PavementU, useGrass);
            if (!m_MedianMaterials.TryGetValue(key, out var entry))
            {
                entry = new MedianMaterials();
                LoadMedianMaterials(manager, prefabs, source, curbSource, profile, useGrass,
                    out entry.Pavement, out entry.Fill, out entry.Textures, out entry.Tiles);
                s_SharedMaterials.Add(entry.Pavement); s_SharedMaterials.Add(entry.Fill);
                m_MedianMaterials.Add(key, entry);
            }
            pavement = entry.Pavement; grass = entry.Fill;
            textures = null; fillTextures = null; // The resource cache owns native asset references and atlas tiles.
        }

        private sealed class TerrainFill
        {
            internal Material Material;
            internal Texture2D Normal, Mask;
            internal bool Pending;
        }
        private bool m_Disposed;
        private sealed class TerrainMaps
        {
            internal Texture2D Normal, Mask;
            internal Exception Error;
            internal readonly List<(Action Ready, Action<Exception> Failed)> Waiting = new();
        }
        private readonly Dictionary<(int Diffuse, int Normal, bool Legacy), TerrainMaps> m_TerrainMaps = new();
        private readonly Dictionary<(int Shader, int Diffuse, int Normal, bool Legacy), TerrainFill> m_TerrainFills = new();

        internal static bool IsPrimaryMap(string property, string kind)
        {
            string name = property.ToLowerInvariant();
            if (name.StartsWith("unity_", StringComparison.Ordinal) || name.Contains("world") || name.Contains("stack") ||
                name.Contains("detail") || name.Contains("control")) return false;
            return kind == "base" ? name.Contains("base") || name.Contains("albedo") || name.Contains("diffuse") : name.Contains(kind);
        }

        internal static Color32 TerrainNormal(Color32 normal, bool legacy) =>
            new Color32(legacy ? (byte)((normal.r * normal.a + 127) / 255) : normal.r, normal.g, 255, 255);

        internal static Color32 TerrainMask(byte smoothness) => new Color32(0, 255, 0, smoothness);

        internal bool TryTerrainMaterial(Material template, PolygonTerrainLayer layer, bool legacy, out Material material)
        {
            material = null;
            if (!RoadGrassTerrainTextures.TryGet(layer, out var diffuse, out var packed)) return false;
            var key = (template.GetInstanceID(), diffuse.GetInstanceID(), packed != null ? packed.GetInstanceID() : 0, legacy);
            if (!m_TerrainFills.TryGetValue(key, out var fill))
            {
                fill = new TerrainFill();
                try
                {
                    // Preserve native shader/batch settings; reset the finish controls below after replacing its maps.
                    fill.Material = new Material(template) { hideFlags = HideFlags.DontSave, enableInstancing = true,
                        name = "Median Controller surface " + layer };
                    fill.Material.DisableKeyword("ENABLE_VT");
                    fill.Material.DisableKeyword("_TANGENTSPACE_OCTO");
                    bool custom = MedianTextureIdentity.IsCustom((int)layer);
                    Texture normalMap, maskMap;
                    if (custom)
                    {
                        if (!RoadGrassCustomTextures.TryGet(layer, out var imported)) throw new InvalidOperationException("Custom median texture unavailable");
                        normalMap = imported.Normal != null ? imported.Normal : Texture2D.normalTexture;
                        maskMap = imported.Mask;
                    }
                    else
                    {
                        // TerrainMaterialSystem owns the loaded diffuse map. Only normal/mask packing needs conversion.
                        fill.Pending = true; m_TerrainFills.Add(key, fill);
                        PrepareTerrainFill(fill, diffuse, packed, legacy, () =>
                        {
                            try
                            {
                                Configure(fill.Normal, fill.Mask, false); fill.Pending = false;
                                s_SharedMaterials.Add(fill.Material);
                            }
                            catch (Exception error) { Fail(error); }
                        }, Fail);
                        if (fill.Pending || fill.Material == null) return false;
                        material = fill.Material; return true;
                    }
                    Configure(normalMap, maskMap, custom);
                    void Configure(Texture normalTexture, Texture maskTexture, bool isCustom)
                    {
                    bool foundBase = false, foundNormal = false, foundMask = false;
                    foreach (string property in fill.Material.GetTexturePropertyNames())
                    {
                        int index = fill.Material.shader.FindPropertyIndex(property);
                        if (index >= 0 && fill.Material.shader.GetPropertyTextureDimension(index) != UnityEngine.Rendering.TextureDimension.Tex2D) continue;
                        var existing = fill.Material.GetTexture(property);
                        if (existing != null && existing.dimension != UnityEngine.Rendering.TextureDimension.Tex2D) continue;
                        Texture map = null;
                        if (IsPrimaryMap(property, "base")) { map = diffuse; foundBase = true; }
                        else if (IsPrimaryMap(property, "normal")) { map = normalTexture; foundNormal = true; }
                        else if (IsPrimaryMap(property, "mask")) { map = maskTexture; foundMask = true; }
                        if (map == null) continue;
                        fill.Material.SetTexture(property, map);
                        fill.Material.SetTextureScale(property, Vector2.one);
                        fill.Material.SetTextureOffset(property, Vector2.zero);
                    }
                    if (!foundBase || !foundNormal || !foundMask) throw new InvalidOperationException("Native median texture slots unavailable");
                    if (fill.Material.HasProperty("_BaseColor")) fill.Material.SetColor("_BaseColor", Color.white);
                    if (fill.Material.HasProperty("_Smoothness")) fill.Material.SetFloat("_Smoothness", 1);
                    if (fill.Material.HasProperty("_Metallic")) fill.Material.SetFloat("_Metallic", isCustom ? 1 : 0);
                    if (isCustom && fill.Material.HasProperty("_MetallicRemapMin")) fill.Material.SetFloat("_MetallicRemapMin", 0);
                    if (isCustom && fill.Material.HasProperty("_MetallicRemapMax")) fill.Material.SetFloat("_MetallicRemapMax", 1);
                    if (fill.Material.HasProperty("_NormalScale")) fill.Material.SetFloat("_NormalScale", 1);
                    if (fill.Material.HasProperty("_CoatStrength")) fill.Material.SetFloat("_CoatStrength", 0);
                    if (fill.Material.HasProperty("_CoatMask")) fill.Material.SetFloat("_CoatMask", 0);
                    if (fill.Material.HasProperty("_SmoothnessRemapMin")) fill.Material.SetFloat("_SmoothnessRemapMin", 0);
                    if (fill.Material.HasProperty("_SmoothnessRemapMax")) fill.Material.SetFloat("_SmoothnessRemapMax", 1);
                    if (fill.Material.HasProperty("_AORemapMin")) fill.Material.SetFloat("_AORemapMin", 0);
                    if (fill.Material.HasProperty("_AORemapMax")) fill.Material.SetFloat("_AORemapMax", 1);
                    HDMaterial.ValidateMaterial(fill.Material);
                    foreach (string keyword in new[] { "_NORMALMAP", "_MASKMAP" })
                        if (fill.Material.shader.keywordSpace.FindKeyword(keyword).isValid) fill.Material.EnableKeyword(keyword);
                    }
                    m_TerrainFills.Add(key, fill);
                }
                catch (Exception exception)
                {
                    Fail(exception);
                    return false;
                }
            }
            if (fill == null || fill.Pending) return false;
            material = fill.Material;
            s_SharedMaterials.Add(material);
            return true;
            void Fail(Exception exception)
            {
                DestroyTerrainFill(fill); m_TerrainFills[key] = null;
                Mod.TryWarn("Terrain median material unavailable: " + exception.Message);
            }
        }

        private void PrepareTerrainFill(TerrainFill fill, Texture diffuse, Texture normal, bool legacy, Action completed, Action<Exception> failed)
        {
            var key = (diffuse.GetInstanceID(), normal != null ? normal.GetInstanceID() : 0, legacy);
            bool cached = m_TerrainMaps.TryGetValue(key, out var maps);
            if (!cached) { maps = new TerrainMaps(); m_TerrainMaps.Add(key, maps); }
            void Apply() { fill.Normal = maps.Normal; fill.Mask = maps.Mask; completed(); }
            if (maps.Error != null) { failed(maps.Error); return; }
            if (maps.Mask != null) { Apply(); return; }
            maps.Waiting.Add((Apply, failed));
            if (cached) return;
            var source = normal != null ? normal : Texture2D.normalTexture;
            int width = source.width, height = source.height, maskWidth = diffuse.width, maskHeight = diffuse.height;
            Color32[] normals = null, masks = null; int received = 0;
            try
            {
                RoadGrassTerrainTextures.ReadAsync(source, width, Vector2.one, Vector2.zero, true, pixels => { normals = pixels; Ready(); }, height);
                RoadGrassTerrainTextures.ReadAsync(diffuse, maskWidth, Vector2.one, Vector2.zero, true, pixels => { masks = pixels; Ready(); }, maskHeight);
            }
            catch (Exception error) { FailMaps(error); }
            void Ready()
            {
                if (++received != 2 || m_Disposed) return;
                if (normals == null || masks == null) { FailMaps(new InvalidOperationException("Terrain GPU transfer failed")); return; }
                System.Threading.Tasks.Task.Run(() =>
                {
                    for (int i = 0; i < normals.Length; i++) normals[i] = TerrainNormal(normals[i], legacy);
                    for (int i = 0; i < masks.Length; i++) masks[i] = TerrainMask(masks[i].a);
                }).ContinueWith(task => RoadGrassTerrainTextures.CompleteOnMainThread(() =>
                {
                    if (m_Disposed) return;
                    if (task.IsFaulted) { FailMaps(task.Exception); return; }
                    try
                    {
                        maps.Normal = Upload(width, height, normals); maps.Mask = Upload(maskWidth, maskHeight, masks);
                        foreach (var waiting in maps.Waiting) waiting.Ready();
                        maps.Waiting.Clear();
                        RoadGrassPolygonPrototypeBatchSystem.Instance?.SurfaceMaterialsReady();
                    }
                    catch (Exception error) { FailMaps(error); }
                }));
            }
            void FailMaps(Exception error)
            {
                maps.Error = error;
                if (maps.Normal != null) UnityEngine.Object.Destroy(maps.Normal);
                if (maps.Mask != null) UnityEngine.Object.Destroy(maps.Mask);
                maps.Normal = maps.Mask = null;
                foreach (var waiting in maps.Waiting) waiting.Failed(error);
                maps.Waiting.Clear();
            }
            static Texture2D Upload(int w, int h, Color32[] pixels)
            {
                var image = new Texture2D(w, h, TextureFormat.RGBA32, true, true)
                { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
                try { image.SetPixels32(pixels); image.Apply(true, true); return image; }
                catch { UnityEngine.Object.Destroy(image); throw; }
            }
        }

        internal void ClearTerrainMaterials()
        {
            foreach (var fill in m_TerrainFills.Values) DestroyTerrainFill(fill);
            m_TerrainFills.Clear();
            foreach (var maps in m_TerrainMaps.Values)
            {
                if (maps.Normal != null) UnityEngine.Object.Destroy(maps.Normal);
                if (maps.Mask != null) UnityEngine.Object.Destroy(maps.Mask);
                maps.Waiting.Clear();
            }
            m_TerrainMaps.Clear();
        }

        private static void DestroyTerrainFill(TerrainFill fill)
        {
            if (fill == null) return;
            if (fill.Material != null) { s_SharedMaterials.Remove(fill.Material); UnityEngine.Object.Destroy(fill.Material); }
        }

        private static void LoadMedianMaterials(EntityManager manager, PrefabSystem prefabs,
            RoadGrassResolvedMedianAnchor source, RoadGrassResolvedMedianAnchor curbSource,
            RoadGrassPolygonPrototypeMesh.Profile profile, bool useGrass,
            out Material pavement, out Material grass, out List<TextureAsset> textures, out List<RenderTexture> fillTextures)
        {
            pavement = grass = null;
            fillTextures = new List<RenderTexture>();
            textures = new List<TextureAsset>();
            try
            {
                foreach (var materialSource in new[] { curbSource, source })
                {
                    bool physical = RoadGrassPolygonPrototypeMesh.HasPhysicalPieces(materialSource);
                    foreach (var piece in materialSource.Pieces)
                    {
                        if (pavement != null && grass != null) break;
                        if (!RoadGrassPolygonPrototypeMesh.IncludePiece(piece, physical)) continue;
                        if (!manager.HasBuffer<MeshMaterial>(piece.m_Piece) ||
                            !prefabs.TryGetPrefab<RenderPrefab>(piece.m_Piece, out var prefab)) continue;
                        var materials = manager.GetBuffer<MeshMaterial>(piece.m_Piece, true);
                        for (int i = 0; i < materials.Length; i++)
                        {
                            var surface = prefab.GetSurfaceAsset(i);
                            if (surface == null) continue;
                            // Source surface names distinguish the grass fill from the curb/pavement.
                            bool isGrass = surface.name.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                surface.name.IndexOf("lawn", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool curb = materialSource == curbSource && !isGrass && pavement == null && (profile.CurbPiece == Entity.Null ||
                                (piece.m_Piece == profile.CurbPiece && i == profile.CurbMaterial));
                            bool fill = materialSource == source && grass == null && (useGrass ? isGrass : !isGrass &&
                                piece.m_Piece == profile.PavementPiece && i == profile.PavementMaterial);
                            if (!curb && !fill) continue;
                            // Load the header before reading its template hash, including cold assets.
                            try
                            {
                                surface.LoadProperties(useVT: false);
                                var original = surface.GetTemplateMaterial();
                                if (original == null || original.shader == null || !original.shader.isSupported) continue;
                                var copy = new Material(original) { hideFlags = HideFlags.DontSave, enableInstancing = true };
                                if (curb) pavement = copy; else grass = copy;
                                // Match native material setup, but bind ordinary textures instead of VT stacks.
                                foreach (var property in surface.floats) if (copy.HasProperty(property.Key)) copy.SetFloat(property.Key, property.Value);
                                foreach (var property in surface.ints) if (copy.HasProperty(property.Key)) copy.SetInt(property.Key, property.Value);
                                foreach (var property in surface.vectors) if (copy.HasProperty(property.Key)) copy.SetVector(property.Key, property.Value);
                                foreach (var property in surface.colors) if (copy.HasProperty(property.Key)) copy.SetColor(property.Key, property.Value);
                                foreach (var keyword in surface.keywords)
                                    if (copy.shader.keywordSpace.FindKeyword(keyword).isValid) copy.EnableKeyword(keyword);
                                foreach (var texture in surface.textures)
                                {
                                    if (texture.Value == null || !copy.HasProperty(texture.Key)) continue;
                                    // Load increments the native refcount before reading the asset.
                                    textures.Add(texture.Value);
                                    copy.SetTexture(texture.Key, texture.Value.Load());
                                }
                                copy.DisableKeyword("_TANGENTSPACE_OCTO");
                                copy.DisableKeyword("ENABLE_VT");
                                HDMaterial.ValidateMaterial(copy);
                                if (curb && fill) grass = new Material(copy) { hideFlags = HideFlags.DontSave };
                                Mod.TryInfo($"[PolygonMaterial] surface={surface.name} shader={copy.shader.name} grass={isGrass}");
                            }
                            finally { surface.UnloadProperties(); }
                        }
                    }
                }
                if (!useGrass && grass != null)
                {
                    grass.name = "Median Controller polygon pavement fill";
                    float span = profile.PavementU.y - profile.PavementU.x;
                    if (span <= .00001f || profile.PavementScale.x <= 0 || profile.PavementScale.y <= 0)
                        throw new InvalidOperationException("Native median pavement mapping unavailable");
                    foreach (string property in grass.GetTexturePropertyNames())
                    {
                        var texture = grass.GetTexture(property);
                        if (texture == null || texture.dimension != UnityEngine.Rendering.TextureDimension.Tex2D ||
                            texture.width <= 4 || property.IndexOf("world", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            texture.name.IndexOf("worldspace", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        // Repeat the authored pavement band, retaining its albedo, packed normal and blend mask.
                        var format = GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat) ?
                            GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
                        var tile = new RenderTexture(new RenderTextureDescriptor(Math.Max(1, (int)Math.Round(texture.width * span)), texture.height, format, 0)
                        { useMipMap = true, autoGenerateMips = true })
                        { name = "Median Controller pavement " + property, hideFlags = HideFlags.DontSave,
                            wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = texture.anisoLevel };
                        fillTextures.Add(tile);
                        if (!tile.Create()) throw new InvalidOperationException("Native pavement texture allocation failed");
                        var previous = RenderTexture.active;
                        bool previousSRGB = GL.sRGBWrite;
                        GL.sRGBWrite = GraphicsFormatUtility.IsSRGBFormat(format);
                        try { Graphics.Blit(texture, tile, new Vector2(span, 1), new Vector2(profile.PavementU.x, 0)); }
                        finally { RenderTexture.active = previous; GL.sRGBWrite = previousSRGB; }
                        grass.SetTexture(property, tile);
                    }
                }
                if (pavement == null || grass == null)
                    throw new InvalidOperationException("Native median curb/fill material unavailable");
            }
            catch
            {
                if (pavement != null) UnityEngine.Object.Destroy(pavement);
                if (grass != null) UnityEngine.Object.Destroy(grass);
                foreach (var tile in fillTextures) UnityEngine.Object.Destroy(tile);
                fillTextures.Clear();
                foreach (var texture in textures) texture.Unload();
                textures.Clear();
                pavement = grass = null;
                throw;
            }
        }

        internal static bool TryLoad(out RoadGrassPolygonPrototypeResources resources, out string reason)
        {
            resources = new RoadGrassPolygonPrototypeResources();
            try
            {
                // Clone the game's validated opaque material, including its pass/queue/keyword setup.
                // The net-composition template requires curve data; Default accepts our independent vertices.
                Material template = AssetDatabase.global.resources.materialLibrary.GetMaterial("Default");
                if (template != null && template.shader != null && template.shader.isSupported)
                {
                    resources.Pavement = new Material(template) { hideFlags = HideFlags.DontSave };
                    resources.Pavement.SetFloat("_Metallic", 0);
                    resources.Pavement.SetFloat("_Smoothness", .25f);
                    resources.Pavement.enableInstancing = true;
                    // Independent vertices use ordinary tangent data and no virtual-texture bindings.
                    resources.Pavement.DisableKeyword("_TANGENTSPACE_OCTO");
                    resources.Pavement.DisableKeyword("ENABLE_VT");
                    template = resources.Pavement;
                }
                else
                {
                    // Use the player's compiled shader, including its HDRP and
                    // DOTS variants. A duplicate stock-Lit bundle adds no shading
                    // behavior and older Unity bundles are rejected by this player.
                    var shader = Shader.Find("HDRP/Lit");
                    if (shader == null || !shader.isSupported)
                        throw new InvalidOperationException("Native polygon fallback shader unavailable");
                    template = resources.Pavement = new Material(shader)
                    {
                        hideFlags = HideFlags.DontSave,
                        enableInstancing = true
                    };
                    template.SetFloat("_Metallic", 0);
                    template.SetFloat("_Smoothness", .25f);
                    HDMaterial.ValidateMaterial(template);
                }
                if (template == null || template.shader == null || !template.shader.isSupported || template.passCount == 0)
                    throw new InvalidOperationException("Polygon shader is unsupported by this renderer");
                if (resources.Pavement == null)
                    resources.Pavement = new Material(template) { hideFlags = HideFlags.DontSave };
                resources.Pavement.name = "Median Controller polygon pavement";
                resources.Grass = new Material(template) { name = "Median Controller polygon grass", hideFlags = HideFlags.DontSave };
                // Used only when the source median's native surface cannot be copied.
                resources.Pavement.SetColor("_BaseColor", new Color(.45f, .45f, .43f, 1));
                resources.Grass.SetColor("_BaseColor", new Color(.22f, .34f, .12f, 1));
                reason = "";
                return true;
            }
            catch (Exception exception)
            {
                resources.Dispose();
                resources = null;
                reason = exception.Message;
                return false;
            }
        }

        // Call after native batches using these materials have been unregistered.
        public void Dispose()
        {
            m_Disposed = true;
            ClearTerrainMaterials();
            foreach (var entry in m_MedianMaterials.Values)
            {
                s_SharedMaterials.Remove(entry.Pavement); s_SharedMaterials.Remove(entry.Fill);
                UnityEngine.Object.Destroy(entry.Pavement); UnityEngine.Object.Destroy(entry.Fill);
                foreach (var tile in entry.Tiles) UnityEngine.Object.Destroy(tile);
                foreach (var texture in entry.Textures) texture.Unload();
            }
            m_MedianMaterials.Clear();
            if (Pavement != null) UnityEngine.Object.Destroy(Pavement);
            if (Grass != null) UnityEngine.Object.Destroy(Grass);
            Pavement = null;
            Grass = null;
        }
    }
}
