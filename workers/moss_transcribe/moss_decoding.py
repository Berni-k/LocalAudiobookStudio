"""Greedy decoding for MOSS-Transcribe-Diarize's language model (Qwen3-0.6B), made fast for long recordings.

Measured on an RTX 3090, with transformers' own generate() writing 26 tokens a second at best:

- Attention for one new token goes through plain matrix products (``attention``). PyTorch's attention kernels split a
  single query's work only by head, so over a long cache most of the GPU waits: over 64,000 cached tokens one layer
  took 4.8 ms; the matrix products take 0.74 ms. Without it a 30-minute recording wrote 10 tokens a second.
- On the GPU each step is replayed as a CUDA graph (``Decoder``): the model is small enough that launching its kernels
  one by one from Python is most of the time (176 tokens a second instead of 26 with a short context, 72 instead of 26
  at 30,000 tokens). The cache is then static (allocated for the whole answer up front); the graph attends over the
  part written so far, rounded up to ``BUCKET`` tokens, and is captured again as the answer grows past each bucket.

The prompt itself (the whole recording) goes in in slices (``Decoder.prefill``), so its attention never holds the
whole recording at once either.
"""

import torch
from transformers import AttentionInterface, DynamicCache, StaticCache
from transformers.integrations.sdpa_attention import sdpa_attention_forward
from transformers.masking_utils import AttentionMaskInterface, sdpa_mask

ATTENTION = "las_decode"  # the attn_implementation to load the model with
BUCKET = 4096             # a graph attends over the cache in steps of this many tokens
_STATE = {"length": None, "mask": None}  # read while a graph is captured: how much of the static cache it attends over


def attention(module, query, key, value, attention_mask, dropout=0.0, scaling=None, is_causal=None, **kwargs):
    """sdpa for several queries (the prompt, the Whisper encoder); matrix products for one new token, which sees the
    whole cache (with a static cache: the part written so far, ``_STATE``)."""
    if query.shape[2] != 1:
        return sdpa_attention_forward(module, query, key, value, attention_mask, dropout=dropout, scaling=scaling,
                                      is_causal=is_causal, **kwargs)
    if _STATE["length"] is not None:
        key, value = key[:, :, :_STATE["length"]], value[:, :, :_STATE["length"]]
    b, h, _, d = query.shape
    groups = key.shape[1]  # query heads share key/value heads (GQA): each group's queries go against its one head
    scores = torch.matmul(query.reshape(b, groups, h // groups, d), key.transpose(-1, -2)).float()
    scores = scores * (scaling if scaling is not None else d ** -0.5)
    if _STATE["mask"] is not None:
        scores = scores + _STATE["mask"][: key.shape[2]]  # slots not written yet
    out = torch.matmul(torch.softmax(scores, -1).to(value.dtype), value).reshape(b, h, 1, d)
    return out.transpose(1, 2).contiguous(), None


AttentionInterface.register(ATTENTION, attention)
# The masks are sdpa's. Without this, transformers makes no mask for an attention it doesn't know, and sdpa's own
# causal mask is only right for a slice of the prompt with nothing before it: later slices came out garbled.
AttentionMaskInterface.register(ATTENTION, sdpa_mask)


class Decoder:
    """The language model's cache and one greedy step at a time: ``prefill`` the prompt but its last token, then
    ``step(token, position)`` returns the next token. CUDA graphs on the GPU, plain calls on the CPU."""

    def __init__(self, model, capacity: int, device: str):
        self.lm = model.model.language_model
        self.head = model.lm_head
        self.graphs = device == "cuda"
        self.capacity = capacity
        self.graph = None
        self.graph_length = 0
        if self.graphs:
            self.cache = StaticCache(config=model.config.text_config, max_cache_len=capacity)
            self.mask = torch.full((capacity,), float("-inf"), device=device)
            self.token = torch.zeros((1, 1), dtype=torch.long, device=device)
            self.position = torch.zeros((1,), dtype=torch.long, device=device)
        else:
            self.cache = DynamicCache()

    def prefill(self, embeds, slice_len: int, report=None):
        """Feeds the prompt's embeddings in slices; ``report(done, total)`` after each."""
        total = embeds.shape[1]
        for start in range(0, total, slice_len):
            end = min(total, start + slice_len)
            positions = torch.arange(start, end, device=embeds.device)
            self.lm(inputs_embeds=embeds[:, start:end], past_key_values=self.cache, use_cache=True,
                    position_ids=positions.unsqueeze(0))
            if report is not None:
                report(end, total)
        if self.graphs:
            self.mask[:total] = 0

    def _run(self, token, position):
        out = self.lm(input_ids=token, past_key_values=self.cache, use_cache=True, position_ids=position.view(1, 1),
                      cache_position=position)
        return self.head(out.last_hidden_state[:, -1:]).argmax(-1)

    def step(self, token: int, position: int) -> int:
        if position >= self.capacity:
            raise IndexError("the answer is longer than the cache")
        if not self.graphs:
            device = self.head.weight.device
            return int(self._run(torch.tensor([[token]], device=device), torch.tensor([position], device=device)))
        self.token.fill_(token)
        self.position.fill_(position)
        self.mask[position] = 0
        length = min(self.capacity, -(-(position + 1) // BUCKET) * BUCKET)
        if length != self.graph_length:
            self._capture(length)
        self.graph.replay()
        return int(self.out)

    def _capture(self, length: int):
        """A graph of one step attending over the first ``length`` slots. Warming up runs the step for real, writing
        the current token into the cache where the replay writes the same again.

        The static cache writes where its own counter says (``cumulative_length``, a tensor on the GPU that each step
        moves on by one, inside the graph too), not where ``cache_position`` says. The warm-up steps move it on, so it
        is put back to the current position before the capture; from then on each replay moves it with the steps."""
        self.graph = None
        _STATE["length"], _STATE["mask"] = length, self.mask
        try:
            stream = torch.cuda.Stream()
            stream.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(stream):
                for _ in range(2):
                    self._run(self.token, self.position)
                for layer in self.cache.layers:
                    layer.cumulative_length.copy_(self.position[0])
            torch.cuda.current_stream().wait_stream(stream)
            graph = torch.cuda.CUDAGraph()
            with torch.cuda.graph(graph):
                self.out = self._run(self.token, self.position)
            self.graph, self.graph_length = graph, length
        finally:
            _STATE["length"], _STATE["mask"] = None, None

    def close(self):
        self.graph = None
        self.cache = None
