using UnityEngine;
using WarArena.Vehicles;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Builds the battlefield in code: dusk ground, a road, ruined cover, a tree
    /// line and lighting. Same reasoning as the fighter's ArenaBuilder: no scene
    /// references means nothing to misconfigure, and the whole layout is
    /// reviewable as code.
    /// </summary>
    public static class BattlefieldBuilder
    {
        public static GameObject Build(Transform parent)
        {
            var root = new GameObject("Battlefield");
            root.transform.SetParent(parent, false);

            BuildGround(root.transform);
            BuildRuins(root.transform);
            BuildTreeLine(root.transform);

            return root;
        }

        private static void BuildGround(Transform parent)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(parent, false);
            ground.transform.localScale = new Vector3(14f, 1f, 14f);

            // Churned earth, from the CC0 scan set. Tiled tightly because a 2K scan
            // across 140 m would be unreadably stretched.
            ground.GetComponent<Renderer>().sharedMaterial =
                VisualMaterials.Textured(new Color(0.85f, 0.82f, 0.78f), "Ground", 34f, 0.06f);
            ground.isStatic = true;

            GameObject road = GameObject.CreatePrimitive(PrimitiveType.Plane);
            road.name = "Road";
            road.transform.SetParent(parent, false);
            road.transform.localScale = new Vector3(0.5f, 1f, 14f);
            road.transform.localPosition = new Vector3(0f, 0.01f, 0f);

            // Compacted gravel, and deliberately glossier than the surrounding mud so
            // the road reads as a different material rather than as a stripe of paint.
            road.GetComponent<Renderer>().sharedMaterial =
                VisualMaterials.Textured(new Color(0.72f, 0.70f, 0.68f), "Road", 18f, 0.28f);
            road.isStatic = true;
        }

        private static void BuildRuins(Transform parent)
        {
            // Broken masonry from the CC0 scan set. Only three repeats across a
            // 10 m wall, so the rubble detail stays legible at close range.
            Material wall = VisualMaterials.Textured(
                new Color(0.80f, 0.78f, 0.74f), "Rubble", 3f, 0.10f);

            Vector3[] spots =
            {
                new Vector3(-22f, 0f, -30f), new Vector3(24f, 0f, -48f),
                new Vector3(-26f, 0f, 18f), new Vector3(26f, 0f, 34f),
                new Vector3(-20f, 0f, 62f), new Vector3(22f, 0f, 72f),
            };

            for (int i = 0; i < spots.Length; i++)
            {
                Vector3 p = spots[i];

                GameObject wallA = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wallA.name = "RuinWallA" + i;
                Object.Destroy(wallA.GetComponent<Collider>());
                wallA.transform.SetParent(parent, false);
                wallA.transform.localPosition = p + new Vector3(0f, 2f, 0f);
                wallA.transform.localRotation = Quaternion.Euler(0f, i * 23f, 0f);
                wallA.transform.localScale = new Vector3(10f, 4f, 0.8f);
                wallA.GetComponent<Renderer>().sharedMaterial = wall;

                GameObject wallB = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wallB.name = "RuinWallB" + i;
                Object.Destroy(wallB.GetComponent<Collider>());
                wallB.transform.SetParent(parent, false);
                wallB.transform.localPosition = p + new Vector3(4f, 1.5f, 3f);
                wallB.transform.localRotation = Quaternion.Euler(0f, i * 23f, 0f);
                wallB.transform.localScale = new Vector3(0.8f, 3f, 8f);
                wallB.GetComponent<Renderer>().sharedMaterial = wall;
            }

            Material sandbag = VisualMaterials.Matte(new Color(0.42f, 0.36f, 0.24f), 0.06f);
            for (int i = 0; i < 10; i++)
            {
                GameObject bag = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                bag.name = "Sandbag" + i;
                Object.Destroy(bag.GetComponent<Collider>());
                bag.transform.SetParent(parent, false);
                bag.transform.localPosition = new Vector3(-8f + (i % 5) * 4f, 0.4f, i < 5 ? -12f : 12f);
                bag.transform.localScale = new Vector3(1.1f, 0.8f, 1.1f);
                bag.GetComponent<Renderer>().sharedMaterial = sandbag;
            }
        }

        private static void BuildTreeLine(Transform parent)
        {
            // Bark gets the ground detail texture too: the trunks are thin verticals
            // seen against a bright sky, and a flat brown cylinder reads as a pipe.
            Material trunk = VisualMaterials.WithGroundDetail(
                VisualMaterials.Matte(new Color(0.25f, 0.18f, 0.12f), 0.05f), 2f);

            // Foliage is two-sided-looking and very matte. Making it slightly glossier
            // than the ground picks up a hint of the sky, which stops the tree line
            // reading as a solid dark wall at the horizon.
            Material leaf = VisualMaterials.Matte(new Color(0.13f, 0.26f, 0.12f), 0.16f);

            for (int i = 0; i < 26; i++)
            {
                float a = i / 26f * Mathf.PI * 2f;
                float r = 105f + (i % 4) * 8f;
                float x = Mathf.Cos(a) * r;
                float z = Mathf.Sin(a) * r;

                GameObject stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                stem.name = "Trunk" + i;
                Object.Destroy(stem.GetComponent<Collider>());
                stem.transform.SetParent(parent, false);
                stem.transform.localPosition = new Vector3(x, 1.5f, z);
                stem.transform.localScale = new Vector3(0.25f, 1.5f, 0.25f);
                stem.GetComponent<Renderer>().sharedMaterial = trunk;

                // The crown is a sphere rather than a second cylinder. Scaled to the
                // old proportions a cylinder came out 1.7 m across and 6 m tall, so at
                // tree-line distance the silhouette collapsed into a thin vertical bar
                // and the whole horizon read as a row of poles. A sphere squashed
                // slightly wider than it is tall gives the rounded, bushy profile that
                // survives being seen from across the map, and it costs the same single
                // draw call.
                GameObject crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                crown.name = "Crown" + i;
                Object.Destroy(crown.GetComponent<Collider>());
                crown.transform.SetParent(parent, false);
                crown.transform.localPosition = new Vector3(x, 3.4f, z);
                crown.transform.localScale = new Vector3(2.4f, 1.9f, 2.4f);
                crown.GetComponent<Renderer>().sharedMaterial = leaf;
            }
        }

        /// <summary>
        /// Sun angle, in degrees above the horizon.
        ///
        /// The old rig left the light's rotation at identity, which points a
        /// directional light straight down. That is the worst possible angle for a
        /// scene like this: every surface is lit identically from above, so nothing
        /// has a lit side and a shaded side, forms have no volume, and every shadow
        /// pools directly underneath its object and reads as a dark patch on the
        /// ground rather than as a cast shadow.
        ///
        /// Around 27 degrees is late afternoon. Low enough that shadows are long and
        /// direction is obvious, high enough that the ground is still readable.
        /// </summary>
        private const float SunElevationDeg = 27f;

        /// <summary>Compass bearing the light comes from, in degrees.</summary>
        private const float SunAzimuthDeg = 142f;

        public static GameObject BuildLighting(Transform parent)
        {
            var rig = new GameObject("Lighting");
            rig.transform.SetParent(parent, false);

            // Warm low sun against a cool sky. The colour separation is doing real
            // work: it makes the sunlit faces read as sunlit and the shadowed faces
            // read as shadow, rather than everything sitting at the same temperature.
            Color sunTint = new Color(1f, 0.90f, 0.74f);
            Color skyAmbient = new Color(0.36f, 0.43f, 0.56f);
            Color horizonAmbient = new Color(0.30f, 0.30f, 0.28f);
            Color bounceAmbient = new Color(0.16f, 0.15f, 0.11f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = skyAmbient;
            RenderSettings.ambientEquatorColor = horizonAmbient;
            RenderSettings.ambientGroundColor = bounceAmbient;
            RenderSettings.ambientIntensity = 1f;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;

            // Denser than before, and it has to be: fog is what gives the 140 m
            // ground plane any sense of scale, because there is nothing else in the
            // scene to judge distance against.
            RenderSettings.fogDensity = 0.0055f;

            // Matched to the sky just above the horizon, so the tree line dissolves
            // into the sky instead of ending against a hard colour change.
            RenderSettings.fogColor = new Color(0.52f, 0.55f, 0.60f);

            // A real sky rather than a flat clear colour. Registered before the light
            // so the procedural sky can find the sun to draw a matching disc.
            Material sky = VisualMaterials.BuildSkybox();
            if (sky != null) RenderSettings.skybox = sky;

            Light sun = new GameObject("Sun").AddComponent<Light>();
            sun.transform.SetParent(rig.transform, false);
            sun.type = LightType.Directional;

            // A directional light's rotation is its beam direction, so this is what
            // actually aims the light. The old code set only a position, which for a
            // directional light does nothing at all.
            sun.transform.rotation = Quaternion.Euler(SunElevationDeg, SunAzimuthDeg, 0f);
            sun.color = sunTint;
            sun.intensity = 1.15f;

            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.78f;

            // Bias tuning. The scene is large and made of long thin primitives
            // (barrels, tree trunks, wall edges), which is the classic case for
            // shadow acne, and nothing was tuned before. Normal bias is the one that
            // matters here, because it offsets along the surface normal rather than
            // along the light ray, so it does not detach shadows the way a large
            // depth bias does.
            sun.shadowBias = 0.02f;
            sun.shadowNormalBias = 0.35f;

            // Registering the sun matters beyond the sky: ambient specular and any
            // future baked-lighting pass both look it up through this slot, and it
            // was previously empty.
            RenderSettings.sun = sun;

            return rig;
        }

        public static GameObject BuildCamera(Transform parent)
        {
            GameObject go = new GameObject("MainCamera");
            go.transform.SetParent(parent, false);
            go.tag = "MainCamera";

            Camera camera = go.AddComponent<Camera>();
            camera.fieldOfView = 60f;
            camera.nearClipPlane = 0.2f;
            camera.farClipPlane = 600f;

            // Skybox, not a solid colour. Every object is an untextured primitive, so
            // the sky is carrying most of the scene's visual weight and a flat fill
            // colour throws all of it away.
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.05f, 0.07f, 0.12f);

            // 4x MSAA. The scene is built from untextured primitives, which means
            // long straight edges against a sky and a ground plane: exactly the
            // geometry that aliases worst. It was running at 2x with no other
            // anti-aliasing available.
            QualitySettings.antiAliasing = 4;

            go.AddComponent<AudioListener>();
            return go;
        }
    }
}