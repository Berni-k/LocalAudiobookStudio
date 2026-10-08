using HartsyInference.Audio.Cache;
using HartsyInference.Engine;

namespace LocalAudiobookStudio.HartsyWorker;

/// <summary>A model to ask the app for (an id in workers/models.json) and where HartsyInference looks for its files.</summary>
internal sealed record ModelNeed(string Model, string? LinkTo = null, IReadOnlyDictionary<string, string>? Links = null);

/// <summary>
/// Which of the app's models a HartsyInference model loads, and where. The app downloads them into its store and makes
/// HartsyInference's own folder for each (<see cref="AudioModelCache.GetRepoDirectory"/>) a link to the store folder,
/// so HartsyInference finds the files there and downloads nothing itself. What it writes there (Kokoro's converted
/// weights, voices) lands in the store and is deleted with the model. ACE-Step's renamed files are single-file hard
/// links. A model this doesn't know is left to HartsyInference.
/// </summary>
internal static class ModelLinks
{
    /// <summary>ACE-Step's files (HartsyInference's catalog: repo, path in the repo) → the model id holding that file.</summary>
    private static readonly Dictionary<(string Repo, string Path), string> AceStepFiles = new()
    {
        [("Comfy-Org/ace_step_1.5_ComfyUI_files", "split_files/diffusion_models/acestep_v1.5_xl_turbo_bf16.safetensors")] = "ace-step-1.5-xl-turbo-comfy",
        [("Comfy-Org/ace_step_1.5_ComfyUI_files", "split_files/diffusion_models/acestep_v1.5_xl_sft_bf16.safetensors")] = "ace-step-1.5-xl-sft-comfy",
        [("ACE-Step/acestep-v15-xl-turbo", "config.json")] = "ace-step-1.5-xl-turbo-config",
        [("ACE-Step/acestep-v15-xl-sft", "config.json")] = "ace-step-1.5-xl-sft-config",
        [("ACE-Step/acestep-v15-base", "silence_latent.pt")] = "ace-step-1.5-silence-latent",
    };

    public static IEnumerable<ModelNeed> ForSpeech(string token)
    {
        var (id, variant) = Split(token);
        switch (id)
        {
            case "kokoro":
                yield return Folder("kokoro-82m", "hexgrad/Kokoro-82M", "tts");
                break;
            case "qwen3tts" when variant.Split('-') is [var size, var kind]:
                yield return Folder($"qwen3-tts-{size.ToLowerInvariant()}-{kind.ToLowerInvariant()}", $"Qwen/Qwen3-TTS-12Hz-{size}-{kind}", "tts");
                break;
            case "vibevoice":
                yield return Folder("vibevoice-1.5b", "microsoft/VibeVoice-1.5B", "tts");
                break;
            case "chatterbox":
                yield return Folder("chatterbox", "ResembleAI/chatterbox", "tts");
                break;
            case "cosyvoice":
                // CosyVoice 2 uses Chatterbox's S3Gen tokenizer.
                yield return Folder("cosyvoice2-0.5b", "FunAudioLLM/CosyVoice2-0.5B", "tts");
                yield return Folder("chatterbox", "ResembleAI/chatterbox", "tts");
                break;
        }
    }

    public static IEnumerable<ModelNeed> ForMusic(string token)
    {
        var (id, _) = Split(token);
        switch (id)
        {
            case "audiogen":
                yield return Folder("audiogen-medium", "facebook/audiogen-medium", "music");
                yield return Folder("t5-large", "google-t5/t5-large", "music");
                break;
            case "stableaudio":
                yield return Folder("stable-audio-open-small", "FastVideo/stable-audio-open-small-Diffusers", "music");
                yield return Folder("t5-base", "google-t5/t5-base", "music");
                break;
            case var _ when id == HartsyInference.Engine.Audio.AudioWeightsCatalog.AceStepId:
                // The variant's own files are renamed into one shared folder: single-file links.
                foreach (var asset in ModelVariants.MusicAssets(token) ?? [])
                    if (asset.Repo != null && asset.RepoPath != null && AceStepFiles.TryGetValue((asset.Repo, asset.RepoPath), out var model))
                        yield return new ModelNeed(model, Links: new Dictionary<string, string> { [asset.RepoPath] = ModelDownloader.TargetPath(asset) });
                    else
                        Protocol.Log($"ACE-Step file {asset.Repo}/{asset.RepoPath} isn't in workers/models.json; HartsyInference downloads it itself.");
                yield return Folder("ace-step-1.5-vae", "Comfy-Org/ace_step_1.5_ComfyUI_files", "music");
                yield return Folder("qwen3-embedding-0.6b", "Qwen/Qwen3-Embedding-0.6B", "music");
                break;
        }
    }

    /// <summary>The model holding a worker's preset voice clips (extra files, fetched one by one), or null.</summary>
    public static string? PresetVoices(string model) => Split(model).Id == "vibevoice" ? "vibevoice-voices" : null;

    /// <summary>Asks the app for each model in turn (it downloads and links them) and waits until all are in place.</summary>
    public static async Task EnsureAsync(string? requestId, IEnumerable<ModelNeed> needs, CancellationToken ct)
    {
        foreach (var need in needs)
            await Protocol.NeedModelAsync(requestId, need, ct);
    }

    private static ModelNeed Folder(string model, string repo, string category) =>
        new(model, LinkTo: AudioModelCache.GetRepoDirectory(repo, category));

    private static (string Id, string Variant) Split(string token)
    {
        var i = token.IndexOf(':');
        return i < 0 ? (token.ToLowerInvariant(), "") : (token[..i].ToLowerInvariant(), token[(i + 1)..]);
    }
}
