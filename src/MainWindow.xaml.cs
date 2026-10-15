using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using VoxRig.Audio;
using VoxRig.Dsp;

namespace VoxRig;

public partial class MainWindow : Window
{
    const int HkBypass = 1, HkMute = 2, HkGlitch = 3;

    readonly AudioEngine _engine = new();
    readonly List<Action> _refreshers = new();
    readonly string _snapshotPath;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    AppSettings _settings;
    bool _loading = true;
    string _statusMsg = "";

    static readonly Dictionary<FxCategory, Color> Accents = new()
    {
        [FxCategory.Clean] = Color.FromRgb(61, 220, 255),
        [FxCategory.Voice] = Color.FromRgb(167, 139, 250),
        [FxCategory.Dirt] = Color.FromRgb(255, 169, 77),
        [FxCategory.Space] = Color.FromRgb(74, 222, 128),
        [FxCategory.Chaos] = Color.FromRgb(255, 77, 141),
    };

    public MainWindow(string snapshotPath = null)
    {
        _snapshotPath = snapshotPath;
        InitializeComponent();
        foreach (var b in new ButtonBase[] { GlitchBtn, MuteToggle, FxToggle, StartBtn })
        {
            b.FontSize = 14; b.FontWeight = FontWeights.Bold;
            b.Padding = new Thickness(18, 10, 18, 10); b.Margin = new Thickness(8, 0, 0, 0);
            if (b.MinWidth < 104) b.MinWidth = 104;
        }
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ------------------------------------------------------------ setup

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = _snapshotPath != null ? new AppSettings() : Presets.LoadSettings();

        foreach (var fx in _engine.Chain.Effects) FxPanel.Children.Add(BuildCard(fx));
        _settings.Current ??= Presets.BuiltIns()[0];
        Presets.Apply(_engine.Chain, _settings.Current);

        LoadDevices();
        LoadPresets(null);

        InGain.Value = _settings.InGainDb;
        OutGain.Value = _settings.OutGainDb;
        MonGain.Value = _settings.MonGainDb;
        MonitorCheck.IsChecked = _settings.MonitorOn;
        FxToggle.IsChecked = !_settings.Bypass;
        LatencyBox.SelectedItem = LatencyBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _settings.LatencyMs.ToString()) ?? LatencyBox.Items[1];

        ApplyGains();
        _engine.Bypass = _settings.Bypass;
        _engine.MonitorOn = _settings.MonitorOn;
        UpdateToggleLabels();
        RefreshAll();

        Viz.Engine = _engine;
        _engine.Faulted += msg => Dispatcher.BeginInvoke(() =>
        {
            _engine.Stop();
            UpdateRunState();
            SetStatus("Audio stopped: " + msg);
        });

        InGain.ValueChanged += (_, _) => ApplyGains();
        OutGain.ValueChanged += (_, _) => ApplyGains();
        MonGain.ValueChanged += (_, _) => ApplyGains();
        MonitorCheck.Checked += (_, _) => _engine.MonitorOn = true;
        MonitorCheck.Unchecked += (_, _) => _engine.MonitorOn = false;
        InputBox.SelectionChanged += (_, _) => DevicesChanged();
        OutputBox.SelectionChanged += (_, _) => DevicesChanged();
        MonitorBox.SelectionChanged += (_, _) => DevicesChanged();
        LatencyBox.SelectionChanged += (_, _) => DevicesChanged();
        RefreshBtn.Click += (_, _) => { LoadDevices(); SetStatus("Device list refreshed."); };
        StartBtn.Click += (_, _) => { if (_engine.IsRunning) StopAudio(); else StartAudio(); };
        FxToggle.Click += (_, _) => SetBypass(FxToggle.IsChecked != true);
        MuteToggle.Click += (_, _) => SetMute(MuteToggle.IsChecked == true);
        GlitchBtn.Click += (_, _) => FireGlitch();
        PresetBox.SelectionChanged += (_, _) => OnPresetPicked();
        SavePresetBtn.Click += (_, _) => SavePreset();
        DeletePresetBtn.Click += (_, _) => DeletePreset();
        AllOffBtn.Click += (_, _) =>
        {
            foreach (var fx in _engine.Chain.Effects) fx.Enabled = false;
            RefreshAll();
            SetStatus("All filters switched off. Your mic goes through clean.");
        };

        _statusTimer.Tick += (_, _) => UpdateLatencyText();
        _statusTimer.Start();
        _loading = false;

        if (_snapshotPath != null) { RunSnapshot(); return; }

        SetStatus("Ready. Set INPUT to your mic and OUTPUT to CABLE Input, then press START. In Discord or OBS pick CABLE Output as the mic.");
        if (_settings.WasRunning) StartAudio();
    }

    void LoadDevices()
    {
        bool was = _loading;
        _loading = true;
        var ins = _engine.GetDevices(DataFlow.Capture);
        var outs = _engine.GetDevices(DataFlow.Render);

        InputBox.ItemsSource = ins;
        OutputBox.ItemsSource = outs;
        var mons = new List<DeviceInfo> { new("", "(none)") };
        mons.AddRange(outs);
        MonitorBox.ItemsSource = mons;

        string curIn = (InputBox.SelectedItem as DeviceInfo)?.Id ?? _settings.InputId;
        string curOut = (OutputBox.SelectedItem as DeviceInfo)?.Id ?? _settings.OutputId;
        string curMon = (MonitorBox.SelectedItem as DeviceInfo)?.Id ?? _settings.MonitorId;

        InputBox.SelectedItem = ins.FirstOrDefault(d => d.Id == curIn)
                                ?? ins.FirstOrDefault(d => d.Id == _engine.DefaultId(DataFlow.Capture) && !IsCable(d))
                                ?? ins.FirstOrDefault(d => !IsCable(d))
                                ?? ins.FirstOrDefault();
        OutputBox.SelectedItem = outs.FirstOrDefault(d => d.Id == curOut)
                                 ?? outs.FirstOrDefault(d => d.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
                                 ?? outs.FirstOrDefault(d => IsCable(d))
                                 ?? outs.FirstOrDefault();
        MonitorBox.SelectedItem = mons.FirstOrDefault(d => d.Id == (curMon ?? ""))
                                  ?? mons.FirstOrDefault(d => d.Id == _engine.DefaultId(DataFlow.Render) && !IsCable(d))
                                  ?? mons[0];
        _loading = was;
    }

    static bool IsCable(DeviceInfo d) =>
        d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        d.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
        d.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ effect cards

    UIElement BuildCard(Effect fx)
    {
        var accent = new SolidColorBrush(Accents[fx.Category]);
        var off = (Brush)FindResource("LineBrush");
        var muted = (Brush)FindResource("MutedBrush");

        var card = new Border
        {
            Width = 306,
            Margin = new Thickness(6),
            Padding = new Thickness(14, 10, 14, 12),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("CardBrush"),
            BorderThickness = new Thickness(1.5),
        };
        var stack = new StackPanel();
        card.Child = stack;

        var toggle = new ToggleButton { MinWidth = 64, Padding = new Thickness(10, 4, 10, 4), FontWeight = FontWeights.Bold };
        var title = new TextBlock { Text = fx.Name.ToUpperInvariant(), FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(toggle, Dock.Right);
        head.Children.Add(toggle);
        head.Children.Add(title);
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = fx.Description, TextWrapping = TextWrapping.Wrap, FontSize = 11.5,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        var paramsPanel = new StackPanel();
        stack.Children.Add(paramsPanel);

        void Visual()
        {
            bool on = fx.Enabled;
            toggle.IsChecked = on;
            toggle.Content = on ? "ON" : "OFF";
            card.BorderBrush = on ? accent : off;
            title.Foreground = on ? accent : muted;
            paramsPanel.Opacity = on ? 1 : 0.55;
        }
        toggle.Click += (_, _) =>
        {
            fx.Enabled = toggle.IsChecked == true;
            Visual();
            SetStatus($"{fx.Name} {(fx.Enabled ? "on" : "off")}.");
        };
        _refreshers.Add(Visual);

        foreach (var p in fx.Params)
        {
            var valueText = new TextBlock { FontSize = 11.5, Foreground = accent, FontWeight = FontWeights.SemiBold };
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            DockPanel.SetDock(valueText, Dock.Right);
            row.Children.Add(valueText);
            row.Children.Add(new TextBlock { Text = p.Name, FontSize = 12 });
            var slider = new Slider
            {
                Minimum = p.Min, Maximum = p.Max, Value = p.Value,
                IsSnapToTickEnabled = p.Decimals == 0, TickFrequency = p.Decimals == 0 ? 1 : 0,
                SmallChange = p.Decimals == 0 ? 1 : 0.1, LargeChange = (p.Max - p.Min) / 10,
                ToolTip = "Double-click to reset",
            };
            slider.ValueChanged += (_, _) => { p.Value = (float)slider.Value; valueText.Text = p.Display; };
            slider.MouseDoubleClick += (_, _) => slider.Value = p.Default;
            valueText.Text = p.Display;
            _refreshers.Add(() => { slider.Value = p.Value; valueText.Text = p.Display; });
            paramsPanel.Children.Add(row);
            paramsPanel.Children.Add(slider);
        }

        if (fx is Glitches)
        {
            var fire = new Button { Content = "Fire a glitch now", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 10, 0, 0) };
            fire.Click += (_, _) => FireGlitch();
            stack.Children.Add(fire);
        }

        Visual();
        return card;
    }

    void RefreshAll()
    {
        foreach (var r in _refreshers) r();
    }

    // ------------------------------------------------------------ presets

    void LoadPresets(string select)
    {
        bool was = _loading;
        _loading = true;
        var all = Presets.BuiltIns();
        all.AddRange(Presets.LoadUser());
        PresetBox.ItemsSource = all;
        PresetBox.SelectedItem = all.FirstOrDefault(p => p.Name == (select ?? _settings.Current?.Name));
        _loading = was;
    }

    void OnPresetPicked()
    {
        if (_loading || PresetBox.SelectedItem is not PresetData p) return;
        Presets.Apply(_engine.Chain, p);
        RefreshAll();
        PresetName.Text = p.BuiltIn ? "" : p.Name;
        SetStatus($"Preset loaded: {p.Name}");
    }

    void SavePreset()
    {
        string name = PresetName.Text.Trim();
        if (name.Length == 0) { SetStatus("Type a name for the preset first."); PresetName.Focus(); return; }
        if (Presets.BuiltIns().Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("That name belongs to a built-in preset. Pick another name.");
            return;
        }
        try
        {
            Presets.SaveUser(Presets.Capture(_engine.Chain, name));
            LoadPresets(name);
            SetStatus($"Saved preset \"{name}\".");
        }
        catch (Exception ex) { SetStatus("Could not save preset: " + ex.Message); }
    }

    void DeletePreset()
    {
        if (PresetBox.SelectedItem is not PresetData p || p.BuiltIn)
        {
            SetStatus("Only your own presets can be deleted.");
            return;
        }
        if (MessageBox.Show(this, $"Delete preset \"{p.Name}\"?", "VoxRig", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Presets.DeleteUser(p);
        LoadPresets(null);
        SetStatus($"Deleted preset \"{p.Name}\".");
    }

    // ------------------------------------------------------------ audio control

    int SelectedLatency() => int.Parse((string)((ComboBoxItem)LatencyBox.SelectedItem ?? (ComboBoxItem)LatencyBox.Items[1]).Tag);

    void StartAudio()
    {
        if (InputBox.SelectedItem is not DeviceInfo inDev || OutputBox.SelectedItem is not DeviceInfo outDev)
        {
            SetStatus("Pick an input and an output first.");
            return;
        }
        var monDev = MonitorBox.SelectedItem as DeviceInfo;
        try
        {
            _engine.Start(inDev.Id, outDev.Id, monDev?.Id, SelectedLatency());
            var warn = inDev.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) && IsCable(outDev)
                ? "  Warning: input is the cable itself, that makes a feedback loop."
                : "";
            SetStatus($"Live: {inDev.Name}  >>  {outDev.Name}{warn}");
        }
        catch (Exception ex)
        {
            SetStatus("Could not start audio: " + ex.Message);
        }
        UpdateRunState();
    }

    void StopAudio()
    {
        _engine.Stop();
        UpdateRunState();
        SetStatus("Stopped.");
    }

    void DevicesChanged()
    {
        if (_loading) return;
        if (_engine.IsRunning) StartAudio();
    }

    void UpdateRunState()
    {
        StartBtn.Content = _engine.IsRunning ? "STOP" : "START";
        UpdateLatencyText();
    }

    void UpdateLatencyText()
    {
        LatencyText.Text = _engine.IsRunning
            ? $"{_engine.SampleRate} Hz   about {(int)(_engine.LatencyMs * 3.5)} ms round trip   dropouts {_engine.Underruns}"
            : "not running";
    }

    void ApplyGains()
    {
        _engine.InputGainDb = (float)InGain.Value;
        _engine.OutputGainDb = (float)OutGain.Value;
        _engine.MonitorGainDb = (float)MonGain.Value;
        InGainText.Text = $"{InGain.Value:+0.0;-0.0;0.0} dB";
        OutGainText.Text = $"{OutGain.Value:+0.0;-0.0;0.0} dB";
        MonGainText.Text = $"{MonGain.Value:+0.0;-0.0;0.0} dB";
    }

    void SetBypass(bool bypass)
    {
        _engine.Bypass = bypass;
        UpdateToggleLabels();
        SetStatus(bypass ? "FX bypassed. Clean mic going out." : "FX back on.");
    }

    void SetMute(bool mute)
    {
        _engine.Muted = mute;
        UpdateToggleLabels();
        SetStatus(mute ? "Output muted." : "Output unmuted.");
    }

    void FireGlitch()
    {
        _engine.TriggerGlitch();
        SetStatus(_engine.IsRunning ? "Glitch fired." : "Start the audio first to hear glitches.");
    }

    void UpdateToggleLabels()
    {
        FxToggle.IsChecked = !_engine.Bypass;
        FxToggle.Content = _engine.Bypass ? "FX OFF" : "FX ON";
        MuteToggle.IsChecked = _engine.Muted;
        MuteToggle.Content = _engine.Muted ? "MUTED" : "MUTE";
    }

    void SetStatus(string msg)
    {
        _statusMsg = msg;
        StatusText.Text = msg;
    }

    // ------------------------------------------------------------ hotkeys

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_snapshotPath != null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        const uint mods = 0x1 | 0x2 | 0x4000; // Alt + Ctrl, no auto-repeat
        bool ok = RegisterHotKey(hwnd, HkBypass, mods, 0x42)   // B
                & RegisterHotKey(hwnd, HkMute, mods, 0x4D)     // M
                & RegisterHotKey(hwnd, HkGlitch, mods, 0x47);  // G
        if (!ok) Dispatcher.BeginInvoke(() => SetStatus("Some hotkeys are taken by another app. Buttons still work."));
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != 0x0312) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case HkBypass: SetBypass(!_engine.Bypass); handled = true; break;
            case HkMute: SetMute(!_engine.Muted); handled = true; break;
            case HkGlitch: FireGlitch(); handled = true; break;
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------ shutdown

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_snapshotPath == null)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(hwnd, HkBypass); UnregisterHotKey(hwnd, HkMute); UnregisterHotKey(hwnd, HkGlitch);

            _settings.InputId = (InputBox.SelectedItem as DeviceInfo)?.Id;
            _settings.OutputId = (OutputBox.SelectedItem as DeviceInfo)?.Id;
            _settings.MonitorId = (MonitorBox.SelectedItem as DeviceInfo)?.Id;
            _settings.MonitorOn = MonitorCheck.IsChecked == true;
            _settings.InGainDb = (float)InGain.Value;
            _settings.OutGainDb = (float)OutGain.Value;
            _settings.MonGainDb = (float)MonGain.Value;
            _settings.LatencyMs = SelectedLatency();
            _settings.Bypass = _engine.Bypass;
            _settings.WasRunning = _engine.IsRunning;
            _settings.Current = Presets.Capture(_engine.Chain, (PresetBox.SelectedItem as PresetData)?.Name ?? "Last session");
            Presets.SaveSettings(_settings);
        }
        _engine.Dispose();
    }

    // ------------------------------------------------------------ screenshot for testing

    void RunSnapshot()
    {
        Viz.Engine = null;
        Viz.Demo = true;
        Presets.Apply(_engine.Chain, Presets.BuiltIns().First(p => p.Name == "Lag Reverb"));
        RefreshAll();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(_snapshotPath)) enc.Save(fs);
            Close();
        };
        timer.Start();
    }
}
