"""Duyu-small: a ~19 M-parameter Turkish + English speech recognizer for CPUs
and edge devices (Conformer encoder with shared refinement passes and a
sparse acoustic memory, CTC + attention decoder), shipped as ONNX graphs at
fp32 / int8 / 4-bit with a numpy decoder. See README.md."""

from .model import DuyuSmall, QUANTS  # noqa: F401

__version__ = "1.0.0"
