namespace TaskFocuser;

/// <summary>
/// Round 6 of the state machine (docs/SPEC-round6.md): the begging while he carries the treat (2.3). The drop
/// itself stays round 2's OnCucumberDropped, which ends the begging before it asks for the eat. Same rules as
/// Conductor.cs: UI thread only, every module call behind Guard or Fail.
/// </summary>
public sealed partial class Conductor
{
    bool treatCarried;   // a treat drag is under way and the companion was asked to beg

    void StartRound6()
    {
        note.TreatDragStarted += () => Guard("note.TreatDragStarted", OnTreatDragStarted);
        note.TreatDragCancelled += () => Guard("note.TreatDragCancelled", EndBegging);
    }

    void OnTreatDragStarted()
    {
        // A companion away, shoving or mid-switch has no one to beg; whether she is free to sit up is hers to say.
        if (busy || switching || !monster.IsOut) return;
        treatCarried = true;
        monster.Beg(true);
    }

    /// <summary>The treat went away (a cancel, or a drop before the eat): a begging dog goes back to rest.</summary>
    void EndBegging()
    {
        if (!treatCarried) return;
        treatCarried = false;
        monster.Beg(false);
    }
}
