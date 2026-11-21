# IAS 模型转换 CLI

2026-11-18

交付一个仓库内的独立离线工具。`hd2` 读取潜兵 `.patch_*` 模型目录，沿用龙炎样本的提取路线。`l4d2` 读取 L4D2 `.vpk` 角色模型，沿用布莱默顿样本的比例保留和握持校准。两条路线都生成模型资源主包和官方 IAS 1.6.3 使用的 RigOnly Companion，游戏内的动作和换装由 IAS 驱动。

## 实施顺序

1. 将两条提取路线迁入独立 .NET 工程，引用仓库现有模型和资源库。由一个 CLI 串起提取与 AQSDK 打包，调用方提供游戏数据、IAS 和 AQSDK 路径。
2. 用独立的项目标识发布两份 ZIP。同一次转换先写临时目录，两份 ZIP 都生成后再发布到指定的新目录。CLI 参数和用法写入本文。
3. 以龙炎和布莱默顿为真实输入各走一遍 CLI。使用官方 Generator 验证 Unit、RigOnly Companion 与场景目录，同时核对 CLI 帮助和失败路径。

IAS-Plus、B-01 直接替换、物理探针、部署兼容和性能基准的历史材料留在 `tmp/ias-lab/`，工作到此封存。

## 使用方法

在 HD2ModManager 中打开普通护甲、头盔 Mod，保留原始 VPK 的 L4D2 条目，或根目录仍保留原始 Patch 与 `sectional-source.json` 的 Sectional Mod。在详情页的「操作」菜单选择「生成 IAS 版」，直接保存 `.hd2irpack.zip`，模型主包 ZIP 会存到同一位置。仓库内运行时会自动使用 `tmp/ias-lab/upstream/` 中已有的 IAS 和 AQSDK，也可以把两个依赖目录放在管理器旁的 `tools/ias/` 中。只有缺少某项依赖时才需要手动选择包含 `Generator` 的 IAS 1.6.3 解包目录或包含 `hd2_system` 的 AQSDK 目录。Sectional 条目沿用来源记录里的护甲与头盔选择。若模型是独立的选项条目，请打开该选项自己的详情。转换使用默认的 `FEMALE` 骨架配置。

在仓库根目录运行。已验证环境为 Windows、Python 3.12 和可构建 `net8.0` 项目的 .NET SDK。准备原版潜兵的 `data` 目录、解包后的官方 IAS 1.6.3 和 HD2SDK-AQ-Edition。AQSDK 验证用的是实验室中的 `86b7252` 版本。L4D2 路线使用仓库工具中的 `texconv.exe`，也可以通过 `--texconv` 指定路径。

```powershell
python tools/IasModelCli/ias_model.py hd2 "D:/models/Longyan/Files" --game-data "E:/Helldivers 2/data" --ias "D:/tools/ias-1.6.3" --aqsdk "D:/tools/HD2SDK-AQ-Edition" --out "D:/ias-output/Longyan"
python tools/IasModelCli/ias_model.py l4d2 "D:/models/Bremerton.vpk" --game-data "E:/Helldivers 2/data" --ias "D:/tools/ias-1.6.3" --aqsdk "D:/tools/HD2SDK-AQ-Edition" --out "D:/ias-output/Bremerton"
```

- `hd2` 使用一套已解包的身体或头盔模型，读取目录里的 `*.patch_N`、`.gpu_resources` 和 `.stream`。多选项 Mod 应传入单个选项的资源目录。转换器依据已有 Unit ID 为身体和头盔各匹配一个原版归档。
- `l4d2` 默认沿用已验证样本的身体和头盔模板 `58e4bd4b2278d15c` 和 `a856edff49cfdd95`，可以用 `--body-archive` 和 `--helmet-archive` 指定其他 AQSDK 支持的归档。多模型 VPK 可用 `--model-stem` 指定内部模型路径。素材引用原版 L4D2 材质时，传入包含 `pak01_dir.vpk` 的 `--l4d2-root`。
- 两条路线可指定 `--name` 和 `--rig-gender FEMALE|MALE`。每次成功都会打印模型 UUID。重建同一模型时可用 `--id` 复用该 UUID。
- `--out` 指向一个尚不存在的目录。成功后包含 `IAS_MainMod.zip` 和 `IAS_Companion.hd2irpack.zip`。主包由 Mod 管理器导入。配套包在 IAS 的「Mod 管理」中通过「选择 ZIP」和「导入配套 ZIP」导入。

## 验收结果

- 龙炎 AR479939 的潜兵补丁和布莱默顿 2505895990 的 L4D2 VPK 均通过官方 IAS 1.6.3 Generator 离线验收，各生成 11 个骨架配置和 2 个装配配置。
- 已验证已有输出目录会被拒绝，原 ZIP 哈希保持一致。传入错误类型的归档时，进程返回错误码，未发布产物且清理了临时目录。
- 仓库和独立转换工程的 Debug 构建均为 0 警告、0 错误。仓库全量测试 2575 项通过、1 项跳过。本轮验收止于离线生成与回读。
- 管理器的 WPF 操作菜单已验证普通护甲显示「生成 IAS 版」、IAS 私有模型隐藏该动作。管理器转换服务生成的胧嫣两份 ZIP 通过官方 Generator 回读。单文件发布输出包含两份 IAS 打包脚本。
- Shinano Sectional 条目保留的 CE-27 身体和头盔通过管理器转换服务及官方 Generator 回读，共生成 13 个骨架配置。已验证有原始 Patch 时菜单显示、仅剩来源清单时菜单隐藏。
