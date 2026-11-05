using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TaskFocuser.Core;
using TaskFocuser.Monster;

namespace TaskFocuser.Views;

/// <summary>
/// The bird picker (SPEC-round5 2.3), shown in the popup under the bar's perch slot. One row per bird in
/// <see cref="Birds.All"/>: a click on the row makes it the active bird, a click on its pencil turns the name
/// into a box in place. Writes <see cref="NoteState.Bird"/> and <see cref="NoteState.BirdNames"/> itself and
/// tells the bar, which closes the popup, refreshes the slot and raises the note's Changed.
/// </summary>
public partial class BirdPicker : UserControl
{
    public const int MinName = 3, MaxName = 14;

    private static readonly Brush ValidEdge = Frozen(Color.FromArgb(0x5A, 0x2B, 0x2A, 0x25));
    private static readonly Brush InvalidEdge = Frozen(Color.FromRgb(0xD0, 0x4A, 0x3A));

    private sealed record Row(BirdSkin Skin, TextBlock Name, TextBox Box);

    private readonly List<Row> _rows = new();
    private NoteState? _state;
    // The row whose name is being edited; null when nothing is. Cleared before the box is hidden or the focus
    // moves, so the LostKeyboardFocus those cause never commits a second time.
    private Row? _editing;

    /// <summary>He picked a bird: <see cref="NoteState.Bird"/> is written; the popup should close.</summary>
    public event Action? Chosen;
    /// <summary>A name was committed into <see cref="NoteState.BirdNames"/>.</summary>
    public event Action? Renamed;
    /// <summary>A rename ended with Enter or Esc: the caret belongs back in the note's text.</summary>
    public event Action? EditFinished;
    /// <summary>A rename ended because the box lost the keyboard (a click elsewhere, another app).</summary>
    public event Action? EditBlurred;

    public BirdPicker()
    {
        InitializeComponent();
        foreach (var skin in Birds.All) Rows.Children.Add(BuildRow(skin));
        // A press on the padding is the picker's: the note would read it as a press below the bar and move
        // the caret (the same guard as the volume popup).
        Box.MouseLeftButtonDown += (_, e) => e.Handled = true;
    }

    /// <summary>True while a name is in its rename box; the note then keeps its own shortcuts off the keys.</summary>
    public bool IsEditing => _editing is not null;

    public void Attach(NoteState state)
    {
        _state = state;
        Refresh();
    }

    /// <summary>The name a bird goes by: his name for it, trimmed, or its default name when he gave none.</summary>
    public static string NameOf(NoteState state, BirdSkin skin) =>
        state.BirdNames.TryGetValue(skin.Id, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : skin.DefaultName;

    /// <summary>3 to 14 characters after trimming: letters (accents included), digits and spaces.</summary>
    public static bool IsValidName(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < MinName || t.Length > MaxName) return false;
        foreach (char c in t)
            if (!char.IsLetterOrDigit(c) && c != ' ') return false;
        return true;
    }

    /// <summary>The names and the bold of the active bird, from the state.</summary>
    public void Refresh()
    {
        if (_state is null) return;
        var active = Birds.ById(_state.Bird);
        foreach (var r in _rows)
        {
            r.Name.Text = NameOf(_state, r.Skin);
            r.Name.FontWeight = ReferenceEquals(r.Skin, active) ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    /// <summary>The same scrap of paper as the volume popup: the note's colour, edged with the strip's.</summary>
    public void SetColours(Brush paper, Brush edge)
    {
        Box.Background = paper;
        Box.BorderBrush = edge;
    }

    /// <summary>Ends a running rename as the popup goes away: a valid name is kept, anything else dropped.</summary>
    public void EndEdit()
    {
        if (_editing is not Row r) return;
        if (IsValidName(r.Box.Text)) Commit(r); else Stop(r);
    }

    // ---- the rows -------------------------------------------------------------------------------------

    private UIElement BuildRow(BirdSkin skin)
    {
        var portrait = new Image
        {
            Source = SpriteImage.Render(skin.IdleOpen, 1),
            Width = 22,
            Height = 16,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 7, 0),
            SnapsToDevicePixels = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetEdgeMode(portrait, EdgeMode.Aliased);
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Children = { portrait, name } };
        var pick = new Button { Style = (Style)FindResource("TF.PickerRow"), Content = line, HorizontalAlignment = HorizontalAlignment.Stretch };

        // The rename box lies over the name, beside the row button rather than inside it: a button would
        // take the box's Space and Enter for a click of its own. 4 (the row's padding) + 22 + 7 = where the name starts.
        var box = new TextBox
        {
            Style = (Style)FindResource("TF.RenameBox"),
            Margin = new Thickness(31, 0, 2, 0),
            Visibility = Visibility.Collapsed,
        };
        var pencil = new Button { Style = (Style)FindResource("TF.Pencil"), Margin = new Thickness(2, 0, 0, 0) };

        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pencil, 1);
        grid.Children.Add(pick);
        grid.Children.Add(box);
        grid.Children.Add(pencil);

        var row = new Row(skin, name, box);
        _rows.Add(row);
        pick.Click += (_, _) => Choose(row);
        pencil.Click += (_, _) => BeginEdit(row);
        box.PreviewKeyDown += (_, e) => OnBoxKey(row, e);
        box.PreviewTextInput += (_, e) => { if (!AllAllowed(e.Text)) e.Handled = true; };
        box.TextChanged += (_, _) => box.BorderBrush = IsValidName(box.Text) ? ValidEdge : InvalidEdge;
        box.LostKeyboardFocus += (_, _) => OnBoxBlur(row);
        return grid;
    }

    private void Choose(Row row)
    {
        if (_state is null) return;
        // A click on a row while a name is open keeps that name if it is valid, as a blur would.
        EndEdit();
        _state.Bird = row.Skin.Id;
        Refresh();
        Chosen?.Invoke();
    }

    // ---- the rename -----------------------------------------------------------------------------------

    private void BeginEdit(Row row)
    {
        if (_state is null) return;
        if (ReferenceEquals(_editing, row)) return;
        EndEdit();
        _editing = row;
        row.Box.Text = NameOf(_state, row.Skin);
        row.Box.BorderBrush = ValidEdge;
        row.Name.Visibility = Visibility.Hidden;
        row.Box.Visibility = Visibility.Visible;
        // The one place the picker takes the keyboard: he is about to type the name.
        row.Box.Focus();
        Keyboard.Focus(row.Box);
        row.Box.SelectAll();
    }

    private void OnBoxKey(Row row, KeyEventArgs e)
    {
        if (!ReferenceEquals(_editing, row)) return;
        if (e.Key == Key.Enter)
        {
            // Under 3 characters the red edge says why nothing happened; the box stays for another try.
            e.Handled = true;
            if (!IsValidName(row.Box.Text)) return;
            Commit(row);
            EditFinished?.Invoke();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Stop(row);
            EditFinished?.Invoke();
        }
    }

    private void OnBoxBlur(Row row)
    {
        if (!ReferenceEquals(_editing, row)) return;
        EndEdit();
        EditBlurred?.Invoke();
    }

    private void Commit(Row row)
    {
        if (_state is null) return;
        string name = row.Box.Text.Trim();
        Stop(row);
        if (string.Equals(name, NameOf(_state, row.Skin), StringComparison.Ordinal)) return;
        _state.BirdNames[row.Skin.Id] = name;
        Refresh();
        Renamed?.Invoke();
    }

    /// <summary>Back to the plain name. <see cref="_editing"/> goes first, so the blur the hiding causes is ignored.</summary>
    private void Stop(Row row)
    {
        if (ReferenceEquals(_editing, row)) _editing = null;
        row.Box.Visibility = Visibility.Collapsed;
        row.Name.Visibility = Visibility.Visible;
    }

    /// <summary>Typing filter: letters, digits and spaces only, so a wrong key simply does nothing.</summary>
    private static bool AllAllowed(string text)
    {
        foreach (char c in text)
            if (!char.IsLetterOrDigit(c) && c != ' ') return false;
        return true;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
