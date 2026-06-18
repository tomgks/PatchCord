using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchCord;

public sealed class Install
{
    [JsonPropertyName("name")]   public string Name   { get; set; } = "";
    [JsonPropertyName("branch")] public string Branch { get; set; } = "Discord";
    [JsonPropertyName("path")]   public string Path   { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("clientMod")] public string ClientMod { get; set; } = ""; // per-install; filled from the default on load
    // legacy per-install flag, migrated to AppConfig.OpenAsar on load
    [JsonPropertyName("openAsar")] public bool OpenAsar { get; set; }
    [JsonPropertyName("custom")] public bool Custom   { get; set; }
}

public sealed class PatchEvent
{
    [JsonPropertyName("when")]    public DateTime When    { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("install")] public string   Install { get; set; } = "";
    [JsonPropertyName("summary")] public string   Summary { get; set; } = "";
}

public sealed class UiConfig
{
    [JsonPropertyName("theme")]                public string Theme                { get; set; } = "Dark";
    [JsonPropertyName("notificationsEnabled")] public bool   NotificationsEnabled { get; set; } = true;
    [JsonPropertyName("notifyDurationSec")]    public int    NotifyDurationSec    { get; set; } = 7;
    [JsonPropertyName("notifyStyle")]          public string NotifyStyle          { get; set; } = "bar";
    [JsonPropertyName("notifyScale")]          public double NotifyScale          { get; set; } = 1.0;
}

public sealed class AppConfig
{
    [JsonPropertyName("monitoringEnabled")] public bool MonitoringEnabled { get; set; } = true;
    [JsonPropertyName("intervalSeconds")]   public int  IntervalSeconds   { get; set; } = 20;
    [JsonPropertyName("clientMod")]         public string ClientMod       { get; set; } = "vencord"; // vencord | equicord | betterdiscord | none
    [JsonPropertyName("openAsar")]          public bool OpenAsar          { get; set; }
    [JsonPropertyName("installs")]          public List<Install> Installs { get; set; } = new();
    [JsonPropertyName("ui")]                public UiConfig Ui            { get; set; } = new();
    [JsonPropertyName("history")]           public List<PatchEvent> History { get; set; } = new();

    public static readonly List<string> ClientMods = new() { "vencord", "equicord", "betterdiscord", "bandagedbd", "none" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly string[] Branches =
        { "Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment" };

    public static AppConfig Load(string path)
    {
        AppConfig cfg;
        if (File.Exists(path))
        {
            try
            {
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                Log.Write($"Bad config, recreating: {ex.Message}", "WARN");
                cfg = FirstRun();
            }
        }
        else
        {
            cfg = FirstRun();
        }

        cfg.EnsureDefaults();
        cfg.Save(path);
        return cfg;
    }

    private static AppConfig FirstRun()
    {
        // First launch: enable Discord by default, leave others off.
        var cfg = new AppConfig();
        foreach (var d in FindStandardInstalls())
        {
            d.Enabled = d.Branch == "Discord";
            cfg.Installs.Add(d);
        }
        return cfg;
    }

    public static List<Install> FindStandardInstalls()
    {
        var found = new List<Install>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var b in Branches)
        {
            var root = System.IO.Path.Combine(local, b);
            if (File.Exists(System.IO.Path.Combine(root, "Update.exe")))
                found.Add(new Install { Name = b, Branch = b, Path = root, Custom = false });
        }
        return found;
    }

    private void EnsureDefaults()
    {
        Ui ??= new UiConfig();
        Installs ??= new();
        if (IntervalSeconds < 5) IntervalSeconds = 5;
        if (NotifyStyles.IndexOf(Ui.NotifyStyle) < 0) Ui.NotifyStyle = "bar";
        if (Ui.NotifyScale <= 0) Ui.NotifyScale = 1.0;
        if (!Theme.Palettes.ContainsKey(Ui.Theme)) Ui.Theme = "Dark";

        ClientMod = (ClientMod ?? "vencord").ToLowerInvariant();
        if (!ClientMods.Contains(ClientMod)) ClientMod = "vencord";
        foreach (var i in Installs)
        {
            // Each install picks its own mod; older configs inherit the global default.
            i.ClientMod = (i.ClientMod ?? "").ToLowerInvariant();
            if (!ClientMods.Contains(i.ClientMod)) i.ClientMod = ClientMod;
            // Migrate legacy per-install OpenAsar flag to the global one.
            if (i.OpenAsar) { OpenAsar = true; i.OpenAsar = false; }
        }
        History ??= new();
        if (History.Count > 12) History.RemoveRange(12, History.Count - 12);
    }

    public void AddHistory(string install, string summary)
    {
        History.Insert(0, new PatchEvent { When = DateTime.UtcNow, Install = install, Summary = summary });
        if (History.Count > 12) History.RemoveRange(12, History.Count - 12);
    }

    public static readonly List<string> NotifyStyles = new() { "bar", "solid", "minimal", "outline" };

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts)); }
        catch (Exception ex) { Log.Write($"Save config failed: {ex.Message}", "ERROR"); }
    }

    public bool EnsureInstall(string name, string branch, string path, bool enabled, bool custom)
    {
        var existing = Installs.FirstOrDefault(i =>
            string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { existing.Enabled = enabled; return false; }
        Installs.Add(new Install { Name = name, Branch = branch, Path = path, Enabled = enabled, Custom = custom, ClientMod = ClientMod });
        return true;
    }
}
