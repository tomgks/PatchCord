using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace VencordAutoUpdater;

/// <summary>Top-left banner notifications. Port of the PowerShell Show-Alert / New-AlertVisual.</summary>
public static class Alert
{
    private static Window? _current;

    private static readonly FontFamily Sans =
        new(new Uri("pack://application:,,,/"), "./fonts/#Space Grotesk");

    private static TextBlock TB(string text, string color, double size, bool bold) => new()
    {
        Text = text,
        Foreground = Theme.Brush(color),
        FontFamily = Sans,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
    };

    /// <summary>Builds the banner visual from the current theme + style + scale.</summary>
    private static Border BuildVisual(AppConfig cfg, string message)
    {
        var ui = cfg.Ui;
        var p = Theme.Resolve(ui.Theme);
        double scale = ui.NotifyScale <= 0 ? 1 : ui.NotifyScale;
        var style = AppConfig.NotifyStyles.Contains(ui.NotifyStyle) ? ui.NotifyStyle : "bar";
        bool isHC = ui.Theme == "HighContrast";
        int pad = (int)Math.Round(16 * scale);
        double titleSize = Math.Round(13 * scale, 1);
        double msgSize = Math.Round(12.5 * scale, 1);

        var outer = new Border
        {
            CornerRadius = new CornerRadius(Math.Round(12 * scale)),
            Padding = new Thickness(pad),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Opacity = 0.7 },
            BorderBrush = Theme.Brush(p.Border),
            BorderThickness = new Thickness(isHC ? Math.Max(3, (int)Math.Round(3 * scale)) : 1),
        };

        switch (style)
        {
            case "solid":
            {
                outer.Background = Theme.Brush(p.Accent);
                var sp = new StackPanel();
                sp.Children.Add(TB("Vencord Auto-Updater", p.OnAccent, titleSize, true));
                var m = TB(message, p.OnAccent, msgSize, false);
                m.Margin = new Thickness(0, (int)(5 * scale), 0, 0);
                sp.Children.Add(m);
                outer.Child = sp;
                break;
            }
            case "minimal":
            {
                outer.Background = Theme.Brush(p.Card);
                var sp = new StackPanel();
                var dot = new Border
                {
                    Width = (int)(8 * scale), Height = (int)(8 * scale),
                    CornerRadius = new CornerRadius((int)(4 * scale)),
                    Background = Theme.Brush(p.Accent),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, (int)(6 * scale)),
                };
                sp.Children.Add(dot);
                sp.Children.Add(TB(message, p.Text, msgSize, false));
                outer.Child = sp;
                break;
            }
            case "outline":
            {
                outer.Background = Theme.Brush(p.Card);
                outer.BorderBrush = Theme.Brush(p.Accent);
                outer.BorderThickness = new Thickness(Math.Max(2, (int)Math.Round(2 * scale)));
                var sp = new StackPanel();
                sp.Children.Add(TB("Vencord Auto-Updater", p.Accent, titleSize, true));
                var m = TB(message, p.Sub, msgSize, false);
                m.Margin = new Thickness(0, (int)(5 * scale), 0, 0);
                sp.Children.Add(m);
                outer.Child = sp;
                break;
            }
            default: // bar
            {
                outer.Background = Theme.Brush(p.Card);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(5, 6 * scale)) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bar = new Border
                {
                    Background = Theme.Brush(p.Accent),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(0, 0, (int)(12 * scale), 0),
                };
                Grid.SetColumn(bar, 0);
                var sp = new StackPanel();
                Grid.SetColumn(sp, 1);
                sp.Children.Add(TB("Vencord Auto-Updater", p.Text, titleSize, true));
                var m = TB(message, p.Sub, msgSize, false);
                m.Margin = new Thickness(0, (int)(5 * scale), 0, 0);
                sp.Children.Add(m);
                grid.Children.Add(bar);
                grid.Children.Add(sp);
                outer.Child = grid;
                break;
            }
        }
        return outer;
    }

    public static void Show(AppConfig cfg, string message, bool force = false)
    {
        if (!force && !cfg.Ui.NotificationsEnabled) return;
        try
        {
            if (_current != null) { try { _current.Close(); } catch { } }
            double scale = cfg.Ui.NotifyScale <= 0 ? 1 : cfg.Ui.NotifyScale;
            double dur = cfg.Ui.NotifyDurationSec;

            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 0.94,
                Width = (int)Math.Round(360 * scale),
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 20,
                Top = 20,
                Content = BuildVisual(cfg, message),
            };
            w.MouseLeftButtonDown += (_, _) => { try { w.Close(); } catch { } };
            _current = w;
            w.Show();

            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(dur) };
            t.Tick += (_, _) => { t.Stop(); try { w.Close(); } catch { } };
            t.Start();
        }
        catch (Exception ex) { Log.Write($"Alert error: {ex.Message}", "WARN"); }
    }
}
