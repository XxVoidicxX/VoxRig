namespace VoxRig.Dsp;

// ---------------------------------------------------------------- Clean

public sealed class NoiseGate : Effect
{
    readonly Param _thr, _rel;
    float _env, _gain;
    int _hold;

    public NoiseGate() : base("Noise Gate", FxCategory.Clean, "Cuts background noise when you are not talking.")
    {
        _thr = Add("Threshold", -80, -10, -48, " dB");
        _rel = Add("Release", 20, 1000, 180, " ms");
    }

    public override void Reset() { _env = 0; _gain = 0; _hold = 0; }

    public override void Process(Span<float> b)
    {
        float t = DbToLin(_thr.Value), envC = Coef(30), att = Coef(1), rel = Coef(_rel.Value);
        int holdN = Ms(60);
        for (int i = 0; i < b.Length; i++)
        {
            float a = MathF.Abs(b[i]);
            _env = a > _env ? a : _env * envC + Anti;
            float target;
            if (_env > t) { _hold = holdN; target = 1; }
            else if (_hold > 0) { _hold--; target = 1; }
            else target = 0;
            _gain = target + (_gain - target) * (target > _gain ? att : rel);
            if (_gain < 1e-9f) _gain = 0;
            b[i] *= _gain;
        }
    }
}

public sealed class Equalizer : Effect
{
    readonly Param _cut, _bass, _mid, _midF, _treble;
    readonly Biquad _hp = new(), _ls = new(), _pk = new(), _hs = new();
    readonly float[] _k = new float[5];

    public Equalizer() : base("EQ", FxCategory.Clean, "Low cut plus bass, mid and treble shaping.")
    {
        _cut = Add("Low Cut", 20, 400, 80, " Hz");
        _bass = Add("Bass", -15, 15, 0, " dB", 1);
        _mid = Add("Mid", -15, 15, 0, " dB", 1);
        _midF = Add("Mid Freq", 200, 5000, 1200, " Hz");
        _treble = Add("Treble", -15, 15, 2, " dB", 1);
    }

    public override void Reset()
    {
        _hp.Reset(); _ls.Reset(); _pk.Reset(); _hs.Reset();
        Array.Fill(_k, float.NaN);
    }

    void Update()
    {
        float a = _cut.Value, b = _bass.Value, c = _mid.Value, d = _midF.Value, e = _treble.Value;
        if (a == _k[0] && b == _k[1] && c == _k[2] && d == _k[3] && e == _k[4]) return;
        _k[0] = a; _k[1] = b; _k[2] = c; _k[3] = d; _k[4] = e;
        _hp.HighPass(SampleRate, a);
        _ls.LowShelf(SampleRate, 150, b);
        _pk.Peak(SampleRate, d, 0.9, c);
        _hs.HighShelf(SampleRate, 5500, e);
    }

    public override void Process(Span<float> b)
    {
        Update();
        for (int i = 0; i < b.Length; i++)
            b[i] = _hs.Process(_pk.Process(_ls.Process(_hp.Process(b[i]))));
    }
}

public sealed class Compressor : Effect
{
    readonly Param _thr, _ratio, _att, _rel, _makeup;
    float _gr;

    public Compressor() : base("Compressor", FxCategory.Clean, "Evens out loud and quiet parts so you sit steady in the mix.")
    {
        _thr = Add("Threshold", -60, 0, -24, " dB");
        _ratio = Add("Ratio", 1, 20, 3, ":1", 1);
        _att = Add("Attack", 1, 100, 5, " ms");
        _rel = Add("Release", 20, 1000, 150, " ms");
        _makeup = Add("Makeup", 0, 24, 6, " dB", 1);
    }

    public override void Reset() { _gr = 0; }

    public override void Process(Span<float> b)
    {
        float thr = _thr.Value, slope = 1 - 1 / _ratio.Value, att = Coef(_att.Value), rel = Coef(_rel.Value);
        float makeup = _makeup.Value;
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            float db = 20f * MathF.Log10(MathF.Abs(x) + 1e-9f);
            float over = db - thr;
            float gr = over > 0 ? over * slope : 0;
            _gr = gr + (_gr - gr) * (gr > _gr ? att : rel);
            b[i] = x * DbToLin(makeup - _gr);
        }
    }
}

// ---------------------------------------------------------------- Voice

public sealed class PitchShifter : Effect
{
    readonly Param _semi, _grain, _mix;
    readonly DelayLine _d = new();
    double _phase;

    public PitchShifter() : base("Pitch", FxCategory.Voice, "Shift your voice down for demon, up for chipmunk. Low mix doubles you.")
    {
        _semi = Add("Semitones", -12, 12, -4, " st", 1);
        _grain = Add("Grain", 20, 100, 45, " ms");
        _mix = Add("Mix", 0, 100, 100, " %");
    }

    protected override void Allocate() => _d.Allocate(Ms(250));
    public override void Reset() { _d.Clear(); _phase = 0; }

    public override void Process(Span<float> b)
    {
        double ratio = Math.Pow(2, _semi.Value / 12.0);
        double w = _grain.Value * 0.001 * SampleRate;
        double step = (1 - ratio) / w;
        float mix = _mix.Value / 100f;
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            _d.Write(x);
            _phase += step;
            _phase -= Math.Floor(_phase);
            double p2 = _phase + 0.5; if (p2 >= 1) p2 -= 1;
            float g1 = (float)Math.Sin(Math.PI * _phase); g1 *= g1;
            float g2 = (float)Math.Sin(Math.PI * p2); g2 *= g2;
            float y = _d.Read(_phase * w + 1) * g1 + _d.Read(p2 * w + 1) * g2;
            b[i] = x + (y - x) * mix;
        }
    }
}

public sealed class Robot : Effect
{
    readonly Param _freq, _metal, _mix;
    readonly DelayLine _comb = new();
    double _ph;

    public Robot() : base("Robot", FxCategory.Voice, "Ring modulator plus metallic comb. Classic robot and dalek voices.")
    {
        _freq = Add("Freq", 20, 400, 90, " Hz");
        _metal = Add("Metal", 0, 95, 40, " %");
        _mix = Add("Mix", 0, 100, 100, " %");
    }

    protected override void Allocate() => _comb.Allocate(Ms(60));
    public override void Reset() { _comb.Clear(); _ph = 0; }

    public override void Process(Span<float> b)
    {
        float freq = _freq.Value, fb = _metal.Value / 100f, mix = _mix.Value / 100f;
        double inc = freq / SampleRate, d = SampleRate / (double)freq;
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            float y = x * (float)Math.Sin(2 * Math.PI * _ph) * 1.4f;
            _ph += inc; if (_ph >= 1) _ph -= 1;
            float c = y + _comb.Read(d) * fb;
            _comb.Write(c + Anti);
            y = c * (1 - fb * 0.6f);
            b[i] = x + (y - x) * mix;
        }
    }
}

// ---------------------------------------------------------------- Dirt

public sealed class Drive : Effect
{
    readonly Param _drive, _tone, _level, _mix;
    readonly Biquad _lp = new();
    float _lastTone = float.NaN;

    public Drive() : base("Drive", FxCategory.Dirt, "Saturation and distortion. Push it for a blown out mic.")
    {
        _drive = Add("Drive", 0, 40, 18, " dB");
        _tone = Add("Tone", 800, 12000, 5000, " Hz");
        _level = Add("Level", -24, 6, -4, " dB", 1);
        _mix = Add("Mix", 0, 100, 100, " %");
    }

    public override void Reset() { _lp.Reset(); _lastTone = float.NaN; }

    public override void Process(Span<float> b)
    {
        float tone = _tone.Value;
        if (tone != _lastTone) { _lastTone = tone; _lp.LowPass(SampleRate, tone); }
        float g = DbToLin(_drive.Value), lvl = DbToLin(_level.Value), mix = _mix.Value / 100f;
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            float y = _lp.Process(MathF.Tanh(x * g)) * lvl;
            b[i] = x + (y - x) * mix;
        }
    }
}

public sealed class Bitcrush : Effect
{
    readonly Param _bits, _down, _mix;
    int _count;
    float _held;

    public Bitcrush() : base("Bitcrush", FxCategory.Dirt, "Lower bit depth and sample rate for crunchy digital lo-fi.")
    {
        _bits = Add("Bits", 1, 16, 6);
        _down = Add("Downsample", 1, 40, 6, "x");
        _mix = Add("Mix", 0, 100, 100, " %");
    }

    public override void Reset() { _count = 0; _held = 0; }

    public override void Process(Span<float> b)
    {
        float levels = MathF.Pow(2, _bits.Value - 1);
        int down = (int)_down.Value;
        float mix = _mix.Value / 100f;
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            if (++_count >= down) { _count = 0; _held = MathF.Round(x * levels) / levels; }
            b[i] = x + (_held - x) * mix;
        }
    }
}

public sealed class Radio : Effect
{
    readonly Param _lo, _hi, _grit, _noise;
    readonly Biquad _hp1 = new(), _hp2 = new(), _lp1 = new(), _lp2 = new();
    float _klo = float.NaN, _khi = float.NaN;

    public Radio() : base("Radio", FxCategory.Dirt, "Walkie talkie and phone line. Narrow band, grit and static.")
    {
        _lo = Add("Low", 200, 1500, 450, " Hz");
        _hi = Add("High", 1500, 6000, 3000, " Hz");
        _grit = Add("Grit", 0, 100, 30, " %");
        _noise = Add("Noise", 0, 100, 15, " %");
    }

    public override void Reset()
    {
        _hp1.Reset(); _hp2.Reset(); _lp1.Reset(); _lp2.Reset();
        _klo = _khi = float.NaN;
    }

    public override void Process(Span<float> b)
    {
        float lo = _lo.Value, hi = _hi.Value;
        if (lo != _klo || hi != _khi)
        {
            _klo = lo; _khi = hi;
            _hp1.HighPass(SampleRate, lo, 0.54); _hp2.HighPass(SampleRate, lo, 1.31);
            _lp1.LowPass(SampleRate, hi, 0.54); _lp2.LowPass(SampleRate, hi, 1.31);
        }
        float n = _noise.Value / 100f; n = n * n * 0.08f;
        float g = 1 + _grit.Value / 100f * 6f, comp = 1.6f / MathF.Sqrt(g);
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i] + Rng.Bipolar() * n;
            float y = _lp2.Process(_lp1.Process(_hp2.Process(_hp1.Process(x))));
            b[i] = MathF.Tanh(y * g) * comp;
        }
    }
}

// ---------------------------------------------------------------- Space

public sealed class Modulation : Effect
{
    readonly Param _rate, _depth, _delay, _fb, _mix;
    readonly DelayLine _d = new();
    double _ph;

    public Modulation() : base("Modulation", FxCategory.Space, "Chorus, flanger and warble. Mix at 100 gives a seasick vibrato.")
    {
        _rate = Add("Rate", 0.05f, 8, 0.6f, " Hz", 2);
        _depth = Add("Depth", 0.1f, 10, 2.5f, " ms", 1);
        _delay = Add("Delay", 0.5f, 25, 7, " ms", 1);
        _fb = Add("Feedback", 0, 90, 25, " %");
        _mix = Add("Mix", 0, 100, 50, " %");
    }

    protected override void Allocate() => _d.Allocate(Ms(60));
    public override void Reset() { _d.Clear(); }

    public override void Process(Span<float> b)
    {
        double inc = _rate.Value / SampleRate;
        double baseS = _delay.Value * 0.001 * SampleRate, depthS = _depth.Value * 0.001 * SampleRate;
        float fb = _fb.Value / 100f, mix = _mix.Value / 100f;
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            double d = baseS + depthS * (0.5 + 0.5 * Math.Sin(2 * Math.PI * _ph));
            _ph += inc; if (_ph >= 1) _ph -= 1;
            float y = _d.Read(Math.Max(1, d));
            _d.Write(x + y * fb + Anti);
            b[i] = x + (y - x) * mix;
        }
    }
}

public sealed class Echo : Effect
{
    readonly Param _time, _fb, _tone, _mix;
    readonly DelayLine _d = new();
    readonly Biquad _lp = new();
    float _lastTone = float.NaN;
    double _ds;

    public Echo() : base("Echo", FxCategory.Space, "Delay with feedback. Moving the time knob live bends the pitch like tape.")
    {
        _time = Add("Time", 20, 2000, 380, " ms");
        _fb = Add("Feedback", 0, 95, 40, " %");
        _tone = Add("Tone", 500, 12000, 3500, " Hz");
        _mix = Add("Mix", 0, 100, 35, " %");
    }

    protected override void Allocate() => _d.Allocate(Ms(2100));

    public override void Reset()
    {
        _d.Clear(); _lp.Reset(); _lastTone = float.NaN;
        _ds = _time.Value * 0.001 * SampleRate;
    }

    public override void Process(Span<float> b)
    {
        float tone = _tone.Value;
        if (tone != _lastTone) { _lastTone = tone; _lp.LowPass(SampleRate, tone); }
        double target = _time.Value * 0.001 * SampleRate;
        float fb = _fb.Value / 100f, mix = _mix.Value / 100f;
        for (int i = 0; i < b.Length; i++)
        {
            _ds += (target - _ds) * 0.0002;
            float x = b[i];
            float y = _lp.Process(_d.Read(_ds));
            _d.Write(x + y * fb + Anti);
            b[i] = x + y * mix;
        }
    }
}

/// <summary>Freeverb style reverb with a long pre-delay for the late, lagging reverb sound.</summary>
public sealed class Reverb : Effect
{
    static readonly int[] CombTune = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    static readonly int[] ApTune = { 556, 441, 341, 225 };

    readonly Param _size, _damp, _pre, _mix;
    readonly DelayLine _pd = new();
    float[][] _comb = Array.Empty<float[]>(), _ap = Array.Empty<float[]>();
    int[] _ci = Array.Empty<int>(), _ai = Array.Empty<int>();
    float[] _fs = Array.Empty<float>();

    public Reverb() : base("Reverb", FxCategory.Space, "Room to cathedral. Turn up Pre-delay for a lagging reverb that lands late.")
    {
        _size = Add("Size", 0, 100, 70, " %");
        _damp = Add("Damping", 0, 100, 40, " %");
        _pre = Add("Pre-delay", 0, 500, 30, " ms");
        _mix = Add("Mix", 0, 100, 30, " %");
    }

    protected override void Allocate()
    {
        double s = SampleRate / 44100.0;
        _comb = CombTune.Select(t => new float[Math.Max(1, (int)(t * s))]).ToArray();
        _ap = ApTune.Select(t => new float[Math.Max(1, (int)(t * s))]).ToArray();
        _ci = new int[_comb.Length]; _ai = new int[_ap.Length]; _fs = new float[_comb.Length];
        _pd.Allocate(Ms(520));
    }

    public override void Reset()
    {
        foreach (var c in _comb) Array.Clear(c);
        foreach (var a in _ap) Array.Clear(a);
        Array.Clear(_ci); Array.Clear(_ai); Array.Clear(_fs);
        _pd.Clear();
    }

    public override void Process(Span<float> b)
    {
        float fb = _size.Value / 100f * 0.28f + 0.7f;
        float damp = _damp.Value / 100f * 0.4f, d1 = 1 - damp;
        double pre = Math.Max(1, _pre.Value * 0.001 * SampleRate);
        float mix = _mix.Value / 100f;
        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            _pd.Write(x);
            float inp = _pd.Read(pre) * 0.03f + Anti;
            float acc = 0;
            for (int c = 0; c < _comb.Length; c++)
            {
                var buf = _comb[c];
                int idx = _ci[c];
                float o = buf[idx];
                _fs[c] = o * d1 + _fs[c] * damp;
                buf[idx] = inp + _fs[c] * fb;
                if (++idx >= buf.Length) idx = 0;
                _ci[c] = idx;
                acc += o;
            }
            for (int a = 0; a < _ap.Length; a++)
            {
                var buf = _ap[a];
                int idx = _ai[a];
                float bo = buf[idx];
                buf[idx] = acc + bo * 0.5f;
                acc = bo - acc;
                if (++idx >= buf.Length) idx = 0;
                _ai[a] = idx;
            }
            b[i] = x * (1 - mix * 0.5f) + acc * mix * 3f;
        }
    }
}

// ---------------------------------------------------------------- Chaos

/// <summary>Constant cheap-mic character: hiss, mains hum, crackle, unstable level, clipping.</summary>
public sealed class BadMic : Effect
{
    readonly Param _hiss, _hum, _crackle, _flutter, _clip;
    double _humPh;
    float _pop, _flutG = 1, _flutTarget = 1, _hissLp;
    int _flutCount;

    public BadMic() : base("Bad Mic", FxCategory.Chaos, "Cheap mic character: hiss, 60 Hz hum, crackle, wobbly level and clipping.")
    {
        _hiss = Add("Hiss", 0, 100, 20, " %");
        _hum = Add("Hum", 0, 100, 0, " %");
        _crackle = Add("Crackle", 0, 100, 20, " %");
        _flutter = Add("Flutter", 0, 100, 10, " %");
        _clip = Add("Clip", 0, 100, 0, " %");
    }

    public override void Reset() { _pop = 0; _flutG = 1; _flutTarget = 1; _flutCount = 0; _hissLp = 0; }

    public override void Process(Span<float> b)
    {
        float h = _hiss.Value / 100f, hissA = h * h * 0.06f;
        float hu = _hum.Value / 100f, humA = hu * hu * 0.05f;
        float pPop = _crackle.Value / 100f * 35f / SampleRate;
        float flut = _flutter.Value / 100f, k = _clip.Value / 100f;
        double humInc = 60.0 / SampleRate;
        float flutC = 1 - Coef(6);

        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            if (k > 0) x = x * (1 - k) + Math.Clamp(x * (1 + 20 * k), -0.5f, 0.5f) * k;

            if (--_flutCount <= 0)
            {
                float depth = Rng.Next() < 0.15f ? 1f : 0.45f;
                _flutTarget = 1 - flut * Rng.Next() * depth;
                _flutCount = Ms(40 + Rng.Next() * 250);
            }
            _flutG += (_flutTarget - _flutG) * flutC;

            _hissLp += (Rng.Bipolar() - _hissLp) * 0.5f;

            double p = 2 * Math.PI * _humPh;
            _humPh += humInc; if (_humPh >= 1) _humPh -= 1;
            float hum = (float)(Math.Sin(p) + 0.5 * Math.Sin(3 * p) + 0.25 * Math.Sin(5 * p)) * humA;

            if (Rng.Next() < pPop) _pop = (0.15f + Rng.Next() * 0.6f) * (Rng.Next() < 0.5f ? -1 : 1);
            float pop = _pop;
            _pop *= -0.55f;
            if (MathF.Abs(_pop) < 1e-4f) _pop = 0;

            b[i] = x * _flutG + _hissLp * hissA + hum + pop;
        }
    }
}

/// <summary>Random fake mic failures: dropouts, stutters, packet-loss freezes, crackle bursts, digital garbage, muffles.</summary>
public sealed class Glitches : Effect
{
    enum Kind { None, Dropout, Stutter, Freeze, Crackle, Digital, Muffle }

    readonly Param _rate, _intensity;
    readonly Param[] _weights;
    readonly Biquad _muffle = new();
    float[] _hist = new float[2];
    int _mask = 1, _w, _contiguous;
    int _trigger;

    Kind _cur;
    int _pos, _total, _sliceStart, _sliceLen, _dsCount, _dsFactor;
    float _pop, _held, _levels;

    public Glitches() : base("Mic Glitches", FxCategory.Chaos, "Random fake failures. Weights pick which glitches happen. Ctrl+Alt+G fires one now.")
    {
        _rate = Add("Rate", 0, 60, 15, " /min");
        _intensity = Add("Intensity", 0, 100, 60, " %");
        _weights = new[]
        {
            Add("Dropout", 0, 100, 60, " %"),
            Add("Stutter", 0, 100, 50, " %"),
            Add("Freeze", 0, 100, 50, " %"),
            Add("Crackle", 0, 100, 40, " %"),
            Add("Digital", 0, 100, 40, " %"),
            Add("Muffle", 0, 100, 30, " %"),
        };
    }

    public void Trigger() => Interlocked.Exchange(ref _trigger, 1);

    public override bool ForceActive => Volatile.Read(ref _trigger) == 1 || _cur != Kind.None;

    protected override void Allocate()
    {
        int n = Pow2AtLeast(Ms(2000));
        _hist = new float[n]; _mask = n - 1;
    }

    public override void Reset()
    {
        Array.Clear(_hist); _w = 0; _contiguous = 0;
        _cur = Kind.None; _pop = 0; _muffle.Reset();
    }

    public override void Skip() { _contiguous = 0; }

    Kind Pick()
    {
        float sum = 0;
        foreach (var w in _weights) sum += w.Value;
        if (sum <= 0) return (Kind)(1 + (int)(Rng.Next() * 5.999f));
        float r = Rng.Next() * sum;
        for (int i = 0; i < _weights.Length; i++)
        {
            r -= _weights[i].Value;
            if (r <= 0) return (Kind)(i + 1);
        }
        return Kind.Dropout;
    }

    void Start(Kind kind)
    {
        float k = _intensity.Value / 100f;
        _cur = kind; _pos = 0;
        switch (kind)
        {
            case Kind.Dropout:
                _total = Ms(40 + 500 * k * Rng.Next() + 40 * k);
                break;
            case Kind.Stutter:
                _sliceLen = Ms(35 + 90 * Rng.Next());
                _total = _sliceLen * (2 + (int)(Rng.Next() * (1 + 6 * k)));
                _sliceStart = _w - _sliceLen;
                break;
            case Kind.Freeze:
                _sliceLen = Ms(8 + 18 * Rng.Next());
                _total = Ms(120 + 700 * k * Rng.Next());
                _sliceStart = _w - _sliceLen;
                break;
            case Kind.Crackle:
                _total = Ms(100 + 500 * k * Rng.Next());
                break;
            case Kind.Digital:
                _total = Ms(60 + 300 * k * Rng.Next());
                _dsFactor = 4 + (int)(Rng.Next() * 16 * k);
                _levels = MathF.Pow(2, 1 + (int)(Rng.Next() * 4));
                _dsCount = 0;
                break;
            case Kind.Muffle:
                _total = Ms(250 + 1500 * k * Rng.Next());
                _muffle.Reset();
                _muffle.LowPass(SampleRate, 350 + 350 * Rng.Next());
                break;
        }
    }

    public override void Process(Span<float> b)
    {
        float pStart = _rate.Value / 60f / SampleRate;
        int edge = Ms(3), warm = Ms(150);

        for (int i = 0; i < b.Length; i++)
        {
            float x = b[i];
            _hist[_w & _mask] = x;
            _w++;
            if (_contiguous < warm) _contiguous++;

            if (_cur == Kind.None && _contiguous >= warm)
            {
                if (Volatile.Read(ref _trigger) == 1) { Interlocked.Exchange(ref _trigger, 0); Start(Pick()); }
                else if (Enabled && Rng.Next() < pStart) Start(Pick());
            }
            if (_cur == Kind.None) continue;

            float f = Math.Min(1f, Math.Min(_pos / (float)edge, (_total - _pos) / (float)edge));
            float y = x;
            switch (_cur)
            {
                case Kind.Dropout:
                    y = x * (1 - f);
                    break;
                case Kind.Stutter:
                case Kind.Freeze:
                {
                    int p = _pos % _sliceLen;
                    float se = Math.Max(1, Math.Min(edge * 0.5f, _sliceLen / 4f));
                    float win = Math.Min(1f, Math.Min(p / se, (_sliceLen - p) / se));
                    float s = _hist[(_sliceStart + p) & _mask] * win;
                    if (_cur == Kind.Freeze) s *= 1 - 0.4f * _pos / _total;
                    y = x + (s - x) * f;
                    break;
                }
                case Kind.Crackle:
                    if (Rng.Next() < 0.004f) _pop = (0.2f + Rng.Next() * 0.6f) * (Rng.Next() < 0.5f ? -1 : 1);
                    float cut = Rng.Next() < 0.02f ? 0 : 1;
                    y = x * (1 - f + f * cut) + _pop * f;
                    _pop *= -0.6f;
                    break;
                case Kind.Digital:
                    if (++_dsCount >= _dsFactor)
                    {
                        _dsCount = 0;
                        _held = MathF.Round(x * 3f * _levels) / _levels * 0.5f;
                        if (Rng.Next() < 0.08f) _held = Rng.Bipolar() * 0.4f;
                    }
                    y = x + (_held - x) * f;
                    break;
                case Kind.Muffle:
                    y = x + (_muffle.Process(x) * 0.7f - x) * f;
                    break;
            }
            b[i] = y;
            if (++_pos >= _total) _cur = Kind.None;
        }
    }
}

/// <summary>Network lag: constant delay, jitter warble, and spikes where audio stalls then rushes to catch up.</summary>
public sealed class Lag : Effect
{
    enum State { Normal, Frozen, CatchUp }

    readonly Param _delay, _jitter, _spikes, _spikeLen, _catch;
    float[] _buf = new float[2];
    long _mask = 1, _w;
    double _r, _base, _jit, _jitTarget;
    int _jitCount, _frozenLeft;
    float _g = 1;
    State _state;

    public Lag() : base("Lag", FxCategory.Chaos, "Bad connection. Spikes stall your voice then speed it up to catch back up.")
    {
        _delay = Add("Delay", 0, 1500, 120, " ms");
        _jitter = Add("Jitter", 0, 100, 30, " %");
        _spikes = Add("Spikes", 0, 30, 6, " /min");
        _spikeLen = Add("Spike Len", 100, 2500, 600, " ms");
        _catch = Add("Catch-up", 10, 100, 50, " %");
    }

    protected override void Allocate()
    {
        int n = Pow2AtLeast(Ms(6000));
        _buf = new float[n]; _mask = n - 1;
    }

    public override void Reset()
    {
        Array.Clear(_buf);
        _w = 0; _base = _delay.Value * 0.001 * SampleRate; _r = -_base;
        _jit = _jitTarget = 0; _jitCount = 0; _state = State.Normal; _g = 1;
    }

    float Read(double pos)
    {
        long i = (long)Math.Floor(pos);
        float f = (float)(pos - i);
        float a = _buf[i & _mask], b = _buf[(i + 1) & _mask];
        return a + (b - a) * f;
    }

    public override void Process(Span<float> b)
    {
        double baseTarget = _delay.Value * 0.001 * SampleRate;
        double jitMax = _jitter.Value / 100.0 * 0.06 * SampleRate;
        float pSpike = _spikes.Value / 60f / SampleRate;
        double speed = 1 + _catch.Value / 100.0;
        float jc = 1 - Coef(80), gStep = 1f / Ms(5);
        long cap = _mask - Ms(50);

        for (int i = 0; i < b.Length; i++)
        {
            _buf[_w & _mask] = b[i];
            _w++;

            if (--_jitCount <= 0)
            {
                _jitTarget = Rng.Next() * jitMax;
                _jitCount = Ms(40 + Rng.Next() * 200);
            }
            _jit += (_jitTarget - _jit) * jc;
            _base += (baseTarget - _base) * 0.00005;
            double desired = Math.Max(2, _base + _jit);

            float gTarget = 1;
            switch (_state)
            {
                case State.Normal:
                    _r = _w - desired;
                    if (Rng.Next() < pSpike)
                    {
                        _state = State.Frozen;
                        _frozenLeft = Ms(_spikeLen.Value * (0.5 + Rng.Next()));
                    }
                    break;
                case State.Frozen:
                    gTarget = 0;
                    if (_g > 0) _r += 1;
                    if (--_frozenLeft <= 0) _state = State.CatchUp;
                    break;
                case State.CatchUp:
                    _r += speed;
                    if (_w - _r <= desired) { _state = State.Normal; _r = _w - desired; }
                    break;
            }
            if (_w - _r > cap) { _state = State.Normal; _r = _w - desired; }

            _g = _g < gTarget ? Math.Min(gTarget, _g + gStep) : Math.Max(gTarget, _g - gStep);
            b[i] = _g > 0 ? Read(_r) * _g : 0;
        }
    }
}

public static class FxFactory
{
    public static EffectChain CreateChain() => new(
        new NoiseGate(), new Equalizer(), new Compressor(),
        new PitchShifter(), new Robot(),
        new Drive(), new Bitcrush(), new Radio(),
        new Modulation(), new Echo(), new Reverb(),
        new BadMic(), new Glitches(), new Lag());
}
