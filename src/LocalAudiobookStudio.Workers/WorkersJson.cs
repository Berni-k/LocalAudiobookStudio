using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LocalAudiobookStudio.Workers.Environments;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Models;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Workers;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = false)]
[JsonSerializable(typeof(WorkerManifest))]
[JsonSerializable(typeof(WorkerMessage))]
[JsonSerializable(typeof(EnvState))]
[JsonSerializable(typeof(EnvironmentsFile))]
[JsonSerializable(typeof(ModelsFile))]
[JsonSerializable(typeof(ModelMarker))]
internal sealed partial class WorkersJsonContext : JsonSerializerContext;

internal static class WorkersJson
{
    private static readonly JsonSerializerOptions Indented = new(WorkersJsonContext.Default.Options) { WriteIndented = true };
    private static readonly WorkersJsonContext IndentedContext = new(Indented);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)WorkersJsonContext.Default.GetTypeInfo(typeof(T))!)
        ?? throw new JsonException($"Empty JSON for {typeof(T).Name}.");

    public static string Serialize<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)(indented ? IndentedContext : WorkersJsonContext.Default).GetTypeInfo(typeof(T))!);
}
