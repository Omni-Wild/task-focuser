//! User-owned login startup entries. Construction and status checks never change them.

#[cfg(unix)]
use std::fs;

#[cfg(any(windows, target_os = "linux"))]
use std::path::Path;
use std::{io, path::PathBuf};

#[derive(Debug, thiserror::Error)]
pub enum StartupError {
    #[error("could not update login startup: {0}")]
    Io(#[from] io::Error),
    #[error("login startup is disabled for isolated profiles")]
    Isolated,
    #[error("the user configuration directory is unavailable")]
    NoHome,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StartupStatus {
    pub registered: bool,
    pub enabled: bool,
    pub points_here: bool,
}

#[derive(Debug)]
pub struct Startup {
    executable: PathBuf,
    isolated: bool,
    #[cfg(unix)]
    entry: PathBuf,
}

impl Startup {
    pub fn new(executable: PathBuf, isolated: bool) -> Result<Self, StartupError> {
        #[cfg(target_os = "linux")]
        let entry = directories::BaseDirs::new()
            .ok_or(StartupError::NoHome)?
            .config_dir()
            .join("autostart/task-focuser.desktop");
        #[cfg(target_os = "macos")]
        let entry = directories::BaseDirs::new()
            .ok_or(StartupError::NoHome)?
            .home_dir()
            .join("Library/LaunchAgents/dev.task-focuser.plist");
        Ok(Self {
            executable,
            isolated,
            #[cfg(unix)]
            entry,
        })
    }

    /// Allows file-backed startup behavior to be verified without touching the user's settings.
    #[cfg(unix)]
    pub fn with_entry(executable: PathBuf, entry: PathBuf, isolated: bool) -> Self {
        Self {
            executable,
            isolated,
            entry,
        }
    }

    pub fn status(&self) -> Result<StartupStatus, StartupError> {
        if self.isolated {
            return Ok(StartupStatus {
                registered: false,
                enabled: false,
                points_here: false,
            });
        }
        #[cfg(windows)]
        {
            windows::status(&self.executable).map_err(Into::into)
        }
        #[cfg(unix)]
        {
            let content = match fs::read_to_string(&self.entry) {
                Ok(content) => content,
                Err(e) if e.kind() == io::ErrorKind::NotFound => {
                    return Ok(StartupStatus {
                        registered: false,
                        enabled: false,
                        points_here: false,
                    });
                }
                Err(e) => return Err(e.into()),
            };
            let points_here = self
                .entry_executable(&content)
                .is_some_and(|path| path == self.executable);
            #[cfg(target_os = "linux")]
            let disabled = content.lines().any(|line| {
                line.trim().eq_ignore_ascii_case("Hidden=true")
                    || line
                        .trim()
                        .eq_ignore_ascii_case("X-GNOME-Autostart-enabled=false")
            });
            #[cfg(target_os = "macos")]
            let disabled = content.contains("<key>Disabled</key>\n<true/>");
            Ok(StartupStatus {
                registered: true,
                enabled: points_here && !disabled,
                points_here,
            })
        }
    }

    pub fn is_enabled(&self) -> Result<bool, StartupError> {
        self.status().map(|status| status.enabled)
    }

    pub fn set_enabled(&self, enabled: bool) -> Result<(), StartupError> {
        if self.isolated {
            return Err(StartupError::Isolated);
        }
        #[cfg(windows)]
        {
            windows::set_enabled(&self.executable, enabled)?;
        }
        #[cfg(unix)]
        {
            if enabled {
                let parent = self
                    .entry
                    .parent()
                    .ok_or_else(|| io::Error::other("startup entry has no parent directory"))?;
                fs::create_dir_all(parent)?;
                fs::write(&self.entry, self.content()?)?;
            } else {
                match fs::remove_file(&self.entry) {
                    Ok(()) => {}
                    Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                    Err(e) => return Err(e.into()),
                }
            }
        }
        Ok(())
    }

    /// Repair only an existing entry whose previous executable no longer exists.
    pub fn repair_if_moved(&self) -> Result<bool, StartupError> {
        if self.isolated {
            return Ok(false);
        }
        #[cfg(windows)]
        {
            windows::repair(&self.executable).map_err(Into::into)
        }
        #[cfg(unix)]
        {
            let content = match fs::read_to_string(&self.entry) {
                Ok(content) => content,
                Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(false),
                Err(e) => return Err(e.into()),
            };
            let Some(old) = self.entry_executable(&content) else {
                return Ok(false);
            };
            if old == self.executable || old.exists() {
                return Ok(false);
            }
            // Preserve external disable switches while replacing only the executable.
            #[cfg(target_os = "linux")]
            let repaired = content
                .lines()
                .map(|line| {
                    if line.starts_with("Exec=") {
                        desktop_exec(&self.executable).map(|exec| format!("Exec={exec}"))
                    } else {
                        Ok(line.to_owned())
                    }
                })
                .collect::<io::Result<Vec<_>>>()?
                .join("\n")
                + "\n";
            #[cfg(target_os = "macos")]
            let repaired = content.replace(
                &xml_escape(&old.to_string_lossy()),
                &xml_escape(&self.executable.to_string_lossy()),
            );
            fs::write(&self.entry, repaired)?;
            Ok(true)
        }
    }

    #[cfg(target_os = "linux")]
    fn content(&self) -> io::Result<String> {
        Ok(format!(
            "[Desktop Entry]\nType=Application\nName=Task Focuser\nExec={}\nTerminal=false\nStartupNotify=false\n",
            desktop_exec(&self.executable)?
        ))
    }
    #[cfg(target_os = "macos")]
    fn content(&self) -> io::Result<String> {
        Ok(format!(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict>\n<key>Label</key><string>dev.task-focuser</string>\n<key>ProgramArguments</key><array><string>{}</string></array>\n<key>RunAtLoad</key><true/>\n</dict></plist>\n",
            xml_escape(&self.executable.to_string_lossy())
        ))
    }

    #[cfg(target_os = "linux")]
    fn entry_executable(&self, content: &str) -> Option<PathBuf> {
        let exec = content
            .lines()
            .find_map(|line| line.strip_prefix("Exec="))?;
        // Desktop-entry string escaping is decoded before Exec's quoted-argument escaping.
        let mut decoded = String::new();
        let mut encoded = exec.chars();
        while let Some(ch) = encoded.next() {
            if ch == '\\' {
                decoded.push(match encoded.next()? {
                    '\\' => '\\',
                    's' => ' ',
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => return None,
                });
            } else {
                decoded.push(ch);
            }
        }
        let mut path = String::new();
        let mut chars = decoded.strip_prefix('"')?.chars();
        while let Some(ch) = chars.next() {
            match ch {
                '"' => return Some(PathBuf::from(path)),
                '\\' => path.push(chars.next()?),
                '%' => {
                    if chars.next()? != '%' {
                        return None;
                    }
                    path.push('%');
                }
                ch => path.push(ch),
            }
        }
        None
    }
    #[cfg(target_os = "macos")]
    fn entry_executable(&self, content: &str) -> Option<PathBuf> {
        let program = content
            .split_once("<key>ProgramArguments</key>")?
            .1
            .split_once("<string>")?
            .1
            .split_once("</string>")?
            .0;
        Some(PathBuf::from(
            program
                .replace("&quot;", "\"")
                .replace("&apos;", "'")
                .replace("&lt;", "<")
                .replace("&gt;", ">")
                .replace("&amp;", "&"),
        ))
    }
}

#[cfg(target_os = "linux")]
fn desktop_exec(path: &Path) -> io::Result<String> {
    let text = path.to_str().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "startup executable path is not UTF-8",
        )
    })?;
    if text.contains(['\n', '\r']) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "startup executable path contains a newline",
        ));
    }
    let escaped = text
        .replace('\\', "\\\\\\\\")
        .replace('"', "\\\\\"")
        .replace('`', "\\\\`")
        .replace('$', "\\\\$")
        .replace('%', "%%");
    Ok(format!("\"{escaped}\""))
}
#[cfg(target_os = "macos")]
fn xml_escape(text: &str) -> String {
    text.replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

#[cfg(windows)]
mod windows {
    use super::*;
    use winreg::{
        RegKey,
        enums::{HKEY_CURRENT_USER, KEY_READ, KEY_WRITE},
    };
    const RUN: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";
    const APPROVED: &str =
        r"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const NAME: &str = "Task Focuser";
    fn value() -> io::Result<Option<String>> {
        match RegKey::predef(HKEY_CURRENT_USER)
            .open_subkey(RUN)
            .and_then(|key| key.get_value(NAME))
        {
            Ok(value) => Ok(Some(value)),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(e),
        }
    }
    fn executable(command: &str) -> PathBuf {
        let command = command.trim();
        if let Some(quoted) = command.strip_prefix('"') {
            return PathBuf::from(quoted.split('"').next().unwrap_or(quoted));
        }
        if let Some(index) = command.to_lowercase().find(".exe") {
            PathBuf::from(&command[..index + 4])
        } else {
            PathBuf::from(command)
        }
    }
    fn disabled() -> io::Result<bool> {
        match RegKey::predef(HKEY_CURRENT_USER)
            .open_subkey(APPROVED)
            .and_then(|key| key.get_raw_value(NAME))
        {
            Ok(flag) => Ok(flag.bytes.first().is_some_and(|byte| byte & 1 == 1)),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(false),
            Err(e) => Err(e),
        }
    }
    pub fn status(path: &Path) -> io::Result<StartupStatus> {
        let Some(command) = value()? else {
            return Ok(StartupStatus {
                registered: false,
                enabled: false,
                points_here: false,
            });
        };
        let points_here = executable(&command)
            .to_string_lossy()
            .eq_ignore_ascii_case(&path.to_string_lossy());
        Ok(StartupStatus {
            registered: true,
            points_here,
            enabled: points_here && !disabled()?,
        })
    }
    pub fn set_enabled(path: &Path, enabled: bool) -> io::Result<()> {
        let hkcu = RegKey::predef(HKEY_CURRENT_USER);
        let (key, _) = hkcu.create_subkey(RUN)?;
        if enabled {
            key.set_value(NAME, &format!("\"{}\"", path.display()))?;
            match hkcu
                .open_subkey_with_flags(APPROVED, KEY_READ | KEY_WRITE)
                .and_then(|key| key.delete_value(NAME))
            {
                Ok(()) => {}
                Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                Err(e) => return Err(e),
            }
        } else {
            match key.delete_value(NAME) {
                Ok(()) => {}
                Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                Err(e) => return Err(e),
            }
        }
        Ok(())
    }
    pub fn repair(path: &Path) -> io::Result<bool> {
        let Some(command) = value()? else {
            return Ok(false);
        };
        if executable(&command).exists() {
            return Ok(false);
        }
        RegKey::predef(HKEY_CURRENT_USER)
            .open_subkey_with_flags(RUN, KEY_WRITE)?
            .set_value(NAME, &format!("\"{}\"", path.display()))?;
        Ok(true)
    }
}
