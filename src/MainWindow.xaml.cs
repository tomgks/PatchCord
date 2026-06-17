using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PatchCord;

public partial class MainWindow : Window
{
    private AppConfig _cfg = new();
    private readonly Dictionary<string, byte[]> _stubs = new(); // "vencord"/"equicord" -> stub bytes
    private readonly Dictionary<string, InstallState> _lastStates = new();
    private readonly HashSet<string> _alerted = new();
    // Installs whose patch failed this session; left alone so we don't keep killing Discord.
    private readonly HashSet<string> _patchFailed = new();

    private WinForms.NotifyIcon? _ni;
    private WinForms.ToolStripMenuItem? _trayHeader, _trayToggle;
    private DispatcherTimer? _timer;
    private Drawing.Icon? _iconOn, _iconOff;
    private bool _quitting;
    private string _warnMod = "vencord"; // mod the install warning/Get button points at

    private const string AppName = "PatchCord";

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Initialize(bool startHidden, bool selfTest)
    {
        _cfg = AppConfig.Load(App.ConfigFile);
        _stubs["vencord"] = PatchEngine.BuildStubAsar(App.VencordPatcherPath);
        _stubs["equicord"] = PatchEngine.BuildStubAsar(App.EquicordPatcherPath);
        WarnIfPatcherMissing();

        ContentRendered += (_, _) => { try { StatusScroll.ScrollToTop(); } catch { } };

        TitleBar.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        BtnMin.Click += (_, _) => { Hide(); _ni?.ShowBalloonTip(2000, AppName,
            "Minimized to the notification area. Click the \"show hidden icons\" arrow to find it.", WinForms.ToolTipIcon.None); };
        BtnClose.Click += (_, _) => { Hide(); _ni?.ShowBalloonTip(2500, AppName,
            "Still running in the background. Right-click the tray icon to quit.", WinForms.ToolTipIcon.None); };
        Closing += (_, e) => { if (!_quitting) { e.Cancel = true; Hide(); } };

        BtnToggle.Click += (_, _) => ToggleMonitoring();

        BtnDetectDiscord.Click += (_, _) => DetectBranch("Discord", "Discord (stable)");
        BtnDetectPTB.Click += (_, _) => DetectBranch("DiscordPTB", "Discord PTB");
        BtnAddCustom.Click += (_, _) => AddCustom();

        BtnNotifyToggle.Click += (_, _) =>
        {
            _cfg.Ui.NotificationsEnabled = !_cfg.Ui.NotificationsEnabled;
            Save();
            AddLogLine("Notifications " + (_cfg.Ui.NotificationsEnabled ? "ON" : "OFF"));
            UpdateSettingsUi();
        };
        BtnTestNotify.Click += (_, _) =>
            Alert.Show(_cfg, "Preview notification — this is how alerts look.", force: true);

        TabStatus.MouseLeftButtonUp += (_, _) => SwitchTab("status");
        TabOptions.MouseLeftButtonUp += (_, _) => SwitchTab("options");
        SwitchTab("status");

        BuildClientModChooser();
        BtnOpenAsarGlobal.Click += (_, _) =>
        {
            if (!_cfg.OpenAsar)
            {
                if (!ConfirmEnableOpenAsar()) return;
                _cfg.OpenAsar = true;
                AddLogLine("OpenAsar enabled.");
            }
            else
            {
                _cfg.OpenAsar = false;
                AddLogLine("OpenAsar disabled (existing installs left as-is).");
            }
            Save();
            UpdateSettingsUi();
            InvokeMonitor();
        };

        BtnGetMod.Click += (_, _) => OpenModDownload();
        BtnGetModOpt.Click += (_, _) => OpenModDownload();

        BtnCopyDiag.Click += (_, _) =>
        {
            var text = BuildDiagnostics();
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try { System.Windows.Clipboard.SetText(text); AddLogLine("Diagnostics copied to clipboard."); return; }
                catch { System.Threading.Thread.Sleep(80); } // clipboard can be briefly locked by another app
            }
            Log.Write("Copy diagnostics: clipboard stayed busy.", "WARN");
            AddLogLine("Couldn't copy, the clipboard was busy. Try again.");
        };

        BtnStartup.Click += (_, _) =>
        {
            try { Startup.Set(!Startup.IsEnabled); }
            catch (Exception ex) { Log.Write($"Startup toggle failed: {ex.Message}", "WARN"); AddLogLine("Couldn't change the startup setting."); }
            AddLogLine("Run at startup " + (Startup.IsEnabled ? "ON" : "OFF"));
            UpdateSettingsUi();
        };

        SliderInterval.Value = _cfg.IntervalSeconds;
        LblInterval.Text = $"Every {_cfg.IntervalSeconds} s";
        SliderInterval.ValueChanged += (_, _) =>
        {
            int v = (int)SliderInterval.Value;
            _cfg.IntervalSeconds = v;
            LblInterval.Text = $"Every {v} s";
            if (_timer != null) _timer.Interval = TimeSpan.FromSeconds(v);
            Save();
        };

        BtnAppearanceToggle.MouseLeftButtonUp += (_, _) =>
        {
            bool show = AppearancePanel.Visibility != Visibility.Visible;
            AppearancePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            AppearanceChevron.Text = show ? "▾" : "▸";
        };

        BuildThemeChips();
        BuildStyleChips();

        SliderNotifyScale.Value = _cfg.Ui.NotifyScale * 100;
        SliderNotifyScale.ValueChanged += (s, _) =>
        {
            _cfg.Ui.NotifyScale = Math.Round(SliderNotifyScale.Value / 100, 2);
            LblNotifyScale.Text = $"{(int)SliderNotifyScale.Value} %";
            Save();
        };
        SliderNotifyScale.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
                Alert.Show(_cfg, "Size preview. Detected a Discord instance without Vencord.", force: true)));
        SliderNotifyDur.Value = _cfg.Ui.NotifyDurationSec;
        SliderNotifyDur.ValueChanged += (s, _) =>
        {
            _cfg.Ui.NotifyDurationSec = (int)SliderNotifyDur.Value;
            LblNotifyDur.Text = $"{(int)SliderNotifyDur.Value} s";
            Save();
        };

        ApplyTheme();

        _iconOn = LoadIcon("tray-on.ico");
        _iconOff = LoadIcon("tray-off.ico");
        var appIcon = LoadIcon("app.ico") ?? Drawing.SystemIcons.Application;
        SetupTray(appIcon);

        UpdateStatusUi();
        UpdateSettingsUi();
        BuildInstallRows();
        AddLogLine($"{AppName} started.");
        Log.Write($"App started (Tray={startHidden}).", "INFO");

        if (selfTest)
        {
            // Validate the UI/tray build without arming the monitor (no patching).
            try { if (_ni != null) { _ni.Visible = false; _ni.Dispose(); } } catch { }
            return;
        }

        _timer = new DispatcherTimer();
        int iv = Math.Max(5, _cfg.IntervalSeconds);
        _timer.Interval = TimeSpan.FromSeconds(iv);
        _timer.Tick += (_, _) => { try { InvokeMonitor(); } catch (Exception ex) { Log.Write($"Monitor error: {ex.Message}", "ERROR"); } };
        _timer.Start();
        Dispatcher.InvokeAsync(() => { try { InvokeMonitor(); } catch (Exception ex) { Log.Write($"Initial monitor error: {ex.Message}", "ERROR"); } });
    }

    private void Save() => _cfg.Save(App.ConfigFile);

    private void SetupTray(Drawing.Icon appIcon)
    {
        var trayIcon = (_cfg.MonitoringEnabled ? _iconOn : _iconOff) ?? appIcon;
        _ni = new WinForms.NotifyIcon { Icon = trayIcon, Visible = true, Text = AppName };

        var menu = new WinForms.ContextMenuStrip();
        _trayHeader = new WinForms.ToolStripMenuItem(AppName) { Enabled = false };
        menu.Items.Add(_trayHeader);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        _trayToggle = new WinForms.ToolStripMenuItem("Pause monitoring", null, (_, _) => ToggleMonitoring());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => QuitApp());
        _ni.ContextMenuStrip = menu;
        _ni.MouseDoubleClick += (_, _) => ShowMainWindow();
    }

    private static Drawing.Icon? LoadIcon(string name)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/{name}", UriKind.Absolute));
            if (info != null) return new Drawing.Icon(info.Stream);
        }
        catch { }
        return null;
    }

    private void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void QuitApp()
    {
        _quitting = true;
        try { _timer?.Stop(); } catch { }
        try { if (_ni != null) { _ni.Visible = false; _ni.Dispose(); } } catch { }
        try { Application.Current.Shutdown(); } catch { }
    }

    private void ToggleMonitoring()
    {
        _cfg.MonitoringEnabled = !_cfg.MonitoringEnabled;
        Save();
        AddLogLine("Monitoring turned " + (_cfg.MonitoringEnabled ? "ON" : "OFF"));
        _alerted.Clear();
        _patchFailed.Clear();
        InvokeMonitor();
    }

    private void DetectBranch(string branch, string label)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), branch);
        if (File.Exists(Path.Combine(root, "Update.exe")))
        {
            _cfg.EnsureInstall(branch, branch, root, true, false);
            Save();
            AddLogLine($"Detected {label} and enabled it.");
            InvokeMonitor();
        }
        else
        {
            AddLogLine($"{label} not found in LOCALAPPDATA.");
            Alert.Show(_cfg, $"{label} was not found.", force: true);
        }
    }

    private void AddCustom()
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "Select a Discord install folder (the one containing Update.exe / app-* folders)",
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        var path = dlg.SelectedPath;
        bool valid = File.Exists(Path.Combine(path, "Update.exe"))
                     || (Directory.Exists(path) && Directory.GetDirectories(path, "app-*").Length > 0);
        if (!valid)
        {
            AddLogLine($"Not a Discord install: {path}");
            Alert.Show(_cfg, "That folder does not look like a Discord install.", force: true);
            return;
        }
        var branch = PatchEngine.BranchFromLeaf(path);
        _cfg.EnsureInstall("Custom: " + Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), branch, path, true, true);
        Save();
        AddLogLine($"Added custom path: {path}");
        InvokeMonitor();
    }

    private void ApplyTheme()
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        void Set(string key, string hex) => Resources[key] = Theme.Brush(hex);
        Set("Bg", p.Bg); Set("Card", p.Card); Set("Card2", p.Card2); Set("Border", p.Border);
        Set("Text", p.Text); Set("Sub", p.Sub); Set("Accent", p.Accent); Set("AccentHover", p.AccentHover);
        Set("OnAccent", p.OnAccent); Set("GhostBg", p.Ghost); Set("GhostHover", p.GhostHover); Set("Scroll", p.Scroll);

        TitleUnderline.Background = Theme.Brush(p.Border);

        UpdateStatusUi();
        UpdateSettingsUi();
        BuildInstallRows();
        SwitchTab(_activeTab);
    }

    private void BuildThemeChips()
    {
        foreach (var key in Theme.Keys)
        {
            var chip = MakeChip(key, Theme.Palettes[key].Label);
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _cfg.Ui.Theme = key;
                Save();
                ApplyTheme();
                AddLogLine($"Theme: {Theme.Palettes[key].Label}");
            };
            ThemePanel.Children.Add(chip);
        }
    }

    private void BuildStyleChips()
    {
        var ti = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
        foreach (var style in AppConfig.NotifyStyles)
        {
            var chip = MakeChip(style, ti.ToTitleCase(style));
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _cfg.Ui.NotifyStyle = style;
                Save();
                UpdateSettingsUi();
                AddLogLine($"Notification style: {style}");
                Alert.Show(_cfg, $"Style preview: {style}. Detected a Discord instance without Vencord.", force: true);
            };
            StylePanel.Children.Add(chip);
        }
    }

    private static Border MakeChip(string tag, string label)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(15, 8, 15, 8),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Tag = tag,
            Child = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold },
        };
        return b;
    }

    private void SetPanelSelection(System.Windows.Controls.Panel panel, string selectedTag)
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        foreach (var child in panel.Children)
        {
            if (child is not Border b) continue;
            bool sel = (string?)b.Tag == selectedTag;
            b.BorderBrush = Theme.Brush(sel ? p.Accent : p.Border);
            b.BorderThickness = new Thickness(sel ? 2 : 1);
            b.Background = Theme.Brush(sel ? p.GhostHover : p.Ghost);
        }
    }

    private void AddLogLine(string text)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text += $"{stamp}  {text}\n";
        var lines = LogText.Text.Split('\n');
        if (lines.Length > 200) LogText.Text = string.Join("\n", lines[^200..]);
        LogScroll.ScrollToBottom();
    }

    private void UpdateStatusUi()
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        bool on = _cfg.MonitoringEnabled;
        var dotColor = on ? p.On : "#80848E";
        StatusDot.Background = Theme.Brush(dotColor);
        StatusText.Text = on ? "Active" : "Paused";
        StatusSub.Text = on
            ? "Watching for Discord instances that need re-patching..."
            : "Monitoring is paused. Discord will not be re-patched.";
        BtnToggle.Content = on ? "Turn Off" : "Turn On";
        BtnToggle.Background = Theme.Brush(on ? p.Accent : p.On);
        BtnToggle.Foreground = Theme.Brush(on ? p.OnAccent : p.OnText);

        if (_ni != null)
        {
            _ni.Text = $"{AppName} - " + (on ? "Active" : "Paused");
            var stateIcon = on ? _iconOn : _iconOff;
            if (stateIcon != null) _ni.Icon = stateIcon;
        }
        if (_trayHeader != null) _trayHeader.Text = $"{AppName} — {(on ? "Active" : "Paused")}";
        if (_trayToggle != null) _trayToggle.Text = on ? "Pause monitoring" : "Resume monitoring";

        UpdateModWarning();
        BuildHistory();
    }

    private void UpdateModWarning()
    {
        var missing = _cfg.Installs
            .Where(i => i.Enabled && i.ClientMod != "none" && !App.ModInstalled(i.ClientMod))
            .Select(i => i.ClientMod).Distinct().ToList();
        var vis = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModWarn.Visibility = vis;
        ModWarnOpt.Visibility = vis;
        if (missing.Count > 0)
        {
            _warnMod = missing[0];
            var label = ModLabel(_warnMod);
            var dir = _warnMod switch
            {
                "equicord" => @"%APPDATA%\Equicord\dist",
                "betterdiscord" => @"%APPDATA%\BetterDiscord\data",
                _ => @"%APPDATA%\Vencord\dist",
            };
            var msg = missing.Count == 1
                ? $"{label} isn't installed yet. Run the {label} installer once (so {dir} exists) and this app will keep it injected after every Discord update."
                : $"Some installs use mods that aren't installed yet ({string.Join(", ", missing.Select(ModShort))}). Run each one's installer once so this app can keep them injected.";
            ModWarnText.Text = msg;
            ModWarnOptText.Text = msg;
            BtnGetMod.Content = $"Get {ModShort(_warnMod)}";
            BtnGetModOpt.Content = $"Get {ModShort(_warnMod)}";
        }
    }

    private static string ModDownloadUrl(string mod) => mod switch
    {
        "equicord" => "https://github.com/Equicord/Equicord#installing--uninstalling",
        "betterdiscord" => "https://betterdiscord.app/",
        _ => "https://vencord.dev/download/",
    };

    private void OpenModDownload()
    {
        if (_warnMod == "none") return;
        var url = ModDownloadUrl(_warnMod);
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Could not open {url}: {ex.Message}", "WARN"); }
        AddLogLine($"Opened {ModLabel(_warnMod)} download page.");
    }

    internal static string ModShort(string mod) => mod switch
    {
        "vencord" => "Vencord",
        "equicord" => "Equicord",
        "betterdiscord" => "BetterDiscord",
        _ => "None",
    };

    private static string Ago(DateTime utc)
    {
        var s = (DateTime.UtcNow - utc).TotalSeconds;
        if (s < 60) return "just now";
        if (s < 3600) return $"{(int)(s / 60)}m ago";
        if (s < 86400) return $"{(int)(s / 3600)}h ago";
        if (s < 7 * 86400) return $"{(int)(s / 86400)}d ago";
        return utc.ToLocalTime().ToString("MMM d");
    }

    private string BuildDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        sb.AppendLine($"PatchCord v{ver}");
        sb.AppendLine(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        sb.AppendLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        sb.AppendLine($"monitoring={(_cfg.MonitoringEnabled ? "on" : "off")}  interval={_cfg.IntervalSeconds}s  openAsar={(_cfg.OpenAsar ? "on" : "off")}  theme={_cfg.Ui.Theme}  runAtStartup={(Startup.IsEnabled ? "on" : "off")}");
        sb.AppendLine($"mods on disk: Vencord={(App.ModInstalled("vencord") ? "yes" : "no")}  Equicord={(App.ModInstalled("equicord") ? "yes" : "no")}  BetterDiscord={(App.ModInstalled("betterdiscord") ? "yes" : "no")}");
        sb.AppendLine();
        sb.AppendLine($"installs ({_cfg.Installs.Count}):");
        foreach (var i in _cfg.Installs)
        {
            InstallState st;
            try { st = PatchEngine.GetState(i, _cfg.OpenAsar); }
            catch { st = new InstallState(false, false, null, null, null, false); }
            sb.AppendLine($"- {i.Name}  mod={ModShort(i.ClientMod)}  {(i.Enabled ? "managed" : "paused")}{(i.Custom ? "  (custom)" : "")}");
            sb.AppendLine($"    {i.Path}");
            sb.AppendLine($"    running={st.Running} installed={st.Installed} app={st.AppName ?? "-"} injected={st.InjectedMod} openAsar={st.OpenAsarPresent}");
        }
        if (_cfg.History.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("recent patches:");
            foreach (var e in _cfg.History.Take(8))
                sb.AppendLine($"- {Ago(e.When)}  {e.Install}  {e.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("log (recent):");
        try
        {
            if (File.Exists(Log.FilePath))
            {
                using var fs = new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = sr.ReadToEnd().Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToArray();
                foreach (var l in lines.Reverse().Take(40).Reverse()) sb.AppendLine(l);
            }
            else sb.Append(LogText.Text);
        }
        catch (Exception ex) { sb.AppendLine($"(couldn't read log: {ex.Message})"); }
        return sb.ToString();
    }

    private void BuildHistory()
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        HistoryPanel.Children.Clear();
        if (_cfg.History.Count == 0)
        {
            HistoryPanel.Children.Add(new TextBlock
            {
                Text = "No re-patches yet.", Foreground = Theme.Brush(p.Sub), FontSize = 12,
                Margin = new Thickness(2, 0, 0, 8),
            });
            return;
        }
        foreach (var e in _cfg.History.Take(6))
        {
            var tb = new TextBlock { FontSize = 12, Margin = new Thickness(2, 0, 0, 7) };
            tb.Inlines.Add(new System.Windows.Documents.Run(Ago(e.When) + "   ") { Foreground = Theme.Brush(p.Sub) });
            tb.Inlines.Add(new System.Windows.Documents.Run(e.Install) { Foreground = Theme.Brush(p.Text), FontWeight = FontWeights.SemiBold });
            tb.Inlines.Add(new System.Windows.Documents.Run("  ·  " + e.Summary) { Foreground = Theme.Brush(p.Sub) });
            HistoryPanel.Children.Add(tb);
        }
    }

    private void UpdateSettingsUi()
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        bool n = _cfg.Ui.NotificationsEnabled;
        BtnNotifyToggle.Content = n ? "On" : "Off";
        BtnNotifyToggle.Background = Theme.Brush(n ? p.On : p.GhostHover);
        BtnNotifyToggle.Foreground = Theme.Brush(n ? p.OnText : p.Text);
        bool oa = _cfg.OpenAsar;
        BtnOpenAsarGlobal.Content = oa ? "On" : "Off";
        BtnOpenAsarGlobal.Background = Theme.Brush(oa ? p.On : p.GhostHover);
        BtnOpenAsarGlobal.Foreground = Theme.Brush(oa ? p.OnText : p.Text);
        bool su = Startup.IsEnabled;
        BtnStartup.Content = su ? "On" : "Off";
        BtnStartup.Background = Theme.Brush(su ? p.On : p.GhostHover);
        BtnStartup.Foreground = Theme.Brush(su ? p.OnText : p.Text);
        LblNotifyScale.Text = $"{(int)(_cfg.Ui.NotifyScale * 100)} %";
        LblNotifyDur.Text = $"{_cfg.Ui.NotifyDurationSec} s";
        SetPanelSelection(ThemePanel, _cfg.Ui.Theme);
        SetPanelSelection(StylePanel, _cfg.Ui.NotifyStyle);
        UpdateClientModSelection();
        foreach (var panel in new[] { ThemePanel, StylePanel })
            foreach (var child in panel.Children)
                if (child is Border b && b.Child is TextBlock tb) tb.Foreground = Theme.Brush(p.Text);
    }

    private string _activeTab = "status";

    private void SwitchTab(string tab)
    {
        _activeTab = tab;
        var p = Theme.Resolve(_cfg.Ui.Theme);
        bool status = tab == "status";
        StatusScroll.Visibility = status ? Visibility.Visible : Visibility.Collapsed;
        OptionsScroll.Visibility = status ? Visibility.Collapsed : Visibility.Visible;
        TabStatus.BorderBrush = Theme.Brush(status ? p.Accent : p.Bg);
        TabOptions.BorderBrush = Theme.Brush(!status ? p.Accent : p.Bg);
        TabStatusText.Foreground = Theme.Brush(status ? p.Text : p.Sub);
        TabOptionsText.Foreground = Theme.Brush(!status ? p.Text : p.Sub);
    }

    private static readonly (string mod, string title, string desc)[] ClientModItems =
    {
        ("vencord",       "Vencord",       "The original Discord client mod — adds plugins, themes and tweaks."),
        ("equicord",      "Equicord",      "A community fork of Vencord with 300+ extra plugins."),
        ("betterdiscord", "BetterDiscord", "The long-running client mod with a plugin/theme store. Patches Discord's core (a different method than Vencord)."),
        ("none",          "No client mod", "Don't keep any client mod injected (you can still use OpenAsar)."),
    };

    private void BuildClientModChooser()
    {
        foreach (var (mod, title, desc) in ClientModItems)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 9),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = mod,
            };
            var sp = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = desc, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            card.Child = sp;
            var capturedMod = mod;
            card.MouseLeftButtonUp += (_, _) =>
            {
                _cfg.ClientMod = capturedMod;                          // default for new installs
                foreach (var i in _cfg.Installs) i.ClientMod = capturedMod; // apply to all current installs
                _patchFailed.Clear();
                Save();
                WarnIfPatcherMissing();
                AddLogLine($"Client mod set to {ModShort(capturedMod)} for all installs.");
                UpdateSettingsUi();
                BuildInstallRows();
                InvokeMonitor();
            };
            ClientModPanel.Children.Add(card);
        }
    }

    private Border MakeModMenuItem(string title, bool selected)
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        var b = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7, 10, 7),
            Cursor = Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            Child = new TextBlock
            {
                Text = title, FontSize = 12,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Theme.Brush(selected ? p.Accent : p.Text),
            },
        };
        b.MouseEnter += (_, _) => b.Background = Theme.Brush(p.GhostHover);
        b.MouseLeave += (_, _) => b.Background = System.Windows.Media.Brushes.Transparent;
        return b;
    }

    private void UpdateClientModSelection()
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        foreach (var child in ClientModPanel.Children)
        {
            if (child is not Border b) continue;
            bool sel = (string?)b.Tag == _cfg.ClientMod;
            b.Background = sel ? Theme.Brush(p.GhostHover) : System.Windows.Media.Brushes.Transparent;
            b.BorderBrush = sel ? Theme.Brush(p.Accent) : System.Windows.Media.Brushes.Transparent;
            b.BorderThickness = new Thickness(sel ? 1 : 0);
            if (b.Child is StackPanel sp && sp.Children.Count == 2)
            {
                ((TextBlock)sp.Children[0]).Foreground = Theme.Brush(p.Text);
                ((TextBlock)sp.Children[1]).Foreground = Theme.Brush(p.Sub);
            }
        }
    }

    private void BuildInstallRows()
    {
        var p = Theme.Resolve(_cfg.Ui.Theme);
        InstallList.Children.Clear();
        if (_cfg.Installs.Count == 0)
        {
            InstallList.Children.Add(new TextBlock
            {
                Text = "No Discord installs added yet. Use the buttons below to detect or add one.",
                Foreground = Theme.Brush(p.Sub),
                FontSize = 12,
                Margin = new Thickness(2, 0, 0, 8),
            });
            return;
        }

        foreach (var inst in _cfg.Installs)
        {
            var captured = inst;
            var st = _lastStates.TryGetValue(inst.Path, out var s) ? s : PatchEngine.GetState(inst, _cfg.OpenAsar);
            var row = new InstallRow();
            row.RowRoot.Background = System.Windows.Media.Brushes.Transparent;
            row.RowRoot.BorderBrush = Theme.Brush(p.Border);
            row.RowName.Text = inst.Name;
            row.RowName.Foreground = Theme.Brush(p.Text);
            row.RowPath.Text = inst.Path;
            row.RowPath.Foreground = Theme.Brush(p.Sub);

            void SetBadge(Border badge, TextBlock label, string bg, string fg, string text)
            {
                badge.Visibility = Visibility.Visible;
                badge.Background = Theme.Brush(bg);
                label.Text = text;
                label.Foreground = Theme.Brush(fg);
            }
            var desired = inst.ClientMod;
            if (!st.Installed)
                SetBadge(row.RowBadge, row.RowStatus, "#80848E", "#FFFFFF", "Not installed");
            else if (st.InjectedMod == "vencord")
                SetBadge(row.RowBadge, row.RowStatus, p.On, p.OnText, "Vencord");
            else if (st.InjectedMod == "equicord")
                SetBadge(row.RowBadge, row.RowStatus, p.On, p.OnText, "Equicord");
            else if (st.InjectedMod == "betterdiscord")
                SetBadge(row.RowBadge, row.RowStatus, p.On, p.OnText, "BetterDiscord");
            else if (st.InjectedMod == "other")
                SetBadge(row.RowBadge, row.RowStatus, "#80848E", "#FFFFFF", "Other mod");
            else if (inst.Enabled && desired != "none" && st.Running)
                SetBadge(row.RowBadge, row.RowStatus, "#F23F43", "#FFFFFF", $"No {ModLabel(desired)}");
            else if (inst.Enabled && desired != "none")
                SetBadge(row.RowBadge, row.RowStatus, "#80848E", "#FFFFFF", "Not patched");
            else
                row.RowBadge.Visibility = Visibility.Collapsed;

            if (st.Installed && st.OpenAsarPresent)
                SetBadge(row.RowOpenAsarBadge, row.RowOpenAsarStatus, p.On, p.OnText, "OpenAsar");
            else if (st.Installed && _cfg.OpenAsar && inst.Enabled && st.Running)
                SetBadge(row.RowOpenAsarBadge, row.RowOpenAsarStatus, "#F23F43", "#FFFFFF", "No OpenAsar");
            else if (st.Installed && _cfg.OpenAsar && inst.Enabled)
                SetBadge(row.RowOpenAsarBadge, row.RowOpenAsarStatus, "#80848E", "#FFFFFF", "OpenAsar off");
            else
                row.RowOpenAsarBadge.Visibility = Visibility.Collapsed;

            var tg = row.RowToggle;
            tg.Background = Theme.Brush(inst.Enabled ? p.On : p.GhostHover);
            tg.Foreground = Theme.Brush(inst.Enabled ? p.OnText : p.Text);
            tg.Content = inst.Enabled ? "Managed" : "Paused";
            tg.Click += (_, _) => { captured.Enabled = !captured.Enabled; _patchFailed.Remove(captured.Path); Save(); InvokeMonitor(); };

            // per-install mod picker
            var modBtn = row.RowModBtn;
            modBtn.Content = ModShort(inst.ClientMod) + "   ▾";
            modBtn.Background = Theme.Brush(p.GhostHover);
            modBtn.Foreground = Theme.Brush(p.Text);
            modBtn.Click += (_, _) => row.RowModPopup.IsOpen = true;
            row.RowModPopupBox.Background = Theme.Brush(p.Card2);
            row.RowModPopupBox.BorderBrush = Theme.Brush(p.Border);
            row.RowModItems.Children.Clear();
            foreach (var (mod, title, _) in ClientModItems)
            {
                var item = MakeModMenuItem(title, mod == inst.ClientMod);
                var capturedMod = mod;
                item.MouseLeftButtonUp += (_, _) =>
                {
                    row.RowModPopup.IsOpen = false;
                    if (captured.ClientMod == capturedMod) return;
                    captured.ClientMod = capturedMod;
                    _patchFailed.Remove(captured.Path);
                    Save();
                    WarnIfPatcherMissing();
                    AddLogLine($"{captured.Name}: client mod set to {ModShort(capturedMod)}");
                    InvokeMonitor();
                };
                row.RowModItems.Children.Add(item);
            }

            var rm = row.RowRemove;
            rm.Foreground = Theme.Brush(p.Sub);
            if (inst.Custom)
            {
                rm.Click += (_, _) =>
                {
                    _cfg.Installs.RemoveAll(i => string.Equals(i.Path, captured.Path, StringComparison.OrdinalIgnoreCase));
                    Save();
                    AddLogLine($"Removed {captured.Name}");
                    UpdateStatusUi();
                    BuildInstallRows();
                };
            }
            else
            {
                rm.Visibility = Visibility.Collapsed;
            }

            InstallList.Children.Add(row);
        }
    }

    private bool ConfirmEnableOpenAsar()
    {
        var result = System.Windows.MessageBox.Show(this,
            "OpenAsar is a separate open-source project (GooseMod, AGPL-3.0) — an alternative " +
            "to Discord's app.asar. It is NOT affiliated with PatchCord or any client mod.\n\n" +
            "If you turn this on, the app will download OpenAsar from its official GitHub " +
            "releases and keep it installed by replacing Discord's app.asar after updates.\n\n" +
            "You do this at your own risk. Enable OpenAsar for this install?",
            "Enable OpenAsar?", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        return result == System.Windows.MessageBoxResult.Yes;
    }

    internal static string ModLabel(string mod) => mod switch
    {
        "vencord" => "Vencord",
        "equicord" => "Equicord",
        "betterdiscord" => "BetterDiscord",
        _ => "the client mod",
    };

    private void WarnIfPatcherMissing()
    {
        foreach (var i in _cfg.Installs)
            if (i.Enabled && i.ClientMod != "none" && !App.ModInstalled(i.ClientMod))
                Log.Write($"{i.Name}: {ModLabel(i.ClientMod)} isn't installed yet (install it once).", "WARN");
    }

    private void InvokeMonitor()
    {
        bool wantOpenAsar = _cfg.OpenAsar;
        var states = new Dictionary<string, InstallState>();
        var candidates = new List<(Install inst, string desiredAsar, bool desiredBD, bool needOpenAsar, bool asarChange, bool bdChange)>();
        foreach (var inst in _cfg.Installs)
        {
            var st = PatchEngine.GetState(inst, wantOpenAsar);
            states[inst.Path] = st;
            var key = inst.Path;

            var desired = inst.ClientMod;                              // each install picks its own mod
            bool modReady = desired == "none" || App.ModInstalled(desired);
            string desiredAsar = desired is "vencord" or "equicord" ? desired : "none";
            bool desiredBD = desired == "betterdiscord";

            bool managed = inst.Enabled && st.Installed && st.Running && st.Resources != null && st.AppDir != null
                           && !_patchFailed.Contains(inst.Path);
            bool needOpenAsar = managed && wantOpenAsar && !st.OpenAsarPresent;

            bool asarChange = false, bdChange = false;
            if (managed && modReady)
            {
                // Layer A (app.asar): only ever touch our own vencord/equicord stubs.
                asarChange = desiredAsar == "none"
                    ? st.AsarMod is "vencord" or "equicord"
                    : st.AsarMod != desiredAsar && st.AsarMod is "vencord" or "equicord" or "none";
                // Layer B (BetterDiscord core patch).
                bdChange = desiredBD ? !st.BdActive : st.BdActive;
            }

            if (needOpenAsar || asarChange || bdChange)
            {
                candidates.Add((inst, desiredAsar, desiredBD, needOpenAsar, asarChange, bdChange));
                if (!_alerted.Contains(key))
                {
                    _alerted.Add(key);
                    var parts = new List<string>();
                    if (asarChange || bdChange) parts.Add(desired == "none" ? "no client mod" : ModLabel(desired));
                    if (needOpenAsar) parts.Add("OpenAsar");
                    var what = string.Join(" + ", parts);
                    if (_cfg.MonitoringEnabled)
                    {
                        Alert.Show(_cfg, $"Restoring {what} on {inst.Name} and restarting Discord...");
                        Log.Write($"{inst.Name}: applying {what}...", "ACTION");
                    }
                    else
                    {
                        Alert.Show(_cfg, $"{inst.Name} needs {what}, but monitoring is OFF — leaving it as-is.");
                        Log.Write($"{inst.Name}: needs {what} (monitoring OFF).", "WARN");
                    }
                }
            }
            else
            {
                _alerted.Remove(key);
            }
        }
        foreach (var kv in states) _lastStates[kv.Key] = kv.Value;

        bool recorded = false;
        if (_cfg.MonitoringEnabled && candidates.Count > 0)
        {
            if (Process.GetProcessesByName("Update").Length > 0)
            {
                Log.Write("Discord update in progress; deferring patch.", "WARN");
            }
            else
            {
                var stopped = new List<Install>();
                var done = new List<(Install inst, string summary)>();
                foreach (var (c, desiredAsar, desiredBD, needOpenAsar, asarChange, bdChange) in candidates)
                {
                    try
                    {
                        PatchEngine.StopProcesses(c.Branch);
                        stopped.Add(c);
                        var st = states[c.Path];
                        var resources = st.Resources!;
                        var appDir = st.AppDir!;
                        var changes = new List<string>();
                        // OpenAsar first (underlying asar), then the app.asar client mod on top.
                        if (needOpenAsar)
                        {
                            OpenAsarEngine.Install(resources);
                            Log.Write($"{c.Name}: OpenAsar installed.", "OK");
                            changes.Add("OpenAsar");
                        }
                        if (asarChange)
                        {
                            if (st.AsarMod is "vencord" or "equicord")
                                PatchEngine.Unpatch(resources);
                            if (desiredAsar != "none")
                            {
                                PatchEngine.Patch(resources, _stubs[desiredAsar]);
                                Log.Write($"{c.Name}: {ModLabel(desiredAsar)} injected.", "OK");
                                changes.Add(ModShort(desiredAsar));
                            }
                            else { Log.Write($"{c.Name}: client mod removed.", "OK"); changes.Add("removed client mod"); }
                        }
                        if (bdChange)
                        {
                            if (desiredBD)
                            {
                                BetterDiscordEngine.Inject(appDir, App.BetterDiscordAsarPath);
                                Log.Write($"{c.Name}: BetterDiscord injected.", "OK");
                                changes.Add("BetterDiscord");
                            }
                            else
                            {
                                BetterDiscordEngine.Restore(appDir);
                                Log.Write($"{c.Name}: BetterDiscord removed.", "OK");
                                changes.Add("removed BetterDiscord");
                            }
                        }
                        done.Add((c, changes.Count > 0 ? string.Join(" + ", changes) : "re-patched"));
                    }
                    catch (Exception ex)
                    {
                        // Back off so we don't kill Discord again on the next check.
                        _patchFailed.Add(c.Path);
                        Log.Write($"Failed to patch {c.Name}: {ex.Message}. Leaving it alone. " +
                                  "Re-run that mod's installer, then toggle the install off and on.", "ERROR");
                    }
                }
                // Always restart Discord if we stopped it, even on a patch failure.
                foreach (var c in stopped)
                {
                    PatchEngine.StartDiscord(c.Path, $"{c.Branch}.exe");
                    Log.Write($"Restarted {c.Name}.", "OK");
                    _lastStates[c.Path] = PatchEngine.GetState(c, wantOpenAsar);
                }
                foreach (var (c, summary) in done)
                {
                    Alert.Show(_cfg, $"Restored {c.Name}. Discord has been restarted.");
                    _cfg.AddHistory(c.Name, summary);
                    recorded = true;
                }
            }
        }
        if (recorded) Save();

        UpdateStatusUi();
        BuildInstallRows();
    }
}
