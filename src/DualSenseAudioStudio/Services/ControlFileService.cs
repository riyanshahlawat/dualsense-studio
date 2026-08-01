using System.Globalization;
using System.IO;
using System.Text.Json;
using DualSenseAudioStudio.Models;

namespace DualSenseAudioStudio.Services;

internal sealed class ControlFileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private int _version;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ControlFileService()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DualSenseAudioStudio");

        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "session-control.json");
    }

    public string FilePath { get; }

    public async Task WriteAsync(AppSettings settings, bool stopRequested = false)
    {
        await _writeLock.WaitAsync();

        try
        {
            await Task.Run(() =>
            {
                (int red, int green, int blue) = ParseHex(settings.LedHex);

                var document = new ControllerControlDocument(
                    Interlocked.Increment(ref _version),
                    Math.Clamp(settings.Volume, 0, 127),
                    red,
                    green,
                    blue,
                    Math.Clamp(settings.LedBrightness, 0, 2),
                    Math.Clamp(settings.PlayerLeds, 0, 31),
                    settings.LightbarEnabled,
                    stopRequested);

                string temporaryPath = FilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporaryPath, FilePath, overwrite: true);
            });
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static (int Red, int Green, int Blue) ParseHex(string value)
    {
        string hex = value.Trim().TrimStart('#');

        if (
            hex.Length != 6 ||
            !int.TryParse(
                hex,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out int rgb))
        {
            throw new FormatException("Enter a six-character HEX color.");
        }

        return (
            (rgb >> 16) & 0xFF,
            (rgb >> 8) & 0xFF,
            rgb & 0xFF);
    }
}
