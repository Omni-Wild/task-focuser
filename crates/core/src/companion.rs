//! Time-driven companion behavior. Positions are the center of the feet in screen DIPs.
//! The host supplies elapsed time and pointer/perch changes; no native window or timer is owned here.

use crate::{
    audio::SoundKind,
    geometry::Point,
    sprites::{BirdId, Frame, WIDTH},
};
use std::{collections::VecDeque, f64::consts::PI, time::Duration};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Mood {
    Calm,
    Happy,
    Annoyed,
    Fuming,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CompanionEvent {
    Sound(SoundKind),
    Landed,
    ShoveDone,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EffectKind {
    Heart,
    Dust,
    Seed,
}
#[derive(Debug, Clone, Copy)]
pub struct Effect {
    pub kind: EffectKind,
    pub position: Point,
    pub opacity: f64,
    pub age: f64,
    pub lifetime: f64,
    velocity: Point,
    origin: Point,
    delay: f64,
    phase: f64,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Motion {
    Rest,
    Arrive,
    Fly,
    Leave,
    FlyOff,
    Away,
    Shove,
    Bump,
    WalkOut,
    WalkPause,
    WalkBack,
    Return,
    Tumble,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Act {
    None,
    Hop,
    Annoyed,
    Fume,
    Eat,
    Peck,
    Ruffle,
    Idle,
    Seed,
    Flap,
}
#[derive(Debug, Clone, Copy)]
struct Step {
    frame: Frame,
    seconds: f64,
}
const HOP: [Step; 1] = [Step {
    frame: Frame::Hop,
    seconds: 0.180,
}];
const ANNOYED: [Step; 1] = [Step {
    frame: Frame::Chirp,
    seconds: 0.420,
}];
const EAT: [Step; 7] = [
    Step {
        frame: Frame::Eat1,
        seconds: 0.083,
    },
    Step {
        frame: Frame::Eat2,
        seconds: 0.083,
    },
    Step {
        frame: Frame::Eat1,
        seconds: 0.083,
    },
    Step {
        frame: Frame::Eat2,
        seconds: 0.083,
    },
    Step {
        frame: Frame::Eat1,
        seconds: 0.083,
    },
    Step {
        frame: Frame::Eat2,
        seconds: 0.085,
    },
    Step {
        frame: Frame::Happy,
        seconds: 0.600,
    },
];
const PECK: [Step; 3] = [
    Step {
        frame: Frame::Peck1,
        seconds: 0.060,
    },
    Step {
        frame: Frame::Peck2,
        seconds: 0.060,
    },
    Step {
        frame: Frame::IdleOpen,
        seconds: 0.040,
    },
];
const RUFFLE: [Step; 9] = [
    Step {
        frame: Frame::Ruffle1,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Ruffle2,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Ruffle1,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Ruffle2,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Ruffle1,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Ruffle2,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Ruffle1,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Ruffle2,
        seconds: 0.040,
    },
    Step {
        frame: Frame::IdleOpen,
        seconds: 0.120,
    },
];
const SEED: [Step; 8] = [
    Step {
        frame: Frame::IdleOpen,
        seconds: 0.220,
    },
    Step {
        frame: Frame::Peck1,
        seconds: 0.060,
    },
    Step {
        frame: Frame::Peck2,
        seconds: 0.060,
    },
    Step {
        frame: Frame::IdleOpen,
        seconds: 0.040,
    },
    Step {
        frame: Frame::Peck1,
        seconds: 0.060,
    },
    Step {
        frame: Frame::Peck2,
        seconds: 0.060,
    },
    Step {
        frame: Frame::IdleOpen,
        seconds: 0.040,
    },
    Step {
        frame: Frame::IdleBlink,
        seconds: 0.120,
    },
];
const TILT_L: [Step; 1] = [Step {
    frame: Frame::TiltL,
    seconds: 0.600,
}];
const TILT_R: [Step; 1] = [Step {
    frame: Frame::TiltR,
    seconds: 0.600,
}];
const PREEN: [Step; 1] = [Step {
    frame: Frame::Preen,
    seconds: 0.800,
}];
const GLANCE: [Step; 3] = [
    Step {
        frame: Frame::IdleBlink,
        seconds: 0.110,
    },
    Step {
        frame: Frame::IdleOpen,
        seconds: 0.140,
    },
    Step {
        frame: Frame::IdleBlink,
        seconds: 0.110,
    },
];
const FLAP: [Frame; 4] = [
    Frame::FlapUp,
    Frame::FlapMid,
    Frame::FlapDown,
    Frame::FlapMid,
];

#[derive(Debug)]
pub struct Companion {
    bird: BirdId,
    pending_bird: Option<BirdId>,
    position: Point,
    perch: Point,
    frame: Frame,
    visible: bool,
    pinned: bool,
    base_opacity: f64,
    opacity: f64,
    facing_left: bool,
    motion: Motion,
    from: Point,
    target: Point,
    motion_time: f64,
    motion_duration: f64,
    carrying: bool,
    self_drag: bool,
    velocity: Point,
    perch_velocity: Point,
    last_perch: Point,
    calm_time: f64,
    clock: f64,
    last_landing: f64,
    last_click: f64,
    click_streak: u32,
    annoyed: VecDeque<f64>,
    mood: Mood,
    act: Act,
    steps: &'static [Step],
    step: usize,
    act_time: f64,
    peck_queued: bool,
    flap_time: f64,
    blink_in: f64,
    blink_hold: f64,
    idle_in: f64,
    span: Option<(f64, f64)>,
    stray: bool,
    seed_position: Option<Point>,
    seed_spot: Point,
    random: u64,
    effects: Vec<Effect>,
    events: VecDeque<CompanionEvent>,
}
impl Companion {
    pub fn new(bird: BirdId, perch: Point) -> Self {
        let mut s = Self {
            bird,
            pending_bird: None,
            position: perch,
            perch,
            frame: Frame::IdleOpen,
            visible: false,
            pinned: true,
            base_opacity: 1.,
            opacity: 1.,
            facing_left: false,
            motion: Motion::Rest,
            from: perch,
            target: perch,
            motion_time: 0.,
            motion_duration: 0.,
            carrying: false,
            self_drag: false,
            velocity: Point::new(0., 0.),
            perch_velocity: Point::new(0., 0.),
            last_perch: perch,
            calm_time: 0.,
            clock: 0.,
            last_landing: 0.,
            last_click: f64::NEG_INFINITY,
            click_streak: 0,
            annoyed: VecDeque::with_capacity(5),
            mood: Mood::Calm,
            act: Act::None,
            steps: &[],
            step: 0,
            act_time: 0.,
            peck_queued: false,
            flap_time: 0.,
            blink_in: 3.,
            blink_hold: 0.,
            idle_in: 30.,
            span: None,
            stray: false,
            seed_position: None,
            seed_spot: perch,
            random: 0x92b137d6,
            effects: Vec::new(),
            events: VecDeque::new(),
        };
        s.schedule_rest();
        s
    }
    pub fn bird(&self) -> BirdId {
        self.bird
    }
    pub fn frame(&self) -> Frame {
        self.frame
    }
    pub fn position(&self) -> Point {
        self.position
    }
    pub fn perch(&self) -> Point {
        self.perch
    }
    pub fn visible(&self) -> bool {
        self.visible
    }
    pub fn opacity(&self) -> f64 {
        self.opacity
    }
    pub fn pinned(&self) -> bool {
        self.pinned
    }
    pub fn facing_left(&self) -> bool {
        self.facing_left
    }
    pub fn mood(&self) -> Mood {
        self.mood
    }
    pub fn is_carrying(&self) -> bool {
        self.carrying
    }
    pub fn is_arriving(&self) -> bool {
        self.motion == Motion::Arrive
    }
    pub fn is_switching(&self) -> bool {
        self.pending_bird.is_some() || self.motion == Motion::Away && self.motion_duration < 0.5
    }
    pub fn is_tumbling(&self) -> bool {
        self.motion == Motion::Tumble
    }
    pub fn effects(&self) -> &[Effect] {
        &self.effects
    }
    /// Per-frame visual shift, separate from the feet anchor used for dragging and flight.
    pub fn sprite_offset(&self) -> Point {
        if self.act == Act::Hop || self.act == Act::Idle && self.frame == Frame::Hop {
            let height = if self.act == Act::Hop { 8. } else { 5. };
            let t = (self.act_time / 0.090).min(2.);
            let q = if t <= 1. { t } else { 2. - t };
            return Point::new(0., -height * (1. - (1. - q).powi(2)));
        }
        if self.steps.as_ptr() == RUFFLE.as_ptr() && self.step < 8 {
            return Point::new(if self.step.is_multiple_of(2) { 1. } else { -1. }, 0.);
        }
        if matches!(self.act, Act::Annoyed | Act::Fume) && self.act_time < 0.210 {
            let values = [0., 2., -2., 2., -2., 2., -2., 0.];
            let t = self.act_time / 0.030;
            let index = t.floor() as usize;
            return Point::new(
                values[index] + (values[index + 1] - values[index]) * t.fract(),
                0.,
            );
        }
        Point::new(0., 0.)
    }
    pub fn seed_position(&self) -> Option<Point> {
        self.seed_position
    }
    pub fn drain_events(&mut self) -> Vec<CompanionEvent> {
        self.events.drain(..).collect()
    }
    pub fn set_pinned(&mut self, pinned: bool) {
        self.pinned = pinned;
    }
    pub fn set_opacity(&mut self, opacity: f64) {
        self.base_opacity = if opacity.is_finite() {
            opacity.clamp(0., 1.)
        } else {
            1.
        };
        if !matches!(self.motion, Motion::Leave | Motion::FlyOff | Motion::Away) {
            self.opacity = self.base_opacity;
        }
    }
    pub fn set_self_drag(&mut self, dragging: bool) {
        self.self_drag = dragging;
    }
    pub fn set_perch_span(&mut self, left: f64, right: f64) {
        self.span = Some((left.min(right), left.max(right)));
        if matches!(self.motion, Motion::WalkOut | Motion::WalkPause)
            && (self.target.x < left.min(right) + 6. || self.target.x > left.max(right) - 6.)
        {
            self.begin_walk_back();
        }
    }
    /// Moving animations request frames; a still bird only needs its next discrete blink/fidget deadline.
    pub fn animation_active(&self) -> bool {
        self.visible
            && (self.motion != Motion::Rest && self.motion != Motion::Away
                || self.carrying
                || self.act != Act::None
                || !self.effects.is_empty())
    }
    pub fn next_update_in(&self) -> Option<Duration> {
        if !self.visible {
            return None;
        }
        let seconds = if self.animation_active() {
            1. / 60.
        } else if self.motion == Motion::Away {
            (self.motion_duration - self.motion_time).max(0.)
        } else {
            (if self.blink_hold > 0. {
                self.blink_hold
            } else {
                self.blink_in
            })
            .min(self.idle_in)
            .max(0.)
        };
        Some(Duration::from_secs_f64(seconds))
    }
    pub fn arrive_at(&mut self, perch: Point) {
        self.perch = perch;
        if self.visible && matches!(self.motion, Motion::FlyOff | Motion::Away) {
            return;
        }
        if self.visible {
            self.fly_to(perch);
            return;
        }
        self.visible = true;
        self.opacity = self.base_opacity;
        self.position = Point::new(perch.x, perch.y - 320.);
        self.start_motion(Motion::Arrive, perch, 0.450);
    }
    pub fn move_to(&mut self, perch: Point) {
        // Arrive and Fly read the current perch every frame, preserving their running clock.
        self.perch = perch;
        if self.motion == Motion::Rest {
            self.position = perch;
            self.stray = false;
        } else if matches!(
            self.motion,
            Motion::WalkOut | Motion::WalkPause | Motion::WalkBack | Motion::Return
        ) {
            self.motion = Motion::Rest;
            self.position = perch;
            self.rest();
        }
    }
    pub fn fly_to(&mut self, perch: Point) {
        self.perch = perch;
        if !self.visible {
            self.arrive_at(perch);
            return;
        }
        if matches!(self.motion, Motion::FlyOff | Motion::Away) {
            return;
        }
        self.start_motion(Motion::Fly, perch, 0.260);
    }
    pub fn leave(&mut self) {
        self.carrying = false;
        if let Some(bird) = self.pending_bird.take() {
            self.bird = bird;
        }
        if !self.visible {
            return;
        }
        if self.motion == Motion::Away {
            self.hide();
            return;
        }
        self.start_motion(
            Motion::Leave,
            Point::new(self.position.x, self.perch.y - 320.),
            0.300,
        );
    }
    pub fn hide(&mut self) {
        if let Some(bird) = self.pending_bird.take() {
            self.bird = bird;
        }
        self.visible = false;
        self.carrying = false;
        self.cancel_activity();
        self.motion = Motion::Rest;
        self.effects.clear();
        self.opacity = self.base_opacity;
    }
    pub fn carry(&mut self, perch: Point) {
        if !self.carrying {
            self.cancel_activity();
            self.motion = Motion::Rest;
            self.last_perch = perch;
            self.perch_velocity = Point::new(0., 0.);
            self.carrying = true;
            self.flap_time = 0.;
        }
        self.perch = perch;
        if self.motion == Motion::Rest {
            self.position = perch;
        }
    }
    pub fn release(&mut self, perch: Point) {
        self.carrying = false;
        self.self_drag = false;
        self.perch = perch;
        if self.motion == Motion::Tumble {
            self.catch();
        } else if self.motion == Motion::Rest {
            self.position = perch;
            self.rest();
        }
    }
    pub fn lose_grip(&mut self) {
        if !self.visible {
            return;
        }
        self.cancel_act();
        self.motion = Motion::Tumble;
        self.velocity = Point::new(self.perch_velocity.x * 0.5, self.perch_velocity.y * 0.5);
        self.calm_time = 0.;
        self.flap_time = 0.;
    }
    pub fn catch(&mut self) {
        if self.visible {
            self.start_motion(Motion::Fly, self.perch, 0.220);
        }
    }
    pub fn shove(&mut self, target: Point) {
        if !self.visible {
            self.visible = true;
            self.position = self.perch;
        }
        let dx = target.x - self.position.x;
        let dy = target.y - (self.position.y - 21.);
        let d = dx.hypot(dy);
        let reach = (d - 31.).max(0.);
        let end = if d > 0.5 {
            Point::new(
                self.position.x + dx / d * reach,
                self.position.y + dy / d * reach,
            )
        } else {
            self.position
        };
        self.facing_left = dx < 0.;
        self.start_motion(Motion::Shove, end, 0.120);
        self.frame = Frame::ShoveA;
    }
    pub fn switch_to(&mut self, bird: BirdId) {
        if self.pending_bird.unwrap_or(self.bird) == bird {
            return;
        }
        if !self.visible {
            self.bird = bird;
            self.reset_mood();
            return;
        }
        self.pending_bird = Some(bird);
        if self.motion == Motion::Away {
            self.bird = bird;
            self.pending_bird = None;
            self.motion_time = 0.;
            self.motion_duration = 0.150;
            return;
        }
        if matches!(
            self.motion,
            Motion::FlyOff | Motion::Leave | Motion::Shove | Motion::Bump
        ) {
            return;
        }
        self.reset_mood();
        self.start_motion(
            Motion::FlyOff,
            Point::new(self.position.x, self.perch.y - 320.),
            0.300,
        );
    }
    pub fn click(&mut self) -> Option<SoundKind> {
        if self.clock - self.last_click >= 2.5 {
            self.reset_mood();
        }
        self.last_click = self.clock;
        self.click_streak = self.click_streak.saturating_add(1);
        if !self.can_act() || matches!(self.act, Act::Eat | Act::Fume) {
            return None;
        }
        if self.click_streak < 3 {
            self.mood = Mood::Happy;
            self.play(Act::Hop, &HOP);
            self.sound(SoundKind::Peep);
            Some(SoundKind::Peep)
        } else {
            self.annoyed.push_back(self.clock);
            while self.annoyed.len() > 5 {
                self.annoyed.pop_front();
            }
            let fume = self.annoyed.len() == 5 && self.clock - self.annoyed[0] <= 12.;
            self.mood = if fume { Mood::Fuming } else { Mood::Annoyed };
            self.play(if fume { Act::Fume } else { Act::Annoyed }, &ANNOYED);
            self.sound(SoundKind::Annoyed);
            Some(SoundKind::Annoyed)
        }
    }
    pub fn eat(&mut self) {
        if !self.can_act() {
            return;
        }
        self.reset_mood();
        self.play(Act::Eat, &EAT);
    }
    pub fn peck(&mut self) {
        if !self.can_act() {
            return;
        }
        if self.act == Act::Peck {
            self.peck_queued = true;
        } else if matches!(self.act, Act::None | Act::Idle | Act::Seed)
            && self.steps.as_ptr() != RUFFLE.as_ptr()
        {
            self.play(Act::Peck, &PECK);
        }
    }
    pub fn ruffle(&mut self) {
        if self.can_act() && matches!(self.act, Act::None | Act::Idle | Act::Seed) {
            self.play(Act::Ruffle, &RUFFLE);
            self.sound(SoundKind::Ruffle);
        }
    }
    pub fn flap(&mut self) {
        if self.can_act() && matches!(self.act, Act::None | Act::Idle | Act::Seed) {
            self.cancel_activity();
            self.act = Act::Flap;
            self.act_time = 0.;
            self.flap_time = 0.;
        }
    }
    pub fn walk_to(&mut self, x: f64) -> bool {
        if !self.can_act() || self.act != Act::None {
            return false;
        }
        let target = if let Some((left, right)) = self.span {
            x.clamp(left + 6., (right - 6.).max(left + 6.))
        } else {
            return false;
        };
        if (target - self.perch.x).abs() < 0.5 {
            return false;
        }
        self.cancel_activity();
        self.target = Point::new(target, self.perch.y);
        self.motion = Motion::WalkOut;
        self.motion_time = 0.;
        self.facing_left = target < self.position.x;
        true
    }
    /// Advance wall-clock deadlines by the complete duration; integrate spring physics in stable 50 ms steps.
    pub fn update(&mut self, dt: Duration) {
        let dt = dt.as_secs_f64();
        self.clock += dt;
        if !self.visible {
            return;
        }
        let mut remaining = dt;
        while remaining > 0. {
            if !self.visible {
                break;
            }
            let step = remaining.min(0.05);
            self.tick(step);
            remaining -= step;
        }
    }
    fn tick(&mut self, dt: f64) {
        self.update_effects(dt);
        if self.carrying || self.motion == Motion::Tumble {
            self.update_grip(dt);
        }
        self.motion_time += dt;
        self.flap_time += dt;
        match self.motion {
            Motion::Arrive | Motion::Fly | Motion::Leave | Motion::FlyOff | Motion::Shove => {
                self.update_motion()
            }
            Motion::Away => {
                if self.motion_time >= self.motion_duration {
                    self.opacity = self.base_opacity;
                    self.position = Point::new(self.perch.x, self.perch.y - 320.);
                    self.start_motion(Motion::Arrive, self.perch, 0.450);
                }
            }
            Motion::Bump => {
                self.frame = Frame::ShoveB;
                self.position = self.target;
                if self.motion_time < 0.020 {
                    let dx = self.target.x - self.from.x;
                    let dy = self.target.y - self.from.y;
                    let distance = dx.hypot(dy);
                    self.position.x += if distance > 0.5 {
                        dx / distance * 3.
                    } else {
                        3.
                    };
                    self.position.y += if distance > 0.5 {
                        dy / distance * 3.
                    } else {
                        0.
                    };
                }
                if self.motion_time >= 0.040 {
                    self.events.push_back(CompanionEvent::ShoveDone);
                    self.hide();
                }
            }
            Motion::Tumble => {
                self.frame = if ((self.flap_time * 10.) as usize).is_multiple_of(2) {
                    Frame::Tumble1
                } else {
                    Frame::Tumble2
                };
            }
            Motion::WalkOut | Motion::WalkBack | Motion::Return => self.update_walk(dt),
            Motion::WalkPause => {
                if self.motion_time >= self.motion_duration {
                    self.begin_walk_back();
                }
            }
            Motion::Rest => {
                if self.carrying {
                    self.flap_frame();
                } else if self.act == Act::Flap {
                    self.flap_frame();
                    self.act_time += dt;
                    if self.act_time >= 0.55 {
                        self.rest();
                    }
                } else if self.act != Act::None {
                    self.update_act(dt);
                } else {
                    self.update_rest(dt);
                }
            }
        }
    }
    fn update_motion(&mut self) {
        let p = (self.motion_time / self.motion_duration).min(1.);
        match self.motion {
            Motion::Arrive => {
                // WPF BackEase, EaseOut, Amplitude=0.5, as in the original arrival.
                let q = 1. - p;
                let eased = 1. - q.powi(3) + q * 0.5 * (PI * q).sin();
                self.position = lerp(self.from, self.perch, eased);
                self.flap_frame();
            }
            Motion::Fly => {
                let q = if p < 0.5 {
                    2. * p * p
                } else {
                    1. - (-2. * p + 2.).powi(2) / 2.
                };
                self.position = lerp(self.from, self.perch, q);
                self.position.y -= 10. * (PI * q).sin();
                self.flap_frame();
            }
            Motion::Shove => {
                self.position = lerp(self.from, self.target, p * p);
                self.frame = Frame::ShoveA;
            }
            Motion::Leave | Motion::FlyOff => {
                self.position.y = if p < 0.3 {
                    let q = p / 0.3;
                    self.from.y - 10. * (1. - (1. - q).powi(2))
                } else {
                    let q = (p - 0.3) / 0.7;
                    self.from.y - 10. + (self.target.y - self.from.y + 10.) * q * q
                };
                self.opacity = self.base_opacity
                    * if p < 0.4 {
                        1.
                    } else {
                        ((1. - p) / 0.6).max(0.)
                    };
                self.flap_frame();
            }
            _ => {}
        }
        if p < 1. {
            return;
        }
        match self.motion {
            Motion::Arrive | Motion::Fly => {
                let arrived = self.motion == Motion::Arrive;
                self.position = self.perch;
                self.motion = Motion::Rest;
                self.last_perch = self.perch;
                self.perch_velocity = Point::new(0., 0.);
                self.events.push_back(CompanionEvent::Landed);
                self.dust();
                if arrived {
                    self.reset_mood();
                }
                self.rest();
            }
            Motion::Leave => self.hide(),
            Motion::FlyOff => {
                let switching = self.pending_bird.is_some();
                if let Some(bird) = self.pending_bird.take() {
                    self.bird = bird;
                }
                self.motion = Motion::Away;
                self.motion_time = 0.;
                self.motion_duration = if switching { 0.150 } else { 1. };
                self.frame = Frame::IdleOpen;
            }
            Motion::Shove => {
                self.position = self.target;
                self.motion = Motion::Bump;
                self.motion_time = 0.;
                self.frame = Frame::ShoveB;
            }
            _ => {}
        }
    }
    fn update_grip(&mut self, dt: f64) {
        let a = 1. - (-dt / 0.060).exp();
        let ix = (self.perch.x - self.last_perch.x) / dt;
        let iy = (self.perch.y - self.last_perch.y) / dt;
        self.last_perch = self.perch;
        self.perch_velocity.x += (ix - self.perch_velocity.x) * a;
        self.perch_velocity.y += (iy - self.perch_velocity.y) * a;
        let speed = self.perch_velocity.x.hypot(self.perch_velocity.y);
        if self.motion == Motion::Rest && self.carrying && !self.self_drag && speed > 1400. {
            self.lose_grip();
        }
        if self.motion != Motion::Tumble {
            return;
        }
        let goal = Point::new(self.perch.x, self.perch.y + 40.);
        self.velocity.x += (80. * (goal.x - self.position.x) - 13. * self.velocity.x) * dt;
        self.velocity.y += (80. * (goal.y - self.position.y) - 13. * self.velocity.y) * dt;
        self.position.x += self.velocity.x * dt;
        self.position.y += self.velocity.y * dt;
        let dx = self.position.x - goal.x;
        let dy = self.position.y - goal.y;
        let d = dx.hypot(dy);
        if d > 240. {
            self.position = Point::new(goal.x + dx * 240. / d, goal.y + dy * 240. / d);
        }
        self.calm_time = if speed < 600. {
            self.calm_time + dt
        } else {
            0.
        };
        if self.calm_time >= 0.120 {
            self.catch();
        }
    }
    fn update_act(&mut self, dt: f64) {
        self.act_time += dt;
        while self.step < self.steps.len() && self.act_time >= self.steps[self.step].seconds {
            self.act_time -= self.steps[self.step].seconds;
            self.step += 1;
            if self.step >= self.steps.len() {
                let act = self.act;
                let overshoot = self.act_time;
                let queued = self.peck_queued;
                self.cancel_act();
                if act == Act::Fume {
                    self.start_motion(
                        Motion::FlyOff,
                        Point::new(self.position.x, self.perch.y - 320.),
                        0.300,
                    );
                } else if act == Act::Peck && queued {
                    self.play(Act::Peck, &PECK);
                    self.act_time = overshoot;
                    continue;
                } else {
                    self.rest();
                }
                return;
            }
            self.frame = self.steps[self.step].frame;
            if self.act == Act::Eat && self.step == 6 {
                self.hearts();
                self.sound(SoundKind::Happy);
            }
            if self.act == Act::Seed {
                self.seed_position = if self.step == 3 {
                    Some(self.seed_spot)
                } else {
                    None
                };
            }
        }
    }
    fn update_rest(&mut self, dt: f64) {
        if self.blink_hold > 0. {
            self.blink_hold -= dt;
            if self.blink_hold <= 0. {
                self.frame = Frame::IdleOpen;
                self.blink_in = 2. + self.random_unit() * 3.;
            }
        } else {
            self.blink_in -= dt;
            if self.blink_in <= 0. {
                self.frame = Frame::IdleBlink;
                self.blink_hold = 0.120;
            }
        }
        self.idle_in -= dt;
        if self.idle_in <= 0. && self.blink_hold <= 0. {
            self.fidget();
        }
    }
    fn fidget(&mut self) {
        let roll = (self.random_unit() * 300.) as u32;
        if roll < 75 {
            self.play(Act::Seed, &SEED);
            let gap = 3. + (self.random_unit() * 3.).floor();
            self.seed_spot = Point::new(
                self.position.x + (15. + gap) * 3. - WIDTH / 2.,
                self.position.y - 3.,
            );
            self.seed_position = Some(self.seed_spot);
        } else if roll < 125 {
            self.play(Act::Idle, &RUFFLE);
        } else if roll < 185 {
            let direction = if self.random_unit() < 0.5 { -1. } else { 1. };
            let started = self.try_walk(direction) || self.try_walk(-direction);
            if !started {
                self.schedule_rest();
            }
        } else {
            let variations: [&[Step]; 5] = [&TILT_L, &TILT_R, &PREEN, &HOP, &GLANCE];
            self.play(Act::Idle, variations[((roll - 185) * 5 / 115) as usize]);
        }
    }
    fn try_walk(&mut self, direction: f64) -> bool {
        let Some((left, right)) = self.span else {
            return false;
        };
        let room = if direction > 0. {
            right - self.perch.x
        } else {
            self.perch.x - left
        };
        if room < WIDTH {
            return false;
        }
        self.walk_to(self.perch.x + direction * (2. * WIDTH).min(room - 6.))
    }
    fn update_walk(&mut self, dt: f64) {
        let speed = if self.motion == Motion::Return {
            (self.target.x - self.from.x).abs() / 0.120
        } else {
            60.
        };
        let dx = self.target.x - self.position.x;
        if dx.abs() <= speed * dt {
            self.position = self.target;
            if self.motion == Motion::WalkOut {
                self.motion = Motion::WalkPause;
                self.motion_time = 0.;
                self.motion_duration = 0.3 + self.random_unit() * 0.5;
                self.frame = Frame::IdleOpen;
            } else {
                self.motion = Motion::Rest;
                self.stray = false;
                self.rest();
            }
        } else {
            self.position.x += dx.signum() * speed * dt;
            self.frame = if self.motion == Motion::Return {
                Frame::IdleOpen
            } else if ((self.motion_time * 8.) as usize).is_multiple_of(2) {
                Frame::Walk1
            } else {
                Frame::Walk2
            };
        }
    }
    fn begin_walk_back(&mut self) {
        self.motion = Motion::WalkBack;
        self.target = self.perch;
        self.motion_time = 0.;
        self.facing_left = self.target.x < self.position.x;
    }
    fn play(&mut self, act: Act, steps: &'static [Step]) {
        if matches!(
            self.motion,
            Motion::WalkOut | Motion::WalkPause | Motion::WalkBack | Motion::Return
        ) {
            self.motion = Motion::Rest;
            self.stray = true;
        }
        self.cancel_act();
        self.act = act;
        self.steps = steps;
        self.step = 0;
        self.act_time = 0.;
        self.frame = steps[0].frame;
        self.facing_left = false;
        self.blink_hold = 0.;
    }
    fn can_act(&self) -> bool {
        self.visible
            && matches!(
                self.motion,
                Motion::Rest
                    | Motion::WalkOut
                    | Motion::WalkPause
                    | Motion::WalkBack
                    | Motion::Return
            )
            && !self.carrying
    }
    fn cancel_act(&mut self) {
        self.act = Act::None;
        self.steps = &[];
        self.step = 0;
        self.peck_queued = false;
        self.seed_position = None;
    }
    fn cancel_activity(&mut self) {
        self.cancel_act();
        self.blink_hold = 0.;
        self.stray = false;
    }
    fn start_motion(&mut self, motion: Motion, target: Point, duration: f64) {
        self.cancel_activity();
        self.from = self.position;
        self.target = target;
        self.motion = motion;
        self.motion_time = 0.;
        self.motion_duration = duration;
        self.flap_time = 0.;
    }
    fn rest(&mut self) {
        self.cancel_act();
        self.frame = Frame::IdleOpen;
        self.facing_left = false;
        if self.stray && (self.position.x - self.perch.x).abs() > 0.5 {
            self.stray = false;
            self.from = self.position;
            self.target = self.perch;
            self.motion = Motion::Return;
            self.motion_time = 0.;
            return;
        }
        self.stray = false;
        self.schedule_rest();
    }
    fn schedule_rest(&mut self) {
        self.blink_in = 2. + self.random_unit() * 3.;
        self.blink_hold = 0.;
        self.idle_in = 20. + self.random_unit() * 40.;
    }
    fn reset_mood(&mut self) {
        self.click_streak = 0;
        self.annoyed.clear();
        self.mood = Mood::Calm;
    }
    fn sound(&mut self, kind: SoundKind) {
        self.events.push_back(CompanionEvent::Sound(kind));
    }
    fn flap_frame(&mut self) {
        let speed = self.perch_velocity.x.hypot(self.perch_velocity.y);
        let fps = if self.carrying && speed > 900. {
            20.
        } else {
            14.
        };
        self.frame = FLAP[((self.flap_time * fps) as usize) % 4];
    }
    fn random_unit(&mut self) -> f64 {
        self.random ^= self.random << 13;
        self.random ^= self.random >> 7;
        self.random ^= self.random << 17;
        (self.random >> 11) as f64 / (1_u64 << 53) as f64
    }
    fn hearts(&mut self) {
        self.effects.retain(|e| e.kind != EffectKind::Heart);
        for i in 0..3 {
            let position = Point::new(
                self.position.x - 7.5 + (i as f64 - 1.) * 16. + (self.random_unit() - 0.5) * 4.,
                self.position.y - 51.,
            );
            let phase = self.random_unit() * 2. * PI;
            self.effects.push(Effect {
                kind: EffectKind::Heart,
                position,
                opacity: 0.,
                age: 0.,
                lifetime: 0.9,
                velocity: Point::new(0., 0.),
                origin: position,
                delay: if i == 1 {
                    0.
                } else if i == 0 {
                    0.120
                } else {
                    0.240
                },
                phase,
            });
        }
    }
    fn dust(&mut self) {
        let since = self.clock - self.last_landing;
        self.last_landing = self.clock;
        if since < 2. {
            return;
        }
        let count = if since <= 60. {
            3
        } else {
            3 + (7. * ((since / 60.).ln() / 60_f64.ln()).min(1.)).round_ties_even() as usize
        };
        self.effects.retain(|e| e.kind != EffectKind::Dust);
        for i in 0..count {
            let side = if i.is_multiple_of(2) { -1. } else { 1. };
            let speed = 90. + self.random_unit() * 110.;
            let position = Point::new(
                self.position.x - 3. + side * (4. + self.random_unit() * 8.),
                self.position.y - 6. - self.random_unit() * 4.,
            );
            let velocity = Point::new(side * speed, -(10. + self.random_unit() * 40.));
            self.effects.push(Effect {
                kind: EffectKind::Dust,
                position,
                opacity: 0.,
                age: 0.,
                lifetime: 0.30,
                velocity,
                origin: position,
                delay: 0.,
                phase: 0.,
            });
        }
    }
    fn update_effects(&mut self, dt: f64) {
        for effect in &mut self.effects {
            if effect.delay > 0. {
                effect.delay -= dt;
                continue;
            }
            effect.age += dt;
            let p = (effect.age / effect.lifetime).min(1.);
            if effect.kind == EffectKind::Dust {
                let drag = (-6. * dt).exp();
                effect.velocity.x *= drag;
                effect.velocity.y *= drag;
                effect.position.x += effect.velocity.x * dt;
                effect.position.y += effect.velocity.y * dt;
                effect.opacity = (1. - p) * 0.7;
            } else {
                effect.position.x = effect.origin.x + (effect.phase + p * 7.).sin() * 3.;
                effect.position.y = effect.origin.y - 52. * (1. - (1. - p).powi(2));
                effect.opacity = if p < 0.4 { 1. } else { 1. - (p - 0.4) / 0.6 };
            }
        }
        self.effects.retain(|e| e.age < e.lifetime);
    }
}
fn lerp(from: Point, to: Point, p: f64) -> Point {
    Point::new(from.x + (to.x - from.x) * p, from.y + (to.y - from.y) * p)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn perched() -> Companion {
        let mut s = Companion::new(BirdId::Lennie, Point::new(100., 100.));
        s.arrive_at(Point::new(100., 100.));
        s.update(Duration::from_millis(900));
        s.drain_events();
        s
    }
    #[test]
    fn peck_queue_is_bounded_and_stops_within_one_extra_peck() {
        let mut s = perched();
        s.peck();
        for _ in 0..100 {
            s.peck();
        }
        s.update(Duration::from_millis(325));
        assert_eq!(s.act, Act::None);
        assert!(!s.animation_active());
    }
    #[test]
    fn a_blink_schedules_a_positive_hold_then_opens_its_eyes() {
        let mut s = perched();
        let until_blink = s.next_update_in().expect("visible rest deadline");
        s.update(until_blink + Duration::from_millis(1));
        assert_eq!(s.frame(), Frame::IdleBlink);
        let hold = s.next_update_in().expect("blink hold deadline");
        assert!(hold > Duration::ZERO);
        assert!(hold <= Duration::from_millis(120));
        s.update(hold + Duration::from_millis(1));
        assert_eq!(s.frame(), Frame::IdleOpen);
        assert!(s.next_update_in().expect("rest deadline") > Duration::from_secs(1));
    }
    #[test]
    fn moods_flee_after_five_annoyed_chirps_then_return_calm() {
        let mut s = perched();
        for _ in 0..6 {
            s.click();
            s.update(Duration::from_millis(430));
        }
        s.click();
        assert_eq!(s.mood(), Mood::Fuming);
        s.update(Duration::from_millis(450));
        assert_eq!(s.motion, Motion::FlyOff);
        s.update(Duration::from_secs(2));
        assert!(s.visible());
        assert_eq!(s.motion, Motion::Rest);
        assert_eq!(s.mood(), Mood::Calm);
    }
    #[test]
    fn calm_gap_and_food_reset_click_streak() {
        let mut s = perched();
        s.click();
        s.click();
        assert_eq!(s.click(), Some(SoundKind::Annoyed));
        s.update(Duration::from_secs(3));
        assert_eq!(s.click(), Some(SoundKind::Peep));
        s.eat();
        assert_eq!(s.mood(), Mood::Calm);
        s.update(Duration::from_millis(510));
        assert!(s.effects.iter().any(|e| e.kind == EffectKind::Heart));
    }
    #[test]
    fn leaving_and_hidden_state_stop_animation_deadlines() {
        let mut s = perched();
        s.carry(Point::new(120., 120.));
        s.leave();
        s.update(Duration::from_secs(1));
        assert!(!s.visible());
        assert!(!s.animation_active());
        assert_eq!(s.next_update_in(), None);
    }
    #[test]
    fn bird_switch_changes_skin_only_when_out_of_sight() {
        let mut s = perched();
        s.switch_to(BirdId::Meggie);
        assert_eq!(s.bird(), BirdId::Lennie);
        s.update(Duration::from_millis(310));
        assert_eq!(s.bird(), BirdId::Meggie);
        assert_eq!(s.opacity(), 0.);
        s.update(Duration::from_secs(1));
        assert_eq!(s.position(), s.perch());
        assert_eq!(s.frame(), Frame::IdleOpen);
    }
    #[test]
    fn layout_changes_retarget_an_arrival_without_delaying_its_landing() {
        let mut s = Companion::new(BirdId::Lennie, Point::new(100., 100.));
        s.arrive_at(Point::new(100., 100.));
        s.update(Duration::from_millis(200));
        assert!(s.is_arriving());
        let moved_perch = Point::new(160., 120.);
        s.move_to(moved_perch);
        s.update(Duration::from_millis(251));
        assert!(!s.is_arriving());
        assert_eq!(s.position(), moved_perch);
        assert!(s.drain_events().contains(&CompanionEvent::Landed));
    }
    #[test]
    fn walk_moves_at_sixty_dips_per_second_and_returns() {
        let mut s = perched();
        s.set_perch_span(0., 500.);
        assert!(s.walk_to(232.));
        s.update(Duration::from_secs(1));
        assert!((s.position().x - 160.).abs() < 1e-8);
        s.update(Duration::from_secs(5));
        assert_eq!(s.position(), s.perch());
        assert_eq!(s.motion, Motion::Rest);
    }
    #[test]
    fn violent_drag_loses_grip_but_self_drag_does_not() {
        let mut s = perched();
        s.carry(s.perch());
        s.carry(Point::new(400., 100.));
        s.update(Duration::from_millis(16));
        assert!(s.is_tumbling());
        s.update(Duration::from_secs(1));
        assert!(!s.is_tumbling());
        s.release(s.perch());
        s.update(Duration::from_secs(1));
        s.carry(s.perch());
        s.set_self_drag(true);
        s.carry(Point::new(1000., 100.));
        s.update(Duration::from_millis(16));
        assert!(!s.is_tumbling());
    }
}
