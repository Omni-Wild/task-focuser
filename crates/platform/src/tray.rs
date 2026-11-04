//! Notification-area integration. Linux uses D-Bus without a GTK event loop.

use std::sync::mpsc::{SyncSender, TrySendError};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayAction {
    ShowNote,
    Tuck,
    StartDot,
    ToggleLock,
    ToggleStartup,
    Quit,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrayMode {
    Note,
    Dot,
    Closed,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TrayState {
    pub mode: TrayMode,
    pub locked: bool,
    pub start_at_login: bool,
}
impl Default for TrayState {
    fn default() -> Self {
        Self {
            mode: TrayMode::Note,
            locked: false,
            start_at_login: false,
        }
    }
}

#[derive(Debug, thiserror::Error)]
#[error("could not create or update the tray icon: {0}")]
pub struct TrayError(String);

fn send(sender: &SyncSender<TrayAction>, action: TrayAction) {
    match sender.try_send(action) {
        Ok(()) => {}
        Err(TrySendError::Full(_)) => tracing::warn!(?action, "tray command queue is full"),
        Err(TrySendError::Disconnected(_)) => tracing::debug!("tray command receiver has closed"),
    }
}

fn icon_rgba() -> Result<(Vec<u8>, u32, u32), TrayError> {
    let directory = ico::IconDir::read(std::io::Cursor::new(include_bytes!(
        "../../../assets/task-focuser.ico"
    )))
    .map_err(|error| TrayError(error.to_string()))?;
    let image = directory
        .entries()
        .iter()
        .min_by_key(|entry| entry.width().abs_diff(32))
        .ok_or_else(|| TrayError("the embedded icon has no images".into()))?
        .decode()
        .map_err(|error| TrayError(error.to_string()))?;
    Ok((image.rgba_data().to_vec(), image.width(), image.height()))
}

#[cfg(target_os = "linux")]
mod native {
    use super::*;
    use ksni::{
        TrayMethods,
        menu::{CheckmarkItem, MenuItem, StandardItem},
    };
    pub struct Tray {
        updates: tokio::sync::watch::Sender<TrayState>,
        stop: Option<tokio::sync::oneshot::Sender<()>>,
        worker: Option<std::thread::JoinHandle<()>>,
    }
    struct Service {
        sender: SyncSender<TrayAction>,
        state: TrayState,
        icon: ksni::Icon,
    }
    impl Service {
        fn item(label: &str, enabled: bool, action: TrayAction) -> MenuItem<Self> {
            StandardItem {
                label: label.into(),
                enabled,
                activate: Box::new(move |tray: &mut Self| send(&tray.sender, action)),
                ..Default::default()
            }
            .into()
        }
    }
    impl ksni::Tray for Service {
        fn id(&self) -> String {
            "task-focuser".into()
        }
        fn title(&self) -> String {
            "Task Focuser".into()
        }
        fn activate(&mut self, _: i32, _: i32) {
            send(&self.sender, TrayAction::ShowNote);
        }
        fn icon_pixmap(&self) -> Vec<ksni::Icon> {
            vec![self.icon.clone()]
        }
        fn menu(&self) -> Vec<MenuItem<Self>> {
            vec![
                Self::item("Show note", true, TrayAction::ShowNote),
                Self::item(
                    "Tuck into the dot",
                    self.state.mode == TrayMode::Note,
                    TrayAction::Tuck,
                ),
                Self::item(
                    "Start the dot",
                    self.state.mode == TrayMode::Closed,
                    TrayAction::StartDot,
                ),
                MenuItem::Separator,
                Self::item(
                    if self.state.locked { "Unlock" } else { "Lock" },
                    true,
                    TrayAction::ToggleLock,
                ),
                CheckmarkItem {
                    label: "Start at login".into(),
                    checked: self.state.start_at_login,
                    activate: Box::new(|tray: &mut Self| {
                        send(&tray.sender, TrayAction::ToggleStartup)
                    }),
                    ..Default::default()
                }
                .into(),
                MenuItem::Separator,
                Self::item("Quit", true, TrayAction::Quit),
            ]
        }
    }
    impl Tray {
        pub fn new(sender: SyncSender<TrayAction>) -> Result<Self, TrayError> {
            let (rgba, width, height) = icon_rgba()?;
            let mut argb = Vec::with_capacity(rgba.len());
            for pixel in rgba.chunks_exact(4) {
                argb.extend_from_slice(&[pixel[3], pixel[0], pixel[1], pixel[2]]);
            }
            let service = Service {
                sender,
                state: TrayState::default(),
                icon: ksni::Icon {
                    width: width as i32,
                    height: height as i32,
                    data: argb,
                },
            };
            let (updates, mut states) = tokio::sync::watch::channel(TrayState::default());
            let (stop, mut stopped) = tokio::sync::oneshot::channel();
            let (ready, readiness) = std::sync::mpsc::sync_channel(1);
            let worker=std::thread::Builder::new().name("task-focuser-tray".into()).spawn(move||{
                let runtime=match tokio::runtime::Builder::new_current_thread().enable_all().build(){
                    Ok(runtime)=>runtime,Err(error)=>{let _=ready.send(Err(error.to_string()));return;}
                };
                runtime.block_on(async{
                    let handle=match service.spawn().await{
                        Ok(handle)=>{let _=ready.send(Ok(()));handle},
                        Err(error)=>{let _=ready.send(Err(error.to_string()));return;}
                    };
                    loop{
                        tokio::select!{
                            _=&mut stopped=>break,
                            changed=states.changed()=>{
                                if changed.is_err(){break;}
                                let state=*states.borrow_and_update();
                                if handle.update(|tray|tray.state=state).await.is_none(){tracing::error!("tray service stopped while updating its menu");break;}
                            }
                        }
                    }
                    if tokio::time::timeout(std::time::Duration::from_secs(1),handle.shutdown()).await.is_err(){tracing::warn!("tray service shutdown timed out");}
                });
            }).map_err(|error|TrayError(error.to_string()))?;
            match readiness.recv() {
                Ok(Ok(())) => Ok(Self {
                    updates,
                    stop: Some(stop),
                    worker: Some(worker),
                }),
                Ok(Err(error)) => {
                    let _ = worker.join();
                    Err(TrayError(error))
                }
                Err(error) => {
                    let _ = worker.join();
                    Err(TrayError(error.to_string()))
                }
            }
        }
        pub fn update(&self, state: TrayState) -> Result<(), TrayError> {
            // The watch channel retains only the latest menu state. Native work stays
            // on the service runtime, never nested inside Iced's entered runtime.
            self.updates
                .send(state)
                .map_err(|_| TrayError("the tray service has stopped".into()))
        }
        pub fn poll(&self) {}
    }
    impl Drop for Tray {
        fn drop(&mut self) {
            if let Some(stop) = self.stop.take() {
                let _ = stop.send(());
            }
            if let Some(worker) = self.worker.take()
                && worker.join().is_err()
            {
                tracing::error!("tray service worker panicked during shutdown");
            }
        }
    }
}

#[cfg(any(windows, target_os = "macos"))]
mod native {
    use super::*;
    use tray_icon::{
        MouseButton, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent,
        menu::{CheckMenuItem, Menu, MenuEvent, MenuItem, PredefinedMenuItem},
    };
    pub struct Tray {
        icon: TrayIcon,
        tuck: MenuItem,
        start: MenuItem,
        lock: MenuItem,
        startup: CheckMenuItem,
        stop: crossbeam_channel::Sender<()>,
        worker: Option<std::thread::JoinHandle<()>>,
    }
    impl Tray {
        /// Create on the UI thread, after starting its native event loop.
        pub fn new(sender: SyncSender<TrayAction>) -> Result<Self, TrayError> {
            let menu = Menu::new();
            let show = MenuItem::new("Show note", true, None);
            let tuck = MenuItem::new("Tuck into the dot", true, None);
            let start = MenuItem::new("Start the dot", false, None);
            let lock = MenuItem::new("Lock", true, None);
            let startup = CheckMenuItem::new(
                if cfg!(windows) {
                    "Start with Windows"
                } else {
                    "Start at login"
                },
                true,
                false,
                None,
            );
            let quit = MenuItem::new("Quit", true, None);
            menu.append_items(&[
                &show,
                &tuck,
                &start,
                &PredefinedMenuItem::separator(),
                &lock,
                &startup,
                &PredefinedMenuItem::separator(),
                &quit,
            ])
            .map_err(|error| TrayError(error.to_string()))?;
            let (rgba, width, height) = icon_rgba()?;
            let icon = tray_icon::Icon::from_rgba(rgba, width, height)
                .map_err(|error| TrayError(error.to_string()))?;
            let icon = TrayIconBuilder::new()
                .with_tooltip("Task Focuser")
                .with_menu(Box::new(menu))
                .with_icon(icon)
                .with_menu_on_left_click(false)
                .build()
                .map_err(|error| TrayError(error.to_string()))?;
            let actions = [
                (show.id().clone(), TrayAction::ShowNote),
                (tuck.id().clone(), TrayAction::Tuck),
                (start.id().clone(), TrayAction::StartDot),
                (lock.id().clone(), TrayAction::ToggleLock),
                (startup.id().clone(), TrayAction::ToggleStartup),
                (quit.id().clone(), TrayAction::Quit),
            ];
            let icon_id = icon.id().clone();
            let (stop, stopped) = crossbeam_channel::bounded(1);
            let worker=std::thread::Builder::new().name("task-focuser-tray-events".into()).spawn(move||loop {
                crossbeam_channel::select! {
                    recv(stopped)->_=>return,
                    recv(MenuEvent::receiver())->event=>match event {
                        Ok(event)=>if let Some((_,action))=actions.iter().find(|(id,_)|*id==event.id){send(&sender,*action);},
                        Err(_)=>return,
                    },
                    recv(TrayIconEvent::receiver())->event=>match event {
                        Ok(TrayIconEvent::Click{id,button:MouseButton::Left,button_state:MouseButtonState::Up,..}) if id==icon_id=>send(&sender,TrayAction::ShowNote),
                        Ok(_)=>{},Err(_)=>return,
                    }
                }
            }).map_err(|error|TrayError(error.to_string()))?;
            Ok(Self {
                icon,
                tuck,
                start,
                lock,
                startup,
                stop,
                worker: Some(worker),
            })
        }
        pub fn update(&self, state: TrayState) -> Result<(), TrayError> {
            self.tuck.set_enabled(state.mode == TrayMode::Note);
            self.start.set_enabled(state.mode == TrayMode::Closed);
            self.lock
                .set_text(if state.locked { "Unlock" } else { "Lock" });
            self.startup.set_checked(state.start_at_login);
            Ok(())
        }
        /// Native callbacks now wake a blocking receiver thread, so the UI does not need a timer.
        pub fn poll(&self) {}
    }
    impl Drop for Tray {
        fn drop(&mut self) {
            if let Err(error) = self.icon.set_visible(false) {
                tracing::warn!(%error,"could not hide tray icon during shutdown");
            }
            let _ = self.stop.try_send(());
            if let Some(worker) = self.worker.take()
                && worker.join().is_err()
            {
                tracing::error!("tray event worker panicked during shutdown");
            }
        }
    }
}
pub use native::Tray;
