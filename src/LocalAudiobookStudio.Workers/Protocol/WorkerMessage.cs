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
    /// <summary>The answer to <see cref="Transcribe"/>: the text heard, with word timings.</summary>
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
    /// Transcribe (or align) the audio at <see cref="WorkerMessage.AudioPath"/>, for workers tagged Transcription or
    /// Alignment. <see cref="WorkerMessage.Text"/> is the text the audio should contain, if known.
    /// </summary>
    public const string Transcribe = "transcribe";
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

    // error
    public bool? Fatal { get; set; }
    public string? Trace { get; set; }

    // voices
    public List<VoiceEntry>? Voices { get; set; }

    // transcribe (app → worker: AudioPath, Text, Settings) and transcript (worker → app: Text, Words, Language: the one it heard)
    public string? AudioPath { get; set; }
    public string? Language { get; set; }
    public List<TranscriptWord>? Words { get; set; }

    // generate (app → worker)
    public string? Text { get; set; }
    public double? DurationSec { get; set; }
    public string? Voice { get; set; }
    public JsonObject? Settings { get; set; }
    public string? OutPath { get; set; }

    // needModel / needFile (worker → app) and their replies
    /// <summary>Matches a reply to the worker's request (the worker's own id; <see cref="Id"/> is the request it is handling).</summary>
    public string? Ref { get; set; }
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

    public JsonObject Settings { get; init; } = new();
    public long? Seed { get; init; }
    public string OutPath { get; set; } = "";
}

/// <summary>What the app asks a transcription or alignment worker to do.</summary>
public sealed class TranscribeRequest
{
    /// <summary>A WAV file.</summary>
    public string AudioPath { get; init; } = "";

    /// <summary>The text the audio should contain: aligners need it; transcribers may ignore it.</summary>
    public string? Text { get; init; }

    public JsonObject Settings { get; init; } = new();
}

/// <summary>One word as heard, with where it is in the audio.</summary>
public sealed record TranscriptWord(string Text, int StartMs, int EndMs, double? Confidence = null);

/// <summary>The text a worker heard in an audio file, word by word.</summary>
public sealed record TranscriptResult(string Text, IReadOnlyList<TranscriptWord> Words, string? Language);

public sealed record GenerateResult(string Path, int DurationMs, long? Seed, int SampleRate);

public sealed record HelloInfo(int Protocol, string Worker, string ModelVersion, string Device);

public sealed record WorkerProgress(double Fraction, string? Message);

/// <summary>A worker reported an error for a request. <see cref="Fatal"/> means the process must be restarted.</summary>
public sealed class WorkerErrorException(string message, bool fatal, string? trace) : Exception(message)
{
    public bool Fatal { get; } = fatal;
    public string? Trace { get; } = trace;

    public bool IsOutOfMemory => Message.Contains("out of memory", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The worker process exited or stopped responding.</summary>
public sealed class WorkerCrashedException(string message) : Exception(message);
