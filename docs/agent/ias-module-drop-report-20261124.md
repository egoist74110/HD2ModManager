# W11 IAS 整窗拖入与压缩包导入

IAS 页现在接管整个窗口的文件拖入。真实样本识别出 1 个主包和 1 个配套包，角色名为 **Milfy发光外套 SR-64**。Debug 构建 0 警告、0 错误。全量 .NET 测试 2782 通过、0 失败、1 跳过，Lua 回归 `PASS=486 FAIL=0`。

## 实现

- `MainWindow` 在 `WorkspaceMode.Ias` 时将拖放交给 IAS 页。卡片显示整窗拖动的高亮和提示。未安装、游戏不匹配、缺少前置、未加入当前配置或忙碌时显示对应状态。其他页面沿用原有的 `ProcessImportQueueAsync` 路径。
- 拖动时只看文件入口，必要时读至多 8 字节的包头，同一组路径复用预判。放下后异步识别并显示当前文件和进度。ZIP、RAR、7z 用随管理器分发的 7-Zip 解压，格式依据文件签名。能穿过目录包装并识别嵌套包，最多展开三层归档，说明 TXT 和图片可随包分发。
- 有补丁主体的是主包，有 `mod_group.json` 的是配套包。成对输入直接进入 IAS 添加事务，配套包里的角色名用于显示。有配套包时也支持名称不带 IAS 标记的独立主包。明确命名为 `IAS_MainMod` 的输入缺少配套包会显示已有的中文计数错误。只有补丁、无 IAS 标记的输入按普通 Mod 进入转换确认。普通 Mod 和求生 VPK 在用户确认后沿用现有 IAS 转换入口。一次拖入的 IAS 主包或配套包多于一个时显示同一条计数错误。
- 解包使用 worker 同样的短临时根和路径长度检查。添加事务使用已展开的输入，保留原始归档和配套包；暂存随流程结束清理。ZIP 继续校验条目路径、重复条目、链接和解压大小。测试后剩余 IAS 短暂存目录为 0。

改动留在 `feat/ias-module` 的指定工作树供审核。物理、Lua 部署、通用 7-Zip 解压器以及 SourceMigration 转换与打包代码均未在 W11 中改动。工作树在 W11 开始前已有其他任务的改动。

## 验证

### 真实样本

从 Downloads 复制 `milfy发光外套等3个文件.rar` 到 `tmp/ias-w11/`。随包的 7-Zip 列表输出 `Type = zip`。临时测试调用 `Classify`、包内容识别和 `IasCharacterPackage.Read`，并用读出的 `mod_group.json` 单独核对角色名。结果原文如下：

```text
Classify=IasPackage; main=1; companion=1; name=Milfy发光外套 SR-64; patches=3
```

随包 7-Zip 支持读取 RAR，能力列表不含 RAR 写入。按任务允许的替代方案，单测用真实 7z 格式覆盖非 ZIP 解压路径。其余测试覆盖改名为 `.rar` 的 ZIP、目录包装、附带说明和图片、同包主包和配套包、通用名称的嵌套包、嵌套层数、缺配套包、多组错误、普通 Patch/VPK 识别、IAS 页接管及拒绝提示、转换确认和直接添加。删除真实样本的副本和临时探针后，永久 IAS 定向测试为 `失败: 0，通过: 64，已跳过: 0，总计: 64`。

### 构建与回归

- Debug 解决方案构建退出码 0，原文为 `已成功生成。`、`0 个警告`、`0 个错误`。所有 dotnet 命令均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。沙盒拒绝 MSBuild 子节点的管道连接，成功命令额外带 `-maxcpucount:1`，还原使用本机已有 NuGet 包缓存。按父进程、进程名和启动时间清理了本次失败尝试残留的 400 个 MSBuild 子进程，复查为 0。
- 首轮全量测试唯一失败是已有的 `Hd2MaterialTemplateTests.EmbeddedTemplatesAreUsedWhenNoSdkDirectoryExists`。测试从工作树内的临时目录向上找到了 SDK 模板，期望值为 `embedded`。全量复测期间把模板目录临时移入同一工作树的 W11 沙盒，测试结束后恢复，10 个模板的 SHA-256 逐一匹配。Debug 全量复测退出码 0，无测试过滤器。

| 测试程序集 | 通过 | 失败 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 1724 | 0 | 0 | 1724 |
| HD2ModAdaptation.Tests | 212 | 0 | 0 | 212 |
| HD2ModCli.Tests | 213 | 0 | 0 | 213 |
| Decryptor.Tests | 5 | 0 | 0 | 5 |
| HD2SourceModel.Tests | 72 | 0 | 0 | 72 |
| HD2SourceMigration.Tests | 556 | 0 | 1 | 557 |
| 合计 | 2782 | 0 | 1 | 2783 |

唯一跳过项为测试自带的三角形绕序诊断 `ReportHowManyTrianglesDisagreeWithTheirVertexNormals`。

- `python tools/physbone-harness/run_physbone_regressions.py` 退出码 0，原文为 `合计 PASS=486 FAIL=0；红=0`。两个已知外部夹具用例由脚本标为 `SKIP(缺夹具)`。使用本机已有的 Lupa 2.8 所带 LuaJIT 2.1，经临时命令入口运行。没有安装开发工具，临时入口已删除。
- `git diff --check` 通过。样本副本、临时探针和 Lua 入口已删除。自动审批拦截了整个 `tmp/ias-w11/` 的清理，目录中保留本轮构建和测试日志及生成的临时文件。真实 Downloads 样本保持原位。

## 待决

- 实际 WPF 整窗手势和真实部署端到端由派活方在沙盒外复跑。本轮验证范围为接管判断、提示状态、自动测试和真实文件识别，未启动管理器、游戏或 IAS 图形客户端。
