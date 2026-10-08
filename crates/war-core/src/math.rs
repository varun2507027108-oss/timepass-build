//! Faithful reimplementation of the slice of `UnityEngine` that the War Arena
//! simulation core actually touches.
//!
//! The port rule for this file is **bit-level parity with `UnityEngine.Mathf`
//! and `UnityEngine.Vector3`**, not mathematical elegance. Two details matter
//! far more than they look:
//!
//! 1. **Unity's transcendental functions are computed in `double` and narrowed
//!    to `float` on return.** `Mathf.Sin` is literally `(float)Math.Sin(f)`,
//!    where `Math.Sin` takes a `double`. Calling Rust's `f32::sin()` computes in
//!    single precision and can differ by 1 ULP. Over 20 000 integration steps in
//!    [`crate::spike::SpikeSim`] and later in the flight model, that is enough to
//!    change a stall verdict. Every transcendental here therefore widens to
//!    `f64`, computes, and narrows.
//!
//! 2. **`Mathf.Deg2Rad` and `Mathf.Rad2Deg` are rounded literals, not
//!    reciprocals.** Unity hard-codes `0.0174532924f` and `57.29578f`; the exact
//!    reciprocal is `57.29577951...`. Using `f32::to_degrees()` (which
//!    multiplies by a precise `PI`) would diverge.
//!
//! Anything Unity does *not* expose — `Quaternion`, `Transform`, `Random`,
//! `MonoBehaviour` — is deliberately absent. Those are either engine objects or
//! are handled at the FFI boundary; see `PORT-NOTES.md`.

use std::ops::{Add, AddAssign, Div, Mul, Neg, Sub, SubAssign};

/// Below this magnitude, Unity's `Normalize` returns the zero vector rather
/// than dividing. Matches `UnityEngine.Vector3.kEpsilon`.
pub const VECTOR3_EPSILON: f32 = 1e-5;

/// Below this squared distance, Unity's `Distance` returns exactly `0`.
/// Matches the `9.99999944E-11f` literal in `UnityEngine.Vector3.Distance`.
pub const DISTANCE_SQR_EPSILON: f32 = 9.999_999_5e-11;

/// Below this denominator, Unity's `Angle` returns exactly `0` degrees.
pub const ANGLE_EPSILON: f32 = 1e-15;

// ---------------------------------------------------------------------------
// Mathf
// ---------------------------------------------------------------------------

/// `UnityEngine.Mathf`, restricted to the members the simulation calls.
pub mod mathf {
    /// `UnityEngine.Mathf.PI` — a rounded `f32` literal, not `PI as f32`.
    pub const PI: f32 = 3.141_592_7;

    /// `UnityEngine.Mathf.Deg2Rad`. Rounded literal; **not** `1 / (PI / 180)`.
    pub const DEG2RAD: f32 = 0.017_453_292;

    /// `UnityEngine.Mathf.Rad2Deg`. Rounded literal, deliberately *not* the
    /// exact reciprocal of [`DEG2RAD`].
    pub const RAD2DEG: f32 = 57.295_78;

    #[inline]
    pub fn abs(f: f32) -> f32 {
        f32::abs(f)
    }

    #[inline]
    pub fn sign(f: f32) -> f32 {
        // Unity: `return f >= 0 ? 1 : -1;` — note that positive zero gives 1 and
        // negative zero also gives 1, unlike IEEE signbit.
        if f >= 0.0 { 1.0 } else { -1.0 }
    }

    #[inline]
    pub fn min(a: f32, b: f32) -> f32 {
        if a < b { a } else { b }
    }

    #[inline]
    pub fn max(a: f32, b: f32) -> f32 {
        if a > b { a } else { b }
    }

    #[inline]
    pub fn floor(f: f32) -> f32 {
        (f as f64).floor() as f32
    }

    #[inline]
    pub fn floor_to_int(f: f32) -> i32 {
        (f as f64).floor() as i32
    }

    /// Unity: `return value < min ? min : (value > max ? max : value);`
    #[inline]
    pub fn clamp(value: f32, min: f32, max: f32) -> f32 {
        if value < min {
            min
        } else if value > max {
            max
        } else {
            value
        }
    }

    /// Unity: `Clamp` with `min = 0`, `max = 1`.
    #[inline]
    pub fn clamp01(value: f32) -> f32 {
        if value < 0.0 {
            0.0
        } else if value > 1.0 {
            1.0
        } else {
            value
        }
    }

    /// Unity: `a + (b - a) * Clamp01(t)`.
    #[inline]
    pub fn lerp(a: f32, b: f32, t: f32) -> f32 {
        a + (b - a) * clamp01(t)
    }

    /// Unity: `a + (b - a) * t` — no clamping of `t`.
    #[inline]
    pub fn lerp_unclamped(a: f32, b: f32, t: f32) -> f32 {
        a + (b - a) * t
    }

    /// Unity: `if (Abs(target - current) <= maxDelta) return target;`
    #[inline]
    pub fn move_towards(current: f32, target: f32, max_delta: f32) -> f32 {
        if (target - current).abs() <= max_delta {
            target
        } else {
            current + sign(target - current) * max_delta
        }
    }

    /// Unity's `Repeat`: `Clamp(t - Floor(t / length) * length, 0, length)`.
    #[inline]
    pub fn repeat(t: f32, length: f32) -> f32 {
        clamp(t - floor(t / length) * length, 0.0, length)
    }

    /// Unity's `DeltaAngle`: the shortest signed difference from `current` to
    /// `target`, in the range (-180, 180].
    #[inline]
    pub fn delta_angle(current: f32, target: f32) -> f32 {
        let mut delta = repeat(target - current, 360.0);
        if delta > 180.0 {
            delta -= 360.0;
        }
        delta
    }

    // -- Transcendentals: computed in f64, narrowed to f32, exactly like Unity.

    #[inline]
    pub fn sin(f: f32) -> f32 {
        (f as f64).sin() as f32
    }

    #[inline]
    pub fn cos(f: f32) -> f32 {
        (f as f64).cos() as f32
    }

    #[inline]
    pub fn tan(f: f32) -> f32 {
        (f as f64).tan() as f32
    }

    #[inline]
    pub fn asin(f: f32) -> f32 {
        (f as f64).asin() as f32
    }

    #[inline]
    pub fn acos(f: f32) -> f32 {
        (f as f64).acos() as f32
    }

    #[inline]
    pub fn atan(f: f32) -> f32 {
        (f as f64).atan() as f32
    }

    #[inline]
    pub fn atan2(y: f32, x: f32) -> f32 {
        (y as f64).atan2(x as f64) as f32
    }

    #[inline]
    pub fn exp(power: f32) -> f32 {
        (power as f64).exp() as f32
    }

    #[inline]
    pub fn log(f: f32) -> f32 {
        (f as f64).ln() as f32
    }

    #[inline]
    pub fn pow(f: f32, p: f32) -> f32 {
        (f as f64).powf(p as f64) as f32
    }

    /// Unity's `Mathf.Sqrt` is `(float)Math.Sqrt(f)` — a widening round-trip,
    /// but IEEE guarantees `f32::sqrt` is correctly rounded to the same result.
    #[inline]
    pub fn sqrt(f: f32) -> f32 {
        f32::sqrt(f)
    }
}

// ---------------------------------------------------------------------------
// Vector3
// ---------------------------------------------------------------------------

/// `UnityEngine.Vector3` — a left-handed, `f32` 3-vector with Unity's exact
/// operator and method semantics.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
#[repr(C)]
pub struct Vec3 {
    pub x: f32,
    pub y: f32,
    pub z: f32,
}

impl Vec3 {
    pub const ZERO: Vec3 = Vec3 { x: 0.0, y: 0.0, z: 0.0 };
    pub const ONE: Vec3 = Vec3 { x: 1.0, y: 1.0, z: 1.0 };

    /// Unity's `+Y`.
    pub const UP: Vec3 = Vec3 { x: 0.0, y: 1.0, z: 0.0 };
    /// Unity's `-Y`.
    pub const DOWN: Vec3 = Vec3 { x: 0.0, y: -1.0, z: 0.0 };
    /// Unity's `+Z` — the direction a `GameObject` with identity rotation faces.
    pub const FORWARD: Vec3 = Vec3 { x: 0.0, y: 0.0, z: 1.0 };
    /// Unity's `-Z`.
    pub const BACK: Vec3 = Vec3 { x: 0.0, y: 0.0, z: -1.0 };
    /// Unity's `+X`.
    pub const RIGHT: Vec3 = Vec3 { x: 1.0, y: 0.0, z: 0.0 };
    /// Unity's `-X`.
    pub const LEFT: Vec3 = Vec3 { x: -1.0, y: 0.0, z: 0.0 };

    #[inline]
    pub const fn new(x: f32, y: f32, z: f32) -> Vec3 {
        Vec3 { x, y, z }
    }

    #[inline]
    pub fn dot(self, other: Vec3) -> f32 {
        self.x * other.x + self.y * other.y + self.z * other.z
    }

    /// Unity's `Cross`, using the left-handed convention that
    /// `Cross(RIGHT, UP) == FORWARD`.
    #[inline]
    pub fn cross(self, other: Vec3) -> Vec3 {
        Vec3 {
            x: self.y * other.z - self.z * other.y,
            y: self.z * other.x - self.x * other.z,
            z: self.x * other.y - self.y * other.x,
        }
    }

    #[inline]
    pub fn sqr_magnitude(self) -> f32 {
        self.x * self.x + self.y * self.y + self.z * self.z
    }

    #[inline]
    pub fn magnitude(self) -> f32 {
        mathf::sqrt(self.sqr_magnitude())
    }

    /// Unity's `Normalize`: returns [`Vec3::ZERO`] at or below
    /// [`VECTOR3_EPSILON`].
    pub fn normalize(self) -> Vec3 {
        let mag = self.magnitude();
        if mag > VECTOR3_EPSILON {
            self / mag
        } else {
            Vec3::ZERO
        }
    }

    /// Unity's `normalized` property.
    #[inline]
    pub fn normalized(self) -> Vec3 {
        self.normalize()
    }

    /// Unity's `Vector3.Distance`, including its near-zero short circuit.
    pub fn distance(a: Vec3, b: Vec3) -> f32 {
        let dist_sqr = (a - b).sqr_magnitude();
        if dist_sqr < DISTANCE_SQR_EPSILON {
            0.0
        } else {
            mathf::sqrt(dist_sqr)
        }
    }

    /// Unity's `Vector3.Scale` — component-wise multiply.
    #[inline]
    pub fn scale(a: Vec3, b: Vec3) -> Vec3 {
        Vec3::new(a.x * b.x, a.y * b.y, a.z * b.z)
    }

    /// Unity's `Vector3.Lerp` — `Clamp01` on `t`.
    #[inline]
    pub fn lerp(a: Vec3, b: Vec3, t: f32) -> Vec3 {
        a + (b - a) * mathf::clamp01(t)
    }

    /// Unity's `Vector3.LerpUnclamped`.
    #[inline]
    pub fn lerp_unclamped(a: Vec3, b: Vec3, t: f32) -> Vec3 {
        a + (b - a) * t
    }

    /// Unity's `Vector3.ProjectOnPlane`: `vector - planeNormal * Dot(planeNormal, vector)`.
    #[inline]
    pub fn project_on_plane(vector: Vec3, plane_normal: Vec3) -> Vec3 {
        let d = plane_normal.dot(vector);
        vector - plane_normal * d
    }

    /// Unity's `Vector3.Angle` — the unsigned angle between two vectors, in
    /// degrees, returning `0` for degenerate input.
    pub fn angle(from: Vec3, to: Vec3) -> f32 {
        let denom = mathf::sqrt(from.sqr_magnitude() * to.sqr_magnitude());
        if denom < ANGLE_EPSILON {
            return 0.0;
        }
        let cos = mathf::clamp(from.dot(to) / denom, -1.0, 1.0);
        mathf::RAD2DEG * mathf::acos(cos)
    }

    /// Unity's `Vector3.SignedAngle` — the sign comes from
    /// `Sign(Dot(axis, Cross(from, to)))`, so a positive result means `to` is
    /// rotated *towards* `axis` from `from`.
    pub fn signed_angle(from: Vec3, to: Vec3, axis: Vec3) -> f32 {
        let sign = mathf::sign(axis.dot(from.cross(to)));
        sign * Self::angle(from, to)
    }
}

impl Add for Vec3 {
    type Output = Vec3;
    #[inline]
    fn add(self, rhs: Vec3) -> Vec3 {
        Vec3::new(self.x + rhs.x, self.y + rhs.y, self.z + rhs.z)
    }
}

impl Sub for Vec3 {
    type Output = Vec3;
    #[inline]
    fn sub(self, rhs: Vec3) -> Vec3 {
        Vec3::new(self.x - rhs.x, self.y - rhs.y, self.z - rhs.z)
    }
}

impl Mul<f32> for Vec3 {
    type Output = Vec3;
    #[inline]
    fn mul(self, rhs: f32) -> Vec3 {
        Vec3::new(self.x * rhs, self.y * rhs, self.z * rhs)
    }
}

impl Mul<Vec3> for f32 {
    type Output = Vec3;
    #[inline]
    fn mul(self, rhs: Vec3) -> Vec3 {
        rhs * self
    }
}

impl Div<f32> for Vec3 {
    type Output = Vec3;
    #[inline]
    fn div(self, rhs: f32) -> Vec3 {
        Vec3::new(self.x / rhs, self.y / rhs, self.z / rhs)
    }
}

impl Neg for Vec3 {
    type Output = Vec3;
    #[inline]
    fn neg(self) -> Vec3 {
        Vec3::new(-self.x, -self.y, -self.z)
    }
}

impl AddAssign for Vec3 {
    #[inline]
    fn add_assign(&mut self, rhs: Vec3) {
        *self = *self + rhs;
    }
}

impl SubAssign for Vec3 {
    #[inline]
    fn sub_assign(&mut self, rhs: Vec3) {
        *self = *self - rhs;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Unity's `Mathf` constants are rounded literals; pinning them stops a
    /// future "tidy up" from silently shifting every angle in the game.
    #[test]
    fn deg2rad_and_rad2deg_are_unity_literals() {
        assert_eq!(mathf::DEG2RAD, 0.017_453_292_4_f32);
        assert_eq!(mathf::RAD2DEG, 57.295_78_f32);

        // The real-number reciprocals differ. Note this has to be checked in
        // `f64`: once both are narrowed to `f32`, `1.0 / DEG2RAD` and `RAD2DEG`
        // happen to round to the same value, so an `f32` assertion here would
        // silently prove nothing.
        let exact_recip = 1.0_f64 / 0.017_453_292_4_f64;
        assert!(
            (exact_recip - 57.295_78).abs() > 1e-9,
            "RAD2DEG is a rounded literal, not the exact reciprocal"
        );
    }

    /// The contract that actually matters: our transcendentals are computed in
    /// `f64` and narrowed, exactly as Unity's `Mathf` does. This holds on every
    /// platform regardless of how that platform's `f32` trig happens to behave.
    #[test]
    fn transcendentals_match_the_widened_double_formula() {
        let samples = [
            0.0f32,
            1.0,
            -3.5,
            1.0e-4,
            12.25,
            0.785398_16,
            1.570_796_3,
            3.141_592_7,
            -2.718_281_7,
            1.047_197_5,
        ];
        for x in samples {
            assert_eq!(
                mathf::sin(x).to_bits(),
                ((x as f64).sin() as f32).to_bits(),
                "sin({x}) must go through f64"
            );
            assert_eq!(
                mathf::cos(x).to_bits(),
                ((x as f64).cos() as f32).to_bits(),
                "cos({x}) must go through f64"
            );
            assert_eq!(
                mathf::atan2(x, 1.75).to_bits(),
                ((x as f64).atan2(1.75_f64) as f32).to_bits(),
                "atan2 must go through f64"
            );
            assert_eq!(
                mathf::exp(x).to_bits(),
                ((x as f64).exp() as f32).to_bits(),
                "exp({x}) must go through f64"
            );
        }
        for a in [-1.0f32, -0.5, -1e-7, 0.0, 0.25, 0.5, 0.999_999_9, 1.0] {
            assert_eq!(
                mathf::asin(a).to_bits(),
                ((a as f64).asin() as f32).to_bits(),
                "asin({a}) must go through f64"
            );
            assert_eq!(
                mathf::acos(a).to_bits(),
                ((a as f64).acos() as f32).to_bits(),
                "acos({a}) must go through f64"
            );
        }
    }

    /// Justifies the `f64` widening as a deliberate choice rather than cargo
    /// cult: single-precision transcendentals genuinely disagree with the
    /// widened-and-narrowed result on a large fraction of inputs, so calling
    /// `f32::sin` would have silently changed the simulation.
    ///
    /// Measured over 2,000,000 inputs on x86_64 Windows:
    /// `asin` diverges on ~6.3%, `sin`/`cos` on ~0.13%. `asin`/`acos` are the
    /// ones that matter — `FlightModel` takes an `Asin` on the flight-path angle
    /// of every single step, and `Penetration` an `Acos` on every shot.
    #[test]
    fn single_precision_transcendentals_would_have_diverged() {
        let samples = 2_000_000u32;
        let mut asin_divergent = 0u32;
        let mut sin_divergent = 0u32;
        for i in 0..samples {
            let x = (i as f32) * 0.000_37 - 370.0;
            if x != 0.0 {
                if ((x as f64).sin() as f32).to_bits() != f32::sin(x).to_bits() {
                    sin_divergent += 1;
                }
                let a = (x / 370.0).clamp(-1.0, 1.0);
                if ((a as f64).asin() as f32).to_bits() != f32::asin(a).to_bits() {
                    asin_divergent += 1;
                }
            }
        }
        assert!(
            asin_divergent > 0,
            "expected f32::asin to diverge from the widened form on some inputs; \
             if this now passes trivially the widening may no longer be load-bearing"
        );
        assert!(
            sin_divergent > 0,
            "expected f32::sin to diverge from the widened form on some inputs"
        );
        // Guard the magnitude, not just the sign: if this drops to a handful of
        // inputs the parity risk has changed character and the constants here
        // should be re-derived.
        assert!(
            asin_divergent as f64 / samples as f64 > 0.01,
            "asin divergence rate fell to {}/{}; re-derive the constants",
            asin_divergent,
            samples
        );
    }

    #[test]
    fn sign_matches_unity_not_ieee() {
        assert_eq!(mathf::sign(0.0), 1.0);
        assert_eq!(mathf::sign(-0.0), 1.0);
        assert_eq!(mathf::sign(-2.0), -1.0);
    }

    #[test]
    fn cross_is_left_handed() {
        // Unity: Cross(RIGHT, UP) == FORWARD.
        assert_eq!(Vec3::RIGHT.cross(Vec3::UP), Vec3::FORWARD);
    }

    #[test]
    fn normalize_returns_zero_below_epsilon() {
        assert_eq!(Vec3::new(1e-6, 0.0, 0.0).normalized(), Vec3::ZERO);
        assert_eq!(Vec3::new(3.0, 4.0, 0.0).normalized(), Vec3::new(0.6, 0.8, 0.0));
    }

    #[test]
    fn distance_short_circuits_near_zero() {
        assert_eq!(Vec3::distance(Vec3::ZERO, Vec3::new(1e-7, 0.0, 0.0)), 0.0);
        assert_eq!(Vec3::distance(Vec3::ZERO, Vec3::new(3.0, 4.0, 0.0)), 5.0);
    }

    #[test]
    fn lerp_clamps_the_weight() {
        assert_eq!(mathf::lerp(0.0, 10.0, 2.0), 10.0);
        assert_eq!(mathf::lerp(0.0, 10.0, -1.0), 0.0);
        assert_eq!(mathf::lerp_unclamped(0.0, 10.0, 2.0), 20.0);
    }

    #[test]
    fn delta_angle_wraps_through_the_seam() {
        assert!((mathf::delta_angle(179.0, -179.0) - 2.0).abs() < 1e-4);
        assert!((mathf::delta_angle(-179.0, 179.0) + 2.0).abs() < 1e-4);
        assert!((mathf::delta_angle(0.0, 90.0) - 90.0).abs() < 1e-4);
    }

    #[test]
    fn move_towards_snaps_then_steps() {
        assert_eq!(mathf::move_towards(0.0, 1.0, 5.0), 1.0);
        assert_eq!(mathf::move_towards(0.0, 1.0, 0.25), 0.25);
        assert_eq!(mathf::move_towards(0.0, -1.0, 0.25), -0.25);
    }

    #[test]
    fn signed_angle_takes_its_sign_from_the_axis() {
        let from = Vec3::FORWARD;
        let to_left = Vec3::LEFT;
        // Rotating FORWARD towards UP is a positive angle.
        let pos = Vec3::signed_angle(from, Vec3::UP, Vec3::UP);
        assert!(pos > 89.0 && pos < 91.0, "got {pos}");
        let neg = Vec3::signed_angle(from, to_left, Vec3::UP);
        assert!(neg < -89.0 && neg > -91.0, "got {neg}");
    }

    #[test]
    fn project_on_plane_removes_the_normal_component() {
        let v = Vec3::new(1.0, 5.0, 2.0);
        let p = Vec3::project_on_plane(v, Vec3::UP);
        assert!((p.y).abs() < 1e-6, "y should be removed, got {}", p.y);
        assert!((p.x - 1.0).abs() < 1e-6);
        assert!((p.z - 2.0).abs() < 1e-6);
    }
}