//! Damageable subsystems and crew.
//!
//! Ported from `Assets/Scripts/Vehicles/DamageModule.cs`.

mod catalog;
mod modules;

pub use catalog::{VehicleCatalog, VehicleDefinition};
pub use modules::{CrewMember, DamageModule, DamageOutcome, ModuleKind, CREW_RECOVERY_SECONDS};
