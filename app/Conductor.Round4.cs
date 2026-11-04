using System.Windows.Threading;
using TaskFocuser.Core;

namespace TaskFocuser;

/// <summary>
/// Round 4 of the state machine (docs/SPEC-round4.md 2.3): the typing chain, where the bird pecks a seed on
/// every character he types for a random stretch. Same rules as Conductor.cs: UI thread only, every module
/// call behind Guard or Fail.
/// </summary>
public sealed partial class Conductor
{
    // 2.3: a 20 % chance per key outside a chain, a chain of 2 to 30 characters, ended by 5 s without a key.
    const double ChainChance = 0.2;
    const int ChainMin = 2, ChainMaxExclusive = 31;   // Random.Next's upper bound is exclusive: 30 is included
    const int ChainIdleMs = 5000;

    // One timer for the whole app, re-armed per key: no allocation on the typing path.
    DispatcherTimer? chainTimer;
    int chainLeft;          // pecks left in the running chain; 0 = no chain

    void StartRound4()
    {
        chainTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ChainIdleMs) };
        chainTimer.Tick += (_, _) => Guard("chain timeout", EndChain);
        // Inline try/catch instead of Guard(..., OnKeyTyped): a method-group delegate would be allocated per key.
        note.KeyTyped += () => { try { OnKeyTyped(); } catch (Exception ex) { Fail("note.KeyTyped", ex); } };
    }

    /// <summary>Keys count only while the bird can visibly peck on an unlocked note: Note mode, unlocked,
    /// the bird out and no dock, pop or vanish running.</summary>
    bool KeysCount => state.Mode == Mode.Note && !state.Locked && !busy && monster.IsOut;

    void OnKeyTyped()
    {
        if (!KeysCount)
        {
            // A chain whose bird left or whose note locked under it ends; keys never start one in this state.
            EndChain();
            return;
        }
        if (chainLeft == 0)
        {
            // The key that wins the draw starts the chain; the N keys after it are the pecks.
            if (Random.Shared.NextDouble() >= ChainChance) return;
            chainLeft = Random.Shared.Next(ChainMin, ChainMaxExclusive);
            RearmChainTimer();
            return;
        }
        RearmChainTimer();
        chainLeft--;
        if (chainLeft == 0) chainTimer?.Stop();
        // Peck is a no-op mid-motion; the count still runs down, so the chain length stays in characters.
        monster.Peck();
    }

    void RearmChainTimer()
    {
        if (chainTimer is null) return;
        chainTimer.Stop();
        chainTimer.Start();
    }

    /// <summary>Ends any running chain; called by the timeout, lock, close and every sequence (tuck, pop, vanish).</summary>
    void EndChain()
    {
        chainLeft = 0;
        chainTimer?.Stop();
    }
}
