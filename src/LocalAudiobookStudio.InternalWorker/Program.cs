using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.HartsyWorker;
using LocalAudiobookStudio.InternalWorker;

// Usage: las-internal-worker --worker <id> --model-version <v> --engine <name> [--gpu-folder <folder>] [--selftest]
//        las-internal-worker --version   prints "Whisper.net <version>, ONNX Runtime <version>" (the About window shows it; plain ASCII, the console's code page is unknown)
//
// --engine       which engine runs the model (Engines.cs): "whisper" (whisper.cpp), "tdt" (NeMo TDT on ONNX Runtime)
// --gpu-folder   the engine's folder in the GPU runtime (envs/internal-gpu/<engine>): its downloaded GPU builds.
//                Without it only the CPU builds inside the exe are there.
//
// Environment (set by the app, like for Python workers):
//   LAS_DEVICE            "cpu" for a CPU process; otherwise CUDA_VISIBLE_DEVICES names the GPU
//   LAS_CPU_THREADS       threads for a CPU process
//   LAS_WORKER_SETTINGS   the worker settings (the model choice; Whisper's "backend": auto, cuda, vulkan, cpu)
//   LAS_ENGINE_CONFIG     the manifest's engineConfig: which models.json id and files each model choice loads

if (args.Contains("--version"))
{
    static string VersionOf(Type t) => (t.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?").Split('+')[0];
    Console.WriteLine($"Whisper.net {VersionOf(typeof(Whisper.net.WhisperFactory))}, ONNX Runtime {VersionOf(typeof(Microsoft.ML.OnnxRuntime.OrtEnv))}");
    return 0;
}

var options = WorkerOptions.Parse(args);
Protocol.Initialize();
VcRuntime.LoadIfMissing();
using var engine = Engines.Create(new EngineContext(options, WorkerOptions.Settings(), WorkerOptions.EngineConfig()));

if (options.SelfTest)
    return engine.SelfTest();

Protocol.Send("hello", null,
    ("protocol", Protocol.Version), ("worker", options.WorkerId), ("modelVersion", options.ModelVersion),
    ("device", engine.DeviceDescription));

// stdin is read on its own thread so a cancel arrives while a request runs (as in protocol.py).
var inbox = new BlockingCollection<JsonObject?>();
var readerThread = new Thread(() =>
{
    foreach (var msg in Protocol.ReadMessages())
    {
        if ((string?)msg["type"] == "cancel") engine.Cancel((string?)msg["id"]);
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
            case "load": await engine.LoadAsync(id); break;
            case "transcribe": await engine.TranscribeAsync(id, msg); break;
            case "generate" when Protocol.RefuseParts(id, msg): break;
            case "generate": await engine.GenerateAsync(id, msg); break;
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
        Protocol.Error(id, message, engine.IsFatal(ex), ex.ToString());
    }
}
return 0;
