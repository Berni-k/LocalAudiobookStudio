namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>Command line of one worker process.</summary>
internal sealed class WorkerOptions
{
    /// <summary>The app's worker id (for hello), e.g. "kokoro".</summary>
    public string WorkerId { get; private set; } = "hartsy";

    /// <summary>HartsyInference catalog id, optionally with a variant: "kokoro", "vibevoice:realtime".</summary>
    public string Model { get; private set; } = "";

    public string ModelVersion { get; private set; } = "";

    /// <summary>"speech" (default) or "music" (the manifest's "hartsyModality").</summary>
    public string Modality { get; private set; } = "speech";

    public bool SelfTest { get; private set; }

    public static WorkerOptions Parse(string[] args)
    {
        var o = new WorkerOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--worker": o.WorkerId = Next(); break;
                case "--model": o.Model = Next(); break;
                case "--model-version": o.ModelVersion = Next(); break;
                case "--modality": o.Modality = Next().ToLowerInvariant(); break;
                case "--selftest": o.SelfTest = true; break;
                default: throw new ArgumentException($"unknown argument {args[i]}");
            }
        }
        if (!o.SelfTest && o.Model.Length == 0) throw new ArgumentException("--model is required");
        return o;
    }
}
