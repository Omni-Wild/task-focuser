using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskFocuser.Core;

namespace TaskFocuser.Monster;

/// <summary>
/// The landing dust and the hearts (SPEC-round2 2.9, 2.10), and the seed on the ground of the
/// "eats a seed" fidget (SPEC-round4 2.3), drawn inside the bird's own window.
/// A fixed pool of glyph views made once, each with its own transform: a frame only writes
/// numbers into existing objects (no layout, no allocations), and when the last particle fades
/// the views collapse and <see cref="Active"/> goes false so the caller can drop its render hook.
/// The seed never moves, so it is not a particle: it needs no frames and does not keep
/// <see cref="Active"/> true. Coordinates are the window's own DIPs.
/// </summary>
internal sealed class Effects
{
    public const int MaxDust = 10;
    private const int HeartCount = 3;
    private const int Count = MaxDust + HeartCount;

    private const double DustLife = 0.30;    // seconds
    private const double DustDrag = 6.0;     // per second; the puff slows quickly like air does
    private const double HeartLife = 0.90;
    private const double HeartRise = 52;     // DIP over a heart's life
    private const double HeartStagger = 0.12;

    private readonly SpriteView[] views = new SpriteView[Count];
    private readonly TranslateTransform[] moves = new TranslateTransform[Count];
    private readonly double[] x = new double[Count], y = new double[Count];
    private readonly double[] vx = new double[Count], vy = new double[Count];
    private readonly double[] age = new double[Count], delay = new double[Count];
    private readonly double[] baseX = new double[Count], sway = new double[Count];
    private readonly bool[] alive = new bool[Count];
    private int aliveCount;

    private readonly double dustW, dustH, heartW, heartH;
    private readonly SpriteView seed;
    private readonly TranslateTransform seedMove = new();

    public Effects(Canvas layer)
    {
        for (int i = 0; i < Count; i++)
        {
            var t = new TranslateTransform();
            var v = new SpriteView
            {
                Frame = i < MaxDust ? Sprites.Dust : Sprites.Heart,
                RenderTransform = t,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            views[i] = v;
            moves[i] = t;
            layer.Children.Add(v);
        }
        dustW = Width(Sprites.Dust) * Sprites.CellSize;
        dustH = Sprites.Dust.Length * Sprites.CellSize;
        heartW = Width(Sprites.Heart) * Sprites.CellSize;
        heartH = Sprites.Heart.Length * Sprites.CellSize;
        // Added last so it lies over the dust: a landing puff must not hide the seed it is about to eat.
        seed = new SpriteView
        {
            Frame = Sprites.Seed,
            RenderTransform = seedMove,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        layer.Children.Add(seed);
    }

    /// <summary>True while any particle is still on its way; the caller ticks only then.</summary>
    public bool Active => aliveCount > 0;

    /// <summary>A puff sideways from the feet, half to each side (<paramref name="feet"/> is the feet line centre).</summary>
    public void BurstDust(int count, Point feet)
    {
        count = Math.Min(count, MaxDust);
        for (int i = 0; i < count; i++)
        {
            // Alternate sides so even three particles read as a burst both ways.
            double side = (i & 1) == 0 ? -1 : 1;
            double speed = 90 + Random.Shared.NextDouble() * 110;
            x[i] = feet.X - dustW / 2 + side * (4 + Random.Shared.NextDouble() * 8);
            y[i] = feet.Y - dustH - Random.Shared.NextDouble() * 4;
            vx[i] = side * speed;
            vy[i] = -(10 + Random.Shared.NextDouble() * 40);
            age[i] = 0;
            delay[i] = 0;
            Wake(i);
        }
    }

    /// <summary>Three hearts rising from above the bird's head and fading, a little apart in time and place.</summary>
    public void Hearts(Point head)
    {
        for (int h = 0; h < HeartCount; h++)
        {
            int i = MaxDust + h;
            baseX[i] = head.X - heartW / 2 + (h - 1) * 16 + (Random.Shared.NextDouble() - 0.5) * 4;
            x[i] = baseX[i];
            y[i] = head.Y - heartH;
            sway[i] = Random.Shared.NextDouble() * Math.PI * 2;
            age[i] = 0;
            // The middle heart first, then the sides: reads as one happy puff rather than a row.
            delay[i] = h == 1 ? 0 : HeartStagger * (h == 0 ? 1 : 2);
            Wake(i);
        }
    }

    /// <summary>Advances every live particle by <paramref name="dt"/> seconds.</summary>
    public void Tick(double dt)
    {
        if (aliveCount == 0) return;
        for (int i = 0; i < Count; i++)
        {
            if (!alive[i]) continue;
            if (delay[i] > 0)
            {
                delay[i] -= dt;
                continue;
            }
            age[i] += dt;
            if (i < MaxDust)
            {
                double f = Math.Exp(-DustDrag * dt);
                vx[i] *= f;
                vy[i] *= f;
                x[i] += vx[i] * dt;
                y[i] += vy[i] * dt;
                double p = age[i] / DustLife;
                if (p >= 1) { Sleep(i); continue; }
                Place(i, 1 - p);
            }
            else
            {
                double p = age[i] / HeartLife;
                if (p >= 1) { Sleep(i); continue; }
                // Ease out on the rise (y keeps the start height), a gentle sway, a fade over the last 60 %.
                double rise = HeartRise * (1 - (1 - p) * (1 - p));
                x[i] = baseX[i] + Math.Sin(sway[i] + p * 7) * 3;
                Place(i, p < 0.4 ? 1 : 1 - (p - 0.4) / 0.6, rise);
            }
        }
    }

    /// <summary>Puts the seed down with its top-left cell at <paramref name="topLeft"/>; no animation.</summary>
    public void ShowSeed(Point topLeft)
    {
        seedMove.X = topLeft.X;
        seedMove.Y = topLeft.Y;
        seed.Visibility = Visibility.Visible;
    }

    /// <summary>The seed is gone (eaten, or the fidget was cut short).</summary>
    public void HideSeed() => seed.Visibility = Visibility.Collapsed;

    /// <summary>Everything gone at once (the bird was hidden).</summary>
    public void Clear()
    {
        for (int i = 0; i < Count; i++)
            if (alive[i]) Sleep(i);
        HideSeed();
    }

    private void Wake(int i)
    {
        if (!alive[i])
        {
            alive[i] = true;
            aliveCount++;
        }
        // A delayed heart waits invisible; the first Place shows it.
        views[i].Opacity = 0;
        moves[i].X = x[i];
        moves[i].Y = y[i];
        views[i].Visibility = Visibility.Visible;
    }

    private void Place(int i, double opacity, double lift = 0)
    {
        moves[i].X = x[i];
        moves[i].Y = y[i] - lift;
        views[i].Opacity = opacity;
    }

    private void Sleep(int i)
    {
        alive[i] = false;
        aliveCount--;
        views[i].Visibility = Visibility.Collapsed;
    }

    private static int Width(string[] rows)
    {
        int w = 0;
        foreach (var r in rows) w = Math.Max(w, r.Length);
        return w;
    }
}

// The perched acts: short frame sequences on the spot, one at a time, on one single-shot timer
// (SPEC-round2 2.5 Flap, 2.9 Eat, 2.10 clicks and idle variations; SPEC-round3 2.3 the ruffle;
// SPEC-round4 2.3 the peck, whose entry and the fidget chooser live in MonsterWindow.Peck.cs). A hop,
// a shake or the ruffle's jitter moves only the sprite inside the window, so the window stays on the
// perch and MoveTo keeps working during an act. What a click means lives in MonsterWindow.Mood.cs.
// The step tables themselves are per bird (round 5) and live in MonsterWindow.Skin.cs (acts.*); an
// act that cuts a walk plays where the bird stands and the rest after it walks back (Walk.cs).
public partial class MonsterWindow
{
    // Ruffle is the public, sounding one; an idle fidget ruffle runs as Idle. Fume is the last annoyed
    // chirp before the sulk flight, which starts when it ends. Peck is the typing peck (Peck()); the
    // seed fidget runs as Idle like every other fidget, so anything that may cut a fidget cuts it.
    private enum Act { None, Eat, Hop, Annoyed, Idle, FlapBurst, Ruffle, Fume, Peck }
    // SeedShow and SeedHide put the fidget's ground seed down and pick it up (at seedSpot).
    private enum Cue { None, Hearts, Hop, IdleHop, Shake, Jitter, Settle, SeedShow, SeedHide }



    private readonly TranslateTransform spriteShift = new();
    private readonly DispatcherTimer actTimer = new(DispatcherPriority.Render);
    private readonly DispatcherTimer idleTimer = new(DispatcherPriority.Background);
    private readonly DoubleAnimation hopAnim = MakeHop(8);
    private readonly DoubleAnimation idleHopAnim = MakeHop(5);
    private readonly DoubleAnimationUsingKeyFrames shakeAnim = MakeShake();

    private Act act;
    private (string[] Frame, int Ms, Cue Cue)[] actSteps = Array.Empty<(string[] Frame, int Ms, Cue Cue)>();
    private int actStep;

    private void InitActs()
    {
        actTimer.Tick += OnActTick;
        idleTimer.Tick += OnIdleTick;
        awayTimer.Tick += OnAwayTick;
    }

    public void Eat()
    {
        // The cucumber makes peace at once (SPEC-round3 2.6), even when it cannot eat right now.
        ResetMood();
        // Ignored mid-flight, while carried or tumbling, and while he is dragging by the bird.
        if (!IsVisible || !IsOut || motion != Motion.None || carrying || grip.Tumbling || dragging) return;
        PlayAct(Act.Eat, acts.Eat);
    }

    public void Flap()
    {
        // The keep-alive's visible half: only a resting or fidgeting bird; any real activity wins.
        if (!IsVisible || !IsOut || motion != Motion.None || carrying || grip.Tumbling || pressed) return;
        if (act is not (Act.None or Act.Idle)) return;
        StopWalk(); // the burst flaps where it stands; the rest after it walks back
        StopAct();
        idleTimer.Stop();
        act = Act.FlapBurst;
        StartFlap();
        actTimer.Interval = TimeSpan.FromMilliseconds(700);
        actTimer.Start();
    }

    private void PlayAct(Act kind, (string[] Frame, int Ms, Cue Cue)[] steps)
    {
        // Every act cuts a walk and plays where the bird stands; EnterRest walks it home afterwards.
        StopWalk();
        StopAct();
        StopLoop();
        idleTimer.Stop();
        blinkTimer.Stop();
        blinking = false;
        Sprite.FlipX = false;
        act = kind;
        actSteps = steps;
        actStep = -1;
        NextStep();
    }

    private void NextStep()
    {
        if (++actStep >= actSteps.Length)
        {
            EndAct();
            return;
        }
        var s = actSteps[actStep];
        Sprite.Frame = s.Frame;
        switch (s.Cue)
        {
            case Cue.Hearts:
                // From just above the head, in the window's own coordinates.
                effects.Hearts(new Point(WinW / 2, SpriteY + 3 * Sprites.CellSize));
                ChirpRequested?.Invoke(ChirpKind.Happy);
                UpdateRenderHook();
                break;
            case Cue.Hop:
                spriteShift.BeginAnimation(TranslateTransform.YProperty, hopAnim);
                break;
            case Cue.IdleHop:
                spriteShift.BeginAnimation(TranslateTransform.YProperty, idleHopAnim);
                break;
            case Cue.Shake:
                spriteShift.BeginAnimation(TranslateTransform.XProperty, shakeAnim);
                ChirpRequested?.Invoke(ChirpKind.Annoyed);
                break;
            case Cue.Jitter:
                // One DIP either way on alternate frames: with the two poses it reads as a shiver.
                spriteShift.X = (actStep & 1) == 0 ? 1 : -1;
                break;
            case Cue.Settle:
                spriteShift.X = 0;
                break;
            case Cue.SeedShow:
                effects.ShowSeed(seedSpot);
                break;
            case Cue.SeedHide:
                effects.HideSeed();
                break;
        }
        // A handler above may have hidden the bird (StopEverything ended the act).
        if (act == Act.None) return;
        actTimer.Interval = TimeSpan.FromMilliseconds(s.Ms);
        actTimer.Start();
    }

    private void OnActTick(object? sender, EventArgs e)
    {
        actTimer.Stop();
        if (act == Act.None) return;
        if (act == Act.FlapBurst) EndAct();
        else NextStep();
    }

    private void EndAct()
    {
        var ended = act;
        // Read before StopAct, which drops the queued peck.
        bool peckAgain = ended == Act.Peck && peckQueued;
        StopAct();
        if (!IsVisible) return;
        // The fifth scream is over: off it goes, unless something now holds it (then it just sits).
        if (ended == Act.Fume && StartFlyOff()) return;
        // The one peck that came in during this one: straight into it, so typing reads as one run.
        if (peckAgain)
        {
            PlayAct(Act.Peck, acts.Peck);
            return;
        }
        EnterRest();
    }

    /// <summary>Ends the running act on the spot without choosing what comes next.</summary>
    private void StopAct()
    {
        actTimer.Stop();
        // Whatever cut the act also drops the queued peck and the seed on the ground, so nothing of
        // it lingers into the next act (a cut seed fidget would otherwise leave its seed behind).
        peckQueued = false;
        effects.HideSeed();
        if (act == Act.None) return;
        act = Act.None;
        spriteShift.BeginAnimation(TranslateTransform.XProperty, null);
        spriteShift.BeginAnimation(TranslateTransform.YProperty, null);
        spriteShift.X = 0;
        spriteShift.Y = 0;
    }

    private static DoubleAnimation MakeHop(double height)
    {
        // Up and back down in 180 ms: ease out on the way up, the reverse eases in on the way down.
        var a = new DoubleAnimation(0, -height, TimeSpan.FromMilliseconds(90))
        {
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        a.Freeze();
        return a;
    }

    private static DoubleAnimationUsingKeyFrames MakeShake()
    {
        // Three cycles of 2 DIP side to side, then back to centre.
        var a = new DoubleAnimationUsingKeyFrames();
        double[] xs = { 2, -2, 2, -2, 2, -2, 0 };
        for (int i = 0; i < xs.Length; i++)
            a.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(30 * (i + 1)))));
        a.Freeze();
        return a;
    }

    /// <summary>The landing dust of 2.10: more the longer nothing landed on the note, none right after a landing.</summary>
    private void Land()
    {
        long now = Environment.TickCount64;
        int n = DustFor(now - lastLanding);
        lastLanding = now;
        if (n > 0) effects.BurstDust(n, new Point(WinW / 2, WinH));
    }

    private static int DustFor(long sinceMs)
    {
        if (sinceMs < 2000) return 0;
        double s = sinceMs / 1000.0;
        if (s <= 60) return 3;
        // Linear in log time: 3 at a minute, 10 at an hour and beyond.
        double t = Math.Min(1, Math.Log(s / 60) / Math.Log(60));
        return 3 + (int)Math.Round(7 * t);
    }
}
