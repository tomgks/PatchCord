using System.IO;

namespace PatchCord;

// File logger that rotates at ~1 MB.
public static class Log
{
    public static string FilePath { get; set; } = "patchcord.log";

    public static void Write(string message, string level = "INFO")
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1024 * 1024)
                File.Move(FilePath, FilePath + ".old", overwrite: true);
            File.AppendAllText(FilePath, line + Environment.NewLine);
        }
        catch { /* logging must never throw */ }
    }
}
