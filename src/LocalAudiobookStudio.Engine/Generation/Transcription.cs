using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Engine;

/// <summary>
/// Who listens to a speaker's audio (<c>ProjectEngine.ListenersFor</c>): the worker that hears the words (tagged
/// Transcription) and the one that times them, when the speaker uses an aligner. Either may be missing. A diarizer, if
/// there is one (tagged Diarization), listens after them and gives each heard word the voice of the turn it falls in.
/// </summary>
public sealed record Listeners(WorkerManifest? Hearer, JsonObject HearerSettings, WorkerManifest? Aligner, JsonObject AlignerSettings,
    WorkerManifest? Diarizer = null, JsonObject? DiarizerSettings = null)
{
    public static readonly Listeners None = new(null, new JsonObject(), null, new JsonObject());

    /// <summary>The words come with times: there is an aligner, or the hearer times them itself.</summary>
    public bool TimesWords => Aligner != null || Hearer?.CanAlign == true;

    /// <summary>The hearer tells voices apart and answers with timed blocks of text (<see cref="ListenMode.TranscribeDiarize"/>).</summary>
    public bool CanSegment => Hearer?.CanDiarize == true;

    /// <summary>
    /// Something can say where each of several lines of one recording is: word times, or blocks by voice that may line up
    /// with the lines (and, when they don't, the aligner that times the words instead).
    /// </summary>
    public bool CanCut => TimesWords || CanSegment;

    /// <summary>The same workers and settings (speakers listened to the same way can share a fused generation).</summary>
    public string Key => $"{Hearer?.Id}|{Hearer?.Runtime}|{Core.Hashing.InputHasher.Canonicalize(HearerSettings)}|" +
                         $"{Aligner?.Id}|{Aligner?.Runtime}|{Core.Hashing.InputHasher.Canonicalize(AlignerSettings)}|" +
                         $"{Diarizer?.Id}|{Diarizer?.Runtime}|{Core.Hashing.InputHasher.Canonicalize(DiarizerSettings ?? new JsonObject())}";

    /// <summary>Who listened, for logs and records: "parakeet", or "parakeet + mms_fa + pyannote".</summary>
    public string Names => string.Join(" + ", new[] { Hearer?.Id, Aligner?.Id, Diarizer?.Id }.OfType<string>());
}

/// <summary>
/// Transcribing audio with a worker tagged Transcription or Alignment, and comparing the result with the text the
/// audio should contain (<see cref="TranscriptCheck"/>).
/// </summary>
public static class Transcription
{
    /// <summary>
    /// Workers get 16 kHz mono 16-bit WAV, whatever the take's format: what speech recognition models expect, and a
    /// worker needs no decoder for Opus or MP3.
    /// </summary>
    public const int SampleRate = 16_000;

    /// <summary>Writes <paramref name="source"/> (WAV, Opus or MP3) as the WAV a transcription worker reads.</summary>
    public static void PrepareAudio(string source, string wavPath)
    {
        var decoded = AudioDecoder.Decode(source);
        var mono = new DecodedAudio(AudioDecoder.ToMono(decoded), decoded.SampleRate, 1);
        // Resampled the way torchaudio does (see SincResampler): models listen to what that filter leaves.
        AudioDecoder.WriteWav(mono.SampleRate == SampleRate ? mono
            : new DecodedAudio(SincResampler.Resample(mono.Samples, mono.SampleRate, SampleRate), SampleRate, 1), wavPath);
    }

    /// <summary>
    /// A job that listens to <paramref name="wavPath"/> (prepared with <see cref="PrepareAudio"/>), asking the worker for
    /// as much as it can do (<see cref="WorkerManifest.ListenMode"/>): the words heard with their times when it is tagged
    /// both Transcription and Alignment. <paramref name="mode"/> asks for less (a text-only transcription).
    /// </summary>
    /// <param name="speakers">Who may be speaking, in order, when known: a hint for workers that tell voices apart.</param>
    /// <param name="speakerCount">How many voices there are, when known (else the number of <paramref name="speakers"/>).</param>
    /// <param name="afterRun">Runs when it is done (see <see cref="GenerationJob.AfterRun"/>).</param>
    /// <param name="then">The job that continues it (see <see cref="GenerationJob.Then"/>).</param>
    public static GenerationJob Job(WorkerManifest worker, string wavPath, string? expectedText, JsonObject settings,
        string label, JobPriority priority, object? tag = null, long readingOrder = 0, ListenMode? mode = null,
        IReadOnlyList<string>? speakers = null, int? speakerCount = null, Func<GenerationJob, Task>? afterRun = null,
        Func<GenerationJob, GenerationJob?>? then = null) =>
        new()
        {
            WorkerId = worker.Id,
            Priority = priority,
            ReadingOrder = readingOrder,
            Label = label,
            Tag = tag,
            Transcribe = new TranscribeRequest
            {
                Mode = mode ?? worker.ListenMode,
                AudioPath = wavPath,
                Text = expectedText,
                Settings = settings,
                Speakers = speakers,
                SpeakerCount = speakerCount ?? speakers?.Count,
            },
            AfterRun = afterRun,
            Then = then,
        };

    /// <summary>
    /// The job that listens to <paramref name="wavPath"/> with these <paramref name="listeners"/>:
    /// <list type="bullet">
    /// <item>a hearer and an aligner: the hearer transcribes, then the aligner times the words it heard (what is
    /// actually there, mistakes included), and that second job finishes in the first one's place;</item>
    /// <item>a hearer alone: it transcribes, with times when it gives them;</item>
    /// <item>an aligner alone: it times <paramref name="expectedText"/> (it can't tell what was said).</item>
    /// </list>
    /// Null when there is nobody to listen. <paramref name="tag"/> and <paramref name="afterRun"/> go with whichever job
    /// finishes; a transcript's language comes from the hearer.
    /// </summary>
    /// <param name="segmentsSuffice">
    /// For a hearer that tells voices apart, with an aligner: the hearer is asked for timed blocks by voice (always, so the
    /// voices are known), and when this says they are all that is needed (they line up with what the caller is looking for),
    /// the aligner is not run and the blocks are the result. Otherwise the aligner times the words the hearer heard, as
    /// usual, and each timed word is given the voice and block it came from (<see cref="WithBlocksOf"/>).
    /// </param>
    public static GenerationJob? Listen(Listeners listeners, string wavPath, string? expectedText, string label, JobPriority priority,
        object? tag, long readingOrder, IReadOnlyList<string>? speakers, Func<GenerationJob, Task>? afterRun = null, int? speakerCount = null,
        Func<TranscriptResult, bool>? segmentsSuffice = null)
    {
        // With a diarizer, once the words are final it listens too, and its turns give each word a voice. (The hearer is
        // then not asked to tell voices apart: the diarizer is the one that does.)
        Func<GenerationJob, GenerationJob?>? voices = listeners.Diarizer is { } diarizer
            ? words => words.Transcript is { Words.Count: > 0 }
                ? Job(diarizer, wavPath, null, listeners.DiarizerSettings ?? new JsonObject(), $"{label} (voices)", priority, tag, readingOrder,
                    ListenMode.Diarize, speakers, speakerCount, async turns =>
                    {
                        if (turns.Transcript != null && turns.ContinuesFrom?.Transcript is { } heard)
                            turns.AmendTranscript(WithTurns(heard, turns.Transcript));
                        if (afterRun != null) await afterRun(turns).ConfigureAwait(false);
                    })
                : null // nothing heard: nobody to give a voice to
            : null;

        if (listeners is { Hearer: { } hearer, Aligner: { } aligner })
        {
            var blocks = hearer.CanDiarize && listeners.Diarizer == null;
            return Job(hearer, wavPath, expectedText, listeners.HearerSettings, label, priority, tag, readingOrder,
                blocks ? ListenMode.TranscribeDiarize : ListenMode.Transcribe, speakers, speakerCount, afterRun,
                then: heard => blocks && segmentsSuffice != null && heard.Transcript is { } blocksHeard && segmentsSuffice(blocksHeard)
                    ? null // the blocks are enough: the hearer's job finishes
                    : heard.Transcript?.Text is { Length: > 0 } text && text.Any(char.IsLetterOrDigit)
                        ? Job(aligner, wavPath, text, listeners.AlignerSettings, $"{label} (timing)", priority, tag, readingOrder,
                            ListenMode.Align, speakers, speakerCount, async timed =>
                            {
                                // The aligner knows the words, not who said them: they get the voice and block they came from.
                                if (timed.Transcript != null && timed.ContinuesFrom?.Transcript is { } hearerHeard)
                                    timed.AmendTranscript(WithBlocksOf(timed.Transcript, hearerHeard));
                                if (afterRun != null) await afterRun(timed).ConfigureAwait(false);
                            }, then: voices)
                        : null); // nothing heard: nothing to time
        }
        if (listeners.Hearer is { } alone)
            return Job(alone, wavPath, expectedText, listeners.HearerSettings, label, priority, tag, readingOrder,
                listeners.Diarizer != null && alone.CanDiarize ? alone.CanAlign ? ListenMode.TranscribeAlign : ListenMode.Transcribe : null,
                speakers, speakerCount, afterRun, then: voices);
        if (listeners.Aligner is { } aligning)
            return Job(aligning, wavPath, expectedText, listeners.AlignerSettings, label, priority, tag, readingOrder,
                ListenMode.Align, speakers, speakerCount, afterRun, then: voices);
        return null;
    }

    /// <summary>
    /// The words of <paramref name="heard"/> with the voice of the turn each falls in, from what a diarization worker said
    /// (<paramref name="turns"/>: segments with a speaker, no text): a word goes to the turn it overlaps most, and one in a
    /// gap to the nearest. The turns that hold words become the transcript's blocks (their text the words in them), and the
    /// words point to them; the times are the words' own.
    /// </summary>
    public static TranscriptResult WithTurns(TranscriptResult heard, TranscriptResult turns)
    {
        if (turns.Segments is not { Count: > 0 } all || heard.Words.Count == 0) return heard;
        int Turn(TranscriptWord w)
        {
            int best = -1, nearest = 0;
            long bestOverlap = 0, nearestGap = long.MaxValue;
            var middle = (w.StartMs + w.EndMs) / 2;
            for (var i = 0; i < all.Count; i++)
            {
                var overlap = Math.Min(w.EndMs, all[i].EndMs) - Math.Max(w.StartMs, all[i].StartMs);
                if (overlap > bestOverlap) { bestOverlap = overlap; best = i; }
                var gap = middle < all[i].StartMs ? all[i].StartMs - middle : middle > all[i].EndMs ? middle - all[i].EndMs : 0;
                if (gap < nearestGap) { nearestGap = gap; nearest = i; }
            }
            return best >= 0 ? best : nearest;
        }
        var turnOf = heard.Words.Select(Turn).ToList();
        var used = turnOf.Distinct().OrderBy(i => i).ToList();
        var renumbered = used.Select((turn, index) => (turn, index)).ToDictionary(x => x.turn, x => x.index);
        var words = heard.Words.Select((w, i) => w with { Speaker = all[turnOf[i]].Speaker, Block = renumbered[turnOf[i]] }).ToList();
        var blocks = used.Select(turn => new TranscriptSegment(
            string.Join(' ', words.Where((_, i) => turnOf[i] == turn).Select(w => w.Text)),
            all[turn].StartMs, all[turn].EndMs, all[turn].Speaker)).ToList();
        return heard with { Words = words, Segments = blocks };
    }

    public static TranscriptCheck Check(string expectedText, TranscriptResult transcript) =>
        TranscriptCheck.Compare(expectedText, Heard(transcript));

    /// <summary>A transcript's words as stored and compared (with who said each and the block it came from, when the worker says).</summary>
    public static List<HeardWord> Heard(TranscriptResult transcript) =>
        transcript.Words.Select(w => new HeardWord(w.Text, w.StartMs, w.EndMs, w.Confidence, w.Speaker, w.Block)).ToList();

    /// <summary>The blocks of speech by one voice a transcript's words point into, or null when it has none.</summary>
    public static List<HeardBlock>? Blocks(TranscriptResult transcript) =>
        transcript.Segments is { Count: > 0 } segments
            ? segments.Select(s => new HeardBlock(s.Text, s.StartMs, s.EndMs, s.Speaker)).ToList()
            : null;

    /// <summary>
    /// <paramref name="timed"/>, the words an aligner timed, with each word given the voice and block it was in when
    /// <paramref name="heard"/> (what a worker that tells voices apart heard, which the aligner was given the text of)
    /// said it, and the blocks themselves. The two have the same words in the same order, spelled alike up to what the
    /// aligner normalises; words are matched by their letters and digits, looking a little ahead when one is dropped or
    /// split, and a word that matches nothing takes the block of the one before it. Nothing changes when
    /// <paramref name="heard"/> has no blocks.
    /// </summary>
    public static TranscriptResult WithBlocksOf(TranscriptResult timed, TranscriptResult heard)
    {
        if (heard.Segments is not { Count: > 0 } blocks || timed.Words.Count == 0) return timed;
        static string Key(string token) => new(token.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var said = new List<(string Key, int Block)>();
        for (var b = 0; b < blocks.Count; b++)
            foreach (var token in blocks[b].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (Key(token) is { Length: > 0 } key) said.Add((key, b));
        if (said.Count == 0) return timed;

        const int Lookahead = 3;
        var at = 0;
        var current = said[0].Block;
        var words = new List<TranscriptWord>(timed.Words.Count);
        foreach (var w in timed.Words)
        {
            var key = Key(w.Text);
            if (key.Length > 0)
            {
                for (var k = at; k < Math.Min(said.Count, at + Lookahead + 1); k++)
                    if (said[k].Key == key) { current = said[k].Block; at = k + 1; break; }
                // (No match: the word keeps the block of the one before it, and the next one is looked for from the same place.)
            }
            words.Add(w with { Block = current, Speaker = w.Speaker ?? blocks[current].Speaker });
        }
        return timed with { Words = words, Segments = blocks };
    }
}
