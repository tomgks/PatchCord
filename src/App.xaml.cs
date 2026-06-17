using System.IO;
using System.Threading;
using System.Windows;

namespace PatchCord;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;

    /// <summary>Folder the exe lives in — config and log sit beside it.</summary>
    public static string BaseDir { get; private set; } = AppContext.BaseDirectory;
    public static string ConfigFile { get; private set; } = "config.json";
    public static string VencordPatcherPath { get; private set; } = "";
    public static string EquicordPatcherPath { get; private set; } = "";
    public static string BetterDiscordAsarPath { get; private set; } = "";

    /// <summary>patcher.js path for a given client mod ("vencord"/"equicord").</summary>
    public static string PatcherPathFor(string mod) =>
        mod == "equicord" ? EquicordPatcherPath : VencordPatcherPath;

    /// <summary>Whether the chosen mod's files are present on disk.</summary>
    public static bool ModInstalled(string mod) => mod switch
    {
        "vencord" => System.IO.File.Exists(VencordPatcherPath),
        "equicord" => System.IO.File.Exists(EquicordPatcherPath),
        "betterdiscord" => System.IO.File.Exists(BetterDiscordAsarPath),
        _ => true, // "none"
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Hidden dev mode: `--dumpstub <outFile> <patcherPath>` writes the stub
        // app.asar bytes and exits, so the build can be diffed against the
        // PowerShell/installer output. No UI, no mutex, no side effects.
        if (e.Args.Length == 3 && e.Args[0] == "--dumpstub")
        {
            File.WriteAllBytes(e.Args[1], PatchEngine.BuildStubAsar(e.Args[2]));
            Shutdown();
            return;
        }

        // Hidden dev mode: `--openasar-test <dir>` fetches OpenAsar and verifies detection.
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

        // Hidden dev mode: `--bd-test <appDir>` dry-runs BetterDiscord detection/injection.
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

        // Resolve our folder (the dir holding the exe), then config/log/patcher paths.
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
        // BetterDiscord ships its asar to %APPDATA%\BetterDiscord\data (BD config dataPath).
        BetterDiscordAsarPath = Path.Combine(appData, "BetterDiscord", "data", "betterdiscord.asar");

        bool tray = e.Args.Any(a => a.TrimStart('-', '/').Equals("tray", StringComparison.OrdinalIgnoreCase));
        bool selfTest = e.Args.Any(a => a.TrimStart('-', '/').Equals("selftest", StringComparison.OrdinalIgnoreCase));

        // Single instance (skipped for the no-side-effect self-test validation).
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

        // Keep running through handler errors instead of hard-crashing.
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
