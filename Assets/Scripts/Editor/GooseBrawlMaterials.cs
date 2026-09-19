using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GooseBrawl.Editor
{
    /// <summary>
    /// Creates the material assets (so URP shaders ship in the build) and the goose materials from the
    /// supplied textures. Every property is re-applied on each run, so tuning here always takes effect.
    /// </summary>
    public static class GooseBrawlMaterials
    {
        public const string Folder = "Assets/Materials";
        public const string GeneratedFolder = "Assets/Art/Generated";

        public static void Apply(MaterialLibrary lib, string gooseFolder)
        {
            GooseBrawlSetup.EnsureFolder(Folder);
            GooseBrawlSetup.EnsureFolder(GeneratedFolder);

            var radial = SaveTexture("Radial", () => ProceduralAssets.RadialTexture(256), TextureWrapMode.Clamp);
            var checker = SaveTexture("Checker", () => ProceduralAssets.CheckerTexture(256), TextureWrapMode.Repeat);
            var dotGrid = SaveTexture("DotGrid", () => ProceduralAssets.DotGridTexture(256), TextureWrapMode.Repeat);
            var softRing = SaveTexture("RingSoft", () => ProceduralAssets.SoftRingTexture(256), TextureWrapMode.Clamp);
            var twigStrip = SaveTexture("TwigStrip", () => ProceduralAssets.TwigStripTexture(512), TextureWrapMode.Repeat);
            var twigNormal = SaveTexture("TwigNormal", () => ProceduralAssets.TwigNormalTexture(512), TextureWrapMode.Repeat, normalMap: true);
            SaveTexture("Feather", () => ProceduralAssets.FeatherTexture(64, 128), TextureWrapMode.Clamp);

            lib.gooseMaterial = GooseMaterial("Goose", gooseFolder, grey: false);
            lib.gooseMaterialGrey = GooseMaterial("GooseGrey", gooseFolder, grey: true);
            lib.placeholderGooseMaterial = Lit("PlaceholderGoose", new Color(0.95f, 0.95f, 0.93f), 0.3f);
            lib.accentMaterial = Lit("GooseAccent", new Color(1f, 0.55f, 0.1f), 0.4f);
            lib.nestMaterial = Lit("Nest", Color.white, 0.14f, default, twigStrip, 1f, twigNormal);
            lib.eggMaterial = Lit("Egg", new Color(0.99f, 0.95f, 0.84f), 0.62f);
            lib.glowMaterial = UnlitTransparent("NestGlow", new Color(1f, 0.85f, 0.25f, 0.8f), radial);
            lib.shadowMaterial = ContactShadow("BlobShadow", radial);
            lib.shadowCatcherMaterial = ShadowCatcher("ShadowCatcher");
            lib.reticleMaterial = UnlitTransparent("Reticle", new Color(1f, 0.965f, 0.87f, 0.85f), softRing);
            lib.planeMaterial = UnlitTransparent("ARPlane", new Color(1f, 0.965f, 0.87f, 0.3f), dotGrid, 4f);
            lib.dustMaterial = UnlitTransparent("Dust", new Color(0.85f, 0.78f, 0.62f, 0.7f), radial);
            lib.mockFloorMaterial = Lit("MockFloor", new Color(0.7f, 0.7f, 0.72f), 0.1f, default, checker, 8f);
            lib.mockWallMaterial = Lit("MockWall", new Color(0.55f, 0.6f, 0.7f), 0.2f);
            lib.nestModel = FindNestModel();
            AssetDatabase.SaveAssets();
        }

        /// <summary>Any model under Assets/Art/Nest becomes the nest (drop an FBX/OBJ there and re-run Setup).</summary>
        static GameObject FindNestModel()
        {
            const string folder = "Assets/Art/Nest";
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go != null)
                {
                    Debug.Log("[Goose Brawl] Using nest model: " + path);
                    return go;
                }
            }
            return null;
        }

        public static Texture2D SaveTexture(string name, System.Func<Texture2D> make, TextureWrapMode wrap, bool normalMap = false, bool linear = false)
        {
            string path = GeneratedFolder + "/" + name + ".png";
            if (!File.Exists(path))
            {
                var tex = make();
                File.WriteAllBytes(path, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(path);
            }
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                bool changed = false;
                if (importer.wrapMode != wrap) { importer.wrapMode = wrap; changed = true; }
                if (!importer.alphaIsTransparency && !normalMap) { importer.alphaIsTransparency = true; changed = true; }
                if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; changed = true; }
                var wantType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
                if (importer.textureType != wantType) { importer.textureType = wantType; changed = true; }
                if ((normalMap || linear) && importer.sRGBTexture) { importer.sRGBTexture = false; changed = true; }
                if (changed) importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Material LoadOrCreate(string name, string shaderName)
        {
            string path = Folder + "/" + name + ".mat";
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning("[Goose Brawl] Shader not found: " + shaderName + ", falling back to URP Lit.");
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                if (existing.shader != shader) existing.shader = shader;
                return existing;
            }
            var m = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material Lit(string name, Color color, float smoothness, Color emission = default, Texture texture = null, float tiling = 1f, Texture normal = null)
        {
            var m = LoadOrCreate(name, "Universal Render Pipeline/Lit");
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", 0f);
            m.SetTexture("_BaseMap", texture);
            m.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            else
            {
                m.SetTexture("_BumpMap", null);
                m.DisableKeyword("_NORMALMAP");
            }
            if (emission != default)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                m.DisableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", Color.black);
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material UnlitTransparent(string name, Color color, Texture texture, float tiling = 1f)
        {
            var m = LoadOrCreate(name, "Universal Render Pipeline/Unlit");
            ProceduralAssets.ConfigureTransparentUnlit(m, color, texture);
            m.SetTexture("_BaseMap", texture);
            m.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material ContactShadow(string name, Texture radial)
        {
            var m = LoadOrCreate(name, "GooseBrawl/ContactShadow");
            m.SetTexture("_BaseMap", radial);
            m.SetFloat("_Strength", 0.28f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material ShadowCatcher(string name)
        {
            var m = LoadOrCreate(name, "GooseBrawl/ShadowCatcher");
            m.SetFloat("_Strength", 0.55f);
            m.SetColor("_Tint", new Color(0.02f, 0.015f, 0.03f, 1f));
            m.SetFloat("_EdgeFade", 0.35f);
            m.SetFloat("_EnvTolerance", 0.12f);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>URP Lit material for the goose: albedo (alpha-clipped feather cards), full-res normal, metallic/smoothness, baked occlusion.</summary>
        static Material GooseMaterial(string name, string gooseFolder, bool grey)
        {
            var m = LoadOrCreate(name, "Universal Render Pipeline/Lit");
            m.SetFloat("_Cull", (float)CullMode.Off); // double sided: never see inside the goose

            var albedo = grey
                ? FindTexture(gooseFolder, "Grey_AlbedoTransparency", "Grey_BaseColor")
                : FindTexture(gooseFolder, "White_AlbedoTransparency", "BaseColor_M", "White_BaseColor", "albedo", "BaseColor", "Albedo", "Diffuse");
            // Prefer the full resolution normal over the 512 px mobile one.
            var normal = FindTexture(gooseFolder, "Goose_Normal.tif", "Goose_Normal", "Normal_M", "Normal", "normal");
            var metallic = FindTexture(gooseFolder, "MetallicSmoothness", "Metallic");
            var orm = FindTexture(gooseFolder, "OcclusionRoughnessMetallic");

            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Metallic", 0f);
            if (albedo != null)
            {
                ConfigureTexture(albedo, false, maxSize: 2048);
                m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(albedo));
                // Feather cards use alpha: enable clipping; alpha-to-coverage smooths the cut with MSAA.
                m.SetFloat("_AlphaClip", 1f);
                m.SetFloat("_Cutoff", 0.4f);
                m.SetFloat("_AlphaToMask", 1f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            if (normal != null)
            {
                ConfigureTexture(normal, true, maxSize: 2048);
                m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normal));
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            if (metallic != null)
            {
                ConfigureTexture(metallic, false, linear: true, maxSize: 1024);
                m.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(metallic));
                // 0.55 tames the waxy specular of the source map; smoothness comes from the map's alpha.
                m.SetFloat("_Smoothness", 0.55f);
                m.SetFloat("_SmoothnessTextureChannel", 0f);
                m.EnableKeyword("_METALLICSPECGLOSSMAP");
            }
            else
            {
                m.SetFloat("_Smoothness", 0.4f);
            }
            var occlusion = BakeOcclusion(orm);
            if (occlusion != null)
            {
                m.SetTexture("_OcclusionMap", occlusion);
                m.SetFloat("_OcclusionStrength", 1f);
                m.EnableKeyword("_OCCLUSIONMAP");
            }
            EditorUtility.SetDirty(m);
            Debug.Log("[Goose Brawl] " + name + " material: albedo=" + (albedo ?? "none") + " normal=" + (normal ?? "none") + " metallic=" + (metallic ?? "none") + " occlusion=" + (occlusion != null ? "baked" : "none"));
            return m;
        }

        /// <summary>URP reads occlusion from the G channel; the packed ORM map has it in R. Bake once to Generated/Goose_Occlusion.png.</summary>
        static Texture2D BakeOcclusion(string ormPath)
        {
            if (ormPath == null) return null;
            string outPath = GeneratedFolder + "/Goose_Occlusion.png";
            if (!File.Exists(outPath))
            {
                var src = AssetDatabase.LoadAssetAtPath<Texture2D>(ormPath);
                if (src == null) return null;
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                var prev = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                var px = readable.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    byte ao = px[i].r;
                    px[i] = new Color32(ao, ao, ao, 255);
                }
                readable.SetPixels32(px);
                readable.Apply();
                File.WriteAllBytes(outPath, readable.EncodeToPNG());
                Object.DestroyImmediate(readable);
                AssetDatabase.ImportAsset(outPath);
                var importer = AssetImporter.GetAtPath(outPath) as TextureImporter;
                if (importer != null)
                {
                    importer.sRGBTexture = false;
                    importer.mipmapEnabled = true;
                    importer.maxTextureSize = 1024;
                    importer.SaveAndReimport();
                }
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        static string FindTexture(string folder, params string[] nameHints)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return null;
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { folder });
            foreach (var hint in nameHints)
            {
                foreach (var g in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.StartsWith(GeneratedFolder)) continue;
                    string file = Path.GetFileName(p);
                    if (hint.Contains(".") ? file.Equals(hint, System.StringComparison.OrdinalIgnoreCase)
                                           : Path.GetFileNameWithoutExtension(p).IndexOf(hint, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return p;
                }
            }
            return null;
        }

        static void ConfigureTexture(string path, bool normalMap, bool linear = false, int maxSize = 1024)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return;
            bool changed = false;
            if (normalMap && importer.textureType != TextureImporterType.NormalMap) { importer.textureType = TextureImporterType.NormalMap; changed = true; }
            if ((linear || normalMap) && importer.sRGBTexture) { importer.sRGBTexture = false; changed = true; }
            if (importer.maxTextureSize != maxSize) { importer.maxTextureSize = maxSize; changed = true; }
            if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; changed = true; }
            if (changed) importer.SaveAndReimport();
        }
    }
}
