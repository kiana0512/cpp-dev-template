from __future__ import annotations

from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any


@dataclass
class ModelCheckResult:
    model_dir_exists: bool
    config_exists: bool
    looks_complete: bool
    found_files: list[str]
    missing_or_suspicious_files: list[str]
    warnings: list[str]
    recommendation: str

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def _find_any(model_dir: Path, names: list[str], patterns: list[str]) -> str | None:
    for name in names:
        p = model_dir / name
        if p.exists():
            return str(p)
    for pattern in patterns:
        matches = list(model_dir.rglob(pattern)) if model_dir.exists() else []
        if matches:
            return str(matches[0])
    return None


def check_indextts2_model_dir(model_dir: Path, config_path: Path) -> ModelCheckResult:
    found: list[str] = []
    suspicious: list[str] = []
    warnings: list[str] = []
    model_dir_exists = model_dir.exists() and model_dir.is_dir()
    config_exists = config_path.exists() and config_path.is_file()

    if not model_dir_exists:
        warnings.append(f"model_dir does not exist: {model_dir}")
    if not config_exists:
        warnings.append(f"config.yaml is missing or unreadable: {config_path}")
        suspicious.append("config.yaml")
    else:
        try:
            config_path.read_text(encoding="utf-8-sig", errors="replace")
            found.append(str(config_path))
        except Exception as exc:
            warnings.append(f"config.yaml exists but could not be read: {exc}")
            suspicious.append("config.yaml unreadable")

    checks = [
        ("tokenizer/bpe", ["bpe.model"], ["*bpe*", "*tokenizer*"]),
        ("gpt weights", ["gpt.pth"], ["*gpt*.pth", "*gpt*.safetensors"]),
        ("s2mel weights", ["s2mel.pth"], ["*s2mel*.pth", "*s2mel*.safetensors"]),
        ("qwen emotion model", ["qwen0.6bemo4-merge"], ["*qwen*", "*.safetensors"]),
        ("wav2vec2bert stats", ["wav2vec2bert_stats.pt"], ["*wav2vec2bert*stats*.pt"]),
    ]

    if model_dir_exists:
        for label, names, patterns in checks:
            hit = _find_any(model_dir, names, patterns)
            if hit:
                found.append(hit)
            else:
                warnings.append(f"Could not find expected {label}; filename may differ, real IndexTTS2 load will decide.")
                suspicious.append(label)

    looks_complete = model_dir_exists and config_exists and len(suspicious) == 0
    recommendation = (
        "If config.yaml is missing, the model download is probably incomplete. "
        "Run: modelscope download --model IndexTeam/IndexTTS-2 --local_dir .\\models\\IndexTTS-2"
    )
    if not config_exists:
        warnings.append("Current model may not be fully downloaded yet; wait for ModelScope download to finish or rerun the command.")

    return ModelCheckResult(
        model_dir_exists=model_dir_exists,
        config_exists=config_exists,
        looks_complete=looks_complete,
        found_files=found,
        missing_or_suspicious_files=suspicious,
        warnings=warnings,
        recommendation=recommendation,
    )
