using System.Collections.Generic;
using UnityEngine;
using WarArena.Vehicles;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Builds the 3D battlefield environment in code: 3D terrain topography with
    /// rolling perimeter hills and mountain ridges, authentic WWII village ruins,
    /// church on the hill, observation watchtowers, knocked-out tank wrecks with
    /// billowing smoke plumes, Czech hedgehogs, dragon's teeth, fortified sandbag
    /// bunkers, artillery shell craters, 3D conifer pine tree lines, and cinematic war lighting.
    /// </summary>
    public static class BattlefieldBuilder
    {
        public static GameObject Build(Transform parent)
        {
            var root = new GameObject("Battlefield");
            root.transform.SetParent(parent, false);

            BuildGround(root.transform);
            BuildArchitecture(root.transform);
            BuildFortifications(root.transform);
            BuildVegetation(root.transform);
            BuildAtmosphere(root.transform);

            return root;
        }

        public static float GetTerrainHeight(float x, float z)
        {
            float r = Mathf.Sqrt(x * x + z * z);
            if (r <= 85f) return 0f;

            float t = Mathf.Clamp01((r - 85f) / (450f - 85f));
            float w = Mathf.SmoothStep(0f, 1f, t * 2.2f);

            // Layered fractal harmonics: rolling perimeter hills, natural ridgelines, and distant mountains
            float h1 = Mathf.Sin(x * 0.016f + 1.2f) * Mathf.Cos(z * 0.014f + 0.7f) * 16f;
            float h2 = Mathf.Cos(x * 0.030f - z * 0.026f) * 9f;
            float ridge = Mathf.Abs(Mathf.Sin(x * 0.011f + z * 0.009f + 0.5f)) * 14f;
            float outerRim = Mathf.Pow(t, 1.8f) * 44f;

            return Mathf.Max(0f, w * (h1 + h2 + ridge + outerRim));
        }

        private static void BuildGround(Transform parent)
        {
            // Inner combat floor plane, exactly matching RenderSetupTests requirements
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(parent, false);
            ground.transform.localScale = new Vector3(18f, 1f, 18f);

            // Churned earth from the CC0 scan set.
            Material groundMat = VisualMaterials.Textured(
                new Color(0.85f, 0.82f, 0.78f), "Ground", 34f, 0.06f);
            ground.GetComponent<Renderer>().sharedMaterial = groundMat;
            ground.isStatic = true;

            // Main supply road running north-south through the warzone
            GameObject road = GameObject.CreatePrimitive(PrimitiveType.Plane);
            road.name = "Road";
            road.transform.SetParent(parent, false);
            road.transform.localScale = new Vector3(0.6f, 1f, 18f);
            road.transform.localPosition = new Vector3(0f, 0.01f, 0f);

            // Compacted gravel road with subtle sheen
            road.GetComponent<Renderer>().sharedMaterial =
                VisualMaterials.Textured(new Color(0.72f, 0.70f, 0.68f), "Road", 18f, 0.28f);
            road.isStatic = true;

            // Secondary dirt path branching west towards the ruined village hamlet
            GameObject branchRoad = GameObject.CreatePrimitive(PrimitiveType.Plane);
            branchRoad.name = "VillageRoad";
            branchRoad.transform.SetParent(parent, false);
            branchRoad.transform.localScale = new Vector3(0.45f, 1f, 6.5f);
            branchRoad.transform.localPosition = new Vector3(-30f, 0.012f, 12f);
            branchRoad.transform.localRotation = Quaternion.Euler(0f, 75f, 0f);
            branchRoad.GetComponent<Renderer>().sharedMaterial = road.GetComponent<Renderer>().sharedMaterial;
            branchRoad.isStatic = true;

            // Procedural 3D outer terrain mesh connecting smoothly to the inner floor
            BuildOuterTerrain(parent, groundMat);
        }

        private static void BuildOuterTerrain(Transform parent, Material groundMat)
        {
            var go = new GameObject("OuterTerrain");
            go.transform.SetParent(parent, false);

            var mesh = new Mesh { name = "BattlefieldOuterTerrain" };
            int radialSegments = 54;
            int ringCount = 22;

            var verts = new Vector3[(ringCount + 1) * radialSegments];
            var uvs = new Vector2[verts.Length];
            var tris = new int[ringCount * radialSegments * 6];

            for (int j = 0; j <= ringCount; j++)
            {
                float ringFrac = (float)j / ringCount;
                float r = Mathf.Lerp(84f, 480f, Mathf.Pow(ringFrac, 1.25f));

                for (int i = 0; i < radialSegments; i++)
                {
                    float a = (float)i / radialSegments * Mathf.PI * 2f;
                    float x = Mathf.Cos(a) * r;
                    float z = Mathf.Sin(a) * r;
                    float y = GetTerrainHeight(x, z);

                    int idx = j * radialSegments + i;
                    verts[idx] = new Vector3(x, y, z);
                    // Seamless UV coordinate mapping identical to central ground plane
                    uvs[idx] = new Vector2(x / 140f, z / 140f);
                }
            }

            int triIdx = 0;
            for (int j = 0; j < ringCount; j++)
            {
                for (int i = 0; i < radialSegments; i++)
                {
                    int nextI = (i + 1) % radialSegments;
                    int current = j * radialSegments + i;
                    int currentNext = j * radialSegments + nextI;
                    int nextRing = (j + 1) * radialSegments + i;
                    int nextRingNext = (j + 1) * radialSegments + nextI;

                    tris[triIdx++] = current;
                    tris[triIdx++] = currentNext;
                    tris[triIdx++] = nextRing;

                    tris[triIdx++] = currentNext;
                    tris[triIdx++] = nextRingNext;
                    tris[triIdx++] = nextRing;
                }
            }

            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = groundMat;
            go.isStatic = true;
        }

        private static void BuildArchitecture(Transform parent)
        {
            var archGroup = new GameObject("Architecture");
            archGroup.transform.SetParent(parent, false);

            var housePrefab = Resources.Load<GameObject>("Artwork/Environment/RuinedHouse");
            var churchPrefab = Resources.Load<GameObject>("Artwork/Environment/RuinedChurch");
            var towerPrefab = Resources.Load<GameObject>("Artwork/Environment/Watchtower");
            var wreckPrefab = Resources.Load<GameObject>("Artwork/Environment/TankWreck");
            var rock4Prefab = Resources.Load<GameObject>("Artwork/Environment/Rock4");
            var rock6Prefab = Resources.Load<GameObject>("Artwork/Environment/Rock6");
            var rock11Prefab = Resources.Load<GameObject>("Artwork/Environment/Rock11");

            Material wallMat = VisualMaterials.Textured(
                new Color(0.80f, 0.78f, 0.74f), "Rubble", 3f, 0.10f);

            // 1. Ruined Village Hamlet (West sector)
            if (housePrefab != null)
            {
                InstantiateProp(housePrefab, archGroup.transform,
                    new Vector3(-46f, 0f, 18f), Quaternion.Euler(0f, 25f, 0f), Vector3.one * 1.0f);

                InstantiateProp(housePrefab, archGroup.transform,
                    new Vector3(-58f, 0f, 32f), Quaternion.Euler(0f, -35f, 0f), Vector3.one * 1.05f);

                InstantiateProp(housePrefab, archGroup.transform,
                    new Vector3(-52f, 0f, -14f), Quaternion.Euler(0f, 70f, 0f), Vector3.one * 0.95f);
            }
            else
            {
                BuildFallbackRuinWalls(archGroup.transform, wallMat);
            }

            // Broken masonry walls framing the village perimeter
            BuildMasonryWall(archGroup.transform, wallMat, new Vector3(-38f, 0f, 10f), 8f, 2.4f, 18f);
            BuildMasonryWall(archGroup.transform, wallMat, new Vector3(-62f, 0f, 22f), 10f, 2.0f, -40f);
            BuildMasonryWall(archGroup.transform, wallMat, new Vector3(-42f, 0f, 40f), 7f, 2.2f, 65f);

            // 2. Ruined Church on the Hill (North-East elevated ridge)
            if (churchPrefab != null)
            {
                float churchX = 88f;
                float churchZ = 72f;
                float churchY = GetTerrainHeight(churchX, churchZ);
                InstantiateProp(churchPrefab, archGroup.transform,
                    new Vector3(churchX, churchY, churchZ),
                    Quaternion.Euler(0f, 210f, 0f),
                    Vector3.one * 40f);

                // Churchyard stone boundary walls
                BuildMasonryWall(archGroup.transform, wallMat,
                    new Vector3(churchX - 8f, churchY, churchZ - 10f), 12f, 1.6f, -30f);
                BuildMasonryWall(archGroup.transform, wallMat,
                    new Vector3(churchX + 10f, churchY, churchZ - 6f), 10f, 1.4f, 60f);
            }

            // 3. Military Observation Watchtowers
            if (towerPrefab != null)
            {
                // North approach watchtower
                float tw1X = 25f, tw1Z = 92f;
                InstantiateProp(towerPrefab, archGroup.transform,
                    new Vector3(tw1X, GetTerrainHeight(tw1X, tw1Z), tw1Z),
                    Quaternion.Euler(0f, 180f, 0f),
                    Vector3.one * 0.55f);

                // South-west ridge watchtower
                float tw2X = -75f, tw2Z = -75f;
                InstantiateProp(towerPrefab, archGroup.transform,
                    new Vector3(tw2X, GetTerrainHeight(tw2X, tw2Z), tw2Z),
                    Quaternion.Euler(0f, 45f, 0f),
                    Vector3.one * 0.55f);

                // East defense outpost
                float tw3X = 92f, tw3Z = -25f;
                InstantiateProp(towerPrefab, archGroup.transform,
                    new Vector3(tw3X, GetTerrainHeight(tw3X, tw3Z), tw3Z),
                    Quaternion.Euler(0f, -65f, 0f),
                    Vector3.one * 0.55f);
            }

            // 4. Knocked-out Tank Wrecks (Authentic T-26 battle casualties)
            if (wreckPrefab != null)
            {
                // Roadside casualty tilted into shell depression
                InstantiateProp(wreckPrefab, archGroup.transform,
                    new Vector3(-15f, 0f, -16f),
                    Quaternion.Euler(4f, 35f, -6f),
                    Vector3.one * 40f);

                // Knocked out near village defense line
                InstantiateProp(wreckPrefab, archGroup.transform,
                    new Vector3(-38f, 0f, 25f),
                    Quaternion.Euler(0f, 145f, 0f),
                    Vector3.one * 40f);

                // Burnt hull on eastern ridge slope
                float wr3X = 72f, wr3Z = 30f;
                InstantiateProp(wreckPrefab, archGroup.transform,
                    new Vector3(wr3X, GetTerrainHeight(wr3X, wr3Z), wr3Z),
                    Quaternion.Euler(-5f, -40f, 3f),
                    Vector3.one * 40f);
            }

            // 5. Geological 3D Rock Outcrops
            if (rock6Prefab != null && rock4Prefab != null && rock11Prefab != null)
            {
                // East ridge crag
                float r1X = 65f, r1Z = -12f;
                InstantiateProp(rock6Prefab, archGroup.transform,
                    new Vector3(r1X, GetTerrainHeight(r1X, r1Z) - 1.2f, r1Z),
                    Quaternion.Euler(5f, 30f, -5f), Vector3.one * 0.85f);
                InstantiateProp(rock4Prefab, archGroup.transform,
                    new Vector3(r1X - 4f, GetTerrainHeight(r1X - 4f, r1Z - 4f), r1Z - 4f),
                    Quaternion.Euler(0f, 75f, 10f), Vector3.one * 1.3f);
                InstantiateProp(rock11Prefab, archGroup.transform,
                    new Vector3(r1X + 3f, GetTerrainHeight(r1X + 3f, r1Z + 3f), r1Z + 3f),
                    Quaternion.Euler(15f, 120f, 0f), Vector3.one * 1.6f);

                // Church hill crag
                float r2X = 95f, r2Z = 65f;
                InstantiateProp(rock6Prefab, archGroup.transform,
                    new Vector3(r2X, GetTerrainHeight(r2X, r2Z) - 1.5f, r2Z),
                    Quaternion.Euler(-8f, 140f, 4f), Vector3.one * 0.95f);
                InstantiateProp(rock4Prefab, archGroup.transform,
                    new Vector3(r2X - 16f, GetTerrainHeight(r2X - 16f, r2Z + 8f), r2Z + 8f),
                    Quaternion.Euler(12f, 40f, 0f), Vector3.one * 1.4f);

                // West ridge boulders
                float r3X = -68f, r3Z = -35f;
                InstantiateProp(rock4Prefab, archGroup.transform,
                    new Vector3(r3X, GetTerrainHeight(r3X, r3Z), r3Z),
                    Quaternion.Euler(0f, -50f, 15f), Vector3.one * 1.6f);
                InstantiateProp(rock11Prefab, archGroup.transform,
                    new Vector3(r3X - 4f, GetTerrainHeight(r3X - 4f, r3Z - 5f), r3Z - 5f),
                    Quaternion.identity, Vector3.one * 1.9f);

                // North-West valley rocks
                float r4X = -82f, r4Z = 58f;
                InstantiateProp(rock6Prefab, archGroup.transform,
                    new Vector3(r4X, GetTerrainHeight(r4X, r4Z) - 1.0f, r4Z),
                    Quaternion.Euler(0f, 85f, -10f), Vector3.one * 0.8f);
                InstantiateProp(rock4Prefab, archGroup.transform,
                    new Vector3(r4X + 6f, GetTerrainHeight(r4X + 6f, r4Z - 6f), r4Z - 6f),
                    Quaternion.identity, Vector3.one * 1.2f);
            }
        }

        private static void BuildFortifications(Transform parent)
        {
            var fortGroup = new GameObject("Fortifications");
            fortGroup.transform.SetParent(parent, false);

            // 1. Czech Hedgehogs (Welded steel anti-tank obstacles)
            // South road roadblock
            CreateCzechHedgehog(fortGroup.transform, new Vector3(-4.5f, 0.85f, -38f), 15f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(-1.5f, 0.85f, -39f), -20f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(1.8f, 0.85f, -37.5f), 35f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(4.8f, 0.85f, -38.5f), -10f);

            // North road roadblock
            CreateCzechHedgehog(fortGroup.transform, new Vector3(-5.0f, 0.85f, 48f), -30f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(-2.0f, 0.85f, 47f), 25f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(2.2f, 0.85f, 49f), -15f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(5.2f, 0.85f, 47.5f), 40f);

            // Village approach obstacle line
            CreateCzechHedgehog(fortGroup.transform, new Vector3(-28f, 0.85f, 6f), 50f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(-30f, 0.85f, 0f), -15f);
            CreateCzechHedgehog(fortGroup.transform, new Vector3(-32f, 0.85f, -6f), 30f);

            // 2. Dragon's Teeth (Concrete anti-tank pyramids)
            // South-West defense line
            for (int i = 0; i < 6; i++)
            {
                float t = i / 5f;
                Vector3 p = Vector3.Lerp(new Vector3(-42f, 0f, -55f), new Vector3(-26f, 0f, -68f), t);
                p += (i % 2 == 0 ? Vector3.forward : Vector3.back) * 0.6f;
                CreateDragonsTooth(fortGroup.transform, p, i * 22f);
            }

            // North-East defense line
            for (int i = 0; i < 6; i++)
            {
                float t = i / 5f;
                Vector3 p = Vector3.Lerp(new Vector3(24f, 0f, 58f), new Vector3(42f, 0f, 48f), t);
                p += (i % 2 == 0 ? Vector3.forward : Vector3.back) * 0.6f;
                CreateDragonsTooth(fortGroup.transform, p, -i * 18f);
            }

            // 3. Fortified Sandbag Revetments & Firing Pits
            CreateSandbagBunker(fortGroup.transform, new Vector3(6.5f, 0f, -34f), 195f);
            CreateSandbagBunker(fortGroup.transform, new Vector3(26f, 0f, 14f), -45f);
            CreateSandbagBunker(fortGroup.transform, new Vector3(-30f, 0f, 15f), 80f);

            // 4. Artillery Shell Craters
            CreateArtilleryCrater(fortGroup.transform, new Vector3(-13f, 0f, -12f), 4.2f);
            CreateArtilleryCrater(fortGroup.transform, new Vector3(18f, 0f, 22f), 3.8f);
            CreateArtilleryCrater(fortGroup.transform, new Vector3(-24f, 0f, 45f), 4.6f);
            CreateArtilleryCrater(fortGroup.transform, new Vector3(35f, 0f, -28f), 3.9f);
            CreateArtilleryCrater(fortGroup.transform, new Vector3(-50f, 0.5f, 55f), 4.4f);
            CreateArtilleryCrater(fortGroup.transform, new Vector3(55f, 1.2f, -50f), 4.5f);
        }

        private static void BuildVegetation(Transform parent)
        {
            var vegGroup = new GameObject("Vegetation");
            vegGroup.transform.SetParent(parent, false);

            // 1. Multi-tiered 3D Conifer / Pine Trees
            // Perimeter forest belt surrounding the battlefield
            int treeCount = 68;
            for (int i = 0; i < treeCount; i++)
            {
                float a = (float)i / treeCount * Mathf.PI * 2f;
                // Clustered bands at varying radii from 88m to 220m
                float r = 90f + (i % 5) * 26f + Mathf.Sin(i * 3.7f) * 14f;
                float x = Mathf.Cos(a) * r;
                float z = Mathf.Sin(a) * r;
                float y = GetTerrainHeight(x, z);

                float scale = 0.85f + (i % 7) * 0.12f;
                CreatePineTree(vegGroup.transform, new Vector3(x, y, z), scale);
            }

            // Flanking stands near the church hill
            Vector3[] churchStands =
            {
                new Vector3(75f, GetTerrainHeight(75f, 85f), 85f),
                new Vector3(98f, GetTerrainHeight(98f, 82f), 82f),
                new Vector3(105f, GetTerrainHeight(105f, 65f), 65f),
                new Vector3(82f, GetTerrainHeight(82f, 58f), 58f),
                new Vector3(68f, GetTerrainHeight(68f, 72f), 72f)
            };
            foreach (var pos in churchStands)
            {
                CreatePineTree(vegGroup.transform, pos, 1.15f);
            }

            // 2. Artillery-Blasted Tree Stumps in combat zones
            Vector3[] stumpPositions =
            {
                new Vector3(-18f, 0f, -10f), new Vector3(-12f, 0f, -24f),
                new Vector3(14f, 0f, 26f), new Vector3(22f, 0f, 18f),
                new Vector3(-20f, 0f, 40f), new Vector3(-28f, 0f, 50f),
                new Vector3(32f, 0f, -22f), new Vector3(38f, 0f, -34f),
                new Vector3(-35f, 0f, 10f), new Vector3(-42f, 0f, -5f),
                new Vector3(28f, 0f, -10f), new Vector3(16f, 0f, -42f),
                new Vector3(-8f, 0f, 32f), new Vector3(8f, 0f, -20f)
            };

            for (int i = 0; i < stumpPositions.Length; i++)
            {
                float h = 1.6f + (i % 4) * 0.45f;
                CreateBlastedStump(vegGroup.transform, stumpPositions[i], h);
            }
        }

        private static void BuildAtmosphere(Transform parent)
        {
            var atmosGroup = new GameObject("Atmosphere");
            atmosGroup.transform.SetParent(parent, false);

            // Rising billowing smoke columns from the knocked-out tank wrecks
            CreateSmokePlume(atmosGroup.transform, new Vector3(-15f, 1.2f, -16f), 18f, 32f);
            CreateSmokePlume(atmosGroup.transform, new Vector3(-38f, 1.2f, 25f), 16f, 28f);

            float wr3X = 72f, wr3Z = 30f;
            CreateSmokePlume(atmosGroup.transform,
                new Vector3(wr3X, GetTerrainHeight(wr3X, wr3Z) + 1.2f, wr3Z), 20f, 36f);
        }

        // ==================== Fortification & Scenery Builders ====================

        private static void CreateCzechHedgehog(Transform parent, Vector3 pos, float rotY)
        {
            var go = new GameObject("CzechHedgehog");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);

            Material steel = VisualMaterials.Metal(
                new Color(0.20f, 0.19f, 0.18f), 0.30f, 0.85f);

            // Three crossed angled I-beams welded at 90-degree offsets
            CreateHedgehogBeam(go.transform, steel, Quaternion.Euler(45f, 0f, 0f));
            CreateHedgehogBeam(go.transform, steel, Quaternion.Euler(-45f, 90f, 0f));
            CreateHedgehogBeam(go.transform, steel, Quaternion.Euler(0f, 45f, 45f));
            go.isStatic = true;
        }

        private static void CreateHedgehogBeam(Transform parent, Material mat, Quaternion rot)
        {
            var beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            beam.name = "SteelBeam";
            Object.Destroy(beam.GetComponent<Collider>());
            beam.transform.SetParent(parent, false);
            beam.transform.localRotation = rot;
            beam.transform.localScale = new Vector3(0.14f, 1.85f, 0.14f);
            beam.GetComponent<Renderer>().sharedMaterial = mat;
        }

        private static void CreateDragonsTooth(Transform parent, Vector3 pos, float rotY)
        {
            var go = new GameObject("DragonsTooth");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);
            go.transform.localScale = new Vector3(1.3f, 1.15f, 1.3f);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = GetUnitPyramidMesh();

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = VisualMaterials.Textured(
                new Color(0.68f, 0.65f, 0.62f), "Rubble", 1.8f, 0.08f);
            go.isStatic = true;
        }

        private static void CreateSandbagBunker(Transform parent, Vector3 pos, float rotY)
        {
            var bunker = new GameObject("SandbagBunker");
            bunker.transform.SetParent(parent, false);
            bunker.transform.localPosition = pos;
            bunker.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);

            Material sandbagMat = VisualMaterials.Matte(new Color(0.44f, 0.38f, 0.27f), 0.06f);

            // U-shaped fortified sandbag revetment (3 stacked layers)
            int arcBags = 7;
            float radius = 3.2f;
            for (int layer = 0; layer < 3; layer++)
            {
                float y = 0.22f + layer * 0.30f;
                float offsetAngle = (layer % 2 == 0) ? 0f : 0.12f;

                for (int i = 0; i < arcBags; i++)
                {
                    float frac = (float)i / (arcBags - 1);
                    float a = Mathf.Lerp(-1.2f, 1.2f, frac) + offsetAngle;

                    var bag = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    bag.name = $"Sandbag_L{layer}_{i}";
                    Object.Destroy(bag.GetComponent<Collider>());
                    bag.transform.SetParent(bunker.transform, false);
                    bag.transform.localPosition = new Vector3(
                        Mathf.Sin(a) * radius, y, Mathf.Cos(a) * radius);
                    bag.transform.localRotation = Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f);
                    bag.transform.localScale = new Vector3(1.15f, 0.55f, 0.75f);
                    bag.GetComponent<Renderer>().sharedMaterial = sandbagMat;
                }
            }
            bunker.isStatic = true;
        }

        private static void CreateArtilleryCrater(Transform parent, Vector3 center, float radius)
        {
            var crater = new GameObject("ArtilleryCrater");
            crater.transform.SetParent(parent, false);
            crater.transform.localPosition = center;

            // 1. Scorched earth blast center disc
            var centerDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            centerDisc.name = "CraterScorch";
            Object.Destroy(centerDisc.GetComponent<Collider>());
            centerDisc.transform.SetParent(crater.transform, false);
            centerDisc.transform.localPosition = new Vector3(0f, 0.015f, 0f);
            centerDisc.transform.localScale = new Vector3(radius * 2f, 0.02f, radius * 2f);
            centerDisc.GetComponent<Renderer>().sharedMaterial =
                VisualMaterials.Matte(new Color(0.12f, 0.10f, 0.09f), 0.04f);

            // 2. Raised circular excavated dirt rim mounds
            Material rimMat = VisualMaterials.Textured(
                new Color(0.55f, 0.48f, 0.38f), "Ground", 4f, 0.04f);

            int rimSegments = 10;
            for (int i = 0; i < rimSegments; i++)
            {
                float a = (float)i / rimSegments * Mathf.PI * 2f;
                float moundR = radius * 0.95f;

                var mound = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                mound.name = $"CraterRim_{i}";
                Object.Destroy(mound.GetComponent<Collider>());
                mound.transform.SetParent(crater.transform, false);
                mound.transform.localPosition = new Vector3(
                    Mathf.Cos(a) * moundR, 0.22f, Mathf.Sin(a) * moundR);
                mound.transform.localRotation = Quaternion.Euler(0f, a * Mathf.Rad2Deg, 0f);
                mound.transform.localScale = new Vector3(1.8f, 0.45f, 1.3f);
                mound.GetComponent<Renderer>().sharedMaterial = rimMat;
            }
            crater.isStatic = true;
        }

        private static void CreatePineTree(Transform parent, Vector3 pos, float scale)
        {
            var tree = new GameObject("PineTree");
            tree.transform.SetParent(parent, false);
            tree.transform.localPosition = pos;

            float seed = RandomHash(pos.x, pos.z);
            float yaw = seed * 360f;
            float tiltX = (RandomHash(pos.z, pos.x) - 0.5f) * 6f;
            float tiltZ = (RandomHash(pos.x + 13f, pos.z - 7f) - 0.5f) * 6f;
            tree.transform.localRotation = Quaternion.Euler(tiltX, yaw, tiltZ);
            tree.transform.localScale = Vector3.one * scale;

            Material trunkMat = VisualMaterials.WithGroundDetail(
                VisualMaterials.Matte(new Color(0.24f, 0.17f, 0.11f), 0.05f), 2.5f);

            // Varied needle canopy colors across the forest (spruce green, deep pine, dark olive)
            float greenShift = (seed - 0.5f) * 0.06f;
            Color needleColor = new Color(
                0.08f + greenShift * 0.5f,
                0.19f + greenShift,
                0.08f + greenShift * 0.3f
            );
            Material leafMat = VisualMaterials.Matte(needleColor, 0.12f);

            // Trunk: textured bark cylinder
            float trunkH = 2.4f + seed * 1.0f;
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            Object.Destroy(trunk.GetComponent<Collider>());
            trunk.transform.SetParent(tree.transform, false);
            trunk.transform.localPosition = new Vector3(0f, trunkH * 0.5f, 0f);
            trunk.transform.localScale = new Vector3(0.34f, trunkH * 0.5f, 0.34f);
            trunk.GetComponent<Renderer>().sharedMaterial = trunkMat;

            // Tiered conical branch canopies with subtle rotation and tier offsets
            int tiers = 4 + (seed > 0.6f ? 1 : 0);
            float baseTierY = 2.0f;
            float topTierY = trunkH + 3.8f;

            Mesh coneMesh = GetUnitConeMesh();
            for (int t = 0; t < tiers; t++)
            {
                float frac = (float)t / (tiers - 1);
                float y = Mathf.Lerp(baseTierY, topTierY, frac);
                float r = Mathf.Lerp(3.0f, 0.8f, frac);
                float h = Mathf.Lerp(2.2f, 1.4f, frac);

                // Alternating rotational offsets per tier so branch silhouettes don't align identically
                float tierYaw = t * 37f + seed * 45f;
                var coneGo = new GameObject($"PineCanopy_T{t}");
                coneGo.transform.SetParent(tree.transform, false);
                coneGo.transform.localPosition = new Vector3(0f, y, 0f);
                coneGo.transform.localRotation = Quaternion.Euler(0f, tierYaw, 0f);
                coneGo.transform.localScale = new Vector3(r, h, r);

                var mf = coneGo.AddComponent<MeshFilter>();
                mf.sharedMesh = coneMesh;

                var mr = coneGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = leafMat;
            }
            tree.isStatic = true;
        }

        private static void CreateBlastedStump(Transform parent, Vector3 pos, float height)
        {
            var stump = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stump.name = "BlastedTreeStump";
            Object.Destroy(stump.GetComponent<Collider>());
            stump.transform.SetParent(parent, false);
            stump.transform.localPosition = pos + Vector3.up * (height * 0.5f);
            stump.transform.localRotation = Quaternion.Euler(
                Mathf.Sin(pos.x) * 6f, pos.x * 23f, Mathf.Cos(pos.z) * 6f);
            stump.transform.localScale = new Vector3(0.42f, height * 0.5f, 0.38f);

            Material stumpMat = VisualMaterials.Matte(
                new Color(0.16f, 0.13f, 0.10f), 0.04f);
            stump.GetComponent<Renderer>().sharedMaterial = stumpMat;
            stump.isStatic = true;
        }

        private static void CreateSmokePlume(Transform parent, Vector3 basePos, float maxSpread, float maxHeight)
        {
            var plume = new GameObject("SmokePlume");
            plume.transform.SetParent(parent, false);
            plume.transform.localPosition = basePos;

            int puffCount = 8;
            for (int i = 0; i < puffCount; i++)
            {
                float t = (float)i / (puffCount - 1);
                float y = Mathf.Lerp(0.8f, maxHeight, t);
                float size = Mathf.Lerp(2.2f, maxSpread, Mathf.Sqrt(t));

                // Natural downwind drift (+X, +Z)
                float driftX = t * 7.5f + Mathf.Sin(i * 1.5f) * 1.2f;
                float driftZ = t * 5.0f + Mathf.Cos(i * 1.5f) * 1.2f;

                var puff = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                puff.name = $"SmokePuff_{i}";
                Object.Destroy(puff.GetComponent<Collider>());
                puff.transform.SetParent(plume.transform, false);
                puff.transform.localPosition = new Vector3(driftX, y, driftZ);
                puff.transform.localScale = new Vector3(size * 1.15f, size * 0.85f, size * 1.15f);

                // Dark sooty black at base, diffusing to atmospheric haze at high altitude
                float gray = Mathf.Lerp(0.14f, 0.48f, t);
                float alpha = Mathf.Lerp(0.75f, 0.18f, t);
                Color smokeColor = new Color(gray, gray * 0.98f, gray * 0.95f, alpha);

                Material smokeMat = VisualMaterials.Matte(smokeColor, 0.01f);
                VisualMaterials.MakeTransparent(smokeMat);
                puff.GetComponent<Renderer>().sharedMaterial = smokeMat;
            }
            plume.isStatic = true;
        }

        private static void BuildMasonryWall(Transform parent, Material mat,
            Vector3 center, float length, float height, float rotY)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "MasonryWall";
            Object.Destroy(wall.GetComponent<Collider>());
            wall.transform.SetParent(parent, false);
            wall.transform.localPosition = center + Vector3.up * (height * 0.5f);
            wall.transform.localRotation = Quaternion.Euler(0f, rotY, 0f);
            wall.transform.localScale = new Vector3(0.9f, height, length);
            wall.GetComponent<Renderer>().sharedMaterial = mat;
            wall.isStatic = true;
        }

        private static void BuildFallbackRuinWalls(Transform parent, Material mat)
        {
            Vector3[] spots =
            {
                new Vector3(-22f, 0f, -30f), new Vector3(24f, 0f, -48f),
                new Vector3(-26f, 0f, 18f), new Vector3(26f, 0f, 34f),
                new Vector3(-20f, 0f, 62f), new Vector3(22f, 0f, 72f),
            };

            for (int i = 0; i < spots.Length; i++)
            {
                BuildMasonryWall(parent, mat, spots[i], 10f, 4f, i * 23f);
            }
        }

        private static Material _wreckMat;
        private static Material _towerMat;
        private static Material _houseMat;
        private static Material _churchMat;
        private static Material _rockMat;

        private static void EnsureValidPropMaterials(GameObject inst, string prefabName)
        {
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var curMat = r.sharedMaterial;
                bool isMissing = curMat == null ||
                                 curMat.shader == null ||
                                 curMat.shader.name == "Hidden/InternalErrorShader" ||
                                 curMat.shader.name == "";

                if (!isMissing) continue;

                if (prefabName.Contains("TankWreck"))
                {
                    if (_wreckMat == null)
                    {
                        var tex = Resources.Load<Texture2D>("Artwork/Environment/Textures/T-26_AM");
                        _wreckMat = VisualMaterials.Lit(new Color(0.38f, 0.35f, 0.32f), 0.25f, 0.65f);
                        _wreckMat.name = "TankWreck_FallbackMat";
                        if (tex != null) _wreckMat.mainTexture = tex;
                    }
                    r.sharedMaterial = _wreckMat;
                }
                else if (prefabName.Contains("Watchtower"))
                {
                    if (_towerMat == null)
                    {
                        _towerMat = VisualMaterials.Lit(new Color(0.26f, 0.23f, 0.20f), 0.20f, 0.40f);
                        _towerMat.name = "Watchtower_FallbackMat";
                    }
                    r.sharedMaterial = _towerMat;
                }
                else if (prefabName.Contains("RuinedHouse"))
                {
                    if (_houseMat == null)
                    {
                        var tex = Resources.Load<Texture2D>("Artwork/Environment/Textures/vhouse_diff");
                        _houseMat = VisualMaterials.Textured(new Color(0.88f, 0.86f, 0.82f), "Rubble", 3.0f, 0.10f);
                        _houseMat.name = "RuinedHouse_FallbackMat";
                        if (tex != null) _houseMat.mainTexture = tex;
                    }
                    r.sharedMaterial = _houseMat;
                }
                else if (prefabName.Contains("RuinedChurch"))
                {
                    if (_churchMat == null)
                    {
                        var tex = Resources.Load<Texture2D>("Artwork/Environment/Textures/blr205_Church");
                        _churchMat = VisualMaterials.Textured(new Color(0.82f, 0.80f, 0.76f), "Rubble", 3.0f, 0.12f);
                        _churchMat.name = "RuinedChurch_FallbackMat";
                        if (tex != null) _churchMat.mainTexture = tex;
                    }
                    r.sharedMaterial = _churchMat;
                }
                else if (prefabName.Contains("Rock"))
                {
                    if (_rockMat == null)
                    {
                        _rockMat = VisualMaterials.Textured(new Color(0.58f, 0.56f, 0.52f), "Rubble", 2.0f, 0.08f);
                        _rockMat.name = "Rock_FallbackMat";
                    }
                    r.sharedMaterial = _rockMat;
                }
                else
                {
                    r.sharedMaterial = VisualMaterials.Lit(new Color(0.5f, 0.5f, 0.5f), 0.2f, 0.2f);
                }
            }
        }

        private static void InstantiateProp(GameObject prefab, Transform parent,
            Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var inst = Object.Instantiate(prefab, parent);
            inst.transform.localPosition = pos;
            inst.transform.localRotation = rot;
            inst.transform.localScale = scale;

            EnsureValidPropMaterials(inst, prefab.name);

            // Strip any colliders so vehicle physics & mouse aiming remain pristine
            foreach (var c in inst.GetComponentsInChildren<Collider>(true))
            {
                Object.Destroy(c);
            }
            inst.isStatic = true;
        }

        private static float RandomHash(float x, float z)
        {
            unchecked
            {
                int ix = Mathf.FloorToInt(x * 137.5f);
                int iz = Mathf.FloorToInt(z * 293.7f);
                int h = ix * 374761393 + iz * 668265263;
                h = (h ^ (h >> 13)) * 1274126177;
                return (h & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        // ==================== Shared Mesh Generators ====================

        private static Mesh _coneMesh;
        private static Mesh GetUnitConeMesh()
        {
            if (_coneMesh != null) return _coneMesh;

            var mesh = new Mesh { name = "UnitCone" };
            int segments = 10;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();

            // Apex
            verts.Add(new Vector3(0f, 1f, 0f));
            uvs.Add(new Vector2(0.5f, 1f));

            // Base rim
            for (int i = 0; i <= segments; i++)
            {
                float a = (float)i / segments * Mathf.PI * 2f;
                verts.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                uvs.Add(new Vector2((Mathf.Cos(a) + 1f) * 0.5f, (Mathf.Sin(a) + 1f) * 0.5f));
            }

            // Side triangles
            for (int i = 1; i <= segments; i++)
            {
                tris.Add(0);
                tris.Add(i + 1);
                tris.Add(i);
            }

            // Base center
            int baseCenterIdx = verts.Count;
            verts.Add(Vector3.zero);
            uvs.Add(new Vector2(0.5f, 0.5f));

            // Base disc triangles
            for (int i = 1; i <= segments; i++)
            {
                tris.Add(baseCenterIdx);
                tris.Add(i);
                tris.Add(i + 1);
            }

            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _coneMesh = mesh;
            return mesh;
        }

        private static Mesh _pyramidMesh;
        private static Mesh GetUnitPyramidMesh()
        {
            if (_pyramidMesh != null) return _pyramidMesh;

            var mesh = new Mesh { name = "UnitPyramid" };
            Vector3[] baseV = {
                new Vector3(-0.6f, 0f, -0.6f),
                new Vector3( 0.6f, 0f, -0.6f),
                new Vector3( 0.6f, 0f,  0.6f),
                new Vector3(-0.6f, 0f,  0.6f)
            };
            Vector3[] topV = {
                new Vector3(-0.18f, 1f, -0.18f),
                new Vector3( 0.18f, 1f, -0.18f),
                new Vector3( 0.18f, 1f,  0.18f),
                new Vector3(-0.18f, 1f,  0.18f)
            };

            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();

            // 4 side faces
            for (int i = 0; i < 4; i++)
            {
                int next = (i + 1) % 4;
                int idx = verts.Count;
                verts.Add(baseV[i]);
                verts.Add(baseV[next]);
                verts.Add(topV[next]);
                verts.Add(topV[i]);

                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(1f, 0f));
                uvs.Add(new Vector2(1f, 1f));
                uvs.Add(new Vector2(0f, 1f));

                tris.Add(idx); tris.Add(idx + 2); tris.Add(idx + 1);
                tris.Add(idx); tris.Add(idx + 3); tris.Add(idx + 2);
            }

            // Top face
            int topIdx = verts.Count;
            verts.Add(topV[0]); verts.Add(topV[1]); verts.Add(topV[2]); verts.Add(topV[3]);
            uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f)); uvs.Add(new Vector2(0f, 1f));
            tris.Add(topIdx); tris.Add(topIdx + 2); tris.Add(topIdx + 1);
            tris.Add(topIdx); tris.Add(topIdx + 3); tris.Add(topIdx + 2);

            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _pyramidMesh = mesh;
            return mesh;
        }

        // ==================== Lighting & Camera ====================

        private const float SunElevationDeg = 27f;
        private const float SunAzimuthDeg = 142f;

        public static GameObject BuildLighting(Transform parent)
        {
            var rig = new GameObject("Lighting");
            rig.transform.SetParent(parent, false);

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
            // Slightly reduced fog density allows the distant 3D hills and mountain ridges to stand out beautifully
            RenderSettings.fogDensity = 0.0036f;
            RenderSettings.fogColor = new Color(0.55f, 0.58f, 0.62f);

            Material sky = VisualMaterials.BuildSkybox();
            if (sky != null) RenderSettings.skybox = sky;

            Light sun = new GameObject("Sun").AddComponent<Light>();
            sun.transform.SetParent(rig.transform, false);
            sun.type = LightType.Directional;
            sun.transform.rotation = Quaternion.Euler(SunElevationDeg, SunAzimuthDeg, 0f);
            sun.color = sunTint;
            sun.intensity = 1.15f;

            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.78f;
            sun.shadowBias = 0.02f;
            sun.shadowNormalBias = 0.35f;

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
            // Increased to 1000m so the distant mountain backdrop is fully visible
            camera.farClipPlane = 1000f;

            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = new Color(0.05f, 0.07f, 0.12f);

            QualitySettings.antiAliasing = 4;
            go.AddComponent<AudioListener>();
            return go;
        }
    }
}