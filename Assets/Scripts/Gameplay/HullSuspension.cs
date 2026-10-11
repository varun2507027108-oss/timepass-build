using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Per-contact suspension for a tracked vehicle hull.
    ///
    /// A real tank is not a rigid slab resting on the ground. Every road wheel sits
    /// on its own spring or torsion unit, and the hull's pitch, roll and ride height
    /// are the <i>emergent</i> result of how far each of those compresses. Braking
    /// dives the nose because the front springs load up. A hard turn rolls the hull
    /// outward. Crossing a camber settles the hull onto an average plane instead of
    /// perching it on whichever corner happens to be highest.
    ///
    /// The previous implementation had none of this. The simulation carried a single
    /// point position and a single yaw; the renderer computed a cosmetic tilt from
    /// four terrain samples using the MAXIMUM corner height -- rigid-body resting
    /// behaviour. The hull therefore visibly floated and rolled away from slopes it
    /// should have settled into, which is exactly what a rigid slab on four points
    /// does and what a tank does not.
    ///
    /// Model
    /// -----
    /// Four contact points, each a spring-damper against the terrain:
    ///
    ///     compression_i = groundHeight_i - (hullHeight + contactOffsetY_i) + restSag_i
    ///     F_i           = k * compression_i + c * compressionRate_i      (>= 0)
    ///
    /// A track cannot pull the hull down, so the force is clamped at zero. Those
    /// forces drive three degrees of freedom:
    ///
    ///     m  * y'' = SUM(F_i) - m*g
    ///     Ip * p'' = +SUM(F_i * z_i) + m * a_long * h_cg
    ///     Ir * r'' = +SUM(F_i * x_i) + m * a_lat  * h_cg
    ///
    /// Three signs in that block are load-bearing and all three are wrong in the
    /// obvious direction:
    ///
    ///   * The damper ADDS. For a mass on a spring, M*y'' = -k*y - c*y'; substituting
    ///     compression = -y gives the upward force as k*compression +
    ///     c*d(compression)/dt. Written as a minus it is negative damping.
    ///   * Both spring terms are added as they stand. For each, the attitude that
    ///     compresses one end of the patch necessarily unbalances SUM(F * offset) in the
    ///     direction that opposes the attitude, so the term is already restoring.'
    ///   * Weight transfer is applied at the CONTACT PATCH, which sits below the
    ///     centre of gravity. Accelerating pitches the nose up, braking pitches it
    ///     down, and a right-hand turn rolls the hull to the left, away from the
    ///     turn -- which is where the load goes.
    ///
    /// The attitude feeds back into the contact heights on the next step, which
    /// redistributes the spring loads. That feedback loop is the whole reason to
    /// model the contacts separately rather than as one rigid body.
    /// </summary>
    public sealed class HullSuspension
    {
        /// <summary>
        /// Contacts: four per track, at z = {-h, -h/3, +h/3, +h}.
        /// </summary>
        /// <remarks>
        /// The layout must be SYMMETRIC about the contact-patch centre and include BOTH
        /// extremes. An asymmetric ladder leaves a dead pair at mid-length contributing no
        /// pitch stiffness at all, so braking and acceleration do different things and the
        /// hull dives far harder than it squats.
        /// </remarks>
        private const int Contacts = 8;

        /// <summary>Total sprung mass, kg.</summary>
        public float mass;

        /// <summary>Spring rate per corner, N/m.</summary>
        public float springRate = 2.2e5f;

        /// <summary>Damping per corner, N.s/m.</summary>
        public float springDamping = 3.4e4f;

        /// <summary>Height of the centre of gravity above the contact plane, metres.</summary>
        public float cgHeight = 1.15f;

        /// <summary>
        /// Maximum suspension travel before the bump stop, metres.
        ///
        /// Real suspension has a finite stroke; beyond it the hull sits on the
        /// frame. Without this the vehicle behaves like a linear spring to infinity
        /// and a steep slope flings it into the air.
        /// </summary>
        public float maxTravel = 0.6f;

        /// <summary>
        /// Static compression per corner at rest, metres. Derived in the constructor.
        /// </summary>
        public float staticSag;

        /// <summary>
        /// Rest length of each corner, metres. Derived from the centre of gravity's
        /// position relative to the contact patch -- see <see cref="ComputeStaticPreload"/>.
        /// </summary>
        private readonly float[] restSag = new float[Contacts];

        /// <summary>Largest damper force a single corner may exert, N.</summary>
        private float maxDamping;

        /// <summary>Largest bump-stop force each corner may exert, N.</summary>
        private readonly float[] maxBumpStopForce = new float[Contacts];

        private void SetBumpStopCeilings()
        {
            for (int i = 0; i < Contacts; i++)
            {
                maxBumpStopForce[i] = springRate * restSag[i] * 2f;
            }
        }

        /// <summary>
        /// Attitude-layer stiffness, N.m/rad.
        /// </summary>
        /// <remarks>
        /// Attitude and ride are SEPARATE jobs with separate springs.
        ///
        /// The corner springs carry the weight and absorb roughness BETWEEN samples.
        /// Making them the only pitch stiffness tied attitude to bump-stop travel: at
        /// 0.35 m the springs bottomed out on ordinary slopes, so the hull attitude was
        /// really being set by the travel clamp rather than by any dynamics. Raise travel
        /// and the clamp stops masking it -- which is why the first attempt at a
        /// realistic travel limit produced a 37 degree braking dive.
        ///
        /// Sizing, for a target dive angle theta under peak acceleration:
        ///     K = m * a_max * h_cg / theta
        /// which at 25 t, 8.34 m/s^2, 1.15 m and 5 degrees is about 2.7e6 N.m/rad.
        /// </remarks>
        public float pitchStiffness = 2.7e6f;

        /// <inheritdoc cref="pitchStiffness"/>
        public float rollStiffness = 3.4e6f;

        /// <summary>Attitude-layer damping, N.m.s/rad. Near critical for the layer.</summary>
        public float pitchDamping = 7.6e5f;

        /// <inheritdoc cref="pitchDamping"/>
        public float rollDamping = 8.8e5f;

        /// <summary>
        /// Ride-height layer stiffness, N/m, pulling the hull toward the fitted
        /// terrain plane's height.
        /// </summary>
        /// <remarks>
        /// The attitude layer fixes pitch and roll, but ride height is the THIRD degree of
        /// freedom and it was still being left entirely to the corner springs. That is
        /// what launches the vehicle: on an 18 degree slope the ends of a 5.67 m contact
        /// patch differ by 1.8 m of ground, which 0.6 m of travel cannot bridge, so every
        /// corner saturates on its bump stop and fires the hull into the air.
        /// </remarks>
        public float heaveStiffness = 6.0e5f;

        /// <inheritdoc cref="heaveStiffness"/>
        public float heaveDamping = 1.4e5f;

        /// <summary>Terrain height sampled beneath each contact this step, metres.</summary>
        private readonly float[] groundY = new float[Contacts];

        /// <summary>
        /// Roll inertia about the longitudinal axis, kg.m^2.
        ///
        /// Derived from the hull's own proportions in the constructor rather than
        /// fixed here, so scaling the vehicle scales its rotational response with it.
        /// </summary>
        public float rollInertia;

        /// <summary>
        /// Pitch inertia about the lateral axis, kg.m^2. Derived in the constructor.
        /// </summary>
        public float pitchInertia;

        /// <summary>Hull attitude, radians. Positive pitch is nose up, positive roll is right side up.</summary>
        public float pitchRad { get; private set; }

        /// <inheritdoc cref="pitchRad"/>
        public float rollRad { get; private set; }

        /// <summary>Height of the hull's contact plane above sea level, metres.</summary>
        public float height { get; private set; }

        /// <summary>Vertical velocity of the hull, m/s.</summary>
        public float verticalVelocity { get; private set; }

        private float pitchVelocity;
        private float rollVelocity;

        // Contact offsets in the hull frame: x = right, z = forward.
        private readonly float[] contactX = new float[Contacts];
        private readonly float[] contactZ = new float[Contacts];
        private readonly float[] lastCompression = new float[Contacts];

        public HullSuspension(float massKg, float halfGauge, float halfTrackWidth,
            float contactCentreOffsetZ = 0f)
        {
            mass = massKg;

            // Inertias follow the vehicle's own proportions rather than being fixed
            // constants, so scaling the hull up scales its rotational response with
            // it rather than leaving a heavy tank as twitchy as a light one.
            float length = halfGauge * 2f;
            float width = halfTrackWidth * 2f;
            const float heightM = 2.4f;
            rollInertia = mass * (width * width + heightM * heightM) / 12f;
            pitchInertia = mass * (length * length + heightM * heightM) / 12f;

            // How far the hull sags at rest, per corner: the compression that carries
            // a quarter of the vehicle's weight through one spring.
            //
            // This is subtracted out when measuring compression so that the hull's
            // reference plane rests exactly ON the ground rather than a full static
            // sag below it. Without it the whole vehicle sinks 14 cm at spawn and
            // the renderer has to compensate for a datum error that belongs here.
            staticSag = mass * TrackedVehicleDynamics.Gravity / (Contacts * Mathf.Max(springRate, 1f));

            // Four contacts per track at z = {-h, -h/3, +h/3, +h}, symmetric about the
            // patch centre with BOTH extremes present.
            //
            // An asymmetric ladder -- {-h, -h/2, 0, +h/2} -- has no contact at the front
            // extreme and a dead pair at mid-length contributing no pitch stiffness at
            // all. Braking and acceleration then behave differently, and the hull dives
            // far harder than it squats.
            const int PerTrack = Contacts / 2;
            float third = halfGauge / 3f;
            for (int i = 0; i < PerTrack; i++)
            {
                float z;
                switch (i)
                {
                    case 0: z = -halfGauge; break;
                    case 1: z = -third; break;
                    case 2: z = third; break;
                    default: z = halfGauge; break;
                }
                z += contactCentreOffsetZ;
                contactX[i] = -halfTrackWidth;
                contactZ[i] = z;
                contactX[i + PerTrack] = halfTrackWidth;
                contactZ[i + PerTrack] = z;
            }

            ComputeStaticPreload();
            SetBumpStopCeilings();
        }

        /// <summary>
        /// Give each corner its own rest length, so a LEVEL hull on LEVEL ground is an
        /// exact equilibrium.
        ///
        /// This is not a fudge factor; it is what makes the model well-posed. With four
        /// identical springs, a level hull on flat ground compresses all four equally,
        /// so all four push equally -- and if the contact patch is not centred under the
        /// centre of gravity, equal pushes do NOT balance:
        ///
        ///     SUM(F * z) = 2*k*z_front - 2*k*z_rear  !=  0
        ///
        /// The vehicle would then have to tilt until the forces differed, which for the
        /// Panzer IV footprint (z_front = +1.47, z_rear = -4.19) parks it at +1.09
        /// degrees nose-up on dead flat ground. That is an artefact of assuming every
        /// spring is identical, not a real behaviour.
        ///
        /// Real suspensions are set up so the vehicle sits level: the front carries
        /// more load because the centre of gravity sits nearer it, and the springs are
        /// preloaded accordingly. Solving for the load split that balances both
        /// equations gives the per-corner rest lengths below.
        /// </summary>
        private void ComputeStaticPreload()
        {
            // Static load per contact, solved as a LINEAR TAPER restSag(z) = a + b*z
            // satisfying both equilibrium conditions:
            //
            //     SUM(F)     = m*g   ->  SUM(restSag)     = S
            //     SUM(F * z) = 0     ->  SUM(restSag * z) = 0
            //
            // with F = k * restSag. Two equations, two unknowns, so this holds for any
            // contact count and any patch offset -- which matters now that the contacts
            // run down the length of each track rather than sitting at two discrete ends.
            // A symmetric patch centred under the CG gives b = 0 and a uniform load.
            float t1 = 0f;   // SUM(z)
            float t2 = 0f;   // SUM(z^2)
            for (int i = 0; i < Contacts; i++)
            {
                t1 += contactZ[i];
                t2 += contactZ[i] * contactZ[i];
            }

            float sumSag = mass * TrackedVehicleDynamics.Gravity / Mathf.Max(springRate, 1f);
            staticSag = sumSag / Contacts;

            float a = staticSag;
            float b = 0f;
            float denom = t1 * t1 - Contacts * t2;
            if (Mathf.Abs(denom) > 1e-4f)
            {
                b = sumSag * t1 / denom;
                a = -b * t2 / t1;
            }

            for (int i = 0; i < Contacts; i++)
            {
                restSag[i] = Mathf.Max(a + b * contactZ[i], 0f);
            }

            // Damper ceiling, keyed to CRITICAL damping for this corner's share of the
            // mass: c_crit = 2 * sqrt(k * m_corner).
            //
            // It used to be preload * springRate, which halves whenever the spring rate
            // halves while the damping actually required barely moves -- so the cap
            // silently changed meaning every time the suspension was retuned. At 30 Hz
            // the explicit damper has an effective stiffness of c/dt, so an unbounded
            // damper is what drives the integrator unstable; bounding it by the
            // corner's own critical damping is both stable and physically meaningful.
            float massPerCorner = mass / Contacts;
maxDamping = 2f * Mathf.Sqrt(springRate * massPerCorner);

            // Bump stop ceilings are set by SetBumpStopCeilings, which needs restSag to
            // be final. It runs immediately after this method in the constructor.
        }

        /// <summary>
        /// Snap the hull onto the ground with no velocity, so the vehicle does not
        /// drop in at spawn or after a respawn.
        /// </summary>
        public void Reset(float x, float z, float headingRad)
        {
            float sum = 0f;
            for (int i = 0; i < Contacts; i++)
            {
                sum += GroundAt(i, x, z, headingRad);
            }
            height = sum * 0.25f;
            pitchRad = 0f;
            rollRad = 0f;
            pitchVelocity = 0f;
            rollVelocity = 0f;
            verticalVelocity = 0f;

            // Seed each damper with the compression it is actually sitting at.
            //
            // Leaving these at zero makes the very first step see a compression rate
            // of staticSag/dt -- fifteen metres per second -- so every damper delivers
            // its capped maximum impulse on frame one and the hull is kicked off the
            // ground as it spawns. The rate is a DIFFERENCE against the previous
            // step, so the previous step has to start from where this one is.
            for (int i = 0; i < Contacts; i++)
            {
                lastCompression[i] = GroundAt(i, x, z, headingRad) - height + restSag[i];
            }
        }

        /// <summary>
        /// Advance the suspension by one fixed step.
        /// </summary>
        /// <param name="dt">Timestep, seconds.</param>
        /// <param name="x">Hull centre, world X.</param>
        /// <param name="z">Hull centre, world Z.</param>
        /// <param name="headingRad">Hull yaw.</param>
        /// <param name="accelLong">Longitudinal acceleration in the hull frame, m/s^2.</param>
        /// <param name="accelLat">Lateral acceleration in the hull frame, m/s^2.</param>
        public void Step(float dt, float x, float z, float headingRad,
            float accelLong, float accelLat)
        {
            float totalForce = 0f;
            float pitchMoment = 0f;
            float rollMoment = 0f;

            for (int i = 0; i < Contacts; i++)
            {
                // The contact sits at (ox, 0, oz) in the hull frame: on the ground
                // plane, offset fore/aft and side to side. Rotating it by the hull
                // attitude gives the vertical offset that decides how compressed
                // that corner is.
                //
                // Roll first, about the forward axis:
                //     y' = ox*sin(r)
                // Then pitch, about the lateral axis:
                //     y'' = y'*cos(p) + z*sin(p)
                //
                // The pitch term is PLUS. A positive pitch is nose-UP, so a contact
                // ahead of the centre (z > 0) must RISE, which is a positive y offset.
                // Writing the standard rotation (y'*cos p - z*sin p) here gives a
                // nose-DOWN positive pitch, and since TankView renders
                // Quaternion.Euler(-pitch) -- nose-up positive -- the renderer then
                // inverts the whole attitude. The tank visibly nose-dives on every
                // uphill and squats under power, and buries itself in the slope.
                // With no attitude this is exactly zero, which is what makes a level
                // hull on flat ground sit still instead of launching itself.
                float ox = contactX[i];
                float oz = contactZ[i];

                float ry = ox * Mathf.Sin(rollRad);
                float localY = ry * Mathf.Cos(pitchRad) + oz * Mathf.Sin(pitchRad);

                float wx = x + ContactWorldX(i, headingRad);
                float wz = z + ContactWorldZ(i, headingRad);

                float groundY = BattlefieldBuilder.GetTerrainHeight(wx, wz);
                this.groundY[i] = groundY;

                // Compression is how far the hull has SUNK BELOW the contact, so it
                // is ground minus hull. Writing it the other way round gives an
                // inverted spring -- rise, compress further, push harder -- and the
                // vehicle launches itself within a second.
                float compression = groundY - (height + localY) + restSag[i];

                // Finite travel. Real suspension has a limited stroke and then the
                // frame sits on a bump stop. Without this the spring is linear to
                // infinity: across a 39 degree slope the corners differ by more than
                // a metre of ground, which at this rate is a quarter of a meganewton
                // of lift per corner and the hull is fired into the sky. Travel is
                // also what makes the vehicle ride OVER an obstacle rather than
                // being crushed by it.
                if (compression > maxTravel) compression = maxTravel;
                else if (compression < -maxTravel) compression = -maxTravel;

                // Damping from this corner's own compression rate. Using a single
                // shared rate would let one corner's motion bleed into the others
                // and damp the hull against a motion it is not making.
                float rate = (compression - lastCompression[i]) / Mathf.Max(dt, 1e-5f);
                lastCompression[i] = compression;

                float dampingForce = springDamping * rate;
                // The damping term is the stiffest thing in this model. Writing it as
                // c * deltaCompression / dt makes its effective stiffness c/dt, which
                // at 30 Hz is more than DOUBLE the spring rate. Explicitly
                // integrating that is unconditionally unstable: one step of corner
                // compression produces a larger impulse than the spring can absorb
                // and the hull oscillates to its stops within a second.
                // Capping it to the bump-stop force keeps the damping physical --
                // a damper cannot exert more than the structure it is mounted on.
                // Capped per the preload-derived structural limit.
                if (dampingForce > maxDamping) dampingForce = maxDamping;
                else if (dampingForce < -maxDamping) dampingForce = -maxDamping;

                // Damping resists the RATE OF CHANGE of compression, and -- the part that is easy
                // to get backwards -- it ADDS to the spring force while the damper
                // is extending. Derivation: for a mass on a spring, M*y'' = -k*y -
                // c*y'. Substituting compression = -y gives the upward force as
                // k*compression + c*d(compression)/dt. Written as MINUS, the damper
                // is negative damping: it pushes the hull onward every time the
                // suspension extends, pumps energy in, and the hull rings itself to
                // its stops within a second. That is exactly the failure mode.
                float force = springRate * compression + dampingForce;
                // A track cannot pull the hull down.
                if (force < 0f) force = 0f;
                // And the bump stop cannot push past its limit either.
                float maxForce = maxBumpStopForce[i];
                if (force > maxForce) force = maxForce;

totalForce += force;

                // Restoring moments. The two spring terms have OPPOSITE signs, and neither is the
                // obvious one.
                //
                // Pitch is nose-up positive, so nose-up lifts the front contacts
                // (z > 0) and presses the rear ones down. F_front therefore falls and
                // F_rear rises, making SUM(F*z) negative -- already restoring. Adding
                // it directly is correct; negating it turns the springs into a second
                // source of pitch energy and the hull rolls onto its nose.
                // Moment arms are measured from the CONTACT PATCH CENTRE, not from the
                // vehicle origin.
                //
                // The Panzer IV's track patch sits 1.36 m BEHIND the model origin, so
                // even uniform loading gives SUM(F*z) = F * SUM(z) != 0 -- a constant
                // nose-down moment proportional to how hard the tracks are pressed. With
                // the attitude layer now owning macro alignment, leaving that artefact in
                // the corner springs made the two layers fight, and the hull sat 31 degrees
                // nose-down on an uphill while the attitude spring was pulling it level.
                pitchMoment += force * contactZ[i];

                // Roll is right-side-up positive. Right-side-up lifts the right contacts
                // (x > 0) and sinks the left ones, so F_left > F_right and SUM(F*x) is
                // negative -- also already restoring, and added for the same reason.
                rollMoment += force * contactX[i];
            }

// --- ATTITUDE LAYER -------------------------------------------------
            //
            // Fit a plane to the sampled ground and spring the hull's attitude toward
            // it. This is a LEAST-SQUARES fit, not a max-corner solve: crossing a dip
            // settles the hull onto the average of its contacts rather than perching on
            // the highest, which is the whole goal stated at the top of this file.
            //
            // With a symmetric contact layout the first moments vanish and the fit is
            // exact in closed form:
            //     pitch = atan2(SUM(z*g), SUM(z^2))
            //     roll  = atan2(SUM(x*g), SUM(x^2))
            //
            // Macro conformity to the terrain is this layer's job; the corner springs
            // only absorb roughness BETWEEN samples. That split is what stops attitude
            // being set by a bump-stop travel clamp.
            // The mean sample height is subtracted before fitting.
            //
            // atan2(SUM(z*g), SUM(z^2)) is exact only when SUM(z) = 0. A layout that is
            // symmetric but merely OFFSET from the origin -- which the Panzer IV is, its
            // track patch sitting 1.36 m behind the model origin -- still has SUM(z) != 0,
            // so the SUM(z * meanHeight) term survives and corrupts the fitted slope with
            // the absolute height of the ground. Subtracting the mean makes the first
            // moments vanish for ANY layout, offset or not.
            float meanGround = 0f;
            for (int i = 0; i < Contacts; i++) meanGround += groundY[i];
            meanGround /= Contacts;

            float szg = 0f, szz = 0f, sxg = 0f, sxx = 0f;
            for (int i = 0; i < Contacts; i++)
            {
                float dg = groundY[i] - meanGround;
                szg += contactZ[i] * dg;
                szz += contactZ[i] * contactZ[i];
                sxg += contactX[i] * dg;
                sxx += contactX[i] * contactX[i];
            }

            float terrainPitch = szz > 1e-4f ? Mathf.Atan2(szg, szz) : 0f;
            float terrainRoll = sxx > 1e-4f ? Mathf.Atan2(sxg, sxx) : 0f;

            // Ride height follows the PLANE, not the corners. The corner springs below
            // then only ever see roughness BETWEEN samples, which is the job they are
            // actually good at.
            //
            // meanGround is the fitted plane's height under the hull reference point, so
            // this makes the vehicle ride up and down hills as a whole instead of each
            // contact fighting the slope through a travel limit it cannot satisfy.
            totalForce += -heaveStiffness * (height - meanGround) - heaveDamping * verticalVelocity;

            // Attitude springs. These, not corner travel, resist weight transfer.
            pitchMoment += -pitchStiffness * (pitchRad - terrainPitch) - pitchDamping * pitchVelocity;
            rollMoment += -rollStiffness * (rollRad - terrainRoll) - rollDamping * rollVelocity;

            // Weight transfer: the lateral and longitudinal forces act at the
            // CONTACT PATCH, which sits below the centre of gravity. A vehicle
            // accelerating forward therefore pitches nose-up, braking pitches nose-down,
            // and a right-hand turn rolls the hull to the left, away from the turn --
            // exactly as the load moves onto the outer track.
            //
            // This DEVIATES the attitude from the fitted plane by m*a*h/K, so braking
            // dives below the plane, power squats above it, and lateral load rolls
            // outward. That offset is the visible weight transfer.
            // CONTACT PATCH, which sits below the centre of gravity. A vehicle
            // accelerating forward therefore pitches nose-up, braking pitches
            // nose-down, and a right-hand turn rolls the hull to the left, away from
            // the turn -- exactly as the load moves onto the outer track.
            pitchMoment += mass * accelLong * cgHeight;
            rollMoment += mass * accelLat * cgHeight;

            // --- integrate (semi-implicit Euler) -------------------------------
            float accelY = (totalForce - mass * TrackedVehicleDynamics.Gravity) / mass;
            verticalVelocity += accelY * dt;
            height += verticalVelocity * dt;

            pitchVelocity += (pitchMoment / Mathf.Max(pitchInertia, 1f)) * dt;
            rollVelocity += (rollMoment / Mathf.Max(rollInertia, 1f)) * dt;
            pitchRad += pitchVelocity * dt;
            rollRad += rollVelocity * dt;

            // --- stabilise -----------------------------------------------------
            // Real suspension is far more heavily damped than an explicit integrator
            // captures at 30 Hz. Without this the hull rings at the integrator's own
            // frequency instead of settling.
            verticalVelocity *= 0.94f;

            // A tank cannot fold its own hull up. These limits are generous, and stop
            // a single pathological terrain sample from rolling it onto its roof.
            pitchRad = ClampAngle(pitchRad, ref pitchVelocity, MaxTiltRad());
            rollRad = ClampAngle(rollRad, ref rollVelocity, MaxTiltRad());

            // Deliberately NO hard floor snapping the hull up to the highest corner.
            //
            // That was the original bug: a rigid slab cannot pass through the
            // ground, so it teleports to sit on whichever corner is highest. Here it
            // fired the hull more than two metres into the air every time the tank
            // met a ledge, and the attitude fought the teleport until it hit its
            // stops.
            //
            // Non-penetration is already enforced, and enforced correctly, by the
            // bump stops: a corner driven below ground compresses, saturates at
            // 1.5 * k * travel, and four of those exceed the vehicle's weight by a
            // factor of three, so the hull is always pushed back out. That is the
            // same physical constraint, expressed as a force rather than as a
            // position edit, which is why the vehicle can now ride UP onto an
            // obstacle instead of teleporting onto it.
        }

        private static float MaxTiltRad()
        {
            return 40f * Mathf.Deg2Rad;
        }

        private static float ClampAngle(float angle, ref float velocity, float limit)
        {
            if (angle > limit) { velocity = velocity > 0f ? 0f : velocity; return limit; }
            if (angle < -limit) { velocity = velocity < 0f ? 0f : velocity; return -limit; }
            return angle;
        }

        private float ContactWorldX(int index, float headingRad)
        {
            float cy = Mathf.Cos(headingRad);
            float sy = Mathf.Sin(headingRad);
            return contactX[index] * cy + contactZ[index] * sy;
        }

        private float ContactWorldZ(int index, float headingRad)
        {
            float cy = Mathf.Cos(headingRad);
            float sy = Mathf.Sin(headingRad);
            return -contactX[index] * sy + contactZ[index] * cy;
        }

        private float GroundAt(int index, float x, float z, float headingRad)
        {
            float wx = x + ContactWorldX(index, headingRad);
            float wz = z + ContactWorldZ(index, headingRad);
            return BattlefieldBuilder.GetTerrainHeight(wx, wz);
        }
    }
}