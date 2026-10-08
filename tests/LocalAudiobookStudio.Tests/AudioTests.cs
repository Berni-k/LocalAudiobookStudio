using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Engine.Export;
using NAudio.Wave;

namespace LocalAudiobookStudio.Tests;

public class AudioTests
{
    private static float[] Sine(double seconds, int rate, double amp, double freq = 997)
    {
        var n = (int)(seconds * rate);
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
        return s;
    }

    private static string WriteWav(string path, float[] mono, int rate)
    {
        using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        w.WriteSamples(mono, 0, mono.Length);
        return path;
    }

    [Fact]
    public void FullScaleMonoSineIsAboutMinus3Lufs()
    {
        var buf = new DecodedAudio(Sine(3, 48000, 1.0), 48000, 1);
        var lufs = LoudnessMeter.IntegratedLoudness(buf)!.Value;
        Assert.InRange(lufs, -3.2, -2.8);
    }

    [Fact]
    public void LoudnessWorksAtOtherRates()
    {
        var at48 = LoudnessMeter.IntegratedLoudness(new DecodedAudio(Sine(3, 48000, 0.1), 48000, 1))!.Value;
        var at24 = LoudnessMeter.IntegratedLoudness(new DecodedAudio(Sine(3, 24000, 0.1), 24000, 1))!.Value;
        Assert.InRange(at24 - at48, -0.2, 0.2);
        Assert.InRange(at48, -23.2, -22.8);
    }

    [Fact]
    public void SilenceIsNullAndShortClipsFallBackToRms()
    {
        Assert.Null(LoudnessMeter.IntegratedLoudness(new DecodedAudio(new float[48000], 48000, 1)));
        Assert.NotNull(LoudnessMeter.IntegratedLoudness(new DecodedAudio(Sine(0.2, 48000, 0.5), 48000, 1)));
    }

    [Fact]
    public void AutoGainIsClamped()
    {
        Assert.Equal(20, LoudnessMeter.AutoGainDb(-18, -60));
        Assert.Equal(-3.4, LoudnessMeter.AutoGainDb(-18, -14.6), 6);
        Assert.Equal(0, LoudnessMeter.AutoGainDb(-18, null));
    }

    [Fact]
    public void DetectsLeadingAndTrailingSilence()
    {
        var rate = 24000;
        var s = new float[rate]; // 1 s
        for (int i = rate / 4; i < rate * 3 / 4; i++) s[i] = 0.5f;
        var (lead, trail) = TakeAnalyzer.DetectSilence(s, rate, new SilenceTrim(-60, 40));
        Assert.InRange(lead, 250 - 40 - 1, 250 - 40 + 1);
        Assert.InRange(trail, 250 - 40 - 1, 250 - 40 + 1);

        // A soft tail (-55 dBFS) is sound at a -60 dB threshold but silence at -50 dB.
        for (int i = rate * 3 / 4; i < rate * 7 / 8; i++) s[i] = 0.0018f;
        Assert.InRange(TakeAnalyzer.DetectSilence(s, rate, new SilenceTrim(-60, 0)).trailMs, 125 - 1, 125 + 1);
        Assert.InRange(TakeAnalyzer.DetectSilence(s, rate, new SilenceTrim(-50, 0)).trailMs, 250 - 1, 250 + 1);

        var shortGap = new float[rate];
        for (int i = rate / 100; i < rate; i++) shortGap[i] = 0.5f; // 10 ms lead < 30 ms minimum
        Assert.Equal(0, TakeAnalyzer.DetectSilence(shortGap, rate).leadMs);
    }

    [Fact]
    public void PeaksRoundTripAndColumns()
    {
        using var tmp = new TempDir();
        var p = Peaks.FromSamples(Sine(1, 24000, 0.5), 24000);
        var file = Path.Combine(tmp.Path, "a.peaks");
        p.Save(file);
        var q = Peaks.Load(file);
        Assert.Equal(p.BinCount, q.BinCount);
        Assert.Equal(1000, q.DurationMs, 3);
        var mins = new float[100];
        var maxs = new float[100];
        q.GetColumns(0, 1000, mins, maxs);
        Assert.All(maxs, m => Assert.InRange(m, 0.45f, 0.51f));
        Assert.All(mins, m => Assert.InRange(m, -0.51f, -0.45f));
    }

    [Fact]
    public void MixerOverlapsWithoutClippingAndAppliesTrim()
    {
        using var tmp = new TempDir();
        var rate = 44100;
        var a = WriteWav(Path.Combine(tmp.Path, "a.wav"), Sine(1, rate, 0.9), rate);
        var b = WriteWav(Path.Combine(tmp.Path, "b.wav"), Sine(1, rate, 0.9), rate);
        var cache = new AudioBufferCache(rate);
        // b starts 500 ms into a: the overlap sums to ~1.8 before the limiter.
        var plan = new MixPlan([
            new MixItem(Guid.NewGuid(), 0, 1000, a, 0, 0, 0, 0),
            new MixItem(Guid.NewGuid(), 500, 500, b, 500, 0, 0, 0), // trimmed: plays b's second half
        ], 1000);
        var mixer = new TimelineMixer(plan, cache);
        var outp = new float[rate * 2];
        var n = mixer.Read(outp);
        Assert.Equal(rate * 2, n);
        var ceiling = (float)Math.Pow(10, -1 / 20.0);
        Assert.All(outp, v => Assert.True(Math.Abs(v) <= ceiling + 1e-4f));
        var peakSecondHalf = outp.Skip(rate + 2000).Max(Math.Abs);
        var peakFirstHalf = outp.Take(rate / 2).Max(Math.Abs);
        Assert.True(peakSecondHalf > 0.8f); // limited, but clearly both playing
        Assert.True(peakFirstHalf > 0.85f);
        Assert.Equal(0, mixer.Read(outp)); // end of timeline
    }

    [Fact]
    public void MixerSeekIsInstantAndGainApplies()
    {
        using var tmp = new TempDir();
        var rate = 44100;
        var a = WriteWav(Path.Combine(tmp.Path, "a.wav"), Sine(2, rate, 0.5), rate);
        var plan = new MixPlan([new MixItem(Guid.NewGuid(), 1000, 2000, a, 0, -6.0206, 0, 0)], 3000);
        var mixer = new TimelineMixer(plan, new AudioBufferCache(rate));
        mixer.Seek(1500);
        var outp = new float[4410 * 2];
        mixer.Read(outp);
        Assert.InRange(outp.Max(), 0.24f, 0.26f);
        mixer.Seek(0);
        mixer.Read(outp);
        Assert.All(outp, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ResamplesToProjectRate()
    {
        using var tmp = new TempDir();
        var a = WriteWav(Path.Combine(tmp.Path, "a.wav"), Sine(1, 24000, 0.5), 24000);
        var buf = AudioDecoder.DecodeStereo(a, 44100);
        Assert.Equal(2, buf.Channels);
        Assert.InRange(buf.Frames, 44000, 44200);
    }

    [Fact]
    public void FfMetadataHasChapters()
    {
        var meta = AudiobookExporter.BuildFfMetadata(new ExportOptions { Title = "Book; One", Author = "Me" },
            [new ExportChapter("One", 0, 1500), new ExportChapter("Two", 2000, 5000)]);
        Assert.Contains("title=Book\\; One", meta);
        Assert.Contains("START=1500\nEND=4500\ntitle=Two", meta);
    }

    [Fact]
    public async Task PackAndUnpackRoundTrip()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "Book");
        var p = ProjectStore.Create(root, "Book");
        var id = Guid.NewGuid();
        Directory.CreateDirectory(p.Paths.SegmentAudioDir(id));
        File.WriteAllText(p.Paths.TakeWav(id, 1), "one");
        File.WriteAllText(p.Paths.TakeWav(id, 2), "two");
        Directory.CreateDirectory(p.Paths.ExportDir);
        File.WriteAllText(Path.Combine(p.Paths.ExportDir, "book.m4b"), "x");

        var pkg = Path.Combine(tmp.Path, "book.abproj");
        await ProjectPackager.PackAsync(root, pkg, new PackOptions { SelectedTakesOnly = true },
            new Dictionary<Guid, int?> { [id] = 2 }, null, CancellationToken.None);

        var dest = Path.Combine(tmp.Path, "Unpacked");
        await ProjectPackager.UnpackAsync(pkg, dest, null, CancellationToken.None);
        var q = ProjectStore.Load(dest);
        Assert.Equal("Book", q.File.Title);
        Assert.False(File.Exists(q.Paths.TakeWav(id, 1)));
        Assert.Equal("two", File.ReadAllText(q.Paths.TakeWav(id, 2)));
        Assert.False(Directory.Exists(q.Paths.ExportDir));

        using var zip = System.IO.Compression.ZipFile.OpenRead(pkg);
        var wav = zip.Entries.First(e => e.FullName.EndsWith(".wav"));
        Assert.Equal(wav.Length, wav.CompressedLength); // stored, not deflated
    }
}
