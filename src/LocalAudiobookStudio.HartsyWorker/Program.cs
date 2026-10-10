using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using HartsyInference.Core.Configuration;
using LocalAudiobookStudio.HartsyWorker;

// Usage: las-hartsy-worker --worker <id> --model <hartsy catalog id> [--model-version v]
//                          [--modality speech|music] [--selftest]
//        las-hartsy-worker --version   prints "HartsyInference <version>" (the app's About window shows it)
//
// Environment (set by the app, like for Python workers):
//   HF_HOME                 the app's models folder; HartsyInference keeps its files under <HF_HOME>/audio. Models
//                           come from the app (needModel), linked into those folders before loading.
//   LAS_DEVICE              "cpu" for a CPU process, otherwise CUDA (CUDA_VISIBLE_DEVICES pins the GPU)
//   LAS_HARTSY_SETTINGS     HartsyInference settings file (instead of ~/.config/hartsyinference/settings.json)
//   LAS_HARTSY_CUDA_LIBS    folder(s) holding cublas64_12.dll / cublasLt64_12.dll, separated by ';'

if (args.Contains("--version"))
{
    var version = typeof(KnobFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
    Console.WriteLine("HartsyInference " + version.Split('+')[0]);
    return 0;
}

var options = WorkerOptions.Parse(args);
Protocol.Initialize();
ConfigureEngine();

if (options.SelfTest)
    return await SelfTest.RunAsync();

using IModelWorker worker = options.Modality == "music" ? new MusicWorker(options) : new SpeechWorker(options);
Protocol.Send("hello", null,
    ("protocol", Protocol.Version), ("worker", options.WorkerId), ("modelVersion", options.ModelVersion),
    ("device", WorkerDevice.Description()));

// stdin is read on its own thread so a cancel arrives while a generation runs (as in protocol.py).
var inbox = new BlockingCollection<JsonObject?>();
var readerThread = new Thread(() =>
{
    foreach (var msg in Protocol.ReadMessages())
    {
        if ((string?)msg["type"] == "cancel") worker.Cancel((string?)msg["id"]);
        else if (!Protocol.TryDeliverReply(msg)) inbox.Add(msg);
    }
    Protocol.FailReplies();
    inbox.Add(null); // EOF: the app went away
}) { IsBackground = true, Name = "stdin" };
readerThread.Start();

while (inbox.Take() is { } msg)
{
    var type = (string?)msg["type"];
    var id = (string?)msg["id"];
    if (type == "shutdown") break;
    try
    {
        switch (type)
        {
            case "load": await worker.LoadAsync(id); break;
            case "generate" when Protocol.RefuseParts(id, msg): break;
            case "generate": await worker.GenerateAsync(id, msg); break;
            case "listVoices": Protocol.Send("voices", id, ("voices", new JsonArray())); break;
            default: Protocol.Error(id, $"unknown message type: {type}", fatal: false); break;
        }
    }
    catch (OperationCanceledException)
    {
        Protocol.Error(id, "cancelled", fatal: false);
    }
    catch (Exception ex)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        Protocol.Error(id, message, WorkerDevice.IsFatal(ex), ex.ToString());
    }
}
return 0;

static void ConfigureEngine()
{
    // The settings file must be chosen before the first knob is read or set.
    if (Environment.GetEnvironmentVariable("LAS_HARTSY_SETTINGS") is { Length: > 0 } settingsFile)
    {
        // An explicit settings file must exist; start the user off with an empty one to edit.
        if (!File.Exists(settingsFile))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!);
            File.WriteAllText(settingsFile, "{\n  \"settings\": {}\n}\n");
        }
        KnobFile.ExplicitPath = settingsFile;
    }
    if (Environment.GetEnvironmentVariable("HF_HOME") is { Length: > 0 } models)
        KnobStore.Set(EngineKnobs.ModelsRoot, models);
    if (Environment.GetEnvironmentVariable("LAS_HARTSY_CUDA_LIBS") is { Length: > 0 } libs)
        KnobStore.Set(EngineKnobs.CudaLibDir, libs.Split(';', StringSplitOptions.RemoveEmptyEntries)[0]);
}
