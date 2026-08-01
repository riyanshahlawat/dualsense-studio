using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using DualSenseAudioStudio.Models;

namespace DualSenseAudioStudio.Services;

internal sealed class EngineProcessService : IDisposable
{
    private readonly object _sync = new();
    private Process? _process;

    public event Action<string>? OutputReceived;
    public event Action<int>? ProcessExited;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false }
                    ? _process.Id
                    : null;
            }
        }
    }

    public Task StartAsync(EngineLaunchOptions options)
    {
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException(
                    "An engine session is already running.");
            }

            string enginePath = ResolveEnginePath();
            var startInfo = new ProcessStartInfo
            {
                FileName = enginePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };

            AddArguments(startInfo, options);

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnOutputDataReceived;
            process.Exited += OnProcessExited;

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "Windows could not start the audio engine.");
            }

            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(TimeSpan gracefulTimeout)
    {
        Process? process;

        lock (_sync)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(gracefulTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            OutputReceived?.Invoke(eventArgs.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        if (sender is not Process process)
        {
            return;
        }

        int exitCode = process.ExitCode;
        ProcessExited?.Invoke(exitCode);
    }

    private static void AddArguments(
        ProcessStartInfo startInfo,
        EngineLaunchOptions options)
    {
        switch (options.Mode)
        {
            case EngineMode.Diagnostic:
                startInfo.ArgumentList.Add("--diagnostic");
                break;

            case EngineMode.Tone:
                startInfo.ArgumentList.Add("--tone");
                startInfo.ArgumentList.Add("--frequency");
                startInfo.ArgumentList.Add(
                    options.ToneFrequency.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--seconds");
                startInfo.ArgumentList.Add(
                    options.ToneSeconds.ToString(CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add("--amplitude");
                startInfo.ArgumentList.Add(
                    options.ToneAmplitude.ToString(CultureInfo.InvariantCulture));
                break;

            case EngineMode.ListDevices:
                startInfo.ArgumentList.Add("--list-devices");
                break;
        }

        startInfo.ArgumentList.Add(options.Full39 ? "--full39" : "--legacy35");
        startInfo.ArgumentList.Add(options.UseHeadset ? "--headset" : "--speaker");

        AddValue(startInfo, "--volume", options.Volume);
        AddValue(startInfo, "--buffer", options.BufferLength);
        AddValue(startInfo, "--period", options.PeriodMs);
        AddValue(startInfo, "--led", options.LedHex.TrimStart('#').ToUpperInvariant());
        AddValue(startInfo, "--led-brightness", options.LedBrightness);
        AddValue(startInfo, "--player-leds", options.PlayerLeds);
        AddValue(startInfo, "--control-file", options.ControlFile);

        if (options.AppControlsLighting)
        {
            startInfo.ArgumentList.Add("--control-lighting");
        }

        if (!options.LightbarEnabled)
        {
            startInfo.ArgumentList.Add("--lightbar-off");
        }

        if (
            options.Mode == EngineMode.Loopback &&
            options.DeviceIndex is not null)
        {
            AddValue(startInfo, "--device", options.DeviceIndex.Value);
        }
    }

    private static void AddValue(
        ProcessStartInfo startInfo,
        string name,
        object value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(
            Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? string.Empty);
    }

    private static string ResolveEnginePath()
    {
        string published = Path.Combine(
            AppContext.BaseDirectory,
            "DualSenseBtAudio.exe");

        if (File.Exists(published))
        {
            return published;
        }

        string runtimeIdentifier = RuntimeInformation.ProcessArchitecture == Architecture.X86
            ? "win-x86"
            : "win-x64";

        string debug = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "DualSenseBtAudio",
            "bin",
            "Debug",
            "net9.0-windows",
            runtimeIdentifier,
            "DualSenseBtAudio.exe"));

        if (File.Exists(debug))
        {
            return debug;
        }

        throw new FileNotFoundException(
            "DualSenseBtAudio.exe was not found. " +
            "Run BUILD_APP.bat from the project root.",
            published);
    }

    public void Dispose()
    {
        Process? process;

        lock (_sync)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        process.OutputDataReceived -= OnOutputDataReceived;
        process.ErrorDataReceived -= OnOutputDataReceived;
        process.Exited -= OnProcessExited;

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        process.Dispose();
    }
}
