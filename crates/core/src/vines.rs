//! The original deterministic pixel vines, anchored to the paper's bottom-right corner.

use std::collections::HashSet;
use std::sync::OnceLock;

pub const BOTTOM: usize = 0;
pub const RIGHT: usize = 1;
pub const STEM: u8 = 1;
pub const LEAF: u8 = 2;
pub const SHINE: u8 = 3;
pub const BUD: u8 = 4;
pub const STEM_DEPTH: i32 = -1;
pub const MAX_INSIDE: i32 = 1;
pub const MAX_OUTSIDE: i32 = 4;
const CAPACITY: i32 = 400;

/// Coordinates count left and up from the paper's right and bottom edges.
/// Negative cells hang outside the paper. `at` is the growth step revealing it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Cell {
    pub x: i32,
    pub y: i32,
    pub color: u8,
    pub at: i32,
}

type Shape = &'static [(i32, i32, u8)];
const ROUND: Shape = &[
    (0, 1, STEM),
    (1, 1, LEAF),
    (0, 2, LEAF),
    (1, 2, SHINE),
    (2, 2, LEAF),
    (1, 3, LEAF),
    (2, 3, LEAF),
];
const LONG: Shape = &[
    (0, 1, STEM),
    (1, 1, LEAF),
    (1, 2, LEAF),
    (2, 2, SHINE),
    (2, 3, LEAF),
];
const SMALL: Shape = &[(0, 1, STEM), (1, 1, LEAF), (1, 2, SHINE), (2, 2, LEAF)];
const TWIN: Shape = &[(0, 1, STEM), (-1, 2, LEAF), (1, 2, SHINE)];
const TAPER: [Shape; 3] = [
    &[(0, 1, STEM), (1, 1, LEAF), (1, 2, SHINE)],
    &[(0, 1, LEAF), (1, 2, LEAF)],
    &[(1, 1, LEAF)],
];
const OUTSIDE: [Shape; 4] = [ROUND, ROUND, ROUND, LONG];
const INSIDE: [Shape; 4] = [SMALL, SMALL, TWIN, TAPER[0]];

#[derive(Debug)]
struct LeafPlan {
    at: i32,
    inside: bool,
    shape: usize,
    bud_after: bool,
}

#[derive(Debug)]
struct Plan {
    leaves: Vec<LeafPlan>,
    droops: Vec<(i32, i32)>,
}

static PLANS: OnceLock<[Plan; 2]> = OnceLock::new();

/// Each vine reaches 85 percent of its edge, up to the original 400-cell plan.
pub fn length_for(edge_cells: i32) -> i32 {
    (f64::from(edge_cells) * 0.85)
        .round_ties_even()
        .clamp(0.0, f64::from(CAPACITY)) as i32
}

/// Builds cells in stable growth order. Hosts should cache by vine and length.
///
/// # Panics
/// Panics if `vine` is neither [`BOTTOM`] nor [`RIGHT`].
pub fn cells(vine: usize, length: i32) -> Vec<Cell> {
    assert!(vine <= RIGHT, "unknown vine index");
    let length = length.clamp(0, CAPACITY);
    let mut cells = Vec::new();
    let mut stem = HashSet::new();
    if vine == BOTTOM {
        cells.extend([
            Cell {
                x: -1,
                y: -1,
                color: STEM,
                at: 0,
            },
            Cell {
                x: 0,
                y: 0,
                color: STEM,
                at: 1,
            },
            Cell {
                x: 1,
                y: 0,
                color: LEAF,
                at: 2,
            },
            Cell {
                x: 0,
                y: 1,
                color: LEAF,
                at: 2,
            },
            Cell {
                x: 1,
                y: 1,
                color: SHINE,
                at: 2,
            },
        ]);
    }
    if length < 2 {
        return sorted(cells);
    }
    let taper = length - 2.max((f64::from(length) * 0.2).ceil() as i32);
    let plans = PLANS.get_or_init(|| [plan(0x5EED_B077), plan(0x0BAD_F00D)]);
    let plan = &plans[vine];
    let mut depth = vec![STEM_DEPTH; usize::try_from(length).expect("length is clamped")];
    for &(from, to) in &plan.droops {
        if to + 1 >= taper {
            break;
        }
        for index in from..=to {
            depth[usize::try_from(index).expect("positive droop")] = STEM_DEPTH - 1;
        }
    }
    let mut put = |t: i32, d: i32, color: u8, at: i32| {
        if !(-MAX_OUTSIDE..=MAX_INSIDE).contains(&d) || t < 0 {
            return;
        }
        let key = (t, d);
        if if color == STEM {
            !stem.insert(key)
        } else {
            stem.contains(&key)
        } {
            return;
        }
        let (x, y) = if vine == BOTTOM { (t, d) } else { (d, t) };
        cells.push(Cell {
            x,
            y,
            color,
            at: at.min(length),
        });
    };
    for (index, &d) in depth.iter().enumerate() {
        let i = i32::try_from(index).expect("bounded plan index");
        if index > 0 && d != depth[index - 1] {
            put(i, depth[index - 1], STEM, i);
        }
        put(i, d, STEM, i);
    }
    let mut kept = i32::MIN / 2;
    let mut last_side = false;
    for (index, leaf) in plan.leaves.iter().enumerate() {
        let at = leaf.at;
        if at + 2 > length - 2 {
            break;
        }
        let s = depth[usize::try_from(at).expect("positive leaf position")];
        let sign = if leaf.inside { 1 } else { -1 };
        let shape = if at < taper {
            let shape = if leaf.inside {
                INSIDE[leaf.shape]
            } else {
                OUTSIDE[leaf.shape]
            };
            let room = if leaf.inside {
                MAX_INSIDE - s
            } else {
                MAX_OUTSIDE + s
            };
            if shape.iter().any(|&(_, dd, _)| dd > room) {
                if room >= 2 { SMALL } else { TAPER[2] }
            } else {
                shape
            }
        } else {
            let progress = f64::from(at - taper) / f64::from(1.max(length - taper));
            if at - kept < 5 + (4.0 * progress).round_ties_even() as i32 {
                continue;
            }
            TAPER[2.min((progress * 3.0) as usize)]
        };
        for &(dt, dd, color) in shape {
            put(at + dt, s + sign * dd, color, at + dt.abs().max(dd.abs()));
        }
        kept = at;
        last_side = leaf.inside;
        if leaf.bud_after && index + 1 < plan.leaves.len() && plan.leaves[index + 1].at < taper {
            let bud_t = (at + plan.leaves[index + 1].at + 1) / 2;
            let bs = depth[usize::try_from(bud_t).expect("positive bud position")];
            let sign = if leaf.inside { -1 } else { 1 };
            put(bud_t, bs + sign, STEM, bud_t + 1);
            put(bud_t, bs + 2 * sign, BUD, bud_t + 2);
        }
    }
    let s = depth[usize::try_from(length - 1).expect("positive length")];
    if vine == BOTTOM {
        let sign = if !last_side && length - kept < 5 {
            1
        } else {
            -1
        };
        put(length, s + sign, STEM, length);
        put(length - 1, s + 2 * sign, STEM, length);
    } else {
        put(length, s - 1, LEAF, length);
    }
    sorted(cells)
}

fn sorted(mut cells: Vec<Cell>) -> Vec<Cell> {
    cells.sort_by_key(|cell| cell.at);
    cells
}

fn plan(seed: u32) -> Plan {
    let mut rng = Rng(seed);
    let mut leaves = Vec::new();
    let mut outside_run = 0;
    let mut last_inside = false;
    let mut at = 3 + rng.next(0, 2);
    while at < CAPACITY {
        let first = leaves.is_empty();
        let inside = !first && !last_inside && (outside_run >= 2 || rng.next(0, 100) < 25);
        outside_run = if inside { 0 } else { outside_run + 1 };
        last_inside = inside;
        let shape = if first {
            0
        } else {
            usize::try_from(rng.next(0, 4)).expect("bounded shape")
        };
        leaves.push(LeafPlan {
            at,
            inside,
            shape,
            bud_after: rng.next(0, 100) < 25,
        });
        at += rng.next(4, 8);
    }
    let mut droops = Vec::new();
    let mut at = 10 + rng.next(0, 8);
    while at < CAPACITY {
        let length = rng.next(5, 10);
        droops.push((at, at + length - 1));
        at += length + rng.next(10, 26);
    }
    Plan { leaves, droops }
}

struct Rng(u32);

impl Rng {
    fn next(&mut self, min: i32, max: i32) -> i32 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 17;
        self.0 ^= self.0 << 5;
        min + i32::try_from(self.0 % u32::try_from(max - min).expect("positive random range"))
            .expect("bounded random value")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn vine_lengths_keep_legacy_rounding_and_limits() {
        assert_eq!(length_for(-1), 0);
        assert_eq!(length_for(10), 8);
        assert_eq!(length_for(30), 26);
        assert_eq!(length_for(1000), 400);
    }

    #[test]
    fn vines_stay_within_the_original_overhang_and_grow_in_order() {
        for vine in [BOTTOM, RIGHT] {
            for length in [2, 10, 40, 100, 400] {
                let cells = cells(vine, length);
                assert!(cells.windows(2).all(|pair| pair[0].at <= pair[1].at));
                assert!(cells.iter().all(|cell| cell.at <= length));
                assert!(cells.iter().all(|cell| {
                    let depth = if vine == BOTTOM { cell.y } else { cell.x };
                    (-MAX_OUTSIDE..=MAX_INSIDE).contains(&depth)
                }));
            }
        }
    }
}
