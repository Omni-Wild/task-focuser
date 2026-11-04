//! Short synthesized bird sounds, played on a dedicated audio thread without a disk cache.

use rodio::{OutputStream, OutputStreamBuilder, Sink, buffer::SamplesBuffer};
use std::{
    collections::HashMap,
    sync::mpsc::{self, Receiver, SyncSender, TrySendError},
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

pub use task_focuser_core::audio::SoundKind as ChirpKind;

#[derive(Debug, thiserror::Error)]
pub enum SoundError {
    #[error("sound is unavailable: {0}")]
    Device(String),
    #[error("the sound command queue is full")]
    Busy,
    #[error("the sound worker has stopped")]
    Stopped,
}

enum Command {
    Play(ChirpKind, f32, String),
    Volume(f32),
    Stop,
}

pub struct Sound {
    sender: Option<SyncSender<Command>>,
    diagnostics: Receiver<String>,
    worker: Option<JoinHandle<()>>,
}
impl Sound {
    /// Disabled sound never opens an audio device, including in isolated test profiles.
    pub fn new(enabled: bool) -> Result<Self, SoundError> {
        let (diagnostic_tx, diagnostics) = mpsc::sync_channel(8);
        if !enabled {
            return Ok(Self {
                sender: None,
                diagnostics,
                worker: None,
            });
        }
        let (sender, receiver) = mpsc::sync_channel(32);
        let (ready_tx, ready_rx) = mpsc::sync_channel(1);
        let worker = thread::Builder::new()
            .name("task-focuser-sound".into())
            .spawn(move || {
                let stream = match open_stream(diagnostic_tx.clone()) {
                    Ok(stream) => {
                        let _ = ready_tx.send(Ok(()));
                        stream
                    }
                    Err(error) => {
                        let _ = ready_tx.send(Err(error));
                        return;
                    }
                };
                let mut stream = Some(stream);
                let mut players: HashMap<(ChirpKind, String), Sink> = HashMap::new();
                let mut last_play = Instant::now();
                loop {
                    let command = match receiver.recv_timeout(Duration::from_secs(1)) {
                        Ok(command) => command,
                        Err(mpsc::RecvTimeoutError::Disconnected) => break,
                        Err(mpsc::RecvTimeoutError::Timeout) => {
                            if last_play.elapsed() > Duration::from_secs(20) {
                                players.clear();
                                stream = None;
                            }
                            continue;
                        }
                    };
                    match command {
                        Command::Stop => break,
                        Command::Volume(volume) => {
                            for sink in players.values() {
                                sink.set_volume(volume);
                            }
                        }
                        Command::Play(kind, volume, bird) => {
                            if stream.is_none() {
                                match open_stream(diagnostic_tx.clone()) {
                                    Ok(opened) => stream = Some(opened),
                                    Err(error) => {
                                        tracing::error!(%error, "could not reopen audio device");
                                        let _ = diagnostic_tx.try_send(error);
                                        continue;
                                    }
                                }
                            }
                            let Some(output) = &stream else {
                                continue;
                            };
                            let key = (kind, bird.clone());
                            if let Some(previous) = players.remove(&key) {
                                previous.stop();
                            }
                            let sink = Sink::connect_new(output.mixer());
                            sink.set_volume(volume);
                            sink.append(SamplesBuffer::new(1, RATE, render(kind, &bird)));
                            players.insert(key, sink);
                            players.retain(|_, sink| !sink.empty());
                            last_play = Instant::now();
                        }
                    }
                }
            })
            .map_err(|error| SoundError::Device(error.to_string()))?;
        match ready_rx.recv() {
            Ok(Ok(())) => Ok(Self {
                sender: Some(sender),
                diagnostics,
                worker: Some(worker),
            }),
            Ok(Err(error)) => {
                let _ = worker.join();
                Err(SoundError::Device(error))
            }
            Err(_) => {
                let _ = worker.join();
                Err(SoundError::Stopped)
            }
        }
    }

    pub fn play(&self, kind: ChirpKind, volume: f32, bird: &str) -> Result<(), SoundError> {
        self.send(Command::Play(kind, safe_volume(volume), voice(bird).into()))
    }
    pub fn set_volume(&self, volume: f32) -> Result<(), SoundError> {
        self.send(Command::Volume(safe_volume(volume)))
    }
    pub fn take_diagnostics(&mut self) -> Receiver<String> {
        let (_, disconnected) = mpsc::channel();
        std::mem::replace(&mut self.diagnostics, disconnected)
    }
    pub fn try_diagnostic(&self) -> Option<String> {
        self.diagnostics.try_recv().ok()
    }
    fn send(&self, command: Command) -> Result<(), SoundError> {
        let Some(sender) = &self.sender else {
            return Ok(());
        };
        sender.try_send(command).map_err(|error| match error {
            TrySendError::Full(_) => SoundError::Busy,
            TrySendError::Disconnected(_) => SoundError::Stopped,
        })
    }
}
impl Drop for Sound {
    fn drop(&mut self) {
        // Dropping the sender also wakes a worker when its bounded queue is full.
        if let Some(sender) = self.sender.take() {
            let _ = sender.try_send(Command::Stop);
        }
        if let Some(worker) = self.worker.take()
            && worker.join().is_err()
        {
            tracing::error!("sound worker panicked during shutdown");
        }
    }
}
fn open_stream(diagnostics: SyncSender<String>) -> Result<OutputStream, String> {
    let builder = OutputStreamBuilder::from_default_device().map_err(|error| error.to_string())?;
    let mut stream = builder
        .with_error_callback(move |error| {
            tracing::error!(%error, "audio output failed");
            let _ =
                diagnostics.try_send(format!("Sound stopped. Check your audio output. {error}"));
        })
        .open_stream()
        .map_err(|error| error.to_string())?;
    stream.log_on_drop(false);
    Ok(stream)
}
fn safe_volume(volume: f32) -> f32 {
    if volume.is_finite() {
        volume.clamp(0., 1.)
    } else {
        0.
    }
}
fn voice(bird: &str) -> &'static str {
    task_focuser_core::sprites::BirdId::from_id(bird).id()
}
const RATE: u32 = task_focuser_core::audio::SAMPLE_RATE;

/// Render the original normalized mono PCM without opening a device or writing to disk.
pub fn render(kind: ChirpKind, bird: &str) -> Vec<f32> {
    task_focuser_core::audio::render(task_focuser_core::sprites::BirdId::from_id(bird), kind)
        .into_iter()
        .map(|sample| f32::from(sample) / f32::from(i16::MAX))
        .collect()
}
