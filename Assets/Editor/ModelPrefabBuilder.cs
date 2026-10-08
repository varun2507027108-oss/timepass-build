using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ModelPrefabBuilder
{
    [System.Serializable]
    private class NodeEntry
    {
        public string name;
        public string parent;
        public string mesh;
        public int tris;
        public float[] pos;
        public float[] rot;
        public float[] scl;
    }

    [System.Serializable]
    private class Manifest
    {
        public string source;
        public NodeEntry[] nodes;
    }

    private const string SourceDir = "Assets/Artwork/SourceModels/PanzerIV";
    private const string OutputPrefab = "Assets/Resources/Artwork/Models/PanzerIV/PanzerIV.prefab";

    /// <summary>
    /// Rebuilds the Panzer IV as a Unity prefab from exported OBJ parts plus a
    /// sidecar manifest.
    ///
    /// Needed because this Unity install has no glTF importer: it stamps
    /// <c>DefaultImporter</c> on a .glb, so <c>Resources.Load</c> returns null and
    /// TankView quietly falls back to its procedural rig. OBJ imports everywhere,
    /// but OBJ cannot express scene hierarchy, and the hierarchy is the whole point
    /// here: the empty pivot nodes <c>turret_low</c> and <c>gun_002_low</c> are what
    /// let TankView traverse the turret and elevate the gun by rotating transforms
    /// that already exist in the file.
    ///
    /// Reconstructing the tree from the manifest keeps those pivots, so no mesh
    /// editing or manual pivot placement is needed.
    /// </summary>
    public static void Build()
    {
        string manifestPath = Path.Combine(SourceDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Debug.LogError($"[ModelPrefabBuilder] missing manifest at {manifestPath}");
            return;
        }

        Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
        if (manifest == null || manifest.nodes == null || manifest.nodes.Length == 0)
        {
            Debug.LogError("[ModelPrefabBuilder] manifest parsed to nothing");
            return;
        }

        // Prove the deserialiser actually populated the transform arrays. A silently
        // empty float[] leaves every node at identity, which stacks the whole tank on
        // the origin and reports bounds of zero with no error anywhere.
        int withPos = 0;
        foreach (NodeEntry n in manifest.nodes)
        {
            if (n.pos != null && n.pos.Length == 3) withPos++;
        }
        Debug.Log($"[ModelPrefabBuilder] manifest nodes={manifest.nodes.Length} with pos[]={withPos}");
        if (withPos == 0)
        {
            Debug.LogError("[ModelPrefabBuilder] no node has a populated pos[]; deserialisation failed");
            return;
        }

        var created = new Dictionary<string, Transform>();
        int missingMeshes = 0;

        // Two passes: create every transform first so a child can always find its
        // parent regardless of the order the manifest happens to list them in.
        for (int i = 0; i < manifest.nodes.Length; i++)
        {
            var go = new GameObject(manifest.nodes[i].name);
            created[manifest.nodes[i].name] = go.transform;
        }

        for (int i = 0; i < manifest.nodes.Length; i++)
        {
            NodeEntry entry = manifest.nodes[i];
            Transform node = created[entry.name];

            if (!string.IsNullOrEmpty(entry.parent) && created.TryGetValue(entry.parent, out Transform parent))
            {
                node.SetParent(parent, false);
            }

            // The manifest carries TRS rather than a 4x4 matrix. glTF stores rotation
            // as a quaternion in x,y,z,w order, which is the same order Unity's
            // Quaternion constructor takes, so there is no axis permutation to get
            // wrong here -- which is the failure that produces a tank at 100x scale or
            // lying on its side.
            if (entry.pos != null && entry.pos.Length == 3)
            {
                node.localPosition = new Vector3(entry.pos[0], entry.pos[1], entry.pos[2]);
            }
            if (entry.rot != null && entry.rot.Length == 4)
            {
                node.localRotation = new Quaternion(entry.rot[0], entry.rot[1], entry.rot[2], entry.rot[3]);
            }
            if (entry.scl != null && entry.scl.Length == 3)
            {
                Vector3 s = new Vector3(entry.scl[0], entry.scl[1], entry.scl[2]);
                // Sketchfab FBX exports insert dummy wrapper nodes with scale 0.01 and 0.0001,
                // which scales the entire vehicle by 10^-6. Normalize them to 1.
                if (entry.name == "Sketchfab_model" || entry.name.EndsWith(".fbx"))
                {
                    s = Vector3.one;
                }
                node.localScale = s;
            }

            if (string.IsNullOrEmpty(entry.mesh)) continue;

            string meshPath = $"{SourceDir}/{entry.mesh}";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh == null)
            {
                missingMeshes++;
                continue;
            }

            // No axis conversion here. glTF is Y-up and trimesh loaded and re-emitted
            // the vertices unchanged, so the OBJ is Y-up too and matches the manifest's
            // Y-up node matrices. A rotation would break the pivots instead of fixing
            // anything.
            node.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            node.gameObject.AddComponent<MeshRenderer>();
        }

        if (missingMeshes > 0)
        {
            Debug.LogError(
                $"[ModelPrefabBuilder] {missingMeshes} meshes failed to load. " +
                "The OBJ assets may not have finished importing.");
            return;
        }

        // Reparent gun parts under gun_002_low so elevation pitches barrel and muzzle together
        if (created.TryGetValue("gun_002_low", out Transform gunPivot))
        {
            foreach (string part in new[] { "barrel_low", "muzzle_low", "mount_low" })
            {
                if (created.TryGetValue(part, out Transform partT))
                {
                    ReparentExplicit(partT, gunPivot);
                }
            }
        }

        // Reparent gun_002_low and all turret components under turret_low so yaw rotates entire turret
        if (created.TryGetValue("turret_low", out Transform turretPivot))
        {
            foreach (string part in new[] {
                "gun_002_low",
                "commanderCupola_low",
                "window_003_low",
                "window_004_low",
                "hatch_002_low",
                "hinge_004_low",
                "handleBracket_low"
            })
            {
                if (created.TryGetValue(part, out Transform partT))
                {
                    ReparentExplicit(partT, turretPivot);
                }
            }
        }

        created.TryGetValue("turret_low", out Transform probe);
        Debug.Log(
            $"[ModelPrefabBuilder] created={created.Count} " +
            $"turret_low.localPosition={probe?.localPosition} " +
            $"localRotation={probe?.localRotation} " +
            $"parent={(probe?.parent != null ? probe.parent.name : "<none>")}");

        // Root everything under a single holder so the prefab has one top object.
        var root = new GameObject("PanzerIV");
        Transform sceneRoot = null;
        foreach (Transform t in created.Values)
        {
            if (t.parent == null && t != root.transform) { sceneRoot = t; break; }
        }
        if (sceneRoot != null) sceneRoot.SetParent(root.transform, false);

        // The ground-contact pivot the model sits on, so TankView can align it.
        var ground = new GameObject("GroundOrigin");
        ground.transform.SetParent(root.transform, false);

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPrefab));
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, OutputPrefab);

        // Verify orientation and pivots before anyone ships a tank lying on its side.
        // A Panzer IV is ~6.4 m long, ~2.9 m wide, ~2.5 m tall, and its turret ring
        // sits roughly 1.7 m up. If those numbers are transposed the OBJ came out in a
        // different axis convention than assumed.
        var bounds = new Bounds();
        bool first = true;
        int renderers = 0;
        int verts = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            renderers++;
            var mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) verts += mf.sharedMesh.vertexCount;
            if (first) { bounds = r.bounds; first = false; }
            else bounds.Encapsulate(r.bounds);
        }
        Debug.Log($"[ModelPrefabBuilder] renderers={renderers} totalVerts={verts}");

        Vector3 size = bounds.size;
        Debug.Log(
            $"[ModelPrefabBuilder] wrote {OutputPrefab} from {manifest.nodes.Length} nodes\n" +
            $"[ModelPrefabBuilder] bounds size = {size.x:F2} x {size.y:F2} x {size.z:F2} " +
            $"(expect ~2.85 wide x 2.56 tall x 6.40 long)\n" +
            $"[ModelPrefabBuilder] bounds centre = ({bounds.center.x:F2}, {bounds.center.y:F2}, {bounds.center.z:F2})");

        foreach (string pivot in new[] { "turret_low", "gun_002_low", "muzzle_low_turret_0" })
        {
            Transform t = FindDeep(root.transform, pivot);
            Debug.Log(t == null
                ? $"[ModelPrefabBuilder] PIVOT MISSING: {pivot}"
                : $"[ModelPrefabBuilder] pivot {pivot} at ({t.position.x:F2}, {t.position.y:F2}, {t.position.z:F2})");
        }

        Object.DestroyImmediate(root);

        Debug.Log($"[ModelPrefabBuilder] wrote {OutputPrefab} from {manifest.nodes.Length} nodes");
        if (prefab == null) Debug.LogError("[ModelPrefabBuilder] SaveAsPrefabAsset returned null");
    }

    private static void ReparentExplicit(Transform child, Transform parent)
    {
        Vector3 childPos = child.localPosition;
        Quaternion childRot = child.localRotation;
        Vector3 childScl = child.localScale;

        Vector3 parentPos = parent.localPosition;
        Quaternion parentRot = parent.localRotation;

        child.SetParent(parent, false);
        child.localPosition = Quaternion.Inverse(parentRot) * (childPos - parentPos);
        child.localRotation = Quaternion.Inverse(parentRot) * childRot;
        child.localScale = childScl;
    }

    /// <summary>glTF matrices are column-major; Unity's Matrix4x4 wants row-major.</summary>
    private static Matrix4x4 ToMatrix(float[] m)
    {
        return new Matrix4x4(
            new Vector4(m[0], m[4], m[8], m[12]),
            new Vector4(m[1], m[5], m[9], m[13]),
            new Vector4(m[2], m[6], m[10], m[14]),
            new Vector4(m[3], m[7], m[11], m[15]));
    }

    /// <summary>
    /// Builds the prefab from the Editor. This is the supported path.
    ///
    /// Batchmode does not work here: transforms set on freshly created GameObjects
    /// are never flushed to the native side, so their managed getters return the
    /// values that were assigned while the native transforms -- which is what
    /// prefab serialisation actually reads -- stay at identity. The build reports
    /// correct localPositions, no warning is raised, and the resulting prefab is
    /// silently identity-transformed. Running interactively, where the scene is
    /// updated between operations, serialises correctly.
    /// </summary>
    [MenuItem("WarArena/Rebuild Panzer IV Prefab", false, 20)]
    public static void BuildFromMenu()
    {
        Build();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Verify();
        Debug.Log("[ModelPrefabBuilder] done. Check the Console for the verification numbers.");
    }

    [MenuItem("WarArena/Verify Panzer IV Prefab", false, 21)]
    public static void VerifyFromMenu()
    {
        Verify();
    }

    /// <summary>
    /// Verifies the saved prefab in a fresh editor session.
    ///
    /// Separate from Build on purpose. Reading <c>Transform.position</c> or
    /// <c>Renderer.bounds</c> on GameObjects created inside a single-shot
    /// <c>-executeMethod</c> call returns identity, because those objects have not been
    /// through a scene update and their local-to-world matrix is still unset. That made
    /// a correctly built prefab report bounds of exactly zero and every pivot at the
    /// origin. World positions are therefore accumulated by hand from the local TRS
    /// chain, which does not depend on Unity having synced anything.
    /// </summary>
    public static void Verify()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPrefab);
        if (prefab == null)
        {
            Debug.LogError($"[ModelPrefabBuilder] no prefab at {OutputPrefab}");
            return;
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

        Debug.Log("[ModelPrefabBuilder] ---- verification ----");

        foreach (string pivot in new[] { "turret_low", "gun_002_low", "barrel_low", "muzzle_low_turret_0" })
        {
            Transform t = FindDeep(instance.transform, pivot);
            if (t == null)
            {
                Debug.Log($"  pivot {pivot,-22} MISSING");
                continue;
            }
            Vector3 w = AccumulateWorld(t, Vector3.zero);
            Debug.Log($"  pivot {pivot,-22} world = ({w.x:F3}, {w.y:F3}, {w.z:F3})");
        }

        var bounds = new Bounds();
        bool first = true;
        int renderers = 0;
        int tris = 0;
        foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            renderers++;
            tris += mf.sharedMesh.triangles.Length / 3;

            // A mesh's world AABB is the AABB of its eight transformed corners.
            // Transforming only the centre would shrink the box to nothing on any
            // rotated node, which is every node in this model.
            Bounds mb = mf.sharedMesh.bounds;
            Vector3 c = mb.center;
            Vector3 e = mb.extents;
            Bounds wb = new Bounds(AccumulateWorld(mf.transform, c), Vector3.zero);
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        wb.Encapsulate(AccumulateWorld(
                            mf.transform, c + new Vector3(sx * e.x, sy * e.y, sz * e.z)));
                    }
                }
            }

            if (first) { bounds = wb; first = false; }
            else bounds.Encapsulate(wb);
        }

        Debug.Log($"  renderers = {renderers}, triangles = {tris:N0}");
        Debug.Log($"  bounds    = {bounds.size.x:F2} x {bounds.size.y:F2} x {bounds.size.z:F2} " +
                  "(expect ~2.85 wide x 2.56 tall x 6.40 long)");
        Debug.Log($"  min       = ({bounds.min.x:F2}, {bounds.min.y:F2}, {bounds.min.z:F2}) " +
                  "(expect min.y ~ 0, i.e. sitting on the ground)");

        Object.DestroyImmediate(instance);
    }

    /// <summary>World position of a point given in <paramref name="t"/>'s local space.</summary>
    private static Vector3 AccumulateWorld(Transform t, Vector3 localPoint)
    {
        Vector3 p = Vector3.Scale(localPoint, t.localScale);
        p = t.localRotation * p + t.localPosition;

        Transform cur = t.parent;
        while (cur != null)
        {
            p = Vector3.Scale(p, cur.localScale);
            p = cur.localRotation * p + cur.localPosition;
            cur = cur.parent;
        }
        return p;
    }

    private static Vector3 AccumulateLossyScale(Transform t)
    {
        Vector3 s = Vector3.one;
        Transform cur = t;
        while (cur != null)
        {
            s = Vector3.Scale(s, cur.localScale);
            cur = cur.parent;
        }
        return s;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = FindDeep(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }
}
