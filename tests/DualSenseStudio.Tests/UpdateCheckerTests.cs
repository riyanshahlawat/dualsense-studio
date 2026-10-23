using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DualSenseAudioStudio.Services;
using Xunit;

namespace DualSenseStudio.Tests;

// Safety rules of the in-app updater: incomplete or untrusted releases are never
// offered, and only a file matching the published SHA-256 may be run.
public class UpdateCheckerTests
{
    private const string Base = "https://github.com/riyanshahlawat/dualsense-studio/releases/download/v9.9.9/";
    private static readonly Version Current = new(1, 0, 13);

    private static JsonElement Release(string tag, params (string Name, string Url)[] assets)
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft = false,
            html_url = "https://github.com/riyanshahlawat/dualsense-studio/releases/tag/" + tag,
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url })
        });
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void Complete_newer_release_is_offered()
    {
        var info = UpdateChecker.ParseRelease(Release("v9.9.9",
            ("DualSenseStudio-Setup.exe", Base + "DualSenseStudio-Setup.exe"),
            ("DualSenseStudio-Setup.exe.sha256", Base + "DualSenseStudio-Setup.exe.sha256")), Current);

        Assert.NotNull(info);
        Assert.Equal(new Version(9, 9, 9), info!.Version);
        Assert.EndsWith("DualSenseStudio-Setup.exe", info.DownloadUrl);
    }

    [Fact]
    public void Release_without_installer_is_not_offered()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release("v9.9.9",
            ("DualSenseStudio-Setup.exe.sha256", Base + "DualSenseStudio-Setup.exe.sha256")), Current));
    }

    [Fact]
    public void Release_without_checksum_is_not_offered()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release("v9.9.9",
            ("DualSenseStudio-Setup.exe", Base + "DualSenseStudio-Setup.exe")), Current));
    }

    [Fact]
    public void Release_with_no_assets_never_falls_back_to_web_page()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release("v9.9.9"), Current));
    }

    [Theory]
    [InlineData("v1.0.13")]
    [InlineData("v1.0.0")]
    [InlineData("not-a-version")]
    public void Same_older_or_invalid_version_is_not_offered(string tag)
    {
        Assert.Null(UpdateChecker.ParseRelease(Release(tag,
            ("DualSenseStudio-Setup.exe", Base + "DualSenseStudio-Setup.exe"),
            ("DualSenseStudio-Setup.exe.sha256", Base + "DualSenseStudio-Setup.exe.sha256")), Current));
    }

    [Theory]
    [InlineData("http://github.com/riyanshahlawat/dualsense-studio/releases/download/v1/x.exe")]
    [InlineData("https://evil.example/riyanshahlawat/dualsense-studio/releases/download/v1/x.exe")]
    [InlineData("https://github.com/someone-else/dualsense-studio/releases/download/v1/x.exe")]
    public void Untrusted_urls_are_rejected(string url)
    {
        Assert.False(UpdateChecker.IsTrustedUrl(url));
    }

    [Fact]
    public void Untrusted_asset_url_makes_release_incomplete()
    {
        Assert.Null(UpdateChecker.ParseRelease(Release("v9.9.9",
            ("DualSenseStudio-Setup.exe", "https://evil.example/DualSenseStudio-Setup.exe"),
            ("DualSenseStudio-Setup.exe.sha256", Base + "DualSenseStudio-Setup.exe.sha256")), Current));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123")]
    [InlineData("<html>Not Found</html>")]
    public void Malformed_checksum_is_rejected(string text)
    {
        Assert.Throws<InvalidOperationException>(() => UpdateChecker.ParseChecksum(text));
    }

    [Fact]
    public void Checksum_in_sha256sum_format_is_parsed()
    {
        string hash = new string('a', 64);
        Assert.Equal(hash, UpdateChecker.ParseChecksum(hash.ToUpperInvariant() + "  DualSenseStudio-Setup.exe"));
    }

    [Fact]
    public void Matching_executable_passes_verification()
    {
        string path = TempFile(new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 });
        UpdateChecker.VerifyInstaller(path, Sha(path));
    }

    [Fact]
    public void Wrong_checksum_fails_verification()
    {
        string path = TempFile(new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 });
        Assert.Throws<InvalidOperationException>(() => UpdateChecker.VerifyInstaller(path, new string('0', 64)));
    }

    [Fact]
    public void Web_page_saved_as_exe_fails_verification()
    {
        string path = TempFile(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><html>release page</html>"));
        Assert.Throws<InvalidOperationException>(() => UpdateChecker.VerifyInstaller(path, Sha(path)));
    }

    private static string TempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
