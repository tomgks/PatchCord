using System.IO;
using System.Threading;
using System.Windows;

namespace PatchCord;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;

    // Folder the exe lives in; config and log sit beside it.
    public static string BaseDir { get; private set; } = AppContext.BaseDirectory;
    public static string ConfigFile { get; private set; } = "config.json";
    public static string VencordPatcherPath { get; private set; } = "";
    public static string EquicordPatcherPath { get; private set; } = "";
    public static string BetterDiscordAsarPath { get; private set; } = "";

    // Read a patcher.js and decide which mod it actually is from its content, not the folder
    // name. Equicord is a Vencord fork so its bundle mentions both, so "Equicord" wins. This is
    // how we catch e.g. Equicord installed into the Vencord folder (it'd otherwise read as Vencord).
    public static string? ModOfPatcher(string patcherPath)
    {
        if (!System.IO.File.Exists(patcherPath)) return null;
        try
        {
            var text = System.IO.File.ReadAllText(patcherPath);
            if (text.Contains("Equicord", StringComparison.Ordinal)) return "equicord";
            if (text.Contains("Vencord", StringComparison.Ordinal)) return "vencord";
            return "other";
        }
        catch { return null; }
    }

    private static (DateTime at, Dictionary<string, string>? map) _locCache;

    // Where vencord/equicord actually live (mod -> real patcher.js), decided by content.
    public static Dictionary<string, string> LocateAsarMods()
    {
        if (_locCache.map != null && (DateTime.UtcNow - _locCache.at).TotalSeconds < 3) return _locCache.map;
        var map = new Dictionary<string, string>();
        foreach (var pj in new[] { VencordPatcherPath, EquicordPatcherPath })
        {
            var mod = ModOfPatcher(pj);
            if ((mod == "vencord" || mod == "equicord") && !map.ContainsKey(mod)) map[mod] = pj;
        }
        _locCache = (DateTime.UtcNow, map);
        return map;
    }

    public static string PatcherPathFor(string mod) =>
        LocateAsarMods().TryGetValue(mod, out var p) ? p
            : (mod == "equicord" ? EquicordPatcherPath : VencordPatcherPath);

    public static bool ModInstalled(string mod) => mod switch
    {
        "vencord" => LocateAsarMods().ContainsKey("vencord"),
        "equicord" => LocateAsarMods().ContainsKey("equicord"),
        "betterdiscord" => System.IO.File.Exists(BetterDiscordAsarPath),
        "bandagedbd" => BandagedBDEngine.HasAnySnapshot(), // we can re-apply once we've snapshotted it
        _ => true, // "none"
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --dumpstub <outFile> <patcherPath>: writes the stub asar and exits (for byte-diffing)
        if (e.Args.Length == 3 && e.Args[0] == "--dumpstub")
        {
            File.WriteAllBytes(e.Args[1], PatchEngine.BuildStubAsar(e.Args[2]));
            Shutdown();
            return;
        }

        // --openasar-test <dir>: fetches OpenAsar and checks detection
        if (e.Args.Length == 2 && e.Args[0] == "--openasar-test")
        {
            BaseDir = AppContext.BaseDirectory;
            Log.FilePath = Path.Combine(BaseDir, "patchcord.log");
            string outcome;
            try { outcome = OpenAsarEngine.TestFetchAndDetect(e.Args[1]); }
            catch (Exception ex) { outcome = "ERROR: " + ex.Message; }
            File.WriteAllText(Path.Combine(e.Args[1], "result.txt"), outcome);
            Shutdown();
            return;
        }

        // --bd-test <appDir>: dry-runs BD detection/injection without touching anything
        if (e.Args.Length == 2 && e.Args[0] == "--bd-test")
        {
            var ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var asar = Path.Combine(ad, "BetterDiscord", "data", "betterdiscord.asar");
            string outcome;
            try { outcome = BetterDiscordEngine.DryRun(e.Args[1], asar); }
            catch (Exception ex) { outcome = "ERROR: " + ex.Message; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "bd_test.txt"), outcome);
            Shutdown();
            return;
        }

        // --bbd-test: exercises the BandagedBD snapshot/restore cycle on a throwaway layout.
        if (e.Args.Length == 1 && e.Args[0] == "--bbd-test")
        {
            var sb = new System.Text.StringBuilder();
            var inst = Path.Combine(Path.GetTempPath(), "pc_bbd_selftest");
            var res = Path.Combine(inst, "app-1.0.0", "resources");
            var appFolder = Path.Combine(res, "app");
            try
            {
                if (Directory.Exists(inst)) Directory.Delete(inst, true);
                Directory.CreateDirectory(Path.Combine(appFolder, "betterdiscord"));
                File.WriteAllText(Path.Combine(appFolder, "index.js"), "require(\"betterdiscord-loader.js\")");
                File.WriteAllText(Path.Combine(appFolder, "package.json"), "{\"name\":\"betterdiscord\"}");
                File.WriteAllText(Path.Combine(appFolder, "betterdiscord", "preload.js"), "bbd");
                sb.AppendLine($"IsInjected={BandagedBDEngine.IsInjected(res)} (want True)");
                BandagedBDEngine.CaptureSnapshot(res, inst);
                sb.AppendLine($"HasSnapshot={BandagedBDEngine.HasSnapshot(inst)} (want True)");
                Directory.Delete(appFolder, true);
                sb.AppendLine($"IsInjected after wipe={BandagedBDEngine.IsInjected(res)} (want False)");
                BandagedBDEngine.Restore(res, inst);
                sb.AppendLine($"IsInjected after restore={BandagedBDEngine.IsInjected(res)} (want True)");
                sb.AppendLine($"restored files={Directory.GetFiles(appFolder, "*", SearchOption.AllDirectories).Length} (want 3)");
                BandagedBDEngine.Remove(res);
                sb.AppendLine($"app exists after remove={Directory.Exists(appFolder)} (want False)");
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex.Message); }
            finally
            {
                try { if (Directory.Exists(inst)) Directory.Delete(inst, true); } catch { }
                try { var r = Path.Combine(AppContext.BaseDirectory, "bbd"); if (Directory.Exists(r)) Directory.Delete(r, true); } catch { }
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "bbd_test.txt"), sb.ToString());
            Shutdown();
            return;
        }

        // --equi-test: verifies content-based detection (Equicord installed in the Vencord folder).
        if (e.Args.Length == 1 && e.Args[0] == "--equi-test")
        {
            var sb = new System.Text.StringBuilder();
            var root = Path.Combine(Path.GetTempPath(), "pc_equi_test");
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
                var vencDir = Path.Combine(root, "Vencord", "dist");          // path says "Vencord"...
                Directory.CreateDirectory(vencDir);
                var equiPatcher = Path.Combine(vencDir, "patcher.js");
                File.WriteAllText(equiPatcher, "// build with Equicord/abc123 user agent over Vencord internals"); // ...but it's Equicord
                var realVenc = Path.Combine(root, "RealVencord", "dist");
                Directory.CreateDirectory(realVenc);
                File.WriteAllText(Path.Combine(realVenc, "patcher.js"), "// plain Vencord build");

                sb.AppendLine($"ModOfPatcher(equicord-in-vencord-folder) = {ModOfPatcher(equiPatcher)} (want equicord)");
                sb.AppendLine($"ModOfPatcher(real vencord)              = {ModOfPatcher(Path.Combine(realVenc, "patcher.js"))} (want vencord)");

                VencordPatcherPath = equiPatcher;
                EquicordPatcherPath = Path.Combine(root, "Equicord", "dist", "patcher.js"); // empty
                sb.AppendLine($"ModInstalled(equicord) = {ModInstalled("equicord")} (want True)");
                sb.AppendLine($"ModInstalled(vencord)  = {ModInstalled("vencord")} (want False)");
                sb.AppendLine($"PatcherPathFor(equicord) points at the vencord folder = {PatcherPathFor("equicord") == equiPatcher} (want True)");

                var res = Path.Combine(root, "fakeResources");
                Directory.CreateDirectory(res);
                File.WriteAllBytes(Path.Combine(res, "app.asar"), PatchEngine.BuildStubAsar(equiPatcher));
                File.WriteAllText(Path.Combine(res, "_app.asar"), "orig");
                sb.AppendLine($"DetectMod(stub) = {PatchEngine.DetectMod(res)} (want equicord)");
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex.Message); }
            finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "equi_test.txt"), sb.ToString());
            Shutdown();
            return;
        }

        // Set up paths relative to the exe.
        BaseDir = AppContext.BaseDirectory;
        Log.FilePath = Path.Combine(BaseDir, "patchcord.log");
        ConfigFile = Path.Combine(BaseDir, "config.json");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var vencordBase = Environment.GetEnvironmentVariable("VENCORD_USER_DATA_DIR")
            ?? Path.Combine(appData, "Vencord");
        var equicordBase = Environment.GetEnvironmentVariable("EQUICORD_USER_DATA_DIR")
            ?? Path.Combine(appData, "Equicord");
        VencordPatcherPath = Path.Combine(vencordBase, "dist", "patcher.js");
        EquicordPatcherPath = Path.Combine(equicordBase, "dist", "patcher.js");
        // BD puts its asar in %APPDATA%\BetterDiscord\data
        BetterDiscordAsarPath = Path.Combine(appData, "BetterDiscord", "data", "betterdiscord.asar");

        bool tray = e.Args.Any(a => a.TrimStart('-', '/').Equals("tray", StringComparison.OrdinalIgnoreCase));
        bool selfTest = e.Args.Any(a => a.TrimStart('-', '/').Equals("selftest", StringComparison.OrdinalIgnoreCase));

        // Single instance mutex (skipped for --selftest).
        if (!selfTest)
        {
            _mutex = new Mutex(initiallyOwned: false, "Global\\PatchCordApp");
            if (!_mutex.WaitOne(0))
            {
                Log.Write("App already running. Exiting.", "WARN");
                Shutdown();
                return;
            }
        }

        // Don't hard-crash on unhandled UI exceptions.
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write($"UI exception: {ex.Exception.Message}\n{ex.Exception.StackTrace}", "ERROR");
            ex.Handled = true;
        };

        try
        {
            var win = new MainWindow();
            win.Initialize(startHidden: tray, selfTest: selfTest);
            if (selfTest)
            {
                Log.Write("Self-test OK.", "INFO");
                Shutdown();
                return;
            }
            if (!tray) win.Show();
        }
        catch (Exception ex)
        {
            Log.Write($"FATAL: {ex.Message}\n{ex.StackTrace}", "ERROR");
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
