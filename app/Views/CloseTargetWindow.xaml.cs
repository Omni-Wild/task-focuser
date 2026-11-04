using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser.Views;

/// <summary>
/// The bottom-right disc that closes the dot (SPEC 3.7). Shown only while the dot is dragged;
/// click-through and non-activating, so it can sit under the cursor without stealing anything.
/// </summary>
public partial class CloseTargetWindow : Window, ICloseTarget
{
    private const double Half = 40;          // half the window side: Centre to Left/Top
    private const double HitRadius = 44;
    private const double HotScale = 48.0 / 40.0;

    // 90 % alpha is in the colour itself, so the swell and the fade never fight over Opacity.
    private static readonly Color Idle = Color.FromArgb(0xE6, 0x3A, 0x3A, 0x3A);
    private static readonly Color Hot = Color.FromArgb(0xE6, 0xD2, 0x3B, 0x3B);

    private bool _hot;
    private bool _shown;

    // Bumped by every show and hide; a fade-out that finishes after a newer ShowAt must not hide the window.
    private int _gen;

    public CloseTargetWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Win32.SetToolWindow(this);
            Win32.SetNoActivate(this, true);
            Win32.SetClickThrough(this, true);
        };
    }

    public Point Centre { get; private set; }

    public bool Contains(Point p) => (p - Centre).Length <= HitRadius;

    public void ShowAt(Point centre)
    {
        ++_gen;
        Centre = centre;
        Left = centre.X - Half;
        Top = centre.Y - Half;

        if (!IsVisible)
        {
            // A fresh appearance always starts cold and transparent.
            StopAll();
            Body.Opacity = 0;
            Swell.ScaleX = Swell.ScaleY = 1;
            DiscBrush.Color = Idle;
            _hot = false;
            Show();
        }

        // Already fading in or fully shown: this only moved it (the dot crossed to another monitor).
        if (_shown) return;
        _shown = true;
        Body.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, Ms(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } },
            HandoffBehavior.SnapshotAndReplace);
    }

    public void SetHot(bool hot)
    {
        if (!IsVisible || hot == _hot) return;
        _hot = hot;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new DoubleAnimation(hot ? HotScale : 1.0, Ms(120)) { EasingFunction = ease };
        Swell.BeginAnimation(ScaleTransform.ScaleXProperty, scale, HandoffBehavior.SnapshotAndReplace);
        Swell.BeginAnimation(ScaleTransform.ScaleYProperty, scale, HandoffBehavior.SnapshotAndReplace);
        DiscBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(hot ? Hot : Idle, Ms(120)) { EasingFunction = ease },
            HandoffBehavior.SnapshotAndReplace);
    }

    public void HideTarget()
    {
        if (!IsVisible) return;
        int gen = ++_gen;
        _shown = false;
        var fade = new DoubleAnimation(0.0, Ms(120)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (gen != _gen) return;
            StopAll();
            Hide();
        };
        Body.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Hidden means nothing runs: drop every clock, keeping the values where they were.</summary>
    private void StopAll()
    {
        double opacity = Body.Opacity, scale = Swell.ScaleX;
        Color color = DiscBrush.Color;
        Body.BeginAnimation(OpacityProperty, null);
        Swell.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        Swell.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DiscBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        Body.Opacity = opacity;
        Swell.ScaleX = Swell.ScaleY = scale;
        DiscBrush.Color = color;
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
}
