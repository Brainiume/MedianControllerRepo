using System;
using UnityEngine;
using Colossal.IO.AssetDatabase;
using Game.Prefabs;
using Unity.Entities;
using System.Collections.Generic;
using UnityEngine.Rendering.HighDefinition;

namespace RoadGrass
{
    // Runtime renderer cache only. No resource handles belong in saved city data.
    internal sealed class RoadGrassPolygonPrototypeResources : IDisposable
    {
        internal Material Pavement { get; private set; }
        internal Material Grass { get; private set; }

        internal static void CopyMedianMaterials(EntityManager manager, PrefabSystem prefabs,
            RoadGrassResolvedMedianAnchor source, bool useGrass, out Material pavement, out Material grass, out List<TextureAsset> textures)
        {
            pavement = grass = null;
            textures = new List<TextureAsset>();
            try
            {
                foreach (var piece in source.Pieces)
                {
                    if (pavement != null && (!useGrass || grass != null)) break;
                    if ((piece.m_PieceFlags & NetPieceFlags.Bottom) != 0 ||
                        (piece.m_SectionFlags & (NetSectionFlags.Hidden | NetSectionFlags.Underground | NetSectionFlags.Overhead)) != 0) continue;
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
                        if (isGrass ? !useGrass || grass != null : pavement != null) continue;
                        // Load the header before reading its template hash, including cold assets.
                        try
                        {
                            surface.LoadProperties(useVT: false);
                            var original = surface.GetTemplateMaterial();
                            if (original == null || original.shader == null || !original.shader.isSupported) continue;
                            var copy = new Material(original) { hideFlags = HideFlags.DontSave, enableInstancing = true };
                            if (isGrass) grass = copy; else pavement = copy;
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
                            Mod.TryInfo($"[PolygonMaterial] surface={surface.name} shader={copy.shader.name} grass={isGrass}");
                        }
                        finally { surface.UnloadProperties(); }
                    }
                }
                if (pavement == null || (useGrass && grass == null))
                    Mod.TryWarn($"[PolygonMaterial] source material incomplete: pavement={pavement != null} grass={grass != null}; unmatched surfaces retain prototype fallback");
            }
            catch
            {
                if (pavement != null) UnityEngine.Object.Destroy(pavement);
                if (grass != null) UnityEngine.Object.Destroy(grass);
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
            if (Pavement != null) UnityEngine.Object.Destroy(Pavement);
            if (Grass != null) UnityEngine.Object.Destroy(Grass);
            Pavement = null;
            Grass = null;
        }
    }
}
