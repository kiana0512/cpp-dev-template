# 虚拟人表情驱动 — UE 插件联调工程

本仓库提供端到端链路：**文本/音频参数 → ExpressionFrame（JSON）→ TCP 文本行 → UE5 Runtime 插件驱动 Morph**。  

定位：**轻量发送端 + 协议参考实现 + UE 插件源码**，便于本地联调、JSON 对照、预设回归。

---

## 项目组成

| 模块 | 说明 |
|------|------|
| **C++ 核心** | `expression_mapper`、`packet_validator`、`json_protocol`、`sender_service`、`transport_client` |
| **CLI** | `vh_demo_sender` — 脚本/自动化 |
| **WPF GUI** | `gui_dotnet/VhSenderGui` — **推荐调试入口**，仅需 **.NET 8 SDK** |
| **UE 插件** | `ue5/Plugins/VHReceiver` |

---

## 目录结构

```text
cpp-dev-template/
├── CMakeLists.txt              # vh_protocol、vh_demo_sender、vh_tests
├── configs/                    # presets、样例帧、blendshape 参考
├── gui_dotnet/                 # .NET 8 WPF 发送端（主 GUI）
├── include/vh/
├── src/
├── tests/
├── scripts/
├── outputs/                    # 默认 frames.jsonl、logs
├── docs/
│   ├── gui_dotnet.md           # ← WPF 详细使用说明（必读）
│   └── ue_integration.md
└── ue5/Plugins/VHReceiver/
```

---

## WPF 图形界面：快速开始

**环境**：Windows + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

在**仓库根目录**（存在 `configs/presets.json`）执行：

```powershell
dotnet build gui_dotnet/VhSenderGui.csproj -c Release
dotnet run --project gui_dotnet/VhSenderGui.csproj -c Release
```

**典型联调流程**：

1. UE PIE 运行，插件监听端口（默认 `7001`）。
2. 打开 WPF：`Host = 127.0.0.1`，`Port = 7001`，模式 `tcp`。
3. 点击 **测试 TCP** 确认端口可达，再 **连接**。
4. 在左侧选预设（或改右侧表单），**发送一次** 或 **开始循环**。
5. 在 **原始 JSON** 页签与 UE Output Log 对照帧内容。

**完整界面说明、每个按钮含义、`presets.json` 格式、日志导出、file/stdout 模式、发布与排障** → 见 **`docs/gui_dotnet.md`**。

---

## 构建 C++（CLI / 测试）

前提：CMake ≥ 3.20，C++17；`nlohmann_json` 可通过 FetchContent 获取。

```powershell
cmake -B cmake-build-debug -G Ninja -DCMAKE_BUILD_TYPE=Debug
cmake --build cmake-build-debug
```

产物示例：`vh_demo_sender.exe`、`vh_tests.exe`（若开启测试）。

使用 **GUI 不要求** 编译 C++。

---

## CLI：`vh_demo_sender` 示例

```powershell
.\cmake-build-debug\vh_demo_sender.exe --text "你好" --emotion happy --mode tcp --host 127.0.0.1 --port 7001
.\cmake-build-debug\vh_demo_sender.exe --config configs/sample_frame.json --mode tcp --port 7001
.\cmake-build-debug\vh_demo_sender.exe --demo --loop --fps 24 --mode tcp --port 7001
```

更多参数见 `src/main.cpp` 或 `--help`。

---

## 协议摘要

- **类型**：`type = "expression_frame"`，`version = "1.0"`。
- **传输**：每帧 **一行紧凑 JSON**，UTF-8，行尾 **`\n`**。
- **音频**：`phoneme_hint` ∈ `rest,a,i,u,e,o`；`rms`、`emotion.confidence` ∈ [0,1]。
- **Blendshapes**：15 个固定 ARKit 键名，与 UE 插件约定一致。
- **头部**：`head_pose` pitch/yaw/roll ∈ [-90,90]。

逻辑管道：`MapperInput` → `buildFrame` → 校验 → 序列化 → 发送（C++ CLI 与 C# GUI 语义对齐）。

---

## 配置文件

| 文件 | 用途 |
|------|------|
| `configs/presets.json` | WPF 预设列表；改后可点界面「重新加载 presets.json」 |
| `configs/sample_frame.json` | 合法整帧样例；WPF 可一键发送 |
| `configs/blendshape_map_yyb_miku.json` | 参考；UE 侧默认映射在插件内 |

---

## UE5 联调

插件路径：`ue5/Plugins/VHReceiver/`。复制到工程 `Plugins/` 后编译，在角色上添加 **Expression Receiver**，绑定网格，`ListenPort` 与发送端一致。

步骤与属性说明见 **`docs/ue_integration.md`** 与 **`docs/gui_dotnet.md`** 中的 TCP 流程。

---

## 排障（摘录）

| 现象 | 方向 |
|------|------|
| TCP 连不上 | PIE 是否运行、端口、防火墙、WPF「测试 TCP」 |
| 无表情 | `TargetMesh`、Morph 覆盖配置 |
| 有表情无口型 | `phoneme` 是否长期 `rest`、`rms` 过小 |

---

## 后续扩展

- 接入 TTS / 唇形驱动仍走同一 JSON 管道  
- 传输层可演进 WebSocket / gRPC（需 UE 配合）  
- 扩展 `ExpressionFrame` 字段时保持版本与兼容策略  

---

## 文档索引

| 文档 | 内容 |
|------|------|
| **`docs/gui_dotnet.md`** | **WPF 安装、界面、按钮、预设、日志、发布、FAQ** |
| `docs/ue_integration.md` | 插件路径与联调要点 |
