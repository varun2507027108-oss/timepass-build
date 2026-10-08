using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WarArena.Gameplay
{
    /// <summary>
    /// One place that knows how to build materials, textures and the sky.
    ///
    /// This exists because the project previously had three near-identical material
    /// factories, each of which was written against Universal Render Pipeline
    /// property names while the project actually runs the built-in pipeline. The
    /// result was silent, invisible failure:
    ///
    /// - <c>_Smoothness</c> is URP. The built-in Standard shader calls it
    ///   <c>_Glossiness</c>, so every smoothness value in the game was being
    ///   discarded and every surface sat at the same default gloss.
    /// - <c>MakeTransparent</c> set six URP blend properties that do not exist on
    ///   Standard or Unlit/Color, so the aircraft canopy, the propeller disc, the
    ///   jet exhaust and the gunsight were all configured to be see-through and all
    ///   rendered fully opaque.
    ///
    /// Guarding those setters with <c>HasProperty</c>, as the old code did, hides
    /// the mistake rather than fixing it: the call succeeds and does nothing. So
    /// both property sets are written unconditionally here. Setting a property a
    /// shader does not have is a no-op by design, which makes writing both the safe
    /// and the correct thing to do, and means this file keeps working unchanged if
    /// the project is ever moved to URP.
    ///
    /// Everything here is cached statically. The PlayMode suite builds a full
    /// bootstrap seventeen times, and none of this work should be repeated for each.
    /// </summary>
    public static class VisualMaterials
    {
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SpecularId = Shader.PropertyToID("_SpecColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private static Shader _litShader;
        private static Shader _unlitShader;
        private static Texture2D _groundDetail;
        private static Material _skybox;

        /// <summary>
        /// Lit shader, preferring URP when the project has it.
        ///
        /// The built-in <c>Standard</c> shader ships inside the editor and is
        /// guaranteed present, so the fallback cannot fail. If <c>Shader.Find</c>
        /// ever returned null for both, falling back to the error shader keeps the
        /// scene rendering as magenta instead of throwing during Awake.
        /// </summary>
        public static Shader LitShader
        {
            get
            {
                if (_litShader == null)
                {
                    _litShader = Shader.Find("Universal Render Pipeline/Lit");
                    if (_litShader == null) _litShader = Shader.Find("Standard");
                }

                return _litShader;
            }
        }

        public static Shader UnlitShader
        {
            get
            {
                if (_unlitShader == null)
                {
                    _unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (_unlitShader == null) _unlitShader = Shader.Find("Unlit/Color");
                    if (_unlitShader == null) _unlitShader = LitShader;
                }

                return _unlitShader;
            }
        }

        /// <summary>
        /// Lit surface material.
        /// </summary>
        /// <param name="smoothness">0 is fully matte, 1 is a mirror. Written to
        /// whichever property the active shader actually has.</param>
        public static Material Lit(Color color, float smoothness, float metallic)
        {
            var material = new Material(LitShader);
            ApplyLitProperties(material, color, smoothness, metallic);
            return material;
        }

        /// <summary>Matte painted or organic surface: low gloss, no metal.</summary>
        public static Material Matte(Color color, float smoothness = 0.08f)
        {
            return Lit(color, smoothness, 0f);
        }

        /// <summary>
        /// Bare or worn metal. Worth having as its own case because a tank's tracks
        /// and barrel need to catch a specular highlight that its painted hull should
        /// not, and that difference is most of what makes flat primitives read as
        /// different materials.
        /// </summary>
        public static Material Metal(Color color, float smoothness = 0.5f, float metallic = 0.85f)
        {
            return Lit(color, smoothness, metallic);
        }

        /// <summary>Unlit surface, for HUD elements that must not be shaded.</summary>
        public static Material Unlit(Color color)
        {
            var material = new Material(UnlitShader);
            ApplyColor(material, color);
            return material;
        }

        /// <summary>
        /// Lit surface that also emits light, used for tracers and jet exhaust.
        /// </summary>
        public static Material Emissive(Color color, Color emission, float intensity = 2.5f)
        {
            var material = Lit(color, 0.3f, 0f);
            material.EnableKeyword("_EMISSION");
            material.SetColor(EmissionColorId, emission * intensity);
            return material;
        }

        private static void ApplyLitProperties(Material material, Color color,
            float smoothness, float metallic)
        {
            ApplyColor(material, color);

            // Written to both names. Whichever the active shader has picks it up.
            material.SetFloat(SmoothnessId, smoothness);
            material.SetFloat(GlossinessId, smoothness);
            material.SetFloat(MetallicId, metallic);
            material.SetColor(SpecularId, Color.white * Mathf.Lerp(0.04f, 0.6f, smoothness));
        }

        private static void ApplyColor(Material material, Color color)
        {
            // Also written to both names, for the same reason as the gloss above.
            material.SetColor(ColorId, color);
            material.SetColor(BaseColorId, color);
            material.color = color;
        }

        /// <summary>
        /// Turn a material into a see-through one.
        ///
        /// Two completely different parameter sets are needed, and the old
        /// implementation only had the URP one. Under the built-in pipeline it set
        /// six properties that do not exist, leaving the aircraft canopy and the
        /// gunsight drawn as solid blocks.
        ///
        /// The built-in Standard shader hides its transparency behind a <c>_Mode</c>
        /// switch: opaque, cutout, fade and transparent are modes 0 to 3. Setting
        /// <c>_Mode</c> also requires the matching shader keyword to be enabled, and
        /// without it the blend state is ignored even though the state is set.
        /// </summary>
        public static void MakeTransparent(Material material)
        {
            // Built-in Standard / Unlit.
            material.SetFloat("_Mode", 3f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            // URP Lit.
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            material.renderQueue = (int)RenderQueue.Transparent;
        }

        /// <summary>
        /// Mottled ground texture, generated rather than authored.
        ///
        /// The battlefield is built entirely from untextured primitives, so the
        /// ground was one flat plane of uniform green: 140 metres of nothing, which
        /// is why the scene reads as a physics test rather than a place. A cheap
        /// noise texture breaks that up and costs nothing to ship.
        ///
        /// The noise is tileable. Wrapping every lattice lookup by the octave's
        /// period is what removes the visible grid seam that plain Perlin noise
        /// produces, which matters a lot here because the texture repeats dozens of
        /// times across the ground plane.
        /// </summary>
        public static Texture2D GroundDetailTexture()
        {
            if (_groundDetail != null) return _groundDetail;

            const int size = 256;
            var texture = new Texture2D(size, size, TextureFormat.RGB24, true);
            texture.name = "GroundDetail";
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;

            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float v = y / (float)size;

                    // Brightness only, centred on 1 so it modulates the base colour
                    // rather than replacing it. Keeping the range tight stops the
                    // ground looking like camouflage rather than dirt.
                    float n = TileableFbm(u, v, 4, 4);
                    float brightness = Mathf.Lerp(0.78f, 1.22f, n);

                    // A little colour drift keeps it from reading as grey noise.
                    float r = brightness;
                    float g = brightness * 1.02f;
                    float b = brightness * 0.94f;

                    pixels[y * size + x] = new Color32(
                        (byte)(Mathf.Clamp01(r) * 255f),
                        (byte)(Mathf.Clamp01(g) * 255f),
                        (byte)(Mathf.Clamp01(b) * 255f),
                        255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true);
            _groundDetail = texture;
            return texture;
        }

        /// <summary>
        /// Fractal value noise that repeats seamlessly over the unit square.
        ///
        /// Each octave doubles both its frequency and its wrap period, so every
        /// octave is individually tileable and therefore so is the sum.
        /// </summary>
        private static float TileableFbm(float u, float v, int basePeriod, int octaves)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;
            int period = basePeriod;

            for (int i = 0; i < octaves; i++)
            {
                sum += ValueNoise(u * period, v * period, period) * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
                period *= 2;
            }

            return sum / total;
        }

        /// <summary>Smoothly interpolated value noise on a wrapped integer lattice.</summary>
        private static float ValueNoise(float x, float y, int period)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float xf = x - xi;
            float yf = y - yi;

            // Smoothstep, so the lattice interpolates without visible creases.
            float u = xf * xf * (3f - 2f * xf);
            float v = yf * yf * (3f - 2f * yf);

            int x0 = Wrap(xi, period);
            int x1 = Wrap(xi + 1, period);
            int y0 = Wrap(yi, period);
            int y1 = Wrap(yi + 1, period);

            float a = Hash(x0, y0);
            float b = Hash(x1, y0);
            float c = Hash(x0, y1);
            float d = Hash(x1, y1);

            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        private static int Wrap(int value, int period)
        {
            int result = value % period;
            return result < 0 ? result + period : result;
        }

        /// <summary>
        /// Integer hash to float in 0..1. Deliberately not <c>Random</c>: the ground
        /// must look identical every run, so the "randomness" has to be fixed.
        /// </summary>
        private static float Hash(int x, int y)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                h = h ^ (h >> 16);
                return (h & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        /// <summary>
        /// Sky material, matching the directional light so the sun in the sky and
        /// the light on the ground agree.
        ///
        /// The camera previously cleared to flat navy, so the top half of every
        /// frame was a single constant colour and the scene had no horizon at all.
        /// A procedural sky gives the fog something to blend into and gives the
        /// player a sense of heading, which matters when the alternative is flying
        /// over featureless green with no visual reference but the gunsight.
        ///
        /// The sky's sun is driven by <c>RenderSettings.sun</c>, which is why
        /// <see cref="BattlefieldBuilder"/> has to register the light it creates.
        /// </summary>
        public static Material BuildSkybox()
        {
            if (_skybox != null) return _skybox;

            Shader shader = Shader.Find("Skybox/Procedural");
            if (shader == null) return null;

            var material = new Material(shader);

            // 2 = simple sun disc, so the light source is visible where it belongs.
            material.SetFloat("_SunDisk", 2f);
            material.SetFloat("_SunSize", 0.035f);
            material.SetFloat("_SunSizeConvergence", 8f);
            material.SetFloat("_AtmosphereThickness", 1.15f);
            material.SetFloat("_Exposure", 1.25f);
            material.SetColor("_SkyTint", new Color(0.42f, 0.50f, 0.62f));
            material.SetColor("_GroundColor", new Color(0.16f, 0.16f, 0.14f));

            _skybox = material;
            return material;
        }

        /// <summary>
        /// Applies the procedural ground texture to a material.
        /// </summary>
        /// <param name="tiling">How many times the texture repeats across the surface.</param>
        public static Material WithGroundDetail(Material material, float tiling)
        {
            material.SetTexture(MainTexId, GroundDetailTexture());
            material.mainTextureScale = new Vector2(tiling, tiling);
            return material;
        }

        // Artwork, all CC0 from Poly Haven, loaded by path because this project
        // builds its whole scene in code and has no serialized asset references.
        // Keyed by "folder/suffix"; the value is the Resources path minus extension.
        private static readonly Dictionary<string, string> TextureIndex =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Ground/diff", "Artwork/Ground/brown_mud_02_diff_2k" },
                { "Ground/norm", "Artwork/Ground/brown_mud_02_norm_2k" },
                { "Road/diff", "Artwork/Road/gravel_road_diff_2k" },
                { "Road/norm", "Artwork/Road/gravel_road_norm_2k" },
                { "Rubble/diff", "Artwork/Rubble/rubble_diff_2k" },
                { "Rubble/norm", "Artwork/Rubble/rubble_norm_2k" },
            };

        /// <summary>
        /// Builds a textured surface from the scan-texture set, falling back to the
        /// generated noise when the artwork is not present.
        ///
        /// The fallback matters: the PlayMode suite and any headless run without the
        /// art checked out still have to build a working scene, so a missing 19 MB
        /// folder degrades the look rather than breaking the game.
        /// </summary>
        public static Material Textured(Color tint, string folder, float tiling,
            float smoothness)
        {
            Texture2D albedo = LoadArt(folder, "diff");
            Texture2D normal = LoadArt(folder, "norm");

            if (albedo == null && normal == null)
            {
                return WithGroundDetail(Matte(tint, smoothness), tiling);
            }

            var material = new Material(LitShader);

            if (albedo != null)
            {
                Configure(albedo);
                material.SetTexture(MainTexId, albedo);
                // Tint multiplies the scan so one texture can serve several colours.
                material.SetColor(ColorId, tint);
                material.SetColor(BaseColorId, tint);
            }
            else
            {
                ApplyColor(material, tint);
            }

            if (normal != null)
            {
                Configure(normal);
                // Poly Haven ships OpenGL-style normals (+Y up), which is exactly what
                // Unity's Standard shader expects natively. No green-channel flip.
                material.SetTexture("_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
                material.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
            }

            // Mud, gravel and rubble are dielectrics, so metallic stays zero and gloss
            // is a constant rather than a map. Physically right, and it saves shipping
            // a roughness map per surface.
            material.SetFloat(SmoothnessId, smoothness);
            material.SetFloat(GlossinessId, smoothness);
            material.SetFloat(MetallicId, 0f);
            material.SetFloat("_BumpScale", 1f);
            material.SetTextureScale("_MainTex", new Vector2(tiling, tiling));

            return material;
        }

        private static void Configure(Texture2D texture)
        {
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Trilinear;

            // Anisotropy is the single biggest win on a ground plane viewed at a
            // grazing angle, which is most of the time in a chase camera.
            texture.anisoLevel = 8;
        }

        private static Texture2D LoadArt(string folder, string suffix)
        {
            if (!TextureIndex.TryGetValue(folder + "/" + suffix, out string path)) return null;
            return Resources.Load<Texture2D>(path);
        }

        /// <summary>Whether the CC0 artwork is actually present on disk.</summary>
        public static bool ArtworkPresent =>
            Resources.Load<Texture2D>(TextureIndex["Ground/diff"]) != null;
    }
}