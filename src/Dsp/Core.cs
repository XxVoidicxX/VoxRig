namespace VoxRig.Dsp;

public enum FxCategory { Clean, Voice, Dirt, Space, Chaos }

/// <summary>A single knob. Written by the UI thread, read by the audio thread.</summary>
public sealed class Param
{
    float _value;

    public Param(string name, float min, float max, float def, string unit, int decimals)
    {
        Name = name; Min = min; Max = max; Default = def; Unit = unit; Decimals = decimals;
        _value = def;
    }

    public string Name { get; }
    public float Min { get; }
    public float Max { get; }
    public float Default { get; }
    public string Unit { get; }
    public int Decimals { get; }

    public float Value
    {
        get => Volatile.Read(ref _value);
        set => Volatile.Write(ref _value, Math.Clamp(value, Min, Max));
    }

    public string Display => Value.ToString("F" + Decimals) + Unit;
}

public abstract class Effect
{
    int _enabled;
    protected readonly FastRandom Rng = new();
    protected const float Anti = 1e-20f; // keeps feedback loops out of denormal territory

    protected Effect(string name, FxCategory category, string description)
    {
        Name = name; Category = category; Description = description;
    }

    public string Name { get; }
    public FxCategory Category { get; }
    public string Description { get; }
    public List<Param> Params { get; } = new();
    public int SampleRate { get; private set; } = 48000;

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) == 1;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    /// <summary>True when the effect must run even though it is switched off (manual glitch trigger).</summary>
    public virtual bool ForceActive => false;

    public Param Find(string name) => Params.Find(p => p.Name == name);

    protected Param Add(string name, float min, float max, float def, string unit = "", int decimals = 0)
    {
        var p = new Param(name, min, max, def, unit, decimals);
        Params.Add(p);
        return p;
    }

    public void Prepare(int sampleRate)
    {
        SampleRate = sampleRate;
        Allocate();
        Reset();
    }

    protected virtual void Allocate() { }
    public virtual void Reset() { }
    /// <summary>Called instead of Process when the chain skips this effect.</summary>
    public virtual void Skip() { }
    public abstract void Process(Span<float> buf);

    protected int Ms(double ms) => Math.Max(1, (int)(ms * 0.001 * SampleRate));
    protected float Coef(double ms) => (float)Math.Exp(-1.0 / Math.Max(1e-6, ms * 0.001 * SampleRate));
    public static float DbToLin(float db) => MathF.Pow(10f, db / 20f);
    public static int Pow2AtLeast(int n) { int p = 1; while (p < n) p <<= 1; return p; }
}

public sealed class FastRandom
{
    uint _s = (uint)Random.Shared.Next(1, int.MaxValue);

    public uint NextU()
    {
        uint x = _s;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        _s = x;
        return x;
    }

    /// <summary>0..1</summary>
    public float Next() => (NextU() >> 8) * (1f / 16777216f);
    /// <summary>-1..1</summary>
    public float Bipolar() => Next() * 2f - 1f;
}

public sealed class Biquad
{
    double _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;

    public void Reset() { _z1 = _z2 = 0; }

    public float Process(float x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        if (Math.Abs(_z1) < 1e-25) _z1 = 0;
        if (Math.Abs(_z2) < 1e-25) _z2 = 0;
        return (float)y;
    }

    void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    static double W(double sr, double f) => 2 * Math.PI * Math.Clamp(f, 10, sr * 0.45) / sr;

    public void LowPass(double sr, double f, double q = 0.7071)
    {
        double w = W(sr, f), c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
        Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
    }

    public void HighPass(double sr, double f, double q = 0.7071)
    {
        double w = W(sr, f), c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
        Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
    }

    public void Peak(double sr, double f, double q, double db)
    {
        double a = Math.Pow(10, db / 40), w = W(sr, f), c = Math.Cos(w), al = Math.Sin(w) / (2 * q);
        Set(1 + al * a, -2 * c, 1 - al * a, 1 + al / a, -2 * c, 1 - al / a);
    }

    public void LowShelf(double sr, double f, double db)
    {
        double a = Math.Pow(10, db / 40), w = W(sr, f), c = Math.Cos(w);
        double sq = 2 * Math.Sqrt(a) * Math.Sin(w) / 2 * Math.Sqrt(2);
        Set(a * ((a + 1) - (a - 1) * c + sq), 2 * a * ((a - 1) - (a + 1) * c), a * ((a + 1) - (a - 1) * c - sq),
            (a + 1) + (a - 1) * c + sq, -2 * ((a - 1) + (a + 1) * c), (a + 1) + (a - 1) * c - sq);
    }

    public void HighShelf(double sr, double f, double db)
    {
        double a = Math.Pow(10, db / 40), w = W(sr, f), c = Math.Cos(w);
        double sq = 2 * Math.Sqrt(a) * Math.Sin(w) / 2 * Math.Sqrt(2);
        Set(a * ((a + 1) + (a - 1) * c + sq), -2 * a * ((a - 1) + (a + 1) * c), a * ((a + 1) + (a - 1) * c - sq),
            (a + 1) - (a - 1) * c + sq, 2 * ((a - 1) - (a + 1) * c), (a + 1) - (a - 1) * c - sq);
    }
}

/// <summary>Power-of-two circular delay with fractional reads.</summary>
public sealed class DelayLine
{
    float[] _buf = new float[2];
    int _mask = 1, _w;

    public void Allocate(int minSamples)
    {
        int n = Effect.Pow2AtLeast(Math.Max(2, minSamples));
        _buf = new float[n]; _mask = n - 1; _w = 0;
    }

    public void Clear() { Array.Clear(_buf); _w = 0; }

    public void Write(float x) { _buf[_w] = x; _w = (_w + 1) & _mask; }

    /// <summary>Read(1) returns the most recently written sample.</summary>
    public float Read(double delay)
    {
        double p = _w - delay;
        int i = (int)Math.Floor(p);
        float f = (float)(p - i);
        float a = _buf[i & _mask], b = _buf[(i + 1) & _mask];
        return a + (b - a) * f;
    }
}

/// <summary>Runs effects in order with click-free 15 ms crossfades when they are toggled.</summary>
public sealed class EffectChain
{
    readonly Effect[] _fx;
    float[] _mix;
    float[] _dry = new float[16384];
    int _sr = 48000;

    public EffectChain(params Effect[] fx)
    {
        _fx = fx;
        _mix = new float[fx.Length];
    }

    public IReadOnlyList<Effect> Effects => _fx;

    public T Get<T>() where T : Effect => _fx.OfType<T>().FirstOrDefault();
    public Effect Find(string name) => Array.Find(_fx, e => e.Name == name);

    public void Prepare(int sampleRate)
    {
        _sr = sampleRate;
        for (int i = 0; i < _fx.Length; i++)
        {
            _fx[i].Prepare(sampleRate);
            _mix[i] = _fx[i].Enabled ? 1 : 0;
        }
    }

    public void ResetAll() { foreach (var e in _fx) e.Reset(); }

    public void Process(Span<float> buf)
    {
        if (_dry.Length < buf.Length) _dry = new float[buf.Length * 2];
        float step = 1f / (0.015f * _sr);

        for (int k = 0; k < _fx.Length; k++)
        {
            var e = _fx[k];
            float target = e.Enabled ? 1 : 0;
            float m = _mix[k];

            if (target == 0 && m == 0)
            {
                if (e.ForceActive) e.Process(buf); else e.Skip();
                continue;
            }
            if (m == 0) e.Reset();
            if (m == 1 && target == 1) { e.Process(buf); continue; }

            var dry = _dry.AsSpan(0, buf.Length);
            buf.CopyTo(dry);
            e.Process(buf);
            for (int i = 0; i < buf.Length; i++)
            {
                m = m < target ? Math.Min(target, m + step) : Math.Max(target, m - step);
                buf[i] = dry[i] + (buf[i] - dry[i]) * m;
            }
            _mix[k] = m;
        }
    }
}
