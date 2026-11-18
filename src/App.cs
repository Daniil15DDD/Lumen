using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;

[assembly: AssemblyTitle("Lumen")]
[assembly: AssemblyProduct("Lumen Light Strip Studio")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Lumen
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--make-icon") { IconArt.SaveIco(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--icon-preview") { IconArt.SavePreview(args[1]); return 0; }
            Motion.EnablePressFeedback();
            if (args.Length == 2 && args[0] == "--shots")
            {
                Spring.Enabled = false;
                var sapp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                new MainUI(false, null, args[1]);
                sapp.Run();
                return 0;
            }

            bool created;
            var mutex = new Mutex(true, "Lumen.LightStrip.Single", out created);
            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "Lumen.LightStrip.Show");
            if (!created) { showEvt.Set(); return 0; }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var ui = new MainUI(Array.IndexOf(args, "--tray") >= 0, showEvt);
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }
    }

    public sealed class MainUI
    {
        readonly Settings S;
        readonly Engine engine;
        readonly bool demo;               // screenshot mode: never touches the strip or saved settings
        readonly string shotsDir;
        Spring navSpring, pageY, pageO;
        int pageIndex;
        bool Live { get { return demo || (strip != null && strip.Connected); } }
        volatile Strip strip;
        Layout layout;
        Window win;
        FrameworkElement root;
        WF.NotifyIcon tray;
        WF.ToolStripMenuItem trayPower;
        readonly DispatcherTimer previewTimer, statsTimer, reconnectTimer, colorDebounce, saveDebounce;
        DispatcherTimer identifyTimer;
        bool loading = true, exiting;
        readonly Process proc = Process.GetCurrentProcess();
        TimeSpan lastCpu; DateTime lastCpuAt;

        // preview visuals
        readonly List<GradientStop> glowStops = new List<GradientStop>();
        readonly List<SolidColorBrush> dotBrushes = new List<SolidColorBrush>();
        byte[] pv;
        double fxPhase;

        static readonly string[] FxNames = { "Rainbow", "Breathing", "Twist", "Beat", "Twirl", "Lemon", "Electric" };
        static readonly string[][] FxColors = {
            new[] { "#FF3B3B", "#FFD93B", "#3BFF7A", "#3BC8FF", "#B03BFF" },
            new[] { "#2A1450", "#8B5CF6", "#2A1450" },
            new[] { "#F43F5E", "#8B5CF6", "#22D3EE" },
            new[] { "#FB7185", "#F59E0B", "#FB7185" },
            new[] { "#22D3EE", "#6366F1", "#EC4899" },
            new[] { "#FDE047", "#A3E635", "#FDE047" },
            new[] { "#0EA5E9", "#E0F2FE", "#6366F1" },
        };
        static readonly string[] Presets = { "#FF3B30", "#FF7A1A", "#FFC83D", "#34D399", "#22D3EE", "#3B82F6", "#8B5CF6", "#EC4899", "#FFFFFF", "#FFE2B8", "#FFB26B" };

        public MainUI(bool startHidden, EventWaitHandle showEvt, string shots = null)
        {
            shotsDir = shots;
            demo = shots != null;
            S = demo ? new Settings() : Settings.Load();
            engine = new Engine(S, () => strip);

            BuildWindow();
            BuildTray();
            WireControls();

            previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            previewTimer.Tick += delegate { UpdatePreview(); };
            statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            statsTimer.Tick += delegate { UpdateStats(); };
            reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            reconnectTimer.Tick += delegate { TryConnect(); };
            colorDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            colorDebounce.Tick += delegate { colorDebounce.Stop(); if (S.Mode == "static" && S.Power && strip != null) { var c = CurrentColor(); strip.SetColor(c.R, c.G, c.B); } };
            saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveDebounce.Tick += delegate { saveDebounce.Stop(); S.Save(); };

            loading = false;
            if (demo)
            {
                tray.Visible = false;
                SetStatus(true, "27\" · 63 LEDs · fw 1.9.4");
                F<TextBlock>("StatsText").Text = "Capture runs on the GPU";
                UpdateModeUi();
                win.Left = -4000; win.Top = 0; win.ShowInTaskbar = false; win.ShowActivated = false;
                win.Show();
                win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(TakeShots));
                return;
            }
            TryConnect();
            reconnectTimer.Start();

            var t = new Thread(() => { while (true) { showEvt.WaitOne(); win.Dispatcher.BeginInvoke(new Action(ShowWindow)); } }) { IsBackground = true };
            t.Start();

            if (startHidden && S.StartTray) TrimMemory();
            else ShowWindow();
        }

        // =============================================================== window
        T F<T>(string name) where T : class { return root.FindName(name) as T; }

        void BuildWindow()
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lumen.ui.xaml"))
                root = (FrameworkElement)XamlReader.Load(st);

            win = new Window
            {
                Title = "Lumen",
                Width = 1000, Height = 720, MinWidth = 900, MinHeight = 680,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0D, 0x12)),
                Content = root,
                Icon = IconArt.ImageSource(),
                ShowActivated = true,
            };
            WindowChrome.SetWindowChrome(win, new WindowChrome
            {
                CaptionHeight = 40,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
                CornerRadius = new CornerRadius(0),
            });
            win.SourceInitialized += delegate
            {
                var h = new WindowInteropHelper(win).Handle;
                int on = 1, round = 2;
                Native.DwmSetWindowAttribute(h, 20, ref on, 4);     // immersive dark mode
                Native.DwmSetWindowAttribute(h, 33, ref round, 4);  // rounded corners (Win11)
            };
            win.StateChanged += delegate
            {
                root.Margin = win.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                if (win.WindowState == WindowState.Minimized) OnHidden(); else OnShown();
            };
            win.Closing += (s, e) =>
            {
                if (exiting) return;
                if (S.CloseToTray) { e.Cancel = true; win.Hide(); OnHidden(); }
                else { e.Cancel = true; Exit(); }
            };

            F<Image>("Logo").Source = IconArt.ImageSource(128);
            foreach (var n in new[] { "BtnMin", "BtnClose", "PowerBtn", "BriSl" })
                WindowChrome.SetIsHitTestVisibleInChrome(F<IInputElement>(n) as UIElement, true);
            F<Button>("BtnMin").Click += delegate { win.WindowState = WindowState.Minimized; };
            F<Button>("BtnClose").Click += delegate { win.Close(); };
        }

        void ShowWindow()
        {
            if (!win.IsVisible) win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            OnShown();
        }

        void OnShown()
        {
            if (!win.IsVisible || win.WindowState == WindowState.Minimized) return;
            previewTimer.Start(); statsTimer.Start();
            lastCpu = proc.TotalProcessorTime; lastCpuAt = DateTime.UtcNow;
        }

        void OnHidden()
        {
            previewTimer.Stop(); statsTimer.Stop();
            TrimMemory();
        }

        void TrimMemory()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { Native.EmptyWorkingSet(proc.Handle); } catch { }
        }

        // =============================================================== tray
        void BuildTray()
        {
            tray = new WF.NotifyIcon { Icon = IconArt.TrayIcon(), Text = "Lumen", Visible = true };
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open Lumen", null, delegate { ShowWindow(); });
            menu.Items.Add("Screen Sync", null, delegate { SetMode("sync"); });
            trayPower = new WF.ToolStripMenuItem("Lights on", null, delegate { SetPower(!S.Power); });
            menu.Items.Add(trayPower);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Exit(); });
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowWindow(); };
        }

        void Exit()
        {
            exiting = true;
            engine.Stop();
            var s = strip;
            if (s != null && S.Mode == "sync" && S.Power) s.TurnOff(); // don't leave a frozen frame on the wall
            S.Save();
            tray.Visible = false; tray.Dispose();
            if (s != null) s.Dispose();
            win.Close();
            Application.Current.Shutdown();
        }

        // =============================================================== device
        void TryConnect()
        {
            if (strip != null && strip.Connected) return;
            if (strip != null) { strip.Dispose(); strip = null; }
            Strip s = null;
            try { s = Strip.Open(); } catch { }
            if (s == null)
            {
                SetStatus(false, "Plug in the strip's USB cable. Close the official app if it's open.");
                return;
            }
            s.Lost += () => win.Dispatcher.BeginInvoke(new Action(() => { SetStatus(false, "Connection lost — retrying…"); }));
            strip = s;
            RebuildLayout();
            SetStatus(true, (s.SizeInch > 0 ? s.SizeInch + "\" · " : "") + s.LedCount + " LEDs · fw " + s.Firmware);
            ApplyMode();
        }

        void SetStatus(bool ok, string detail)
        {
            var sc = new SolidColorBrush(ok ? Color.FromRgb(0x34, 0xD3, 0x99) : Color.FromRgb(0xF5, 0x9E, 0x0B));
            F<Ellipse>("StatusDot").Fill = sc;
            F<Ellipse>("StatusHalo").Fill = sc;
            F<TextBlock>("StatusText").Text = ok ? "Connected" : "Not connected";
            F<TextBlock>("DeviceText").Text = detail;
            tray.Text = ok ? "Lumen — connected" : "Lumen — not connected";
        }

        void ApplyMode()
        {
            UpdateModeUi();
            var s = strip;
            if (s == null || !s.Connected) { engine.Stop(); return; }
            if (identifyTimer != null) return;
            if (!S.Power) { engine.Stop(); s.TurnOff(); return; }
            s.SetBrightnessPercent(S.Brightness);
            switch (S.Mode)
            {
                case "static":
                    engine.Stop();
                    s.StopAnimation();
                    var c = CurrentColor();
                    s.SetColor(c.R, c.G, c.B);
                    break;
                case "effect":
                    engine.Stop();
                    s.SetEffect(S.Effect);
                    s.SetSpeed(S.Speed);
                    break;
                default:
                    s.StopAnimation();
                    engine.Start();
                    break;
            }
        }

        void SetMode(string m)
        {
            S.Mode = m;
            if (!S.Power) { S.Power = true; F<ToggleButton>("PowerBtn").IsChecked = true; }
            ApplyMode();
            Save();
        }

        void SetPower(bool on)
        {
            S.Power = on;
            F<ToggleButton>("PowerBtn").IsChecked = on;
            ApplyMode();
            Save();
        }

        void Save() { if (!loading && !demo) { saveDebounce.Stop(); saveDebounce.Start(); } }

        // =============================================================== controls
        void WireControls()
        {
            // nav
            string[][] pages = {
                new[] { "NavSync", "PageSync", "Screen Sync", "Your strip mirrors the edges of your display." },
                new[] { "NavColor", "PageColor", "Color", "One calm color across the whole strip." },
                new[] { "NavFx", "PageFx", "Effects", "Animations that run on the strip itself — zero PC load." },
                new[] { "NavSet", "PageSet", "Settings", "Layout calibration and app behaviour." },
            };
            // One pill behind the nav list springs to the selected item (critically damped: no overshoot).
            var pillT = Motion.Translate(F<Border>("NavPill"));
            navSpring = new Spring(0, 1.0, 0.36, v => pillT.Y = v);
            // Pages enter along the sidebar's axis: going down the list → content rises from below, and vice versa.
            var pagesT = Motion.Translate(F<Grid>("Pages"));
            var header = F<FrameworkElement>("Header");
            var headerT = Motion.Translate(header);
            pageY = new Spring(0, 1.0, 0.38, v => { pagesT.Y = v; headerT.Y = v * 0.4; });
            pageO = new Spring(1, 1.0, 0.3, v => { F<Grid>("Pages").Opacity = v; header.Opacity = 0.35 + 0.65 * v; });
            for (int pi = 0; pi < pages.Length; pi++)
            {
                var page = pages[pi];
                int index = pi;
                F<RadioButton>(page[0]).Checked += delegate
                {
                    int dir = Math.Sign(index - pageIndex);
                    pageIndex = index;
                    foreach (var q in pages) F<FrameworkElement>(q[1]).Visibility = q == page ? Visibility.Visible : Visibility.Collapsed;
                    F<TextBlock>("PageTitle").Text = page[2];
                    F<TextBlock>("PageSub").Text = page[3];
                    navSpring.To(index * 48);
                    if (dir != 0 && Spring.Enabled)
                    {
                        pageY.Snap(18 * dir); pageY.To(0);
                        pageO.Snap(0); pageO.To(1);
                    }
                };
            }

            // header
            var bri = F<Slider>("BriSl");
            bri.Value = S.Brightness;
            F<TextBlock>("BriText").Text = S.Brightness + "%";
            bri.ValueChanged += delegate
            {
                S.Brightness = (int)Math.Round(bri.Value);
                F<TextBlock>("BriText").Text = S.Brightness + "%";
                if (strip != null && S.Power) strip.SetBrightnessPercent(S.Brightness);
                Save();
            };
            var power = F<ToggleButton>("PowerBtn");
            power.IsChecked = S.Power;
            power.Click += delegate { SetPower(power.IsChecked == true); };

            // sync page
            BindSlider("SmoothSl", "SmoothVal", S.Smooth, v => { S.Smooth = v; }, v => v == 0 ? "Instant" : v + "%");
            BindSlider("VibSl", "VibVal", S.Vibrance, v => { S.Vibrance = v; }, v => v + "%");
            BindSlider("DepthSl", "DepthVal", S.Depth, v => { S.Depth = v; }, v => v + "%");
            var accent = Res<Brush>("Accent"); var segStyle = Res<Style>("Seg");
            int[] fpsOpts = { 30, 60, 120, 144 };
            var fpsSeg = new Segmented(new[] { "30", "60", "120", "144" }, Math.Max(0, Array.IndexOf(fpsOpts, S.Fps)), accent, segStyle);
            fpsSeg.Changed += i => { S.Fps = fpsOpts[i]; Save(); };
            F<ContentControl>("FpsHost").Content = fpsSeg;

            var mons = Capture.Enumerate();
            if (mons.Count == 0)
                F<ContentControl>("MonHost").Content = new TextBlock { Text = "No display found", Foreground = Res<Brush>("Muted"), FontSize = 12 };
            else
            {
                var labels = new List<string>();
                for (int i = 0; i < mons.Count; i++) labels.Add(mons.Count > 1 ? (i + 1) + " · " + mons[i].Width + "×" + mons[i].Height : mons[i].Width + " × " + mons[i].Height);
                var monSeg = new Segmented(labels, S.Monitor < mons.Count ? S.Monitor : 0, accent, segStyle);
                monSeg.Changed += i => { S.Monitor = i; engine.RequestReinit(); Save(); };
                F<ContentControl>("MonHost").Content = monSeg;
            }
            var detSeg = new Segmented(new[] { "Balanced", "Per-LED" }, S.Precise ? 1 : 0, accent, segStyle);
            detSeg.Changed += i => { S.Precise = i == 1; Save(); };
            F<ContentControl>("DetHost").Content = detSeg;
            F<Button>("SyncBtn").Click += delegate { SetMode("sync"); };

            // color page
            var hue = F<Slider>("HueSl"); var sat = F<Slider>("SatSl");
            hue.Value = S.Hue; sat.Value = S.Sat;
            hue.ValueChanged += delegate { S.Hue = (int)hue.Value; OnColorChanged(); };
            sat.ValueChanged += delegate { S.Sat = (int)sat.Value; OnColorChanged(); };
            var sw = F<WrapPanel>("Swatches");
            foreach (var hex in Presets)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new Button { Style = Res<Style>("Swatch"), Background = new SolidColorBrush(c), ToolTip = hex };
                b.Click += delegate
                {
                    double h, s2, v; ToHsv(c, out h, out s2, out v);
                    loading = true; hue.Value = h; sat.Value = s2 * 100; loading = false;
                    S.Hue = (int)Math.Round(h); S.Sat = (int)Math.Round(s2 * 100);
                    OnColorChanged();
                };
                sw.Children.Add(b);
            }
            RefreshColorUi();

            // effects page
            var fxPanel = F<WrapPanel>("FxPanel");
            for (int i = 0; i < FxNames.Length; i++)
            {
                int fi = i;
                var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                var cols = FxColors[i];
                for (int k = 0; k < cols.Length; k++) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(cols[k]), (double)k / (cols.Length - 1)));
                var rb = new RadioButton { Content = FxNames[i], Style = Res<Style>("FxCard"), Background = g, IsChecked = S.Mode == "effect" && S.Effect == i };
                rb.Click += delegate { S.Effect = fi; SetMode("effect"); };
                fxPanel.Children.Add(rb);
            }
            BindSlider("SpeedSl", "SpeedVal", S.Speed, v => { S.Speed = v; if (S.Mode == "effect" && S.Power && strip != null) strip.SetSpeed(v); }, v => v + "%");

            // settings page
            F<CheckBox>("MirrorSw").IsChecked = S.Mirror;
            F<CheckBox>("MirrorSw").Click += delegate { S.Mirror = F<CheckBox>("MirrorSw").IsChecked == true; RebuildLayout(); Save(); };
            F<Button>("IdentifyBtn").Click += delegate { Identify(); };
            F<Button>("ResetLayoutBtn").Click += delegate { S.Right = S.Top = S.Left = -1; S.Bottom = 0; S.Mirror = false; F<CheckBox>("MirrorSw").IsChecked = false; RebuildLayout(); Save(); };
            BindSwitch("StartupSw", S.Startup, v => { S.Startup = v; SetStartup(v); });
            BindSwitch("TraySw", S.StartTray, v => { S.StartTray = v; });
            BindSwitch("CloseTraySw", S.CloseToTray, v => { S.CloseToTray = v; });
            F<TextBlock>("AboutText").Text =
                "Lumen talks to your Robobloq strip directly over USB. Screen capture runs on the GPU " +
                "(DXGI desktop duplication + GPU downscale), so only a tiny thumbnail ever reaches the CPU. " +
                "Effects run on the strip's own controller.\n\nSettings: %AppData%\\LumenStrip";

            RebuildLayout();
        }

        T Res<T>(string key) where T : class { return root.FindResource(key) as T; }

        void BindSlider(string sl, string val, int initial, Action<int> set, Func<int, string> fmt)
        {
            var s = F<Slider>(sl); var t = F<TextBlock>(val);
            s.Value = initial; t.Text = fmt(initial);
            s.ValueChanged += delegate { int v = (int)Math.Round(s.Value); t.Text = fmt(v); set(v); Save(); };
        }

        void BindSwitch(string name, bool initial, Action<bool> set)
        {
            var c = F<CheckBox>(name);
            c.IsChecked = initial;
            c.Click += delegate { set(c.IsChecked == true); Save(); };
        }

        void SetStartup(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (on) k.SetValue("Lumen", "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else k.DeleteValue("Lumen", false);
                }
            }
            catch { }
        }

        // =============================================================== color
        Color CurrentColor() { return FromHsv(S.Hue, S.Sat / 100.0, 1); }

        void OnColorChanged()
        {
            if (loading) return;
            RefreshColorUi();
            if (S.Mode != "static" || !S.Power) { SetMode("static"); }
            else { colorDebounce.Stop(); colorDebounce.Start(); }
            Save();
        }

        void RefreshColorUi()
        {
            var c = CurrentColor();
            F<Ellipse>("ColorDot").Fill = new SolidColorBrush(c);
            F<Ellipse>("ColorGlow").Fill = new SolidColorBrush(c);
            F<TextBlock>("HexText").Text = string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            var pure = FromHsv(S.Hue, 1, 1);
            F<Slider>("SatSl").Background = new LinearGradientBrush(Colors.White, pure, 0);
        }

        static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; } else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; s = max == 0 ? 0 : d / max;
            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        // =============================================================== layout
        void RebuildLayout()
        {
            int leds = strip != null && strip.LedCount > 0 ? strip.LedCount : 63;
            int inch = strip != null ? strip.SizeInch : 27;
            layout = Layout.Build(S, leds, inch);
            engine.SetLayout(layout);
            BuildStage();
            BuildLayoutEditor();
        }

        void BuildLayoutEditor()
        {
            var panel = F<StackPanel>("LayoutPanel");
            if (panel == null) return;
            panel.Children.Clear();
            string[] names = { "Right side", "Top", "Left side", "Bottom" };
            int[] vals = { layout.R, layout.T, layout.L, layout.B };
            for (int i = 0; i < 4; i++)
            {
                int side = i;
                var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = names[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
                var minus = new Button { Content = "−", Style = Res<Style>("Ghost"), Padding = new Thickness(10, 2, 10, 4), FontSize = 14 };
                var plus = new Button { Content = "+", Style = Res<Style>("Ghost"), Padding = new Thickness(10, 2, 10, 4), FontSize = 14 };
                var val = new TextBlock { Text = vals[i].ToString(), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(minus, 1); Grid.SetColumn(val, 2); Grid.SetColumn(plus, 3);
                g.Children.Add(minus); g.Children.Add(val); g.Children.Add(plus);
                minus.Click += delegate { Step(side, -1); };
                plus.Click += delegate { Step(side, +1); };
                panel.Children.Add(g);
            }
            int sum = layout.R + layout.T + layout.L + layout.B;
            var hint = F<TextBlock>("LayoutHint");
            hint.Text = sum == layout.Count
                ? "✓ " + sum + " of " + layout.Count + " LEDs mapped. Use “Identify sides” — right should glow red, top green, left blue."
                : "⚠ " + sum + " mapped but the strip has " + layout.Count + " LEDs — adjust until they match.";
            hint.Foreground = sum == layout.Count ? Res<Brush>("Muted") : new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        }

        void Step(int side, int d)
        {
            S.Right = layout.R; S.Top = layout.T; S.Left = layout.L; S.Bottom = layout.B;
            if (side == 0) S.Right = Math.Max(0, S.Right + d);
            else if (side == 1) S.Top = Math.Max(0, S.Top + d);
            else if (side == 2) S.Left = Math.Max(0, S.Left + d);
            else S.Bottom = Math.Max(0, S.Bottom + d);
            RebuildLayout();
            Save();
            if (identifyTimer != null) SendIdentify();
        }

        void Identify()
        {
            if (strip == null) return;
            engine.Stop();
            if (identifyTimer == null)
            {
                identifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                identifyTimer.Tick += delegate { identifyTimer.Stop(); identifyTimer = null; ApplyMode(); };
            }
            identifyTimer.Stop(); identifyTimer.Start();
            strip.StopAnimation();
            strip.SetBrightnessPercent(Math.Max(40, S.Brightness));
            SendIdentify();
        }

        void SendIdentify()
        {
            var s = strip; if (s == null) return;
            byte[][] cols = { new byte[] { 255, 0, 0 }, new byte[] { 0, 255, 0 }, new byte[] { 0, 0, 255 }, new byte[] { 255, 255, 255 } };
            int[] counts = { layout.R, layout.T, layout.L, layout.B };
            var tuples = new List<byte>();
            int start = 1;
            for (int i = 0; i < 4; i++)
            {
                if (counts[i] <= 0) continue;
                int end = Math.Min(layout.Count, start + counts[i] - 1);
                if (start > layout.Count) break;
                tuples.AddRange(new byte[] { (byte)start, cols[i][0], cols[i][1], cols[i][2], (byte)end });
                start = end + 1;
            }
            if (start <= layout.Count) tuples.AddRange(new byte[] { (byte)start, 0, 0, 0, (byte)layout.Count });
            var arr = tuples.ToArray();
            s.SendRanges(arr, arr.Length);
        }

        // =============================================================== preview
        const double SW = 640, SH = 232, MW = 330, MH = 186;

        void BuildStage()
        {
            var stage = F<Canvas>("Stage");
            stage.Children.Clear(); glowStops.Clear(); dotBrushes.Clear();
            double mx = (SW - MW) / 2, my = (SH - MH) / 2 - 6;

            for (int i = 0; i < layout.Count; i++)
            {
                float nx, ny; layout.Center(i, out nx, out ny);
                double x = mx + nx * MW, y = my + ny * MH;
                var stop = new GradientStop(Color.FromArgb(0, 0, 0, 0), 0);
                var g = new RadialGradientBrush();
                g.GradientStops.Add(stop);
                g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
                glowStops.Add(stop);
                double r = 46;
                var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = g, IsHitTestVisible = false };
                Canvas.SetLeft(e, x - r); Canvas.SetTop(e, y - r);
                stage.Children.Add(e);
            }

            var screen = new Rectangle
            {
                Width = MW, Height = MH, RadiusX = 8, RadiusY = 8,
                Fill = new LinearGradientBrush(Color.FromRgb(0x1A, 0x1C, 0x24), Color.FromRgb(0x0D, 0x0E, 0x13), 90),
                Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1,
            };
            Canvas.SetLeft(screen, mx); Canvas.SetTop(screen, my);
            stage.Children.Add(screen);
            var neck = new Rectangle { Width = 34, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(0x20, 0x22, 0x2A)) };
            Canvas.SetLeft(neck, SW / 2 - 17); Canvas.SetTop(neck, my + MH);
            stage.Children.Add(neck);
            var foot = new Rectangle { Width = 96, Height = 4, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x37)) };
            Canvas.SetLeft(foot, SW / 2 - 48); Canvas.SetTop(foot, my + MH + 14);
            stage.Children.Add(foot);

            for (int i = 0; i < layout.Count; i++)
            {
                float nx, ny; layout.Center(i, out nx, out ny);
                double x = mx + nx * MW, y = my + ny * MH;
                var b = new SolidColorBrush(Color.FromRgb(40, 40, 48));
                dotBrushes.Add(b);
                var d = new Ellipse { Width = 4, Height = 4, Fill = b, IsHitTestVisible = false };
                Canvas.SetLeft(d, x - 2); Canvas.SetTop(d, y - 2);
                stage.Children.Add(d);
            }
            UpdatePreview();
        }

        void UpdatePreview()
        {
            int n = glowStops.Count;
            if (n == 0) return;
            if (pv == null || pv.Length != n * 3) pv = new byte[n * 3];

            bool on = S.Power && Live;
            if (!on) Array.Clear(pv, 0, pv.Length);
            else if (S.Mode == "sync" && demo)
            {
                for (int i = 0; i < n; i++)
                {
                    float nx, ny; layout.Center(i, out nx, out ny);
                    // walk left-bottom → top → right-bottom, same rainbow as the app icon
                    double t = ny == 0 ? (1 + nx) / 3.0 : (nx < 0.5 ? (1 - ny) / 3.0 : (2 + ny) / 3.0);
                    var c = FromHsv(60 + t * 340, 1, 1);
                    pv[i * 3] = c.R; pv[i * 3 + 1] = c.G; pv[i * 3 + 2] = c.B;
                }
            }
            else if (S.Mode == "sync") { if (!engine.CopyPreview(ref pv) || pv.Length != n * 3) { pv = new byte[n * 3]; } }
            else if (S.Mode == "static")
            {
                var c = CurrentColor();
                for (int i = 0; i < n; i++) { pv[i * 3] = c.R; pv[i * 3 + 1] = c.G; pv[i * 3 + 2] = c.B; }
            }
            else
            {
                fxPhase += 0.004 + S.Speed / 100.0 * 0.02;
                var cols = FxColors[Math.Max(0, Math.Min(FxColors.Length - 1, S.Effect))];
                for (int i = 0; i < n; i++)
                {
                    double t = ((double)i / n + fxPhase) % 1.0 * (cols.Length - 1);
                    int k = (int)t; double f = t - k;
                    var a = (Color)ColorConverter.ConvertFromString(cols[k]);
                    var b = (Color)ColorConverter.ConvertFromString(cols[Math.Min(cols.Length - 1, k + 1)]);
                    pv[i * 3] = (byte)(a.R + (b.R - a.R) * f); pv[i * 3 + 1] = (byte)(a.G + (b.G - a.G) * f); pv[i * 3 + 2] = (byte)(a.B + (b.B - a.B) * f);
                }
            }

            double bri = 0.35 + 0.65 * S.Brightness / 100.0;
            long ar = 0, ag = 0, ab = 0;
            for (int i = 0; i < n; i++) { ar += pv[i * 3]; ag += pv[i * 3 + 1]; ab += pv[i * 3 + 2]; }
            var amb = Color.FromArgb((byte)(on ? 255 : 0), (byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
            var ambStop = F<GradientStop>("AmbientStop");
            if (ambStop != null && ambStop.Color != amb) ambStop.Color = amb;
            for (int i = 0; i < n; i++)
            {
                byte r = pv[i * 3], g = pv[i * 3 + 1], b = pv[i * 3 + 2];
                int mx = Math.Max(r, Math.Max(g, b));
                var gc = Color.FromArgb((byte)Math.Min(255, mx * 0.85 * bri), r, g, b);
                if (glowStops[i].Color != gc) glowStops[i].Color = gc;
                var dc = mx < 20 ? Color.FromRgb(40, 40, 48) : Color.FromRgb(r, g, b);
                if (dotBrushes[i].Color != dc) dotBrushes[i].Color = dc;
            }
        }

        void UpdateModeUi()
        {
            string label; Color dot;
            if (!Live) { label = "Waiting for strip"; dot = Color.FromRgb(0xF5, 0x9E, 0x0B); }
            else if (!S.Power) { label = "Lights off"; dot = Color.FromRgb(0x6B, 0x70, 0x7C); }
            else if (S.Mode == "static") { label = "Solid color"; dot = CurrentColor(); }
            else if (S.Mode == "effect") { label = "Effect · " + FxNames[Math.Max(0, Math.Min(FxNames.Length - 1, S.Effect))]; dot = Color.FromRgb(0x8B, 0x5C, 0xF6); }
            else { label = "Screen Sync · live"; dot = Color.FromRgb(0x22, 0xD3, 0xEE); }
            F<TextBlock>("ModeText").Text = label;
            F<Ellipse>("ModeDot").Fill = new SolidColorBrush(dot);
            var btn = F<Button>("SyncBtn");
            bool syncing = S.Mode == "sync" && S.Power;
            btn.Content = syncing ? "✓  Screen Sync is running" : "Start Screen Sync";
            btn.Opacity = syncing ? 0.6 : 1;
            trayPower.Text = S.Power ? "Turn lights off" : "Turn lights on";
            foreach (RadioButton rb in F<WrapPanel>("FxPanel").Children)
                rb.IsChecked = S.Mode == "effect" && (string)rb.Content == FxNames[Math.Max(0, Math.Min(FxNames.Length - 1, S.Effect))];
        }

        // =============================================================== screenshots (for the README)
        void Pump()
        {
            root.UpdateLayout();
            win.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        static BitmapSource Render(FrameworkElement e, double w, double h)
        {
            e.Measure(new Size(w, h)); e.Arrange(new Rect(0, 0, w, h)); e.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)(w * 2), (int)(h * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(e);
            rtb.Freeze();
            return rtb;
        }

        static void SavePng(BitmapSource b, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(b));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        /// <summary>A window screenshot as a floating, rounded "material" with a soft shadow.</summary>
        static FrameworkElement Framed(BitmapSource shot, double w, double h, double radius)
        {
            var card = new Border
            {
                Width = w, Height = h, CornerRadius = new CornerRadius(radius),
                Background = new ImageBrush(shot) { Stretch = Stretch.Fill },
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 60, ShadowDepth = 18, Direction = 270, Opacity = 0.55, Color = Colors.Black },
            };
            return card;
        }

        void TakeShots()
        {
            Directory.CreateDirectory(shotsDir);
            string[] navs = { "NavSync", "NavColor", "NavFx", "NavSet" };
            string[] files = { "sync", "color", "effects", "settings" };
            string[] modes = { "sync", "static", "effect", "sync" };
            double W = root.ActualWidth, H = root.ActualHeight;
            BitmapSource hero = null;
            for (int i = 0; i < navs.Length; i++)
            {
                S.Mode = modes[i];
                if (i == 2) { S.Effect = 0; fxPhase = 0.15; }
                F<RadioButton>(navs[i]).IsChecked = true;
                UpdateModeUi();
                UpdatePreview();
                Pump();
                var rtb = new RenderTargetBitmap((int)(W * 2), (int)(H * 2), 192, 192, PixelFormats.Pbgra32);
                rtb.Render(root);
                rtb.Freeze();
                if (i == 0) hero = rtb;
                var host = new Grid { Width = W + 120, Height = H + 140, Background = Brushes.Transparent };
                host.Children.Add(Framed(rtb, W, H, 14));
                SavePng(Render(host, W + 120, H + 140), System.IO.Path.Combine(shotsDir, files[i] + ".png"));
            }

            // ----- banner / social preview (1280×640) -----
            var banner = new Grid { Width = 1280, Height = 640, Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x10)), ClipToBounds = true };
            var glow = new Ellipse { Width = 1100, Height = 760, Margin = new Thickness(520, -40, -380, -160), Opacity = 0.5 };
            var rg = new RadialGradientBrush();
            rg.GradientStops.Add(new GradientStop(Color.FromRgb(0x8B, 0x5C, 0xF6), 0));
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0x22, 0xD3, 0xEE), 0.45));
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0x22, 0xD3, 0xEE), 1));
            glow.Fill = rg;
            banner.Children.Add(glow);
            var shotImg = Framed(hero, 630, 453.6, 14);
            shotImg.HorizontalAlignment = HorizontalAlignment.Left; shotImg.VerticalAlignment = VerticalAlignment.Top;
            shotImg.Margin = new Thickness(596, 93, 0, 0);
            banner.Children.Add(shotImg);
            var text = new StackPanel { Margin = new Thickness(76, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 500, HorizontalAlignment = HorizontalAlignment.Left };
            text.Children.Add(new Image { Source = IconArt.ImageSource(256), Width = 112, Height = 112, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 0, 18) });
            text.Children.Add(new TextBlock { Text = "Lumen", FontSize = 88, FontWeight = FontWeights.Bold, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), Margin = new Thickness(-4, 0, 0, 0) });
            text.Children.Add(new TextBlock { Text = "A fast, beautiful Windows app for Robobloq USB light strips.", FontSize = 24, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCD, 0xD8)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 22), FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") });
            text.Children.Add(new TextBlock { Text = "GPU screen sync  ·  ~0.1% CPU  ·  no installer", FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)) });
            banner.Children.Add(text);
            SavePng(Render(banner, 1280, 640), System.IO.Path.Combine(shotsDir, "banner.png"));

            // ----- icon -----
            var ico = new Image { Source = IconArt.ImageSource(256), Width = 256, Height = 256 };
            SavePng(Render(ico, 256, 256), System.IO.Path.Combine(shotsDir, "icon.png"));

            tray.Dispose();
            Application.Current.Shutdown();
        }

        void UpdateStats()
        {
            proc.Refresh();
            var now = DateTime.UtcNow;
            var cpu = proc.TotalProcessorTime;
            double pct = (cpu - lastCpu).TotalMilliseconds / Math.Max(1, (now - lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount * 100;
            lastCpu = cpu; lastCpuAt = now;
            string fps = engine.Running ? "  ·  " + engine.SendFps + " fps" : "";
            F<TextBlock>("StatsText").Text = string.Format("CPU {0:0.0}%  ·  RAM {1} MB{2}", pct, proc.WorkingSet64 / (1024 * 1024), fps);
        }
    }

    /// <summary>App icon drawn in code: a glowing gradient ring.</summary>
    static class IconArt
    {
        static SD.Bitmap Draw(int size)
        {
            var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
            using (var g = SD.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(SD.Color.Transparent);
                float s = size;
                using (var path = Rounded(s * 0.04f, s * 0.04f, s * 0.92f, s * 0.92f, s * 0.24f))
                using (var bg = new SD.Drawing2D.LinearGradientBrush(new SD.PointF(0, 0), new SD.PointF(0, s), SD.Color.FromArgb(255, 28, 30, 40), SD.Color.FromArgb(255, 12, 13, 18)))
                    g.FillPath(bg, path);
                // monitor box
                float mx = s * 0.22f, my = s * 0.24f, mw = s * 0.56f, mh = s * 0.40f;

                // rainbow glow around left → top → right edges (yellow, green, cyan, blue, purple, red, orange)
                int steps = 36;
                float glowR = s * (size <= 24 ? 0.16f : 0.13f);
                for (int i = 0; i < steps; i++)
                {
                    float t = (i + 0.5f) / steps;
                    float px, py, per = mh + mw + mh, d = t * per;
                    if (d < mh) { px = mx; py = my + mh - d; }
                    else if (d < mh + mw) { px = mx + (d - mh); py = my; }
                    else { px = mx + mw; py = my + (d - mh - mw); }
                    var c = Hsv(60 + t * 340, 1, 1);
                    using (var gp = new SD.Drawing2D.GraphicsPath())
                    {
                        gp.AddEllipse(px - glowR, py - glowR, glowR * 2, glowR * 2);
                        using (var pb = new SD.Drawing2D.PathGradientBrush(gp))
                        {
                            pb.CenterColor = SD.Color.FromArgb(150, c);
                            pb.SurroundColors = new[] { SD.Color.FromArgb(0, c) };
                            g.FillPath(pb, gp);
                        }
                    }
                }

                // screen
                using (var path = Rounded(mx, my, mw, mh, s * 0.07f))
                using (var fill = new SD.Drawing2D.LinearGradientBrush(new SD.PointF(0, my), new SD.PointF(0, my + mh), SD.Color.FromArgb(255, 30, 32, 42), SD.Color.FromArgb(255, 14, 15, 20)))
                {
                    g.FillPath(fill, path);
                    if (size >= 32)
                        using (var edge = new SD.Pen(SD.Color.FromArgb(70, 255, 255, 255), Math.Max(1f, s / 128f))) g.DrawPath(edge, path);
                }

                // LED dots along the edges (only where there's room to see them)
                if (size >= 48)
                {
                    int dots = 26; float dr = s * 0.012f;
                    for (int i = 0; i < dots; i++)
                    {
                        float t = (i + 0.5f) / dots, per = mh + mw + mh, d = t * per, px, py;
                        if (d < mh) { px = mx; py = my + mh - d; }
                        else if (d < mh + mw) { px = mx + (d - mh); py = my; }
                        else { px = mx + mw; py = my + (d - mh - mw); }
                        using (var b = new SD.SolidBrush(Hsv(60 + t * 340, 0.6, 1))) g.FillEllipse(b, px - dr, py - dr, dr * 2, dr * 2);
                    }
                }

                // stand
                using (var st = new SD.SolidBrush(SD.Color.FromArgb(255, 44, 47, 58)))
                {
                    g.FillRectangle(st, s * 0.46f, my + mh, s * 0.08f, s * 0.07f);
                    using (var foot = Rounded(s * 0.36f, my + mh + s * 0.07f, s * 0.28f, s * 0.04f, s * 0.02f)) g.FillPath(st, foot);
                }
            }
            return bmp;
        }

        static SD.Color Hsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; } else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
            return SD.Color.FromArgb(255, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        static SD.Drawing2D.GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new SD.Drawing2D.GraphicsPath();
            p.AddArc(x, y, r, r, 180, 90); p.AddArc(x + w - r, y, r, r, 270, 90);
            p.AddArc(x + w - r, y + h - r, r, r, 0, 90); p.AddArc(x, y + h - r, r, r, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static SD.Icon TrayIcon()
        {
            using (var b = Draw(32)) return SD.Icon.FromHandle(b.GetHicon());
        }

        public static ImageSource ImageSource(int size = 64)
        {
            using (var b = Draw(size))
            {
                IntPtr h = b.GetHbitmap();
                try { return Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
                finally { DeleteObject(h); }
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        public static void SavePreview(string path)
        {
            int[] sizes = { 256, 64, 32, 16 };
            using (var sheet = new SD.Bitmap(256 + 64 + 32 + 16 + 50, 256))
            using (var g = SD.Graphics.FromImage(sheet))
            {
                g.Clear(SD.Color.FromArgb(255, 32, 32, 32));
                int x = 0;
                foreach (var sz in sizes) { using (var b = Draw(sz)) g.DrawImage(b, x, 256 - sz); x += sz + 10; }
                sheet.Save(path, SD.Imaging.ImageFormat.Png);
            }
        }

        /// <summary>Writes a multi-size PNG-compressed .ico (used for the .exe icon at build time).</summary>
        public static void SaveIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 256 };
            var pngs = new List<byte[]>();
            foreach (var sz in sizes)
                using (var b = Draw(sz)) using (var ms = new MemoryStream()) { b.Save(ms, SD.Imaging.ImageFormat.Png); pngs.Add(ms.ToArray()); }
            using (var fs = File.Create(path)) using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
