using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using WarArena.Gameplay;

namespace WarArena.Tests
{
    /// <summary>
    /// Verification for the rendering fixes.
    ///
    /// These exist because the bugs they cover were all silent. Every one of them
    /// compiled cleanly, threw nothing, and produced a plausible-looking frame: the
    /// canopy rendered opaque, every surface sat at the same gloss, and the sun
    /// shone straight down. A test that only checks for exceptions would have passed
    /// every single one of them.
    ///
    /// The assertion is therefore always on the value that was actually wrong, never
    /// on "did not throw".
    /// </summary>
    public class RenderSetupTests
    {
        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
        }

        private GameObject _root;

        private GameObject Build()
        {
            if (_root != null) Object.Destroy(_root);
            _root = new GameObject("RenderProbe");
            _root.SetActive(false);
            _root.AddComponent<WarArenaBootstrap>();
            _root.SetActive(true);
            return _root;
        }

        [Test]
        public void LitMaterialsActuallyReceiveGlossiness()
        {
            // Regression test for the URP/built-in property mismatch. The old code set
            // _Smoothness, which the built-in Standard shader does not have, so every
            // surface in the game was rendering at the default gloss regardless of
            // what value was requested.
            Material material = VisualMaterials.Matte(new Color(0.4f, 0.4f, 0.4f), 0.85f);

            Assert.IsTrue(material.HasProperty("_Glossiness"),
                $"expected the built-in shader '{material.shader.name}' to expose _Glossiness");
            Assert.AreEqual(0.85f, material.GetFloat("_Glossiness"), 0.01f,
                "requested smoothness must reach the built-in shader");

            // The URP name is written too, so this keeps working if the project moves.
            if (material.HasProperty("_Smoothness"))
            {
                Assert.AreEqual(0.85f, material.GetFloat("_Smoothness"), 0.01f,
                    "the URP smoothness property must be written too");
            }
        }

        [Test]
        public void TransparencyIsActuallyConfigured()
        {
            // Regression test for the second silent failure: MakeTransparent set six
            // URP blend properties and none of them exist on Standard, so the
            // aircraft canopy, propeller disc, exhaust and gunsight all rendered
            // solid. Mode 3 is the built-in shader's transparent mode.
            Material material = VisualMaterials.Matte(Color.white, 0.2f);
            VisualMaterials.MakeTransparent(material);

            Assert.AreEqual(3f, material.GetFloat("_Mode"), 0.001f,
                "built-in transparent mode must be selected");
            Assert.IsTrue(material.IsKeywordEnabled("_ALPHABLEND_ON"),
                "the alpha-blend keyword must be enabled or the blend state is ignored");
            Assert.AreEqual((int)RenderQueue.Transparent, material.renderQueue,
                "transparent surfaces must render after opaque geometry");
        }

        [Test]
        public void SunIsAngledOffVertical()
        {
            // A directional light's rotation is its beam direction. The old rig left
            // it at identity, which points the light straight down: every surface lit
            // identically, no form, and shadows pooled underneath their objects.
            GameObject root = Build();

            Light sun = root.GetComponentsInChildren<Light>(true)[0];
            Vector3 direction = sun.transform.forward;

            float downward = -direction.y;
            Assert.Greater(downward, 0.05f,
                $"the sun must point downward at all, was {direction}");
            Assert.Less(downward, 0.95f,
                $"the sun must not point straight down, was {direction}. " +
                "A vertical sun flattens the whole scene.");
        }

        [Test]
        public void SunIsWarmAndRegisteredAsTheSceneSun()
        {
            GameObject root = Build();
            Light sun = root.GetComponentsInChildren<Light>(true)[0];

            Assert.AreEqual(LightType.Directional, sun.type);

            // Warm light against cool ambient is what separates lit from shaded.
            Assert.Greater(sun.color.r, sun.color.b,
                $"the sun should be warm (more red than blue), was {sun.color}");

            // Registering the sun is what lets the procedural sky draw a matching
            // disc, and what ambient specular looks it up through.
            Assert.AreSame(sun, RenderSettings.sun,
                "the sun must be registered in RenderSettings");

            Assert.AreEqual(LightShadows.Soft, sun.shadows);
            Assert.Greater(sun.shadowNormalBias, 0f,
                "normal bias must be tuned or long thin primitives shadow-acne");
        }

        [Test]
        public void SkyIsRenderedRatherThanClearedToAColour()
        {
            GameObject root = Build();

            Camera camera = root.GetComponentsInChildren<Camera>(true)[0];
            Assert.AreEqual(CameraClearFlags.Skybox, camera.clearFlags,
                "a flat clear colour throws away the entire sky");

            Assert.IsNotNull(RenderSettings.skybox,
                "a skybox material must be assigned for the sky to draw");
            Assert.IsNotNull(RenderSettings.skybox.shader, "the skybox needs a shader");
        }

        [Test]
        public void ArtworkIsCc0AndActuallyLoading()
        {
            // Guards the scan-texture path. If the art is missing the scene still
            // builds, falling back to the generated noise, so nothing else here would
            // notice that the real textures had stopped loading.
            Assert.IsTrue(VisualMaterials.ArtworkPresent,
                "the CC0 scan textures should be present under Resources/Artwork");

            GameObject root = Build();
            Renderer ground = root.transform.Find("Runtime/Battlefield/Ground")
                .GetComponent<Renderer>();
            Material material = ground.sharedMaterial;

            Assert.IsNotNull(material.mainTexture,
                "the ground must carry the scan texture when the artwork is present");

            Texture2D albedo = material.mainTexture as Texture2D;
            Assert.IsNotNull(albedo, "ground albedo should be a Texture2D");
            Assert.GreaterOrEqual(albedo.width, 1024,
                $"expected at least a 1K scan, got {albedo.width}px");
        }

        [Test]
        public void GroundSurfaceHasBumpMapping()
        {
            // Normal maps only do anything if the keyword is enabled; setting the
            // texture alone leaves the surface dead flat.
            GameObject root = Build();

            Renderer ground = root.transform.Find("Runtime/Battlefield/Ground")
                .GetComponent<Renderer>();
            Material material = ground.sharedMaterial;

            if (!VisualMaterials.ArtworkPresent)
            {
                Assert.Pass("artwork absent, bump mapping not applicable");
                return;
            }

            Assert.IsTrue(material.IsKeywordEnabled("_NORMALMAP"),
                "the normal map keyword must be enabled or the bump map is ignored");
            Assert.IsNotNull(material.GetTexture("_BumpMap"),
                "the ground must have a normal map");
        }

        [Test]
        public void TexturedSurfacesTileAndWrap()
        {
            GameObject root = Build();
            Renderer ground = root.transform.Find("Runtime/Battlefield/Ground")
                .GetComponent<Renderer>();
            Material material = ground.sharedMaterial;

            Texture2D albedo = material.mainTexture as Texture2D;
            if (albedo == null) { Assert.Pass("no albedo loaded"); return; }

            Assert.AreEqual(TextureWrapMode.Repeat, albedo.wrapMode,
                "a clamped texture shows a hard edge where the plane ends");
            Assert.GreaterOrEqual(albedo.anisoLevel, 4,
                "a ground plane seen at a grazing angle needs anisotropy");
            Assert.Greater(material.mainTextureScale.x, 1f,
                "the ground texture must tile rather than stretch over 140 m");
        }

        [Test]
        public void ProceduralGroundFallbackStillWorksWithoutArtwork()
        {
            // The fallback must be a real material, not a broken one. This asserts
            // the generator itself rather than the absence of the scan textures.
            Texture2D generated = VisualMaterials.GroundDetailTexture();
            Assert.IsNotNull(generated, "the procedural fallback texture must generate");
            Assert.AreEqual(256, generated.width);
            Assert.AreEqual(TextureWrapMode.Repeat, generated.wrapMode);

            Material fallback = VisualMaterials.Textured(Color.white, "NoSuchFolder", 8f, 0.1f);
            Assert.IsNotNull(fallback, "an unknown folder must fall back, not return null");
            Assert.IsNotNull(fallback.mainTexture,
                "the fallback material must still carry a texture");
        }

        [Test]
        public void GroundHasProceduralDetail()
        {
            // 140 m of untextured plane was one flat green. The detail texture breaks
            // it up, and it must tile without a visible seam.
            GameObject root = Build();

            Renderer ground = root.transform.Find("Runtime/Battlefield/Ground")
                .GetComponent<Renderer>();
            Material material = ground.sharedMaterial;

            Assert.IsNotNull(material.mainTexture, "the ground must have a detail texture");

            Texture2D texture = material.mainTexture as Texture2D;
            Assert.IsNotNull(texture, "the ground texture should be a Texture2D");
            Assert.AreEqual(TextureWrapMode.Repeat, texture.wrapMode,
                "a clamp-wrapped ground texture shows a hard edge at the horizon");
            Assert.Greater(material.mainTextureScale.x, 1f,
                "the detail texture must tile rather than stretch across the plane");
        }

        [Test]
        public void FogIsEnabledAndColouredToMatchTheHorizon()
        {
            GameObject root = Build();

            Assert.IsTrue(RenderSettings.fog, "fog is what gives the ground plane scale");
            Assert.Greater(RenderSettings.fogDensity, 0f);

            // Fog matched to the horizon means the tree line dissolves instead of
            // ending against a hard colour change.
            Assert.Greater(RenderSettings.fogColor.maxColorComponent,
                RenderSettings.ambientEquatorColor.maxColorComponent,
                "fog should be lighter than the horizon ambient it replaces");
        }

        [Test]
        public void AntiAliasingIsRaised()
        {
            Build();

            // The scene is untextured primitives: long straight edges against sky and
            // ground, the geometry that aliases worst. It was running at 2x.
            Assert.GreaterOrEqual(QualitySettings.antiAliasing, 4,
                "expected at least 4x MSAA for untextured geometry");
        }

        [Test]
        public void GroundDetailTextureIsSeamless()
        {
            // The texture repeats dozens of times across the ground plane, so a seam
            // would appear as a grid of lines. Comparing opposite edges of the tile
            // catches that.
            Texture2D texture = VisualMaterials.GroundDetailTexture();

            int size = texture.width;
            Color[] pixels = texture.GetPixels();

            // Sample the wrap seam at the tile boundary: column size-1 against
            // column 0. A tileable texture has these close in value; a clamped one
            // has a visible jump.
            float worstDelta = 0f;
            for (int y = 0; y < size; y += 8)
            {
                Color right = pixels[y * size + (size - 1)];
                Color left = pixels[y * size + 0];
                worstDelta = Mathf.Max(worstDelta,
                    Mathf.Abs(right.r - left.r) + Mathf.Abs(right.g - left.g));
            }

            Assert.Less(worstDelta, 0.25f,
                $"ground detail texture has a visible tile seam (delta {worstDelta:F2})");
        }

        [Test]
        public void SteelPartsAreMetallicAndPaintIsNot()
        {
            // Tracks, wheels and barrels are metal; the hull is paint. Treating them
            // identically is most of why the tanks read as coloured boxes.
            GameObject root = Build();

            Transform tank = root.transform.Find("Runtime/Tank_Player");
            Assert.IsNotNull(tank, "player tank should exist");

            Material track = tank.Find("LeftTrack").GetComponent<Renderer>().sharedMaterial;
            Material hull = tank.Find("LowerHull").GetComponent<Renderer>().sharedMaterial;

            Assert.Greater(track.GetFloat("_Metallic"), hull.GetFloat("_Metallic"),
                "tracks must be more metallic than painted hull");
            Assert.Greater(track.GetFloat("_Glossiness"), hull.GetFloat("_Glossiness"),
                "tracks must catch more specular than matte paint");
        }

        [Test]
        public void AircraftCanopyIsSeeThrough()
        {
            // The canopy was configured transparent and rendered opaque, which read
            // as a solid blue box sitting on the fuselage.
            GameObject root = Build();

            Transform plane = root.transform.Find("Runtime/Plane_Player");
            Assert.IsNotNull(plane, "player aircraft should exist");

            Material canopy = plane.Find("Canopy").GetComponent<Renderer>().sharedMaterial;
            Assert.Less(canopy.color.a, 1f,
                "canopy glass must actually have partial alpha");
            Assert.AreEqual(3f, canopy.GetFloat("_Mode"), 0.001f,
                "canopy must be in the shader's transparent mode");
        }

        [Test]
        public void HudElementsAreUnlit()
        {
            // A shaded reticle dims when the tank turns away from the sun, which is
            // exactly when the player most needs to read the penetration cue.
            GameObject root = Build();

            Material reticle = root.transform.Find("Runtime/Reticle")
                .GetComponent<Renderer>().sharedMaterial;

            // Checked via the shader rather than by reading _Metallic, because an
            // unlit shader has no such property and reading it raises an engine error
            // even though the absence is the thing being asserted.
            Assert.AreEqual("Unlit/Color", reticle.shader.name,
                "the reticle must be unlit so the sun cannot dim it");
            Assert.IsFalse(reticle.shader.name.StartsWith("Standard"),
                "a lit reticle dims when the tank turns away from the sun");
        }

        [UnityTest]
        public IEnumerator DestroyingTheBootstrapRestoresGlobalRenderState()
        {
            // The PlayMode suite builds a full bootstrap seventeen times, and
            // RenderSettings is global. Without restoration, the last test leaves the
            // editor permanently lit by a sun that no longer exists.
            // Deliberately set to values the lighting rig does not use, so the test
            // does not depend on the current ambient happening to differ already.
            // Another test in this class leaves these settings applied, and this test
            // must still be meaningful whichever order they run in.
            Color sentinelSky = new Color(0.123f, 0.456f, 0.789f);
            int sentinelMsaa = QualitySettings.antiAliasing;
            RenderSettings.ambientSkyColor = sentinelSky;

            GameObject root = Build();
            Assert.AreNotEqual(sentinelSky, RenderSettings.ambientSkyColor,
                "sanity check: building should change ambient");

            // Destroy, not DestroyImmediate: OnDestroy runs at the end of the frame for
            // Destroy, so the restore has not happened yet at this point. This is a
            // deliberate end-of-frame boundary, not a defect in the restore.
            Object.Destroy(root);
            root = null;
            yield return null;

            Assert.AreEqual(sentinelSky, RenderSettings.ambientSkyColor,
                "ambient must be restored on destroy");
            Assert.AreEqual(sentinelMsaa, QualitySettings.antiAliasing,
                "anti-aliasing must be restored on destroy");
        }
    }
}
