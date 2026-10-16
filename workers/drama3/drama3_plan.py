"""How Drama 3 fits in the GPU memory it has: the worker settings Expert precision, Expert layers in RAM, Codec and
8-bit attention and tower, each a choice or Auto, and the GPU memory target that the Auto ones are fitted to.

The Auto settings are switched on in this order until the estimate fits: 4-bit experts (precision Auto: FP8 only when
it fits without anything else), more layers' experts in RAM, the codec on the CPU, 8-bit attention and tower weights.
When even all of them don't fit, the plan says so and the worker runs anyway.

Two ways to run (drama3_patches / drama3_offload):
- resident: everything on the GPU(s), the model split over several when the GPU count is above 1;
- lean (one GPU): the embedding and output layer keep on the GPU only the rows decoding uses, one CUDA-graph bucket,
  and optionally layers whose experts stay in RAM (with GPU cache slots) and 8-bit attention and tower weights.

Sizes are what PyTorch allocates at its peak, measured on RTX 3090s (Windows, torch 2.11).
"""

from dataclasses import dataclass, field

GIB = 1024 ** 3
LAYERS = 40

# One layer's 256 experts, and one expert in the GPU cache.
EXPERT_LAYER = {"fp8": 0.745 * GIB, "int4": 0.418 * GIB}
SLOT = {"fp8": 3.2e6, "int4": 1.77e6}
# Attention, routers and shared experts of all 40 layers; the audio decoder ("tower"); bf16 and 8-bit.
DENSE = {False: 2.8 * GIB, True: 1.63 * GIB}
TOWER = {False: 2.3 * GIB, True: 1.34 * GIB}
# The embedding and output layer: whole, or (lean) the 4097 rows decoding uses.
VOCAB, VOCAB_ROWS = 1.92 * GIB, 0.03 * GIB
# The codec's weights on the GPU, and what decoding a segment's audio there takes on top (it peaks at the end).
CODEC, CODEC_DECODE = 3.8 * GIB, 1.6 * GIB
# Graphs, KV caches, prefill and sampling: resident (four KV buckets) per GPU, and lean (one bucket, 4096 rows).
WORKING_BASE, WORKING_PER_GPU = 2.3 * GIB, 0.75 * GIB
WORKING_LEAN = 0.6 * GIB
# Left free on every GPU besides the estimate (the lean estimates came within 0.2 GB of the measured peaks). On
# Windows a GPU that runs over doesn't fail, it spills into system memory and everything gets many times slower.
SAFETY = 0.5 * GIB
MIN_SLOTS, MAX_SLOTS = 8, 128


@dataclass
class Plan:
    precision: str               # "fp8" or "int4"
    lean: bool                   # one GPU, the lean way
    layers_in_ram: int = 0       # lean: layers whose experts stay in RAM (the last ones)
    slots: int = 0               # lean: GPU cache slots per such layer
    codec_cpu: bool = False
    int8: bool = False           # lean: 8-bit attention and tower weights
    estimate: float = 0.0        # bytes on the GPU(s)
    budget: float = 0.0          # bytes the GPU(s) may use
    ram: float = 0.0             # bytes of RAM it needs besides what any worker needs
    warnings: list = field(default_factory=list)

    @property
    def fits(self):
        return self.estimate <= self.budget

    def describe(self, gpus):
        parts = ["FP8 experts" if self.precision == "fp8" else "4-bit experts"]
        if self.lean and self.layers_in_ram:
            parts.append(f"{self.layers_in_ram} of {LAYERS} layers' experts in RAM ({self.slots} cache slots each)")
        if self.codec_cpu:
            parts.append("codec on the CPU")
        if self.int8:
            parts.append("8-bit attention and tower")
        where = f"{gpus} GPUs" if gpus > 1 else "1 GPU"
        return (f"{', '.join(parts)}; about {self.estimate / GIB:.1f} GB of {self.budget / GIB:.1f} GB on {where}"
                + ("" if self.fits else " (may not fit)"))


def setting(settings, key, default="auto"):
    return str(settings.get(key) or default).strip().lower()


def choose(settings, budgets, ram_free, reserves):
    """The plan for these worker settings, GPU budgets (bytes per GPU, the target already applied) and free RAM.
    `reserves` holds drama3_patches' placement reserves (layer GPU, tower GPU, codec), which the resident split uses."""
    gpus = len(budgets)
    precision_setting = setting(settings, "expertPrecision")
    layers_setting = setting(settings, "expertLayersInRam")
    codec_setting = setting(settings, "codecDevice")
    int8_setting = setting(settings, "int8Weights")
    warnings = []

    if gpus > 1:
        if layers_setting not in ("auto", "0") or int8_setting == "on":
            warnings.append("Experts in RAM and 8-bit weights only work with a GPU count of 1; they're off.")
        layers_options, int8_options = [None], [False]  # None: resident
    else:
        int8_options = {"auto": [False, True], "off": [False], "on": [True]}.get(int8_setting, [False, True])
        if layers_setting == "auto":
            layers_options = [None] + list(range(LAYERS + 1))
        else:
            n = max(0, min(LAYERS, int(layers_setting)))
            layers_options = [None, 0] if n == 0 else [n]
    codec_options = {"auto": [False, True], "gpu": [False], "cpu": [True]}.get(codec_setting, [False, True])

    def estimate(precision, layers, codec_cpu, int8):
        if layers is None:  # resident: as drama3_patches places it, over the GPUs
            return resident_need(precision, codec_cpu, gpus, reserves)
        slots = MIN_SLOTS if layers else 0
        return (DENSE[int8] + TOWER[int8] + VOCAB_ROWS + (LAYERS - layers) * EXPERT_LAYER[precision]
                + layers * slots * SLOT[precision] + (0 if codec_cpu else CODEC + CODEC_DECODE) + WORKING_LEAN)

    # The resident split has its own measured reserves; the lean estimate gets the safety margin.
    budget = sum(budgets) - SAFETY * gpus

    def fits(precision, layers, codec_cpu, int8):
        if layers is None:
            return resident_fits(precision, codec_cpu, budgets, reserves)
        return estimate(precision, layers, codec_cpu, int8) <= budget

    def candidates(precision):
        for int8 in int8_options:
            for codec_cpu in codec_options:
                for layers in layers_options:
                    if layers is None and int8:
                        continue  # 8-bit weights are a lean-path thing
                    yield precision, layers, codec_cpu, int8

    if precision_setting == "fp8":
        precision = "fp8"
    elif precision_setting == "int4":
        precision = "int4"
    else:
        # FP8 only when it fits with nothing else switched on (the codec on the GPU unless it is set to the CPU): all
        # resident, or lean without experts in RAM (its trimmed embedding costs nothing).
        bare = [layers for layers in layers_options[:2] if not layers]
        precision = "fp8" if any(fits("fp8", layers, codec_options[0], False) for layers in bare) else "int4"

    chosen = next((c for c in candidates(precision) if fits(*c)), None)
    if chosen is None:
        chosen = list(candidates(precision))[-1]  # everything Auto switched on
    precision, layers, codec_cpu, int8 = chosen
    plan = Plan(precision, lean=layers is not None, layers_in_ram=layers or 0, codec_cpu=codec_cpu, int8=int8,
                budget=budget if layers is not None else sum(budgets), warnings=warnings)
    plan.estimate = estimate(precision, layers, codec_cpu, int8)
    if layers is None and resident_fits(precision, codec_cpu, budgets, reserves):
        plan.estimate = min(plan.estimate, plan.budget)  # fits as the split places it
    if plan.lean and plan.layers_in_ram:
        # What is left goes to cache slots: each one saves loads from RAM.
        spare = max(0.0, budget - plan.estimate)
        extra = int(spare // (plan.layers_in_ram * SLOT[precision]))
        plan.slots = min(MAX_SLOTS, MIN_SLOTS + extra)
        plan.estimate += plan.layers_in_ram * (plan.slots - MIN_SLOTS) * SLOT[precision]
    if not plan.fits:
        plan.warnings.append(f"Drama 3 needs about {plan.estimate / GIB:.1f} GB of GPU memory and has "
                             f"{plan.budget / GIB:.1f} GB; trying anyway. Raise the GPU count or the GPU memory "
                             "target, or set the Auto memory settings back to Auto.")
    if plan.lean:
        plan.ram = (plan.layers_in_ram * EXPERT_LAYER[precision] + VOCAB
                    + (CODEC if plan.codec_cpu else 0))  # float32 on the CPU: the same bytes as on the GPU
        if ram_free and plan.ram + 2 * GIB > ram_free:
            plan.warnings.append(f"It needs about {plan.ram / GIB:.0f} GB of free RAM and "
                                 f"{ram_free / GIB:.0f} GB are free; loading may fail or be slow.")
    return plan


def resident_layers(precision):
    return LAYERS * (EXPERT_LAYER[precision] + DENSE[False] / LAYERS)


def resident_need(precision, codec_cpu, gpus, reserves):
    layer_reserve, tower_reserve, codec_reserve = reserves
    return (resident_layers(precision) + VOCAB + tower_reserve + (0 if codec_cpu else codec_reserve)
            + layer_reserve * gpus)


def resident_fits(precision, codec_cpu, budgets, reserves):
    """As drama3_patches' split: every GPU keeps its layer reserve, the last one also the tower's and the codec's."""
    layer_reserve, tower_reserve, codec_reserve = reserves
    room = [b - layer_reserve for b in budgets]
    room[-1] -= VOCAB + tower_reserve + (0 if codec_cpu else codec_reserve)
    layer = resident_layers(precision) / LAYERS  # whole layers only, as the split places them
    return room[-1] >= 0 and sum(int(max(0.0, r) // layer) for r in room) >= LAYERS


def available_ram():
    """Free physical memory in bytes (0 when unknown)."""
    import os
    import sys

    try:
        if sys.platform == "win32":
            import ctypes

            class Status(ctypes.Structure):
                _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                            ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                            ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                            ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                            ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]

            status = Status()
            status.dwLength = ctypes.sizeof(Status)
            ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status))
            return float(status.ullAvailPhys)
        return float(os.sysconf("SC_AVPHYS_PAGES") * os.sysconf("SC_PAGE_SIZE"))
    except Exception:
        return 0.0
