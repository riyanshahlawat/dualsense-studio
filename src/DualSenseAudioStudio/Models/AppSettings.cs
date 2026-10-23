namespace DualSenseAudioStudio.Models;

internal sealed record AppSettings
{
    public int DeviceIndex { get; init; } = -1;
    public bool UseHeadset { get; init; } = true;
    public int Volume { get; init; } = 100;
    // Single-packet Legacy35 every 10.67 ms (as DS5Dongle/vDS) avoids the crackle seen with Full39.
    public string Protocol { get; init; } = "Legacy35";
    public int BufferLength { get; init; } = 64;
    public double PeriodMs { get; init; } = 21.333333;
    public int ToneFrequency { get; init; } = 440;
    public int ToneSeconds { get; init; } = 5;
    public double ToneAmplitude { get; init; } = 0.08;
    public string LedHex { get; init; } = "627CFF";
    public int LedBrightness { get; init; }
    public int PlayerLeds { get; init; } = 4;
    public bool LightbarEnabled { get; init; } = true;
    public bool AppControlsLighting { get; init; }
}

internal sealed record AudioDeviceItem(int Index, string Name, bool IsDefault)
{
    public string DisplayName => IsDefault ? $"{Name}  ·  Default" : Name;
}

internal enum EngineMode
{
    Loopback,
    Diagnostic,
    Tone,
    ListDevices,
    MicProbe
}

internal sealed record EngineLaunchOptions
{
    public EngineMode Mode { get; init; } = EngineMode.Loopback;
    public int? DeviceIndex { get; init; }
    public bool UseHeadset { get; init; } = true;
    public int Volume { get; init; } = 100;
    public bool Full39 { get; init; } = true;
    public int BufferLength { get; init; } = 64;
    public double PeriodMs { get; init; } = 21.333333;
    public int ToneFrequency { get; init; } = 440;
    public int ToneSeconds { get; init; } = 5;
    public double ToneAmplitude { get; init; } = 0.08;
    public string LedHex { get; init; } = "627CFF";
    public int LedBrightness { get; init; }
    public int PlayerLeds { get; init; } = 4;
    public bool LightbarEnabled { get; init; } = true;
    public bool AppControlsLighting { get; init; }
    public bool EnableMic { get; init; }
    public bool NoiseRemoval { get; init; }
    public bool Sidetone { get; init; }
    public bool Haptics { get; init; }
    public bool LowLatency { get; init; }
    // Exact HID path of Player 1's controller: audio and mic go to this one controller.
    public string? ControllerPath { get; init; }
    public required string ControlFile { get; init; }
}

internal sealed record ControllerControlDocument(
    int Version,
    int Volume,
    int Red,
    int Green,
    int Blue,
    int LedBrightness,
    int PlayerLeds,
    bool LightbarEnabled,
    bool StopRequested);
