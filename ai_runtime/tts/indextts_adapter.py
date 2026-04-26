from __future__ import annotations

import importlib
import inspect
import json
import os
import subprocess
import sys
import textwrap
import threading
import time
import traceback
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Callable, Iterator, Optional

from ai_runtime.config import nested_get
from ai_runtime.paths import resolve_project_path

from .base import TtsBackend
from .dryrun_tts import DryRunTts
from .local_model_resolver import analyze_indextts2_local_paths


def _truthy(value: Any, default: bool = False) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    return str(value).lower() in {"1", "true", "yes", "on"}


class IndexTtsAdapter(TtsBackend):
    name = "indextts"

    def __init__(self, backend_mode: str = "auto", config: dict[str, Any] | None = None, logger: Callable[[str, str, str, dict[str, Any] | None], None] | None = None) -> None:
        self.backend_mode = backend_mode
        self.config = config or {}
        self.tts_config = self.config.get("tts", self.config)
        self.warnings: list[str] = []
        self.resolved_mode = backend_mode
        self.attempts: list[dict[str, str]] = []
        self.logger = logger

    def _log(self, level: str, stage: str, message: str, data: dict[str, Any] | None = None) -> None:
        if self.logger:
            self.logger(level, stage, message, data)
        else:
            print(f"[AI_RUNTIME][{level}][{stage}] {message}", flush=True)

    @contextmanager
    def _heartbeat(self, stage: str, interval_sec: float = 2.0) -> Iterator[None]:
        stop = threading.Event()
        start = time.perf_counter()

        def run() -> None:
            while not stop.wait(interval_sec):
                elapsed = time.perf_counter() - start
                self._log("HEARTBEAT", stage, f"elapsed={elapsed:.1f} still_running=true", {"elapsed": round(elapsed, 1)})

        thread = threading.Thread(target=run, name=f"ai-runtime-heartbeat-{stage}", daemon=True)
        thread.start()
        try:
            yield
        finally:
            stop.set()
            thread.join(timeout=interval_sec + 0.5)

    def _path(self, key: str) -> Path | None:
        value = nested_get({"tts": self.tts_config}, "tts", key, default="")
        if not value:
            return None
        return resolve_project_path(value)

    @property
    def repo(self) -> Path | None:
        return self._path("indextts_repo")

    @property
    def model_dir(self) -> Path | None:
        return self._path("indextts_model_dir")

    @property
    def config_path(self) -> Path | None:
        return self._path("indextts_config")

    @property
    def infer_script(self) -> Path | None:
        return self._path("indextts_infer_script")

    @property
    def python_executable(self) -> str:
        configured = str(self.tts_config.get("python_executable") or "").strip()
        return configured or sys.executable

    def _friendly_error(self, original: Exception | str, backend_mode: str | None = None) -> RuntimeError:
        mode = backend_mode or self.backend_mode
        attempts = "; ".join(f"{a['mode']}: {a['error']}" for a in self.attempts) or "none"
        return RuntimeError(
            "IndexTTS synthesis failed.\n"
            f"backend_mode: {mode}\n"
            f"attempts: {attempts}\n"
            f"repo: {self.repo or ''}\n"
            f"model_dir: {self.model_dir or ''}\n"
            f"config: {self.config_path or ''}\n"
            f"infer_script: {self.infer_script or ''}\n"
            f"python_executable: {self.python_executable}\n"
            f"sys_path_head: {sys.path[:8]}\n"
            f"original_error: {str(original).splitlines()[0][:800]}\n"
            "Suggestions:\n"
            "- Ensure the official index-tts repo is installed: cd <index-tts repo>; pip install -e .\n"
            "- Or run: uv sync --all-extras\n"
            "- Ensure model weights are complete: modelscope download --model IndexTeam/IndexTTS-2 --local_dir <project>\\models\\IndexTTS-2\n"
            "- Provide a Speaker WAV for real IndexTTS2 inference."
        )

    def _record_failure(self, mode: str, exc: Exception | str) -> None:
        self.attempts.append({"mode": mode, "error": str(exc).splitlines()[0][:500]})

    def _local_dirs(self) -> tuple[Path, Path]:
        if not self.model_dir:
            raise RuntimeError("indextts_model_dir is required for local/offline IndexTTS2.")
        return self.model_dir / "w2v-bert-2.0", self.model_dir / "MaskGCT"

    def _prepare_local_offline(self, offline_mode: bool) -> None:
        if not self.model_dir or not self.config_path:
            raise RuntimeError("IndexTTS2 local mode requires indextts_model_dir and indextts_config.")
        w2v_dir, mask_dir = self._local_dirs()
        os.environ["INDEXTTS_MODEL_DIR"] = str(self.model_dir)
        os.environ["W2V_BERT_LOCAL_DIR"] = str(w2v_dir)
        os.environ["MASKGCT_LOCAL_DIR"] = str(mask_dir)
        os.environ["BIGVGAN_LOCAL_DIR"] = str(self.model_dir / "BigVGAN")
        if offline_mode:
            os.environ["HF_HUB_OFFLINE"] = "1"
            os.environ["TRANSFORMERS_OFFLINE"] = "1"
            os.environ["HF_HUB_DISABLE_XET"] = "1"
        self._log("INFO", "local_model", f"W2V_BERT_LOCAL_DIR={w2v_dir}")
        self._log("INFO", "local_model", f"MASKGCT_LOCAL_DIR={mask_dir}")
        report = analyze_indextts2_local_paths(self.config_path, self.model_dir)
        if not report.can_run_offline and offline_mode:
            raise RuntimeError(
                "Local IndexTTS2 dependencies are incomplete; refusing model load in offline mode.\n"
                "Missing local files:\n"
                + "\n".join(report.missing_local_files)
                + "\nRecommended commands:\n"
                + "\n".join(report.recommendations)
            )
        self._patch_hf_local_paths(w2v_dir, mask_dir, offline_mode)
        self._log("INFO", "local_model", "local dependencies OK")

    def _patch_hf_local_paths(self, w2v_dir: Path, mask_dir: Path, offline_mode: bool) -> None:
        try:
            import huggingface_hub  # type: ignore

            original_hf_hub_download = huggingface_hub.hf_hub_download

            def local_hf_hub_download(repo_id: str, filename: str | None = None, *args: Any, **kwargs: Any) -> str:
                if repo_id == "amphion/MaskGCT" and filename:
                    local = mask_dir / filename
                    if not local.exists():
                        raise FileNotFoundError(f"Local MaskGCT file missing: {local}")
                    return str(local)
                if repo_id == "funasr/campplus" and filename:
                    local = self.model_dir / "campplus" / filename if self.model_dir else Path(filename)
                    if not local.exists():
                        raise FileNotFoundError(f"Local campplus file missing: {local}")
                    return str(local)
                if repo_id.startswith("nvidia/") and filename:
                    local = (self.model_dir / "BigVGAN" / filename) if self.model_dir else Path(filename)
                    if not local.exists():
                        raise FileNotFoundError(f"Local BigVGAN file missing: {local}")
                    return str(local)
                if offline_mode:
                    raise RuntimeError(f"Remote model access is forbidden in local/offline mode: repo_id={repo_id}, filename={filename}")
                return original_hf_hub_download(repo_id, filename=filename, *args, **kwargs)

            huggingface_hub.hf_hub_download = local_hf_hub_download  # type: ignore
        except Exception as exc:
            self._log("WARN", "local_model", f"Could not monkey patch huggingface_hub: {exc}")

        try:
            from transformers import SeamlessM4TFeatureExtractor  # type: ignore

            original_from_pretrained = SeamlessM4TFeatureExtractor.from_pretrained

            def local_from_pretrained(pretrained_model_name_or_path: Any, *args: Any, **kwargs: Any) -> Any:
                if str(pretrained_model_name_or_path) == "facebook/w2v-bert-2.0":
                    kwargs["local_files_only"] = True
                    return original_from_pretrained(str(w2v_dir), *args, **kwargs)
                if offline_mode and str(pretrained_model_name_or_path).startswith(("facebook/", "amphion/")):
                    raise RuntimeError(f"Remote from_pretrained is forbidden in local/offline mode: {pretrained_model_name_or_path}")
                return original_from_pretrained(pretrained_model_name_or_path, *args, **kwargs)

            SeamlessM4TFeatureExtractor.from_pretrained = local_from_pretrained  # type: ignore
        except Exception as exc:
            self._log("WARN", "local_model", f"Could not monkey patch SeamlessM4TFeatureExtractor: {exc}")

    def _import_indextts2_class(self) -> Any:
        repo = self.repo
        if repo and repo.exists():
            repo_text = str(repo.resolve())
            if repo_text in sys.path:
                sys.path.remove(repo_text)
            sys.path.insert(0, repo_text)
            os.environ["PYTHONPATH"] = repo_text + os.pathsep + os.environ.get("PYTHONPATH", "")
        try:
            self._log("INFO", "tts_import", "importing indextts.infer_v2")
            from indextts.infer_v2 import IndexTTS2  # type: ignore

            self._log("INFO", "tts_import", "imported IndexTTS2")
            return IndexTTS2
        except Exception as first_exc:
            details = self._import_debug_details(first_exc)
            self._log("ERROR", "tts_import", details)
            raise ImportError(details) from first_exc

    def _import_debug_details(self, exc: Exception) -> str:
        transformers_version = "not importable"
        try:
            import transformers  # type: ignore

            transformers_version = str(getattr(transformers, "__version__", "unknown"))
        except Exception as version_exc:
            transformers_version = f"not importable: {version_exc}"
        try:
            import omegaconf  # type: ignore  # noqa: F401

            omegaconf_status = "OK"
        except Exception as omega_exc:
            omegaconf_status = f"FAILED: {omega_exc}"
        tb = "".join(traceback.format_exception_only(type(exc), exc)).strip()
        special = ""
        if "OffloadedCache" in str(exc):
            special += (
                "\nDetected transformers.cache_utils.OffloadedCache import failure.\n"
                "This usually means the selected Python environment has an incompatible transformers version.\n"
                "Use the IndexTTS2 uv environment:\n"
                "D:\\RT\\ue5-virtual-human-bridge\\third_party\\index-tts\\.venv\\Scripts\\python.exe\n"
            )
        if "omegaconf" in str(exc).lower() or omegaconf_status.startswith("FAILED"):
            special += (
                "\nMissing omegaconf. The selected Python is missing IndexTTS2 dependencies.\n"
                "Run uv sync in third_party/index-tts or select the uv .venv python.\n"
            )
        return (
            "indextts.infer_v2 could not be imported.\n"
            f"sys.executable: {sys.executable}\n"
            f"python_executable_config: {self.python_executable}\n"
            f"indextts_repo: {self.repo or ''}\n"
            f"sys_path_head: {sys.path[:10]}\n"
            f"transformers_version: {transformers_version}\n"
            f"omegaconf: {omegaconf_status}\n"
            f"original_traceback_summary: {tb}\n"
            "Fix: cd D:\\RT\\ue5-virtual-human-bridge\\third_party\\index-tts && uv sync --default-index \"https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple\"\n"
            "Or select the uv .venv python in GUI.\n"
            f"{special}"
        )

    @staticmethod
    def _filter_kwargs(callable_obj: Any, kwargs: dict[str, Any]) -> dict[str, Any]:
        try:
            sig = inspect.signature(callable_obj)
        except Exception:
            return kwargs
        accepted = set(sig.parameters)
        has_kwargs = any(p.kind == inspect.Parameter.VAR_KEYWORD for p in sig.parameters.values())
        if has_kwargs:
            return {k: v for k, v in kwargs.items() if v is not None}
        return {k: v for k, v in kwargs.items() if k in accepted and v is not None}

    def _emotion_vector(self, emotion: str) -> list[float]:
        vec = [0.0] * 8
        slot = {
            "happy": 0,
            "angry": 1,
            "sad": 2,
            "surprised": 6,
            "neutral": 7,
        }.get((emotion or "neutral").lower(), 7)
        vec[slot] = 0.8 if slot != 7 else 0.7
        return vec

    def _speaker_wav(self, speaker_wav: Optional[Path]) -> Path:
        selected = speaker_wav or self._path("default_speaker_wav")
        if not selected:
            raise RuntimeError("IndexTTS2 requires spk_audio_prompt. Please provide Speaker WAV.")
        if not selected.exists():
            raise RuntimeError(f"Speaker WAV does not exist: {selected}")
        return selected

    def _synthesize_api(
        self,
        text: str,
        out_wav: Path,
        speaker_wav: Optional[Path],
        emotion: str,
        emotion_prompt: Optional[str],
        target_duration_sec: Optional[float],
        language: str,
        offline_mode: bool,
    ) -> Path:
        self._log("INFO", "tts_prepare", "checking IndexTTS2 API paths")
        if not self.model_dir or not self.config_path:
            raise RuntimeError("IndexTTS2 API mode requires indextts_model_dir and indextts_config.")
        if not self.model_dir.exists():
            raise RuntimeError(
                f"Model dir does not exist: {self.model_dir}. "
                "Please finish: modelscope download --model IndexTeam/IndexTTS-2 --local_dir .\\models\\IndexTTS-2"
            )
        if not self.config_path.exists():
            raise RuntimeError(f"config.yaml not found. Model download may be incomplete: {self.config_path}")
        spk = self._speaker_wav(speaker_wav)
        self._prepare_local_offline(offline_mode)
        self._log("STAGE", "tts_model_load", f"cfg_path={self.config_path}")
        self._log("STAGE", "tts_model_load", f"model_dir={self.model_dir}")
        self._log("STAGE", "tts_model_load", f"offline_mode={offline_mode}")
        self._log("INFO", "tts_import", f"sys.executable={sys.executable}")
        self._log("INFO", "tts_import", f"indextts_repo={self.repo}")
        self._log("INFO", "tts_import", f"sys.path_head={sys.path[:5]}")
        IndexTTS2 = self._import_indextts2_class()
        out_wav.parent.mkdir(parents=True, exist_ok=True)

        use_fp16 = _truthy(self.tts_config.get("use_fp16"), False)
        init_kwargs_raw = {
            "cfg_path": str(self.config_path),
            "model_dir": str(self.model_dir),
            "use_fp16": use_fp16,
            "is_fp16": use_fp16,
            "use_cuda_kernel": _truthy(self.tts_config.get("use_cuda_kernel"), False),
            "use_deepspeed": _truthy(self.tts_config.get("use_deepspeed"), False),
        }
        init_kwargs = self._filter_kwargs(IndexTTS2, init_kwargs_raw)
        self._log("STAGE", "tts_model_load", "Loading IndexTTS2 model...", {"kwargs": sorted(init_kwargs)})
        start = time.perf_counter()
        try:
            with self._heartbeat("tts_model_load"):
                tts = IndexTTS2(**init_kwargs)
        except Exception as exc:
            err = str(exc)
            if offline_mode and any(token in err for token in ["cas-bridge.xethub.hf.co", "huggingface.co", "ReadTimeout", "facebook/w2v-bert-2.0", "amphion/MaskGCT"]):
                self._log("ERROR", "tts_model_load", "Online access detected in offline mode.")
                raise RuntimeError(
                    "Remote model access is forbidden in local/offline mode.\n"
                    "IndexTTS2 attempted to download from Hugging Face during model load.\n"
                    "This means some dependency/model path is not resolved locally.\n"
                    "Check models\\IndexTTS-2\\config.yaml and qwen0.6bemo4-merge local files.\n"
                    "Offline mode is enabled, so online download should be avoided."
                ) from exc
            self._log("ERROR", "tts_model_load", str(exc))
            raise
        self._log("STAGE", "tts_model_load", f"Model loaded elapsed={time.perf_counter() - start:.2f}", {"elapsed": round(time.perf_counter() - start, 3)})
        infer = getattr(tts, "infer")
        self._log("INFO", "tts_infer", "preparing infer kwargs")
        infer_kwargs: dict[str, Any] = {
            "spk_audio_prompt": str(spk),
            "text": text,
            "output_path": str(out_wav),
            "verbose": True,
            "use_random": _truthy(self.tts_config.get("use_random"), False),
        }
        if emotion_prompt:
            infer_kwargs.update(
                {
                    "use_emo_text": True,
                    "emo_text": emotion_prompt,
                    "emo_alpha": float(self.tts_config.get("emo_alpha", 0.6) or 0.6),
                }
            )
        else:
            infer_kwargs["emo_vector"] = self._emotion_vector(emotion)
        emo_audio = self._path("default_emo_audio_prompt")
        if emo_audio and emo_audio.exists():
            infer_kwargs["emo_audio_prompt"] = str(emo_audio)
        if target_duration_sec:
            infer_kwargs["target_dur"] = target_duration_sec
            infer_kwargs["use_speed"] = True
        if language and language != "auto":
            infer_kwargs["language"] = language
        filtered_infer_kwargs = self._filter_kwargs(infer, infer_kwargs)
        if target_duration_sec and "target_dur" not in filtered_infer_kwargs and "use_speed" not in filtered_infer_kwargs:
            self.warnings.append("target_duration_sec was provided, but this IndexTTS2 infer signature does not support target_dur/use_speed.")
        self._log("STAGE", "tts_infer", "Running IndexTTS2 inference...", {"keys": sorted(filtered_infer_kwargs)})
        start = time.perf_counter()
        try:
            with self._heartbeat("tts_infer"):
                infer(**filtered_infer_kwargs)
        except Exception as exc:
            self._log("ERROR", "tts_infer", str(exc))
            raise
        self._log("STAGE", "tts_infer", f"generated wav={out_wav} elapsed={time.perf_counter() - start:.2f}", {"wav": str(out_wav), "elapsed": round(time.perf_counter() - start, 3)})
        if not out_wav.exists() or out_wav.stat().st_size <= 0:
            raise RuntimeError(f"IndexTTS2 API finished but did not create a non-empty wav: {out_wav}")
        self._log("INFO", "tts_infer", f"output wav exists bytes={out_wav.stat().st_size}")
        return out_wav

    def _write_wrapper(self, path: Path) -> None:
        wrapper = r'''
import argparse
import inspect
import json
import sys
from pathlib import Path

def filter_kwargs(fn, kwargs):
    try:
        sig = inspect.signature(fn)
    except Exception:
        return {k: v for k, v in kwargs.items() if v is not None}
    params = set(sig.parameters)
    has_kwargs = any(p.kind == inspect.Parameter.VAR_KEYWORD for p in sig.parameters.values())
    return {k: v for k, v in kwargs.items() if v is not None and (has_kwargs or k in params)}

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--payload", required=True)
    args = ap.parse_args()
    data = json.loads(Path(args.payload).read_text(encoding="utf-8-sig"))
    repo = data.get("repo")
    if repo:
        sys.path.insert(0, repo)
    print("[IndexTTS2Wrapper] importing indextts.infer_v2", flush=True)
    from indextts.infer_v2 import IndexTTS2
    init_kwargs = filter_kwargs(IndexTTS2, {
        "cfg_path": data["config"],
        "model_dir": data["model_dir"],
        "use_fp16": data.get("use_fp16", False),
        "is_fp16": data.get("use_fp16", False),
        "use_cuda_kernel": data.get("use_cuda_kernel", False),
        "use_deepspeed": data.get("use_deepspeed", False),
    })
    print("[IndexTTS2Wrapper] init kwargs=" + json.dumps(init_kwargs, ensure_ascii=False), flush=True)
    tts = IndexTTS2(**init_kwargs)
    infer = getattr(tts, "infer")
    infer_kwargs = data["infer_kwargs"]
    print("[IndexTTS2Wrapper] infer keys=" + ",".join(sorted(infer_kwargs)), flush=True)
    infer(**filter_kwargs(infer, infer_kwargs))
    out = Path(data["out_wav"])
    if not out.exists() or out.stat().st_size <= 0:
        raise RuntimeError(f"output wav missing or empty: {out}")
    print(f"[IndexTTS2Wrapper] wrote {out} bytes={out.stat().st_size}", flush=True)

if __name__ == "__main__":
    main()
'''
        path.write_text(textwrap.dedent(wrapper).lstrip(), encoding="utf-8")

    def _synthesize_cli_wrapper(
        self,
        text: str,
        out_wav: Path,
        speaker_wav: Optional[Path],
        emotion: str,
        emotion_prompt: Optional[str],
        target_duration_sec: Optional[float],
        language: str,
        offline_mode: bool,
    ) -> Path:
        self._log("INFO", "tts_prepare", "checking IndexTTS2 CLI wrapper paths")
        if not self.model_dir or not self.config_path:
            raise RuntimeError("IndexTTS2 CLI wrapper mode requires indextts_model_dir and indextts_config.")
        if not self.model_dir.exists():
            raise RuntimeError(
                f"Model dir does not exist: {self.model_dir}. "
                "Please finish: modelscope download --model IndexTeam/IndexTTS-2 --local_dir .\\models\\IndexTTS-2"
            )
        if not self.config_path.exists():
            raise RuntimeError(f"config.yaml not found. Model download may be incomplete: {self.config_path}")
        out_wav.parent.mkdir(parents=True, exist_ok=True)
        spk = self._speaker_wav(speaker_wav)
        self._prepare_local_offline(offline_mode)
        wrapper = out_wav.parent / "run_indextts2_wrapper.py"
        payload = out_wav.parent / "run_indextts2_payload.json"
        self._write_wrapper(wrapper)
        infer_kwargs: dict[str, Any] = {
            "spk_audio_prompt": str(spk),
            "text": text,
            "output_path": str(out_wav),
            "verbose": True,
            "use_random": _truthy(self.tts_config.get("use_random"), False),
        }
        if emotion_prompt:
            infer_kwargs.update(
                {
                    "use_emo_text": True,
                    "emo_text": emotion_prompt,
                    "emo_alpha": float(self.tts_config.get("emo_alpha", 0.6) or 0.6),
                }
            )
        else:
            infer_kwargs["emo_vector"] = self._emotion_vector(emotion)
        emo_audio = self._path("default_emo_audio_prompt")
        if emo_audio and emo_audio.exists():
            infer_kwargs["emo_audio_prompt"] = str(emo_audio)
        if target_duration_sec:
            infer_kwargs["target_dur"] = target_duration_sec
            infer_kwargs["use_speed"] = True
        if language and language != "auto":
            infer_kwargs["language"] = language
        data = {
            "repo": str(self.repo) if self.repo else "",
            "model_dir": str(self.model_dir),
            "config": str(self.config_path),
            "out_wav": str(out_wav),
            "use_fp16": _truthy(self.tts_config.get("use_fp16"), False),
            "use_cuda_kernel": _truthy(self.tts_config.get("use_cuda_kernel"), False),
            "use_deepspeed": _truthy(self.tts_config.get("use_deepspeed"), False),
            "infer_kwargs": infer_kwargs,
        }
        payload.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        cmd = [self.python_executable, str(wrapper), "--payload", str(payload)]
        cwd = str(self.repo) if self.repo and self.repo.exists() else str(Path.cwd())
        env = os.environ.copy()
        if self.repo:
            env["PYTHONPATH"] = str(self.repo) + os.pathsep + env.get("PYTHONPATH", "")
        self._log("STAGE", "tts_infer", "Running IndexTTS2 CLI wrapper...")
        print("[AI_RUNTIME][INFO][tts_infer] IndexTTS2 wrapper command: " + " ".join(cmd), flush=True)
        with self._heartbeat("tts_infer"):
            result = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, encoding="utf-8", timeout=1800)
        if result.stdout:
            print(result.stdout, end="", flush=True)
        if result.stderr:
            print(result.stderr, end="", file=sys.stderr, flush=True)
        if result.returncode != 0:
            raise RuntimeError(f"IndexTTS2 wrapper failed rc={result.returncode}: {(result.stderr or result.stdout)[:1200]}")
        if not out_wav.exists() or out_wav.stat().st_size <= 0:
            raise RuntimeError(f"IndexTTS2 wrapper finished but wav is missing or empty: {out_wav}")
        return out_wav

    def _synthesize_legacy_api(
        self,
        text: str,
        out_wav: Path,
        speaker_wav: Optional[Path],
        emotion: str,
        emotion_prompt: Optional[str],
        target_duration_sec: Optional[float],
        language: str,
    ) -> Path:
        modules = ["indextts", "index_tts", "IndexTTS"]
        loaded = None
        for module in modules:
            try:
                loaded = importlib.import_module(module)
                break
            except Exception:
                continue
        if loaded is None:
            raise RuntimeError(f"Could not import legacy IndexTTS modules: {modules}")
        for attr in ["synthesize", "infer", "tts"]:
            fn = getattr(loaded, attr, None)
            if callable(fn):
                kwargs: dict[str, Any] = {"text": text, "out_wav": str(out_wav)}
                if speaker_wav:
                    kwargs["speaker_wav"] = str(speaker_wav)
                try:
                    out_wav.parent.mkdir(parents=True, exist_ok=True)
                    fn(**self._filter_kwargs(fn, kwargs))
                except TypeError:
                    fn(text, str(out_wav))
                if out_wav.exists() and out_wav.stat().st_size > 0:
                    return out_wav
        raise RuntimeError("Legacy module importable, but no compatible synthesize/infer/tts entry was found.")

    def _candidate_modes(self) -> list[str]:
        if self.backend_mode != "auto":
            return [self.backend_mode]
        modes: list[str] = []
        repo_ok = bool(self.repo and self.repo.exists())
        model_ok = bool(self.model_dir and self.model_dir.exists())
        config_ok = bool(self.config_path and self.config_path.exists())
        if repo_ok and model_ok and config_ok:
            modes.extend(["indextts2_local_api", "indextts2_local_cli"])
        else:
            modes.append("indextts2_local_api")
            if repo_ok:
                modes.append("indextts2_local_cli")
        modes.append("indextts_legacy")
        return modes

    def synthesize(
        self,
        text: str,
        out_wav: Path,
        speaker_wav: Optional[Path] = None,
        emotion: str = "neutral",
        emotion_prompt: Optional[str] = None,
        target_duration_sec: Optional[float] = None,
        language: str = "auto",
        offline_mode: bool = True,
    ) -> Path:
        last_exc: Exception | None = None
        for mode in self._candidate_modes():
            try:
                self.resolved_mode = mode
                self.name = mode
                if mode in {"indextts2", "indextts2_local_api"}:
                    self._log("INFO", "tts_prepare", f"attempting backend={mode}")
                    return self._synthesize_api(text, out_wav, speaker_wav, emotion, emotion_prompt, target_duration_sec, language, offline_mode)
                if mode == "indextts2_local_cli":
                    self._log("INFO", "tts_prepare", f"attempting backend={mode}")
                    return self._synthesize_cli_wrapper(text, out_wav, speaker_wav, emotion, emotion_prompt, target_duration_sec, language, offline_mode)
                if mode == "indextts_legacy":
                    if emotion_prompt:
                        self.warnings.append("emotion_prompt is not supported by legacy IndexTTS; ignored.")
                    return self._synthesize_legacy_api(text, out_wav, speaker_wav, emotion, emotion_prompt, target_duration_sec, language)
                if mode == "dryrun":
                    self.resolved_mode = "dryrun"
                    self.name = "dryrun"
                    return DryRunTts().synthesize(text, out_wav, emotion=emotion, target_duration_sec=target_duration_sec)
                raise RuntimeError(f"Unsupported backend mode: {mode}")
            except Exception as exc:
                last_exc = exc
                self._record_failure(mode, exc)
                if self.backend_mode != "auto":
                    break
        if self.backend_mode == "auto" and _truthy(self.tts_config.get("fallback_to_dryrun"), False):
            self.warnings.append("IndexTTS auto failed; fallback_to_dryrun=true so dryrun wav was generated.")
            self.resolved_mode = "dryrun_fallback"
            self.name = "dryrun_fallback"
            return DryRunTts().synthesize(text, out_wav, emotion=emotion, target_duration_sec=target_duration_sec)
        raise self._friendly_error(last_exc or "No backend attempted")
