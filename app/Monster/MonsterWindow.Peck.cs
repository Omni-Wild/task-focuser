using System.Windows;

namespace TaskFocuser.Monster;

// The peck (SPEC-round4 2.3): the typing chain's visible half, and the idle fidget that eats a seed
// off the ground. Both run on the act machine in Effects.cs, so every rule there about what cuts an
// act (a flight, a drag, a click, a hide) holds for them without anything new to stop: no timer of
// their own, and StopAct drops the queued peck and the ground seed.
//
// The queue: Peck() while a peck runs only sets peckQueued (one flag, so at most one more peck);
// EndAct starts that one straight away. A burst of keys therefore reads as continuous pecking at the
// peck's own pace (about six a second) and stops within one peck after the last key, never trailing
// a backlog behind the typing.
public partial class MonsterWindow
{
    // The peck and seed step tables are per bird (round 5): MonsterWindow.Skin.cs, acts.Peck and acts.Seed.

    // The fidget draw, out of 300 so every share is a whole number: a quarter the seed, a sixth the
    // silent ruffle (as in round 3), a fifth the walk (round 5, taken from the old variations), and
    // the rest split evenly over the five round 2 variations (23/300 each, 115 in all).
    private const int FidgetRoll = 300;
    private const int SeedShare = 75;
    private const int RuffleShare = 50;
    private const int WalkShare = 60;

    // The seed lies 3 to 5 empty cells in front of the front toe (cell 14 of every perched frame);
    // 5 is exactly where Peck2 holds it, so the nearer spots read as the beak reaching for it.
    private const int FrontToeColumn = 14;

    private bool peckQueued;
    private Point seedSpot;

    /// <summary>One quick peck at a seed; a peck asked for during one is queued, at most one deep.</summary>
    public void Peck()
    {
        // Only a bird sitting on the note: any flight (the sulk and its unseen wait included), a drag,
        // a tumble, a loop (the shove's bump) or a press on the bird wins.
        if (!IsVisible || !IsOut || motion != Motion.None || loop != Loop.None || carrying
            || grip.Tumbling || pressed) return;
        if (act == Act.Peck)
        {
            peckQueued = true;
            return;
        }
        // A plain fidget (the seed one too) or a blink gives way; a real act does not: the meal, the
        // click hop and chirps, the fume, the flap burst, and the ruffle, also when a fidget plays it.
        if (act is not (Act.None or Act.Idle)) return;
        if (act == Act.Idle && ReferenceEquals(actSteps, acts.Ruffle)) return;
        PlayAct(Act.Peck, acts.Peck);
    }

    private void ScheduleIdle()
    {
        if (!IsVisible) return;
        idleTimer.Interval = TimeSpan.FromMilliseconds(Random.Shared.Next(20000, 60001));
        idleTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        idleTimer.Stop();
        if (!IsVisible) return;
        bool calm = IsOut && motion == Motion.None && loop == Loop.None && act == Act.None && !carrying
            && !grip.Tumbling && !effects.Active && !blinking && !pressed && walkPhase == WalkPhase.None && !stray;
        if (!calm)
        {
            ScheduleIdle();
            return;
        }
        int roll = Random.Shared.Next(FidgetRoll);
        if (roll < SeedShare)
        {
            int gap = Random.Shared.Next(3, 6);
            // On the ground line: the window's bottom edge is the feet line, the seed one cell tall.
            seedSpot = new Point(SpriteX + (FrontToeColumn + 1 + gap) * Sprites.CellSize, WinH - Sprites.CellSize);
            PlayAct(Act.Idle, acts.Seed);
            return;
        }
        roll -= SeedShare;
        // One fidget in six is a silent ruffle: the brrrp itself comes only from the conductor's idle
        // scheduler, so his choice of idle sounds is honoured.
        if (roll < RuffleShare)
        {
            PlayAct(Act.Idle, acts.Ruffle);
            return;
        }
        roll -= RuffleShare;
        // The walk (round 5): on a note too narrow for it this fidget is simply skipped.
        if (roll < WalkShare)
        {
            if (!TryStartWalk()) ScheduleIdle();
            return;
        }
        roll -= WalkShare;
        var variations = acts.IdleVariations;
        PlayAct(Act.Idle, variations[roll * variations.Length / (FidgetRoll - SeedShare - RuffleShare - WalkShare)]);
    }
}
