# Skeleton Tree Runtime

`skeleton_tree` JSON 来源于 UE 蓝图角色的 `SkeletalMeshComponent` 导出。当前 YYB Miku 导出骨骼数量为 573，包含平铺 `bones` 列表和 `tree` 层级结构。

运行分析：

```powershell
.\scripts\analyze_skeleton_tree.ps1
```

分析结果用于生成或更新 `configs/skeleton_semantic_map_yyb_miku.json`，记录 root、waist、spine、chest、neck、head、eye、tongue 等语义槽候选。

当前阶段 skeleton tree 只作为参考数据，不驱动骨骼。AI Runtime 和 GUI 仍只输出 morph-only timeline：

- `drive_skeleton_by_default=false`
- `morph_only=true`
- `skeleton_motion=false`

后续如要做 V2M / Control Rig / AnimBP，需要先在 UE 内验证安全的骨骼驱动链路，再接入 runtime。