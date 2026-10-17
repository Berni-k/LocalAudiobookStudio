using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Analysis;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Audio.Mixing;
using LocalAudiobookStudio.Core.Hashing;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Engine.Hosting;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>Identifies jobs that belong to a project (as opposed to speaker previews and one-off generations).</summary>
public sealed record SegmentJobTag(ProjectEngine Engine, Guid ItemId);

/// <summary>Identifies the job that transcribes a take; <see cref="WavPath"/> is the 16 kHz copy it listens to.</summary>
/// <param name="ExpectedText">The segment's text the take is checked against (not what an aligner was given to time).</param>
public sealed record TranscriptJobTag(ProjectEngine Engine, Guid ItemId, int Take, string WavPath, string ExpectedText);

/// <summary>A segment's state on disk (the app adds queued and generating from the scheduler).</summary>
public enum SegmentAudioState
{
    Ready,
    NotGenerated,
    Stale,
    Failed,
}

/// <summary>A snapshot of what the timeline needs, taken where the model may be read.</summary>
public sealed record LayoutInput(List<Chapter> Chapters, Dictionary<Guid, ItemDuration> Durations, ProjectDefaults Defaults);

/// <summary>
/// The rules of a project without any user interface: input hashes and staleness, the settings and voice a
/// segment generates with, lengths, building jobs, saving finished takes, migrations of older projects and
/// the timeline. Not thread-safe: the model is read and changed on the owner's thread (the app's UI thread),
/// except <see cref="SaveGeneratedTake"/>, which only writes take files and the take index.
/// </summary>
public sealed partial class ProjectEngine(StudioHost host, Project project)
{
    private readonly SpeakingRateTracker _rates = new();

    public StudioHost Host { get; } = host;
    public Project Project { get; } = project;
    public ProjectPaths Paths => Project.Paths;
    public TakeIndex Takes { get; } = new(project.Paths);
    private WorkerCatalog Catalog => Host.Catalog;

    /// <summary>Called when the engine changes or adds a speaker (migrations, new speakers), so the owner saves it.</summary>
    public Action<Speaker>? SpeakerChanged { get; set; }

    // ================================================================== opening

    /// <summary>
    /// Converts older project formats; returns the chapters that changed (to be saved). The migrations may
    /// refresh item hashes, so a take that was current stays current.
    /// </summary>
    public HashSet<Chapter> Migrate()
    {
        var voicesMigrated = MigrateVoicesToSettings();
        return voicesMigrated ? Project.Chapters.ToHashSet() : [];
    }

    /// <summary>
    /// Indexes takes and clips (in the background), then forgets selected takes whose WAV is gone, so those
    /// segments need generation again.
    /// </summary>
    public async Task IndexAudioAsync()
    {
        var ids = Project.AllItems().Where(i => i.IsGenerated).Select(i => i.Id).ToList();
        var clips = Project.AllItems().Where(i => i.Kind == ItemKind.Clip && i.ClipFile != null).Select(i => i.ClipFile!).Distinct().ToList();
        await Task.Run(() =>
        {
            Takes.LoadAll(ids);
            foreach (var c in clips) Takes.AnalyzeClip(c);
        });
        foreach (var item in Project.AllItems())
            if (item.IsGenerated && item.SelectedTake != null && Takes.Selected(item) == null)
                item.SelectedTake = null;
    }

    // ================================================================== hashing / staleness

    public WorkerManifest? WorkerFor(TimelineItem item) => item.Kind switch
    {
        ItemKind.Tts => Catalog.Get(Project.FindSpeaker(item.Speaker)?.Worker),
        _ => null,
    };

    public string? ComputeHash(TimelineItem item) =>
        InputHasher.Compute(item, item.Kind == ItemKind.Tts ? Project.FindSpeaker(item.Speaker) : null,
            WorkerFor(item) is { } w ? Host.ModelVersionOf(w) : null);

    public bool IsStale(TimelineItem item) =>
        item.IsGenerated && item.SelectedTake != null && item.InputHash != ComputeHash(item);

    /// <summary>A speech segment whose speaker has no worker: it is recorded with the microphone, not generated.</summary>
    public bool IsRecorded(TimelineItem item) =>
        item.Kind == ItemKind.Tts && Project.FindSpeaker(item.Speaker) is { } sp && string.IsNullOrEmpty(sp.Worker);

    public SegmentAudioState StateOf(TimelineItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Pause:
            case ItemKind.Comment:
            case ItemKind.Unknown:
                return SegmentAudioState.Ready;
            case ItemKind.Clip:
                return Takes.Clip(item.ClipFile) != null ? SegmentAudioState.Ready : SegmentAudioState.NotGenerated;
        }
        var take = Takes.Selected(item);
        if (take == null) return item.LastError != null ? SegmentAudioState.Failed : SegmentAudioState.NotGenerated;
        if (item.LastError != null) return SegmentAudioState.Failed;
        return IsStale(item) ? SegmentAudioState.Stale : SegmentAudioState.Ready;
    }

    // ================================================================== settings and voices

    /// <summary>Speaker settings overlaid with the segment's overrides; file settings become absolute paths.</summary>
    public JsonObject MergeSettings(JsonObject? baseSettings, JsonObject? overrides, WorkerManifest manifest) =>
        OneShot.MergeSettings(baseSettings, overrides, manifest, Paths.Resolve);

    /// <summary>A speaker's settings: its worker's, plus those its ComfyUI workflow adds.</summary>
    public List<SettingDefinition> SettingDefinitionsFor(Speaker speaker, WorkerManifest worker) =>
        worker.SettingsFor(speaker.Settings, Paths.Resolve);

    /// <summary>A segment's settings: as its speaker's, with the workflow its overrides may pick.</summary>
    public List<SettingDefinition> SettingDefinitionsFor(TimelineItem item) =>
        WorkerFor(item) is { } w ? w.SettingsFor(EffectiveSettings(item, w)) : [];

    /// <summary>The settings a segment generates with: its speaker's, overlaid with its own overrides.</summary>
    private JsonObject? EffectiveSettings(TimelineItem item, WorkerManifest worker) =>
        Project.FindSpeaker(item.Speaker) is { } sp ? MergeSettings(sp.Settings, item.SettingsOverride, worker) : item.SettingsOverride;

    /// <summary>A voice is a file in the speakers folder, an absolute path, or a worker voice id.</summary>
    public string? ResolveVoice(string? voice)
    {
        if (string.IsNullOrWhiteSpace(voice)) return null;
        var inSpeakers = Path.Combine(Paths.SpeakersDir, voice);
        if (File.Exists(inSpeakers)) return inSpeakers;
        var inProject = Paths.Resolve(voice);
        if (File.Exists(inProject)) return inProject;
        return voice;
    }

    // ================================================================== lengths

    /// <summary>
    /// The length a segment's worker should be asked for, in seconds: the segment's own length, or for
    /// a worker that requires one, the guess. Null when the worker takes no length or decides itself.
    /// </summary>
    public double? RequestedLengthSec(TimelineItem item)
    {
        if (item.Kind != ItemKind.Tts || WorkerFor(item) is not { SupportsLength: true } w) return null;
        if (item.DurationSec is double sec) return w.ClampLength(sec, EffectiveSettings(item, w));
        return w.RequiresLength ? GuessLengthSec(item, w) : null;
    }

    /// <summary>The longest length the segment's worker allows with its settings (e.g. per model).</summary>
    public double MaxLengthSec(TimelineItem item) =>
        WorkerFor(item) is { } w ? w.MaxLengthSecFor(EffectiveSettings(item, w)) : 600;

    /// <summary>
    /// Guessed length for a segment without its own. A worker that needs a length gets one named in the text
    /// ("thunder, 4s"), else the default; otherwise it is the text at the speaker's rate. Rounded to 0.1 s and kept
    /// within the worker's range.
    /// </summary>
    public double GuessLengthSec(TimelineItem item, WorkerManifest? worker = null)
    {
        worker ??= WorkerFor(item);
        var guess = worker?.RequiresLength == true
            ? LengthGuess.NamedIn(item.Text) ?? LengthGuess.DefaultSeconds
            : LengthGuess.FromText(item.Text, _rates.RateFor(item.Speaker));
        guess = Math.Round(guess, 1);
        return worker == null ? guess : worker.ClampLength(guess, EffectiveSettings(item, worker));
    }

    // ================================================================== jobs

    /// <summary>
    /// The job that generates a segment, or null with <paramref name="error"/> set (null error: nothing to
    /// generate, e.g. a pause). Reading order and label come from the chapter and item positions.
    /// </summary>
    public GenerationJob? BuildJob(TimelineItem item, int chapterIndex, int itemIndex, JobPriority priority, out string? error)
    {
        error = null;
        if (item.Kind != ItemKind.Tts) return null;

        var speaker = Project.FindSpeaker(item.Speaker);
        if (speaker == null) { error = $"Speaker '{item.Speaker}' does not exist."; return null; }
        var manifest = Catalog.Get(speaker.Worker);
        if (manifest == null) { error = $"Speaker {speaker.DisplayName} uses unknown worker '{speaker.Worker}'."; return null; }
        if (string.IsNullOrWhiteSpace(item.Text)) { error = "Empty text."; return null; }
        var settings = MergeSettings(speaker.Settings, item.SettingsOverride, manifest);
        // A choice the worker's runtime can't do (e.g. a preset voice HartsyInference lacks) fails here, clearly,
        // rather than silently generating something else.
        if (manifest.UnsupportedChoice(settings) is { } unsupported) { error = unsupported; return null; }
        var request = new GenerateRequest
        {
            Text = item.Text,
            Voice = ResolveVoice(speaker.Voice),
            DurationSec = RequestedLengthSec(item),
            Settings = settings,
            Seed = OneShot.SeedFrom(settings),
        };

        var label = item.Text!;
        if (label.Length > 60) label = label[..57] + "…";
        return new GenerationJob
        {
            ItemId = item.Id,
            WorkerId = manifest.Id,
            Priority = priority,
            ReadingOrder = chapterIndex * 1_000_000L + itemIndex,
            InputHash = ComputeHash(item),
            Request = request,
            SegmentDir = Paths.SegmentAudioDir(item.Id),
            Label = $"{chapterIndex + 1}.{itemIndex + 1}  {label}",
            Tag = new SegmentJobTag(this, item.Id),
        };
    }

    public bool Owns(GenerationJob job) => job.Tag is SegmentJobTag tag && ReferenceEquals(tag.Engine, this);

    /// <summary>
    /// For the scheduler's OnGenerated (a background thread): analyses the new take (loudness, trims, peaks)
    /// and saves its take file. Doesn't touch the model; <see cref="ApplyCompletedTake"/> does that.
    /// </summary>
    public Task SaveGeneratedTake(GenerationJob job, GenerateResult result)
    {
        if (!Owns(job)) return Task.CompletedTask;
        SaveTake(job, job.ItemId, job.TakeNumber, result.Path, job.InputHash, result.Seed, job.Request.Text?.Length);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Analyses a generated take's WAV (loudness, trims, peaks), stores it in the project's format and saves its take
    /// file. Thread-safe like <see cref="SaveGeneratedTake"/>: it doesn't touch the model.
    /// </summary>
    private TakeInfo SaveTake(GenerationJob job, Guid itemId, int take, string wavPath, string? inputHash, long? seed, int? textLength,
        TakeFusion? fusion = null)
    {
        var analysis = TakeAnalyzer.Analyze(wavPath, Paths.TakePeaks(itemId, take), Host.SilenceTrim);
        var manifest = Catalog.Get(job.WorkerId);
        var info = new TakeInfo
        {
            Take = take,
            CreatedUtc = DateTime.UtcNow,
            InputHash = inputHash ?? "",
            Worker = job.WorkerId,
            ModelVersion = manifest != null ? Host.ModelVersionOf(manifest) : "",
            Runtime = manifest?.Runtime,
            Seed = seed,
            Gpu = job.DeviceName,
            DurationMs = analysis.DurationMs,
            SampleRate = analysis.SampleRate,
            LoudnessLufs = analysis.LoudnessLufs is double l ? Math.Round(l, 2) : null,
            GenerationMs = job.StartedUtc is DateTime s ? (int)(DateTime.UtcNow - s).TotalMilliseconds : 0,
            TrimStartMs = analysis.TrimStartMs,
            TrimEndMs = analysis.TrimEndMs,
            TextLength = textLength,
            Fusion = fusion,
            Format = StoreAudio(wavPath),
        };
        TakeStore.SaveTakeInfo(Paths, itemId, info);
        Takes.Add(itemId, info);
        return info;
    }

    // ================================================================== transcripts

    /// <summary>The transcription worker of a segment's speaker, or null when it has none.</summary>
    public WorkerManifest? TranscriberFor(TimelineItem item) =>
        item.Kind == ItemKind.Tts && Project.FindSpeaker(item.Speaker) is { Transcriber.Length: > 0 } sp
        && Catalog.Get(sp.Transcriber) is { IsTranscriber: true } m ? m : null;

    /// <summary>
    /// What of <paramref name="text"/> a worker says aloud: its inline tags (<see cref="WorkerManifest.InlineTags"/>)
    /// blanked out, the rest in place. The text as it is for a worker that isn't known here.
    /// </summary>
    public string SpokenText(string? workerId, string text) =>
        !string.IsNullOrEmpty(workerId) && Catalog.Get(workerId) is { } worker ? worker.SpokenText(text) : text;

    /// <summary>The words of a segment's take that should be heard: its text without the tags of the worker that made it.</summary>
    public string SpokenText(TimelineItem item, int? take) =>
        SpokenText(take is int t && Takes.Find(item.Id, t) is { } info ? info.Worker : Project.FindSpeaker(item.Speaker)?.Worker,
            item.Text ?? "");

    /// <summary>
    /// The job that transcribes a take with its speaker's transcription worker, or null when the speaker has none or
    /// it can't run here. Decodes the take into the 16 kHz copy the worker reads (a moment for a segment-sized take).
    /// </summary>
    public GenerationJob? BuildTranscriptionJob(TimelineItem item, int take, int chapterIndex, int itemIndex)
    {
        if (TranscriberFor(item) is not { } manifest || Project.FindSpeaker(item.Speaker) is not { } speaker) return null;
        // Checked against (or, aligning only, timing) the words alone: the generating worker's inline tags aren't said.
        var spoken = SpokenText(item, take);
        if (string.IsNullOrWhiteSpace(spoken)) return null;
        if (Host.WhyWorkerCannotRun(manifest.Id) is { } why)
        {
            Host.Log.LogInformation("Not transcribing {Item}: {Why}", item.Id, why);
            return null;
        }
        // Heard by the transcription worker and, with an aligner, timed by it. A transcription worker that only aligns
        // times the segment's own text (it can't check it).
        var listeners = ListenersFor(speaker);
        var audio = Takes.TakeAudio(item.Id, take);
        if (!File.Exists(audio)) return null;

        var dir = Path.Combine(Paths.CacheDir, "transcribe");
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".wav");
        Transcription.PrepareAudio(audio, wav);
        var label = item.Text!.Length > 50 ? item.Text[..47] + "…" : item.Text;
        // Never null: the transcription worker can run, so it hears the words or (aligning only) times the text.
        return Transcription.Listen(listeners, wav, spoken, $"{chapterIndex + 1}.{itemIndex + 1}  Transcribe: {label}", JobPriority.Low,
            new TranscriptJobTag(this, item.Id, take, wav, spoken), chapterIndex * 1_000_000L + itemIndex, [speaker.DisplayName]);
    }

    /// <summary>
    /// Who listens to a speaker's audio: its transcription worker, when it hears words (tagged Transcription) and can run
    /// here, and its aligner, when it uses one that can run here. A forced aligner chosen as the transcription worker hears
    /// nothing: it is the aligner, timing the segment's text.
    /// </summary>
    public Listeners ListenersFor(Speaker speaker)
    {
        WorkerManifest? Usable(string id, Func<WorkerManifest, bool> can) =>
            !string.IsNullOrEmpty(id) && Catalog.Get(id) is { } w && can(w) && Host.WhyWorkerCannotRun(w.Id) == null ? w : null;
        // An aligner times a given text (the heard words) as it is: only a forced aligner can (AlignsText).
        var hearer = Usable(speaker.Transcriber, w => w.CanTranscribe);
        var (aligner, alignerSettings) = speaker.UseAligner && Usable(speaker.Aligner, w => w.AlignsText) is { } chosen
            ? (chosen, speaker.AlignerSettings)
            : hearer == null && Usable(speaker.Transcriber, w => w.AlignsText) is { } aligning
                ? (aligning, speaker.TranscriberSettings)
                : ((WorkerManifest?)null, new JsonObject());
        return new Listeners(hearer, hearer != null ? MergeSettings(speaker.TranscriberSettings, null, hearer) : new JsonObject(),
            aligner, aligner != null ? MergeSettings(alignerSettings, null, aligner) : new JsonObject());
    }

    public bool OwnsTranscript(GenerationJob job) => job.Tag is TranscriptJobTag tag && ReferenceEquals(tag.Engine, this);

    /// <summary>
    /// Saves what a finished transcription job heard next to its take, and removes the job's 16 kHz copy.
    /// Returns the item and take it belongs to, or null when the job failed or the take is gone.
    /// </summary>
    public (Guid ItemId, int Take)? FinishTranscription(GenerationJob job)
    {
        if (job.Tag is not TranscriptJobTag tag || !ReferenceEquals(tag.Engine, this)) return null;
        try { File.Delete(tag.WavPath); } catch (IOException) { /* cache */ }
        if (job.State != JobState.Completed || job.Transcript is not { } transcript) return null;
        if (!Directory.Exists(Paths.SegmentAudioDir(tag.ItemId))) return null;
        new StoredTranscript
        {
            // Heard by one worker and timed by another: both.
            Worker = job.ContinuesFrom is { } heard ? $"{heard.WorkerId} + {job.WorkerId}" : job.WorkerId,
            Language = transcript.Language,
            Words = Transcription.Heard(transcript),
            Blocks = Transcription.Blocks(transcript),
        }.Save(Paths.TakeTranscript(tag.ItemId, tag.Take));

        // The verdict goes into the take's own file: it is read anyway, and it is what the take is judged by from now on.
        // Only for words that were heard: an aligner given the segment's text finds that text whatever was said.
        var heardWords = job.Transcribe?.Mode != ListenMode.Align || job.ContinuesFrom != null;
        if (heardWords && Takes.Find(tag.ItemId, tag.Take) is { } info)
        {
            var check = Transcription.Check(tag.ExpectedText, transcript);
            info.TranscriptErrors = check.Errors;
            info.TranscriptWordCount = check.ExpectedCount;
            TakeStore.SaveTakeInfo(Paths, tag.ItemId, info);
        }
        return (tag.ItemId, tag.Take);
    }

    /// <summary>
    /// Puts a new take's WAV (already analysed) into the project's audio format: with Opus, encodes it next to the
    /// WAV and deletes the WAV. Returns the format the take ended up in; if encoding fails the WAV stays.
    /// </summary>
    public string StoreAudio(string wavPath)
    {
        if (Project.Defaults.AudioFormat != AudioFormats.Opus) return AudioFormats.Wav;
        var opus = Path.ChangeExtension(wavPath, OggOpus.Extension);
        try
        {
            OggOpus.Write(AudioDecoder.Decode(wavPath), opus, OpusBitrate);
            File.Delete(wavPath);
            return AudioFormats.Opus;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            try { File.Delete(opus); } catch (IOException) { }
            Host.Log.LogWarning(e, "Could not compress {Path}; it stays a WAV", wavPath);
            return AudioFormats.Wav;
        }
    }

    /// <summary>The project's Opus bit rate per channel, in bits per second.</summary>
    public int OpusBitrate => Math.Clamp(Project.Defaults.OpusKbps, 16, 256) * 1000;

    /// <summary>The WAV takes that <see cref="CompressTakes"/> would convert, and their size in bytes.</summary>
    public (int Count, long Bytes) UncompressedTakes()
    {
        var count = 0;
        long bytes = 0;
        foreach (var item in Project.AllItems())
            foreach (var t in Takes.Get(item.Id).Where(t => t.Format == AudioFormats.Wav))
            {
                var path = Paths.TakeAudio(item.Id, t);
                if (!File.Exists(path)) continue;
                count++;
                bytes += new FileInfo(path).Length;
            }
        return (count, bytes);
    }

    /// <summary>
    /// Converts every WAV take to Opus (a background thread). Each take switches over in one step: its Opus file
    /// is written, then its take file says so, then the WAV goes. Returns the takes converted and the bytes saved.
    /// </summary>
    public (int Count, long BytesSaved) CompressTakes(IProgress<double>? progress, CancellationToken ct)
    {
        var todo = Project.AllItems()
            .SelectMany(item => Takes.Get(item.Id).Where(t => t.Format == AudioFormats.Wav).Select(t => (item.Id, Take: t)))
            .ToList();
        var done = 0;
        long saved = 0;
        foreach (var (itemId, take) in todo)
        {
            ct.ThrowIfCancellationRequested();
            var wav = Paths.TakeAudio(itemId, take);
            if (File.Exists(wav))
            {
                var opus = Path.ChangeExtension(wav, OggOpus.Extension);
                try
                {
                    var before = new FileInfo(wav).Length;
                    OggOpus.Write(AudioDecoder.Decode(wav), opus, OpusBitrate);
                    take.Format = AudioFormats.Opus;
                    TakeStore.SaveTakeInfo(Paths, itemId, take);
                    Takes.Add(itemId, take);
                    File.Delete(wav);
                    saved += before - new FileInfo(opus).Length;
                    done++;
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    take.Format = AudioFormats.Wav;
                    Host.Log.LogWarning(e, "Could not compress {Path}", wav);
                }
            }
            progress?.Report((double)(done + 1) / todo.Count);
        }
        return (done, saved);
    }

    /// <summary>
    /// Selects a finished job's take, if the segment's input is still what was generated. Returns false when
    /// the segment changed meanwhile (the take is kept but not selected).
    /// </summary>
    public bool ApplyCompletedTake(TimelineItem item, GenerationJob job) => ApplyTake(item, job.TakeNumber, job.InputHash);

    /// <summary>Selects a new take made from <paramref name="inputHash"/>, if the segment's input is still that.</summary>
    public bool ApplyTake(TimelineItem item, int take, string? inputHash)
    {
        if (ComputeHash(item) != inputHash) return false;
        item.SelectedTake = take;
        item.InputHash = inputHash;
        item.LastError = null;
        item.TrimStartMs = null; // use the new take's detected silence
        item.TrimEndMs = null;
        return true;
    }

    /// <summary>Records a failed job's error on its segment. Returns true when the segment changed.</summary>
    public static bool ApplyFailure(TimelineItem item, GenerationJob job)
    {
        if (job.State != JobState.Failed) return false;
        item.LastError = job.LastError ?? "Unknown error";
        return true;
    }

    // ================================================================== speakers and migrations

    /// <param name="voiceVariant">Picks the n-th built-in voice of the worker (see <see cref="WorkerManifest.DefaultSettings"/>).</param>
    public Speaker AddSpeaker(string displayName, string worker, int voiceVariant = 0)
    {
        var sp = new Speaker
        {
            Id = Project.UniqueSpeakerId(displayName),
            DisplayName = displayName,
            Color = ProjectStore.SpeakerPalette[Project.Speakers.Count % ProjectStore.SpeakerPalette.Length],
            Worker = worker,
            Settings = Catalog.Get(worker)?.DefaultSettings(voiceVariant) ?? new(),
            MarkerAliases = [displayName],
        };
        Project.Speakers[sp.Id] = sp;
        SpeakerChanged?.Invoke(sp);
        return sp;
    }

    /// <summary>
    /// Built-in voices used to be stored in <see cref="Speaker.Voice"/>; they are now the worker's "voice"
    /// setting (Voice only holds a voice-cloning reference clip). Moves them over. Segments that were up to
    /// date stay up to date. Returns true when anything changed.
    /// </summary>
    public bool MigrateVoicesToSettings()
    {
        var changed = false;
        foreach (var sp in Project.Speakers.Values)
        {
            var voiceSetting = Catalog.Get(sp.Worker)?.VoiceSetting;
            if (sp.Voice is not { } voice || voiceSetting == null || !voiceSetting.Options.Contains(voice)) continue;
            var current = Project.AllItems()
                .Where(i => i.Kind == ItemKind.Tts && string.Equals(i.Speaker, sp.Id, StringComparison.OrdinalIgnoreCase)
                            && i.InputHash != null && i.InputHash == ComputeHash(i))
                .ToList();
            sp.Settings[WorkerManifest.VoiceKey] = voice;
            sp.Voice = null;
            foreach (var i in current) i.InputHash = ComputeHash(i);
            SpeakerChanged?.Invoke(sp);
            changed = true;
        }
        return changed;
    }

    // ================================================================== timeline

    /// <summary>
    /// Snapshots what the timeline needs (ids, kinds, pause and background settings, durations) and updates
    /// the speaking rates that estimate ungenerated speech. Run where the model may be read.
    /// </summary>
    public LayoutInput SnapshotLayout()
    {
        _rates.Clear();
        foreach (var item in Project.AllItems())
        {
            if (item.Kind != ItemKind.Tts || item.Speaker == null) continue;
            var src = Takes.SourceOf(item);
            if (src == null) continue;
            var (ts, te) = ProjectMixPlanBuilder.Trims(item, src);
            _rates.Add(item.Speaker, item.Text?.Length ?? 0, TimelineCalculator.TrimmedMs(src.RawDurationMs, ts, te));
        }
        var durations = new Dictionary<Guid, ItemDuration>();
        var lite = new List<Chapter>(Project.Chapters.Count);
        foreach (var ch in Project.Chapters)
        {
            var c = new Chapter { Id = ch.Id, Items = new List<TimelineItem>(ch.Items.Count) };
            foreach (var item in ch.Items)
            {
                durations[item.Id] = DurationOf(item);
                c.Items.Add(new TimelineItem
                {
                    Id = item.Id,
                    Kind = item.Kind,
                    DurationMs = item.DurationMs,
                    Background = item.Background,
                    BackgroundLength = item.BackgroundLength,
                    BackgroundLimit = item.BackgroundLimit,
                    BackgroundLoop = item.BackgroundLoop,
                });
            }
            lite.Add(c);
        }
        return new LayoutInput(lite, durations, Project.Defaults);
    }

    /// <summary>The prefix sum over a snapshot; safe on any thread.</summary>
    public static TimelineLayout ComputeLayout(LayoutInput input, long version = 0) =>
        TimelineCalculator.Compute(input.Chapters, input.Defaults, i => input.Durations[i.Id], version);

    /// <summary>The real mix (export) and, with <paramref name="placeholders"/>, the playback mix with quiet static for missing audio.</summary>
    public MixPlan BuildMixPlan(TimelineLayout layout, bool placeholders = false) =>
        ProjectMixPlanBuilder.Build(Project.Chapters, Project.Defaults, layout, Takes.SourceOf, placeholders: placeholders);

    /// <summary>Snapshot, layout and export mix in one go, for callers without a UI thread to protect.</summary>
    public async Task<(TimelineLayout layout, MixPlan mix)> ComputeTimelineAsync()
    {
        var input = SnapshotLayout();
        var layout = await Task.Run(() => ComputeLayout(input));
        return (layout, BuildMixPlan(layout));
    }

    public ItemDuration DurationOf(TimelineItem item)
    {
        switch (item.Kind)
        {
            case ItemKind.Pause:
                return new ItemDuration(item.DurationMs ?? 0, false);
            case ItemKind.Comment:
            case ItemKind.Unknown:
                return new ItemDuration(0, false);
            case ItemKind.Clip:
            {
                var src = Takes.SourceOf(item);
                if (src == null) return new ItemDuration(0, true);
                var (ts, te) = ProjectMixPlanBuilder.Trims(item, src);
                return new ItemDuration(TimelineCalculator.TrimmedMs(src.RawDurationMs, ts, te), false);
            }
            default:
            {
                var src = Takes.SourceOf(item);
                if (src != null)
                {
                    var (ts, te) = ProjectMixPlanBuilder.Trims(item, src);
                    return new ItemDuration(TimelineCalculator.TrimmedMs(src.RawDurationMs, ts, te), false);
                }
                return RequestedLengthSec(item) is double sec
                    ? new ItemDuration(sec * 1000.0, true)
                    : new ItemDuration(TimelineCalculator.EstimateSpeechMs(item.Text?.Length ?? 0, _rates.RateFor(item.Speaker)), true);
            }
        }
    }
}
