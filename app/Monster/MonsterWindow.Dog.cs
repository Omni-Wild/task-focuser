using System.Windows;

namespace TaskFocuser.Monster;

// Rori's own acts (SPEC-round6 2.3): the wag, the play bow, the zoomies, the nap, and begging for the treat.
// They are drawn only for a skin with Dog extras: their step tables exist only then (ActSet in Skin.cs builds
// them from the extras and leaves them null for a bird), the fidget roll reaches PlayDogFidget only then
// (Peck.cs), and Beg and the wagging Flap return early on a null table. Everything but the zoomies runs on
// the act machine of Effects.cs, so a flight, a drag, a click, a peck or a hide cuts it like any act; the
// zoomies are the walk with its zoom flag (Walk.cs), so they are cut like a walk.
//
// The nap needs quiet: lastTouch is stamped by every click or press on her, every drag (SetCarrying), every
// key (Peck), every treat (Beg, Eat) and every arrival, and the nap is drawn only three minutes after it.
public partial class MonsterWindow
{
    // Her share of the fidget roll, after the shared FidgetRoll of Peck.cs (450 in all for a dog): the wag
    // as often as the seed (75), the play bow uncommon (25), the zoomies one fidget in fifteen (30), the nap
    // rare (the 20 left). A zoomies draw on a narrow note or a nap draw before the quiet is skipped, like the walk.
    private const int DogFidgetRoll = 150;
    private const int WagShare = 75;
    private const int BowShare = 25;
    private const int ZoomShare = 30;

    private const long NapQuietMs = 3 * 60 * 1000;
    private const int NapBreathMs = 1200;           // Sleep1 and Sleep2 alternate this often; a z every other one
    private const int NapBreathsMin = 9;            // 10.8 s ...
    private const int NapBreathsMax = 16;           // ... to 19.2 s asleep, inside the 10 to 20 s of the spec

    private const double ZoomSpeed = 4 * WalkSpeed;   // 240 DIP/s
    private const double ZoomFrameTime = 1.0 / 12;    // the run frames at 12 fps, as when carried
    private const double ZoomMinSpan = 2 * SpriteW;   // less room than two sprite widths: no zoomies
    private const double ZoomSkidTime = 0.25;         // the turn; the dust has nearly faded when she is off again
    private const int ZoomSkidDust = 3;

    private const double BowLean = 2;                 // the play bow leans 2 DIP forward (she faces right in an act)
    // Where a nap's z starts (its bottom-left, window DIPs): just above and ahead of the far ear of the
    // sleeping head. In Sleep1 and Sleep2 (the frames the Zz cue plays on) the skull tops out on row 7 and
    // the far ear fills columns 15-17 of row 6 with its tip at column 16 of row 5, so a 5 x 5 z based on
    // row 7 at column 15 would start over the ear and the eye; based on row 5 at column 18 it fills rows 0-4,
    // columns 18-22 (the last one past the grid, inside the window), a clear cell off the ear tip.
    private static readonly Point ZzHead = new(SpriteX + 18 * Sprites.CellSize, SpriteY + 5 * Sprites.CellSize);
    // The eat's hearts rise over her head, right of the window centre (ActSet.HeartsAt).
    private const double DogHeartsShift = 12;

    private long lastTouch = Environment.TickCount64;

    /// <summary>True while a nap table plays (lying, asleep or the stretch): the idle sounds' visible halves skip it.</summary>
    private bool Napping
    {
        get
        {
            if (act != Act.Idle || acts.Naps is not { } naps) return false;
            foreach (var n in naps)
                if (ReferenceEquals(n, actSteps)) return true;
            return false;
        }
    }

    /// <summary>SPEC-round6 2.3: a dog sits up while he carries the treat; a bird ignores it.</summary>
    public void Beg(bool begging)
    {
        if (acts.Beg is not { } beg) return;
        if (!begging)
        {
            // Put away without an eat: she sits down again. Whatever else cut the begging (the eat, a drag,
            // a click) already ended it, so then there is nothing to undo.
            if (act == Act.Beg) EndAct();
            return;
        }
        Touch();
        // Only a resting or fidgeting dog sits up (a walk and the zoomies are fidgets: she begs where she
        // stands and trots home after); a flight, a carry, a tumble, a press or a real act wins and the call
        // is dropped, so she does not beg later on her own.
        if (!IsVisible || !IsOut || motion != Motion.None || loop != Loop.None || carrying || grip.Tumbling
            || pressed || act is not (Act.None or Act.Idle)) return;
        PlayAct(Act.Beg, beg);
    }

    /// <summary>Something touched her (a click, a drag, a key, a treat, an arrival): the nap's quiet starts over.</summary>
    private void Touch() => lastTouch = Environment.TickCount64;

    /// <summary>One of her fidgets; <paramref name="roll"/> is her part of the draw, 0 to DogFidgetRoll.</summary>
    private void PlayDogFidget(int roll)
    {
        if (acts is not { Wag: { } wag, Bow: { } bow, Naps: { } naps })
        {
            ScheduleIdle();
            return;
        }
        if (roll < WagShare)
        {
            PlayAct(Act.Idle, wag);
            return;
        }
        roll -= WagShare;
        if (roll < BowShare)
        {
            PlayAct(Act.Idle, bow);
            return;
        }
        roll -= BowShare;
        if (roll < ZoomShare)
        {
            if (!TryStartZoomies()) ScheduleIdle();
            return;
        }
        if (Environment.TickCount64 - lastTouch < NapQuietMs)
        {
            ScheduleIdle();
            return;
        }
        PlayAct(Act.Idle, naps[Random.Shared.Next(naps.Length)]);
    }

    /// <summary>The zoomies: a dash to the far end of the span, a skid, back past the perch to the other end, a skid, home.</summary>
    private bool TryStartZoomies()
    {
        if (double.IsNaN(spanLeft) || spanRight - spanLeft < ZoomMinSpan) return false;
        // The ends the feet may reach, as window lefts; the far one first, so the first dash is the long one.
        double leftEnd = spanLeft + WalkEdgeGap - WinW / 2, rightEnd = spanRight - WalkEdgeGap - WinW / 2;
        double home = TopLeftFor(perch).X;
        bool rightFirst = rightEnd - home >= home - leftEnd;
        zoomOtherEnd = rightFirst ? leftEnd : rightEnd;
        BeginWalk(rightFirst ? rightEnd : leftEnd, ZoomSpeed, zoomies: true);
        return true;
    }

    // ---- her step tables, built once per dog skin by ActSet (Skin.cs) ------------------------------

    /// <summary>The tail swung left and right, <paramref name="swings"/> frames of <paramref name="ms"/> each.</summary>
    private static (string[] Frame, int Ms, Cue Cue)[] DogWag(BirdSkin.DogExtras d, int swings, int ms)
    {
        var steps = new (string[] Frame, int Ms, Cue Cue)[swings];
        for (int i = 0; i < swings; i++) steps[i] = ((i & 1) == 0 ? d.Wag1 : d.Wag2, ms, Cue.None);
        return steps;
    }

    /// <summary>
    /// The nap, one table per length so a nap picks its length without building anything: lying down, the
    /// breathing (a z rising with every Sleep1, so every 2.4 s), the stretch; the rest that follows sits her up.
    /// </summary>
    private static (string[] Frame, int Ms, Cue Cue)[][] DogNaps(BirdSkin.DogExtras d)
    {
        var naps = new (string[] Frame, int Ms, Cue Cue)[NapBreathsMax - NapBreathsMin + 1][];
        for (int n = NapBreathsMin; n <= NapBreathsMax; n++)
        {
            var steps = new (string[] Frame, int Ms, Cue Cue)[n + 2];
            steps[0] = (d.Lie, 600, Cue.None);
            for (int i = 0; i < n; i++)
                steps[1 + i] = (i & 1) == 0 ? (d.Sleep1, NapBreathMs, Cue.Zz) : (d.Sleep2, NapBreathMs, Cue.None);
            steps[n + 1] = (d.Stretch, 500, Cue.None);
            naps[n - NapBreathsMin] = steps;
        }
        return naps;
    }
}
