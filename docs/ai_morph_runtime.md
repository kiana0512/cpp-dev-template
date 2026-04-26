# AI Morph Runtime

当前阶段目标：

文本 -> IndexTTS2/IndexTTS 语音 -> 音频/文本启发式分析 -> Morph Target 时间线 -> UE VHReceiver TCP JSON line 播放。

当前默认不做骨骼动画。所有输出帧都保持：

- `meta.morph_only=true`
- `meta.skeleton_motion=false`

不会生成 `bone_rotation`、`skeleton_pose`、`control_rig` 等真实骨骼控制字段。

## 当前资产

- Morph 映射：`configs/blendshape_map_yyb_miku.json`
- Skeleton 树：`configs/skeleton_tree_*.json`
- Skeleton 语义映射：`configs/skeleton_semantic_map_yyb_miku.json`
- IndexTTS2 模型目录：`models/IndexTTS-2`
- IndexTTS2 config：`models/IndexTTS-2/config.yaml`

下载模型：

```powershell
modelscope download --model IndexTeam/IndexTTS-2 --local_dir .\models\IndexTTS-2
```

本项目默认优先本地模型，不应在 `tts_model_load` 阶段访问 Hugging Face。离线模式相关环境变量：

- `HF_HUB_OFFLINE=1`：禁用 Hugging Face Hub 在线请求；
- `TRANSFORMERS_OFFLINE=1`：Transformers 仅离线加载；
- `HF_HUB_DISABLE_XET=1`：禁用 Xet bridge 访问路径。

如果日志出现 `cas-bridge.xethub.hf.co` 或 `huggingface.co` 访问，通常说明本地路径尚未完全配置好（例如 `config.yaml` 中仍是远程引用，或 qwen/wav2vec 文件未本地化）。

## repo / model_dir / config 的区别

- `indextts_repo`：官方 `index-tts` 源码目录或 Python 包路径，用来 import `from indextts.infer_v2 import IndexTTS2`。
- `indextts_model_dir`：权重目录，当前默认 `models/IndexTTS-2`。
- `indextts_config`：模型配置，当前默认 `models/IndexTTS-2/config.yaml`。
- `python_executable`：实际运行 IndexTTS2 的 Python 环境。使用 uv 管理官方源码时，推荐 `third_party/index-tts/.venv/Scripts/python.exe`。
- `Speaker WAV`：真实 IndexTTS2 的必要输入，对应 `spk_audio_prompt`。只下载 `models/IndexTTS-2` 但没有安装/配置 `index-tts` repo 时，真实 TTS 仍然不能运行。

这三者需要互相对应：

- repo：`third_party/index-tts`
- python：`third_party/index-tts/.venv/Scripts/python.exe`
- model_dir：`models/IndexTTS-2`
- config：`models/IndexTTS-2/config.yaml`

测试 Python import：

```powershell
python -c "from indextts.infer_v2 import IndexTTS2; print('OK')"
```

使用推荐 uv 环境测试：

```powershell
D:\RT\ue5-virtual-human-bridge\third_party\index-tts\.venv\Scripts\python.exe -c "from indextts.infer_v2 import IndexTTS2; print('OK')"
```

如果这里报 `OffloadedCache`，通常是选错 Python 或 transformers 版本不匹配；先回到 `third_party\index-tts` 执行：

```powershell
uv sync --default-index "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple"
```

## TTS 后端

`--tts-backend` 支持：

- `dryrun`
- `auto`
- `indextts2_local_api`
- `indextts2_local_cli`
- `indextts_legacy`

`dryrun` 永远可用，不依赖 torch、IndexTTS2 或 numpy。

`indextts2_local_api` 使用官方 API：

```python
from indextts.infer_v2 import IndexTTS2
```

初始化时会根据 `inspect.signature` 只传当前版本支持的参数，例如 `cfg_path`、`model_dir`、`use_fp16`、`use_cuda_kernel`、`use_deepspeed`。

`indextts2_local_cli` 会在当前 session 输出目录生成临时 `run_indextts2_wrapper.py`，wrapper 内部 import 官方 API 并调用 `infer`，不会假设官方脚本有固定 CLI。

## 示例

Dry-run：

```powershell
.\scripts\run_ai_morph_tts.ps1 -Text "你好，我是虚拟人助手" -Emotion happy -DryRun -VerboseLog
```

模型检查：

```powershell
.\scripts\run_ai_morph_tts.ps1 -CheckModelOnly -IndexTtsModelDir .\models\IndexTTS-2 -IndexTtsConfig .\models\IndexTTS-2\config.yaml
```

真实 IndexTTS2 API：

```powershell
.\scripts\run_ai_morph_tts.ps1 `
  -Text "你好，我是虚拟人助手" `
  -Emotion happy `
  -TtsBackend indextts2_local_api `
  -IndexTtsRepo .\third_party\index-tts `
  -IndexTtsModelDir .\models\IndexTTS-2 `
  -IndexTtsConfig .\models\IndexTTS-2\config.yaml `
  -DefaultSpeakerWav path\to\speaker.wav `
  -VerboseLog
```

播放到 UE：

```powershell
.\scripts\play_ai_morph_frames_tcp.ps1 -Frames output\ai_sessions\demo_happy\frames.jsonl -Host 127.0.0.1 -Port 7001 -UseTimestamp
```

## 日志和 manifest

Python stdout 使用结构化标签：

- `[AI_RUNTIME][INFO]`
- `[AI_RUNTIME][WARN]`
- `[AI_RUNTIME][ERROR]`
- `[AI_RUNTIME][STAGE]`
- `[AI_RUNTIME][DONE]`

每次生成会写：

- `manifest.json`
- `ai_runtime_events.jsonl`
- 失败时尽量写 `manifest_failed.json`

manifest 包含 `stage_timings`、`command_line`、`python_executable`、`numpy_available`、`tts_backend_resolved`、`model_check`、`morph_only`、`skeleton_motion` 等字段。

运行 manifest 还会记录离线相关字段，如 `offline_mode`、`hf_hub_offline`、`transformers_offline`、`hf_hub_disable_xet`、`hf_home`，便于追踪模型加载来源。

## 推荐调试顺序

1. Check Model。
2. DryRun。
3. SkipTts with existing wav。
4. IndexTTS2 真实后端。
5. GUI Generate。
6. GUI Play to UE。

UE 侧要求：

- VHReceiver 插件启用。
- `ExpressionReceiverComponent` 已挂到角色。
- Listen Port 与脚本 Port 一致，当前默认 7001。
- `StartListening` 已启动。
- `TargetMesh` 已解析正确。

当前阶段不直接做骨骼动画。

真实 TTS 常见失败：

- import failed：当前 Python 不能 import `indextts.infer_v2`。
- OffloadedCache import failure：当前 Python 不是 IndexTTS2 正确环境，或 transformers 版本和官方源码不匹配。
- config missing：`models/IndexTTS-2/config.yaml` 不存在，模型可能未下载完成。
- speaker wav missing：真实 IndexTTS2 缺少 `spk_audio_prompt`。
- CUDA / torch error：IndexTTS2 依赖或显卡环境问题。
- output wav not generated：推理返回后 `generated.wav` 不存在或为空。
## Local Offline IndexTTS2 Notes

Real IndexTTS2 should run from local files in this project:

- repo: `third_party/index-tts`
- python: `third_party/index-tts/.venv/Scripts/python.exe`
- model_dir: `models/IndexTTS-2`
- config: `models/IndexTTS-2/config.yaml`
- w2v-bert: `models/IndexTTS-2/w2v-bert-2.0`
- MaskGCT: `models/IndexTTS-2/MaskGCT`

The AI Runtime sets `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`, and `HF_HUB_DISABLE_XET=1` by default for the child process. Model loading should not access Hugging Face. If `huggingface.co` or `cas-bridge.xethub.hf.co` appears in logs, run:

```powershell
.\scripts\download_indextts2_local_deps.ps1
.\scripts\patch_indextts2_local_paths.ps1
```

`app.py` runs with unbuffered output from the GUI and flushes stage / heartbeat logs. The GUI parses `[AI_RUNTIME][STAGE]` and `[AI_RUNTIME][HEARTBEAT]` lines to keep progress live during long model load and inference.
