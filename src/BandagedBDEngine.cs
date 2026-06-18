using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PatchCord;

// BandagedBD injects differently from the others: it drops a whole "app" folder into
// resources/ that Electron loads instead of app.asar. There's no small stub to generate,
// so we snapshot that folder when we see it installed and copy it back after a Discord
// update wipes it. Snapshots live in <exe dir>\bbd\<install hash>.
public static class BandagedBDEngine
{
    private static string SnapRoot => Path.Combine(App.BaseDir, "bbd");

    private static string Key(string installPath)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(installPath.ToLowerInvariant().TrimEnd('\\', '/')));
        return Convert.ToHexString(bytes)[..12];
    }

    private static string SnapDir(string installPath) => Path.Combine(SnapRoot, Key(installPath));

    // resources/app is a directory whose index.js loads BetterDiscord.
    public static bool IsInjected(string resourcesDir)
    {
        var idx = Path.Combine(resourcesDir, "app", "index.js");
        if (!File.Exists(idx)) return false;
        try { return File.ReadAllText(idx).Contains("betterdiscord", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static bool HasSnapshot(string installPath) =>
        File.Exists(Path.Combine(SnapDir(installPath), "index.js"));

    public static bool HasAnySnapshot() =>
        Directory.Exists(SnapRoot) &&
        Directory.GetDirectories(SnapRoot).Any(d => File.Exists(Path.Combine(d, "index.js")));

    // Copy resources/app -> snapshot. No-op unless the live copy is newer than the snapshot.
    public static void CaptureSnapshot(string resourcesDir, string installPath)
    {
        var src = Path.Combine(resourcesDir, "app");
        var srcIdx = Path.Combine(src, "index.js");
        if (!File.Exists(srcIdx)) return;
        var dst = SnapDir(installPath);
        var dstIdx = Path.Combine(dst, "index.js");
        if (File.Exists(dstIdx) && File.GetLastWriteTimeUtc(dstIdx) >= File.GetLastWriteTimeUtc(srcIdx)) return;
        try
        {
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            CopyDir(src, dst);
            Log.Write($"Snapshotted BandagedBD ({Path.GetFileName(installPath)}).", "OK");
        }
        catch (Exception ex) { Log.Write($"BandagedBD snapshot failed: {ex.Message}", "WARN"); }
    }

    // Copy snapshot -> resources/app. Discord must be stopped first.
    public static void Restore(string resourcesDir, string installPath)
    {
        var snap = SnapDir(installPath);
        if (!File.Exists(Path.Combine(snap, "index.js")))
            throw new DirectoryNotFoundException("No BandagedBD snapshot to restore from.");
        var dst = Path.Combine(resourcesDir, "app");
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
        CopyDir(snap, dst);
    }

    public static void Remove(string resourcesDir)
    {
        var dst = Path.Combine(resourcesDir, "app");
        if (Directory.Exists(dst)) Directory.Delete(dst, true);
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
