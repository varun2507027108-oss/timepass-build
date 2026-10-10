//! Gameplay simulations: ground tank combat and air combat.
//!
//! Ported from `Assets/Scripts/Gameplay/`.

mod air_combat_sim;
mod battle_sim;

pub use air_combat_sim::AirCombatSim;
pub use battle_sim::BattleSim;
