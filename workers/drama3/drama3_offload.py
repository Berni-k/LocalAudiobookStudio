"""The lean way to run Drama 3 on one GPU (drama3_plan: Plan.lean), applied after drama3_patches.apply.

- The embedding and output layer stay in RAM; decoding only ever produces the end token or one of the 4096 audio
  codes, so their rows of both tables go on the GPU (16 MB instead of 1.9 GB). Prefill looks text tokens up in RAM.
- One CUDA-graph KV bucket and KV caches for 4096 rows (instead of four buckets and 10240 rows).
- Experts of the last N layers can stay in pinned RAM. Decoding one of those layers is a graph up to its router, one
  sync for the host to read the 8 experts it chose, copies of the ones missing from the layer's GPU cache slots, and
  one graph shared by all such layers that runs the chosen experts from the cache. Prefill copies a layer's experts
  in chunks. Measured on PCIe 4.0 x16: about 20 GiB/s per expert-sized copy; routing isn't sticky (a frame shares
  about 2 of its 8 experts with the one before), so cache slots help in proportion to their number.
- Attention and tower weights can be 8-bit (one scale per output row), loaded into RAM, quantized there and moved
  to the GPU, all dequantizing into one shared scratch buffer (otherwise every CUDA graph keeps its own copy).
  Small matrices (routers, gates) stay bf16: quantizing those derails the audio.
"""

import time
from collections import OrderedDict

import torch
import torch.nn.functional as F

import drama3_patches as patches

GPU = torch.device("cuda:0")
CPU = torch.device("cpu")
CAPACITY = 4096
PREFILL_EXPERT_CHUNK = 8
# 8-bit only for matrices of at least this many weights.
MIN_INT8 = 4 * 1024 * 1024
PARTS = {"int4": ("w13_q", "w13_scale", "w13_offset", "w2_q", "w2_scale", "w2_offset"),
         "fp8": ("w13_weight", "w13_scale_inv", "w2_weight", "w2_scale_inv")}


def prepare(plan, log):
    """Before the model loads: where its parts go, the prefill and graph settings."""
    import fish_speech.models.text2semantic.fish_omni.generate as omni_generate
    import fish_speech.models.text2semantic.fish_omni.moe as moe
    from fish_speech.models.text2semantic import inference
    from fish_speech.models.text2semantic.fish_omni.backbone import FishOmniBackbone

    from drama3_plan import LAYERS

    in_ram = set(range(LAYERS - plan.layers_in_ram, LAYERS))
    dense = CPU if plan.int8 else GPU  # 8-bit: quantized in RAM, moved to the GPU afterwards

    def materialize(self, devices):
        self.model.norm.to_empty(device=GPU)
        for module in (self.model.embed_tokens, self.lm_head):
            module.to_empty(device=CPU)
        for i, layer in enumerate(self.model.layers):
            for name, child in layer.named_children():
                if name != "mlp":
                    child.to_empty(device=dense)
                    continue
                for sub_name, sub in child.named_children():
                    sub.to_empty(device=CPU if sub_name == "experts" and i in in_ram else dense)
        self.inverse_frequencies.clear()

    def embed(self, input_ids):
        """Prefill: the table is in RAM; look the tokens up there."""
        return self.model.embed_tokens(input_ids.to(CPU)).to(GPU, non_blocking=True)

    FishOmniBackbone.materialize = materialize
    FishOmniBackbone.embed = embed
    inference.resolve_drama3_devices = lambda device: (["cuda:0"], "cpu" if plan.int8 else "cuda:0")
    if plan.int8:
        inference.resolve_drama3_dtype = lambda precision, devices: torch.bfloat16  # it would ask the CPU for its SM
    omni_generate.DRAMA3_GRAPH_CAPACITY = CAPACITY
    patches.PREFILL_EXPERT_CHUNK = PREFILL_EXPERT_CHUNK

    resident_weights = moe.MoEExperts.expert_weights

    def expert_weights(self, index, dtype):
        """Prefill on a layer whose experts are in RAM: the chosen experts are copied in."""
        offload = getattr(self, "offload", None)
        if offload is None:
            return resident_weights(self, index, dtype)
        if not torch.is_tensor(index):  # one expert (prefill's busiest ones)
            w13, w2 = Offload.unpack(offload.blob[index][None].to(GPU, non_blocking=True), dtype)
            return w13[0], w2[0]
        ids = index.tolist()
        # A run of experts (the padded prefill's chunks): whole blocks of pinned memory, one fast copy.
        run = ids == list(range(ids[0], ids[0] + len(ids)))
        rows = offload.blob[ids[0]:ids[0] + len(ids)] if run else offload.blob[ids]
        return Offload.unpack(rows.to(GPU, non_blocking=True), dtype)

    moe.MoEExperts.expert_weights = expert_weights
    omni_generate.Drama3DecodeGraphs = _lean_graphs(omni_generate)
    log(f"Lean on one GPU: experts of {plan.layers_in_ram} layers in RAM"
        + (", 8-bit attention and tower" if plan.int8 else ""))


def finish(model, plan, log):
    """After the model loaded: experts in RAM pinned with their GPU cache, 8-bit weights, everything else on the GPU."""
    started = time.perf_counter()
    layers = model.backbone.model.layers
    # The plan's last N layers (with 8-bit weights every layer loaded into RAM first; the rest move to the GPU below).
    offloaded = [layer.mlp.experts for layer in layers[len(layers) - plan.layers_in_ram:]] if plan.layers_in_ram else []
    Shared.parts = PARTS[plan.precision]
    for n, experts in enumerate(offloaded):
        experts.offload = Offload(experts, plan.slots, n * plan.slots)
    if offloaded:
        Shared.cache = torch.empty((len(offloaded) * plan.slots, offloaded[0].offload.blob.shape[1]),
                                   dtype=torch.uint8, device=GPU)
        Shared.slot_idx = torch.zeros(8, dtype=torch.long, device=GPU)
        Shared.slot_host = torch.zeros(8, dtype=torch.long).pin_memory()
    if plan.int8:
        saved = quantize_linears(layers) + quantize_linears(model.audio_decoder)
        for layer in layers:
            for name, child in layer.named_children():
                if name != "mlp":
                    child.to(GPU)
                    continue
                for sub_name, sub in child.named_children():
                    if sub_name != "experts" or not hasattr(sub, "offload"):
                        sub.to(GPU)
        model.audio_decoder.to(GPU)
        largest = max(m.q.numel() for m in model.modules() if isinstance(m, Int8Linear))
        Int8Linear.scratch = torch.empty(largest, dtype=model.dtype, device=GPU)
        log(f"8-bit attention and tower: {saved / 2**30:.1f} GB less")
    torch.cuda.empty_cache()
    log(f"Experts of {len(offloaded)} layers pinned in RAM, {plan.slots} GPU cache slots each "
        f"({time.perf_counter() - started:.0f} s)")
    return offloaded


# ------------------------------------------------------------------------------------------------ experts in RAM

class Shared:
    """What every layer with its experts in RAM shares: one GPU cache tensor (each layer owns its slots), the byte
    layout of an expert, and the slot indices the one expert graph reads, written by the host for the layer at hand."""
    parts = PARTS["int4"]
    cache = None
    layout = None
    slot_idx = None
    slot_host = None


class Offload:
    """A layer's experts pinned in RAM, one contiguous block per expert (so a miss is one copy), with its slots of the
    shared GPU cache kept least recently used first."""

    def __init__(self, experts, slots, base):
        self.experts, self.slots, self.base = experts, slots, base
        tensors = [getattr(experts, n) for n in Shared.parts]
        count = tensors[0].shape[0]
        layout, offset = [], 0  # (name, byte offset, byte length, dtype, shape per expert)
        for name, t in zip(Shared.parts, tensors):
            size = t[0].numel() * t.element_size()
            layout.append((name, offset, size, t.dtype, tuple(t.shape[1:])))
            offset += size
        self.blob = torch.cat([t.reshape(count, -1).view(torch.uint8) for t in tensors], dim=1).pin_memory()
        # The module's buffers become views into the block (still pinned, for prefill).
        for name, start, size, dtype, shape in layout:
            experts._buffers[name] = self.blob[:, start:start + size].view(dtype).view(count, *shape)
        Shared.layout = layout
        self.lru = OrderedDict()  # expert -> slot (in the shared cache)
        self.misses = self.lookups = 0

    def fetch(self, chosen):
        """Makes the 8 chosen experts' cache slots current and tells the expert graph which they are."""
        slots = []
        for e in chosen:
            self.lookups += 1
            if e in self.lru:
                self.lru.move_to_end(e)
                slots.append(self.lru[e])
                continue
            self.misses += 1
            if len(self.lru) < self.slots:
                slot = self.base + len(self.lru)
            else:
                slot = self.lru.pop(next(old for old in self.lru if old not in chosen))
            Shared.cache[slot].copy_(self.blob[e], non_blocking=True)
            self.lru[e] = slot
            slots.append(slot)
        Shared.slot_host.copy_(torch.tensor(slots))
        Shared.slot_idx.copy_(Shared.slot_host, non_blocking=True)

    @staticmethod
    def unpack(rows, dtype):
        """Dequantized (w13, w2) of expert blocks [n, bytes] on the GPU."""
        import fish_speech.models.text2semantic.fish_omni.moe as moe

        n = rows.shape[0]
        p = {name: rows[:, start:start + size].view(dt).view(n, *shape) for name, start, size, dt, shape in Shared.layout}
        if "w13_q" in p:
            return (patches.dequantize_int4(p["w13_q"], p["w13_scale"], p["w13_offset"], dtype),
                    patches.dequantize_int4(p["w2_q"], p["w2_scale"], p["w2_offset"], dtype))
        return (moe.dequantize_fp8_blocks(p["w13_weight"], p["w13_scale_inv"], dtype),
                moe.dequantize_fp8_blocks(p["w2_weight"], p["w2_scale_inv"], dtype))


# ------------------------------------------------------------------------------------------------ 8-bit weights

class Int8Linear(torch.nn.Module):
    """A Linear with an int8 weight and one scale per output row, dequantized where it is used (into the shared
    scratch buffer: one stream, so each matmul has read it before the next layer writes). `.weight` still works."""

    scratch = None

    def __init__(self, linear):
        super().__init__()
        w = linear.weight.data
        scale = (w.float().abs().amax(dim=1, keepdim=True) / 127).clamp(min=1e-12)
        self.register_buffer("q", (w.float() / scale).round_().clamp_(-127, 127).to(torch.int8))
        self.register_buffer("scale", scale.to(w.dtype))
        self.bias = linear.bias
        self.in_features, self.out_features = linear.in_features, linear.out_features
        self.dtype = w.dtype

    @property
    def weight(self):
        if Int8Linear.scratch is None or Int8Linear.scratch.device != self.q.device:
            return self.q.to(self.dtype) * self.scale
        return torch.mul(self.q, self.scale, out=Int8Linear.scratch[: self.q.numel()].view(self.q.shape))

    def forward(self, x):
        return F.linear(x, self.weight, self.bias)


def quantize_linears(root):
    """Every nn.Linear under root of at least MIN_INT8 weights (not the experts, not a weight shared with another
    parameter) becomes an Int8Linear. Returns the bytes saved."""
    shared = {}
    for p in root.parameters():
        shared[p.data_ptr()] = shared.get(p.data_ptr(), 0) + 1
    saved = 0
    for name, module in list(root.named_modules()):
        for child_name, child in list(module.named_children()):
            full = f"{name}.{child_name}" if name else child_name
            if (not isinstance(child, torch.nn.Linear) or ".experts" in full or child.weight.numel() < MIN_INT8
                    or shared.get(child.weight.data_ptr(), 0) > 1):
                continue
            saved += child.weight.numel()  # 2 bytes to 1 (plus a scale per row)
            setattr(module, child_name, Int8Linear(child))
    return saved


# ------------------------------------------------------------------------------------------------ decode graphs

def _lean_graphs(omni_generate):
    from fish_speech.models.text2semantic.fish_omni.layers import LayerContext

    base = omni_generate.Drama3DecodeGraphs

    class Replay:
        """One decode step: graphs, and between a layer's two graphs the host's expert fetch."""

        def __init__(self, owner):
            self.owner, self.steps = owner, []  # ("graph", graph) or ("fetch", Offload)

        def replay(self):
            o = self.owner
            o.position_next.copy_(o.position + 1)
            for kind, item in self.steps:
                if kind == "graph":
                    item.replay()
                else:
                    item.fetch(o.ids.tolist()[0])  # the one sync per layer with its experts in RAM

    class LeanGraphs(base):
        def __init__(self, model):
            super().__init__(model)
            self.buckets = (max(self.buckets),)  # one KV bucket: attention reads padding, the graphs exist once
            backbone = model.backbone
            width, rdtype = backbone.config.hidden_size, backbone.residual_dtype
            self.h = torch.zeros((1, 1, width), dtype=rdtype, device=GPU)
            self.xa = torch.zeros_like(self.h)
            self.xn = torch.zeros((1, 1, width), dtype=backbone.dtype, device=GPU)
            self.shared_out = torch.zeros((1, width), dtype=backbone.dtype, device=GPU)
            self.ids = torch.zeros((1, 8), dtype=torch.long, device=GPU)
            self.weights = torch.zeros((1, 8), dtype=backbone.dtype, device=GPU)
            self.position_next = torch.zeros(1, dtype=torch.long, device=GPU)
            self.graph_pool = torch.cuda.graph_pool_handle()
            # The decoding rows of the output layer and embedding (end token + audio codes, sorted as decode_step's
            # candidates), and logits that are -inf everywhere else.
            tower = model.audio_decoder
            ids = torch.cat((torch.tensor([tower.eos_id]),
                             torch.arange(tower.semantic_begin, tower.semantic_end + 1))).sort().values
            self.cand_ids = ids.to(GPU)
            self.head_rows = backbone.lm_head.weight[ids].to(GPU)
            self.embed_rows = backbone.model.embed_tokens.weight[ids].to(GPU)
            self.full_logits = torch.full((1, backbone.config.vocab_size), float("-inf"), dtype=backbone.dtype, device=GPU)
            # Runs of layers: ("dense", [layers]) or ("routed", layer) for one with its experts in RAM.
            self.runs = []
            for layer in backbone.model.layers:
                if hasattr(layer.mlp.experts, "offload"):
                    self.runs.append(("routed", layer))
                elif self.runs and self.runs[-1][0] == "dense":
                    self.runs[-1][1].append(layer)
                else:
                    self.runs.append(("dense", [layer]))

        @classmethod
        def for_model(cls, model, capacity):
            if capacity > cls.graph_capacity(model):
                return None
            if model.decode_graphs is None:
                before = torch.cuda.memory_reserved()
                omni_generate.ensure_drama3_caches(model, cls.graph_capacity(model))
                graphs = cls(model)
                started = time.perf_counter()
                for kv_len in graphs.buckets:
                    graphs.capture(kv_len)
                peak = torch.cuda.max_memory_reserved()
                torch.cuda.empty_cache()  # the warm-ups' blocks
                omni_generate.logger.info(
                    f"Drama 3 lean decode graphs captured in {time.perf_counter() - started:.1f}s; reserved "
                    f"{before / 2**30:.1f} -> {torch.cuda.memory_reserved() / 2**30:.1f} GB (peak {peak / 2**30:.1f})")
                model.decode_graphs = graphs
            return model.decode_graphs

        def context(self, kv_len):
            config = self.model.backbone.config
            exponents = torch.arange(0, config.rotary_dim, 2, dtype=torch.float32, device=GPU)
            frequencies = 1.0 / (config.rope_theta ** (exponents / config.rotary_dim))
            angles = self.position_next.float()[:, None] * frequencies[None, :]
            angles = torch.cat((angles, angles), dim=-1)
            return LayerContext(input_pos=self.position_next, cos=angles.cos(), sin=angles.sin(), kv_len=kv_len,
                                continue_state=True, use_cache=True)

        def tower_front(self):
            tower = self.model.audio_decoder
            candidate_logits = self.hidden @ self.head_rows.T
            self.full_logits[:, self.cand_ids] = candidate_logits
            self.finite.copy_(torch.isfinite(candidate_logits).all())
            sampled = tower.decode_step(self.full_logits, self.hidden, self.position, self.frame, self.history,
                                        temperature=self.temperature, top_k=self.top_k, top_p=self.top_p,
                                        check_finite=False)
            self.sampled.copy_(sampled)
            row = torch.searchsorted(self.cand_ids, sampled[:, 0])  # the main token is always a candidate
            x = tower.embed_backbone(sampled, self.embed_rows[row])
            self.h.copy_(x[None].to(self.h.dtype))

        def dense(self, layers, kv_len):
            context = self.context(kv_len)
            hidden = self.h
            for layer in layers:
                hidden = layer(hidden, context)
            self.h.copy_(hidden)

        def routed_front(self, layer, kv_len):
            """Attention, the router and the shared expert of a layer with its experts in RAM."""
            context = self.context(kv_len)
            compute = layer.input_layernorm.weight.dtype
            mixer = layer.self_attn if layer.layer_type == "full_attention" else layer.linear_attn
            x = self.h + mixer(layer.input_layernorm(self.h).to(compute), context)
            xn = layer.post_attention_layernorm(x).to(compute)
            block = layer.mlp
            flat = xn.reshape(-1, xn.shape[-1])
            probabilities = torch.softmax(block.gate(flat).float(), dim=-1)
            weights, ids = probabilities.topk(block.top_k, dim=-1)
            if block.norm_topk_prob:
                weights = weights / weights.sum(dim=-1, keepdim=True)
            self.shared_out.copy_(torch.sigmoid(block.shared_expert_gate(flat)) * block.shared_expert(flat))
            self.xa.copy_(x)
            self.xn.copy_(xn)
            self.ids.copy_(ids)
            self.weights.copy_(weights.to(self.weights.dtype))

        def routed_back(self):
            """The 8 chosen experts from the shared cache, the shared expert and the residual: the same for every
            layer with its experts in RAM, so one graph serves them all."""
            flat = self.xn.reshape(-1, self.xn.shape[-1])
            w13, w2 = Offload.unpack(Shared.cache[Shared.slot_idx], flat.dtype)
            rows = flat.repeat_interleave(8, dim=0).unsqueeze(-1)
            gate, up = torch.bmm(w13, rows).squeeze(-1).chunk(2, dim=-1)
            out = torch.bmm(w2, (F.silu(gate) * up).unsqueeze(-1)).squeeze(-1)
            out = (out.float() * self.weights.reshape(-1, 1).float()).sum(dim=0, keepdim=True).to(flat.dtype)
            self.h.copy_(self.xa + (out + self.shared_out).view_as(self.xa))

        def tower_back(self):
            backbone = self.model.backbone
            self.next_hidden.copy_(backbone.model.norm(self.h).to(backbone.dtype)[0])

        def record(self, fn):
            # One capture stream for all graphs: PyTorch caches memory per stream, so a new stream per graph would
            # keep each warm-up's blocks apart (gigabytes over all the graphs).
            stream = self.__dict__.setdefault("capture_stream", torch.cuda.Stream())
            stream.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(stream):
                for _ in range(2):
                    fn()
            torch.cuda.current_stream().wait_stream(stream)
            torch.cuda.synchronize()
            graph = torch.cuda.CUDAGraph()
            with torch.cuda.graph(graph, pool=self.graph_pool, stream=stream):
                fn()
            return graph

        def capture(self, kv_len):
            """Graphs without full attention don't read the KV length: captured once, shared by the buckets."""
            shared = self.__dict__.setdefault("shared", {})

            def once(key, fn):
                if key not in shared:
                    shared[key] = self.record(fn)
                return shared[key]

            def full(layers):
                return any(layer.layer_type == "full_attention" for layer in layers)

            for attention in self.tower_attentions():
                attention.static_kv_len = kv_len
            try:
                replay = Replay(self)
                replay.steps.append(("graph", self.record(self.tower_front)))
                for index, (kind, item) in enumerate(self.runs):
                    if kind == "dense":
                        fn = lambda l=item: self.dense(l, kv_len)
                        replay.steps.append(("graph", self.record(fn) if full(item) else once(("dense", index), fn)))
                    else:
                        fn = lambda l=item: self.routed_front(l, kv_len)
                        replay.steps.append(("graph", self.record(fn) if full([item]) else once(("front", index), fn)))
                        replay.steps.append(("fetch", item.mlp.experts.offload))
                        replay.steps.append(("graph", once("back", self.routed_back)))
                replay.steps.append(("graph", once("tower_back", self.tower_back)))
                self.graphs[kv_len] = replay
            finally:
                for attention in self.tower_attentions():
                    attention.static_kv_len = None

    return LeanGraphs
