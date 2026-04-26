from __future__ import annotations

import argparse
import json
import os
import platform
import re
import shutil
import sys
import time
import traceback
from contextlib import contextmanager
from datetime import datetime
from pathlib import Path
from typing import Any, Iterator

if __package__ is None or __package__ == "":
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from ai_runtime import __version__
from ai_runtime.alignment.heuristic_aligner import HeuristicAligner
from ai_runtime.alignment.optional_forced_aligner import OptionalForcedAligner
from ai_runtime.audio.wav_io import load_wav_mono
from ai_runtime.config import nested_get
from ai_runtime.face.morph_timeline_generator import MorphTimelineGenerator
from ai_runtime.paths import PROJECT_ROOT, first_existing_skeleton_tree, resolve_project_path
from ai_runtime.protocol.jsonl_io import write_jsonl
from ai_runtime.protocol.validator import validate_frames
from ai_runtime.skeleton.skeleton_tree_loader import load_skeleton_tree
from ai_runtime.tts.dryrun_tts import DryRunTts
from ai_runtime.tts.indextts_adapter import IndexTtsAdapter
from ai_runtime.tts.local_model_resolver import analyze_indextts2_local_paths
from ai_runtime.tts.model_check import check_indextts2_model_dir
from ai_runtime.tts.remote_reference_scanner import scan_remote_references


def _str_to_bool(value: str | bool | None, default: bool = False) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return value.lower() in {"1", "true", "yes", "on"}


def _default_out_dir(session_name: str | None = None) -> Path:
    if session_name:
        return Path("output") / "ai_sessions" / session_name
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    return Path("output") / "ai_sessions" / f"session_{stamp}"


def _copy_wav(src: Path, dst: Path) -> Path:
    dst.parent.mkdir(parents=True, exist_ok=True)
    if src.resolve() != dst.resolve():
        shutil.copy2(src, dst)
    return dst


def _numpy_available() -> bool:
    try:
        import numpy  # noqa: F401

        return True
    except Exception:
        return False


class RuntimeLogger:
    def __init__(self, log_json: Path | None = None, verbose: bool = False) -> None:
        self.log_json = log_json
        self.verbose = verbose
        if self.log_json:
            self.log_json.parent.mkdir(parents=True, exist_ok=True)

    def emit(self, level: str, stage: str, message: str, data: dict[str, Any] | None = None) -> None:
        print(f"[AI_RUNTIME][{level}][{stage}] {message}", flush=True)
        if self.log_json:
            event = {
                "time": datetime.now().isoformat(timespec="milliseconds"),
                "level": level,
                "stage": stage,
                "message": message,
                "data": data or {},
            }
            with self.log_json.open("a", encoding="utf-8") as f:
                f.write(json.dumps(event, ensure_ascii=False, separators=(",", ":")) + "\n")


def write_json_atomic(path: Path, data: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    text = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    with tmp.open("w", encoding="utf-8") as f:
        f.write(text)
        f.flush()
    try:
        tmp.replace(path)
        return
    except PermissionError:
        try:
            if path.exists():
                os.chmod(path, 0o666)
                path.unlink()
            tmp.replace(path)
            return
        except Exception:
            # Last resort: keep the payload valid and visible rather than losing the real error.
            with path.open("w", encoding="utf-8") as f:
                f.write(text)
            try:
                tmp.unlink(missing_ok=True)
            except Exception:
                pass


def log_event(level: str, stage: str, message: str, data: dict[str, Any] | None = None, logger: RuntimeLogger | None = None) -> None:
    if logger:
        logger.emit(level, stage, message, data)
    else:
        print(f"[AI_RUNTIME][{level}][{stage}] {message}", flush=True)


def load_json_file(path: Path) -> dict[str, Any]:
    p = Path(path)
    try:
        with p.open("r", encoding="utf-8-sig") as f:
            data = json.load(f)
    except Exception as exc:
        raise ValueError(f"Failed to load JSON file {p}: {exc}") from exc
    if not isinstance(data, dict):
        raise ValueError(f"JSON file must contain an object: {p}")
    return data


@contextmanager
def timed(stage_timings: dict[str, float], stage: str, logger: RuntimeLogger) -> Iterator[None]:
    start = time.perf_counter()
    logger.emit("STAGE", stage, "start")
    try:
        yield
    finally:
        elapsed = time.perf_counter() - start
        stage_timings[stage] = round(elapsed, 4)
        logger.emit("STAGE", stage, f"done in {elapsed:.3f}s")


def build_arg_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Generate AI TTS wav and morph-only ExpressionFrame JSONL.")
    parser.add_argument("--config")
    parser.add_argument("--log-json")
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--print-progress", action="store_true")
    parser.add_argument("--session-name")
    parser.add_argument("--playback-hint", action="store_true")
    parser.add_argument("--check-model-only", action="store_true")
    parser.add_argument("--text")
    parser.add_argument("--emotion", choices=["neutral", "happy", "sad", "angry", "surprised"])
    parser.add_argument("--speaker-wav")
    parser.add_argument("--emotion-prompt")
    parser.add_argument("--target-duration-sec", type=float)
    parser.add_argument("--out-dir")
    parser.add_argument("--fps", type=float)
    parser.add_argument("--character-id")
    parser.add_argument("--morph-map")
    parser.add_argument("--skeleton-tree")
    parser.add_argument("--tts-backend", choices=["auto", "indextts2", "indextts2_local_cli", "indextts2_local_api", "indextts_legacy", "dryrun"])
    parser.add_argument("--alignment-backend", choices=["heuristic", "forced_optional"])
    parser.add_argument("--emotion-strength", type=float)
    parser.add_argument("--blink-enabled")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--skip-tts", action="store_true")
    parser.add_argument("--wav")
    parser.add_argument("--python-executable")
    parser.add_argument("--indextts-repo")
    parser.add_argument("--indextts-model-dir")
    parser.add_argument("--indextts-config")
    parser.add_argument("--indextts-infer-script")
    parser.add_argument("--default-speaker-wav")
    parser.add_argument("--use-fp16", action="store_true")
    parser.add_argument("--use-cuda-kernel", action="store_true")
    parser.add_argument("--use-deepspeed", action="store_true")
    parser.add_argument("--use-random", action="store_true")
    parser.add_argument("--fallback-to-dryrun", action="store_true")
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--allow-online", action="store_true")
    parser.add_argument("--use-generated-local-config", action="store_true")
    return parser


def _cfg(config: dict[str, Any], group: str, key: str, default: Any) -> Any:
    return nested_get(config, group, key, default=default)


def _resolved(args: argparse.Namespace, config: dict[str, Any]) -> dict[str, Any]:
    tts = dict(config.get("tts") or {})
    cli_map = {
        "backend": args.tts_backend,
        "python_executable": args.python_executable,
        "indextts_repo": args.indextts_repo,
        "indextts_model_dir": args.indextts_model_dir,
        "indextts_config": args.indextts_config,
        "indextts_infer_script": args.indextts_infer_script,
        "default_speaker_wav": args.default_speaker_wav,
    }
    for k, v in cli_map.items():
        if v is not None:
            tts[k] = v
    for k, v in {
        "use_fp16": args.use_fp16,
        "use_cuda_kernel": args.use_cuda_kernel,
        "use_deepspeed": args.use_deepspeed,
        "use_random": args.use_random,
        "fallback_to_dryrun": args.fallback_to_dryrun,
    }.items():
        if v:
            tts[k] = True

    runtime = dict(config.get("runtime") or {})
    face = dict(config.get("face") or {})
    alignment = dict(config.get("alignment") or {})
    if args.fps is not None:
        runtime["fps"] = args.fps
    if args.character_id is not None:
        runtime["character_id"] = args.character_id
    if args.morph_map is not None:
        runtime["morph_map"] = args.morph_map
    if args.skeleton_tree is not None:
        runtime["skeleton_tree"] = args.skeleton_tree
    if args.emotion_strength is not None:
        face["emotion_strength"] = args.emotion_strength
    if args.blink_enabled is not None:
        face["blink_enabled"] = _str_to_bool(args.blink_enabled, True)
    if args.alignment_backend is not None:
        alignment["backend"] = args.alignment_backend
    return {"tts": tts, "runtime": runtime, "face": face, "alignment": alignment}


def _resolve_path_or_empty(value: Any) -> Path | None:
    if value is None or str(value).strip() == "":
        return None
    return resolve_project_path(str(value))


def _resolve_offline_mode(args: argparse.Namespace) -> bool:
    if args.allow_online:
        return False
    if args.offline:
        return True
    return True


def _apply_offline_env(offline_mode: bool) -> None:
    if not offline_mode:
        return
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_XET"] = "1"


def _log_env(logger: RuntimeLogger) -> None:
    for key in ["HF_HUB_OFFLINE", "TRANSFORMERS_OFFLINE", "HF_HUB_DISABLE_XET", "HF_HOME"]:
        logger.emit("INFO", "env", f"{key}={os.environ.get(key, '')}")


def _generate_local_config(config_yaml: Path, model_dir: Path, out_dir: Path) -> Path:
    text = config_yaml.read_text(encoding="utf-8-sig")
    qwen_abs = str((model_dir / "qwen0.6bemo4-merge").resolve()).replace("\\", "/")
    wav2vec_abs = str((model_dir / "wav2vec2bert_stats.pt").resolve()).replace("\\", "/")
    patched = re.sub(r"(?<![/\\\w.-])qwen0\.6bemo4-merge(?![/\\\w.-])", qwen_abs, text)
    patched = re.sub(r"(?<![/\\\w.-])wav2vec2bert_stats\.pt(?![/\\\w.-])", wav2vec_abs, patched)
    target = out_dir / "config.local.generated.yaml"
    target.write_text(patched, encoding="utf-8")
    return target


def run(argv: list[str] | None = None) -> int:
    args = build_arg_parser().parse_args(argv)
    stage_timings: dict[str, float] = {}
    warnings: list[str] = []
    failed_stage = ""
    config: dict[str, Any] = {}
    resolved: dict[str, Any] = {}
    out_dir = resolve_project_path(args.out_dir) if args.out_dir else resolve_project_path(_default_out_dir(args.session_name))
    assert out_dir is not None
    out_dir.mkdir(parents=True, exist_ok=True)
    logger = RuntimeLogger(resolve_project_path(args.log_json) if args.log_json else None, args.verbose)
    logger.emit("INFO", "startup", f"runtime_python={sys.executable}")
    manifest_path = out_dir / "manifest.json"
    failed_manifest_path = out_dir / "manifest_failed.json"
    model_check = None
    local_model_report = None
    remote_reference_report = None
    offline_mode = _resolve_offline_mode(args)
    original_config_path = ""
    effective_config_path = ""
    config_patched = False

    try:
        _apply_offline_env(offline_mode)
        _log_env(logger)
        with timed(stage_timings, "config_load", logger):
            failed_stage = "config_load"
            config_path = resolve_project_path(args.config) if args.config else (PROJECT_ROOT / "configs" / "ai_runtime.example.json")
            original_config_path = str(config_path)
            logger.emit("INFO", "config_load", f"config_path={config_path}")
            config = load_json_file(config_path)
            resolved = _resolved(args, config)
            if not args.out_dir:
                configured_root = _cfg(resolved, "runtime", "output_root", "")
                if configured_root and args.session_name:
                    out_dir = (resolve_project_path(configured_root) or Path(configured_root)) / args.session_name
                    out_dir.mkdir(parents=True, exist_ok=True)
                    manifest_path = out_dir / "manifest.json"
                    failed_manifest_path = out_dir / "manifest_failed.json"

        with timed(stage_timings, "path_resolve", logger):
            failed_stage = "path_resolve"
            tts_cfg = resolved["tts"]
            config_python = str(tts_cfg.get("python_executable") or "")
            if config_python and Path(config_python).name and str(Path(config_python)) != sys.executable:
                logger.emit("WARN", "path_resolve", "Config python_executable differs from runtime sys.executable.", {
                    "python_executable_config": config_python,
                    "python_executable_runtime": sys.executable,
                })
            runtime_cfg = resolved["runtime"]
            face_cfg = resolved["face"]
            text = args.text if args.text is not None else str(_cfg(config, "runtime", "text", ""))
            if not text:
                text = "你好，我是初音未来虚拟人助手，很高兴见到你。"
            emotion = args.emotion or str(_cfg(config, "runtime", "emotion", "neutral"))
            fps = float(runtime_cfg.get("fps", 30))
            character_id = str(runtime_cfg.get("character_id", "yyb_miku"))
            morph_map = _resolve_path_or_empty(runtime_cfg.get("morph_map")) or PROJECT_ROOT / "configs" / "blendshape_map_yyb_miku.json"
            skeleton_tree = _resolve_path_or_empty(runtime_cfg.get("skeleton_tree")) or first_existing_skeleton_tree()
            model_dir = _resolve_path_or_empty(tts_cfg.get("indextts_model_dir")) or PROJECT_ROOT / "models" / "IndexTTS-2"
            config_yaml = _resolve_path_or_empty(tts_cfg.get("indextts_config")) or model_dir / "config.yaml"
            effective_config = config_yaml
            local_model_report = analyze_indextts2_local_paths(config_yaml, model_dir)
            if local_model_report.remote_references:
                logger.emit("WARN", "model_check", f"Remote references detected: {', '.join(local_model_report.remote_references)}")
                if offline_mode:
                    logger.emit(
                        "ERROR",
                        "model_check",
                        "Config may still point to remote HF model names. Please ensure paths are local.",
                    )
            for miss in local_model_report.missing_local_files:
                logger.emit("WARN", "model_check", f"Missing local file: {miss}")
            if args.use_generated_local_config and local_model_report.remote_references:
                effective_config = _generate_local_config(config_yaml, model_dir, out_dir)
                config_patched = True
                logger.emit("WARN", "config_patch", f"Generated local config: {effective_config}")
            tts_cfg["indextts_model_dir"] = str(model_dir)
            tts_cfg["indextts_config"] = str(effective_config)
            effective_config_path = str(effective_config)
            if tts_cfg.get("indextts_repo"):
                tts_cfg["indextts_repo"] = str(_resolve_path_or_empty(tts_cfg.get("indextts_repo")) or tts_cfg.get("indextts_repo"))
            if tts_cfg.get("indextts_infer_script"):
                tts_cfg["indextts_infer_script"] = str(_resolve_path_or_empty(tts_cfg.get("indextts_infer_script")) or tts_cfg.get("indextts_infer_script"))
            if tts_cfg.get("default_speaker_wav"):
                tts_cfg["default_speaker_wav"] = str(_resolve_path_or_empty(tts_cfg.get("default_speaker_wav")) or tts_cfg.get("default_speaker_wav"))
            speaker = _resolve_path_or_empty(args.speaker_wav) or _resolve_path_or_empty(tts_cfg.get("default_speaker_wav"))
            generated_wav = out_dir / "generated.wav"
            frames_path = out_dir / "frames.jsonl"
            events_path = out_dir / "ai_runtime_events.jsonl"
            if args.log_json is None:
                logger.log_json = events_path
            remote_reference_report = scan_remote_references(tts_cfg.get("indextts_repo"), effective_config, PROJECT_ROOT)
            if remote_reference_report.has_dangerous_refs:
                logger.emit("WARN", "remote_scan", "Remote model references detected:\n" + remote_reference_report.format_summary())

        with timed(stage_timings, "model_check", logger):
            failed_stage = "model_check"
            model_check = check_indextts2_model_dir(model_dir, config_yaml)
            for w in model_check.warnings:
                logger.emit("WARN", "model_check", w)
                warnings.append(w)
            if args.check_model_only:
                print(
                    json.dumps(
                        {
                            "model_check": model_check.to_dict(),
                            "local_model_report": local_model_report.to_dict() if local_model_report else None,
                            "remote_reference_report": remote_reference_report.to_dict() if remote_reference_report else None,
                        },
                        ensure_ascii=False,
                        indent=2,
                    ),
                    flush=True,
                )
                return 0 if model_check.config_exists else 2

        skeleton_available = bool(skeleton_tree and skeleton_tree.exists())
        skeleton_bone_count: int | None = None
        if skeleton_available and skeleton_tree:
            try:
                skeleton_bone_count = load_skeleton_tree(skeleton_tree).bone_count
            except Exception as exc:
                warnings.append(f"Could not read skeleton tree: {exc}")
                skeleton_available = False

        tts_backend = str(tts_cfg.get("backend") or args.tts_backend or "auto")
        if args.dry_run:
            tts_backend = "dryrun"
        if tts_backend != "dryrun" and not speaker:
            msg = "IndexTTS2 real inference usually needs spk_audio_prompt; choose Speaker WAV or set default_speaker_wav."
            warnings.append(msg)
            logger.emit("WARN", "tts", msg)
        if offline_mode and tts_backend != "dryrun" and not args.skip_tts:
            if local_model_report and not local_model_report.can_run_offline:
                raise RuntimeError(
                    "Local IndexTTS2 dependencies are incomplete; refusing to enter model load in offline mode.\n"
                    "Missing local files:\n"
                    + "\n".join(local_model_report.missing_local_files)
                    + "\nRecommended commands:\n"
                    + "\n".join(local_model_report.recommendations)
                )
            if remote_reference_report and remote_reference_report.has_dangerous_refs:
                raise RuntimeError(
                    "Remote model references detected while offline mode is enabled; refusing to wait for Hugging Face timeout.\n"
                    + remote_reference_report.format_summary()
                    + "\nRun: .\\scripts\\patch_indextts2_local_paths.ps1"
                )

        with timed(stage_timings, "tts", logger):
            failed_stage = "tts"
            log_event("STAGE", "tts_prepare", f"backend={tts_backend} python={sys.executable}", logger=logger)
            if args.skip_tts:
                if not args.wav:
                    raise RuntimeError("--skip-tts requires --wav")
                source_wav = _resolve_path_or_empty(args.wav)
                if not source_wav or not source_wav.exists():
                    raise RuntimeError(f"--wav does not exist: {source_wav}")
                _copy_wav(source_wav, generated_wav)
                tts_backend_resolved = "skip_tts_existing_wav"
            elif tts_backend == "dryrun":
                log_event("STAGE", "tts_infer", "dryrun synthesis start", logger=logger)
                DryRunTts().synthesize(text, generated_wav, emotion=emotion, target_duration_sec=args.target_duration_sec)
                log_event("STAGE", "tts_infer", f"generated wav={generated_wav}", logger=logger)
                tts_backend_resolved = "dryrun"
            else:
                adapter = IndexTtsAdapter(tts_backend, resolved, logger=logger.emit)
                adapter.synthesize(
                    text,
                    generated_wav,
                    speaker_wav=speaker,
                    emotion=emotion,
                    emotion_prompt=args.emotion_prompt or str(tts_cfg.get("emotion_prompt") or ""),
                    target_duration_sec=args.target_duration_sec,
                    offline_mode=offline_mode,
                )
                tts_backend_resolved = adapter.resolved_mode
                warnings.extend(adapter.warnings)

        with timed(stage_timings, "wav_load", logger):
            failed_stage = "wav_load"
            sample_rate, samples = load_wav_mono(generated_wav)
            duration_sec = len(samples) / float(sample_rate) if sample_rate else 0.0

        alignment_backend = str(resolved["alignment"].get("backend") or "heuristic")
        aligner = OptionalForcedAligner() if alignment_backend == "forced_optional" else HeuristicAligner()
        logger.emit("STAGE", "audio_features", "scheduled inside morph_timeline")
        logger.emit("STAGE", "alignment", f"backend={aligner.name}")

        with timed(stage_timings, "morph_timeline", logger):
            failed_stage = "morph_timeline"
            generator = MorphTimelineGenerator(morph_map, alignment_backend=aligner)
            frames = generator.generate(
                text=text,
                wav_path=generated_wav,
                emotion=emotion,
                fps=fps,
                character_id=character_id,
                emotion_strength=float(face_cfg.get("emotion_strength", 0.7)),
                blink_enabled=_str_to_bool(face_cfg.get("blink_enabled"), True),
                tts_backend=tts_backend_resolved,
                skeleton_tree_available=skeleton_available,
                meta_extra={"skeleton_motion": False, "morph_only": True},
            )
            stage_timings.setdefault("audio_features", 0.0)
            stage_timings.setdefault("alignment", 0.0)

        with timed(stage_timings, "validation", logger):
            failed_stage = "validation"
            errors = validate_frames(frames)
            if errors:
                raise RuntimeError("Generated frames failed validation:\n" + "\n".join(errors[:20]))

        with timed(stage_timings, "write_outputs", logger):
            failed_stage = "write_outputs"
            write_jsonl(frames, frames_path)
            manifest: dict[str, Any] = {
                "text": text,
                "emotion": emotion,
                "emotion_prompt": args.emotion_prompt,
                "character_id": character_id,
                "fps": fps,
                "duration_sec": round(duration_sec, 4),
                "frame_count": len(frames),
                "wav_path": str(generated_wav),
                "frames_path": str(frames_path),
                "manifest_path": str(manifest_path),
                "morph_map_path": str(morph_map),
                "skeleton_tree_path": str(skeleton_tree) if skeleton_tree else "",
                "skeleton_tree_available": skeleton_available,
                "skeleton_bone_count": skeleton_bone_count,
                "tts_backend": tts_backend,
                "tts_backend_resolved": tts_backend_resolved,
                "alignment_backend": aligner.name,
                "emotion_strength": float(face_cfg.get("emotion_strength", 0.7)),
                "blink_enabled": _str_to_bool(face_cfg.get("blink_enabled"), True),
                "morph_only": True,
                "skeleton_motion": False,
                "dry_run": args.dry_run or tts_backend == "dryrun",
                "skip_tts": args.skip_tts,
                "generated_at": datetime.now().isoformat(timespec="seconds"),
                "ai_runtime_version": __version__,
                "command_line": " ".join(sys.argv),
                "python_executable": sys.executable,
                "python_executable_runtime": sys.executable,
                "python_executable_config": str(tts_cfg.get("python_executable") or ""),
                "platform": platform.platform(),
                "numpy_available": _numpy_available(),
                "indextts_adapter_mode": tts_backend_resolved,
                "indextts_repo": str(tts_cfg.get("indextts_repo") or ""),
                "indextts_model_dir": str(model_dir),
                "indextts_config": str(config_yaml),
                "indextts_infer_script": str(tts_cfg.get("indextts_infer_script") or ""),
                "stage_timings": stage_timings,
                "model_check": model_check.to_dict() if model_check else None,
                "local_model_report": local_model_report.to_dict() if local_model_report else None,
                "remote_reference_report": remote_reference_report.to_dict() if remote_reference_report else None,
                "warnings": warnings,
                "offline_mode": offline_mode,
                "hf_hub_offline": os.environ.get("HF_HUB_OFFLINE", ""),
                "transformers_offline": os.environ.get("TRANSFORMERS_OFFLINE", ""),
                "hf_hub_disable_xet": os.environ.get("HF_HUB_DISABLE_XET", ""),
                "hf_home": os.environ.get("HF_HOME", ""),
                "original_config": original_config_path,
                "effective_config": effective_config_path or str(config_yaml),
                "config_patched": config_patched,
                "exit_status": "success",
            }
            write_json_atomic(manifest_path, manifest)
        logger.emit("DONE", "done", f"frames={frames_path} duration={duration_sec:.3f} manifest={manifest_path}", {"frame_count": len(frames)})
        return 0
    except Exception as exc:
        failed_stage = failed_stage or "runtime"
        logger.emit("ERROR", failed_stage, str(exc))
        print(f"[AI_RUNTIME][ERROR] failed_stage={failed_stage} error={str(exc)}", flush=True)
        failed = {
            "exit_status": "failed",
            "failed_stage": failed_stage,
            "error_message": str(exc),
            "traceback_summary": "".join(traceback.format_exception_only(type(exc), exc)).strip(),
            "warnings": warnings,
            "resolved_paths": {
                "out_dir": str(out_dir),
                "project_root": str(PROJECT_ROOT),
            },
            "model_check": model_check.to_dict() if model_check else None,
            "local_model_report": local_model_report.to_dict() if local_model_report else None,
            "remote_reference_report": remote_reference_report.to_dict() if remote_reference_report else None,
            "stage_timings": stage_timings,
            "command_line": " ".join(sys.argv),
            "python_executable": sys.executable,
            "python_executable_runtime": sys.executable,
            "python_executable_config": str((resolved.get("tts") or {}).get("python_executable") or "") if isinstance(resolved, dict) else "",
            "platform": platform.platform(),
            "stdout_hint": "See AI Process Output",
            "offline_mode": offline_mode,
            "hf_hub_offline": os.environ.get("HF_HUB_OFFLINE", ""),
            "transformers_offline": os.environ.get("TRANSFORMERS_OFFLINE", ""),
            "hf_hub_disable_xet": os.environ.get("HF_HUB_DISABLE_XET", ""),
            "hf_home": os.environ.get("HF_HOME", ""),
            "original_config": original_config_path,
            "effective_config": effective_config_path,
            "config_patched": config_patched,
            "traceback": traceback.format_exc(),
        }
        write_json_atomic(failed_manifest_path, failed)
        print(f"[AI_RUNTIME][ERROR][manifest] wrote {failed_manifest_path}", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(run())
