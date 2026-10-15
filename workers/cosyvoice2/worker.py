"""CosyVoice 2 worker on PyTorch, with the upstream code (https://github.com/FunAudioLLM/CosyVoice).

Upstream isn't a package: the app downloads the inference files at a pinned commit (models.json "cosyvoice-code",
and "matcha-tts-code" for the flow-matching blocks it imports), and this worker puts them on sys.path. It builds
the model itself instead of going through ``CosyVoice2(model_dir)``: that loads cosyvoice2.yaml with hyperpyyaml,
which also builds the training stack (datasets, discriminators, Lightning). The values below are the yaml's.

Three ways to use the reference clip, as upstream:

- with its transcript    -> zero-shot: the clip's words and speech tokens prompt the LLM (closest match)
- without a transcript   -> cross-lingual: only the speaker embedding and the flow prompt (timbre only)
- with an instruction    -> instruct2: the instruction prompts the LLM, the clip gives the voice

The LLM runs in this thread (not upstream's ``tts()`` thread), so progress and cancelling work per token.
"""

import logging
import os
import sys
import types
import uuid
from functools import partial

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import ensure_model, log, run_worker, write_wav  # noqa: E402

SAMPLE_RATE = 24000
END_OF_PROMPT = "<|endofprompt|>"
MAX_CLIP_SEC = 30  # the speech tokenizer refuses longer clips


def _load_wav(path, target_sr, min_sr=16000):
    """Upstream's load_wav with soundfile: torchaudio.load needs torchcodec (and FFmpeg DLLs) since torchaudio 2.9."""
    import soundfile as sf
    import torch
    import torchaudio

    data, sr = sf.read(path, dtype="float32", always_2d=True)
    speech = torch.from_numpy(data.T.copy()).mean(dim=0, keepdim=True)
    if sr != target_sr:
        speech = torchaudio.functional.resample(speech, sr, target_sr)
    return speech


def _import_upstream():
    """Puts the downloaded upstream code on sys.path and patches what doesn't fit here."""
    for model in ("cosyvoice-code", "matcha-tts-code"):
        path = ensure_model(model)
        if path not in sys.path:
            sys.path.insert(0, path)

    # matcha/utils/__init__.py imports Matcha's training helpers (Lightning, Hydra) and isn't downloaded; the flow
    # code only wants its logger.
    pylogger = types.ModuleType("matcha.utils.pylogger")
    pylogger.get_pylogger = logging.getLogger
    sys.modules["matcha.utils.pylogger"] = pylogger

    import cosyvoice.cli.frontend as frontend
    import cosyvoice.utils.file_utils as file_utils

    frontend.load_wav = file_utils.load_wav = _load_wav
    # file_utils sets the root logger to DEBUG on import, which floods the log with every library's debug lines.
    logging.getLogger().setLevel(logging.INFO)


class CosyVoice2Worker:
    def __init__(self):
        self.frontend = None
        self.model = None
        self.device = None
        self.prompts = {}  # (clip, mtime, transcript) -> the clip's prompt features

    def selftest(self):
        import onnxruntime  # noqa: F401
        import transformers
        import whisper  # noqa: F401
        from diffusers.models.lora import LoRACompatibleLinear  # noqa: F401  (Matcha's transformer needs it)

        log(f"libraries imported, transformers {transformers.__version__}")

    def prepare(self, report):
        for model in ("cosyvoice2-0.5b", "cosyvoice2-0.5b-frontend", "cosyvoice-code", "matcha-tts-code"):
            ensure_model(model)

    def load(self, report):
        import torch
        from omegaconf import DictConfig
        from transformers import Qwen2Config, Qwen2ForCausalLM

        _import_upstream()
        from cosyvoice.cli.frontend import CosyVoiceFrontEnd
        from cosyvoice.cli.model import CosyVoice2Model
        from cosyvoice.flow.decoder import CausalConditionalDecoder
        from cosyvoice.flow.flow import CausalMaskedDiffWithXvec
        from cosyvoice.flow.flow_matching import CausalConditionalCFM
        from cosyvoice.hifigan.f0_predictor import ConvRNNF0Predictor
        from cosyvoice.hifigan.generator import HiFTGenerator
        from cosyvoice.llm.llm import Qwen2Encoder, Qwen2LM
        from cosyvoice.tokenizer.tokenizer import get_qwen_tokenizer
        from cosyvoice.transformer.upsample_encoder import UpsampleConformerEncoder
        from cosyvoice.utils.common import ras_sampling
        from matcha.utils.audio import mel_spectrogram

        weights = ensure_model("cosyvoice2-0.5b")
        extras = ensure_model("cosyvoice2-0.5b-frontend")
        qwen = os.path.join(extras, "CosyVoice-BlankEN")

        class Qwen2FromConfig(Qwen2Encoder):
            # Upstream loads CosyVoice-BlankEN's weights (1 GB) and then overwrites all of them with llm.pt; the
            # architecture is all it needs from there. Only inference calls it, one sequence at a time.
            def __init__(self, path):
                torch.nn.Module.__init__(self)
                self.model = Qwen2ForCausalLM(Qwen2Config.from_pretrained(path))

            def forward_one_step(self, xs, masks, cache=None):
                # Upstream passes the last row of a mask as long as this step's input, which is one token after the
                # first step. transformers 4.57 then masks the cached tokens out, and the LLM never stops (it runs
                # to its length limit, as noise). One unpadded sequence needs no mask: causal attention is the default.
                outs = self.model(inputs_embeds=xs, output_hidden_states=True, return_dict=True,
                                  use_cache=True, past_key_values=cache)
                return outs.hidden_states[-1], outs.past_key_values

        report(0.1, "Building the model")
        llm = Qwen2LM(llm_input_size=896, llm_output_size=896, speech_token_size=6561,
                      length_normalized_loss=True, lsm_weight=0, mix_ratio=[5, 15],
                      llm=Qwen2FromConfig(qwen),
                      sampling=partial(ras_sampling, top_p=0.8, top_k=25, win_size=10, tau_r=0.1))
        flow = CausalMaskedDiffWithXvec(
            input_size=512, output_size=80, spk_embed_dim=192, output_type="mel", vocab_size=6561,
            input_frame_rate=25, only_mask_loss=True, token_mel_ratio=2, pre_lookahead_len=3,
            encoder=UpsampleConformerEncoder(
                input_size=512, output_size=512, attention_heads=8, linear_units=2048, num_blocks=6,
                dropout_rate=0.1, positional_dropout_rate=0.1, attention_dropout_rate=0.1, normalize_before=True,
                input_layer="linear", pos_enc_layer_type="rel_pos_espnet", selfattention_layer_type="rel_selfattn",
                use_cnn_module=False, macaron_style=False, static_chunk_size=25),
            decoder=CausalConditionalCFM(
                in_channels=240, n_spks=1, spk_emb_dim=80,
                cfm_params=DictConfig({"sigma_min": 1e-06, "solver": "euler", "t_scheduler": "cosine",
                                       "training_cfg_rate": 0.2, "inference_cfg_rate": 0.7, "reg_loss_type": "l1"}),
                estimator=CausalConditionalDecoder(
                    in_channels=320, out_channels=80, channels=[256], dropout=0.0, attention_head_dim=64,
                    n_blocks=4, num_mid_blocks=12, num_heads=8, act_fn="gelu",
                    static_chunk_size=25 * 2, num_decoding_left_chunks=-1)))
        hift = HiFTGenerator(
            in_channels=80, base_channels=512, nb_harmonics=8, sampling_rate=SAMPLE_RATE,
            nsf_alpha=0.1, nsf_sigma=0.003, nsf_voiced_threshold=10,
            upsample_rates=[8, 5, 3], upsample_kernel_sizes=[16, 11, 7],
            istft_params={"n_fft": 16, "hop_len": 4},
            resblock_kernel_sizes=[3, 7, 11], resblock_dilation_sizes=[[1, 3, 5], [1, 3, 5], [1, 3, 5]],
            source_resblock_kernel_sizes=[7, 7, 11], source_resblock_dilation_sizes=[[1, 3, 5], [1, 3, 5], [1, 3, 5]],
            lrelu_slope=0.1, audio_limit=0.99,
            f0_predictor=ConvRNNF0Predictor(num_class=1, in_channels=80, cond_channels=512))

        report(0.3, "Loading the speech tokenizer")
        self.frontend = CosyVoiceFrontEnd(
            partial(get_qwen_tokenizer, token_path=qwen, skip_special_tokens=True),
            partial(mel_spectrogram, n_fft=1920, num_mels=80, sampling_rate=SAMPLE_RATE, hop_size=480,
                    win_size=1920, fmin=0, fmax=8000, center=False),
            os.path.join(extras, "campplus.onnx"),
            os.path.join(extras, "speech_tokenizer_v2.onnx"),
            "",
            "all")

        # Upstream picks CUDA whenever torch sees it; on a CPU slot the app hides the GPUs, so this follows the slot.
        report(0.5, "Loading the weights")
        self.model = CosyVoice2Model(llm, flow, hift, fp16=False)
        self.model.load(os.path.join(weights, "llm.pt"), os.path.join(weights, "flow.pt"),
                        os.path.join(weights, "hift.pt"))
        self.device = self.model.device
        log(f"CosyVoice 2 loaded on {self.device}")
        report(1.0, "Ready")

    def _prompt(self, clip, ref_text):
        """The clip's speech tokens, mel features and speaker embedding (and its transcript's tokens), cached."""
        key = (clip, os.path.getmtime(clip), ref_text)
        prompt = self.prompts.get(key)
        if prompt is None:
            import soundfile as sf

            if sf.info(clip).duration > MAX_CLIP_SEC:
                raise ValueError(f"The reference clip is longer than {MAX_CLIP_SEC} s. "
                                 "Use a shorter clip (10 to 20 s works best).")
            text = self.frontend.text_normalize(ref_text, split=False) if ref_text else ""
            prompt = self.frontend.frontend_zero_shot("", text, clip, SAMPLE_RATE, "")
            del prompt["text"], prompt["text_len"]
            self.prompts = {key: prompt}  # one clip at a time: a speaker's segments come in a row
        return prompt

    def _inputs(self, chunk, prompt, ref_text, instruct):
        """Upstream's frontend_zero_shot / frontend_cross_lingual / frontend_instruct2 inputs for one chunk."""
        inputs = dict(prompt)
        inputs["text"], inputs["text_len"] = self.frontend._extract_text_token(chunk)
        if instruct:
            inputs["prompt_text"], inputs["prompt_text_len"] = self.frontend._extract_text_token(instruct + END_OF_PROMPT)
            del inputs["llm_prompt_speech_token"], inputs["llm_prompt_speech_token_len"]
        elif not ref_text:
            for k in ("prompt_text", "prompt_text_len", "llm_prompt_speech_token", "llm_prompt_speech_token_len"):
                del inputs[k]
        return inputs

    def _speech_tokens(self, inputs, progress):
        """Runs the LLM token by token (``CosyVoice2Model.llm_job`` without its thread)."""
        import torch

        dev = self.device
        empty = torch.zeros(1, 0, dtype=torch.int32)
        text = inputs["text"].to(dev)
        prompt_text = inputs.get("prompt_text", empty).to(dev)
        prompt_speech = inputs.get("llm_prompt_speech_token", empty).to(dev)
        # Speech tokens run at 25 per second; a text token is about a quarter of a second of speech.
        expected = max(25, 7 * text.shape[1])
        tokens = []
        for token in self.model.llm.inference(
                text=text,
                text_len=torch.tensor([text.shape[1]], dtype=torch.int32, device=dev),
                prompt_text=prompt_text,
                prompt_text_len=torch.tensor([prompt_text.shape[1]], dtype=torch.int32, device=dev),
                prompt_speech_token=prompt_speech,
                prompt_speech_token_len=torch.tensor([prompt_speech.shape[1]], dtype=torch.int32, device=dev),
                embedding=inputs.get("llm_embedding", torch.zeros(0, 192)).to(dev),
                uuid=""):
            tokens.append(token)
            if len(tokens) % 10 == 0:
                progress(min(0.9, len(tokens) / expected))  # raises Cancelled
        return tokens

    def _wav(self, tokens, inputs, speed):
        """The flow and HiFT over all of a chunk's tokens (``CosyVoice2Model.tts`` without streaming)."""
        import torch

        uid = str(uuid.uuid4())
        self.model.hift_cache_dict[uid] = None
        try:
            speech = self.model.token2wav(
                token=torch.tensor([tokens], dtype=torch.int32),
                prompt_token=inputs["flow_prompt_speech_token"],
                prompt_feat=inputs["prompt_speech_feat"],
                embedding=inputs["flow_embedding"],
                token_offset=0, uuid=uid, finalize=True, speed=speed)
        finally:
            self.model.hift_cache_dict.pop(uid, None)
        return speech.reshape(-1).float().cpu().numpy()

    def generate(self, req, report):
        import numpy as np
        import torch

        text = " ".join((req.text or "").split())
        if not text:
            raise ValueError("empty text")
        clip = (req.voice or "").strip()
        if not clip:
            raise ValueError("CosyVoice 2 needs a reference clip: set one on the speaker (Generation reference).")
        if not os.path.isfile(clip):
            raise FileNotFoundError(f"voice reference clip not found: {clip}")

        s = req.settings
        ref_text = " ".join(str(s.get("refText") or "").split())
        instruct = " ".join(str(s.get("instruct") or "").split())
        speed = float(s.get("speed") or 1.0)

        report(0.02, "Reading the reference clip")
        with torch.inference_mode():
            prompt = self._prompt(clip, ref_text)
            chunks = self.frontend.text_normalize(text, split=True)
            pieces = []
            for i, chunk in enumerate(chunks):
                start, span = 0.05 + 0.9 * i / len(chunks), 0.9 / len(chunks)
                inputs = self._inputs(chunk, prompt, ref_text, instruct)
                tokens = self._speech_tokens(inputs, lambda f: report(start + span * f, "Synthesizing"))
                if not tokens:
                    log(f"no speech tokens for chunk {i + 1}: {chunk!r}")
                    continue
                pieces.append(self._wav(tokens, inputs, speed))
                report(start + span, "Synthesizing")

        if not pieces:
            raise RuntimeError("CosyVoice 2 produced no audio")
        duration_ms = write_wav(req.out_path, np.concatenate(pieces), SAMPLE_RATE)
        report(1.0)
        return {"durationMs": duration_ms, "seed": req.seed, "sampleRate": SAMPLE_RATE}


if __name__ == "__main__":
    run_worker(CosyVoice2Worker(), name="cosyvoice2", model_version="0.5B")
