using System.IO;
using System.Net.Http;
using System.Text;

namespace PatchCord;

// Install/detect OpenAsar. Logic from the Vencord installer's openasar.go.
public static class OpenAsarEngine
{
    private const string DownloadUrl = "https://github.com/GooseMod/OpenAsar/releases/download/nightly/app.asar";
    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("OpenAsar");

    // OpenAsar's asar is ~50 KB; anything bigger isn't it, so don't scan large files.
    private const long MaxScanBytes = 4 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PatchCord");
        return c;
    }

    // The asar that actually runs: _app.asar if a mod is patched on top, else app.asar.
    private static string UnderlyingAsar(string resourcesDir)
    {
        var under = Path.Combine(resourcesDir, "_app.asar");
        return File.Exists(under) ? under : Path.Combine(resourcesDir, "app.asar");
    }

    public static bool IsInstalled(string resourcesDir)
    {
        try
        {
            var asar = UnderlyingAsar(resourcesDir);
            if (!File.Exists(asar)) return false;
            if (new FileInfo(asar).Length > MaxScanBytes) return false;
            return ContainsMarker(File.ReadAllBytes(asar), Marker);
        }
        catch { return false; }
    }

    private static bool ContainsMarker(byte[] hay, byte[] needle)
    {
        for (int i = 0; i <= hay.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    // Backs up the underlying asar to app.asar.backup and writes OpenAsar in its place.
    // Discord must be stopped first.
    public static void Install(string resourcesDir)
    {
        var bytes = GetOpenAsarBytes();
        var asar = UnderlyingAsar(resourcesDir);
        if (!File.Exists(asar)) throw new FileNotFoundException($"No asar to back up in {resourcesDir}");

        var backup = Path.Combine(resourcesDir, "app.asar.backup");
        if (File.Exists(backup)) File.Delete(backup); // stale backup from a prior install
        File.Move(asar, backup);
        try
        {
            File.WriteAllBytes(asar, bytes);
        }
        catch
        {
            // roll back if writing OpenAsar failed
            if (!File.Exists(asar) && File.Exists(backup))
            {
                try { File.Move(backup, asar); } catch { }
            }
            throw;
        }
    }

    // --openasar-test hook: downloads OpenAsar, writes it, reports whether detection works.
    public static string TestFetchAndDetect(string resourcesDir)
    {
        Directory.CreateDirectory(resourcesDir);
        var bytes = GetOpenAsarBytes();
        File.WriteAllBytes(Path.Combine(resourcesDir, "app.asar"), bytes);
        return $"downloaded={bytes.Length} bytes; detected={IsInstalled(resourcesDir)}";
    }

    // Cached locally for 12h; falls back to a stale cache if the download fails.
    private static byte[] GetOpenAsarBytes()
    {
        var cache = Path.Combine(App.BaseDir, "openasar.asar");
        bool cacheFresh = File.Exists(cache) &&
            (DateTime.UtcNow - File.GetLastWriteTimeUtc(cache)) < CacheTtl;
        if (cacheFresh) return File.ReadAllBytes(cache);

        try
        {
            var data = Http.GetByteArrayAsync(DownloadUrl).GetAwaiter().GetResult();
            if (data.Length == 0) throw new IOException("OpenAsar download was empty");
            try { File.WriteAllBytes(cache, data); } catch { /* cache is best-effort */ }
            Log.Write($"Downloaded OpenAsar ({data.Length} bytes).", "OK");
            return data;
        }
        catch (Exception ex)
        {
            if (File.Exists(cache))
            {
                Log.Write($"OpenAsar download failed ({ex.Message}); using cached copy.", "WARN");
                return File.ReadAllBytes(cache);
            }
            throw new IOException($"Could not download OpenAsar and no cached copy exists: {ex.Message}", ex);
        }
    }
}
