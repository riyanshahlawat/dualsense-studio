using System.Diagnostics;
using System.IO;

namespace DualSenseAudioStudio.Services;

// Windows sometimes gives a reconnected DualSense a new device ID. HidHide hides by
// ID, so the new one is visible to games, which then see two controllers (the real
// one and our virtual one) and input fights itself (e.g. Fortnite aim jerking left
// and right). This checks each connected controller against HidHide's list.
internal static class HidHideGuard
{
    private const string Cli = @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe";

    public static bool Installed => File.Exists(Cli);

    /// <summary>"\\?\hid#{...}_vid&amp;..#9&amp;x&amp;7&amp;0000#{guid}" -> "HID\{...}_VID&amp;..\9&amp;X&amp;7&amp;0000".</summary>
    internal static string? InstanceIdFromPath(string path)
    {
        string p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        int lastGuid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (lastGuid <= 0)
        {
            return null;
        }
        return p[..lastGuid].Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>Controller IDs (from HID paths) that HidHide is not hiding yet.</summary>
    public static List<string> FindUnhidden(IEnumerable<string> hidPaths)
    {
        var missing = new List<string>();
        if (!Installed)
        {
            return missing;
        }

        string list = Run("--dev-list");
        foreach (string path in hidPaths)
        {
            string? id = InstanceIdFromPath(path);
            if (id is not null && !list.Contains(id, StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(id);
            }
        }
        return missing;
    }

    /// <summary>Hides the IDs (one admin prompt). Returns false if declined or failed.</summary>
    public static bool Hide(IEnumerable<string> instanceIds)
    {
        string args = string.Join(" ", instanceIds.Select(id => $"--dev-hide \"{id}\"")) + " --cloak-on";
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo(Cli, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            p?.WaitForExit(15000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false; // admin prompt declined
        }
    }

    private static string Run(string args)
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo(Cli, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            })!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
