using System.Windows;

namespace TaskFocuser.Core;

// The interfaces the conductor (App.xaml.cs) talks to and every module implements.
// All points and rects are screen coordinates in WPF DIPs (system DPI scale). See docs/SPEC.md section 6.

/// <summary>The post-it itself (Views/NoteWindow). Owns its drag, its lock state and its growth.</summary>
public interface INoteWindow
{
    NoteState State { get; }

    /// <summary>Cursor position at the press that started a drag (locked: anywhere; unlocked: the bar).</summary>
    event Action<Point>? DragStarted;
    /// <summary>Cursor position on every move while dragging; the note has already moved itself.</summary>
    event Action<Point>? DragMoved;
    /// <summary>Cursor position at release; State.X/Y are already updated.</summary>
    event Action<Point>? DragEnded;
    /// <summary>Raised after State.Locked changed and the bar animation started (true = now locked).</summary>
    event Action<bool>? LockedChanged;
    /// <summary>Raised after any persisted field changed (text, colour, opacity, font, size, position).</summary>
    event Action? Changed;

    /// <summary>Shows the note at State.X/Y, clamped into a visible work area; a soft fade-in is fine.</summary>
    void ShowNote();
    void HideNote();
    void BringToFront();
    /// <summary>Locks or unlocks with the bar animation; a no-op when already in that state.</summary>
    void SetLocked(bool locked);

    /// <summary>Where the monster's feet go: on the note's top edge, centred on the bar's perch slot.</summary>
    Point PerchPoint { get; }
    /// <summary>The note's current rectangle on screen.</summary>
    Rect Bounds { get; }

    /// <summary>Appears from <paramref name="from"/> (the dot centre) growing into place at <paramref name="topLeft"/> with a bounce.</summary>
    void PopOut(Point from, Point topLeft);
    /// <summary>Squashes into <paramref name="target"/> (the dot centre) and hides, then calls <paramref name="onDone"/>.</summary>
    void SquashInto(Point target, Action onDone);

    // Round 2 (docs/SPEC-round2.md).
    /// <summary>The tiny horizontal shake of 2.1; the note calls it itself on lock and unlock.</summary>
    void Shake();
    /// <summary>Moves the visible note by <paramref name="delta"/> and updates State.X/Y; raises nothing (the bird drags the note through the conductor).</summary>
    void NudgeBy(Vector delta);
    /// <summary>The treat (the cucumber slice, or the bone since round 6) was released by the button at this cursor position (screen DIPs); a lost capture is TreatDragCancelled instead.</summary>
    event Action<Point>? CucumberDropped;
    /// <summary>Round 4: one character typed or deleted by the keyboard while unlocked (not shortcuts).</summary>
    event Action? KeyTyped;
    /// <summary>Round 6: the treat (cucumber or bone) drag made its first move; the companion may beg (SPEC-round6 2.3).</summary>
    event Action? TreatDragStarted;
    /// <summary>Round 6: the treat drag ended without a drop (lock, hide, squash, a lost capture); the drop itself is CucumberDropped.</summary>
    event Action? TreatDragCancelled;
}

/// <summary>The blue pixel bird (Monster/MonsterWindow): its own non-activating window above the note; clickable on its pixels since round 2.</summary>
public interface IMonster
{
    /// <summary>Flies in from above the screen and settles with its feet on <paramref name="perch"/>.</summary>
    void ArriveAt(Point perch);
    /// <summary>Moves instantly (used on every drag move).</summary>
    void MoveTo(Point perch);
    /// <summary>Flap loop on or off; off returns to idle.</summary>
    void SetCarrying(bool carrying);
    /// <summary>Darts from <paramref name="from"/> to <paramref name="target"/> leaning in, bumps, then calls <paramref name="onDone"/>.</summary>
    void Shove(Point from, Point target, Action onDone);
    /// <summary>Hops off upward and hides.</summary>
    void Leave();
    /// <summary>True while on screen (arrived or arriving).</summary>
    bool IsOut { get; }

    // Round 2 (docs/SPEC-round2.md).
    /// <summary>Takes off, flaps, glides to the new perch and lands (2.1); used when the note resized under a perched bird.</summary>
    void FlyTo(Point perch);
    /// <summary>The window's opacity, 0 to 1 (2.7).</summary>
    void SetOpacity(double opacity);
    /// <summary>Chomps the cucumber, then is happy with hearts (2.9); requests a Happy chirp.</summary>
    void Eat();
    /// <summary>A short flap burst while perched, then idle (the keep-alive of 2.5).</summary>
    void Flap();
    /// <summary>Round 3: fluffs up and shakes its feathers (the "brrrrp"), requesting a Ruffle chirp; a no-op mid-motion or hidden.</summary>
    void Ruffle();
    /// <summary>Round 4: one quick peck at a seed on the ground (about 160 ms); bursts coalesce; a no-op mid-motion or hidden.</summary>
    void Peck();
    /// <summary>Round 5: the current bird leaves, the skin swaps, the new bird arrives at <paramref name="perch"/>; silent when not out.</summary>
    void SwitchTo(TaskFocuser.Monster.BirdSkin skin, Point perch);
    /// <summary>Round 5: the x range the feet may occupy on the note's top edge (screen DIPs); the walk stays inside it.</summary>
    void SetPerchSpan(double left, double right);
    /// <summary>Round 6: he picked the treat up (true) or put it away without a drop (false); a dog sits up begging, a bird ignores it (SPEC-round6 2.3).</summary>
    void Beg(bool begging);
    /// <summary>True when the point lies on the sprite (generous by 6 DIP); for the cucumber drop.</summary>
    bool Contains(Point screenDip);
    /// <summary>A click with no movement (the hop is the bird's own; informational).</summary>
    event Action? Clicked;
    /// <summary>The bird wants a sound; the conductor plays it at the user's volume.</summary>
    event Action<ChirpKind>? ChirpRequested;
    /// <summary>The bird as a drag handle for the note (cursor in screen DIPs, 3 DIP threshold, capture).</summary>
    event Action<Point>? DragStarted;
    event Action<Point>? DragMoved;
    event Action<Point>? DragEnded;
}

/// <summary>The red glowing dot (Views/DotWindow).</summary>
public interface IDot
{
    Point Centre { get; }

    /// <summary>Appears as a drop target at <paramref name="centre"/> (during a note drag).</summary>
    void Arm(Point centre);
    /// <summary>Swells when the dragged note is within reach.</summary>
    void SetHot(bool hot);
    /// <summary>Shrinks away (the drag ended elsewhere).</summary>
    void Disarm();
    /// <summary>The catch: squash-and-stretch at <paramref name="centre"/>, then rests there; calls <paramref name="onDone"/> when settled.</summary>
    void Dock(Point centre, Action onDone);
    /// <summary>Shows the resting, pulsing dot at <paramref name="centre"/> with no entrance animation.</summary>
    void Rest(Point centre);
    /// <summary>The spit: a quick squash and bounce; calls <paramref name="onDone"/> at the peak so the note can pop out.</summary>
    void Pop(Action onDone);
    /// <summary>Shrinks to nothing and hides (dropped on the close target).</summary>
    void Vanish(Action onDone);
    void HideDot();
    /// <summary>Generous hit test for drops (44 DIP radius per the spec).</summary>
    bool Contains(Point p);

    /// <summary>Double-click on the resting dot.</summary>
    event Action? DoubleClicked;
    event Action<Point>? DragStarted;
    event Action<Point>? DragMoved;
    event Action<Point>? DragEnded;
}

/// <summary>The bottom-right disc that closes the dot (Views/CloseTargetWindow).</summary>
public interface ICloseTarget
{
    Point Centre { get; }
    void ShowAt(Point centre);
    void SetHot(bool hot);
    void HideTarget();
    bool Contains(Point p);
}

/// <summary>The tray icon and its menu (Core/TrayIcon).</summary>
public interface ITray : IDisposable
{
    event Action? ShowNote;
    event Action? Tuck;
    event Action? StartDot;
    event Action? ToggleLock;
    event Action? ToggleStartWithWindows;
    event Action? Quit;

    /// <summary>Relabels and enables the items for the current situation.</summary>
    void Update(Mode mode, bool locked, bool startWithWindows);
}
