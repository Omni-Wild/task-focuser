//! Durable state persistence with backups before replacing unread or corrupt data.

use std::{
    fs::{self, File, OpenOptions},
    io::{self, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

use tempfile::NamedTempFile;
use thiserror::Error;

use crate::state::{NoteState, StateError};

#[derive(Debug, Error)]
pub enum StoreError {
    #[error("cannot {operation} {path}: {source}")]
    Io {
        operation: &'static str,
        path: PathBuf,
        #[source]
        source: io::Error,
    },
    #[error("cannot serialize state: {0}")]
    State(#[from] StateError),
}

#[derive(Debug)]
pub enum LoadDiagnostic {
    Created,
    /// The original is preserved at this path before defaults are written.
    Corrupt {
        backup: PathBuf,
        error: StateError,
    },
    /// Defaults are usable, but writing is blocked until the original can be backed up.
    Unreadable {
        error: io::Error,
    },
}

#[derive(Debug)]
pub struct LoadOutcome {
    pub state: NoteState,
    pub diagnostics: Vec<LoadDiagnostic>,
}

#[derive(Debug)]
pub struct StateStore {
    path: PathBuf,
    backup_before_write: bool,
}

impl StateStore {
    /// Construction never discovers or opens the user's live state path.
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self {
            path: path.into(),
            backup_before_write: false,
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Loads forgiving field-by-field state and reports recoveries to the host.
    /// Missing or corrupt state is replaced with defaults only after safe recovery.
    pub fn load(&mut self) -> Result<LoadOutcome, StoreError> {
        match fs::read_to_string(&self.path) {
            Ok(json) => match NoteState::from_json(&json) {
                Ok(state) => {
                    self.backup_before_write = false;
                    Ok(LoadOutcome {
                        state,
                        diagnostics: Vec::new(),
                    })
                }
                Err(error) => {
                    // Leave this guard set if either the backup or the replacement fails.
                    self.backup_before_write = true;
                    let backup = self.backup("corrupt")?;
                    self.backup_before_write = false;
                    let state = NoteState::default();
                    self.flush(&state)?;
                    Ok(LoadOutcome {
                        state,
                        diagnostics: vec![LoadDiagnostic::Corrupt { backup, error }],
                    })
                }
            },
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                // Check again at write time, in case a previously unavailable path appears.
                self.backup_before_write = true;
                let state = NoteState::default();
                self.flush(&state)?;
                Ok(LoadOutcome {
                    state,
                    diagnostics: vec![LoadDiagnostic::Created],
                })
            }
            Err(error) => {
                self.backup_before_write = true;
                Ok(LoadOutcome {
                    state: NoteState::default(),
                    diagnostics: vec![LoadDiagnostic::Unreadable { error }],
                })
            }
        }
    }

    /// Persists immediately. The host owns debounce scheduling and calls this on quit.
    pub fn flush(&mut self, state: &NoteState) -> Result<(), StoreError> {
        let json = state.to_json()?;
        let directory = parent(&self.path);
        fs::create_dir_all(directory)
            .map_err(|source| self.io_error("create state directory for", source))?;
        if self.backup_before_write {
            match fs::metadata(&self.path) {
                Ok(_) => {
                    self.backup("unread")?;
                }
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(source) => return Err(self.io_error("inspect before backing up", source)),
            }
            self.backup_before_write = false;
        }
        let mut temporary = NamedTempFile::new_in(directory)
            .map_err(|source| self.io_error("create temporary state beside", source))?;
        temporary
            .write_all(json.as_bytes())
            .map_err(|source| self.io_error("write temporary state for", source))?;
        temporary
            .write_all(b"\n")
            .map_err(|source| self.io_error("finish temporary state for", source))?;
        temporary
            .as_file()
            .sync_all()
            .map_err(|source| self.io_error("sync temporary state for", source))?;
        temporary
            .persist(&self.path)
            .map_err(|error| self.io_error("replace", error.error))?;
        sync_directory(directory)
            .map_err(|source| self.io_error("sync state directory for", source))?;
        Ok(())
    }

    fn backup(&self, kind: &str) -> Result<PathBuf, StoreError> {
        let mut source_file = File::open(&self.path)
            .map_err(|source| self.io_error("read before backing up", source))?;
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        for attempt in 0..100 {
            let mut name = self.path.as_os_str().to_owned();
            name.push(format!(".{kind}-{stamp}-{}-{attempt}", std::process::id()));
            let backup = PathBuf::from(name);
            let mut destination = match OpenOptions::new()
                .create_new(true)
                .write(true)
                .open(&backup)
            {
                Ok(file) => file,
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(source) => return Err(self.io_error("create backup of", source)),
            };
            io::copy(&mut source_file, &mut destination)
                .map_err(|source| self.io_error("copy backup of", source))?;
            destination
                .sync_all()
                .map_err(|source| self.io_error("sync backup of", source))?;
            sync_directory(parent(&self.path))
                .map_err(|source| self.io_error("sync backup directory for", source))?;
            return Ok(backup);
        }
        Err(self.io_error(
            "create unique backup of",
            io::Error::new(io::ErrorKind::AlreadyExists, "backup names already exist"),
        ))
    }

    fn io_error(&self, operation: &'static str, source: io::Error) -> StoreError {
        StoreError::Io {
            operation,
            path: self.path.clone(),
            source,
        }
    }
}

fn parent(path: &Path) -> &Path {
    path.parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."))
}

#[cfg(unix)]
fn sync_directory(path: &Path) -> io::Result<()> {
    File::open(path)?.sync_all()
}

// Rust cannot open a directory handle with the flags needed by Windows FlushFileBuffers.
// The replacement file has already been synced before the atomic rename.
#[cfg(not(unix))]
fn sync_directory(_path: &Path) -> io::Result<()> {
    Ok(())
}
