using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TaskFocuser.Core;

namespace TaskFocuser.Views;

/// <summary>
/// The post-it (SPEC 3.1 to 3.4). This file: the contract surface, lock and unlock, the mouse and keys.
/// Sizing and placement live in NoteWindow.Layout.cs, the show/pop/squash motions in NoteWindow.Motion.cs.
/// </summary>
public partial class NoteWindow : Window, INoteWindow
{
    private const double DragThreshold = 3;

    private static readonly Brush InkBrush = Frozen(Palette.Ink);

    public NoteState State { get; }

    public event Action<Point>? DragStarted;
    public event Action<Point>? DragMoved;
    public event Action<Point>? DragEnded;
    public event Action<bool>? LockedChanged;
    public event Action? Changed;
    /// <summary>Round 4 (SPEC-round4 2.3): one keyboard edit of the text while unlocked; the conductor's typing chain counts these.</summary>
    public event Action? KeyTyped;

    // Round 2: Shake is in NoteWindow.Motion.cs, NudgeBy in NoteWindow.Layout.cs, the cucumber drag and
    // CucumberDropped in NoteWindow.Cucumber.cs.

    // A press is armed on mouse down; it becomes a drag only past the threshold, so a double-click
    // (two presses that never move) unlocks without nudging the note.
    private bool _pressed;
    private bool _dragging;
    // The bar double-click lock (SPEC-round2 2.2). WPF counts clicks by time and spot only, not by element,
    // so a click on A+ followed by one on the bar where A+ was a moment ago (the note grows to the left)
    // would read as a double-click. The lock therefore wants both presses of the pair on empty bar.
    private bool _pressOnEmptyBar, _prevPressOnEmptyBar;
    // Set when a double-click locked; the third click of a fast triple then does not unlock it again.
    private bool _lockedByClicks;
    // The window he was typing in before he unlocked the note; locking hands the keyboard back to it,
    // so "write the line, Esc, keep typing" lands in his app and not nowhere.
    private nint _foregroundBefore;
    private Point _pressScreen, _lastMove;
    private double _pressWinLeft, _pressWinTop;
    // Set by a key that edits the text (a character, Back, Delete, Enter, a paste or a cut) just before the
    // text box applies it, consumed by the TextChanged it causes: one KeyTyped per keyboard edit, none for
    // shortcuts, none for changes from anywhere else.
    private bool _keyEdit;

    public NoteWindow(NoteState state)
    {
        State = state;
        InitializeComponent();

        Bar.Attach(state);
        Bar.StateChanged += OnLookChanged;
        Bar.LockRequested += () => SetLocked(true);
        // The bird and sound fields change nothing on the note itself; the conductor reacts to Changed.
        Bar.SettingChanged += () => Changed?.Invoke();
        // A rename in the bird picker ended with Enter or Esc: the caret goes home to the note's text.
        Bar.EditorFocusWanted += () =>
        {
            if (State.Locked || !IsVisible) return;
            Activate();
            FocusEditorAtEnd();
        };
        // The picker closes when he goes to another app (deferred: the rename box deactivates the note too).
        Deactivated += (_, _) => Bar.CloseIfFocusLeftSoon();
        WireCucumber();
        WireGrip();

        ApplyLook();
        CalibrateEditor();
        Editor.Text = state.Text;
        // Setting Text is an undo step in WPF: without this, Ctrl+Z on a fresh session swaps his note for the
        // calibration probe ("MMMMMMMM"), which then gets saved. Toggling undo off clears the stack.
        Editor.IsUndoEnabled = false;
        Editor.IsUndoEnabled = true;
        Editor.TextChanged += OnTextChanged;
        Editor.PreviewKeyDown += OnEditorKey;
        Editor.PreviewTextInput += OnEditorTextInput;
        // A mouse paste from the context menu is not typing; a flag left by a key that changed nothing
        // (Back at the start) must not make it count.
        Editor.PreviewMouseDown += (_, _) => _keyEdit = false;
        ApplyLockVisuals(state.Locked);
        SetValue(UnfoldProperty, state.Locked ? 0.0 : 1.0);
        LayoutAt(new Point(state.X, state.Y));

        // Tunnelling, so it sees every press, also the ones a control below will swallow.
        Paper.PreviewMouseLeftButtonDown += OnPreviewPress;
        Paper.MouseLeftButtonDown += OnPress;
        Paper.MouseMove += OnMouseMoved;
        Paper.MouseLeftButtonUp += (_, _) => { if (_pressed) Paper.ReleaseMouseCapture(); };
        Paper.LostMouseCapture += (_, _) => EndPress();
        PreviewKeyDown += OnKey;

        SourceInitialized += (_, _) =>
        {
            Win32.SetToolWindow(this);
            Win32.SetNoActivate(this, State.Locked);
        };
        LocationChanged += OnLocationChanged;
    }

    /// <summary>
    /// The perch of the layout the note is heading for (SPEC-round3 2.2): during a fold or an unfold the
    /// final paper rect is already known, so the bird flies straight to where it will sit instead of to
    /// the edge that is still moving. At rest the two are the same rect.
    /// </summary>
    public Point PerchPoint
    {
        get
        {
            var r = _animating ? _animTarget : _snapping ? _snapTo : _paper;
            return new Point(r.X + PerchOffset, r.Y);
        }
    }

    /// <summary>Where the paper is now (the dot's monitor, the beside-the-dot size); not the perch's business.</summary>
    public Rect Bounds => _paper;

    public void SetLocked(bool locked)
    {
        if (State.Locked == locked) return;
        State.Locked = locked;
        Bar.ClosePopups();
        CancelPress();
        CancelCucumber();
        // A grip drag cut short by Esc or Ctrl+L keeps the size it reached.
        CancelGrip();
        _keyEdit = false;
        ApplyLockVisuals(locked);

        if (locked)
        {
            Keyboard.ClearFocus();
            Win32.SetNoActivate(this, true);
            var back = _foregroundBefore;
            _foregroundBefore = 0;
            if (back != Win32.Handle(this)) Win32.GiveForeground(back);
        }
        else
        {
            var fg = Win32.ForegroundWindow();
            _foregroundBefore = fg == Win32.Handle(this) ? 0 : fg;
            Win32.SetNoActivate(this, false);
            if (IsVisible)
            {
                Activate();
                FocusEditorAtEnd();
            }
        }

        AnimateUnfold(locked ? 0 : 1);
        Shake();
        LockedChanged?.Invoke(locked);
    }

    private void ApplyLockVisuals(bool locked)
    {
        // Locked, the text box is not there for the mouse at all: no I-beam, no selection, no hover,
        // and every press lands on the paper, which drags.
        Editor.IsReadOnly = locked;
        Editor.IsHitTestVisible = !locked;
        Editor.Focusable = !locked;
        Editor.CaretBrush = locked ? Brushes.Transparent : InkBrush;
        if (locked) Editor.Select(Editor.Text.Length, 0);
        BarClip.IsHitTestVisible = !locked;
        // The resize corner exists only while unlocked (SPEC-round4 2.4); locked, the whole paper drags.
        Grip.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FocusEditorAtEnd()
    {
        Editor.Focus();
        Keyboard.Focus(Editor);
        Editor.CaretIndex = Editor.Text.Length;
    }

    /// <summary>Colour, font and opacity from the state; cheap enough to run on every bar change.</summary>
    private void ApplyLook()
    {
        var paper = Frozen(Palette.Parse(State.Color));
        var strip = Frozen(Palette.StripOf(State.Color));
        Paper.Background = paper;
        Shadow.Background = paper;
        Strip.Background = strip;
        BarClip.Background = strip;

        if (Editor.FontFamily.Source != State.Font)
        {
            Editor.FontFamily = new FontFamily(State.Font);
            _typeface = null;
        }
        Editor.FontSize = State.FontSize;
        // The vines grow out or fold back by themselves when this flips (VinesView animates its extent).
        Vines.Enabled = State.PlantsOn;

        Opacity = State.Opacity / 100.0;
    }

    private void OnLookChanged()
    {
        ApplyLook();
        Relayout();
        Changed?.Invoke();
    }

    private void OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        bool typed = _keyEdit && !State.Locked;
        _keyEdit = false;
        State.Text = Editor.Text;
        Relayout();
        Changed?.Invoke();
        if (typed) KeyTyped?.Invoke();
    }

    /// <summary>
    /// Decides, per key, whether the key edits the text. Every key resets the flag first, so a key that did
    /// nothing never leaves it set. The window's own shortcuts (Esc, Ctrl+L, Ctrl+=, Ctrl+-) are handled in
    /// its PreviewKeyDown, which tunnels first, so they never reach here.
    /// </summary>
    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        _keyEdit = !State.Locked && (e.Key is Key.Back or Key.Delete or Key.Enter
            || (ctrl && e.Key is Key.V or Key.X)        // paste and cut: one edit each, however long
            || (shift && e.Key == Key.Insert));          // the old paste
    }

    /// <summary>A typed character; control characters (the carriage return of Enter, Ctrl+letter) are not text.</summary>
    private void OnEditorTextInput(object sender, TextCompositionEventArgs e)
    {
        if (State.Locked || string.IsNullOrEmpty(e.Text)) return;
        foreach (char c in e.Text)
        {
            if (!char.IsControl(c))
            {
                _keyEdit = true;
                return;
            }
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // While he renames a bird, Esc, Enter and the shortcuts belong to the rename box (this handler tunnels first).
        if (State.Locked || Bar.IsRenaming) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape || (ctrl && e.Key == Key.L))
        {
            SetLocked(true);
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.OemPlus or Key.Add)
        {
            Bar.StepFontSize(+1);
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.OemMinus or Key.Subtract)
        {
            Bar.StepFontSize(-1);
            e.Handled = true;
        }
    }

    // ---- the mouse ---------------------------------------------------------------------------

    /// <summary>Notes, for every press, whether it landed on the empty bar (strip included) or on a control.</summary>
    private void OnPreviewPress(object sender, MouseButtonEventArgs e)
    {
        // A press anywhere else on the note closes the bird picker (SPEC-round5 2.3).
        Bar.NotePressed(e.OriginalSource as DependencyObject);
        if (e.ClickCount == 1) _lockedByClicks = false;
        _prevPressOnEmptyBar = _pressOnEmptyBar;
        _pressOnEmptyBar = !State.Locked
            && e.GetPosition(Paper).Y <= StripHeight + BarClip.ActualHeight
            && !Bar.IsOnControl(e.OriginalSource as DependencyObject);
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        // Controls handle their own presses, so only the paper, the strip and the bar's empty areas get here.
        if (e.ClickCount >= 2 && (State.Locked || !_pressOnEmptyBar || _prevPressOnEmptyBar))
        {
            CancelPress();
            if (State.Locked)
            {
                if (!_lockedByClicks) SetLocked(false);
            }
            else if (_pressOnEmptyBar)
            {
                SetLocked(true);
                _lockedByClicks = true;
            }
            e.Handled = true;
            return;
        }
        // Left: a second press on the empty bar whose first half hit a control. It is an ordinary press,
        // so it can still start a bar drag.

        // Unlocked, only the strip and the bar's empty stretches grab; the padding around the text
        // just hands the click to the text box. Bar controls never get here: they handle their own press.
        if (!State.Locked && e.GetPosition(Paper).Y > StripHeight + BarClip.ActualHeight)
        {
            FocusEditorAtEnd();
            e.Handled = true;
            return;
        }

        _pressed = true;
        _dragging = false;
        _pressScreen = ScreenPoint(e.GetPosition(this));
        _lastMove = _pressScreen;
        _pressWinLeft = _winLeft;
        _pressWinTop = _winTop;
        Paper.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        var p = ScreenPoint(e.GetPosition(this));
        // Moving the window under a still cursor makes Windows send a move of its own; it changes nothing.
        if (p == _lastMove) return;
        _lastMove = p;
        double dx = p.X - _pressScreen.X, dy = p.Y - _pressScreen.Y;

        if (!_dragging)
        {
            if (Math.Abs(dx) <= DragThreshold && Math.Abs(dy) <= DragThreshold) return;
            _dragging = true;
            // A popup is its own window and would stay behind while the note moves away under it.
            Bar.ClosePopups();
            DragStarted?.Invoke(_pressScreen);
        }

        MoveWindowTo(_pressWinLeft + dx, _pressWinTop + dy);
        DragMoved?.Invoke(p);
    }

    private void EndPress()
    {
        if (!_pressed) return;
        bool wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        if (!wasDragging) return;

        var p = ScreenPoint(Mouse.GetPosition(this));
        State.X = _paper.X;
        State.Y = _paper.Y;
        Changed?.Invoke();
        DragEnded?.Invoke(p);
    }

    /// <summary>Drops a press without reporting it, e.g. when a double-click turns it into an unlock.</summary>
    private void CancelPress()
    {
        bool wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        if (Paper.IsMouseCaptured) Paper.ReleaseMouseCapture();
        if (wasDragging)
        {
            State.X = _paper.X;
            State.Y = _paper.Y;
            Changed?.Invoke();
            DragEnded?.Invoke(ScreenPoint(Mouse.GetPosition(this)));
        }
    }

    /// <summary>
    /// Alt+F4 on the unlocked note (the active window then) would close the window for good: every later show
    /// throws and the note is gone until a restart, the bird with it (it is owned). Here it means "done typing",
    /// like Esc. The app's own shutdown ignores the cancel (Application.Shutdown closes windows regardless).
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        if (!State.Locked) SetLocked(true);
        base.OnClosing(e);
    }

    private Point ScreenPoint(Point inWindow) => new(Left + inWindow.X, Top + inWindow.Y);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
