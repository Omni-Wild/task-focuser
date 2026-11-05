namespace TaskFocuser.Core;

// Rori, the Japanese spitz (SPEC-round6 2.4). Her own partial and her own helpers, so the birds' render paths
// (AddNote, AddHoot, RenderRuffle) are never touched and their files stay the same bytes.
public static partial class Sound
{
    /// <summary>Rori's five: puppy-sized, round and soft, at the same peaks as the birds'. She plays at 10 % by
    /// default to keep Bluetooth headphones awake, so each one has to stay gentle at full volume too: nothing
    /// sharp above about 3 kHz, every sound swelling in and out of silence.</summary>
    private static short[] RenderRori(ChirpKind kind) => kind switch
    {
        ChirpKind.Keepalive => ToPcm(RoriWuf(), 0.50),
        ChirpKind.Happy => ToPcm(RoriYips(), 0.50),
        ChirpKind.Annoyed => ToPcm(RoriGrumble(), 0.40),
        ChirpKind.Ruffle => ToPcm(RoriShake(), RufflePeak),
        _ => ToPcm(RoriHm(), 0.45),
    };

    // Lead-in silence, the sound, then 20 ms of silent tail, as the birds' files are laid out.
    private static double[] RoriBuffer(double length) => new double[(int)((Lead + length + 0.020) * Rate)];

    // Raised-cosine attack, an exponential sag, a raised-cosine tail that reaches exactly zero at the end:
    // every one of her sounds starts and stops at silence, so nothing clicks.
    private static double Swell(double t, double length, double attack, double tail, double sag)
    {
        if (t <= 0 || t >= length) return 0;
        double env = t < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / attack) : Math.Exp(-(t - attack) / sag);
        double left = length - t;
        if (left < tail) env *= 0.5 - 0.5 * Math.Cos(Math.PI * left / tail);
        return env;
    }

    // A pitch glide on a log scale: equal steps in musical terms, which is how a voice bends.
    private static double Glide(double f0, double f1, double u) => f0 * Math.Pow(f1 / f0, u);

    // A raised-cosine bump of width w centred on c: the short puffs and swishes ride on it.
    private static double Bump(double t, double c, double w) =>
        Math.Abs(t - c) < w / 2 ? 0.5 + 0.5 * Math.Cos(2 * Math.PI * (t - c) / w) : 0;

    /// <summary>"Wuf": a small breathy bark, the pitch falling 650 to 380 Hz, a puff of 1.5 kHz air at the onset,
    /// a round low-passed body and a breath that grows toward the end (the "f").</summary>
    private static double[] RoriWuf()
    {
        const double length = 0.110;
        var buf = RoriBuffer(length);
        var rng = new Random(0x0D06);
        // The puff's band-pass leaks highs on its skirt; a low-pass after it keeps the onset a breath, not a tick.
        var puff = Biquad.BandPass(1500, 1.2);
        var puffSoft = Biquad.LowPass(2500, 0.707);
        var breath = Biquad.LowPass(1100, 0.707);
        // The body's harmonics through a low-pass: a mouth-shaped "u" rather than a buzz.
        var round = Biquad.LowPass(1600, 0.707);
        int first = (int)(Lead * Rate), count = (int)(length * Rate);
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Rate, u = t / length;
            // Most of the fall happens early, as a bark drops off its first push of air.
            phase += 2 * Math.PI * Glide(650, 380, Math.Sqrt(u)) / Rate;
            double white = rng.NextDouble() * 2 - 1;
            double body = round.Next(Math.Sin(phase) + 0.45 * Math.Sin(2 * phase) + 0.20 * Math.Sin(3 * phase)
                                     + 0.08 * Math.Sin(4 * phase));
            double air = breath.Next(white);
            double onset = puffSoft.Next(puff.Next(white)) * Bump(t, 0.005, 0.010);
            buf[first + i] = (body + (1.2 + 1.8 * u) * air) * Swell(t, length, 0.015, 0.060, 0.15) + 4.0 * onset;
        }
        return buf;
    }

    /// <summary>"Yip-yip": two bright 70 ms yips 90 ms apart, rising 900 to 1300 Hz, the second a whole tone
    /// higher, each with a little nip of noise at its onset.</summary>
    private static double[] RoriYips()
    {
        const double yip = 0.070, gap = 0.090;
        var buf = RoriBuffer(2 * yip + gap);
        // Fixed seed: the same nips on every machine and every render.
        var rng = new Random(0x0D07);
        AddYip(buf, Lead, yip, 900, 1300, rng);
        AddYip(buf, Lead + yip + gap, yip, 1010, 1460, rng);
        return buf;
    }

    private static void AddYip(double[] buf, double start, double length, double f0, double f1, Random rng)
    {
        var nip = Biquad.BandPass(2400, 1.0);
        var nipSoft = Biquad.LowPass(3200, 0.707);
        // Bright from the second and third harmonics, then low-passed so the top never turns shrill.
        var soft = Biquad.LowPass(3200, 0.707);
        int first = (int)(start * Rate), count = (int)(length * Rate);
        double phase = 0;
        for (int i = 0; i < count && first + i < buf.Length; i++)
        {
            double t = (double)i / Rate, u = t / length;
            // Up fast, easing at the top: a yip goes up and stays there, it does not slide.
            phase += 2 * Math.PI * Glide(f0, f1, 1 - (1 - u) * (1 - u)) / Rate;
            double tone = soft.Next(Math.Sin(phase) + 0.30 * Math.Sin(2 * phase) + 0.10 * Math.Sin(3 * phase));
            double onset = nipSoft.Next(nip.Next(rng.NextDouble() * 2 - 1)) * Bump(t, 0.004, 0.008);
            buf[first + i] += tone * Swell(t, length, 0.006, 0.030, 0.08) + 2.5 * onset;
        }
    }

    /// <summary>Annoyed: a short grumble that ends in one sharp "arf". It plays with the bark frame (mouth wide open
    /// from the third quick click on), so what he hears has to be a bark too: 150 ms of growl, a 30 ms gap, then a
    /// 90 ms bark brighter and stronger than the wuf. The 0.40 peak is the bark's; the growl sits under it.</summary>
    private static double[] RoriGrumble()
    {
        const double growl = 0.150, gap = 0.030, arf = 0.090;
        var buf = RoriBuffer(growl + gap + arf);
        // Fixed seed: the throat noise and the bark's onset are the same on every machine and every render.
        var rng = new Random(0x0D08);
        AddGrowl(buf, Lead, growl, 0.65, rng);
        AddArf(buf, Lead + growl + gap, arf, rng);
        return buf;
    }

    // The growl: a rough low tone gliding 130 to 105 Hz, its level rolled by a 24 Hz tremolo, with a little
    // band-limited throat noise.
    private static void AddGrowl(double[] buf, double start, double length, double gain, Random rng)
    {
        // A band-limited saw (eight harmonics) through a low-pass: a growl has to have overtones to be heard on
        // small speakers at all, and the low-pass keeps them from buzzing.
        var throat = Biquad.LowPass(850, 0.707);
        var rasp = Biquad.BandPass(420, 0.8);
        var amps = new double[9];
        for (int k = 1; k <= 8; k++) amps[k] = 1 / Math.Pow(k, 1.2);
        int first = (int)(start * Rate), count = (int)(length * Rate);
        double phase = 0;
        for (int i = 0; i < count && first + i < buf.Length; i++)
        {
            double t = (double)i / Rate, u = t / length;
            phase += 2 * Math.PI * Glide(130, 105, u) / Rate;
            double saw = 0;
            for (int k = 1; k <= 8; k++) saw += amps[k] * Math.Sin(k * phase);
            // The tremolo dips by up to 55 %: the rolling "rrr" of a puppy's growl, smooth so it never gates.
            double trem = 1 - 0.55 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * 24 * t));
            double s = throat.Next(saw) + 1.6 * rasp.Next(rng.NextDouble() * 2 - 1);
            buf[first + i] += gain * s * trem * Swell(t, length, 0.025, 0.050, 0.40);
        }
    }

    // The "arf": the wuf's recipe pushed brighter and sharper. Pitch falls 800 to 450 Hz, harmonics 2 to 5 are
    // stronger, a 2.4 kHz low-pass instead of 1.6 kHz, an 8 ms attack and a firmer puff of air at the onset. Still
    // nothing left above about 3 kHz, so it snaps without stinging at full volume.
    private static void AddArf(double[] buf, double start, double length, Random rng)
    {
        var puff = Biquad.BandPass(1800, 1.0);
        var puffSoft = Biquad.LowPass(3000, 0.707);
        var breath = Biquad.LowPass(1300, 0.707);
        var mouth = Biquad.LowPass(2400, 0.707);
        int first = (int)(start * Rate), count = (int)(length * Rate);
        double phase = 0;
        for (int i = 0; i < count && first + i < buf.Length; i++)
        {
            double t = (double)i / Rate, u = t / length;
            phase += 2 * Math.PI * Glide(800, 450, Math.Sqrt(u)) / Rate;
            double white = rng.NextDouble() * 2 - 1;
            double body = mouth.Next(Math.Sin(phase) + 0.60 * Math.Sin(2 * phase) + 0.40 * Math.Sin(3 * phase)
                                     + 0.25 * Math.Sin(4 * phase) + 0.12 * Math.Sin(5 * phase));
            double onset = puffSoft.Next(puff.Next(white)) * Bump(t, 0.005, 0.010);
            buf[first + i] += (body + 1.2 * breath.Next(white)) * Swell(t, length, 0.008, 0.045, 0.10) + 5.0 * onset;
        }
    }

    /// <summary>"Hm?": a tiny questioning whine, 90 ms of a soft tone rising 620 to 880 Hz, most of the rise at
    /// the end (the question), a slow 20 ms attack and a breath of air.</summary>
    private static double[] RoriHm()
    {
        const double length = 0.090;
        var buf = RoriBuffer(length);
        var rng = new Random(0x0D09);
        var breath = Biquad.LowPass(1000, 0.707);
        int first = (int)(Lead * Rate), count = (int)(length * Rate);
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Rate, u = t / length;
            phase += 2 * Math.PI * Glide(620, 880, u * u) / Rate;
            // Nearly a bare sine with a hint of second harmonic: a nasal hum, closed-mouthed.
            double s = Math.Sin(phase) + 0.12 * Math.Sin(2 * phase) + 0.5 * breath.Next(rng.NextDouble() * 2 - 1);
            buf[first + i] = s * Swell(t, length, 0.020, 0.035, 0.30);
        }
        return buf;
    }

    /// <summary>The shake-off, "fwup-fwup-fwup-fwup": soft low thumps of a 120 Hz burst (the ears) at about
    /// 9 Hz, slowing toward the end, each led by a swell of band-limited swish (the fur), 420 ms in all.</summary>
    private static double[] RoriShake()
    {
        const double length = 0.420;
        // Intervals of 104, 112 and 122 ms: about 9 Hz, the shake running down as it ends.
        double[] flaps = { 0.006, 0.110, 0.222, 0.344 };
        var buf = RoriBuffer(length);
        var rng = new Random(0x0D0A);
        var fur = Biquad.BandPass(850, 0.6);
        var furLow = Biquad.LowPass(1900, 0.707);
        int first = (int)(Lead * Rate), count = (int)(length * Rate);
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / Rate;
            double hiss = furLow.Next(fur.Next(rng.NextDouble() * 2 - 1));
            // The swish rises into each flap and falls away after it ("fw-up"), over a little steady fur.
            double swish = 0.25, thump = 0;
            foreach (double at in flaps)
            {
                swish += Bump(t, at - 0.012, 0.060);
                thump += Thump(t - at);
            }
            buf[first + i] = (thump + 2.2 * swish * hiss) * Swell(t, length, 0.010, 0.050, 0.80);
        }
        return buf;
    }

    // One ear flap: a 120 Hz burst that starts a little sharp and settles (the "wup"), with two harmonics so it is
    // heard on small speakers, a 3 ms attack, a 25 ms decay and a raised-cosine end at exactly zero by 80 ms.
    private static double Thump(double tau)
    {
        const double window = 0.080, attack = 0.003, end = 0.015, decay = 0.025;
        if (tau <= 0 || tau >= window) return 0;
        // The phase of 120 Hz * (1 + 0.25 e^(-tau/10 ms)), integrated in closed form so it is exact per sample.
        double p = 2 * Math.PI * 120 * (tau + 0.25 * 0.010 * (1 - Math.Exp(-tau / 0.010)));
        double s = Math.Sin(p) + 0.5 * Math.Sin(2 * p) + 0.25 * Math.Sin(3 * p);
        double env = tau < attack ? 0.5 - 0.5 * Math.Cos(Math.PI * tau / attack) : Math.Exp(-(tau - attack) / decay);
        double left = window - tau;
        if (left < end) env *= 0.5 - 0.5 * Math.Cos(Math.PI * left / end);
        return s * env;
    }
}
