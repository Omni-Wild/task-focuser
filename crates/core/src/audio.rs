//! Deterministic, device-independent synthesis of the two original companion voices.

use crate::{
    sprites::BirdId,
    state::{IdleRate, IdleSounds},
};
use std::{
    f64::consts::PI,
    io::{self, Write},
    time::Duration,
};

pub const SAMPLE_RATE: u32 = 44_100;
pub const DEFAULT_VOLUME: f64 = 0.10;
const RATE: f64 = SAMPLE_RATE as f64;
const LEAD: f64 = 0.010;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SoundKind {
    Keepalive,
    Happy,
    Annoyed,
    Peep,
    Ruffle,
}
impl SoundKind {
    pub const ALL: [Self; 5] = [
        Self::Keepalive,
        Self::Happy,
        Self::Annoyed,
        Self::Peep,
        Self::Ruffle,
    ];
}

fn unit(value: f64) -> f64 {
    if value.is_finite() {
        value.clamp(0.0, 1.0)
    } else {
        0.0
    }
}
pub fn slider_to_volume(position: f64) -> f64 {
    let p = unit(position);
    if p <= 0.5 {
        0.2 * p
    } else {
        0.1 + 1.8 * (p - 0.5)
    }
}
pub fn volume_to_slider(volume: f64) -> f64 {
    let v = unit(volume);
    if v <= 0.1 {
        v / 0.2
    } else {
        0.5 + (v - 0.1) / 1.8
    }
}

#[derive(Clone, Copy)]
struct Note {
    start: f64,
    length: f64,
    f0: f64,
    f1: f64,
    attack: f64,
    tail: f64,
    decay: f64,
    rough: bool,
}
impl Note {
    const fn budgie(start: f64, length: f64, f0: f64, f1: f64, decay: f64, rough: bool) -> Self {
        Self {
            start: LEAD + start,
            length,
            f0,
            f1,
            attack: 0.008,
            tail: 0.012,
            decay,
            rough,
        }
    }
    const fn owl(
        start: f64,
        length: f64,
        f0: f64,
        f1: f64,
        attack: f64,
        tail: f64,
        rough: bool,
    ) -> Self {
        Self {
            start: LEAD + start,
            length,
            f0,
            f1,
            attack,
            tail,
            decay: 0.30,
            rough,
        }
    }
}

/// Synthesize signed 16-bit mono PCM at [`SAMPLE_RATE`]. Playback and device selection belong to the host.
pub fn render(bird: BirdId, kind: SoundKind) -> Vec<i16> {
    if kind == SoundKind::Ruffle {
        let (f0, length, band, low) = if bird == BirdId::Meggie {
            (210.0, 0.360, 700.0, 1400.0)
        } else {
            (420.0, 0.320, 1400.0, 2800.0)
        };
        return pcm(&ruffle(f0, length, band, low), 0.398);
    }
    use SoundKind::*;
    let (notes, peak): (Vec<Note>, f64) = match (bird, kind) {
        (BirdId::Lennie, Keepalive) => (
            vec![
                Note::budgie(0., 0.070, 1600., 1650., 0.060, false),
                Note::budgie(0.130, 0.070, 2000., 2060., 0.060, false),
            ],
            0.50,
        ),
        (BirdId::Lennie, Happy) => (
            vec![
                Note::budgie(0., 0.080, 1300., 1340., 0.080, false),
                Note::budgie(0.120, 0.080, 1600., 1650., 0.080, false),
                Note::budgie(0.240, 0.080, 2000., 2080., 0.080, false),
            ],
            0.50,
        ),
        (BirdId::Lennie, Annoyed) => (vec![Note::budgie(0., 0.180, 720., 640., 0.120, true)], 0.40),
        (BirdId::Lennie, _) => (
            vec![Note::budgie(0., 0.045, 1800., 1820., 0.060, false)],
            0.45,
        ),
        (BirdId::Meggie, Keepalive) => (
            vec![
                Note::owl(0., 0.140, 390., 380., 0.025, 0.100, false),
                Note::owl(0.230, 0.140, 330., 322., 0.025, 0.100, false),
            ],
            0.50,
        ),
        (BirdId::Meggie, Happy) => (
            vec![
                Note::owl(0., 0.120, 330., 334., 0.020, 0.080, false),
                Note::owl(0.180, 0.120, 370., 375., 0.020, 0.080, false),
                Note::owl(0.360, 0.120, 420., 427., 0.020, 0.080, false),
            ],
            0.50,
        ),
        (BirdId::Meggie, Annoyed) => (
            vec![Note::owl(0., 0.200, 560., 480., 0.010, 0.060, true)],
            0.398,
        ),
        (BirdId::Meggie, _) => (
            vec![Note::owl(0., 0.070, 330., 326., 0.015, 0.045, false)],
            0.45,
        ),
    };
    let end = notes.iter().map(|n| n.start + n.length).fold(0., f64::max) + 0.020;
    let mut buf = vec![0.; (end * RATE) as usize];
    let mut rng = LegacyRandom::new(0x0771);
    for n in notes {
        add_note(&mut buf, n, bird, &mut rng);
    }
    pcm(&buf, peak)
}
fn triangle(p: f64) -> f64 {
    p.sin() - (3. * p).sin() / 9. + (5. * p).sin() / 25. - (7. * p).sin() / 49.
}
fn add_note(buf: &mut [f64], n: Note, bird: BirdId, rng: &mut LegacyRandom) {
    let owl = bird == BirdId::Meggie;
    let (vib_hz, vib_cents) = if owl { (4.5, 3.) } else { (5., 4.) };
    let mut breath = Biquad::low_pass(900., 0.707);
    let mut phase = 0.;
    let first = (n.start * RATE) as usize;
    for i in 0..(n.length * RATE) as usize {
        let Some(sample) = buf.get_mut(first + i) else {
            break;
        };
        let t = i as f64 / RATE;
        let f = (n.f0 + (n.f1 - n.f0) * t / n.length)
            * 2_f64.powf(vib_cents * (2. * PI * vib_hz * t).sin() / 1200.);
        phase += 2. * PI * f / RATE;
        let s = if owl {
            let air = breath.next(rng.next_double() * 2. - 1.);
            if n.rough {
                (triangle(phase) + 0.35 * air)
                    * (1. - 0.6 * (0.5 - 0.5 * (2. * PI * 28. * t).cos()))
            } else {
                phase.sin() + 0.10 * (2. * phase + 0.5).sin() + 0.25 * air
            }
        } else if n.rough {
            triangle(phase) * (1. - 0.5 * (0.5 - 0.5 * (2. * PI * 25. * t).cos()))
        } else {
            phase.sin() + 0.15 * (2. * phase).sin()
        };
        let mut env = if t < n.attack {
            0.5 - 0.5 * (PI * t / n.attack).cos()
        } else {
            (-(t - n.attack) / n.decay).exp()
        };
        let left = n.length - t;
        if left < n.tail {
            env *= 0.5 - 0.5 * (PI * left.max(0.) / n.tail).cos();
        }
        *sample += s * env;
    }
}
fn pcm(buf: &[f64], peak: f64) -> Vec<i16> {
    let max = buf.iter().map(|x| x.abs()).fold(0., f64::max);
    let gain = if max > 0. { peak / max } else { 0. };
    buf.iter()
        .map(|s| (s * gain * i16::MAX as f64).round_ties_even() as i16)
        .collect()
}
fn ruffle(f0: f64, length: f64, band_hz: f64, low_hz: f64) -> Vec<f64> {
    let first = (LEAD * RATE) as usize;
    let count = (length * RATE) as usize;
    let mut buf = vec![0.; first + count + (0.020 * RATE) as usize];
    let mut rng = LegacyRandom::new(0x0B1D);
    let mut band = Biquad::band_pass(band_hz, 0.9);
    let mut low = Biquad::low_pass(low_hz, 0.707);
    let (mut phase, mut am_phase, mut waver) = (0., 0., 0.);
    let waver_k = 1. - (-2. * PI * 40. / RATE).exp();
    for i in 0..count {
        let t = i as f64 / RATE;
        let u = t / length;
        phase += 2. * PI * f0 * (1. - 0.08 * u) / RATE;
        am_phase += 2. * PI * (33. - 3. * u) / RATE;
        let white = rng.next_double() * 2. - 1.;
        let hiss = low.next(band.next(white));
        waver += waver_k * (white - waver);
        let tone = phase.sin() + (0.16 + 0.6 * waver) * (2. * phase + 0.4).sin();
        let am = 1. - 0.85 * (0.5 - 0.5 * am_phase.cos());
        let am_noise = 1. - 0.85 * (0.5 - 0.5 * (am_phase + 0.6).cos());
        let mut env = if t < 0.010 {
            0.5 - 0.5 * (PI * t / 0.010).cos()
        } else {
            (-(t - 0.010) / 0.45).exp()
        };
        if length - t < 0.080 {
            env *= 0.5 - 0.5 * (PI * (length - t).max(0.) / 0.080).cos();
        }
        buf[first + i] = (tone * am + 1.1 * hiss * am_noise) * env;
    }
    buf
}
struct Biquad {
    b0: f64,
    b1: f64,
    b2: f64,
    a1: f64,
    a2: f64,
    x1: f64,
    x2: f64,
    y1: f64,
    y2: f64,
}
impl Biquad {
    fn new(b0: f64, b1: f64, b2: f64, a0: f64, a1: f64, a2: f64) -> Self {
        Self {
            b0: b0 / a0,
            b1: b1 / a0,
            b2: b2 / a0,
            a1: a1 / a0,
            a2: a2 / a0,
            x1: 0.,
            x2: 0.,
            y1: 0.,
            y2: 0.,
        }
    }
    fn band_pass(f: f64, q: f64) -> Self {
        let w = 2. * PI * f / RATE;
        let a = w.sin() / (2. * q);
        Self::new(a, 0., -a, 1. + a, -2. * w.cos(), 1. - a)
    }
    fn low_pass(f: f64, q: f64) -> Self {
        let w = 2. * PI * f / RATE;
        let a = w.sin() / (2. * q);
        let c = w.cos();
        Self::new(
            (1. - c) / 2.,
            1. - c,
            (1. - c) / 2.,
            1. + a,
            -2. * c,
            1. - a,
        )
    }
    fn next(&mut self, x: f64) -> f64 {
        let y = self.b0 * x + self.b1 * self.x1 + self.b2 * self.x2
            - self.a1 * self.y1
            - self.a2 * self.y2;
        self.x2 = self.x1;
        self.x1 = x;
        self.y2 = self.y1;
        self.y1 = y;
        y
    }
}

/// The seeded .NET Random algorithm preserves the original noise samples as well as the note parameters.
struct LegacyRandom {
    seed: [i32; 56],
    next: usize,
    next_p: usize,
}
impl LegacyRandom {
    fn new(seed: i32) -> Self {
        let mut state = Self {
            seed: [0; 56],
            next: 0,
            next_p: 21,
        };
        let mut mj = 161803398 - seed.abs();
        state.seed[55] = mj;
        let mut mk = 1;
        for i in 1..55 {
            let ii = (21 * i) % 55;
            state.seed[ii] = mk;
            mk = mj - mk;
            if mk < 0 {
                mk += i32::MAX;
            }
            mj = state.seed[ii];
        }
        for _ in 0..4 {
            for i in 1..56 {
                state.seed[i] -= state.seed[1 + (i + 30) % 55];
                if state.seed[i] < 0 {
                    state.seed[i] += i32::MAX;
                }
            }
        }
        state
    }
    fn next_double(&mut self) -> f64 {
        self.next += 1;
        if self.next >= 56 {
            self.next = 1;
        }
        self.next_p += 1;
        if self.next_p >= 56 {
            self.next_p = 1;
        }
        let mut n = self.seed[self.next] - self.seed[self.next_p];
        if n == i32::MAX {
            n -= 1;
        }
        if n < 0 {
            n += i32::MAX;
        }
        self.seed[self.next] = n;
        n as f64 / i32::MAX as f64
    }
}

/// Encode a standard RIFF/WAVE stream without creating cache files or selecting an audio device.
pub fn write_wav(mut writer: impl Write, samples: &[i16]) -> io::Result<()> {
    let data = u32::try_from(samples.len())
        .ok()
        .and_then(|n| n.checked_mul(2))
        .filter(|n| *n <= u32::MAX - 36)
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "PCM stream exceeds RIFF size limit",
            )
        })?;
    writer.write_all(b"RIFF")?;
    writer.write_all(&(36 + data).to_le_bytes())?;
    writer.write_all(b"WAVEfmt ")?;
    writer.write_all(&16_u32.to_le_bytes())?;
    writer.write_all(&1_u16.to_le_bytes())?;
    writer.write_all(&1_u16.to_le_bytes())?;
    writer.write_all(&SAMPLE_RATE.to_le_bytes())?;
    writer.write_all(&(SAMPLE_RATE * 2).to_le_bytes())?;
    writer.write_all(&2_u16.to_le_bytes())?;
    writer.write_all(&16_u16.to_le_bytes())?;
    writer.write_all(b"data")?;
    writer.write_all(&data.to_le_bytes())?;
    for s in samples {
        writer.write_all(&s.to_le_bytes())?;
    }
    Ok(())
}

/// A single deadline, independent of note or companion visibility. Other sounds restart the interval.
#[derive(Debug, Clone)]
pub struct IdleSoundScheduler {
    enabled: bool,
    rate: IdleRate,
    sounds: IdleSounds,
    remaining: Duration,
    random: u64,
}
impl IdleSoundScheduler {
    pub fn new(seed: u64) -> Self {
        let mut s = Self {
            enabled: true,
            rate: IdleRate::Mid,
            sounds: IdleSounds::Both,
            remaining: Duration::ZERO,
            random: seed.max(1),
        };
        s.restart();
        s
    }
    pub fn configure(&mut self, enabled: bool, rate: IdleRate, sounds: IdleSounds) {
        self.enabled = enabled;
        self.rate = rate;
        self.sounds = sounds;
        self.restart();
    }
    /// Change the next idle sound's choice while keeping its existing deadline.
    pub fn set_sounds(&mut self, sounds: IdleSounds) {
        self.sounds = sounds;
    }
    pub fn restart(&mut self) {
        let (lo, hi) = match self.rate {
            IdleRate::Often => (90., 180.),
            IdleRate::Mid => (180., 360.),
            IdleRate::Rarely => (480., 900.),
        };
        let random = self.random_unit();
        self.remaining = Duration::from_secs_f64(lo + (hi - lo) * random);
    }
    pub fn deadline_in(&self) -> Option<Duration> {
        self.enabled.then_some(self.remaining)
    }
    pub fn update(&mut self, dt: Duration) -> Option<SoundKind> {
        if !self.enabled {
            return None;
        }
        if dt < self.remaining {
            self.remaining -= dt;
            return None;
        }
        let kind = match self.sounds {
            IdleSounds::Chirp => SoundKind::Keepalive,
            IdleSounds::Ruffle => SoundKind::Ruffle,
            IdleSounds::Both => {
                if self.random_unit() < 0.5 {
                    SoundKind::Keepalive
                } else {
                    SoundKind::Ruffle
                }
            }
        };
        self.restart();
        Some(kind)
    }
    fn random_unit(&mut self) -> f64 {
        self.random ^= self.random << 13;
        self.random ^= self.random >> 7;
        self.random ^= self.random << 17;
        (self.random >> 11) as f64 / (1_u64 << 53) as f64
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn nonlinear_volume_round_trips() {
        for p in [0., 0.25, 0.5, 0.75, 1.] {
            assert!((volume_to_slider(slider_to_volume(p)) - p).abs() < 1e-12);
        }
        assert_eq!(slider_to_volume(0.5), 0.1);
    }
    #[test]
    fn all_voices_are_deterministic_with_silent_edges_and_headroom() {
        for bird in BirdId::ALL {
            for kind in SoundKind::ALL {
                let samples = render(bird, kind);
                assert_eq!(samples, render(bird, kind));
                assert!(samples[..441].iter().all(|s| *s == 0));
                assert!(samples[samples.len() - 800..].iter().all(|s| *s == 0));
                let peak = samples
                    .iter()
                    .map(|s| i32::from(*s).abs())
                    .max()
                    .expect("samples");
                assert!((13000..=16400).contains(&peak));
            }
        }
    }
    #[test]
    fn wave_header_matches_data() {
        let samples = render(BirdId::Lennie, SoundKind::Peep);
        let mut wav = Vec::new();
        write_wav(&mut wav, &samples).expect("write memory");
        assert_eq!(&wav[..4], b"RIFF");
        assert_eq!(wav.len(), 44 + samples.len() * 2);
        assert_eq!(
            u32::from_le_bytes(wav[24..28].try_into().expect("rate")),
            SAMPLE_RATE
        );
    }
    #[test]
    fn intervals_and_disabled_state() {
        let mut scheduler = IdleSoundScheduler::new(4);
        for (rate, lo, hi) in [
            (IdleRate::Often, 90., 180.),
            (IdleRate::Mid, 180., 360.),
            (IdleRate::Rarely, 480., 900.),
        ] {
            scheduler.configure(true, rate, IdleSounds::Both);
            for _ in 0..30 {
                let d = scheduler.deadline_in().expect("enabled");
                assert!((lo..=hi).contains(&d.as_secs_f64()));
                assert!(scheduler.update(d).is_some());
            }
        }
        scheduler.configure(false, IdleRate::Often, IdleSounds::Chirp);
        assert_eq!(scheduler.deadline_in(), None);
        assert_eq!(scheduler.update(Duration::from_secs(10000)), None);
    }
}
