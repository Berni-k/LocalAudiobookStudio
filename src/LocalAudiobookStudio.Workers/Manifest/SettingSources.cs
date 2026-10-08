namespace LocalAudiobookStudio.Workers.Manifest;

/// <summary>
/// Readers for settings that come from a file instead of the manifest. A setting with
/// <c>"addsSettings": "&lt;reader&gt;"</c> picks a file (usually an asset); the reader turns that file into
/// more settings, shown below it (see <see cref="WorkerManifest.SettingsFor"/>). To add a kind of file, add a
/// reader here.
/// </summary>
public static class SettingSources
{
    /// <summary>A ComfyUI workflow: its <c>{{tags}}</c> (see <see cref="ComfyWorkflow"/>).</summary>
    public const string ComfyWorkflow = "comfyWorkflow";

    public static IReadOnlyList<string> Readers { get; } = [ComfyWorkflow];

    /// <summary>The settings <paramref name="path"/> adds, read by <paramref name="reader"/>; empty when unknown or unreadable.</summary>
    public static List<SettingDefinition> Read(string reader, string path) => reader switch
    {
        ComfyWorkflow => Manifest.ComfyWorkflow.Settings(path),
        _ => [],
    };
}
