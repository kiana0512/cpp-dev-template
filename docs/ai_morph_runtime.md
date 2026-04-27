# AI Morph Runtime

## 总链路

文本输入 -> IndexTTS2 真实 TTS 或 dryrun / existing wav -> `generated.wav` -> 音频 RMS / 文本启发式对齐 -> morph-only `frames.jsonl` -> GUI 或脚本通过 TCP JSON line 播放到 UE VHReceiver -> UE 端驱动 Morph Target。

当前不改 UE 插件协议，不改 TCP JSON line 协议，不做 WebSocket，不做完整 V2M。

## Morph-only 状态

所有 AI Runtime 输出保持：

- `meta.morph_only=true`
- `meta.skeleton_motion=false`

当前不生成真实骨骼控制，不驱动 Control Rig / AnimBP / ModifyBone。

## TTS Backend

推荐真实后端：`indextts2_local_cli`。它会在 session 输出目录生成 wrapper，在 import IndexTTS2 前设置环境变量，比 GUI 直接走 local API 更稳定。

保留模式：`dryrun`、`skip-tts / existing wav`、`indextts2_local_cli`、高级调试用 `indextts2_local_api` 和 `auto`。`indextts_legacy` 标记为 deprecated，不进入默认尝试链路。

## 模型目录

- IndexTTS2 repo：`third_party/index-tts`
- Python：`third_party/index-tts/.venv/Scripts/python.exe`
- Model dir：`models/IndexTTS-2`
- Config：`models/IndexTTS-2/config.yaml`
- w2v-bert：`models/IndexTTS-2/w2v-bert-2.0`
- MaskGCT：`models/IndexTTS-2/MaskGCT`
- campplus：`models/IndexTTS-2/campplus`
- BigVGAN：`models/IndexTTS-2/BigVGAN`

`models/` 是本地权重目录，不提交 Git。

## 依赖固定

`sentencepiece==0.1.99` 必须固定。当前 Windows/Anaconda-based uv venv 中，`sentencepiece==0.2.1` 加载 `bpe.model` 会 0xC0000005 native crash；`0.1.99` 已验证 `sp.Load(...)` 返回 `True` 且 `pieces=12000`。

`Text Normalizer` 默认 `fallback`。Windows 下 `wetext/kaldifst` 仍会 `_kaldifst DLL load failed`，fallback 只是当前 demo 跑通方案，不等价完整文本规范化。不要删除 wetext/kaldifst 功能。

BigVGAN 必须是 80-band。IndexTTS2 当前 s2mel 输出 80-channel mel；100-band BigVGAN 会报 `expected input to have 100 channels, but got 80 channels instead`。

## 命令示例

Dryrun：

```powershell
.\scripts\run_ai_morph_tts.ps1 -Text "你好，我是虚拟人助手" -Emotion happy -DryRun -VerboseLog
```

Existing wav：

```powershell
.\scripts\run_ai_morph_tts.ps1 -SkipTts -Wav .\wav\鹿乃-心做し.wav -Text "你好，我是虚拟人助手" -Emotion happy -VerboseLog
```

Real IndexTTS2：

```powershell
.\scripts\run_ai_morph_tts.ps1 `
  -Text "你好，我是初音未来虚拟人助手，很高兴见到你。" `
  -Emotion happy `
  -TtsBackend indextts2_local_cli `
  -PythonExe "D:\RT\ue5-virtual-human-bridge\third_party\index-tts\.venv\Scripts\python.exe" `
  -IndexTtsRepo "D:\RT\ue5-virtual-human-bridge\third_party\index-tts" `
  -IndexTtsModelDir "D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2" `
  -IndexTtsConfig "D:\RT\ue5-virtual-human-bridge\models\IndexTTS-2\config.yaml" `
  -DefaultSpeakerWav "D:\RT\ue5-virtual-human-bridge\wav\鹿乃-心做し.wav" `
  -EmotionPrompt "开心、自然、语速适中" `
  -VerboseLog
```

播放到 UE：

```powershell
.\scripts\play_ai_morph_frames_tcp.ps1 `
  -Frames .\output\ai_sessions\gui_latest\frames.jsonl `
  -Host 127.0.0.1 `
  -Port 7001 `
  -UseTimestamp
```

## Manifest

成功写 `manifest.json`，失败写 `manifest_failed.json`。

失败 manifest 保留关键字段：`failed_stage`、`error_message`、`selected_backend`、`text_normalizer`、`sentencepiece_version`、`python_executable`、`model_dir`、`config`、`speaker_wav`。较长诊断信息放在 `diagnostics` 子对象中。