using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using TaskFocuser.Core;
using TaskFocuser.Monster;

namespace TaskFocuser.Views;

/// <summary>
/// The unlocked note's top bar (SPEC 3.2, SPEC-round2 2.11, one row again since SPEC-round3 2.1). Writes
/// straight into the shared <see cref="NoteState"/> and raises <see cref="StateChanged"/> (the look) or
/// <see cref="SettingChanged"/> (the bird and the sound); the note window re-applies what it must and saves.
/// </summary>
public partial class SettingsBar : UserControl
{
    // One preview peep per this many ms while the volume slider moves (SPEC-round2 2.5).
    private const int PeepGapMs = 150;
    // The volume popup: a hover this long opens it, and it closes this long after the mouse left both the
    // button and the popup (SPEC-round3 2.1). The close delay is also what carries the mouse across the
    // few DIP between the button and the popup.
    private const int PopupOpenMs = 250, PopupCloseMs = 300;
    private const double ChoiceDim = 0.6;

    private static readonly string[] SoundLabels = { "chirp", "brrrp", "both" };
    private static readonly IdleSounds[] SoundValues = { IdleSounds.Chirp, IdleSounds.Ruffle, IdleSounds.Both };
    private static readonly string[] RateLabels = { "often", "mid", "rarely" };
    private static readonly IdleRate[] RateValues = { IdleRate.Often, IdleRate.Mid, IdleRate.Rarely };

    private NoteState? _state;
    private bool _refreshing;
    private long _lastPeep = long.MinValue / 2;
    private DispatcherTimer? _peepTrail;
    private readonly DispatcherTimer _popupOpen, _popupClose;

    /// <summary>A field of the look was written by the bar (opacity, colour, font, size, plants).</summary>
    public event Action? StateChanged;
    /// <summary>A field that changes no pixel of the note was written (BirdStays, BirdOpaque, SoundOn, Volume, IdleSounds, IdleRate).</summary>
    public event Action? SettingChanged;
    public event Action? LockRequested;
    /// <summary>A rename in the picker ended with Enter or Esc: the note puts the caret back in its text.</summary>
    public event Action? EditorFocusWanted;

    public SettingsBar()
    {
        InitializeComponent();

        var swatchStyle = (Style)FindResource("TF.Swatch");
        foreach (var s in Palette.Colors)
        {
            var c = Palette.Parse(s.Hex);
            var rb = new RadioButton
            {
                Style = swatchStyle,
                Background = Frozen(c),
                BorderBrush = Frozen(Palette.Darken(c, 0.30)),
                Tag = s.Hex,
                ToolTip = s.Name,
            };
            rb.Checked += OnSwatchChecked;
            Swatches.Children.Add(rb);
        }

        OpacitySlider.ValueChanged += OnOpacityChanged;
        FontButton.Click += (_, _) => CycleFont(+1);
        // Right-click steps back, so overshooting by one never costs four more clicks.
        FontButton.MouseRightButtonUp += (_, e) => { CycleFont(-1); e.Handled = true; };
        SmallerButton.Click += (_, _) => StepFontSize(-1);
        BiggerButton.Click += (_, _) => StepFontSize(+1);
        LockButton.Click += (_, _) => LockRequested?.Invoke();

        // Click, not Checked: it fires only for his clicks, never for Refresh setting IsChecked.
        StaysToggle.Click += (_, _) => Toggle(s => s.BirdStays = StaysToggle.IsChecked == true);
        OpaqueToggle.Click += (_, _) => Toggle(s => s.BirdOpaque = OpaqueToggle.IsChecked == true);
        SoundToggle.Click += (_, _) => Toggle(s => s.SoundOn = SoundToggle.IsChecked == true);
        // The plants are part of the look: the note re-applies it and the vines fold or grow.
        PlantsToggle.Click += (_, _) => ChangeLook(s => s.PlantsOn = PlantsToggle.IsChecked == true);
        VolumeSlider.ValueChanged += OnVolumeChanged;

        BuildChoices(SoundChoices, SoundLabels, i => Toggle(s => s.IdleSounds = SoundValues[i]));
        BuildChoices(RateChoices, RateLabels, i => Toggle(s => s.IdleRate = RateValues[i]));

        _popupOpen = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(PopupOpenMs) };
        _popupClose = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(PopupCloseMs) };
        _popupOpen.Tick += (_, _) => OnPopupOpenTick();
        _popupClose.Tick += (_, _) => OnPopupCloseTick();
        WirePopup();
        WirePicker();
    }

    /// <summary>The cucumber slice's grab handle; the note runs the drag because it owns the screen coordinates.</summary>
    public FrameworkElement CucumberHandle => Cucumber;

    public void Attach(NoteState state)
    {
        _state = state;
        Picker.Attach(state);
        Refresh();
    }

    /// <summary>Re-reads the state into the controls (after a change made elsewhere, e.g. Ctrl+=).</summary>
    public void Refresh()
    {
        if (_state is null) return;
        _refreshing = true;
        try
        {
            OpacitySlider.Value = _state.Opacity;
            foreach (RadioButton rb in Swatches.Children)
                rb.IsChecked = string.Equals((string)rb.Tag, _state.Color, StringComparison.OrdinalIgnoreCase);

            int i = Math.Max(0, Array.IndexOf(Palette.Fonts, _state.Font));
            FontButton.ToolTip = $"Font: {Palette.Fonts[i]} (click for the next, right-click for the previous)";

            SmallerButton.IsEnabled = _state.FontSize > Palette.MinFontSize;
            BiggerButton.IsEnabled = _state.FontSize < Palette.MaxFontSize;

            StaysToggle.IsChecked = _state.BirdStays;
            OpaqueToggle.IsChecked = _state.BirdOpaque;
            SoundToggle.IsChecked = _state.SoundOn;
            PlantsToggle.IsChecked = _state.PlantsOn;
            VolumeSlider.Value = Sound.VolumeToSlider(_state.Volume);
            ApplyToggleGlyphs();
            ApplyPopupColours();
            RefreshName();
            Picker.Refresh();
        }
        finally { _refreshing = false; }
    }

    /// <summary>Changes the font size by <paramref name="delta"/> within the limits; shared with the note's shortcuts.</summary>
    public void StepFontSize(int delta)
    {
        if (_state is null) return;
        int next = Math.Clamp(_state.FontSize + delta, Palette.MinFontSize, Palette.MaxFontSize);
        if (next == _state.FontSize) return;
        _state.FontSize = next;
        Refresh();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// True when <paramref name="source"/> (a press's original source) lies on one of the bar's controls, on
    /// the slice, in the volume popup or in the bird picker: such a press belongs to the control and is never half of a bar
    /// double-click. The popup's tree has no visual parent, so its border is checked by name.
    /// </summary>
    public bool IsOnControl(DependencyObject? source)
    {
        for (var d = source; d is not null && !ReferenceEquals(d, this); d = ParentOf(d))
        {
            if (d is ButtonBase or Slider or Thumb || ReferenceEquals(d, Cucumber) || ReferenceEquals(d, VolumeBox)
                || ReferenceEquals(d, Picker)) return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private void CycleFont(int step)
    {
        if (_state is null) return;
        int n = Palette.Fonts.Length;
        int i = Math.Max(0, Array.IndexOf(Palette.Fonts, _state.Font));
        _state.Font = Palette.Fonts[((i + step) % n + n) % n];
        Refresh();
        StateChanged?.Invoke();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || _state is null) return;
        int v = (int)Math.Round(e.NewValue);
        if (v == _state.Opacity) return;
        _state.Opacity = v;
        StateChanged?.Invoke();
    }

    private void OnSwatchChecked(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _state is null) return;
        var hex = (string)((RadioButton)sender).Tag;
        if (string.Equals(hex, _state.Color, StringComparison.OrdinalIgnoreCase)) return;
        _state.Color = hex;
        ApplyPopupColours();
        StateChanged?.Invoke();
    }

    // ---- the toggles ------------------------------------------------------------------------------

    private void Toggle(Action<NoteState> write)
    {
        if (_state is null) return;
        write(_state);
        ApplyToggleGlyphs();
        SettingChanged?.Invoke();
    }

    private void ChangeLook(Action<NoteState> write)
    {
        if (_state is null) return;
        write(_state);
        ApplyToggleGlyphs();
        StateChanged?.Invoke();
    }

    /// <summary>Each toggle shows its off glyph or its on glyph; the 70 % / 100 % ink is the style's.</summary>
    private void ApplyToggleGlyphs()
    {
        if (_state is null) return;
        Show(StaysOn, StaysOff, _state.BirdStays);
        Show(OpaqueOn, OpaqueOff, _state.BirdOpaque);
        Show(SoundOnWaves, SoundOffCross, _state.SoundOn);
        Show(PlantsOnGlyph, PlantsOffGlyph, _state.PlantsOn);
        // Muted, the slider still works but reads as resting, so the mute shows on the slider too.
        VolumeSlider.Opacity = _state.SoundOn ? 1.0 : 0.45;
        MarkChoice(SoundChoices, Array.IndexOf(SoundValues, _state.IdleSounds));
        MarkChoice(RateChoices, Array.IndexOf(RateValues, _state.IdleRate));
    }

    private static void Show(UIElement on, UIElement off, bool isOn)
    {
        on.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        off.Visibility = isOn ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- the volume popup ---------------------------------------------------------------------------

    /// <summary>One row of text choices; each is a non-focusable button whose click writes the state.</summary>
    private void BuildChoices(Panel host, string[] labels, Action<int> pick)
    {
        // Looked up from the bar, not the host: the popup's content has no visual parent until it opens.
        var style = (Style)FindResource("TF.Choice");
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var b = new Button { Style = style, Content = new TextBlock { Text = labels[i] } };
            b.Click += (_, _) => pick(index);
            host.Children.Add(b);
        }
    }

    /// <summary>The chosen one underlined at full ink, the others at 60 %: readable as a choice without colour.</summary>
    private static void MarkChoice(Panel host, int chosen)
    {
        for (int i = 0; i < host.Children.Count; i++)
        {
            var b = (Button)host.Children[i];
            bool on = i == chosen;
            b.Opacity = on ? 1.0 : ChoiceDim;
            if (b.Content is TextBlock t) t.TextDecorations = on ? TextDecorations.Underline : null;
        }
    }

    /// <summary>Both popups are scraps of the same paper: the note's colour, edged with the strip's.</summary>
    private void ApplyPopupColours()
    {
        if (_state is null) return;
        var paper = Frozen(Palette.Parse(_state.Color));
        var edge = Frozen(Palette.StripOf(_state.Color));
        VolumeBox.Background = paper;
        VolumeBox.BorderBrush = edge;
        Picker.SetColours(paper, edge);
    }

    private void WirePopup()
    {
        SoundToggle.MouseEnter += (_, _) =>
        {
            _popupClose.Stop();
            if (!VolumePopup.IsOpen) _popupOpen.Start();
        };
        SoundToggle.MouseLeave += (_, _) =>
        {
            _popupOpen.Stop();
            if (VolumePopup.IsOpen) Restart(_popupClose);
        };
        VolumeBox.MouseEnter += (_, _) => _popupClose.Stop();
        VolumeBox.MouseLeave += (_, _) => Restart(_popupClose);
        // A slider drag that ends outside the popup: the leave came while the thumb held the mouse, so the
        // close is armed again once the capture is let go.
        VolumeBox.LostMouseCapture += (_, _) => { if (!VolumeBox.IsMouseOver) Restart(_popupClose); };
        // A press on the popup's padding is the popup's: it must not reach the note's press handling, which
        // would read it as a click below the bar and move the caret.
        VolumeBox.MouseLeftButtonDown += (_, e) => e.Handled = true;
        // Hidden (the note hid or squashed into the dot) or folded away: the popup goes with the bar.
        IsVisibleChanged += (_, _) => { if (!IsVisible) ClosePopups(); };
    }

    private void OnPopupOpenTick()
    {
        _popupOpen.Stop();
        // Only for a bar that is really there and still under the mouse; a hover that began as the bar
        // folded away never opens it.
        // Nor over an open bird picker: the two would overlap, and he is busy with the picker.
        if (!IsVisible || _state is null || _state.Locked || !SoundToggle.IsMouseOver || PickerPopup.IsOpen) return;
        VolumePopup.IsOpen = true;
    }

    private void OnPopupCloseTick()
    {
        _popupClose.Stop();
        // Back over either, or still dragging the volume thumb: stay open; their leave events re-arm the close.
        if (SoundToggle.IsMouseOver || VolumeBox.IsMouseOver || VolumeBox.IsMouseCaptureWithin) return;
        VolumePopup.IsOpen = false;
    }

    /// <summary>Closes the volume popup at once (lock, hide, a note drag); safe when it is not open.</summary>
    public void CloseVolumePopup()
    {
        _popupOpen.Stop();
        _popupClose.Stop();
        if (VolumeBox.IsMouseCaptureWithin) Mouse.Capture(null);
        VolumePopup.IsOpen = false;
    }

    // ---- the bird's name and the picker (SPEC-round5 2.3) -----------------------------------------------

    /// <summary>True while a bird's name is in its rename box: the note's Esc, Ctrl+L and Ctrl+= stay off those keys.</summary>
    public bool IsRenaming => Picker.IsEditing;

    private void WirePicker()
    {
        NameButton.Click += (_, _) => { if (PickerPopup.IsOpen) ClosePicker(); else OpenPicker(); };
        // The conductor swaps the birds on the note's Changed, which SettingChanged raises.
        Picker.Chosen += () => { ClosePicker(); RefreshName(); SettingChanged?.Invoke(); };
        Picker.Renamed += () => { RefreshName(); SettingChanged?.Invoke(); };
        Picker.EditFinished += () => EditorFocusWanted?.Invoke();
        // The box lost the keyboard, maybe to another app: once that settles, a note not in front closes it.
        Picker.EditBlurred += CloseIfFocusLeftSoon;
    }

    private void OpenPicker()
    {
        if (!IsVisible || _state is null || _state.Locked) return;
        CloseVolumePopup();
        Picker.Refresh();
        PickerPopup.IsOpen = true;
    }

    /// <summary>Closes the picker; a rename still open keeps a valid name and drops anything else.</summary>
    public void ClosePicker() { Picker.EndEdit(); PickerPopup.IsOpen = false; }

    /// <summary>Both popups at once: lock, hide, a note drag, the grip, the squash.</summary>
    public void ClosePopups() { CloseVolumePopup(); ClosePicker(); }

    /// <summary>Every press on the note passes here first: one outside the picker and the name (which toggles by itself) closes it.</summary>
    public void NotePressed(DependencyObject? source)
    {
        if (!PickerPopup.IsOpen) return;
        for (var d = source; d is not null; d = ParentOf(d))
            if (ReferenceEquals(d, Picker) || ReferenceEquals(d, NameButton)) return;
        ClosePicker();
    }

    /// <summary>The note lost the foreground. Deferred: the rename box taking the keyboard deactivates it too (the popup is its own window).</summary>
    public void CloseIfFocusLeftSoon() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (PickerPopup.IsOpen && !Picker.IsKeyboardFocusWithin && Window.GetWindow(this) is not { IsActive: true })
            ClosePicker();
    });

    /// <summary>The active bird's name in the slot; beyond 12 characters it ends in an ellipsis.</summary>
    private void RefreshName()
    {
        if (_state is null) return;
        string name = BirdPicker.NameOf(_state, Birds.ById(_state.Bird));
        NameButton.Content = name.Length > 12 ? name[..11] + (char)0x2026 : name;
    }

    private static void Restart(DispatcherTimer t)
    {
        t.Stop();
        t.Start();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || _state is null) return;
        // Three decimals is finer than the ear and keeps state.json tidy.
        double v = Math.Round(Math.Clamp(Sound.SliderToVolume(e.NewValue), 0, 1), 3);
        if (v == _state.Volume) return;
        _state.Volume = v;
        SettingChanged?.Invoke();
        PeepThrottled();
    }

    /// <summary>
    /// He hears what he sets: a peep at once, then at most one per <see cref="PeepGapMs"/>, plus a trailing
    /// one so the value he lets go at is always the last one heard.
    /// </summary>
    private void PeepThrottled()
    {
        if (_state is not { SoundOn: true }) return;
        long wait = PeepGapMs - (Environment.TickCount64 - _lastPeep);
        if (wait <= 0)
        {
            Peep();
            return;
        }
        if (_peepTrail is null)
        {
            _peepTrail = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _peepTrail.Tick += (_, _) => { _peepTrail.Stop(); Peep(); };
        }
        if (_peepTrail.IsEnabled) return;
        _peepTrail.Interval = TimeSpan.FromMilliseconds(wait);
        _peepTrail.Start();
    }

    private void Peep()
    {
        _lastPeep = Environment.TickCount64;
        // The active bird's own peep (SPEC-round5 2.3): Meggie answers with her soft "hu".
        if (_state is { SoundOn: true } s) Sound.Play(ChirpKind.Peep, s.Volume, s.Bird);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
