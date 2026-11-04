using System.Windows;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser;

// The entry point. WPF generates Main from App.xaml; early setup goes in the static constructor.
// The state machine itself is Conductor.cs.
public partial class App : Application
{
    static App()
    {
        // Let storyboards run at the monitor's refresh rate instead of WPF's 60 fps default.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata(165));
    }

    Conductor? conductor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Built before the mutex check so a forwarded command line always has somewhere to land.
        var candidate = new Conductor(this);
        if (!SingleInstance.TryBecomePrimary(candidate.OnForwardedArgs))
        {
            SingleInstance.Forward(e.Args);
            Shutdown();
            return;
        }
        conductor = candidate;
        DispatcherUnhandledException += conductor.OnUnhandledException;
        try { conductor.Start(e.Args); }
        catch (Exception ex)
        {
            // The handler above would swallow this and leave a process with no window, maybe no tray, that still
            // holds the single-instance mutex: every later launch is forwarded into it and lost. Say so and exit.
            MessageBox.Show($"Task Focuser could not start.\n\n{ex.GetType().Name}: {ex.Message}",
                "Task Focuser", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only the primary flushes: a secondary never loaded the state, and its defaults would wipe the note.
        conductor?.Teardown();
        base.OnExit(e);
    }
}
