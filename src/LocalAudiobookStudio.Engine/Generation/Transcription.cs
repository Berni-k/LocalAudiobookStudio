using System.Text.Json.Nodes;
using LocalAudiobookStudio.Audio.Decoding;
using LocalAudiobookStudio.Core.Transcripts;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Protocol;
using LocalAudiobookStudio.Workers.Scheduling;

namespace LocalAudiobookStudio.Engine;

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
    public static GenerationJob Job(WorkerManifest worker, string wavPath, string? expectedText, JsonObject settings,
        string label, JobPriority priority, object? tag = null, long readingOrder = 0, ListenMode? mode = null,
        IReadOnlyList<string>? speakers = null, int? speakerCount = null) =>
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
        };

    public static TranscriptCheck Check(string expectedText, TranscriptResult transcript) =>
        TranscriptCheck.Compare(expectedText, Heard(transcript));

    /// <summary>A transcript's words as stored and compared (with who said each, when the worker says).</summary>
    public static List<HeardWord> Heard(TranscriptResult transcript) =>
        transcript.Words.Select(w => new HeardWord(w.Text, w.StartMs, w.EndMs, w.Confidence, w.Speaker)).ToList();
}
