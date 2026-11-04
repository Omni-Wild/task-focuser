//! Profile-scoped paths. Discovering paths never creates directories or opens state.

use std::{env, path::PathBuf};

use thiserror::Error;

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Profile {
    name: Option<String>,
}

#[derive(Debug, Error)]
pub enum ProfileError {
    #[error("TASKFOCUSER_PROFILE may contain only letters, digits, '-' and '_'")]
    InvalidName,
    #[error("{0} is not valid Unicode")]
    InvalidEnvironment(&'static str),
    #[error("cannot locate {0}; set TASKFOCUSER_DATA_DIR to an explicit directory")]
    MissingDirectory(&'static str),
    #[error("TASKFOCUSER_DATA_DIR must not be empty")]
    EmptyDataDirectory,
}

impl Profile {
    pub fn new(name: Option<&str>) -> Result<Self, ProfileError> {
        let name = name.map(str::trim).filter(|name| !name.is_empty());
        if name.is_some_and(|name| {
            !name
                .chars()
                .all(|c| c.is_alphanumeric() || c == '-' || c == '_')
        }) {
            return Err(ProfileError::InvalidName);
        }
        Ok(Self {
            name: name.map(str::to_owned),
        })
    }

    pub fn from_env() -> Result<Self, ProfileError> {
        match env::var("TASKFOCUSER_PROFILE") {
            Ok(name) => Self::new(Some(&name)),
            Err(env::VarError::NotPresent) => Self::new(None),
            Err(env::VarError::NotUnicode(_)) => {
                Err(ProfileError::InvalidEnvironment("TASKFOCUSER_PROFILE"))
            }
        }
    }

    pub fn name(&self) -> Option<&str> {
        self.name.as_deref()
    }
    pub fn is_test(&self) -> bool {
        self.name.is_some()
    }
    pub fn suffix(&self) -> String {
        self.name
            .as_ref()
            .map_or_else(String::new, |name| format!(".{name}"))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Platform {
    Windows,
    Macos,
    Linux,
}

impl Platform {
    pub const fn current() -> Self {
        if cfg!(target_os = "windows") {
            Self::Windows
        } else if cfg!(target_os = "macos") {
            Self::Macos
        } else {
            Self::Linux
        }
    }
}

/// Explicit environment inputs keep path resolution testable without process-env mutation.
#[derive(Clone, Debug, Default)]
pub struct PathEnvironment {
    pub home: Option<PathBuf>,
    pub appdata: Option<PathBuf>,
    pub local_appdata: Option<PathBuf>,
    pub xdg_config_home: Option<PathBuf>,
    pub xdg_cache_home: Option<PathBuf>,
    pub data_override: Option<PathBuf>,
}

impl PathEnvironment {
    pub fn from_env() -> Result<Self, ProfileError> {
        let path = |key| {
            env::var_os(key)
                .filter(|v| !v.is_empty())
                .map(PathBuf::from)
        };
        let data_override = env::var_os("TASKFOCUSER_DATA_DIR").map(PathBuf::from);
        if data_override
            .as_ref()
            .is_some_and(|path| path.as_os_str().is_empty())
        {
            return Err(ProfileError::EmptyDataDirectory);
        }
        Ok(Self {
            home: path(if cfg!(windows) { "USERPROFILE" } else { "HOME" }),
            appdata: path("APPDATA"),
            local_appdata: path("LOCALAPPDATA"),
            xdg_config_home: path("XDG_CONFIG_HOME"),
            xdg_cache_home: path("XDG_CACHE_HOME"),
            data_override,
        })
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AppPaths {
    pub profile: Profile,
    pub state_file: PathBuf,
    /// All generated assets, including sounds, belong under this directory.
    pub cache_dir: PathBuf,
    pub data_dir: PathBuf,
    pub is_isolated: bool,
}

impl AppPaths {
    pub fn from_env() -> Result<Self, ProfileError> {
        Self::resolve(
            Profile::from_env()?,
            Platform::current(),
            &PathEnvironment::from_env()?,
        )
    }

    pub fn resolve(
        profile: Profile,
        platform: Platform,
        environment: &PathEnvironment,
    ) -> Result<Self, ProfileError> {
        let home = || {
            environment
                .home
                .clone()
                .ok_or(ProfileError::MissingDirectory("home directory"))
        };
        let (data_dir, cache_base) = if let Some(directory) = &environment.data_override {
            if directory.as_os_str().is_empty() {
                return Err(ProfileError::EmptyDataDirectory);
            }
            (directory.clone(), directory.join("cache"))
        } else {
            match platform {
                Platform::Windows => {
                    let appdata = environment
                        .appdata
                        .clone()
                        .ok_or(ProfileError::MissingDirectory("APPDATA"))?;
                    let local = environment
                        .local_appdata
                        .clone()
                        .unwrap_or_else(|| appdata.clone());
                    (appdata.join("Task Focuser"), local.join("Task Focuser"))
                }
                Platform::Macos => {
                    let home = home()?;
                    (
                        home.join("Library/Application Support/Task Focuser"),
                        home.join("Library/Caches/Task Focuser"),
                    )
                }
                Platform::Linux => {
                    // XDG paths must be absolute; a relative value is ignored per the XDG specification.
                    let config = match &environment.xdg_config_home {
                        Some(path) if path.is_absolute() => path.clone(),
                        _ => home()?.join(".config"),
                    };
                    let cache = match &environment.xdg_cache_home {
                        Some(path) if path.is_absolute() => path.clone(),
                        _ => home()?.join(".cache"),
                    };
                    (config.join("task-focuser"), cache.join("task-focuser"))
                }
            }
        };
        let state_file = data_dir.join(format!("state{}.json", profile.suffix()));
        let cache_dir = match profile.name() {
            Some(name) => cache_base.join("profiles").join(name),
            None => cache_base,
        };
        let is_isolated = profile.is_test() || environment.data_override.is_some();
        Ok(Self {
            profile,
            state_file,
            cache_dir,
            data_dir,
            is_isolated,
        })
    }

    /// Isolated profiles must never alter the user's login startup settings.
    pub fn allows_autostart(&self) -> bool {
        !self.is_isolated
    }
}
