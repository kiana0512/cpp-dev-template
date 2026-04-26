from __future__ import annotations

import argparse
import socket
import time
from pathlib import Path

from ai_runtime.protocol.jsonl_io import read_jsonl


def play_frames(
    frames_path: Path,
    host: str = "127.0.0.1",
    port: int = 7001,
    fps: float = 30.0,
    use_timestamp: bool = True,
) -> None:
    frames = read_jsonl(frames_path)
    total = len(frames)
    if total == 0:
        raise ValueError(f"No frames found in {frames_path}")

    try:
        sock = socket.create_connection((host, int(port)), timeout=8.0)
    except OSError as exc:
        raise ConnectionError(
            f"Could not connect to UE at {host}:{port}: {exc}\n"
            "- Check UE is open.\n"
            "- Check ExpressionReceiverComponent StartListening is running.\n"
            "- Check the port matches the component Listen Port.\n"
            "- Check Windows firewall."
        ) from exc

    start = time.monotonic()
    try:
        with sock:
            for i, frame in enumerate(frames, start=1):
                if use_timestamp:
                    target = float(frame.get("timestamp_ms", 0)) / 1000.0
                else:
                    target = (i - 1) / float(fps)
                delay = start + target - time.monotonic()
                if delay > 0:
                    time.sleep(delay)
                line = __import__("json").dumps(frame, ensure_ascii=False, separators=(",", ":")) + "\n"
                sock.sendall(line.encode("utf-8"))
                meta = frame.get("meta", {})
                if i == 1 or i == total or i % 30 == 0:
                    print(f"{i}/{total} sequence_id={frame.get('sequence_id')} time_sec={meta.get('time_sec', target)}")
    except KeyboardInterrupt:
        print("Interrupted; socket closed.")


def main() -> int:
    parser = argparse.ArgumentParser(description="Play ExpressionFrame JSONL to UE over one TCP connection.")
    parser.add_argument("--frames", required=True)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7001)
    parser.add_argument("--fps", type=float, default=30.0)
    parser.add_argument("--use-timestamp", action="store_true")
    args = parser.parse_args()
    play_frames(Path(args.frames), args.host, args.port, args.fps, args.use_timestamp)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
