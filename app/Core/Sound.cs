using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace TaskFocuser.Core;

/// <summary>Which of the bird's sounds to play (SPEC-round2 2.5; Ruffle is SPEC-round3 2.3).</summary>
public enum ChirpKind { Keepalive, Happy, Annoyed, Peep, Ruffle }

/// <summary>
/// The companions' voices: five sounds each synthesized in code, rendered once to wave files under
/// %LOCALAPPDATA%\Task Focuser\sounds\ and played through WPF MediaPlayer (SPEC-round2 2.5, SPEC-round5 2.3,
/// SPEC-round6 2.4; Rori's synthesis lives in Sound.Rori.cs).
/// Synthesizing keeps the exe free of binary assets and lets us tune the sound in code;
/// files are needed because MediaPlayer plays from a URI, not from memory.
/// </summary>
public static partial class Sound
{
    /// <summary>Round 2's fixed keep-alive interval. Round 3's conductor draws random intervals from
    /// <c>state.IdleRate</c> instead; kept so nothing that still names it breaks.</summary>
    public const int KeepaliveMinutes = 5;

    // Bump when the synthesis changes so old files on disk are ignored and re-rendered.
    private const string Version = "v1";
    private const int Rate = 44100;

    /// <summary>The birds that have a voice, by the ids of Monster/Birds.cs. Kept here rather than read from
    /// Birds so Core stays free of the sprite code; a bird without its own synthesis sounds like Lennie.</summary>
    internal static readonly string[] Voices = { "lennie", "meggie", "rori" };
    private const string Lennie = "lennie", Meggie = "meggie", Rori = "rori";

    private static readonly object RenderLock = new();
    // One player per (bird, kind), held in a field: repeated plays reuse the opened media (instant),
    // and a playing player is never left without a reference for the GC to collect mid-sound.
    private static readonly Dictionary<(string Bird, ChirpKind Kind), Voice> Players = new();

    // A player left idle longer than this is closed and opened afresh at its next play, so it binds to the
    // audio device that is the default now (headphones plugged in or switched since the last sound);
    // quick repeats (clicks, the slider's peeps) still reuse the open one.
    private static readonly TimeSpan Reopen = TimeSpan.FromSeconds(20);

    private sealed class Voice(MediaPlayer player)
    {
        public MediaPlayer Player { get; } = player;
        public long LastPlay;
    }

    internal static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Task Focuser", "sounds");

    /// <summary>The voice a bird id plays with; an unknown or empty id is Lennie, as in Birds.ById.</summary>
    internal static string VoiceOf(string? bird)
    {
        string id = bird?.Trim() ?? "";
        foreach (var v in Voices) if (string.Equals(v, id, StringComparison.OrdinalIgnoreCase)) return v;
        return Lennie;
    }

    /// <summary>Round 5 names, <c>&lt;birdId&gt;-&lt;kind&gt;-v1.wav</c>, so each bird's set lives side by side.</summary>
    internal static string PathOf(ChirpKind kind, string bird = Lennie) =>
        Path.Combine(Folder, $"{VoiceOf(bird)}-{kind.ToString().ToLowerInvariant()}-{Version}.wav");

    /// <summary>Renders every bird's missing wave files and removes the round-2/3 names; call once at startup,
    /// off the UI thread. Never throws.</summary>
    public static void Warm()
    {
        foreach (var bird in Voices)
            foreach (ChirpKind kind in Enum.GetValues<ChirpKind>())
            {
                DropIfDamaged(PathOf(kind, bird));
                EnsureFile(kind, bird);
            }
        // The pre-round-5 files (<kind>-v1.wav) are Lennie's sounds under the old names; nothing plays them
        // any more, so they would only sit on disk.
        foreach (ChirpKind kind in Enum.GetValues<ChirpKind>())
        {
            try
            {
                string old = Path.Combine(Folder, $"{kind.ToString().ToLowerInvariant()}-{Version}.wav");
                if (File.Exists(old)) File.Delete(old);
            }
            catch (Exception ex) { Debug.WriteLine($"Sound legacy delete {kind}: {ex.Message}"); }
        }
    }

    /// <summary>Lennie's sound: the round-2 form, kept for callers that have no bird to name.</summary>
    public static void Play(ChirpKind kind, double volume) => Play(kind, volume, Lennie);

    /// <summary>
    /// Plays one bird's sound at <paramref name="volume"/> (0 to 1, clamped). Call it on the UI thread; from any
    /// other thread it marshals itself onto Application.Current.Dispatcher, because a MediaPlayer is bound to the
    /// thread that created it. The caller skips the call when sound is off. Never throws.
    /// </summary>
    public static void Play(ChirpKind kind, double volume, string bird)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => Play(kind, volume, bird));
                return;
            }
            var key = (Bird: VoiceOf(bird), Kind: kind);
            if (!EnsureFile(kind, key.Bird)) return;
            if (Players.TryGetValue(key, out var voice) && Stopwatch.GetElapsedTime(voice.LastPlay) > Reopen)
            {
                Drop(key, voice.Player, deleteFile: false);
                voice = null;
            }
            if (voice is null)
            {
                var player = new MediaPlayer();
                // A failed open or play (no audio device, a file it cannot read) closes the player and deletes the
                // file, so the next play renders and opens afresh instead of staying silent or leaking a player.
                player.MediaFailed += (_, e) =>
                {
                    Debug.WriteLine($"Sound {key.Bird} {key.Kind}: {e.ErrorException?.Message}");
                    player.Dispatcher.BeginInvoke(() => Drop(key, player, deleteFile: true));
                };
                player.Open(new Uri(PathOf(kind, key.Bird), UriKind.Absolute));
                voice = new Voice(player);
                Players[key] = voice;
            }
            voice.LastPlay = Stopwatch.GetTimestamp();
            voice.Player.Stop(); // rewinds, so a play during a play restarts the sound rather than being ignored
            voice.Player.Volume = Math.Clamp(double.IsNaN(volume) ? 0 : volume, 0, 1);
            voice.Player.Play();
        }
        catch (Exception ex) { Debug.WriteLine($"Sound.Play {bird} {kind}: {ex.Message}"); }
    }

    /// <summary>Forgets and closes one player (Close releases its native player and the file at once).</summary>
    private static void Drop((string Bird, ChirpKind Kind) key, MediaPlayer player, bool deleteFile)
    {
        if (Players.TryGetValue(key, out var v) && ReferenceEquals(v.Player, player)) Players.Remove(key);
        try { player.Close(); } catch (Exception ex) { Debug.WriteLine($"Sound close {key.Bird} {key.Kind}: {ex.Message}"); }
        if (!deleteFile) return;
        try { File.Delete(PathOf(key.Kind, key.Bird)); }
        catch (Exception ex) { Debug.WriteLine($"Sound delete {key.Bird} {key.Kind}: {ex.Message}"); }
    }

    /// <summary>Slider position (0 to 1) to volume: the left half covers 0 to 0.1.</summary>
    public static double SliderToVolume(double p) => p <= 0.5 ? 0.2 * p : 0.1 + 1.8 * (p - 0.5);

    /// <summary>Volume (0 to 1) to slider position, the inverse of <see cref="SliderToVolume"/>.</summary>
    public static double VolumeToSlider(double v) => v <= 0.1 ? v / 0.2 : 0.5 + (v - 0.1) / 1.8;

    // ---- rendering ----

    // Locked so Warm on a background thread and a first Play on the UI thread never write the same file twice;
    // written to a temp name and moved, so MediaPlayer never opens a half-written file.
    private static bool EnsureFile(ChirpKind kind, string bird)
    {
        try
        {
            string path = PathOf(kind, bird);
            if (File.Exists(path)) return true;
            lock (RenderLock)
            {
                if (File.Exists(path)) return true;
                Directory.CreateDirectory(Folder);
                string tmp = path + ".tmp";
                WriteWav(tmp, Render(kind, bird));
                File.Move(tmp, path, overwrite: true);
            }
            return true;
        }
        catch (Exception ex) { Debug.WriteLine($"Sound render {bird} {kind}: {ex.Message}"); return false; }
    }

    // A file cut short or emptied outside our own write (a power loss, a disk tool) would open and play cut, or fail,
    // on every play for good. Our header states the data length, so a mismatch is caught here, once per start, and
    // the file is rendered again.
    private static void DropIfDamaged(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            bool ok;
            using (var f = File.OpenRead(path))
            {
                var h = new byte[44];
                ok = f.Length >= 44 && f.Read(h, 0, 44) == 44 && h[0] == (byte)'R' && h[1] == (byte)'I'
                     && 44L + BitConverter.ToInt32(h, 40) == f.Length;
            }
            if (!ok) File.Delete(path);
        }
        catch (Exception ex) { Debug.WriteLine($"Sound check {path}: {ex.Message}"); }
    }

    private enum Wave { Round, Rough }

    // One note: start and length in seconds, pitch (Hz) at start and end (a small glide), decay time constant (s).
    private readonly record struct Note(double Start, double Length, double F0, double F1, double Decay, Wave Wave);

    // 10 ms of lead-in silence: MediaPlayer can swallow the very first milliseconds of a file.
    private const double Lead = 0.010;

    internal static short[] Render(ChirpKind kind, string bird = Lennie) => VoiceOf(bird) switch
    {
        Meggie => RenderMeggie(kind),
        Rori => RenderRori(kind),
        _ => RenderLennie(kind),
    };

    /// <summary>Lennie the budgie: the round-2/3 sounds, unchanged sample for sample (only the file names moved).</summary>
    private static short[] RenderLennie(ChirpKind kind)
    {
        // The ruffle is not built from notes (it is one modulated tone plus noise), so it has its own path;
        // the four round-2 sounds below render exactly as before.
        if (kind == ChirpKind.Ruffle) return ToPcm(RenderRuffle(420, 0.320, 1400, 2800), RufflePeak);

        (Note[] notes, double peak) = kind switch
        {
            // Budgie "pip-pip": two short round notes, the second higher, 60 ms apart.
            ChirpKind.Keepalive => (new[]
            {
                new Note(Lead, 0.070, 1600, 1650, 0.060, Wave.Round),
                new Note(Lead + 0.130, 0.070, 2000, 2060, 0.060, Wave.Round),
            }, 0.50),
            // Three rising notes, a little happy trill.
            ChirpKind.Happy => (new[]
            {
                new Note(Lead, 0.080, 1300, 1340, 0.080, Wave.Round),
                new Note(Lead + 0.120, 0.080, 1600, 1650, 0.080, Wave.Round),
                new Note(Lead + 0.240, 0.080, 2000, 2080, 0.080, Wave.Round),
            }, 0.50),
            // A grumble: lower, a rough triangle with a fast tremolo, gliding slightly down; a bit quieter.
            ChirpKind.Annoyed => (new[] { new Note(Lead, 0.180, 720, 640, 0.120, Wave.Rough) }, 0.40),
            // The click and slider preview: one very short soft note.
            _ => (new[] { new Note(Lead, 0.045, 1800, 1820, 0.060, Wave.Round) }, 0.45),
        };

        double end = notes.Max(n => n.Start + n.Length) + 0.020; // short silent tail
        var buf = new double[(int)(end * Rate)];
        foreach (var n in notes) AddNote(buf, n);
        return ToPcm(buf, peak);
    }

    private static short[] ToPcm(double[] buf, double peak)
    {
        // Normalize to the target peak (0.5 = -6 dBFS) so the volume slider has headroom and the kinds match.
        double max = buf.Max(Math.Abs);
        double gain = max > 0 ? peak / max : 0;
        var pcm = new short[buf.Length];
        for (int i = 0; i < buf.Length; i++) pcm[i] = (short)Math.Round(buf[i] * gain * short.MaxValue);
        return pcm;
    }

    private static void AddNote(double[] buf, Note n)
    {
        const double attack = 0.008, release = 0.012, vibHz = 5, vibCents = 4;
        int first = (int)(n.Start * Rate), count = (int)(n.Length * Rate);
        double phase = 0;
        for (int i = 0; i < count && first + i < buf.Length; i++)
        {
            double t = (double)i / Rate, u = t / n.Length;
            // Pitch: the glide plus a 5 Hz vibrato of a few cents, integrated as phase so the
            // frequency changes never make the waveform jump.
            double f = (n.F0 + (n.F1 - n.F0) * u) * Math.Pow(2, vibCents * Math.Sin(2 * Math.PI * vibHz * t) / 1200);
            phase += 2 * Math.PI * f / Rate;

            double s = n.Wave == Wave.Round
                // Sine with about 15 % second harmonic: rounder than a bare sine, nothing sharp.
                ? Math.Sin(phase) + 0.15 * Math.Sin(2 * phase)
                // Band-limited triangle (odd harmonics at 1/n^2): rough, yet no aliasing or corner clicks;
                // a 25 Hz tremolo dips the level by up to half, which is what makes it read as a grumble.
                : (Math.Sin(phase) - Math.Sin(3 * phase) / 9 + Math.Sin(5 * phase) / 25 - Math.Sin(7 * phase) / 49)
                  * (1 - 0.5 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * 25 * t)));

            // Envelope: raised-cosine attack, exponential decay, raised-cosine release to exactly zero at
            // the note's end, so every note starts and stops at silence (no clicks).
            double env = t < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / attack) : Math.Exp(-(t - attack) / n.Decay);
            double left = n.Length - t;
            if (left < release) env *= 0.5 - 0.5 * Math.Cos(Math.PI * Math.Max(0, left) / release);
            buf[first + i] += s * env;
        }
    }

    // ---- Meggie, the owl (SPEC-round5 2.3) ----

    // One hoot: like Note, but the owl's envelope is slow, with its own attack and a long tail per sound,
    // so each value the spec tunes is a field here.
    private readonly record struct Hoot(double Start, double Length, double F0, double F1, double Attack, double Tail, Wave Wave);

    /// <summary>Meggie's five: low, warm and soft, at the same peaks as Lennie's.</summary>
    private static short[] RenderMeggie(ChirpKind kind)
    {
        // The round-3 ruffle an octave down (tone and feather noise alike), a little longer, the same roll.
        if (kind == ChirpKind.Ruffle) return ToPcm(RenderRuffle(210, 0.360, 700, 1400), RufflePeak);

        (Hoot[] hoots, double peak) = kind switch
        {
            // "Hoo-hoo": two round low notes drooping a little, 90 ms apart; slow attack, long tail.
            ChirpKind.Keepalive => (new[]
            {
                new Hoot(Lead, 0.140, 390, 380, 0.025, 0.100, Wave.Round),
                new Hoot(Lead + 0.230, 0.140, 330, 322, 0.025, 0.100, Wave.Round),
            }, 0.50),
            // Three hoots, each a step higher, 60 ms apart: the owl's version of the happy trill.
            ChirpKind.Happy => (new[]
            {
                new Hoot(Lead, 0.120, 330, 334, 0.020, 0.080, Wave.Round),
                new Hoot(Lead + 0.180, 0.120, 370, 375, 0.020, 0.080, Wave.Round),
                new Hoot(Lead + 0.360, 0.120, 420, 427, 0.020, 0.080, Wave.Round),
            }, 0.50),
            // A short rough screech-grumble gliding down from about 550 Hz; -8 dBFS, the quietest of the set.
            ChirpKind.Annoyed => (new[] { new Hoot(Lead, 0.200, 560, 480, 0.010, 0.060, Wave.Rough) }, 0.398),
            // The click and slider preview: one short soft "hu".
            _ => (new[] { new Hoot(Lead, 0.070, 330, 326, 0.015, 0.045, Wave.Round) }, 0.45),
        };

        double end = hoots.Max(h => h.Start + h.Length) + 0.020; // short silent tail
        var buf = new double[(int)(end * Rate)];
        // Fixed seed: the breath noise is the same on every machine and every render.
        var rng = new Random(0x0771);
        foreach (var h in hoots) AddHoot(buf, h, rng);
        return ToPcm(buf, peak);
    }

    private static void AddHoot(double[] buf, Hoot h, Random rng)
    {
        const double vibHz = 4.5, vibCents = 3;
        int first = (int)(h.Start * Rate), count = (int)(h.Length * Rate);
        // The breath: white noise low-passed at 900 Hz so it sits under the tone as air, never as hiss.
        var breath = Biquad.LowPass(900, 0.707);
        double phase = 0;
        for (int i = 0; i < count && first + i < buf.Length; i++)
        {
            double t = (double)i / Rate, u = t / h.Length;
            // The glide plus a slow, shallow vibrato, integrated as phase so nothing jumps.
            double f = (h.F0 + (h.F1 - h.F0) * u) * Math.Pow(2, vibCents * Math.Sin(2 * Math.PI * vibHz * t) / 1200);
            phase += 2 * Math.PI * f / Rate;
            double air = breath.Next(rng.NextDouble() * 2 - 1);

            double s = h.Wave == Wave.Round
                // A sine with about 10 % second harmonic (offset in phase, so it is hollow rather than bright)
                // and a breath of noise: the "hoo".
                ? Math.Sin(phase) + 0.10 * Math.Sin(2 * phase + 0.5) + 0.25 * air
                // The band-limited triangle of Lennie's grumble with a little more air, its 28 Hz tremolo
                // dipping by up to 60 %: rough and scratchy, still nothing that clicks.
                : ((Math.Sin(phase) - Math.Sin(3 * phase) / 9 + Math.Sin(5 * phase) / 25 - Math.Sin(7 * phase) / 49)
                   + 0.35 * air) * (1 - 0.6 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * 28 * t)));

            // Envelope: raised-cosine attack, a gentle sag, and a raised-cosine tail to exactly zero over the
            // note's last stretch, so each hoot swells in and fades out the way an owl's does.
            double env = t < h.Attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / h.Attack) : Math.Exp(-(t - h.Attack) / 0.30);
            double left = h.Length - t;
            if (left < h.Tail) env *= 0.5 - 0.5 * Math.Cos(Math.PI * Math.Max(0, left) / h.Tail);
            buf[first + i] += s * env;
        }
    }

    // ---- the ruffle (SPEC-round3 2.3) ----

    // -8 dBFS: a touch under the chirps' -6, because a low tone with a rolling AM reads louder than its peak.
    private const double RufflePeak = 0.398;

    /// <summary>
    /// The "brrrrp" of a bird shaking its feathers: a soft tone (<paramref name="f0"/>: Lennie 420 Hz, Meggie 210)
    /// gliding down 8 %, its level rolled at about 32 Hz with a deep raised-cosine AM (the "rrr"), a touch of
    /// second harmonic whose level wavers, and a little band-limited noise for the feathers (band-pass at
    /// <paramref name="bandHz"/>, low-pass at <paramref name="lowHz"/>). Smooth AM rather than a gate keeps it
    /// from buzzing. Lennie's arguments reproduce the round-3 file exactly.
    /// </summary>
    private static double[] RenderRuffle(double f0, double length, double bandHz, double lowHz)
    {
        const double attack = 0.010, tail = 0.080;
        const double glide = 0.08, depth = 0.85;
        int first = (int)(Lead * Rate), count = (int)(length * Rate);
        var buf = new double[first + count + (int)(0.020 * Rate)];   // lead-in, the sound, a silent tail

        // Fixed seed: the same file on every machine and every render, so a re-render never changes the sound.
        var rng = new Random(0x0B1D);
        // The feathers: white noise through a band-pass and a low-pass (two RBJ biquads), so it is a soft
        // hiss with nothing near the top of the band to click or fizz.
        var band = Biquad.BandPass(bandHz, 0.9);
        var low = Biquad.LowPass(lowHz, 0.707);
        // The second harmonic's waver: the same kind of noise slowed to about 40 Hz by a one-pole low-pass.
        double waver = 0, waverK = 1 - Math.Exp(-2 * Math.PI * 40.0 / Rate);

        double phase = 0, amPhase = 0;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Rate, u = t / length;
            // Pitch and AM rate both ease down a little, as a shake slows; integrated as phase so nothing jumps.
            phase += 2 * Math.PI * f0 * (1 - glide * u) / Rate;
            amPhase += 2 * Math.PI * (33 - 3 * u) / Rate;

            double white = rng.NextDouble() * 2 - 1;
            double hiss = low.Next(band.Next(white));
            waver += waverK * (white - waver);

            double tone = Math.Sin(phase) + (0.16 + 0.6 * waver) * Math.Sin(2 * phase + 0.4);
            // The rolling "rrr": each AM cycle dips to 15 %; the noise rides a slightly earlier phase so the
            // feathery part leads the tone in each roll, which is what reads as flutter rather than tremolo.
            double am = 1 - depth * (0.5 - 0.5 * Math.Cos(amPhase));
            double amNoise = 1 - depth * (0.5 - 0.5 * Math.Cos(amPhase + 0.6));
            double s = tone * am + 1.1 * hiss * amNoise;

            // Envelope: raised-cosine attack, a gentle decay, then a raised-cosine tail to exactly zero, so
            // the file starts and ends in silence (no clicks).
            double env = t < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / attack) : Math.Exp(-(t - attack) / 0.45);
            double left = length - t;
            if (left < tail) env *= 0.5 - 0.5 * Math.Cos(Math.PI * Math.Max(0, left) / tail);
            buf[first + i] = s * env;
        }
        return buf;
    }

    /// <summary>One RBJ cookbook biquad (direct form I); the ruffle's feathers, the owl's breath and Rori's air use it.</summary>
    private sealed class Biquad
    {
        private readonly double b0, b1, b2, a1, a2;
        private double x1, x2, y1, y2;

        private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            this.b0 = b0 / a0; this.b1 = b1 / a0; this.b2 = b2 / a0; this.a1 = a1 / a0; this.a2 = a2 / a0;
        }

        public static Biquad BandPass(double f, double q)
        {
            double w = 2 * Math.PI * f / Rate, alpha = Math.Sin(w) / (2 * q);
            return new Biquad(alpha, 0, -alpha, 1 + alpha, -2 * Math.Cos(w), 1 - alpha);
        }

        public static Biquad LowPass(double f, double q)
        {
            double w = 2 * Math.PI * f / Rate, alpha = Math.Sin(w) / (2 * q), c = Math.Cos(w);
            return new Biquad((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha);
        }

        public double Next(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }

    // A plain 44-byte RIFF/WAVE header (PCM, 16-bit mono), then the samples, little-endian as the format requires.
    private static void WriteWav(string path, short[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        int data = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (short s in pcm) w.Write(s);
    }
}
