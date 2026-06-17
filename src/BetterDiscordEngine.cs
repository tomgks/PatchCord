using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PatchCord;

// BetterDiscord injection. It overwrites app-<ver>\modules\discord_desktop_core\index.js
// to require betterdiscord.asar (a different file than Vencord/Equicord's app.asar).
// Logic from BetterDiscord's scripts/inject.ts.
public static class BetterDiscordEngine
{
    // What Discord ships, and what we restore to.
    private const string Vanilla = "module.exports = require('./core.asar');\n";

    // Newest wrapped discord_desktop_core-N first, then the legacy layout.
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

    public static bool IsInjected(string appDir)
    {
        var idx = FindCoreIndexJs(appDir);
        if (idx == null) return false;
        try { return File.ReadAllText(idx).Contains("betterdiscord.asar", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static string InjectContent(string asarPath)
    {
        // JS-escape the path the same way the installer's require("...") does.
        var json = JsonSerializer.Serialize(asarPath,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $"require({json});\nmodule.exports = require(\"./core.asar\");\n";
    }

    // asarPath must exist and Discord must be stopped first.
    public static void Inject(string appDir, string asarPath)
    {
        var idx = FindCoreIndexJs(appDir)
            ?? throw new FileNotFoundException($"discord_desktop_core/index.js not found under {appDir}");
        File.WriteAllText(idx, InjectContent(asarPath));
    }

    // --bd-test hook: reports the index.js path, detection, and what would be written.
    public static string DryRun(string appDir, string asarPath)
    {
        var idx = FindCoreIndexJs(appDir);
        var cur = idx != null && File.Exists(idx) ? File.ReadAllText(idx).Trim() : "(none)";
        return $"index.js: {idx ?? "(NOT FOUND)"}\ncurrent: {cur}\ninjected={IsInjected(appDir)}\n" +
               $"would write:\n{InjectContent(asarPath)}";
    }

    public static void Restore(string appDir)
    {
        var idx = FindCoreIndexJs(appDir);
        if (idx == null) return;
        File.WriteAllText(idx, Vanilla);
    }
}
