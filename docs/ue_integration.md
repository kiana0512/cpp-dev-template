# 与 UE5 VHReceiver 联调

本仓库的 UE 插件位于：

```text
ue5/Plugins/VHReceiver/
```

将其复制到目标工程的 `Plugins/VHReceiver/` 后，生成工程文件并编译。在角色 Blueprint 上添加 **Expression Receiver** 组件，绑定目标骨骼网格，**ListenPort** 与发送端 **Port**（默认 `7001`）一致。

## 与发送端配合

1. 启动 UE PIE（或打包运行），确保插件已开始监听。
2. 使用 **CLI**（`vh_demo_sender`）或 **WPF GUI**（`gui_dotnet`）向 `127.0.0.1:7001`（或你的 IP）发送 **单行 JSON** 帧。
3. 在发送端 **Raw JSON** 或日志中核对 `sequence_id`、`emotion`、`blendshapes` 是否与 UE Output Log 一致。

组件属性、Morph 默认映射与更多排障说明见根目录 **`README.md`**，发送端操作细节见 **`docs/gui_dotnet.md`**。

## 协议要点（与实现无关的约定）

- 每帧一条 **紧凑 JSON**，行尾 **`\n`**。
- `type` 固定为 `expression_frame`，`version` 为 `1.0`。
- 15 个 `blendshapes` 键名是发送端与插件之间的稳定接口；具体 Morph 名由插件内映射（及可选 Override）解析。
