using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Settings;

namespace LocalAudiobookStudio.Core.Serialization;

/// <summary>
/// Source-generated JSON metadata for all persisted types: indented, camelCase, nulls omitted.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    Converters = [typeof(TimelineItemConverter)])]
[JsonSerializable(typeof(ProjectFile))]
[JsonSerializable(typeof(Speaker))]
[JsonSerializable(typeof(Chapter))]
[JsonSerializable(typeof(TimelineItem))]
[JsonSerializable(typeof(List<TimelineItem>))]
[JsonSerializable(typeof(TakeInfo))]
[JsonSerializable(typeof(FusionRecord))]
[JsonSerializable(typeof(Transcripts.StoredTranscript))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
public sealed partial class CoreJsonContext : JsonSerializerContext;

public static class CoreJson
{
    public static CoreJsonContext Context => CoreJsonContext.Default;

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Context.GetTypeInfo(typeof(T))!);

    public static byte[] SerializeToUtf8<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Context.GetTypeInfo(typeof(T))!);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Context.GetTypeInfo(typeof(T))!)
        ?? throw new JsonException($"Empty JSON for {typeof(T).Name}.");

    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize(utf8, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)Context.GetTypeInfo(typeof(T))!)
        ?? throw new JsonException($"Empty JSON for {typeof(T).Name}.");

    public static T Clone<T>(T value) => Deserialize<T>(SerializeToUtf8(value));
}
