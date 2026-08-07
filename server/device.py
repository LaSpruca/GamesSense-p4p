"""
Accelerator selection for the haptics server.

The server should use the best hardware the host actually has, rather than assuming CUDA.
Detection order for --device auto:

    cuda  -> NVIDIA GPU via PyTorch. Fastest; runs the whole model.
    xpu   -> Intel discrete/integrated GPU via PyTorch XPU (torch>=2.4 or intel-extension-for-pytorch).
    cpu   -> always available.

NPU is deliberately NOT in the auto chain; it is opt-in via --device npu. Measured on an
Intel Core Ultra 7 155U (AI Boost), 1s clip:

    EnCodec decode   234 ms on CPU  ->  146 ms on NPU   (1.6x on that stage, output matches
                                                         PyTorch to 0.05% relative error)
    full generation  ~33 s          ->  ~33 s           (the LM dominates; NPU saves ~0.3%)

and the first request for a given clip duration pays a ~22 minute one-off OpenVINO compile
(cached to disk afterwards, ~8 s on later runs). Auto-selecting that trade would mean a 22-minute
stall on first use for a sub-1% win, so you have to ask for it.

What "npu" actually means here
------------------------------
HapticGen is AudioGen: a 1.5B autoregressive transformer LM that emits EnCodec tokens, plus an
EnCodec decoder that turns those tokens into a waveform. Those two halves suit very different
hardware:

  * The LM is autoregressive with a growing KV cache, so its shapes change on every step. NPUs
    want static shapes and compile per shape, so stepping an LM on one is a losing trade — it
    stays on the best PyTorch device (cuda/xpu/cpu).
  * The EnCodec decoder is a fixed-shape conv stack run once per clip. That IS a good NPU fit,
    and it is what we offload (see npu_decode.py).

So `--device npu` means "LM on CPU (or the best torch device), EnCodec decode on the NPU". It is
not a claim that the whole model runs on the NPU. Anything that fails to compile or run on the
NPU falls back to PyTorch automatically, so enabling it can't break generation — only slow it.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, List, Optional

VALID_PREFERENCES = ("auto", "cuda", "xpu", "npu", "cpu")


@dataclass(frozen=True)
class DeviceChoice:
    """Resolved placement for one server process."""

    kind: str                    # cuda | xpu | npu | cpu  (what the user asked for / we picked)
    torch_device: str            # device string handed to PyTorch for the LM
    ov_device: Optional[str]     # OpenVINO device for the offloaded decode, or None
    detail: str                  # human-readable, surfaced in /health and at startup

    @property
    def uses_npu(self) -> bool:
        return self.ov_device is not None

    def describe(self) -> str:
        # ASCII only: this goes to stdout, and Windows consoles default to cp1252.
        if self.uses_npu:
            return (f"{self.kind} (LM on {self.torch_device}, EnCodec decode on OpenVINO "
                    f"{self.ov_device}) - {self.detail}")
        return f"{self.kind} (torch: {self.torch_device}) - {self.detail}"


def _torch():
    try:
        import torch
        return torch
    except ImportError:
        return None


def _openvino_devices() -> List[str]:
    """OpenVINO's device list, e.g. ['CPU', 'GPU', 'NPU']. Empty when openvino isn't installed."""
    try:
        import openvino as ov
        return list(ov.Core().available_devices)
    except Exception:
        return []


def _openvino_name(device: str) -> str:
    try:
        import openvino as ov
        return str(ov.Core().get_property(device, "FULL_DEVICE_NAME"))
    except Exception:
        return device


def cuda_available() -> bool:
    torch = _torch()
    return bool(torch is not None and torch.cuda.is_available())


def xpu_available() -> bool:
    """Intel GPU through PyTorch. torch.xpu only exists on newer builds, hence the getattr."""
    torch = _torch()
    if torch is None:
        return False
    xpu = getattr(torch, "xpu", None)
    try:
        return bool(xpu is not None and xpu.is_available())
    except Exception:
        return False


def npu_available() -> bool:
    """True when OpenVINO can see an NPU on this host."""
    return "NPU" in _openvino_devices()


def probe() -> Dict[str, object]:
    """Everything we can see. Used by /health and the --list-devices flag."""
    ov_devices = _openvino_devices()
    torch = _torch()
    return {
        "torch": getattr(torch, "__version__", None),
        "cuda": cuda_available(),
        "xpu": xpu_available(),
        "npu": "NPU" in ov_devices,
        "npu_name": _openvino_name("NPU") if "NPU" in ov_devices else None,
        "openvino_devices": ov_devices,
    }


def resolve_device(preference: str = "auto") -> DeviceChoice:
    """
    Turn a --device preference into a concrete placement.

    An explicit preference that isn't available degrades to the next best option with a reason in
    `detail` rather than raising — a server that starts slow beats one that won't start.
    """
    preference = (preference or "auto").lower()
    if preference not in VALID_PREFERENCES:
        raise ValueError(f"--device must be one of {', '.join(VALID_PREFERENCES)} (got {preference!r})")

    if preference in ("auto", "cuda") and cuda_available():
        torch = _torch()
        try:
            name = torch.cuda.get_device_name(0)
        except Exception:
            name = "CUDA device"
        return DeviceChoice("cuda", "cuda", None, name)
    if preference == "cuda":
        return _fallback("cuda", "no CUDA device visible to PyTorch")

    if preference in ("auto", "xpu") and xpu_available():
        return DeviceChoice("xpu", "xpu", None, "Intel GPU via torch.xpu")
    if preference == "xpu":
        return _fallback("xpu", "torch.xpu unavailable (needs torch>=2.4 or intel-extension-for-pytorch)")

    # Opt-in only -- see the module docstring for the measurements behind that.
    if preference == "npu" and npu_available():
        # The LM still needs a torch device; the NPU only takes the decode stage.
        lm_device = "xpu" if xpu_available() else "cpu"
        print("[npu] First request per clip duration compiles the decoder for the NPU; that takes "
              "~20 minutes and is then cached under server/.ov_cache. Later starts are seconds.")
        return DeviceChoice("npu", lm_device, "NPU", _openvino_name("NPU"))
    if preference == "npu":
        reason = ("openvino not installed (pip install openvino)" if not _openvino_devices()
                  else f"no NPU among OpenVINO devices {_openvino_devices()}")
        return _fallback("npu", reason)

    if preference == "auto" and npu_available():
        return DeviceChoice("cpu", "cpu", None,
                            f"NPU present ({_openvino_name('NPU')}) but not auto-selected; "
                            "pass --device npu to use it")
    return DeviceChoice("cpu", "cpu", None, "no accelerator detected" if preference == "auto"
                        else "explicitly requested")


def _fallback(requested: str, reason: str) -> DeviceChoice:
    """Requested accelerator is missing — take the best remaining option and say why."""
    if cuda_available():
        return DeviceChoice("cuda", "cuda", None, f"{requested} unavailable ({reason}); using CUDA")
    if xpu_available():
        return DeviceChoice("xpu", "xpu", None, f"{requested} unavailable ({reason}); using Intel GPU")
    return DeviceChoice("cpu", "cpu", None, f"{requested} unavailable ({reason}); using CPU")
