using System.IO;
using System.Text.Json;
using VoxRig.Dsp;

namespace VoxRig;

public sealed class EffectState
{
    public bool Enabled { get; set; }
    public Dictionary<string, float> Params { get; set; } = new();
}

public sealed class PresetData
{
    public string Name { get; set; }
    public bool BuiltIn { get; set; }
    public Dictionary<string, EffectState> Effects { get; set; } = new();
    public override string ToString() => BuiltIn ? Name : Name + "  (yours)";
}

public sealed class AppSettings
{
    public string InputId { get; set; }
    public string OutputId { get; set; }
    public string MonitorId { get; set; }
    public bool MonitorOn { get; set; }
    public float InGainDb { get; set; }
    public float OutGainDb { get; set; }
    public float MonGainDb { get; set; } = -6;
    public int LatencyMs { get; set; } = 20;
    public bool Bypass { get; set; }
    public bool WasRunning { get; set; }
    public PresetData Current { get; set; }
}

public static class Presets
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoxRig");
    static string PresetDir => Path.Combine(Dir, "presets");
    static string SettingsPath => Path.Combine(Dir, "settings.json");

    public static PresetData Capture(EffectChain chain, string name)
    {
        var p = new PresetData { Name = name };
        foreach (var e in chain.Effects)
            p.Effects[e.Name] = new EffectState
            {
                Enabled = e.Enabled,
                Params = e.Params.ToDictionary(x => x.Name, x => x.Value),
            };
        return p;
    }

    public static void Apply(EffectChain chain, PresetData p)
    {
        foreach (var e in chain.Effects)
        {
            p.Effects.TryGetValue(e.Name, out var st);
            foreach (var prm in e.Params)
                prm.Value = st != null && st.Params.TryGetValue(prm.Name, out var v) ? v : prm.Default;
            e.Enabled = st?.Enabled ?? false;
        }
    }

    sealed class Builder
    {
        readonly PresetData _p;
        public Builder(string name) => _p = new PresetData { Name = name, BuiltIn = true };

        public Builder On(string fx, params (string Name, float Value)[] ps)
        {
            _p.Effects[fx] = new EffectState { Enabled = true, Params = ps.ToDictionary(x => x.Name, x => x.Value) };
            return this;
        }

        public PresetData Done() => _p;
    }

    static Builder B(string name) => new(name);

    public static List<PresetData> BuiltIns() => new()
    {
        B("Clean Voice").On("Noise Gate").On("EQ").On("Compressor").Done(),

        B("Lag Reverb")
            .On("Noise Gate")
            .On("Reverb", ("Size", 88), ("Damping", 35), ("Pre-delay", 260), ("Mix", 45))
            .On("Echo", ("Time", 420), ("Feedback", 35), ("Tone", 3000), ("Mix", 28))
            .On("Lag", ("Delay", 150), ("Jitter", 40), ("Spikes", 6), ("Spike Len", 500), ("Catch-up", 50))
            .Done(),

        B("Bad Connection")
            .On("Noise Gate")
            .On("Radio", ("Low", 300), ("High", 4200), ("Grit", 15), ("Noise", 5))
            .On("Mic Glitches", ("Rate", 25), ("Intensity", 70), ("Dropout", 70), ("Stutter", 40), ("Freeze", 80),
                ("Crackle", 10), ("Digital", 60), ("Muffle", 0))
            .On("Lag", ("Delay", 200), ("Jitter", 60), ("Spikes", 10), ("Spike Len", 700), ("Catch-up", 60))
            .Done(),

        B("Broken Mic")
            .On("Bad Mic", ("Hiss", 40), ("Hum", 30), ("Crackle", 45), ("Flutter", 45), ("Clip", 30))
            .On("Mic Glitches", ("Rate", 20), ("Intensity", 60), ("Dropout", 70), ("Stutter", 10), ("Freeze", 0),
                ("Crackle", 80), ("Digital", 10), ("Muffle", 60))
            .Done(),

        B("Cave")
            .On("Noise Gate")
            .On("Reverb", ("Size", 96), ("Damping", 25), ("Pre-delay", 70), ("Mix", 55))
            .On("Echo", ("Time", 620), ("Feedback", 30), ("Tone", 2500), ("Mix", 20))
            .Done(),

        B("Walkie Talkie")
            .On("Noise Gate")
            .On("Compressor", ("Threshold", -30), ("Ratio", 6), ("Makeup", 10))
            .On("Radio", ("Low", 500), ("High", 2800), ("Grit", 45), ("Noise", 25))
            .On("Bad Mic", ("Hiss", 30), ("Hum", 0), ("Crackle", 15), ("Flutter", 0), ("Clip", 0))
            .Done(),

        B("Robot")
            .On("Noise Gate")
            .On("Pitch", ("Semitones", -2), ("Mix", 100))
            .On("Robot", ("Freq", 85), ("Metal", 55), ("Mix", 100))
            .On("Bitcrush", ("Bits", 10), ("Downsample", 2), ("Mix", 70))
            .Done(),

        B("Demon")
            .On("Noise Gate")
            .On("Pitch", ("Semitones", -7), ("Grain", 60), ("Mix", 100))
            .On("Drive", ("Drive", 14), ("Tone", 3500), ("Level", -6))
            .On("Reverb", ("Size", 82), ("Damping", 50), ("Pre-delay", 20), ("Mix", 35))
            .Done(),

        B("Chipmunk")
            .On("Noise Gate")
            .On("Pitch", ("Semitones", 8), ("Grain", 35), ("Mix", 100))
            .Done(),

        B("Haunted")
            .On("Noise Gate")
            .On("Pitch", ("Semitones", -3), ("Mix", 50))
            .On("Modulation", ("Rate", 0.3f), ("Depth", 6), ("Delay", 12), ("Feedback", 40), ("Mix", 50))
            .On("Echo", ("Time", 520), ("Feedback", 45), ("Tone", 2200), ("Mix", 30))
            .On("Reverb", ("Size", 90), ("Damping", 30), ("Pre-delay", 140), ("Mix", 45))
            .Done(),

        B("Total Chaos")
            .On("Pitch", ("Semitones", -5), ("Mix", 60))
            .On("Bitcrush", ("Bits", 5), ("Downsample", 8), ("Mix", 50))
            .On("Echo", ("Time", 300), ("Feedback", 55), ("Mix", 35))
            .On("Reverb", ("Size", 85), ("Pre-delay", 200), ("Mix", 40))
            .On("Bad Mic", ("Hiss", 35), ("Hum", 20), ("Crackle", 50), ("Flutter", 50), ("Clip", 40))
            .On("Mic Glitches", ("Rate", 45), ("Intensity", 90))
            .On("Lag", ("Delay", 250), ("Jitter", 80), ("Spikes", 15), ("Spike Len", 900), ("Catch-up", 80))
            .Done(),
    };

    public static List<PresetData> LoadUser()
    {
        var list = new List<PresetData>();
        try
        {
            if (!Directory.Exists(PresetDir)) return list;
            foreach (var f in Directory.GetFiles(PresetDir, "*.json"))
            {
                try
                {
                    var p = JsonSerializer.Deserialize<PresetData>(File.ReadAllText(f));
                    if (p?.Name != null) { p.BuiltIn = false; list.Add(p); }
                }
                catch { /* skip broken file */ }
            }
        }
        catch { }
        return list.OrderBy(p => p.Name).ToList();
    }

    static string FileFor(string name)
    {
        var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(PresetDir, safe + ".json");
    }

    public static void SaveUser(PresetData p)
    {
        Directory.CreateDirectory(PresetDir);
        File.WriteAllText(FileFor(p.Name), JsonSerializer.Serialize(p, Json));
    }

    public static void DeleteUser(PresetData p)
    {
        var f = FileFor(p.Name);
        if (File.Exists(f)) File.Delete(f);
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Json));
        }
        catch { }
    }
}
