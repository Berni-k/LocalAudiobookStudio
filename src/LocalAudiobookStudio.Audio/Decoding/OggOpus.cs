using System.Buffers.Binary;
using System.Text;
using Concentus;
using Concentus.Enums;

namespace LocalAudiobookStudio.Audio.Decoding;

/// <summary>
/// Ogg Opus files (RFC 7845) with the Concentus codec. The container is written and read here rather than with
/// Concentus.OggFile, which writes a pre-skip of 0 and doesn't trim the last frame: takes would come back about
/// 6.5 ms late and up to 20 ms long. Here the pre-skip is the encoder's lookahead and the last granule position
/// is the exact length, and reading applies both, so a take decodes to exactly its samples (also for files made
/// by opusenc or ffmpeg). Opus always runs at 48 kHz; other rates are resampled on the way in.
/// </summary>
public static class OggOpus
{
    public const string Extension = ".opus";
    private const int Rate = 48000;
    private const int FrameSize = 960; // 20 ms
    private const int MaxFrameSize = 5760; // 120 ms, the longest Opus packet

    /// <summary>Default bit rate per channel: 64 kbps mono speech, 128 kbps stereo music. Transparent enough for audiobooks.</summary>
    public const int DefaultBitratePerChannel = 64000;

    static OggOpus()
    {
        // The same managed codec on every machine, rather than whichever libopus happens to be installed.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    // ================================================================== writing

    /// <summary>Encodes audio (mono or stereo; more channels keep the first two) to an Ogg Opus file.</summary>
    /// <param name="bitratePerChannel">Bits per second for each channel (stereo gets twice).</param>
    public static void Write(DecodedAudio audio, string path, int bitratePerChannel = DefaultBitratePerChannel)
    {
        var channels = audio.Channels == 1 ? 1 : 2;
        var input = audio.Channels is 1 or 2 ? audio : AudioDecoder.ToStereo(audio);
        if (input.SampleRate != Rate) input = AudioDecoder.Resample(input, Rate);
        var frames = input.Frames;

        var encoder = OpusCodecFactory.CreateEncoder(Rate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = bitratePerChannel * channels;
        var preSkip = encoder.Lookahead;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var ogg = new OggWriter(file);
        ogg.WritePage([OpusHead(channels, preSkip, audio.SampleRate)], granule: 0, bos: true, eos: false);
        ogg.WritePage([OpusTags()], granule: 0, bos: false, eos: false);

        // The decoder output runs preSkip samples behind the input: encode enough (silent) frames to get all of it out.
        var total = preSkip + frames;
        var frameCount = (total + FrameSize - 1) / FrameSize;
        var pcm = new float[FrameSize * channels];
        var packet = new byte[4000];
        var page = new List<byte[]>();
        for (var f = 0; f < frameCount; f++)
        {
            Array.Clear(pcm);
            var start = (long)f * FrameSize;
            var n = (int)Math.Clamp(frames - start, 0, FrameSize);
            if (n > 0) input.Samples.AsSpan((int)(start * channels), n * channels).CopyTo(pcm);
            var len = encoder.Encode(pcm, FrameSize, packet, packet.Length);
            page.Add(packet[..len]);
            var last = f == frameCount - 1;
            // About one second per page; the last page's granule position is the exact end.
            if (page.Count == 50 || last)
            {
                var granule = last ? total : (long)(f + 1) * FrameSize;
                ogg.WritePage(page, granule, bos: false, eos: last);
                page.Clear();
            }
        }
    }

    private static byte[] OpusHead(int channels, int preSkip, int inputRate)
    {
        var head = new byte[19];
        Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
        head[8] = 1; // version
        head[9] = (byte)channels;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), (uint)inputRate);
        // Output gain 0, channel mapping family 0 (mono or stereo).
        return head;
    }

    private static byte[] OpusTags()
    {
        var vendor = Encoding.UTF8.GetBytes("Local Audiobook Studio (Concentus)");
        var tags = new byte[8 + 4 + vendor.Length + 4];
        Encoding.ASCII.GetBytes("OpusTags").CopyTo(tags, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), (uint)vendor.Length);
        vendor.CopyTo(tags, 12);
        return tags; // no user comments
    }

    // ================================================================== reading

    /// <summary>Decodes an Ogg Opus file at 48 kHz, its pre-skip removed and cut to its last granule position.</summary>
    public static DecodedAudio Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new OggReader(file);

        var head = reader.NextPacket() ?? throw new InvalidDataException("Not an Ogg Opus file: it is empty.");
        if (head.Length < 19 || Encoding.ASCII.GetString(head, 0, 8) != "OpusHead")
            throw new InvalidDataException("Not an Ogg Opus file (no OpusHead).");
        var channels = head[9];
        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10));
        var gainDb = BinaryPrimitives.ReadInt16LittleEndian(head.AsSpan(16)) / 256.0;
        if (head[18] != 0 || channels is < 1 or > 2)
            throw new InvalidDataException($"Opus files with {channels} channels (mapping family {head[18]}) aren't supported.");
        reader.NextPacket(); // OpusTags

        var decoder = OpusCodecFactory.CreateDecoder(Rate, channels);
        var output = new List<float>(Rate * channels * 10);
        var buffer = new float[MaxFrameSize * channels];
        while (reader.NextPacket() is { } packet)
        {
            var n = decoder.Decode(packet, buffer, MaxFrameSize, false);
            output.AddRange(buffer.AsSpan(0, n * channels));
        }

        var decodedFrames = output.Count / channels;
        var end = reader.LastGranule is long g and >= 0 ? (int)Math.Min(decodedFrames, g) : decodedFrames;
        var frames = Math.Max(0, end - preSkip);
        var samples = new float[frames * channels];
        output.CopyTo(preSkip * channels, samples, 0, samples.Length);
        if (gainDb != 0)
        {
            var gain = (float)Math.Pow(10, gainDb / 20);
            for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
        }
        return new DecodedAudio(samples, Rate, channels);
    }

    // ================================================================== Ogg pages

    /// <summary>Writes pages of one logical stream; each page holds whole packets.</summary>
    private sealed class OggWriter(Stream stream)
    {
        private readonly int _serial = Random.Shared.Next();
        private int _sequence;

        public void WritePage(IReadOnlyList<byte[]> packets, long granule, bool bos, bool eos)
        {
            var lacing = new List<byte>();
            foreach (var p in packets)
            {
                for (var left = p.Length; ; left -= 255)
                {
                    lacing.Add((byte)Math.Min(left, 255));
                    if (left < 255) break;
                }
            }
            if (lacing.Count > 255) throw new InvalidOperationException("Too many packets for one Ogg page.");

            var body = packets.Sum(p => p.Length);
            var page = new byte[27 + lacing.Count + body];
            Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
            page[5] = (byte)((bos ? 0x02 : 0) | (eos ? 0x04 : 0));
            BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(14), _serial);
            BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(18), _sequence++);
            page[26] = (byte)lacing.Count;
            lacing.CopyTo(page, 27);
            var at = 27 + lacing.Count;
            foreach (var p in packets)
            {
                p.CopyTo(page, at);
                at += p.Length;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc(page));
            stream.Write(page);
        }
    }

    /// <summary>Reads the packets of the first logical stream; remembers the last granule position seen.</summary>
    private sealed class OggReader(Stream stream)
    {
        private readonly Queue<byte[]> _ready = new();
        private readonly List<byte> _partial = [];
        private int? _serial;
        private bool _ended;

        public long? LastGranule { get; private set; }

        public byte[]? NextPacket()
        {
            while (_ready.Count == 0 && !_ended) ReadPage();
            return _ready.Count > 0 ? _ready.Dequeue() : null;
        }

        private void ReadPage()
        {
            var header = new byte[27];
            if (!ReadExactly(header))
            {
                _ended = true;
                return;
            }
            if (Encoding.ASCII.GetString(header, 0, 4) != "OggS") throw new InvalidDataException("Damaged Ogg file (no page header).");
            var granule = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(6));
            var serial = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(14));
            var lacing = new byte[header[26]];
            if (!ReadExactly(lacing)) throw new InvalidDataException("Damaged Ogg file (cut off).");
            var body = new byte[lacing.Sum(b => b)];
            if (!ReadExactly(body)) throw new InvalidDataException("Damaged Ogg file (cut off).");

            _serial ??= serial;
            if (serial != _serial) return; // another logical stream: not ours
            if (granule != -1) LastGranule = granule;
            var at = 0;
            foreach (var len in lacing)
            {
                _partial.AddRange(body.AsSpan(at, len));
                at += len;
                if (len < 255)
                {
                    _ready.Enqueue([.. _partial]);
                    _partial.Clear();
                }
            }
            if ((header[5] & 0x04) != 0) _ended = true;
        }

        private bool ReadExactly(byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var j = 0; j < 8; j++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }

    /// <summary>Ogg's CRC-32 (polynomial 0x04C11DB7, no reflection), over the page with its checksum field zero.</summary>
    private static uint Crc(byte[] page)
    {
        uint crc = 0;
        foreach (var b in page) crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b];
        return crc;
    }
}
