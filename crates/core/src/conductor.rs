//! Note, dot, and tray transitions, without window-system dependencies.

use crate::geometry::{
    DOT_MARGIN, FALLBACK_WORK_AREA, Monitor, Point, Rect, Size, beside_dot, bottom_right_corner,
    clamp_dot, clamp_into, dot_spot_on, is_on_screen, nearest_monitor, target_contains,
    work_area_at,
};
use crate::state::{Mode, NoteState};

/// Commands accepted from both the tray and another process.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Command {
    Show,
    Lock,
    Unlock,
    ToggleLock,
    Dot,
    Close,
    Start,
    Quit,
}

impl Command {
    /// Parses arguments in order. An empty forwarded invocation shows the note;
    /// unrecognized arguments are ignored, and nothing after `--quit` runs.
    pub fn parse_args<I, S>(args: I) -> Vec<Self>
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        let mut commands = Vec::new();
        let mut any_argument = false;
        for argument in args {
            any_argument = true;
            let argument = argument.as_ref().trim();
            let command = [
                ("--show", Self::Show),
                ("--lock", Self::Lock),
                ("--unlock", Self::Unlock),
                ("--dot", Self::Dot),
                ("--close", Self::Close),
                ("--start", Self::Start),
                ("--quit", Self::Quit),
            ]
            .into_iter()
            .find_map(|(name, command)| argument.eq_ignore_ascii_case(name).then_some(command));
            if let Some(command) = command {
                commands.push(command);
                if command == Self::Quit {
                    break;
                }
            }
        }
        if !any_argument {
            commands.push(Self::Show);
        }
        commands
    }
}

/// An armed destination for a drag. Its hit area is a 44-pixel radius.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct DragTarget {
    pub center: Point,
    pub hot: bool,
}

/// Work the native host performs after a transition. Persistence failures belong
/// to the host and must be reported there rather than hidden by the state machine.
#[derive(Debug, Clone, Copy, PartialEq)]
pub enum Effect {
    Render,
    Persist,
    BringToFront,
    Quit,
    Carrying(bool),
    DotTarget(Option<DragTarget>),
    CloseTarget(Option<DragTarget>),
    Dock { target: Point },
    PopOut { origin: Point },
    Vanish,
}

pub type Effects = Vec<Effect>;

#[derive(Debug, Clone, Copy)]
struct Drag {
    last_cursor: Point,
    monitor: Option<usize>,
    target: DragTarget,
}

/// Owns persistent state and accepts input on the host's event thread.
/// The host controls visual interpolation; state always records the destination.
#[derive(Debug)]
pub struct Conductor {
    state: NoteState,
    monitors: Vec<Monitor>,
    note_size: Size,
    dot_position: Option<Point>,
    note_drag: Option<Drag>,
    dot_drag: Option<Drag>,
    busy: bool,
}

impl Conductor {
    pub fn new(mut state: NoteState) -> Self {
        state.normalize();
        let dot_position = state.dot_x.zip(state.dot_y).map(|(x, y)| Point::new(x, y));
        Self {
            state,
            monitors: vec![Monitor::new(FALLBACK_WORK_AREA, FALLBACK_WORK_AREA)],
            note_size: Size::new(200.0, 80.0),
            dot_position,
            note_drag: None,
            dot_drag: None,
            busy: false,
        }
    }

    pub fn state(&self) -> &NoteState {
        &self.state
    }

    /// Settings and text edits can update state before requesting a host redraw.
    pub fn state_mut(&mut self) -> &mut NoteState {
        &mut self.state
    }

    pub fn note_bounds(&self) -> Rect {
        Rect::new(
            self.state.x,
            self.state.y,
            self.note_size.width,
            self.note_size.height,
        )
    }

    /// Adopts measured layout and moves made by the operating system.
    pub fn set_note_bounds(&mut self, bounds: Rect) {
        if bounds.origin().is_finite()
            && bounds.width > 0.0
            && bounds.height > 0.0
            && bounds.width.is_finite()
            && bounds.height.is_finite()
        {
            self.state.x = bounds.x;
            self.state.y = bounds.y;
            self.note_size = bounds.size();
        }
    }

    /// The host supplies current monitor bounds, primary monitor first.
    pub fn set_monitors(&mut self, monitors: Vec<Monitor>) {
        self.monitors = monitors
            .into_iter()
            .filter(|monitor| {
                let valid = |rect: Rect| {
                    rect.origin().is_finite()
                        && rect.width.is_finite()
                        && rect.height.is_finite()
                        && rect.width > 0.0
                        && rect.height > 0.0
                };
                valid(monitor.bounds) && valid(monitor.work_area)
            })
            .collect();
        if self.monitors.is_empty() {
            self.monitors
                .push(Monitor::new(FALLBACK_WORK_AREA, FALLBACK_WORK_AREA));
        }
    }

    /// Refuses gestures while a host animation owns the note or bird.
    pub fn set_busy(&mut self, busy: bool) {
        self.busy = busy;
    }

    pub fn is_busy(&self) -> bool {
        self.busy
    }

    pub fn is_note_dragging(&self) -> bool {
        self.note_drag.is_some()
    }

    pub fn is_dot_dragging(&self) -> bool {
        self.dot_drag.is_some()
    }

    pub fn bird_wanted(&self) -> bool {
        self.state.mode == Mode::Note
            && (!self.state.locked || self.state.bird_stays || self.is_note_dragging())
    }

    pub fn dot_center(&self) -> Option<Point> {
        self.dot_position
    }

    /// Adopts a resting dot moved by the operating system. Drag positions stay
    /// temporary until release so closing a dragged dot preserves its last home.
    pub fn adopt_dot_position(&mut self, center: Point) -> bool {
        if self.state.mode != Mode::Dot || self.is_dot_dragging() || !center.is_finite() {
            return false;
        }
        self.store_dot(center);
        true
    }

    /// Enters the saved mode after the host has supplied monitors and note size.
    pub fn restore(&mut self) -> Effects {
        match self.state.mode {
            Mode::Note => self.clamp_note(),
            Mode::Dot => {
                let center = self
                    .dot_center()
                    .filter(|&center| is_on_screen(&self.monitors, center, DOT_MARGIN))
                    .map_or_else(
                        || self.home_corner(),
                        |center| clamp_dot(center, self.area_at(center)),
                    );
                self.store_dot(center);
            }
            Mode::Closed => {}
        }
        vec![Effect::Render, Effect::Persist]
    }

    pub fn handle(&mut self, command: Command) -> Effects {
        if command == Command::Quit {
            return vec![Effect::Persist, Effect::Quit];
        }
        if self.busy {
            return vec![];
        }
        match command {
            Command::Show => match self.state.mode {
                Mode::Note => {
                    self.clamp_note();
                    vec![Effect::Render, Effect::BringToFront, Effect::Persist]
                }
                Mode::Dot if !self.is_dot_dragging() => self.pop_out(),
                Mode::Closed => {
                    self.state.mode = Mode::Note;
                    self.clamp_note();
                    vec![Effect::Render, Effect::BringToFront, Effect::Persist]
                }
                Mode::Dot => vec![],
            },
            Command::Lock => self.set_locked(true),
            Command::Unlock => self.set_locked(false),
            Command::ToggleLock => self.set_locked(!self.state.locked),
            Command::Dot if self.state.mode == Mode::Note && !self.is_note_dragging() => {
                let target = self.dot_spot(self.area_at(self.note_bounds().center()));
                self.dock(target)
            }
            Command::Start if self.state.mode == Mode::Closed => {
                let center = self.home_corner();
                self.store_dot(center);
                self.state.mode = Mode::Dot;
                vec![Effect::Render, Effect::Persist]
            }
            Command::Close if self.state.mode != Mode::Closed => {
                self.note_drag = None;
                self.dot_drag = None;
                self.state.mode = Mode::Closed;
                vec![
                    Effect::Carrying(false),
                    Effect::DotTarget(None),
                    Effect::CloseTarget(None),
                    Effect::Render,
                    Effect::Persist,
                ]
            }
            _ => vec![],
        }
    }

    pub fn begin_note_drag(&mut self, cursor: Point) -> Effects {
        if self.busy || self.state.mode != Mode::Note || !cursor.is_finite() {
            return vec![];
        }
        let target = DragTarget {
            center: self.dot_spot(self.area_at(cursor)),
            hot: false,
        };
        self.note_drag = Some(Drag {
            last_cursor: cursor,
            monitor: nearest_monitor(&self.monitors, cursor),
            target,
        });
        vec![
            Effect::Carrying(true),
            Effect::DotTarget(Some(target)),
            Effect::Render,
        ]
    }

    /// Moves the note by cursor delta, retaining the original grab point.
    pub fn move_note_drag(&mut self, cursor: Point) -> Effects {
        let Some(mut drag) = self.note_drag else {
            return vec![];
        };
        if !cursor.is_finite() {
            return vec![];
        }
        self.state.x += cursor.x - drag.last_cursor.x;
        self.state.y += cursor.y - drag.last_cursor.y;
        drag.last_cursor = cursor;
        let monitor = nearest_monitor(&self.monitors, cursor);
        if monitor != drag.monitor {
            drag.monitor = monitor;
            drag.target.center = self.dot_spot(self.area_at(cursor));
        }
        let old_target = self.note_drag.map(|drag| drag.target);
        drag.target.hot = target_contains(drag.target.center, cursor);
        self.note_drag = Some(drag);
        let mut effects = vec![Effect::Render];
        if old_target != Some(drag.target) {
            effects.push(Effect::DotTarget(Some(drag.target)));
        }
        effects
    }

    pub fn end_note_drag(&mut self, cursor: Point) -> Effects {
        let mut effects = self.move_note_drag(cursor);
        let Some(drag) = self.note_drag.take() else {
            return effects;
        };
        effects.push(Effect::Carrying(false));
        if target_contains(drag.target.center, cursor) {
            effects.extend(self.dock(drag.target.center));
        } else {
            effects.extend([Effect::DotTarget(None), Effect::Persist]);
        }
        effects
    }

    pub fn begin_dot_drag(&mut self, cursor: Point) -> Effects {
        if self.busy || self.state.mode != Mode::Dot || !cursor.is_finite() {
            return vec![];
        }
        let target = DragTarget {
            center: bottom_right_corner(self.area_at(cursor)),
            hot: false,
        };
        self.dot_drag = Some(Drag {
            last_cursor: cursor,
            monitor: nearest_monitor(&self.monitors, cursor),
            target,
        });
        vec![Effect::CloseTarget(Some(target))]
    }

    pub fn move_dot_drag(&mut self, cursor: Point) -> Effects {
        let Some(mut drag) = self.dot_drag else {
            return vec![];
        };
        if !cursor.is_finite() {
            return vec![];
        }
        let center = self.dot_center().unwrap_or_else(|| self.home_corner());
        self.dot_position = Some(Point::new(
            center.x + cursor.x - drag.last_cursor.x,
            center.y + cursor.y - drag.last_cursor.y,
        ));
        drag.last_cursor = cursor;
        let monitor = nearest_monitor(&self.monitors, cursor);
        if monitor != drag.monitor {
            drag.monitor = monitor;
            drag.target.center = bottom_right_corner(self.area_at(cursor));
        }
        let old_target = self.dot_drag.map(|drag| drag.target);
        drag.target.hot = target_contains(drag.target.center, cursor);
        self.dot_drag = Some(drag);
        let mut effects = vec![Effect::Render];
        if old_target != Some(drag.target) {
            effects.push(Effect::CloseTarget(Some(drag.target)));
        }
        effects
    }

    pub fn end_dot_drag(&mut self, cursor: Point) -> Effects {
        let mut effects = self.move_dot_drag(cursor);
        let Some(drag) = self.dot_drag.take() else {
            return effects;
        };
        if target_contains(drag.target.center, cursor) {
            self.state.mode = Mode::Closed;
            effects.push(Effect::Vanish);
        } else if let Some(center) = self.dot_center() {
            self.store_dot(center);
        }
        effects.extend([Effect::CloseTarget(None), Effect::Render, Effect::Persist]);
        effects
    }

    /// Recovers a resting dot after a monitor is unplugged. Dragged dots are left alone.
    pub fn display_changed(&mut self) -> Effects {
        if self.busy || self.is_dot_dragging() || self.state.mode != Mode::Dot {
            return vec![];
        }
        if self
            .dot_center()
            .is_some_and(|center| is_on_screen(&self.monitors, center, DOT_MARGIN))
        {
            return vec![];
        }
        let center = self.dot_spot(
            self.area_at(
                self.dot_center()
                    .unwrap_or(Point::new(self.state.x, self.state.y)),
            ),
        );
        self.store_dot(center);
        vec![Effect::Render, Effect::Persist]
    }

    fn set_locked(&mut self, locked: bool) -> Effects {
        if self.state.locked == locked {
            return vec![];
        }
        self.state.locked = locked;
        vec![Effect::Render, Effect::Persist]
    }

    fn dock(&mut self, target: Point) -> Effects {
        self.state.mode = Mode::Dot;
        self.store_dot(target);
        vec![
            Effect::Dock { target },
            Effect::DotTarget(None),
            Effect::Render,
            Effect::Persist,
        ]
    }

    fn pop_out(&mut self) -> Effects {
        let origin = self.dot_center().unwrap_or_else(|| self.home_corner());
        let bounds = beside_dot(origin, self.note_size, self.area_at(origin));
        self.state.x = bounds.x;
        self.state.y = bounds.y;
        self.state.mode = Mode::Note;
        vec![
            Effect::PopOut { origin },
            Effect::Render,
            Effect::BringToFront,
            Effect::Persist,
        ]
    }

    fn clamp_note(&mut self) {
        let bounds = self.note_bounds();
        let clamped = clamp_into(bounds, self.area_at(bounds.center()));
        self.state.x = clamped.x;
        self.state.y = clamped.y;
    }

    fn area_at(&self, point: Point) -> Rect {
        work_area_at(&self.monitors, point)
    }

    fn home_corner(&self) -> Point {
        self.dot_spot(self.area_at(Point::new(self.state.x, self.state.y)))
    }

    fn dot_spot(&self, area: Rect) -> Point {
        let remembered = self
            .state
            .dot_x
            .zip(self.state.dot_y)
            .map(|(x, y)| Point::new(x, y));
        dot_spot_on(area, remembered, &self.monitors)
    }

    fn store_dot(&mut self, center: Point) {
        self.dot_position = Some(center);
        self.state.dot_x = Some(center.x);
        self.state.dot_y = Some(center.y);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arguments_preserve_order_and_bare_launch_shows_the_note() {
        assert_eq!(Command::parse_args(Vec::<String>::new()), [Command::Show]);
        assert!(Command::parse_args(["--unknown"]).is_empty());
        assert_eq!(
            Command::parse_args([" --LOCK ", "--unknown", "--unlock", "--quit", "--show"]),
            [Command::Lock, Command::Unlock, Command::Quit]
        );
    }

    #[test]
    fn tray_lifecycle_keeps_lock_state_and_start_only_opens_closed_dot() {
        let mut conductor = Conductor::new(NoteState::default());
        conductor.handle(Command::Lock);
        conductor.handle(Command::Dot);
        assert_eq!(conductor.state().mode, Mode::Dot);
        let center = conductor.dot_center().expect("docked dot");
        assert!(conductor.handle(Command::Start).is_empty());
        conductor.handle(Command::Show);
        assert_eq!(conductor.state().mode, Mode::Note);
        assert!(conductor.state().locked);
        assert!((conductor.note_bounds().right() - (center.x - 24.0)).abs() < 0.01);
        conductor.handle(Command::Close);
        conductor.handle(Command::Unlock);
        conductor.handle(Command::Start);
        assert_eq!(conductor.state().mode, Mode::Dot);
        assert!(!conductor.state().locked);
    }

    #[test]
    fn note_and_dot_drags_move_targets_between_monitors_and_close() {
        let mut conductor = Conductor::new(NoteState::default());
        let left = Rect::new(-1000.0, 0.0, 1000.0, 800.0);
        let right = Rect::new(0.0, 0.0, 1000.0, 800.0);
        conductor.set_monitors(vec![Monitor::new(left, left), Monitor::new(right, right)]);
        conductor.begin_note_drag(Point::new(100.0, 100.0));
        let effects = conductor.move_note_drag(Point::new(-28.0, 28.0));
        assert!(effects.contains(&Effect::DotTarget(Some(DragTarget {
            center: Point::new(-28.0, 28.0),
            hot: true
        }))));
        conductor.end_note_drag(Point::new(-28.0, 28.0));
        assert_eq!(conductor.state().mode, Mode::Dot);
        conductor.begin_dot_drag(Point::new(-28.0, 28.0));
        conductor.end_dot_drag(Point::new(964.0, 764.0));
        assert_eq!(conductor.state().mode, Mode::Closed);
        assert!(!conductor.is_dot_dragging());
        assert_eq!(conductor.state().dot_x, Some(-28.0));
        conductor.handle(Command::Start);
        assert_eq!(conductor.dot_center(), Some(Point::new(-28.0, 28.0)));
    }

    #[test]
    fn closing_a_drag_clears_carrying_and_busy_accepts_only_quit() {
        let mut conductor = Conductor::new(NoteState::default());
        conductor.begin_note_drag(Point::new(100.0, 100.0));
        let effects = conductor.handle(Command::Close);
        assert!(effects.contains(&Effect::Carrying(false)));
        assert!(!conductor.is_note_dragging());
        conductor.set_busy(true);
        assert!(conductor.handle(Command::Show).is_empty());
        assert!(conductor.handle(Command::Quit).contains(&Effect::Quit));
    }

    #[test]
    fn saved_dot_on_missing_monitor_returns_to_note_monitor_home() {
        let mut conductor = Conductor::new(NoteState {
            mode: Mode::Dot,
            dot_x: Some(-500.0),
            dot_y: Some(100.0),
            ..NoteState::default()
        });
        conductor.restore();
        assert_eq!(conductor.dot_center(), Some(Point::new(1892.0, 28.0)));
    }

    #[test]
    fn operating_system_dot_move_survives_sync_and_carries_its_new_offset() {
        let mut conductor = Conductor::new(NoteState {
            mode: Mode::Dot,
            dot_x: Some(1892.0),
            dot_y: Some(28.0),
            ..NoteState::default()
        });
        let left = Rect::new(0.0, 0.0, 1920.0, 1040.0);
        let right = Rect::new(1920.0, 0.0, 1280.0, 1040.0);
        conductor.set_monitors(vec![Monitor::new(left, left), Monitor::new(right, right)]);

        let moved = Point::new(1600.0, 120.0);
        assert!(conductor.adopt_dot_position(moved));
        assert_eq!(conductor.dot_center(), Some(moved));
        assert_eq!(conductor.state().dot_x, Some(moved.x));
        assert_eq!(conductor.state().dot_y, Some(moved.y));
        assert!(conductor.display_changed().is_empty());
        assert!(
            conductor
                .handle(Command::Show)
                .contains(&Effect::PopOut { origin: moved })
        );

        conductor.set_note_bounds(Rect::new(2100.0, 100.0, 200.0, 80.0));
        conductor.handle(Command::Dot);
        assert_eq!(conductor.dot_center(), Some(Point::new(2880.0, 120.0)));
    }

    #[test]
    fn dot_adoption_rejects_invalid_or_dragged_positions_without_changing_home() {
        let mut conductor = Conductor::new(NoteState::default());
        assert!(!conductor.adopt_dot_position(Point::new(200.0, 200.0)));
        conductor.handle(Command::Dot);
        let home = conductor.dot_center().expect("resting dot");
        assert!(!conductor.adopt_dot_position(Point::new(f64::NAN, 200.0)));
        conductor.begin_dot_drag(home);
        assert!(!conductor.adopt_dot_position(Point::new(200.0, 200.0)));
        assert_eq!(conductor.dot_center(), Some(home));
        assert_eq!(conductor.state().dot_x, Some(home.x));
        assert_eq!(conductor.state().dot_y, Some(home.y));
    }
}
