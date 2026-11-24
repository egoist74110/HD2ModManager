# W12 IAS 总开关、角色开关与内置 Lua 提示

工作树为 `D:\gitCode\HD2ModManager-ias-module`，分支为 `feat/ias-module`。改动留在工作区，供派活方审核。实现依据为 [IAS 页面设计第 8、10 节](ias-page-design-20261124.md)。

## 实现

- **总开关按当前配置保存。** 停用时撤下 IAS 托管 data 包和所有已登记角色的主 Mod，保留角色、源文件、Mod 库条目及已安装部件，再部署当前配置。启用时读官方 `LibraryStatus`，加回托管 data 包和官方库中已启用的角色主 Mod。重新加入的条目追加到现有条目之后。已安装的 IAS 更新部件时保留当前配置开关状态。
- **角色开关以官方库为准。** 调用 worker `set-enabled` 后用 `LibraryStatus` 校验状态，再生成并替换同一托管节点的数据。当前配置启用 IAS 时才同步该角色的主 Mod 并部署。总开关关闭时，从 CLI 调整角色仍会更新官方库和托管 data 包，当前配置条目保持原样。保留卸载后的角色时，官方组件还未装回就将状态显示为未知。
- **IAS 页面反映配置和角色状态。** 「启用 IAS」取代「加入当前配置」，更新按钮可以与开关并列，缺少 MDL3 时仍显示前置提示。总开关关闭时显示灰色停用状态，隐藏冲突警告，锁住角色开关。停用角色保持「转为普通 Mod」「卸载」操作，行内显示「已停用」。忙时开关维持点击后的位置，失败后按刷新结果弹回并显示错误。Debug 假服务示例为 `HD2MM_IAS_FAKE=1`、`HD2MM_IAS_FAKE_STATUS=Installed+Physics+Lua+Mapping`，附加 `+Off` 可演示总开关关闭，两种状态均含已停用角色。
- **冲突提示覆盖管理器内置 Lua。** 成功启用 IAS 后，按当时设置显示仅物理、仅内置 Lua 或两项一起的确认框。警告条分别提供单项按钮。确认后复用设置页的保存及部署请求方法，刷新打开的设置页和 IAS 页。内置 Lua 对应管理器 `BuiltInLuaEnabled`。物理/Lua 部署实现、IAS 运行时实现、`HD2SourceMigration/` 和其他页面的行为沿用本轮开始前的代码。
- **CLI 和复跑脚本。** 新增 `ias enable|disable` 和 `ias role enable|disable <id>`，支持 `--manager-root`、`--game-root`、`--json`。`ias list` 的 JSON 增加 `enabled`，文本标注已停用角色。[`tmp/ias-w12/e2e.py`](../../tmp/ias-w12/e2e.py) 复用 W8 游戏夹具和 W7 布莱默顿包，在独立工作树沙盒内核对总开关往返、Milfy 停用、布莱默顿保持启用、两种列表输出及 Milfy 恢复。

## 事务与测试覆盖

两个开关沿用 IAS 事务锁、Mod 库写入租约、快照、配置发布和部署回滚。总开关只发布配置。角色开关在同一事务内执行官方标记、读回、重新生成、Mod 库发布和可选部署。worker、导入或部署失败走原有回滚路径，恢复官方库、运行目录及管理器快照。取消和 worker 超时沿用现有子进程终止及释放逻辑。读取官方角色状态也持有 IAS 锁，以避免和 worker 更新并发，读取暂存目录在结束时清理。

CLI 和桌面入口沿用本地进程及目录权限。角色 ID 先在托管角色表内匹配，再作为 JSON 值传给 worker。操作使用现有路径约束与官方 DLL 校验。

假 worker 测试覆盖总开关关后恢复、只恢复启用角色、停用最后一个角色后的 data 重新生成、总开关关闭时调整角色仍保持配置为空，以及 worker 和部署错误后的逐文件哈希回滚。ViewModel 测试覆盖可见性、忙时禁用、失败弹回、开启后确认、确认和取消、以及警告条的按钮组合。

## 验证

所有 dotnet 命令带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`，另加 `-maxcpucount:1` 适配本机沙盒。Debug 构建关闭共享编译，使用本机现有 NuGet 缓存。未安装开发工具。

```text
已成功生成。
    0 个警告
    0 个错误

已通过! - 失败:     0，通过:  1740，已跳过:     0，总计:  1740，持续时间: 13 s - HD2ModCore.Tests.dll (net10.0)
已通过! - 失败:     0，通过:   212，已跳过:     0，总计:   212，持续时间: 162 ms - HD2ModAdaptation.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   213，已跳过:     0，总计:   213，持续时间: 7 s - HD2ModCli.Tests.dll (net10.0)
已通过! - 失败:     0，通过:     5，已跳过:     0，总计:     5，持续时间: 13 ms - Decryptor.Tests.dll (net8.0)
已通过! - 失败:     0，通过:    72，已跳过:     0，总计:    72，持续时间: 33 ms - HD2SourceModel.Tests.dll (net8.0)
已通过! - 失败:     0，通过:   556，已跳过:     1，总计:   557，持续时间: 4 s - HD2SourceMigration.Tests.dll (net8.0)

合计 PASS=486 FAIL=0；红=0
```

- Debug 全量 .NET 测试合计 **2,798 通过、0 失败、1 跳过**。跳过项是已有三角形绕序诊断。首轮两个 ViewModel 断言仍以旧状态语义构造或判断测试数据，已修正。材质内嵌回归要求临时目录的祖先没有外置材质模板。完整复测时将同一工作树的 10 个 SDK 材质暂存到 `tmp/ias-w12/`，随后恢复，SHA-256 全部一致。
- Lua 回归运行 `python tools/physbone-harness/run_physbone_regressions.py`，使用本机已有 Lupa 2.8 的 LuaJIT 2.1。首轮未将 harness 的 `LOCALAPPDATA` 日志路径重定向，日志断言失败，抽查默认目录中的对应文件未见产出。在子进程环境中把 `LOCALAPPDATA`、`TEMP`、`TMP` 指向工作树沙盒后通过。两个缺外部夹具的用例沿用 harness 的跳过规则，临时解释器入口已删除。
- 对复制的 W8 官方库运行 worker `status`，确认返回 `Library.entries[].id` 和布尔值 `enabled`。复跑脚本通过 Python 语法检查，CLI 帮助列出四个新命令，`git diff --check` 通过。
- 构建及测试输出保存在 [build-final.log](../../tmp/ias-w12/build-final.log)、[test-final.log](../../tmp/ias-w12/test-final.log) 和 [lua-isolated.log](../../tmp/ias-w12/lua-isolated.log)。两个检查过的 worker 临时根中均无 `h2i-*` 遗留。自动审批以 `blocked by policy` 拒绝递归清理本轮沙盒，探针副本、失败夹具和测试缓存仍在 Git 忽略的 `tmp/ias-w12/` 内。

## 待决

- **真实部署复跑。** 在本机沙盒执行 `python tmp/ias-w12/e2e.py` 时，第一步离线安装通过验签和解压，随后官方 worker `prepare-base` 在 `ExternalRuntimeDeployment.ApplyCore → FileSystem.ReplaceFile` 收到 `System.UnauthorizedAccessException: Access to the path is denied.`，CLI 退出码为 2。开关序列尚未跑到，待派活方在沙盒外用同一脚本复跑。 [退出输出](../../tmp/ias-w12/e2e-install.stdout) · [worker 日志](../../tmp/ias-w12/worker-prepare-base.log)
- **WPF 目视验收。** 已编译 XAML 并通过 ViewModel 测试。实际观感、开关点击和设置页联动待在允许启动管理器的环境验收。本轮没有启动游戏、管理器或 IAS 图形客户端。
