using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;

namespace LocalAudiobookStudio.InternalWorker;

/// <summary>
/// A word as an engine heard it, in the file's time. <see cref="OpenEnd"/>: the engine doesn't know where it ends;
/// <see cref="OpenStart"/>: where it starts (the first word of a whisper.cpp segment: <see cref="StartMs"/> is only
/// where it can't start before). <see cref="TranscriptWords"/> finds them in the audio. <see cref="Speaker"/>: who said
/// it, from an engine that tells voices apart (worker tag Diarization), as its own label ("S1").
/// </summary>
internal sealed record HeardWord(string Text, long StartMs, long EndMs, double Confidence, bool OpenEnd = false, bool OpenStart = false,
    string? Speaker = null);

/// <summary>
/// What every transcribing engine does with its words before sending them: ends open words where the speech after them
/// stops, keeps words from overlapping, and (for models that invent text over silence) drops words heard in silence.
/// </summary>
internal static class TranscriptWords
{
    /// <summary>A word whose loudest 20 ms is this far below the take's speech (about -30 dB) was heard in silence.</summary>
    private const float SilentRatio = 0.03f;

    /// <summary>
    /// How far around a word to look for its sound. Loose on purpose: word times can be off by a few hundred ms (a
    /// final full stop runs on, a model times a word badly), a word's quiet edges ("th", a final "s" mostly above 8 kHz) are near silence at
    /// 16 kHz, and keeping a doubtful word is better than losing a real one. Invented words sit in longer silences.
    /// </summary>
    private const double MarginSec = 0.5;

    /// <summary>
    /// Below this (-60 dBFS) a word is in silence whatever the take's speech level: a take of silence has no speech level
    /// to compare with, and Whisper hears "Thank you." in it.
    /// </summary>
    private const float SilenceFloor = 0.001f;

    /// <param name="dropSilent">
    /// Drop words heard where the audio is near silence: for models that invent text over silence (Whisper). Others
    /// (transducers such as Parakeet) only write what they hear, so the check could only lose real words.
    /// </param>
    public static List<JsonObject> Finish(List<HeardWord> heard, float[] audio, bool dropSilent)
    {
        var levels = AudioPieces.FrameLevels(audio);
        var speech = AudioPieces.SpeechLevel(levels);
        var audioMs = audio.Length * 1000L / AudioPieces.SampleRate;
        var words = heard.Where(w => w.Text.Length > 0).OrderBy(w => w.StartMs).ToList();

        var result = new List<JsonObject>();
        var dropped = new List<string>();
        long previousEnd = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            // Starts where the speech up to its end starts, not before the word before it ends.
            if (w.OpenStart)
            {
                var limit = Math.Max(w.StartMs, previousEnd);
                w = w with { StartMs = Math.Clamp((long)Math.Round(1000 * AudioPieces.SpeechStart(levels, w.EndMs / 1000.0, limit / 1000.0, speech)), limit, Math.Max(limit, w.EndMs)) };
            }
            var nextStart = i + 1 < words.Count ? words[i + 1].StartMs : audioMs;
            var end = w.OpenEnd
                // Ends where the speech after it stops, before the next word at the latest.
                ? (long)Math.Round(1000 * AudioPieces.SpeechEnd(levels, w.StartMs / 1000.0, nextStart / 1000.0, speech))
                // A predicted end can run past the next word's start (a token's duration); words don't overlap.
                : Math.Min(w.EndMs, nextStart);
            end = Math.Max(w.StartMs, end);
            if (dropSilent && AudioPieces.PeakLevel(levels, w.StartMs / 1000.0 - MarginSec, end / 1000.0 + MarginSec) < Math.Max(SilentRatio * speech, SilenceFloor))
            {
                dropped.Add($"{w.Text} ({w.StartMs / 1000.0:0.00}-{end / 1000.0:0.00} s)");
                continue;
            }
            var word = new JsonObject
            {
                ["text"] = w.Text,
                ["startMs"] = w.StartMs,
                ["endMs"] = end,
                ["confidence"] = Math.Round(w.Confidence, 4),
            };
            if (w.Speaker != null) word["speaker"] = w.Speaker;
            result.Add(word);
            previousEnd = end;
        }
        if (dropped.Count > 0) Protocol.Log($"Dropped words heard in silence: {string.Join(" ", dropped)}");
        return result;
    }
}
