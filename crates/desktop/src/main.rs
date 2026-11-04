#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

use std::{env, process::ExitCode, sync::mpsc};

use anyhow::{Context, bail};
use task_focuser::{
    host,
    surface::{Backend, Services, Startup},
};
use task_focuser_core::{
    conductor::Command,
    profile::AppPaths,
    store::{LoadDiagnostic, StateStore},
};
use task_focuser_platform::{
    instance::{Instance, InstanceOutcome},
    sound::Sound,
    startup::Startup as LoginStartup,
    tray::Tray,
};

const HELP: &str = "Task Focuser\n\nUsage: task-focuser [OPTIONS]\n\n  --show                 Show the note\n  --lock / --unlock      Lock or edit the note\n  --dot                  Tuck the note into the dot\n  --close                Hide the note and dot\n  --start                Restore a closed dot\n  --quit                 Save and quit\n  --backend auto|native|wayland\n                         Choose the desktop host (default: auto)\n  --no-tray              Skip the tray in an isolated test profile\n  --no-sound             Skip audio in an isolated test profile\n  -h, --help             Show this help\n  -V, --version          Show the version\n\nTASKFOCUSER_PROFILE selects a separate profile. TASKFOCUSER_DATA_DIR\nselects an isolated data directory. Isolated runs cannot change autostart.\n";

#[derive(Debug, Default)]
struct Options {
    backend: Option<Backend>,
    no_tray: bool,
    no_sound: bool,
    commands: Vec<String>,
}

fn main() -> ExitCode {
    tracing_subscriber::fmt()
        .with_target(false)
        .with_writer(std::io::stderr)
        .init();
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("Task Focuser could not start: {error:#}");
            show_startup_error(&format!("Task Focuser could not start.\n\n{error:#}"));
            ExitCode::FAILURE
        }
    }
}

fn run() -> anyhow::Result<()> {
    let args: Vec<String> = env::args().skip(1).collect();
    if args.iter().any(|arg| arg == "--help" || arg == "-h") {
        print!("{HELP}");
        return Ok(());
    }
    if args.iter().any(|arg| arg == "--version" || arg == "-V") {
        println!("Task Focuser {}", env!("CARGO_PKG_VERSION"));
        return Ok(());
    }
    let options = parse_options(args)?;
    #[cfg(windows)]
    task_focuser_platform::windows::initialize_dpi().context("set Windows DPI awareness")?;
    let paths = AppPaths::from_env().context("resolve profile paths")?;
    if (options.no_tray || options.no_sound) && !paths.is_isolated {
        bail!("--no-tray and --no-sound require TASKFOCUSER_PROFILE or TASKFOCUSER_DATA_DIR");
    }

    // A secondary launch never opens, normalizes, or writes the shared state file.
    let instance = match Instance::acquire(
        &paths.data_dir,
        paths.profile.name().unwrap_or_default(),
        &options.commands,
    )
    .context("acquire profile instance")?
    {
        InstanceOutcome::Forwarded => return Ok(()),
        InstanceOutcome::Primary(instance) => instance,
    };
    let commands = if options.commands.is_empty() {
        Vec::new()
    } else {
        Command::parse_args(&options.commands)
    };
    if commands == [Command::Quit] {
        return Ok(());
    }
    let backend = options.backend.unwrap_or_else(default_backend);
    #[cfg(target_os = "linux")]
    if backend == Backend::Native && env::var_os("DISPLAY").is_none_or(|display| display.is_empty())
    {
        bail!(
            "the native Linux host requires DISPLAY; use --backend wayland in a layer-shell session"
        );
    }
    let monitors = host::monitors(backend).context("read desktop monitors")?;
    if monitors.is_empty() {
        bail!("the desktop reported no monitors");
    }
    let mut store = StateStore::new(paths.state_file.clone());
    let loaded = store.load().context("load note state")?;
    let mut diagnostics = Vec::new();
    for diagnostic in loaded.diagnostics {
        match diagnostic {
            LoadDiagnostic::Created => {}
            LoadDiagnostic::Corrupt { backup, error } => diagnostics.push(format!(
                "The saved note could not be read. A copy is at {}. {error}", backup.display()
            )),
            LoadDiagnostic::Unreadable { error } => diagnostics.push(format!(
                "The saved note is unavailable. Saving will wait until a backup can be made. {error}"
            )),
        }
    }
    let mut state = loaded.state;
    let login_startup = LoginStartup::new(
        env::current_exe().context("locate Task Focuser executable")?,
        !paths.allows_autostart(),
    )
    .context("prepare login startup")?;
    if paths.allows_autostart() {
        login_startup
            .repair_if_moved()
            .context("repair moved login startup executable")?;
        state.start_with_windows = login_startup
            .is_enabled()
            .context("read login startup status")?;
    }
    let (tray_sender, tray_actions) = mpsc::sync_channel(32);
    let tray = if options.no_tray {
        None
    } else {
        Some(Tray::new(tray_sender).context("create notification-area icon")?)
    };
    let sound = match Sound::new(!options.no_sound) {
        Ok(sound) => sound,
        Err(error) => {
            diagnostics.push(format!("Bird sounds are unavailable. {error}"));
            Sound::new(false).context("prepare disabled sound output")?
        }
    };
    for diagnostic in &diagnostics {
        tracing::warn!(message = %diagnostic, "startup diagnostic");
    }
    host::run(Startup {
        paths,
        state,
        store,
        commands,
        backend,
        monitors,
        services: Services {
            instance,
            login_startup,
            tray,
            tray_actions,
            sound,
            diagnostics,
        },
    })
}

fn parse_options(args: Vec<String>) -> anyhow::Result<Options> {
    let mut options = Options::default();
    let mut args = args.into_iter();
    while let Some(argument) = args.next() {
        match argument.as_str() {
            "--backend" => {
                options.backend = parse_backend(
                    &args
                        .next()
                        .context("--backend needs auto, native, or wayland")?,
                )?
            }
            "--no-tray" => options.no_tray = true,
            "--no-sound" => options.no_sound = true,
            _ if argument.starts_with("--backend=") => {
                options.backend = parse_backend(&argument[10..])?
            }
            _ => options.commands.push(argument),
        }
    }
    Ok(options)
}

fn parse_backend(value: &str) -> anyhow::Result<Option<Backend>> {
    match value {
        "auto" => Ok(None),
        "native" => Ok(Some(Backend::Native)),
        #[cfg(target_os = "linux")]
        "wayland" => Ok(Some(Backend::Wayland)),
        _ => bail!("unsupported desktop host {value:?}; use auto, native, or wayland on Linux"),
    }
}

fn default_backend() -> Backend {
    #[cfg(target_os = "linux")]
    if ["WAYLAND_DISPLAY", "WAYLAND_SOCKET"]
        .iter()
        .any(|name| env::var_os(name).is_some_and(|value| !value.is_empty()))
    {
        return Backend::Wayland;
    }
    Backend::Native
}

#[cfg(windows)]
fn show_startup_error(message: &str) {
    use windows_sys::Win32::UI::WindowsAndMessaging::{MB_ICONERROR, MB_OK, MessageBoxW};
    let message: Vec<u16> = message.encode_utf16().chain(Some(0)).collect();
    let title: Vec<u16> = "Task Focuser".encode_utf16().chain(Some(0)).collect();
    // SAFETY: both UTF-16 strings remain live and terminated for this synchronous call.
    unsafe {
        MessageBoxW(
            std::ptr::null_mut(),
            message.as_ptr(),
            title.as_ptr(),
            MB_OK | MB_ICONERROR,
        );
    }
}

#[cfg(not(windows))]
fn show_startup_error(_message: &str) {}
