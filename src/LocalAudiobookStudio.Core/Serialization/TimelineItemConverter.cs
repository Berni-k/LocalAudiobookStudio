using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Core.Serialization;

/// <summary>
/// Reads and writes segments. A segment of a kind this version doesn't know (made by a newer version) becomes
/// <see cref="ItemKind.Unknown"/> holding its JSON as it was, and is written back unchanged, so opening and saving
/// a project here never loses it. Known kinds go through the generated serializer as usual.
/// </summary>
public sealed class TimelineItemConverter : JsonConverter<TimelineItem>
{
    /// <summary>The "kind" values this version reads ("tts", "clip", …); <see cref="ItemKind.Unknown"/> isn't one.</summary>
    private static readonly HashSet<string> KnownKinds = typeof(ItemKind).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => (ItemKind)f.GetValue(null)! != ItemKind.Unknown)
        .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? f.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public override TimelineItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var obj = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A segment is not a JSON object.");
        var kind = obj["kind"] is JsonValue v && v.TryGetValue<string>(out var k) ? k : null;
        if (kind == null || KnownKinds.Contains(kind))
            return obj.Deserialize(PlainTimelineItemContext.Default.TimelineItem)!;
        var id = obj["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : Guid.NewGuid();
        return new TimelineItem { Id = id, Kind = ItemKind.Unknown, Raw = obj };
    }

    public override void Write(Utf8JsonWriter writer, TimelineItem value, JsonSerializerOptions options)
    {
        if (value is { Kind: ItemKind.Unknown, Raw: { } raw })
        {
            // As it was read; only the id follows the segment (a pasted copy has a new one).
            var copy = (JsonObject)raw.DeepClone();
            copy["id"] = value.Id.ToString("D");
            copy.WriteTo(writer);
        }
        else
        {
            JsonSerializer.Serialize(writer, value, PlainTimelineItemContext.Default.TimelineItem);
        }
    }
}

/// <summary>The generated serializer for known segment kinds (the converter above can't call itself).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(TimelineItem))]
internal sealed partial class PlainTimelineItemContext : JsonSerializerContext;
