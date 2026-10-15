using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoxRig.Dsp;

namespace VoxRig.Audio;

public sealed record DeviceInfo(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Lock-free-ish snapshot buffer for the visualizer. One writer (audio), one reader (UI).</summary>
public sealed class Scope
{
    public const int Size = 8192;
    readonly float[] _buf = new float[Size];
    int _w;

    public void Write(ReadOnlySpan<float> s)
    {
        int w = _w;
        foreach (float x in s) { _buf[w] = x; w = (w + 1) & (Size - 1); }
        Volatile.Write(ref _w, w);
    }

    public void CopyLatest(float[] dst)
    {
        int w = Volatile.Read(ref _w), n = dst.Length;
        for (int i = 0; i < n; i++) dst[i] = _buf[(w - n + i) & (Size - 1)];
    }

    public void Clear() => Array.Clear(_buf);
}

/// <summary>Feeds processed mono audio into a WASAPI render device, matching its mix format.</summary>
sealed class OutputTap : IWaveProvider
{
    const int RingSize = 1 << 17, Mask = RingSize - 1;
    readonly float[] _ring = new float[RingSize];
    readonly object _lock = new();
    readonly int _ch, _target;
    readonly double _step;
    long _w;
    double _r;
    bool _primed;

    public OutputTap(WaveFormat mix, int inRate, int latencyMs)
    {
        WaveFormat = mix;
        _ch = mix.Channels;
        _step = (double)inRate / mix.SampleRate;
        _target = Math.Max(64, (int)(inRate * latencyMs * 1.5 / 1000));
    }

    public WaveFormat WaveFormat { get; }
    public int Underruns { get; private set; }

    public void Write(ReadOnlySpan<float> s, float gain)
    {
        lock (_lock)
        {
            foreach (float x in s) { _ring[_w & Mask] = x * gain; _w++; }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var dst = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
        int frames = dst.Length / _ch;
        lock (_lock)
        {
            double avail = _w - _r;
            if (!_primed)
            {
                if (avail < _target) { dst.Clear(); return count; }
                _primed = true;
            }
            // Clock drift between mic and output: if we fall far behind, jump forward.
            if (avail > _target * 3 + 2048) _r = _w - _target;

            int f = 0;
            for (; f < frames; f++)
            {
                if (_r + 1 >= _w) { _primed = false; Underruns++; break; }
                long i = (long)_r;
                float frac = (float)(_r - i);
                float a = _ring[i & Mask];
                float v = a + (_ring[(i + 1) & Mask] - a) * frac;
                _r += _step;
                int o = f * _ch;
                for (int c = 0; c < _ch; c++) dst[o + c] = v;
            }
            if (f < frames) dst[(f * _ch)..].Clear();
        }
        return count;
    }
}

public sealed class AudioEngine : IDisposable
{
    readonly MMDeviceEnumerator _enum = new();
    WasapiCapture _capture;
    WasapiOut _out, _mon;
    OutputTap _outTap, _monTap;

    WaveFormat _inFmt;
    bool _inFloat;
    float[] _mono = new float[16384], _dry = new float[16384];

    float _inGain = 1, _outGain = 1, _monGain = 1;
    int _bypass, _muted, _monitorOn;
    float _masterMix = 1, _muteMix = 1, _limEnv, _limGain = 1;

    public EffectChain Chain { get; } = FxFactory.CreateChain();
    public Scope InScope { get; } = new();
    public Scope OutScope { get; } = new();
    public bool IsRunning { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public int LatencyMs { get; private set; }
    public int Underruns => (_outTap?.Underruns ?? 0) + (_monTap?.Underruns ?? 0);
    public bool HasMonitor => _mon != null;

    public event Action<string> Faulted;

    public float InputGainDb { set => Volatile.Write(ref _inGain, Effect.DbToLin(value)); }
    public float OutputGainDb { set => Volatile.Write(ref _outGain, Effect.DbToLin(value)); }
    public float MonitorGainDb { set => Volatile.Write(ref _monGain, Effect.DbToLin(value)); }

    public bool Bypass
    {
        get => Volatile.Read(ref _bypass) == 1;
        set => Volatile.Write(ref _bypass, value ? 1 : 0);
    }

    public bool Muted
    {
        get => Volatile.Read(ref _muted) == 1;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    public bool MonitorOn
    {
        get => Volatile.Read(ref _monitorOn) == 1;
        set => Volatile.Write(ref _monitorOn, value ? 1 : 0);
    }

    public List<DeviceInfo> GetDevices(DataFlow flow)
    {
        var list = new List<DeviceInfo>();
        foreach (var d in _enum.EnumerateAudioEndPoints(flow, DeviceState.Active))
            list.Add(new DeviceInfo(d.ID, d.FriendlyName));
        return list.OrderBy(d => d.Name).ToList();
    }

    public string DefaultId(DataFlow flow)
    {
        try { return _enum.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID; }
        catch { return null; }
    }

    public void Start(string inputId, string outputId, string monitorId, int latencyMs)
    {
        Stop();
        try
        {
            var inDev = _enum.GetDevice(inputId);
            var outDev = _enum.GetDevice(outputId);

            _capture = new WasapiCapture(inDev, true, latencyMs);
            _inFmt = _capture.WaveFormat;
            _inFloat = _inFmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (_inFmt is WaveFormatExtensible ext && ext.SubFormat == FloatSubType && _inFmt.BitsPerSample == 32);
            SampleRate = _inFmt.SampleRate;
            LatencyMs = latencyMs;

            Chain.Prepare(SampleRate);
            _masterMix = Bypass ? 0 : 1;
            _limEnv = 0; _limGain = 1;
            InScope.Clear(); OutScope.Clear();

            (_out, _outTap) = OpenOutput(outDev, latencyMs);
            if (!string.IsNullOrEmpty(monitorId) && monitorId != outputId)
                (_mon, _monTap) = OpenOutput(_enum.GetDevice(monitorId), latencyMs);

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Faulted?.Invoke("Input: " + e.Exception.Message); };
            _out.PlaybackStopped += (_, e) => { if (e.Exception != null) Faulted?.Invoke("Output: " + e.Exception.Message); };
            if (_mon != null)
                _mon.PlaybackStopped += (_, e) => { if (e.Exception != null) Faulted?.Invoke("Monitor: " + e.Exception.Message); };

            _out.Play();
            _mon?.Play();
            _capture.StartRecording();
            IsRunning = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    static readonly Guid FloatSubType = new("00000003-0000-0010-8000-00aa00389b71");

    (WasapiOut, OutputTap) OpenOutput(MMDevice dev, int latencyMs)
    {
        var mix = dev.AudioClient.MixFormat;
        if (mix.BitsPerSample != 32)
            mix = WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        var tap = new OutputTap(mix, SampleRate, latencyMs);
        var wo = new WasapiOut(dev, AudioClientShareMode.Shared, true, latencyMs);
        wo.Init(tap);
        return (wo, tap);
    }

    public void Stop()
    {
        IsRunning = false;
        try { _capture?.StopRecording(); } catch { }
        try { _out?.Stop(); } catch { }
        try { _mon?.Stop(); } catch { }
        _capture?.Dispose(); _out?.Dispose(); _mon?.Dispose();
        _capture = null; _out = null; _mon = null; _outTap = null; _monTap = null;
    }

    public void TriggerGlitch() => Chain.Get<Glitches>()?.Trigger();

    void OnData(object sender, WaveInEventArgs e)
    {
        try { Process(e.Buffer, e.BytesRecorded); }
        catch (Exception ex) { Faulted?.Invoke("Processing: " + ex.Message); }
    }

    void Process(byte[] data, int bytes)
    {
        int ch = _inFmt.Channels, frames = bytes / _inFmt.BlockAlign;
        if (frames == 0) return;
        if (_mono.Length < frames) { _mono = new float[frames * 2]; _dry = new float[frames * 2]; }
        var buf = _mono.AsSpan(0, frames);
        float inv = 1f / ch;

        if (_inFloat)
        {
            var src = MemoryMarshal.Cast<byte, float>(data.AsSpan(0, frames * _inFmt.BlockAlign));
            for (int f = 0; f < frames; f++)
            {
                float s = 0; int o = f * ch;
                for (int c = 0; c < ch; c++) s += src[o + c];
                buf[f] = s * inv;
            }
        }
        else if (_inFmt.BitsPerSample == 16)
        {
            var src = MemoryMarshal.Cast<byte, short>(data.AsSpan(0, frames * _inFmt.BlockAlign));
            for (int f = 0; f < frames; f++)
            {
                float s = 0; int o = f * ch;
                for (int c = 0; c < ch; c++) s += src[o + c] / 32768f;
                buf[f] = s * inv;
            }
        }
        else if (_inFmt.BitsPerSample == 24)
        {
            for (int f = 0; f < frames; f++)
            {
                float s = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = (f * ch + c) * 3;
                    int v = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                    s += v / 8388608f;
                }
                buf[f] = s * inv;
            }
        }
        else
        {
            var src = MemoryMarshal.Cast<byte, int>(data.AsSpan(0, frames * _inFmt.BlockAlign));
            for (int f = 0; f < frames; f++)
            {
                float s = 0; int o = f * ch;
                for (int c = 0; c < ch; c++) s += src[o + c] / 2147483648f;
                buf[f] = s * inv;
            }
        }

        float ig = Volatile.Read(ref _inGain);
        for (int i = 0; i < frames; i++) buf[i] *= ig;
        InScope.Write(buf);

        // Master FX bypass with a short crossfade so toggling live never clicks.
        float target = Bypass ? 0 : 1;
        float step = 1f / (0.02f * SampleRate);
        if (!(target == 0 && _masterMix == 0))
        {
            if (_masterMix == 0) Chain.ResetAll();
            if (_masterMix == 1 && target == 1)
            {
                Chain.Process(buf);
            }
            else
            {
                var dry = _dry.AsSpan(0, frames);
                buf.CopyTo(dry);
                Chain.Process(buf);
                float m = _masterMix;
                for (int i = 0; i < frames; i++)
                {
                    m = m < target ? Math.Min(target, m + step) : Math.Max(target, m - step);
                    buf[i] = dry[i] + (buf[i] - dry[i]) * m;
                }
                _masterMix = m;
            }
        }

        // Output gain, mute ramp, safety limiter.
        float og = Volatile.Read(ref _outGain);
        float muteTarget = Muted ? 0 : 1;
        float relC = 1 - 1f / (0.12f * SampleRate);
        for (int i = 0; i < frames; i++)
        {
            _muteMix = _muteMix < muteTarget ? Math.Min(muteTarget, _muteMix + step) : Math.Max(muteTarget, _muteMix - step);
            float x = buf[i] * og * _muteMix;
            if (float.IsNaN(x) || float.IsInfinity(x)) { x = 0; Chain.ResetAll(); }
            float a = MathF.Abs(x);
            _limEnv = a > _limEnv ? a : _limEnv * relC;
            float g = _limEnv > 0.92f ? 0.92f / _limEnv : 1f;
            _limGain = g < _limGain ? g : _limGain + (g - _limGain) * 0.002f;
            buf[i] = Math.Clamp(x * _limGain, -1f, 1f);
        }

        OutScope.Write(buf);
        _outTap?.Write(buf, 1f);
        if (_monTap != null) _monTap.Write(buf, MonitorOn ? Volatile.Read(ref _monGain) : 0f);
    }

    public void Dispose()
    {
        Stop();
        _enum.Dispose();
    }
}
