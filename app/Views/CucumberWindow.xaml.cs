using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser.Views;

/// <summary>
/// The cucumber slice he drags from the bar to the bird (SPEC-round2 2.9). Owned by the note so it stays
/// above it; click-through and non-activating, so it can sit under the cursor without taking the mouse
/// (the bar holds the capture) or the keyboard (the caret stays in the note).
/// </summary>
public partial class CucumberWindow : Window
{
    private const double Half = 14;          // the slice's square is 28 DIP; the cursor is its centre
    private const double FallDip = 20;
    private const int FallMs = 200;

    // Bumped by every show and hide; a fall that finishes after a newer drag started must not hide it.
    private int _gen;
    private bool _falling;

    public CucumberWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Win32.SetToolWindow(this);
            Win32.SetNoActivate(this, true);
            Win32.SetClickThrough(this, true);
        };
    }

    /// <summary>Shows the slice centred on <paramref name="cursor"/> (screen DIPs), or moves it there.</summary>
    public void ShowAt(Point cursor)
    {
        if (_falling || !IsVisible)
        {
            ++_gen;
            Reset();
        }
        Left = cursor.X - Half;
        Top = cursor.Y - Half;
        if (!IsVisible) Show();
    }

    /// <summary>The release: the slice drops a little and fades (200 ms), then the window hides.</summary>
    public void FallAway()
    {
        if (!IsVisible) return;
        int gen = ++_gen;
        _falling = true;
        var dur = TimeSpan.FromMilliseconds(FallMs);
        var fall = new DoubleAnimation(0, FallDip, dur) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var fade = new DoubleAnimation(1, 0, dur);
        fade.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            Hide();
            Reset();
        };
        Fall.BeginAnimation(TranslateTransform.YProperty, fall);
        Slice.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Gone at once, no fall (the drag was cancelled: lock, hide, squash).</summary>
    public void HideNow()
    {
        ++_gen;
        if (IsVisible) Hide();
        Reset();
    }

    private void Reset()
    {
        _falling = false;
        Fall.BeginAnimation(TranslateTransform.YProperty, null);
        Fall.Y = 0;
        Slice.BeginAnimation(OpacityProperty, null);
        Slice.Opacity = 1;
    }
}
