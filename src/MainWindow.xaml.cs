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
    private DispatcherTimer? _timer;
    private Drawing.Icon? _iconOn, _iconOff;
    private bool _quitting;

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
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Toggle monitoring", null, (_, _) => ToggleMonitoring());
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

        UpdateModWarning();
    }

    private void UpdateModWarning()
    {
        bool missing = _cfg.ClientMod != "none" && !App.ModInstalled(_cfg.ClientMod);
        var vis = missing ? Visibility.Visible : Visibility.Collapsed;
        ModWarn.Visibility = vis;
        ModWarnOpt.Visibility = vis;
        if (missing)
        {
            var label = ModLabel(_cfg.ClientMod);
            var dir = _cfg.ClientMod switch
            {
                "equicord" => @"%APPDATA%\Equicord\dist",
                "betterdiscord" => @"%APPDATA%\BetterDiscord\data",
                _ => @"%APPDATA%\Vencord\dist",
            };
            var msg = $"{label} isn't installed yet. Run the {label} installer at least once " +
                      $"(so {dir} exists) — then this app will keep it injected after every Discord update.";
            ModWarnText.Text = msg;
            ModWarnOptText.Text = msg;
            BtnGetMod.Content = $"Get {label}";
            BtnGetModOpt.Content = $"Get {label}";
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
        if (_cfg.ClientMod == "none") return;
        var url = ModDownloadUrl(_cfg.ClientMod);
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Could not open {url}: {ex.Message}", "WARN"); }
        AddLogLine($"Opened {ModLabel(_cfg.ClientMod)} download page.");
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
                if (_cfg.ClientMod == capturedMod) return;
                _cfg.ClientMod = capturedMod;
                _patchFailed.Clear();
                Save();
                WarnIfPatcherMissing();
                AddLogLine($"Client mod: {(capturedMod == "none" ? "None" : ModLabel(capturedMod))}");
                UpdateSettingsUi();
                InvokeMonitor();
            };
            ClientModPanel.Children.Add(card);
        }
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
            var desired = _cfg.ClientMod;
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
            row.RowOpenAsarToggle.Visibility = Visibility.Collapsed; // mod/OpenAsar choice lives in Options

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
        if (_cfg.ClientMod == "none") return;
        if (!App.ModInstalled(_cfg.ClientMod))
            Log.Write($"{ModLabel(_cfg.ClientMod)} isn't installed yet (install it once).", "WARN");
    }

    private void InvokeMonitor()
    {
        var desired = _cfg.ClientMod;                 // vencord / equicord / betterdiscord / none
        bool wantOpenAsar = _cfg.OpenAsar;
        // Don't touch Discord if the mod isn't installed yet.
        bool modReady = desired == "none" || App.ModInstalled(desired);
        string desiredAsar = desired is "vencord" or "equicord" ? desired : "none"; // app.asar layer
        bool desiredBD = desired == "betterdiscord";                                 // core index.js layer

        var states = new Dictionary<string, InstallState>();
        var candidates = new List<(Install inst, bool needOpenAsar, bool asarChange, bool bdChange)>();
        foreach (var inst in _cfg.Installs)
        {
            var st = PatchEngine.GetState(inst, wantOpenAsar);
            states[inst.Path] = st;
            var key = inst.Path;

            bool managed = inst.Enabled && st.Installed && st.Running && st.Resources != null && st.AppDir != null
                           && !_patchFailed.Contains(inst.Path);
            bool needOpenAsar = managed && wantOpenAsar && !st.OpenAsarPresent;

            bool asarChange = false, bdChange = false;
            if (managed && (desired == "none" || modReady))
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
                candidates.Add((inst, needOpenAsar, asarChange, bdChange));
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

        if (_cfg.MonitoringEnabled && candidates.Count > 0)
        {
            if (Process.GetProcessesByName("Update").Length > 0)
            {
                Log.Write("Discord update in progress; deferring patch.", "WARN");
            }
            else
            {
                var stopped = new List<Install>();
                var done = new List<Install>();
                foreach (var (c, needOpenAsar, asarChange, bdChange) in candidates)
                {
                    try
                    {
                        PatchEngine.StopProcesses(c.Branch);
                        stopped.Add(c);
                        var st = states[c.Path];
                        var resources = st.Resources!;
                        var appDir = st.AppDir!;
                        // OpenAsar first (underlying asar), then the app.asar client mod on top.
                        if (needOpenAsar)
                        {
                            OpenAsarEngine.Install(resources);
                            Log.Write($"{c.Name}: OpenAsar installed.", "OK");
                        }
                        if (asarChange)
                        {
                            if (st.AsarMod is "vencord" or "equicord")
                                PatchEngine.Unpatch(resources);
                            if (desiredAsar != "none")
                            {
                                PatchEngine.Patch(resources, _stubs[desiredAsar]);
                                Log.Write($"{c.Name}: {ModLabel(desiredAsar)} injected.", "OK");
                            }
                            else Log.Write($"{c.Name}: client mod removed.", "OK");
                        }
                        if (bdChange)
                        {
                            if (desiredBD)
                            {
                                BetterDiscordEngine.Inject(appDir, App.BetterDiscordAsarPath);
                                Log.Write($"{c.Name}: BetterDiscord injected.", "OK");
                            }
                            else
                            {
                                BetterDiscordEngine.Restore(appDir);
                                Log.Write($"{c.Name}: BetterDiscord removed.", "OK");
                            }
                        }
                        done.Add(c);
                    }
                    catch (Exception ex)
                    {
                        // Back off so we don't kill Discord again on the next check.
                        _patchFailed.Add(c.Path);
                        Log.Write($"Failed to patch {c.Name}: {ex.Message}. Leaving it alone — " +
                                  $"re-run the {ModLabel(desired)} installer, then toggle this install off/on.", "ERROR");
                    }
                }
                // Always restart Discord if we stopped it, even on a patch failure.
                foreach (var c in stopped)
                {
                    PatchEngine.StartDiscord(c.Path, $"{c.Branch}.exe");
                    Log.Write($"Restarted {c.Name}.", "OK");
                    _lastStates[c.Path] = PatchEngine.GetState(c, wantOpenAsar);
                }
                foreach (var c in done)
                    Alert.Show(_cfg, $"Restored {c.Name}. Discord has been restarted.");
            }
        }

        UpdateStatusUi();
        BuildInstallRows();
    }
}
