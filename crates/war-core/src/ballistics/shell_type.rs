//! Shot types. Armour behaviour is the meaningful difference, not damage numbers:
//! HEAT and HE rounds ignore distance falloff (they punch a hole, then blast),
//! APCBC detonates if it fails to penetrate, APCBC carries a spall cone.
//!
//! **Discriminants are explicit and the order is load-bearing.** The C# original
//! relied on default sequential values, and those values are baked into saved
//! games and any future network protocol. They are pinned here rather than
//! inferred, so reordering the variants cannot silently renumber them.

/// Armour-piercing, capped. Fragile if it fails to penetrate: breaks up inside.
pub const APCBC: u8 = 0;
/// Armour-piercing, high-explosive filler. Detonates 0.5-1.5 m after failing.
pub const APHE: u8 = 1;
/// Armour-piercing, capped and ballistically capped. The WWII workhorse.
pub const APHEBC: u8 = 2;
/// Armour-piercing, composite, no explosive. Post-penetration energy keeps killing.
pub const APC: u8 = 3;
/// Armour-piercing, capped, sub-calibre core. High penetration, tiny spall.
pub const APCR: u8 = 4;
/// Shaped-charge. Penetration is constant with range; jet effect burns crew.
pub const HEAT: u8 = 5;
/// High explosive. Fragments; only useful against thin plate and crew.
pub const HE: u8 = 6;

/// Shot type. See the module docs for why the discriminants are explicit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum ShellType {
    Apcbc = APCBC,
    Aphe = APHE,
    Aphebc = APHEBC,
    Apc = APC,
    Apcr = APCR,
    Heat = HEAT,
    He = HE,
}

impl ShellType {
    /// Every variant, in declaration order.
    pub const ALL: [ShellType; 7] = [
        ShellType::Apcbc,
        ShellType::Aphe,
        ShellType::Aphebc,
        ShellType::Apc,
        ShellType::Apcr,
        ShellType::Heat,
        ShellType::He,
    ];

    /// The discriminant, for serialisation.
    pub fn as_u8(self) -> u8 {
        self as u8
    }

    /// Normalisation angle for this shell family, in degrees. Positive values
    /// normalise, i.e. the round bites in and gains.
    ///
    /// Plate resistances are published for a round arriving square to the plate.
    /// In reality the nose does not stay aligned with the velocity vector at
    /// impact: a blunt or capped nose bites into the plate and rotates *toward*
    /// the surface normal, so the plate presents less effective depth than its
    /// geometry alone suggests. A sharp uncapped nose is pushed the other way.
    ///
    /// The effect is small in degrees and decisive in millimetres, because
    /// resistance is 1/cos(theta). Five degrees of normalisation against a 60
    /// degree glacis moves the effective angle to 55 degrees, worth about 13
    /// percent of required penetration: the difference between defeating a
    /// Panther's glacis and bouncing off it.
    ///
    /// Applied to the plate rather than to the round's penetration figure,
    /// because normalisation is a property of how the nose meets the plate.
    pub fn normalisation_deg(self) -> f32 {
        match self {
            // Soviet blunt-nosed APBC, notable for biting hard.
            ShellType::Apcbc => 6.0,
            ShellType::Aphebc => 5.0,
            ShellType::Aphe => 4.0,
            // Plain capped AP: small cap, modest bite.
            ShellType::Apc => 3.0,
            // APCR discards its carrier and presents a sharp long rod, which is
            // pushed away from the plate. This is why sub-calibre rounds are so
            // unforgiving of sloped armour despite their high quoted figure.
            ShellType::Apcr => -2.0,
            // A shaped-charge jet is not a projectile with a nose at all: it forms
            // on the plate normal however oblique the impact, so it neither
            // normalises nor denormalises.
            ShellType::Heat | ShellType::He => 0.0,
        }
    }

    /// Critical ricochet angle for this shell family, degrees of impact.
    ///
    /// A single global threshold is wrong for most of the roster. A sharp
    /// sub-calibre rod skips off plate at angles where a blunt projectile still
    /// punches in, and a shaped-charge jet does not bounce at all because it has
    /// already collapsed onto the plate normal. Collapsing these into one number
    /// quietly made APCR far too usable against sloped armour and HEAT far too
    /// vulnerable to it.
    pub fn critical_ricochet_deg(self) -> f32 {
        match self {
            // Sharp rod: skips earliest.
            ShellType::Apcr => 60.0,
            // Blunt APBC bites, so it tolerates the steepest kinetic plate.
            ShellType::Apcbc => 73.0,
            ShellType::Aphebc => 71.0,
            ShellType::Aphe => 70.0,
            ShellType::Apc => 68.0,
            // The jet has collapsed onto the normal, so it does not bounce.
            ShellType::Heat | ShellType::He => 89.0,
        }
    }

    /// True when the round forms its effect on the plate normal and therefore
    /// ignores slope entirely.
    ///
    /// A shaped-charge jet collapses onto the target surface normal however
    /// oblique the firing angle, so sloped armour buys it nothing. That is why
    /// HEAT dominates heavy sloped frontage that stops every kinetic round in
    /// the roster, and it is a real tactical distinction rather than a
    /// convenience for the model.
    pub fn ignores_slope(self) -> bool {
        matches!(self, ShellType::Heat | ShellType::He)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn discriminants_match_the_csharp_declaration_order() {
        // The C# enum relied on default sequential values. Pin them so a
        // reordering here cannot silently renumber anything.
        assert_eq!(ShellType::Apcbc.as_u8(), 0);
        assert_eq!(ShellType::Aphe.as_u8(), 1);
        assert_eq!(ShellType::Aphebc.as_u8(), 2);
        assert_eq!(ShellType::Apc.as_u8(), 3);
        assert_eq!(ShellType::Apcr.as_u8(), 4);
        assert_eq!(ShellType::Heat.as_u8(), 5);
        assert_eq!(ShellType::He.as_u8(), 6);
    }

    #[test]
    fn all_is_complete_and_in_discriminant_order() {
        for (index, kind) in ShellType::ALL.iter().enumerate() {
            assert_eq!(kind.as_u8() as usize, index);
        }
    }

    #[test]
    fn normalisation_direction_is_as_documented() {
        // Blunt noses bite in; the sharp rod is pushed away.
        assert!(ShellType::Apcbc.normalisation_deg() > 0.0);
        assert!(ShellType::Aphebc.normalisation_deg() > 0.0);
        assert!(ShellType::Apc.normalisation_deg() > 0.0);
        assert!(ShellType::Aphe.normalisation_deg() > 0.0);
        assert!(ShellType::Apcr.normalisation_deg() < 0.0);
        assert_eq!(ShellType::Heat.normalisation_deg(), 0.0);
        assert_eq!(ShellType::He.normalisation_deg(), 0.0);
    }

    #[test]
    fn only_jet_and_he_ignore_slope() {
        for kind in ShellType::ALL {
            let expected = matches!(kind, ShellType::Heat | ShellType::He);
            assert_eq!(kind.ignores_slope(), expected, "{kind:?}");
        }
    }

    #[test]
    fn apcr_ricochets_earliest_and_apcbc_latest() {
        assert!(ShellType::Apcr.critical_ricochet_deg() < ShellType::Apc.critical_ricochet_deg());
        assert!(ShellType::Apcbc.critical_ricochet_deg() > ShellType::Apc.critical_ricochet_deg());
        // The jet does not bounce at any playable angle.
        assert_eq!(ShellType::Heat.critical_ricochet_deg(), 89.0);
    }
}