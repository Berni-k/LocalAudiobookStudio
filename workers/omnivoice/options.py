"""The request's settings as OmniVoice arguments, shared by the PyTorch and ONNX workers."""

from dataclasses import dataclass
from typing import Optional


@dataclass
class Options:
    text: str
    language: Optional[str]  # an OmniVoice language id, None for Auto
    instruct: Optional[str]  # upstream's voice design string, e.g. "female, elderly, low pitch"
    ref_text: str
    speed: float
    steps: int


def read_options(req) -> Options:
    text = " ".join((req.text or "").split())
    if not text:
        raise ValueError("empty text")
    s = req.settings
    language = str(s.get("language") or "Auto")
    attributes = [str(s.get(k) or "any") for k in ("gender", "age", "pitch", "accent")]
    attributes = [a for a in attributes if a != "any"]
    if s.get("whisper"):
        attributes.append("whisper")
    return Options(
        text=text,
        language=None if language == "Auto" else language,
        instruct=", ".join(attributes) or None,
        ref_text=str(s.get("refText") or "").strip(),
        speed=float(s.get("speed", 1.0)),
        steps=int(s.get("steps", 32)),
    )
