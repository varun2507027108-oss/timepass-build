//! Terminal ballistics: ordnance and plate resolution.
//!
//! Ported from `Assets/Scripts/Ballistics/` (`Shell.cs`, `Penetration.cs`).

mod penetration;
mod shell;
mod shell_type;

pub use penetration::{ArmourPlate, ImpactReport, ImpactResult, Penetration};
pub use shell::{
    Shell, ShellType, SOLID_SHOT_DRAG, SUBCALIBRE_DRAG, TYPICAL_TANK_ROUND_DRAG,
};