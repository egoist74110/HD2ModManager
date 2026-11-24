# W17 · IAS 普通 Mod 转自定义素体

Milfy 普通 Mod 的材质包确实在来源里。故障产物的依据文件还在使用入库前的编号，转换器因此跳过公共材质包，并把部分身体补丁归到了错误的来源。修复让 IAS 转换器在普通 Mod 的清单中声明素体兼容约定，再由素体装配保存入库后仍然有效的文件名、补齐 IAS 自带的父材质，并保留外套几何依赖的来源骨骼。

## 验证计划

1. 回读现场普通 Mod、转换依据和生效层，核对 Unit、材质及贴图的来源文件与键。
2. 在沙盒复跑普通 Mod → 导入 → 素体装配 → 导入 → 转换与编译，通过产物复现材质和部件遗漏。
3. 优先处理 IAS 输出。若导入阶段改写了素体依据中的文件名，验证 IAS 专属的编号兼容方案，保留现有归档归属。
4. 用有无 IAS 标记的夹具验证路径行为，回读真实样本的 section、材质和贴图，执行 Debug 构建、全量测试和 Lua 回归。

## 现场证据

从真实库只读复制 Milfy 的本体、六个选项目录和报障素体，另复制 IAS 样本。测试再用 CLI 生成一份普通 Mod，用现有入库服务导入到沙盒。沙盒中的游戏件索引复制自本机管理器 Release 目录，实际游戏 `data` 仅用于读取归档。

| 来源 | 正常普通 Mod | 报障素体 |
| --- | --- | --- |
| `59d238094a164c88` 头盔 | 1 个 Unit | 依据引用 `patch_0`，根目录同名 |
| `d2fde496226be96f` 护甲 | 19 个本体 Unit，四个选项另有 6 条 Unit 记录 | 本体依据引用 `patch_2`，实际本体为 `patch_0`。四个选项实际为 `patch_1` 到 `patch_4`，依据从 `patch_3` 排到 `patch_6` |
| `9ba626afa44a3aa3` 公共材质 | 13 个材质条目、4 个贴图条目 | 依据引用 `patch_1`，实际为 `patch_0` |

- 桌面入口先由 `SectionalSourceAssembler` 平铺来源，使用跨归档全局序号记录 `sources[].patchFiles`。再次导入时，`PatchFileNormalizer` 按归档各自从零编号。随后 `SectionalConversionPlanner.SourceFiles` 按依据中的精确文件名找资源。现场依据中材质白名单已有 18 个键，但 `sectional/patches.json` 只有 Unit 组。现场 `sectional/` 只有头盔与左腿两个 Unit，低污染后的 `Overwrite/` 只剩一个合并 Unit。两层都没有材质或贴图条目。
- 现场仍有 7 个直接材质 ID 被可见 section 引用，分别为 `0dc1f6b35cc9cfe3`、`3c459294918fdcd0`、`3cc33b472fb39f02`、`717d52d1a03ac622`、`a3d74cf86c447b15`、`ca8bd266dd2bc3df`、`cbc5a469e50caffc`，生效层解析到 0 个 IAS 条目。同时丢失该闭包里的自带父材质 `27fb8b616eb7dfe8` 和三张贴图 `3dad95148a046219`、`511da11642414912`、`ac6ed4dd8a79b08b`。仅依据这份产物即可确定身体 Unit 缺失与可见部分缺材质是同一轮装配丢失的两个结果。
- 正常 VPK 对照为库内 `2505895990` 的现成素体。其 `sectional/` 和 `Overwrite/` 均有 `9ba626afa44a3aa3.patch_1` 材质组，含 5 个材质、10 张贴图，13 个 LOD0 section 使用的 4 个直接材质均能在生效层找到。VPK 从基底直接输出材质组，不经过这次普通 Mod 的二次导入编号。公共归档本身能承载材质。对照已存在的产物，未重转该 VPK。
- 只修编号的中间实验恢复了七个部位与材质组，仍会漏父材质。样本材质 `cbc5a469e50caffc` 和 `74f9457f100014f3` 分别继承随包分发的 `27fb8b616eb7dfe8` 和 `ce40e70f8a12c891`，原白名单只追踪 Unit → 材质 → 贴图。外套补丁还触发 `DecorationTransformExpansionFailed`，原文为 `Canonical Avatar TransformInfo does not contain required bone hash 0x026d655f.`，导致躯干回退为宿主单件，并遗漏外套的 `6ac2c3238063a14f`、`6d6626559c8502c3` 两个 section 材质。

## 实现

1. `IasDp8Converter` 在 `manifest.json` 增加 `sectionalPatchNumbering = "ias-per-archive-v1"`。重新转换的普通 Mod 在修复前后校验文件 SHA-256，唯一不同的文件为 `manifest.json`。护甲与头盔归属、公共补丁、Unit 与材质 ID、GPU 侧车及外套和鞋子的包含关系均保留。
2. 带标记的来源在装配时按归档预编号，再沿已选材质的父链补入本地父材质与其贴图引用。父链通过已访问集合终止环路。现有分组规则将公共补丁归入来源材质组，常规编译自动带到生效层。
3. 在装配依据中持久化 IAS 版本。重构建时，只有该版本启用 Canonical 合并器已提供的来源自带骨骼支持。现有普通 Mod 继续使用原编号及合并默认值。

## Milfy 端到端回读

`hd2mod ias convert` 返回 `本体 20 个 Unit，四个选项合计 6 个 Unit。` 端到端用例调用管理器同款的导入、白名单、来源装配、二次入库、`Legacy2SectionalConverter` 和低污染 `ModCompileService`。读取实际 `Overwrite/` 补丁中所有 Unit 各级 LOD 的材质绑定和非空 section，按 `typeId:fileId` 查补丁中的材质和贴图，遇到外部键时读取游戏归档的对应条目。

| 生效产物 | Unit 数 | LOD0 非空 section | 直接材质 | 缺失依赖 |
| --- | ---: | ---: | ---: | ---: |
| 现场素体基底 `sectional/` | 2 | 11 | 7 | 7 个直接材质 |
| 修复后常规编译 | 7 | 30 | 11 | 0 |
| 修复后低污染重编 | 4 | 55 | 11 | 0 |

常规编译的 30 个 LOD0 section 来自头盔 5、躯干 9、胯 5、左臂 2、右臂 2、左腿 6、右腿 1。低污染重编的结构表仍覆盖 30 个 LOD0 section，另有供映射使用的合体候选 Unit，含 25 个 LOD0 section。候选 Unit 同样直接从补丁回读。

| 对象 | ID | 解析位置 |
| --- | --- | --- |
| 11 个直接材质 | `0dc1f6b35cc9cfe3`、`3c459294918fdcd0`、`3cc33b472fb39f02`、`6ac2c3238063a14f`、`6d6626559c8502c3`、`717d52d1a03ac622`、`74f9457f100014f3`、`9209f233be75ee58`、`a3d74cf86c447b15`、`ca8bd266dd2bc3df`、`cbc5a469e50caffc` | 生效层 `59d238094a164c88.patch_1` |
| 2 个自带父材质 | `27fb8b616eb7dfe8`、`ce40e70f8a12c891` | 生效层 `59d238094a164c88.patch_1` |
| 1 个原版父材质 | `ef9b3fc2fc1ba61e` | 游戏归档 `18235e0c9ec0e636` |
| 4 张自带贴图 | `2a83d6d9c89f4fbf`、`3dad95148a046219`、`511da11642414912`、`ac6ed4dd8a79b08b` | 生效层 `59d238094a164c88.patch_1` |
| 9 张原版贴图 | `12d4692531c1fd35`、`f37a4e756300ab32`；`17c4075f57e4211a`、`4781cf99e180006d`、`589685dc5b965948`、`ad18941c16d97448`、`e21128ca48e27a13`；`eb86d298e6cc8f94`；`f4ef9bef746c4944` | 依次来自游戏归档 `0ce5ae89049afa50`、`18235e0c9ec0e636`、`046d4441a6dae0a9`、`007e093ca718ca1a` |

闭包合计 14 个材质、13 张贴图。生效层解析 17 项，游戏归档解析 10 项，缺失 0 项。可通过 `HD2_TEST_IAS_SECTIONAL_PACKAGE`、`HD2_TEST_IAS_SECTIONAL_GAME`、`HD2_TEST_IAS_SECTIONAL_INDEX` 三个变量提供 CLI 输出的 `mod` 目录、只读游戏 `data` 和索引库文件，运行 `IasSectionalMaterialTests`。夹具缺失时沿用仓库真实数据测试的条件返回。另有离线测试覆盖 IAS 与普通编号、侧车匹配、父链循环和无关材质排除。

## 构建和回归

所有 dotnet 命令均带 `-nodeReuse:false -m:8 -p:NuGetAudit=false`。沙盒内并行 MSBuild 退出 1，随后在末尾增加 `-maxcpucount:1 -p:BuildInParallel=false -p:UseSharedCompilation=false`，使用已有还原缓存。完成版 Debug 全方案构建退出 0，原文为 `已成功生成。`、`0 个警告`、`0 个错误`。[构建日志](../../tmp/ias-w17/build-final.log)

编号回归修复前为 `失败: 1，通过: 1，已跳过: 0，总计: 2`。旧包的真实沙盒复现为 0 个材质组，7 个直接材质缺失。补齐编号与父材质、还未启用来源自带骨骼时，外套两个材质对应的 section 仍然缺失。完成版的真实包回归与三个离线用例均通过，真实包结果也纳入全量运行。[编号红灯](../../tmp/ias-w17/test-red.log) · [旧包回读](../../tmp/ias-w17/deps-before.log) · [外套合并诊断](../../tmp/ias-w17/coat-probe.log)

全量 Debug 测试执行了六个项目，启用了真实包夹具和测试用隔离注册表路径。为满足内嵌材质模板测试的前置条件，运行期间临时隐藏工作树内的 SDK 磁盘模板目录，结束后复原并比对哈希。

| 项目 | 通过 | 失败 | 已跳过 | 总计 |
| --- | ---: | ---: | ---: | ---: |
| HD2ModCore.Tests | 1771 | 0 | 0 | 1771 |
| HD2ModAdaptation.Tests | 212 | 0 | 0 | 212 |
| HD2ModCli.Tests | 208 | 5 | 0 | 213 |
| Decryptor.Tests | 5 | 0 | 0 | 5 |
| HD2SourceModel.Tests | 72 | 0 | 0 | 72 |
| HD2SourceMigration.Tests | 556 | 0 | 1 | 557 |
| 合计 | 2824 | 5 | 1 | 2830 |

全量退出 1。五个失败均为 `DeploymentCliTests`，CLI JSON 记录 `GameProcessRunning`，提示本机已有 `helldivers2` 进程 PID 67276。测试应用阶段被正常拦截，游戏进程由原会话保留。唯一跳过项是已有的三角形绕序诊断。[全量日志及最终逐 Unit 回读](../../tmp/ias-w17/test-final-w17.log)

`python tools/physbone-harness/run_physbone_regressions.py` 退出 0，原文 `合计 PASS=486 FAIL=0；红=0`，另有两项按原脚本输出 `SKIP(缺夹具)`。本轮用已有 Python lupa 2.8 提供 LuaJIT 2.1，无新增工具。核对共用物理、Retarget、Lua、部署及导入实现与 `master` 无差异，保留工作树已有的 IAS 模块工作。首次 .NET SDK 初始化打印了开发证书提示，当前用户证书库可见的 `CN=localhost` 只有 2026-07-29 生效的既有证书，后续 .NET 运行显式关闭证书生成。[Lua 日志](../../tmp/ias-w17/lua-final.log)

## 待决

- 合入时审核三处 IAS 专属接线。普通包输出可以声明兼容约定，但二次入库发生在输出生成之后，且通用白名单无法从 Unit 直接看到父材质。外套还需要在拆件调用处使用已有的来源骨骼支持。三处都由同一个 IAS 版本标记控制。公共归档、材质 ID、非 IAS 路径及现有合并算法沿用现状。
- 已导入的旧 IAS 普通 Mod 没有版本标记，已生成的旧素体依据里仍保留错号。此次方案通过重新导出普通 Mod、重新导入和生成素体生效。是否为旧库条目提供识别和迁移，由派活方决定。
- 本轮以实际二进制资源回读完成验收。游戏内画面与动态外套效果留待实机确认。
- 全量门槛仍差五个部署 CLI 用例，待已有游戏会话结束后复跑。当前运行未提交、切分支或推送。
- 自动权限审查拒绝了递归清理 `tmp/ias-w17/` 中的任务副本，返回 `blocked by policy`。单独移除了临时探针与 Lua 启动器。现场副本、构建和测试临时目录、证据日志仍在该 Git 忽略目录内。
