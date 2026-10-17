"""ComfyUI bridge worker: runs a ComfyUI workflow per generation and returns its audio. The same worker also
transcribes, aligns and compares voices with a workflow: see "listening" below.

ComfyUI manages its own GPU, so this process needs no CUDA. The app starts up to "Parallel workers"
processes of it; each has a lane number (``protocol.lane()``) and uses server ``lane % len(servers)``
from the ``servers`` worker setting (Workers window), so several ComfyUI instances render in parallel.

A generation:
1. reads the speaker's workflow (a project asset, ``*.comfy.json``, exported with "Export (API)") and
   fills in its tags: a constant node titled ``{{name}}`` gets the value, and ``{{name}}`` in a text widget
   is replaced. ``text``, ``seed`` and ``duration`` are the segment's; every other tag is a speaker
   setting of that name (the app lists them, see ComfyWorkflow.cs; keep the two in step). A Load Audio
   node titled with a tag gets an audio file from the project's assets;
2. uploads those audio files once per server into ``input/las/`` (named by their content hash);
3. swaps Save Audio nodes for Preview Audio, so the result lands in ComfyUI's temp folder (emptied when
   ComfyUI starts) instead of its output folder;
4. queues it (POST /prompt) and follows it on the websocket for progress and errors;
5. downloads the audio (GET /view), writes it as WAV, and deletes the prompt from ComfyUI's history.
"""

import hashlib
import io
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.protocol import Cancelled, lane, log, run_worker, worker_settings, write_wav  # noqa: E402

MODEL_VERSION = "1"
CLOUD = "https://cloud.comfy.org"  # used when no server is configured
TAG = re.compile(r"\{\{\s*(\w+)\s*\}\}")
MISSING = object()  # a tag without a value: a constant node keeps its own
SAVE_NODES = {"SaveAudio", "SaveAudioMP3", "SaveAudioOpus", "SaveAudioAdvanced"}
SEED_INPUTS = {"seed", "noise_seed"}


def server_list(settings: dict) -> list:
    raw = str(settings.get("servers") or "")
    servers = []
    for line in raw.replace(",", "\n").splitlines():
        s = line.strip().rstrip("/")
        if not s:
            continue
        if "://" not in s:
            s = ("https://" if is_cloud(s) else "http://") + s
        servers.append(s)
    return servers or [CLOUD]


def is_cloud(server: str) -> bool:
    """Comfy Cloud (cloud.comfy.org) speaks ComfyUI's API under /api and wants an API key."""
    host = urllib.parse.urlparse(server if "://" in server else "//" + server).hostname or ""
    return host == "comfy.org" or host.endswith(".comfy.org")


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Lets us read a redirect's Location instead of following it (see ComfyBridge._download)."""

    def redirect_request(self, *args, **kwargs):
        return None


def target_input(inputs: dict):
    """The input a node titled with a tag holds its value in: "value" (ComfyUI's Int, Float, String and Boolean
    nodes), "audio" (Load Audio: an audio file to upload), otherwise its only widget. Links (lists) aren't widgets."""
    widgets = [k for k, v in inputs.items() if not isinstance(v, list)]
    if "value" in widgets:
        return "value"
    if "audio" in widgets:
        return "audio"
    return widgets[0] if len(widgets) == 1 else None


LISTEN_TAGS = ("audio", "reference", "candidate")  # files a listening job provides


def is_result(node):
    """The node whose text is a listening job's answer: titled {{result}}."""
    title = str((node.get("_meta") or {}).get("title") or "").strip()
    m = TAG.fullmatch(title)
    return bool(m) and m.group(1) == "result"


def text_of(output):
    """The text a node put in its history output (Preview Any: "text"; other nodes use other keys), or None."""
    for key in ("text", "string", "result"):
        value = output.get(key)
        if value is not None:
            output = {key: value}
            break
    for value in output.values():
        if isinstance(value, list) and value and isinstance(value[0], str):
            return value[0]
        if isinstance(value, str):
            return value
    return None


def parse_words(items):
    """Words as the protocol wants them. Times are ``startMs``/``endMs`` (or ``start``/``end`` in seconds)."""
    words = []
    for w in items:
        if not isinstance(w, dict):
            raise RuntimeError(f"A word in the workflow's {{{{result}}}} is not an object: {w!r}")
        text = w.get("text", w.get("word"))
        if "startMs" in w and "endMs" in w:
            start, end = w["startMs"], w["endMs"]
        elif "start" in w and "end" in w:
            start, end = float(w["start"]) * 1000, float(w["end"]) * 1000
        else:
            raise RuntimeError("A word in the workflow's {{result}} has no times (startMs and endMs, or start and end in seconds).")
        if text is None:
            raise RuntimeError("A word in the workflow's {{result}} has no text.")
        words.append({"text": str(text), "startMs": int(start), "endMs": int(end)})
    return words


def parse_transcript(text):
    """A listening result: plain text, JSON ``{"text", "words", "language"}``, or a JSON list of words."""
    stripped = text.strip()
    if stripped[:1] in ("{", "["):
        try:
            data = json.loads(stripped)
        except ValueError:
            data = None
        if isinstance(data, list):
            words = parse_words(data)
            return {"text": " ".join(w["text"] for w in words), "words": words, "language": None}
        if isinstance(data, dict):
            words = parse_words(data.get("words") or [])
            return {"text": str(data.get("text") or " ".join(w["text"] for w in words)), "words": words,
                    "language": data.get("language")}
    return {"text": stripped, "words": [], "language": None}


def parse_score(text):
    """A voice comparison's result: a number, or JSON with a ``score``."""
    stripped = text.strip()
    try:
        data = json.loads(stripped)
    except ValueError:
        raise RuntimeError(f"The workflow's {{{{result}}}} is not a score: {stripped[:80]!r}") from None
    if isinstance(data, dict):
        data = data.get("score")
    if isinstance(data, bool) or not isinstance(data, (int, float)):
        raise RuntimeError(f"The workflow's {{{{result}}}} is not a score: {stripped[:80]!r}")
    return float(data)


class ComfyError(RuntimeError):
    code = None  # the HTTP status, when the server answered


class ComfyBridge:
    requires_cuda = False

    def __init__(self):
        self.server = None
        self.cloud = False          # Comfy Cloud: paths under /api, API key, outputs kept and fetched via a redirect
        self.api = ""               # path prefix: "/api" on Comfy Cloud
        self.api_key = ""
        self.client_id = uuid.uuid4().hex
        self.timeout = 600
        self.uploaded = {}          # reference clips already on this lane's server -> the name Load Audio takes
        self.workflows = {}         # path -> (mtime, workflow)
        self.ws_failed = False      # logged once, not for every generation

    def selftest(self):
        import soundfile  # noqa: F401
        import websocket  # noqa: F401

        log(f"servers: {server_list(worker_settings())}")

    # ------------------------------------------------------------------ HTTP

    def _request(self, path, body=None, timeout=30, content_type="application/json"):
        data = None
        headers = {}
        if body is not None:
            data = body if isinstance(body, bytes) else json.dumps(body).encode("utf-8")
            headers["Content-Type"] = content_type
        if self.cloud:
            headers["X-API-Key"] = self.api_key
        req = urllib.request.Request(self.server + self.api + path, data=data, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            error = ComfyError(self._http_error(e))
            error.code = e.code
            raise error from e
        except (urllib.error.URLError, OSError) as e:
            raise ComfyError(f"ComfyUI at {self.server} did not answer: {e}") from e

    def _download(self, query):
        """The bytes of a result file. Comfy Cloud answers /view with a redirect to a signed storage URL, which
        must be fetched without the API key, so the redirect is read here instead of followed."""
        if not self.cloud:
            return self._request("/view?" + query, timeout=120)
        req = urllib.request.Request(f"{self.server}{self.api}/view?{query}", headers={"X-API-Key": self.api_key})
        try:
            with urllib.request.build_opener(_NoRedirect).open(req, timeout=60) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            location = e.headers.get("Location")
            if e.code not in (301, 302, 303, 307, 308) or not location:
                raise ComfyError(self._http_error(e)) from e
        try:
            with urllib.request.urlopen(location, timeout=120) as r:
                return r.read()
        except (urllib.error.URLError, OSError) as e:
            raise ComfyError(f"Could not download the result from Comfy Cloud: {e}") from e

    def _json(self, path, body=None, timeout=30):
        raw = self._request(path, body, timeout)
        return json.loads(raw.decode("utf-8")) if raw else {}

    def _http_error(self, e):
        """ComfyUI's validation errors (400 on /prompt) as one readable line."""
        try:
            info = json.loads(e.read().decode("utf-8"))
        except Exception:  # noqa: BLE001
            return f"ComfyUI answered {e.code} {e.reason}"
        if not isinstance(info, dict):
            return f"ComfyUI answered {e.code} {e.reason}"
        parts = []
        err = info.get("error")
        if isinstance(err, dict):
            parts.append(err.get("message") or "")
            if err.get("details"):
                parts.append(str(err["details"]))
        elif err:
            parts.append(str(err))
        if info.get("message"):  # Comfy Cloud's plain errors
            parts.append(str(info["message"]))
        for node_id, node in (info.get("node_errors") or {}).items():
            for ne in node.get("errors", []):
                parts.append(f"node {node_id} ({node.get('class_type', '?')}): {ne.get('message', '')} {ne.get('details', '')}".strip())
        hint = {401: "check the API key", 402: "Comfy Cloud is out of credits",
                429: "the Comfy Cloud plan has no API access or is inactive"}.get(e.code) if self.cloud else None
        text = "; ".join(p for p in parts if p) or f"{e.code} {e.reason}"
        return f"ComfyUI refused the request ({hint}): {text}" if hint else f"ComfyUI refused the workflow: {text}"

    # ------------------------------------------------------------------ worker

    def load(self, report):
        settings = worker_settings()
        servers = server_list(settings)
        self.server = servers[lane() % len(servers)]
        self.cloud = is_cloud(self.server)
        self.api = "/api" if self.cloud else ""
        self.api_key = str(settings.get("apiKey") or "").strip()
        self.timeout = int(settings.get("timeoutSec") or 600)
        if self.cloud and not self.api_key:
            raise RuntimeError("Comfy Cloud needs an API key. Create one at platform.comfy.org (Profile > API Keys) "
                               "and enter it in Workers > ComfyUI bridge > Comfy Cloud API key.")
        report(0.5, f"Connecting to {self.server}")
        if self.cloud:
            self._json("/user", timeout=10)  # 401 for a wrong key, 429 for a plan without API access
            log(f"lane {lane()}: Comfy Cloud accepted the API key")
        else:
            stats = self._json("/system_stats", timeout=10)
            devices = ", ".join(d.get("name", "?") for d in stats.get("devices", []))
            log(f"lane {lane()}: ComfyUI at {self.server} is up ({devices or 'no device info'})")
        report(1.0, f"Connected to {self.server}")

    def _execute(self, workflow, report):
        """Queues the workflow and waits for it. Returns (prompt id, history entry); the caller forgets the prompt
        once it has what it needs from the entry."""
        ws = self._connect_ws()
        try:
            prompt_id = self._json("/prompt", {"prompt": workflow, "client_id": self.client_id})["prompt_id"]
            log(f"queued prompt {prompt_id} on {self.server}")
            try:
                entry = self._wait(prompt_id, ws, report)
            except Cancelled:
                self._cancel(prompt_id)
                raise
            except Exception:
                self._forget(prompt_id)  # a failed prompt stays in ComfyUI's history otherwise
                raise
        finally:
            if ws is not None:
                try:
                    ws.close()
                except Exception:  # noqa: BLE001
                    pass
        return prompt_id, entry

    def generate(self, req, report):
        workflow = self._prepare(req)
        outputs = self._redirect_outputs(workflow)
        prompt_id, entry = self._execute(workflow, report)
        try:
            audio = self._find_audio(entry, outputs)
            report(0.97, "Downloading the audio")
            query = urllib.parse.urlencode({"filename": audio["filename"], "subfolder": audio.get("subfolder", ""),
                                            "type": audio.get("type", "temp")})
            data = self._download(query)
        finally:
            self._forget(prompt_id)

        import soundfile as sf

        samples, rate = sf.read(io.BytesIO(data), dtype="float32", always_2d=True)
        ms = write_wav(req.out_path, samples, rate)
        return {"durationMs": ms, "seed": req.seed, "sampleRate": rate}

    # ------------------------------------------------------------------ listening
    # The same workflow setting does these jobs too; a workflow ends in a node titled {{result}} (Preview Any, Show
    # Text …) whose text is the answer. A workflow without what a job needs just fails with a message saying so.

    def transcribe_align(self, req, report):
        """The words heard. The result is plain text, or JSON ``{"text", "words": [{"text", "startMs", "endMs"}]}``."""
        text = self._listen(req, report, {"audio": req.audio_path})
        return parse_transcript(text)

    def align(self, req, report):
        """The times of ``req.text``'s words. The result is JSON: a list of words, or ``{"words": [...]}``."""
        text = self._listen(req, report, {"audio": req.audio_path})
        result = parse_transcript(text)
        if not result["words"]:
            raise RuntimeError("The workflow's {{result}} has no word times. Aligning needs JSON: a list of "
                               '{"text", "startMs", "endMs"} words, or {"words": [...]}.')
        return result

    def compare_voices(self, req, report):
        """Scores every pair of files with the workflow ({{reference}} and {{candidate}} are Load Audio nodes); the
        result is a number, or JSON with a ``score``. Each candidate's score is its average against the references."""
        import soundfile as sf

        files = req.files
        n = len(files)
        matrix = [[1.0 if i == j else 0.0 for j in range(n)] for i in range(n)]
        pairs = [(i, j) for i in range(n) for j in range(i + 1, n)]
        for k, (i, j) in enumerate(pairs):
            a, b = files[i], files[j]
            if b["role"] == "reference" and a["role"] != "reference":
                a, b = b, a  # the reference goes in {{reference}}, the other file is the candidate
            score = parse_score(self._listen(
                req, lambda f, m, k=k: report((k + f) / len(pairs), m), {"reference": a["path"], "candidate": b["path"]}))
            matrix[i][j] = matrix[j][i] = score
        references = [i for i, f in enumerate(files) if f["role"] == "reference"]
        candidates = [i for i, f in enumerate(files) if f["role"] != "reference"]
        scores = [sum(matrix[c][r] for r in references) / len(references) for c in candidates] if references else []
        speech = [int(sf.info(f["path"]).duration * 1000) for f in files]
        return {"scores": scores, "matrix": matrix, "speechMs": speech,
                "model": "comfyui:" + os.path.basename(str(req.settings.get("workflow") or ""))}

    def _listen(self, req, report, extra):
        """Runs the workflow with the job's audio and returns the text of its {{result}} node."""
        workflow = self._prepare(req, extra)
        result_nodes = [node_id for node_id, node in workflow.items() if is_result(node)]
        if not result_nodes:
            raise RuntimeError("The workflow has no node titled {{result}}. Title a Preview Any (or Show Text) node "
                               "{{result}}: its text is the answer.")
        prompt_id, entry = self._execute(workflow, report)
        try:
            if entry.get("status", {}).get("status_str") == "error":
                raise RuntimeError(self._history_error(entry))
            results = entry.get("outputs") or {}
            for node_id in result_nodes:
                text = text_of(results.get(node_id) or {})
                if text is not None:
                    return text
            raise RuntimeError("The workflow's {{result}} node produced no text.")
        finally:
            self._forget(prompt_id)

    # ------------------------------------------------------------------ workflow

    def _load_workflow(self, path):
        if not path:
            raise RuntimeError("No workflow chosen. Set the speaker's Workflow to a file saved with "
                               "ComfyUI's Workflow > Export (API).")
        if not os.path.isfile(path):
            raise FileNotFoundError(f"Workflow not found: {path}")
        mtime = os.path.getmtime(path)
        cached = self.workflows.get(path)
        if cached and cached[0] == mtime:
            return json.loads(json.dumps(cached[1]))
        with open(path, encoding="utf-8-sig") as f:
            wf = json.load(f)
        if isinstance(wf, dict) and isinstance(wf.get("prompt"), dict):
            wf = wf["prompt"]
        if not isinstance(wf, dict) or "nodes" in wf or not all(isinstance(n, dict) and "class_type" in n for n in wf.values()):
            raise RuntimeError(f"{os.path.basename(path)} is not in ComfyUI's API format. "
                               "Open it in ComfyUI and save it with Workflow > Export (API).")
        self.workflows[path] = (mtime, wf)
        return json.loads(json.dumps(wf))

    def _prepare(self, req, extra=None):
        """The workflow with every tag filled in. Values are only worked out when a tag asks for them, so a
        workflow that leaves an audio setting empty uploads nothing. ``extra`` has the files of a listening job."""
        s = req.settings
        extra = extra or {}
        seed = req.seed if req.seed is not None else (s["seed"] if isinstance(s.get("seed"), int) else 0)
        workflow = self._load_workflow(str(s.get("workflow") or "").strip())
        used = set()

        def value(name):
            """The tag's value, or MISSING: no length for this segment, or a setting the speaker never set
            (the workflow's own value stays)."""
            used.add(name)
            if name == "text":
                return req.text or ""
            if name == "seed":
                return int(seed)
            if name in extra:
                return extra[name]
            if name == "duration":
                return float(req.duration_sec) if req.duration_sec else MISSING
            v = s.get(name)
            return MISSING if v is None else v

        def required(name):
            """A tag in a text widget has nothing to fall back on."""
            v = value(name)
            if v is not MISSING:
                return v
            if name == "duration":
                raise RuntimeError("This workflow needs a length ({{duration}}). Set a length on the segment.")
            if name in LISTEN_TAGS:
                raise RuntimeError(f"This workflow uses {{{{{name}}}}}, which this job doesn't provide "
                                   "({{audio}}: transcribing and aligning; {{reference}} and {{candidate}}: comparing voices).")
            return ""

        def fill(v):
            if not isinstance(v, str) or "{{" not in v:
                return v
            whole = TAG.fullmatch(v.strip())
            if whole:  # the whole value: keep the type (a seed stays a number)
                return required(whole.group(1))
            return TAG.sub(lambda m: str(required(m.group(1))), v)

        for node in workflow.values():
            inputs = node.get("inputs") or {}
            # A node titled {{name}}: a constant (Int, Float, String, Boolean) or Load Audio.
            title = str((node.get("_meta") or {}).get("title") or "").strip()
            titled = None if is_result(node) else TAG.fullmatch(title)  # {{result}} is read, not set
            target = target_input(inputs) if titled else None
            for key, v in inputs.items():
                if key == target:
                    new = value(titled.group(1))
                    if new is not MISSING:
                        inputs[key] = self._upload_audio(new) if key == "audio" else new
                else:
                    inputs[key] = fill(v)
            if titled and target is None:
                log(f"node {title} ({node.get('class_type')}) has no single value to set; ignored")

        # Without {{seed}}, every plain seed input gets the take's seed, so regenerating gives a new take.
        if "seed" not in used:
            for node in workflow.values():
                inputs = node.get("inputs") or {}
                for key in SEED_INPUTS & inputs.keys():
                    if isinstance(inputs[key], (int, float)) and not isinstance(inputs[key], bool):
                        inputs[key] = int(seed)
        return workflow

    def _redirect_outputs(self, workflow):
        """Turns Save Audio nodes into Preview Audio (ComfyUI's temp folder instead of its output folder) and
        returns the ids of the nodes that produce audio, in workflow order. Comfy Cloud has no folders to keep
        tidy (its results are removed with the history entry), so its Save Audio nodes stay as they are."""
        outputs = []
        for node_id, node in workflow.items():
            kind = node.get("class_type")
            if kind in SAVE_NODES:
                if not self.cloud:
                    node["class_type"] = "PreviewAudio"
                    node["inputs"] = {"audio": node.get("inputs", {}).get("audio")}
                outputs.append(node_id)
            elif kind == "PreviewAudio":
                outputs.append(node_id)
        if not outputs:
            raise RuntimeError("The workflow has no audio output. Add a Save Audio or Preview Audio node.")
        return outputs

    def _upload_audio(self, path):
        """Uploads an audio file to the server's input/las folder, named by its content so each file goes up
        once and re-uploads overwrite it instead of piling up copies. Returns the name Load Audio takes."""
        path = str(path)
        if not os.path.isfile(path):
            raise FileNotFoundError(f"Audio file not found: {path}")
        with open(path, "rb") as f:
            data = f.read()
        name = hashlib.sha256(data).hexdigest()[:20] + (os.path.splitext(path)[1].lower() or ".wav")
        if name not in self.uploaded:
            boundary = uuid.uuid4().hex
            parts = []
            for field, val in (("type", "input"), ("subfolder", "las"), ("overwrite", "true")):
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{field}"\r\n\r\n{val}\r\n'.encode())
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="image"; filename="{name}"\r\n'
                         f"Content-Type: application/octet-stream\r\n\r\n".encode() + data + b"\r\n")
            parts.append(f"--{boundary}--\r\n".encode())
            raw = self._request("/upload/image", b"".join(parts), timeout=120,
                                content_type=f"multipart/form-data; boundary={boundary}")
            # The server says where the file landed; Comfy Cloud ignores the subfolder and stores by content.
            info = json.loads(raw.decode("utf-8")) if raw else {}
            stored = info.get("name") or name
            sub = info.get("subfolder") or ""
            self.uploaded[name] = f"{sub}/{stored}" if sub else stored
            log(f"uploaded {path} as {self.uploaded[name]}")
        return self.uploaded[name]

    # ------------------------------------------------------------------ running

    def _connect_ws(self):
        """The websocket for progress; without it the bridge still works by polling the history."""
        import websocket

        url = self.server.replace("http://", "ws://", 1).replace("https://", "wss://", 1)
        query = f"clientId={self.client_id}" + (f"&token={urllib.parse.quote(self.api_key)}" if self.cloud else "")
        try:
            ws = websocket.create_connection(f"{url}/ws?{query}", timeout=10)
            ws.settimeout(0.5)
            return ws
        except Exception as e:  # noqa: BLE001
            if not self.ws_failed:
                log(f"no websocket ({e}); polling for progress instead")
                self.ws_failed = True
            return None

    def _wait(self, prompt_id, ws, report):
        """Waits until the prompt finished and returns its history entry. Raises on errors and timeouts."""
        import websocket

        deadline = time.monotonic() + self.timeout
        fraction, message = 0.02, "Waiting in the ComfyUI queue"
        next_poll = time.monotonic() + 2.0
        finished = False  # the websocket said so; the history entry follows a moment later
        while True:
            report(fraction, message)  # raises Cancelled
            if time.monotonic() > deadline:
                self._cancel(prompt_id)
                raise RuntimeError(f"ComfyUI took longer than {self.timeout} s (Timeout in the worker settings).")
            if ws is not None:
                try:
                    raw = ws.recv()
                except websocket.WebSocketTimeoutException:
                    raw = None
                except Exception as e:  # noqa: BLE001
                    log(f"websocket closed ({e}); polling instead")
                    ws, raw = None, None
                if isinstance(raw, str):
                    msg = json.loads(raw)
                    data = msg.get("data") or {}
                    if data.get("prompt_id") == prompt_id:
                        kind = msg.get("type")
                        if kind == "execution_start":
                            fraction, message = 0.1, "Running"
                        elif kind == "progress" and data.get("max"):
                            step = float(data.get("value", 0)) / float(data["max"])
                            fraction = 0.1 + 0.85 * step
                            message = f"Running (step {data.get('value')}/{data.get('max')})"
                        elif kind == "execution_error":
                            raise RuntimeError(f"ComfyUI failed in {data.get('node_type', 'a node')}: "
                                               f"{data.get('exception_message', '').strip()}")
                        elif kind == "execution_interrupted":
                            raise RuntimeError("The prompt was interrupted in ComfyUI.")
                        elif kind == "execution_success" or (kind == "executing" and data.get("node") is None):
                            finished, next_poll = True, time.monotonic()
            else:
                time.sleep(0.25 if finished else 0.5)
            if time.monotonic() >= next_poll:
                next_poll = time.monotonic() + (0.25 if finished else 2.0)
                entry = self._job_entry(prompt_id)
                if entry and entry.get("status", {}).get("completed", entry.get("outputs") is not None):
                    return entry
                if entry and entry.get("status", {}).get("status_str") == "error":
                    raise RuntimeError(self._history_error(entry))

    def _job_entry(self, prompt_id):
        """The prompt's history entry, or None while it isn't there yet. Comfy Cloud has /jobs/<id> instead of
        /history/<id>; its answer is turned into the same shape (status.completed, outputs, error messages)."""
        if not self.cloud:
            return self._json(f"/history/{prompt_id}").get(prompt_id)
        try:
            job = self._json(f"/jobs/{prompt_id}")
        except ComfyError as e:
            if e.code == 404:  # not known yet
                return None
            raise
        state = job.get("status")
        if state == "completed":
            return {"status": {"completed": True, "status_str": "success"}, "outputs": job.get("outputs") or {}}
        if state in ("failed", "cancelled"):
            err = job.get("execution_error") or {}
            data = {"node_type": err.get("node_type", "a node"),
                    "exception_message": err.get("exception_message") or f"the job {state}."}
            return {"status": {"status_str": "error", "messages": [["execution_error", data]]}}
        return None

    @staticmethod
    def _history_error(entry):
        for kind, data in entry.get("status", {}).get("messages", []):
            if kind == "execution_error":
                return f"ComfyUI failed in {data.get('node_type', 'a node')}: {data.get('exception_message', '').strip()}"
        return "ComfyUI reported an error."

    def _find_audio(self, entry, outputs):
        if entry.get("status", {}).get("status_str") == "error":
            raise RuntimeError(self._history_error(entry))
        results = entry.get("outputs") or {}
        for node_id in outputs + [n for n in results if n not in outputs]:
            audio = (results.get(node_id) or {}).get("audio")
            if audio:
                return audio[0]
        raise RuntimeError("The workflow finished without audio.")

    def _cancel(self, prompt_id):
        """Takes the prompt out of the queue, or stops it when it is the one running. Older servers ignore
        prompt_id on /interrupt and stop whatever runs, so it is only sent when that is this prompt."""
        try:
            queue = {} if self.cloud else self._json("/queue", timeout=10)  # Comfy Cloud cancels either kind this way
            running = any(len(item) > 1 and item[1] == prompt_id for item in queue.get("queue_running", []))
            if running:
                self._request("/interrupt", {"prompt_id": prompt_id}, timeout=10)
            else:
                self._request("/queue", {"delete": [prompt_id]}, timeout=10)
        except Exception as e:  # noqa: BLE001
            log(f"could not cancel prompt {prompt_id}: {e}")
        self._forget(prompt_id)

    def _forget(self, prompt_id):
        try:
            self._request("/history", {"delete": [prompt_id]}, timeout=10)
        except Exception as e:  # noqa: BLE001
            log(f"could not delete prompt {prompt_id} from ComfyUI's history: {e}")


if __name__ == "__main__":
    run_worker(ComfyBridge(), name="comfyui_bridge", model_version=MODEL_VERSION)
