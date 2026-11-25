# W34 · VPK 转 IAS 头发物理

## 结论与证据等级

- **确定，生成器把 Source 的连续角向阻尼折算得过大。** 现场 VPK 里 `tf1`–`tf21` 的 yaw/pitch 阻尼为 7。旧转换写入 `7 × 0.08 = 0.56`，含义是每个 60 Hz 固定步都衰减 56% 的速度，等价于约 49.26/s，约为源值的 7 倍。按仓库中反向换算 `IasPhysicsMigration.cs:169–175` 使用的同一约定，正向应为 `-expm1(-c × fixed_dt)`，这次实包解码得到 0.110118。只改这一量，官方原生核心回放下的最大末端位移从 **14.97 mm 增至 36.25 mm**，最大带权链根转角变化从 **4.84° 增至 15.14°**。
- **很可能，过阻尼让这个角色的头发在游戏里看起来很僵。** 旧包已经能在离线官方核心执行 90/90 次物理步，13 个末端都有非零位移，带权骨也存在转动。实机所述「完全不动」尚未复现，因而无法把全部现象归因于阻尼。旧刚度 10 在对照的 4–12 量级内，步长有效。HD2PHY1 没有独立的质量字段，Source 的 `TipMass = 200` 不能按 IAS 质量为零来解释。
- **确定，链名和缺末端骨均未触发离线拒绝。** 原链名 `helmet_chain_*` 的旧包可加载并求解。A、B 每根关节的离线回放结果一致，B 额外验证把链名改成首骨名的实机表现。三个有实机证据的官方头盔对照都用固定 driver，现场转换包用 `root_swing` 保留带权首骨。该模式通过 SDK、Generator、原生核心与节点索引，实机场景写回的差异仍待验证。

## 样本与完整解码

开始排查时现场角色已从当前 `sources` 和部署清单移除。由 10:59:52 卸载前事务 `ias/backup/aca3817d43d84f579ff50a72cc3aa471/external-20261125-105952-692-0644d872e3c943daab515ab9f2054930` 的 before 哈希回读 CAS、旧 profiles 和物理节点索引。头盔物理为 7216 B，SHA-256 为 `7651bf4c16b76fe9d8659ff282272f03cc4a4a2a8aa7b0d14af5977ea2442848`。用同一 VPK、项目 UUID、身体归档 `89395c2f8ce3d8a0` 和头盔归档 `2a7c626239a5d6b4` 复打旧版，头盔及 BODY 的物理文件均与现场恢复件逐字节一致。CAS 旧条目只有配套载荷，现场原主包未找回。旧渲染 Unit 的读数使用这份复打包，现场原始主包的字节等同性待确认。

[逐链对照表](../../tmp/w34/chain-comparison.csv) 共 270 条链，覆盖现场、复打旧版、三个官方对照的全部头盔与 BODY 物理、11-18 历史包、A、B。[解码与回放明细](../../tmp/w34/) 以样本名保存 JSON。解码使用仓库 `format_v1.py:789`，Rig 用 AQ SDK `rig_format.py:11–17,204–215,267–277` 的布局回读，Unit 用官方 `archive_payloads.decode` 与 `scene_catalog.py:20–38` 回读。表里的关节明细包括名称、FNV64、Unit 哈希、palette/父槽位、Rig flag、静止坐标、每段长度、关节缩放、限位类型和角、限位轴及参考方向/帧、碰撞半径和覆盖掩码。碰撞体和链链接也完整列出。BODY 的 canonical 关节经 `.hd2shared` 映射到实际 consumer Unit，直接以 canonical Unit ID 在主包搜不到是正常的。

| 样本 | 头盔 Unit | 链 / 关节 | 固定步长 | 刚度 | 每步阻尼 | 重力 Z | 段长 mm | 碰撞体 / 链链接 |
| --- | --- | ---: | ---: | --- | --- | --- | --- | ---: |
| 布莱默顿现场及复打 | `a62a0a5d708bc707` | 13 / 34 | 1/60 s | 10 | 0.56 | -9.81 | 65.40–220.00 | 0 / 0 |
| Eku | `580a2119460c0807`、`d5a30868f335e94f`、`e41a5be7839be823`，每份 | 22 / 99 | 1/60 s | 4、6、8 | 0.25、0.40 | -4、-9.81 | 11.52–304.27 | 6 / 0 |
| CE-07 | `5bb8b939f64a5639` | 13 / 60 | 1/60 s | 4、4.5、12 | 0.27、0.30、0.40 | -0.5、-3 | 27.53–283.32 | 2 / 0 |
| Milfy SR-64 | `1c0c131d96ecbb19` | 41 / 275 | 1/120 s | 4、6、8 | 0.40、0.48、0.60 | -0.25、-4 | 16.16–217.06 | 3 / 0 |
| 布莱默顿 A、B | `a62a0a5d708bc707` | 13 / 34 | 1/60 s | 10 | 0.110118 | -9.81 | 65.40–220.00 | 0 / 0 |

| 现场头盔链尾号 | 父骨 | 摆动骨到末端 | 段长 mm |
| --- | --- | --- | --- |
| 00 | head | tf1 → tf1_HD2PB_Tip | 220.0 |
| 01 | head | tf12 → tf13 → tf14 → tf14_HD2PB_Tip | 84.1、65.4、220.0 |
| 02 | head | tf15 → tf16 → tf17 → tf17_HD2PB_Tip | 84.1、65.4、220.0 |
| 03 | head | tf18 → tf18_HD2PB_Tip | 220.0 |
| 04 | head | tf19 → tf19_HD2PB_Tip | 220.0 |
| 05 | head | tf2 → tf2_HD2PB_Tip | 220.0 |
| 06 | head | tf20 → tf20_HD2PB_Tip | 220.0 |
| 07 | head | tf21 → tf21_HD2PB_Tip | 220.0 |
| 08 | head | tf3 → tf3_HD2PB_Tip | 220.0 |
| 09 | head | tf4 → tf4_HD2PB_Tip | 220.0 |
| 10 | head | tf5 → tf5_HD2PB_Tip | 220.0 |
| 11 | head | tf6 → tf7 → tf8 → tf8_HD2PB_Tip | 138.0、141.4、220.0 |
| 12 | head | tf9 → tf10 → tf11 → tf11_HD2PB_Tip | 138.0、141.4、220.0 |

现场头盔 13 条链均为零风力、组掩码 1、碰撞掩码 `0x40000000`、根摆动启用、局部弯曲关闭。链根的 radius、刚度缩放、阻尼缩放均为 0，限位为 NONE。其余关节半径 5 mm、刚度缩放 1–1.3333、阻尼缩放 1，使用 0.87266 rad 圆锥和默认 `ANIMATED_SEGMENT` 参考。Eku 同时用 NONE 和窄圆锥，CE-07 头盔全为 NONE，Milfy 同时用 NONE 和圆锥。旧 BODY 为 10 链 / 22 关节，A、B 都保留该域物理、共享映射、预览、rig 和全部主包补丁载荷。

11-18 的 `WithIasPhys` 历史包同样为 13 链、34 关节、阻尼 0.56，头盔走另一归档，离线回放收到了同样的 14.97 mm。历史 Generator PASS 及本轮回放只能证明离线可用。用户此前看到的晃动来自自家 PhysBone 探针。

## IAS 1.6.4 模拟前提与逐项判定

下表中的 `companion/` 指官方 `expanded/Generator/runtime/companion/`，`hd2_system/` 指本机 AQ SDK 86b7252。

| 前提 | 文件与行 | 现场旧包 | 修复 A / 变体 B |
| --- | --- | --- | --- |
| 可加载的配套、归档与选择归属 | `companion/library_catalog.py:89–145` 取启用的 CAS，校验 SHA 和冲突；`declared_assembly.py:22–106` 核对 isolation、归档、appearance 和 Unit 路由；`metadata_generation.py:27–52` 生成域与场景目录 | 卸载前索引为启用。头盔 isolation 由 `39db8fb22c278559` 路由到 `a62a0a5d708bc707`，且登记 `rig_profile` | 官方 worker 导入预检返回同一身体及头盔归档，Generator 各生成 11 profiles、2 assemblies、2 selections |
| 解析通过，物理 Unit 与 Rig Profile、slot、拓扑相符 | `companion/core.py:60–121` 原生 open、catalog、profile；`hd2_system/physics_compiler.py:68–85,266–304` 以 profile 生成物理骨表与文件；`format_v1.py:106–113,746–789` 记录 fingerprint 并校验文件尺寸、版本、CRC、步长等 | 两域都能被原生核心加载，重算 fingerprint 一致。复打旧头盔 34/34 骨命中正确 Unit 槽位和名称哈希 | 34/34 逐骨命中，13 根末端均在 rigbin 和 Unit 骨表，关节父子关系连贯、各段长度非零。渲染读回为 21 根带正权重摆动骨、13 根无权重末端，41,376 条受影响顶点记录 |
| 物理节点可写且由物理路径接管 | `companion/library_catalog.py:26–86` 按 Unit ID、链 flags 和槽位建索引，排除 `anchor_only`；`copy_stage.lua:416–447` 将物理槽位对应到复制 Unit，链接路径避开物理写入 | 旧部署的 profile 可用且 active，`physics-nodes.txt` 有该 Unit，34/34 槽位入索引。所有物理骨均为独立骨，无 `anchor_only` | 34/34 入索引，BODY 的 22 个 canonical 物理关节均可在复打主包 consumer Unit 中找到映射 |
| 实际装备进入正确域，复制 Unit 的布局与资源身份可验证 | `companion/scene_catalog.py:20–38,40–71` 验证 Unit 图或编译 direct schema；`copy_stage.lua:350–375,396–457` 核对资源与可用 profile，注册、校验并完成复制；`native_scene.lua:18–58` 提交域、资源和场景 token；`parallel_physics.py:57–84` 的 companion 传输要求帧只含同一声明域 | 旧部署的离线登记满足，实际现场复制 Unit 身份与写回未取证 | Generator direct scene metadata、Unit 骨表及权重离线校验通过，实际场景注册待实机 |
| 活体角色开关、连续帧、求解结果成功写回 | `companion/runtime_entry.lua:51–55` 缺 settings 时默认关闭；`native_scene.lua:60–85` 只对本机活体或允许队友传 true，隐藏观察路径不写；`copy_stage.lua:596–624` 处理 ready、retired 与写回回执；`physics_settings.py:15–31` 将策略传入核心，`physics_preferences.py:36–47` 校验偏好；`ArmorBase/files/physics.json:1–8` 是全局开关，无逐链登记 | 用户确认实机开关开启且同环境官方包活动。原生数值入口能求解旧头盔，实机回执未取证 | OFF 时 0 步、位移只有浮点噪声；ON 时两包各 90 步、1 次初始化、0 拒绝。实机写骨待验证 |

`.hd2phys` 链名用于格式排序、唯一性、链连接引用，见 `format_v1.py:506–520,803–832`。索引使用 Unit ID、槽位与 flags。原生场景服务 DLL 内部没有可引用的 C++ 源码行，因而这里只把可见的加载结果、步计数、位移、Lua 的 native 接口和 Generator 声明当作证据。没有证据要求另加逐链 `physics.json` 登记。官方 shared BODY 清单绑定 canonical 载荷哈希和 consumer 映射，见 `hd2_system/shared_physics.py:210–264`。

## 修复和实机包

- `VpkExtractor.cs:839` 从 flexible jigglebone 导出 yaw/pitch 较大的角向阻尼，保留源值 0。有平移底座弹簧时，底座阻尼不参与此换算。
- `package_model.py:230–235,376–384` 将链上有角向数据的源系数取均值，按实际编译固定步长计算每步衰减。缺角向数据时由 HD2PHY1 默认阻尼 0.25 接管。默认步长 1/60 s 来自 AQ SDK `physics_packaging.py:383`，同一数值传入换算和编译。调整范围限于 VPK 头盔物理。普通 Mod→IAS、共享 BODY、原有用户改动均保留。
- 生产新增写死项只有官方默认步长的命名常量及非负边界。原有项目通用参数仍有刚度换算、局部弯曲阈值与系数、重力、碰撞掩码与半径、角度兜底与范围、末端长度范围；本轮没有新增角色名、骨名、归档、刚度或阻尼特例。从源 VPK 复打测试包时有意沿用现场归档、UUID 和性别，便于逐字节对照。

| 包 | IAS 页要拖入的文件 | 用途 |
| --- | --- | --- |
| A · 阻尼修复 | [主包](../../tmp/w34/A-damping-fix/IAS_MainMod.zip) + [配套包](../../tmp/w34/A-damping-fix/IAS_Companion.hd2irpack.zip) | 先测这套。修正每帧衰减过强，观察跑动、急停和转头时头发有没有甩动。 |
| B · 只改头盔链名 | [主包](../../tmp/w34/B-chain-name-only/IAS_MainMod.zip) + [配套包](../../tmp/w34/B-chain-name-only/IAS_Companion.hd2irpack.zip) | 和 A 一样的物理参数，链名改成第一根骨名。用于确认游戏里是否受命名影响。 |

**测前先在 IAS 页卸载已有的布莱默顿角色。** A、B 和旧角色占用同一底子。把 A 目录的两份 ZIP 一起拖进 IAS 页，安装后确认物理开着。在第三人称下跑几步、急停、左右快转，重点看发梢是否甩出去再回弹。测完卸载 A，再用同样动作测 B。两套每次只装一套。

包内成员比较通过。A 与旧复打的主包补丁字节相同，配套里头盔参数只变阻尼。B 与 A 的主包各成员字节相同，配套里头盔的数据差异仅为链名及其排序。官方工具重生成作者声明和 group 清单，内含时间、载荷哈希，因此 ZIP 整体哈希不同。

## 验收与待决

| 要求 | 状态 | 实测 |
| --- | --- | --- |
| 解码和对照 | done | 270 链，现场 CAS 对照、复打 Unit、三个官方包、历史包和两套实机包，逐骨 fingerprint、哈希、槽位、父子和末端核对 |
| 运行时前提和归因 | partial | 安装前提及离线求解已验证，实机「完全不动」的完整因果和 scene DLL 写回待定 |
| VPK 修复和回归 | done | 60/120 Hz 序列化回读、角向与底座阻尼区分、零值、缺省和 BODY 兼容。旧代码在 60 Hz 合成夹具解码为 62.98932/s，期望 7/s；新版 Python `Ran 5 tests`、`OK`（含 3 个既有测试） |
| 实机包与官方离线验收 | done | A、B 配套由官方导入器接受，`library → assembly → icons → package → complete`，各自 `PASS`、11 profiles、2 assemblies、2 selections；当前 Debug CLI 另起一次端到端转换产出与 A 相同的模型及运行时载荷 |
| 官方离线求解 | done | 用 `Core.frame` 喂 90 帧 60 Hz 的源静止骨架加 8 cm 往复平移，比较模型坐标。旧包 13/13 末端移动，OFF 0 步，ON 90 步、0 拒绝。A 与 B 同为 90 步、0 拒绝，13/13 末端移动，位移与角度如上 |
| Debug 和全量回归 | done | `已成功生成。`、`0 个警告`、`0 个错误`。.NET 六个测试项目合计 `失败: 0，通过: 2937，已跳过: 1，总计: 2938`；Lua `合计 PASS=486 FAIL=0；红=0`，两条外部夹具按原规则跳过 |

| .NET 测试项目 | 失败 | 通过 | 跳过 |
| --- | ---: | ---: | ---: |
| HD2ModCore.Tests | 0 | 1840 | 0 |
| HD2ModAdaptation.Tests | 0 | 212 | 0 |
| HD2ModCli.Tests | 0 | 213 | 0 |
| Decryptor.Tests | 0 | 5 | 0 |
| HD2SourceModel.Tests | 0 | 72 | 0 |
| HD2SourceMigration.Tests | 0 | 595 | 1 |

默认解决方案并行构建在沙盒无诊断退出 1，在线 NuGet 也无法连接。用本机包缓存完成还原，随后用临时串行目标完成整个 Debug 解决方案构建。六个测试项目分别用 `dotnet test <项目> -c Debug --no-build -nodeReuse:false -m:8 -p:NuGetAudit=false` 运行全量。内嵌模板回归用例执行期间临时隔离 10 个工作树内的受跟踪开发模板，结束时逐个恢复并确认 SHA-256。Lua 用本机已有的 Lupa LuaJIT 2.1 运行原仓库 harness，日志路径指向工作树内沙盒。没有安装工具、启动游戏或图形客户端。首次 .NET CLI 欢迎输出含开发证书提示，只读检查当前用户证书存储只见 2026-07-29 的既有 ASP.NET 开发证书，后续命令显式禁用自动生成。

- **实机根因确认。** 若 A 有晃动，记录旧版与 A 的同场景录屏；若 A、B 都仍像铁丝，优先取原生场景的装备资源、schema、step/observe、attempted/verified 回执以及当时的物理偏好。链名与固定 driver 的进一步实机差异仍待验证。
- **BODY 阻尼迁移。** 共用旧换算在 BODY 依然可见。此次实机反馈只覆盖头发，BODY 改变留待独立验收。
- **历史现场主包。** 精确的旧配套与旧部署节点记录已恢复。复打主包可核对绑定、权重和离线物理，旧现场主包本身未找回。

日志、逐关节读数、权重读回及两份 Generator RESULT 保存在 `tmp/w34`。自动审批以 `blocked by policy` 拒绝递归清理命令，单独指定临时 CLI 副本路径重试也被拦截。复打中间包、离线沙盒、临时工具和缓存因此留在该忽略目录，实机只使用上表的 A、B 两对 ZIP。

没有写游戏目录、真实 Mod 库或 Release IAS 数据。Git 保留在原分支和工作区，未提交或推送。
