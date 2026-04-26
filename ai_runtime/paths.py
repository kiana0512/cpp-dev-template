from __future__ import annotations

from pathlib import Path


def find_project_root(start: Path | None = None) -> Path:
    current = (start or Path.cwd()).resolve()
    for path in (current, *current.parents):
        if (path / "configs").is_dir() and (path / "scripts").is_dir():
            return path
    return current


PROJECT_ROOT = find_project_root()


def resolve_project_path(path: str | Path | None) -> Path | None:
    if path is None:
        return None
    p = Path(path)
    if p.is_absolute():
        return p
    return PROJECT_ROOT / p


def first_existing_skeleton_tree() -> Path | None:
    configs = PROJECT_ROOT / "configs"
    if not configs.exists():
        return None
    matches = sorted(configs.glob("skeleton_tree*.json"))
    return matches[0] if matches else None
