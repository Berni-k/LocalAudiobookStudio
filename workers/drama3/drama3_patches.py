"""Changes to fish-speech's Drama 3 code that make it fit and run on consumer GPUs (applied by worker.py).

fish-speech v3 targets one large SM89+ GPU with Triton (its FP8 expert kernel). On anything else it dequantizes
every expert to bf16 at load (about 70 GB), decodes eagerly when the model spans several GPUs (thousands of small
kernel launches per audio frame: about 2 frames/s on Windows), and prefills 128 rows at a time. Here:

- Experts stay FP8 in memory (about 32 GB) and only the ones a step routes to are dequantized, straight to bf16.
- Prefill runs the whole prompt at once, and its experts in a few padded batched matmuls instead of a Python loop
  over the 256 experts.
- The layers are split over the visible GPUs by their free memory; the last GPU also holds the embedding, final norm,
  output head and the audio decoder ("tower").
- Decoding runs as CUDA graphs, one per GPU, with the few hand-offs between GPUs as ordinary copies: a single graph
  can't span GPUs without peer access, which GeForce cards on Windows don't have.

On one GPU with Triton and SM89+ (Linux), fish-speech's own FP8 kernel is kept.
"""

import re
import time

import torch
import torch.nn.functional as F

GIB = 1024 ** 3

# Working memory left free on every GPU (prefill, CUDA graphs, caches), and on the last one also for the tower and
# what runs there (sampling, prefill of the tower); the codec's share is counted apart. Measured on 2 × RTX 3090,
# where 25 + 15 layers fit and 23 + 17 ran out of memory on the second GPU in prefill.
LAYER_GPU_RESERVE = 1.5 * GIB
TOWER_GPU_RESERVE = 5.0 * GIB
CODEC_BYTES = 1.5 * GIB

# Experts dequantized at once during prefill (each chunk is a bmm over these experts).
PREFILL_EXPERT_CHUNK = 32
# Prompt rows per prefill pass: every pass dequantizes all experts once, so the whole prompt goes in one.
PREFILL_ROWS = 4096

# Set by the worker: bytes each GPU may use (its free memory, or less with the GPU memory target), and whether a split
# that doesn't fit is an error (else it is placed anyway: the memory plan has already warned).
BUDGETS = None
STRICT = True


class PlacementError(RuntimeError):
    """The model doesn't fit on the visible GPUs."""


def _free_bytes(device):
    """Free memory on a GPU; with DRAMA3_SIMULATE_GPU_GB (testing), as if the card had that much (less the CUDA
    context's half GB), and PyTorch's allocator is held to it. Memory a separate process merely reserves doesn't do
    for this on Windows: the driver pages it out when another process needs the room."""
    import os

    free, total = torch.cuda.mem_get_info(device)
    simulated = os.environ.get("DRAMA3_SIMULATE_GPU_GB")
    if not simulated:
        return free
    limit = float(simulated) * GIB - CONTEXT_BYTES
    torch.cuda.set_per_process_memory_fraction(min(1.0, limit / total), device)
    return max(0, min(free, limit - torch.cuda.memory_reserved(device)))


# What a GPU keeps for itself and the CUDA context, which PyTorch can't use (measured: 1.21 GB of an idle 24 GB
# RTX 3090 on Windows).
CONTEXT_BYTES = 1.2 * GIB


def gpu_bytes(device):
    """What Drama 3 may plan with on a GPU: all of it but the CUDA context, as if no other process used it (other
    workers' memory is the app's business). With DRAMA3_SIMULATE_GPU_GB, as if the card had that much (and PyTorch is
    held to it)."""
    import os

    simulated = os.environ.get("DRAMA3_SIMULATE_GPU_GB")
    if simulated:
        _free_bytes(device)  # holds PyTorch to it
        return float(simulated) * GIB - CONTEXT_BYTES
    return torch.cuda.mem_get_info(device)[1] - CONTEXT_BYTES


def apply(codec_on_gpu: bool, int4: bool, log) -> bool:
    """Patches fish-speech in place. With int4 the model is Fish Audio's int4 checkpoint, whose experts stay 4-bit (half
    the memory). Returns whether fish-speech's own FP8 kernel is used (one SM89+ GPU with Triton, FP8 experts)."""
    import fish_speech.models.text2semantic.fish_omni.config as omni_config
    import fish_speech.models.text2semantic.fish_omni.generate as omni_generate
    import fish_speech.models.text2semantic.fish_omni.model as omni_model
    import fish_speech.models.text2semantic.fish_omni.moe as moe
    from fish_speech.models.text2semantic import inference
    from fish_speech.models.text2semantic.fish_omni.backbone import FishOmniBackbone, module_bytes

    devices = [f"cuda:{i}" for i in range(torch.cuda.device_count())]
    native = not int4 and len(devices) == 1 and omni_model.fp8_gemm_supported(devices)
    if not native:
        # Load the FP8 buffers as they are, then run the dequantize-on-use paths (fish-speech's CPU fallback).
        omni_model.fp8_gemm_supported = lambda _devices: True
        fp8_to_float = moe.dequantize_fp8_blocks
        moe.dequantize_fp8_blocks = _dequantize_bf16(fp8_to_float, moe.FP8_BLOCK_SIZE)
        moe.MoEExperts.forward = _expert_forward(moe.GATHERED_EXPERT_MAX_ROWS)
        if int4:
            _use_int4(moe, omni_model, omni_config)
    omni_generate.generate_drama3.__wrapped__.__kwdefaults__["prefill_chunk_size"] = PREFILL_ROWS

    # Every visible GPU holds backbone layers; the last one also the tower (fish-speech keeps one for the tower alone).
    inference.resolve_drama3_devices = lambda device: (devices, devices[-1])
    reserve = TOWER_GPU_RESERVE + (CODEC_BYTES if codec_on_gpu else 0)
    FishOmniBackbone.materialize = _materialize(module_bytes, reserve, log)
    if len(devices) > 1:  # on one GPU fish-speech's own graphs work
        omni_generate.Drama3DecodeGraphs = _segment_graphs(omni_generate)
    return native


# ------------------------------------------------------------------------------------------------ experts

def _dequantize_bf16(original, block):
    """fish-speech dequantizes via float32 (three passes over 4-byte values); FP8 e4m3 is exact in bf16, so cast
    straight to it and scale there. About 40% faster decoding; the scale is rounded to bf16."""

    def dequantize(weight, scale_inv, dtype):
        if dtype != torch.bfloat16:
            return original(weight, scale_inv, dtype)
        *lead, rows, cols = weight.shape
        row_blocks, col_blocks = scale_inv.shape[-2:]
        blocks = weight.view(*lead, row_blocks, block, col_blocks, block).to(dtype)
        return (blocks * scale_inv.to(dtype)[..., :, None, :, None]).view(*lead, rows, cols)

    return dequantize


def _expert_forward(gathered_max_rows):
    def forward(self, x, topk_ids, topk_weights):
        if topk_ids.numel() <= gathered_max_rows:
            return self.forward_gathered(x, topk_ids, topk_weights)  # decoding: no host sync, graph-safe
        return _forward_padded(self, x, topk_ids, topk_weights)

    return forward


def _forward_padded(self, x, topk_ids, topk_weights):
    """Prefill without fish-speech's per-expert Python loop (launch-bound on Windows).

    Rows go into [experts, rows, H] and every expert runs in one bmm per chunk of experts. Routing is uneven, so
    padding every expert to the busiest one would multiply the work: experts with more than about twice the average
    rows run on their own instead (few of them). One host sync (the row counts).
    """
    tokens, top_k = topk_ids.shape
    experts = self.num_experts
    flat = topk_ids.reshape(-1)
    pairs = flat.numel()
    order = torch.argsort(flat, stable=True)
    sorted_experts = flat[order]
    counts = torch.bincount(flat, minlength=experts)
    starts = torch.cumsum(counts, 0) - counts
    rank = torch.arange(pairs, device=x.device) - starts[sorted_experts]
    token_rows = order // top_k
    count_list = counts.tolist()
    cap = max(16, 2 * -(-pairs // experts))
    busy = [e for e, c in enumerate(count_list) if c > cap]
    rows = max([c for c in count_list if c <= cap] or [1])
    # Pairs of busy experts go to a dummy slot (index `experts`) of the padded batch.
    busy_mask = torch.zeros(experts + 1, dtype=torch.bool, device=x.device)
    if busy:
        busy_mask[torch.tensor(busy, device=x.device)] = True
    is_busy = busy_mask[sorted_experts]
    slot = torch.where(is_busy, experts, sorted_experts)
    slot_rank = torch.where(is_busy, 0, rank)
    padded = x.new_zeros((experts + 1, rows, self.hidden))
    padded[slot, slot_rank] = x[token_rows]
    result = torch.zeros_like(padded)
    for first in range(0, experts, PREFILL_EXPERT_CHUNK):
        last = min(first + PREFILL_EXPERT_CHUNK, experts)
        w13, w2 = self.expert_weights(torch.arange(first, last, device=x.device), x.dtype)
        gate, up = torch.bmm(padded[first:last], w13.transpose(1, 2)).chunk(2, dim=-1)
        result[first:last] = torch.bmm(F.silu(gate) * up, w2.transpose(1, 2))
    pair_out = result[slot, slot_rank]
    bounds = [0]
    for c in count_list:
        bounds.append(bounds[-1] + c)
    for expert in busy:
        first, last = bounds[expert], bounds[expert + 1]
        w13, w2 = self.expert_weights(expert, x.dtype)
        gate, up = F.linear(x[token_rows[first:last]], w13).chunk(2, dim=-1)
        pair_out[first:last] = F.linear(F.silu(gate) * up, w2)
    weighted = pair_out.float() * topk_weights.reshape(-1)[order, None].float()
    out = torch.zeros(tokens, self.hidden, dtype=torch.float32, device=x.device)
    out.index_add_(0, token_rows, weighted)
    return out.to(x.dtype)


# ------------------------------------------------------------------------------------------------ INT4 experts

# Weights per group sharing one scale and offset: 64 divides both of the experts' input sizes (2048, 512).
INT4_GROUP = 64


# Fish Audio's int4 checkpoint (fishaudio/drama-3-int4, GPTQ symmetric): one scale per 128 inputs, value = (q - 8) * scale.
GPTQ_GROUP = 128
INT4_EXPERT_KEY = re.compile(
    r"model\.layers\.(?P<layer>\d+)\.mlp\.experts\.(?P<expert>\d+)\.(?P<proj>gate_proj|up_proj|down_proj)\.(?P<kind>qweight|scales)")
# Dense layers (attention) are int4 in the checkpoint too, with fields only GPTQ kernels need (zeros are all 8).
INT4_DENSE_KEY = re.compile(r"(?P<base>model\.layers\.\d+\.(?:self_attn|linear_attn)\.\w+)\.(?P<kind>qweight|scales|qzeros|g_idx)")


def gptq_to_packed(qweight):
    """GPTQ qweight int32 [cols / 8, rows] -> packed uint8 [rows, cols / 2], the even column in the low nibble."""
    return qweight.t().contiguous().view(torch.uint8)


def gptq_to_groups(scales):
    """GPTQ scales [cols / 128, rows] -> (scale, offset) fp16 [rows, cols / INT4_GROUP]: the same value as
    (q - 8) * scale, written as q * scale + offset for groups of 64."""
    scale = scales.t().repeat_interleave(GPTQ_GROUP // INT4_GROUP, dim=1).contiguous()
    return scale, -8 * scale


def dequantize_int4(packed, scale, offset, dtype):
    """Packed [..., rows, cols / 2] weights as dtype values (q * scale + offset per group of INT4_GROUP)."""
    *lead, rows, half = packed.shape
    q = torch.stack((packed & 15, packed >> 4), dim=-1).view(*lead, rows, half * 2 // INT4_GROUP, INT4_GROUP)
    return (q.to(dtype) * scale.to(dtype)[..., None] + offset.to(dtype)[..., None]).view(*lead, rows, half * 2)


def _dequantize_gptq_dense(qweight, scales, dtype):
    """A dense layer's int4 weight [rows, cols] as dtype, on the device of its tensors."""
    packed = gptq_to_packed(qweight)
    scale = scales.t().repeat_interleave(GPTQ_GROUP, dim=1)
    q = torch.stack((packed & 15, packed >> 4), dim=-1).view(packed.shape[0], -1)
    return ((q.to(torch.float32) - 8) * scale.to(torch.float32)).to(dtype)


def _use_int4(moe, omni_model, omni_config):
    """Experts hold 4-bit weights instead of FP8 ones, filled from Fish Audio's int4 checkpoint: the experts' packed
    values are used as they are, the attention layers' are expanded to bf16 (they are a small part)."""
    import fish_speech.models.text2semantic.fish_omni.backbone as backbone

    # The checkpoint's config names its own quantization, which fish-speech doesn't know: it loads as if FP8 (the
    # experts' buffers are replaced below).
    from_dict = omni_config.FishOmniConfig.from_dict.__func__

    def config_from_dict(cls, data):
        quantization = data.get("quantization_config")
        if quantization and quantization.get("quant_method") == "drama3-int4":
            data = {**data, "quantization_config": {"quant_method": omni_config.FP8_QUANT_METHOD,
                                                    "weight_block_size": [128, 128]}}
        return from_dict(cls, data)

    omni_config.FishOmniConfig.from_dict = classmethod(config_from_dict)

    experts_init = moe.MoEExperts.__init__
    parts = ("w13", "w2")

    def init(self, num_experts, hidden, intermediate, *, fp8, dtype=None, device=None):
        experts_init(self, num_experts, hidden, intermediate, fp8=fp8, dtype=dtype, device=device)
        for name in parts:
            rows, cols = (2 * intermediate, hidden) if name == "w13" else (hidden, intermediate)
            del self._buffers[f"{name}_weight"], self._buffers[f"{name}_scale_inv"]
            self.register_buffer(f"{name}_q", torch.empty((num_experts, rows, cols // 2), dtype=torch.uint8, device=device))
            for kind in ("scale", "offset"):
                self.register_buffer(f"{name}_{kind}",
                                     torch.empty((num_experts, rows, cols // INT4_GROUP), dtype=torch.float16, device=device))

    def expert_weights(self, index, dtype):
        return tuple(dequantize_int4(getattr(self, f"{n}_q")[index], getattr(self, f"{n}_scale")[index],
                                     getattr(self, f"{n}_offset")[index], dtype) for n in parts)

    @torch.no_grad()
    def load_weights(model, weights):
        """fish-speech's load_backbone_weights, for the int4 checkpoint: every tensor exactly once, checked."""
        targets = dict(model.named_parameters())
        targets.update(model.named_buffers())
        dense = {n for n in targets if ".mlp.experts." not in n}
        loaded, pending, pending_dense, expert_parts = set(), {}, {}, 0
        for checkpoint_name, tensor in weights:
            name = backbone.backbone_name(checkpoint_name)
            match = INT4_EXPERT_KEY.fullmatch(name)
            if match is None and (dense_match := INT4_DENSE_KEY.fullmatch(name)) is not None:
                # qzeros (all 8) and g_idx (in order) say nothing more for this symmetric, unordered format.
                if dense_match["kind"] in ("qweight", "scales"):
                    parts_of = pending_dense.setdefault(dense_match["base"] + ".weight", {})
                    parts_of[dense_match["kind"]] = tensor
                    if len(parts_of) == 2:
                        target = targets[dense_match["base"] + ".weight"]
                        device = target.device if target.is_cuda else torch.device("cuda", torch.cuda.current_device())
                        value = _dequantize_gptq_dense(parts_of["qweight"].to(device), parts_of["scales"].to(device), target.dtype)
                        if target.shape != value.shape:
                            raise ValueError(f"{dense_match['base']}: checkpoint shape {tuple(value.shape)} != {tuple(target.shape)}")
                        target.copy_(value)
                        loaded.add(dense_match["base"] + ".weight")
                        del pending_dense[dense_match["base"] + ".weight"]
                continue
            if match is None:
                if name not in dense or name in loaded:
                    raise ValueError(f"checkpoint key {checkpoint_name!r} has no free parameter {name!r}")
                if targets[name].shape != tensor.shape:
                    raise ValueError(f"{name}: checkpoint shape {tuple(tensor.shape)} != {tuple(targets[name].shape)}")
                targets[name].copy_(tensor)
                loaded.add(name)
                continue
            experts = model.get_submodule(f"model.layers.{match['layer']}.mlp.experts")
            expert = int(match["expert"])
            if match["proj"] == "down_proj":
                rows, name = slice(None), "w2"
            else:
                half = experts.w13_q.shape[1] // 2
                rows, name = (slice(0, half) if match["proj"] == "gate_proj" else slice(half, 2 * half)), "w13"
            if match["kind"] == "qweight":
                getattr(experts, f"{name}_q")[expert, rows].copy_(gptq_to_packed(tensor))
            else:
                scale, offset = gptq_to_groups(tensor)
                getattr(experts, f"{name}_scale")[expert, rows].copy_(scale)
                getattr(experts, f"{name}_offset")[expert, rows].copy_(offset)
            expert_parts += 1
        layers = model.model.layers
        wanted = sum(layer.mlp.experts.num_experts * 6 for layer in layers)  # gate, up and down: values and scales
        if pending_dense or expert_parts != wanted or dense - loaded:
            raise ValueError(f"Drama 3 checkpoint incomplete: {expert_parts} of {wanted} expert weights, "
                             f"{len(dense - loaded)} other tensors missing, {len(pending_dense)} without their scale")

    moe.MoEExperts.__init__ = init
    moe.MoEExperts.expert_weights = expert_weights
    omni_model.load_backbone_weights = load_weights


# ------------------------------------------------------------------------------------------------ placement

def _materialize(module_bytes, tower_reserve, log):
    def materialize(self, devices):
        """Contiguous layer ranges per GPU, in proportion to the memory each has free for them."""
        devices = [torch.device(d) for d in devices]
        layers = self.model.layers
        tower = devices[-1]
        head = module_bytes(self.model.embed_tokens) + module_bytes(self.model.norm) + module_bytes(self.lm_head)
        sizes = [module_bytes(layer) for layer in layers]
        free = list(BUDGETS) if BUDGETS else [_free_bytes(d) for d in devices]
        room = [max(0.0, f - LAYER_GPU_RESERVE - ((head + tower_reserve) if d == tower else 0))
                for d, f in zip(devices, free)]
        needed = sum(sizes)
        if sum(room) < needed:
            total = needed + head + tower_reserve + LAYER_GPU_RESERVE * len(devices)
            if STRICT:
                raise PlacementError(
                    f"Drama 3 needs about {total / GIB:.0f} GB of GPU memory and {len(devices)} GPU(s) have "
                    f"{sum(free) / GIB:.0f} GB free. Raise its GPU count in the worker settings, or put the codec on the CPU.")
            # Placed anyway, in proportion to what each GPU has (the memory plan has warned).
            room = [max(1.0, f) for f in free]

        # Proportional split by midpoints (as fish-speech balances by bytes), then fixed up where a range overflows.
        counts = [0] * len(devices)
        bounds = [needed * sum(room[:i + 1]) / sum(room) for i in range(len(devices))]
        cumulative = 0.0
        for size in sizes:
            middle = cumulative + size / 2
            counts[next((i for i, b in enumerate(bounds) if middle < b), len(devices) - 1)] += 1
            cumulative += size
        counts = _fit(counts, sizes, room)
        for module in (self.model.embed_tokens, self.model.norm, self.lm_head):
            module.to_empty(device=tower)
        first = 0
        for device, count in zip(devices, counts):
            for layer in layers[first:first + count]:
                layer.to_empty(device=device)
            first += count
        self.inverse_frequencies.clear()
        log("Layer split: " + ", ".join(
            f"{d}: {c} layers ({sum(sizes[sum(counts[:i]):sum(counts[:i + 1])]) / GIB:.1f} GB, {f / GIB:.1f} GB free)"
            for i, (d, c, f) in enumerate(zip(devices, counts, free))))

    return materialize


def _fit(counts, sizes, room):
    """Moves layers from GPUs whose range overflows to the next GPU with room (forward, then backward)."""
    counts = list(counts)

    def used(i):
        start = sum(counts[:i])
        return sum(sizes[start:start + counts[i]])

    for i in range(len(counts) - 1):
        while counts[i] > 0 and used(i) > room[i]:
            counts[i] -= 1
            counts[i + 1] += 1
    for i in range(len(counts) - 1, 0, -1):
        while counts[i] > 0 and used(i) > room[i]:
            counts[i] -= 1
            counts[i - 1] += 1
    if STRICT and any(used(i) > room[i] for i in range(len(counts))):
        raise PlacementError("Drama 3's layers don't fit on the GPUs. Raise its GPU count in the worker settings.")
    return counts


# ------------------------------------------------------------------------------------------------ decoding

def _segment_graphs(omni_generate):
    """fish-speech's Drama3DecodeGraphs captures one decode step as one CUDA graph, on one GPU only. This captures it
    as a graph per GPU: the tower's (output head, sample a frame, embed it), each GPU's layers in order, then the
    tower's again (final norm). The hand-offs between GPUs are eager copies."""
    from fish_speech.models.text2semantic.fish_omni.layers import LayerContext

    base = omni_generate.Drama3DecodeGraphs

    class SegmentReplay:
        """The graphs of one KV-length bucket and the copies between them."""

        def __init__(self, owner):
            self.owner = owner
            self.graphs = []

        def replay(self):
            o = self.owner
            for position in o.positions.values():
                position.copy_(o.position + 1)
            with torch.cuda.device(o.tower_device):
                self.graphs[0].replay()
            current = o.x_tower
            for (device, inbox, outbox), graph in zip(o.groups, self.graphs[1:-1]):
                inbox.copy_(current)
                with torch.cuda.device(device):
                    graph.replay()
                current = outbox
            o.h_tower.copy_(current)
            with torch.cuda.device(o.tower_device):
                self.graphs[-1].replay()

    class SegmentGraphs(base):
        def __init__(self, model):
            super().__init__(model)
            backbone = model.backbone
            self.tower_device = model.audio_decoder.device
            width = backbone.config.hidden_size
            rdtype = backbone.residual_dtype
            self.x_tower = torch.zeros((1, 1, width), dtype=rdtype, device=self.tower_device)
            self.h_tower = torch.zeros_like(self.x_tower)
            self.groups, self.group_layers, self.positions, self.pools = [], [], {}, {}
            for layer in backbone.model.layers:
                device = layer.input_layernorm.weight.device
                if not self.groups or self.groups[-1][0] != device:
                    inbox = torch.zeros((1, 1, width), dtype=rdtype, device=device)
                    self.groups.append((device, inbox, torch.zeros_like(inbox)))
                    self.group_layers.append([])
                    self.positions.setdefault(device, torch.zeros(1, dtype=torch.long, device=device))
                self.group_layers[-1].append(layer)

        @classmethod
        def for_model(cls, model, capacity):
            if capacity > cls.graph_capacity(model):
                return None
            if model.decode_graphs is None:
                omni_generate.ensure_drama3_caches(model, cls.graph_capacity(model))
                graphs = cls(model)
                started = time.perf_counter()
                for kv_len in graphs.buckets:
                    graphs.capture(kv_len)
                for device in graphs.pools:
                    with torch.cuda.device(device):
                        torch.cuda.empty_cache()  # the warm-ups' blocks
                omni_generate.logger.info(
                    f"Drama 3 decode graphs on {len(graphs.groups)} GPU group(s), buckets {graphs.buckets}, "
                    f"captured in {time.perf_counter() - started:.1f}s")
                model.decode_graphs = graphs
            return model.decode_graphs

        def tower_front(self):
            backbone, tower = self.model.backbone, self.model.audio_decoder
            logits = backbone.lm_head(self.hidden)
            self.finite.copy_(torch.isfinite(logits).all())
            sampled = tower.decode_step(logits, self.hidden, self.position, self.frame, self.history,
                                        temperature=self.temperature, top_k=self.top_k, top_p=self.top_p,
                                        check_finite=False)
            self.sampled.copy_(sampled)
            x = tower.embed_backbone(sampled, backbone.model.embed_tokens(sampled[:, 0]))
            self.x_tower.copy_(x[None].to(self.x_tower.dtype))

        def layer_group(self, index, kv_len):
            config = self.model.backbone.config
            device, inbox, outbox = self.groups[index]
            exponents = torch.arange(0, config.rotary_dim, 2, dtype=torch.float32, device=device)
            frequencies = 1.0 / (config.rope_theta ** (exponents / config.rotary_dim))
            position = self.positions[device]
            angles = position.float()[:, None] * frequencies[None, :]
            angles = torch.cat((angles, angles), dim=-1)
            context = LayerContext(input_pos=position, cos=angles.cos(), sin=angles.sin(), kv_len=kv_len,
                                   continue_state=True, use_cache=True)
            hidden = inbox
            for layer in self.group_layers[index]:
                hidden = layer(hidden, context)
            outbox.copy_(hidden)

        def tower_back(self):
            backbone = self.model.backbone
            self.next_hidden.copy_(backbone.model.norm(self.h_tower).to(backbone.dtype)[0])

        def record(self, device, fn):
            pool = self.pools.setdefault(device, torch.cuda.graph_pool_handle())
            with torch.cuda.device(device):
                # One capture stream per GPU: PyTorch caches memory per stream, so a new one per graph would keep
                # each warm-up's blocks apart.
                stream = self.__dict__.setdefault("streams", {}).setdefault(device, torch.cuda.Stream())
                stream.wait_stream(torch.cuda.current_stream())
                with torch.cuda.stream(stream):
                    for _ in range(2):
                        fn()
                torch.cuda.current_stream().wait_stream(stream)
                torch.cuda.synchronize()
                graph = torch.cuda.CUDAGraph()
                # torch.cuda.graph's default capture stream is made once, on the first GPU it ran on: use this one's.
                with torch.cuda.graph(graph, pool=pool, stream=stream):
                    fn()
            return graph

        def capture(self, kv_len):
            for attention in self.tower_attentions():
                attention.static_kv_len = kv_len
            try:
                replay = SegmentReplay(self)
                replay.graphs.append(self.record(self.tower_device, self.tower_front))
                for index, (device, _, _) in enumerate(self.groups):
                    replay.graphs.append(self.record(device, lambda i=index: self.layer_group(i, kv_len)))
                replay.graphs.append(self.record(self.tower_device, self.tower_back))
                self.graphs[kv_len] = replay
            finally:
                for attention in self.tower_attentions():
                    attention.static_kv_len = None

    return SegmentGraphs
