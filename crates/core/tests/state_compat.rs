#![allow(clippy::unwrap_used)] // Fixture setup and assertions should fail the test immediately.

use std::{collections::BTreeSet, fs, path::PathBuf};

use task_focuser_core::{
    profile::{AppPaths, PathEnvironment, Platform, Profile},
    state::{COLORS, FONTS, IdleRate, IdleSounds, Mode, NoteState, StateError},
    store::{LoadDiagnostic, StateStore},
};

#[test]
fn python_state_survives_and_new_fields_take_defaults() {
    let state = NoteState::from_json(r##"{"text":"Číst knihu\nDruhý řádek","x":-120,"y":52.5,"color":"#C4F0C9","opacity":75,"locked":true}"##).unwrap();
    assert_eq!(state.text, "Číst knihu\nDruhý řádek");
    assert_eq!(state.x, -120.0);
    assert_eq!(state.y, 52.5);
    assert_eq!(state.color, "#C4F0C9");
    assert_eq!(state.opacity, 75);
    assert!(state.locked);
    assert_eq!(state.font, FONTS[0]);
    assert_eq!(state.mode, Mode::Note);
    assert_eq!(state.dot_x, None);
    assert_eq!(state.bird, "lennie");
    assert_eq!(state.bird_name("meggie"), "Meggie");
}

#[test]
fn complete_v2_state_round_trips_with_all_original_keys() {
    let json = r##"{
        "version":2,"text":"My note","x":-10.25,"y":90.5,"color":"#E3E1DC",
        "opacity":65,"locked":true,"font":"Bahnschrift","fontSize":22,"mode":"closed",
        "dotX":800.25,"dotY":400.5,"startWithWindows":true,"birdStays":true,
        "birdOpaque":false,"soundOn":false,"volume":0.125,"idleSounds":"ruffle",
        "idleRate":"rarely","plantsOn":false,"manualWidth":600.25,"manualHeight":380.5,
        "bird":"meggie","birdNames":{"lennie":"Leonard","meggie":"Meggie Owl"}
    }"##;
    let state = NoteState::from_json(json).unwrap();
    assert_eq!(state.mode, Mode::Closed);
    assert_eq!(state.idle_sounds, IdleSounds::Ruffle);
    assert_eq!(state.idle_rate, IdleRate::Rarely);
    assert_eq!(state.bird_name("LENNIE"), "Leonard");
    assert_eq!(state.active_bird_id(), "meggie");
    let written = state.to_json().unwrap();
    let expected: serde_json::Value = serde_json::from_str(json).unwrap();
    let actual: serde_json::Value = serde_json::from_str(&written).unwrap();
    assert_eq!(expected, actual);
    assert_eq!(NoteState::from_json(&written).unwrap(), state);
    let keys = actual
        .as_object()
        .unwrap()
        .keys()
        .cloned()
        .collect::<BTreeSet<_>>();
    assert_eq!(keys.len(), 24);
    assert!(keys.contains("startWithWindows"));
    assert!(!keys.contains("start_with_windows"));
}

#[test]
fn malformed_fields_never_discard_valid_note_text() {
    let state = NoteState::from_json(
        r##"{
        "TEXT":"Keep this note", "X":"-42.5", "Y":{}, "COLOR":[],
        "OPACITY":"0.65", "LOCKED":"TrUe", "FONTSIZE":"22.5", "MODE":1,
        "DOTX":"Infinity", "DOTY":true, "VOLUME":"NaN", "BIRDOPAQUE":0,
        "SOUNDON":-2, "PLANTSON":"false", "BIRD": " MEGGIE ",
        "IDLESOUNDS":"CHIRP","IDLERATE":"OFTEN",
        "BIRDNAMES":{"LENNIE":"  Jiří  ","meggie":"ab","other":42}
    }"##,
    )
    .unwrap();
    assert_eq!(state.text, "Keep this note");
    assert_eq!(state.x, -42.5);
    assert_eq!(state.y, 100.0);
    assert_eq!(state.opacity, 65);
    assert_eq!(state.font_size, 22);
    assert_eq!(state.mode, Mode::Dot);
    assert!(state.locked);
    assert!(!state.bird_opaque);
    assert!(state.sound_on);
    assert!(!state.plants_on);
    assert_eq!(state.volume, 0.1);
    assert_eq!(state.dot_x, None);
    assert_eq!(state.dot_y, None);
    assert_eq!(state.bird, "meggie");
    assert_eq!(state.bird_name("lennie"), "Jiří");
    assert_eq!(state.bird_name("meggie"), "Meggie");
    assert_eq!(state.idle_sounds, IdleSounds::Chirp);
    assert_eq!(state.idle_rate, IdleRate::Often);
}

#[test]
fn comments_trailing_commas_and_bom_are_accepted_without_changing_strings() {
    let state = NoteState::from_json(concat!(
        "\u{feff}",
        r#"{
        // Earlier versions allowed comments.
        "text": "https://example.test/*literal*/\"hi\",}",
        /* A multi-line
           comment with diacritics čřž. */
        "birdNames": { "lennie": "Lennie", },
        "fontSize": 18,
    }"#
    ))
    .unwrap();
    assert_eq!(state.text, "https://example.test/*literal*/\"hi\",}");
    assert_eq!(state.font_size, 18);
    assert!(NoteState::from_json("{/* unfinished").is_err());
}

#[test]
fn fractional_opacity_and_clamping_follow_legacy_conventions() {
    for (value, expected) in [
        ("0.5", 50),
        ("1.0", 100),
        ("1", 20),
        ("0", 20),
        ("-20", 20),
        ("999", 100),
        ("\"0.45\"", 45),
    ] {
        let state = NoteState::from_json(&format!(
            r#"{{"opacity":{value},"fontSize":999,"volume":-9}}"#
        ))
        .unwrap();
        assert_eq!(state.opacity, expected, "{value}");
        assert_eq!(state.font_size, 28);
        assert_eq!(state.volume, 0.0);
    }
}

#[test]
fn normalize_removes_nonfinite_coordinates_and_invalid_dimensions() {
    let mut state = NoteState {
        version: 99,
        x: f64::INFINITY,
        y: f64::NAN,
        dot_x: Some(f64::NAN),
        dot_y: Some(20.0),
        manual_width: Some(-1.0),
        manual_height: Some(f64::INFINITY),
        volume: f64::NAN,
        color: "unknown".into(),
        font: "missing".into(),
        bird: " future-bird ".into(),
        ..NoteState::default()
    };
    state.normalize();
    assert_eq!(state.version, 2);
    assert_eq!(state.x, 100.0);
    assert_eq!(state.y, 100.0);
    assert_eq!(state.dot_x, None);
    assert!(!state.has_dot_position());
    assert_eq!(state.manual_width, None);
    assert_eq!(state.manual_height, None);
    assert_eq!(state.volume, 0.1);
    assert_eq!(state.color, COLORS[0].hex);
    assert_eq!(state.font, FONTS[0]);
    assert_eq!(state.bird, "future-bird");
    assert_eq!(state.active_bird_id(), "lennie");
    assert!(state.to_json().unwrap().contains("\"dotX\": null"));
}

#[test]
fn non_objects_and_broken_json_fail_explicitly() {
    for json in ["null", "[]", "42", "\"hello\""] {
        assert!(matches!(
            NoteState::from_json(json),
            Err(StateError::NotObject)
        ));
    }
    assert!(matches!(
        NoteState::from_json("{broken"),
        Err(StateError::Json(_))
    ));
}

#[test]
fn missing_store_creates_defaults_and_subsequent_flush_replaces_them() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("nested/state.json");
    let mut store = StateStore::new(&path);
    let loaded = store.load().unwrap();
    assert!(matches!(
        loaded.diagnostics.as_slice(),
        [LoadDiagnostic::Created]
    ));
    assert_eq!(loaded.state, NoteState::default());
    let state = NoteState {
        text: "Only in a temporary directory".into(),
        ..loaded.state
    };
    store.flush(&state).unwrap();
    let loaded = store.load().unwrap();
    assert_eq!(loaded.state, state);
    assert!(loaded.diagnostics.is_empty());
    assert_eq!(fs::read_dir(path.parent().unwrap()).unwrap().count(), 1);
}

#[test]
fn corrupt_store_preserves_the_exact_original_before_replacing_it() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("state.json");
    let original = b"{\"text\":\"recover me\",BROKEN";
    fs::write(&path, original).unwrap();
    let mut store = StateStore::new(&path);
    let loaded = store.load().unwrap();
    let [LoadDiagnostic::Corrupt { backup, .. }] = loaded.diagnostics.as_slice() else {
        panic!("expected corrupt diagnostic")
    };
    assert_eq!(fs::read(backup).unwrap(), original);
    assert_eq!(
        NoteState::from_json(&fs::read_to_string(path).unwrap()).unwrap(),
        NoteState::default()
    );
}

#[test]
fn unreadable_store_blocks_flush_until_a_backup_can_be_made() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("state.json");
    fs::create_dir(&path).unwrap();
    let mut store = StateStore::new(&path);
    let loaded = store.load().unwrap();
    assert!(matches!(
        loaded.diagnostics.as_slice(),
        [LoadDiagnostic::Unreadable { .. }]
    ));
    assert!(store.flush(&NoteState::default()).is_err());
    assert!(path.is_dir());
    fs::remove_dir(&path).unwrap();
    let original = b"{\"text\":\"available again\"}";
    fs::write(&path, original).unwrap();
    store.flush(&NoteState::default()).unwrap();
    let backups = fs::read_dir(directory.path())
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .contains(".unread-")
        })
        .collect::<Vec<_>>();
    assert!(
        backups
            .iter()
            .any(|path| fs::read(path).unwrap() == original)
    );
}

#[test]
fn profiles_reject_unsafe_names_instead_of_falling_back_to_live_state() {
    for name in ["../real", "a/b", "a\\b", "test.profile", "hello world"] {
        assert!(Profile::new(Some(name)).is_err());
    }
    assert_eq!(
        Profile::new(Some("  Žluťoučký_42-test  "))
            .unwrap()
            .suffix(),
        ".Žluťoučký_42-test"
    );
    assert_eq!(Profile::new(Some(" ")).unwrap(), Profile::default());
}

#[test]
fn windows_uses_original_state_path_but_profiles_isolate_caches_and_startup() {
    let directory = tempfile::tempdir().unwrap();
    let environment = PathEnvironment {
        appdata: Some(directory.path().join("Roaming")),
        local_appdata: Some(directory.path().join("Local")),
        ..PathEnvironment::default()
    };
    let normal = AppPaths::resolve(Profile::default(), Platform::Windows, &environment).unwrap();
    assert_eq!(
        normal.state_file,
        directory.path().join("Roaming/Task Focuser/state.json")
    );
    assert!(normal.allows_autostart());
    let isolated = AppPaths::resolve(
        Profile::new(Some("test")).unwrap(),
        Platform::Windows,
        &environment,
    )
    .unwrap();
    assert_eq!(
        isolated.state_file,
        directory
            .path()
            .join("Roaming/Task Focuser/state.test.json")
    );
    assert_ne!(isolated.cache_dir, normal.cache_dir);
    assert!(!isolated.allows_autostart());
}

#[test]
fn linux_and_macos_paths_and_explicit_data_override_are_isolated() {
    let directory = tempfile::tempdir().unwrap();
    let mut environment = PathEnvironment {
        home: Some(directory.path().to_owned()),
        xdg_config_home: Some(directory.path().join("config")),
        xdg_cache_home: Some(directory.path().join("cache")),
        ..PathEnvironment::default()
    };
    let linux = AppPaths::resolve(Profile::default(), Platform::Linux, &environment).unwrap();
    assert_eq!(
        linux.state_file,
        directory.path().join("config/task-focuser/state.json")
    );
    let mac = AppPaths::resolve(Profile::default(), Platform::Macos, &environment).unwrap();
    assert_eq!(
        mac.state_file,
        directory
            .path()
            .join("Library/Application Support/Task Focuser/state.json")
    );
    environment.xdg_config_home = Some(PathBuf::from("relative"));
    let fallback = AppPaths::resolve(Profile::default(), Platform::Linux, &environment).unwrap();
    assert_eq!(
        fallback.state_file,
        directory.path().join(".config/task-focuser/state.json")
    );
    environment.data_override = Some(directory.path().join("explicit"));
    let isolated = AppPaths::resolve(Profile::default(), Platform::Linux, &environment).unwrap();
    assert_eq!(
        isolated.state_file,
        directory.path().join("explicit/state.json")
    );
    assert_eq!(isolated.cache_dir, directory.path().join("explicit/cache"));
    assert!(!isolated.allows_autostart());
    assert_eq!(
        fs::read_dir(directory.path()).unwrap().count(),
        0,
        "path resolution must not create directories"
    );
}

#[test]
fn oversized_numeric_fields_do_not_discard_note_text() {
    let state = NoteState::from_json(r#"{"text":"Keep even this note","volume":1e400,"x":-1e400,"fontSize":99999999999999999999999999999999999999999999999999999999}"#).unwrap();
    assert_eq!(state.text, "Keep even this note");
    assert_eq!(state.volume, 0.1);
    assert_eq!(state.x, 100.0);
    assert_eq!(state.font_size, 28);
}

#[test]
fn invalid_utf8_is_kept_untouched_until_flush_can_back_it_up() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("state.json");
    let original = b"{\"text\":\"recovery \xff\"}";
    fs::write(&path, original).unwrap();
    let mut store = StateStore::new(&path);
    let loaded = store.load().unwrap();
    assert!(matches!(
        loaded.diagnostics.as_slice(),
        [LoadDiagnostic::Unreadable { .. }]
    ));
    assert_eq!(fs::read(&path).unwrap(), original);
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 1);
    store.flush(&loaded.state).unwrap();
    let backup = fs::read_dir(directory.path())
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .find(|path| {
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .contains(".unread-")
        })
        .unwrap();
    assert_eq!(fs::read(backup).unwrap(), original);
    assert_eq!(
        NoteState::from_json(&fs::read_to_string(path).unwrap()).unwrap(),
        NoteState::default()
    );
}

#[test]
fn duplicate_fields_apply_in_source_order_and_invalid_values_preserve_text() {
    for (json, expected) in [
        (r#"{"text":"keep this","text":null}"#, "keep this"),
        (r#"{"text":"keep this","TEXT":false}"#, "keep this"),
        (r#"{"TEXT":"keep this","text":[]}"#, "keep this"),
        (r#"{"TEXT":"first","text":"last"}"#, "last"),
        (r#"{"text":"first","TEXT":"last"}"#, "last"),
        (r#"{"text":null,"TEXT":"valid later"}"#, "valid later"),
    ] {
        let state = NoteState::from_json(json).unwrap();
        assert_eq!(state.text, expected, "{json}");
        let through_serde: NoteState = serde_json::from_str(json).unwrap();
        assert_eq!(through_serde.text, expected, "direct Deserialize: {json}");
    }
    let state = NoteState::from_json(
        r#"{"x":52,"X":"not a number","mode":"closed","MODE":null,"locked":true,"LOCKED":{}}"#,
    )
    .unwrap();
    assert_eq!(state.x, 52.0);
    assert_eq!(state.mode, Mode::Closed);
    assert!(state.locked);
}

#[test]
fn duplicate_bird_names_keep_prior_valid_names_in_source_order() {
    let state = NoteState::from_json(r#"{
        "birdNames":{"lennie":"Original","lennie":null,"LENNIE":"Shortlisted","lennie":"ab","meggie":"First owl","MEGGIE":"Last owl","other":"🦉a"},
        "BIRDNAMES":{"lennie":false,"meggie":"x"}
    }"#).unwrap();
    assert_eq!(state.bird_name("lennie"), "Shortlisted");
    assert_eq!(state.bird_name("meggie"), "Last owl");
    assert_eq!(state.bird_name("other"), "🦉a");
}

#[test]
fn duplicate_text_survives_loading_and_saving_the_store() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("state.json");
    fs::write(&path, r#"{"text":"keep this","text":null}"#).unwrap();
    let mut store = StateStore::new(&path);
    let loaded = store.load().unwrap();
    assert!(loaded.diagnostics.is_empty());
    assert_eq!(loaded.state.text, "keep this");
    store.flush(&loaded.state).unwrap();
    assert_eq!(store.load().unwrap().state.text, "keep this");
}

#[test]
fn serialization_keeps_large_finite_coordinates_and_bounds_settings() {
    let state = NoteState {
        x: f64::MAX,
        y: -f64::MAX,
        dot_x: Some(f64::MAX),
        dot_y: Some(-f64::MAX),
        manual_width: Some(f64::MAX),
        manual_height: Some(f64::MAX),
        volume: f64::MAX,
        opacity: u8::MAX,
        font_size: u16::MAX,
        ..NoteState::default()
    };
    let written = state.to_json().unwrap();
    let loaded = NoteState::from_json(&written).unwrap();
    assert_eq!(loaded.x, f64::MAX);
    assert_eq!(loaded.y, -f64::MAX);
    assert_eq!(loaded.dot_x, Some(f64::MAX));
    assert_eq!(loaded.dot_y, Some(-f64::MAX));
    assert_eq!(loaded.manual_width, Some(f64::MAX));
    assert_eq!(loaded.manual_height, Some(f64::MAX));
    assert_eq!(loaded.volume, 1.0);
    assert_eq!(loaded.opacity, 100);
    assert_eq!(loaded.font_size, 28);
}
