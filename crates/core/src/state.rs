//! Persisted schema shared with the original Windows application.

use std::{collections::BTreeMap, fmt};

use serde::{
    Deserialize, Deserializer, Serialize,
    de::{MapAccess, Visitor},
};
use serde_json::{Value, value::RawValue};
use thiserror::Error;

/// The original palette remains valid even when the host uses a dark interface.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Swatch {
    pub name: &'static str,
    pub hex: &'static str,
}

pub const COLORS: [Swatch; 6] = [
    Swatch {
        name: "Yellow",
        hex: "#FFF59E",
    },
    Swatch {
        name: "Peach",
        hex: "#FFD3A8",
    },
    Swatch {
        name: "Pink",
        hex: "#FFC6DD",
    },
    Swatch {
        name: "Mint",
        hex: "#C4F0C9",
    },
    Swatch {
        name: "Sky",
        hex: "#BFE3FF",
    },
    Swatch {
        name: "Grey",
        hex: "#E3E1DC",
    },
];
pub const FONTS: [&str; 5] = [
    "Segoe UI",
    "Segoe Print",
    "Comic Sans MS",
    "Cascadia Code",
    "Bahnschrift",
];
pub const MIN_FONT_SIZE: u16 = 11;
pub const MAX_FONT_SIZE: u16 = 28;
pub const DEFAULT_FONT_SIZE: u16 = 15;
pub const MIN_OPACITY: u8 = 20;
pub const MAX_OPACITY: u8 = 100;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    #[default]
    Note,
    Dot,
    Closed,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum IdleSounds {
    Chirp,
    Ruffle,
    #[default]
    Both,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum IdleRate {
    Often,
    #[default]
    Mid,
    Rarely,
}

/// Every schema-v2 field. Optional coordinates replace the old app's NaN sentinel.
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NoteState {
    pub version: u32,
    pub text: String,
    pub x: f64,
    pub y: f64,
    pub color: String,
    pub opacity: u8,
    pub locked: bool,
    pub font: String,
    pub font_size: u16,
    pub mode: Mode,
    pub dot_x: Option<f64>,
    pub dot_y: Option<f64>,
    pub start_with_windows: bool,
    pub bird_stays: bool,
    pub bird_opaque: bool,
    pub sound_on: bool,
    pub volume: f64,
    pub idle_sounds: IdleSounds,
    pub idle_rate: IdleRate,
    pub plants_on: bool,
    pub manual_width: Option<f64>,
    pub manual_height: Option<f64>,
    pub bird: String,
    pub bird_names: BTreeMap<String, String>,
}

impl Default for NoteState {
    fn default() -> Self {
        Self {
            version: 2,
            text: String::new(),
            x: 100.0,
            y: 100.0,
            color: COLORS[0].hex.into(),
            opacity: 100,
            locked: false,
            font: FONTS[0].into(),
            font_size: DEFAULT_FONT_SIZE,
            mode: Mode::Note,
            dot_x: None,
            dot_y: None,
            start_with_windows: false,
            bird_stays: false,
            bird_opaque: true,
            sound_on: true,
            volume: 0.10,
            idle_sounds: IdleSounds::Both,
            idle_rate: IdleRate::Mid,
            plants_on: true,
            manual_width: None,
            manual_height: None,
            bird: "lennie".into(),
            bird_names: BTreeMap::new(),
        }
    }
}

#[derive(Debug, Error)]
pub enum StateError {
    #[error("invalid state JSON: {0}")]
    Json(#[from] serde_json::Error),
    #[error("state.json is not an object")]
    NotObject,
}

impl NoteState {
    /// Reads fields in source order. Malformed values leave earlier valid values intact.
    pub fn from_json(json: &str) -> Result<Self, StateError> {
        let json = clean_json(json.trim_start_matches('\u{feff}'));
        if !json.trim_start().starts_with('{') {
            let _: Value = serde_json::from_str(&json)?;
            return Err(StateError::NotObject);
        }
        Ok(serde_json::from_str(&json)?)
    }

    /// Writes normalized schema-v2 JSON without changing the caller's state.
    pub fn to_json(&self) -> Result<String, StateError> {
        let mut state = self.clone();
        state.normalize();
        state.x = round_decimal(state.x, 100.0);
        state.y = round_decimal(state.y, 100.0);
        state.dot_x = state.dot_x.map(|v| round_decimal(v, 100.0));
        state.dot_y = state.dot_y.map(|v| round_decimal(v, 100.0));
        state.manual_width = state.manual_width.map(|v| round_decimal(v, 100.0));
        state.manual_height = state.manual_height.map(|v| round_decimal(v, 100.0));
        state.volume = round_decimal(state.volume, 1000.0);
        Ok(serde_json::to_string_pretty(&state)?)
    }

    pub fn normalize(&mut self) {
        self.version = 2;
        if !COLORS
            .iter()
            .any(|swatch| swatch.hex.eq_ignore_ascii_case(&self.color))
        {
            self.color = COLORS[0].hex.into();
        }
        self.opacity = self.opacity.clamp(MIN_OPACITY, MAX_OPACITY);
        if !FONTS.contains(&self.font.as_str()) {
            self.font = FONTS[0].into();
        }
        self.font_size = self.font_size.clamp(MIN_FONT_SIZE, MAX_FONT_SIZE);
        if !self.x.is_finite() {
            self.x = 100.0;
        }
        if !self.y.is_finite() {
            self.y = 100.0;
        }
        self.dot_x = self.dot_x.filter(|v| v.is_finite());
        self.dot_y = self.dot_y.filter(|v| v.is_finite());
        if !self.volume.is_finite() {
            self.volume = 0.10;
        }
        self.volume = self.volume.clamp(0.0, 1.0);
        self.manual_width = self.manual_width.filter(|v| v.is_finite() && *v > 0.0);
        self.manual_height = self.manual_height.filter(|v| v.is_finite() && *v > 0.0);
        self.bird = self.bird.trim().to_lowercase();
        if self.bird.is_empty() {
            self.bird = "lennie".into();
        }
        self.bird_names = std::mem::take(&mut self.bird_names)
            .into_iter()
            .filter_map(|(id, name)| {
                let name = name.trim();
                (name.encode_utf16().count() >= 3).then(|| (id.to_lowercase(), name.to_owned()))
            })
            .collect();
    }

    pub fn has_dot_position(&self) -> bool {
        self.dot_x.is_some_and(f64::is_finite) && self.dot_y.is_some_and(f64::is_finite)
    }

    /// Unknown ids remain persisted, but display and sound fall back to Lennie.
    pub fn active_bird_id(&self) -> &str {
        match self.bird.as_str() {
            "meggie" => "meggie",
            _ => "lennie",
        }
    }

    pub fn bird_name(&self, id: &str) -> &str {
        let id = id.to_lowercase();
        if let Some(name) = self.bird_names.get(&id) {
            return name;
        }
        match id.as_str() {
            "meggie" => "Meggie",
            _ => "Lennie",
        }
    }

    fn apply_property(&mut self, key: &str, value: &Value, raw: &str) {
        match key.to_ascii_lowercase().as_str() {
            "text" => assign_string(&mut self.text, value),
            "x" => assign_number(&mut self.x, value),
            "y" => assign_number(&mut self.y, value),
            "color" => assign_string(&mut self.color, value),
            "opacity" => {
                if let Some(mut number) = number(value) {
                    if number > 0.0 && number <= 1.0 && raw.contains('.') {
                        number *= 100.0;
                    }
                    self.opacity =
                        rounded_bounded(number, f64::from(MIN_OPACITY), f64::from(MAX_OPACITY))
                            as u8;
                }
            }
            "locked" => assign_bool(&mut self.locked, value),
            "font" => assign_string(&mut self.font, value),
            "fontsize" => {
                if let Some(number) = number(value) {
                    self.font_size =
                        rounded_bounded(number, f64::from(MIN_FONT_SIZE), f64::from(MAX_FONT_SIZE))
                            as u16;
                }
            }
            "mode" => {
                if let Some(mode) = mode(value) {
                    self.mode = mode;
                }
            }
            "dotx" => self.dot_x = number(value),
            "doty" => self.dot_y = number(value),
            "startwithwindows" => assign_bool(&mut self.start_with_windows, value),
            "birdstays" => assign_bool(&mut self.bird_stays, value),
            "birdopaque" => assign_bool(&mut self.bird_opaque, value),
            "soundon" => assign_bool(&mut self.sound_on, value),
            "volume" => assign_number(&mut self.volume, value),
            "idlesounds" => {
                if let Some(sounds) = value.as_str().and_then(|name| {
                    match name.trim().to_ascii_lowercase().as_str() {
                        "chirp" | "0" => Some(IdleSounds::Chirp),
                        "ruffle" | "1" => Some(IdleSounds::Ruffle),
                        "both" | "2" => Some(IdleSounds::Both),
                        _ => None,
                    }
                }) {
                    self.idle_sounds = sounds;
                }
            }
            "idlerate" => {
                if let Some(rate) = value.as_str().and_then(|name| {
                    match name.trim().to_ascii_lowercase().as_str() {
                        "often" | "0" => Some(IdleRate::Often),
                        "mid" | "1" => Some(IdleRate::Mid),
                        "rarely" | "2" => Some(IdleRate::Rarely),
                        _ => None,
                    }
                }) {
                    self.idle_rate = rate;
                }
            }
            "plantson" => assign_bool(&mut self.plants_on, value),
            "manualwidth" => self.manual_width = number(value),
            "manualheight" => self.manual_height = number(value),
            "bird" => assign_string(&mut self.bird, value),
            _ => {}
        }
    }
}

impl<'de> Deserialize<'de> for NoteState {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct StateVisitor;

        impl<'de> Visitor<'de> for StateVisitor {
            type Value = NoteState;

            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str("a state object")
            }

            fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
                let mut state = NoteState::default();
                while let Some((key, raw)) = map.next_entry::<String, Box<RawValue>>()? {
                    if key.eq_ignore_ascii_case("birdNames") {
                        // A Value map would discard duplicate bird ids before we can apply
                        // the legacy rule that an invalid name preserves the prior valid one.
                        if raw.get().trim_start().starts_with('{') {
                            let names: BirdNames = serde_json::from_str(raw.get())
                                .map_err(serde::de::Error::custom)?;
                            state.bird_names.extend(names.0);
                        }
                    } else {
                        let value =
                            serde_json::from_str(raw.get()).map_err(serde::de::Error::custom)?;
                        state.apply_property(&key, &value, raw.get());
                    }
                }
                state.normalize();
                Ok(state)
            }
        }

        deserializer.deserialize_map(StateVisitor)
    }
}

struct BirdNames(BTreeMap<String, String>);

impl<'de> Deserialize<'de> for BirdNames {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct NamesVisitor;

        impl<'de> Visitor<'de> for NamesVisitor {
            type Value = BirdNames;

            fn expecting(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
                formatter.write_str("a bird name object")
            }

            fn visit_map<M: MapAccess<'de>>(self, mut map: M) -> Result<Self::Value, M::Error> {
                let mut names = BTreeMap::new();
                while let Some((id, value)) = map.next_entry::<String, Value>()? {
                    if let Some(name) = value.as_str().map(str::trim)
                        && name.encode_utf16().count() >= 3
                    {
                        names.insert(id.to_lowercase(), name.to_owned());
                    }
                }
                Ok(BirdNames(names))
            }
        }

        deserializer.deserialize_map(NamesVisitor)
    }
}

fn number(value: &Value) -> Option<f64> {
    let number = match value {
        Value::Number(number) => number.as_f64(),
        Value::String(text) => text.trim().parse::<f64>().ok(),
        _ => None,
    }?;
    number.is_finite().then_some(number)
}

fn assign_number(target: &mut f64, value: &Value) {
    if let Some(number) = number(value) {
        *target = number;
    }
}

fn assign_string(target: &mut String, value: &Value) {
    if let Some(text) = value.as_str() {
        *target = text.into();
    }
}

fn assign_bool(target: &mut bool, value: &Value) {
    let parsed = match value {
        Value::Bool(value) => Some(*value),
        Value::Number(value) => value.as_f64().map(|v| v != 0.0),
        Value::String(value) if value.trim().eq_ignore_ascii_case("true") => Some(true),
        Value::String(value) if value.trim().eq_ignore_ascii_case("false") => Some(false),
        _ => None,
    };
    if let Some(value) = parsed {
        *target = value;
    }
}

fn mode(value: &Value) -> Option<Mode> {
    match value
        .as_str()
        .map(str::trim)
        .map(str::to_ascii_lowercase)
        .as_deref()
    {
        Some("note" | "0") => Some(Mode::Note),
        Some("dot" | "1") => Some(Mode::Dot),
        Some("closed" | "2") => Some(Mode::Closed),
        Some(_) => None,
        None => match value.as_i64() {
            Some(0) => Some(Mode::Note),
            Some(1) => Some(Mode::Dot),
            Some(2) => Some(Mode::Closed),
            _ => None,
        },
    }
}

fn rounded_bounded(value: f64, min: f64, max: f64) -> f64 {
    value.round_ties_even().clamp(min, max)
}

fn round_decimal(value: f64, scale: f64) -> f64 {
    let scaled = value * scale;
    if scaled.is_finite() {
        scaled.round_ties_even() / scale
    } else {
        value
    }
}

// Replace comments with whitespace so JSON error locations still point at the source.
// String contents, including URLs and escaped quotes, are left byte-for-byte intact.
fn clean_json(input: &str) -> String {
    let bytes = input.as_bytes();
    let mut output = bytes.to_vec();
    let mut index = 0;
    let mut in_string = false;
    while index < bytes.len() {
        if in_string {
            match bytes[index] {
                b'\\' => {
                    index += 2;
                    continue;
                }
                b'"' => in_string = false,
                _ => {}
            }
        } else if bytes[index] == b'"' {
            in_string = true;
        } else if bytes[index] == b'/' && bytes.get(index + 1) == Some(&b'/') {
            while index < bytes.len() && bytes[index] != b'\n' {
                output[index] = b' ';
                index += 1;
            }
            continue;
        } else if bytes[index] == b'/' && bytes.get(index + 1) == Some(&b'*') {
            let start = index;
            index += 2;
            while index + 1 < bytes.len() && &bytes[index..index + 2] != b"*/" {
                index += 1;
            }
            if index + 1 >= bytes.len() {
                break;
            } // Leave invalid unterminated comments for serde to reject.
            index += 2;
            for byte in &mut output[start..index] {
                if *byte != b'\n' && *byte != b'\r' {
                    *byte = b' ';
                }
            }
            continue;
        }
        index += 1;
    }
    in_string = false;
    index = 0;
    while index < output.len() {
        if in_string {
            match output[index] {
                b'\\' => {
                    index += 2;
                    continue;
                }
                b'"' => in_string = false,
                _ => {}
            }
        } else if output[index] == b'"' {
            in_string = true;
        } else if output[index] == b',' {
            let next = output[index + 1..]
                .iter()
                .find(|byte| !byte.is_ascii_whitespace());
            if next.is_some_and(|byte| matches!(*byte, b'}' | b']')) {
                output[index] = b' ';
            }
        }
        index += 1;
    }
    // Only ASCII comment bytes were replaced, preserving UTF-8 everywhere else.
    String::from_utf8(output).expect("replacing comment bytes with ASCII preserves UTF-8")
}
