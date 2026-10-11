using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Engine;

/// <summary>
/// Who listens to a speaker's audio (<c>ProjectEngine.ListenersFor</c>): the worker that hears the words (tagged
/// Transcription) and the one that times them, when the speaker uses an aligner. Either may be missing.
/// </summary>
public sealed record Listeners(WorkerManifest? Hearer, JsonObject HearerSettings, WorkerManifest? Aligner, JsonObject AlignerSettings)
{
    public static readonly Listeners None = new(null, new JsonObject(), null, new JsonObject());

    /// <summary>The words come with times: there is an aligner, or the hearer times them itself.</summary>
    public bool TimesWords => Aligner != null || Hearer?.CanAlign == true;

    /// <summary>The same workers and settings (speakers listened to the same way can share a fused generation).</summary>
    public string Key => $"{Hearer?.Id}|{Hearer?.Runtime}|{Core.Hashing.InputHasher.Canonicalize(HearerSettings)}|" +
                         $"{Aligner?.Id}|{Aligner?.Runtime}|{Core.Hashing.InputHasher.Canonicalize(AlignerSettings)}";

    /// <summary>Who listened, for logs and records: "parakeet", or "parakeet + mms_fa".</summary>
    public string Names => string.Join(" + ", new[] { Hearer?.Id, Aligner?.Id }.OfType<string>());
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
        AudioDecoder.WriteWav(mono.SampleRate == SampleRate ? mono : AudioDecoder.Resample(mono, SampleRate), wavPath);
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
    public static GenerationJob? Listen(Listeners listeners, string wavPath, string? expectedText, string label, JobPriority priority,
        object? tag, long readingOrder, IReadOnlyList<string>? speakers, Func<GenerationJob, Task>? afterRun = null, int? speakerCount = null)
    {
        if (listeners is { Hearer: { } hearer, Aligner: { } aligner })
            return Job(hearer, wavPath, expectedText, listeners.HearerSettings, label, priority, tag, readingOrder,
                ListenMode.Transcribe, speakers, speakerCount, afterRun,
                then: heard => heard.Transcript?.Text is { Length: > 0 } text && text.Any(char.IsLetterOrDigit)
                    ? Job(aligner, wavPath, text, listeners.AlignerSettings, $"{label} (timing)", priority, tag, readingOrder,
                        ListenMode.Align, speakers, speakerCount, afterRun)
                    : null); // nothing heard: nothing to time
        if (listeners.Hearer is { } alone)
            return Job(alone, wavPath, expectedText, listeners.HearerSettings, label, priority, tag, readingOrder,
                speakers: speakers, speakerCount: speakerCount, afterRun: afterRun);
        if (listeners.Aligner is { } aligning)
            return Job(aligning, wavPath, expectedText, listeners.AlignerSettings, label, priority, tag, readingOrder,
                ListenMode.Align, speakers, speakerCount, afterRun);
        return null;
    }

    public static TranscriptCheck Check(string expectedText, TranscriptResult transcript) =>
        TranscriptCheck.Compare(expectedText, Heard(transcript));

    /// <summary>A transcript's words as stored and compared (with who said each, when the worker says).</summary>
    public static List<HeardWord> Heard(TranscriptResult transcript) =>
        transcript.Words.Select(w => new HeardWord(w.Text, w.StartMs, w.EndMs, w.Confidence, w.Speaker)).ToList();
}
