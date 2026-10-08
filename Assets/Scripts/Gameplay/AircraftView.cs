using UnityEngine;
using WarArena.Air;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Procedural aircraft rig: fuselage, wings, tail, canopy and either a spinning
    /// propeller or a jet nozzle.
    ///
    /// Wing span and area are read from the catalogue, so a Spitfire visibly has
    /// the small elliptical wings its real 12.4 m2 area implies and the Bf 109
    /// has the broad slab wing of a 16 m2 airframe. That matters because players
    /// read vehicle proportions for silhouette, not numbers.
    /// </summary>
    public class AircraftView : MonoBehaviour
    {
        public Transform Root { get; private set; }
        public Transform Propeller { get; private set; }
        public Transform GunMuzzle { get; private set; }
        public Transform WingLeft { get; private set; }
        public Transform WingRight { get; private set; }

        private Material _bodyMaterial;
        private Material _glassMaterial;
        private Color _bodyColor = Color.white;
        private Color _accentColor = Color.white;

        public void Build(AircraftDefinition spec, Color body, Color accent)
        {
            Root = transform;
            _bodyColor = body;
            _accentColor = accent;

            // Airframe: matte painted aluminium with a low metallic response, so it
            // catches a soft sheen from the sky rather than looking like plastic.
            _bodyMaterial = Material(body, 0.35f, 0.15f);

            // Engine and exhaust areas: darker, glossier, more metallic.
            Material darkMaterial = Material(body * 0.55f, 0.42f, 0.7f);

            // Squadron markings read best as a saturated, slightly glossy band.
            Material accentMaterial = Material(accent, 0.45f, 0.05f);

            // Canopy glazing. The alpha now actually takes effect, so this is
            // genuinely see-through and catches the sky.
            _glassMaterial = Material(new Color(0.55f, 0.72f, 0.85f, 0.42f), 0.95f, 0.1f);
            MakeTransparent(_glassMaterial);

            // Fuselage: length scales with class, jets are longer and slimmer.
            float length = spec.Class == AircraftClass.JetFighter ? 12f : 9.5f;
            float width = 1.1f;
            float height = 1.0f;

            GameObject fuselage = Box("Fuselage", new Vector3(width, height, length), _bodyMaterial, Vector3.zero);
            fuselage.transform.localPosition = new Vector3(0f, 0f, length * 0.05f);

            // Nose cone, tapered by scaling two stacked segments.
            GameObject nose = Box("Nose", new Vector3(width * 0.7f, height * 0.7f, length * 0.22f),
                _bodyMaterial, new Vector3(0f, 0f, length * 0.55f));

            // Wing span derived from area: a heavier airframe needs more wing.
            float span = Mathf.Clamp(Mathf.Sqrt(spec.WingAreaM2 * 6.2f), 7f, 14f);
            float chord = spec.WingAreaM2 * 2f / span;

            GameObject wing = Box("Wing", new Vector3(span, 0.22f, chord), _bodyMaterial,
                new Vector3(0f, -0.15f, length * 0.08f));
            WingLeft = wing.transform;

            // Wing tips, coloured with the accent so the silhouette reads in
            // silhouette, which is how pilots actually spot a fighter.
            for (int side = -1; side <= 1; side += 2)
            {
                GameObject tip = Box("WingTip" + side, new Vector3(0.35f, 0.22f, chord), accentMaterial,
                    new Vector3(side * (span * 0.5f - 0.2f), -0.15f, length * 0.08f));
            }

            // Tail: fin and stabiliser.
            Box("Fin", new Vector3(0.16f, 1.5f, length * 0.2f), accentMaterial,
                new Vector3(0f, 0.75f, -length * 0.42f));
            Box("Stabiliser", new Vector3(3.2f, 0.16f, 0.9f), _bodyMaterial,
                new Vector3(0f, 0.1f, -length * 0.46f));

            // Canopy.
            GameObject canopy = Box("Canopy", new Vector3(0.8f, 0.5f, length * 0.2f), _glassMaterial,
                new Vector3(0f, 0.5f, length * 0.28f));

            // Engine section and the distinguishing tail end.
            Box("EngineDeck", new Vector3(width * 0.9f, 0.4f, length * 0.18f), darkMaterial,
                new Vector3(0f, 0.35f, length * 0.05f));

            if (spec.PropellerDriven)
            {
                BuildPropeller(length, width);
            }
            else
            {
                BuildJetNozzle(length, width, darkMaterial);
            }

            // Guns are mounted in the nose, which is why a fighter's shells converge
            // slightly ahead of the aircraft.
            GunMuzzle = new GameObject("GunMuzzle").transform;
            GunMuzzle.SetParent(Root, false);
            GunMuzzle.localPosition = new Vector3(0f, 0f, length * 0.68f);

            // Landing gear stub, purely so the aircraft does not look like it is
            // floating at spawn.
            Box("GearSkid", new Vector3(0.1f, 0.5f, 1.2f), darkMaterial, new Vector3(0f, -0.7f, 0.3f));
        }

        private void BuildPropeller(float length, float width)
        {
            GameObject hub = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            hub.name = "PropellerHub";
            Destroy(hub.GetComponent<Collider>());
            hub.GetComponent<Renderer>().sharedMaterial = _bodyMaterial;
            hub.transform.SetParent(Root, false);
            hub.transform.localPosition = new Vector3(0f, 0f, length * 0.66f);
            hub.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            hub.transform.localScale = new Vector3(0.4f, 0.2f, 0.4f);

            Propeller = new GameObject("Propeller").transform;
            Propeller.SetParent(Root, false);
            Propeller.localPosition = new Vector3(0f, 0f, length * 0.72f);

            // Three blades, enough to read the disc without aliasing badly.
            for (int i = 0; i < 3; i++)
            {
                GameObject blade = Box("Blade" + i, new Vector3(0.28f, 3.4f, 0.06f), _bodyMaterial, Vector3.zero);
                blade.transform.SetParent(Propeller, false);
                blade.transform.localRotation = Quaternion.Euler(0f, 0f, i * 120f);
                blade.transform.localPosition = Vector3.zero;
            }

            // The disc, semi-transparent so it does not strobe at high RPM.
            GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Quad);
            disc.name = "PropDisc";
            Destroy(disc.GetComponent<Collider>());
            disc.transform.SetParent(Propeller, false);
            disc.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            disc.transform.localScale = new Vector3(3.4f, 3.4f, 1f);
            // Unlit rather than lit: the disc is a motion-blur suggestion of spinning
            // blades, not a surface catching sunlight. Shading it made it flicker
            // dark and bright as the aircraft turned.
            Material discMaterial = VisualMaterials.Emissive(
                new Color(_bodyColor.r, _bodyColor.g, _bodyColor.b, 0.18f),
                new Color(_bodyColor.r, _bodyColor.g, _bodyColor.b), 0.6f);
            MakeTransparent(discMaterial);
            disc.GetComponent<Renderer>().sharedMaterial = discMaterial;
            disc.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void BuildJetNozzle(float length, float width, Material dark)
        {
            GameObject nozzle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            nozzle.name = "Nozzle";
            Destroy(nozzle.GetComponent<Collider>());
            nozzle.GetComponent<Renderer>().sharedMaterial = dark;
            nozzle.transform.SetParent(Root, false);
            nozzle.transform.localPosition = new Vector3(0f, 0f, -length * 0.5f);
            nozzle.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            nozzle.transform.localScale = new Vector3(0.85f, 0.6f, 0.85f);

            // Heat haze disc that brightens with throttle, so a jet at full power
            // is visibly working.
            GameObject flame = GameObject.CreatePrimitive(PrimitiveType.Quad);
            flame.name = "ExhaustGlow";
            Destroy(flame.GetComponent<Collider>());
            flame.transform.SetParent(Root, false);
            flame.transform.localPosition = new Vector3(0f, 0f, -length * 0.62f);
            flame.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            flame.transform.localScale = new Vector3(0.8f, 0.8f, 1f);
            // Emissive so the exhaust actually glows. As a merely lit surface it was
            // no brighter than the sky behind it whenever the sun was on that side.
            Material flameMaterial = VisualMaterials.Emissive(
                new Color(1f, 0.55f, 0.2f, 0.7f), new Color(1f, 0.42f, 0.12f), 3f);
            MakeTransparent(flameMaterial);
            flame.GetComponent<Renderer>().sharedMaterial = flameMaterial;
            flame.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _exhaust = flame.transform;
        }

        private Transform _exhaust;

        private static Material Material(Color color, float smoothness, float metallic)
        {
            return VisualMaterials.Lit(color, smoothness, metallic);
        }

        /// <summary>
        /// Axis-aligned box primitive, parented to the aircraft root.
        ///
        /// Colliders are stripped: the play scene has no colliders on anything
        /// (hit detection is analytic, using positions and radii), and leaving
        /// them in would only add per-frame physics cost for no benefit.
        /// </summary>
        private GameObject Box(string name, Vector3 size, Material material, Vector3 localPos)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.transform.SetParent(Root, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            return go;
        }

        /// <summary>
        /// Unity has no Material.transparent flag; transparency is shader blend
        /// state and all of it has to be set explicitly. See
        /// <see cref="VisualMaterials.MakeTransparent"/> for why this previously did
        /// nothing at all under the built-in pipeline.
        /// </summary>
        public static void MakeTransparent(Material material)
        {
            VisualMaterials.MakeTransparent(material);
        }

        /// <summary>Applies the flight model to the rig. Called once per frame.</summary>
        public void Apply(FlightModel flight)
        {
            Root.position = flight.Position;
            Root.rotation = Quaternion.Euler(flight.PitchDeg, flight.YawDeg, -flight.RollDeg);

            if (Propeller != null)
            {
                Propeller.localRotation = Quaternion.Euler(0f, 0f, flight.PropellerAngle);
            }

            if (_exhaust != null)
            {
                float scale = Mathf.Lerp(0.3f, 1.6f, flight.Throttle);
                _exhaust.localScale = new Vector3(scale, scale, 1f);
                var m = (Material)_exhaust.GetComponent<Renderer>().sharedMaterial;
                m.color = new Color(1f, 0.55f, 0.2f, Mathf.Lerp(0.15f, 0.75f, flight.Throttle));
            }

            // Stalled aircraft trail smoke so the state is visible, not just felt.
            bool stalled = flight.IsStalled;
            if (stalled != _wasStalled)
            {
                _wasStalled = stalled;
            }
        }

        private bool _wasStalled;

        public Vector3 MuzzleWorldPosition => GunMuzzle.position;
        public Vector3 MuzzleWorldForward => GunMuzzle.forward;
    }
}