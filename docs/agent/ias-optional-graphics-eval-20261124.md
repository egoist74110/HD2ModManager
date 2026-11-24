# IAS 可选画面组件（ReShade / DLSS）可行性评估（2026-11-24）

用户问：IAS 还有滤镜之类功能，能否在管理器里做成可选，让用户自己选。本文件只评估，未实现。

## IAS 1.6.4 里有什么（release.zip 已含，我们安装时已经下载，只是没保留）
| 组件 | 大小(zip) | 内容 | 用途 |
|---|---|---|---|
| ReShade | 16.6 MB | ReShade 6.8（插件支持版）dxgi.dll、ShaderToggler 插件、预设 `HD2IAS-FX.ini`（FakeHDR + Tonemap）、着色器、中文字体、boot 补丁 | 画面滤镜；加载 DLSS 插件；「隐藏披风」(ShaderToggler) |
| DLSSFrameGeneration | 42.8 MB | hd2dlss_fg 插件、nvngx_dlss/dlssg、sm86 后端 | DLSS 插帧（实验，NVIDIA 30/40/50；游戏内抗锯齿用 TAA） |
| DLSSNeuralRendering | 121 MB | hd2dlss5 插件、nvngx_dlssnr | DLSS5 神经渲染（实验；需 DX12、游戏内抗锯齿改 DLSS） |
DLSS 两个二选一，且必须先有 ReShade。

## 官方怎么装（反编译 HD2RuntimeManager.ExternalRuntimeDeployment）
- ReShade 不往游戏 bin 放 dxgi.dll：文件放在 IAS 运行目录 `Runtime\<gameKey>\reshade\`，由 boot 补丁 + MDL3 加载器“晚加载”（ReShade.ini 被改为 EnableLateAttach=1、AddonPath=./addons、Home 键开面板）。
- 写游戏目录的只有 `data/9ba626afa44a3aa3.patch_*`（与护甲同一个 data 包，ExportData 时打成一份）和 `bin/ShaderToggler.ini`。
- API：`ExternalRuntimeDeployment.Prepare(game, packageRoot)`（ReShade）/ `PrepareDlss`、`PrepareReShadeRemoval`、`PrepareDlssRemoval`、`ReadDlssSettings`、`PrepareDlssSettings`，之后同样 `Apply` + `ExportData`。和我们 worker 已在用的护甲部署是同一套事务。
- 若游戏 bin 已有别的 dxgi.dll / ReShade，官方 `ReShadeInstallCheck` 会拒绝覆盖并给出中文说明。

## 结论：能做
- 机制与已跑通的护甲部署相同：worker 加 `reshade on/off`、`dlss none|fg|nr` 几个命令，组件库多保留 ReShade 与 DLSS 两个 zip（来自同一个已验签的 release.zip，不额外下载），开关后重新生成 data 包即可。
- 文件仍来自 IAS 官方发布，管理器不托管。

## 风险 / 需要用户知道的
1. ReShade、DLSS 都是 IAS 作者自己标的“可选 / 实验”，出问题按官方说法卸载 DLSS 或改用 NVIDIA Smooth Motion。
2. 联网游戏里用 ReShade/注入插件，有被反作弊误判的可能；这是 IAS 用户本来就承担的风险，管理器只是代为开关，需要在开启前提示一次。
3. 用户自己装过 ReShade（bin/dxgi.dll）时会冲突，按官方检查直接提示、不覆盖。
4. DLSS 只适用 NVIDIA 30/40/50 系；管理器不做显卡检测，只写在说明里（或者读显卡名做提示，待定）。
5. IAS 客户端里有一个“系统暂时停用，请先完全移除 ReShade”的远程停用提示文案，说明作者可能随时让 ReShade 路线暂停；跟随官方 feed 状态即可。

## 建议的界面（待用户确认后再设计细节）
IAS 页新增第三张卡「画面增强（可选）」，仅 IAS 已安装时可用：
- 「ReShade 滤镜」开关（含官方预设；游戏内按 Home 调整）。
- 「隐藏披风」随 ReShade 一起提供（官方就是 ShaderToggler，游戏内面板里切）。
- 「DLSS」：关 / 插帧 / 神经渲染 三选一，ReShade 关时禁用；每项下面一行小字写清显卡与游戏设置要求。
- 首次打开 ReShade 时弹一次风险说明。
