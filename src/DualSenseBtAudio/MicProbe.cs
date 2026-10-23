using HidApi;

// Experimental: asks the controller to enable its microphone over Bluetooth,
// then records every HID input report it sends so we can see whether any
// microphone data arrives. Nothing here is a confirmed protocol.
internal static class MicProbe
{
    private const int ReadLength = 547;
    private const int BaselineSeconds = 4;
    private const int ListenSeconds = 8;

    public static void Run()
    {
        Console.WriteLine("Mic probe (experimental)");
        Console.WriteLine("Keep the controller still during the probe.");
        Console.WriteLine();

        using var hid = new Device(ProtocolConstants.VendorId, ProtocolConstants.ProductId);

        Console.WriteLine($"Phase 1: baseline for {BaselineSeconds}s, stay silent...");
        ProbeStats baseline = Listen(hid, BaselineSeconds);
        baseline.Print("Baseline");

        Console.WriteLine();
        Console.WriteLine("Sending mic enable (state report + 0x32 mic open, per vDS)...");
        for (int i = 0; i < 5; i++)
        {
            hid.Write(BuildMicEnableReport((byte)i));
            Thread.Sleep(10);
            hid.Write(BuildMicOpenReport((byte)i, open: true));
            Thread.Sleep(20);
        }

        Console.WriteLine($"Phase 2: TALK or blow into the controller mic for {ListenSeconds}s...");
        ProbeStats active = Listen(hid, ListenSeconds);
        active.Print("While talking");

        for (int i = 0; i < 3; i++)
        {
            hid.Write(BuildMicOpenReport((byte)i, open: false));
            Thread.Sleep(10);
        }

        Console.WriteLine();
        Console.WriteLine("Result:");
        if (active.AudioFrames > 0)
        {
            Console.WriteLine(
                $"  MIC AUDIO RECEIVED: {active.AudioFrames} audio frames " +
                $"({active.AudioFrames / (double)ListenSeconds:0} per second, 100 expected for 10 ms Opus).");
            Console.WriteLine(
                $"  Distinct Opus payloads: {active.DistinctAudioPayloads} (more than a few = real sound, not silence).");
            return;
        }
        var newIds = active.Ids.Keys.Except(baseline.Ids.Keys).ToList();
        if (newIds.Count > 0)
        {
            Console.WriteLine(
                "  New report IDs appeared after enabling the mic: " +
                string.Join(", ", newIds.Select(id => $"0x{id:X2}")) +
                ". This may be mic data - share this log.");
        }
        else if (active.MaxLength > baseline.MaxLength)
        {
            Console.WriteLine("  Reports got longer after enabling the mic. Possible mic data - share this log.");
        }
        else
        {
            Console.WriteLine("  No new report types or sizes. The controller sent no visible mic data");
            Console.WriteLine("  over Bluetooth to Windows (expected with the standard Windows HID driver).");
        }
    }

    private static ProbeStats Listen(Device hid, int seconds)
    {
        var stats = new ProbeStats();
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            ReadOnlySpan<byte> data = hid.ReadTimeout(ReadLength, 50);
            if (data.Length == 0)
            {
                continue;
            }
            stats.Add(data);
        }
        return stats;
    }

    private static byte[] BuildMicEnableReport(byte sequence)
    {
        var report = new byte[78];
        report[0] = 0x31;
        report[1] = (byte)((sequence & 0x0F) << 4);
        report[2] = 0x10;
        const int common = 3;
        report[common + 0] = 0x40 | 0x80;          // valid_flag0: mic volume + audio control
        report[common + 1] = 0x01 | 0x02 | 0x80;   // valid_flag1: mute LED + power save + audio control 2
        report[common + 6] = 0x40;                 // mic volume
        report[common + 7] = 0x08;                 // audio control: auto mic select (headset when plugged)
        report[common + 8] = 0x00;                 // mute LED off
        report[common + 9] = 0x00;                 // power save: mic not muted
        report[common + 37] = 0x01;                // audio control 2 (vDS default)
        uint crc = Crc32(report.AsSpan(0, 74));
        BitConverter.GetBytes(crc).CopyTo(report, 74);
        return report;
    }

    // Mic stream open/close report, layout taken from vDS build_bt_mic_report.
    private static byte[] BuildMicOpenReport(byte sequence, bool open)
    {
        var report = new byte[142];
        report[0] = 0x32;
        report[1] = (byte)((sequence & 0x0F) << 4);
        report[2] = 0x91;
        report[3] = 0x07;
        report[4] = open ? (byte)0xFF : (byte)0xFE;
        for (int i = 5; i <= 9; i++)
        {
            report[i] = 64;
        }
        report[10] = (byte)(sequence & 0x0F);
        report[11] = 0x92;
        report[12] = 0x40;
        uint crc = Crc32(report.AsSpan(0, 138));
        BitConverter.GetBytes(crc).CopyTo(report, 138);
        return report;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        crc = Step(crc, 0xA2);
        foreach (byte b in data)
        {
            crc = Step(crc, b);
        }
        return ~crc;

        static uint Step(uint crc, byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc;
        }
    }

    private sealed class ProbeStats
    {
        public readonly Dictionary<byte, int> Ids = new();
        public readonly Dictionary<int, int> Lengths = new();
        public int MaxLength;
        private byte[]? _previous;
        private readonly Dictionary<int, int> _changes = new();
        private int _total;

        public int AudioFrames;
        public int DistinctAudioPayloads => _audioPayloads.Count;
        private readonly HashSet<string> _audioPayloads = new();

        public void Add(ReadOnlySpan<byte> data)
        {
            _total++;
            // vDS: header low nibble 0x2 marks a mic audio frame; 71-byte Opus follows the header.
            if (data.Length >= 3 + 71 && data[0] == 0x31 && (data[1] & 0x0F) == 0x02)
            {
                AudioFrames++;
                _audioPayloads.Add(Convert.ToHexString(data.Slice(3, 71)));
                return;
            }
            Ids[data[0]] = Ids.GetValueOrDefault(data[0]) + 1;
            Lengths[data.Length] = Lengths.GetValueOrDefault(data.Length) + 1;
            MaxLength = Math.Max(MaxLength, data.Length);

            if (_previous is not null && _previous.Length == data.Length)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (_previous[i] != data[i])
                    {
                        _changes[i] = _changes.GetValueOrDefault(i) + 1;
                    }
                }
            }
            _previous = data.ToArray();
        }

        public void Print(string title)
        {
            Console.WriteLine($"  {title}: {_total} reports ({AudioFrames} mic audio frames)");
            Console.WriteLine("    IDs:     " + string.Join(", ", Ids.Select(p => $"0x{p.Key:X2} x{p.Value}")));
            Console.WriteLine("    Lengths: " + string.Join(", ", Lengths.Select(p => $"{p.Key}B x{p.Value}")));
            var busy = _changes.Where(p => p.Value > _total / 4).Select(p => p.Key).OrderBy(i => i);
            Console.WriteLine("    Frequently changing bytes: " + string.Join(" ", busy));
            if (_previous is not null)
            {
                Console.WriteLine("    Last report: " + Convert.ToHexString(_previous));
            }
        }
    }
}
