using System.Text.Json.Nodes;

namespace LocalAudiobookStudio.Workers.Protocol;

/// <summary>Message types of the newline-delimited JSON worker protocol (version 2).</summary>
public static class MessageTypes
{
    // Worker → app
    public const string Hello = "hello";
    public const string Ready = "ready";
    /// <summary>
    /// Sent during <c>load</c> by workers with a prepare stage: imports work and the model files are
    /// downloaded; what's left is reading them into memory. Optional (older workers go straight to ready).
    /// </summary>
    public const string Prepared = "prepared";
    public const string Progress = "progress";
    public const string Done = "done";
    public const string Error = "error";
    public const string Voices = "voices";
    /// <summary>The answer to <see cref="Transcribe"/>, <see cref="Align"/> and <see cref="TranscribeAlign"/>: the text, and the words with their times.</summary>
    public const string Transcript = "transcript";

    /// <summary>
    /// The worker needs a model from models.json (<see cref="WorkerMessage.Model"/>) while it handles the request
    /// <see cref="WorkerMessage.Id"/>; it waits for <see cref="ModelReady"/> or <see cref="ModelFailed"/> with the
    /// same <see cref="WorkerMessage.Ref"/>. Optional <see cref="WorkerMessage.LinkTo"/> / <see cref="WorkerMessage.Links"/>
    /// ask for links from the worker's own folders to the model.
    /// </summary>
    public const string NeedModel = "needModel";

    /// <summary>The worker needs an extra file of a model (a voice; <see cref="WorkerMessage.File"/>), not listed in models.json.</summary>
    public const string NeedFile = "needFile";

    // App → worker
    public const string Load = "load";
    public const string Generate = "generate";
    public const string Cancel = "cancel";
    public const string ListVoices = "listVoices";
    /// <summary>
    /// What words are in the audio at <see cref="WorkerMessage.AudioPath"/>: text only (workers tagged Transcription).
    /// Answered with a <see cref="Transcript"/> whose words may be missing.
    /// </summary>
    public const string Transcribe = "transcribe";

    /// <summary>
    /// When each word of a known text (<see cref="WorkerMessage.Text"/>) is said in the audio (workers tagged Alignment):
    /// a <see cref="Transcript"/> of that text's words with their times. It doesn't check that they were said.
    /// </summary>
    public const string Align = "align";

    /// <summary>
    /// What words are in the audio and when each is said (workers tagged both Transcription and Alignment): a
    /// <see cref="Transcript"/> of the words heard with their times. <see cref="WorkerMessage.Text"/>, if given, is
    /// the expected text, never used as a prompt.
    /// </summary>
    public const string TranscribeAlign = "transcribeAlign";

    /// <summary>
    /// What is said and by whom (workers tagged Transcription and Multi-speaker): a <see cref="Transcript"/> whose
    /// <see cref="WorkerMessage.Segments"/> are timed blocks of text, each with the voice that said it. The times are
    /// the block's, not each word's. The worker may add timed words as well. <see cref="WorkerMessage.Text"/> is never
    /// a prompt.
    /// </summary>
    public const string TranscribeDiarize = "transcribeDiarize";

    /// <summary>
    /// How alike voices are (workers tagged Speaker Verification): two or more audio files (<see cref="WorkerMessage.Files"/>),
    /// each a reference or a candidate. Answered with <see cref="VoiceScores"/>.
    /// </summary>
    public const string CompareVoices = "compareVoices";

    /// <summary>The answer to <see cref="CompareVoices"/>: scores, the pairwise matrix, the speech in each file and the model.</summary>
    public const string VoiceScores = "voiceScores";
    public const string Shutdown = "shutdown";

    // App → worker, answering needModel / needFile
    public const string ModelReady = "modelReady";
    public const string ModelFailed = "modelFailed";
    public const string FileReady = "fileReady";
    public const string FileFailed = "fileFailed";
}

/// <summary>
/// One protocol line. A single flat shape covers every message type; fields that don't apply stay null.
/// </summary>
public sealed class WorkerMessage
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }

    // hello
    public int? Protocol { get; set; }
    public string? Worker { get; set; }
    public string? ModelVersion { get; set; }
    public string? Device { get; set; }

    // progress
    public double? Fraction { get; set; }
    public string? Message { get; set; }

    // done
    public string? Path { get; set; }
    public int? DurationMs { get; set; }
    public long? Seed { get; set; }
    public int? SampleRate { get; set; }

    /// <summary>Base64 WAV bytes, used by remote transports that don't share a filesystem.</summary>
    public string? Audio { get; set; }

    /// <summary>
    /// done, for a request with parts: where each part is in the audio, in the order of the parts (null where the
    /// worker can't tell; the app then finds it by transcription). Omitted when the worker can't tell for any part.
    /// </summary>
    public List<PartSpan?>? Spans { get; set; }

    // error
    public bool? Fatal { get; set; }
    public string? Trace { get; set; }

    /// <summary>What kind of error, when the app acts on it (<see cref="ErrorCodes"/>); null for any other.</summary>
    public string? Code { get; set; }

    // voices
    public List<VoiceEntry>? Voices { get; set; }

    // transcribe (app → worker: AudioPath, Text, Settings) and transcript (worker → app: Text, Words, Language: the one it heard)
    public string? AudioPath { get; set; }

    /// <summary>transcribe, align, transcribeAlign: who may be speaking, when known (see <see cref="TranscribeRequest.Speakers"/>).</summary>
    public List<string>? Speakers { get; set; }

    /// <summary>transcribe, align, transcribeAlign: how many voices there are, when known.</summary>
    public int? SpeakerCount { get; set; }
    public string? Language { get; set; }
    public List<TranscriptWord>? Words { get; set; }

    /// <summary>
    /// transcript (worker → app), answering transcribeDiarize: the text as timed blocks, in order, each said by one
    /// voice. They may overlap in time when voices talk at once.
    /// </summary>
    public List<TranscriptSegment>? Segments { get; set; }

    /// <summary>compareVoices (app → worker): the audio files to compare, in this order (the matrix follows it).</summary>
    public List<VoiceFile>? Files { get; set; }

    /// <summary>voiceScores: the candidates' similarity to the references, in the order the candidates are in <see cref="Files"/>.</summary>
    public List<double>? Scores { get; set; }

    /// <summary>voiceScores: the similarity of every pair of files, [i][j] being file i against file j.</summary>
    public List<List<double>>? Matrix { get; set; }

    /// <summary>voiceScores: how much speech each file has, in ms (short clips make unreliable scores).</summary>
    public List<int>? SpeechMs { get; set; }

    // generate (app → worker)
    public string? Text { get; set; }
    public double? DurationSec { get; set; }
    public string? Voice { get; set; }
    public JsonObject? Settings { get; set; }
    public string? OutPath { get; set; }

    /// <summary>generate: the parts of one fused generation (see <see cref="GenerateRequest.Parts"/>).</summary>
    public List<GeneratePart>? Parts { get; set; }

    // needModel / needFile (worker → app) and their replies
    /// <summary>Matches a reply to the worker's request (the worker's own id; <see cref="Id"/> is the request it is handling).</summary>
    public string? Ref { get; set; }
    /// <summary>needModel: the models.json id wanted. voiceScores: the model that scored (its engineConfig key).</summary>
    public string? Model { get; set; }
    public string? File { get; set; }
    /// <summary>Make this folder a link to the model's folder (a junction on Windows).</summary>
    public string? LinkTo { get; set; }
    /// <summary>Hard-link single files: path in the model → target file.</summary>
    public Dictionary<string, string>? Links { get; set; }
    /// <summary>On a failed reply: the request the worker was handling was cancelled.</summary>
    public bool? Cancelled { get; set; }
}

public sealed class VoiceEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>What the app asks a worker to render.</summary>
public sealed class GenerateRequest
{
    public string? Text { get; init; }
    public double? DurationSec { get; init; }

    /// <summary>Absolute path of a reference clip, or a worker-specific voice id.</summary>
    public string? Voice { get; init; }

    /// <summary>The settings; with <see cref="Parts"/>, only those all parts share (the per-voice ones are on each part).</summary>
    public JsonObject Settings { get; init; } = new();
    public long? Seed { get; init; }
    public string OutPath { get; set; } = "";

    /// <summary>
    /// A fused generation, for workers whose manifest declares <c>fusion</c>: several segments (and lines of context
    /// around them) rendered in one go, into one file. <see cref="Text"/> and <see cref="Voice"/> are then unused.
    /// The worker may refuse a combination with an error of code <see cref="ErrorCodes.Unfusable"/>; the app then
    /// generates the segments one by one.
    /// </summary>
    public List<GeneratePart>? Parts { get; init; }
}

/// <summary>
/// One part of a fused generation. A <see cref="PartRoles.Generate"/> part is a segment whose audio the app keeps; a
/// <see cref="PartRoles.Context"/> part is a neighbouring line the model hears or says for a natural delivery, cut away
/// afterwards. Context parts have no generation settings, only their voice's: the worker renders them as it sees fit.
/// </summary>
public sealed class GeneratePart
{
    public string Role { get; set; } = PartRoles.Generate;

    /// <summary>
    /// Who says it: parts with the same key are the same voice in one generation ("s1", "s2" …). Only meaningful
    /// within the request.
    /// </summary>
    public string Speaker { get; set; } = "";

    public string? Text { get; set; }

    /// <summary>
    /// Absolute path of a reference clip, or a worker-specific voice id (as <see cref="GenerateRequest.Voice"/>). Null
    /// for a context line whose speaker uses another worker: the worker picks a voice for it.
    /// </summary>
    public string? Voice { get; set; }

    /// <summary>
    /// The settings the manifest marks <c>perVoice</c> (a preset voice, a reference transcript): the voice's, for
    /// context parts too (null when the speaker uses another worker).
    /// </summary>
    public JsonObject? Settings { get; set; }

    /// <summary>
    /// Context parts: the line's existing take (WAV), when it has one. A worker that takes audio context (manifest
    /// <c>fusion.context</c> contains "audio") can condition on it instead of rendering the text again.
    /// </summary>
    public string? AudioPath { get; set; }
}

public static class PartRoles
{
    /// <summary>A segment to generate and keep.</summary>
    public const string Generate = "generate";

    /// <summary>A line around the segments, for context only; its audio is cut away.</summary>
    public const string Context = "context";
}

/// <summary>Where one part of a fused generation is in its audio.</summary>
public sealed record PartSpan(int StartMs, int EndMs);

public static class ErrorCodes
{
    /// <summary>The worker can't fuse this combination of parts (too many voices, settings that differ, …): not a failure.</summary>
    public const string Unfusable = "unfusable";
}

/// <summary>What the app asks a transcription or alignment worker to do.</summary>
public sealed class TranscribeRequest
{
    /// <summary>Which of the three: the words, their times for a known text, or both.</summary>
    public ListenMode Mode { get; init; } = ListenMode.TranscribeAlign;

    /// <summary>A WAV file.</summary>
    public string AudioPath { get; init; } = "";

    /// <summary>The text the audio should contain: <see cref="ListenMode.Align"/> needs it; the others may ignore it.</summary>
    public string? Text { get; init; }

    public JsonObject Settings { get; init; } = new();

    /// <summary>
    /// Who may be speaking, when known: names, in the order they first speak. A hint for workers that tell voices apart
    /// (Multi-speaker); any worker may ignore it, and the labels it returns needn't be these.
    /// </summary>
    public IReadOnlyList<string>? Speakers { get; init; }

    /// <summary>How many different voices there are, when known (a hint, like <see cref="Speakers"/>).</summary>
    public int? SpeakerCount { get; init; }
}

/// <summary>What a listening worker is asked for (the worker's tags say which it can do: <c>WorkerManifest.ListenMode</c>).</summary>
public enum ListenMode
{
    /// <summary>The words heard, without times (Transcription): <see cref="MessageTypes.Transcribe"/>.</summary>
    Transcribe,

    /// <summary>The times of a known text's words (Alignment): <see cref="MessageTypes.Align"/>.</summary>
    Align,

    /// <summary>The words heard with their times (Transcription and Alignment): <see cref="MessageTypes.TranscribeAlign"/>.</summary>
    TranscribeAlign,

    /// <summary>The text as timed blocks, each with the voice that said it (Transcription and Multi-speaker): <see cref="MessageTypes.TranscribeDiarize"/>.</summary>
    TranscribeDiarize,
}

/// <summary>One audio file of a voice comparison: a 16 kHz mono WAV made like a transcription's (<c>Transcription.PrepareAudio</c>).</summary>
public sealed class VoiceFile
{
    public string Path { get; set; } = "";

    /// <summary>A <see cref="VoiceRoles"/> value.</summary>
    public string Role { get; set; } = VoiceRoles.Candidate;
}

public static class VoiceRoles
{
    /// <summary>Known to be the voice: candidates are scored against it (several references: against their average).</summary>
    public const string Reference = "reference";

    /// <summary>A recording whose voice is judged.</summary>
    public const string Candidate = "candidate";
}

/// <summary>What the app asks a speaker verification worker to do.</summary>
public sealed class VoiceCompareRequest
{
    /// <summary>Two or more files. The matrix of the answer follows their order.</summary>
    public IReadOnlyList<VoiceFile> Files { get; init; } = [];

    public JsonObject Settings { get; init; } = new();
}

/// <summary>
/// A worker's judgement of voices. Scores of different <see cref="Model"/>s are not comparable: never mix them.
/// </summary>
/// <param name="Scores">One per candidate, in their order: similarity to the references (empty without references).</param>
/// <param name="Matrix">The similarity of every pair of files, in request order; 1 on the diagonal.</param>
/// <param name="SpeechMs">Per file, in request order: how much speech the score rests on.</param>
/// <param name="Model">Which of the worker's models scored.</param>
public sealed record VoiceScores(IReadOnlyList<double> Scores, IReadOnlyList<IReadOnlyList<double>> Matrix, IReadOnlyList<int> SpeechMs, string Model);

/// <summary>One word as heard, with where it is in the audio.</summary>
/// <param name="Speaker">
/// Who said it, from a worker tagged Multi-speaker: any label the model likes ("S0", "S1"), the same for one voice
/// within a transcript; null from other workers.
/// </param>
/// <param name="Block">
/// Which of the transcript's <see cref="TranscriptResult.Segments"/> it belongs to (their place in the list), when the
/// worker says; the connection sets it for the blocks it turns into words, and the app after an aligner timed the words
/// of a block it was given.
/// </param>
public sealed record TranscriptWord(string Text, int StartMs, int EndMs, double? Confidence = null, string? Speaker = null, int? Block = null);

/// <summary>
/// A block of text a worker that tells voices apart heard, said by one voice: a sentence or a turn, timed as a whole.
/// </summary>
/// <param name="Speaker">The voice's label in this transcript ("S01"), the same for one voice throughout it.</param>
public sealed record TranscriptSegment(string Text, int StartMs, int EndMs, string? Speaker = null);

/// <summary>
/// The text a worker heard in an audio file, word by word. <paramref name="Segments"/> are set when the worker answered
/// with timed blocks (<see cref="ListenMode.TranscribeDiarize"/>); <paramref name="Words"/> are then those blocks
/// (each a "word" holding several), unless the worker gave words too. A block's words share its time, in proportion to
/// their length, when the transcript is compared with a text.
/// </summary>
public sealed record TranscriptResult(string Text, IReadOnlyList<TranscriptWord> Words, string? Language,
    IReadOnlyList<TranscriptSegment>? Segments = null)
{
    /// <summary>
    /// Only blocks are timed: each "word" is one of <see cref="Segments"/>. False for words an aligner timed, even though
    /// they still point into the blocks they came from.
    /// </summary>
    public bool TimedByBlocks => Segments is { Count: > 0 } blocks && Words.Count == blocks.Count
        && Words.Select((w, i) => w.Block == i).All(same => same);
}

/// <param name="Spans">For a request with parts: where each part is, as the worker reported (see <see cref="WorkerMessage.Spans"/>).</param>
public sealed record GenerateResult(string Path, int DurationMs, long? Seed, int SampleRate, IReadOnlyList<PartSpan?>? Spans = null);

public sealed record HelloInfo(int Protocol, string Worker, string ModelVersion, string Device);

public sealed record WorkerProgress(double Fraction, string? Message);

/// <summary>A worker reported an error for a request. <see cref="Fatal"/> means the process must be restarted.</summary>
public sealed class WorkerErrorException(string message, bool fatal, string? trace, string? code = null) : Exception(message)
{
    public bool Fatal { get; } = fatal;
    public string? Trace { get; } = trace;

    /// <summary>The error's kind (<see cref="ErrorCodes"/>), if the worker gave one.</summary>
    public string? Code { get; } = code;

    public bool IsOutOfMemory => Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>The worker refused to fuse the parts; generate them one by one instead.</summary>
    public bool IsUnfusable => Code == ErrorCodes.Unfusable;
}

/// <summary>The worker process exited or stopped responding.</summary>
public sealed class WorkerCrashedException(string message) : Exception(message);
