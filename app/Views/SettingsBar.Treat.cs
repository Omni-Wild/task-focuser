using System.Windows.Controls;
using System.Windows.Media;
using TaskFocuser.Monster;

namespace TaskFocuser.Views;

// Round 6 (SPEC-round6 2.6): the treat on the bar follows the companion, the cucumber for the birds and the
// bone for Rori, the volume popup's idle-sound words follow her voice, and the tooltips carry the companion's name. A partial of its own, so
// SettingsBar.xaml.cs stays near its size.
public partial class SettingsBar
{
    // 2 DIP per cell: a whole number keeps the pixel cells even (2 px at 100 %, 3 px at 150 %), and the 10 by 5 bone becomes
    // 20 by 10, inside the 22 DIP handle and no taller than the 14 DIP glyphs around it.
    private const double BarTreatCell = 2;
    private const double CucumberDip = 14;

    private static readonly string[] DogSoundLabels = { "wuf", "shake", "both" };

    // The rows last put on the handle (null = the cucumber), so the many Refresh calls render nothing twice.
    private string[]? _treatRows;
    private bool _treatShown;

    /// <summary>Called with every name refresh, which runs at Attach (the saved companion), on a pick and on a rename.</summary>
    private void ApplyTreat(string name)
    {
        if (_state is null) return;
        var skin = Birds.ById(_state.Bird);
        // The tooltips name the companion, so a bird reads with its own name too; the XAML holds neutral
        // "companion" wording only for the instant before Attach. The glyphs stay the bird's on purpose.
        Cucumber.ToolTip = $"Drag onto {name}";
        StaysToggle.ToolTip = $"{name} stays when locked";
        OpaqueToggle.ToolTip = $"{name} always opaque";
        SoundChoices.ToolTip = $"Which idle sounds {name} makes";
        ApplySoundLabels(skin.Dog is not null);

        var rows = skin.Treat;
        if (_treatShown && ReferenceEquals(rows, _treatRows)) return;
        _treatShown = true;
        _treatRows = rows;
        if (rows is null)
        {
            TreatImage.Source = (ImageSource)FindResource("TF.CucumberSlice");
            TreatImage.Width = TreatImage.Height = CucumberDip;
            return;
        }
        var size = SpriteImage.SizeOf(rows, BarTreatCell);
        TreatImage.Source = SpriteImage.Render(rows, BarTreatCell);
        TreatImage.Width = size.Width;
        TreatImage.Height = size.Height;
    }

    /// <summary>"chirp / brrrp / both" for a bird, "wuf / shake / both" for a dog: the words are what he hears.</summary>
    private void ApplySoundLabels(bool dog)
    {
        var labels = dog ? DogSoundLabels : SoundLabels;
        for (int i = 0; i < SoundChoices.Children.Count && i < labels.Length; i++)
            if (SoundChoices.Children[i] is Button { Content: TextBlock t } && t.Text != labels[i]) t.Text = labels[i];
    }
}
