//! Gameplay simulations: ground tank combat and air combat.
//!
//! Ported from `Assets/Scripts/Gameplay/`.

mod air_combat_sim;
mod battle_sim;

pub use air_combat_sim::AirCombatSim;
pub use battle_sim::BattleSim;
// Exposed for testing and for the Unity bridge. This was crate-private while the
// module was private, which meant the swept hit test — the thing that decides
// whether a shot connects at all — could not be pinned by a test outside the
// module, and could not be reached from the FFI.
pub use battle_sim::segment_hits_target;
pub use battle_sim::{clamp_to_arena, get_terrain_height, slope_speed_factor};
