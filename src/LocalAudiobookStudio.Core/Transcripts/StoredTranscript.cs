using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Serialization;

namespace LocalAudiobookStudio.Core.Transcripts;

/// <summary>
/// What a transcription worker heard in a take, saved next to it as <c>take-NNNN.transcript</c>. Only what was heard is
/// stored: the comparison with the segment's text is made when it is shown, so it follows edits of the text.
/// Times are in the take's file (before any trim).
/// </summary>
public sealed class StoredTranscript
{
    /// <summary>The worker that listened.</summary>
    public string Worker { get; set; } = "";

    /// <summary>The language the worker reported, when it did.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// The words, in order. When the worker timed only blocks of text, each "word" is a whole block; when an aligner
    /// timed the words afterwards they are single words, each still tied to its block (<see cref="HeardWord.Block"/>).
    /// </summary>
    public List<HeardWord> Words { get; set; } = [];

    /// <summary>
    /// The stretches of speech by one voice that a worker telling voices apart heard, in order; null (and left out of the
    /// file) from any other worker. Words point into this list, through their <see cref="HeardWord.Block"/>.
    /// </summary>
    public List<HeardBlock>? Blocks { get; set; }

    /// <summary>
    /// The part of this transcript between <paramref name="startMs"/> and <paramref name="endMs"/>, timed from
    /// <paramref name="startMs"/>: the words whose middle is inside (a word belongs where its middle is), their times kept
    /// within the range, and the blocks they came from, renumbered. A block with all its words kept stays whole, clipped
    /// to the range; one cut across takes only the text and span of the words that stayed.
    /// </summary>
    public StoredTranscript Slice(int startMs, int endMs)
    {
        var words = Words.Where(w => (w.StartMs + w.EndMs) / 2 >= startMs && (w.StartMs + w.EndMs) / 2 < endMs).ToList();
        var blocks = new List<HeardBlock>();
        var renumbered = new Dictionary<int, int>();
        var sliced = new List<HeardWord>(words.Count);
        foreach (var w in words)
        {
            int? block = null;
            if (w.Block is { } b && Blocks is { } all && b >= 0 && b < all.Count)
            {
                if (!renumbered.TryGetValue(b, out var index))
                {
                    var kept = words.Where(x => x.Block == b).ToList();
                    var whole = kept.Count == Words.Count(x => x.Block == b);
                    var original = all[b];
                    blocks.Add(whole
                        ? original with { StartMs = Math.Max(original.StartMs, startMs) - startMs, EndMs = Math.Min(original.EndMs, endMs) - startMs }
                        : new HeardBlock(string.Join(' ', kept.Select(x => x.Text)), Math.Max(kept.Min(x => x.StartMs), startMs) - startMs,
                            Math.Min(kept.Max(x => x.EndMs), endMs) - startMs, original.Speaker));
                    renumbered[b] = index = blocks.Count - 1;
                }
                block = index;
            }
            sliced.Add(w with { StartMs = Math.Max(w.StartMs, startMs) - startMs, EndMs = Math.Min(w.EndMs, endMs) - startMs, Block = block });
        }
        return new StoredTranscript { Worker = Worker, Language = Language, Words = sliced, Blocks = blocks.Count > 0 ? blocks : null };
    }

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
