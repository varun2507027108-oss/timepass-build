using UnityEngine;

namespace WarArena.Gameplay
{
    /// <summary>
    /// Terrain-dependent motion for a tracked vehicle.
    ///
    /// Everything here is a pure function of geometry plus a handful of vehicle
    /// constants, so it is unit-testable without a scene, a Rigidbody or PhysX. It
    /// deliberately does NOT touch Unity physics: the authoritative simulation
    /// integrates these accelerations itself at a fixed 30 Hz so that the result is
    /// frame-rate independent and reproducible, which a Rigidbody stepped inside
    /// <c>Update</c> is not.
    ///
    /// Physics basis
    /// -------------
    /// The model is the standard terrain-vehicle decomposition used for military
    /// mobility analysis (US Army Corps of Engineers ERDC/GSL TR-05-6 "Standard for
    /// Ground Vehicle Mobility"; TOP 2-2-610 "Gradeability and Side Slope
    /// Performance"). A vehicle on a slope has TWO independent limits, and conflating
    /// them is the single most common error in arcade tank handling:
    ///
    ///  1. <b>Traction</b> — the tracks must develop enough force to climb. Governed
    ///     by the drawbar pull the vehicle can generate against its own weight:
    ///     <c>sin(theta) = P / W</c>. Past this the tracks simply slip.
    ///  2. <b>Tipping</b> — the centre of gravity must stay inside the support
    ///     polygon. For a longitudinal slope the critical angle is
    ///     <c>tan(theta_max) = h_cg / d_half</c>, the CG height over the half-width
    ///     of the track contact patch. Past this the vehicle rolls over.
    ///
    /// The previous implementation had neither. It multiplied displacement by a
    /// scalar between 1.0 and 0, applied <b>only uphill</b> (so gravity was absent
    /// entirely and a tank rolled downhill faster and faster), never touched the
    /// speed state, and ignored side slope completely.
    ///
    /// Everything below is expressed as an acceleration in m/s^2 rather than as a
    /// multiplier on distance, so the two integrate together and the HUD speed and
    /// the actual motion can no longer disagree.
    /// </summary>
    public static class TrackedVehicleDynamics
    {
        /// <summary>Standard gravity, m/s^2.</summary>
        public const float Gravity = 9.81f;

        // ------------------------------------------------------- contact geometry
        //
        // ONE definition, used by both the force model and the visual ground-contact
        // solve in TankView.Place. These were previously two independent sets of
        // numbers -- the speed penalty sampled over a 3.0 m baseline while the
        // visible tilt was computed from 3.4 m by 2.4 m -- so the slope the player
        // felt and the slope they could see were different terrain measurements and
        // could disagree at exactly the moment it mattered.

        /// <summary>Distance between front and rear road wheels, metres.</summary>
        public const float TrackGaugeM = 3.4f;
        /// <summary>Overall track width, metres.</summary>
        public const float TrackWidthM = 3.0f;
        /// <summary>Half the wheelbase, for the front/rear contact samples.</summary>
        public const float HalfGaugeM = TrackGaugeM * 0.5f;
        /// <summary>Half the track width, for the left/right contact samples.</summary>
        public const float HalfTrackWidthM = TrackWidthM * 0.5f;

        // --- Suspension footprint ---------------------------------------------
        //
        // The contact geometry the SUSPENSION uses is measured from the rendered
        // vehicle, and is deliberately separate from TrackGaugeM/TrackWidthM above.
        // Those two feed the skid-steer yaw rate, where the number means "distance
        // between track centrelines" and is used as a pure turning geometry.
        //
        // These three are measured off the Panzer IV rig that actually ships:
        //   * the track contact patch is 5.89 m long, not 3.4 m;
        //   * it is 2.97 m wide;
        //   * and its centre sits 1.42 m BEHIND the model origin, because the
        //     origin is not the hull centre.
        //
        // The old constants put the simulated contacts at +/-1.70 m around a 9 m
        // tank, so the suspension only ever felt the middle third of the vehicle.
        // The last 2.5 m of track at the rear was invisible to the physics: it
        // could bury itself in a rise, or hang in the air over a dip, while the
        // hull reported a perfectly healthy ride height. The renderer used to hide
        // this by clamping the model up to the highest of four corner samples; once
        // that clamp went away -- correctly, it was rigid-body behaviour -- the
        // mismatch became visible.
        //
        // At scale 1, measured from the Panzer IV mesh.
        public const float BaseContactHalfLengthM = 2.947f;
        public const float BaseContactHalfWidthM = 1.485f;
        public const float BaseContactCentreZM = -1.418f;

        /// <summary>
        /// Render scale for a vehicle, mirroring <c>WarArenaBootstrap.BuildTank</c>.
        ///
        /// The renderer scales the whole rig by this, so the suspension's footprint
        /// has to scale with it. When these two drift apart the vehicle drives on a
        /// contact patch of the wrong size, which is the same class of bug as
        /// measuring a car with a bus's wheelbase.
        /// </summary>
        public static float VehicleScale(float massKg)
        {
            return Mathf.Clamp(massKg / 26000f, 0.7f, 1.35f);
        }

        /// <summary>
        /// Contact half-length in metres for a vehicle of the given mass.
        /// </summary>
        public static float ContactHalfLengthM(float massKg)
        {
            return BaseContactHalfLengthM * VehicleScale(massKg);
        }

        /// <summary>
        /// Contact half-width in metres for a vehicle of the given mass.
        /// </summary>
        public static float ContactHalfWidthM(float massKg)
        {
            return BaseContactHalfWidthM * VehicleScale(massKg);
        }

        /// <summary>
        /// Longitudinal offset of the contact patch centre from the model origin.
        /// Negative is rearward, matching the Panzer IV whose origin is forward of
        /// the middle of its tracks.
        /// </summary>
        public static float ContactCentreZM(float massKg)
        {
            return BaseContactCentreZM * VehicleScale(massKg);
        }
        /// <summary>
        /// Height of the centre of gravity above the ground plane, metres. Sets the
        /// tipping limit: tan(theta_max) = h_cg / d_half.
        /// </summary>
        public const float CgHeightM = 1.15f;

        // ---------------------------------------------------------------- terrain

        /// <summary>
        /// Slope of the ground resolved into the vehicle's own heading frame.
        /// </summary>
        /// <remarks>
        /// Both components are exact angles, not small-angle approximations, and both
        /// are signed: positive means the ground rises in that direction.
        ///
        /// The probe arguments are HALF offsets from the centre of the hull — pass
        /// <see cref="HalfGaugeM"/> and <see cref="HalfTrackWidthM"/>, not the overall
        /// wheelbase and track width. Passing the full dimensions samples the terrain
        /// twice as far out, which on any curvature reports a different slope and
        /// silently desynchronises the two language implementations.
        ///
        /// The offsets are the vehicle's own contact geometry rather than an arbitrary
        /// probe distance, so the speed the player feels and the tilt they see are
        /// derived from the same four points and cannot disagree.
        /// </remarks>
        public static void SampleSlopes(
            float x, float z, Vector2 forward, Vector2 right,
            float halfGauge, float halfTrackWidth,
            out float forwardSlopeDeg, out float lateralSlopeDeg)
        {
            forwardSlopeDeg = SlopeAlong(x, z, forward, halfGauge);
            lateralSlopeDeg = SlopeAlong(x, z, right, halfTrackWidth);
        }

        /// <summary>
        /// Signed angle of the ground along a horizontal direction, degrees.
        /// Positive means rising in that direction. Exact, via atan2.
        /// </summary>
        public static float SlopeAlong(float x, float z, Vector2 dir, float probe)
        {
            float ahead = BattlefieldBuilder.GetTerrainHeight(x + dir.x * probe, z + dir.y * probe);
            float behind = BattlefieldBuilder.GetTerrainHeight(x - dir.x * probe, z - dir.y * probe);
            return Mathf.Atan2(ahead - behind, 2f * probe) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Gravity's component along the ground surface, resolved into the vehicle's
        /// heading frame. This is the term that was previously missing entirely.
        /// </summary>
        /// <remarks>
        /// Computed from the surface normal rather than as <c>-g*sin(slope)</c> so it
        /// stays correct on a side slope, where the vehicle accelerates <i>sideways</i>
        /// down the fall line. That sideways pull is what makes a tank on a hillside
        /// want to slide off, and it is the reason a real one turns into the
        /// contour rather than driving straight across.
        ///
        /// As a useful side effect this makes the result conservative at zero input:
        /// the tank runs away downhill on its own, exactly as it should, and the
        /// player has to brake or steer into the slope.
        /// </remarks>
        public static Vector2 GravityOnGroundPlane(Vector2 forward, Vector2 right,
            float forwardSlopeDeg, float lateralSlopeDeg, out float normalLoadG)
        {
            // Surface normal from the two measured gradients. The slope angles are
            // already the exact angles, so the normal follows directly.
            float along = -Mathf.Sin(forwardSlopeDeg * Mathf.Deg2Rad) * Gravity;
            float across = -Mathf.Sin(lateralSlopeDeg * Mathf.Deg2Rad) * Gravity;

            normalLoadG = Mathf.Cos(forwardSlopeDeg * Mathf.Deg2Rad)
                        * Mathf.Cos(lateralSlopeDeg * Mathf.Deg2Rad);

            return new Vector2(along, across);
        }

        // ------------------------------------------------------------ longitudinal

/// <summary>
        /// Engine acceleration along the heading, m/s^2.
        /// </summary>
        /// <remarks>
        /// Modelled as tractive effort rather than a target speed, so the same force
        /// budget pays for climbing. A tank delivers roughly constant tractive force
        /// at low speed and constant *power* above the point where resistance balances
        /// it, so available force falls as 1/v.
        ///
        /// The <paramref name="maxSpeedMps"/> governor matters: without it the
        /// terminal speed is simply maxAccel / rollingResistance, which for the
        /// catalogue values works out around 98 km/h -- more than double the fastest
        /// tank in the roster. In the game the vehicle would accelerate without limit
        /// and every distance in the ballistics model would be scaled wrongly.
        ///
        /// The rated top speed IS the speed at which engine power balances resistance;
        /// past it the engine is simply cut, which is what the governor expresses.
        /// </remarks>
        public static float EngineAcceleration(float throttle, float speedMps, float maxAccel,
            float speedAtMaxPower, float maxSpeedMps)
        {
            float t = Mathf.Clamp(throttle, -1f, 1f);
            if (Mathf.Abs(t) < 1e-4f) return 0f;

            // Past the rated top speed in the direction being asked for, the engine
            // makes no further effort.
            float v = speedMps;
            if (Mathf.Abs(v) >= maxSpeedMps && t * v > 0f) return 0f;

            float spd = Mathf.Max(Mathf.Abs(v), 0.5f);
            float falloff = Mathf.Clamp(speedAtMaxPower / spd, 0.35f, 1f);
            return t * maxAccel * falloff;
        }

        /// <summary>
        /// Rolling resistance acceleration, m/s^2.
        /// </summary>
        /// <remarks>
        /// The coefficient band of 0.03-0.09 is the documented range for tracked
        /// vehicles on firm ground (DTIC TR-06-6 and tank-theory sources). The
        /// resistance is proportional to the normal load rather than to weight, so it
        /// correctly falls away as the vehicle climbs onto a slope and unloads its
        /// tracks.
        ///
        /// Unlike the old model this term is signed by the direction of travel and
        /// therefore applies when coasting, reversing, or rolling backwards — not only
        /// under throttle.
        /// </remarks>
        public static float RollingResistance(float speedMps, float normalLoadG, float coeff)
        {
            if (Mathf.Abs(speedMps) < 1e-3f) return 0f;
            float mag = coeff * normalLoadG * Gravity;
            return -Mathf.Sign(speedMps) * mag;
        }

        // -------------------------------------------------------------- steering

        /// <summary>
        /// Slowest yaw rate achievable by pivoting on the spot, rad/s.
        /// </summary>
        /// <remarks>
        /// A skid-steered vehicle genuinely cannot turn without moving — its tracks
        /// have to push against something. But it CAN neutral-turn, by driving one
        /// track backwards against the other and scrubbing both across the ground.
        /// That costs an enormous amount of energy and is very slow, which is exactly
        /// why it is modelled as a small floor rather than either "no turn at all" or
        /// a free 360-degree pivot.
        ///
        /// Without this the vehicle would be unable to rotate at a standstill, which
        /// combined with the auto-align being gated on motion would leave a player
        /// parked broadside with no way to bring the hull round.
        /// </remarks>
        public const float MinNeutralTurnRateRad = 0.35f;

        /// <summary>
        /// Turn rate available from a skid-steered (differential) input, rad/s.
        /// </summary>
        /// <remarks>
        /// A track-laying vehicle has no steering wheel. It turns by driving one
        /// track faster than the other. If the two track speeds are
        /// <c>v(1 +/- s*k)</c>, the instantaneous centre of rotation sits at
        /// <c>R = L / (2*s*k)</c> — independent of speed — so the turn RADIUS is
        /// roughly constant and the YAW RATE is proportional to speed:
        ///
        /// <c>yaw = (|v| / gauge) * s</c>
        ///
        /// That is the opposite of the intuitive "cars turn better when slow", and
        /// it is what makes a tank feel like a tank: at a crawl it barely rotates,
        /// at speed it swings round hard. It is also why the vehicle cannot rotate
        /// at all when stationary — there is nothing for the tracks to push against.
        ///
        /// On top of that sits a small floor representing the NEUTRAL TURN: the
        /// driver can lock one track and drive the other, pivoting on the spot.
        /// Real tanks do this, but it scrubs both tracks across the ground, so it is
        /// very slow and expensive. Without the floor the vehicle would be unable to
        /// rotate while parked, which combined with the hull auto-align being gated
        /// on motion would leave a player broadside with no way to turn around.
        /// </remarks>
        public static float SkidSteerYawRate(
            float speedMps, float steerNorm, float gaugeM, float maxYawRateRad,
            float speedAtMaxYawRad)
        {
            float s = Mathf.Clamp(steerNorm, -1f, 1f);
            if (Mathf.Abs(s) < 1e-4f) return 0f;

            float v = Mathf.Abs(speedMps);

            // Differential-limited: the yaw the track-speed difference can support.
            float differential = gaugeM > 1e-3f ? v / gaugeM : maxYawRateRad;

            // Above the speed at which full lock no longer uses the whole
            // differential, the excess authority is not usable.
            float authority = 1f / (1f + v / Mathf.Max(speedAtMaxYawRad, 0.5f));
            differential = Mathf.Min(differential, maxYawRateRad * authority);

            // Neutral turn: one track locked, the other driven, scrubbing on the
            // spot. This takes OVER from the differential as speed falls away rather
            // than adding to it -- they are alternative mechanisms for turning the
            // same hull, and summing them would let a crawling vehicle exceed the
            // yaw its tracks could physically produce.
            float neutral = MinNeutralTurnRateRad * Mathf.Exp(-v / Mathf.Max(speedAtMaxYawRad, 0.5f));

            return Mathf.Clamp(Mathf.Max(differential, neutral) * s, -maxYawRateRad, maxYawRateRad);
        }

        /// <summary>
        /// Fraction of top speed lost to track scrub while skidding to turn.
        /// </summary>
        /// <remarks>
        /// Pivot steering at low speed drags the whole track width sideways across the
        /// ground. That is a real and substantial energy loss and it is the reason
        /// neutral turns cost you momentum. Modelling it keeps tight low-speed
        /// handling from being strictly dominant.
        /// </remarks>
        public static float SkidScrubSpeedLoss(float steerNorm, float speedMps)
        {
            float scrub = Mathf.Abs(Mathf.Clamp(steerNorm, -1f, 1f));
            return -scrub * scrub * Mathf.Abs(speedMps) * 0.35f;
        }

        // ---------------------------------------------------------------- limits

        /// <summary>
        /// Critical tilt angle from the CG height over the track contact half-width,
        /// degrees. Past this the vehicle cannot stay on its wheels.
        /// </summary>
        /// <remarks>
        /// TOP 2-2-610 gives the critical angle as <c>B = atan(h / d)</c>. With a CG
        /// height of about 1.15 m and a 1.2 m contact half-width this lands near
        /// 44 degrees, which is why tanks can be driven onto very steep ground and
        /// why the limit in practice is traction, not balance, on firm terrain.
        /// </remarks>
        public static float CriticalTiltDeg(float cgHeightM, float contactHalfWidthM)
        {
            if (contactHalfWidthM <= 1e-3f) return 89f;
            return Mathf.Atan2(cgHeightM, contactHalfWidthM) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Steering authority multiplier on a given side slope.
        /// </summary>
        /// <remarks>
        /// On a side slope a real vehicle is steered partly by the differential and
        /// partly by sliding: the driver turns <i>into</i> the slope. A vehicle already
        /// sliding down the fall line has some of its lateral friction spent before it
        /// turns at all, so the effective authority drops. This is what produces the
        /// characteristic "fight the tank round a hillside" behaviour.
        /// </remarks>
        public static float LateralSlopeSteeringScale(float lateralSlopeDeg)
        {
            float a = Mathf.Abs(lateralSlopeDeg);
            if (a <= 12f) return 1f;
            return Mathf.Lerp(1f, 0.55f, Mathf.Clamp01((a - 12f) / 20f));
        }

        /// <summary>
        /// Lateral acceleration from track friction, m/s^2.
        /// </summary>
        /// <remarks>
        /// This is a STIFFNESS-limited restoring force, not a bang-bang controller:
        ///
        /// <c>a = -clamp(k * v_lat, -grip, +grip)</c>
        ///
        /// A small sideways slip must produce a small correcting force. Returning the
        /// full friction limit for any non-zero slip looks harmless but is not: the
        /// Kamm circle is then saturated by the tiniest cross-slope, leaving zero
        /// budget for longitudinal force, and the vehicle stops accelerating while
        /// still rolling. On any terrain with a lateral gradient that is a permanent
        /// lock-up, not a handling characteristic.
        ///
        /// The stiffness is set so the full friction limit is reached at roughly
        /// half a metre per second of slip, which is firm without being twitchy.
        /// </remarks>
        public static float LateralGripAccel(float lateralSpeedMps, float normalLoadG,
            float coeff, float slipStiffnessPerMps)
        {
            float demand = -lateralSpeedMps * slipStiffnessPerMps;
            float grip = coeff * normalLoadG * Gravity;
            return Mathf.Clamp(demand, -grip, grip);
        }

        /// <summary>
        /// Lateral slip stiffness, m/s^2 per m/s of sideways velocity.
        ///
        /// Set so the full friction limit is reached at about half a metre per second
        /// of slip.
        /// </summary>
        public const float LateralSlipStiffnessPerMps = 16.7f;

        /// <summary>
        /// Longitudinal acceleration actually available once the friction circle is
        /// applied, m/s^2.
        /// </summary>
        /// <remarks>
        /// The Kamm circle: <c>sqrt(F_long^2 + F_lat^2) &lt;= mu * N</c>. Longitudinal
        /// and lateral demand compete for a single friction budget, so a vehicle that
        /// is already sliding sideways cannot accelerate as hard. Without this,
        /// traction is infinite in any direction at once and the handling feels like
        /// driving on rails.
        /// </remarks>
        public static float ApplyFrictionCircle(float desiredLongAccel, float lateralDemandAccel,
            float gripAccel)
        {
            if (gripAccel <= 1e-3f) return 0f;
            float remaining = gripAccel * gripAccel - lateralDemandAccel * lateralDemandAccel;
            if (remaining <= 0f) return 0f;
            float cap = Mathf.Sqrt(remaining);
            return Mathf.Clamp(desiredLongAccel, -cap, cap);
        }
    }
}