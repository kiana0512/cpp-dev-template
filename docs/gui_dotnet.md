# WPF GUI 使用说明

启动：

```powershell
dotnet run --project gui_dotnet/VhSenderGui.csproj -c Release
```

构建：

```powershell
dotnet build gui_dotnet/VhSenderGui.csproj -c Release
```

## AI Runtime Tab 使用说明

GUI 默认使用 `dryrun`，所以即使 IndexTTS2 模型或 Python 环境还没有配置好，也可以先生成测试 wav 和 Morph 时间线。

AI Runtime 页现在分成两层：

- `Basic`：普通使用只需要这里。包含 Text、Emotion、Backend、Runtime Environment、Speaker WAV、Existing WAV、Emotion Prompt 和常用按钮。
- `Advanced`：默认折叠。包含 Model Dir、Config、Raw Backend、FPS、表情强度、输出路径和实验选项。

默认路径会从仓库根目录自动解析：

- 模型目录：`D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2`
- 模型配置：`D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2\config.yaml`
- 推荐 IndexTTS repo：`D:\RT\ue5-virtual-human-bridge\third_party\index-tts`
- 推荐 IndexTTS2 Python：`D:\RT\ue5-virtual-human-bridge\third_party\index-tts\.venv\Scripts\python.exe`
- 输出目录：`output/ai_sessions/gui_latest`
- UE 默认端口：`7001`

IndexTTS repo 和 model_dir 不是同一个东西：

- repo 是官方 `index-tts` 源码目录或 Python 包目录，用来 import `indextts.infer_v2`。
- model_dir 是 `IndexTTS-2` 权重目录，里面应有 `config.yaml`、`gpt.pth`、`s2mel.pth` 等文件。
- config 是模型目录下的 `config.yaml`。
- python 是实际运行环境。真实 IndexTTS2 推荐使用 repo 里的 uv 环境：`third_party\index-tts\.venv\Scripts\python.exe`。

## Runtime Environment

Basic 区域会直接显示 Python 和 IndexTTS Repo，不再把 Python 环境选择藏在 Advanced 里。

- `Use IndexTTS2 uv Env`：自动填入 `D:\RT\ue5-virtual-human-bridge\third_party\index-tts\.venv\Scripts\python.exe`。
- `Browse Python`：手动选择 Python。真实 IndexTTS2 生成会使用这里选中的 Python 调用 `ai_runtime/app.py`。
- `Open Repo`：打开 `third_party\index-tts`，方便执行 `uv sync` 或检查源码。
- Runtime status 会显示 Python exists、Repo exists、venv exists、import indextts 状态。

不建议用 `D:\anaconda3\envs\llm\python.exe` 跑真实 IndexTTS2，除非你确认该环境的 torch、transformers 和 IndexTTS2 依赖完全一致。遇到 `OffloadedCache` import failure 时，通常就是 GUI 选错 Python 或 transformers 版本不匹配。

`Offline local model only` 默认开启。开启后仅允许本地模型加载，GUI 会为 AI Runtime 子进程设置：

- `HF_HUB_OFFLINE=1`
- `TRANSFORMERS_OFFLINE=1`
- `HF_HUB_DISABLE_XET=1`
- `HF_HOME=<repo>\models\hf_cache`
- `TRANSFORMERS_CACHE=<repo>\models\hf_cache\transformers`
- `HF_HUB_CACHE=<repo>\models\hf_cache\hub`

如果你手动关闭 Offline 模式，GUI 会提示：

- `Online model loading may access Hugging Face and can hang or timeout.`

修复 uv 环境：

```powershell
cd D:\RT\ue5-virtual-human-bridge\third_party\index-tts
uv sync --default-index "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple"
```

如果官方依赖需要 extras：

```powershell
uv sync --all-extras --default-index "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple"
```

## Check Setup

点击 `Check Setup` 会检查：

- model_dir 是否存在。
- config.yaml 是否存在。
- bpe/tokenizer、gpt、s2mel、qwen emotion、wav2vec2bert stats 等常见文件是否能找到。
- 当前 GUI 选中的 Python 是否可执行。
- 使用当前 Python、以 IndexTTS repo 为 cwd、并把 repo 加入 `PYTHONPATH` 后，是否能 import `indextts.infer_v2`。
- `transformers.cache_utils.OffloadedCache` 是否可 import，用来快速发现 transformers 版本不匹配。
- real IndexTTS2 模式下 Speaker WAV 是否存在。

检查只是提前提示，不替代真实 IndexTTS2 加载。如果 `config.yaml` 缺失，通常表示下载未完成，可以重新运行：

```powershell
modelscope download --model IndexTeam/IndexTTS-2 --local_dir .\models\IndexTTS-2
```

## Basic 三种模式

- `dryrun`：不需要模型、不需要 speaker，直接生成测试 wav 和 frames。
- `real IndexTTS2`：真实 TTS，必须选择 Speaker WAV。
- `use existing WAV`：跳过 TTS，用已有 wav 生成 Morph frames。

## Generate AI Frames

1. 打开 `AI Runtime` tab。
2. 保持 `TTS Backend=dryrun`。
3. 输入文本和 emotion。
4. 点击 `Generate AI Frames`。

成功后会生成：

- `generated.wav`
- `frames.jsonl`
- `manifest.json`

AI Manifest 页会显示 frame_count、duration、tts_backend_resolved、morph_only、skeleton_motion 等信息。

## 进度和取消

真实 IndexTTS2 生成可能耗时较长，尤其是第一次加载模型时。AI Runtime 右侧会显示：

- `Status`：Idle / Running / Waiting / Success / Failed / Cancelled。
- `Current stage`：例如 `config_load`、`tts_model_load`、`tts_infer`、`morph_timeline`、`validation`。
- `ProgressBar`：已知阶段显示确定进度；`tts_model_load` 和 `tts_infer` 由于没有精确百分比，会显示 indeterminate。
- `Elapsed`：当前操作耗时。
- `Heartbeat`：最近一次 Python heartbeat 或 GUI still-running 更新时间。
- `UI heartbeat`：运行期间每秒刷新，用于判断 WPF 主线程是否卡住。

如果看到显存上升，并且 `AI Log` 或 `AI Process Output` 持续出现 heartbeat，例如 `Still running stage=tts_infer elapsed=...`，说明模型仍在运行。长时间没有 heartbeat 才更像是真正卡死。

`Stop` 会取消当前 Generate / Check Setup / Playback。Generate 取消时 GUI 会请求杀掉 Python 进程树，尽量释放 Python / torch / CUDA 进程资源。

如果真实生成阶段出现：

- `cas-bridge.xethub.hf.co` 超时

表示模型加载仍在尝试访问 Hugging Face。优先检查：

1. `models\IndexTTS-2` 是否完整；
2. `config.yaml` 是否仍引用远程模型名；
3. `qwen0.6bemo4-merge` 本地文件是否完整；
4. Offline 模式是否保持开启。

## Play AI Frames To UE

1. 在 UE 中启动 VHReceiver，确认 `ExpressionReceiverComponent` 已 `StartListening`。
2. GUI 顶部旧连接区点击 `Connect`，Host 默认 `127.0.0.1`，Port 默认 `7001`。
3. 在 AI Runtime tab 点击 `Play Existing Frames` 或 `Generate + Play`。

播放复用现有 `SenderSession` 单连接发送，不会偷偷创建第二套 TCP 连接。

## 配置真实 IndexTTS2

1. 确保模型下载完成。
2. `IndexTTS Repo` 填官方 `index-tts` 源码目录，例如 `D:\RT\ue5-virtual-human-bridge\third_party\index-tts`。
3. 在该 repo 中执行过 `uv sync` 或 `uv sync --all-extras`，让 `third_party\index-tts\.venv\Scripts\python.exe` 能 import `indextts.infer_v2`。
4. `Model Dir` 填 `D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2`。
5. `Config` 填 `D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2\config.yaml`。
6. 选择一个 `Speaker WAV`，IndexTTS2 真实推理通常需要 `spk_audio_prompt`。
7. Basic backend 选择 `real IndexTTS2`。
8. 点击 `Check Setup`，确认 Import 为 OK。
9. 点击 `Generate AI Frames`。

如果环境不正确，AI Log 和 AI Process Output 会显示 import、模型、speaker wav 或生成 wav 的具体错误，不会让 GUI 崩溃。

如果 Python 写出了不完整或非法的 `manifest_failed.json`，GUI 不再用 JSON 解析错误覆盖真实错误；它会显示 raw manifest 片段，并优先把 stderr 或 `[AI_RUNTIME][ERROR]` 作为 Last error。

推荐使用顺序：

1. `dryrun`
2. `Check Setup`
3. `use existing WAV`
4. `real IndexTTS2`
5. `Play Last` 到 UE

## 日志页

- `AI Log`：GUI 层日志，标签包括 `[AI][INFO]`、`[AI][WARN]`、`[AI][ERROR]`、`[AI][MODEL]`、`[AI][PLAYBACK]`。
- `AI Process Output`：Python stdout/stderr，标签为 `[AI][PROCESS][STDOUT]` 和 `[AI][PROCESS][STDERR]`。
- `AI Manifest`：生成后的 manifest 内容。

AI Log 和 AI Process Output 会批量刷新并限制最大长度，避免真实 TTS 输出较多时拖慢 WPF UI。

常见错误：

- Python 找不到：检查 `Python` 路径，优先使用 `.venv\Scripts\python.exe`。
- `config.yaml` 找不到：模型可能没下载完。
- `indextts.infer_v2` 无法 import：IndexTTS repo 没安装进当前 Python。
- `OffloadedCache` import failure：当前 Python 不是 IndexTTS2 正确环境，或 transformers 版本不匹配。优先切到 `third_party\index-tts\.venv\Scripts\python.exe`。
- speaker wav 为空：真实 IndexTTS2 通常需要参考音色 wav。
- `generated.wav` 不存在：查看 AI Process Output。
- `frames.jsonl` 为空：检查 wav 是否成功生成。
- UE 端口不通：确认 UE 运行、StartListening、端口 7001、防火墙。

当前 AI Runtime 只做 Morph Target，不做骨骼动画。

## Local-only IndexTTS2 update

真实 IndexTTS2 默认使用本地离线模型，`Offline local model only` 默认开启。推荐保持三件套一致：

- Repo: `D:\RT\ue5-virtual-human-bridge\third_party\index-tts`
- Python: `D:\RT\ue5-virtual-human-bridge\third_party\index-tts\.venv\Scripts\python.exe`
- Model Dir: `D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2`

如果日志里出现 `huggingface.co` 或 `cas-bridge.xethub.hf.co`，说明第三方源码或配置仍在尝试远程加载。先运行：

```powershell
.\scripts\download_indextts2_local_deps.ps1
.\scripts\patch_indextts2_local_paths.ps1
```

然后点击 GUI 的 `Check Setup`。期望看到 `Main model OK`、`w2v-bert OK`、`MaskGCT OK`、`Remote refs OK`、`IMPORT_OK`。

真实 IndexTTS2 加载和推理可能较久。GUI 会显示 Current stage、ProgressBar、Elapsed、UI heartbeat 和 Python heartbeat。`tts_model_load` / `tts_infer` 没有精确百分比，会使用不确定进度条。只要 UI heartbeat 和 Python heartbeat 仍在刷新，就表示进程还在运行。

`Stop` 会取消当前任务，并尝试 kill Python process tree，以释放 torch/GPU 子进程。
