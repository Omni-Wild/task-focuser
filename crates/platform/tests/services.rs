#[cfg(unix)]
use task_focuser_platform::startup::{Startup, StartupError};
use task_focuser_platform::{
    instance::{Instance, InstanceError, InstanceOutcome},
    sound::{ChirpKind, Sound},
};

#[test]
fn another_launch_forwards_to_primary_then_releases_ownership() {
    let directory = tempfile::tempdir().expect("test directory");
    let InstanceOutcome::Primary(primary) =
        Instance::acquire(directory.path(), "ipc-test", &[]).expect("primary")
    else {
        panic!("first launch was forwarded")
    };
    let arguments = vec![
        "--set-text".into(),
        "Some Unicode 🦉 text\nwith a newline".into(),
    ];
    assert!(matches!(
        Instance::acquire(directory.path(), "ipc-test", &arguments).expect("forward"),
        InstanceOutcome::Forwarded
    ));
    assert_eq!(primary.try_recv().expect("receive"), Some(arguments));
    drop(primary);
    assert!(matches!(
        Instance::acquire(directory.path(), "ipc-test", &[]).expect("reacquire"),
        InstanceOutcome::Primary(_)
    ));
}

#[test]
fn long_data_paths_and_separate_profiles_do_not_share_commands() {
    let directory = tempfile::tempdir().expect("test directory");
    let deep = directory
        .path()
        .join("long-profile-directory-name".repeat(6));
    let InstanceOutcome::Primary(first) = Instance::acquire(&deep, "first", &[]).expect("first")
    else {
        panic!("unexpected forwarding")
    };
    let InstanceOutcome::Primary(second) = Instance::acquire(&deep, "second", &[]).expect("second")
    else {
        panic!("unexpected forwarding")
    };
    Instance::acquire(&deep, "second", &["--show".into()]).expect("forward");
    assert_eq!(first.try_recv().expect("first queue"), None);
    assert_eq!(
        second.try_recv().expect("second queue"),
        Some(vec!["--show".into()])
    );
}

#[test]
fn oversized_commands_fail_before_creating_an_instance() {
    let directory = tempfile::tempdir().expect("test directory");
    assert!(matches!(
        Instance::acquire(directory.path(), "limit", &["x".repeat(65536)]),
        Err(InstanceError::TooLarge)
    ));
}

#[test]
fn disabled_sound_never_requires_an_audio_device() {
    let sound = Sound::new(false).expect("disabled sound");
    sound
        .play(ChirpKind::Happy, 0.1, "meggie")
        .expect("disabled play");
    sound.set_volume(f32::NAN).expect("disabled volume");
    assert_eq!(sound.try_diagnostic(), None);
}

#[cfg(unix)]
#[test]
fn startup_is_opt_in_and_isolated_profiles_cannot_write_entries() {
    let directory = tempfile::tempdir().expect("test directory");
    let entry = directory.path().join("login-entry");
    let startup = Startup::with_entry(directory.path().join("Task Focuser"), entry.clone(), true);
    assert!(!startup.is_enabled().expect("status"));
    assert!(matches!(
        startup.set_enabled(true),
        Err(StartupError::Isolated)
    ));
    assert!(!startup.repair_if_moved().expect("repair"));
    assert!(!entry.exists());
}

#[cfg(unix)]
#[test]
fn startup_repairs_an_existing_missing_executable_without_enabling_a_disabled_entry() {
    let directory = tempfile::tempdir().expect("test directory");
    let entry = directory.path().join("login-entry");
    let old = Startup::with_entry(
        directory.path().join("missing-old-app"),
        entry.clone(),
        false,
    );
    old.set_enabled(true).expect("enable test entry");
    #[cfg(target_os = "linux")]
    std::fs::write(
        &entry,
        format!(
            "{}Hidden=true\n",
            std::fs::read_to_string(&entry).expect("entry")
        ),
    )
    .expect("disable test entry");
    let new = Startup::with_entry(directory.path().join("new-app"), entry.clone(), false);
    assert!(new.repair_if_moved().expect("repair"));
    assert!(new.status().expect("status").points_here);
    #[cfg(target_os = "linux")]
    assert!(!new.is_enabled().expect("still disabled"));
    new.set_enabled(false).expect("remove test entry");
    assert!(!entry.exists());
}

#[cfg(target_os = "linux")]
#[test]
fn startup_quotes_exec_paths_with_desktop_entry_and_exec_escaping() {
    let directory = tempfile::tempdir().expect("test directory");
    let executable = directory
        .path()
        .join("a path with $cash `ticks` %percent \\\"quotes\"");
    let startup = Startup::with_entry(executable, directory.path().join("entry.desktop"), false);
    startup.set_enabled(true).expect("enable isolated entry");
    assert!(startup.status().expect("status").points_here);
}

#[cfg(windows)]
#[test]
fn case_aliases_forward_to_the_same_profile_writer() {
    let directory = tempfile::tempdir().expect("test directory");
    let InstanceOutcome::Primary(primary) =
        Instance::acquire(directory.path(), "CaseAlias", &[]).expect("primary")
    else {
        panic!("unexpected forward")
    };
    assert!(matches!(
        Instance::acquire(directory.path(), "casealias", &["--show".into()]).expect("case alias"),
        InstanceOutcome::Forwarded
    ));
    assert_eq!(
        primary.try_recv().expect("forwarded"),
        Some(vec!["--show".into()])
    );
}
