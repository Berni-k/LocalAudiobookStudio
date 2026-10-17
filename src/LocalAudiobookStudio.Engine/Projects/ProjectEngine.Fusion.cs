using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.Hashing;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Serialization;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.Engine.Projects;

/// <summary>A segment of a fused generation: which part it is and what it was made from.</summary>
public sealed record FusedMember(Guid ItemId, int Part, string Text, string? InputHash);

/// <summary>A take cut out of a fused generation. <see cref="Transcribed"/>: its transcript was saved with it.</summary>
public sealed record FusedTake(Guid ItemId, int Take, string? InputHash, bool Transcribed);

/// <summary>
/// A fused generation (several segments, or a segment with context, in one go), kept on its job and on the job that
/// transcribes it. Filled in on the scheduler's threads as the audio is cut; read on the model's thread when it ends.
/// </summary>
public sealed class FusedJobTag(ProjectEngine engine)
{
    public ProjectEngine Engine { get; } = engine;
    public List<FusedMember> Members { get; init; } = [];

    /// <summary>
    /// Every part, context included, in order: as the worker was sent them, or for a worker that doesn't fuse, the lines
    /// its one text was made of.
    /// </summary>
    public List<GeneratePart> Parts { get; init; } = [];

    public List<string> PartTexts => Parts.Select(p => p.Text ?? "").ToList();

    /// <summary>The record kept of it for the fused generation inspector (<see cref="FusionRecord"/>), or null.</summary>
    public string? RecordId { get; init; }

    public int ContextCount => PartTexts.Count - Members.Count;

    /// <summary>
    /// Who listens to the whole when the worker gives no spans: the members' transcription worker hears the words and
    /// their aligner, if they use one, times what it heard (<see cref="Transcription.Listen"/>).
    /// </summary>
    public Listeners Listeners { get; init; } = Listeners.None;

    /// <summary>
    /// The words heard are each member's transcript: they were heard (not only a given text timed) by the members'
    /// own transcription worker. Otherwise the takes are transcribed afterwards like any other.
    /// </summary>
    public bool KeepsTranscripts { get; init; }

    /// <summary>Context parts heard from their take: the part and the take's audio (decoded to WAV before the run).</summary>
    public List<(GeneratePart Part, string Audio)> ContextAudio { get; init; } = [];

    /// <summary>Where the worker writes the whole generation.</summary>
    public string FusedPath { get; init; } = "";

    // ---- filled on the scheduler's threads

    /// <summary>The members' reserved take numbers, by item.</summary>
    public Dictionary<Guid, int> TakeNumbers { get; } = [];
    public List<FusedTake> Taken { get; } = [];

    /// <summary>Members that couldn't be cut out (generated on their own instead).</summary>
    public List<Guid> Uncut { get; } = [];

    /// <summary>The job that generated it: what the takes record (worker, device, seed), also when a transcription cuts them.</summary>
    public GenerationJob? Generation { get; set; }

    /// <summary>The worker didn't say where the parts are: this 16 kHz copy is transcribed to find them.</summary>
    public string? TranscriptWav { get; set; }

    /// <summary>Files to delete once it is over (decoded context takes).</summary>
    public List<string> TempFiles { get; } = [];

    // ---- for the record

    /// <summary>Where each member's take was cut out of the whole, by item.</summary>
    public Dictionary<Guid, (int StartMs, int EndMs)> Cuts { get; } = [];

    /// <summary>Why a member wasn't cut out, by item.</summary>
    public Dictionary<Guid, string> Notes { get; } = [];

    /// <summary>What the transcription heard in the whole, when it was used to cut it.</summary>
    public List<HeardWord>? Transcript { get; set; }

    /// <summary>The blocks of speech by one voice that <see cref="Transcript"/>'s words came from, when the transcriber tells voices apart.</summary>
    public List<HeardBlock>? TranscriptBlocks { get; set; }
}

/// <summary>Identifies the job that transcribes a fused generation to cut it into takes.</summary>
public sealed record SplitJobTag(FusedJobTag Fused);

/// <summary>
/// How one segment takes part in a fused generation, for the row's indicators: context lines before it (the first
/// segment of the generation) or after it (the last), and batch mates before and after it.
/// </summary>
public readonly record struct FusionRoles(bool ContextBefore, bool ContextAfter, bool BatchBefore, bool BatchAfter);

/// <summary>What a fused generation's end means for the project (worked out on the model's thread).</summary>
public sealed class FusionOutcome
{
    /// <summary>Segments that got a take; <c>Selected</c> unless the segment changed while it was generating.</summary>
    public List<(TimelineItem Item, int Take, bool Selected, bool Transcribed)> Takes { get; } = [];

    /// <summary>Segments whose generation failed for good (their <see cref="TimelineItem.LastError"/> is set).</summary>
    public List<TimelineItem> Failed { get; } = [];

    /// <summary>Segments whose generation was cancelled.</summary>
    public List<Guid> Cancelled { get; } = [];

    /// <summary>Jobs to queue next: the transcription that cuts the audio, or segments generated again on their own.</summary>
    public List<GenerationJob> FollowUps { get; } = [];
}

public sealed partial class ProjectEngine
{
    /// <summary>A segment that can take part in a fused generation, with what it would be generated with.</summary>
    private sealed record FusionPlan(TimelineItem Item, int Chapter, int Index, Speaker Speaker, WorkerManifest Manifest,
        JsonObject Settings, JsonObject Shared, JsonObject PerVoice, string SharedKey, Listeners Listeners)
    {
        public bool CanJoin(FusionPlan o) =>
            Manifest.Id == o.Manifest.Id && Manifest.Runtime == o.Manifest.Runtime && SharedKey == o.SharedKey
            && Listeners.Key == o.Listeners.Key;
    }

    /// <summary>
    /// The jobs that generate these segments, in reading order. Segments whose speakers' context settings allow it and
    /// whose worker takes fused generations go together: neighbouring segments as one batch, and lines around them as
    /// context. The rest get a job each (<see cref="BuildJob"/>). Segments that can't be queued add to <paramref name="errors"/>.
    /// </summary>
    public List<GenerationJob> BuildJobs(IEnumerable<TimelineItem> items, JobPriority priority, List<string> errors)
    {
        var positions = new Dictionary<Guid, (int Chapter, int Index)>();
        for (int ci = 0; ci < Project.Chapters.Count; ci++)
            for (int ii = 0; ii < Project.Chapters[ci].Items.Count; ii++)
                positions[Project.Chapters[ci].Items[ii].Id] = (ci, ii);
        var ordered = items.Where(i => positions.ContainsKey(i.Id)).DistinctBy(i => i.Id)
            .OrderBy(i => positions[i.Id].Chapter).ThenBy(i => positions[i.Id].Index).ToList();

        var jobs = new List<GenerationJob>();
        var batched = new HashSet<Guid>(); // members of a batch already planned
        for (int n = 0; n < ordered.Count; n++)
        {
            var item = ordered[n];
            if (batched.Contains(item.Id)) continue;
            var (ci, ii) = positions[item.Id];
            var group = new List<FusionPlan>();
            if (PlanFusion(item, ci, ii) is { } first)
            {
                group.Add(first);
                // The longest run of neighbours that allow batching, within the worker's limits and every member's
                // own (its speaker's most segments and characters). Only workers that render several voices batch.
                // Background items are passed over (they come up on their own later); anything else ends the run.
                var fusion = first.Manifest.Fusion;
                var maxChars = fusion?.MaxChars ?? first.Manifest.MaxChars;
                for (int k = n + 1; first.Manifest.CanBatch && first.Speaker.Context.Batches && k < ordered.Count; k++)
                {
                    var next = ordered[k];
                    if (next.IsBackground) continue;
                    var (nci, nii) = positions[next.Id];
                    if (!Adjacent(group[^1], nci, nii) || PlanFusion(next, nci, nii) is not { } plan) break;
                    if (!plan.Speaker.Context.Batches || !first.CanJoin(plan)) break;
                    var batch = group.Append(plan).ToList();
                    var chars = batch.Sum(p => p.Item.Text!.Length);
                    if (batch.Select(p => p.Speaker.Id).Distinct().Count() > fusion!.Speakers || chars > maxChars) break;
                    if (batch.Any(p => p.Speaker.Context.BatchMaxSegments < batch.Count || p.Speaker.Context.BatchMaxChars < chars)) break;
                    group.Add(plan);
                }
            }
            if (group.Count > 0 && BuildFusedJob(group, priority) is { } fused)
            {
                jobs.Add(fused);
                foreach (var p in group) batched.Add(p.Item.Id);
                continue;
            }
            var job = BuildJob(item, ci, ii, priority, out var error);
            if (job != null) jobs.Add(job);
            else if (error != null) errors.Add($"{ci + 1}.{ii + 1}: {error}");
        }
        return jobs;
    }

    /// <summary>
    /// Items batching and context look past, as if they weren't there: pauses, comments, and background items (they play
    /// under what follows, not in turn). Everything else (a clip, another segment) ends a batch or the context.
    /// </summary>
    private static bool Transparent(TimelineItem item) =>
        item.IsBackground || item.Kind is ItemKind.Pause or ItemKind.Comment or ItemKind.Unknown;

    /// <summary>The next segment in the same chapter, with only <see cref="Transparent"/> items between.</summary>
    private bool Adjacent(FusionPlan previous, int chapter, int index) =>
        chapter == previous.Chapter && index > previous.Index
        && Project.Chapters[chapter].Items.Skip(previous.Index + 1).Take(index - previous.Index - 1).All(Transparent);

    /// <summary>
    /// How a segment would be generated in a fused generation, or null when it can't be: its speaker wants no context
    /// (or, with a worker that doesn't fuse, only batching, which such a worker can't do), it asks for a length, or
    /// nothing could cut it out again (no spans from the worker and no transcription worker that runs).
    /// </summary>
    private FusionPlan? PlanFusion(TimelineItem item, int chapter, int index)
    {
        // A background item plays under what follows: it is generated on its own, without context.
        if (item.Kind != ItemKind.Tts || item.IsBackground || string.IsNullOrWhiteSpace(item.Text) || item.DurationSec != null) return null;
        if (Project.FindSpeaker(item.Speaker) is not { } speaker || !speaker.Context.Any) return null;
        if (Catalog.Get(speaker.Worker) is not { } manifest) return null;
        var fusion = manifest.Fusion;
        if (fusion == null && !speaker.Context.HasContext) return null;
        var settings = MergeSettings(speaker.Settings, item.SettingsOverride, manifest);
        if (manifest.UnsupportedChoice(settings) != null) return null;

        // Cutting by transcription needs word times: an aligner, or a transcription worker that times words. Or one that tells
        // voices apart: its timed blocks may line up with the parts (when they don't, an aligner times the words instead,
        // or the parts are generated one by one). One that only transcribes can't say where the parts are.
        var listeners = ListenersFor(speaker);
        if (!listeners.CanCut && fusion?.Spans != true) return null;

        var (shared, perVoice) = SplitSettings(settings, manifest);
        var key = (JsonObject)shared.DeepClone();
        foreach (var def in manifest.SettingsFor(settings, Paths.Resolve).Where(d => d.Type == SettingTypes.Seed)) key.Remove(def.Key);
        return new FusionPlan(item, chapter, index, speaker, manifest, settings, shared, perVoice, InputHasher.Canonicalize(key), listeners);
    }

    /// <summary>A segment's settings as a fused request takes them: those all parts share, and those of its voice.</summary>
    private (JsonObject Shared, JsonObject PerVoice) SplitSettings(JsonObject settings, WorkerManifest manifest)
    {
        var perVoiceKeys = manifest.SettingsFor(settings, Paths.Resolve).Where(d => d.PerVoice).Select(d => d.Key).ToHashSet();
        JsonObject shared = new(), perVoice = new();
        foreach (var (k, v) in settings) (perVoiceKeys.Contains(k) ? perVoice : shared)[k] = v?.DeepClone();
        return (shared, perVoice);
    }

    /// <summary>
    /// The fused job for a group of neighbouring segments (one or more) with the context their speakers ask for: lines
    /// before the first (its speaker's "before") and after the last (its speaker's "after"), nearest first, as long as
    /// the worker can take them in one go. Null for a single segment without context: that is an ordinary job.
    /// A worker that doesn't fuse gets the context as plain text around the segment, in the segment's voice, in an
    /// ordinary request; transcription trims it away again.
    /// </summary>
    private GenerationJob? BuildFusedJob(List<FusionPlan> group, JobPriority priority)
    {
        var first = group[0];
        var manifest = first.Manifest;
        var fusion = manifest.Fusion;
        var items = Project.Chapters[first.Chapter].Items;
        var lastIndex = group[^1].Index;

        // Context goes around the whole batch, never between its segments: the first one's speaker decides what comes
        // before, the last one's what comes after.
        var before = new List<ContextLine>();
        var after = new List<ContextLine>();
        if (fusion == null || fusion.Context.Count > 0)
        {
            // Plain text in one voice: any speaker's line fits.
            var limits = new ContextLimits(group.Select(p => p.Item.Speaker).ToHashSet(), fusion?.Speakers ?? int.MaxValue,
                group.Sum(p => p.Item.Text!.Length), fusion?.MaxChars ?? manifest.MaxChars);
            var (bc, ac) = (first.Speaker.Context, group[^1].Speaker.Context);
            if (bc.HasContext) Gather(before, bc.Before, bc.MaxChars, limits, Enumerable.Range(0, first.Index).Reverse().Select(i => items[i]), keepEnd: true);
            before.Reverse();
            if (ac.HasContext) Gather(after, ac.After, ac.MaxChars, limits, Enumerable.Range(lastIndex + 1, items.Count - lastIndex - 1).Select(i => items[i]), keepEnd: false);
        }
        if (group.Count == 1 && before.Count + after.Count == 0) return null;
        if (fusion == null) return TextContextJob(first, before, after, priority);

        var parts = new List<GeneratePart>();
        var contextAudio = new List<(GeneratePart, string)>();
        var members = new List<FusedMember>();
        foreach (var c in before) parts.Add(ContextPart(c, manifest, contextAudio));
        foreach (var p in group)
        {
            members.Add(new FusedMember(p.Item.Id, parts.Count, p.Item.Text!, ComputeHash(p.Item)));
            parts.Add(new GeneratePart
            {
                Role = PartRoles.Generate,
                Speaker = p.Item.Speaker ?? "",
                Text = p.Item.Text,
                Voice = ResolveVoice(p.Speaker.Voice),
                Settings = p.PerVoice,
            });
        }
        foreach (var c in after) parts.Add(ContextPart(c, manifest, contextAudio));

        var request = new GenerateRequest { Settings = first.Shared, Seed = OneShot.SeedFrom(first.Settings), Parts = parts };
        return FusedJob(group, members, parts, contextAudio, request, priority);
    }

    /// <summary>
    /// Context for a worker that doesn't fuse: an ordinary request whose text is the context lines and the segment in a
    /// row, all in the segment's voice and settings. The worker can't say where the segment is, so transcription finds it.
    /// </summary>
    private GenerationJob TextContextJob(FusionPlan plan, List<ContextLine> before, List<ContextLine> after, JobPriority priority)
    {
        // Not sent as parts: they describe the lines the one text is made of, for cutting and the record.
        GeneratePart Line(ContextLine l, string role) => new() { Role = role, Speaker = l.Item.Speaker ?? "", Text = l.Text };
        var parts = before.Select(i => Line(i, PartRoles.Context))
            .Append(new GeneratePart { Speaker = plan.Item.Speaker ?? "", Text = plan.Item.Text, Voice = ResolveVoice(plan.Speaker.Voice) })
            .Concat(after.Select(i => Line(i, PartRoles.Context))).ToList();
        var member = new FusedMember(plan.Item.Id, before.Count, plan.Item.Text!, ComputeHash(plan.Item));
        var request = new GenerateRequest
        {
            // A blank line between the lines, so the model reads each as a paragraph of its own.
            Text = string.Join("\n\n", parts.Select(p => p.Text)),
            Voice = ResolveVoice(plan.Speaker.Voice),
            Settings = plan.Settings,
            Seed = OneShot.SeedFrom(plan.Settings),
        };
        return FusedJob([plan], [member], parts, [], request, priority);
    }

    /// <summary>The job of a fused generation: its audio goes to the cache, and <see cref="CutFused"/> makes the takes.</summary>
    private GenerationJob FusedJob(List<FusionPlan> group, List<FusedMember> members, List<GeneratePart> parts,
        List<(GeneratePart, string)> contextAudio, GenerateRequest request, JobPriority priority)
    {
        var first = group[0];
        var tag = new FusedJobTag(this)
        {
            Members = members,
            Parts = parts,
            RecordId = Host.Settings.KeepFusedGenerations ? DateTime.Now.ToString("yyyyMMdd-HHmmss-") + Guid.NewGuid().ToString("N")[..6] : null,
            Listeners = first.Listeners,
            KeepsTranscripts = first.Listeners is { Hearer: { } hearer, CanCut: true } && group.All(p => p.Speaker.Transcriber == hearer.Id),
            ContextAudio = contextAudio,
            FusedPath = Path.Combine(Paths.CacheDir, "fused", Guid.NewGuid().ToString("N") + ".wav"),
        };
        var text = first.Item.Text!;
        if (text.Length > 50) text = text[..47] + "…";
        var place = group.Count > 1 ? $"{first.Chapter + 1}.{first.Index + 1}–{group[^1].Index + 1}" : $"{first.Chapter + 1}.{first.Index + 1}";
        var what = group.Count > 1 ? $"{group.Count} segments" : "with context";
        return new GenerationJob
        {
            ItemId = first.Item.Id,
            WorkerId = first.Manifest.Id,
            Priority = priority,
            ReadingOrder = first.Chapter * 1_000_000L + first.Index,
            InputHash = members[0].InputHash,
            Request = request,
            FixedOutPath = tag.FusedPath,
            Members = members.Select(m => new JobMember { ItemId = m.ItemId, SegmentDir = Paths.SegmentAudioDir(m.ItemId) }).ToList(),
            Label = $"{place}  {text} ({what})",
            Tag = tag,
            BeforeRun = contextAudio.Count > 0 ? DecodeContextAudio : null,
            AfterRun = CutFused,
        };
    }

    /// <summary>
    /// A line of context. Its voice (and the settings that belong to the voice) only when its speaker uses the same
    /// worker: another worker's voice means nothing to this one. Its take, when it is current and the worker can hear
    /// context, is decoded for it before the run.
    /// </summary>
    private GeneratePart ContextPart(ContextLine line, WorkerManifest manifest, List<(GeneratePart, string)> contextAudio)
    {
        var item = line.Item;
        var part = new GeneratePart { Role = PartRoles.Context, Speaker = item.Speaker ?? "", Text = line.Text };
        if (Project.FindSpeaker(item.Speaker) is { } speaker && speaker.Worker == manifest.Id)
        {
            part.Voice = ResolveVoice(speaker.Voice);
            part.Settings = SplitSettings(MergeSettings(speaker.Settings, item.SettingsOverride, manifest), manifest).PerVoice;
        }
        // A shortened line still sends its whole take: the worker trims the audio itself if it needs to.
        if (manifest.Fusion!.TakesContext(FusionContext.Audio) && StateOf(item) == SegmentAudioState.Ready && item.SelectedTake is int take)
            contextAudio.Add((part, Takes.TakeAudio(item.Id, take)));
        return part;
    }

    /// <summary>Before the run (a scheduler thread): the context takes as WAV, which is what workers read.</summary>
    private Task DecodeContextAudio(GenerationJob job, CancellationToken ct) => Task.Run(() =>
    {
        var tag = (FusedJobTag)job.Tag!;
        foreach (var (part, audio) in tag.ContextAudio)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(audio)) continue; // deleted meanwhile: the worker gets the text
            if (Path.GetExtension(audio).Equals(".wav", StringComparison.OrdinalIgnoreCase)) { part.AudioPath = audio; continue; }
            var wav = Path.Combine(Path.GetDirectoryName(tag.FusedPath)!, Guid.NewGuid().ToString("N") + ".wav");
            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            AudioDecoder.WriteWav(AudioDecoder.Decode(audio), wav);
            lock (tag.TempFiles) tag.TempFiles.Add(wav);
            part.AudioPath = wav;
        }
    }, ct);

    /// <summary>
    /// After the run (a scheduler thread): cuts the members' takes out where the worker said they are. Without that,
    /// prepares the copy the transcription worker listens to; without a transcription worker either, every member is
    /// left to be generated on its own.
    /// </summary>
    private Task CutFused(GenerationJob job)
    {
        var tag = (FusedJobTag)job.Tag!;
        tag.Generation = job;
        foreach (var m in job.Members!) tag.TakeNumbers[m.ItemId] = m.TakeNumber;
        var spans = job.Result?.Spans;
        if (spans != null && tag.Members.All(m => spans[m.Part] is { } s && s.EndMs > s.StartMs))
        {
            var audio = AudioDecoder.Decode(tag.FusedPath);
            var fusion = new TakeFusion { Segments = tag.Members.Count, Context = tag.ContextCount, Split = TakeFusion.Worker, Record = tag.RecordId };
            foreach (var m in tag.Members)
            {
                var span = spans[m.Part]!;
                var info = SaveMemberTake(job, tag, m, audio, span.StartMs, span.EndMs, fusion);
                tag.Taken.Add(new FusedTake(m.ItemId, info.Take, m.InputHash, false));
            }
        }
        else if (tag.Listeners.CanCut)
        {
            tag.TranscriptWav = Path.ChangeExtension(tag.FusedPath, ".16k.wav");
            Transcription.PrepareAudio(tag.FusedPath, tag.TranscriptWav);
        }
        else
        {
            Host.Log.LogWarning("{Worker} didn't say where the parts are, and no transcription worker is set to find them", job.WorkerId);
            foreach (var m in tag.Members)
            {
                tag.Uncut.Add(m.ItemId);
                tag.Notes[m.ItemId] = "Not cut out: the worker didn't say where it is, and no transcription worker is set.";
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Writes a member's piece of the fused audio as its take and saves it like a generated one.</summary>
    private TakeInfo SaveMemberTake(GenerationJob job, FusedJobTag tag, FusedMember m, DecodedAudio audio, int startMs, int endMs, TakeFusion fusion)
    {
        var take = tag.TakeNumbers[m.ItemId];
        var wav = Paths.TakeWav(m.ItemId, take);
        var from = Math.Clamp((long)startMs * audio.SampleRate / 1000, 0, audio.Frames);
        var to = Math.Clamp((long)endMs * audio.SampleRate / 1000, from, audio.Frames);
        var samples = audio.Samples.AsSpan((int)(from * audio.Channels), (int)((to - from) * audio.Channels)).ToArray();
        AudioDecoder.WriteWav(new DecodedAudio(samples, audio.SampleRate, audio.Channels), wav);
        tag.Cuts[m.ItemId] = (startMs, endMs);
        return SaveTake(job, m.ItemId, take, wav, m.InputHash, job.Result?.Seed ?? job.Request.Seed, m.Text.Length, fusion);
    }

    /// <summary>
    /// After the transcription of a fused generation (a scheduler thread): finds each part's words, cuts between the
    /// parts (halfway through the gap between one part's last word and the next one's first) and saves the members'
    /// takes with their share of the transcript. A member whose first or last word wasn't heard at all, or with many
    /// words wrong, isn't cut: it is generated on its own instead. A word heard as another ("Moor" as "More") still
    /// says where the part starts or ends.
    /// </summary>
    private Task CutByTranscript(GenerationJob job)
    {
        var tag = ((SplitJobTag)job.Tag!).Fused;
        var generation = tag.Generation!; // what the takes record: the generating worker, not this transcription
        var transcript = job.Transcript!;

        // Each part's place in the joined text, then its heard span from the words that line up with it.
        var heard = Transcription.Heard(transcript);
        tag.Transcript = heard;
        tag.TranscriptBlocks = Transcription.Blocks(transcript);
        var (ranges, words) = PartWords(tag, generation.WorkerId, heard);
        HeardSpan? Heard(int part)
        {
            var ok = words[part].Where(w => w.Kind is WordMatch.Match or WordMatch.Close or WordMatch.Substituted).ToList();
            return ok.Count == 0 ? null : new HeardSpan(ok[0].StartMs, ok[^1].EndMs, ok[^1].StartMs, ok[0].EndMs);
        }
        List<HeardSpan?> spans;
        if (transcript.TimedByBlocks)
        {
            // Timed blocks by voice, not words: the parts are cut at the blocks' edges, when each part is whole blocks.
            // (When they aren't and the speaker has an aligner, it has timed the words instead and this isn't reached.)
            if (BlockSpans(tag, generation.WorkerId, transcript) is not { } blockSpans)
            {
                Host.Log.LogInformation("Not cutting the fused generation: the transcriber's blocks don't line up with the parts");
                foreach (var m in tag.Members)
                {
                    tag.Notes[m.ItemId] = "Not cut out: the transcription's blocks of text don't line up with the segments, and no aligner is set to time the words.";
                    tag.Uncut.Add(m.ItemId);
                }
                return Task.CompletedTask;
            }
            spans = blockSpans;
        }
        else
            spans = Enumerable.Range(0, ranges.Count).Select(Heard).ToList();

        var audio = AudioDecoder.Decode(tag.FusedPath);
        var durationMs = (int)audio.DurationMs;
        var levels = new FrameLevels(audio);
        var fusion = new TakeFusion { Segments = tag.Members.Count, Context = tag.ContextCount, Split = TakeFusion.Transcript, Record = tag.RecordId };
        foreach (var m in tag.Members)
        {
            var own = words[m.Part];
            var expected = own.Where(w => w.Kind != WordMatch.Extra).ToList();
            var errors = own.Count(w => w.IsError);
            if (spans[m.Part] is not { } span || expected.Count == 0 || expected[0].Kind == WordMatch.Missing
                || expected[^1].Kind == WordMatch.Missing || errors * 3 > expected.Count)
            {
                var heardText = string.Join(" ", own.Select(w => w.Heard ?? $"[{w.Expected}?]"));
                Host.Log.LogInformation("Not cutting out \"{Text}\": heard {Heard} ({Errors} of {Words} words wrong)", m.Text,
                    heardText, errors, expected.Count);
                tag.Notes[m.ItemId] = expected.Count > 0 && (expected[0].Kind == WordMatch.Missing || expected[^1].Kind == WordMatch.Missing)
                    ? $"Not cut out: its first or last word wasn't heard (heard \"{heardText}\")."
                    : $"Not cut out: {errors} of {expected.Count} words wrong (heard \"{heardText}\").";
                tag.Uncut.Add(m.ItemId);
                continue;
            }
            // The cuts: in the pause just outside this part's own words, keeping 3/4 of it, so whatever comes before or
            // after is left out even when the transcription missed it (a context line not heard at all). The pause is
            // looked for near the edge words only, not past the neighbours' words. Without one, in the quietest stretch
            // between this part and the nearest heard one (the file's ends where there is none).
            var prev = Enumerable.Range(0, m.Part).Reverse().Select(i => spans[i]).FirstOrDefault(s => s != null);
            var next = Enumerable.Range(m.Part + 1, ranges.Count - m.Part - 1).Select(i => spans[i]).FirstOrDefault(s => s != null);
            var before = levels.LongestPause(Math.Max(prev?.LastWordStart ?? 0, span.Start - FrameLevels.PauseSearchMs),
                span.FirstWordEnd, span.Start);
            var after = levels.LongestPause(span.LastWordStart,
                Math.Min(next?.FirstWordEnd ?? durationMs, span.End + FrameLevels.PauseSearchMs), span.End);
            var start = before is { } b ? b.Start + (b.End - b.Start) / 4 : prev is { } p ? levels.CutBetween(p, span) : 0;
            var end = after is { } a ? a.Start + (a.End - a.Start) * 3 / 4 : next is { } n ? levels.CutBetween(span, n) : durationMs;
            if (prev is { } p2 && p2.End > span.Start || next is { } n2 && span.End > n2.Start || end <= start)
            {
                Host.Log.LogInformation("Not cutting out \"{Text}\": its words overlap a neighbour's", m.Text);
                tag.Notes[m.ItemId] = "Not cut out: its words overlap a neighbour's.";
                tag.Uncut.Add(m.ItemId);
                continue;
            }
            var info = SaveMemberTake(generation, tag, m, audio, start, end, fusion);
            if (!tag.KeepsTranscripts)
            {
                // Timed by an aligner (or another worker than the speaker's transcriber): transcribed afterwards.
                lock (tag.Taken) tag.Taken.Add(new FusedTake(m.ItemId, info.Take, m.InputHash, false));
                continue;
            }

            // Its share of the transcript, timed from the start of its own file, saved as its transcription.
            // A word belongs where its middle is: Parakeet runs a word's end on into the pause after it, and can start the
            // next line's first word early, in that pause, before the cut. Its times are kept within the take.
            // The blocks its words came from go with them (renumbered), so a timed word can still be traced to its voice.
            var mineStored = new StoredTranscript
            {
                Worker = tag.Listeners.Names, Language = transcript.Language, Words = heard, Blocks = tag.TranscriptBlocks,
            }.Slice(start, end);
            var mine = mineStored.Words;
            mineStored.Save(Paths.TakeTranscript(m.ItemId, info.Take));
            var own2 = TranscriptCheck.Compare(SpokenText(generation.WorkerId, m.Text), mine);
            info.TranscriptErrors = own2.Errors;
            info.TranscriptWordCount = own2.ExpectedCount;
            TakeStore.SaveTakeInfo(Paths, m.ItemId, info);
            Takes.Add(m.ItemId, info);
            lock (tag.Taken) tag.Taken.Add(new FusedTake(m.ItemId, info.Take, m.InputHash, true));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Each part's place in the joined text and the comparison words that belong to it. The generating worker's inline
    /// tags are blanked out in place: they aren't said, and the parts keep their places.
    /// </summary>
    private (List<(int Start, int End)> Ranges, List<List<WordComparison>> Words) PartWords(FusedJobTag tag, string generatorId, List<HeardWord> heard)
    {
        var joined = SpokenText(generatorId, string.Join("\n", tag.PartTexts));
        var ranges = new List<(int Start, int End)>();
        var at = 0;
        foreach (var t in tag.PartTexts) { ranges.Add((at, at + t.Length)); at += t.Length + 1; }
        var check = TranscriptCheck.Compare(joined, heard);
        return (ranges, ranges.Select(r => check.Words.Where(w => w.TextStart >= r.Start && w.TextStart < r.End).ToList()).ToList());
    }

    /// <summary>
    /// Where each part is, from a transcript of timed blocks by voice (<see cref="ListenMode.TranscribeDiarize"/>), or null
    /// when the blocks don't line up with the parts and can't be cut by: a block that holds words of two parts (the
    /// same voice speaking on from one part to the next, say), a segment none of whose words were heard, or parts that
    /// come out of order or overlap. Nothing short of whole blocks is used, because a block says nothing about where
    /// inside it a word is. The spans are the blocks', so a cut next to one is looked for in the gap to its neighbour.
    /// </summary>
    private List<HeardSpan?>? BlockSpans(FusedJobTag tag, string generatorId, TranscriptResult transcript)
    {
        if (!transcript.TimedByBlocks) return null;
        var heard = Transcription.Heard(transcript);
        var (ranges, words) = PartWords(tag, generatorId, heard);
        var owner = new Dictionary<int, int>(); // heard block -> the part its words belong to
        var spans = new List<HeardSpan?>();
        for (var part = 0; part < ranges.Count; part++)
        {
            int? start = null, end = null;
            foreach (var w in words[part].Where(w => w.Kind is WordMatch.Match or WordMatch.Close or WordMatch.Substituted))
                foreach (var unit in new[] { w.Unit, w.LastUnit }.Where(u => u >= 0).Distinct())
                {
                    if (owner.TryGetValue(unit, out var other) && other != part) return null;
                    owner[unit] = part;
                    start = Math.Min(start ?? heard[unit].StartMs, heard[unit].StartMs);
                    end = Math.Max(end ?? heard[unit].EndMs, heard[unit].EndMs);
                }
            spans.Add(start is { } s && end is { } e ? new HeardSpan(s, e, e, s) : null);
        }
        if (tag.Members.Any(m => spans[m.Part] == null)) return null;
        HeardSpan? last = null;
        foreach (var span in spans.OfType<HeardSpan>())
        {
            if (last != null && last.End > span.Start) return null; // out of order, or talking over each other
            last = span;
        }
        return spans;
    }

    /// <summary>The segments a job is for: its own, or for the transcription that cuts a fused generation, that one's.</summary>
    public static IEnumerable<Guid> SegmentsOf(GenerationJob job) =>
        job.Tag is SplitJobTag s ? s.Fused.Members.Select(m => m.ItemId) : job.Items;

    /// <summary>How <paramref name="itemId"/> takes part in a fused generation job (or its cutting transcription); null for any other job.</summary>
    public static FusionRoles? RolesOf(GenerationJob job, Guid itemId)
    {
        var tag = job.Tag switch { FusedJobTag f => f, SplitJobTag s => s.Fused, _ => null };
        if (tag == null) return null;
        var k = tag.Members.FindIndex(m => m.ItemId == itemId);
        if (k < 0) return null;
        var last = tag.Members.Count - 1;
        return new FusionRoles(
            ContextBefore: k == 0 && tag.Members[0].Part > 0,
            ContextAfter: k == last && tag.Parts.Count - 1 - tag.Members[last].Part > 0,
            BatchBefore: k > 0,
            BatchAfter: k < last);
    }

    /// <summary>Whether the job is one of this project's fused generations, or the transcription that cuts one.</summary>
    public bool OwnsFusion(GenerationJob job) => job.Tag switch
    {
        FusedJobTag f => ReferenceEquals(f.Engine, this),
        SplitJobTag s => ReferenceEquals(s.Fused.Engine, this),
        _ => false,
    };

    /// <summary>
    /// On the model's thread, when a fused generation or its transcription ended: selects the new takes, and works out
    /// what comes next. The transcription that cuts it, when the worker didn't say where the parts are; each member that
    /// couldn't be cut out (or whose generation failed, or was refused) as a job of its own, without context.
    /// </summary>
    public FusionOutcome FinishFusion(GenerationJob job)
    {
        var outcome = new FusionOutcome();
        var split = job.Tag as SplitJobTag;
        var tag = split?.Fused ?? (FusedJobTag)job.Tag!;
        var pending = false;

        if (job.State == JobState.Cancelled)
        {
            outcome.Cancelled.AddRange(tag.Members.Select(m => m.ItemId));
        }
        else if (job.State != JobState.Completed)
        {
            Host.Log.LogWarning("Fused generation {Label} failed ({Error}); generating its segments one by one", job.Label, job.LastError);
            foreach (var m in tag.Members) Fallback(m.ItemId, job.Priority, outcome);
        }
        else if (split == null && tag.TranscriptWav != null)
        {
            outcome.FollowUps.Add(SplitJob(job, tag));
            pending = true;
        }
        else
        {
            foreach (var t in tag.Taken)
                if (Project.FindItem(t.ItemId) is (Chapter chapter, int index))
                {
                    var item = chapter.Items[index];
                    var selected = ApplyTake(item, t.Take, t.InputHash);
                    outcome.Takes.Add((item, t.Take, selected, t.Transcribed));
                }
            foreach (var id in tag.Uncut) Fallback(id, job.Priority, outcome);
        }

        if (!pending)
        {
            if (tag.RecordId != null && tag.Generation != null && File.Exists(tag.FusedPath))
            {
                try { SaveRecord(tag, split != null ? job : null); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Host.Log.LogWarning(e, "Could not keep the fused generation {Id}", tag.RecordId);
                }
            }
            foreach (var file in tag.TempFiles.Append(tag.FusedPath).Append(tag.TranscriptWav).OfType<string>())
                try { File.Delete(file); } catch (IOException) { /* cache */ }
        }
        return outcome;
    }

    /// <summary>
    /// Keeps a finished fused generation for the fused generation inspector: the worker's whole
    /// output, what it was sent, where each take was cut, and what the transcription heard.
    /// </summary>
    private void SaveRecord(FusedJobTag tag, GenerationJob? split)
    {
        var gen = tag.Generation!;
        var manifest = Catalog.Get(gen.WorkerId);
        var dir = Paths.FusionRecordDir(tag.RecordId!);
        Directory.CreateDirectory(dir);
        File.Move(tag.FusedPath, Path.Combine(dir, FusionRecord.AudioFile), overwrite: true);

        var spans = gen.Result?.Spans;
        var record = new FusionRecord
        {
            Id = tag.RecordId!,
            CreatedUtc = gen.FinishedUtc ?? DateTime.UtcNow,
            Worker = gen.WorkerId,
            Runtime = manifest?.Runtime,
            ModelVersion = manifest != null ? Host.ModelVersionOf(manifest) : "",
            Seed = gen.Result?.Seed ?? gen.Request.Seed,
            Device = gen.DeviceName,
            GenerationMs = gen is { StartedUtc: { } s, FinishedUtc: { } f } ? (int)(f - s).TotalMilliseconds : 0,
            Settings = (JsonObject)gen.Request.Settings.DeepClone(),
            RequestText = gen.Request.Parts == null ? gen.Request.Text : null,
            Split = tag.Transcript != null ? TakeFusion.Transcript : TakeFusion.Worker,
            Transcriber = tag.Transcript != null ? tag.Listeners.Names : null,
            Transcript = tag.Transcript,
            TranscriptBlocks = tag.TranscriptBlocks is { Count: > 0 } ? tag.TranscriptBlocks : null,
            Error = split is { State: JobState.Failed } ? $"The transcription failed: {split.LastError}" : null,
        };
        for (var i = 0; i < tag.Parts.Count; i++)
        {
            var p = tag.Parts[i];
            var member = tag.Members.FirstOrDefault(m => m.Part == i);
            var audio = tag.ContextAudio.FirstOrDefault(c => ReferenceEquals(c.Part, p)).Audio;
            var part = new FusionRecordPart
            {
                Role = p.Role,
                Speaker = p.Speaker,
                Text = p.Text ?? "",
                Voice = p.Voice,
                Settings = (JsonObject?)p.Settings?.DeepClone(),
                Audio = audio != null ? Paths.MakeRelative(audio) : null,
                SpanStartMs = spans?.ElementAtOrDefault(i)?.StartMs,
                SpanEndMs = spans?.ElementAtOrDefault(i)?.EndMs,
            };
            if (member != null)
            {
                part.ItemId = member.ItemId;
                part.Take = tag.Taken.FirstOrDefault(t => t.ItemId == member.ItemId)?.Take;
                if (tag.Cuts.TryGetValue(member.ItemId, out var cut)) (part.CutStartMs, part.CutEndMs) = cut;
                part.Note = tag.Notes.GetValueOrDefault(member.ItemId)
                            ?? (tag.Uncut.Contains(member.ItemId) || part.Take == null ? "Not cut out: generated again on its own." : null);
            }
            record.Parts.Add(part);
        }
        AtomicFile.WriteAllBytes(Path.Combine(dir, FusionRecord.RecordFile), CoreJson.SerializeToUtf8(record));
    }

    /// <summary>The job that transcribes a fused generation so <see cref="CutByTranscript"/> can cut it.</summary>
    /// <remarks>
    /// The transcription worker says what was heard (which is how a part that wasn't said shows) and an aligner, if the
    /// members use one, times those words; an aligner alone times the parts' text as given.
    /// </remarks>
    private GenerationJob SplitJob(GenerationJob fused, FusedJobTag tag) =>
        Transcription.Listen(tag.Listeners, tag.TranscriptWav!, SpokenText(fused.WorkerId, string.Join("\n", tag.PartTexts)), $"{fused.Label}: finding the segments",
            fused.Priority, new SplitJobTag(tag), fused.ReadingOrder, Voices(tag), afterRun: CutByTranscript,
            segmentsSuffice: heard => BlockSpans(tag, fused.WorkerId, heard) != null)
        ?? throw new InvalidOperationException("a fused generation without spans has nobody to listen to it"); // CanCut was checked when it was planned

    /// <summary>The voices of a fused generation by name, in the order they first speak (a hint for the transcription).</summary>
    private List<string> Voices(FusedJobTag tag) =>
        tag.Parts.Select(p => p.Speaker).Distinct()
            .Select(id => Project.FindSpeaker(id)?.DisplayName is { Length: > 0 } name ? name : id).ToList();

    /// <summary>A member generated again on its own, without context; or failed, when it can't be queued.</summary>
    private void Fallback(Guid itemId, JobPriority priority, FusionOutcome outcome)
    {
        if (Project.FindItem(itemId) is not (Chapter chapter, int index)) return; // deleted meanwhile
        var item = chapter.Items[index];
        var job = BuildJob(item, Project.Chapters.IndexOf(chapter), index, priority, out var error);
        if (job != null) { outcome.FollowUps.Add(job); return; }
        item.LastError = error ?? "Could not be generated.";
        outcome.Failed.Add(item);
    }

    /// <summary>
    /// Where a part's heard words are: from its first word's start to its last word's end, with its last word's start and
    /// its first word's end (how far a cut next to it may reach in).
    /// </summary>
    private sealed record HeardSpan(int Start, int End, int LastWordStart, int FirstWordEnd);

    /// <summary>
    /// The loudness of a fused generation in 10 ms frames, for putting a cut in the pause between two parts. Word times
    /// from transcription are rough: Parakeet works in 80 ms steps and runs a word's end on to the next word's start, so
    /// halfway between them can land in a word.
    /// </summary>
    private sealed class FrameLevels
    {
        private const int FrameMs = 10;
        private readonly float[] _rms;
        private readonly float _quiet;

        public FrameLevels(DecodedAudio audio)
        {
            var perFrame = Math.Max(1, audio.SampleRate * FrameMs / 1000) * audio.Channels;
            _rms = new float[(audio.Samples.Length + perFrame - 1) / perFrame];
            for (var f = 0; f < _rms.Length; f++)
            {
                double sum = 0;
                var end = Math.Min(audio.Samples.Length, (f + 1) * perFrame);
                for (var i = f * perFrame; i < end; i++) sum += audio.Samples[i] * audio.Samples[i];
                _rms[f] = (float)Math.Sqrt(sum / Math.Max(1, end - f * perFrame));
            }
            // Quiet: 20 dB below the speech (its loud frames), and never below digital silence.
            var sorted = _rms.Order().ToArray();
            var speech = sorted.Length == 0 ? 0 : sorted[(int)(sorted.Length * 0.9)];
            _quiet = Math.Max(speech * 0.1f, 1e-4f);
        }

        /// <summary>Shorter quiet stretches are within speech (a stop consonant, a breath), not a pause between lines.</summary>
        private const int MinPauseFrames = 120 / FrameMs;

        private bool Quiet(int frame) => _rms[frame] < _quiet;

        /// <summary>How far from a part's edge word its pause is looked for: word times can be off by half a second.</summary>
        public const int PauseSearchMs = 2000;

        /// <summary>
        /// The longest pause (quiet for at least 120 ms) that reaches into <paramref name="fromMs"/>–<paramref name="toMs"/>,
        /// whole (it may extend past them); of two as long, the one nearer <paramref name="nearMs"/>. Null when there is none.
        /// The longest rather than the nearest: a line's pause is longer than the gaps within it, and the word times that
        /// say where the line starts can be off.
        /// </summary>
        public (int Start, int End)? LongestPause(int fromMs, int toMs, int nearMs)
        {
            if (_rms.Length == 0) return null;
            var to = Math.Clamp(toMs / FrameMs, 0, _rms.Length - 1);
            (int Start, int End)? best = null;
            for (var f = Math.Clamp(fromMs / FrameMs, 0, _rms.Length - 1); f <= to; f++)
            {
                if (!Quiet(f)) continue;
                var (start, end) = Run(f);
                f = end;
                if (end - start < MinPauseFrames) continue;
                var run = (start * FrameMs, end * FrameMs);
                if (best is not { } b || end - start > (b.End - b.Start) / FrameMs
                    || end - start == (b.End - b.Start) / FrameMs && Distance(run, nearMs) < Distance(b, nearMs))
                    best = run;
            }
            return best;
        }

        private static int Distance((int Start, int End) run, int ms) =>
            ms < run.Start ? run.Start - ms : ms > run.End ? ms - run.End : 0;

        /// <summary>The quiet frames around <paramref name="frame"/> (a quiet one): first, and one past the last.</summary>
        private (int Start, int End) Run(int frame)
        {
            var start = frame;
            while (start > 0 && Quiet(start - 1)) start--;
            var end = frame + 1;
            while (end < _rms.Length && Quiet(end)) end++;
            return (start, end);
        }

        /// <summary>
        /// The cut between part <paramref name="a"/> and the following part <paramref name="b"/>: the middle of the
        /// longest quiet stretch from <paramref name="a"/>'s last word's start to <paramref name="b"/>'s first word's end
        /// (the nearer to halfway between their words, of two as long), or halfway when there is no quiet at all.
        /// </summary>
        public int CutBetween(HeardSpan a, HeardSpan b)
        {
            var halfway = (a.End + b.Start) / 2;
            var from = Math.Clamp(Math.Min(a.LastWordStart, halfway) / FrameMs, 0, _rms.Length);
            var to = Math.Clamp(Math.Max(b.FirstWordEnd, halfway) / FrameMs, from, _rms.Length);
            (int Start, int Length) best = (-1, 0);
            for (var f = from; f < to;)
            {
                if (_rms[f] >= _quiet) { f++; continue; }
                var run = f;
                while (f < to && _rms[f] < _quiet) f++;
                var length = f - run;
                var middle = (run + f) * FrameMs / 2;
                if (length > best.Length || length == best.Length
                    && Math.Abs(middle - halfway) < Math.Abs((best.Start * 2 + best.Length) * FrameMs / 2 - halfway))
                    best = (run, length);
            }
            return best.Start < 0 ? halfway : (best.Start * 2 + best.Length) * FrameMs / 2;
        }
    }

    /// <summary>What a fused generation may still take in: its voices so far and how many it may have, its characters.</summary>
    private sealed class ContextLimits(HashSet<string?> voices, int maxVoices, int chars, int maxChars)
    {
        /// <summary>How many more characters the generation takes.</summary>
        public int Room => maxChars - chars;

        public bool AllowsVoice(string? speaker) => voices.Contains(speaker) || voices.Count < maxVoices;

        public void Add(string? speaker, int length)
        {
            chars += length;
            voices.Add(speaker);
        }
    }

    /// <summary>A line of context: the segment it comes from and the text sent for it (shortened when the segment's whole text doesn't fit).</summary>
    private readonly record struct ContextLine(TimelineItem Item, string Text);

    /// <summary>The fewest characters worth sending as a shortened line of context.</summary>
    private const int MinContextChars = 20;

    /// <summary>
    /// Adds up to <paramref name="lines"/> spoken lines from <paramref name="candidates"/> (nearest first) to
    /// <paramref name="into"/>, looking past <see cref="Transparent"/> items. Stops at a clip, at a segment whose speaker
    /// doesn't include context (that is also what lets a speaker's lines be context for others), and at a line whose
    /// voice the worker can't take. A line that would go past <paramref name="maxChars"/> on this side or past what the
    /// worker takes in one go is shortened at a word instead of dropped (<see cref="Shorten"/>), and is the last one.
    /// </summary>
    private void Gather(List<ContextLine> into, int lines, int maxChars, ContextLimits limits, IEnumerable<TimelineItem> candidates, bool keepEnd)
    {
        var chars = 0;
        foreach (var c in candidates)
        {
            if (into.Count >= lines) break;
            if (Transparent(c)) continue;
            if (c.Kind != ItemKind.Tts || Project.FindSpeaker(c.Speaker)?.Context.Include != true) break;
            if (string.IsNullOrWhiteSpace(c.Text)) continue;
            if (!limits.AllowsVoice(c.Speaker)) break;
            var text = c.Text!;
            var room = Math.Min(maxChars - chars, limits.Room);
            if (text.Length > room)
            {
                if (Shorten(text, room, keepEnd) is not { } shorter) break;
                text = shorter;
            }
            chars += text.Length;
            limits.Add(c.Speaker, text.Length);
            into.Add(new ContextLine(c, text));
            if (text.Length < c.Text!.Length) break; // the room is used up
        }
    }

    /// <summary>
    /// <paramref name="text"/> cut down to at most <paramref name="room"/> characters at a word: the end of it when
    /// <paramref name="keepEnd"/> (context before a segment is nearest at its end), else the start. Null when too little would be left.
    /// </summary>
    internal static string? Shorten(string text, int room, bool keepEnd)
    {
        if (room < MinContextChars) return null;
        string cut;
        if (keepEnd)
        {
            var slice = text[^room..];
            // Cut at a word start: the slice starts on one when the character before it is a space.
            var startsWord = char.IsWhiteSpace(text[text.Length - room - 1]);
            var i = startsWord ? 0 : slice.IndexOfAny([' ', '\t', '\r', '\n']);
            if (i < 0) return null;
            cut = slice[i..].Trim();
        }
        else
        {
            var slice = text[..room];
            var endsWord = char.IsWhiteSpace(text[room]);
            var i = endsWord ? slice.Length : slice.LastIndexOfAny([' ', '\t', '\r', '\n']);
            if (i < 0) return null;
            cut = slice[..i].Trim();
        }
        return cut.Length >= MinContextChars ? cut : null;
    }
}
