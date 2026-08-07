"""
OpenVINO NPU offload for HapticGen's EnCodec decode stage.

Why only this stage: see the long note in device.py. Short version — the autoregressive LM changes
shape every step and NPUs compile per shape, so it stays in PyTorch. The EnCodec decoder is a
fixed-shape conv stack run once per clip, which is exactly what an NPU is good at.

The wrapper is transparent: it forwards every attribute to the real CompressionModel and only
intercepts decode(). Any failure — export, compile, or inference — falls back to PyTorch and is
logged once, so turning the NPU on can never break generation.

Compiled blobs are cached per input shape under <cache_dir>, since OpenVINO compiles for one
static shape at a time and clip duration determines the token count.
"""

from __future__ import annotations

import os
from typing import Dict, Optional, Tuple


class NpuEncodecDecoder:
    """CompressionModel proxy that runs .decode() on an OpenVINO device."""

    def __init__(self, compression_model, ov_device: str = "NPU", cache_dir: Optional[str] = None):
        self._model = compression_model
        self._ov_device = ov_device
        self._cache_dir = cache_dir or os.path.join(os.path.dirname(__file__), ".ov_cache")
        self._compiled: Dict[Tuple[int, ...], object] = {}
        self._disabled = False          # set after a hard failure; from then on we stay on torch
        self._warned = False
        self.last_backend = "torch"     # "npu" | "torch", surfaced in the response headers

    # --- everything we don't override behaves exactly like the wrapped model ---
    def __getattr__(self, item):
        return getattr(self._model, item)

    def decode(self, codes, scale=None):
        import torch

        if self._disabled or scale is not None:
            # A non-None scale means per-item rescaling we don't replicate in the exported graph.
            self.last_backend = "torch"
            return self._model.decode(codes, scale)

        try:
            compiled = self._compiled_for(codes)
            if compiled is None:
                self.last_backend = "torch"
                return self._model.decode(codes, scale)

            out = compiled({0: codes.detach().cpu().to(torch.int32).numpy()})
            wav = torch.from_numpy(next(iter(out.values())))
            self.last_backend = "npu"
            return wav.to(codes.device)
        except Exception as e:  # pragma: no cover - hardware/driver dependent
            self._warn(f"NPU decode failed ({e}); falling back to PyTorch for the rest of this run.")
            self._disabled = True
            self.last_backend = "torch"
            return self._model.decode(codes, scale)

    # --- export + compile, cached per input shape ---
    def _compiled_for(self, codes):
        key = tuple(codes.shape)
        if key in self._compiled:
            return self._compiled[key]

        try:
            import openvino as ov
            import torch

            class _DecodeOnly(torch.nn.Module):
                """decode() as a standalone graph: int codes in, waveform out."""

                def __init__(self, model):
                    super().__init__()
                    self.model = model

                def forward(self, codes):
                    return self.model.decode(codes.long(), None)

            os.makedirs(self._cache_dir, exist_ok=True)
            core = ov.Core()
            core.set_property({"CACHE_DIR": self._cache_dir})

            example = codes.detach().cpu().to(torch.int32)
            with torch.no_grad():
                ov_model = ov.convert_model(
                    _DecodeOnly(self._model).eval(),
                    example_input=example,
                    input=[ov.PartialShape(list(key))],   # NPU needs a fully static shape
                )
            compiled = core.compile_model(ov_model, self._ov_device)
            self._compiled[key] = compiled
            print(f"[npu] EnCodec decode compiled for shape {key} on {self._ov_device}")
            return compiled
        except Exception as e:
            self._warn(f"Could not compile EnCodec decode for {self._ov_device} ({e}); "
                       "staying on PyTorch. Generation is unaffected, just not NPU-accelerated.")
            self._compiled[key] = None
            return None

    def _warn(self, msg: str):
        if not self._warned:
            print(f"[npu] {msg}")
            self._warned = True


def maybe_wrap(model, choice, cache_dir: Optional[str] = None) -> str:
    """
    Swap in the NPU decoder when the resolved device asks for it.

    Returns a short status string for logging / the /health payload. Mutates model.compression_model
    in place, which is how AudioGen.generate reaches the decoder.
    """
    if not getattr(choice, "uses_npu", False):
        return "off"
    try:
        model.compression_model = NpuEncodecDecoder(
            model.compression_model, ov_device=choice.ov_device, cache_dir=cache_dir
        )
        return f"decode->{choice.ov_device}"
    except Exception as e:  # pragma: no cover
        print(f"[npu] Could not attach NPU decoder ({e}); running fully in PyTorch.")
        return "off (attach failed)"
