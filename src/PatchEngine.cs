using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PatchCord;

// Live state of one install. AsarMod is the app.asar-layer mod
// (none/vencord/equicord/other); BdActive is the BetterDiscord core patch;
// BbdActive is the BandagedBD app-folder injection.
public sealed record InstallState(
    bool Running, bool Patched, string? AppName, string? Resources, string? AppDir, bool Installed,
    bool OpenAsarPresent = false, string AsarMod = "none", bool BdActive = false, bool BbdActive = false)
{
    public string InjectedMod => AsarMod is "vencord" or "equicord" ? AsarMod
        : BbdActive ? "bandagedbd"
        : BdActive ? "betterdiscord"
        : (AsarMod == "other" ? "other" : "none");
}

// Vencord/Equicord asar patch: rename app.asar to _app.asar, write a stub app.asar
// that requires the mod's patcher.js. Logic from the Vencord installer.
public static class PatchEngine
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static byte[] BuildStubAsar(string patcher)
    {
        var patcherJson = JsonSerializer.Serialize(patcher,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var indexJs = $"require({patcherJson})";
        var packageJson = "{\n\t\"name\": \"discord\",\n\t\"main\": \"index.js\"\n}";

        int indexLen = Utf8.GetByteCount(indexJs);
        int pkgLen = Utf8.GetByteCount(packageJson);
        var fileContents = indexJs + packageJson;

        var header = "{\"files\":{\"index.js\":{\"size\":" + indexLen + ",\"offset\":\"0\"}," +
                     "\"package.json\":{\"size\":" + pkgLen + ",\"offset\":\"" + indexLen + "\"}}}";

        int headerStringSize = Utf8.GetByteCount(header);
        const int dataSize = 4;
        int alignedSize = (headerStringSize + dataSize - 1) & ~(dataSize - 1);
        int headerSize = alignedSize + 8;
        int headerObjectSize = alignedSize + dataSize;
        int diff = alignedSize - headerStringSize;
        if (diff > 0) header += new string('0', diff);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        foreach (var n in new[] { dataSize, headerSize, headerObjectSize, headerStringSize })
            bw.Write(n); // little-endian int32
        bw.Write(Utf8.GetBytes(header));
        bw.Write(Utf8.GetBytes(fileContents));
        bw.Flush();
        return ms.ToArray();
    }

    // Highest-versioned app-* folder that has a resources dir.
    public static DirectoryInfo? GetLatestAppDir(string branchRoot)
    {
        if (!Directory.Exists(branchRoot)) return null;
        return new DirectoryInfo(branchRoot)
            .GetDirectories("app-*")
            .Where(d => Directory.Exists(Path.Combine(d.FullName, "resources")))
            .OrderBy(d =>
            {
                return Version.TryParse(d.Name.Length > 4 ? d.Name[4..] : "", out var v)
                    ? v : new Version(0, 0, 0);
            })
            .LastOrDefault();
    }

    public static string BranchFromLeaf(string path)
    {
        var leaf = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)).ToLowerInvariant();
        if (leaf.Contains("canary")) return "DiscordCanary";
        if (leaf.Contains("ptb")) return "DiscordPTB";
        if (leaf.Contains("development") || leaf.Contains("dev")) return "DiscordDevelopment";
        return "Discord";
    }

    // OpenAsar is only scanned for when checkOpenAsar is set (it costs a file read).
    public static InstallState GetState(Install inst, bool checkOpenAsar = false)
    {
        bool running = Process.GetProcessesByName(inst.Branch).Length > 0;
        var appDir = GetLatestAppDir(inst.Path);
        bool patched = false, openAsar = false, bd = false, bbd = false;
        string asarMod = "none";
        string? appName = null, resources = null, appDirPath = null;
        if (appDir != null)
        {
            appName = appDir.Name;
            appDirPath = appDir.FullName;
            resources = Path.Combine(appDir.FullName, "resources");
            patched = File.Exists(Path.Combine(resources, "_app.asar"));
            asarMod = DetectMod(resources);
            bd = BetterDiscordEngine.IsInjected(appDir.FullName);
            bbd = BandagedBDEngine.IsInjected(resources);
            if (checkOpenAsar) openAsar = OpenAsarEngine.IsInstalled(resources);
        }
        return new InstallState(running, patched, appName, resources, appDirPath, appDir != null, openAsar, asarMod, bd, bbd);
    }

    // Which mod the current stub points at: none / vencord / equicord / other.
    public static string DetectMod(string resourcesDir)
    {
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var bak = Path.Combine(resourcesDir, "_app.asar");
        if (!File.Exists(bak) || !File.Exists(appAsar)) return "none";
        try
        {
            if (new FileInfo(appAsar).Length > 64 * 1024) return "other"; // our stub is tiny
            var bytes = File.ReadAllBytes(appAsar);
            if (ContainsAscii(bytes, "Equicord")) return "equicord";
            if (ContainsAscii(bytes, "Vencord")) return "vencord";
            return "other";
        }
        catch { return "none"; }
    }

    private static bool ContainsAscii(byte[] hay, string needleStr)
    {
        var needle = Encoding.ASCII.GetBytes(needleStr);
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    // Reverts a mod patch (used when switching mods). No-op if not patched.
    public static void Unpatch(string resourcesDir)
    {
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var bak = Path.Combine(resourcesDir, "_app.asar");
        if (!File.Exists(bak)) return;
        if (File.Exists(appAsar)) File.Delete(appAsar);
        File.Move(bak, appAsar);
    }

    public static void StopProcesses(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        if (procs.Length == 0) return;
        Log.Write($"Stopping {procs.Length} '{processName}' process(es) to patch.");
        foreach (var p in procs)
        {
            try { p.Kill(); } catch { }
        }
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(200);
            if (Process.GetProcessesByName(processName).Length == 0) break;
        }
        Thread.Sleep(300);
    }

    // Throws if app.asar is missing or already patched.
    public static void Patch(string resourcesDir, byte[] stubBytes)
    {
        var appAsar = Path.Combine(resourcesDir, "app.asar");
        var bakAsar = Path.Combine(resourcesDir, "_app.asar");
        if (!File.Exists(appAsar)) throw new FileNotFoundException($"app.asar missing in {resourcesDir}");
        if (File.Exists(bakAsar)) throw new IOException($"_app.asar already present in {resourcesDir}");

        File.Move(appAsar, bakAsar);
        try
        {
            File.WriteAllBytes(appAsar, stubBytes);
        }
        catch
        {
            // roll back the rename if writing the stub failed
            if (!File.Exists(appAsar) && File.Exists(bakAsar))
            {
                try { File.Move(bakAsar, appAsar); } catch { }
            }
            throw;
        }
    }

    public static void StartDiscord(string branchRoot, string exeName)
    {
        var update = Path.Combine(branchRoot, "Update.exe");
        if (!File.Exists(update)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = update,
            Arguments = $"--processStart {exeName}",
            UseShellExecute = false,
        });
    }
}
