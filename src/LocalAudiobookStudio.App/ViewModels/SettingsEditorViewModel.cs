using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// An editor form generated from a list of setting definitions: a worker's generation settings (speaker
/// settings, per-segment overrides where an empty field means "inherit from the speaker") or its
/// machine settings (Workers window).
/// </summary>
public sealed class SettingsEditorViewModel
{
    /// <param name="mixedKeys">Settings whose value differs between the edited sections: shown empty, marked "mixed".</param>
    public SettingsEditorViewModel(WorkerManifest manifest, JsonObject? values, bool allowInherit,
        Action<string, JsonNode?> onChanged, string? projectRoot = null, IReadOnlySet<string>? mixedKeys = null)
        : this(manifest.Settings, values, allowInherit, onChanged, projectRoot, mixedKeys)
    {
    }

    /// <param name="importAsset">
    /// Adds a file chosen with an asset setting's browse button to the project's assets (into the setting's
    /// <see cref="SettingDefinition.AssetFolder"/>) and returns its project-relative path. Without it, asset
    /// settings have no browse button.
    /// </param>
    public SettingsEditorViewModel(IEnumerable<SettingDefinition> definitions, JsonObject? values, bool allowInherit,
        Action<string, JsonNode?> onChanged, string? projectRoot = null, IReadOnlySet<string>? mixedKeys = null,
        Func<SettingDefinition, string, Task<string>>? importAsset = null)
    {
        foreach (var def in definitions)
            Fields.Add(new SettingFieldViewModel(def, values?[def.Key], allowInherit, onChanged, projectRoot, mixedKeys?.Contains(def.Key) == true, importAsset));
    }

    public ObservableCollection<SettingFieldViewModel> Fields { get; } = new();
    public bool HasFields => Fields.Count > 0;

    /// <summary>
    /// Label to the left of each editor (Speakers dialog), sharing the "label" column width of the
    /// surrounding form. Off: label above the editor (the narrow inspector).
    /// </summary>
    public bool Inline { get; init; }
    public int EditorRow => Inline ? 0 : 1;
    public int EditorColumn => Inline ? 1 : 0;
    public int Span => Inline ? 1 : 2;
}

public sealed partial class SettingFieldViewModel : ObservableObject
{
    private readonly Action<string, JsonNode?> _onChanged;
    private readonly string? _projectRoot;
    private readonly Func<SettingDefinition, string, Task<string>>? _importAsset;
    private bool _loading;

    public SettingFieldViewModel(SettingDefinition def, JsonNode? value, bool allowInherit, Action<string, JsonNode?> onChanged, string? projectRoot,
        bool isMixed = false, Func<SettingDefinition, string, Task<string>>? importAsset = null)
    {
        Definition = def;
        AllowInherit = allowInherit;
        IsMixed = isMixed;
        _onChanged = onChanged;
        _projectRoot = projectRoot;
        _importAsset = importAsset;
        _loading = true;
        Load(value);
        _loading = false;
    }

    public SettingDefinition Definition { get; }
    public bool AllowInherit { get; }

    /// <summary>The edited sections have different values for this setting (multi-selection in the inspector).</summary>
    public bool IsMixed { get; }
    public string Label => Definition.DisplayLabel;
    public string? Description => Definition.Description;
    public string? Note => Definition.Note;
    public bool HasNote => !string.IsNullOrEmpty(Definition.Note);

    /// <summary>False when the worker's runtime ignores this setting: the editor is greyed out, the value kept.</summary>
    public bool IsSupported => Definition.UnsupportedReason == null;

    /// <summary>The tooltip: why the setting is greyed out (if it is), then its description.</summary>
    public string? Tooltip => Definition.UnsupportedReason is { } reason
        ? reason + (Description is { Length: > 0 } d ? ". " + d : ".")
        : Description;
    public string Type => Definition.Type;

    public bool IsNumber => Type is SettingTypes.Float or SettingTypes.Int or SettingTypes.Seed;
    public bool IsBool => Type == SettingTypes.Bool;
    public bool IsString => Type == SettingTypes.String;
    /// <summary>A dropdown: an enum's options, or for an asset setting the project's assets with its extension.</summary>
    public bool IsEnum => Type is SettingTypes.Enum or SettingTypes.Asset;
    public bool IsSeed => Type == SettingTypes.Seed;
    public bool IsFile => Type == SettingTypes.File;

    /// <summary>The "…" button: a free file path, or an asset setting that can add a file to the project.</summary>
    public bool CanBrowse => IsFile || (Type == SettingTypes.Asset && _importAsset != null);
    public bool IsText => IsString || IsFile;
    public bool IsMultiline => IsString && Definition.Multiline;

    public decimal Minimum => Definition.Min is double m ? (decimal)m : IsSeed ? 0 : -1_000_000m;
    public decimal Maximum => Definition.Max is double m ? (decimal)m : IsSeed ? int.MaxValue : 1_000_000m;
    public decimal Increment => Definition.Step is double s ? (decimal)s : Type == SettingTypes.Float ? 0.05m : 1m;
    public string FormatString => Type == SettingTypes.Float ? "0.###" : "0";
    /// <summary>Dropdown entries (value + display label). A stored value the manifest no longer lists is kept as an extra entry.</summary>
    public ObservableCollection<EnumOption> Options { get; } = new();

    public string Placeholder => IsMixed ? "mixed" : AllowInherit ? "inherit" : Definition.Default?.ToString() ?? (IsSeed ? "random" : "");

    [ObservableProperty] private decimal? _numberValue;
    [ObservableProperty] private bool? _boolValue;
    [ObservableProperty] private string? _textValue;
    [ObservableProperty] private EnumOption? _enumValue;

    private void Load(JsonNode? v)
    {
        switch (Type)
        {
            case SettingTypes.Float:
            case SettingTypes.Int:
            case SettingTypes.Seed:
                NumberValue = v is JsonValue jv && jv.TryGetValue<double>(out var d) ? (decimal)d
                    : !AllowInherit && Definition.Default is JsonValue dv && dv.TryGetValue<double>(out var dd) ? (decimal)dd : null;
                break;
            case SettingTypes.Bool:
                BoolValue = v is JsonValue bv && bv.TryGetValue<bool>(out var b) ? b
                    : !AllowInherit && Definition.Default is JsonValue db && db.TryGetValue<bool>(out var bb) ? bb : AllowInherit ? null : false;
                break;
            case SettingTypes.Enum:
                foreach (var option in Definition.Options.Select((o, i) => Definition.UnsupportedOptions.Contains(o)
                    ? new EnumOption(o, $"{Definition.OptionLabel(i)} ({Definition.UnsupportedOptionsReason?.ToLowerInvariant()})", IsSupported: false)
                    : new EnumOption(o, Definition.OptionLabel(i))))
                    Options.Add(option);
                var current = v?.ToString() ?? (!AllowInherit ? Definition.Default?.ToString() : null);
                if (current != null && Options.All(o => o.Value != current)) Options.Add(new EnumOption(current, current + " (not offered)"));
                EnumValue = Options.FirstOrDefault(o => o.Value == current);
                break;
            case SettingTypes.Asset:
                foreach (var option in AssetOptions()) Options.Add(option);
                var asset = v?.ToString();
                if (asset != null && Options.All(o => o.Value != asset)) Options.Add(new EnumOption(asset, asset + " (missing)"));
                EnumValue = Options.FirstOrDefault(o => o.Value == asset);
                break;
            default:
                TextValue = v?.ToString() ?? (!AllowInherit ? Definition.Default?.ToString() : null);
                break;
        }
    }

    partial void OnNumberValueChanged(decimal? value)
    {
        if (_loading) return;
        JsonNode? node = value switch
        {
            null => null,
            _ when Type == SettingTypes.Float => JsonValue.Create(Math.Round((double)value.Value, 6)),
            _ => JsonValue.Create((long)value.Value),
        };
        _onChanged(Definition.Key, node);
    }

    partial void OnBoolValueChanged(bool? value)
    {
        if (!_loading) _onChanged(Definition.Key, value is bool b ? JsonValue.Create(b) : null);
    }

    partial void OnTextValueChanged(string? value)
    {
        if (!_loading) _onChanged(Definition.Key, string.IsNullOrEmpty(value) ? null : JsonValue.Create(value));
    }

    partial void OnEnumValueChanged(EnumOption? value)
    {
        if (!_loading) _onChanged(Definition.Key, value == null ? null : JsonValue.Create(value.Value));
    }

    /// <summary>
    /// The project's assets (subfolders included) with one of the setting's extensions, as project-relative paths,
    /// named by their path below assets/ without the extension ("Voice Reference/narrator").
    /// </summary>
    private IEnumerable<EnumOption> AssetOptions()
    {
        var dir = _projectRoot == null ? null : Path.Combine(_projectRoot, ProjectPaths.AssetsFolder);
        if (dir == null || !Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => (File: f, Ext: Definition.Extensions.FirstOrDefault(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Ext != null)
            .Select(x => new EnumOption(ProjectPaths.AssetsFolder + "/" + x.File, x.File[..^x.Ext!.Length]));
    }

    [RelayCommand]
    private void RandomizeSeed() => NumberValue = Random.Shared.Next(0, int.MaxValue);

    [RelayCommand]
    private void Clear()
    {
        NumberValue = null;
        BoolValue = null;
        TextValue = null;
        EnumValue = null;
    }

    [RelayCommand]
    private async Task BrowseFile()
    {
        if (Type == SettingTypes.Asset)
        {
            await BrowseAsset();
            return;
        }
        var file = await Dialogs.OpenFile("Choose file for " + Label);
        if (file == null) return;
        // Files inside the project are stored relative to it, so the project stays portable.
        if (_projectRoot != null && Path.GetFullPath(file).StartsWith(Path.GetFullPath(_projectRoot), StringComparison.OrdinalIgnoreCase))
            file = Path.GetRelativePath(_projectRoot, file).Replace('\\', '/');
        TextValue = file;
    }

    /// <summary>
    /// Copies a chosen file into the project's assets (so the project keeps it) and selects it. Audio in any
    /// format is offered: it is converted to WAV on the way in.
    /// </summary>
    private async Task BrowseAsset()
    {
        if (_importAsset == null) return;
        var isAudio = !Definition.Extensions.Any(e => e.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        var filter = isAudio
            ? new FileFilter("Audio", Session.ProjectSession.AudioExtensions.Select(e => "*" + e).ToArray())
            : new FileFilter("ComfyUI workflows (API format)", "*.json");
        var file = await Dialogs.OpenFile("Choose file for " + Label, filter);
        if (file == null) return;
        string rel;
        try
        {
            rel = await _importAsset(Definition, file);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Cannot add file", $"{file}\n\n{ex.Message}");
            return;
        }
        var option = Options.FirstOrDefault(o => o.Value == rel);
        if (option == null)
        {
            var ext = Definition.Extensions.FirstOrDefault(e => rel.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ?? "";
            option = new EnumOption(rel, rel[(ProjectPaths.AssetsFolder.Length + 1)..^ext.Length]);
            Options.Add(option);
        }
        EnumValue = option;
    }

    public override string ToString() => Label + " = " + (NumberValue?.ToString(CultureInfo.InvariantCulture) ?? TextValue ?? EnumValue?.Value ?? BoolValue?.ToString());
}

/// <summary>One entry of an enum setting's dropdown. Unsupported entries (on the worker's runtime) are greyed out.</summary>
public sealed record EnumOption(string Value, string Label, bool IsSupported = true)
{
    public override string ToString() => Label;
}
