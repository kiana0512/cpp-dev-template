from __future__ import annotations

from collections.abc import Iterable


def clamp01(x: float) -> float:
    return max(0.0, min(1.0, float(x)))


def smooth_attack_release(values: Iterable[float], attack: float = 0.65, release: float = 0.25) -> list[float]:
    out: list[float] = []
    y = 0.0
    for raw in values:
        x = clamp01(float(raw))
        alpha = attack if x > y else release
        y = y + alpha * (x - y)
        out.append(clamp01(y))
    return out


def smooth_moving_average(values: Iterable[float], window: int = 3) -> list[float]:
    items = [float(v) for v in values]
    if window <= 1 or not items:
        return [clamp01(v) for v in items]
    radius = window // 2
    out: list[float] = []
    for i in range(len(items)):
        lo = max(0, i - radius)
        hi = min(len(items), i + radius + 1)
        out.append(clamp01(sum(items[lo:hi]) / max(1, hi - lo)))
    return out
