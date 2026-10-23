using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DualSenseAudioStudio.Services;

// Checks GitHub Releases for a newer DualSense Studio and installs it on request.
internal static class UpdateChecker
{
    private const string Repo = "riyanshahlawat/dualsense-studio";
    private const string SetupAssetName = "DualSenseStudio-Setup.exe";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public sealed record UpdateInfo(Version Version, string DownloadUrl, string PageUrl);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DualSenseStudio-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Returns the newer release, or null when up to date or offline.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            JsonElement root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest) || latest <= Trim(CurrentVersion))
            {
                return null;
            }

            string page = root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";
            foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == SetupAssetName)
                {
                    return new UpdateInfo(latest, asset.GetProperty("browser_download_url").GetString()!, page);
                }
            }

            return new UpdateInfo(latest, page, page);
        }
        catch
        {
            return null; // offline or rate-limited: try again next launch
        }
    }

    /// <summary>Downloads the update in the background, reporting progress 0..1. Returns the installer path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"DualSenseStudio-Setup-{update.Version}.exe");
        using HttpResponseMessage response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        await using Stream download = await response.Content.ReadAsStreamAsync();
        await using FileStream file = File.Create(path);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await download.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (total > 0)
            {
                progress?.Report((double)done / total);
            }
        }

        return path;
    }

    /// <summary>
    /// Installs silently (no windows, no admin prompt) and the installer reopens the
    /// app when it's done. The caller exits right after.
    /// </summary>
    public static void ApplyAndRestart(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true
        });
    }

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
