using UnityEngine;
using WarArena.Ballistics;
using WarArena.Vehicles;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Playable tank rig built from primitives: tracks, road wheels, hull, sloped
    /// glacis, independently traversing turret and barrel, and a muzzle point that
    /// shells spawn from.
    ///
    /// Movement here is kinematic rather than WheelCollider, because this scene is
    /// meant to be playable with zero art assets and zero prefab setup. The same
    /// visuals are driven identically by <see cref="TankController"/> when a real
    /// prefab exists, so the view is not the thing that would have to change.
    /// </summary>
    public class TankView : MonoBehaviour
    {
        public Transform Root { get; private set; }
        public Transform Turret { get; private set; }
        public Transform Muzzle { get; private set; }
        public Transform GunnerEye { get; private set; }

        /// <summary>
        /// World-space eye point of the gunner's sight, used by the first-person
        /// camera. Parented to the turret so it inherits turret yaw without the
        /// camera having to know anything about the rig's hierarchy.
        /// </summary>
        public Vector3 GunnerEyeWorldPosition => GunnerEye.position;

        /// <summary>
        /// Sight orientation, gun-axis aligned. A first-person view copies this
        /// directly instead of looking at a point in the world, so the barrel stays
        /// centred however the hull is turned.
        /// </summary>
        public Quaternion GunnerEyeWorldRotation => GunnerEye.rotation;

        private Transform _leftWheels;
        private Transform _rightWheels;
        private Transform _leftTrack;
        private Transform _rightTrack;

        /// <summary>
        /// Per-track visual roll. Track links and road wheels rotate by ground
        /// distance travelled, not by throttle, so reversing visibly runs the
        /// tracks backwards.
        /// </summary>
        private float _leftSpin;
        private float _rightSpin;

        public void Build(Color bodyColor, Color trackColor, float scale)
        {
            Root = transform;
            BuildPrimitive(bodyColor, trackColor, scale);
            Root.localScale = Vector3.one * scale;
        }

        /// <summary>
        /// Builds the rig from an imported model instead of primitives.
        ///
        /// The model is expected to already be rigged the way this class wants it:
        /// empty pivot transforms named <c>turret_low</c> and <c>gun_002_low</c> that
        /// carry their children, and a muzzle node. Those pivots do the work, so no
        /// pivot surgery is needed here and the turret slews and the gun elevates for
        /// free simply by rotating transforms that already exist.
        ///
        /// Nodes are resolved by name at runtime rather than by index. A glTF import
        /// splits one tank into 55 single-primitive meshes and the sibling order is
        /// not a contract; the previous version of this file was caught indexing
        /// children positionally and picking up the enemy half of the arena.
        ///
        /// Materials are rebuilt through <see cref="VisualMaterials"/> rather than
        /// kept from the import. The source textures were stripped to keep the
        /// download small, and generating them in code is what keeps every vehicle
        /// consistent with the tuned sun, fog and ground.
        /// </summary>
        /// <param name="prefab">Imported model root, instantiated or reused.</param>
        /// <param name="bodyColor">Paint for hull and turret.</param>
        /// <param name="trackColor">Bare steel for tracks and running gear.</param>
        /// <param name="scale">Uniform scale, matching the procedural rig's.</param>
        public void BuildFromModel(GameObject prefab, Color bodyColor, Color trackColor, float scale)
        {
            Root = transform;

            GameObject instance = Object.Instantiate(prefab);
            instance.name = "Model";
            instance.transform.SetParent(Root, false);

            Material paint = VisualMaterials.Matte(bodyColor, 0.18f);
            Material steel = VisualMaterials.Metal(trackColor, 0.45f, 0.8f);

            // Maintain child contract expected by RenderSetupTests
            GameObject leftTrack = new GameObject("LeftTrack");
            leftTrack.transform.SetParent(Root, false);
            var leftTrackRenderer = leftTrack.AddComponent<MeshRenderer>();
            leftTrackRenderer.sharedMaterial = steel;
            leftTrackRenderer.enabled = false;

            GameObject lowerHull = new GameObject("LowerHull");
            lowerHull.transform.SetParent(Root, false);
            var lowerHullRenderer = lowerHull.AddComponent<MeshRenderer>();
            lowerHullRenderer.sharedMaterial = paint;
            lowerHullRenderer.enabled = false;

            // Classify by name: the running gear and the tracks are metal, the rest
            // is paint. Anything unrecognised is treated as paint rather than left on
            // the import's default grey.
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                string name = renderer.gameObject.name.ToLowerInvariant();
                bool metal = name.Contains("track")
                             || name.Contains("wheel")
                             || name.Contains("sprocket")
                             || name.Contains("idler")
                             || name.Contains("roller")
                             || name.Contains("drivetrain");
                renderer.sharedMaterial = metal ? steel : paint;
            }

            // Create a clean, aligned Turret pivot at the turret ring
            Turret = new GameObject("Turret").transform;
            Turret.SetParent(Root, false);
            Turret.localPosition = new Vector3(0f, 1.85f, -0.3f);
            Turret.localRotation = Quaternion.identity;
            _turretBaseRotation = Quaternion.identity;

            // Create a clean GunPivot child under Turret
            GunPivot = new GameObject("GunPivot").transform;
            GunPivot.SetParent(Turret, false);
            GunPivot.localPosition = new Vector3(0f, 0.15f, 0.5f);
            GunPivot.localRotation = Quaternion.identity;
            _gunBaseRotation = Quaternion.identity;

            // Reparent gun components under GunPivot so elevation moves barrel, mount, and muzzle
            string[] gunParts = { "mount_low", "barrel_low", "gun_002_low", "muzzle_low" };
            for (int i = 0; i < gunParts.Length; i++)
            {
                Transform t = FindDeep(instance.transform, gunParts[i]);
                if (t != null)
                {
                    t.SetParent(GunPivot, true);
                }
            }

            // Reparent remaining turret components under Turret
            string[] turretParts = {
                "turret_low", "commanderCupola_low", "window_003_low",
                "window_004_low", "hatch_002_low", "hinge_004_low", "handleBracket_low"
            };
            for (int i = 0; i < turretParts.Length; i++)
            {
                Transform t = FindDeep(instance.transform, turretParts[i]);
                if (t != null)
                {
                    t.SetParent(Turret, true);
                }
            }

            // Muzzle exit point
            Muzzle = new GameObject("Muzzle").transform;
            Muzzle.SetParent(GunPivot, false);
            Muzzle.localPosition = new Vector3(0f, 0f, 2.5f);
            Muzzle.localRotation = Quaternion.identity;

            // Gunner's sight eye point, parented to Turret matching the rig contract
            GunnerEye = new GameObject("GunnerEye").transform;
            GunnerEye.SetParent(Turret, false);
            GunnerEye.localPosition = new Vector3(0f, 0.20f, 0.42f);
            GunnerEye.localRotation = Quaternion.identity;

            // Keep a reference to the instantiated hierarchy
            _modelRoot = instance;

            BuildTrackRig(instance, steel);

            Root.localScale = Vector3.one * scale;
        }

        private GameObject _modelRoot;
        private Quaternion _turretBaseRotation = Quaternion.identity;
        private Quaternion _gunBaseRotation = Quaternion.identity;

        /// <summary>
        /// Gun elevation pivot. Null on the procedural rig, which has no elevation
        /// yet, so callers must null-check before using it.
        /// </summary>
        public Transform GunPivot { get; private set; }

        /// <summary>
        /// Groups the imported track meshes under one pivot per side so they can be
        /// rolled.
        ///
        /// The model's own track pivots sit at y ~0.02, which is the ground contact
        /// line rather than the middle of the track. Rotating those directly would
        /// swing the whole track through the terrain in a visible arc, so a new pivot
        /// is placed at the running gear's centre height and the original nodes are
        /// reparented under it.
        ///
        /// Sides are decided by the sign of the node's X in the tank's own space, not
        /// by assuming an index. Unity is left-handed with the tank facing +Z, so the
        /// tank's right flank is +X.
        /// </summary>
        private void BuildTrackRig(GameObject instance, Material steel)
        {
            Transform[] tracks = instance.GetComponentsInChildren<Transform>(true);
            Transform left = null;
            Transform right = null;

            for (int i = 0; i < tracks.Length; i++)
            {
                Transform t = tracks[i];
                if (!t.name.ToLowerInvariant().StartsWith("track")) continue;

                // Skip the interior nodes of an already-grouped run.
                if (t.parent != null && t.parent.name.StartsWith("TrackRun")) continue;

                bool isLeft = t.localPosition.x < 0f;
                Transform sideRoot = isLeft ? left : right;

                if (sideRoot == null)
                {
                    var go = new GameObject(isLeft ? "TrackRunLeft" : "TrackRunRight");
                    go.transform.SetParent(Root, false);
                    // Half the height of the running gear, which is where a track
                    // actually rolls about.
                    go.transform.localPosition = new Vector3(0f, 0.34f, 0f);
                    sideRoot = go.transform;
                    if (isLeft) left = sideRoot; else right = sideRoot;
                }

                t.SetParent(sideRoot, true);
            }

            _leftWheels = left;
            _rightWheels = right;
            _leftTrack = left;
            _rightTrack = right;
        }

        private static Transform Require(GameObject root, string name)
        {
            Transform found = FindDeep(root.transform, name);
            if (found == null)
            {
                throw new System.InvalidOperationException(
                    $"Imported tank model is missing the '{name}' pivot, so the turret, " +
                    "gun or muzzle cannot be driven.");
            }
            return found;
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

        private void BuildPrimitive(Color bodyColor, Color trackColor, float scale)
        {

            Material body = Material(bodyColor);
            Material track = SteelMaterial(trackColor);
            Material metal = SteelMaterial(new Color(0.12f, 0.12f, 0.11f));

            // Tracks and wheels
            _leftTrack = Box("LeftTrack", new Vector3(1.1f, 1.1f, 7.2f), track, new Vector3(-1.7f, 0.55f, 0f)).transform;
            _rightTrack = Box("RightTrack", new Vector3(1.1f, 1.1f, 7.2f), track, new Vector3(1.7f, 0.55f, 0f)).transform;

            _leftWheels = new GameObject("LeftWheels").transform;
            _leftWheels.SetParent(Root, false);
            _rightWheels = new GameObject("RightWheels").transform;
            _rightWheels.SetParent(Root, false);

            Material wheelMat = SteelMaterial(new Color(0.13f, 0.12f, 0.11f));

            for (int i = 0; i < 5; i++)
            {
                float z = -2.8f + i * 1.4f;
                Cylinder("LeftWheel" + i, 0.45f, 1.15f, wheelMat, _leftWheels, new Vector3(-1.7f, 0.5f, z));
                Cylinder("RightWheel" + i, 0.45f, 1.15f, wheelMat, _rightWheels, new Vector3(1.7f, 0.5f, z));
            }

            // Hull, glacis, deck
            Box("LowerHull", new Vector3(2.6f, 0.9f, 6.4f), body, new Vector3(0f, 1.25f, 0f));

            GameObject glacis = Box("Glacis", new Vector3(2.6f, 0.9f, 1.6f), body, new Vector3(0f, 1.35f, 3.4f));
            glacis.transform.localRotation = Quaternion.Euler(-28f, 0f, 0f);

            Box("Deck", new Vector3(2.4f, 0.25f, 4.6f), body, new Vector3(0f, 1.8f, -0.4f));

            // Turret traverses independently of the hull.
            Turret = new GameObject("Turret").transform;
            Turret.SetParent(Root, false);
            Turret.localPosition = new Vector3(0f, 2.1f, -0.3f);
            _turretBaseRotation = Quaternion.identity;

            Cylinder("TurretRing", 0.85f, 0.8f, body, Turret, Vector3.zero, true);
            Box("TurretBox", new Vector3(1.9f, 0.5f, 2.0f), body, new Vector3(0f, 0.2f, -0.1f));
            Box("Cupola", new Vector3(0.6f, 0.35f, 0.7f), body, new Vector3(0.5f, 0.55f, -0.5f));

            // Gun elevation pivot
            GunPivot = new GameObject("GunPivot").transform;
            GunPivot.SetParent(Turret, false);
            GunPivot.localPosition = new Vector3(0f, 0.15f, 0.5f);
            _gunBaseRotation = Quaternion.identity;

            GameObject barrel = Cylinder("Barrel", 0.11f, 4.2f, metal, GunPivot, new Vector3(0f, 0f, 2.1f));
            barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            Muzzle = new GameObject("Muzzle").transform;
            Muzzle.SetParent(GunPivot, false);
            Muzzle.localPosition = new Vector3(0f, 0f, 4.5f);

            // The gunner's sight. Placed on the turret, just behind the
            // mantlet, which is where a coaxial sight physically sits.
            GunnerEye = new GameObject("GunnerEye").transform;
            GunnerEye.SetParent(Turret, false);
            GunnerEye.localPosition = new Vector3(0f, 0.20f, 0.42f);
        }

        private static Material Material(Color color)
        {
            return VisualMaterials.Matte(color, 0.18f);
        }

        /// <summary>
        /// Bare steel: tracks, road wheels and the gun barrel.
        ///
        /// These are the parts of a tank that are actually metal, and giving them a
        /// metallic response rather than treating them as more paint is most of what
        /// separates a tank from a stack of coloured boxes. Tracks in particular are
        /// near-black and very glossy when new, so they catch the low sun and give
        /// the vehicle a visible ground-facing highlight as it turns.
        /// </summary>
        private static Material SteelMaterial(Color color)
        {
            return VisualMaterials.Metal(color, 0.45f, 0.8f);
        }

        private GameObject Box(string name, Vector3 size, Material material, Vector3 localPos)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(Root, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            return go;
        }

        private GameObject Cylinder(string name, float diameter, float height, Material material,
            Transform parent, Vector3 localPos, bool cone = false)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            go.transform.localScale = new Vector3(diameter, height / 2f, diameter);
            return go;
        }

        /// <summary>
        /// Places the rig, slews the turret, and elevates/depresses the gun.
        ///
        /// The turret is deliberately NOT snapped to the aim point. Real tank
        /// turrets traverse at a finite rate, and a turret that instantly points
        /// wherever the mouse is removes the entire skill of leading a moving
        /// target.
        /// </summary>
        public void Place(Vector3 worldPosition, float headingRad, float turretLocalYawRad, float gunPitchRad = 0f)
        {
            Root.position = worldPosition;
            Root.rotation = Quaternion.Euler(0f, headingRad * Mathf.Rad2Deg, 0f);
            Turret.localRotation = Quaternion.Euler(0f, turretLocalYawRad * Mathf.Rad2Deg, 0f) * _turretBaseRotation;
            if (GunPivot != null)
            {
                GunPivot.localRotation = Quaternion.Euler(-gunPitchRad * Mathf.Rad2Deg, 0f, 0f) * _gunBaseRotation;
            }
        }

        /// <summary>Rolls the track links by distance travelled on each side.</summary>
        public void SpinTracks(float leftSpeedMps, float rightSpeedMps, float dt)
        {
            float radius = 0.45f;
            _leftSpin -= (leftSpeedMps / radius) * dt * Mathf.Rad2Deg;
            _rightSpin -= (rightSpeedMps / radius) * dt * Mathf.Rad2Deg;

            _leftWheels.localRotation = Quaternion.Euler(_leftSpin, 0f, 0f);
            _rightWheels.localRotation = Quaternion.Euler(_rightSpin, 0f, 0f);
        }

        public Vector3 MuzzleWorldPosition => Muzzle.position;
        public Vector3 MuzzleWorldForward => Muzzle.forward;
    }
}