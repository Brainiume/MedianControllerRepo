using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Colossal.Json;
using UnityEngine;

namespace RoadGrass
{
    // Library files are durable; city data stores only their stable numeric identity.
    internal static class RoadGrassCustomTextures
    {
        internal sealed class Entry
        {
            internal Manifest Definition;
            internal string Directory, Preview;
            internal Texture2D Base, Normal, Mask;
            internal bool Attempted;
            internal bool PreviewQueued;
        }

#pragma warning disable CS0649 // Field-based JSON DTO, matching the PO2 material library convention.
        [Serializable]
        internal sealed class Manifest
        {
            public int schemaVersion = 1, id;
            public string displayName, fingerprint;
            public string baseColor, normal, maskMap, metallic, occlusion, roughness, smoothness;
            public bool normalDirectX;
            public float tileSizeMeters = 1;
        }
#pragma warning restore CS0649

        private static readonly Dictionary<int, Entry> s_Entries = new();
        // shortcut: loaded library files are immutable until restart; re-import changed maps as a new material.
        private static readonly List<Entry> s_Ordered = new();
        internal static IEnumerable<Entry> Entries => s_Ordered;
        internal static int Version { get; private set; }
        internal static int PreviewVersion { get; private set; }
        private static readonly List<Entry> s_Previews = new();
        private static int s_PreviewHead;
        private static int s_PreviewChanges;
        internal static string Status { get; set; } = "Not imported";
        internal static string Root => Path.Combine(Application.persistentDataPath, "ModsData", "MedianController", "Materials");
        internal static string Imports => Path.Combine(Root, "Imports");
        internal static string Library => Path.Combine(Root, "Library");
        private static bool s_Loaded;

        internal static void EnsureLoaded()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            try
            {
                Directory.CreateDirectory(Imports);
                Directory.CreateDirectory(Library);
                foreach (string folder in Directory.GetDirectories(Library).Where(p => !Path.GetFileName(p).StartsWith(".", StringComparison.Ordinal)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    try
                    {
                        var manifest = JSON.MakeInto<Manifest>(JSON.Load(File.ReadAllText(Path.Combine(folder, "material.json"))));
                        ValidateManifest(manifest, folder);
                        if (s_Entries.TryGetValue(manifest.id, out var existing))
                        {
                            if (existing.Definition.fingerprint != manifest.fingerprint) throw new InvalidDataException("Duplicate material ID");
                            continue;
                        }
                        s_Entries.Add(manifest.id, new Entry { Definition = manifest, Directory = folder });
                    }
                    catch (Exception error) { Mod.TryWarn("Custom median material: " + folder + ": " + error.Message); }
                s_Ordered.Clear(); s_Ordered.AddRange(s_Entries.Values.OrderBy(e => e.Definition.displayName, StringComparer.OrdinalIgnoreCase));
                Version++;
            }
            catch (Exception error) { Status = error.Message; Mod.TryWarn("Custom median library: " + error.Message); }
        }

        internal static void OpenImports()
        {
            EnsureLoaded();
            try
            {
                string folder = Path.GetFullPath(Imports);
                Directory.CreateDirectory(folder);
                Colossal.RemoteProcess.OpenFolder(folder);
            }
            catch (Exception error) { Status = error.Message; Mod.TryWarn(Status); }
        }

        internal static void Import()
        {
            EnsureLoaded();
            int imported = 0, failed = 0;
            string lastError = "";
            try
            {
                foreach (string folder in Directory.GetDirectories(Imports).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string staging = null;
                    try
                    {
                        var files = Directory.GetFiles(folder, "*.png").ToDictionary(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase);
                        string Find(params string[] names)
                        {
                            var matches = files.Where(file => names.Any(name => file.Key.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                file.Key.EndsWith("_" + name, StringComparison.OrdinalIgnoreCase))).Select(file => file.Value).ToArray();
                            if (matches.Length > 1) throw new InvalidDataException("Ambiguous maps: " + string.Join(", ", names));
                            return matches.FirstOrDefault();
                        }
                        var maps = new[] { Find("basecolor", "basecolour", "base_color", "base_colour", "albedo", "diffuse"), Find("normal", "normalgl", "normaldx"),
                            Find("maskmap", "mask"), Find("metallic", "metalness"), Find("occlusion", "ao"), Find("roughness"), Find("smoothness") };
                        if (maps[0] == null) throw new InvalidDataException("basecolor.png is required");
                        using var digest = SHA256.Create();
                        using var signature = new MemoryStream();
                        for (int i = 0; i < maps.Length; i++)
                        {
                            signature.WriteByte((byte)i);
                            if (maps[i] == null) continue;
                            byte[] bytes = ReadPng(maps[i]);
                            var texture = Decode(bytes, i != 0);
                            UnityEngine.Object.Destroy(texture);
                            byte[] hash = digest.ComputeHash(bytes);
                            signature.Write(hash, 0, hash.Length);
                        }
                        bool directX = maps[1] != null && Path.GetFileNameWithoutExtension(maps[1]).EndsWith("normaldx", StringComparison.OrdinalIgnoreCase);
                        signature.WriteByte(directX ? (byte)1 : (byte)0);
                        byte[] identity = digest.ComputeHash(signature.ToArray());
                        int id = 1000 + (int)(BitConverter.ToUInt32(identity, 0) % 999999001);
                        string fingerprint = BitConverter.ToString(identity).Replace("-", "");
                        if (s_Entries.TryGetValue(id, out var existing))
                        {
                            if (existing.Definition.fingerprint != fingerprint) throw new InvalidDataException("Material ID collision; import rejected");
                            continue;
                        }
                        staging = Path.Combine(Library, ".import-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(staging);
                        string[] roles = { "basecolor.png", "normal.png", "maskmap.png", "metallic.png", "occlusion.png", "roughness.png", "smoothness.png" };
                        for (int i = 0; i < maps.Length; i++) if (maps[i] != null) File.Copy(maps[i], Path.Combine(staging, roles[i]));
                        var manifest = new Manifest { id = id, fingerprint = fingerprint, displayName = Path.GetFileName(folder),
                            baseColor = roles[0], normal = maps[1] == null ? null : roles[1], maskMap = maps[2] == null ? null : roles[2],
                            metallic = maps[3] == null ? null : roles[3], occlusion = maps[4] == null ? null : roles[4],
                            roughness = maps[5] == null ? null : roles[5], smoothness = maps[6] == null ? null : roles[6], normalDirectX = directX };
                        File.WriteAllText(Path.Combine(staging, "material.json"), JSON.Dump(manifest, EncodeOptions.PreserveUnicode), new UTF8Encoding(false));
                        string target = Path.Combine(Library, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Directory.Move(staging, target);
                        staging = null;
                        s_Entries.Add(id, new Entry { Definition = manifest, Directory = target });
                        imported++;
                    }
                    catch (Exception error) { failed++; lastError = Path.GetFileName(folder) + ": " + error.Message; Mod.TryWarn(lastError); }
                    finally { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
                }
                Version++;
                Status = $"{imported} imported; {s_Entries.Count} available; {failed} failed." + (failed == 0 ? "" : " " + lastError);
            }
            catch (Exception error) { Status = error.Message; Mod.TryWarn(Status); }
        }

        internal static void RefreshLibrary()
        {
            s_Loaded = false;
            EnsureLoaded();
            Status = s_Entries.Count + " custom surfaces available";
        }

        private static void ValidateManifest(Manifest manifest, string folder)
        {
            if (manifest == null || manifest.schemaVersion != 1 || !MedianTextureIdentity.IsCustom(manifest.id) ||
                string.IsNullOrWhiteSpace(manifest.displayName) || string.IsNullOrWhiteSpace(manifest.fingerprint) ||
                float.IsNaN(manifest.tileSizeMeters) || float.IsInfinity(manifest.tileSizeMeters) || manifest.tileSizeMeters < .1f || manifest.tileSizeMeters > 100)
                throw new InvalidDataException("Invalid custom material manifest");
            Resolve(folder, manifest.baseColor, true);
            foreach (var map in new[] { manifest.normal, manifest.maskMap, manifest.metallic, manifest.occlusion, manifest.roughness, manifest.smoothness }) Resolve(folder, map, false);
        }

        private static string Resolve(string folder, string file, bool required)
        {
            if (string.IsNullOrEmpty(file)) { if (required) throw new InvalidDataException("Base colour required"); return null; }
            if (Path.IsPathRooted(file) || Path.GetFileName(file) != file || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Texture must be a PNG filename inside its material folder");
            return Path.Combine(folder, file);
        }

        internal static byte[] ReadPng(string path)
        {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("PNG exceeds 64 MiB");
            byte[] bytes = File.ReadAllBytes(path);
            byte[] header = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
            if (bytes.Length < 24 || !header.SequenceEqual(bytes.Take(16))) throw new InvalidDataException("Invalid PNG header");
            int Dimension(int offset) => (bytes[offset] << 24) | (bytes[offset+1] << 16) | (bytes[offset+2] << 8) | bytes[offset+3];
            if (Dimension(16) < 1 || Dimension(20) < 1 || Dimension(16) > 4096 || Dimension(20) > 4096)
                throw new InvalidDataException("PNG dimensions must be 1-4096 pixels");
            return bytes;
        }

        private static Texture2D Decode(byte[] bytes, bool linear)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear)
                { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
            try { if (!ImageConversion.LoadImage(texture, bytes, false)) throw new InvalidDataException("PNG decode failed"); return texture; }
            catch { UnityEngine.Object.Destroy(texture); throw; }
        }

        internal static bool TryGet(PolygonTerrainLayer layer, out Entry entry)
        {
            EnsureLoaded();
            if (!s_Entries.TryGetValue((int)layer, out entry)) return false;
            if (entry.Attempted) return entry.Mask != null;
            entry.Attempted = true;
            var current = entry;
            var acquired = new List<Texture2D>();
            try
            {
                Texture2D Load(string file, bool linear)
                {
                    string path = Resolve(current.Directory, file, false);
                    if (path == null) return null;
                    var image = Decode(ReadPng(path), linear); acquired.Add(image); return image;
                }
                var definition = entry.Definition;
                if (entry.Base == null) entry.Base = Decode(ReadPng(Resolve(entry.Directory, definition.baseColor, true)), false);
                entry.Base.Apply(false, true);
                var normal = Load(definition.normal, true);
                var mask = Load(definition.maskMap, true);
                var metallic = Load(definition.metallic, true); var ao = Load(definition.occlusion, true);
                var roughness = Load(definition.roughness, true); var smoothness = Load(definition.smoothness, true);
                int width = new[] { mask, metallic, ao, roughness, smoothness }.Where(t => t != null).Select(t => t.width).DefaultIfEmpty(1).Max();
                int height = new[] { mask, metallic, ao, roughness, smoothness }.Where(t => t != null).Select(t => t.height).DefaultIfEmpty(1).Max();
                var packed = new Texture2D(width, height, TextureFormat.RGBA32, true, true)
                    { hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
                acquired.Add(packed);
                Color32[] Pixels(Texture2D image)
                {
                    if (image == null) return null;
                    return Resample(image, width, height);
                }
                var maskPixels = Pixels(mask); var metalPixels = Pixels(metallic); var aoPixels = Pixels(ao);
                var roughPixels = Pixels(roughness); var smoothPixels = Pixels(smoothness);
                var pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = PackMask(maskPixels?[i] ?? new Color32(0,255,0,64),
                    metalPixels?[i].r, aoPixels?[i].r, roughPixels?[i].r, smoothPixels?[i].r);
                packed.SetPixels32(pixels); packed.Apply(true, true);
                if (normal != null)
                {
                    pixels = normal.GetPixels32();
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = PackNormal(pixels[i], definition.normalDirectX);
                    normal.SetPixels32(pixels); normal.Apply(true, true);
                }
                entry.Normal = normal; entry.Mask = packed;
                acquired.Remove(normal); acquired.Remove(packed);
                return true;
            }
            catch (Exception error) { Mod.TryWarn("Custom median texture " + entry.Definition.displayName + ": " + error.Message); Status = error.Message; return false; }
            finally { foreach (var image in acquired) UnityEngine.Object.Destroy(image); }
        }

        internal static bool Contains(int id) { EnsureLoaded(); return s_Entries.ContainsKey(id); }

        internal static Color32[] Resample(Texture2D image, int width, int height)
        {
            var source = image.GetPixels32();
            int w = image.width, h = image.height;
            if (w == width && h == height) return source;
            var result = new Color32[width * height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                float sx = (x + .5f) * w / width - .5f, sy = (y + .5f) * h / height - .5f;
                int ax = (int)Math.Floor(sx), ay = (int)Math.Floor(sy);
                int x0 = (ax % w + w) % w, x1 = (x0 + 1) % w, y0 = (ay % h + h) % h, y1 = (y0 + 1) % h;
                result[y * width + x] = Color32.Lerp(Color32.Lerp(source[y0 * w + x0], source[y0 * w + x1], sx - ax),
                    Color32.Lerp(source[y1 * w + x0], source[y1 * w + x1], sx - ax), sy - ay);
            }
            return result;
        }
        internal static void RequestPreview(Entry entry)
        {
            if (entry.Preview != null || entry.PreviewQueued) return;
            entry.PreviewQueued = true; s_Previews.Add(entry);
        }
        internal static void PumpPreviews()
        {
            if (s_PreviewHead == s_Previews.Count) return;
            TryPreview(s_Previews[s_PreviewHead++]); s_PreviewChanges++;
            // Publish preview batches instead of resending the whole base64 catalog for every thumbnail.
            if (s_PreviewChanges >= 8 || s_PreviewHead == s_Previews.Count) { PreviewVersion++; s_PreviewChanges = 0; }
            if (s_PreviewHead == s_Previews.Count) { s_Previews.Clear(); s_PreviewHead = 0; }
        }
        internal static bool TryPreview(Entry entry)
        {
            if (entry.Preview != null) return entry.Preview.Length != 0;
            entry.Preview = "";
            Texture2D image = null;
            try
            {
                string thumbnail = Path.Combine(entry.Directory, "preview.png");
                string cached = Path.Combine(Root, "Previews", entry.Definition.id + "-128.png");
                foreach (var path in new[] { thumbnail, cached })
                    try
                    {
                        if (!File.Exists(path)) continue;
                        byte[] bytes = ReadPng(path);
                        if (bytes[16] != 0 || bytes[17] != 0 || bytes[18] != 0 || bytes[19] != 128 ||
                            bytes[20] != 0 || bytes[21] != 0 || bytes[22] != 0 || bytes[23] != 128) continue;
                        entry.Preview = "data:image/png;base64," + Convert.ToBase64String(bytes); return true;
                    }
                    catch (Exception) { } // A damaged optional thumbnail must not disable a valid material.
                // Older imports have no thumbnail. Decode one preview per UI update without retaining the full base map.
                image = Decode(ReadPng(Resolve(entry.Directory, entry.Definition.baseColor, true)), false);
                var pixels = Resample(image, 128, 128);
                for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
                UnityEngine.Object.Destroy(image);
                image = new Texture2D(128, 128, TextureFormat.RGBA32, false, false);
                image.SetPixels32(pixels);
                byte[] preview = image.EncodeToPNG();
                entry.Preview = "data:image/png;base64," + Convert.ToBase64String(preview);
                try { Directory.CreateDirectory(Path.GetDirectoryName(cached)); File.WriteAllBytes(cached, preview); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return true;
            }
            catch (Exception error) { Status = error.Message; Mod.TryWarn(Status); return false; }
            finally { if (image != null) UnityEngine.Object.Destroy(image); }
        }

        internal static Color32 PackNormal(Color32 normal, bool directX) => new Color32(normal.r, directX ? (byte)(255 - normal.g) : normal.g, 255, 255);

        internal static Color32 PackMask(Color32 mask, byte? metal, byte? ao, byte? rough, byte? smooth) =>
            // Blue belongs to native road blending, not the HDRP detail/height channel.
            new Color32(metal ?? mask.r, ao ?? mask.g, 0, smooth ?? (rough.HasValue ? (byte)(255 - rough.Value) : mask.a));

        internal static void Dispose()
        {
            foreach (var entry in s_Entries.Values)
            { if (entry.Base != null) UnityEngine.Object.Destroy(entry.Base); if (entry.Normal != null) UnityEngine.Object.Destroy(entry.Normal); if (entry.Mask != null) UnityEngine.Object.Destroy(entry.Mask); }
            s_Entries.Clear(); s_Loaded = false; Version++;
            s_Previews.Clear(); s_PreviewHead = 0; PreviewVersion++;
            s_Ordered.Clear(); s_PreviewChanges = 0;
        }
    }
}
