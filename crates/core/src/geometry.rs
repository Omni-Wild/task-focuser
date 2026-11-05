//! Logical desktop coordinates and the note's placement rules.

/// A point in logical desktop pixels, including negative monitor coordinates.
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}

impl Point {
    pub const fn new(x: f64, y: f64) -> Self {
        Self { x, y }
    }

    pub fn distance_squared(self, other: Self) -> f64 {
        (self.x - other.x).powi(2) + (self.y - other.y).powi(2)
    }

    pub fn is_finite(self) -> bool {
        self.x.is_finite() && self.y.is_finite()
    }
}

/// Width and height in logical pixels.
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct Size {
    pub width: f64,
    pub height: f64,
}

impl Size {
    pub const fn new(width: f64, height: f64) -> Self {
        Self { width, height }
    }
}

/// An axis-aligned rectangle in logical desktop coordinates.
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct Rect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl Rect {
    pub const fn new(x: f64, y: f64, width: f64, height: f64) -> Self {
        Self {
            x,
            y,
            width,
            height,
        }
    }

    pub const fn from_origin_size(origin: Point, size: Size) -> Self {
        Self::new(origin.x, origin.y, size.width, size.height)
    }

    pub const fn origin(self) -> Point {
        Point::new(self.x, self.y)
    }

    pub const fn size(self) -> Size {
        Size::new(self.width, self.height)
    }

    pub fn right(self) -> f64 {
        self.x + self.width
    }

    pub fn bottom(self) -> f64 {
        self.y + self.height
    }

    pub fn center(self) -> Point {
        Point::new(self.x + self.width / 2.0, self.y + self.height / 2.0)
    }

    pub fn contains(self, point: Point) -> bool {
        point.x >= self.x
            && point.x <= self.right()
            && point.y >= self.y
            && point.y <= self.bottom()
    }

    pub fn inflated(self, margin: f64) -> Self {
        Self::new(
            self.x - margin,
            self.y - margin,
            self.width + 2.0 * margin,
            self.height + 2.0 * margin,
        )
    }

    fn distance_squared(self, point: Point) -> f64 {
        let x = point.x.clamp(self.x, self.right());
        let y = point.y.clamp(self.y, self.bottom());
        Point::new(x, y).distance_squared(point)
    }
}

/// Full monitor bounds and the usable area excluding taskbars and docks.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Monitor {
    pub bounds: Rect,
    pub work_area: Rect,
}

impl Monitor {
    pub const fn new(bounds: Rect, work_area: Rect) -> Self {
        Self { bounds, work_area }
    }
}

pub const DOT_MARGIN: f64 = 28.0;
pub const CLOSE_MARGIN: f64 = 36.0;
pub const TARGET_HIT_RADIUS: f64 = 44.0;
pub const FALLBACK_WORK_AREA: Rect = Rect::new(0.0, 0.0, 1920.0, 1040.0);

/// Finds the containing monitor, or the nearest one if the point is off screen.
pub fn nearest_monitor(monitors: &[Monitor], point: Point) -> Option<usize> {
    if !point.is_finite() {
        return (!monitors.is_empty()).then_some(0);
    }
    if let Some(index) = monitors.iter().position(|monitor| {
        point.x >= monitor.bounds.x
            && point.x < monitor.bounds.right()
            && point.y >= monitor.bounds.y
            && point.y < monitor.bounds.bottom()
    }) {
        return Some(index);
    }
    monitors
        .iter()
        .enumerate()
        .min_by(|(_, a), (_, b)| {
            a.bounds
                .distance_squared(point)
                .total_cmp(&b.bounds.distance_squared(point))
        })
        .map(|(index, _)| index)
}

pub fn work_area_at(monitors: &[Monitor], point: Point) -> Rect {
    nearest_monitor(monitors, point).map_or(FALLBACK_WORK_AREA, |index| monitors[index].work_area)
}

pub fn is_on_screen(monitors: &[Monitor], point: Point, slack: f64) -> bool {
    point.is_finite()
        && monitors
            .iter()
            .any(|monitor| monitor.bounds.inflated(slack).contains(point))
}

/// Preserves size. Oversized notes align to the area's start so their text stays reachable.
pub fn clamp_into(rect: Rect, area: Rect) -> Rect {
    fn axis(position: f64, length: f64, start: f64, available: f64) -> f64 {
        if !position.is_finite() || length >= available {
            start
        } else {
            position.clamp(start, start + available - length)
        }
    }
    Rect::new(
        axis(rect.x, rect.width, area.x, area.width),
        axis(rect.y, rect.height, area.y, area.height),
        rect.width,
        rect.height,
    )
}

/// Resizes around the top-right corner, respecting edges the user has not dragged past.
pub fn place_note(anchor: Rect, size: Size, area: Rect) -> Rect {
    let left = (anchor.right() - size.width).max(anchor.x.min(area.x));
    let allowed_bottom = anchor.bottom().max(area.bottom());
    let top = if anchor.y + size.height > allowed_bottom {
        (allowed_bottom - size.height).max(anchor.y.min(area.y))
    } else {
        anchor.y
    };
    Rect::from_origin_size(Point::new(left, top), size)
}

pub fn top_right_corner(area: Rect) -> Point {
    Point::new(area.right() - DOT_MARGIN, area.y + DOT_MARGIN)
}

pub fn bottom_right_corner(area: Rect) -> Point {
    Point::new(area.right() - CLOSE_MARGIN, area.bottom() - CLOSE_MARGIN)
}

pub fn target_contains(center: Point, point: Point) -> bool {
    center.distance_squared(point) <= TARGET_HIT_RADIUS * TARGET_HIT_RADIUS
}

pub fn clamp_dot(center: Point, area: Rect) -> Point {
    let rect = clamp_into(
        Rect::new(
            center.x - DOT_MARGIN,
            center.y - DOT_MARGIN,
            DOT_MARGIN * 2.0,
            DOT_MARGIN * 2.0,
        ),
        area,
    );
    Point::new(rect.x + DOT_MARGIN, rect.y + DOT_MARGIN)
}

/// Carries a remembered offset from one monitor's top-right corner to another.
pub fn dot_spot_on(area: Rect, remembered: Option<Point>, monitors: &[Monitor]) -> Point {
    let corner = top_right_corner(area);
    let Some(remembered) = remembered.filter(|&point| is_on_screen(monitors, point, DOT_MARGIN))
    else {
        return corner;
    };
    let previous_corner = top_right_corner(work_area_at(monitors, remembered));
    clamp_dot(
        Point::new(
            corner.x + remembered.x - previous_corner.x,
            corner.y + remembered.y - previous_corner.y,
        ),
        area,
    )
}

/// Pop-out placement: top-right 24 pixels left and 12 pixels below the dot.
pub fn beside_dot(center: Point, size: Size, area: Rect) -> Rect {
    clamp_into(
        Rect::new(
            center.x - 24.0 - size.width,
            center.y + 12.0,
            size.width,
            size.height,
        ),
        area,
    )
}

/// Inputs whose measurements belong to the host's text and toolbar renderer.
#[derive(Debug, Clone, Copy)]
pub struct NoteSizing {
    pub unfold: f64,
    pub bar_width: f64,
    pub editor_chrome: f64,
    pub manual_width: Option<f64>,
    pub manual_height: Option<f64>,
}

/// Measures with real host font metrics. The callback receives the text's wrap width.
/// Manual dimensions are floors only while unlocked, interpolated during the fold.
pub fn measure_note(
    natural_text_width: f64,
    sizing: NoteSizing,
    mut wrapped_text_height: impl FnMut(f64) -> f64,
) -> Size {
    let open = sizing.unfold.clamp(0.0, 1.0);
    let minimum = 140.0 + (sizing.bar_width.clamp(140.0, 520.0) - 140.0) * open;
    let natural = (natural_text_width + sizing.editor_chrome).ceil() + 1.0;
    let mut width = (natural + 28.0).max(minimum).min(520.0).round_ties_even();
    let floor = |auto: f64, manual: Option<f64>| {
        manual
            .filter(|value| value.is_finite() && *value > auto)
            .map_or(auto, |value| auto + (value.round_ties_even() - auto) * open)
            .round_ties_even()
    };
    width = floor(width, sizing.manual_width);
    let text_height = wrapped_text_height((width - 28.0 - sizing.editor_chrome).max(1.0)).ceil();
    let height = floor(
        6.0 + 34.0 * sizing.unfold.max(0.0) + 20.0 + text_height,
        sizing.manual_height,
    );
    Size::new(width, height)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn negative_monitor_coordinates_and_oversized_notes_remain_reachable() {
        let area = Rect::new(-1920.0, -200.0, 1920.0, 1080.0);
        assert_eq!(
            clamp_into(Rect::new(40.0, -500.0, 200.0, 80.0), area),
            Rect::new(-200.0, -200.0, 200.0, 80.0)
        );
        assert_eq!(
            clamp_into(Rect::new(-3000.0, 200.0, 2100.0, 1200.0), area),
            Rect::new(-1920.0, -200.0, 2100.0, 1200.0)
        );
    }

    #[test]
    fn growth_keeps_the_right_edge_until_the_left_edge_pins() {
        let area = Rect::new(-1000.0, 0.0, 1000.0, 800.0);
        let anchor = Rect::new(-300.0, 700.0, 200.0, 80.0);
        assert_eq!(
            place_note(anchor, Size::new(400.0, 180.0), area),
            Rect::new(-500.0, 620.0, 400.0, 180.0)
        );
        assert_eq!(
            place_note(anchor, Size::new(1100.0, 100.0), area).x,
            -1000.0
        );
        let dragged = Rect::new(-1200.0, 0.0, 200.0, 80.0);
        assert_eq!(place_note(dragged, Size::new(300.0, 80.0), area).x, -1200.0);
    }

    #[test]
    fn dot_offset_crosses_monitors_and_missing_monitor_returns_home() {
        let left = Rect::new(-1920.0, 0.0, 1920.0, 1040.0);
        let right = Rect::new(0.0, 0.0, 2560.0, 1400.0);
        let monitors = [Monitor::new(left, left), Monitor::new(right, right)];
        assert_eq!(nearest_monitor(&monitors, Point::new(0.0, 100.0)), Some(1));
        assert_eq!(
            dot_spot_on(right, Some(Point::new(-228.0, 128.0)), &monitors),
            Point::new(2332.0, 128.0)
        );
        assert_eq!(
            dot_spot_on(right, Some(Point::new(-228.0, 128.0)), &monitors[1..]),
            top_right_corner(right)
        );
        assert!(target_contains(Point::default(), Point::new(44.0, 0.0)));
        assert!(!target_contains(Point::default(), Point::new(44.01, 0.0)));
    }

    #[test]
    fn manual_floor_unfolds_but_locked_note_still_wraps_long_words() {
        let sizing = NoteSizing {
            unfold: 0.0,
            bar_width: 360.0,
            editor_chrome: 4.0,
            manual_width: Some(800.0),
            manual_height: Some(500.0),
        };
        let locked = measure_note(900.0, sizing, |wrap| {
            assert_eq!(wrap, 488.0);
            80.0
        });
        assert_eq!(locked, Size::new(520.0, 106.0));
        assert_eq!(
            measure_note(
                900.0,
                NoteSizing {
                    unfold: 1.0,
                    ..sizing
                },
                |_| 40.0
            ),
            Size::new(800.0, 500.0)
        );
    }
}
