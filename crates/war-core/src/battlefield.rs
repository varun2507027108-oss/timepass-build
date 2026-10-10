//! Deterministic obstacle table shared by rendering and collision.
//!
//! Rust mirror of `Assets/Scripts/Gameplay/BattlefieldObstacles.cs`. The two must
//! generate the identical table: the C# side draws the trees from it and the parity
//! harness drives both simulations through the same scenario, so a single differing
//! constant would show up as a position divergence the moment a vehicle touched an
//! obstacle.
//!
//! Each obstacle is a vertical cylinder. That is a poor fit for walls, which are long
//! and thin, but it is exact enough for a tree trunk and cheap enough to evaluate
//! several times per simulation tick.
//!
//! Not covered here: houses, the church, watchtowers, wrecks and rocks. Those come
//! from prefabs whose bounds cannot be regenerated deterministically on this side, and
//! walls are long and thin enough that a circle is a poor stand-in. The C# table has the
//! same gap, deliberately, rather than covering one side and desynchronising.

use crate::math::mathf;

/// Radius of a pine tree's trunk at unit scale, metres.
const TREE_TRUNK_RADIUS: f32 = 1.15;

/// Count of perimeter trees in the forest belt.
pub const PERIMETER_TREE_COUNT: usize = 68;

/// Count of blasted stumps.
pub const STUMP_COUNT: usize = 14;

/// Radius of a blasted stump, metres.
const STUMP_RADIUS: f32 = 0.75;

/// Stump positions, identical to `BattlefieldBuilder.BuildVegetation`.
const STUMP_X: [f32; STUMP_COUNT] = [
    -18.0, -12.0, 14.0, 22.0, -20.0, -28.0, 32.0, 38.0, -35.0, -42.0, 28.0, 16.0, -8.0, 8.0,
];
const STUMP_Z: [f32; STUMP_COUNT] = [
    -10.0, -24.0, 26.0, 18.0, 40.0, 50.0, -22.0, -34.0, 10.0, -5.0, -10.0, -42.0, 32.0, -20.0,
];

/// An upright cylinder the vehicle must not enter.
#[derive(Debug, Clone, Copy)]
pub struct Obstacle {
    pub x: f32,
    pub z: f32,
    pub radius: f32,
}

impl Obstacle {
    /// True once the vehicle's centre is inside this obstacle.
    pub fn contains(&self, px: f32, pz: f32, vehicle_radius: f32) -> bool {
        let dx = px - self.x;
        let dz = pz - self.z;
        let reach = self.radius + vehicle_radius;
        dx * dx + dz * dz < reach * reach
    }
}

fn perimeter_tree_radius(index: usize) -> f32 {
    90.0 + (index % 5) as f32 * 26.0 + mathf::sin(index as f32 * 3.7) * 14.0
}

/// World X of perimeter tree `index`.
pub fn perimeter_tree_x(index: usize) -> f32 {
    let a = index as f32 / PERIMETER_TREE_COUNT as f32 * mathf::PI * 2.0;
    mathf::cos(a) * perimeter_tree_radius(index)
}

/// World Z of perimeter tree `index`.
pub fn perimeter_tree_z(index: usize) -> f32 {
    let a = index as f32 / PERIMETER_TREE_COUNT as f32 * mathf::PI * 2.0;
    mathf::sin(a) * perimeter_tree_radius(index)
}

/// Render scale of perimeter tree `index`.
pub fn perimeter_tree_scale(index: usize) -> f32 {
    0.85 + (index % 7) as f32 * 0.12
}

fn add_stand(list: &mut Vec<Obstacle>, x: f32, z: f32) {
    // Stand trees are placed at scale 1.15 by the renderer.
    list.push(Obstacle {
        x,
        z,
        radius: TREE_TRUNK_RADIUS * 1.15,
    });
}

/// Build the obstacle table.
///
/// Cached by the caller where it matters; this is cheap enough (87 entries, no
/// allocation beyond the vector) to call directly in tests.
pub fn build() -> Vec<Obstacle> {
    let mut list = Vec::with_capacity(PERIMETER_TREE_COUNT + 5 + STUMP_COUNT);

    for i in 0..PERIMETER_TREE_COUNT {
        list.push(Obstacle {
            x: perimeter_tree_x(i),
            z: perimeter_tree_z(i),
            radius: TREE_TRUNK_RADIUS * perimeter_tree_scale(i),
        });
    }

    add_stand(&mut list, 75.0, 85.0);
    add_stand(&mut list, 98.0, 82.0);
    add_stand(&mut list, 105.0, 65.0);
    add_stand(&mut list, 82.0, 58.0);
    add_stand(&mut list, 68.0, 72.0);

    for i in 0..STUMP_COUNT {
        list.push(Obstacle {
            x: STUMP_X[i],
            z: STUMP_Z[i],
            radius: STUMP_RADIUS,
        });
    }

    list
}

/// Half-width of a vehicle's collision footprint, metres.
///
/// Mirrors `BattleSim.VehicleCollisionRadiusM`. This is the contact half-width the
/// suspension uses, so the body that collides is the same size as the body that rides.
pub const VEHICLE_COLLISION_RADIUS_M: f32 = 1.5;

/// Push a vehicle out of any solid obstacle it has entered.
///
/// The vehicle is treated as a circle and the obstacle as a vertical cylinder; overlap
/// is resolved by pushing the vehicle out along the contact normal. Several passes are
/// made because two obstacles can overlap and one pass leaves the vehicle inside the
/// second.
///
/// Returns true if anything was touched.
///
/// The caller rebuilds its longitudinal/lateral velocity components from the resulting
/// position delta, which is what scrubs off the component driving into the obstacle
/// while preserving tangential speed.
pub fn resolve(obstacles: &[Obstacle], pos: &mut (f32, f32), vehicle_radius: f32) -> bool {
    if obstacles.is_empty() {
        return false;
    }

    let mut touched_anywhere = false;

    // Resolve the DEEPEST overlap each pass, one obstacle at a time.
    //
    // Sweeping every obstacle in order looks equivalent but is not. Two adjacent trees
    // have exclusion discs that do not overlap, yet pushing a vehicle out of one TOWARD
    // its neighbour lands it inside that neighbour, and the next sweep pushes it back.
    // Sequential resolution oscillates on any pair of nearby obstacles and runs out of
    // passes still inside one of them. Taking the single deepest violation first
    // converges, because the largest error is always removed before a smaller one can
    // reintroduce it.
    for _ in 0..24 {
        let mut best_index = usize::MAX;
        let mut best_depth = 0.0_f32;

        for (i, o) in obstacles.iter().enumerate() {
            let dx = pos.0 - o.x;
            let dz = pos.1 - o.z;
            let dist_sq = dx * dx + dz * dz;
            let reach = o.radius + vehicle_radius;

            if dist_sq >= reach * reach {
                continue;
            }

            let depth = reach - mathf::sqrt(dist_sq);
            if depth > best_depth {
                best_depth = depth;
                best_index = i;
            }
        }

        if best_index == usize::MAX {
            break;
        }

        let o = obstacles[best_index];
        let dx = pos.0 - o.x;
        let dz = pos.1 - o.z;
        let dist = mathf::sqrt(dx * dx + dz * dz);

        let (nx, nz) = if dist < 1e-4 {
            // Dead centre: no direction to resolve along, so pick one deterministically
            // rather than dividing by zero. The DISTANCE is left at its true value of
            // zero -- overwriting it pushed the vehicle each - 1 instead of each,
            // leaving it a metre inside the obstacle it was being ejected from.
            (1.0_f32, 0.0_f32)
        } else {
            (dx / dist, dz / dist)
        };

        // A hair of separation beyond contact.
        //
        // Resolving to exactly the reach distance leaves the vehicle resting on the
        // boundary, where float rounding can place it a fraction of a millimetre
        // INSIDE, which then reads as a collision that never actually happened. A
        // millimetre of clearance is invisible at any gameplay distance and makes the
        // contact unambiguous.
        const SEPARATION: f32 = 1.0e-3;
        pos.0 += nx * (best_depth + SEPARATION);
        pos.1 += nz * (best_depth + SEPARATION);
        touched_anywhere = true;
    }

    touched_anywhere
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn table_has_the_expected_shape() {
        let o = build();
        assert_eq!(o.len(), PERIMETER_TREE_COUNT + 5 + STUMP_COUNT);
        assert_eq!(o.len(), 87);
    }

    #[test]
    fn perimeter_trees_sit_in_the_expected_belt() {
        // All clustered between roughly 76 m and 220 m from the origin.
        for i in 0..PERIMETER_TREE_COUNT {
            let r = mathf::sqrt(perimeter_tree_x(i).powi(2) + perimeter_tree_z(i).powi(2));
            assert!(r > 70.0 && r < 230.0, "tree {i} at radius {r}");
        }
    }

    #[test]
    fn stumps_sit_inside_the_playable_bowl() {
        // The stumps are the ones a player actually drives into, so they must be within
        // the arena rather than out in the forest belt.
        for i in 0..STUMP_COUNT {
            let r = mathf::sqrt(STUMP_X[i].powi(2) + STUMP_Z[i].powi(2));
            assert!(r < 60.0, "stump {i} at radius {r}");
        }
    }

    #[test]
    fn resolve_pushes_a_vehicle_out_of_a_tree() {
        let o = build();
        let target = &o[0];
        // Start just inside the trunk.
        let mut pos = (target.x, target.z);
        let hit = resolve(&o, &mut pos, VEHICLE_COLLISION_RADIUS_M);
        assert!(hit, "a vehicle centred in a trunk must register a hit");
        assert!(
            !target.contains(pos.0, pos.1, VEHICLE_COLLISION_RADIUS_M),
            "vehicle must end up outside every obstacle"
        );
    }

    #[test]
    fn resolve_leaves_open_ground_alone() {
        let o = build();
        // (0,0) is the middle of the flat combat bowl.
        let mut pos = (0.0_f32, 0.0_f32);
        let hit = resolve(&o, &mut pos, VEHICLE_COLLISION_RADIUS_M);
        assert!(!hit, "open ground must not register a hit");
        assert_eq!(pos, (0.0, 0.0), "open ground must not be moved");
    }

#[test]
    fn diag_obstacle2() {
        let o = build();
        let t = &o[2];
        println!(
            "obstacle2 x={:.3} z={:.3} r={:.3} reach={:.3}",
            t.x,
            t.z,
            t.radius,
            t.radius + VEHICLE_COLLISION_RADIUS_M
        );
        let mut pos = (t.x, t.z);
        resolve(&o, &mut pos, VEHICLE_COLLISION_RADIUS_M);
        println!("final pos=({:.4},{:.4})", pos.0, pos.1);
        for (i, other) in o.iter().enumerate() {
            if other.contains(pos.0, pos.1, VEHICLE_COLLISION_RADIUS_M) {
                let d =
                    ((pos.0 - other.x).powi(2) + (pos.1 - other.z).powi(2)).sqrt();
                let reach = other.radius + VEHICLE_COLLISION_RADIUS_M;
                println!(
                    "  INSIDE {} at ({:.2},{:.2}) r={:.3} dist={:.4} reach={:.4} overshoot={:.4}",
                    i, other.x, other.z, other.radius, d, reach, d - reach
                );
            }
        }
    }
    #[test]
    fn resolve_ends_outside_every_obstacle_not_just_the_first() {
        let o = build();
        for target in o.iter() {
            let mut pos = (target.x, target.z);
            resolve(&o, &mut pos, VEHICLE_COLLISION_RADIUS_M);
            for (i, other) in o.iter().enumerate() {
                let dist = ((pos.0 - other.x).powi(2) + (pos.1 - other.z).powi(2)).sqrt();
                let reach = other.radius + VEHICLE_COLLISION_RADIUS_M;
                assert!(
                    dist >= reach - 1.0e-3,
                    "after escaping obstacle {i} the vehicle was pushed into {dist:.4} < {reach:.4}"
                );
            }
        }
    }
}