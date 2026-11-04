//! Per-user local command forwarding with bounded I/O and explicit ownership.

use fs2::FileExt;
#[cfg(unix)]
use interprocess::local_socket::{GenericFilePath, ToFsName};
use interprocess::local_socket::{
    ListenerOptions,
    tokio::{Listener, Stream, prelude::*},
};
use sha2::{Digest, Sha256};
use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read},
    path::{Path, PathBuf},
    sync::mpsc::{self, Receiver, SyncSender, TryRecvError},
    thread::{self, JoinHandle},
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    sync::oneshot,
    time::timeout,
};

const MAX_MESSAGE: usize = 64 * 1024;
const IO_TIMEOUT: Duration = Duration::from_secs(2);

#[derive(Debug, thiserror::Error)]
pub enum InstanceError {
    #[error("could not establish the app instance: {0}")]
    Io(#[from] io::Error),
    #[error("the forwarded command is larger than 64 KiB")]
    TooLarge,
    #[error("the running app did not accept the command")]
    Rejected,
    #[error("the app instance listener has stopped")]
    ListenerStopped,
    #[error(
        "Task Focuser is already using this profile in another desktop session; choose a different profile for an independent note"
    )]
    OtherSession,
    #[error("the Windows version is already running; quit it before starting this version")]
    LegacyRunning,
}

pub enum InstanceOutcome {
    Primary(Instance),
    Forwarded,
}

pub struct Instance {
    receiver: Receiver<Vec<String>>,
    stop: Option<oneshot::Sender<()>>,
    worker: Option<JoinHandle<()>>,
    _lock: File,
    #[cfg(windows)]
    _legacy: Option<crate::windows::LegacyGuard>,
}

impl Instance {
    /// Acquire before loading state. An isolated profile must pass its own data directory.
    pub fn acquire(
        data_dir: &Path,
        profile: &str,
        args: &[String],
    ) -> Result<InstanceOutcome, InstanceError> {
        let validated_profile = task_focuser_core::profile::Profile::new(Some(profile))
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidInput, error))?;
        let profile = validated_profile.name().unwrap_or("");
        let payload = serde_json::to_vec(args).map_err(io::Error::other)?;
        if payload.len() > MAX_MESSAGE {
            return Err(InstanceError::TooLarge);
        }
        fs::create_dir_all(data_dir)?;
        let data_dir = data_dir.canonicalize()?;
        let session = session_id()?;
        let mut digest = Sha256::new();
        digest.update(data_dir.as_os_str().as_encoded_bytes());
        digest.update([0]);
        digest.update(user_identity()?.as_bytes());
        digest.update([0]);
        digest.update(profile.as_bytes());
        digest.update([0]);
        digest.update(session.as_bytes());
        let key: String = digest
            .finalize()
            .iter()
            .map(|byte| format!("{byte:02x}"))
            .collect();
        let directory = data_dir.join(".ipc");
        private_directory(&directory)?;
        let owner_file = directory.join(format!("state{}.owner", validated_profile.suffix()));
        let lock = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(directory.join(format!("state{}.lock", validated_profile.suffix())))?;
        #[cfg(unix)]
        let socket_directory = {
            let directory = PathBuf::from("/tmp").join(format!("task-focuser-{}", current_uid()));
            private_directory_for_owner(&directory, current_uid())?;
            directory
        };
        #[cfg(windows)]
        let socket_directory = directory;
        let endpoint = endpoint(&socket_directory, &key)?;
        match lock.try_lock_exclusive() {
            Ok(()) => {}
            Err(error) if error.raw_os_error() == fs2::lock_contended_error().raw_os_error() => {
                let started = std::time::Instant::now();
                let (owner_session, owner_endpoint) = loop {
                    let mut record = String::new();
                    match File::open(&owner_file) {
                        Ok(file) => {
                            file.take(16 * 1024).read_to_string(&mut record)?;
                        }
                        Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                        Err(error) => return Err(error.into()),
                    }
                    if let Ok(owner) = serde_json::from_str::<(String, PathBuf)>(&record) {
                        break owner;
                    }
                    if started.elapsed() >= IO_TIMEOUT {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "the running app has no valid instance record",
                        )
                        .into());
                    }
                    thread::sleep(Duration::from_millis(10));
                };
                if owner_session != session {
                    return Err(InstanceError::OtherSession);
                }
                runtime()?.block_on(forward(owner_endpoint, &payload))?;
                return Ok(InstanceOutcome::Forwarded);
            }
            Err(error) => return Err(error.into()),
        }
        // Windows byte-range locks also deny reads through other handles, so discovery
        // lives in a separate private file while the ownership file stays locked.
        let record = serde_json::to_vec(&(&session, &endpoint)).map_err(io::Error::other)?;
        fs::write(&owner_file, record)?;
        #[cfg(windows)]
        let legacy = if uses_legacy_data(&data_dir, std::env::var_os("APPDATA").as_deref())? {
            Some(
                crate::windows::LegacyGuard::acquire(profile).map_err(|error| {
                    if error.kind() == io::ErrorKind::AlreadyExists {
                        InstanceError::LegacyRunning
                    } else {
                        error.into()
                    }
                })?,
            )
        } else {
            None
        };
        // Holding the file lock excludes an active server before removing a crashed server's socket.
        #[cfg(unix)]
        match fs::remove_file(&endpoint) {
            Ok(()) => {}
            Err(e) if e.kind() == io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
        }
        let (sender, receiver) = mpsc::sync_channel(32);
        let (stop, stopped) = oneshot::channel();
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("task-focuser-ipc".into())
            .spawn(move || {
                let run = runtime().and_then(|rt| {
                    rt.block_on(async {
                        let listener = create_listener(&endpoint)?;
                        ready_tx
                            .send(Ok(()))
                            .map_err(|_| io::Error::other("instance startup cancelled"))?;
                        serve(listener, sender, stopped).await;
                        Ok(())
                    })
                });
                if let Err(error) = run {
                    let _ = ready_tx.send(Err(error));
                }
            })?;
        ready_rx
            .recv()
            .map_err(|_| InstanceError::ListenerStopped)??;
        Ok(InstanceOutcome::Primary(Self {
            receiver,
            stop: Some(stop),
            worker: Some(worker),
            _lock: lock,
            #[cfg(windows)]
            _legacy: legacy,
        }))
    }

    /// Move command reception into an event subscription while retaining the ownership guard.
    pub fn take_receiver(&mut self) -> Receiver<Vec<String>> {
        let (_, disconnected) = mpsc::channel();
        std::mem::replace(&mut self.receiver, disconnected)
    }

    pub fn try_recv(&self) -> Result<Option<Vec<String>>, InstanceError> {
        match self.receiver.try_recv() {
            Ok(args) => Ok(Some(args)),
            Err(TryRecvError::Empty) => Ok(None),
            Err(TryRecvError::Disconnected) => Err(InstanceError::ListenerStopped),
        }
    }
}

impl Drop for Instance {
    fn drop(&mut self) {
        if let Some(stop) = self.stop.take() {
            let _ = stop.send(());
        }
        if let Some(worker) = self.worker.take()
            && worker.join().is_err()
        {
            tracing::error!("instance listener panicked during shutdown");
        }
    }
}

#[cfg(any(windows, test))]
fn uses_legacy_data(data_dir: &Path, appdata: Option<&std::ffi::OsStr>) -> io::Result<bool> {
    let Some(appdata) = appdata else {
        return Ok(false);
    };
    let original = Path::new(appdata).join("Task Focuser");
    let original = match original.canonicalize() {
        Ok(path) => path,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error),
    };
    #[cfg(windows)]
    {
        crate::windows::same_directory(data_dir, &original)
    }
    #[cfg(not(windows))]
    {
        Ok(data_dir.canonicalize()? == original)
    }
}

fn runtime() -> io::Result<tokio::runtime::Runtime> {
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
}

fn private_directory(path: &Path) -> io::Result<()> {
    #[cfg(unix)]
    {
        let owner = current_uid();
        private_directory_for_owner(path, owner)?;
    }
    #[cfg(windows)]
    fs::create_dir_all(path)?;
    Ok(())
}
#[cfg(unix)]
fn private_directory_for_owner(path: &Path, owner: u32) -> io::Result<()> {
    use std::os::unix::fs::{DirBuilderExt, MetadataExt};
    match fs::DirBuilder::new().mode(0o700).create(path) {
        Ok(()) => {}
        Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
        Err(e) => return Err(e),
    }
    let meta = fs::symlink_metadata(path)?;
    if !meta.is_dir() || meta.uid() != owner || meta.mode() & 0o077 != 0 {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "instance directory must belong to the current user and have mode 0700",
        ));
    }
    Ok(())
}

fn endpoint(directory: &Path, key: &str) -> io::Result<PathBuf> {
    #[cfg(unix)]
    {
        let path = directory.join(format!("{}.sock", &key[..24]));
        if path.as_os_str().as_encoded_bytes().len() > 103 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "the data directory path is too long for a local instance socket",
            ));
        }
        Ok(path)
    }
    #[cfg(windows)]
    {
        let _ = directory;
        Ok(PathBuf::from(format!("task-focuser-rust-v1-{key}")))
    }
}

fn create_listener(endpoint: &Path) -> io::Result<Listener> {
    #[cfg(unix)]
    {
        use interprocess::os::unix::local_socket::ListenerOptionsExt;
        ListenerOptions::new()
            .name(endpoint.to_fs_name::<GenericFilePath>()?)
            .mode(0o600)
            .create_tokio()
    }
    #[cfg(windows)]
    {
        use interprocess::{
            local_socket::{GenericNamespaced, ToNsName},
            os::windows::{
                local_socket::ListenerOptionsExt, security_descriptor::SecurityDescriptor,
            },
        };
        let sddl = widestring::U16CString::from_str(format!(
            "D:P(A;;GA;;;{})",
            crate::windows::current_user_sid()?
        ))
        .map_err(io::Error::other)?;
        let sd = SecurityDescriptor::deserialize(&sddl)?;
        ListenerOptions::new()
            .name(endpoint.as_os_str().to_ns_name::<GenericNamespaced>()?)
            .security_descriptor(sd)
            .create_tokio()
    }
}

async fn connect(endpoint: &Path) -> io::Result<Stream> {
    #[cfg(unix)]
    {
        Stream::connect(endpoint.to_fs_name::<GenericFilePath>()?).await
    }
    #[cfg(windows)]
    {
        use interprocess::local_socket::{GenericNamespaced, ToNsName};
        Stream::connect(endpoint.as_os_str().to_ns_name::<GenericNamespaced>()?).await
    }
}

async fn forward(endpoint: PathBuf, payload: &[u8]) -> Result<(), InstanceError> {
    timeout(IO_TIMEOUT, async {
        // The first process can hold the lock just before its listener is ready.
        let mut stream = loop {
            match connect(&endpoint).await {
                Ok(stream) => break stream,
                Err(error)
                    if matches!(
                        error.kind(),
                        io::ErrorKind::NotFound | io::ErrorKind::ConnectionRefused
                    ) =>
                {
                    tokio::time::sleep(Duration::from_millis(20)).await
                }
                Err(error) => return Err(InstanceError::Io(error)),
            }
        };
        stream
            .write_u32_le(u32::try_from(payload.len()).map_err(|_| InstanceError::TooLarge)?)
            .await?;
        stream.write_all(payload).await?;
        stream.flush().await?;
        if stream.read_u8().await? != 1 {
            return Err(InstanceError::Rejected);
        }
        Ok(())
    })
    .await
    .map_err(|_| {
        io::Error::new(
            io::ErrorKind::TimedOut,
            "the running app did not respond within two seconds",
        )
    })?
}

async fn serve(
    listener: Listener,
    sender: SyncSender<Vec<String>>,
    mut stop: oneshot::Receiver<()>,
) {
    loop {
        tokio::select! {
            _ = &mut stop => return,
            connection = listener.accept() => match connection {
                Ok(mut stream) => {
                    let receive = timeout(IO_TIMEOUT, async {
                        let length = usize::try_from(stream.read_u32_le().await?).map_err(io::Error::other)?;
                        if length > MAX_MESSAGE { return Err(io::Error::new(io::ErrorKind::InvalidData, "instance message exceeds 64 KiB")); }
                        let mut buffer = vec![0; length];
                        stream.read_exact(&mut buffer).await?;
                        let args = serde_json::from_slice::<Vec<String>>(&buffer).map_err(io::Error::other)?;
                        let accepted = sender.try_send(args).is_ok();
                        stream.write_u8(u8::from(accepted)).await?;
                        stream.flush().await
                    });
                    tokio::select! { _ = &mut stop => return, result = receive => match result {
                        Ok(Ok(())) => {}, Ok(Err(error)) => tracing::warn!(%error, "rejected instance command"),
                        Err(_) => tracing::warn!("instance command timed out"),
                    }}
                },
                Err(error) => { tracing::error!(%error, "instance listener failed"); return; },
            }
        }
    }
}

#[cfg(unix)]
fn current_uid() -> u32 {
    // SAFETY: geteuid has no preconditions and returns the calling process's effective user identity.
    unsafe { libc::geteuid() }
}
fn user_identity() -> io::Result<String> {
    #[cfg(unix)]
    {
        Ok(current_uid().to_string())
    }
    #[cfg(windows)]
    {
        crate::windows::current_user_sid()
    }
}

fn session_id() -> io::Result<String> {
    #[cfg(windows)]
    {
        crate::windows::session_id().map(|id| id.to_string())
    }
    #[cfg(unix)]
    {
        Ok(std::env::var("XDG_SESSION_ID")
            .or_else(|_| std::env::var("WAYLAND_DISPLAY"))
            .or_else(|_| std::env::var("DISPLAY"))
            .unwrap_or_else(|_| "desktop".into()))
    }
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;
    #[test]
    fn a_stalled_client_cannot_delay_shutdown() {
        let directory = tempfile::tempdir().expect("test directory");
        runtime().expect("runtime").block_on(async {
            let path = directory.path().join("stall.sock");
            let listener = create_listener(&path).expect("listener");
            let (sender, _receiver) = mpsc::sync_channel(1);
            let (stop, stopped) = oneshot::channel();
            let worker = tokio::spawn(serve(listener, sender, stopped));
            let mut client = connect(&path).await.expect("client");
            client.write_u32_le(10).await.expect("incomplete header");
            tokio::task::yield_now().await;
            stop.send(()).expect("shutdown");
            timeout(Duration::from_millis(200), worker)
                .await
                .expect("shutdown must not await client timeout")
                .expect("listener worker");
        });
    }
    #[test]
    fn oversized_and_invalid_messages_do_not_stop_the_listener() {
        let directory = tempfile::tempdir().expect("test directory");
        runtime().expect("runtime").block_on(async {
            let path = directory.path().join("bad.sock");
            let listener = create_listener(&path).expect("listener");
            let (sender, receiver) = mpsc::sync_channel(1);
            let (stop, stopped) = oneshot::channel();
            let worker = tokio::spawn(serve(listener, sender, stopped));
            let mut client = connect(&path).await.expect("client");
            client.write_u32_le(65537).await.expect("oversized frame");
            assert!(client.read_u8().await.is_err());
            let mut client = connect(&path).await.expect("client");
            client.write_u32_le(1).await.expect("invalid frame");
            client.write_all(b"{").await.expect("invalid JSON");
            assert!(client.read_u8().await.is_err());
            forward(path, br#"["--show"]"#)
                .await
                .expect("valid command after rejections");
            assert_eq!(
                receiver.try_recv().expect("accepted command"),
                vec!["--show"]
            );
            stop.send(()).expect("shutdown");
            worker.await.expect("worker");
        });
    }
}

#[cfg(test)]
mod legacy_path_tests {
    use super::*;
    #[test]
    fn only_existing_original_data_directory_needs_legacy_mutex() {
        let temporary = tempfile::tempdir().expect("test directory");
        let appdata = temporary.path().join("Roaming");
        let original = appdata.join("Task Focuser");
        let isolated = temporary.path().join("isolated");
        fs::create_dir_all(&isolated).expect("isolated directory");
        assert!(!uses_legacy_data(&isolated, Some(appdata.as_os_str())).expect("missing original"));
        assert!(
            !original.exists(),
            "lookup must not create an original app directory"
        );
        fs::create_dir_all(&original).expect("fake original");
        assert!(uses_legacy_data(&original, Some(appdata.as_os_str())).expect("same data"));
        assert!(
            uses_legacy_data(
                &original.join("..").join("Task Focuser"),
                Some(appdata.as_os_str())
            )
            .expect("canonical alias")
        );
        assert!(
            !uses_legacy_data(&isolated, Some(appdata.as_os_str())).expect("independent override")
        );
        assert!(!uses_legacy_data(&isolated, None).expect("missing appdata"));
        #[cfg(windows)]
        assert!(
            uses_legacy_data(
                &PathBuf::from(original.to_string_lossy().to_uppercase()),
                Some(appdata.as_os_str())
            )
            .expect("case alias")
        );
    }
}
