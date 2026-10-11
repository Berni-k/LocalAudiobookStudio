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
/// A kept fused generation (<see cref="FusionRecord"/>): the worker's whole output with where its takes were cut, what
/// it was sent, and what the transcription heard, highlighted as it plays.
/// </summary>
public sealed partial class FusionRecordViewModel : ObservableObject, IWaveformHost, IDisposable
{
    private readonly MainWindowViewModel _main;
    private readonly string _folder;
    private IDisposable? _follow;

    public FusionRecordViewModel(MainWindowViewModel main, ProjectSession session, string recordId, Guid? itemId)
    {
        _main = main;
        _folder = session.Paths.FusionRecordDir(recordId);
        AudioPath = Path.Combine(_folder, FusionRecord.AudioFile);
        Record = CoreJson.Deserialize<FusionRecord>(File.ReadAllText(Path.Combine(_folder, FusionRecord.RecordFile)));

        string Name(string speaker) => session.Project.FindSpeaker(speaker)?.DisplayName is { Length: > 0 } n ? n : speaker;
        Parts = Record.Parts.Select((p, i) => new FusionPartRow(this, i + 1, p, Name(p.Speaker), p.ItemId != null && p.ItemId == itemId)).ToList();
        Markers = Parts.Where(p => p.Range != null)
            .Select(p => new FusionMarker(p.Range!.Value.Start, p.Range.Value.End, $"{p.Number} {p.SpeakerName}", p.IsSegment, p.IsCurrent))
            .ToList();

        var r = Record;
        Summary = string.Join(" · ", new[]
        {
            $"{r.Worker}{(r.Runtime != null ? $" ({r.Runtime})" : "")} {r.ModelVersion}",
            $"seed {r.Seed?.ToString() ?? "–"}",
            r.Device,
            r.GenerationMs > 0 ? $"{r.GenerationMs / 1000.0:0.0} s" : null,
            r.Split == TakeFusion.Transcript ? $"cut by transcription ({r.Transcriber})" : "cut where the worker said",
            r.CreatedUtc.ToLocalTime().ToString("g"),
        }.Where(s => !string.IsNullOrEmpty(s)));
        SettingsText = r.Settings.Count == 0 ? "" : "Shared settings: " + r.Settings.ToJsonString();

        // The transcript shows against all the parts' text, one line each, as it was compared when cutting.
        if (r.Transcript is { } words)
        {
            TranscriptSource = string.Join("\n", r.Parts.Select(p => p.Text));
            TranscriptCheck = TranscriptCheck.Compare(TranscriptSource, words);
        }
        LoadPeaks();
    }

    public FusionRecord Record { get; }
    public string AudioPath { get; }
    public string Title => $"Fused generation inspector – {Record.Id}";
    public string Summary { get; }
    public string SettingsText { get; }
    public bool HasSettings => SettingsText.Length > 0;
    public IReadOnlyList<FusionPartRow> Parts { get; }
    public IReadOnlyList<FusionMarker> Markers { get; }

    /// <summary>For a worker that doesn't fuse: the one text it was sent.</summary>
    public string? RequestText => Record.RequestText;
    public bool HasRequestText => Record.RequestText != null;
    public string? Error => Record.Error;
    public bool HasError => Record.Error != null;

    public string? TranscriptSource { get; }
    public TranscriptCheck? TranscriptCheck { get; }
    public bool HasTranscript => TranscriptCheck != null;
    public string NoTranscriptText => "Not transcribed: the worker said where each part is.";

    [ObservableProperty] private Peaks? _peaks;
    [ObservableProperty] private double _durationMs;

    /// <summary>Where playback is in the file, in ms; -1 when it isn't playing.</summary>
    [ObservableProperty] private double _playheadMs = -1;
    [ObservableProperty] private bool _isPlaying;

    private async void LoadPeaks()
    {
        try
        {
            Peaks = await Task.Run(() => Peaks.FromWav(AudioPath));
            DurationMs = Peaks.DurationMs;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
        {
            _main.StatusText = "Cannot read the fused generation's audio: " + e.Message;
        }
    }

    [RelayCommand]
    private Task PlayFromStart() => Play(0);

    public async Task Play(double fromMs)
    {
        await _main.PlayFileAsync(AudioPath, fromMs);
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
public sealed partial class FusionPartRow(FusionRecordViewModel owner, int number, FusionRecordPart part, string speakerName, bool isCurrent)
{
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
