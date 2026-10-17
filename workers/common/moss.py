"""Shared code of the MOSS-TTS workers (moss_tts, moss_ttsd, moss_voicegen).

All of them are Hugging Face remote-code models (``trust_remote_code``) used the same way: a processor turns
messages (text, reference clips, instruction) into token ids, ``model.generate`` samples audio tokens, and the
processor's codec decodes them. The model folders and the codec folder come from the app (``ensure_model``);
nothing here touches the Hugging Face cache.

Two things differ from upstream's examples:
- Reference clips are read with soundfile and encoded from a tensor. The processors read paths with torchaudio,
  which needs torchcodec and FFmpeg DLLs since torchaudio 2.9.
- The 24 kHz codec's weights are float32 (6.6 GB). On the GPU they are held in bfloat16 and run under autocast,
  or the 8B models plus the codec would not fit a 24 GB card.
"""

import json
import os
import sys
from contextlib import contextmanager

from common.protocol import log

FRAMES_PER_SECOND = 12.5  # every MOSS codec runs at 12.5 Hz


def load(model_dir, codec_dir, device, **processor_kwargs):
    """Returns (processor, model) for a MOSS model folder, with the codec from ``codec_dir``."""
    import torch
    from transformers import AutoModel
    from transformers.dynamic_module_utils import get_class_from_dynamic_module

    # Upstream disables the cuDNN SDPA backend (broken on some CUDA / PyTorch combinations).
    if device == "cuda":
        torch.backends.cuda.enable_cudnn_sdp(False)
        torch.backends.cuda.enable_flash_sdp(True)
        torch.backends.cuda.enable_mem_efficient_sdp(True)
        torch.backends.cuda.enable_math_sdp(True)
    dtype = torch.bfloat16 if device == "cuda" else torch.float32

    # AutoProcessor hands its own extra arguments (revision …) on to the processor's constructor, which rejects
    # them, so the processor class is taken from the model folder's auto_map and called directly.
    with open(os.path.join(model_dir, "processor_config.json"), encoding="utf-8") as f:
        reference = json.load(f)["auto_map"]["AutoProcessor"]
    processor_class = get_class_from_dynamic_module(reference, model_dir)
    processor = processor_class.from_pretrained(model_dir, trust_remote_code=True, codec_path=codec_dir,
                                                **processor_kwargs)
    codec = processor.audio_tokenizer
    # The v2 codec (Local model) keeps its weights in bfloat16 and picks its compute dtype itself, switching
    # autocast off inside; forcing a dtype or autocast on it breaks its convolutions. The 24 kHz codec does neither.
    if hasattr(codec, "set_compute_dtype"):
        processor.audio_tokenizer = codec.to(device)
    elif device == "cuda":
        processor.audio_tokenizer = codec.to(device=device, dtype=dtype)
    else:
        processor.audio_tokenizer = codec.to(device)
    processor._las_plain_codec = hasattr(codec, "set_compute_dtype")
    attn = "sdpa" if device == "cuda" else "eager"
    model = AutoModel.from_pretrained(model_dir, trust_remote_code=True, attn_implementation=attn, torch_dtype=dtype)
    model = model.to(device)
    model.eval()
    sr = int(processor.model_config.sampling_rate)
    log(f"MOSS model loaded from {os.path.basename(model_dir)} on {device}, {sr} Hz, "
        f"{getattr(processor.model_config, 'n_vq', '?')} codebooks")
    return processor, model


def unload(*things):
    import gc

    import torch

    del things
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


def read_clip(path):
    """A reference clip as (float32 tensor of shape (channels, samples), sample rate)."""
    import numpy as np
    import soundfile as sf
    import torch

    data, sr = sf.read(path, dtype="float32", always_2d=True)
    return torch.from_numpy(np.ascontiguousarray(data.T)), int(sr)


@contextmanager
def codec_autocast(device, processor):
    import torch

    if device == "cuda" and not getattr(processor, "_las_plain_codec", False):
        with torch.autocast("cuda", dtype=torch.bfloat16):
            yield
    else:
        yield


class ReferenceCache:
    """Codec tokens of reference clips, by (path, modified time): the same speaker's clip is heard in every segment."""

    def __init__(self):
        self.codes = {}

    def clear(self):
        self.codes.clear()

    def get(self, processor, device, path):
        import torch

        if not os.path.isfile(path):
            raise FileNotFoundError(f"voice reference clip not found: {path}")
        key = (path, os.path.getmtime(path))
        codes = self.codes.get(key)
        if codes is None:
            wav, sr = read_clip(path)
            with torch.inference_mode(), codec_autocast(device, processor):
                codes = processor.encode_audios_from_wav([wav], sr)[0]
            self.codes[key] = codes
        return codes

    def encode_wav(self, processor, device, wav, sr):
        """Tokens of an audio tensor that is not a file (the speakers' clips joined, for TTSD)."""
        import torch

        with torch.inference_mode(), codec_autocast(device, processor):
            return processor.encode_audios_from_wav([wav], sr)[0]


def generate(processor, model, device, conversation, mode, max_new_tokens, expected_steps, report, **sampling):
    """Runs one conversation and returns the audio as a float tensor, (samples,) or (channels, samples), plus its
    sample rate. ``expected_steps`` only sets the progress bar; a cancel raises from ``report``."""
    import torch

    batch = processor([conversation], mode=mode)
    input_ids = batch["input_ids"].to(device)
    attention_mask = batch["attention_mask"].to(device)

    steps = [0]

    def tick():
        steps[0] += 1
        report(min(0.93, 0.05 + 0.88 * steps[0] / max(1.0, expected_steps)), "Synthesizing")

    def progress_tqdm(iterable, *args, **kwargs):
        for x in iterable:
            tick()
            yield x

    # The Delay models loop under tqdm; the Local model has no loop hook, so its global transformer's forward
    # (once per audio frame) is the tick instead.
    module = sys.modules.get(type(model).__module__)
    patched = module is not None and hasattr(module, "tqdm") and not hasattr(model, "local_transformer")
    handle = None
    original = None
    if patched:
        original = module.tqdm
        module.tqdm = progress_tqdm
    elif hasattr(model, "transformer"):
        handle = model.transformer.register_forward_hook(lambda *a: tick())
    try:
        with torch.inference_mode(), codec_autocast(device, processor):
            outputs = model.generate(input_ids=input_ids, attention_mask=attention_mask,
                                     max_new_tokens=int(max_new_tokens), **sampling)
            report(0.95, "Decoding")
            messages = processor.decode(outputs)
    finally:
        if patched:
            module.tqdm = original
        if handle is not None:
            handle.remove()
        if device == "cuda":
            torch.cuda.empty_cache()

    message = messages[0] if messages else None
    if message is None or not message.audio_codes_list:
        raise RuntimeError("MOSS produced no audio (check the text)")
    audio = message.audio_codes_list[0].detach().float().cpu()
    if audio.numel() == 0:
        raise RuntimeError("MOSS produced no audio (check the text)")
    return audio, int(processor.model_config.sampling_rate)


def frame_budget(text, duration_sec=None):
    """(max new steps, expected steps) for a text: about one frame per character of Latin text, up to three for
    Chinese or Japanese, so the cap is generous; a requested duration pins both."""
    if duration_sec:
        frames = int(round(duration_sec * FRAMES_PER_SECOND))
        return min(8192, int(frames * 1.5) + 96), frames
    chars = len(text or "")
    return min(8192, chars * 4 + 200), max(40.0, chars * 1.0)
