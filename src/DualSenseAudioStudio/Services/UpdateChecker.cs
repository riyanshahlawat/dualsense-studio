using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DualSenseAudioStudio.Services;

// Checks GitHub Releases for a newer DualSense Studio and installs it on request.
//
// Safety rules (fail closed):
// - a release is only offered if it has BOTH the installer and its SHA-256 checksum asset;
// - downloads are only accepted from this repository's GitHub release download URL over HTTPS;
// - the downloaded file must match the published SHA-256 and be a Windows executable
//   ("MZ" header) before it is ever started. Anything else is deleted and not run.
internal static class UpdateChecker
{
    private const string Repo = "riyanshahlawat/dualsense-studio";
    private const string SetupAssetName = "DualSenseStudio-Setup.exe";
    private const string ChecksumAssetName = SetupAssetName + ".sha256";
    private const string TrustedDownloadPrefix = "https://github.com/" + Repo + "/releases/download/";

    // Metadata calls are short; the installer download gets its own generous timeout.
    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(20));
    private static readonly HttpClient DownloadHttp = CreateClient(TimeSpan.FromMinutes(15));

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public sealed record UpdateInfo(Version Version, string DownloadUrl, string ChecksumUrl, string PageUrl);

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DualSenseStudio-Updater");
        return client;
    }

    /// <summary>
    /// Returns the newer release, or null when up to date, offline, or the release is
    /// incomplete (missing installer or checksum) — an incomplete release is never offered.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return ParseRelease(doc.RootElement, CurrentVersion);
        }
        catch
        {
            return null; // offline or rate-limited: try again later
        }
    }

    /// <summary>Pure parsing step (kept separate so it can be unit-tested).</summary>
    internal static UpdateInfo? ParseRelease(JsonElement release, Version current)
    {
        if (release.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean())
        {
            return null;
        }

        string tag = release.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest) || latest <= Trim(current))
        {
            return null;
        }

        string? installer = null, checksum = null;
        if (release.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (url is null || !IsTrustedUrl(url))
                {
                    continue;
                }
                if (name == SetupAssetName) installer = url;
                else if (name == ChecksumAssetName) checksum = url;
            }
        }

        if (installer is null || checksum is null)
        {
            return null; // incomplete release: never fall back to a web page
        }

        string page = release.TryGetProperty("html_url", out JsonElement p) ? p.GetString() ?? "" : "";
        return new UpdateInfo(latest, installer, checksum, page);
    }

    internal static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && url.StartsWith(TrustedDownloadPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Downloads and verifies the installer, reporting progress 0..1. Returns the verified
    /// installer path, or throws (and deletes the file) if any check fails.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null)
    {
        if (!IsTrustedUrl(update.DownloadUrl) || !IsTrustedUrl(update.ChecksumUrl))
        {
            throw new InvalidOperationException("Update URL is not from the trusted release host.");
        }

        string expected = ParseChecksum(await DownloadHttp.GetStringAsync(update.ChecksumUrl));
        string path = Path.Combine(Path.GetTempPath(), $"DualSenseStudio-Setup-{update.Version}.exe");

        try
        {
            using (HttpResponseMessage response = await DownloadHttp.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
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
            }

            VerifyInstaller(path, expected);
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    /// <summary>Accepts "&lt;hex&gt;" or "&lt;hex&gt;  filename" (sha256sum format).</summary>
    internal static string ParseChecksum(string text)
    {
        string hash = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Published checksum is missing or malformed.");
        }
        return hash.ToLowerInvariant();
    }

    internal static void VerifyInstaller(string path, string expectedSha256)
    {
        using (FileStream file = File.OpenRead(path))
        {
            if (file.Length < 2 || file.ReadByte() != 'M' || file.ReadByte() != 'Z')
            {
                throw new InvalidOperationException("Downloaded file is not a Windows installer.");
            }
        }

        string actual;
        using (FileStream file = File.OpenRead(path))
        {
            actual = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        }

        if (actual != expectedSha256)
        {
            throw new InvalidOperationException("Downloaded installer failed the checksum check.");
        }
    }

    /// <summary>
    /// Installs silently (no windows, no admin prompt) and the installer reopens the
    /// app when it's done. Only call with a path returned by DownloadAsync (verified).
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
