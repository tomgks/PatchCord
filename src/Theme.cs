using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PatchCord;

// A theme's colour set.
public sealed class ThemePalette
{
    public required string Label, Bg, Card, Card2, Border, Text, Sub,
        Accent, AccentHover, OnAccent, Ghost, GhostHover, RowBg, On, OnText, Scroll;
}

public static class Theme
{
    // Insertion order is the order shown in the theme selector.
    public static readonly string[] Keys = { "Discord", "Dark", "Light", "HighContrast" };

    public static readonly IReadOnlyDictionary<string, ThemePalette> Palettes =
        new Dictionary<string, ThemePalette>
        {
            ["Discord"] = new ThemePalette {
                Label = "Discord", Bg = "#1E1F22", Card = "#2B2D31", Card2 = "#313338", Border = "#3A3C42",
                Text = "#F2F3F5", Sub = "#B5BAC1", Accent = "#5865F2", AccentHover = "#4752C4", OnAccent = "#FFFFFF",
                Ghost = "#383A40", GhostHover = "#41434A", RowBg = "#313338", On = "#23A55A", OnText = "#FFFFFF", Scroll = "#4E5058" },
            ["Dark"] = new ThemePalette {
                Label = "Dark mode", Bg = "#05070C", Card = "#0B0E15", Card2 = "#121726", Border = "#1E2636",
                Text = "#EEF1F7", Sub = "#99A2B5", Accent = "#C8CEDD", AccentHover = "#A3ABBD", OnAccent = "#05070C",
                Ghost = "#121726", GhostHover = "#1B2234", RowBg = "#0B0E15", On = "#46B27D", OnText = "#FFFFFF", Scroll = "#29314A" },
            ["Light"] = new ThemePalette {
                Label = "Light", Bg = "#E1E3E7", Card = "#FFFFFF", Card2 = "#F4F5F7", Border = "#D2D5DA",
                Text = "#1A1B1E", Sub = "#5C5E66", Accent = "#5865F2", AccentHover = "#4752C4", OnAccent = "#FFFFFF",
                Ghost = "#E6E8EC", GhostHover = "#D8DBE0", RowBg = "#F2F3F5", On = "#1F9D55", OnText = "#FFFFFF", Scroll = "#C2C6CC" },
            ["HighContrast"] = new ThemePalette {
                Label = "High Contrast", Bg = "#070210", Card = "#0B0418", Card2 = "#120824", Border = "#682084",
                Text = "#F4EEFF", Sub = "#B9A3E6", Accent = "#742BD7", AccentHover = "#612D96", OnAccent = "#FFFFFF",
                Ghost = "#0E0619", GhostHover = "#1E1136", RowBg = "#08030F", On = "#86C25A", OnText = "#0A1505", Scroll = "#5C4A82" },
        };

    public static ThemePalette Resolve(string? key) =>
        key != null && Palettes.TryGetValue(key, out var p) ? p : Palettes["Dark"];

    public static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex)!);

    public static DropShadowEffect Glow(string hex, double blur = 14) => new()
    {
        BlurRadius = blur, ShadowDepth = 0, Opacity = 0.9,
        Color = (Color)ColorConverter.ConvertFromString(hex)!,
    };

    public static LinearGradientBrush Gradient(string from, string to, bool horizontal = false)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1),
        };
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(from)!, 0));
        b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(to)!, 1));
        return b;
    }
}
