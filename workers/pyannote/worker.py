"""pyannote community-1 speaker diarization on ONNX Runtime (CPU or CUDA).

The same pipeline as the built-in runtime (src/LocalAudiobookStudio.InternalWorker/PyannoteEngine.cs and
DiarizationMath.cs, ports of pyannote.audio 4.0.7, MIT), with numpy and scipy around the two networks: windows of 10 s
every second through the segmentation network, one embedding per voice that is active in a window, agglomerative
clustering, PLDA and VBx, and the windows put back together into turns. The converted ONNX models are Sidekick's
(CC-BY-4.0, see workers/models.json). Keep the two runtimes in step.

One script for both ONNX runtimes: the onnx1_30-cpu environment has the CPU build of onnxruntime, the onnx1_30-cu13
environment the CUDA build (NVIDIA's libraries come as packages and are loaded with onnxruntime.preload_dlls()).
"""

import json
import os
import sys
import threading
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

from common.audio import SAMPLE_RATE, read_wav  # noqa: E402
from common.protocol import ensure_model, log, run_worker  # noqa: E402

MODEL = "pyannote-community-1-onnx"  # workers/models.json

# Kaldi filterbank (kaldi-native-fbank / torchaudio.compliance.kaldi.fbank) as WeSpeaker takes it: Hamming window.
WINDOW = 400
SHIFT = 160
FFT = 512
MEL_BINS = 80
LOW_HZ = 20.0
PREEMPHASIS = 0.97
MAX_AGGLOMERATIVE = 6000  # most embeddings the agglomerative step groups at once


def _cuda_build():
    """True in the CUDA environment (onnxruntime-gpu). Loads NVIDIA's pip-installed libraries first."""
    import onnxruntime as ort

    if "CUDAExecutionProvider" not in ort.get_available_providers():
        return False
    if hasattr(ort, "preload_dlls"):
        ort.preload_dlls()
    return True


def _mel(hz):
    import numpy as np

    return 1127.0 * np.log(1.0 + hz / 700.0)


_FBANK = {}


def _fbank_tables():
    if not _FBANK:
        import numpy as np

        bins = FFT // 2
        width = SAMPLE_RATE / FFT
        low, high = _mel(LOW_HZ), _mel(SAMPLE_RATE / 2.0)
        delta = (high - low) / (MEL_BINS + 1)
        mel = _mel(width * np.arange(bins))
        weights = np.zeros((MEL_BINS, bins), dtype=np.float32)
        for b in range(MEL_BINS):
            left, center, right = low + b * delta, low + (b + 1) * delta, low + (b + 2) * delta
            rising = (mel > left) & (mel <= center)
            falling = (mel > center) & (mel < right)
            weights[b, rising] = ((mel - left) / (center - left))[rising]
            weights[b, falling] = ((right - mel) / (right - center))[falling]
        i = np.arange(WINDOW)
        _FBANK["window"] = (0.54 - 0.46 * np.cos(2 * np.pi * i / (WINDOW - 1))).astype(np.float32)
        _FBANK["mel"] = weights
    return _FBANK["window"], _FBANK["mel"]


def fbank(chunks, frames_wanted):
    """Features [n, frames_wanted, 80] of windows [n, samples] (already scaled to the 16-bit range), with each window's
    mean taken off the features. Same arithmetic as Fbank.cs."""
    import numpy as np

    window, mel = _fbank_tables()
    n, total = chunks.shape
    frames = (total - WINDOW) // SHIFT + 1
    index = np.arange(frames)[:, None] * SHIFT + np.arange(WINDOW)[None, :]
    out = np.zeros((n, frames_wanted, MEL_BINS), dtype=np.float32)
    for c in range(n):
        x = chunks[c].astype(np.float64)[index]                      # [frames, 400]
        x = x - x.mean(axis=1, keepdims=True)
        prev = np.concatenate([x[:, :1], x[:, :-1]], axis=1)         # frame[0] is pre-emphasised against itself
        x = (x - PREEMPHASIS * prev) * window
        power = np.abs(np.fft.rfft(x, n=FFT, axis=1)[:, : FFT // 2]) ** 2
        feats = np.log(np.maximum(power @ mel.T.astype(np.float64), 1.1920929e-07))
        feats -= feats.mean(axis=0, keepdims=True)
        keep = min(frames, frames_wanted)
        out[c, :keep] = feats[:keep]
    return out


# ---------------------------------------------------------------------------------------------- arithmetic


def closest_frame(t, frame_duration, frame_step):
    import numpy as np

    return np.rint((t - 0.5 * frame_duration) / frame_step).astype(np.int64)


def aggregate(scores, frames_per_chunk, chunk_duration, chunk_step, frame_duration, frame_step, skip_average):
    """Adds up overlapping chunks [chunks, frames, classes] into one track [frames, classes] (NaN: missing)."""
    import numpy as np

    chunks, _, classes = scores.shape
    frames = int(closest_frame(chunk_duration + (chunks - 1) * chunk_step + 0.5 * frame_duration, frame_duration, frame_step)) + 1
    total = np.zeros((frames, classes))
    count = np.zeros((frames, classes))
    for c in range(chunks):
        first = int(closest_frame(c * chunk_step + 0.5 * frame_duration, frame_duration, frame_step))
        n = min(frames_per_chunk, frames - first)
        piece = scores[c, :n]
        ok = ~np.isnan(piece)
        total[first:first + n] += np.where(ok, piece, 0)
        count[first:first + n] += ok
    if skip_average:
        return total
    return np.where(count == 0, 0, total / np.maximum(count, 1e-12))


class Plda:
    def __init__(self, data):
        import numpy as np

        def vec(n):
            return np.array(n, dtype=np.float64)

        def mat(n):
            return np.array(n["data"], dtype=np.float64).reshape(n["shape"])

        self.mean1, self.mean2, self.mu, self.phi = vec(data["mean1"]), vec(data["mean2"]), vec(data["mu"]), vec(data["phi"])
        self.lda, self.transform = mat(data["lda"]), mat(data["transform"])

    def __call__(self, x):
        import numpy as np

        def unit(v):
            return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-300)

        v = unit(x - self.mean1)
        w = unit(np.sqrt(self.lda.shape[0]) * v @ self.lda - self.mean2) * np.sqrt(self.lda.shape[1])
        return (w - self.mu) @ self.transform.T


def vbx(x, phi, fa, fb, initial, max_iters, epsilon):
    """VBx (Landini et al.), as utils/vbx.py with cluster_vbx's settings. Returns the responsibilities and priors."""
    import numpy as np

    frames, dim = x.shape
    speakers = initial.shape[1]
    gamma = initial.copy()
    pi = np.full(speakers, 1.0 / speakers)
    constant = -0.5 * ((x ** 2).sum(axis=1) + dim * np.log(2 * np.pi))
    rho = x * np.sqrt(phi)
    previous = None
    for iteration in range(max_iters):
        total = gamma.sum(axis=0)                                        # [S]
        inv_l = 1.0 / (1 + fa / fb * total[:, None] * phi[None, :])      # (17) [S, D]
        alpha = fa / fb * inv_l * (gamma.T @ rho)                        # (16)
        penalty = ((inv_l + alpha ** 2) * phi).sum(axis=1)
        log_pi = np.log(pi + 1e-8)
        log_p = fa * (rho @ alpha.T - 0.5 * penalty[None, :] + constant[:, None])   # (23)
        scored = log_p + log_pi
        peak = scored.max(axis=1, keepdims=True)
        log_px = peak[:, 0] + np.log(np.exp(scored - peak).sum(axis=1))
        gamma = np.exp(scored - log_px[:, None])
        pi = gamma.sum(axis=0)
        pi = pi / pi.sum()
        regulariser = (np.log(inv_l) - inv_l - alpha ** 2 + 1).sum()
        elbo = log_px.sum() + fb * 0.5 * regulariser                      # (25)
        if iteration > 0 and elbo - previous < epsilon:
            break
        previous = elbo
    return gamma, pi


def kmeans(vectors, clusters):
    """k-means on unit vectors with a fixed start (farthest point); the same stand-in as DiarizationMath.KMeans."""
    import numpy as np

    n = len(vectors)
    clusters = min(clusters, n)
    centers = [vectors[0].astype(np.float64)]
    while len(centers) < clusters:
        d = np.min([((vectors - c) ** 2).sum(axis=1) for c in centers], axis=0)
        centers.append(vectors[int(np.argmax(d))].astype(np.float64))
    centers = np.array(centers)
    labels = np.zeros(n, dtype=int)
    for round_ in range(100):
        d = ((vectors[:, None, :] - centers[None, :, :]) ** 2).sum(axis=2)
        best = d.argmin(axis=1)
        changed = bool((best != labels).any())
        labels = best
        for k in range(clusters):
            members = labels == k
            if members.any():
                centers[k] = vectors[members].mean(axis=0)
        if not changed and round_ > 0:
            break
    return labels


def turns_of(discrete, frame_duration, frame_step):
    """(start, end, speaker) per run of active frames, sorted by start (Binarize with onset = offset = 0.5)."""
    import numpy as np

    frames, speakers = discrete.shape
    turns = []
    if frames == 0:
        return turns

    def middle(f):
        return f * frame_step + frame_duration / 2

    for k in range(speakers):
        on = (discrete[:, k] > 0.5).astype(np.int8)
        edges = np.diff(np.concatenate([[0], on, [0]]))
        starts = np.nonzero(edges == 1)[0]
        stops = np.nonzero(edges == -1)[0]     # the first frame that is off, or `frames` when it runs to the end
        for a, b in zip(starts, stops):
            start = middle(0) if a == 0 else middle(a)
            end = middle(frames - 1) if b >= frames else middle(b)
            turns.append((start, end, k))
    turns.sort(key=lambda t: (t[0], t[2]))
    return turns


# ---------------------------------------------------------------------------------------------- the worker


class PyannoteWorker:
    # The protocol's default self-test would import torch to check CUDA; this environment has none.
    requires_cuda = False

    def __init__(self):
        self.model_dir = None
        self.config = None
        self.segmentation = None
        self.embedding = None
        self.plda = None
        self.device = "cpu"
        self.cuda_lock = threading.Lock()

    def selftest(self):
        import onnxruntime as ort

        cuda = _cuda_build()
        log(f"onnxruntime {ort.__version__}, providers {ort.get_available_providers()}")
        if "cuda" in os.environ.get("LAS_DEVICES", "") and not cuda:
            raise RuntimeError("This is the CUDA runtime, but onnxruntime has no CUDA provider (the CPU build got installed?).")

    def prepare(self, report):
        self.model_dir = ensure_model(MODEL)

    def load(self, report):
        import onnxruntime as ort

        if self.model_dir is None:
            self.prepare(report)
        report(0.1, "Loading the pyannote models (ONNX)")
        with open(os.path.join(self.model_dir, "community-1-plda.json"), encoding="utf-8") as f:
            self.config = json.load(f)
        self.plda = Plda(self.config["plda"])
        cuda = _cuda_build() and os.environ.get("LAS_DEVICE") != "cpu"
        options = ort.SessionOptions()
        if not cuda:
            options.intra_op_num_threads = 1  # many runs at once, one thread each (see _concurrency)
        # CUDA_VISIBLE_DEVICES already pins the app's chosen GPU, so it is device 0 here.
        providers = ([("CUDAExecutionProvider", {"device_id": 0})] if cuda else []) + ["CPUExecutionProvider"]
        self.segmentation = ort.InferenceSession(os.path.join(self.model_dir, "community-1-segmentation.onnx"), options, providers=providers)
        self.embedding = ort.InferenceSession(os.path.join(self.model_dir, "community-1-embedding.onnx"), options, providers=providers)
        if cuda and "CUDAExecutionProvider" not in self.embedding.get_providers():
            raise RuntimeError("CUDA could not be used (ONNX Runtime 1.30 needs an NVIDIA driver with CUDA 13, 580 or newer).")
        self.device = "cuda" if cuda else "cpu"
        report(1.0, "Ready")
        log(f"pyannote community-1 loaded on {self.device}, providers {self.embedding.get_providers()}")

    @staticmethod
    def _concurrency():
        threads = os.environ.get("LAS_CPU_THREADS")
        return int(threads) if threads and threads.isdigit() and int(threads) > 0 else (os.cpu_count() or 4)

    def diarize(self, req, report):
        import numpy as np
        from scipy.cluster.hierarchy import fcluster, linkage
        from scipy.optimize import linear_sum_assignment

        cfg = self.config
        seg, emb, clu = cfg["segmentation"], cfg["embedding"], cfg["clustering"]
        if cfg["sample_rate"] != SAMPLE_RATE:
            raise RuntimeError(f"the models want {cfg['sample_rate']} Hz audio")
        settings = req.settings or {}
        overlap = str(settings.get("overlap", False)).lower() == "true"
        num, low, high = (int(settings.get(k) or 0) for k in ("numSpeakers", "minSpeakers", "maxSpeakers"))
        if num == 0 and low == 0 and high == 0:
            num = int(req.speaker_count or 0)
        if num > 0:
            low = high = num
        low = max(low, 1)
        high = high if high > 0 else 1 << 30

        audio = read_wav(req.audio_path)
        window, step = seg["window_samples"], seg["step_samples"]
        frames, speakers = seg["num_frames"], seg["num_speakers"]
        frame_duration, frame_step = seg["frame_duration"], seg["frame_step"]
        chunk_duration, chunk_step = seg["chunk_duration"], seg["chunk_step"]
        powerset = np.array(seg["powerset"], dtype=np.float32)
        total = len(audio)
        complete = (total - window) // step + 1 if total >= window else 0
        chunks = complete + (1 if total < window or (total - window) % step > 0 else 0)

        def chunk_audio(first, count):
            out = np.zeros((count, window), dtype=np.float32)
            for b in range(count):
                start = (first + b) * step
                if start < total:
                    piece = audio[start:start + window]
                    out[b, :len(piece)] = piece
            return out

        cuda = self.device == "cuda"
        pool = ThreadPoolExecutor(max_workers=1 if cuda else self._concurrency())

        def run(session, feed, outputs=None):
            return session.run(outputs, feed)

        # ---- Segmentation: which local speakers talk in each frame.
        batch = 32 if cuda else 4
        starts = list(range(0, chunks, batch))
        binary = np.zeros((chunks, frames, speakers), dtype=np.float32)
        done = [0]

        def segment(first):
            count = min(batch, chunks - first)
            out = run(self.segmentation, {self.segmentation.get_inputs()[0].name: chunk_audio(first, count)[:, None, :]})[0]
            binary[first:first + count] = powerset[out.argmax(axis=2)]
            done[0] += 1
            report(0.05 + 0.35 * done[0] / len(starts), "Finding who talks when")

        list(pool.map(segment, starts))

        speaker_sums = binary.sum(axis=2, keepdims=True)
        counts = np.rint(aggregate(speaker_sums, frames, chunk_duration, chunk_step, frame_duration, frame_step, False)[:, 0]).astype(int)
        if len(counts) == 0 or counts.max() == 0:
            return {"segments": []}

        # ---- Embeddings: one per local speaker active in a chunk, weighted by when they talk alone (if enough).
        min_frames = int(np.ceil(frames * emb["min_num_samples"] / window))
        others = speaker_sums[:, :, 0]
        jobs = []                                  # (chunk, speaker, weights)
        for c in range(chunks):
            for sp in range(speakers):
                mask = binary[c, :, sp]
                if mask.sum() == 0:
                    continue
                clean = np.where(others[c] < 2, mask, 0)
                jobs.append((c, sp, clean if clean.sum() > min_frames else mask))
        by_chunk = {}
        for job in jobs:
            by_chunk.setdefault(job[0], []).append(job)
        groups = list(by_chunk.values())
        pool_frames, fbank_frames = emb["pool_frames"], emb["fbank_frames"]
        scale = frames / pool_frames
        source = np.minimum(np.floor(np.arange(pool_frames) * scale).astype(int), frames - 1)
        embeddings = {}

        per_call = 16 if cuda else 1               # windows per run
        batches = [groups[i:i + per_call] for i in range(0, len(groups), per_call)]
        embedded = [0]

        def embed(batch_groups):
            ids = [g[0][0] for g in batch_groups]
            wave = np.zeros((len(ids), window), dtype=np.float32)
            for i, c in enumerate(ids):
                wave[i] = chunk_audio(c, 1)[0]
            feats = fbank(wave * 32768.0, fbank_frames)
            rows, weights = [], []
            for i, g in enumerate(batch_groups):
                for _, _, w in g:
                    rows.append(feats[i])
                    weights.append(w[source])
            names = [inp.name for inp in self.embedding.get_inputs()]
            out = run(self.embedding, {names[0]: np.stack(rows), names[1]: np.stack(weights).astype(np.float32)})[0]
            k = 0
            for g in batch_groups:
                for c, sp, _ in g:
                    embeddings[(c, sp)] = out[k]
                    k += 1
            embedded[0] += 1
            report(0.4 + 0.45 * embedded[0] / len(batches), "Telling the voices apart")

        list(pool.map(embed, batches))
        pool.shutdown()

        # ---- Clustering.
        report(0.88, "Grouping the voices")
        hard = np.full((chunks, speakers), -2, dtype=int)
        train = [(c, sp) for (c, sp) in embeddings
                 if float(np.where(others[c] == 1, binary[c, :, sp], 0).sum()) >= clu["min_active_ratio"] * frames]
        train.sort()
        if len(train) >= 2:
            vectors = np.array([embeddings[k] for k in train], dtype=np.float64)
            normed = vectors / np.linalg.norm(vectors, axis=1, keepdims=True)
            # linkage keeps every pair's distance, so a long recording is grouped from an evenly spaced part of its
            # embeddings (windows overlap by 90%) and the rest join the nearest cluster, as PyannoteEngine.cs does.
            stride = -(-len(normed) // MAX_AGGLOMERATIVE)
            part = normed[::stride]
            sub = fcluster(linkage(part, method="centroid", metric="euclidean"), clu["threshold"], criterion="distance") - 1
            if stride > 1:
                means = np.array([part[sub == k].mean(axis=0) for k in range(int(sub.max()) + 1)])
                ahc = np.array([int(((means - v) ** 2).sum(axis=1).argmin()) for v in normed])
            else:
                ahc = sub
            seen = {}
            ahc = np.array([seen.setdefault(int(v), len(seen)) for v in ahc])  # numbered by first appearance
            count = int(ahc.max()) + 1
            fea = self.plda(vectors)
            start = np.full((len(ahc), count), 1.0)
            start[np.arange(len(ahc)), ahc] = np.exp(clu["init_smoothing"])
            start /= start.sum(axis=1, keepdims=True)
            gamma, pi = vbx(fea, self.plda.phi, clu["fa"], clu["fb"], start, clu["max_iters"], clu["epsilon"])
            kept = np.nonzero(pi > 1e-7)[0]
            centroids = (gamma[:, kept].T @ vectors) / gamma[:, kept].sum(axis=0)[:, None]
            constrained = True
            wanted = min(max(len(centroids), low), high)
            if wanted != len(centroids):
                labels = kmeans(normed, wanted)
                centroids = np.array([vectors[labels == k].mean(axis=0) for k in range(int(labels.max()) + 1)])
                constrained = False
            # 2 - cosine distance, per embedding and centroid
            ev = {k: v for k, v in embeddings.items()}
            soft = {}
            for key, e in ev.items():
                soft[key] = 1 + (centroids @ e) / (np.linalg.norm(centroids, axis=1) * np.linalg.norm(e))
            floor = min(v.min() for v in soft.values())
            for c in range(chunks):
                matrix = np.array([soft.get((c, sp), np.full(len(centroids), floor - 1)) for sp in range(speakers)])
                if constrained:
                    rows, cols = linear_sum_assignment(matrix, maximize=True)
                    choice = np.full(speakers, -2)
                    choice[rows] = cols
                else:
                    choice = matrix.argmax(axis=1)
                hard[c] = choice
        else:
            hard[:] = 0
        for c in range(chunks):
            for sp in range(speakers):
                if (c, sp) not in embeddings:
                    hard[c, sp] = -2

        # ---- Back to speaker turns.
        clusters = int(hard.max()) + 1
        if clusters <= 0:
            return {"segments": []}
        clustered = np.full((chunks, frames, clusters), np.nan)
        for k in range(clusters):
            member = hard == k                                     # [chunks, speakers]
            for sp in range(speakers):
                rows = np.nonzero(member[:, sp])[0]
                if len(rows):
                    current = clustered[rows, :, k]
                    clustered[rows, :, k] = np.where(np.isnan(current), binary[rows, :, sp], np.maximum(current, binary[rows, :, sp]))
        used = counts if overlap else np.minimum(counts, 1)
        activations = aggregate(clustered, frames, chunk_duration, chunk_step, frame_duration, frame_step, True)
        discrete = np.zeros_like(activations)
        order = np.argsort(-activations, axis=1, kind="stable")
        for t in range(len(activations)):
            n = used[t] if t < len(used) else 0
            if n:
                discrete[t, order[t, :min(n, clusters)]] = 1
        turns = turns_of(discrete, frame_duration, frame_step)
        names = {}
        for _, _, sp in turns:
            names.setdefault(sp, f"S{len(names) + 1}")
        log(f"diarize: {chunks} windows, {len(embeddings)} embeddings, {clusters} clusters, {len(turns)} turns")
        report(1.0)
        return {"segments": [{"startMs": int(round(a * 1000)), "endMs": int(round(b * 1000)), "speaker": names[sp]} for a, b, sp in turns]}


if __name__ == "__main__":
    run_worker(PyannoteWorker(), name="pyannote", model_version="community-1")
