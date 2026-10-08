"""VibeVoice text-to-speech worker.

Written against the community fork https://github.com/vibevoice-community/VibeVoice (commit
952326d), which keeps the inference code Microsoft removed from its own repository. The API used
here mirrors that fork's ``demo/inference_from_file.py``.

VibeVoice is an LLM + diffusion model that clones a voice from a short reference clip. Each request
is rendered as a one-speaker script ("Speaker 1: <text>") with one voice sample: the speaker's
reference clip if it has one, otherwise a preset voice from the fork's ``demo/voices`` folder
(downloaded once into the model cache).

The fork was developed on transformers 4.51.3; the app runs it on 4.57 (shared with Qwen3-TTS), and
``_patch_transformers`` bridges the two changes it trips over.
"""

import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, log, model_file, run_worker, torch_device, write_wav  # noqa: E402

SAMPLE_RATE = 24000
DEFAULT_MODEL = "vibevoice/VibeVoice-1.5B"
# The "model" setting's values → model ids in workers/models.json (each requires its Qwen2.5 tokenizer).
MODELS = {"vibevoice/VibeVoice-1.5B": "vibevoice-1.5b", "vibevoice/VibeVoice-7B": "vibevoice-7b"}
# The tokenizer the processor loads by name (preprocessor_config.json) → its model id.
TOKENIZERS = {"Qwen/Qwen2.5-1.5B": "qwen2.5-1.5b-tokenizer", "Qwen/Qwen2.5-7B": "qwen2.5-7b-tokenizer"}
VOICES = "vibevoice-voices"  # the preset voice clips, extra files of this model
DEFAULT_VOICE = "en-Carter_man"

# Speech is generated at 7.5 tokens per second and English is read at roughly 14 characters per
# second, so a segment needs about 0.55 tokens per character. Only used for the progress bar.
TOKENS_PER_CHAR = 0.55


def _patch_transformers():
    """Lets the fork's generate() run on transformers 4.56+ (checked: same audio as on 4.51.3 for a seed).

    1. ``_prepare_cache_for_generation`` lost its trailing ``device`` argument, which the fork still passes.
    2. The cache sizes itself from ``config.get_text_config()``; VibeVoice keeps the language model's
       settings (layer count etc.) in ``decoder_config``, which that lookup doesn't find.
    """
    import inspect

    from transformers.generation.utils import GenerationMixin
    from vibevoice.modular.configuration_vibevoice import VibeVoiceConfig
    from vibevoice.modular.modeling_vibevoice_inference import VibeVoiceForConditionalGenerationInference as Model

    if "device" in inspect.signature(GenerationMixin._prepare_cache_for_generation).parameters:
        return  # transformers 4.55 or older: the fork works as written
    prepare = GenerationMixin._prepare_cache_for_generation

    def prepare_cache(self, generation_config, model_kwargs, _assistant_model, batch_size, max_cache_length, device=None):
        return prepare(self, generation_config, model_kwargs, None, batch_size, max_cache_length)

    Model._prepare_cache_for_generation = prepare_cache
    VibeVoiceConfig.get_text_config = lambda self, *args, **kwargs: self.decoder_config


def _load_tokenizer_from(repo, folder):
    """Makes the processor's ``VibeVoiceTextTokenizerFast.from_pretrained(repo)`` read ``folder``."""
    from vibevoice.modular import modular_vibevoice_text_tokenizer as module

    cls = module.VibeVoiceTextTokenizerFast
    original = getattr(cls, "_las_original_from_pretrained", None) or cls.from_pretrained.__func__
    cls._las_original_from_pretrained = original
    cls._las_folders = dict(getattr(cls, "_las_folders", {}), **{repo: folder})

    def from_pretrained(klass, name, *args, **kwargs):
        local = cls._las_folders.get(str(name))
        return original(klass, local or name, *args, **kwargs)

    cls.from_pretrained = classmethod(from_pretrained)


class VibeVoiceWorker:
    def __init__(self):
        self.model = None
        self.processor = None
        self.model_id = None
        self.device = "cpu"
        self.voice_cache = {}

    def selftest(self):
        from vibevoice.modular.modeling_vibevoice_inference import VibeVoiceForConditionalGenerationInference  # noqa: F401
        from vibevoice.processor.vibevoice_processor import VibeVoiceProcessor  # noqa: F401
        import transformers

        log(f"vibevoice imported, transformers {transformers.__version__}")

    def prepare(self, report):
        # The default model with its tokenizer. 7B downloads when first used.
        ensure_model(MODELS[DEFAULT_MODEL])

    def load(self, report):
        self._load_model(DEFAULT_MODEL, report)
        report(1.0, "Ready")

    def _load_model(self, model_id, report):
        import torch
        from vibevoice.modular.modeling_vibevoice_inference import VibeVoiceForConditionalGenerationInference
        from vibevoice.processor.vibevoice_processor import VibeVoiceProcessor

        _patch_transformers()
        if self.model is not None:
            log(f"Unloading {self.model_id}")
            self.model = None
            self.processor = None
            self.model_id = None
            if torch.cuda.is_available():
                torch.cuda.empty_cache()

        self.device = torch_device()
        dtype = torch.bfloat16 if self.device == "cuda" else torch.float32
        # flash-attention is what upstream tested, but it has no Windows wheels; SDPA works everywhere.
        attn = "sdpa"
        if self.device == "cuda":
            try:
                import flash_attn  # noqa: F401

                attn = "flash_attention_2"
            except ImportError:
                pass

        if model_id not in MODELS:
            raise ValueError(f"unknown VibeVoice model: {model_id}")
        path = ensure_model(MODELS[model_id])
        report(0.05, f"Loading {model_id}")
        # The processor loads the Qwen tokenizer named in preprocessor_config.json by repo id; it comes from
        # the app's folder instead (workers don't use the Hugging Face cache).
        with open(os.path.join(path, "preprocessor_config.json"), encoding="utf-8") as f:
            tokenizer_repo = json.load(f).get("language_model_pretrained_name") or "Qwen/Qwen2.5-1.5B"
        if tokenizer_repo not in TOKENIZERS:
            raise RuntimeError(f"{model_id} wants the tokenizer {tokenizer_repo}, which workers/models.json doesn't have")
        _load_tokenizer_from(tokenizer_repo, ensure_model(TOKENIZERS[tokenizer_repo]))
        self.processor = VibeVoiceProcessor.from_pretrained(path)
        report(0.3, f"Loading {model_id} weights")
        model = VibeVoiceForConditionalGenerationInference.from_pretrained(
            path, torch_dtype=dtype, device_map=self.device, attn_implementation=attn)
        model.eval()
        self.model = model
        self.model_id = model_id
        log(f"{model_id} loaded on {self.device} ({dtype}, {attn})")

    def _voice_sample(self, req):
        """The reference clip as 24 kHz mono float32: the speaker's clip, or a preset voice."""
        import librosa
        import numpy as np

        path = (req.voice or "").strip()
        if not path:
            name = str(req.settings.get("voice") or DEFAULT_VOICE)
            path = self._preset_path(name)
        elif not os.path.isfile(path):
            raise FileNotFoundError(f"voice reference clip not found: {path}")

        key = (path, os.path.getmtime(path))
        wav = self.voice_cache.get(key)
        if wav is None:
            wav, _ = librosa.load(path, sr=SAMPLE_RATE, mono=True)
            wav = np.asarray(wav, dtype=np.float32)
            self.voice_cache[key] = wav
        return wav

    def _preset_path(self, name):
        if not name.replace("-", "").replace("_", "").isalnum():
            raise ValueError(f"invalid preset voice name: {name}")
        return model_file(VOICES, name + ".wav")

    def generate(self, req, report):
        import torch

        text = " ".join((req.text or "").split()).replace("’", "'")
        if not text:
            raise ValueError("empty text")

        model_id = str(req.settings.get("model") or DEFAULT_MODEL)
        if model_id != self.model_id:
            self._load_model(model_id, lambda f, m=None: report(0.0, m))

        cfg_scale = float(req.settings.get("cfgScale", 1.3))
        steps = int(req.settings.get("steps", 10))
        voice = self._voice_sample(req)

        inputs = self.processor(
            text=[f"Speaker 1: {text}"],
            voice_samples=[[voice]],
            padding=True,
            return_tensors="pt",
            return_attention_mask=True,
        )
        for k, v in inputs.items():
            if torch.is_tensor(v):
                inputs[k] = v.to(self.device)

        self.model.set_ddpm_inference_steps(num_steps=steps)
        expected = max(8.0, len(text) * TOKENS_PER_CHAR)

        class Progress:
            """Stands in for tqdm inside model.generate() to report progress (and cancel)."""

            def __init__(self, iterable, **_):
                self.iterable = iterable

            def __iter__(self):
                for i, step in enumerate(self.iterable):
                    report(min(0.95, 0.05 + 0.9 * i / expected), "Synthesizing")
                    yield step

            def set_description(self, _):
                pass

        report(0.05, "Synthesizing")
        with torch.inference_mode():
            outputs = self.model.generate(
                **inputs,
                max_new_tokens=None,
                cfg_scale=cfg_scale,
                tokenizer=self.processor.tokenizer,
                generation_config={"do_sample": False},
                verbose=False,
                is_prefill=True,
                stop_check_fn=lambda: req.cancelled,
                tqdm_class=Progress,
            )

        audio = outputs.speech_outputs[0] if outputs.speech_outputs else None
        if audio is None or audio.numel() == 0:
            raise RuntimeError("VibeVoice produced no audio")
        duration_ms = write_wav(req.out_path, audio.reshape(-1), SAMPLE_RATE)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": SAMPLE_RATE}


if __name__ == "__main__":
    run_worker(VibeVoiceWorker(), name="vibevoice", model_version="1.5B")
