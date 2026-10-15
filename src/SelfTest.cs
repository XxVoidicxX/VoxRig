using System.IO;
using System.Text;
using VoxRig.Dsp;

namespace VoxRig;

/// <summary>Offline DSP check: run with --selftest report.txt. No audio devices needed.</summary>
static class SelfTest
{
    public static int Run(string reportPath, bool live = false)
    {
        var sb = new StringBuilder();
        int failures = 0;
        void Fail(string s) { failures++; sb.AppendLine("FAIL " + s); }

        // 1. Preset names must match real effects and params.
        var chain = FxFactory.CreateChain();
        foreach (var p in Presets.BuiltIns())
            foreach (var (fxName, st) in p.Effects)
            {
                var fx = chain.Find(fxName);
                if (fx == null) { Fail($"preset '{p.Name}': unknown effect '{fxName}'"); continue; }
                foreach (var key in st.Params.Keys)
                    if (fx.Find(key) == null) Fail($"preset '{p.Name}': '{fxName}' has no param '{key}'");
            }

        // 2. Every preset plus extreme settings, at common rates, must stay finite and bounded.
        var cases = Presets.BuiltIns().Select(p => (p.Name, (Action<EffectChain>)(c => Presets.Apply(c, p)))).ToList();
        cases.Add(("ALL MAX", c => { foreach (var e in c.Effects) { e.Enabled = true; foreach (var q in e.Params) q.Value = q.Max; } }));
        cases.Add(("ALL MIN", c => { foreach (var e in c.Effects) { e.Enabled = true; foreach (var q in e.Params) q.Value = q.Min; } }));
        cases.Add(("ALL DEFAULT", c => { foreach (var e in c.Effects) e.Enabled = true; }));

        foreach (int sr in new[] { 44100, 48000, 96000 })
            foreach (var (name, setup) in cases)
            {
                var c = FxFactory.CreateChain();
                setup(c);
                c.Prepare(sr);
                var g = c.Get<Glitches>();
                int block = sr / 100;
                var buf = new float[block];
                double ph = 0;
                float peak = 0; double energy = 0; bool bad = false;
                int blocks = sr * 6 / block;
                for (int b = 0; b < blocks; b++)
                {
                    if (b % 50 == 10) g.Trigger();
                    if (b == blocks / 2) foreach (var e in c.Effects) e.Enabled = !e.Enabled; // toggle storm
                    if (b == blocks / 2 + 30) foreach (var e in c.Effects) e.Enabled = !e.Enabled;
                    for (int i = 0; i < block; i++)
                    {
                        double t = (b * block + i) / (double)sr;
                        double env = Math.Max(0, Math.Sin(t * 3.1));
                        ph += 140.0 / sr; if (ph >= 1) ph -= 1;
                        buf[i] = (float)((2 * ph - 1) * 0.3 * env);
                    }
                    c.Process(buf);
                    foreach (float x in buf)
                    {
                        if (!float.IsFinite(x)) { bad = true; break; }
                        peak = Math.Max(peak, Math.Abs(x));
                        energy += x * x;
                    }
                    if (bad) break;
                }
                if (bad) Fail($"{sr} {name}: NaN/Inf");
                else if (peak > 20) Fail($"{sr} {name}: runaway peak {peak:F2}");
                sb.AppendLine($"{(bad || peak > 20 ? "FAIL" : "ok  ")} {sr,6} {name,-16} peak {peak,7:F3}  rms {Math.Sqrt(energy / (blocks * block)),6:F3}");
            }

        // 3. Performance: full chain at 48 kHz must run far faster than real time.
        {
            var c = FxFactory.CreateChain();
            foreach (var e in c.Effects) e.Enabled = true;
            c.Prepare(48000);
            var buf = new float[480];
            var rng = new Random(1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int b = 0; b < 1000; b++)
            {
                for (int i = 0; i < buf.Length; i++) buf[i] = (float)(rng.NextDouble() - 0.5) * 0.2f;
                c.Process(buf);
            }
            double rt = 10.0 / sw.Elapsed.TotalSeconds;
            sb.AppendLine($"perf: every effect on, 10 s of audio in {sw.Elapsed.TotalMilliseconds:F0} ms ({rt:F0}x real time)");
            if (rt < 5) Fail("too slow for live use");
        }

        // 4. Optional live check against real WASAPI devices: default mic into CABLE Input for 4 s.
        if (live)
        {
            using var eng = new Audio.AudioEngine();
            var outs = eng.GetDevices(NAudio.CoreAudioApi.DataFlow.Render);
            var cable = outs.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase));
            string inId = eng.DefaultId(NAudio.CoreAudioApi.DataFlow.Capture);
            if (cable == null || inId == null) Fail("live: no CABLE Input or no default mic");
            else
            {
                Presets.Apply(eng.Chain, Presets.BuiltIns().First(p => p.Name == "Lag Reverb"));
                string fault = null;
                eng.Faulted += m => fault = m;
                eng.Start(inId, cable.Id, null, 20);
                var probe = new float[4096];
                float peakIn = 0;
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(100);
                    if (i == 15) eng.Bypass = true;
                    if (i == 25) eng.Bypass = false;
                    if (i == 30) eng.TriggerGlitch();
                    eng.InScope.CopyLatest(probe);
                    peakIn = Math.Max(peakIn, probe.Max(Math.Abs));
                }
                sb.AppendLine($"live: {eng.SampleRate} Hz, running {eng.IsRunning}, dropouts {eng.Underruns}, mic peak {peakIn:F4}, fault {fault ?? "none"}");
                if (fault != null || !eng.IsRunning) Fail("live audio fault");
                eng.Stop();
            }
        }

        sb.Insert(0, failures == 0 ? "SELFTEST PASSED\n" : $"SELFTEST FAILED ({failures})\n");
        File.WriteAllText(reportPath, sb.ToString());
        return failures == 0 ? 0 : 1;
    }
}
