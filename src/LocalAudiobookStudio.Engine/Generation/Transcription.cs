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

    /// <summary>A job that transcribes <paramref name="wavPath"/> (prepared with <see cref="PrepareAudio"/>).</summary>
    public static GenerationJob Job(WorkerManifest worker, string wavPath, string? expectedText, JsonObject settings,
        string label, JobPriority priority, object? tag = null, long readingOrder = 0) =>
        new()
        {
            WorkerId = worker.Id,
            Priority = priority,
            ReadingOrder = readingOrder,
            Label = label,
            Tag = tag,
            Transcribe = new TranscribeRequest { AudioPath = wavPath, Text = expectedText, Settings = settings },
        };

    public static TranscriptCheck Check(string expectedText, TranscriptResult transcript) =>
        TranscriptCheck.Compare(expectedText, transcript.Words.Select(w => new HeardWord(w.Text, w.StartMs, w.EndMs, w.Confidence)).ToList());
}
