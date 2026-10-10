//! # war-core
//!
//! Engine-agnostic simulation core for War Arena, ported from the Unity project's
//! C# implementation. Contains no rendering, no engine objects and no `unsafe`
//! outside [`spike`], which is a temporary Phase 0 harness.
//!
//! Module layout maps onto the original C# namespaces. The phase each one lands
//! in is from `PORT-NOTES.md`:
//!
//! | Rust | C# origin | Phase |
//! |---|---|---|
//! | [`math`] | `UnityEngine.Vector3` / `UnityEngine.Mathf` | 0 |
//! | [`ballistics`] | `Assets/Scripts/Ballistics/` | 1 |
//! | [`vehicles`] | `Assets/Scripts/Vehicles/` | 1 |
//! | [`vehicle_dynamics`] | `Assets/Scripts/Gameplay/TrackedVehicleDynamics.cs` | 3 |
//! | [`hull_suspension`] | `Assets/Scripts/Gameplay/HullSuspension.cs` | 3 |
//! | [`air`] | `Assets/Scripts/Air/` | 1 (catalogue), 2 (flight model) |
//! | `gameplay` | `Assets/Scripts/Gameplay/{BattleSim,AirCombatSim}.cs` | 3, 2 |

pub mod air;
pub mod battlefield;
pub mod ballistics;
pub mod gameplay;
pub mod hull_suspension;
pub mod math;
pub mod spike;
pub mod vehicle_dynamics;
pub mod vehicles;

pub use math::{mathf, Vec3};