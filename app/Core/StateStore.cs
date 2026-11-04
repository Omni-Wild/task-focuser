using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace TaskFocuser.Core;

/// <summary>
/// Loads and saves <see cref="NoteState"/> as %APPDATA%\Task Focuser\state.json (SPEC 3.9).
/// Reading is key by key and forgiving about types, so the old Python app's file (text, x, y, color,
/// opacity, locked) loads unchanged and one odd value never costs him the note text.
/// </summary>
public static class StateStore
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Task Focuser", 
        // A test profile (TASKFOCUSER_PROFILE, see SingleInstance) keeps its own file beside the real one.
        "state" + SingleInstance.Profile + ".json");

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly object WriteLock = new();

    private static DispatcherTimer? _timer;
    private static NoteState? _pending;
    // Load fell back to defaults without having read the file (a lock, a share or cloud folder not up yet; File.Exists
    // is false on any error too): the first write copies whatever is on disk aside before it replaces it.
    private static bool _keepCopyBeforeWrite;

    public static NoteState Load()
    {
        string path = Path;
        try
        {
            if (!File.Exists(path))
            {
                var fresh = new NoteState();
                fresh.Normalize();
                _keepCopyBeforeWrite = true;   // a true first run finds nothing to copy and clears it
                Write(fresh, path);
                return fresh;
            }

            string json = ReadWithRetry(path);
            var state = Parse(json);
            state.Normalize();
            return state;
        }
        catch (IOException e)
        {
            // A transient lock (the old app or an editor mid-write): never treat it as corruption, and
            // never write defaults over a file that is probably fine.
            Debug.WriteLine($"StateStore.Load: {e.Message}");
            _keepCopyBeforeWrite = true;   // the conductor's first Commit would otherwise save these defaults over it
            var fresh = new NoteState();
            fresh.Normalize();
            return fresh;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"StateStore.Load: {e.Message}");
            // Keep the unreadable file beside the new one: it may still hold text he wants back.
            TryBackup(path);
            var fresh = new NoteState();
            fresh.Normalize();
            Write(fresh, path);
            return fresh;
        }
    }

    static string ReadWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.ReadAllText(path, Encoding.UTF8); }
            catch (IOException) when (attempt < 4) { Thread.Sleep(50); }
        }
    }

    /// <summary>Writes 300 ms after the last call, on the WPF dispatcher.</summary>
    public static void Save(NoteState state)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => Save(state));
                return;
            }

            _pending = state;
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Debounce };
                _timer.Tick += OnTick;
            }
            _timer.Stop(); // restarting is what makes it a debounce, not a throttle
            _timer.Start();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"StateStore.Save: {e.Message}");
        }
    }

    /// <summary>Writes now and cancels any pending debounced write (quit, crash-safe points).</summary>
    public static void Flush(NoteState state)
    {
        try
        {
            _timer?.Stop();
            _pending = null;
            Write(state, Path);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"StateStore.Flush: {e.Message}");
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        var state = _pending;
        _pending = null;
        if (state is not null) Write(state, Path);
    }

    private static void Write(NoteState state, string path)
    {
        try
        {
            state.Normalize();
            byte[] bytes = Serialize(state);
            lock (WriteLock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                if (_keepCopyBeforeWrite)
                {
                    // Throws while the file is still unreadable, so nothing replaces it until a copy exists.
                    if (File.Exists(path))
                        File.Copy(path, path + ".unread-" + DateTime.Now.ToString("yyyyMMdd-HHmmss",
                            System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
                    _keepCopyBeforeWrite = false;
                }
                // A scanner or the indexer can hold the target for a moment; a short retry beats a lost save.
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Move(tmp, path, overwrite: true); break; }
                    catch (IOException) when (attempt < 3) { Thread.Sleep(25); }
                    catch (UnauthorizedAccessException) when (attempt < 3) { Thread.Sleep(25); }
                }
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"StateStore.Write: {e.Message}");
        }
    }

    private static void TryBackup(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true);
        }
        catch { /* best effort */ }
    }

    // ---- format ----

    internal static byte[] Serialize(NoteState s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Indented = true,
            // Keeps Czech diacritics readable in the file instead of escaped code points.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", s.Version);
            w.WriteString("text", s.Text);
            w.WriteNumber("x", Math.Round(s.X, 2));
            w.WriteNumber("y", Math.Round(s.Y, 2));
            w.WriteString("color", s.Color);
            w.WriteNumber("opacity", s.Opacity);
            w.WriteBoolean("locked", s.Locked);
            w.WriteString("font", s.Font);
            w.WriteNumber("fontSize", s.FontSize);
            w.WriteString("mode", s.Mode.ToString().ToLowerInvariant());
            // JSON has no NaN; an unplaced dot is null, and reading null gives NaN back.
            WriteMaybe(w, "dotX", s.DotX);
            WriteMaybe(w, "dotY", s.DotY);
            w.WriteBoolean("startWithWindows", s.StartWithWindows);
            // Round 2: the bird and the sound.
            w.WriteBoolean("birdStays", s.BirdStays);
            w.WriteBoolean("birdOpaque", s.BirdOpaque);
            w.WriteBoolean("soundOn", s.SoundOn);
            w.WriteNumber("volume", Math.Round(s.Volume, 3));
            // Round 3: the idle sounds and the plants.
            w.WriteString("idleSounds", s.IdleSounds.ToString().ToLowerInvariant());
            w.WriteString("idleRate", s.IdleRate.ToString().ToLowerInvariant());
            w.WriteBoolean("plantsOn", s.PlantsOn);
            // Round 4: the corner grip.
            WriteMaybe(w, "manualWidth", s.ManualWidth);
            WriteMaybe(w, "manualHeight", s.ManualHeight);
            // Round 5: the birds.
            w.WriteString("bird", s.Bird);
            w.WriteStartObject("birdNames");
            foreach (var (id, name) in s.BirdNames) w.WriteString(id, name);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void WriteMaybe(Utf8JsonWriter w, string name, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) w.WriteNull(name);
        else w.WriteNumber(name, Math.Round(v, 2));
    }

    /// <summary>Throws only when the file is not a JSON object at all; a bad single value falls back to its default.</summary>
    internal static NoteState Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("state.json is not an object");

        var s = new NoteState();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var v = p.Value;
            switch (p.Name.ToLowerInvariant())
            {
                case "version": if (Num(v) is double ver) s.Version = (int)ver; break;
                case "text": if (v.ValueKind == JsonValueKind.String) s.Text = v.GetString() ?? ""; break;
                case "x": if (Num(v) is double x) s.X = x; break;
                case "y": if (Num(v) is double y) s.Y = y; break;
                case "color": if (v.ValueKind == JsonValueKind.String) s.Color = v.GetString() ?? s.Color; break;
                case "opacity":
                    if (Num(v) is double o)
                        // A fraction (0..1) would be another writer's convention; read it as a share of 100.
                        s.Opacity = (int)Math.Round(o is > 0 and <= 1 && v.GetRawText().Contains('.') ? o * 100 : o);
                    break;
                case "locked": if (Bool(v) is bool l) s.Locked = l; break;
                case "font": if (v.ValueKind == JsonValueKind.String) s.Font = v.GetString() ?? s.Font; break;
                case "fontsize": if (Num(v) is double fs) s.FontSize = (int)Math.Round(fs); break;
                case "mode": if (ModeOf(v) is Mode m) s.Mode = m; break;
                case "dotx": s.DotX = Num(v) ?? double.NaN; break;
                case "doty": s.DotY = Num(v) ?? double.NaN; break;
                case "startwithwindows": if (Bool(v) is bool sw) s.StartWithWindows = sw; break;
                case "birdstays": if (Bool(v) is bool bs) s.BirdStays = bs; break;
                case "birdopaque": if (Bool(v) is bool bo) s.BirdOpaque = bo; break;
                case "soundon": if (Bool(v) is bool so) s.SoundOn = so; break;
                case "volume": if (Num(v) is double vol) s.Volume = vol; break;
                case "idlesounds": if (v.ValueKind == JsonValueKind.String && Enum.TryParse(v.GetString(), ignoreCase: true, out IdleSounds isnd)) s.IdleSounds = isnd; break;
                case "idlerate": if (v.ValueKind == JsonValueKind.String && Enum.TryParse(v.GetString(), ignoreCase: true, out IdleRate irt)) s.IdleRate = irt; break;
                case "plantson": if (Bool(v) is bool po) s.PlantsOn = po; break;
                case "manualwidth": s.ManualWidth = Num(v) ?? double.NaN; break;
                case "manualheight": s.ManualHeight = Num(v) ?? double.NaN; break;
                case "bird": if (v.ValueKind == JsonValueKind.String) s.Bird = v.GetString() ?? "lennie"; break;
                case "birdnames":
                    if (v.ValueKind == JsonValueKind.Object)
                        foreach (var bp in v.EnumerateObject())
                            if (bp.Value.ValueKind == JsonValueKind.String && bp.Value.GetString() is string nm && nm.Trim().Length >= 3)
                                s.BirdNames[bp.Name] = nm.Trim();
                    break;
            }
        }
        return s;
    }

    private static double? Num(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;
            case JsonValueKind.String:
                return double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double sd) && double.IsFinite(sd) ? sd : null;
            default:
                return null;
        }
    }

    private static bool? Bool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetDouble(out double d) ? d != 0 : null,
        JsonValueKind.String => bool.TryParse(v.GetString(), out bool b) ? b : null,
        _ => null,
    };

    private static Mode? ModeOf(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String && Enum.TryParse(v.GetString(), ignoreCase: true, out Mode m)
            && Enum.IsDefined(m))
            return m;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) && Enum.IsDefined((Mode)i))
            return (Mode)i;
        return null;
    }
}
