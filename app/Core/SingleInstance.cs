using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace TaskFocuser.Core;

/// <summary>
/// One running copy (SPEC 3.10). The first launch holds a named mutex and listens on a pipe; a later launch
/// sends its arguments there and exits, so scripts and the tests can drive the running note.
/// </summary>
public static class SingleInstance
{
    // TASKFOCUSER_PROFILE=<name> runs a separate copy with its own mutex, pipe and state file (the release tests
    // drive one beside the real note); unset for everyone else.
    internal static readonly string Profile = ProfileSuffix();
    private static readonly string MutexName = @"Local\TaskFocuser.v2.instance" + Profile;
    // Pipe names are machine-wide while the mutex is per session, so the pipe is per session too: otherwise a
    // second signed-in user's copy can never create its pipe (a failing retry every 250 ms for good) and a
    // second session of the same user forwards its launches to the other session's note.
    private static readonly string PipeName = $"TaskFocuser.v2.pipe.{Process.GetCurrentProcess().SessionId}{Profile}";
    private const int MaxMessageBytes = 64 * 1024;

    // Held in a static field for the life of the process: a collected mutex would release the claim.
    private static Mutex? _mutex;
    private static CancellationTokenSource? _cts;

    /// <summary>"" normally; ".<name>" when TASKFOCUSER_PROFILE names a test profile (letters, digits, - and _ only).</summary>
    private static string ProfileSuffix()
    {
        string? p = Environment.GetEnvironmentVariable("TASKFOCUSER_PROFILE");
        if (string.IsNullOrWhiteSpace(p)) return "";
        p = p.Trim();
        foreach (char c in p) if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return "";
        return "." + p;
    }

    /// <summary>
    /// True when this process is the primary; it then starts listening and calls <paramref name="onForwardedArgs"/>
    /// on the WPF dispatcher for each forwarded command line. False means another instance runs: call
    /// <see cref="Forward"/> and exit.
    /// </summary>
    public static bool TryBecomePrimary(Action<string[]> onForwardedArgs)
    {
        if (_mutex is not null) return true;
        try
        {
            var m = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                bool owned;
                // The other instance may be quitting right now, or have died holding the mutex.
                try { owned = m.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) { m.Dispose(); return false; }
            }
            _mutex = m;
        }
        catch (Exception e)
        {
            // Without a mutex we cannot know; running a possible second copy beats not starting at all.
            Debug.WriteLine($"SingleInstance mutex: {e.Message}");
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _cts.Cancel(); } catch { } };
        // Thread-pool threads are background threads, so the listener never keeps the process alive.
        Task.Run(() => ServeAsync(onForwardedArgs, dispatcher, token));
        return true;
    }

    /// <summary>Sends <paramref name="args"/> to the primary. Gives up after about a second; never throws.</summary>
    public static void Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1000);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(args ?? Array.Empty<string>());
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            try { client.WaitForPipeDrain(); } catch { /* the server may already have closed its end */ }
        }
        catch (Exception e)
        {
            Debug.WriteLine($"SingleInstance.Forward: {e.Message}");
        }
    }

    private static async Task ServeAsync(Action<string[]> onArgs, Dispatcher fallbackDispatcher, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                // A client that connects and then stalls must not block the next launch forever.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                readCts.CancelAfter(TimeSpan.FromSeconds(2));
                string message = await ReadAllAsync(server, readCts.Token).ConfigureAwait(false);

                string[] args = Decode(message);
                var dispatcher = System.Windows.Application.Current?.Dispatcher ?? fallbackDispatcher;
                if (!dispatcher.HasShutdownStarted)
                    _ = dispatcher.BeginInvoke(() =>
                    {
                        try { onArgs(args); }
                        catch (Exception e) { Debug.WriteLine($"SingleInstance callback: {e}"); }
                    });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // One broken connection must not end the loop; the pause stops a tight spin if the pipe
                // keeps failing (for example a name clash with a stale instance).
                Debug.WriteLine($"SingleInstance server: {e.Message}");
                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static async Task<string> ReadAllAsync(Stream s, CancellationToken token)
    {
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await s.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxMessageBytes) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>A JSON string array, or, from a hand-rolled sender, one argument per line.</summary>
    internal static string[] Decode(string message)
    {
        string trimmed = message.Trim();
        if (trimmed.Length == 0) return Array.Empty<string>();
        if (trimmed.StartsWith('['))
        {
            try { return JsonSerializer.Deserialize<string[]>(trimmed) ?? Array.Empty<string>(); }
            catch (JsonException) { /* fall back to lines */ }
        }
        return trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
