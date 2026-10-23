using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text.Json;
using HidApi;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;

internal static class Program
{
    // Exits this engine when the app process (parent) is gone.
    private static void WatchParent(int parentPid)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch (ArgumentException)
        {
            Environment.Exit(0);
            return;
        }

        new Thread(() =>
        {
            parent.WaitForExit();
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "ParentWatch" }.Start();
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        TextWriter originalOutput = Console.Out;
        StreamWriter? logFile = null;
        TeeTextWriter? tee = null;

        try
        {
            // One-time setup (run elevated): name the virtual cables like a real headset
            // and hide the unused ones, so Windows shows a single "DualSense" device.
            if (args.Contains("--setup-devices", StringComparer.OrdinalIgnoreCase))
            {
                using var devices = new MMDeviceEnumerator();
                // Make the cable the default first, so hiding CABLE In 16ch can't leave Windows without one.
                foreach (MMDevice d in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    if (d.FriendlyName.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
                        AudioPolicy.SetDefault(d.ID);
                }
                foreach (MMDevice d in devices.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
                {
                    string name = d.FriendlyName;
                    try
                    {
                        if (d.DataFlow == DataFlow.Render && name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase))
                            AudioPolicy.Rename(d.ID, "DualSense Wireless Controller", "Headphones");
                        else if (d.DataFlow == DataFlow.Capture && (name.Contains("Hi-Fi Cable Output", StringComparison.OrdinalIgnoreCase)
                                                                   || (name.StartsWith("DualSense Wireless Controller", StringComparison.OrdinalIgnoreCase) && name.Contains("Hi-Fi", StringComparison.OrdinalIgnoreCase))))
                        {
                            if (!name.StartsWith("DualSense Wireless Controller", StringComparison.OrdinalIgnoreCase))
                                AudioPolicy.Rename(d.ID, "DualSense Wireless Controller", "Headset Microphone");
                            // Must match the mic link's 48 kHz, or the voice sounds muffled.
                            AudioPolicy.SetFormat(d.ID, 48000, 2, 24);
                        }
                        else if (d.DataFlow == DataFlow.Render && (name.Contains("Hi-Fi Cable Input", StringComparison.OrdinalIgnoreCase)
                                                                  || name.StartsWith("DualSense mic link", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!name.StartsWith("DualSense mic link", StringComparison.OrdinalIgnoreCase))
                                AudioPolicy.Rename(d.ID, "DualSense mic link", "Used by DualSense Studio - don't select");
                            AudioPolicy.SetFormat(d.ID, 48000, 2, 24);
                        }
                        else if (name.StartsWith("CABLE In 16ch", StringComparison.OrdinalIgnoreCase)
                                 || (d.DataFlow == DataFlow.Capture && name.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase)))
                            AudioPolicy.SetVisible(d.ID, false);
                        else
                            continue;
                        Console.WriteLine($"Updated: {name}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed: {name}: {ex.Message}");
                    }
                }
                return 0;
            }

            if (args.Contains("--mic-probe", StringComparer.OrdinalIgnoreCase))
            {
                MicProbe.Run();
                return 0;
            }

            MicBridge.Enabled = args.Contains("--mic", StringComparer.OrdinalIgnoreCase);
            MicBridge.NoiseRemoval = args.Contains("--noise-removal", StringComparer.OrdinalIgnoreCase);
            MicBridge.Sidetone = args.Contains("--sidetone", StringComparer.OrdinalIgnoreCase);
            Haptics.Enabled = args.Contains("--haptics", StringComparer.OrdinalIgnoreCase);
            DualSenseAudioEngine.LowLatency = args.Contains("--low-latency", StringComparer.OrdinalIgnoreCase);
            string[] appFlags = { "--mic", "--noise-removal", "--sidetone", "--haptics", "--low-latency" };
            args = args.Where(a => !appFlags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

            // --parent-pid N: exit as soon as DualSense Studio closes or crashes, so the
            // mic and headphone audio never keep running without the app.
            int parentIndex = Array.FindIndex(args, a => a.Equals("--parent-pid", StringComparison.OrdinalIgnoreCase));
            if (parentIndex >= 0 && parentIndex + 1 < args.Length && int.TryParse(args[parentIndex + 1], out int parentPid))
            {
                WatchParent(parentPid);
                args = args.Where((_, i) => i != parentIndex && i != parentIndex + 1).ToArray();
            }

            Options options = Options.Parse(args);

            if (options.Help)
            {
                Options.PrintHelp();
                return 0;
            }

            if (options.ListDevices)
            {
                AudioDevices.PrintOutputDevices();
                return 0;
            }

            Directory.CreateDirectory("logs");
            string logPath = Path.Combine(
                "logs",
                $"DualSenseAudio_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            logFile = new StreamWriter(logPath, append: false)
            {
                AutoFlush = true
            };

            tee = new TeeTextWriter(originalOutput, logFile);
            Console.SetOut(tee);

            Console.WriteLine(
                "DualSense Bluetooth Audio — Protocol Lab V5.5");
            Console.WriteLine(
                "Does not modify firmware or install a kernel driver.");
            Console.WriteLine($"Log: {Path.GetFullPath(logPath)}");
            Console.WriteLine();

            using var engine = new DualSenseAudioEngine(options);

            if (options.DiagnosticOnly)
            {
                engine.RunDiagnostic();
                return 0;
            }

            if (options.Tone)
            {
                engine.StreamTone();
                return 0;
            }

            using MicBridge? mic = MicBridge.Enabled ? MicBridge.Start() : null;
            engine.StreamLoopback();
            return 0;
        }
        catch (Exception ex)
        {
            // Restore the real output BEFORE printing the error.
            // In V5, the catch block tried to write to an already closed logger,
            // hiding the actual loopback exception.
            Console.SetOut(originalOutput);

            try
            {
                if (logFile is not null)
                {
                    logFile.WriteLine();
                    logFile.WriteLine("ORIGINAL ERROR:");
                    logFile.WriteLine(ex);
                    logFile.Flush();
                }
            }
            catch
            {
                // Never let a logger failure hide the actual error.
            }

            Console.ForegroundColor = ConsoleColor.Red;
            originalOutput.WriteLine();
            originalOutput.WriteLine("ORIGINAL ERROR:");
            originalOutput.WriteLine(ex);
            Console.ResetColor();

            return 1;
        }
        finally
        {
            // Console.Out cannot keep pointing to TeeTextWriter after the log
            // file has been closed.
            Console.SetOut(originalOutput);

            try
            {
                tee?.Flush();
            }
            catch
            {
            }

            try
            {
                tee?.Dispose();
            }
            catch
            {
            }

            try
            {
                logFile?.Dispose();
            }
            catch
            {
            }
        }
    }
}

internal enum AudioTarget
{
    Speaker,
    Headset
}

internal enum ProtocolVariant
{
    Full39,
    Legacy35
}

internal static class ProtocolConstants
{
    public const int VendorId = 0x054C;
    public const int ProductId = 0x0CE6;

    public const int SampleRate = 48000;
    public const int Channels = 2;
    // The controller receives 480 Opus frames, while the reference
    // implementation feeds each packet with 512 PCM frames and compresses
    // them to 480. This synchronizes the approximately 10.666 ms clock.
    public const int InputFramesPerOpusPacket = 512;
    public const int InputSamplesPerOpusPacket =
        InputFramesPerOpusPacket * Channels;

    public const int FramesPerOpusPacket = 480;
    public const int SamplesPerOpusPacket = FramesPerOpusPacket * Channels;
    public const int OpusPacketBytes = 200;

    public const int StateReportLength = 142;
    public const int FullReportLength = 547;
    public const int LegacyReportLength = 334;
}

internal sealed class DualSenseAudioEngine : IDisposable
{
    private readonly Options _options;
    private readonly Device _hid;
    private readonly OpusEncoder _encoder;

    private byte _packetCounter;
    private int _sequenceCounter;
    private long _reportsSent;

    // Reusable buffers in the critical path prevent GC pauses.
    private readonly byte[] _fullReport =
        new byte[ProtocolConstants.FullReportLength];
    private readonly byte[] _legacyReport =
        new byte[ProtocolConstants.LegacyReportLength];

    private readonly float[] _opusFrameA =
        new float[ProtocolConstants.SamplesPerOpusPacket];
    private readonly float[] _opusFrameB =
        new float[ProtocolConstants.SamplesPerOpusPacket];

    private readonly byte[] _opusEncodedA =
        new byte[ProtocolConstants.OpusPacketBytes];
    private readonly byte[] _opusEncodedB =
        new byte[ProtocolConstants.OpusPacketBytes];

    private readonly ClockResampler _clockResampler = new();

    // Low latency mode: smaller PC-side audio buffers (set from --low-latency).
    public static bool LowLatency;

    private long _hidWriteCount;
    private long _hidWriteTicksTotal;
    private long _hidWriteTicksMaximum;
    private ControllerControlState _controllerState;
    private readonly ControllerControlMonitor? _controlMonitor;

    public DualSenseAudioEngine(Options options)
    {
        _options = options;
        _controllerState = ControllerControlState.FromOptions(options);
        _controlMonitor = string.IsNullOrWhiteSpace(options.ControlFile)
            ? null
            : new ControllerControlMonitor(options.ControlFile);

        Console.WriteLine(
            $"Opening DualSense HID " +
            $"{ProtocolConstants.VendorId:X4}:{ProtocolConstants.ProductId:X4}...");

        _hid = new Device(
            ProtocolConstants.VendorId,
            ProtocolConstants.ProductId);

        _encoder = new OpusEncoder(
            ProtocolConstants.SampleRate,
            ProtocolConstants.Channels,
            // Low-delay mode (as DS5Dongle) skips Opus look-ahead.
            OpusPredefinedValues.OPUS_APPLICATION_RESTRICTED_LOWDELAY);

        _encoder.SetExpertFrameDuration(
            OpusPredefinedValues.OPUS_FRAMESIZE_10_MS);
        _encoder.SetBitRate(
            ProtocolConstants.OpusPacketBytes * 8 * 100);
        _encoder.SetVbr(false);

        Console.WriteLine("HID controller opened.");
        Console.WriteLine($"Protocol: {_options.Protocol}");
        Console.WriteLine($"Target: {_options.Target}");
        Console.WriteLine($"Logical volume: {_options.Volume}/127");
        Console.WriteLine($"Period: {_options.PeriodMs:0.###} ms");
        Console.WriteLine(
            $"Lightbar: #{_controllerState.Red:X2}" +
            $"{_controllerState.Green:X2}{_controllerState.Blue:X2}");
        Console.WriteLine(_options.AppControlsLighting
            ? "Lighting control from this app: enabled."
            : "Lighting control from this app: disabled; DSX will continue controlling the LEDs.");
        Console.WriteLine();
    }

    public void RunDiagnostic()
    {
        Console.WriteLine("Sending audio initialization only...");
        SendAudioInitialization(repetitions: 5);
        Console.WriteLine("Initialization sent without an exception.");
        Console.WriteLine(
            "This confirms HID writing, but not that the firmware accepted the command.");
    }

    public void StreamTone()
    {
        Console.WriteLine(
            $"Tone: {_options.Frequency:0.#} Hz for {_options.Seconds}s.");
        Console.WriteLine("Start with a low volume.");
        Console.WriteLine();

        SendAudioInitialization(repetitions: 8);

        using var cancellation = CreateCancellationHandler();

        int framesPerReport = _options.Protocol == ProtocolVariant.Full39
            ? ProtocolConstants.FramesPerOpusPacket * 2
            : ProtocolConstants.FramesPerOpusPacket;

        int samplesPerReport = framesPerReport * ProtocolConstants.Channels;
        var samples = new float[samplesPerReport];

        double phase = 0;
        double increment =
            2.0 * Math.PI * _options.Frequency / ProtocolConstants.SampleRate;

        int preRollReports = Math.Max(
            4,
            (int)Math.Ceiling(250.0 / _options.PeriodMs));

        Console.WriteLine($"Silent pre-roll: {preRollReports} reports.");
        var silence = new float[samplesPerReport];

        var clock = new PacketClock(_options.PeriodMs);

        for (int i = 0; i < preRollReports; i++)
        {
            clock.Wait(cancellation.Token);
            SendAudioReport(silence);
        }

        int totalReports = (int)Math.Ceiling(
            _options.Seconds * 1000.0 / _options.PeriodMs);

        Console.WriteLine($"Sending {totalReports} tone reports...");

        for (
            int reportIndex = 0;
            reportIndex < totalReports &&
            !cancellation.IsCancellationRequested;
            reportIndex++)
        {
            double progress =
                reportIndex / (double)Math.Max(1, totalReports - 1);

            double fade = Math.Min(
                1.0,
                Math.Min(progress * 25.0, (1.0 - progress) * 25.0));

            float amplitude = (float)(_options.Amplitude * Math.Max(0, fade));

            for (int frame = 0; frame < framesPerReport; frame++)
            {
                float value = (float)(Math.Sin(phase) * amplitude);
                phase += increment;

                if (phase >= Math.PI * 2)
                {
                    phase -= Math.PI * 2;
                }

                int offset = frame * 2;
                samples[offset] = value;
                samples[offset + 1] = value;
            }

            clock.Wait(cancellation.Token);
            SendAudioReport(samples);

            if ((_reportsSent % 100) == 0)
            {
                Console.WriteLine($"Reports sent: {_reportsSent}");
            }
        }

        Console.WriteLine("Sending silent post-roll...");

        for (int i = 0; i < preRollReports; i++)
        {
            clock.Wait(cancellation.Token);
            SendAudioReport(silence);
        }

        Console.WriteLine($"Test complete. Total: {_reportsSent} reports.");
    }

    public void StreamLoopback()
    {
        Console.WriteLine(
            "Loopback captures an existing Windows output.");
        Console.WriteLine(
            "It does not create a new output in Windows Sound Settings.");
        Console.WriteLine();

        SendAudioInitialization(repetitions: 8);

        using var cancellation = CreateCancellationHandler();
        using var timerResolution = new WindowsTimerResolution(1);
        using var reportWriter = new QueuedHidReportWriter(
            _hid,
            capacity: 10,
            reportLength:
                _options.Protocol == ProtocolVariant.Full39
                    ? ProtocolConstants.FullReportLength
                    : ProtocolConstants.LegacyReportLength);

        GCLatencyMode previousLatencyMode =
            System.Runtime.GCSettings.LatencyMode;

        try
        {
            System.Runtime.GCSettings.LatencyMode =
                GCLatencyMode.SustainedLowLatency;
        }
        catch
        {
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device =
                AudioDevices.SelectOutput(
                    enumerator,
                    _options.DeviceIndex);
            using var capture =
                new WasapiLoopbackCapture(device);

            WaveFormat format = capture.WaveFormat;
            ValidateLoopbackFormat(format);

            Console.WriteLine(
                $"Capturing: {device.FriendlyName}");
            Console.WriteLine($"Format: {format}");
            Console.WriteLine(
                $"Report period: " +
                $"{_options.PeriodMs:0.###} ms");
            Console.WriteLine(
                "ClockFix: 1024 frames PCM → " +
                "960 Opus frames per Full39 report.");
            Console.WriteLine(
                "Press Ctrl+C to stop.");
            Console.WriteLine();

            int inputFramesPerReport =
                _options.Protocol == ProtocolVariant.Full39
                    ? ProtocolConstants.InputFramesPerOpusPacket * 2
                    : ProtocolConstants.InputFramesPerOpusPacket;

            int outputFramesPerReport =
                _options.Protocol == ProtocolVariant.Full39
                    ? ProtocolConstants.FramesPerOpusPacket * 2
                    : ProtocolConstants.FramesPerOpusPacket;

            int inputSamplesPerReport =
                inputFramesPerReport *
                ProtocolConstants.Channels;

            int outputSamplesPerReport =
                outputFramesPerReport *
                ProtocolConstants.Channels;

            // Lowered from 170/380 ms to cut headphone delay; Low latency mode halves it again.
            int targetBufferMs = LowLatency ? 34 : 60;
            int maximumBufferMs = LowLatency ? 80 : 140;

            int targetBufferSamples =
                ProtocolConstants.SampleRate *
                ProtocolConstants.Channels *
                targetBufferMs / 1000;

            int maximumBufferSamples =
                ProtocolConstants.SampleRate *
                ProtocolConstants.Channels *
                maximumBufferMs / 1000;

            var ring = new FloatRingBuffer(
                ProtocolConstants.SampleRate *
                ProtocolConstants.Channels *
                4);

            Exception? captureError = null;
            int stopped = 0;

            capture.DataAvailable += (_, eventArgs) =>
            {
                AddCapturedSamples(
                    eventArgs.Buffer,
                    eventArgs.BytesRecorded,
                    format,
                    ring);
            };

            capture.RecordingStopped += (_, eventArgs) =>
            {
                captureError = eventArgs.Exception;
                Interlocked.Exchange(ref stopped, 1);
            };

            capture.StartRecording();

            Console.WriteLine(
                $"Creating a {targetBufferMs} ms pre-buffer. " +
                "Keep audio playing...");

            var prebufferTimer = Stopwatch.StartNew();

            while (
                ring.Count < targetBufferSamples &&
                !cancellation.IsCancellationRequested &&
                Volatile.Read(ref stopped) == 0 &&
                prebufferTimer.Elapsed <
                    TimeSpan.FromSeconds(8))
            {
                Thread.Sleep(4);
            }

            double initialBufferMs =
                ring.Count * 1000.0 /
                (ProtocolConstants.SampleRate *
                 ProtocolConstants.Channels);

            Console.WriteLine(
                $"Initial buffer: {initialBufferMs:0.0} ms.");
            Console.WriteLine();

            try
            {
                Process.GetCurrentProcess().PriorityClass =
                    ProcessPriorityClass.High;
            }
            catch
            {
            }

            try
            {
                Thread.CurrentThread.Priority =
                    ThreadPriority.Highest;
            }
            catch
            {
            }

            var inputSamples =
                new float[inputSamplesPerReport];
            var outputSamples =
                new float[outputSamplesPerReport];

            var clock =
                new PacketClock(_options.PeriodMs);
            var stats = Stopwatch.StartNew();

            long underflows = 0;
            long droppedSamples = 0;
            long lateReports = 0;

            while (
                !cancellation.IsCancellationRequested &&
                Volatile.Read(ref stopped) == 0)
            {
                int bufferedNow = ring.Count;

                if (bufferedNow > maximumBufferSamples)
                {
                    int discard =
                        bufferedNow -
                        targetBufferSamples;

                    droppedSamples +=
                        ring.Discard(discard);
                }

                // With pre-buffering active, this data is normally already
                // available. The short wait avoids inserting silence only
                // because the WASAPI callback arrived a few milliseconds late.
                var dataDeadline =
                    Stopwatch.StartNew();

                while (
                    ring.Count < inputSamplesPerReport &&
                    dataDeadline.ElapsedMilliseconds < 8 &&
                    !cancellation.IsCancellationRequested &&
                    Volatile.Read(ref stopped) == 0)
                {
                    Thread.Sleep(1);
                }

                int read = ring.Read(inputSamples);

                if (read < inputSamples.Length)
                {
                    Array.Clear(
                        inputSamples,
                        read,
                        inputSamples.Length - read);
                    underflows++;
                }

                // Headphone loudness for the mic's echo reduction.
                float playEnergy = 0;
                for (int i = 0; i < inputSamples.Length; i++)
                {
                    playEnergy += inputSamples[i] * inputSamples[i];
                }
                MicBridge.PlaybackLevel = MathF.Sqrt(playEnergy / Math.Max(1, inputSamples.Length));
                EchoCanceller.AddPlayback(inputSamples);

                MicBridge.MixSidetone(inputSamples);
                _lastInputWindow = inputSamples;
                _clockResampler.Process(
                    inputSamples,
                    inputFramesPerReport,
                    outputSamples,
                    outputFramesPerReport);

                // Encode before the send time so Opus processing does not add
                // jitter to the Bluetooth radio path.
                byte[] preparedReport =
                    PrepareAudioReport(outputSamples);

                if (ApplyPendingControls(
                        reportWriter,
                        cancellation.Token))
                {
                    break;
                }

                long beforeWait =
                    Stopwatch.GetTimestamp();

                clock.Wait(cancellation.Token);

                if (cancellation.IsCancellationRequested)
                {
                    break;
                }

                long afterWait =
                    Stopwatch.GetTimestamp();

                // If preparation took so long that the clock was already late,
                // record it and let PacketClock recover.
                if (afterWait == beforeWait)
                {
                    lateReports++;
                }

                reportWriter.EnqueueCopy(
                    preparedReport,
                    cancellation.Token);

                _reportsSent++;

                if (stats.ElapsedMilliseconds >= 2000)
                {
                    double bufferedMs =
                        ring.Count * 1000.0 /
                        (ProtocolConstants.SampleRate *
                         ProtocolConstants.Channels);

                    double droppedMs =
                        droppedSamples * 1000.0 /
                        (ProtocolConstants.SampleRate *
                         ProtocolConstants.Channels);

                    HidWriterSnapshot hid =
                        reportWriter.Snapshot();

                    Console.WriteLine(
                        $"Produced: {_reportsSent} | " +
                        $"Sent: {hid.WritesCompleted} | " +
                        $"Queue: {hid.QueueDepth}/10 " +
                        $"(max {hid.MaximumQueueDepth}) | " +
                        $"PCM buffer: {bufferedMs:0.0} ms | " +
                        $"Underflows: {underflows} | " +
                        $"Dropped: {droppedMs:0.0} ms | " +
                        $"HID avg/max: " +
                        $"{hid.AverageWriteMs:0.00}/" +
                        $"{hid.MaximumWriteMs:0.00} ms");

                    stats.Restart();
                }
            }

            reportWriter.Complete();
            reportWriter.WaitForDrain(
                TimeSpan.FromSeconds(2));

            try
            {
                capture.StopRecording();
            }
            catch
            {
            }

            if (captureError is not null)
            {
                throw new InvalidOperationException(
                    $"WASAPI capture stopped: " +
                    $"{captureError.Message}",
                    captureError);
            }
        }
        finally
        {
            try
            {
                System.Runtime.GCSettings.LatencyMode =
                    previousLatencyMode;
            }
            catch
            {
            }
        }
    }

    private static void ResampleStereoLinear(
        float[] input,
        int inputFrames,
        float[] output,
        int outputFrames)
    {
        if (
            input.Length !=
            inputFrames * ProtocolConstants.Channels)
        {
            throw new ArgumentException(
                "Invalid input size.");
        }

        if (
            output.Length !=
            outputFrames * ProtocolConstants.Channels)
        {
            throw new ArgumentException(
                "Invalid output size.");
        }

        double step =
            inputFrames / (double)outputFrames;

        for (
            int outputFrame = 0;
            outputFrame < outputFrames;
            outputFrame++)
        {
            double sourcePosition =
                outputFrame * step;

            int sourceFrame =
                (int)sourcePosition;

            double fraction =
                sourcePosition - sourceFrame;

            int nextFrame =
                Math.Min(
                    sourceFrame + 1,
                    inputFrames - 1);

            int sourceOffset =
                sourceFrame *
                ProtocolConstants.Channels;

            int nextOffset =
                nextFrame *
                ProtocolConstants.Channels;

            int outputOffset =
                outputFrame *
                ProtocolConstants.Channels;

            for (
                int channel = 0;
                channel < ProtocolConstants.Channels;
                channel++)
            {
                float a =
                    input[sourceOffset + channel];
                float b =
                    input[nextOffset + channel];

                output[outputOffset + channel] =
                    a + (float)((b - a) * fraction);
            }
        }
    }

    private void SendAudioInitialization(int repetitions)
    {
        for (int i = 0; i < repetitions; i++)
        {
            byte[] report = BuildStateReport();
            _hid.Write(report);
            Thread.Sleep(20);
        }
    }

    private byte[] BuildStateReport()
    {
        var report = new byte[ProtocolConstants.StateReportLength];

        report[0] = 0x32;
        report[1] = NextSequenceByte();
        report[2] = 0x90;
        report[3] = 0x3F;

        int state = 4;

        // SetStateData byte 0:
        // AllowHeadphoneVolume | AllowSpeakerVolume | AllowAudioControl.
        report[state + 0] = MicBridge.Enabled ? (byte)0xF0 : (byte)0xB0;   // + AllowMicVolume

        // Without lighting control, preserve the original V5.5 audio flags and
        // do not declare lightbar or Player LEDs as valid fields.
        report[state + 1] = _options.AppControlsLighting
            ? (byte)0x96
            : (byte)0x82;

        report[state + 4] = _controllerState.Volume;
        report[state + 5] = _controllerState.Volume;
        report[state + 6] = 0x24; // mic gain: louder voice; MicBridge soft-limits peaks

        // AudioControl:
        // Headset uses OutputPathSelect 2 (bits 4-5); speaker uses 0.
        report[state + 7] =
            _options.Target == AudioTarget.Headset
                ? (byte)0x20
                : (byte)0x00;

        // MuteControl: all unmuted.
        report[state + 9] = 0x00;

        // AudioControl2: modest speaker pre-gain.
        report[state + 37] = 0x03;

        if (_options.AppControlsLighting)
        {
            // Standard DualSense report layout: lightbar controls.
            // valid_flag2 enables setup and lightbar_setup enables the RGB color.
            report[state + 38] = 0x02;
            report[state + 41] = 0x02;
            report[state + 42] = _controllerState.LedBrightness;
            report[state + 43] = _controllerState.PlayerLeds;
            report[state + 44] = _controllerState.LightbarEnabled
                ? _controllerState.Red
                : (byte)0;
            report[state + 45] = _controllerState.LightbarEnabled
                ? _controllerState.Green
                : (byte)0;
            report[state + 46] = _controllerState.LightbarEnabled
                ? _controllerState.Blue
                : (byte)0;
        }

        FillSonyCrc(report);
        return report;
    }

    private bool ApplyPendingControls(
        QueuedHidReportWriter reportWriter,
        CancellationToken cancellationToken)
    {
        if (
            _controlMonitor is null ||
            !_controlMonitor.TryRead(out ControllerControlState next))
        {
            return false;
        }

        _controllerState = next;

        if (next.StopRequested)
        {
            Console.WriteLine("Shutdown requested by the interface.");
            return true;
        }

        reportWriter.EnqueueCopy(
            BuildStateReport(),
            cancellationToken);

        Console.WriteLine(_options.AppControlsLighting
            ? $"Controls updated | Volume: {next.Volume}/127 | " +
              $"Lightbar: #{next.Red:X2}{next.Green:X2}{next.Blue:X2}"
            : $"Audio control updated | Volume: {next.Volume}/127 | DSX controls the LEDs");

        return false;
    }

    private void SendAudioReport(float[] samples)
    {
        byte[] report =
            PrepareAudioReport(samples);

        WritePreparedAudioReport(report);
    }

    private byte[] PrepareAudioReport(
        float[] samples)
    {
        return _options.Protocol ==
               ProtocolVariant.Full39
            ? PrepareFull39(samples)
            : PrepareLegacy35(samples);
    }

    private byte[] PrepareFull39(
        float[] samples)
    {
        int required =
            ProtocolConstants.SamplesPerOpusPacket * 2;

        if (samples.Length != required)
        {
            throw new ArgumentException(
                $"Full39 requires {required} " +
                "interleaved samples.");
        }

        byte[] report = _fullReport;
        Array.Clear(report);

        report[0] = 0x39;
        report[1] = NextSequenceByte();
        report[2] = 0x91;
        report[3] = 0x06;
        // Bit 0 enables the mic section (vDS: 0xFF vs 0xFE).
        report[4] = MicBridge.Enabled ? (byte)0x7F : (byte)0x7E;

        byte bufferLength =
            checked((byte)_options.BufferLength);

        report[5] = bufferLength;
        report[6] = bufferLength;
        report[7] = bufferLength;
        report[8] = bufferLength;

        unchecked
        {
            _packetCounter += 2;
        }

        report[9] = _packetCounter;

        // Two silent 64-byte haptic blocks.
        report[10] = 0xD2;
        report[11] = 64;

        report[140] = (byte)(
            GetAudioPacketType() | 0xC0);
        report[141] =
            ProtocolConstants.OpusPacketBytes;

        Array.Copy(
            samples,
            0,
            _opusFrameA,
            0,
            ProtocolConstants.SamplesPerOpusPacket);

        Array.Copy(
            samples,
            ProtocolConstants.SamplesPerOpusPacket,
            _opusFrameB,
            0,
            ProtocolConstants.SamplesPerOpusPacket);

        EncodeOpusFrame(
            _opusFrameA,
            _opusEncodedA);

        EncodeOpusFrame(
            _opusFrameB,
            _opusEncodedB);

        Array.Copy(
            _opusEncodedA,
            0,
            report,
            142,
            ProtocolConstants.OpusPacketBytes);

        Array.Copy(
            _opusEncodedB,
            0,
            report,
            342,
            ProtocolConstants.OpusPacketBytes);

        FillSonyCrc(report);
        return report;
    }

    private byte[] PrepareLegacy35(
        float[] samples)
    {
        if (
            samples.Length !=
            ProtocolConstants.SamplesPerOpusPacket)
        {
            throw new ArgumentException(
                $"Legacy35 requires " +
                $"{ProtocolConstants.SamplesPerOpusPacket} " +
                "samples.");
        }

        if (Haptics.Enabled)
        {
            return PrepareHaptics36(samples);
        }

        byte[] report = _legacyReport;
        Array.Clear(report);

        report[0] = 0x35;
        report[1] = NextSequenceByte();
        report[2] = 0x91;
        report[3] = 0x07;
        report[4] = MicBridge.Enabled ? (byte)0xFF : (byte)0xFE;
        report[9] = 0xFF;
        report[10] = _packetCounter++;

        report[11] = (byte)(
            GetAudioPacketType() | 0x80);
        report[12] =
            ProtocolConstants.OpusPacketBytes;

        Array.Copy(
            samples,
            0,
            _opusFrameA,
            0,
            ProtocolConstants.SamplesPerOpusPacket);

        EncodeOpusFrame(
            _opusFrameA,
            _opusEncodedA);

        Array.Copy(
            _opusEncodedA,
            0,
            report,
            13,
            ProtocolConstants.OpusPacketBytes);

        FillSonyCrc(report);
        return report;
    }

    // vDS 0x36 packet (398 bytes): same pacing as Legacy35, plus a 64-byte
    // haptics block (32 stereo int8 frames) derived from the game audio's bass.
    private readonly byte[] _hapticsReport = new byte[398];

    private byte[] PrepareHaptics36(float[] samples)
    {
        byte[] report = _hapticsReport;
        Array.Clear(report);

        report[0] = 0x36;
        report[1] = NextSequenceByte();
        report[2] = 0x91;
        report[3] = 0x07;
        report[4] = MicBridge.Enabled ? (byte)0xFF : (byte)0xFE;
        byte bufferLength = checked((byte)_options.BufferLength);
        for (int i = 5; i <= 9; i++)
        {
            report[i] = bufferLength;
        }
        report[10] = _packetCounter++;

        // State block: all valid-flags zero, so it changes nothing.
        report[11] = 0x90;
        report[12] = 63;

        report[76] = 0x92;
        report[77] = 64;
        Haptics.Fill(_lastInputWindow, report.AsSpan(78, 64));

        report[142] = (byte)(GetAudioPacketType() | 0x80);
        report[143] = ProtocolConstants.OpusPacketBytes;
        Array.Copy(samples, 0, _opusFrameA, 0, ProtocolConstants.SamplesPerOpusPacket);
        EncodeOpusFrame(_opusFrameA, _opusEncodedA);
        Array.Copy(_opusEncodedA, 0, report, 144, ProtocolConstants.OpusPacketBytes);

        FillSonyCrc(report);
        return report;
    }

    private float[] _lastInputWindow = Array.Empty<float>();

    private void WritePreparedAudioReport(
        byte[] report)
    {
        long started =
            Stopwatch.GetTimestamp();

        _hid.Write(report);

        long elapsed =
            Stopwatch.GetTimestamp() - started;

        _hidWriteCount++;
        _hidWriteTicksTotal += elapsed;

        if (elapsed > _hidWriteTicksMaximum)
        {
            _hidWriteTicksMaximum = elapsed;
        }

        _reportsSent++;
    }

    private void EncodeOpusFrame(
        float[] input,
        byte[] encoded)
    {
        Array.Clear(encoded);

        int encodedBytes = _encoder.Encode(
            input,
            ProtocolConstants.FramesPerOpusPacket,
            encoded,
            ProtocolConstants.OpusPacketBytes);

        if (
            encodedBytes <= 0 ||
            encodedBytes >
            ProtocolConstants.OpusPacketBytes)
        {
            throw new InvalidOperationException(
                $"Opus produced an invalid size: " +
                $"{encodedBytes}.");
        }

        // CBR mode normally produces 200 bytes. If it produces less, the
        // remaining buffer is already zeroed.
    }

    private byte GetAudioPacketType()
    {
        return _options.Target == AudioTarget.Headset
            ? (byte)0x16
            : (byte)0x13;
    }

    private byte NextSequenceByte()
    {
        byte result = (byte)(_sequenceCounter << 4);
        _sequenceCounter = (_sequenceCounter + 1) & 0x0F;
        return result;
    }

    private void EncodeOpusFrame(
        ReadOnlySpan<float> input,
        Span<byte> destination)
    {
        float[] source = input.ToArray();
        byte[] encoded = new byte[ProtocolConstants.OpusPacketBytes];

        int encodedBytes = _encoder.Encode(
            source,
            ProtocolConstants.FramesPerOpusPacket,
            encoded,
            ProtocolConstants.OpusPacketBytes);

        if (
            encodedBytes <= 0 ||
            encodedBytes >
            ProtocolConstants.OpusPacketBytes)
        {
            throw new InvalidOperationException(
                $"Opus produced an invalid size: {encodedBytes}.");
        }

        destination.Clear();
        encoded.AsSpan(0, encodedBytes).CopyTo(destination);
    }

    private static void FillSonyCrc(byte[] report)
    {
        uint crc = SonyCrc32(
            report.AsSpan(0, report.Length - 4));

        BinaryPrimitives.WriteUInt32LittleEndian(
            report.AsSpan(report.Length - 4, 4),
            crc);
    }

    private static uint SonyCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = ~0xEADA2D49u;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc =
                    (crc >> 1) ^
                    (0xEDB88320u &
                     (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static void ValidateLoopbackFormat(WaveFormat format)
    {
        bool supported =
            format.SampleRate == ProtocolConstants.SampleRate &&
            format.Channels == ProtocolConstants.Channels &&
            (
                (
                    format.Encoding ==
                    WaveFormatEncoding.IeeeFloat &&
                    format.BitsPerSample == 32
                ) ||
                (
                    format.Encoding ==
                    WaveFormatEncoding.Pcm &&
                    format.BitsPerSample == 16
                )
            );

        if (!supported)
        {
            throw new NotSupportedException(
                "Use an output configured as " +
                "48 kHz stereo, 32-bit float, or 16-bit PCM. " +
                $"Current format: {format}");
        }
    }

    private static void AddCapturedSamples(
        byte[] source,
        int byteCount,
        WaveFormat format,
        FloatRingBuffer destination)
    {
        if (
            format.Encoding == WaveFormatEncoding.IeeeFloat &&
            format.BitsPerSample == 32)
        {
            int count = byteCount / sizeof(float);
            var converted = new float[count];
            ReadOnlySpan<byte> bytes = source.AsSpan(0, byteCount);

            for (int i = 0; i < count; i++)
            {
                int bits = BinaryPrimitives.ReadInt32LittleEndian(
                    bytes.Slice(i * sizeof(float), sizeof(float)));
                converted[i] = BitConverter.Int32BitsToSingle(bits);
            }

            destination.Write(converted);
            return;
        }

        if (
            format.Encoding == WaveFormatEncoding.Pcm &&
            format.BitsPerSample == 16)
        {
            int count = byteCount / sizeof(short);
            var converted = new float[count];
            ReadOnlySpan<byte> bytes = source.AsSpan(0, byteCount);

            for (int i = 0; i < count; i++)
            {
                short value = BinaryPrimitives.ReadInt16LittleEndian(
                    bytes.Slice(i * sizeof(short), sizeof(short)));
                converted[i] = value / 32768f;
            }

            destination.Write(converted);
        }
    }

    private static CancellationTokenSource CreateCancellationHandler()
    {
        var source = new CancellationTokenSource();

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            source.Cancel();
        };

        return source;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _hid.Dispose();
    }
}

internal static class AudioDevices
{
    private const int ElementNotFoundHResult =
        unchecked((int)0x80070490);

    public static void PrintOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();

        MMDeviceCollection devices =
            enumerator.EnumerateAudioEndPoints(
                DataFlow.Render,
                DeviceState.Active);

        string? multimediaId =
            TryGetDefaultId(enumerator, Role.Multimedia);
        string? consoleId =
            TryGetDefaultId(enumerator, Role.Console);
        string? communicationsId =
            TryGetDefaultId(enumerator, Role.Communications);

        Console.WriteLine("Active Windows audio outputs:");
        Console.WriteLine();

        if (devices.Count == 0)
        {
            Console.WriteLine(
                "No active output was found.");
            Console.WriteLine(
                "Enable a speaker, monitor, TV, headset, or HDMI output " +
                "in Windows Sound Settings.");
            return;
        }

        for (int i = 0; i < devices.Count; i++)
        {
            MMDevice device = devices[i];
            var markers = new List<string>();

            if (device.ID == multimediaId)
            {
                markers.Add("DEFAULT MULTIMEDIA");
            }

            if (device.ID == consoleId)
            {
                markers.Add("DEFAULT CONSOLE");
            }

            if (device.ID == communicationsId)
            {
                markers.Add("DEFAULT COMMUNICATIONS");
            }

            string suffix =
                markers.Count > 0
                    ? $" [{string.Join(", ", markers)}]"
                    : string.Empty;

            Console.WriteLine(
                $"[{i}] {device.FriendlyName}{suffix}");
            Console.WriteLine($"    ID: {device.ID}");
        }

        if (
            multimediaId is null &&
            consoleId is null &&
            communicationsId is null)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                "WARNING: active outputs exist, but Windows has not set " +
                "any of them as default.");
            Console.WriteLine(
                "V5.2 will automatically choose output index 0, " +
                "or you can use --device N.");
            Console.ResetColor();
        }
    }

    public static MMDevice SelectOutput(
        MMDeviceEnumerator enumerator,
        int? index)
    {
        MMDeviceCollection devices =
            enumerator.EnumerateAudioEndPoints(
                DataFlow.Render,
                DeviceState.Active);

        if (devices.Count == 0)
        {
            throw new InvalidOperationException(
                "Windows has no active audio output. " +
                "Open Settings > System > Sound, enable an output, " +
                "and set it as default.");
        }

        if (index is not null)
        {
            if (index.Value < 0 || index.Value >= devices.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    $"Index {index.Value} is invalid. " +
                    $"There are {devices.Count} active outputs. " +
                    "Execute 07_LIST_AUDIO_DEVICES.bat.");
            }

            MMDevice selected = devices[index.Value];

            Console.WriteLine(
                $"Manually selected output [{index.Value}]: " +
                $"{selected.FriendlyName}");

            return selected;
        }

        foreach (
            Role role in
            new[]
            {
                Role.Multimedia,
                Role.Console,
                Role.Communications
            })
        {
            MMDevice? candidate =
                TryGetDefaultDevice(enumerator, role);

            if (candidate is not null)
            {
                Console.WriteLine(
                    $"Default output found ({RoleName(role)}): " +
                    $"{candidate.FriendlyName}");

                return candidate;
            }
        }

        MMDevice fallback = devices[0];

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(
            "Windows did not report a default output.");
        Console.WriteLine(
            $"Automatically using the first active output [0]: " +
            $"{fallback.FriendlyName}");
        Console.WriteLine(
            "To choose another output, run 07_LIST_AUDIO_DEVICES.bat " +
            "and then 08A_LOOPBACK_CHOOSE_HEADSET.bat.");
        Console.ResetColor();

        return fallback;
    }

    private static MMDevice? TryGetDefaultDevice(
        MMDeviceEnumerator enumerator,
        Role role)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                role);
        }
        catch (System.Runtime.InteropServices.COMException ex)
            when (ex.HResult == ElementNotFoundHResult)
        {
            return null;
        }
    }

    private static string? TryGetDefaultId(
        MMDeviceEnumerator enumerator,
        Role role)
    {
        MMDevice? device = TryGetDefaultDevice(
            enumerator,
            role);

        if (device is null)
        {
            return null;
        }

        using (device)
        {
            return device.ID;
        }
    }

    private static string RoleName(Role role)
    {
        return role switch
        {
            Role.Multimedia => "Multimedia",
            Role.Console => "Console",
            Role.Communications => "Communications",
            _ => role.ToString()
        };
    }
}

internal sealed class PacketClock
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _periodTicks;
    private long _next;

    public PacketClock(double periodMs)
    {
        _periodTicks = (long)(
            periodMs * Stopwatch.Frequency / 1000.0);
        _next = _clock.ElapsedTicks;
    }

    public void Wait(CancellationToken token)
    {
        _next += _periodTicks;

        while (
            _clock.ElapsedTicks < _next &&
            !token.IsCancellationRequested)
        {
            long remaining = _next - _clock.ElapsedTicks;

            if (remaining > Stopwatch.Frequency / 500)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }

        if (
            _clock.ElapsedTicks - _next >
            Stopwatch.Frequency / 10)
        {
            _next = _clock.ElapsedTicks;
        }
    }
}

internal sealed class FloatRingBuffer
{
    private readonly float[] _data;
    private readonly object _sync = new();

    private int _read;
    private int _write;
    private int _count;

    public FloatRingBuffer(int capacity)
    {
        _data = new float[capacity];
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<float> source)
    {
        lock (_sync)
        {
            foreach (float sample in source)
            {
                if (_count == _data.Length)
                {
                    _read = (_read + 1) % _data.Length;
                    _count--;
                }

                _data[_write] = sample;
                _write = (_write + 1) % _data.Length;
                _count++;
            }
        }
    }

    public int Read(float[] destination)
    {
        lock (_sync)
        {
            int amount = Math.Min(
                destination.Length,
                _count);

            for (int i = 0; i < amount; i++)
            {
                destination[i] = _data[_read];
                _read = (_read + 1) % _data.Length;
            }

            _count -= amount;
            return amount;
        }
    }

    public int Discard(int requestedSamples)
    {
        if (requestedSamples <= 0)
        {
            return 0;
        }

        lock (_sync)
        {
            int amount = Math.Min(
                requestedSamples,
                _count);

            _read =
                (_read + amount) % _data.Length;
            _count -= amount;

            return amount;
        }
    }
}

internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override System.Text.Encoding Encoding =>
        _first.Encoding;

    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
    }

    public override void Flush()
    {
        _first.Flush();
        _second.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        // Do not close Console.Out or StreamWriter. Each writer's owner
        // manages its own lifetime.
        base.Dispose(disposing);
    }
}

internal readonly record struct HidWriterSnapshot(
    long WritesCompleted,
    int QueueDepth,
    int MaximumQueueDepth,
    double AverageWriteMs,
    double MaximumWriteMs);

internal sealed class QueuedHidReportWriter : IDisposable
{
    private readonly Device _device;
    private readonly BlockingCollection<byte[]> _queue;
    private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _pools =
        new();
    private readonly Thread _thread;
    private readonly int _maximumReportLength;

    private long _writesCompleted;
    private long _writeTicksTotal;
    private long _writeTicksMaximum;
    private int _maximumQueueDepth;
    private Exception? _writerException;
    private int _disposed;

    public QueuedHidReportWriter(
        Device device,
        int capacity,
        int reportLength)
    {
        if (capacity < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity));
        }

        if (reportLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportLength));
        }

        _device = device;
        _maximumReportLength = Math.Max(
            reportLength,
            ProtocolConstants.StateReportLength);
        _queue = new BlockingCollection<byte[]>(
            new ConcurrentQueue<byte[]>(),
            capacity);

        // Preallocate all buffers so the critical path does not create garbage.
        ConcurrentBag<byte[]> primaryPool =
            _pools.GetOrAdd(
                reportLength,
                static _ => new ConcurrentBag<byte[]>());

        for (int i = 0; i < capacity + 2; i++)
        {
            primaryPool.Add(new byte[reportLength]);
        }

        _thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "DualSense HID Audio Writer",
            Priority = ThreadPriority.Highest
        };

        _thread.Start();
    }

    public void EnqueueCopy(
        byte[] report,
        CancellationToken cancellationToken)
    {
        ThrowIfFailed();

        if (
            report.Length <= 0 ||
            report.Length > _maximumReportLength)
        {
            throw new ArgumentException(
                $"HID report has an invalid size: {report.Length} bytes.",
                nameof(report));
        }

        ConcurrentBag<byte[]> pool =
            _pools.GetOrAdd(
                report.Length,
                static _ => new ConcurrentBag<byte[]>());

        if (!pool.TryTake(out byte[]? buffer))
        {
            buffer = new byte[report.Length];
        }

        Buffer.BlockCopy(
            report,
            0,
            buffer,
            0,
            report.Length);

        try
        {
            // Preserve every report. The ten-packet queue absorbs short
            // hid_write stalls without creating gaps in the audio.
            _queue.Add(
                buffer,
                cancellationToken);
        }
        catch
        {
            ReturnBuffer(buffer);
            throw;
        }

        int depth = _queue.Count;
        UpdateMaximumQueueDepth(depth);
    }

    public HidWriterSnapshot Snapshot()
    {
        long writes =
            Interlocked.Read(ref _writesCompleted);

        long totalTicks =
            Interlocked.Read(ref _writeTicksTotal);

        long maximumTicks =
            Interlocked.Read(ref _writeTicksMaximum);

        double averageMs =
            writes == 0
                ? 0
                : totalTicks *
                  1000.0 /
                  Stopwatch.Frequency /
                  writes;

        double maximumMs =
            maximumTicks *
            1000.0 /
            Stopwatch.Frequency;

        return new HidWriterSnapshot(
            writes,
            _queue.Count,
            Volatile.Read(ref _maximumQueueDepth),
            averageMs,
            maximumMs);
    }

    public void Complete()
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.CompleteAdding();
        }
    }

    public bool WaitForDrain(TimeSpan timeout)
    {
        Complete();
        return _thread.Join(timeout);
    }

    private void WriterLoop()
    {
        try
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass =
                    ProcessPriorityClass.High;
            }
            catch
            {
            }

            foreach (
                byte[] report in
                _queue.GetConsumingEnumerable())
            {
                long started =
                    Stopwatch.GetTimestamp();

                _device.Write(report);

                long elapsed =
                    Stopwatch.GetTimestamp() - started;

                Interlocked.Increment(
                    ref _writesCompleted);

                Interlocked.Add(
                    ref _writeTicksTotal,
                    elapsed);

                UpdateMaximumWriteTicks(
                    elapsed);

                Array.Clear(report);
                ReturnBuffer(report);
            }
        }
        catch (Exception ex)
        {
            _writerException = ex;

            while (_queue.TryTake(out byte[]? report))
            {
                Array.Clear(report);
                ReturnBuffer(report);
            }
        }
    }

    private void ReturnBuffer(byte[] report)
    {
        _pools.GetOrAdd(
                report.Length,
                static _ => new ConcurrentBag<byte[]>())
            .Add(report);
    }

    private void ThrowIfFailed()
    {
        Exception? error = _writerException;

        if (error is not null)
        {
            throw new InvalidOperationException(
                "The HID writer thread was interrupted.",
                error);
        }
    }

    private void UpdateMaximumQueueDepth(
        int candidate)
    {
        while (true)
        {
            int current =
                Volatile.Read(
                    ref _maximumQueueDepth);

            if (candidate <= current)
            {
                return;
            }

            if (
                Interlocked.CompareExchange(
                    ref _maximumQueueDepth,
                    candidate,
                    current) == current)
            {
                return;
            }
        }
    }

    private void UpdateMaximumWriteTicks(
        long candidate)
    {
        while (true)
        {
            long current =
                Interlocked.Read(
                    ref _writeTicksMaximum);

            if (candidate <= current)
            {
                return;
            }

            if (
                Interlocked.CompareExchange(
                    ref _writeTicksMaximum,
                    candidate,
                    current) == current)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (
            Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }

        Complete();
        _thread.Join(
            TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}

internal sealed class WindowsTimerResolution : IDisposable
{
    private readonly uint _milliseconds;
    private readonly bool _active;

    public WindowsTimerResolution(uint milliseconds)
    {
        _milliseconds = milliseconds;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _active =
                timeBeginPeriod(milliseconds) == 0;
        }
        catch
        {
            _active = false;
        }
    }

    public void Dispose()
    {
        if (!_active)
        {
            return;
        }

        try
        {
            timeEndPeriod(_milliseconds);
        }
        catch
        {
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(
        uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(
        uint uPeriod);
}

internal readonly record struct ControllerControlState(
    byte Volume,
    byte Red,
    byte Green,
    byte Blue,
    byte LedBrightness,
    byte PlayerLeds,
    bool LightbarEnabled,
    bool StopRequested)
{
    public static ControllerControlState FromOptions(Options options)
    {
        return new ControllerControlState(
            checked((byte)options.Volume),
            options.LedRed,
            options.LedGreen,
            options.LedBlue,
            options.LedBrightness,
            options.PlayerLeds,
            options.LightbarEnabled,
            false);
    }
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

internal sealed class ControllerControlMonitor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private long _nextCheckAt;
    private int _lastVersion = -1;

    public ControllerControlMonitor(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public bool TryRead(out ControllerControlState state)
    {
        state = default;

        long now = Environment.TickCount64;

        if (now < Volatile.Read(ref _nextCheckAt))
        {
            return false;
        }

        Volatile.Write(ref _nextCheckAt, now + 20);

        try
        {
            if (!File.Exists(_path))
            {
                return false;
            }

            ControllerControlDocument? document =
                JsonSerializer.Deserialize<ControllerControlDocument>(
                    File.ReadAllText(_path),
                    JsonOptions);

            if (
                document is null ||
                document.Version <= _lastVersion)
            {
                return false;
            }

            state = new ControllerControlState(
                (byte)Math.Clamp(document.Volume, 0, 127),
                (byte)Math.Clamp(document.Red, 0, 255),
                (byte)Math.Clamp(document.Green, 0, 255),
                (byte)Math.Clamp(document.Blue, 0, 255),
                (byte)Math.Clamp(document.LedBrightness, 0, 2),
                (byte)Math.Clamp(document.PlayerLeds, 0, 31),
                document.LightbarEnabled,
                document.StopRequested);

            _lastVersion = document.Version;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed record Options(
    bool Help,
    bool DiagnosticOnly,
    bool ListDevices,
    bool Tone,
    ProtocolVariant Protocol,
    AudioTarget Target,
    int Volume,
    int BufferLength,
    double PeriodMs,
    double Frequency,
    int Seconds,
    double Amplitude,
    int? DeviceIndex,
    byte LedRed,
    byte LedGreen,
    byte LedBlue,
    byte LedBrightness,
    byte PlayerLeds,
    bool LightbarEnabled,
    bool AppControlsLighting,
    string? ControlFile)
{
    public static Options Parse(string[] args)
    {
        bool help = false;
        bool diagnostic = false;
        bool list = false;
        bool tone = false;

        ProtocolVariant protocol = ProtocolVariant.Full39;
        AudioTarget target = AudioTarget.Headset;

        int volume = 100;
        int bufferLength = 64;
        double period = 21.333333;
        double frequency = 440;
        int seconds = 5;
        double amplitude = 0.08;
        int? deviceIndex = null;
        byte ledRed = 0x62;
        byte ledGreen = 0x7C;
        byte ledBlue = 0xFF;
        byte ledBrightness = 0;
        byte playerLeds = 0x04;
        bool lightbarEnabled = true;
        bool appControlsLighting = false;
        string? controlFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].ToLowerInvariant();

            switch (arg)
            {
                case "-h":
                case "--help":
                    help = true;
                    break;

                case "--diagnostic":
                    diagnostic = true;
                    break;

                case "--list-devices":
                    list = true;
                    break;

                case "--tone":
                    tone = true;
                    break;

                case "--full39":
                    protocol = ProtocolVariant.Full39;
                    break;

                case "--legacy35":
                    protocol = ProtocolVariant.Legacy35;
                    period = 10.666;
                    break;

                case "--speaker":
                    target = AudioTarget.Speaker;
                    break;

                case "--headset":
                    target = AudioTarget.Headset;
                    break;

                case "--volume":
                    volume = int.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture);
                    break;

                case "--buffer":
                    bufferLength = int.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture);
                    break;

                case "--period":
                    period = double.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture);
                    break;

                case "--frequency":
                    frequency = double.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture);
                    break;

                case "--seconds":
                    seconds = int.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture);
                    break;

                case "--amplitude":
                    amplitude = double.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture);
                    break;

                case "--device":
                    deviceIndex = int.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture);
                    break;

                case "--led":
                    (ledRed, ledGreen, ledBlue) =
                        ParseRgb(Require(args, ref i, arg));
                    break;

                case "--led-brightness":
                    ledBrightness = checked((byte)int.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture));
                    break;

                case "--player-leds":
                    playerLeds = checked((byte)int.Parse(
                        Require(args, ref i, arg),
                        CultureInfo.InvariantCulture));
                    break;

                case "--lightbar-off":
                    lightbarEnabled = false;
                    break;

                case "--control-lighting":
                    appControlsLighting = true;
                    break;

                case "--control-file":
                    controlFile = Require(args, ref i, arg);
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown argument: {args[i]}");
            }
        }

        if (volume < 0 || volume > 127)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volume),
                "Volume must be between 0 and 127.");
        }

        if (bufferLength < 1 || bufferLength > 127)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferLength));
        }

        if (period < 5 || period > 40)
        {
            throw new ArgumentOutOfRangeException(
                nameof(period));
        }

        if (amplitude <= 0 || amplitude > 0.25)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amplitude),
                "Amplitude must be greater than 0 and no more than 0.25.");
        }

        if (ledBrightness > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ledBrightness),
                "LED brightness must be between 0 and 2.");
        }

        if (playerLeds > 31)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerLeds),
                "Player LEDs must be between 0 and 31.");
        }

        return new Options(
            help,
            diagnostic,
            list,
            tone,
            protocol,
            target,
            volume,
            bufferLength,
            period,
            frequency,
            seconds,
            amplitude,
            deviceIndex,
            ledRed,
            ledGreen,
            ledBlue,
            ledBrightness,
            playerLeds,
            lightbarEnabled,
            appControlsLighting,
            controlFile);
    }

    private static (byte Red, byte Green, byte Blue) ParseRgb(
        string value)
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
            throw new ArgumentException(
                $"Invalid color: {value}. Use RRGGBB, for example 627CFF.");
        }

        return (
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
    }

    private static string Require(
        string[] args,
        ref int index,
        string name)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException(
                $"{name} requires a value.");
        }

        index++;
        return args[index];
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
Protocol Lab V5

Main tests:
  --tone --full39 --speaker
  --tone --full39 --headset
  --tone --legacy35 --speaker
  --tone --legacy35 --headset

Loopback:
  --full39 --headset
  --full39 --speaker
  --device 0 --full39 --headset

Diagnostics:
  --diagnostic --full39 --headset
  --list-devices

Settings:
  --volume 100
  --buffer 48
  --period 21.333333
  --frequency 440
  --seconds 5
  --amplitude 0.08
  --led 627CFF
  --led-brightness 0
  --player-leds 4
  --lightbar-off
  --control-lighting
  --control-file path.json

Full39 sends:
  - SetState 0x32 to enable audio and volume;
  - a 547-byte 0x39 report;
  - two 200-byte Opus packets per report;
  - silent haptic blocks;
  - counter, sequence, and CRC.
""");
    }
}
