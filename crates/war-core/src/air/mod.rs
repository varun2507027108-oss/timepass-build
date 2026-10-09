//! Aircraft roster and flight model.
//!
//! Ported from `Assets/Scripts/Air/AircraftCatalog.cs` and `FlightModel.cs`. The
//! Me 262 and Meteor pairing is the whole point of the roster: the 262 was ~150 mph
//! faster and climbed far better, but the Meteor could turn with it, which is why
//! the Meteor ended up the better fighter in the air even though it was outclassed
//! on paper.

mod catalog;
mod flight_model;

pub use catalog::{AircraftCatalog, AircraftClass, AircraftDefinition};
pub use flight_model::{air_density, FlightModel};
