using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser.Views;

/// <summary>
/// The treat he drags from the bar to the companion (SPEC-round2 2.9; since SPEC-round6 2.6 the cucumber slice
/// for the birds, the bone for Rori). Owned by the note so it stays above it; click-through and non-activating,
/// so it can sit under the cursor without taking the mouse (the bar holds the capture) or the keyboard (the
/// caret stays in the note).
/// </summary>
public partial class CucumberWindow : Window
{
    private const double Half = 14;          // the treat's square is 28 DIP; the cursor is its centre
    private const double CucumberDip = 20;
    // 2 DIP per cell as on the bar: a whole number keeps the pixel cells even (2 px at 100 %, 3 px at 150 %),
    // and the 10 by 5 bone (20 by 10) is as wide as the carried cucumber.
    private const double TreatCell = 2;
    private const double FallDip = 20;
    private const int FallMs = 200;

    // Bumped by every show and hide; a fall that finishes after a newer drag started must not hide it.
    private int _gen;
    private bool _falling;
    // The rows on show (null = the cucumber); a drag of the same treat renders nothing.
    private string[]? _treatRows;
    private bool _treatSet;

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

    /// <summary>The treat to carry: a sprite glyph, or null for the cucumber drawing; centred on the cursor either way.</summary>
    public void SetTreat(string[]? rows)
    {
        if (_treatSet && ReferenceEquals(rows, _treatRows)) return;
        _treatSet = true;
        _treatRows = rows;
        Size size;
        if (rows is null)
        {
            Slice.Source = (ImageSource)FindResource("TF.CucumberSlice");
            size = new Size(CucumberDip, CucumberDip);
        }
        else
        {
            Slice.Source = SpriteImage.Render(rows, TreatCell);
            size = SpriteImage.SizeOf(rows, TreatCell);
        }
        Slice.Width = size.Width;
        Slice.Height = size.Height;
        Canvas.SetLeft(Slice, Half - size.Width / 2);
        Canvas.SetTop(Slice, Half - size.Height / 2);
    }

    /// <summary>Shows the treat centred on <paramref name="cursor"/> (screen DIPs), or moves it there.</summary>
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

    /// <summary>The release: the treat drops a little and fades (200 ms), then the window hides.</summary>
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
