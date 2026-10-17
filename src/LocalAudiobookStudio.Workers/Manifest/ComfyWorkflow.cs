using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LocalAudiobookStudio.Workers.Manifest;

/// <summary>
/// A ComfyUI workflow in the API format (<c>*.comfy.json</c> in a project's assets), run by the ComfyUI bridge.
/// Its tags become speaker settings: a constant node titled <c>{{name}}</c> (its value is the default), a Load
/// Audio node titled <c>{{name}}</c> (an audio file from the assets, uploaded to ComfyUI), or <c>{{name}}</c>
/// typed into a text widget. The bridge fills the same tags in workers/comfyui_bridge/worker.py;
/// keep the two in step.
/// </summary>
public static partial class ComfyWorkflow
{
    public const string Extension = ".comfy.json";

    /// <summary>
    /// Tags the bridge fills itself: the segment's text, the take's seed, the segment's length; for listening jobs
    /// <c>audio</c> (what to transcribe or align), <c>reference</c> and <c>candidate</c> (voices to compare). <c>result</c>
    /// titles the node whose text is a listening job's answer: it is read, never set.
    /// </summary>
    public static readonly IReadOnlySet<string> BuiltInTags =
        new HashSet<string>(["text", "seed", "duration", "audio", "reference", "candidate", "result"], StringComparer.Ordinal);

    /// <summary>The input of an audio node (Load Audio) a tag sets: its setting picks an audio asset.</summary>
    public const string AudioInput = "audio";

    /// <summary>Audio assets offered for audio tags (the Assets panel converts imported audio to WAV).</summary>
    public static readonly IReadOnlyList<string> AudioExtensions = [".wav", ".opus", ".mp3", ".flac", ".ogg"];

    /// <summary>Where audio picked from a file for an audio tag is kept in the project's assets.</summary>
    public const string AudioFolder = "Voice Reference";

    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex Tag();

    private static readonly ConcurrentDictionary<string, (DateTime Time, List<SettingDefinition> Settings)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a workflow; throws <see cref="InvalidDataException"/> when it isn't in the API format.</summary>
    public static JsonObject Read(string path)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not valid JSON: {ex.Message}");
        }
        if (node is JsonObject { } wrapper && wrapper["prompt"] is JsonObject inner) node = inner;
        if (node is not JsonObject wf || wf.Count == 0 || wf.ContainsKey("nodes")
            || wf.Any(n => n.Value is not JsonObject o || o["class_type"] is not JsonValue))
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a ComfyUI workflow in the API format. " +
                                           "Open it in ComfyUI and save it with Workflow → Export (API).");
        return wf;
    }

    /// <summary>The settings a workflow's tags add (built-in tags left out), in the order they appear. Empty when the file can't be read.</summary>
    public static List<SettingDefinition> Settings(string path)
    {
        DateTime time;
        try
        {
            time = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var hit) && hit.Time == time) return hit.Settings;
        }
        catch (IOException)
        {
            return [];
        }
        List<SettingDefinition> settings;
        try
        {
            settings = SettingsOf(Read(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [];
        }
        Cache[path] = (time, settings);
        return settings;
    }

    public static List<SettingDefinition> SettingsOf(JsonObject workflow)
    {
        var byKey = new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);
        var order = new List<string>();
        void Add(SettingDefinition def, bool fromConstant)
        {
            if (BuiltInTags.Contains(def.Key)) return;
            if (!byKey.TryGetValue(def.Key, out var existing)) order.Add(def.Key);
            // A constant node knows the type and default; it wins over a placeholder in some text.
            else if (!fromConstant || existing.Type != SettingTypes.String || existing.Default != null) return;
            byKey[def.Key] = def;
        }

        var seeded = false;
        foreach (var (_, node) in workflow)
        {
            if (node is not JsonObject n || n["inputs"] is not JsonObject inputs) continue;
            var kind = n["class_type"]?.GetValue<string>() ?? "";
            var title = (n["_meta"] as JsonObject)?["title"] is JsonValue t && t.TryGetValue<string>(out var s) ? s.Trim() : "";
            if (Tag().Match(title) is { Success: true } m && m.Length == title.Length)
            {
                if (m.Groups[1].Value == "seed") seeded = true;
                else if (TargetInput(inputs) is { } input)
                    Add(FromConstant(m.Groups[1].Value, kind, input, inputs[input]!), fromConstant: true);
            }
            foreach (var (name, value) in inputs)
            {
                // A number in an input named seed gets the take's seed (see worker.py).
                if (SeedInputs.Contains(name) && value is JsonValue num && num.GetValueKind() == JsonValueKind.Number) seeded = true;
                if (value is JsonValue v && v.TryGetValue<string>(out var text))
                    foreach (Match tag in Tag().Matches(text))
                    {
                        if (tag.Groups[1].Value == "seed") seeded = true;
                        Add(new SettingDefinition
                        {
                            Key = tag.Groups[1].Value, Type = SettingTypes.String, Label = LabelOf(tag.Groups[1].Value),
                            Description = $"Fills {{{{{tag.Groups[1].Value}}}}} in the workflow.",
                        }, fromConstant: false);
                    }
            }
        }
        var settings = order.Select(k => byKey[k]).ToList();
        // Only a workflow that has something to seed gets the setting.
        if (seeded) settings.Insert(0, new SettingDefinition { Key = "seed", Type = SettingTypes.Seed, Label = "Seed" });
        return settings;
    }

    /// <summary>Inputs that get the take's seed when the workflow has no {{seed}} tag.</summary>
    private static readonly HashSet<string> SeedInputs = new(["seed", "noise_seed"], StringComparer.Ordinal);

    /// <summary>
    /// The input a node titled with a tag holds its value in: "value" (ComfyUI's Int, Float, String and Boolean
    /// nodes), "audio" (Load Audio), otherwise its only widget. Links (arrays) aren't widgets.
    /// </summary>
    public static string? TargetInput(JsonObject inputs)
    {
        var widgets = inputs.Where(i => i.Value is JsonValue).Select(i => i.Key).ToList();
        if (widgets.Contains("value")) return "value";
        if (widgets.Contains(AudioInput)) return AudioInput;
        return widgets.Count == 1 ? widgets[0] : null;
    }

    private static SettingDefinition FromConstant(string key, string kind, string input, JsonNode value)
    {
        // The node's own file is on the ComfyUI server, not in the project: it stays when the setting is empty.
        if (input == AudioInput)
            return new SettingDefinition
            {
                Key = key, Label = LabelOf(key), Type = SettingTypes.Asset, Extensions = [.. AudioExtensions], AssetFolder = AudioFolder,
                Description = $"An audio file from the project's assets for the workflow's {{{{{key}}}}} node. " +
                              $"Empty: the node's own file on the ComfyUI server ({value}).",
            };

        var def = new SettingDefinition
        {
            Key = key, Label = LabelOf(key), Default = value.DeepClone(),
            Description = $"The value of the workflow's {{{{{key}}}}} node ({kind}).",
        };
        var v = (JsonValue)value;
        switch (v.GetValueKind())
        {
            case JsonValueKind.True or JsonValueKind.False:
                def.Type = SettingTypes.Bool;
                break;
            case JsonValueKind.Number:
                // The editor exports 7.0 as 7, so the node type says more than the number.
                var isFloat = kind.Contains("Float", StringComparison.OrdinalIgnoreCase)
                              || (!kind.Contains("Int", StringComparison.OrdinalIgnoreCase)
                                  && v.ToJsonString().IndexOfAny(['.', 'e', 'E']) >= 0);
                def.Type = isFloat ? SettingTypes.Float : SettingTypes.Int;
                break;
            default:
                def.Type = SettingTypes.String;
                def.Multiline = kind.Contains("Multiline", StringComparison.OrdinalIgnoreCase)
                                || (v.TryGetValue<string>(out var s) && s.Contains('\n'));
                break;
        }
        return def;
    }

    /// <summary>"voice_text" → "Voice text".</summary>
    private static string LabelOf(string key)
    {
        var words = key.Replace('_', ' ').Trim();
        return words.Length == 0 ? key : char.ToUpper(words[0], CultureInfo.InvariantCulture) + words[1..];
    }
}
