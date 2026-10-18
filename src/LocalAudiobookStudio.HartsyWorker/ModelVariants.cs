using System.Text.Json.Nodes;
using HartsyInference.Engine;
using HartsyInference.Engine.Audio;
using HartsyInference.Engine.Requests;

namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>
/// What depends on the model. HartsyInference loads one mode per checkpoint (a "variant":
/// <c>qwen3tts:1.7B-CustomVoice</c>), while our settings choose per segment, so the worker picks the variant for
/// each request and switches the loaded model when it changes. Music models also differ in which request field
/// holds what (ACE-Step: the style caption is <c>Genre</c>, the lyrics are <c>Prompt</c>).
/// </summary>
internal static class ModelVariants
{
    /// <summary>The catalog token for a speech request: the model id, with a variant when the model needs one.</summary>
    public static string For(string model, JsonObject settings, bool hasReference) => model.ToLowerInvariant() switch
    {
        // Qwen3-TTS: cloning uses the Base checkpoint, preset voices CustomVoice (VoiceDesign is greyed out:
        // HartsyInference's voice design takes no description). Size from the "size" setting.
        "qwen3tts" => $"qwen3tts:{(Settings.String(settings, "size") == "0.6B" ? "0.6B" : "1.7B")}-{(hasReference ? "Base" : "CustomVoice")}",
        // Drama 3: the "build" setting is Fish Audio's FP8 or int4 weights, or a precision HartsyInference makes while loading.
        "fishaudio-drama3" or "drama3" => Settings.String(settings, "build") is ("int4" or "q8" or "q6" or "q5" or "q4") and var build
            ? $"{model}:{build}" : model,
        _ => model,
    };

    /// <summary>The catalog token for a music request.</summary>
    public static string ForMusic(string model, JsonObject settings) => model.ToLowerInvariant() switch
    {
        // ACE-Step 1.5 XL, like the Python worker: the "model" setting is turbo or sft.
        AudioWeightsCatalog.AceStepId => $"{AudioWeightsCatalog.AceStepId}:xl-{(Settings.String(settings, "model") == "sft" ? "sft" : "turbo")}",
        _ => model,
    };

    /// <summary>A music model's own files (with SHA-256 checks), when HartsyInference publishes the list; else null.</summary>
    public static IReadOnlyList<ModelAsset>? MusicAssets(string token)
    {
        var (id, variant) = Split(token);
        if (id != AudioWeightsCatalog.AceStepId) return null;
        var assets = AudioWeightsCatalog.AssetsFor(id, variant);
        return assets.Count > 0 ? assets : null;
    }

    /// <summary>
    /// Files a music model shares with its family and fetches while loading (repo, file). Mirrors
    /// HartsyInference's AceStepMusicModel (the Oobleck VAE and the Qwen3 embedding condition encoder), so the
    /// prepare stage downloads them with progress instead of the first generation.
    /// </summary>
    public static IEnumerable<(string Repo, string File)> MusicSharedFiles(string token) =>
        Split(token).Id == AudioWeightsCatalog.AceStepId
            ? [("Comfy-Org/ace_step_1.5_ComfyUI_files", "split_files/vae/ace_1.5_vae.safetensors"), ("Qwen/Qwen3-Embedding-0.6B", "model.safetensors")]
            : [];

    /// <summary>Maps the segment and its settings onto HartsyInference's music request.</summary>
    public static MusicRequest MusicRequest(string model, string prompt, double durationSec, JsonObject s, int seed)
    {
        if (model.Equals(AudioWeightsCatalog.AceStepId, StringComparison.OrdinalIgnoreCase))
        {
            var sft = Settings.String(s, "model") == "sft";
            return new MusicRequest
            {
                // ACE-Step: the caption is the style prompt, Prompt the lyrics (empty = "[Instrumental]").
                Genre = prompt,
                Prompt = Settings.String(s, "lyrics") ?? "",
                VocalLanguage = Settings.String(s, "language") ?? "",
                Duration = durationSec,
                Bpm = Settings.Int(s, "bpm") is > 0 and var bpm ? bpm : null,
                KeyScale = Settings.String(s, "key") ?? "",
                InferSteps = Settings.Int(s, "steps") is > 0 and var steps ? steps : null,
                // Turbo has guidance distilled in (the Python worker ignores the setting too).
                CfgScale = sft ? Settings.Double(s, "guidance") : null,
                Seed = seed,
            };
        }
        return new MusicRequest
        {
            Prompt = prompt,
            Duration = durationSec,
            InferSteps = Settings.Int(s, "steps") is > 0 and var n ? n : null,
            CfgScale = Settings.Double(s, "cfgScale") ?? Settings.Double(s, "guidance"),
            // AudioGen / MusicGen sampling; 0 or absent = the model's default.
            Temperature = Settings.Double(s, "temperature") is > 0 and var t ? t : null,
            TopK = Settings.Int(s, "topK") is > 0 and var k ? k : null,
            TopP = Settings.Double(s, "topP") is > 0 and var p ? p : null,
            Seed = seed,
        };
    }

    private static (string Id, string Variant) Split(string token)
    {
        var i = token.IndexOf(':');
        return i < 0 ? (token.ToLowerInvariant(), "") : (token[..i].ToLowerInvariant(), token[(i + 1)..]);
    }
}
