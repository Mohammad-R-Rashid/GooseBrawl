using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GooseBrawl
{
    /// <summary>
    /// Runtime-generated textures, materials, meshes and particle systems so the game has no dependency on art files.
    /// Textures are RGBA32, readable and Apply()'d so editor code can EncodeToPNG them. Meshes have smooth normals,
    /// UVs, tangents and bounds. Everything is deterministic for a given seed.
    /// </summary>
    public static class ProceduralAssets
    {
        // ------------------------------------------------------------------------------------------------
        // Texture cache (first call wins for parameterless-style textures; keyed by parameters otherwise).
        // ------------------------------------------------------------------------------------------------

        static readonly Dictionary<string, Texture2D> s_TexCache = new Dictionary<string, Texture2D>();

        static Texture2D Cached(string key, Func<Texture2D> make)
        {
            if (s_TexCache.TryGetValue(key, out var tex) && tex != null) return tex; // Unity fake-null => regenerate
            tex = make();
            s_TexCache[key] = tex;
            return tex;
        }

        static Texture2D NewTexture(string name, int w, int h, bool mips, TextureWrapMode wrap, bool linear = false)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, mips, linear)
            {
                name = name,
                wrapMode = wrap,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                anisoLevel = mips ? 4 : 1,
            };
            return t;
        }

        static float SmoothStep(float a, float b, float x)
        {
            if (b <= a) return x < a ? 0f : 1f;
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Anti-aliased coverage for a signed distance (negative inside) with a ~1.5 px edge.</summary>
        static float Coverage(float sdf, float px)
        {
            return 1f - SmoothStep(-0.75f * px, 0.75f * px, sdf);
        }

        public static Texture2D RadialTexture(int size = 128)
        {
            return Cached("Radial", () =>
            {
                var tex = NewTexture("RadialGradient", size, size, false, TextureWrapMode.Clamp);
                var px = new Color[size * size];
                float half = size * 0.5f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half)) / half;
                        float a = Mathf.Clamp01(1f - d);
                        a = a * a * (3f - 2f * a);
                        px[y * size + x] = new Color(1f, 1f, 1f, a);
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        public static Texture2D CheckerTexture(int size = 256, int cells = 8)
        {
            return Cached("Checker", () =>
            {
                var tex = NewTexture("Checker", size, size, true, TextureWrapMode.Repeat);
                var px = new Color[size * size];
                int cell = Mathf.Max(1, size / Mathf.Max(1, cells));
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        bool on = ((x / cell) + (y / cell)) % 2 == 0;
                        px[y * size + x] = on ? new Color(0.72f, 0.72f, 0.74f) : new Color(0.5f, 0.5f, 0.54f);
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        // ------------------------------------------------------------------------------------------------
        // Materials
        // ------------------------------------------------------------------------------------------------

        static Shader FindShader(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null) return s;
            }
            return null;
        }

        public static Material LitMaterial(string name, Color color, float smoothness = 0.3f, Color emission = default, Texture texture = null)
        {
            var shader = FindShader("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Standard");
            var m = new Material(shader) { name = name };
            m.SetColor("_BaseColor", color);
            m.SetColor("_Color", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Glossiness", smoothness);
            if (texture != null)
            {
                m.SetTexture("_BaseMap", texture);
                m.SetTexture("_MainTex", texture);
                m.SetTextureScale("_BaseMap", new Vector2(8f, 8f));
            }
            if (emission != default)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            return m;
        }

        public static Material UnlitTransparent(string name, Color color, Texture texture)
        {
            var shader = FindShader("Universal Render Pipeline/Unlit", "Unlit/Transparent", "Sprites/Default");
            var m = new Material(shader) { name = name };
            ConfigureTransparentUnlit(m, color, texture);
            return m;
        }

        /// <summary>Configures a URP Unlit material as alpha-blended, double sided, no depth write.</summary>
        public static void ConfigureTransparentUnlit(Material m, Color color, Texture texture)
        {
            m.SetColor("_BaseColor", color);
            m.SetColor("_Color", color);
            if (texture != null)
            {
                m.SetTexture("_BaseMap", texture);
                m.SetTexture("_MainTex", texture);
            }
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_Cull", (float)CullMode.Off);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        // ------------------------------------------------------------------------------------------------
        // Simple flat GameObjects
        // ------------------------------------------------------------------------------------------------

        /// <summary>Flat ring mesh lying on the XZ plane (for the placement reticle).</summary>
        public static GameObject CreateRing(string name, float radius, float thickness, Material mat, int segments = 40)
        {
            var go = new GameObject(name);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            var mesh = new Mesh { name = name + "_Mesh" };
            float inner = Mathf.Max(0f, radius - thickness);
            var verts = new Vector3[segments * 2];
            var uvs = new Vector2[segments * 2];
            var tris = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                verts[i * 2] = new Vector3(c * inner, 0f, s * inner);
                verts[i * 2 + 1] = new Vector3(c * radius, 0f, s * radius);
                uvs[i * 2] = new Vector2(0.5f + c * 0.25f, 0.5f + s * 0.25f);
                uvs[i * 2 + 1] = new Vector2(0.5f + c * 0.5f, 0.5f + s * 0.5f);
                int n = (i + 1) % segments;
                int t = i * 6;
                tris[t] = i * 2; tris[t + 1] = n * 2 + 1; tris[t + 2] = i * 2 + 1;
                tris[t + 3] = i * 2; tris[t + 4] = n * 2; tris[t + 5] = n * 2 + 1;
            }
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            return go;
        }

        /// <summary>A size x size quad lying in the XZ plane (normal +Y), UVs 0..1, no shadows, no collider.</summary>
        public static GameObject CreateQuad(string name, float size, Material mat)
        {
            var go = new GameObject(name);
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            float h = size * 0.5f;
            var mesh = new Mesh { name = name + "_Mesh" };
            mesh.vertices = new[]
            {
                new Vector3(-h, 0f, -h), new Vector3(h, 0f, -h),
                new Vector3(-h, 0f, h), new Vector3(h, 0f, h),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            // (A, C, B), (B, C, D) => cross products point +Y (Unity clockwise front faces).
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            return go;
        }

        // ------------------------------------------------------------------------------------------------
        // Noise and distance-field helpers
        // ------------------------------------------------------------------------------------------------

        static float Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2246822519u);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
        }

        /// <summary>Smooth value noise in [0,1].</summary>
        static float ValueNoise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        /// <summary>Value noise that tiles every <paramref name="periodX"/> lattice cells along x (x is in lattice units).</summary>
        static float TiledNoise(float x, float y, int periodX, int seed)
        {
            periodX = Mathf.Max(1, periodX);
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            int x0 = ((xi % periodX) + periodX) % periodX;
            int x1 = (x0 + 1) % periodX;
            float a = Hash(x0, yi, seed), b = Hash(x1, yi, seed), c = Hash(x0, yi + 1, seed), d = Hash(x1, yi + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        /// <summary>Periodic noise around a circle: angle01 in [0,1] wraps seamlessly. Returns [0,1].</summary>
        static float RingNoise(float angle01, float v, float freq, int seed)
        {
            float a = angle01 * Mathf.PI * 2f;
            // Sample 2D noise along a circle of radius freq (periodic by construction) and offset by v for variety.
            return ValueNoise(Mathf.Cos(a) * freq + 17.3f + v * 0.5f, Mathf.Sin(a) * freq + 9.1f + v * 2.7f, seed);
        }

        static float SdCircle(Vector2 p, Vector2 c, float r) => (p - c).magnitude - r;

        static float SdSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 pa = p - a, ba = b - a;
            float bb = Vector2.Dot(ba, ba);
            float h = bb > 1e-12f ? Mathf.Clamp01(Vector2.Dot(pa, ba) / bb) : 0f;
            return (pa - ba * h).magnitude;
        }

        /// <summary>Approximate signed distance to an ellipse centred at c with radii r, rotated by angleDeg.</summary>
        static float SdEllipse(Vector2 p, Vector2 c, Vector2 r, float angleDeg)
        {
            float ca = Mathf.Cos(-angleDeg * Mathf.Deg2Rad), sa = Mathf.Sin(-angleDeg * Mathf.Deg2Rad);
            Vector2 d = p - c;
            float x = d.x * ca - d.y * sa, y = d.x * sa + d.y * ca;
            float rx = Mathf.Max(r.x, 1e-5f), ry = Mathf.Max(r.y, 1e-5f);
            float k0 = Mathf.Sqrt(x * x / (rx * rx) + y * y / (ry * ry));
            float k1 = Mathf.Sqrt(x * x / (rx * rx * rx * rx) + y * y / (ry * ry * ry * ry));
            if (k1 < 1e-6f) return -Mathf.Min(rx, ry);
            return k0 * (k0 - 1f) / k1;
        }

        static float SdTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 e0 = b - a, e1 = c - b, e2 = a - c;
            Vector2 v0 = p - a, v1 = p - b, v2 = p - c;
            Vector2 pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Mathf.Max(1e-12f, Vector2.Dot(e0, e0)));
            Vector2 pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Mathf.Max(1e-12f, Vector2.Dot(e1, e1)));
            Vector2 pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Mathf.Max(1e-12f, Vector2.Dot(e2, e2)));
            float s = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
            float dx = Mathf.Min(Vector2.Dot(pq0, pq0), Mathf.Min(Vector2.Dot(pq1, pq1), Vector2.Dot(pq2, pq2)));
            float dy = Mathf.Min(s * (v0.x * e0.y - v0.y * e0.x), Mathf.Min(s * (v1.x * e1.y - v1.y * e1.x), s * (v2.x * e2.y - v2.y * e2.x)));
            return -Mathf.Sqrt(Mathf.Max(0f, dx)) * Mathf.Sign(dy);
        }

        // ------------------------------------------------------------------------------------------------
        // Surface textures
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// A small white feather card: narrow quill base, widest at 60% of the length, rounded tip, fine barb lines
        /// angled ~35 degrees from the shaft, soft alpha edge and a slightly darker quill. v = 0 is the quill base.
        /// Cached: the first call's size wins.
        /// </summary>
        public static Texture2D FeatherTexture(int w = 64, int h = 128)
        {
            return Cached("Feather", () =>
            {
                var tex = NewTexture("Feather", w, h, true, TextureWrapMode.Clamp);
                var px = new Color[w * h];
                Color vane = new Color(0.975f, 0.965f, 0.945f, 1f);
                Color barbCol = new Color(0.80f, 0.80f, 0.81f, 1f);
                Color quill = new Color(0.84f, 0.79f, 0.70f, 1f);
                const float hwMax = 0.46f;                 // half-width in u units (u in -1..1)
                float cosB = Mathf.Cos(35f * Mathf.Deg2Rad), sinB = Mathf.Sin(35f * Mathf.Deg2Rad);
                float pxU = 2f / w;                        // one texel in u units
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float v = (y + 0.5f) / h;                 // 0 = quill base, 1 = tip
                        float u = (x + 0.5f) / w * 2f - 1f;       // -1..1 across
                        float au = Mathf.Abs(u);
                        float hw;
                        if (v <= 0.6f)
                        {
                            float s = v / 0.6f;
                            hw = 0.04f + (hwMax - 0.04f) * (1f - (1f - s) * (1f - s));
                        }
                        else
                        {
                            float s = (v - 0.6f) / 0.4f;
                            hw = hwMax * Mathf.Sqrt(Mathf.Max(0f, 1f - s * s));
                        }
                        hw = Mathf.Max(hw, 0.0f);
                        // Signed distance to the silhouette (approx.): horizontal distance, plus the tip.
                        float sd = au - hw;
                        if (v > 0.985f) sd = Mathf.Max(sd, (v - 0.985f) * 8f);
                        float inside = Coverage(sd, pxU);
                        // Soft edge: alpha fades over the outer 8% of the half-width.
                        float edge = hw > 1e-4f ? SmoothStep(0f, 0.08f, (hw - au) / hw) : 0f;
                        float alpha = inside * edge;
                        // Barb lines at 35 degrees from the shaft, sweeping toward the tip, ~3.5 px apart.
                        float X = au * (w * 0.5f), Y = v * h;
                        float q = X * cosB - Y * sinB;
                        float spacing = 3.5f;
                        float fq = q / spacing;
                        float dist = Mathf.Abs(fq - Mathf.Round(fq)) * spacing;
                        float pulse = 1f - SmoothStep(0.25f, 0.85f, dist);
                        pulse *= SmoothStep(0.02f, 0.10f, v);       // no barbs right at the base
                        Color c = Color.Lerp(vane, barbCol, pulse * 0.4f);
                        // Barbs separate slightly near the outer edge: nick the alpha there.
                        alpha *= 1f - 0.35f * pulse * (1f - SmoothStep(0.08f, 0.35f, hw > 1e-4f ? (hw - au) / hw : 0f));
                        // Quill: darker line down the shaft, tapering toward the tip.
                        float quillHw = 0.035f + 0.03f * (1f - v);
                        float qd = au - quillHw;
                        float quillCov = Coverage(qd, pxU) * (1f - SmoothStep(0.86f, 0.94f, v));
                        c = Color.Lerp(c, quill, quillCov);
                        alpha = Mathf.Max(alpha, quillCov * inside);
                        c.a = Mathf.Clamp01(alpha);
                        px[y * w + x] = c;
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        /// <summary>Streaky brown twig texture for the nest (legacy single-tint version).</summary>
        public static Texture2D TwigTexture(int size = 256)
        {
            return Cached("Twigs", () =>
            {
                var tex = NewTexture("Twigs", size, size, true, TextureWrapMode.Repeat);
                var px = new Color[size * size];
                Color dark = new Color(0.28f, 0.16f, 0.06f), mid = new Color(0.52f, 0.33f, 0.14f), light = new Color(0.72f, 0.52f, 0.28f);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = x / (float)size, v = y / (float)size;
                        float streak = ValueNoise(u * 3f, v * 40f, 7) * 0.6f + ValueNoise(u * 9f, v * 90f, 11) * 0.4f;
                        float blotch = ValueNoise(u * 6f, v * 6f, 3);
                        float t = Mathf.Clamp01(streak * 0.7f + blotch * 0.5f - 0.1f);
                        Color c = t < 0.5f ? Color.Lerp(dark, mid, t * 2f) : Color.Lerp(mid, light, (t - 0.5f) * 2f);
                        px[y * size + x] = c;
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        /// <summary>Cream egg shell with brown speckles.</summary>
        /// <summary>Golden bread crust: baked-brown value noise, darker toward the rim, a little flour on top.</summary>
        public static Texture2D CrustTexture(int size = 256)
        {
            return Cached("BreadCrust", () =>
            {
                var tex = NewTexture("BreadCrust", size, size, true, TextureWrapMode.Repeat);
                var px = new Color[size * size];
                Color light = new Color(0.90f, 0.70f, 0.42f), mid = new Color(0.74f, 0.48f, 0.22f), dark = new Color(0.50f, 0.28f, 0.10f), flour = new Color(0.96f, 0.92f, 0.82f);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = x / (float)size, v = y / (float)size;
                        float bake = ValueNoise(u * 6f, v * 6f, 33) * 0.6f + ValueNoise(u * 18f, v * 18f, 34) * 0.4f;
                        // v runs bottom (0) to top (1) of the roll: the top bakes darker, the base stays pale.
                        float rim = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((v - 0.35f) / 0.5f));
                        Color c = Color.Lerp(light, mid, bake);
                        c = Color.Lerp(c, dark, rim * 0.55f * (0.5f + 0.5f * bake));
                        float dust = ValueNoise(u * 40f, v * 40f, 35);
                        if (v > 0.8f && dust > 0.72f) c = Color.Lerp(c, flour, (dust - 0.72f) * 2.2f);
                        // Tiny pores.
                        float pore = ValueNoise(u * 90f, v * 90f, 36);
                        if (pore > 0.86f) c *= 0.85f;
                        px[y * size + x] = c;
                    }
                tex.SetPixels(px);
                tex.Apply(true, false);
                return tex;
            });
        }

        // ------------------------------------------------------------------------------------------------
        // Bread: a slice of toast (the one bread silhouette that reads as bread at any size)
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Outline of a slice of toast in a unit box (x in [-0.5, 0.5], y in [0, 1]), counter-clockwise from the
        /// bottom-left: flat bottom with rounded corners, straight sides, rounded top corners and two soft humps
        /// with a shallow dip between them. Star-shaped around (0, 0.5), which the texture relies on.
        /// </summary>
        static List<Vector2> ToastOutline()
        {
            var pts = new List<Vector2>(128);
            const float r = 0.11f;     // bottom corner radius
            const float rc = 0.10f;    // top corner radius
            const float side = 0.66f;  // height where the top corners end
            void Arc(Vector2 c, float radius, float a0Deg, float a1Deg, int n)
            {
                for (int i = 0; i <= n; i++)
                {
                    float a = Mathf.Lerp(a0Deg, a1Deg, i / (float)n) * Mathf.Deg2Rad;
                    pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
                }
            }
            float Top(float s) // height of the top edge at |x| = s, for s in [0, 0.5 - rc]
            {
                const float hump = 0.26f, end = 0.40f;
                float g = s >= hump ? 0.5f + 0.5f * Mathf.Cos((s - hump) / (end - hump) * Mathf.PI)
                                    : 0.7f + 0.3f * (0.5f - 0.5f * Mathf.Cos(s / hump * Mathf.PI));
                return side + 0.34f * g;
            }
            pts.Add(new Vector2(-0.5f + r, 0f));
            pts.Add(new Vector2(0.5f - r, 0f));
            Arc(new Vector2(0.5f - r, r), r, -90f, 0f, 6);
            pts.Add(new Vector2(0.5f, side - rc));
            Arc(new Vector2(0.5f - rc, side - rc), rc, 0f, 90f, 6);
            const int top = 44;
            for (int i = 1; i < top; i++)
            {
                float x = Mathf.Lerp(0.5f - rc, -(0.5f - rc), i / (float)top);
                pts.Add(new Vector2(x, Top(Mathf.Abs(x))));
            }
            Arc(new Vector2(-(0.5f - rc), side - rc), rc, 90f, 180f, 6);
            pts.Add(new Vector2(-0.5f, r));
            Arc(new Vector2(-0.5f + r, r), r, 180f, 270f, 6);
            // Drop consecutive duplicates.
            for (int i = pts.Count - 1; i > 0; i--) if ((pts[i] - pts[i - 1]).sqrMagnitude < 1e-8f) pts.RemoveAt(i);
            if ((pts[0] - pts[pts.Count - 1]).sqrMagnitude < 1e-8f) pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        /// <summary>
        /// A slice of toast: the outline extruded to a slab with a gently puffed face on both sides, pivot at the
        /// centre, face normal along +Z. Planar UVs over the unit box so ToastTexture's crust band lines up with the
        /// rim; the side quads sample the texture at the rim and come out crust-coloured.
        /// </summary>
        public static Mesh ToastSliceMesh(string name, float width, float height, float thickness)
        {
            var outline = ToastOutline();
            int n = outline.Count;
            var centre = new Vector2(0f, 0.5f);
            var verts = new List<Vector3>(n * 8);
            var uvs = new List<Vector2>(n * 8);
            var tris = new List<int>(n * 24);
            Vector3 P(Vector2 p, float z) => new Vector3(p.x * width, (p.y - 0.5f) * height, z);
            Vector2 Uv(Vector2 p) => new Vector2(p.x + 0.5f, p.y);

            int Face(float sign)
            {
                int c = verts.Count;
                verts.Add(P(centre, sign * thickness * 0.5f * 1.35f));
                uvs.Add(Uv(centre));
                int mid = verts.Count;
                for (int i = 0; i < n; i++)
                {
                    Vector2 p = centre + (outline[i] - centre) * 0.55f;
                    verts.Add(P(p, sign * thickness * 0.5f * 1.25f));
                    uvs.Add(Uv(p));
                }
                int rim = verts.Count;
                for (int i = 0; i < n; i++)
                {
                    verts.Add(P(outline[i], sign * thickness * 0.5f));
                    uvs.Add(Uv(outline[i]));
                }
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    if (sign > 0f) { tris.Add(c); tris.Add(mid + i); tris.Add(mid + j); }
                    else { tris.Add(c); tris.Add(mid + j); tris.Add(mid + i); }
                    int a = mid + i, b = mid + j, d = rim + i, e = rim + j;
                    if (sign > 0f) { tris.Add(a); tris.Add(d); tris.Add(e); tris.Add(a); tris.Add(e); tris.Add(b); }
                    else { tris.Add(a); tris.Add(e); tris.Add(d); tris.Add(a); tris.Add(b); tris.Add(e); }
                }
                return rim;
            }
            Face(1f);
            Face(-1f);
            // Sides: their own vertices so the rim stays a hard edge.
            int s0 = verts.Count;
            for (int i = 0; i < n; i++)
            {
                verts.Add(P(outline[i], thickness * 0.5f)); uvs.Add(Uv(outline[i]));
                verts.Add(P(outline[i], -thickness * 0.5f)); uvs.Add(Uv(outline[i]));
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int a = s0 + i * 2, b = s0 + i * 2 + 1, c = s0 + j * 2, d = s0 + j * 2 + 1;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>
        /// The face of a slice of lightly toasted bread over the outline's unit box: a golden crumb with pores, a
        /// dark-to-golden crust band that follows the outline, crust colour outside it (what the side quads sample).
        /// </summary>
        public static Texture2D ToastTexture(int size = 256)
        {
            return Cached("Toast", () =>
            {
                var outline = ToastOutline();
                var centre = new Vector2(0f, 0.5f);
                // Outline radius per angle around the centre (the shape is star-shaped), from finely sampled segments.
                const int bins = 720;
                var radius = new float[bins];
                for (int i = 0; i < outline.Count; i++)
                {
                    Vector2 a = outline[i], b = outline[(i + 1) % outline.Count];
                    for (int k = 0; k <= 8; k++)
                    {
                        Vector2 p = Vector2.Lerp(a, b, k / 8f) - centre;
                        int bin = Mathf.FloorToInt((Mathf.Atan2(p.y, p.x) + Mathf.PI) / (2f * Mathf.PI) * bins) % bins;
                        if (bin < 0) bin += bins;
                        radius[bin] = Mathf.Max(radius[bin], p.magnitude);
                    }
                }
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < bins; i++)
                        if (radius[i] <= 0f) radius[i] = Mathf.Max(radius[(i + bins - 1) % bins], radius[(i + 1) % bins]);

                var tex = NewTexture("Toast", size, size, true, TextureWrapMode.Clamp);
                var px = new Color[size * size];
                Color crumbPale = new Color(0.93f, 0.80f, 0.55f), crumbGold = new Color(0.80f, 0.56f, 0.28f), pore = new Color(0.64f, 0.44f, 0.22f);
                Color crustOuter = new Color(0.34f, 0.16f, 0.04f), crustInner = new Color(0.66f, 0.40f, 0.14f);
                const float crust = 0.10f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size, v = (y + 0.5f) / size;
                        Vector2 p = new Vector2(u - 0.5f, v) - centre;
                        float ang = Mathf.Atan2(p.y, p.x);
                        float rOut = radius[((int)((ang + Mathf.PI) / (2f * Mathf.PI) * bins)) % bins];
                        float d = rOut - p.magnitude; // > 0 inside, roughly the distance to the rim
                        float toast = ValueNoise(u * 5f, v * 5f, 41) * 0.6f + ValueNoise(u * 14f, v * 14f, 42) * 0.4f;
                        Color c = Color.Lerp(crumbPale, crumbGold, 0.3f + 0.55f * toast);
                        float pores = ValueNoise(u * 26f, v * 26f, 43);
                        float pores2 = ValueNoise(u * 60f, v * 60f, 44);
                        if (pores > 0.60f) c = Color.Lerp(c, pore, Mathf.Clamp01((pores - 0.60f) * 4f) * 0.8f);
                        if (pores2 > 0.78f) c = Color.Lerp(c, pore, Mathf.Clamp01((pores2 - 0.78f) * 5f) * 0.6f);
                        // Crust band: dark at the rim, golden toward the crumb, with a little bake noise.
                        float band = Mathf.Clamp01(d / crust);
                        float bake = ValueNoise(u * 30f, v * 30f, 45);
                        Color crustC = Color.Lerp(crustOuter, crustInner, Mathf.SmoothStep(0f, 1f, band) * (0.85f + 0.3f * bake));
                        float inCrust = 1f - Mathf.SmoothStep(0.5f, 1f, band);
                        c = Color.Lerp(c, crustC, inCrust);
                        if (d <= 0f) c = Color.Lerp(crustOuter, crustInner, 0.25f * bake);
                        px[y * size + x] = c;
                    }
                tex.SetPixels(px);
                tex.Apply(true, false);
                return tex;
            });
        }

        /// <summary>A dinner roll: flat-bottomed lathe dome with a lumpy rim, pivot at the base centre.</summary>
        public static Mesh BreadRollMesh(string name, float width, float height, int seed)
        {
            var rnd = new System.Random(seed);
            float bump = 0.06f + (float)rnd.NextDouble() * 0.05f;
            float r0 = width * 0.5f;
            float Radius(float v)
            {
                // Base sits on a small flat, the body bulges out, the top rounds off like a risen bun.
                float body = Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow((v - 0.42f) / 0.62f, 2f)));
                float flat = Mathf.SmoothStep(0.72f, 1f, Mathf.Clamp01(v / 0.12f));
                float lump = 1f + bump * Mathf.Sin(v * 9f + seed) * Mathf.Sin(v * 3.1f);
                return r0 * Mathf.Max(0.02f, body * flat * lump);
            }
            return LatheMesh(name, Radius, height, 28, 18, 0f, 1f, 0f, 360f, true, false);
        }

        /// <summary>Crumbs flying off a bread roll as the goose pecks: tiny warm specks, short-lived.</summary>
        public static ParticleSystem CreateCrumbPuff(Transform parent, Material mat)
        {
            var ps = NewParticleSystem("Crumbs", parent, mat);
            var main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.03f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.85f, 0.62f, 0.34f, 0.95f);
            main.gravityModifier = 0.9f;
            main.maxParticles = 40;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)5) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 40f;
            shape.radius = 0.02f;
            FadeOut(ps, 0.9f, 0.3f);
            return ps;
        }

        public static Texture2D SpeckleTexture(int size = 256)
        {
            return Cached("EggSpeckle", () =>
            {
                var tex = NewTexture("EggSpeckle", size, size, true, TextureWrapMode.Repeat);
                var px = new Color[size * size];
                Color shell = new Color(0.99f, 0.95f, 0.84f), warm = new Color(0.95f, 0.87f, 0.72f), speck = new Color(0.55f, 0.38f, 0.22f);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = x / (float)size, v = y / (float)size;
                        // The egg mesh pinches this texture into a point at each pole; the fan of triangles there
                        // samples the whole width of the top rows and smeared the speckles into a dark ring on the
                        // tip. Fade the speckles and the tone noise out toward both poles so there is nothing to smear.
                        float pole = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.90f, 0.76f, v)) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.16f, v));
                        float tone = Mathf.Lerp(0.35f, ValueNoise(u * 5f, v * 5f, 21), pole);
                        Color c = Color.Lerp(shell, warm, tone * 0.6f);
                        float sp = ValueNoise(u * 60f, v * 60f, 33);
                        float sp2 = ValueNoise(u * 140f, v * 140f, 34);
                        if (sp > 0.78f) c = Color.Lerp(c, speck, Mathf.Clamp01((sp - 0.78f) * 6f) * pole);
                        if (sp2 > 0.86f) c = Color.Lerp(c, speck, Mathf.Clamp01((sp2 - 0.86f) * 5f) * 0.7f * pole);
                        px[y * size + x] = c;
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        /// <summary>
        /// Twig bark height field in [0,1] for one strip. u in [0,1) tiles horizontally (streaks run along u),
        /// v in [0,1] runs across the strip. Shared by the colour strips and the normal map so bumps line up.
        /// </summary>
        static float TwigHeight(float u, float v, int strip)
        {
            int seed = 101 + strip * 17;
            float streak = TiledNoise(u * 4f, v * 28f, 4, seed) * 0.55f
                         + TiledNoise(u * 12f, v * 70f, 12, seed + 1) * 0.30f
                         + TiledNoise(u * 36f, v * 140f, 36, seed + 2) * 0.15f;
            float knot = TiledNoise(u * 10f, v * 9f, 10, seed + 3);
            float crackField = TiledNoise(u * 6f, v * 18f, 6, seed + 4);
            float crack = 1f - SmoothStep(0f, 0.035f, Mathf.Abs(crackField - 0.5f));
            float h = streak * 0.75f + knot * 0.35f - 0.05f - crack * 0.35f;
            return Mathf.Clamp01(h);
        }

        static readonly Color[] s_TwigTints =
        {
            new Color(0.30f, 0.23f, 0.17f),   // cool dark
            new Color(0.52f, 0.38f, 0.22f),   // mid warm
            new Color(0.66f, 0.55f, 0.38f),   // pale straw
            new Color(0.48f, 0.44f, 0.38f),   // greyed
        };

        /// <summary>
        /// 4 horizontal strips (each size/4 tall) of twig bark, each a different tint (cool dark, mid warm, light
        /// straw, greyed). Streaks run along U. Repeat on U, Clamp on V. Strands pick a strip via UV.y = (strip + 0.5) / 4.
        /// </summary>
        public static Texture2D TwigStripTexture(int size = 512)
        {
            return Cached("TwigStrip" + size, () =>
            {
                var tex = NewTexture("TwigStrip", size, size, true, TextureWrapMode.Repeat);
                tex.wrapModeU = TextureWrapMode.Repeat;
                tex.wrapModeV = TextureWrapMode.Clamp;
                var px = new Color[size * size];
                int stripH = Mathf.Max(1, size / 4);
                for (int y = 0; y < size; y++)
                {
                    int strip = Mathf.Min(3, y / stripH);
                    float vLocal = (y - strip * stripH + 0.5f) / stripH;
                    Color tint = s_TwigTints[strip];
                    Color dark = tint * 0.5f; dark.a = 1f;
                    Color light = new Color(Mathf.Min(1f, tint.r * 1.35f + 0.03f), Mathf.Min(1f, tint.g * 1.32f + 0.02f), Mathf.Min(1f, tint.b * 1.25f), 1f);
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size;
                        float h = TwigHeight(u, vLocal, strip);
                        Color c = h < 0.5f ? Color.Lerp(dark, tint, h * 2f) : Color.Lerp(tint, light, (h - 0.5f) * 2f);
                        c.a = 1f;
                        px[y * size + x] = c;
                    }
                }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        /// <summary>
        /// Tangent-space normal map (OpenGL/Unity convention, +Y up, blue-ish) derived from the twig height field
        /// via a Sobel filter. Linear data; wraps like TwigStripTexture.
        /// </summary>
        public static Texture2D TwigNormalTexture(int size = 512, float strength = 2f)
        {
            return Cached("TwigNormal" + size + "_" + strength, () =>
            {
                var tex = NewTexture("TwigNormal", size, size, true, TextureWrapMode.Repeat, linear: true);
                tex.wrapModeU = TextureWrapMode.Repeat;
                tex.wrapModeV = TextureWrapMode.Clamp;
                int stripH = Mathf.Max(1, size / 4);
                var hf = new float[size * size];
                for (int y = 0; y < size; y++)
                {
                    int strip = Mathf.Min(3, y / stripH);
                    float vLocal = (y - strip * stripH + 0.5f) / stripH;
                    for (int x = 0; x < size; x++)
                        hf[y * size + x] = TwigHeight((x + 0.5f) / size, vLocal, strip);
                }
                var px = new Color[size * size];
                float scale = strength * size / 64f;   // makes bump slope independent of resolution
                for (int y = 0; y < size; y++)
                {
                    int strip = Mathf.Min(3, y / stripH);
                    int y0 = strip * stripH, y1 = Mathf.Min(size - 1, y0 + stripH - 1);
                    int ym = Mathf.Max(y0, y - 1), yp = Mathf.Min(y1, y + 1);
                    for (int x = 0; x < size; x++)
                    {
                        int xm = (x - 1 + size) % size, xp = (x + 1) % size;
                        float dx = (hf[ym * size + xp] + 2f * hf[y * size + xp] + hf[yp * size + xp])
                                 - (hf[ym * size + xm] + 2f * hf[y * size + xm] + hf[yp * size + xm]);
                        float dy = (hf[yp * size + xm] + 2f * hf[yp * size + x] + hf[yp * size + xp])
                                 - (hf[ym * size + xm] + 2f * hf[ym * size + x] + hf[ym * size + xp]);
                        dx *= 0.125f * scale;
                        dy *= 0.125f * scale;
                        var n = new Vector3(-dx, -dy, 1f).normalized;
                        px[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                    }
                }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        // ------------------------------------------------------------------------------------------------
        // UI / AR helper textures
        // ------------------------------------------------------------------------------------------------

        /// <summary>Soft round white dots on a 4x4 cell grid (radius ~0.12 of a cell), transparent elsewhere. Repeat wrap.</summary>
        public static Texture2D DotGridTexture(int size = 256)
        {
            return Cached("DotGrid" + size, () =>
            {
                var tex = NewTexture("DotGrid", size, size, true, TextureWrapMode.Repeat);
                var px = new Color[size * size];
                float cell = size / 4f;
                float r = 0.12f * cell;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float cx = Mathf.Repeat(x + 0.5f, cell) - cell * 0.5f;
                        float cy = Mathf.Repeat(y + 0.5f, cell) - cell * 0.5f;
                        float d = Mathf.Sqrt(cx * cx + cy * cy);
                        float a = 1f - SmoothStep(r * 0.45f, r, d);          // solid core, soft falloff
                        a *= Coverage(d - r, 1f);                            // anti-aliased outer rim
                        px[y * size + x] = new Color(1f, 1f, 1f, a);
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        /// <summary>White ring with smoothstep-feathered inner/outer edges. Radii are fractions of the half-size. Clamp wrap.</summary>
        public static Texture2D SoftRingTexture(int size = 256, float innerRadius = 0.72f, float outerRadius = 0.92f, float feather = 0.06f)
        {
            string key = "SoftRing" + size + "_" + innerRadius + "_" + outerRadius + "_" + feather;
            return Cached(key, () =>
            {
                var tex = NewTexture("RingSoft", size, size, true, TextureWrapMode.Clamp);
                var px = new Color[size * size];
                float half = size * 0.5f;
                float f = Mathf.Max(feather, 1.5f / half) * 0.5f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f - half) / half, dy = (y + 0.5f - half) / half;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = SmoothStep(innerRadius - f, innerRadius + f, d) * (1f - SmoothStep(outerRadius - f, outerRadius + f, d));
                        px[y * size + x] = new Color(1f, 1f, 1f, a);
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        /// <summary>White goose silhouette (side view, facing right) on transparent, anti-aliased. UI icon.</summary>
        public static Texture2D GooseIconTexture(int size = 128)
        {
            return Cached("GooseIcon" + size, () =>
            {
                var tex = NewTexture("GooseIcon", size, size, false, TextureWrapMode.Clamp);
                var px = new Color[size * size];
                float pxN = 1f / size;
                Vector2 bodyC = new Vector2(0.42f, 0.40f), bodyR = new Vector2(0.31f, 0.175f);
                Vector2 tailA = new Vector2(0.15f, 0.36f), tailB = new Vector2(0.02f, 0.57f), tailC = new Vector2(0.23f, 0.49f);
                Vector2 n0 = new Vector2(0.60f, 0.46f), n1 = new Vector2(0.69f, 0.64f), n2 = new Vector2(0.71f, 0.80f);
                Vector2 headC = new Vector2(0.71f, 0.84f);
                Vector2 bkA = new Vector2(0.78f, 0.875f), bkB = new Vector2(0.78f, 0.815f), bkC = new Vector2(0.93f, 0.835f);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        var p = new Vector2((x + 0.5f) * pxN, (y + 0.5f) * pxN);
                        float sd = SdEllipse(p, bodyC, bodyR, -6f);
                        sd = Mathf.Min(sd, SdTriangle(p, tailA, tailB, tailC));
                        sd = Mathf.Min(sd, SdSegment(p, n0, n1) - 0.068f);
                        sd = Mathf.Min(sd, SdSegment(p, n1, n2) - 0.060f);
                        sd = Mathf.Min(sd, SdCircle(p, headC, 0.088f));
                        sd = Mathf.Min(sd, SdTriangle(p, bkA, bkB, bkC));
                        px[y * size + x] = new Color(1f, 1f, 1f, Coverage(sd, pxN));
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        static Sprite s_GooseSprite;

        /// <summary>Cached sprite of <see cref="GooseIconTexture"/> (128 px, centre pivot, 100 px/unit).</summary>
        public static Sprite GooseIconSprite()
        {
            if (s_GooseSprite != null && s_GooseSprite.texture != null) return s_GooseSprite;
            var tex = GooseIconTexture(128);
            s_GooseSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            s_GooseSprite.name = "GooseIcon";
            return s_GooseSprite;
        }

        /// <summary>Normalised egg radius (0..1, max 1) at t in [-1,1]; t = +1 is the pointy top. Widest a bit below the middle.</summary>
        static float EggProfile(float t)
        {
            const float k = 0.18f;
            const float norm = 1.0156f;   // max of sqrt(1-t^2)(1-kt) for k = 0.18 (at t ~ -0.17)
            t = Mathf.Clamp(t, -1f, 1f);
            return Mathf.Sqrt(Mathf.Max(0f, 1f - t * t)) * (1f - k * t) / norm;
        }

        /// <summary>
        /// App icon: yolk background, a big tilted cream egg silhouette (~78% height), a dark zig-zag crack across its
        /// upper third and a small black eye dot near the top right. Opaque, no text.
        /// </summary>
        public static Texture2D AppIconTexture(int size = 1024)
        {
            return Cached("AppIcon" + size, () =>
            {
                var tex = NewTexture("AppIcon", size, size, false, TextureWrapMode.Clamp);
                var px = new Color[size * size];
                float pxN = 1f / size;
                Color yolk = new Color(1f, 0.72f, 0.15f, 1f);
                Color cream = new Color(1f, 0.965f, 0.87f, 1f);
                Color creamShade = new Color(0.96f, 0.91f, 0.80f, 1f);
                Color crackCol = new Color(0.30f, 0.18f, 0.07f, 1f);
                Color eyeCol = new Color(0.06f, 0.05f, 0.04f, 1f);

                float tilt = 8f * Mathf.Deg2Rad;
                Vector2 up = new Vector2(Mathf.Sin(tilt), Mathf.Cos(tilt));
                Vector2 right = new Vector2(Mathf.Cos(tilt), -Mathf.Sin(tilt));
                Vector2 centre = new Vector2(0.5f, 0.5f);
                float a = 0.39f;                 // half height (78% of the icon)
                float R = 0.78f / 1.36f * 0.5f;  // half width

                float EggF(float ex, float ey)
                {
                    float t = ey / a;
                    float at = Mathf.Abs(t);
                    if (at < 1f) return Mathf.Abs(ex) - R * EggProfile(t);
                    return Mathf.Abs(ex) + (at - 1f) * a;
                }

                // Crack polyline in egg-local coordinates, across the upper third, extending past the shell edge.
                const int crackSegs = 7;
                var crack = new Vector2[crackSegs + 1];
                for (int i = 0; i <= crackSegs; i++)
                {
                    float f = i / (float)crackSegs;
                    float cy = a / 3f + (i % 2 == 0 ? -1f : 1f) * (0.020f + 0.016f * Hash(i, 3, 77)) + 0.012f * (Hash(i, 5, 78) - 0.5f);
                    float hw = R * EggProfile(cy / a) + 0.02f;
                    float cx = Mathf.Lerp(-hw, hw, f) + (i == 0 || i == crackSegs ? 0f : 0.02f * (Hash(i, 9, 79) - 0.5f));
                    crack[i] = new Vector2(cx, cy);
                }
                float crackHalf = 0.011f;
                Vector2 eye = new Vector2(0.55f * R * EggProfile(0.58f), 0.58f * a);
                float eyeR = 0.02f;
                float h = 0.5f * pxN;

                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        var p = new Vector2((x + 0.5f) * pxN, (y + 0.5f) * pxN);
                        Vector2 d = p - centre;
                        float ex = Vector2.Dot(d, right), ey = Vector2.Dot(d, up);

                        // Background with a faint vignette.
                        float vig = 1f - 0.08f * SmoothStep(0.55f, 1.05f, d.magnitude / 0.7071f);
                        Color c = new Color(yolk.r * vig, yolk.g * vig, yolk.b * vig, 1f);

                        // Egg coverage from a gradient-normalised implicit function.
                        float f0 = EggF(ex, ey);
                        float gx = (EggF(ex + h, ey) - EggF(ex - h, ey)) / (2f * h);
                        float gy = (EggF(ex, ey + h) - EggF(ex, ey - h)) / (2f * h);
                        float g = Mathf.Max(1f, Mathf.Sqrt(gx * gx + gy * gy));
                        float eggCov = Coverage(f0 / g, pxN);
                        if (eggCov > 0f)
                        {
                            float shade = SmoothStep(-1f, 0.6f, ey / a);
                            Color egg = Color.Lerp(creamShade, cream, shade);
                            c = Color.Lerp(c, egg, eggCov);

                            float cd = float.MaxValue;
                            var ep = new Vector2(ex, ey);
                            for (int i = 0; i < crackSegs; i++) cd = Mathf.Min(cd, SdSegment(ep, crack[i], crack[i + 1]));
                            float crackCov = Coverage(cd - crackHalf, pxN) * eggCov;
                            c = Color.Lerp(c, crackCol, crackCov);

                            float eyeCov = Coverage(SdCircle(ep, eye, eyeR), pxN) * eggCov;
                            c = Color.Lerp(c, eyeCol, eyeCov);
                        }
                        c.a = 1f;
                        px[y * size + x] = c;
                    }
                tex.SetPixels(px);
                tex.Apply();
                return tex;
            });
        }

        // ------------------------------------------------------------------------------------------------
        // Meshes: surfaces of revolution
        // ------------------------------------------------------------------------------------------------

        static float RingRadius(Func<float, float, Vector3> surface, float t)
        {
            float r = 0f;
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = surface(k * 0.25f, t);
                r = Mathf.Max(r, Mathf.Sqrt(p.x * p.x + p.z * p.z));
            }
            return r;
        }

        /// <summary>
        /// Core surface-of-revolution builder. surface(u, t): u in [0,1] across the angular span (sampled slightly
        /// outside it when wrapU), t in [0,1] along the profile. Normals are central differences of surface, so any
        /// displacement folded into it shades correctly. Ends whose ring collapses to a point become one pole vertex
        /// (normal +/-Y) with a degenerate-free fan; otherwise optional flat caps are added. Front faces follow
        /// cross(dP/dt, dP/du): angle increasing with u and height increasing with t gives outward normals.
        /// </summary>
        static Mesh BuildRevolved(string name, Func<float, float, Vector3> surface, Func<float, float, Vector2> uvAt,
            int segments, int rings, bool wrapU, bool capStart, bool capEnd)
        {
            segments = Mathf.Max(3, segments);
            rings = Mathf.Max(1, rings);
            const float poleEps = 1e-5f;
            bool poleStart = RingRadius(surface, 0f) < poleEps;
            bool poleEnd = RingRadius(surface, 1f) < poleEps;
            if ((poleStart || poleEnd) && rings < 2) rings = 2;
            int jFirst = poleStart ? 1 : 0, jLast = poleEnd ? rings - 1 : rings;
            int rows = jLast - jFirst + 1;
            int cols = segments + 1;
            bool doCapStart = capStart && !poleStart, doCapEnd = capEnd && !poleEnd;

            int vCount = rows * cols + (poleStart ? 1 : 0) + (poleEnd ? 1 : 0) + (doCapStart ? cols + 1 : 0) + (doCapEnd ? cols + 1 : 0);
            var verts = new Vector3[vCount];
            var norms = new Vector3[vCount];
            var uvs = new Vector2[vCount];
            var tris = new List<int>(rows * segments * 6 + segments * 12);

            float du = 0.25f / segments, dt = 0.25f / rings;
            Vector3 NormalAt(float u, float t)
            {
                float t0 = Mathf.Max(0f, t - dt), t1 = Mathf.Min(1f, t + dt);
                float u0 = u - du, u1 = u + du;
                if (!wrapU) { u0 = Mathf.Max(0f, u0); u1 = Mathf.Min(1f, u1); }
                Vector3 pt = surface(u, t1) - surface(u, t0);
                Vector3 pu = surface(u1, t) - surface(u0, t);
                Vector3 n = Vector3.Cross(pt, pu);
                if (n.sqrMagnitude < 1e-20f || float.IsNaN(n.x) || float.IsNaN(n.y) || float.IsNaN(n.z))
                {
                    Vector3 p = surface(u, t);
                    Vector3 radial = new Vector3(p.x, 0f, p.z);
                    n = radial.sqrMagnitude > 1e-12f ? radial : (t < 0.5f ? Vector3.down : Vector3.up);
                }
                return n.normalized;
            }
            int Idx(int i, int j) => (j - jFirst) * cols + i;

            int v = 0;
            for (int j = jFirst; j <= jLast; j++)
            {
                float t = j / (float)rings;
                for (int i = 0; i <= segments; i++)
                {
                    float u = i / (float)segments;
                    verts[v] = surface(u, t);
                    norms[v] = NormalAt(u, t);
                    uvs[v] = uvAt(u, t);
                    v++;
                }
            }
            for (int j = jFirst; j < jLast; j++)
                for (int i = 0; i < segments; i++)
                {
                    int a = Idx(i, j), b = Idx(i + 1, j), c = Idx(i, j + 1), d = Idx(i + 1, j + 1);
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }

            Vector3 PoleNormal(int row, bool start)
            {
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < segments; i++) sum += norms[Idx(i, row)];
                if (Mathf.Abs(sum.y) < 1e-6f) return start ? Vector3.down : Vector3.up;
                return sum.y > 0f ? Vector3.up : Vector3.down;
            }
            if (poleStart)
            {
                int p = v++;
                verts[p] = surface(0.5f, 0f);
                norms[p] = PoleNormal(jFirst, true);
                uvs[p] = uvAt(0.5f, 0f);
                for (int i = 0; i < segments; i++) { tris.Add(p); tris.Add(Idx(i, jFirst)); tris.Add(Idx(i + 1, jFirst)); }
            }
            if (poleEnd)
            {
                int p = v++;
                verts[p] = surface(0.5f, 1f);
                norms[p] = PoleNormal(jLast, false);
                uvs[p] = uvAt(0.5f, 1f);
                for (int i = 0; i < segments; i++) { tris.Add(p); tris.Add(Idx(i + 1, jLast)); tris.Add(Idx(i, jLast)); }
            }

            void AddCap(float t, bool start)
            {
                Vector3 inward = surface(0f, start ? 0.05f : 0.95f) - surface(0f, t);
                Vector3 capN = inward.y > 1e-6f ? Vector3.down : (inward.y < -1e-6f ? Vector3.up : (start ? Vector3.down : Vector3.up));
                float avgY = 0f, maxR = 1e-6f;
                var ring = new Vector3[cols];
                for (int i = 0; i <= segments; i++)
                {
                    ring[i] = surface(i / (float)segments, t);
                    avgY += ring[i].y;
                    maxR = Mathf.Max(maxR, Mathf.Sqrt(ring[i].x * ring[i].x + ring[i].z * ring[i].z));
                }
                avgY /= cols;
                int c = v++;
                verts[c] = new Vector3(0f, avgY, 0f);
                norms[c] = capN;
                uvs[c] = new Vector2(0.5f, 0.5f);
                int first = v;
                for (int i = 0; i <= segments; i++)
                {
                    verts[v] = ring[i];
                    norms[v] = capN;
                    uvs[v] = new Vector2(0.5f + 0.5f * ring[i].x / maxR, 0.5f + 0.5f * ring[i].z / maxR);
                    v++;
                }
                float wy = Vector3.Cross(ring[0] - verts[c], ring[1] - verts[c]).y;
                bool forward = (wy >= 0f) == (capN.y >= 0f);
                for (int i = 0; i < segments; i++)
                {
                    tris.Add(c);
                    if (forward) { tris.Add(first + i); tris.Add(first + i + 1); }
                    else { tris.Add(first + i + 1); tris.Add(first + i); }
                }
            }
            if (doCapStart) AddCap(0f, true);
            if (doCapEnd) AddCap(1f, false);

            var mesh = new Mesh { name = name };
            if (vCount > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>
        /// Revolves a radius profile around +Y. radiusAt01(v) gives the radius at normalised height v in [vStart,vEnd];
        /// height is the total object height (y = v * height). Smooth analytic-style normals, UVs (u around, v along
        /// the generated span), optional flat caps. Angles increase counter-clockwise seen from above.
        /// </summary>
        public static Mesh LatheMesh(string name, Func<float, float> radiusAt01, float height, int segments = 32, int rings = 24,
            float vStart = 0f, float vEnd = 1f, float angleFromDeg = 0f, float angleToDeg = 360f, bool capBottom = false, bool capTop = false)
        {
            if (angleToDeg < angleFromDeg) { float tmp = angleToDeg; angleToDeg = angleFromDeg; angleFromDeg = tmp; }
            float a0 = angleFromDeg * Mathf.Deg2Rad, span = (angleToDeg - angleFromDeg) * Mathf.Deg2Rad;
            bool wrap = Mathf.Abs(angleToDeg - angleFromDeg - 360f) < 1e-3f;
            Vector3 Surf(float u, float t)
            {
                float v = Mathf.Lerp(vStart, vEnd, t);
                float r = radiusAt01(v);
                if (float.IsNaN(r) || r < 0f) r = 0f;
                float ang = a0 + span * u;
                return new Vector3(Mathf.Cos(ang) * r, v * height, Mathf.Sin(ang) * r);
            }
            Vector2 Uv(float u, float t) => new Vector2(u, t);
            return BuildRevolved(name, Surf, Uv, segments, rings, wrap, capBottom, capTop);
        }

        /// <summary>
        /// An egg, pointy end up, pivot at the bottom centre. width = max diameter, height = total height. Rings are
        /// cosine-spaced so the poles are round; both poles are single vertices. UV: u around, v = normalised height.
        /// </summary>
        public static Mesh EggMesh(string name, float width, float height, int segments = 32, int rings = 28)
        {
            float R = 0.5f * Mathf.Max(1e-4f, width);
            float EggV(float t) => 0.5f - 0.5f * Mathf.Cos(Mathf.Clamp01(t) * Mathf.PI);
            Vector3 Surf(float u, float t)
            {
                float v = EggV(t);
                float r = R * EggProfile(v * 2f - 1f);
                float ang = u * Mathf.PI * 2f;
                return new Vector3(Mathf.Cos(ang) * r, v * height, Mathf.Sin(ang) * r);
            }
            Vector2 Uv(float u, float t) => new Vector2(u, EggV(t));
            return BuildRevolved(name, Surf, Uv, segments, rings, true, false, false);
        }

        /// <summary>Jagged edge profile in [-1,1]: smooth noise plus a zig-zag so shell edges read as broken, not wavy.</summary>
        static float Jag(float x, int seed, bool periodic = false)
        {
            float n = periodic
                ? RingNoise(x, 0f, 2.5f, seed) * 0.6f + RingNoise(x, 1f, 5.5f, seed + 1) * 0.4f
                : ValueNoise(x * 5f + 0.37f, seed * 0.113f + 3.1f, seed) * 0.6f + ValueNoise(x * 13f + 2.1f, seed * 0.29f + 7.7f, seed + 1) * 0.4f;
            float tri = Mathf.Abs(Mathf.Repeat(x * 8f + Hash(seed, 1, 2), 1f) * 2f - 1f);
            return Mathf.Clamp((n - 0.5f) * 1.4f + (tri - 0.5f) * 0.6f, -1f, 1f);
        }

        /// <summary>
        /// A piece of egg shell: the egg surface (same width/height/pivot as <see cref="EggMesh"/>, so pieces line up
        /// when given the egg's transform) between vFrom..vTo and angleFrom..angleTo, with noisy jagged boundaries
        /// displaced by up to jaggedAmp (normalised height units; angular edges use the same world amplitude).
        /// Single thin surface; render double-sided. Edges at a pole (v = 0 or 1) close with a pole vertex.
        /// UV matches the whole egg (u = angle/360, v = height fraction).
        /// </summary>
        public static Mesh EggShellPieceMesh(string name, float width, float height, float vFrom, float vTo, float angleFromDeg, float angleToDeg,
            float jaggedAmp, int seed, int segments = 20, int rings = 10)
        {
            if (angleToDeg < angleFromDeg) { float tmp = angleToDeg; angleToDeg = angleFromDeg; angleFromDeg = tmp; }
            if (vTo < vFrom) { float tmp = vTo; vTo = vFrom; vFrom = tmp; }
            vFrom = Mathf.Clamp01(vFrom); vTo = Mathf.Clamp01(vTo);
            if (vTo - vFrom < 0.02f) vTo = Mathf.Min(1f, vFrom + 0.02f);
            float R = 0.5f * Mathf.Max(1e-4f, width);
            float H = Mathf.Max(1e-4f, height);
            float amp = Mathf.Max(0f, jaggedAmp);
            bool poleBottom = vFrom <= 1e-4f, poleTop = vTo >= 1f - 1e-4f;
            float spanDeg = angleToDeg - angleFromDeg;
            bool wrap = spanDeg >= 360f - 1e-3f;
            float a0 = angleFromDeg * Mathf.Deg2Rad, span = spanDeg * Mathf.Deg2Rad;
            float maxAngJag = 0.35f * span;

            float RadiusAtV(float v) => R * EggProfile(v * 2f - 1f);
            float Warp(float t)
            {
                t = Mathf.Clamp01(t);
                if (poleTop && poleBottom) return 0.5f - 0.5f * Mathf.Cos(t * Mathf.PI);
                if (poleTop) return Mathf.Sin(t * Mathf.PI * 0.5f);
                if (poleBottom) return 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
                return t;
            }
            void Params(float u, float t, out float ang, out float v)
            {
                float lo = poleBottom ? 0f : vFrom + amp * Jag(u, seed, wrap);
                float hi = poleTop ? 1f : vTo + amp * Jag(u + 0.5f, seed + 11, wrap);
                lo = Mathf.Clamp01(lo); hi = Mathf.Clamp01(hi);
                if (poleTop) lo = Mathf.Min(lo, 0.97f);                       // keep the pole ring collapsed
                else if (poleBottom) hi = Mathf.Max(hi, 0.03f);
                else if (hi - lo < 0.02f) { float mid = 0.5f * (lo + hi); lo = Mathf.Clamp01(mid - 0.01f); hi = Mathf.Clamp01(mid + 0.01f); }
                v = Mathf.Lerp(lo, hi, Warp(t));
                float r = RadiusAtV(v);
                float angAmp = wrap ? 0f : Mathf.Min(maxAngJag, amp * H / Mathf.Max(r, 0.08f * R));
                float aLo = a0 + angAmp * Jag(t, seed + 23);
                float aHi = a0 + span + angAmp * Jag(t + 0.5f, seed + 37);
                ang = Mathf.Lerp(aLo, aHi, u);
            }
            Vector3 Surf(float u, float t)
            {
                Params(u, t, out float ang, out float v);
                float r = RadiusAtV(v);
                return new Vector3(Mathf.Cos(ang) * r, v * H, Mathf.Sin(ang) * r);
            }
            Vector2 Uv(float u, float t)
            {
                Params(u, t, out float ang, out float v);
                return new Vector2(ang / (Mathf.PI * 2f), v);
            }
            return BuildRevolved(name, Surf, Uv, segments, rings, wrap, false, false);
        }

        /// <summary>Arc-length parametrised Catmull-Rom spline through 2D points.</summary>
        sealed class ArcPath
        {
            readonly Vector2[] m_Pts;
            readonly float[] m_Len;
            public float Length => m_Len[m_Len.Length - 1];

            public ArcPath(Vector2[] control, int samples = 512)
            {
                m_Pts = new Vector2[samples + 1];
                m_Len = new float[samples + 1];
                int segs = control.Length - 1;
                for (int k = 0; k <= samples; k++)
                {
                    float s = k / (float)samples * segs;
                    int i = Mathf.Min(segs - 1, Mathf.FloorToInt(s));
                    float f = s - i;
                    Vector2 p0 = control[Mathf.Max(0, i - 1)], p1 = control[i], p2 = control[i + 1], p3 = control[Mathf.Min(control.Length - 1, i + 2)];
                    m_Pts[k] = 0.5f * ((2f * p1) + (-p0 + p2) * f + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (f * f) + (-p0 + 3f * p1 - 3f * p2 + p3) * (f * f * f));
                    m_Len[k] = k == 0 ? 0f : m_Len[k - 1] + Vector2.Distance(m_Pts[k - 1], m_Pts[k]);
                }
            }

            public Vector2 At(float t)
            {
                float target = Mathf.Clamp01(t) * Length;
                int lo = 0, hi = m_Len.Length - 1;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    if (m_Len[mid] <= target) lo = mid; else hi = mid;
                }
                float seg = m_Len[hi] - m_Len[lo];
                float f = seg > 1e-9f ? (target - m_Len[lo]) / seg : 0f;
                return Vector2.LerpUnclamped(m_Pts[lo], m_Pts[hi], f);
            }
        }

        // Nest bowl profile (r/R, y/H): flat bottom, rounded outer wall curling out at the lip, over the lip and down
        // the inside to a shallow floor. Both ends are on the axis so the mesh is closed with pole vertices.
        static readonly Vector2[] s_BowlProfile =
        {
            new Vector2(0.00f, 0.00f), new Vector2(0.55f, 0.00f), new Vector2(0.80f, 0.02f), new Vector2(0.90f, 0.10f),
            new Vector2(0.94f, 0.35f), new Vector2(0.97f, 0.62f), new Vector2(1.00f, 0.84f), new Vector2(0.98f, 0.95f),
            new Vector2(0.92f, 1.00f), new Vector2(0.85f, 0.98f), new Vector2(0.80f, 0.90f), new Vector2(0.70f, 0.72f),
            new Vector2(0.58f, 0.55f), new Vector2(0.42f, 0.42f), new Vector2(0.25f, 0.35f), new Vector2(0.00f, 0.33f),
        };

        /// <summary>
        /// Lumpy mud nest bowl: radius = outer radius, height = lip height. Open profile outer bottom -> outer wall ->
        /// lip -> inner wall -> inner floor (a 2D path, so the radius decreases again after the lip), displaced by
        /// low-frequency noise (~6% of radius; the flat bottom stays flat). Pivot at the bottom centre.
        /// </summary>
        public static Mesh NestBowlMesh(string name, float radius, float height, int seed, int segments = 40, int rings = 22)
        {
            float R = Mathf.Max(1e-4f, radius), H = Mathf.Max(1e-4f, height);
            var control = new Vector2[s_BowlProfile.Length];
            for (int i = 0; i < control.Length; i++) control[i] = new Vector2(s_BowlProfile[i].x * R, s_BowlProfile[i].y * H);
            var path = new ArcPath(control);
            float uTiles = Mathf.Max(1f, Mathf.Round(2f * Mathf.PI * R / Mathf.Max(1e-4f, path.Length)));
            const float eps = 0.002f;

            Vector3 Surf(float u, float t)
            {
                Vector2 p = path.At(t);
                Vector2 pa = path.At(Mathf.Max(0f, t - eps)), pb = path.At(Mathf.Min(1f, t + eps));
                Vector2 tan = pb - pa;
                Vector2 n2 = new Vector2(tan.y, -tan.x);           // outward normal in the (r, y) profile plane
                if (n2.sqrMagnitude < 1e-14f) n2 = new Vector2(1f, 0f); else n2.Normalize();
                float ang = u * Mathf.PI * 2f;
                float c = Mathf.Cos(ang), s = Mathf.Sin(ang);
                float fade = Mathf.Sqrt(Mathf.Max(0f, Mathf.Sin(t * Mathf.PI)));      // no displacement at the poles
                float flatMask = SmoothStep(0.02f * H, 0.15f * H, p.y);                // keep the flat bottom flat
                float bump = ((RingNoise(u, t * 2f, 1.1f, seed) - 0.5f) * 2f * 0.06f + (RingNoise(u, t * 5f, 3f, seed + 1) - 0.5f) * 2f * 0.02f) * R;
                bump *= fade * flatMask;
                float r = Mathf.Max(0f, p.x + n2.x * bump);
                float y = p.y + n2.y * bump;
                return new Vector3(c * r, y, s * r);
            }
            Vector2 Uv(float u, float t) => new Vector2(u * uTiles, t);
            return BuildRevolved(name, Surf, Uv, segments, rings, true, false, false);
        }

        // ------------------------------------------------------------------------------------------------
        // Meshes: nest strands and flat discs
        // ------------------------------------------------------------------------------------------------

        /// <summary>A thin tube following an arc around the Y axis, with sag and wobble: one twig strand of a nest.</summary>
        public static Mesh StrandMesh(string name, float radius, float startAngle, float sweepAngle, float baseY, float sag, float thickness, int seed, int segments = 18, int sides = 5)
        {
            var mesh = new Mesh { name = name };
            var verts = new Vector3[(segments + 1) * sides];
            var uvs = new Vector2[verts.Length];
            var normals = new Vector3[verts.Length];
            for (int i = 0; i <= segments; i++)
            {
                float u = i / (float)segments;
                float a = (startAngle + sweepAngle * u) * Mathf.Deg2Rad;
                float wob = (ValueNoise(u * 4f + seed, seed * 0.37f, seed) - 0.5f) * 0.06f;
                float r = radius + wob + Mathf.Sin(u * Mathf.PI) * (ValueNoise(seed, u * 3f, seed + 9) - 0.5f) * 0.05f;
                float y = baseY + Mathf.Sin(u * Mathf.PI) * sag + (ValueNoise(u * 6f, seed * 0.11f, seed + 3) - 0.5f) * 0.02f;
                Vector3 center = new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                // Tangent along the arc
                float a2 = (startAngle + sweepAngle * Mathf.Min(1f, u + 0.02f)) * Mathf.Deg2Rad;
                Vector3 next = new Vector3(Mathf.Cos(a2) * r, y, Mathf.Sin(a2) * r);
                Vector3 tangent = (next - center);
                if (tangent.sqrMagnitude < 1e-8f) tangent = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));
                tangent.Normalize();
                Vector3 n1 = Vector3.Cross(tangent, Vector3.up).normalized;
                Vector3 n2 = Vector3.Cross(n1, tangent).normalized;
                float taper = thickness * (0.6f + 0.4f * Mathf.Sin(u * Mathf.PI));
                for (int s = 0; s < sides; s++)
                {
                    float b = s / (float)sides * Mathf.PI * 2f;
                    Vector3 n = n1 * Mathf.Cos(b) + n2 * Mathf.Sin(b);
                    int idx = i * sides + s;
                    verts[idx] = center + n * taper;
                    normals[idx] = n;
                    uvs[idx] = new Vector2(u * 3f, s / (float)sides);
                }
            }
            var tris = new int[segments * sides * 6];
            int t = 0;
            for (int i = 0; i < segments; i++)
                for (int s = 0; s < sides; s++)
                {
                    int a = i * sides + s, b = i * sides + (s + 1) % sides;
                    int c = a + sides, d = b + sides;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>
        /// All nest strands as one mesh (no submeshes): woven arcs (radius 0.26->0.15 + jitter, baseY 0.03->0.14,
        /// sweep 60..200 deg, sag, thickness 0.007..0.013) plus 12 loose ends poking out of the rim. Each strand's UV.y
        /// is the centre of one of 4 horizontal strips ((strip + 0.5) / 4) so <see cref="TwigStripTexture"/> gives
        /// per-strand tint; u runs along the strand. Deterministic from seed.
        /// </summary>
        public static Mesh NestStrandsMesh(string name, int strands, int seed, out Bounds bounds)
        {
            var rng = new System.Random(seed);
            float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            var combines = new List<CombineInstance>();
            var temp = new List<Mesh>();
            int total = 0;
            void Add(Mesh m, Matrix4x4 mtx, int strip)
            {
                var uv = m.uv;
                float vy = (strip + 0.5f) / 4f;
                for (int k = 0; k < uv.Length; k++) uv[k].y = vy;
                m.uv = uv;
                combines.Add(new CombineInstance { mesh = m, transform = mtx, subMeshIndex = 0 });
                temp.Add(m);
                total += m.vertexCount;
            }

            strands = Mathf.Max(1, strands);
            for (int i = 0; i < strands; i++)
            {
                float layer = strands > 1 ? i / (float)(strands - 1) : 0.5f;       // 0 = bottom outer, 1 = top inner
                float radius = Mathf.Lerp(0.26f, 0.15f, layer) + Range(-0.03f, 0.03f);
                float baseY = Mathf.Lerp(0.03f, 0.14f, layer) + Range(-0.012f, 0.012f);
                float sweep = Range(60f, 200f);
                float start = Range(0f, 360f);
                float sag = Range(-0.015f, 0.02f);
                float thick = Range(0.007f, 0.013f);
                int strip = rng.Next(0, 4);
                Add(StrandMesh(name + "_S" + i, radius, start, sweep, baseY, sag, thick, seed * 7 + i * 7 + 1), Matrix4x4.identity, strip);
            }
            for (int i = 0; i < 12; i++)
            {
                float a = Range(0f, 360f);
                float radius = Range(0.27f, 0.34f);
                float sweep = Range(25f, 50f);
                float baseY = Range(0.06f, 0.15f);
                var m = StrandMesh(name + "_L" + i, radius, a, sweep, baseY, Range(0.02f, 0.06f), 0.008f, seed * 11 + 500 + i);
                // Tilt the loose end about the rim tangent at its midpoint so it pokes out at an angle.
                float mid = (a + sweep * 0.5f) * Mathf.Deg2Rad;
                Vector3 pivot = new Vector3(Mathf.Cos(mid) * radius, baseY, Mathf.Sin(mid) * radius);
                Vector3 axis = new Vector3(-Mathf.Sin(mid), 0f, Mathf.Cos(mid));
                Quaternion tilt = Quaternion.AngleAxis(Range(-25f, 25f), axis);
                Matrix4x4 mtx = Matrix4x4.TRS(pivot, tilt, Vector3.one) * Matrix4x4.Translate(-pivot);
                Add(m, mtx, rng.Next(0, 4));
            }

            var mesh = new Mesh { name = name };
            if (total > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.CombineMeshes(combines.ToArray(), true, true, false);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            bounds = mesh.bounds;
            foreach (var m in temp) UnityEngine.Object.DestroyImmediate(m);
            return mesh;
        }

        /// <summary>
        /// Flat organic disc in the XZ plane (y = 0, normal +Y), radius jittered by periodic noise (noise = amplitude
        /// fraction, e.g. 0.25), pivot at the centre, planar UVs. Used for the egg-white puddle.
        /// </summary>
        public static Mesh DiscMesh(string name, float radius, float noise, int seed, int segments = 48)
        {
            segments = Mathf.Max(8, segments);
            noise = Mathf.Clamp(noise, 0f, 0.9f);
            float R = Mathf.Max(1e-4f, radius);
            float uvScale = 0.5f / (R * (1f + noise));
            int n = segments;
            var verts = new Vector3[1 + 2 * n];
            var norms = new Vector3[verts.Length];
            var uvs = new Vector2[verts.Length];
            verts[0] = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)n;
                float a = u * Mathf.PI * 2f;
                float jit = (RingNoise(u, 0f, 1.3f, seed) - 0.5f) * 2f * 0.75f + (RingNoise(u, 0f, 3.2f, seed + 1) - 0.5f) * 2f * 0.25f;
                float r = R * (1f + noise * jit);
                float jitIn = (RingNoise(u, 1f, 1.6f, seed + 2) - 0.5f) * 2f;
                float rIn = r * (0.5f + 0.08f * jitIn);
                verts[1 + i] = new Vector3(Mathf.Cos(a) * rIn, 0f, Mathf.Sin(a) * rIn);
                verts[1 + n + i] = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            }
            for (int i = 0; i < verts.Length; i++)
            {
                norms[i] = Vector3.up;
                uvs[i] = new Vector2(0.5f + verts[i].x * uvScale, 0.5f + verts[i].z * uvScale);
            }
            var tris = new int[n * 3 + n * 6];
            int t = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int ai = 1 + i, aj = 1 + j, bi = 1 + n + i, bj = 1 + n + j;
                // (C, R[i+1], R[i]) and matching outer quad => +Y normals.
                tris[t++] = 0; tris[t++] = aj; tris[t++] = ai;
                tris[t++] = ai; tris[t++] = aj; tris[t++] = bi;
                tris[t++] = aj; tris[t++] = bj; tris[t++] = bi;
            }
            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // ------------------------------------------------------------------------------------------------
        // Particles (all Billboard, unlit transparent, no shadows, world simulation space, not playOnAwake,
        // pooled by the caller)
        // ------------------------------------------------------------------------------------------------

        static Material s_FeatherMaterial;
        static Material s_SplashMaterial;

        static Material FeatherMaterial()
        {
            if (s_FeatherMaterial == null) s_FeatherMaterial = UnlitTransparent("Feather_Runtime", Color.white, FeatherTexture());
            return s_FeatherMaterial;
        }

        static ParticleSystem NewParticleSystem(string name, Transform parent, Material mat)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (mat != null) r.sharedMaterial = mat;
            return ps;
        }

        static void FadeOut(ParticleSystem ps, float startAlpha, float holdUntil)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(startAlpha, 0f), new GradientAlphaKey(startAlpha, holdUntil), new GradientAlphaKey(0f, 1f) });
            col.color = grad;
        }

        static void FeatherFlutter(ParticleSystem ps)
        {
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.3f);
            noise.frequency = 0.6f;
            noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.4f);
            noise.damping = true;
            noise.octaveCount = 1;
            noise.quality = ParticleSystemNoiseQuality.Medium;
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.separateAxes = true;
            rot.x = new ParticleSystem.MinMaxCurve(-2f, 2f);
            rot.y = new ParticleSystem.MinMaxCurve(-2f, 2f);
            rot.z = new ParticleSystem.MinMaxCurve(-4f, 4f);
        }

        /// <summary>Little white feather cards tumbling and fluttering out of the goose (one-shot burst of 14-18).</summary>
        public static ParticleSystem CreateFeatherBurst(Transform parent)
        {
            var ps = NewParticleSystem("Feathers", parent, FeatherMaterial());
            var main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.0f, 1.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.11f);
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 0.95f), new Color(0.86f, 0.86f, 0.84f, 0.95f));
            main.gravityModifier = 0.18f;
            main.maxParticles = 48;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)8), new ParticleSystem.Burst(0f, (short)6, (short)10) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.18f;
            FeatherFlutter(ps);
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
            vel.y = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
            FadeOut(ps, 1f, 0.7f);
            return ps;
        }

        /// <summary>
        /// Continuous feather shedding while the goose runs in rage: emits per distance travelled. Left playing with
        /// emission disabled; the caller toggles <c>ps.emission.enabled</c>.
        /// </summary>
        public static ParticleSystem CreateFeatherTrail(Transform parent)
        {
            var ps = NewParticleSystem("FeatherTrail", parent, FeatherMaterial());
            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.08f);
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 0.95f), new Color(0.86f, 0.86f, 0.84f, 0.95f));
            main.gravityModifier = 0.25f;
            main.maxParticles = 60;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = 2.5f;
            emission.enabled = false;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.15f;
            shape.position = new Vector3(0f, 0.45f, -0.1f);
            FeatherFlutter(ps);
            FadeOut(ps, 1f, 0.65f);
            ps.Play();
            return ps;
        }

        /// <summary>Yolk droplets for the egg crack (or any liquid splat). The cone points up.</summary>
        public static ParticleSystem CreateSplash(Transform parent, Color color)
        {
            if (s_SplashMaterial == null) s_SplashMaterial = UnlitTransparent("Splash_Runtime", Color.white, RadialTexture());
            var ps = NewParticleSystem("Splash", parent, s_SplashMaterial);
            var main = ps.main;
            main.duration = 0.4f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.65f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.9f, 2.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.055f);
            main.startColor = color;
            main.gravityModifier = 1.6f;
            main.maxParticles = 48;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)30) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 60f;
            shape.radius = 0.04f;
            ps.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            FadeOut(ps, 1f, 0.6f);
            return ps;
        }

        /// <summary>Small one-shot dust puff. Pooled by the caller (kept on the goose).</summary>
        public static ParticleSystem CreateDustPuff(Transform parent, Material mat, int burst = 10)
        {
            var ps = NewParticleSystem("DustPuff", parent, mat);
            var main = ps.main;
            main.duration = 0.6f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.75f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.18f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.9f, 0.85f, 0.72f, 0.35f);
            main.gravityModifier = -0.04f;
            main.maxParticles = Mathf.Max(24, burst * 3);
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Clamp(burst, 1, short.MaxValue)) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.15f;
            FadeOut(ps, 0.8f, 0.1f);
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.7f));
            return ps;
        }

        /// <summary>Tiny per-footstep puff (burst of 3). Pooled by the caller.</summary>
        public static ParticleSystem CreateFootDust(Transform parent, Material mat)
        {
            var ps = NewParticleSystem("FootDust", parent, mat);
            var main = ps.main;
            main.duration = 0.3f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.45f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(0.9f, 0.85f, 0.72f, 0.28f);
            main.gravityModifier = -0.03f;
            main.maxParticles = 12;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)3) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Hemisphere;
            shape.radius = 0.08f;
            FadeOut(ps, 0.8f, 0.1f);
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.7f, 1f, 1.5f));
            return ps;
        }
    }
}
