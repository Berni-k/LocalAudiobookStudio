using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Controls;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Core.Transcripts;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// The generation inspector: a waveform with what the transcription heard, highlighted as it plays, differences marked.
/// Opened for a kept fused generation (<see cref="FusionRecord"/>: the worker's whole output with where its takes were
/// cut and what it was sent) or for one take with a transcript (its audio and the segment's text).
/// </summary>
public sealed partial class GenerationInspectorViewModel : ObservableObject, IWaveformHost, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly string _folder;
    private IDisposable? _follow;

    /// <summary>A kept fused generation, with <paramref name="itemId"/>'s part highlighted.</summary>
    public GenerationInspectorViewModel(MainWindowViewModel main, ProjectSession session, string recordId, Guid? itemId)
    {
        _main = main;
        _folder = session.Paths.FusionRecordDir(recordId);
        AudioPath = Path.Combine(_folder, FusionRecord.AudioFile);
        var r = Record = CoreJson.Deserialize<FusionRecord>(File.ReadAllText(Path.Combine(_folder, FusionRecord.RecordFile)));
        Title = $"Generation inspector – {r.Id}";

        string Name(string speaker) => session.Project.FindSpeaker(speaker)?.DisplayName is { Length: > 0 } n ? n : speaker;
        Blocks = r.TranscriptBlocks is { Count: > 0 } heardBlocks ? heardBlocks : null;
        Parts = Record.Parts.Select((p, i) => new FusionPartRow(this, i + 1, p, Name(p.Speaker), p.ItemId != null && p.ItemId == itemId,
            VoicesIn(r.Transcript, p))).ToList();
        VoiceNote = VoiceConsistency(Parts);
        Markers = Parts.Where(p => p.Range != null)
            .Select(p => new FusionMarker(p.Range!.Value.Start, p.Range.Value.End, $"{p.Number} {p.SpeakerName}", p.IsSegment, p.IsCurrent))
            .ToList();

        Summary = string.Join(" · ", new[]
        {
            $"{r.Worker}{(r.Runtime != null ? $" ({r.Runtime})" : "")} {r.ModelVersion}",
            $"seed {r.Seed?.ToString() ?? "–"}",
            r.Device,
            r.GenerationMs > 0 ? $"{r.GenerationMs / 1000.0:0.0} s" : null,
            r.Split == TakeFusion.Transcript ? $"cut by transcription ({r.Transcriber})" : "cut where the worker said",
            VoicesText(Blocks),
            r.CreatedUtc.ToLocalTime().ToString("g"),
        }.Where(s => !string.IsNullOrEmpty(s)));
        SettingsText = r.Settings.Count == 0 ? "" : "Shared settings: " + r.Settings.ToJsonString();
        NoTranscriptText = "Not transcribed: the worker said where each part is.";

        // The transcript shows against all the parts' text, one line each, as it was compared when cutting.
        if (r.Transcript is { } words)
        {
            TranscriptSource = string.Join("\n", r.Parts.Select(p => p.Text));
            Compare(session.Engine.SpokenText(r.Worker, TranscriptSource), words);
        }
        _ = LoadPeaksAsync(() => Peaks.FromWav(AudioPath));
    }

    /// <summary>One take that has a transcript: its audio against the segment's text.</summary>
    public GenerationInspectorViewModel(MainWindowViewModel main, ProjectSession session, ItemViewModel item, TakeInfo take)
    {
        _main = main;
        _folder = session.Paths.SegmentAudioDir(item.Id);
        AudioPath = session.Takes.TakeAudio(item.Id, take.Take);
        Title = $"Generation inspector – take {take.Take}";

        Summary = string.Join(" · ", new[]
        {
            $"{take.Worker}{(take.Runtime != null ? $" ({take.Runtime})" : "")} {take.ModelVersion}",
            $"seed {take.Seed?.ToString() ?? "–"}",
            take.Gpu,
            take.GenerationMs > 0 ? $"{take.GenerationMs / 1000.0:0.0} s" : null,
            take.CreatedUtc.ToLocalTime().ToString("g"),
        }.Where(s => !string.IsNullOrEmpty(s)));
        NoTranscriptText = "Not transcribed.";

        if (StoredTranscript.TryLoad(session.Paths.TakeTranscript(item.Id, take.Take)) is { } stored)
        {
            Blocks = stored.Blocks is { Count: > 0 } storedBlocks ? storedBlocks : null;
            Summary += $" · heard by {stored.Worker}" + (VoicesText(Blocks) is { } voices ? " · " + voices : "");
            TranscriptSource = item.Model.Text ?? "";
            Compare(session.Engine.SpokenText(item.Model, take.Take), stored.Words);
        }
        var peaksPath = session.Takes.TakePeaks(item.Id, take.Take);
        _ = LoadPeaksAsync(async () => await session.PeaksCache.LoadAsync(peaksPath, AudioPath));
    }

    /// <summary>
    /// Whether <paramref name="take"/> has anything to inspect: a kept fused generation or a transcript.
    /// </summary>
    public static bool CanInspect(ProjectSession session, Guid itemId, TakeInfo take) =>
        take.Fusion?.Record != null || File.Exists(session.Paths.TakeTranscript(itemId, take.Take));

    private void Compare(string spokenText, IReadOnlyList<HeardWord> words)
    {
        Words = words;
        TranscriptCheck = TranscriptCheck.Compare(spokenText, words);
        // A heard word is wrong when a difference that was heard (another word, an extra one) falls on its time.
        var errors = TranscriptCheck.Words.Where(c => c.IsError && c.Heard != null).ToList();
        WrongWords = words.Where(w => errors.Any(c => c.StartMs < w.EndMs && c.EndMs > w.StartMs || c.StartMs == w.StartMs)).ToHashSet();
    }

    /// <summary>
    /// The stretches of speech by one voice the transcription heard (drawn over the waveform), when the worker tells voices
    /// apart; null from any other. The heard words point into them.
    /// </summary>
    public IReadOnlyList<HeardBlock>? Blocks { get; }
    public bool HasBlocks => Blocks != null;

    /// <summary>"voices heard: S01, S02", or null without blocks.</summary>
    private static string? VoicesText(IReadOnlyList<HeardBlock>? blocks) =>
        blocks?.Select(b => b.Speaker).Where(s => s != null).Distinct().ToList() is { Count: > 0 } voices
            ? "voices heard: " + string.Join(", ", voices) : null;

    /// <summary>
    /// The voices the transcription gave the words inside a part's cut (or, uncut, where the worker said it is), the one
    /// with most words first. Empty when the transcript has no voices or the part has no place in the file.
    /// </summary>
    private static IReadOnlyList<string> VoicesIn(IReadOnlyList<HeardWord>? words, FusionRecordPart part)
    {
        var (from, to) = part is { CutStartMs: { } cs, CutEndMs: { } ce } ? (cs, ce)
            : part is { SpanStartMs: { } ss, SpanEndMs: { } se } ? (ss, se) : (0, 0);
        if (words == null || to <= from) return [];
        return words.Where(w => w.Speaker != null && (w.StartMs + w.EndMs) / 2 >= from && (w.StartMs + w.EndMs) / 2 < to)
            .GroupBy(w => w.Speaker!).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
    }

    /// <summary>
    /// Whether the transcription's voices agree with the project's speakers: one speaker's parts should be one voice and
    /// two speakers' parts two voices. The labels are the worker's own, so this only says whether they are consistent.
    /// Null when they are, or when no voices were heard.
    /// </summary>
    private static string? VoiceConsistency(IReadOnlyList<FusionPartRow> parts)
    {
        var heard = parts.Where(p => p.IsSegment && p.Voices.Count > 0).Select(p => (p.SpeakerName, Voice: p.Voices[0])).ToList();
        var notes = new List<string>();
        foreach (var g in heard.GroupBy(h => h.SpeakerName).Where(g => g.Select(h => h.Voice).Distinct().Count() > 1))
            notes.Add($"{g.Key} was heard as {string.Join(" and ", g.Select(h => h.Voice).Distinct())}.");
        foreach (var g in heard.GroupBy(h => h.Voice).Where(g => g.Select(h => h.SpeakerName).Distinct().Count() > 1))
            notes.Add($"{g.Key} was heard for {string.Join(" and ", g.Select(h => h.SpeakerName).Distinct())}.");
        return notes.Count == 0 ? null : "The voices heard don't match the speakers: " + string.Join(" ", notes);
    }

    /// <summary>Set when the voices the transcription heard don't line up with the project's speakers (see <see cref="VoiceConsistency"/>).</summary>
    public string? VoiceNote { get; }
    public bool HasVoiceNote => VoiceNote != null;

    /// <summary>The kept fused generation; null when this shows a single take.</summary>
    public FusionRecord? Record { get; }
    public string AudioPath { get; }
    public string Title { get; }
    public string Summary { get; }
    public string SettingsText { get; } = "";
    public bool HasSettings => SettingsText.Length > 0;

    /// <summary>The parts the worker was sent (fused generations only; a single take has none to show).</summary>
    public IReadOnlyList<FusionPartRow> Parts { get; } = [];
    public bool HasParts => Record != null;
    public IReadOnlyList<FusionMarker> Markers { get; } = [];

    /// <summary>For a worker that doesn't fuse: the one text it was sent.</summary>
    public string? RequestText => Record?.RequestText;
    public bool HasRequestText => RequestText != null;
    public string? Error => Record?.Error;
    public bool HasError => Error != null;

    public string? TranscriptSource { get; }
    public TranscriptCheck? TranscriptCheck { get; private set; }

    /// <summary>The heard words with their times, shown on the waveform; null when nothing was transcribed.</summary>
    public IReadOnlyList<HeardWord>? Words { get; private set; }

    /// <summary>The heard words that don't match the text (their bars are red).</summary>
    public IReadOnlySet<HeardWord>? WrongWords { get; private set; }
    public bool HasTranscript => TranscriptCheck != null;
    public string NoTranscriptText { get; }

    [ObservableProperty] private Peaks? _peaks;
    [ObservableProperty] private double _durationMs;

    /// <summary>Where playback is in the file, in ms; -1 when it isn't playing.</summary>
    [ObservableProperty] private double _playheadMs = -1;
    [ObservableProperty] private bool _isPlaying;

    private async Task LoadPeaksAsync(Func<Peaks?> load) => await LoadPeaksAsync(() => Task.Run(load));

    private async Task LoadPeaksAsync(Func<Task<Peaks?>> load)
    {
        try
        {
            Peaks = await load();
            DurationMs = Peaks?.DurationMs ?? 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
        {
            _main.StatusText = "Cannot read the generation's audio: " + e.Message;
        }
    }

    [RelayCommand]
    private Task PlayFromStart() => Play(0);

    /// <summary>Plays one heard word (a bar under the waveform), to judge its timing.</summary>
    [RelayCommand]
    private Task PlayWord(HeardWord word) => Play(word.StartMs, word.EndMs);

    public async Task Play(double fromMs, double? toMs = null)
    {
        await _main.PlayFileAsync(AudioPath, fromMs, toMs);
        IsPlaying = _main.FilePositionMs != null;
        if (IsPlaying) Follow();
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsPlaying) _main.StopPlayback();
        IsPlaying = false;
        PlayheadMs = -1;
    }

    [RelayCommand]
    private void OpenFolder() => Dialogs.Reveal(AudioPath);

    /// <summary>Follows the playback position, for the playhead and the transcript's highlight, until it stops.</summary>
    private void Follow()
    {
        _follow?.Dispose();
        _follow = DispatcherTimer.Run(() =>
        {
            if (_main.FilePositionMs is double p && IsPlaying)
            {
                PlayheadMs = p;
                return true;
            }
            IsPlaying = false;
            PlayheadMs = -1;
            return false;
        }, TimeSpan.FromMilliseconds(33));
    }

    // The waveform works as a segment row's does (through the main window's playback): a click plays from there and a
    // drag scrubs. No trims here.
    public void WaveformSeek(double relativeMs) => _ = Play(relativeMs);

    public void WaveformScrub(double relativeMs)
    {
        _follow?.Dispose();
        IsPlaying = false;
        _main.ScrubFile(AudioPath, relativeMs);
        PlayheadMs = relativeMs;
    }

    public void WaveformScrubEnd() => _main.ScrubFileEnd();

    public void WaveformTrim(double trimStartMs, double trimEndMs) { }

    public void Dispose()
    {
        _follow?.Dispose();
        if (IsPlaying) _main.StopPlayback();
    }
}

/// <summary>One part of a kept fused generation, as the worker was sent it, and what became of it.</summary>
public sealed partial class FusionPartRow(GenerationInspectorViewModel owner, int number, FusionRecordPart part, string speakerName, bool isCurrent,
    IReadOnlyList<string> voices)
{
    /// <summary>The voices the transcription heard in this part, the main one first (empty when it doesn't tell voices apart).</summary>
    public IReadOnlyList<string> Voices { get; } = voices;
    public string? HeardAs => Voices.Count == 0 ? null
        : Voices.Count == 1 ? "heard as " + Voices[0] : "heard as " + Voices[0] + ", and also " + string.Join(", ", Voices.Skip(1));
    public bool HasHeardAs => HeardAs != null;

    public int Number { get; } = number;
    public FusionRecordPart Part { get; } = part;
    public string SpeakerName { get; } = speakerName;
    public bool IsCurrent { get; } = isCurrent;
    public bool IsSegment => Part.Role != Workers.Protocol.PartRoles.Context;
    public string RoleText => IsSegment ? "Segment" : Part.Audio != null ? "Context (heard)" : "Context";
    public string Text => Part.Text;

    public string? VoiceText => Part.Voice is { Length: > 0 } v ? "Voice: " + (Path.IsPathRooted(v) ? Path.GetFileName(v) : v) : null;
    public string? SettingsText => Part.Settings is { Count: > 0 } s ? s.ToJsonString() : null;
    public string? AudioText => Part.Audio != null ? "Heard from " + Part.Audio : null;
    public string Details => string.Join(" · ", new[] { VoiceText, SettingsText, AudioText }.Where(s => s != null));
    public bool HasDetails => Details.Length > 0;

    /// <summary>Where it is in the file: its cut for a segment, else where the worker said it is.</summary>
    public (double Start, double End)? Range =>
        Part is { CutStartMs: { } cs, CutEndMs: { } ce } ? (cs, ce)
        : Part is { SpanStartMs: { } ss, SpanEndMs: { } se } ? (ss, se)
        : null;

    public string Timing => string.Join(" · ", new[]
    {
        Part is { SpanStartMs: { } ss, SpanEndMs: { } se } ? $"worker said {Ms(ss)}–{Ms(se)}" : null,
        Part is { CutStartMs: { } cs, CutEndMs: { } ce } ? $"cut {Ms(cs)}–{Ms(ce)}" : null,
        Part.Take is int t ? $"take {t}" : null,
    }.Where(s => s != null));
    public bool HasTiming => Timing.Length > 0;

    public string? Note => Part.Note;
    public bool HasNote => Part.Note != null;
    public bool CanPlay => Range != null;

    private static string Ms(int ms) => TimeFormat.Short(ms);

    [RelayCommand]
    private Task Play() => Range is { } r ? owner.Play(r.Start) : Task.CompletedTask;
}
