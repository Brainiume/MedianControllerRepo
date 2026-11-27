using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace RoadGrass
{
    internal static class RoadGrassTextureImportForm
    {
        internal static void Open()
        {
            try
            {
                Directory.CreateDirectory(RoadGrassCustomTextures.Library);
                File.WriteAllText(Path.Combine(RoadGrassCustomTextures.Library, ".median-controller-materials"), "MedianController.MaterialLibrary.1");
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("RoadGrass.TextureImporter.html");
                if (resource == null) throw new InvalidOperationException("Texture importer form resource unavailable");
                using var reader = new StreamReader(resource);
                string html = reader.ReadToEnd().Replace("__SURFACE_FOLDER__", System.Net.WebUtility.HtmlEncode(RoadGrassCustomTextures.Library));
                string path = Path.Combine(RoadGrassCustomTextures.Root, "TextureImporter.html");
                File.WriteAllText(path, html, new UTF8Encoding(false));
                Application.OpenURL(new Uri(path).AbsoluteUri);
            }
            catch (Exception error) { RoadGrassCustomTextures.Status = error.Message; Mod.TryWarn("Texture importer form: " + error.Message); }
        }
    }
}
