# Skeleton Tree Runtime

`skeleton_tree` JSON 来源于 UE 蓝图角色的 `SkeletalMeshComponent` 导出。当前 YYB Miku 导出骨骼数量为 573，文件包含平铺 `bones` 列表和 `tree` 层级结构。

当前关键骨骼包括：

- `miku条纹袜_arm`
- `全ての親`
- `センター`
- `グルーブ`
- `腰`
- `上半身`
- `上半身2`
- `首`
- `頭`
- `目_R`
- `目_L`
- `舌１` / `舌２` / `舌３`

运行：

```powershell
.\scripts\analyze_skeleton_tree.ps1
```

会生成 `configs/skeleton_semantic_map_yyb_miku.json`。该文件只用于后续 V2M、Control Rig、AnimBP 或 ModifyBone 研究，记录 root、waist、spine、chest、neck、head、eye、tongue 等语义槽的候选骨骼、置信度和 warning。

当前阶段不直接控制骨骼。AI runtime 只生成 Morph Target 权重：

- `drive_skeleton_by_default=false`
- `morph_only=true`
- `skeleton_motion=false`

GUI AI Runtime Tab 会读取 skeleton tree 和 skeleton semantic map，并把可用性、bone_count 等参考信息写入 manifest。当前阶段 GUI 也不会驱动骨骼。

未来阶段需要在 UE 内通过 AnimBP / Control Rig / ModifyBone 验证安全驱动链路后，再把 V2M 接入。
