using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VencordAutoUpdater;

/// <summary>
/// BetterDiscord support. Unlike Vencord/Equicord (which patch <c>app.asar</c>),
/// BetterDiscord injects by overwriting Discord's core module entry point:
/// <c>app-&lt;ver&gt;\modules\discord_desktop_core(-N)\discord_desktop_core\index.js</c>
/// with <c>require("&lt;betterdiscord.asar&gt;"); module.exports = require("./core.asar");</c>.
/// Ported from BetterDiscord's scripts/inject.ts.
/// </summary>
public static class BetterDiscordEngine
{
    // The vanilla entry point Discord ships (and what we restore to).
    private const string Vanilla = "module.exports = require('./core.asar');\n";

    /// <summary>Resolves discord_desktop_core/index.js under an app-&lt;ver&gt; folder,
    /// preferring the newest wrapped <c>discord_desktop_core-N</c>, then the legacy layout.</summary>
    public static string? FindCoreIndexJs(string appDir)
    {
        var modules = Path.Combine(appDir, "modules");
        if (!Directory.Exists(modules)) return null;

        var wrapped = Directory.GetDirectories(modules, "discord_desktop_core-*")
            .Select(d => (dir: d, n: ParseSuffix(Path.GetFileName(d))))
            .Where(x => x.n >= 0)
            .OrderByDescending(x => x.n);
        foreach (var (dir, _) in wrapped)
        {
            var p = Path.Combine(dir, "discord_desktop_core", "index.js");
            if (File.Exists(p)) return p;
        }
        var legacy = Path.Combine(modules, "discord_desktop_core", "index.js");
        return File.Exists(legacy) ? legacy : null;
    }

    private static int ParseSuffix(string name)
    {
        var dash = name.LastIndexOf('-');
        return dash >= 0 && int.TryParse(name[(dash + 1)..], out var n) ? n : -1;
    }

    /// <summary>True if BetterDiscord is currently injected into this install.</summary>
    public static bool IsInjected(string appDir)
    {
        var idx = FindCoreIndexJs(appDir);
        if (idx == null) return false;
        try { return File.ReadAllText(idx).Contains("betterdiscord.asar", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The exact index.js contents the installer writes to load BetterDiscord.</summary>
    public static string InjectContent(string asarPath)
    {
        // JS-escape the absolute path exactly like the installer's require("...").
        var json = JsonSerializer.Serialize(asarPath,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $"require({json});\nmodule.exports = require(\"./core.asar\");\n";
    }

    /// <summary>Overwrites the core index.js to load BetterDiscord, then the real core.
    /// Caller must ensure <paramref name="asarPath"/> exists and Discord is stopped.</summary>
    public static void Inject(string appDir, string asarPath)
    {
        var idx = FindCoreIndexJs(appDir)
            ?? throw new FileNotFoundException($"discord_desktop_core/index.js not found under {appDir}");
        File.WriteAllText(idx, InjectContent(asarPath));
    }

    /// <summary>Dry-run: reports the core index.js path, detection, and the exact
    /// content that would be injected — without touching anything. Used by --bd-test.</summary>
    public static string DryRun(string appDir, string asarPath)
    {
        var idx = FindCoreIndexJs(appDir);
        var cur = idx != null && File.Exists(idx) ? File.ReadAllText(idx).Trim() : "(none)";
        return $"index.js: {idx ?? "(NOT FOUND)"}\ncurrent: {cur}\ninjected={IsInjected(appDir)}\n" +
               $"would write:\n{InjectContent(asarPath)}";
    }

    /// <summary>Restores the vanilla core index.js (removes BetterDiscord).</summary>
    public static void Restore(string appDir)
    {
        var idx = FindCoreIndexJs(appDir);
        if (idx == null) return;
        File.WriteAllText(idx, Vanilla);
    }
}
