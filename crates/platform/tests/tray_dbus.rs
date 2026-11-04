#![cfg(target_os = "linux")]

use futures_util::StreamExt;
use std::{
    collections::HashMap,
    io::{BufRead, BufReader},
    process::{Child, Command, Stdio},
    sync::mpsc,
    thread,
    time::{Duration, Instant},
};
use task_focuser_platform::tray::{Tray, TrayAction, TrayMode, TrayState};
use zbus::zvariant::{OwnedObjectPath, OwnedValue};

struct Daemon(Child);
impl Drop for Daemon {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

/// Runs the actual Linux tray against its own bus, without contacting a desktop session.
#[test]
fn native_tray_updates_and_actions_use_an_isolated_bus() {
    let daemon = Command::new("dbus-daemon")
        .args(["--session", "--nofork", "--print-address=1"])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("install dbus-daemon to run the native tray protocol test");
    let mut daemon = Daemon(daemon);
    let mut address = String::new();
    BufReader::new(daemon.0.stdout.take().expect("daemon address pipe"))
        .read_line(&mut address)
        .expect("private session bus address");
    assert!(
        address.starts_with("unix:"),
        "private bus did not report a Unix address"
    );
    let mut child = Command::new(std::env::current_exe().expect("test executable"))
        .args([
            "--ignored",
            "--exact",
            "private_bus_scenario",
            "--nocapture",
        ])
        .env("DBUS_SESSION_BUS_ADDRESS", address.trim())
        .env("TASK_FOCUSER_PRIVATE_TRAY_TEST", "1")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("isolated tray test process");
    let started = Instant::now();
    loop {
        if child.try_wait().expect("test process status").is_some() {
            break;
        }
        if started.elapsed() > Duration::from_secs(20) {
            let _ = child.kill();
            let output = child.wait_with_output().expect("timed out test output");
            panic!(
                "isolated tray test timed out: {}{}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(20));
    }
    let output = child.wait_with_output().expect("isolated test output");
    assert!(
        output.status.success(),
        "isolated tray test failed: {}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

struct Watcher {
    registered: mpsc::Sender<String>,
    items: Vec<String>,
}
#[zbus::interface(name = "org.kde.StatusNotifierWatcher")]
impl Watcher {
    fn register_status_notifier_item(&mut self, service: &str) {
        self.items.push(service.into());
        self.registered
            .send(service.into())
            .expect("registration receiver");
    }
    fn register_status_notifier_host(&self, _service: &str) {}
    #[zbus(property)]
    fn registered_status_notifier_items(&self) -> Vec<String> {
        self.items.clone()
    }
    #[zbus(property)]
    fn is_status_notifier_host_registered(&self) -> bool {
        true
    }
    #[zbus(property)]
    fn protocol_version(&self) -> i32 {
        0
    }
}

type Menu = Vec<(i32, HashMap<String, OwnedValue>)>;
fn item<'a>(menu: &'a Menu, label: &str) -> (i32, &'a HashMap<String, OwnedValue>) {
    let (id, properties) = menu
        .iter()
        .find(|(_, properties)| {
            properties
                .get("label")
                .and_then(|value| <&str>::try_from(value).ok())
                == Some(label)
        })
        .unwrap_or_else(|| panic!("missing menu item {label}"));
    (*id, properties)
}
fn enabled(menu: &Menu, label: &str) -> bool {
    // dbusmenu omits properties equal to their protocol defaults; enabled defaults true.
    item(menu, label)
        .1
        .get("enabled")
        .map(|value| bool::try_from(value).expect("enabled is boolean"))
        .unwrap_or(true)
}
fn checked(menu: &Menu, label: &str) -> i32 {
    i32::try_from(
        item(menu, label)
            .1
            .get("toggle-state")
            .expect("toggle property"),
    )
    .expect("toggle state is integer")
}
async fn menu_properties(menu: &zbus::Proxy<'_>) -> Menu {
    menu.call(
        "GetGroupProperties",
        &(Vec::<i32>::new(), vec!["label", "enabled", "toggle-state"]),
    )
    .await
    .expect("menu properties")
}

#[test]
#[ignore = "subprocess entry point, launched by native_tray_updates_and_actions_use_an_isolated_bus"]
fn private_bus_scenario() {
    assert_eq!(
        std::env::var("TASK_FOCUSER_PRIVATE_TRAY_TEST").as_deref(),
        Ok("1")
    );
    let (registered, registrations) = mpsc::channel();
    let (ready, readiness) = mpsc::sync_channel(1);
    let (stop, stopped) = tokio::sync::oneshot::channel();
    let watcher = thread::spawn(move || {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("watcher runtime");
        runtime.block_on(async {
            let connection = zbus::connection::Builder::session()
                .expect("private watcher bus")
                .name("org.kde.StatusNotifierWatcher")
                .expect("watcher name")
                .serve_at(
                    "/StatusNotifierWatcher",
                    Watcher {
                        registered,
                        items: Vec::new(),
                    },
                )
                .expect("watcher interface")
                .build()
                .await
                .expect("watcher registration");
            ready.send(()).expect("watcher readiness");
            let _ = stopped.await;
            drop(connection);
        });
    });
    readiness
        .recv_timeout(Duration::from_secs(3))
        .expect("watcher startup");
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .expect("app runtime");
    runtime.block_on(async {
        // Both calls run inside an entered Tokio runtime, matching Iced's update context.
        // ksni's blocking facade would panic here by trying to enter another runtime.
        let (actions, received) = mpsc::sync_channel(16);
        let tray = Tray::new(actions).expect("native tray service");
        let service = registrations
            .recv_timeout(Duration::from_secs(2))
            .expect("watcher saw tray registration");
        let connection = zbus::Connection::session()
            .await
            .expect("private bus client");
        let sni = zbus::Proxy::new(
            &connection,
            service.clone(),
            "/StatusNotifierItem",
            "org.kde.StatusNotifierItem",
        )
        .await
        .expect("notifier proxy");
        assert_eq!(
            sni.get_property::<String>("Title")
                .await
                .expect("tray title"),
            "Task Focuser"
        );
        let path = sni
            .get_property::<OwnedObjectPath>("Menu")
            .await
            .expect("menu object path");
        let menu = zbus::Proxy::new(&connection, service.clone(), path, "com.canonical.dbusmenu")
            .await
            .expect("menu proxy");
        let initial = menu_properties(&menu).await;
        assert!(enabled(&initial, "Tuck into the dot"));
        assert!(!enabled(&initial, "Start the dot"));
        assert_eq!(checked(&initial, "Start at login"), 0);
        let mut signals = menu
            .receive_signal("ItemsPropertiesUpdated")
            .await
            .expect("menu signal subscription");
        tray.update(TrayState {
            mode: TrayMode::Closed,
            locked: true,
            start_at_login: true,
        })
        .expect("menu update inside app runtime");
        let signal = tokio::time::timeout(Duration::from_secs(2), signals.next())
            .await
            .expect("menu update signal timeout")
            .expect("menu update signal");
        let (changed, _removed): (Menu, Vec<(i32, Vec<String>)>) = signal
            .body()
            .deserialize()
            .expect("changed menu properties");
        assert!(!changed.is_empty());
        let updated = menu_properties(&menu).await;
        assert!(!enabled(&updated, "Tuck into the dot"));
        assert!(enabled(&updated, "Start the dot"));
        item(&updated, "Unlock");
        assert_eq!(checked(&updated, "Start at login"), 1);
        sni.call::<_, _, ()>("Activate", &(0i32, 0i32))
            .await
            .expect("tray left click");
        assert_eq!(
            received
                .recv_timeout(Duration::from_secs(1))
                .expect("show action"),
            TrayAction::ShowNote
        );
        for (label, expected) in [
            ("Start the dot", TrayAction::StartDot),
            ("Unlock", TrayAction::ToggleLock),
            ("Start at login", TrayAction::ToggleStartup),
            ("Quit", TrayAction::Quit),
        ] {
            menu.call::<_, _, ()>(
                "Event",
                &(
                    item(&updated, label).0,
                    "clicked",
                    OwnedValue::from(0i32),
                    0u32,
                ),
            )
            .await
            .expect("menu click");
            assert_eq!(
                received
                    .recv_timeout(Duration::from_secs(1))
                    .expect("forwarded menu action"),
                expected
            );
        }
        assert_eq!(
            checked(&menu_properties(&menu).await, "Start at login"),
            1,
            "the check mark follows app state rather than toggling on click"
        );
        drop(tray);
    });
    stop.send(()).expect("watcher shutdown");
    watcher.join().expect("watcher thread");
}
