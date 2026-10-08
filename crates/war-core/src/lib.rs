//! # war-core
//!
//! Engine-agnostic simulation core for War Arena, ported from the Unity project's
//! C# implementation. Contains no rendering, no engine objects and no
//! `unsafe` outside [`crate::spike`], which is a temporary Phase 0 harness.
//!
//! Module layout maps one-to-one onto the original C# namespaces:
//!
//! | Rust | C# origin |
//! |---|---|
//! | [`math`] | `UnityEngine.Vector3` / `UnityEngine.Mathf` |
//! | `ballistics` | `Assets/Scripts/Ballistics/` |
//! | `vehicles` | `Assets/Scripts/Vehicles/` |
//! | `air` | `Assets/Scripts/Air/` |
//! | `gameplay` | `Assets/Scripts/Gameplay/{BattleSim,AirCombatSim}.cs` |

pub mod math;
pub mod spike;

pub use math::{mathf, Vec3};