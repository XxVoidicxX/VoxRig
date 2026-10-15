using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using VoxRig.Audio;

namespace VoxRig;

/// <summary>Real-time spectrum analyzer and IN/OUT level meters, drawn every frame.</summary>
public sealed class Visualizer : FrameworkElement
{
    const int N = 2048, Bands = 72;
    const float FloorDb = -60f;

    public AudioEngine Engine { get; set; }
    public bool Demo { get; set; }

    readonly float[] _in = new float[N], _out = new float[N], _re = new float[N], _im = new float[N], _win = new float[N];
    readonly float[] _barOut = new float[Bands], _barIn = new float[Bands], _peak = new float[Bands], _peakVel = new float[Bands];
    readonly double[] _edges = new double[Bands + 1];
    readonly Meter _mIn = new(), _mOut = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _last, _demoT, _fMin, _fMax;
    int _rate;
    TimeSpan _lastRender;

    Brush _barBrush, _meterBrush;
    double _brushTop = -1, _brushBottom = -1;
    static readonly Brush Bg = Frozen(new SolidColorBrush(Color.FromRgb(7, 9, 13)));
    static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromRgb(20, 24, 32)));
    static readonly Brush Cap = Frozen(new SolidColorBrush(Color.FromArgb(230, 240, 245, 255)));
    static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromRgb(110, 118, 135)));
    static readonly Brush TextB = Frozen(new SolidColorBrush(Color.FromRgb(225, 230, 240)));
    static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(244, 63, 94)));
    static readonly Brush Amber = Frozen(new SolidColorBrush(Color.FromRgb(250, 204, 21)));
    static readonly Brush ClipOff = Frozen(new SolidColorBrush(Color.FromRgb(45, 22, 30)));
    static readonly Pen Grid = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1));
    static readonly Pen InLine = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.4) { LineJoin = PenLineJoin.Round });
    static readonly Pen HoldPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(255, 255, 255)), 2));
    static readonly Typeface Font = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface FontBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    sealed class Meter
    {
        public float PeakDb = FloorDb, RmsDb = FloorDb, HoldDb = FloorDb;
        public double HoldAge, ClipAge = 99;

        public void Update(float[] src, int n, float dt)
        {
            float pk = 0; double sq = 0;
            for (int i = src.Length - n; i < src.Length; i++)
            {
                float a = MathF.Abs(src[i]);
                if (a > pk) pk = a;
                sq += a * a;
            }
            float pDb = ToDb(pk), rDb = ToDb((float)Math.Sqrt(sq / Math.Max(1, n)));
            PeakDb = pDb > PeakDb ? pDb : Math.Max(pDb, PeakDb - 30 * dt);
            RmsDb = RmsDb + (rDb - RmsDb) * Math.Min(1, dt * 8);
            if (PeakDb >= HoldDb) { HoldDb = PeakDb; HoldAge = 0; }
            else if ((HoldAge += dt) > 1.5) HoldDb = Math.Max(FloorDb, HoldDb - 20 * dt);
            if (pk >= 0.99f) ClipAge = 0; else ClipAge += dt;
        }

        public void Decay(float dt)
        {
            PeakDb = Math.Max(FloorDb, PeakDb - 40 * dt);
            RmsDb = Math.Max(FloorDb, RmsDb - 40 * dt);
            HoldDb = Math.Max(FloorDb, HoldDb - 40 * dt);
            ClipAge += dt;
        }
    }

    public Visualizer()
    {
        for (int i = 0; i < N; i++) _win[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (N - 1));
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        SetRate(48000);
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    static float ToDb(float a) => Math.Max(FloorDb, 20f * MathF.Log10(a + 1e-9f));

    void SetRate(int rate)
    {
        if (rate == _rate) return;
        _rate = rate;
        _fMin = 35; _fMax = Math.Min(18000, rate * 0.45);
        for (int k = 0; k <= Bands; k++) _edges[k] = _fMin * Math.Pow(_fMax / _fMin, k / (double)Bands);
    }

    void OnFrame(object sender, EventArgs e)
    {
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _lastRender) return;
            _lastRender = r.RenderingTime;
        }
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _last, 0.001, 0.1);
        _last = now;

        bool live = false;
        if (Demo) { FillDemo(dt); live = true; }
        else if (Engine is { IsRunning: true })
        {
            SetRate(Engine.SampleRate);
            Engine.InScope.CopyLatest(_in);
            Engine.OutScope.CopyLatest(_out);
            live = true;
        }

        if (live)
        {
            int n = Math.Clamp((int)(_rate * dt), 128, N);
            _mIn.Update(_in, n, dt);
            _mOut.Update(_out, n, dt);
            Analyze(_out, _barOut, dt);
            Analyze(_in, _barIn, dt);
        }
        else
        {
            _mIn.Decay(dt); _mOut.Decay(dt);
            for (int k = 0; k < Bands; k++)
            {
                _barOut[k] = Math.Max(0, _barOut[k] - dt * 1.5f);
                _barIn[k] = Math.Max(0, _barIn[k] - dt * 1.5f);
            }
        }
        for (int k = 0; k < Bands; k++)
        {
            if (_barOut[k] >= _peak[k]) { _peak[k] = _barOut[k]; _peakVel[k] = 0; }
            else { _peakVel[k] += dt * 1.6f; _peak[k] = Math.Max(0, _peak[k] - _peakVel[k] * dt); }
        }
        InvalidateVisual();
    }

    void FillDemo(float dt)
    {
        double t0 = _demoT;
        _demoT += dt;
        double f0 = 135 + 25 * Math.Sin(t0 * 2.1);
        double env = 0.25 + 0.2 * Math.Sin(t0 * 5.3) * Math.Sin(t0 * 1.7);
        for (int i = 0; i < N; i++)
        {
            double t = t0 + i / 48000.0, s = 0;
            for (int h = 1; h < 40; h++)
            {
                double f = f0 * h;
                double formant = Math.Exp(-Math.Pow((f - 650) / 300, 2)) + 0.6 * Math.Exp(-Math.Pow((f - 1700) / 500, 2)) +
                                 0.25 * Math.Exp(-Math.Pow((f - 3000) / 900, 2)) + 0.08;
                s += Math.Sin(2 * Math.PI * f * t) * formant / h;
            }
            _in[i] = (float)(s * env);
            _out[i] = (float)(s * env * 1.4 + (Random.Shared.NextDouble() - 0.5) * 0.004);
        }
    }

    void Analyze(float[] src, float[] bars, float dt)
    {
        for (int i = 0; i < N; i++) { _re[i] = src[i] * _win[i]; _im[i] = 0; }
        Fft(_re, _im);
        double binHz = _rate / (double)N;
        int half = N / 2 - 1;
        for (int k = 0; k < Bands; k++)
        {
            double b0 = _edges[k] / binHz, b1 = _edges[k + 1] / binHz;
            double mag;
            if (b1 - b0 < 1)
            {
                double c = (b0 + b1) / 2;
                int j = Math.Min((int)c, half - 1);
                double f = c - j;
                mag = Mag(j) * (1 - f) + Mag(j + 1) * f;
            }
            else
            {
                mag = 0;
                int jEnd = Math.Min(half, (int)Math.Ceiling(b1));
                for (int j = (int)b0; j <= jEnd; j++) mag = Math.Max(mag, Mag(j));
            }
            double fc = Math.Sqrt(_edges[k] * _edges[k + 1]);
            double db = 20 * Math.Log10(mag * 4.0 / N + 1e-9) + 3 * Math.Log2(fc / 1000);
            float norm = (float)Math.Clamp((db + 72) / 72, 0, 1);
            bars[k] = norm > bars[k] ? bars[k] + (norm - bars[k]) * 0.7f : Math.Max(norm, bars[k] - dt * 1.4f);
        }
    }

    double Mag(int j) => Math.Sqrt(_re[j] * _re[j] + _im[j] * _im[j]);

    static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    float nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
    }

    FormattedText Text(string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? FontBold : Font, size, b,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    void EnsureBrushes(double top, double bottom)
    {
        if (top == _brushTop && bottom == _brushBottom) return;
        _brushTop = top; _brushBottom = bottom;
        _barBrush = Frozen(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(34, 211, 238), 1.0),
            new(Color.FromRgb(139, 92, 246), 0.5),
            new(Color.FromRgb(255, 77, 141), 0.0),
        }, new Point(0, top), new Point(0, bottom)) { MappingMode = BrushMappingMode.Absolute });
        _meterBrush = Frozen(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(74, 222, 128), 1.0),
            new(Color.FromRgb(74, 222, 128), 0.35),
            new(Color.FromRgb(250, 204, 21), 0.2),
            new(Color.FromRgb(244, 63, 94), 0.0),
        }, new Point(0, top), new Point(0, bottom)) { MappingMode = BrushMappingMode.Absolute });
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 200 || h < 80) return;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));

        const double meterArea = 190;
        double left = 16, top = 34, bottom = h - 26;
        double specW = w - meterArea - left - 8, specH = bottom - top;
        EnsureBrushes(top, bottom);

        // Grid + frequency labels
        for (int i = 1; i < 4; i++)
        {
            double y = top + specH * i / 4;
            dc.DrawLine(Grid, new Point(left, y), new Point(left + specW, y));
        }
        foreach (var (f, label) in new[] { (100.0, "100"), (500.0, "500"), (1000.0, "1k"), (5000.0, "5k"), (10000.0, "10k") })
        {
            if (f > _fMax) continue;
            double x = left + specW * Math.Log(f / _fMin) / Math.Log(_fMax / _fMin);
            dc.DrawLine(Grid, new Point(x, top), new Point(x, bottom));
            var t = Text(label, 10, Muted);
            dc.DrawText(t, new Point(x - t.Width / 2, bottom + 6));
        }

        // Output spectrum bars + falling peak caps
        double slot = specW / Bands, bw = Math.Max(1, slot * 0.7);
        for (int k = 0; k < Bands; k++)
        {
            double x = left + k * slot + (slot - bw) / 2;
            double bh = _barOut[k] * specH;
            if (bh > 0.5) dc.DrawRoundedRectangle(_barBrush, null, new Rect(x, bottom - bh, bw, bh), 1.5, 1.5);
            if (_peak[k] > 0.01) dc.DrawRectangle(Cap, null, new Rect(x, bottom - _peak[k] * specH - 2, bw, 2));
        }

        // Input spectrum as an outline so you can see what the FX changed
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (int k = 0; k < Bands; k++)
            {
                var p = new Point(left + (k + 0.5) * slot, bottom - _barIn[k] * specH);
                if (k == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, InLine, geo);

        // Header legend + state badges
        dc.DrawText(Text("SPECTRUM", 11, Muted, true), new Point(left, 10));
        dc.DrawRectangle(_barBrush, null, new Rect(left + 78, 15, 10, 8));
        dc.DrawText(Text("output", 11, Muted), new Point(left + 92, 10));
        dc.DrawLine(InLine, new Point(left + 140, 19), new Point(left + 154, 19));
        dc.DrawText(Text("input", 11, Muted), new Point(left + 158, 10));

        double bx = left + specW;
        bool running = Demo || Engine is { IsRunning: true };
        if (Engine is { Muted: true }) bx = Badge(dc, "MUTED", Red, bx);
        if (Engine is { Bypass: true }) bx = Badge(dc, "FX BYPASSED", Amber, bx);
        if (!running)
        {
            var t = Text("STOPPED. Pick your mic and cable, then press START.", 14, Muted);
            dc.DrawText(t, new Point(left + (specW - t.Width) / 2, top + specH / 2 - t.Height / 2));
        }

        // Level meters
        double mx = w - meterArea + 18;
        double scaleX = mx + 36;
        DrawMeter(dc, _mIn, "MIC IN", mx, top, bottom);
        DrawMeter(dc, _mOut, "OUT", mx + 70, top, bottom);
        foreach (int db in new[] { 0, -6, -12, -18, -24, -36, -48, -60 })
        {
            double y = DbY(db, top, bottom);
            var t = Text(db.ToString(), 9, Muted);
            dc.DrawText(t, new Point(scaleX + 17 - t.Width / 2, y - t.Height / 2));
        }

        // Big loudness readout
        var big = Text(_mIn.RmsDb <= FloorDb + 0.5 ? "-inf" : _mIn.RmsDb.ToString("F1"), 22, TextB, true);
        dc.DrawText(Text("MIC dB", 10, Muted, true), new Point(w - 58, top));
        dc.DrawText(big, new Point(w - 14 - big.Width, top + 14));
    }

    double Badge(DrawingContext dc, string s, Brush b, double right)
    {
        var t = Text(s, 11, b, true);
        double x = right - t.Width - 14;
        dc.DrawRoundedRectangle(null, new Pen(b, 1.2), new Rect(x, 8, t.Width + 14, 20), 10, 10);
        dc.DrawText(t, new Point(x + 7, 10));
        return x - 8;
    }

    static double DbY(float db, double top, double bottom) =>
        bottom - (bottom - top) * Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    void DrawMeter(DrawingContext dc, Meter m, string label, double x, double top, double bottom)
    {
        const double mw = 30;
        double clipH = 8, mt = top + clipH + 4;
        dc.DrawRoundedRectangle(m.ClipAge < 1.5 ? Red : ClipOff, null, new Rect(x, top, mw, clipH), 2, 2);
        dc.DrawRoundedRectangle(Track, null, new Rect(x, mt, mw, bottom - mt), 3, 3);

        double rmsY = DbY(m.RmsDb, mt, bottom), pkY = DbY(m.PeakDb, mt, bottom);
        var dim = _meterBrush.Clone();
        dim.Opacity = 0.35;
        dim.Freeze();
        if (bottom - pkY > 0.5) dc.DrawRectangle(dim, null, new Rect(x + 2, pkY, mw - 4, bottom - pkY));
        if (bottom - rmsY > 0.5) dc.DrawRectangle(_meterBrush, null, new Rect(x + 2, rmsY, mw - 4, bottom - rmsY));
        if (m.HoldDb > FloorDb + 0.5)
        {
            double hy = DbY(m.HoldDb, mt, bottom);
            dc.DrawLine(HoldPen, new Point(x + 2, hy), new Point(x + mw - 2, hy));
        }

        var t = Text(label, 10, Muted, true);
        dc.DrawText(t, new Point(x + mw / 2 - t.Width / 2, bottom + 6));
    }
}
