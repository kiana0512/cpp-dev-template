from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


DANGEROUS_PATTERNS = [
    "huggingface.co",
    "hf_hub_download",
    "snapshot_download",
    "facebook/w2v-bert-2.0",
    "amphion/MaskGCT",
    'from_pretrained("facebook/',
    "from_pretrained('facebook/",
    'from_pretrained("amphion/',
    "from_pretrained('amphion/",
    "cas-bridge.xethub.hf.co",
]


@dataclass
class RemoteReference:
    path: str
    line: int
    pattern: str
    text: str

    def to_dict(self) -> dict[str, Any]:
        return {"path": self.path, "line": self.line, "pattern": self.pattern, "text": self.text}


@dataclass
class RemoteReferenceReport:
    references: list[RemoteReference] = field(default_factory=list)

    @property
    def has_dangerous_refs(self) -> bool:
        return bool(self.references)

    def to_dict(self) -> dict[str, Any]:
        return {"has_dangerous_refs": self.has_dangerous_refs, "references": [r.to_dict() for r in self.references]}

    def format_summary(self, limit: int = 20) -> str:
        rows = [f"{r.path}:{r.line}: {r.pattern}: {r.text}" for r in self.references[:limit]]
        if len(self.references) > limit:
            rows.append(f"... {len(self.references) - limit} more")
        return "\n".join(rows)


def _scan_file(path: Path, root: Path) -> list[RemoteReference]:
    found: list[RemoteReference] = []
    try:
        lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
    except Exception:
        return found
    rel = str(path.relative_to(root)) if path.is_relative_to(root) else str(path)
    for idx, line in enumerate(lines, start=1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        for pattern in DANGEROUS_PATTERNS:
            # The bundled GPT transformer files contain many docstring URLs to
            # Hugging Face docs. They are not model-loading paths and would
            # otherwise block offline runs with false positives.
            if pattern == "huggingface.co" and "indextts/gpt" in str(path).replace("\\", "/"):
                continue
            if pattern.lower() in stripped.lower():
                found.append(RemoteReference(rel, idx, pattern, stripped[:500]))
    return found


def scan_remote_references(index_tts_repo: str | Path | None, model_config: str | Path | None, project_root: str | Path | None = None) -> RemoteReferenceReport:
    root = Path(project_root or Path.cwd()).resolve()
    refs: list[RemoteReference] = []
    if index_tts_repo:
        repo = Path(index_tts_repo)
        indextts = repo / "indextts"
        if indextts.exists():
            for py in indextts.rglob("*.py"):
                refs.extend(_scan_file(py, root))
    if model_config:
        cfg = Path(model_config)
        if cfg.exists():
            refs.extend(_scan_file(cfg, root))
    return RemoteReferenceReport(refs)
