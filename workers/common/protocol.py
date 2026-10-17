"""Shared worker protocol helper (protocol version 2).

A worker only implements ``load(report)`` and ``generate(req, report)`` (and optionally
``prepare(report)``: download the model files before ``load`` reads them). A worker that listens instead of
generating implements what its tags say, in place of ``generate`` (``req.audio_path`` is a 16 kHz mono 16-bit WAV,
``req.text`` the text it should contain, if known):

- tag Transcription: ``transcribe(req, report)`` returns ``{"text", "language"}``, the words heard;
- tags Transcription and Alignment: ``transcribe_align(req, report)`` returns ``{"text", "words", "language"}``, the
  words heard with their times. A ``transcribe`` request goes to it when there is no ``transcribe`` (its words are
  dropped);
- tag Speaker Verification: ``compare_voices(req, report)`` returns ``{"scores", "matrix", "speechMs", "model"}`` for
  ``req.files`` (``[{"path", "role": "reference" or "candidate"}]``, 16 kHz mono WAVs, two or more): ``scores`` one per
  candidate against the references (several: their average), ``matrix`` every pair in request order, ``speechMs`` the
  speech in each file, ``model`` what scored (scores of different models are never compared);
- tag Alignment Forced: ``align(req, report)`` returns ``{"words": [{"text", "startMs", "endMs", "confidence"}]}``, the
  times of ``req.text``'s words as written (it needs the text, and doesn't check that they were said). With
  Transcription, it is the full set: it also implements ``transcribe_align``, timing its own transcript;
- tag Diarization (alone, without Transcription): ``diarize(req, report)`` returns ``{"segments": [{"startMs", "endMs",
  "speaker"}]}``, who speaks when: the turns in order, their ``"text"`` empty or left out, turns of different voices
  overlapping when they talk at once. ``req.settings`` may carry ``numSpeakers``, ``minSpeakers`` and ``maxSpeakers``.
  The app pairs it with a transcriber: each heard word gets the voice of the turn it falls in.

A worker tagged Diarization tells voices apart, with labels "S1", "S2" … (S0: a voice it can't place; any other string
works too, such as a name it knows), the same for one voice within the transcript. With Transcription it also answers
``diarize`` with its ``transcribe_diarize`` blocks, which the app reads as turns. ``req.speakers`` (names, in the order they first speak) and ``req.speaker_count`` say who and how many,
when the app knows; a hint that any worker may ignore. How the voices are reported depends on the other tags:

- with Transcription and Alignment, each word returned by ``transcribe_align`` may have a ``"speaker"``;
- with Transcription alone: ``transcribe_diarize(req, report)`` returns ``{"text", "segments", "language"}``, the text
  as timed blocks, ``"segments": [{"text", "startMs", "endMs", "speaker"}]`` in order, each said by one voice (blocks
  may overlap in time when voices talk at once). Words are not needed: a block is as fine a timing as the model has.
  A worker that also times words may add a ``"block"`` to each word (the index of its segment). The app uses the
  blocks as they are (they are compared with the expected text word by word, sharing the block's
  time), and cuts fused generations at their edges when they line up with the parts. When an aligner times the words
  afterwards, each aligned word gets the speaker and block it came from. A ``transcribe`` request goes to
  ``transcribe_diarize`` when there is no ``transcribe`` (its blocks are joined into the text).

A worker whose manifest declares ``fusion`` also implements ``generate_parts(req, report)``: ``req.parts`` is a
list of :class:`Part` (segments to generate, and lines of context around them), ``req.settings`` the settings they
share. It writes them all into ``req.out_path`` and returns what ``generate`` does, plus optionally
``"spans": [(startMs, endMs) or None, ...]``, one per part, where it knows them. It raises :class:`Unfusable` for a
combination it can't do; the app then generates the segments one by one. This module handles
the rest: the ``hello`` handshake, newline-delimited JSON over stdin/stdout, redirecting stray
``print`` output to stderr, turning exceptions into ``error`` messages, cooperative cancellation,
progress throttling, the ``.partial`` rename, and ``shutdown``. Models come from the app:
``ensure_model(id)`` asks for one (ids are in workers/models.json) and returns its folder.

Stdout carries protocol messages only. Everything else (logging, print) goes to stderr, which the
app captures into the worker log.
"""

from __future__ import annotations

import itertools
import json
import os
import queue
import random
import sys
import threading
import time
import traceback
from dataclasses import dataclass, field
from typing import Any, Callable, Optional

PROTOCOL_VERSION = 2

# Keep the real stdout for protocol messages and send everything else to stderr.
_PROTO_OUT = sys.stdout
sys.stdout = sys.stderr
_SEND_LOCK = threading.Lock()
NEWLINE = bytes([10])
BOM = chr(0xFEFF)


def log(*args: Any) -> None:
    """Write a line to the worker log (stderr)."""
    print(*args, file=sys.stderr, flush=True)


def send(msg: dict) -> None:
    line = json.dumps(msg, ensure_ascii=False)
    with _SEND_LOCK:
        _PROTO_OUT.write(line + "\n")
        _PROTO_OUT.flush()


class Cancelled(Exception):
    """Raised from ``report()`` when the app cancelled the running request."""


class Unfusable(Exception):
    """Raised from ``generate_parts`` when the worker can't render these parts together (too many voices,
    settings it can't mix, …). Not a failure: the app generates the segments one by one instead."""


@dataclass
class Part:
    """One part of a fused generation (``Request.parts``)."""

    role: str                   # "generate": a segment the app keeps; "context": a neighbouring line, cut away
    speaker: str                # parts with the same key are the same voice ("s1", "s2" …)
    text: str = ""
    voice: Optional[str] = None  # a reference clip's path or a voice id, as Request.voice (None: pick one)
    settings: dict = field(default_factory=dict)  # the voice's settings (manifest perVoice), context parts too
    audio_path: Optional[str] = None  # context parts: the line's existing take (WAV), if it has one

    @property
    def is_context(self) -> bool:
        return self.role == "context"


@dataclass
class Request:
    id: str
    text: Optional[str] = None  # the segment's text: spoken, or a description of the sound or music to make
    duration_sec: Optional[float] = None
    voice: Optional[str] = None
    settings: dict = field(default_factory=dict)  # with parts: only the settings all parts share
    seed: Optional[int] = None
    out_path: str = ""          # where the worker must write the WAV (already ends in .partial)
    final_path: str = ""        # the path the app asked for
    audio_path: str = ""        # transcribe: the audio to listen to (16 kHz mono 16-bit WAV)
    parts: list = field(default_factory=list)  # a fused generation's parts (list of Part), in order
    speakers: list = field(default_factory=list)  # listening: who may be speaking (names, in order), when known; a hint
    speaker_count: Optional[int] = None  # listening: how many voices there are, when known; a hint
    files: list = field(default_factory=list)  # compareVoices: [{"path", "role": "reference" or "candidate"}], in order
    cancelled: bool = False


def seed_everything(seed: int) -> None:
    """Seed Python, NumPy and torch (if they are already imported or importable)."""
    random.seed(seed)
    try:
        import numpy as np

        np.random.seed(seed % (2**32))
    except Exception:
        pass
    try:
        import torch

        torch.manual_seed(seed)
        if torch.cuda.is_available():
            torch.cuda.manual_seed_all(seed)
    except Exception:
        pass


def write_wav(path: str, audio: Any, sample_rate: int) -> int:
    """Write mono or (channels, samples)/(samples, channels) float audio as 16-bit PCM WAV.

    Uses only the standard library and NumPy, so it works for any path (including ``.partial``).
    Returns the duration in milliseconds.
    """
    import wave

    import numpy as np

    try:
        import torch  # noqa: F401

        if hasattr(audio, "detach"):
            audio = audio.detach().float().cpu().numpy()
    except Exception:
        pass
    a = np.asarray(audio, dtype=np.float32)
    if a.ndim == 1:
        a = a[:, None]
    elif a.ndim == 2 and a.shape[0] < a.shape[1] and a.shape[0] <= 8:
        a = a.T  # (channels, samples) -> (samples, channels)
    a = np.clip(a, -1.0, 1.0)
    pcm = (a * 32767.0).astype("<i2")
    with wave.open(path, "wb") as w:
        w.setnchannels(pcm.shape[1])
        w.setsampwidth(2)
        w.setframerate(int(sample_rate))
        w.writeframes(pcm.tobytes())
    return int(round(pcm.shape[0] * 1000.0 / sample_rate))


def worker_settings() -> dict:
    """This worker's machine settings from the Workers window (manifest ``workerSettings``), with the
    manifest defaults filled in. Example: the ComfyUI bridge's server addresses."""
    raw = os.environ.get("LAS_WORKER_SETTINGS")
    if not raw:
        return {}
    try:
        value = json.loads(raw)
        return value if isinstance(value, dict) else {}
    except Exception:
        log("ignoring unparseable LAS_WORKER_SETTINGS")
        return {}


def lane() -> int:
    """For external workers running several lanes in parallel: this process's 0-based lane number
    (lets each lane pick its own server)."""
    try:
        return int(os.environ.get("LAS_LANE", "0"))
    except ValueError:
        return 0


# Replies to the worker's own requests (needModel, needFile), by ref. The stdin thread fills it.
_REPLIES: dict = {}
_REPLIES_CHANGED = threading.Condition()
_REF_COUNTER = itertools.count(1)
_STATE = {"id": None, "eof": False}  # the app request being handled; stdin closed


def _ask(msg: dict) -> dict:
    """Sends a request to the app and waits for its reply (same ``ref``). Blocks for as long as the app
    takes: a model download can take many minutes, and the app pauses its hang watchdog meanwhile."""
    ref = f"r{next(_REF_COUNTER)}"
    send(dict(msg, ref=ref, id=_STATE["id"]))
    with _REPLIES_CHANGED:
        while ref not in _REPLIES:
            if _STATE["eof"]:
                raise RuntimeError("the app went away")
            _REPLIES_CHANGED.wait(1.0)
        return _REPLIES.pop(ref)


def _check_reply(reply: dict, ok_type: str, what: str) -> str:
    if reply.get("type") == ok_type and reply.get("path"):
        return reply["path"]
    if reply.get("cancelled"):
        raise Cancelled()
    raise RuntimeError(reply.get("message") or f"{what} is not available")


def ensure_model(model_id: str) -> str:
    """The folder of a model from workers/models.json (an id like "kokoro-82m"). The app downloads it
    (and the models it requires) the first time, checks every file and shows the progress itself; this call
    waits until the model is there. Load the model from this folder, not by repo id: libraries given a repo
    id try the Hugging Face cache, which workers don't use (HF_HUB_OFFLINE is set)."""
    log(f"asking the app for model {model_id}")
    return _check_reply(_ask({"type": "needModel", "model": model_id}), "modelReady", model_id)


def model_file(model_id: str, file: str) -> str:
    """An extra file of a model that models.json doesn't list (a voice, "voices/af_heart.pt"): the app
    downloads it into the model's folder from the model's source once, and returns its path."""
    return _check_reply(_ask({"type": "needFile", "model": model_id, "file": file}), "fileReady", f"{model_id}/{file}")


def torch_device() -> str:
    """The device this process should use: "cpu" on a CPU slot (LAS_DEVICE=cpu), else "cuda" when
    available. On a CPU slot also applies LAS_CPU_THREADS (the slot's thread limit) to torch."""
    import torch

    if os.environ.get("LAS_DEVICE") == "cpu" or not torch.cuda.is_available():
        threads = os.environ.get("LAS_CPU_THREADS")
        if threads and threads.isdigit() and int(threads) > 0:
            torch.set_num_threads(int(threads))
        return "cpu"
    return "cuda"


def fix_espeak_data_path() -> None:
    """Points misaki's espeak-ng at a short path to the espeakng_loader data when the real one is too long.

    libespeak-ng keeps its data path in a 160-byte buffer on Linux and silently truncates longer paths,
    then falls back to the path baked in on espeakng-loader's CI machine ("Error processing file
    '/home/runner/work/...phontab'"). A venv deep in the data folder easily exceeds that, so link the
    data folder from the temp dir. Call before the first G2P / KPipeline is created.

    phonemizer resolve()s a path given to set_data_path (undoing the link), so the link reaches espeak
    through ESPEAK_DATA_PATH instead, which names the folder that contains espeak-ng-data."""
    import hashlib
    import tempfile

    import espeakng_loader
    from misaki import espeak  # noqa: F401  (sets the loader's paths on import; overridden below)
    from phonemizer.backend.espeak.wrapper import EspeakWrapper

    data = espeakng_loader.get_data_path()
    if len(data.encode("utf-8")) < 140 or os.name == "nt":
        return
    parent = os.path.dirname(data)
    if os.path.basename(data) != "espeak-ng-data":
        log(f"espeak-ng data folder is named {os.path.basename(data)}; can't shorten its path.")
        return
    link = os.path.join(tempfile.gettempdir(),
                        "las-espeak-" + hashlib.sha1(parent.encode("utf-8")).hexdigest()[:12])
    try:
        if not (os.path.islink(link) and os.readlink(link) == parent):
            tmp = f"{link}.{os.getpid()}"
            os.symlink(parent, tmp)
            os.replace(tmp, link)
    except OSError as e:
        log(f"espeak-ng data path is {len(data)} characters and couldn't be shortened ({e}); "
            "espeak may not find its data.")
        return
    os.environ.pop("PHONEMIZER_ESPEAK_DATA_PATH", None)
    os.environ["ESPEAK_DATA_PATH"] = link
    EspeakWrapper.set_data_path(None)


def device_description() -> str:
    cvd = os.environ.get("CUDA_VISIBLE_DEVICES")
    if cvd is None:
        return "cuda:0 (all GPUs visible)"
    if cvd.strip() == "":
        return "cpu"
    return f"cuda:0 (GPU {cvd})"


def _default_selftest(worker: Any) -> int:
    """Import the worker's libraries (via ``worker.selftest`` if present) and check CUDA."""
    try:
        if hasattr(worker, "selftest"):
            worker.selftest()
        # LAS_DEVICES (from the manifest's "devices") says whether CUDA is required.
        devices = [d.strip() for d in os.environ.get("LAS_DEVICES", "cuda").split(",") if d.strip()]
        # requires_cuda = False: the worker checks the GPU itself in selftest() (no torch, e.g. ONNX Runtime).
        if not getattr(worker, "requires_cuda", True) or "cuda" not in devices:
            log("selftest OK" + ("" if "cuda" in devices else " (CPU worker)"))
            return 0
        import torch

        cuda = torch.cuda.is_available()
        log(f"torch {torch.__version__}, CUDA available: {cuda}")
        if cuda:
            log(f"device 0: {torch.cuda.get_device_name(0)}")
        elif "cpu" in devices:
            log("CUDA is not available; this worker will only run on the CPU.")
        elif os.environ.get("LAS_ALLOW_CPU") != "1":
            log("CUDA is not available (set LAS_ALLOW_CPU=1 to accept CPU-only).")
            return 2
        log("selftest OK")
        return 0
    except Exception:
        traceback.print_exc(file=sys.stderr)
        return 1


def _stdin_lines():
    """Yields stdin lines without ever blocking inside a read call on Windows.

    On Windows a thread blocked in ReadFile on a synchronous pipe also blocks every other call
    on that handle, including GetFileType, which each DLL's C runtime makes while loading.
    Importing NumPy or torch in the main thread would then hang until the next message arrives.
    So on Windows we peek at the pipe and only read bytes that are already there.
    """
    if os.name != "nt":
        yield from sys.stdin.buffer
        return

    import ctypes
    import msvcrt
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    peek = kernel32.PeekNamedPipe
    peek.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p,
                     ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p]
    peek.restype = wintypes.BOOL
    fd = sys.stdin.fileno()
    handle = msvcrt.get_osfhandle(fd)
    available = wintypes.DWORD(0)
    pending = b""
    while True:
        if not peek(handle, None, 0, None, ctypes.byref(available), None):
            break  # pipe closed: the app went away
        if available.value == 0:
            time.sleep(0.02)
            continue
        chunk = os.read(fd, available.value)
        if not chunk:
            break
        pending += chunk
        while NEWLINE in pending:
            line, pending = pending.split(NEWLINE, 1)
            yield line + NEWLINE
    if pending:
        yield pending


def _stdin_reader(q: "queue.Queue[Optional[dict]]", current: dict) -> None:
    """Reads stdin on a background thread so ``cancel`` arrives while a generation runs."""
    for raw_bytes in _stdin_lines():
        # Tolerate a UTF-8 byte-order mark (some hosts write one at the start of the stream).
        raw = raw_bytes.decode("utf-8", errors="replace").lstrip(BOM).strip()
        if not raw:
            continue
        try:
            msg = json.loads(raw)
        except Exception:
            log(f"ignoring unparseable input: {raw[:200]}")
            continue
        mtype = msg.get("type")
        if mtype == "cancel":
            req = current.get("request")
            if req is not None and (msg.get("id") in (None, req.id)):
                req.cancelled = True
            continue
        if mtype in ("modelReady", "modelFailed", "fileReady", "fileFailed"):
            with _REPLIES_CHANGED:
                _REPLIES[msg.get("ref")] = msg
                _REPLIES_CHANGED.notify_all()
            continue
        q.put(msg)
    with _REPLIES_CHANGED:
        _STATE["eof"] = True
        _REPLIES_CHANGED.notify_all()
    q.put(None)  # EOF: the app went away


def run_worker(worker: Any, name: str, model_version: str) -> None:
    if "--selftest" in sys.argv:
        sys.exit(_default_selftest(worker))

    # Diagnostics: LAS_DEBUG_HANG=<seconds> dumps every thread's stack to the worker log periodically.
    hang = os.environ.get("LAS_DEBUG_HANG")
    if hang:
        import faulthandler

        faulthandler.dump_traceback_later(float(hang), repeat=True, file=sys.__stderr__)

    send({"type": "hello", "protocol": PROTOCOL_VERSION, "worker": name,
          "modelVersion": model_version, "device": device_description()})

    inbox: "queue.Queue[Optional[dict]]" = queue.Queue()
    current: dict = {"request": None}
    threading.Thread(target=_stdin_reader, args=(inbox, current), daemon=True).start()

    while True:
        msg = inbox.get()
        if msg is None:
            break
        mtype = msg.get("type")
        mid = msg.get("id")
        if mtype == "shutdown":
            break
        _STATE["id"] = mid  # model requests belong to this request (its progress, its cancel)
        try:
            if mtype == "load":
                report = _make_reporter(mid, None)
                # Optional prepare stage: cheap checks and model downloads. "prepared" tells the app the
                # files are in place, so it may start other processes of this worker while this one
                # does the slow part (reading the weights onto the device).
                if hasattr(worker, "prepare"):
                    worker.prepare(report)
                    send({"type": "prepared", "id": mid})
                # Workers import torch and their model library at the start of load(), which alone can
                # take 10 s or more; say so, or the UI keeps showing the last prepare message.
                send({"type": "progress", "id": mid, "fraction": 0.05, "message": "Importing libraries (torch, model code)…"})
                worker.load(report)
                send({"type": "ready", "id": mid})
            elif mtype == "generate":
                _handle_generate(worker, msg, current)
            elif mtype == "compareVoices":
                _handle_compare_voices(worker, msg, current)
            elif mtype in ("transcribe", "align", "transcribeAlign", "transcribeDiarize", "diarize"):
                _handle_listen(worker, mtype, msg, current)
            elif mtype == "listVoices":
                voices = worker.list_voices() if hasattr(worker, "list_voices") else []
                send({"type": "voices", "id": mid, "voices": voices})
            else:
                send({"type": "error", "id": mid, "message": f"unknown message type: {mtype}", "fatal": False})
        except Cancelled:
            send({"type": "error", "id": mid, "message": "cancelled", "fatal": False})
        except Unfusable as e:
            send({"type": "error", "id": mid, "message": str(e) or "can't render these parts together",
                  "fatal": False, "code": "unfusable"})
        except Exception as e:  # noqa: BLE001
            message = f"{type(e).__name__}: {e}"
            fatal = "out of memory" in message.lower() or isinstance(e, (MemoryError, SystemError))
            send({"type": "error", "id": mid, "message": message, "fatal": fatal,
                  "trace": traceback.format_exc()})
        finally:
            current["request"] = None
            _STATE["id"] = None

    try:
        if hasattr(worker, "shutdown"):
            worker.shutdown()
    finally:
        sys.exit(0)


def _make_reporter(mid: str, req: Optional[Request]) -> Callable[..., None]:
    """Progress callback: ``report(fraction, message=None)``. Throttled to 10 per second.
    Raises :class:`Cancelled` when the app cancelled the request."""
    last = [0.0]

    def report(fraction: float = 0.0, message: Optional[str] = None) -> None:
        if req is not None and req.cancelled:
            raise Cancelled()
        now = time.monotonic()
        if now - last[0] < 0.1 and fraction < 1.0:
            return
        last[0] = now
        m = {"type": "progress", "id": mid, "fraction": max(0.0, min(1.0, float(fraction)))}
        if message:
            m["message"] = message
        send(m)

    return report


def _handle_generate(worker: Any, msg: dict, current: dict) -> None:
    final_path = msg.get("outPath") or ""
    if not final_path:
        raise ValueError("generate request has no outPath")
    seed = msg.get("seed")
    if seed is None:
        seed = (msg.get("settings") or {}).get("seed")
    if seed is None:
        seed = random.randint(0, 2**31 - 1)
    seed = int(seed)

    parts = [
        Part(
            role=str(p.get("role") or "generate"),
            speaker=str(p.get("speaker") or ""),
            text=p.get("text") or "",
            voice=p.get("voice"),
            settings=p.get("settings") or {},
            audio_path=p.get("audioPath"),
        )
        for p in msg.get("parts") or []
    ]
    if parts and not hasattr(worker, "generate_parts"):
        raise Unfusable("this worker doesn't render several parts together")

    req = Request(
        id=msg.get("id"),
        text=msg.get("text"),
        duration_sec=msg.get("durationSec"),
        voice=msg.get("voice"),
        settings=msg.get("settings") or {},
        seed=seed,
        out_path=final_path + ".partial",
        final_path=final_path,
        parts=parts,
    )
    current["request"] = req
    os.makedirs(os.path.dirname(final_path) or ".", exist_ok=True)
    seed_everything(seed)

    report = _make_reporter(req.id, req)
    try:
        result = (worker.generate_parts(req, report) if parts else worker.generate(req, report)) or {}
        if req.cancelled:
            raise Cancelled()
        if not os.path.exists(req.out_path):
            raise RuntimeError("worker returned without writing audio")
        os.replace(req.out_path, final_path)
    except BaseException:
        try:
            if os.path.exists(req.out_path):
                os.remove(req.out_path)
        except OSError:
            pass
        raise

    done = {
        "type": "done",
        "id": req.id,
        "path": final_path,
        "durationMs": int(result.get("durationMs", 0)),
        "seed": int(result.get("seed", seed)),
        "sampleRate": int(result.get("sampleRate", 0)),
    }
    spans = result.get("spans")
    if parts and spans:
        if len(spans) != len(parts):
            log(f"ignoring spans: {len(spans)} for {len(parts)} parts")
        else:
            done["spans"] = [None if s is None else {"startMs": int(s[0]), "endMs": int(s[1])} for s in spans]
    send(done)


def _handle_compare_voices(worker: Any, msg: dict, current: dict) -> None:
    """``compareVoices``: how alike the voices in two or more audio files are, answered with ``voiceScores``."""
    method = getattr(worker, "compare_voices", None)
    if method is None:
        raise RuntimeError("this worker doesn't compare voices (tag Speaker Verification)")
    files = [{"path": str(f.get("path") or ""), "role": str(f.get("role") or "candidate")} for f in msg.get("files") or []]
    if len(files) < 2:
        raise ValueError("comparing voices takes at least two audio files")
    for f in files:
        if not os.path.isfile(f["path"]):
            raise FileNotFoundError(f"no audio at {f['path']}")
    req = Request(id=msg.get("id"), settings=msg.get("settings") or {}, files=files)
    current["request"] = req
    result = method(req, _make_reporter(req.id, req)) or {}
    if req.cancelled:
        raise Cancelled()
    send({
        "type": "voiceScores",
        "id": req.id,
        "scores": [float(s) for s in result.get("scores") or []],
        "matrix": [[float(v) for v in row] for row in result.get("matrix") or []],
        "speechMs": [int(v) for v in result.get("speechMs") or []],
        "model": str(result.get("model") or ""),
    })


def _handle_listen(worker: Any, mtype: str, msg: dict, current: dict) -> None:
    """``transcribe`` (the words heard), ``align`` (the times of a known text's words), ``transcribeAlign`` (both) or
    ``transcribeDiarize`` (the text as timed blocks by voice), each answered with a ``transcript``."""
    if mtype == "transcribe":
        method = (getattr(worker, "transcribe", None) or getattr(worker, "transcribe_align", None)
                  or getattr(worker, "transcribe_diarize", None))
        missing = "doesn't transcribe audio (tag Transcription)"
    elif mtype == "transcribeDiarize":
        method = getattr(worker, "transcribe_diarize", None) or getattr(worker, "transcribe_align", None)
        missing = "doesn't tell voices apart (tags Transcription and Diarization)"
    elif mtype == "diarize":
        method = getattr(worker, "diarize", None) or getattr(worker, "transcribe_diarize", None)
        missing = "doesn't diarize audio (tag Diarization)"
    elif mtype == "align":
        method = getattr(worker, "align", None)
        missing = "doesn't align a known text (tag Alignment Forced)"
    else:
        method = getattr(worker, "transcribe_align", None)
        missing = "doesn't time the words it hears (tags Transcription and Alignment)"
    if method is None:
        raise RuntimeError(f"this worker {missing}")
    audio_path = msg.get("audioPath") or ""
    if not os.path.isfile(audio_path):
        raise FileNotFoundError(f"no audio at {audio_path}")
    req = Request(
        id=msg.get("id"),
        text=msg.get("text"),
        settings=msg.get("settings") or {},
        audio_path=audio_path,
        speakers=list(msg.get("speakers") or []),
        speaker_count=msg.get("speakerCount"),
    )
    if mtype == "align" and not (req.text or "").strip():
        raise ValueError("an align request needs the text to align")
    current["request"] = req
    result = method(req, _make_reporter(req.id, req)) or {}
    if req.cancelled:
        raise Cancelled()
    if mtype == "transcribe":
        result = dict(result, words=[], segments=[])  # text only, as asked
    elif mtype == "diarize":
        # Turns only, as asked: a worker that also hears (its blocks of text) gives the voices and times without the words.
        result = dict(result, text="", words=[], segments=[dict(s, text="") for s in result.get("segments") or []])
    segments = [
        {
            "text": str(s.get("text") or ""),
            "startMs": int(s["startMs"]),
            "endMs": int(s["endMs"]),
            **({"speaker": str(s["speaker"])} if s.get("speaker") is not None else {}),
        }
        for s in result.get("segments") or []
        if mtype == "diarize" or str(s.get("text") or "").strip()  # (a turn has no text)
    ]
    words = [
        {
            "text": str(w["text"]),
            "startMs": int(w["startMs"]),
            "endMs": int(w["endMs"]),
            **({"confidence": float(w["confidence"])} if w.get("confidence") is not None else {}),
            **({"speaker": str(w["speaker"])} if w.get("speaker") is not None else {}),
            **({"block": int(w["block"])} if w.get("block") is not None else {}),
        }
        for w in result.get("words") or []
    ]
    send({
        "type": "transcript",
        "id": req.id,
        "text": result.get("text") or " ".join(s["text"].strip() for s in segments).strip() or " ".join(w["text"] for w in words),
        "words": words,
        "segments": segments,
        "language": result.get("language"),
    })
