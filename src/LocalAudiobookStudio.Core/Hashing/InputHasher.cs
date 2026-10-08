using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.Core.Hashing;

/// <summary>
/// Computes <c>inputHash</c>: SHA-256 of the canonical JSON of everything that affects generation.
/// A segment is stale when its stored hash differs from the recomputed one.
/// </summary>
public static class InputHasher
{
    public const string Prefix = "sha256:";

    /// <param name="modelVersion">The worker's model version from its manifest (null if unknown).</param>
    public static string? Compute(TimelineItem item, Speaker? speaker, string? modelVersion)
    {
        var o = new JsonObject { ["kind"] = item.Kind.ToString().ToLowerInvariant() };
        switch (item.Kind)
        {
            case ItemKind.Tts:
                o["text"] = item.Text ?? "";
                o["speaker"] = item.Speaker ?? "";
                o["worker"] = speaker?.Worker ?? "";
                o["voice"] = speaker?.Voice ?? "";
                o["settings"] = speaker?.Settings?.DeepClone() ?? new JsonObject();
                // The length asked of the worker. Only hashed when set, so segments without one keep their hashes.
                if (item.DurationSec is double sec) o["durationSec"] = sec;
                break;
            default:
                return null; // Clips and pauses are never generated.
        }
        o["overrides"] = item.SettingsOverride?.DeepClone() ?? new JsonObject();
        o["modelVersion"] = modelVersion ?? "";
        return Prefix + Sha256Hex(Canonicalize(o));
    }

    /// <summary>Compact JSON with object keys sorted ordinally, so equal content always hashes equally.</summary>
    public static string Canonicalize(JsonNode? node)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            Write(w, node);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject obj:
                w.WriteStartObject();
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(kv.Key);
                    Write(w, kv.Value);
                }
                w.WriteEndObject();
                break;
            case JsonArray arr:
                w.WriteStartArray();
                foreach (var e in arr) Write(w, e);
                w.WriteEndArray();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }

    private static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
