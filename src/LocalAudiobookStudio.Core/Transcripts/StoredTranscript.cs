using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Serialization;

namespace LocalAudiobookStudio.Core.Transcripts;

/// <summary>
/// What a transcription worker heard in a take, saved next to it as <c>take-NNNN.transcript</c>. Only the heard words
/// are stored: the comparison with the segment's text is made when it is shown, so it follows edits of the text.
/// Times are in the take's file (before any trim).
/// </summary>
public sealed class StoredTranscript
{
    /// <summary>The worker that listened.</summary>
    public string Worker { get; set; } = "";

    /// <summary>The language the worker reported, when it did.</summary>
    public string? Language { get; set; }

    public List<HeardWord> Words { get; set; } = [];

    public void Save(string path) => AtomicFile.WriteAllBytes(path, CoreJson.SerializeToUtf8(this));

    /// <summary>The transcript in <paramref name="path"/>, or null when there is none (or it can't be read).</summary>
    public static StoredTranscript? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? CoreJson.Deserialize<StoredTranscript>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null; // a damaged transcript only hides the markers; the take is fine
        }
    }
}
