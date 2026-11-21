# 布莱默顿自定义骨架与 B-01 替换交接

2026-11-18 · 暂停于方案收敛和离线素材准备，尚未修改管理器生产代码。

## 用户要验证的效果

让布莱默顿保留自定义骨架和比例，穿上指定的 B-01 时直接显示模型，站姿、移动和持枪正常。用户明确的顺序是先看纯补丁直接替换，再考虑接入管理器的护甲映射与低污染。IAS 幻化因用户感受到卡顿而退出这条主线。物理单独比较，观感由用户本人实测。硬链接只是部署补丁文件的方式。第一轮基线应只有目标护甲补丁，关闭自定义物理。

## 已确认的边界

- **普通补丁和软映射。** 普通补丁可以将 Unit 写到 B-01 原资源 ID，游戏直接加载。现有软映射用 `Application.set_resource_override` 更换资源指向，低污染规划负责选承载件并生成显隐规则。现有逐节点挂接按骨名建立联系。两种现有机制都没有把 Avatar 动作重定向到不同绑定姿态和骨长的求解器。像老 Mod 一样沿用潜兵骨架，可以使用已经成熟的原版动画链。 [软映射](../../HD2RuntimeArmorOverride/src/armor_override_runtime.lua) · [逐节点挂接](../../HD2RuntimeArmorOverride/src/player_armor_instances.lua)
- **官方 IAS 1.6.3。** 布莱默顿 VPK 转换后，用户实测官方 IAS 能显示、换装和活动，也确认加我们的 PhysBone 探针后能摆动和换装。`WithIasPhys` 目前有离线生成验收。已有 IAS MainMod 放的是独立资源 ID，官方 IAS 运行时负责求解动作及显隐。官方包中的 `direct_models` 指元数据直接引用模型 ID，实际驱动仍会走 IAS 的场景运行时。现有工具链里未找到一个关闭 IAS 幻化后仍能靠普通补丁保住同一独立骨架动画的模式。 [生成器](../../tmp/ias-lab/upstream/ias-1.6.3/Generator/runtime/companion/metadata_generation.py) · [游戏内姿态驱动](../../tmp/ias-lab/upstream/ias-1.6.3/Generator/runtime/companion/copy_stage.lua)
- **IAS-Plus 是另一个实验。** 纯 Lua 原型经历过上船崩溃、倒挂、拉伸及移动闪现。0.1.5 的实机日志在第 1559 帧记录 `Clone pose readback failed at node 5`，保护路径随后恢复原甲并停用替换。扩展后的 Lua 5.1 模拟覆盖全部 11 个映射，也复现了故障回退。实机的节点身份、期望和实际变换未采集，根因未定。暂停原型试包，保留历史记录。 [可行性报告](ias-integration-feasibility-20261118.md) · [模拟回归](../../tmp/ias-lab/ias-plus-proto/dist/ias_plus_verification_report.json)
- **性能。** 用户反馈 IAS 幻化卡顿。性能表仍为 0 条有效实机采样，无法量化开销或归因。即使管理器支持导入和安装 IAS，运行时的动画求解开销仍存在。IAS 兼容最多是安装便利性，不应作为低开销替换已经实现的依据。 [性能汇总](../../tmp/ias-lab/benchmark/results/SUMMARY.md)

## 纯补丁实验停在哪里

- 已用本地游戏原版归档和布莱默顿 VPK 重新导出 B-01 Tactical 第一变体身体 `58e4bd4b2278d15c` 和头盔 `fd109bfeeed36726`。旧 IAS 样本的头盔实际来自 A-9，测试时别混用。新素材包括 10 个身体 Unit、1 个头部 Unit 和 12 个退化点网格候选。1 个位于目标身体归档之外的共用引用 `3c33cf10a26cbb3e` 保留原样。目标原 ID 的补丁仍可能影响共用这些 ID 的其他护甲，低污染需要另验。 [新素材清单](../../tmp/ias-lab/samples/2505895990_bremerton/direct-b01/raw/authored_manifest.json)
- [纯补丁打包草稿](../../tmp/ias-lab/converter/build_bremerton_b01_direct.py) 将以上素材写回原 ID，设计上仅包含资源 Patch。早期运行发现材质引用了当地归档以外的原版模板贴图。为此写了 [原版贴图定位工具](../../tmp/ias-lab/converter/VpkToIasLab/StockTextureLookup.cs)，并且实验 C# 工程以 0 警告、0 错误构建成功。定位尚未运行，`direct-b01/raw/stock_texture_sources.json` 未生成，改后的打包草稿未跑通，也没有可安装的 ZIP 或实机结果。继续打包时同时核对材质的父模板、贴图和 Bones 依赖。
- 实验中间文件在 Git 忽略的 `tmp/ias-lab/`。接手时保留仓库中的其他变更。此次交接只写文档，没有安装、部署或启动游戏。

## 下次恢复时

若仍要验证「保留独立骨架且无 IAS 幻化」，先完成一次隔离的 B-01 纯补丁基线。离线重读最终 ZIP，核对原 Unit ID、骨骼层级、绑定矩阵、网格与材质依赖，再由用户一次性确认模型能否出现、原甲是否被替换、站姿和动作能否保留比例。纯补丁若被原版动画拉回潜兵姿态，同一模型进入现有软映射也会遇到相同的驱动限制。

若优先交付低开销的普通替换和低污染映射，先使用现有原生潜兵骨架转换，离线尽量保留布莱默顿外观。保留外观与保留独立骨长是不同的验收条件。基础动作稳定后，再单独比较物理效果。
