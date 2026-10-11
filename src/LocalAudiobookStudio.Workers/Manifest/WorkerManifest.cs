using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Workers.Manifest;

/// <summary>
/// Contents of <c>workers/&lt;id&gt;/manifest.json</c>, for one runtime. Read without starting the worker.
/// A worker that runs on several runtimes has a <c>manifest.&lt;runtime&gt;.json</c> per runtime next to
/// <c>manifest.json</c>; each one's fields replace the shared file's (see <see cref="WorkerCatalog"/>).
/// </summary>
public sealed class WorkerManifest
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>
    /// A regular expression for the directions a model takes in the text but doesn't say (Fish's <c>[whisper]</c>,
    /// Breeze's <c>(laugh)</c>): what it matches is left out when a take's text is compared with what was heard, or given
    /// to an aligner to time. Null: everything in the text is spoken.
    /// </summary>
    public string? InlineTags
    {
        get => _inlineTags;
        set { _inlineTags = value; _inlineTagRegex = null; }
    }

    private string? _inlineTags;
    private Regex? _inlineTagRegex;

    /// <summary>
    /// <paramref name="text"/> with what <see cref="InlineTags"/> matches blanked out by as many spaces, so the words left
    /// keep their place in the text (for highlighting them).
    /// </summary>
    public string SpokenText(string text)
    {
        if (string.IsNullOrEmpty(InlineTags) || string.IsNullOrEmpty(text)) return text;
        _inlineTagRegex ??= new Regex(InlineTags, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return _inlineTagRegex.Replace(text, m => new string(' ', m.Length));
    }

    /// <summary>
    /// Sort order in worker lists (Speakers dialog, Workers window): higher first, then by name. Default 0, so
    /// workers without one come after those that set it.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// What the worker does, shown as small tags in the Workers window ("Voice", "Voice clone", "Music", "SFX").
    /// Common ones get a colour there; any other text shows as a grey tag. <see cref="WorkerTags.Transcription"/>,
    /// <see cref="WorkerTags.Alignment"/> and <see cref="WorkerTags.ForcedAlignment"/> also say what the worker does: it
    /// listens to audio instead of making it.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    public bool HasTag(string tag) => Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Listens to audio instead of generating it (tagged Transcription, Alignment Forced, or both). Such workers aren't
    /// offered as a speaker's voice.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTranscriber => CanTranscribe || CanAlign || AlignsText;

    /// <summary>Hears the words in audio (<c>transcribe</c>; tag Transcription).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanTranscribe => HasTag(WorkerTags.Transcription);

    /// <summary>
    /// Times the words it hears (<c>transcribeAlign</c>): tagged Transcription with Alignment, or with Alignment Forced
    /// (a forced aligner times its own transcript too).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanAlign => HasTag(WorkerTags.Alignment) || CanTranscribe && HasTag(WorkerTags.ForcedAlignment);

    /// <summary>
    /// Times a given text's words without changing them (forced alignment, <c>align</c>; tag Alignment Forced): it can be
    /// a speaker's aligner, timing what its transcription worker heard.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AlignsText => HasTag(WorkerTags.ForcedAlignment);

    /// <summary>Says who said each word it returns (tag Multi-speaker).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TellsSpeakers => HasTag(WorkerTags.MultiSpeaker);

    /// <summary>
    /// The most this worker can be asked for in one request: the words heard with their times when it is tagged
    /// Transcription and Alignment (<c>transcribeAlign</c>), the words heard with Transcription alone, the times of a given
    /// text when it only aligns (Alignment Forced).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ListenMode ListenMode => CanTranscribe && CanAlign ? ListenMode.TranscribeAlign
        : CanTranscribe ? ListenMode.Transcribe
        : ListenMode.Align;

    /// <summary>
    /// Not well tested yet. Set per runtime (in a runtime's overlay) or for the whole worker (in manifest.json).
    /// The Workers window hides workers whose runtimes are all experimental unless "Show all workers" is on.
    /// </summary>
    public bool Experimental { get; set; }

    /// <summary>
    /// Known not to work properly: the reason. The worker still installs and runs; the Workers window tags it
    /// "Broken" with the reason and the CLI warns. Set per runtime (in a runtime's overlay) or for the whole worker.
    /// </summary>
    public string? Broken { get; set; }

    /// <summary>What runs the model: <see cref="WorkerRuntimes.Python"/> (default) or <see cref="WorkerRuntimes.Hartsy"/>.</summary>
    public string Runtime { get; set; } = WorkerRuntimes.Python;

    /// <summary>HartsyInference catalog id for the Hartsy runtime ("kokoro", "vibevoice:realtime").</summary>
    public string? HartsyModel { get; set; }

    /// <summary>Which HartsyInference catalog <see cref="HartsyModel"/> is in: "speech" (default) or "music".</summary>
    public string? HartsyModality { get; set; }

    /// <summary>
    /// Settings this runtime ignores (keys of <see cref="Settings"/>): editors show them greyed out. Set in a
    /// runtime's overlay, so the shared settings list stays the same for every runtime.
    /// </summary>
    public List<string> UnsupportedSettings { get; set; } = new();

    /// <summary>Enum options this runtime can't do, by setting key: greyed out in the dropdown, refused at generation.</summary>
    public Dictionary<string, List<string>> UnsupportedOptions { get; set; } = new();

    /// <summary>
    /// Why these settings can't be generated on this runtime (an unsupported option chosen), or null. Settings
    /// the speaker doesn't set use the manifest default.
    /// </summary>
    public string? UnsupportedChoice(System.Text.Json.Nodes.JsonObject settings)
    {
        foreach (var def in Settings)
        {
            if (def.UnsupportedOptions.Count == 0) continue;
            var value = settings[def.Key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s
                : def.Default is System.Text.Json.Nodes.JsonValue dv && dv.TryGetValue<string>(out var d) ? d : null;
            if (value == null || !def.UnsupportedOptions.Contains(value)) continue;
            var index = def.Options.IndexOf(value);
            var label = index >= 0 ? def.OptionLabel(index) : value;
            return $"{def.DisplayLabel} \"{label}\" is not supported on {RuntimeLabel}. " +
                   "Choose another option, or switch the worker's runtime in Workers → Manage workers.";
        }
        return null;
    }

    /// <summary>Every runtime this worker has (set by the catalog), in the order they are offered.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Runtimes { get; set; } = [WorkerRuntimes.Python];

    /// <summary>
    /// How the runtime is named in the app (set by the catalog): "PyTorch" for a Python runtime in a torch
    /// environment, so it isn't confused with the ONNX runtimes (also Python); otherwise <see cref="WorkerRuntimes.Label"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RuntimeLabel { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsHartsy => string.Equals(Runtime, WorkerRuntimes.Hartsy, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs on the bundled internal worker (tools/internal-worker/las-internal-worker): one of its engines (<see cref="Engine"/>) with
    /// the builds that come with the app, or (<see cref="IsInternalGpu"/>) with GPU builds downloaded on install.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInternal => IsInternalGpu || string.Equals(Runtime, WorkerRuntimes.Internal, StringComparison.OrdinalIgnoreCase);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInternalGpu => string.Equals(Runtime, WorkerRuntimes.InternalGpu, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The internal worker's engine that runs this worker ("whisper": whisper.cpp; "tdt": NeMo TDT on ONNX Runtime).
    /// Required for the internal runtimes; see src/LocalAudiobookStudio.InternalWorker/Engines.cs.
    /// </summary>
    public string? Engine { get; set; }

    /// <summary>
    /// What the internal worker's engine loads, so a new model of a known kind needs no code: its <c>models</c> map a
    /// value of the worker setting named by <c>modelSetting</c> to a models.json id and its files
    /// (<c>{ "modelSetting": "model", "models": { "small": { "model": "whisper-small-ggml-q8", "file": "ggml-small-q8_0.bin" } } }</c>;
    /// the other keys of an entry are the engine's). The worker gets it as LAS_ENGINE_CONFIG.
    /// </summary>
    public JsonObject? EngineConfig { get; set; }

    /// <summary>
    /// Runs on a program that comes with the app (HartsyInference, the internal worker) instead of Python: no environment
    /// to create; its folder in envs/ only holds the install state (and, for whisper.cpp's GPU runtime, its GPU builds).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBundled => IsHartsy || IsInternal;

    /// <summary>
    /// Kept the same across runtimes: it is part of each segment's input hash, and switching runtime
    /// shouldn't mark every take out of date. The take records its runtime instead.
    /// </summary>
    public string ModelVersion { get; set; } = "";

    /// <summary>The Python environment group; the Hartsy runtime uses <see cref="WorkerRuntimes.HartsyGroup"/>.</summary>
    public string EnvGroup { get; set; } = "";
    public string Python { get; set; } = "3.11";
    public string Requirements { get; set; } = "requirements.txt";
    public string? TorchIndexUrl { get; set; }
    public string Entry { get; set; } = "worker.py";
    public int MaxChars { get; set; } = 1500;
    public int SampleRate { get; set; } = 24000;
    public bool VoiceCloning { get; set; }
    public double ApproxVramGb { get; set; }

    /// <summary>True for workers that manage their own GPU (ComfyUI bridge); they run on their own external device.</summary>
    public bool External { get; set; }

    /// <summary>
    /// Devices the worker can run on: "cuda" (a GPU) and/or "cpu". Default: GPU only.
    /// The process learns its device from LAS_DEVICE; on the CPU all GPUs are hidden too.
    /// </summary>
    public List<string>? Devices { get; set; }

    /// <summary>
    /// False when the worker can run on the CPU (devices contains "cpu") but impractically slowly. The
    /// "device" worker setting still offers the CPU, labelled not recommended. Default: true.
    /// </summary>
    public bool? CpuRecommended { get; set; }

    /// <summary>Optional note on running on the CPU, e.g. how slow it is (for the UI).</summary>
    public string? CpuNote { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public CpuSupport CpuSupport => !RunsOnCpu ? CpuSupport.Unsupported
        : CpuRecommended == false ? CpuSupport.NotRecommended
        : CpuSupport.Recommended;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool RunsOnGpu => !External && (Devices is not { Count: > 0 } || Devices.Contains(DeviceNames.Cuda, StringComparer.OrdinalIgnoreCase));

    [System.Text.Json.Serialization.JsonIgnore]
    public bool RunsOnCpu => !External && Devices?.Contains(DeviceNames.Cpu, StringComparer.OrdinalIgnoreCase) == true;

    // ------------------------------------------------------------------ hosting (common worker settings)

    /// <summary>Keys of the worker settings every local worker has: where and how wide it runs.</summary>
    public const string DeviceKey = "device", ParallelKey = "parallel", GpusKey = "gpus";
    public const string DeviceGpu = "gpu", DeviceCpu = "cpu";

    /// <summary>
    /// The hosting settings every worker has, ahead of its own <see cref="WorkerSettings"/>: device
    /// (GPU / CPU, from "devices"), parallel processes per device, and the GPU ids it may use.
    /// External workers (ComfyUI bridge) only have "parallel": how many requests they send at once.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<SettingDefinition> HostingSettings
    {
        get
        {
            var parallel = new SettingDefinition
            {
                Key = ParallelKey, Type = SettingTypes.Int, Label = "Parallel workers", Min = 1, Max = 16, Default = 1,
                Description = External
                    ? "How many requests this worker sends at once (one process each)."
                    : "How many processes of this worker may run on one GPU (or the CPU) at once. Each loads its own copy of the model.",
            };
            if (External) return [parallel];
            var list = new List<SettingDefinition>();
            var device = new SettingDefinition
            {
                Key = DeviceKey, Type = SettingTypes.Enum, Label = "Device",
                Default = RunsOnGpu ? DeviceGpu : DeviceCpu,
                Description = CpuSupport switch
                {
                    CpuSupport.Unsupported => "This worker runs on the GPU only.",
                    CpuSupport.NotRecommended => "Runs on the CPU, but too slowly to be practical." + (CpuNote is { } n ? " " + n : ""),
                    _ => CpuNote,
                },
            };
            if (RunsOnGpu) { device.Options.Add(DeviceGpu); device.OptionLabels.Add("GPU"); }
            if (RunsOnCpu)
            {
                device.Options.Add(DeviceCpu);
                device.OptionLabels.Add(CpuSupport == CpuSupport.NotRecommended ? "CPU (not recommended)" : "CPU");
            }
            list.Add(device);
            list.Add(parallel);
            if (RunsOnGpu)
                list.Add(new SettingDefinition
                {
                    Key = GpusKey, Type = SettingTypes.String, Label = "GPU ids", Default = "",
                    Description = "Comma-separated GPU ids this worker may use, for example \"0, 2\". Blank = all GPUs.",
                });
            return list;
        }
    }

    /// <summary>Hosting settings, then the worker's own settings: what the Workers window edits.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<SettingDefinition> AllWorkerSettings => [.. HostingSettings, .. WorkerSettings];

    /// <summary>
    /// Where this worker runs, from its saved worker settings. Without a saved device, a worker that runs
    /// well on the CPU uses it when the machine has no GPU.
    /// </summary>
    public Scheduling.WorkerHosting Hosting(JsonObject? saved, bool machineHasGpu = true)
    {
        var parallel = Number(saved, ParallelKey) is double p ? (int)Math.Clamp(Math.Round(p), 1, 16) : 1;
        if (External) return new Scheduling.WorkerHosting(Scheduling.WorkerPlacement.External, parallel);
        var useCpu = Text(saved, DeviceKey)?.ToLowerInvariant() switch
        {
            DeviceCpu => RunsOnCpu,
            DeviceGpu => !RunsOnGpu,
            _ => !RunsOnGpu || (!machineHasGpu && CpuSupport == CpuSupport.Recommended),
        };
        HashSet<int>? gpus = null;
        if (!useCpu && Text(saved, GpusKey) is { Length: > 0 } ids)
        {
            gpus = new HashSet<int>();
            foreach (var part in ids.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var id) && id >= 0) gpus.Add(id);
        }
        return new Scheduling.WorkerHosting(useCpu ? Scheduling.WorkerPlacement.Cpu : Scheduling.WorkerPlacement.Gpu, parallel, gpus);
    }

    /// <summary>A number setting, whether stored as a JSON number (any CLR type) or as text.</summary>
    private static double? Number(JsonObject? o, string key) =>
        o?[key] is JsonValue v && double.TryParse(v.ToJsonString().Trim('"'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    /// <summary>Generation settings, per speaker (and overridable per segment).</summary>
    public List<SettingDefinition> Settings { get; set; } = new();

    /// <summary>
    /// <see cref="Settings"/> plus those the files chosen in "addsSettings" settings add (a ComfyUI workflow's
    /// tags, for example; see <see cref="SettingSources"/>). <paramref name="values"/> are the speaker's (or a
    /// segment's merged) settings; <paramref name="resolve"/> makes a project-relative path absolute.
    /// </summary>
    public List<SettingDefinition> SettingsFor(JsonObject? values, Func<string, string>? resolve = null)
    {
        var list = new List<SettingDefinition>(Settings);
        foreach (var def in Settings)
        {
            if (def.AddsSettings is not { } reader || Text(values, def.Key) is not { Length: > 0 } file) continue;
            var path = Path.IsPathRooted(file) ? file : resolve?.Invoke(file);
            if (path == null || !File.Exists(path)) continue;
            list.AddRange(SettingSources.Read(reader, path).Where(d => list.All(e => e.Key != d.Key)));
        }
        return list;
    }

    /// <summary>
    /// Settings of the worker itself on this machine (for example the ComfyUI server addresses), edited in
    /// the Workers window and stored in settings.json. The process gets them as JSON in the
    /// <c>LAS_WORKER_SETTINGS</c> environment variable (read with <c>protocol.worker_settings()</c>).
    /// </summary>
    public List<SettingDefinition> WorkerSettings { get; set; } = new();

    /// <summary>
    /// The models this runtime may ask for (ids in workers/models.json), shown in the Workers window. Nothing is
    /// downloaded ahead of time: the worker asks for a model when it needs it (<c>needModel</c>).
    /// </summary>
    public List<string> Models { get; set; } = new();

    /// <summary>
    /// Whether a segment can ask for an output length (<c>durationSec</c> in the generate request). Without this
    /// entry the worker takes none.
    /// </summary>
    public LengthSupport? Length { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string LengthMode => Length?.Mode?.ToLowerInvariant() is { } m and (LengthModes.None or LengthModes.Optional or LengthModes.Required)
        ? m
        : LengthModes.None;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool SupportsLength => LengthMode != LengthModes.None;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool RequiresLength => LengthMode == LengthModes.Required;

    [System.Text.Json.Serialization.JsonIgnore]
    public double MinLengthSec => Length?.Min is > 0 and var min ? min : 0.2;

    /// <summary>The longest length when the settings aren't known (the manifest's "max").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double MaxLengthSec => Length?.Max is > 0 and var max ? Math.Max(max, MinLengthSec) : 600;

    /// <summary>
    /// The longest length with these generation settings: "maxBy" can give a different maximum per value
    /// of one setting (Stable Audio 3: 120 s for the Small models, 380 s for Medium). The setting's
    /// default applies when the settings don't contain it.
    /// </summary>
    public double MaxLengthSecFor(JsonObject? settings)
    {
        if (Length?.MaxBy is not { Setting: { Length: > 0 } key, Values.Count: > 0 } by) return MaxLengthSec;
        var value = settings?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s
            : Settings.FirstOrDefault(d => d.Key == key)?.Default is JsonValue dv && dv.TryGetValue<string>(out var d) ? d
            : null;
        return value != null && by.Values.TryGetValue(value, out var max) && max > 0 ? Math.Max(max, MinLengthSec) : MaxLengthSec;
    }

    /// <summary>Keeps a length within the worker's range for these settings (see <see cref="MaxLengthSecFor"/>).</summary>
    public double ClampLength(double seconds, JsonObject? settings = null) =>
        Math.Clamp(seconds, MinLengthSec, MaxLengthSecFor(settings));

    /// <summary>
    /// Whether the worker takes fused generations (a generate request with parts: several segments, and lines of
    /// context around them, in one go). Without this entry it only gets one segment per request.
    /// </summary>
    public FusionSupport? Fusion { get; set; }

    /// <summary>
    /// Renders several voices in one go, so neighbouring segments can be batched. Context works with every worker: one
    /// that doesn't fuse gets it as plain text around the segment, trimmed away by transcription.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanBatch => Fusion is { Speakers: > 1 };

    /// <summary>Folder the manifest was loaded from (not serialized in the file).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Folder { get; set; } = "";

    public override string ToString() => string.IsNullOrEmpty(DisplayName) ? Id : DisplayName;

    /// <summary>The key of the built-in voice setting: an enum setting a worker can offer (Kokoro does).</summary>
    public const string VoiceKey = "voice";

    /// <summary>
    /// Default values of all settings, for new speakers. <paramref name="variant"/> picks a different
    /// built-in voice (the n-th option of the "voice" setting), so new speakers don't all sound alike.
    /// </summary>
    public JsonObject DefaultSettings(int variant = 0)
    {
        var o = new JsonObject();
        foreach (var s in Settings)
            if (s.Default != null && s.Type != SettingTypes.Seed) o[s.Key] = s.Default.DeepClone();
        if (variant > 0 && VoiceSetting is { Options.Count: > 0 } voice)
            o[VoiceKey] = voice.Options[variant % voice.Options.Count];
        return o;
    }

    /// <summary>Worker settings: the manifest defaults overlaid with the values saved on this machine.</summary>
    public JsonObject WorkerSettingValues(JsonObject? saved)
    {
        var o = new JsonObject();
        foreach (var s in AllWorkerSettings)
            if (s.Default != null) o[s.Key] = s.Default.DeepClone();
        if (saved != null)
            foreach (var (k, v) in saved) o[k] = v?.DeepClone();
        return o;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public SettingDefinition? VoiceSetting =>
        Settings.FirstOrDefault(s => s.Key == VoiceKey && s.Type == SettingTypes.Enum);
}

/// <summary>
/// The manifest's <c>length</c> entry: <c>{ "mode": "optional", "min": 0.5, "max": 30 }</c>, optionally with
/// <c>"maxBy": { "setting": "model", "values": { "medium": 380 } }</c> for a maximum that depends on a setting.
/// </summary>
public sealed class LengthSupport
{
    /// <summary>"none", "optional" (the model decides when no length is set) or "required".</summary>
    public string Mode { get; set; } = LengthModes.None;
    public double? Min { get; set; }
    /// <summary>The maximum, and the default for setting values that <see cref="MaxBy"/> doesn't list.</summary>
    public double? Max { get; set; }
    public LengthMaxBy? MaxBy { get; set; }
}

/// <summary>A maximum length per value of one generation setting.</summary>
public sealed class LengthMaxBy
{
    /// <summary>The setting's key, e.g. "model".</summary>
    public string Setting { get; set; } = "";
    /// <summary>Maximum seconds by setting value.</summary>
    public Dictionary<string, double> Values { get; set; } = new();
}

/// <summary>
/// The manifest's <c>fusion</c> entry: <c>{ "speakers": 4, "maxChars": 3000, "context": ["text", "audio"], "spans": true }</c>.
/// The app only groups segments within these limits, whose settings are the same apart from those marked
/// <see cref="SettingDefinition.PerVoice"/>; the worker can still refuse a group (error code "unfusable").
/// </summary>
public sealed class FusionSupport
{
    /// <summary>Different voices in one generation, context included.</summary>
    public int Speakers { get; set; } = 1;

    /// <summary>All the parts' text together, context included; null: the worker's <see cref="WorkerManifest.MaxChars"/>.</summary>
    public int? MaxChars { get; set; }

    /// <summary>What context parts the worker uses (<see cref="FusionContext"/>); empty: it takes no context parts.</summary>
    public List<string> Context { get; set; } = new();

    /// <summary>The worker usually reports where each part is in the audio, so the app needn't transcribe to cut it.</summary>
    public bool Spans { get; set; }

    public bool TakesContext(string kind) => Context.Contains(kind, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Kinds of context a fusing worker can use.</summary>
public static class FusionContext
{
    /// <summary>The line's text, rendered along with the segments and cut away.</summary>
    public const string Text = "text";

    /// <summary>The line's existing take, which the model hears instead of rendering it again.</summary>
    public const string Audio = "audio";
}

/// <summary>How well a worker runs on the CPU (from "devices" and "cpuRecommended").</summary>
public enum CpuSupport
{
    /// <summary>GPU only.</summary>
    Unsupported,
    /// <summary>Works, but too slowly to be practical; offered as "CPU (not recommended)".</summary>
    NotRecommended,
    /// <summary>Runs well on the CPU.</summary>
    Recommended,
}

/// <summary>What runs a worker's model.</summary>
public static class WorkerRuntimes
{
    /// <summary>A Python script in a uv environment (workers/&lt;id&gt;/worker.py).</summary>
    public const string Python = "python";

    /// <summary>The bundled .NET worker (tools/hartsy/las-hartsy-worker) running a HartsyInference model.</summary>
    public const string Hartsy = "hartsy";

    /// <summary>A Python script on ONNX Runtime, CPU only (a small environment without torch).</summary>
    public const string Onnx = "onnx";

    /// <summary>A Python script on ONNX Runtime with CUDA (its own GPU build, still without torch).</summary>
    public const string OnnxCuda = "onnx-cuda";

    /// <summary>The bundled internal worker (tools/internal-worker/las-internal-worker) with the builds that come with the app (CPU).</summary>
    public const string Internal = "internal";

    /// <summary>The same worker with its engine's GPU builds, downloaded on install (<see cref="Environments.InternalGpuRuntime"/>).</summary>
    public const string InternalGpu = "internal-gpu";

    /// <summary>How an internal runtime is named: what its engine runs on, and where ("Internal (whisper.cpp GPU)", "Internal (ONNX CPU)").</summary>
    public static string InternalLabel(string? engine, bool gpu) =>
        $"Internal ({(engine == "whisper" ? "whisper.cpp" : "ONNX")} {(gpu ? "GPU" : "CPU")})";

    /// <summary>The "environment" Hartsy workers are recorded in: there is no venv, only the install state.</summary>
    public const string HartsyGroup = "hartsy";

    /// <summary>The worker setting that picks the runtime of a worker that has several.</summary>
    public const string SettingKey = "runtime";

    /// <summary>A Python runtime whose environment brings torch (see <see cref="WorkerManifest.RuntimeLabel"/>).</summary>
    public const string PyTorchLabel = "PyTorch";

    public static string Label(string runtime) => runtime.ToLowerInvariant() switch
    {
        Python => "Python",
        Hartsy => "HartsyInference (.NET)",
        Onnx => "ONNX Runtime (CPU)",
        OnnxCuda => "ONNX Runtime (CUDA)",
        Internal => "Internal (CPU)",
        InternalGpu => "Internal (GPU)",
        _ => runtime,
    };
}

/// <summary>Tags that change what the app does with a worker (the others are only shown).</summary>
public static class WorkerTags
{
    /// <summary>Hears the words in audio (speech recognition): answers <c>transcribe</c>.</summary>
    public const string Transcription = "Transcription";

    /// <summary>
    /// Times the words it hears (<c>transcribeAlign</c>). Only with <see cref="Transcription"/>: on its own it would
    /// have nothing to time. A worker that times a given text is <see cref="ForcedAlignment"/>.
    /// </summary>
    public const string Alignment = "Alignment";

    /// <summary>
    /// Times a given text's words without changing them (forced alignment, <c>align</c>): it can be a speaker's aligner.
    /// Alone (MMS), or with <see cref="Transcription"/> (Qwen3-ASR), which then also times the words it hears
    /// (<c>transcribeAlign</c>) without the <see cref="Alignment"/> tag.
    /// </summary>
    public const string ForcedAlignment = "Alignment Forced";

    /// <summary>
    /// Tells voices apart: each word it returns says who said it (a label of its own, "S0", "S1"). With Transcription or
    /// Alignment.
    /// </summary>
    public const string MultiSpeaker = "Multi-speaker";
}

public static class DeviceNames
{
    public const string Cuda = "cuda";
    public const string Cpu = "cpu";
}

public static class LengthModes
{
    public const string None = "none";
    public const string Optional = "optional";
    public const string Required = "required";
}

public static class SettingTypes
{
    public const string Float = "float";
    public const string Int = "int";
    public const string Bool = "bool";
    public const string String = "string";
    public const string Enum = "enum";
    public const string Seed = "seed";
    public const string File = "file";

    /// <summary>A file in the project's assets folder with one of <see cref="SettingDefinition.Extensions"/>, picked from a list.</summary>
    public const string Asset = "asset";
}

/// <summary>One entry of the manifest's settings list; editors are generated from it.</summary>
public sealed class SettingDefinition
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = SettingTypes.String;
    public string? Label { get; set; }
    public string? Description { get; set; }

    /// <summary>A short note shown under the editor (unlike the description, which is a tooltip), e.g. where a token is stored.</summary>
    public string? Note { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public JsonNode? Default { get; set; }
    public List<string> Options { get; set; } = new();

    /// <summary>For string settings: a multi-line text box (for example one server address per line).</summary>
    public bool Multiline { get; set; }

    /// <summary>For asset settings: the file name endings offered ([".comfy.json"], or audio files).</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>For asset settings: the folder below assets/ a file chosen with the browse button is copied into (null: assets/).</summary>
    public string? AssetFolder { get; set; }

    /// <summary>
    /// The file this setting picks adds more settings, read by this reader (<see cref="SettingSources"/>); null
    /// for an ordinary setting. Choosing another file changes the settings shown below it.
    /// </summary>
    public string? AddsSettings { get; set; }

    /// <summary>
    /// Belongs to the voice rather than to the generation (a preset voice, a reference transcript): in a fused
    /// generation it goes on each part, and may differ between them. All other settings must be the same.
    /// </summary>
    public bool PerVoice { get; set; }

    /// <summary>Display names for <see cref="Options"/>, in the same order (optional; the value is shown otherwise).</summary>
    public List<string> OptionLabels { get; set; } = new();

    public string OptionLabel(int index) =>
        index < OptionLabels.Count && !string.IsNullOrEmpty(OptionLabels[index]) ? OptionLabels[index] : Options[index];

    /// <summary>Set when the worker's runtime ignores this setting (see <see cref="WorkerManifest.UnsupportedSettings"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UnsupportedReason { get; set; }

    /// <summary>Options the worker's runtime can't do (see <see cref="WorkerManifest.UnsupportedOptions"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string> UnsupportedOptions { get; } = new(StringComparer.Ordinal);

    /// <summary>Why unsupported options are greyed out ("Not supported on HartsyInference (.NET)").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? UnsupportedOptionsReason { get; set; }

    public string DisplayLabel => string.IsNullOrEmpty(Label) ? Key : Label!;
}
