using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Engine;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>Generate audio: a text (speech, a sound or music, by the worker) to a WAV file.</summary>
public sealed partial class OneShotGenerateViewModel(OneShotViewModel owner) : OneShotTabViewModel(owner, OneShotKind.Generate, "Generate audio")
{
    /// <summary>A pause between paragraphs when a long text is generated in pieces.</summary>
    private const int ParagraphGapMs = 500;

    [ObservableProperty] private string _text = "The quick brown fox jumps over the lazy dog.";
    [ObservableProperty] private string? _voiceFile;
    [ObservableProperty] private decimal? _length;
    [ObservableProperty] private string? _outputPath;

    protected override IEnumerable<WorkerManifest> Candidates() => AppHost.Catalog.Generators;
    public override string RequiredTags => "Voice, SFX or Music";

    public bool SupportsCloning => Worker?.VoiceCloning == true;
    public bool SupportsLength => Worker?.SupportsLength == true;
    public string LengthWatermark => Worker is { RequiresLength: true } ? "guessed from the text" : "auto";
    public bool HasOutput => OutputPath != null && File.Exists(OutputPath);

    protected override void WorkerSelected(WorkerManifest? worker)
    {
        OnPropertyChanged(nameof(SupportsCloning));
        OnPropertyChanged(nameof(SupportsLength));
        OnPropertyChanged(nameof(LengthWatermark));
    }

    partial void OnOutputPathChanged(string? value) => OnPropertyChanged(nameof(HasOutput));

    protected override async Task RunJobAsync(WorkerManifest worker)
    {
        if (string.IsNullOrWhiteSpace(Text)) { Status = "Type a text first."; return; }
        if (JobSettings(worker) is not { } settings) return;
        var seed = OneShot.SeedFrom(settings);
        var dir = Owner.ScratchFor("generate");
        OutputPath = null;

        var parts = OneShot.SplitForWorker(Text, worker.MaxChars)
            .SelectMany(p => p.Select((text, i) => (text, gap: i == p.Count - 1 ? ParagraphGapMs : 0))).ToList();
        var voice = SupportsCloning && !string.IsNullOrWhiteSpace(VoiceFile) ? VoiceFile : null;
        var length = SupportsLength && Length is decimal l ? Math.Round((double)l, 1) : (double?)null;
        var jobs = parts.Select((part, i) => OneShot.Job(worker, part.text, voice, settings, Path.Combine(dir, $"{i:D4}.wav"),
            parts.Count == 1 ? "One shot" : $"One shot {i + 1}/{parts.Count}", JobPriority.Normal, seed, length, tag: "one-shot", readingOrder: i)).ToList();

        var clock = Stopwatch.StartNew();
        await RunJobsAsync(jobs);
        if (FailureOf(jobs) is { } failure) { Status = failure; return; }

        var output = jobs[0].FixedOutPath!;
        if (jobs.Count > 1)
        {
            output = Path.Combine(dir, "result.wav");
            OneShot.ConcatenateWavs(jobs.Select(j => j.FixedOutPath!).ToList(), parts.Select(p => p.gap).ToList(), output);
        }
        OutputPath = output;
        var ms = jobs.Sum(j => j.Result?.DurationMs ?? 0) + parts.Take(parts.Count - 1).Sum(p => p.gap);
        Status = $"Done in {clock.Elapsed.TotalSeconds:0.0} s.";
        ResultText = $"{Time(ms)} of audio" + (jobs[0].Result?.Seed is long s ? $", seed {s}" : "") + $", {jobs[0].DeviceName}.";
        await Owner.PlayAsync(output);
    }

    [RelayCommand]
    private async Task BrowseVoice()
    {
        if (await Dialogs.OpenFile("Choose a reference clip", new FileFilter("Audio", "*.wav", "*.mp3", "*.opus", "*.flac", "*.ogg")) is { } file)
            VoiceFile = file;
    }

    [RelayCommand]
    private void ClearVoice() => VoiceFile = null;

    [RelayCommand]
    private async Task Play()
    {
        if (HasOutput) await Owner.PlayAsync(OutputPath!);
    }

    [RelayCommand]
    private void Stop() => Owner.StopPlayback();

    [RelayCommand]
    private async Task SaveAs()
    {
        if (!HasOutput) return;
        if (await Dialogs.SaveFile("Save audio", "one-shot.wav", ".wav", new FileFilter("WAV audio", "*.wav")) is { } target)
            File.Copy(OutputPath!, target, overwrite: true);
    }
}

/// <summary>What the two listening tabs share: an audio file in, a transcript out.</summary>
public abstract partial class OneShotListenViewModel(OneShotViewModel owner, OneShotKind kind, string title) : OneShotTabViewModel(owner, kind, title)
{
    [ObservableProperty] private string? _audioFile;
    [ObservableProperty] private string _text = "";

    /// <summary>The text the audio should contain: optional for transcribing (the result is compared with it), required for aligning.</summary>
    public abstract bool TextRequired { get; }

    protected abstract ListenMode ModeOf(WorkerManifest worker);

    protected override async Task RunJobAsync(WorkerManifest worker)
    {
        if (string.IsNullOrWhiteSpace(AudioFile) || !File.Exists(AudioFile)) { Status = "Choose an audio file first."; return; }
        if (TextRequired && string.IsNullOrWhiteSpace(Text)) { Status = "Type the text to time first."; return; }
        if (JobSettings(worker) is not { } settings) return;

        var dir = Owner.ScratchFor(Kind.ToString().ToLowerInvariant());
        var wav = Path.Combine(dir, "audio-16k.wav");
        Status = "Reading the audio…";
        try
        {
            await Task.Run(() => Transcription.PrepareAudio(AudioFile, wav));
        }
        catch (Exception ex)
        {
            Status = "Can't read the audio: " + ex.Message;
            return;
        }

        var expected = string.IsNullOrWhiteSpace(Text) ? null : Text;
        var job = Transcription.Job(worker, wav, expected, settings, "One shot", JobPriority.Normal, tag: "one-shot", mode: ModeOf(worker));
        var clock = Stopwatch.StartNew();
        await RunJobsAsync([job]);
        if (FailureOf([job]) is { } failure) { Status = failure; return; }

        var transcript = job.Transcript!;
        Status = $"Done in {clock.Elapsed.TotalSeconds:0.0} s ({job.DeviceName}).";
        ResultText = Describe(transcript, expected);
    }

    private static string Describe(TranscriptResult transcript, string? expected)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(transcript.Text)) sb.AppendLine(transcript.Text.Trim());
        else sb.AppendLine("(nothing heard)");
        if (transcript.Language is { Length: > 0 } language) sb.AppendLine().AppendLine($"Language: {language}");
        if (expected != null && transcript.Words.Count > 0)
        {
            var check = Transcription.Check(expected, transcript);
            sb.AppendLine().AppendLine(check.Errors == 0
                ? "Matches the text."
                : $"{check.Errors} difference(s) in {check.ExpectedCount} words ({check.WordErrorRate:P0}) against the text.");
        }
        if (transcript.Segments is { Count: > 0 } blocks)
        {
            sb.AppendLine().AppendLine("Blocks:");
            foreach (var b in blocks)
                sb.AppendLine($"  {Time(b.StartMs)} – {Time(b.EndMs)}  {(b.Speaker != null ? $"[{b.Speaker}] " : "")}{b.Text}");
        }
        else if (transcript.Words.Count > 0)
        {
            sb.AppendLine().AppendLine("Words:");
            foreach (var w in transcript.Words)
                sb.AppendLine($"  {Time(w.StartMs)} – {Time(w.EndMs)}  {w.Text}{(w.Speaker != null ? $"  [{w.Speaker}]" : "")}");
        }
        return sb.ToString().TrimEnd();
    }

    [RelayCommand]
    private async Task BrowseAudio()
    {
        if (await Dialogs.OpenFile("Choose an audio file", new FileFilter("Audio", "*.wav", "*.mp3", "*.opus")) is { } file)
            AudioFile = file;
    }
}

/// <summary>Transcribe: the words in an audio file (with their times when the worker gives them).</summary>
public sealed class OneShotTranscribeViewModel(OneShotViewModel owner) : OneShotListenViewModel(owner, OneShotKind.Transcribe, "Transcribe")
{
    protected override IEnumerable<WorkerManifest> Candidates() => AppHost.Catalog.Transcribers.Where(w => w.CanTranscribe);
    public override string RequiredTags => "Transcription";
    public override bool TextRequired => false;
    protected override ListenMode ModeOf(WorkerManifest worker) => worker.ListenMode;
}

/// <summary>Align: the times of a known text's words in an audio file.</summary>
public sealed class OneShotAlignViewModel(OneShotViewModel owner) : OneShotListenViewModel(owner, OneShotKind.Align, "Align text")
{
    protected override IEnumerable<WorkerManifest> Candidates() => AppHost.Catalog.Transcribers.Where(w => w.AlignsText);
    public override string RequiredTags => "Alignment Forced";
    public override bool TextRequired => true;
    protected override ListenMode ModeOf(WorkerManifest worker) => ListenMode.Align;
}

/// <summary>Compare voices: how alike the voices in some audio files are.</summary>
public sealed partial class OneShotCompareViewModel(OneShotViewModel owner) : OneShotTabViewModel(owner, OneShotKind.Compare, "Compare voices")
{
    public ObservableCollection<string> References { get; } = new();
    public ObservableCollection<string> Recordings { get; } = new();

    [ObservableProperty] private string? _selectedReference;
    [ObservableProperty] private string? _selectedCandidate;

    protected override IEnumerable<WorkerManifest> Candidates() => AppHost.Catalog.SimScorers;
    public override string RequiredTags => "Speaker Verification";

    private static readonly FileFilter AudioFilter = new("Audio", "*.wav", "*.mp3", "*.opus");

    [RelayCommand]
    private async Task AddReferences()
    {
        foreach (var file in await Dialogs.OpenFiles("Reference clips (the voice to compare with)", true, AudioFilter))
            if (!References.Contains(file)) References.Add(file);
    }

    [RelayCommand]
    private async Task AddCandidates()
    {
        foreach (var file in await Dialogs.OpenFiles("Recordings to compare", true, AudioFilter))
            if (!Recordings.Contains(file)) Recordings.Add(file);
    }

    [RelayCommand]
    private void RemoveReference()
    {
        if (SelectedReference != null) References.Remove(SelectedReference);
    }

    [RelayCommand]
    private void RemoveCandidate()
    {
        if (SelectedCandidate != null) Recordings.Remove(SelectedCandidate);
    }

    protected override async Task RunJobAsync(WorkerManifest worker)
    {
        var paths = References.Concat(Recordings).ToList();
        if (paths.Count < 2) { Status = "Add at least two audio files."; return; }
        if (JobSettings(worker) is not { } settings) return;

        var dir = Owner.ScratchFor("compare");
        Status = "Reading the audio…";
        List<VoiceFile> files;
        try
        {
            files = await Task.Run(() => paths.Select((path, i) =>
            {
                var wav = Path.Combine(dir, $"{i:D3}.wav");
                Transcription.PrepareAudio(path, wav);
                return new VoiceFile { Path = wav, Role = i < References.Count ? VoiceRoles.Reference : VoiceRoles.Candidate };
            }).ToList());
        }
        catch (Exception ex)
        {
            Status = "Can't read the audio: " + ex.Message;
            return;
        }

        var job = SpeakerVerification.Job(worker, files, settings, "One shot", JobPriority.Normal, tag: "one-shot");
        var clock = Stopwatch.StartNew();
        await RunJobsAsync([job]);
        if (FailureOf([job]) is { } failure) { Status = failure; return; }
        Status = $"Done in {clock.Elapsed.TotalSeconds:0.0} s ({job.DeviceName}).";
        ResultText = Describe(job.VoiceScores!, paths, References.Count, worker);
    }

    private static string Describe(VoiceScores scores, List<string> paths, int referenceCount, WorkerManifest worker)
    {
        string Name(int i) => Path.GetFileName(paths[i]);
        string Score(double s) => s.ToString("0.000", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        if (referenceCount > 0)
        {
            sb.AppendLine($"Scores against {(referenceCount == 1 ? Name(0) : $"the average of {referenceCount} references")}  (higher is more alike):");
            for (var i = 0; i < scores.Scores.Count; i++)
                sb.AppendLine($"  {Score(scores.Scores[i])}  {Name(referenceCount + i)}  ({scores.SpeechMs[referenceCount + i] / 1000.0:0.0} s of speech)");
        }
        else
        {
            sb.AppendLine("Every pair (higher is more alike):");
            for (var i = 0; i < paths.Count; i++)
                for (var j = i + 1; j < paths.Count; j++)
                    sb.AppendLine($"  {Score(scores.Matrix[i][j])}  {Name(i)}  –  {Name(j)}");
        }
        sb.AppendLine().Append($"Scored by {worker.DisplayName} / {scores.Model}. Scores of different models can't be compared.");
        if (Enumerable.Range(0, paths.Count).Any(i => scores.SpeechMs[i] < SpeakerVerification.MinSpeechMs))
            sb.AppendLine().Append($"Clips with less than {SpeakerVerification.MinSpeechMs / 1000.0:0.#} s of speech score unreliably.");
        return sb.ToString();
    }
}
