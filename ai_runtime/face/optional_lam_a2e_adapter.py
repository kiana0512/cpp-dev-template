from __future__ import annotations

from pathlib import Path
from typing import Any

from .morph_map_loader import MorphMapLoader


SUPPORTED_ARKIT_SUBSET = [
    "jawOpen",
    "mouthSmile",
    "mouthFrown",
    "eyeBlinkLeft",
    "eyeBlinkRight",
    "eyeSquintLeft",
    "eyeSquintRight",
    "eyeWideLeft",
    "eyeWideRight",
    "browInnerUp",
    "browDown",
    "browOuterUpLeft",
    "browOuterUpRight",
]


class OptionalLamA2EAdapter:
    name = "lam_audio2expression_optional"

    def __init__(self, repo_path: str | Path | None = None) -> None:
        self.repo_path = Path(repo_path) if repo_path else None
        self.enabled = bool(self.repo_path and self.repo_path.exists())

    def infer(self, wav_path: Path, morph_map: MorphMapLoader) -> list[dict[str, float]]:
        if not self.enabled:
            raise RuntimeError("LAM-Audio2Expression repo is not configured; optional backend disabled.")
        raise RuntimeError(
            "LAM-Audio2Expression adapter is a reserved interface. "
            "Install/configure the local repo and wire its ARKit coefficient output in a later stage."
        )

    def manifest_status(self) -> dict[str, Any]:
        return {
            "backend": self.name,
            "enabled": self.enabled,
            "repo_path": str(self.repo_path) if self.repo_path else "",
            "supported_subset": SUPPORTED_ARKIT_SUBSET,
        }
