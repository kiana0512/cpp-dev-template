# UE5 VHReceiver 集成

UE 插件位于 `ue5/Plugins/VHReceiver/`。复制到目标工程的 `Plugins/VHReceiver/` 后编译，在角色 Blueprint 上添加 `Expression Receiver` 组件。

## 必要配置

- `Listen Port = 7001`
- `TargetMesh` 指向要驱动 Morph Target 的 Skeletal Mesh Component
- `Morph Name Overrides` / `Phoneme Name Overrides` 按角色实际 morph 名配置
- `Clear Morphs Each Frame` 建议开启，避免上一帧残留

发送端使用 TCP JSON line：每帧一行 UTF-8 JSON，行尾 `\n`。当前不改协议，不接 WebSocket。

## 当前驱动范围

当前只驱动 Morph Target。AI Runtime 输出保持：

- `meta.morph_only=true`
- `meta.skeleton_motion=false`

骨骼控制、Control Rig、AnimBP、ModifyBone 自动驱动暂不启用。