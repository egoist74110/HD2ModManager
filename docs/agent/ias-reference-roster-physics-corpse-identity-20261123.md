# IAS 1.6.4 做法备忘：护甲名单、物理绑定、每帧开销、身份、尸体定格（2026-11-23）

目的：把 IAS 的做法一次查清楚并记下来，以后不用再翻源码或反编译。
来源：`D:\gitCode\HD2ModManager\tmp\ias-lab\upstream\ias-1.6.4\Generator\runtime\lua\lua\runtime.lua`（下文 `R:行号`）。
本文由 5 个 Haiku 只读侦察子代理逐行查证，再由 Claude 汇总。带「不确定」的条目要实读确认后才能照搬。
已有的相关文档：`ias-runtime-lifecycle-map-20261123.md`（生命周期对照表）。本文是对它的补充，不重复其中内容。

用户硬规则：先 100% 复刻 IAS 的效果，开销不超过 IAS；然后和我们自己的 Lua 合成一套，不走两套；最后才减配。不自己出方案，IAS 没有对应做法的，报告给用户决定。

---

## 1. 护甲名单（profile 表）从哪来

- 名单只有一份，来自原生 DLL `HD2IndependentRig.addon64` 的导出：`profile_count / profile_id / profile_hash / profile_flags / profile_available / profile_target_*`（R:590–610）。
- `load_bridge(frame)`（R:562）启动时读一次，失败的话每 300 帧重试一次（R:564）。成功后逐个读出，存进 upvalue `profiles`（R:757–844），**之后不再按帧重建**。
- 每个 profile 的字段（R:823–839）：`index, id(u64), hash(16 位小写 hex), flags, active, observe_only, female, nodes, anchor_nodes, target_sources, target_flags, authored, domain, target_count`。
  - `observe_only = floor(flags/2)%2==1`（R:764）
  - `gender = floor(flags/4)%4`（R:766）。`gender==3` 时整个表作废（R:767）；`gender~=0 and active` 的才放进表（R:771）。
  - `authored`：任何一个 target 的 flag 满足 `floor(f/1024)%2==1`（R:798–800）。
- **一件护甲就是一个 profile。重定向（独立骨）和物理用的是同一份名单，物理没有单独的名单。** 只有物理、没有重定向的件，在 Lua 里没有单独的分支，都进同一个 `bindings`；原生会不会接受，取决于 `bind_unit` 的返回值（不确定）。

### 和我们的差距（这一节是 E12e 卡住的原因）
- 我们的 `roster_hashes` 只读 `options.independentPieces`（独立骨件）。物理件来自 `loadCarriers`，记录只有 `{rig, path}`，**没有护甲哈希**，靠 `Compat.select_rig` 比对骨名来匹配。
- 所以物理件永远进不了 Roster，`physics_sync` 挂上 0 个会话。
- 照 IAS 的做法：转换器（`IasDp8Converter`）要产出一份**含全部转换件的哈希表**（相当于 IAS 的 profile 表：有独立骨的、有物理的，都算），运行时的 Roster 只按这份表枚举。物理会话再按"哈希 → 物理容器"绑定。

## 2. 哈希 → `units_by_resource` 的键，以及发现过程

- 键：`IdString64.from_hex(profile.hash)`，在发现循环里每次现取（R:1542），然后 `World.units_by_resource(world, key)`（R:1543）。
- 对返回的**每个** unit 都调 `Unit.debug_name`（小写），名字里含这个 hash 才算（R:1553–1554）；不含的只记日志（R:1560–1562）。
- 句柄、匹配结果都**不跨帧缓存**（R:1417–1428），只存每个 hash 的命中计数和时间戳（R:1643、1649）。
- 负缓存：只有查询返回**空表**时，才把这个 hash 冷却 0.25 s（R:1546）；查询失败不冷却（R:1545）。avatar signature 一变就重置（R:1507–1510）。
- 一个 hash 本轮该不该查（R:1537–1538）：`need_scan`，或 `population_changed`，或没有 signature，或上次命中数 > 0，或负缓存已过期。
- 全量 `World.units` 兜底扫描（R:1576，对每个 unit 调 debug_name，R:1610；名字匹配结果缓存上限 256，R:1611–1614）只在 `need_scan` 时跑：
  - 非战斗：`frame - last >= 60`（R:1516）；或世界 unit 数 ≤512 且数量变了（R:1515）。
  - 战斗（gameplay）：只在 `actor_changed` 或 `now >= next_full_scan` 时跑（R:1523）；扫完后 `next_full_scan = now + 2.0`（R:1644）。
  - 任何一个 profile 的命中数比上次少，就扫一次（R:1572）。
  - `generation_scan` 默认关闭（R:1530）。
- gameplay 的判定（R:6672–6678）：`#scene_sources==0`，并且身份模块给出的 role 是 1 或 2。
- signature 用 `__hd2ir_unit_instance_slot`（R:925–945，映射表 + 定期清理），**不用 `tostring(unit)`**。
- 只扫 active 世界（R:1386–1392：n>0，且有 avatar / scene_sources / fp_graph）。
- 跳过 native_handoff 的 unit（R:1552、1609）。
- 600 帧没扫过的世界记录会被清掉（R:1651–1656）。
- 另一个调用点在 R:6375：只用于 unit 数 ≤512、有 2–32 个 avatar 且互相重叠的世界，不传 signature，所以每个 profile 每次都要查。
- 每次渲染回调另外还有 `discover_armour_markers`（R:1332，经 R:6691 调用）：`ARMOUR_MARKER_IDS`（R:871 起）里每个 marker 查一次 `units_by_resource`，再对结果调 debug_name，没有负缓存（marker 的数量不确定）。

### 开销口径（用户规则：不能超过 IAS）
- 战斗中、15000 个 unit 时，IAS 每 2 s 全扫一次，平均每帧约 15000/120 ≈ **125 次 debug_name**（一次性集中发出，不是平摊）。
- 我们 `perf-combat-test.lua` 的门槛"每帧 < 40"**比 IAS 还严**。按用户规则，门槛应改成"不超过 IAS 同场景的调用数"。

## 3. 归属：哪件护甲属于哪个 avatar

- 探针：`root_probes = observe_only and anchor_nodes or {1, 0}`（R:6702–6704）。**每个探针都要算**，每个 avatar 取最小的 d2（R:6705–6725）。
  - 我们 WIP 在第一个探针命中后就 `break`，探针 0 不再算。这和 IAS 不一样，要改。
- 家族过滤：`PROFILE_REQUIRED_ARMOUR[hash]`（R:1717–1725，都是 `"ex16"`），authored 的件不做这一步过滤（R:6761–6762）。
- 优先级 1：租约 `avatar.carrier_units[unit]` 直接指定归属（R:6766–6777）。
- 优先级 2：在候选里取最近和次近（R:6779–6791）。距离要求 `d2 < 9.0`，也就是 3 m（R:6794）；歧义余量 `BINDING_AMBIGUITY_MARGIN = 0.20`（R:1815）。另外还要 `binding_gate ~= "stable"` 且 `not pose_owner_proven`，否则视为歧义（R:6813–6857）。
- authored 件必须经 `__hd2ir_refine_authored_owner`（R:3093 起）证明归属，否则拒绝（R:6799–6813；阈值 .01/.03，不确定）。
- 对端抑制：`handoff.early(...)` 命中的件不绑定（R:6806–6812）。
- 第一人称门控：`first_person_binding_allowed`（R:1837–1890），`FP_LOCAL_CAMERA_MAX = 0.35`（R:1714）。
- 标记租约：`MARKER_ASSOCIATION_MARGIN = 0.05`（R:889），租约生效逻辑在 R:1031–1067。
- 可见性探针只对 `VISIBILITY_PROBE_PROFILES`（R:1736–1739）生效（R:6731–6758）。
- 每帧开销：每件护甲 1 次 `num_scene_graph_items` + 每个探针 1 次 `world_pose`；每个 avatar 1 次 `num_scene_graph_items` + 1 次 `world_pose(avatar, 1)`。`world_pose` 内部用了几个 getter，不确定（R:397 起）。

## 4. 物理绑定

- `discover_armour_units` 的结果 `armour_units`（R:6680）同时交给 `__hd2ir_prepare_native_snapshots`（R:6684）、非玩家展示（R:6686）、第一人称展示（R:6687），以及主循环（R:6693 起）。
- 主循环把归属成立的件写进 `bindings`（R:6862–6867：`profile, avatar, unit, root_node, root_q, root_p`）。
- 物理：对 `bindings` 里每一项调 `C.bind_unit(binding.profile.id, instance_id, binding.avatar.id, root)`（**R:7004**），每帧一次。每个 avatar 另外调一次 `C.set_avatar_physics_class(...)`（R:6975–6976）。物理状态从 R:7074–7100 读出。
- 原生状态挂在实例槽位上；帧号或时间跳变、传送、布局变化时直接重置，不追赶（见生命周期对照表）。

## 5. 身份（本人 / 队友 / 死亡）——`__hd2ir_physics_identity`（R:4718 起，`update` 在 R:4840）

- `#avatars > 32` 时直接返回（R:4841）。先作废不在场的 unit（R:4851–4852），再按 4 Hz 节流（`next_poll = now + .25`，R:4853）。每轮先把所有人的 role 置 0（R:4855）。
- 会话变了：重置 `known`、states、各游标、远端缓存（`remote_cursors / local_order / remote_orders`），`session_epoch + 1`（R:4861–4870）；会话没了就全部清空（R:4857–4863）。
- 本地拥有的对象：`Net.peer_id()` → `GS.objects_owned_by(session, peer)`，最多 8192 个，遇到非法 id 则 `complete=false`（R:4871–4884），按降序排列（R:4740–4741）。
- 每轮考察的对象（`consider`）：最近 16 个自有 id、自有列表轮询 64 个（`owned_cursor`）、全局低位 id 64 个（`cursor % 4096`）（R:4900–4904）。
- **远端 peer 枚举（我们没有）**，仅在 `#avatars > 1` 时做（R:4909–4951）：合并 `GS.peers` 和 `GS.other_peers`，每个来源最多读 32 个，去掉自己；排序后取前 4 个 owner；每个 owner 调 `objects_owned_by`（上限 8192）；考察前 8 个，再加游标 16 个。
- 未解析、`#avatars > 1` 且 `observed_high >= 4096` 时：在近邻窗口 `[max(4096, H-512), min(2^32-1, H+256)]` 里考察 16 个，扩展游标再考察 16 个（R:4959–4967）。
- `known` 超过 32 个就清空（R:4971）。对象类型检查 `GS.game_object_is_type(session, id, "gl7xl2w")`（R:4889–4890）。读 `position` 和 `health`（R:4973–4983）。
- avatar 状态：`U.alive` 为真（R:4987）。**state 绑定在 `instance = __hd2ir_unit_instance_slot(unit)` 上，world 或 instance 变了就新建 state**（R:4989–4990）。我们只比较 world，复活后可能残留 dead。
- 死亡：对象 health ≤ 0，且已经关联 network，就设 `dead=true; dead_since = dead_since or now`（R:4998）。**全文没有把 dead 清回 false 的代码**，复活靠 instance 变化重建 state。
- 绑定：对 health > 0 的对象，在活体（非 dead 且 `has_animation_state_machine`）里找最近的 avatar（R:4999–5009）。接受条件：距离 ≤ `s.network==id ? 1.0 : 0.05` m，次近与最近相差 > 0.25，并且 `not s.network or s.network==id`（R:5015–5016）。
- **稳定互斥回退（我们没有）**（R:5029–5060）：
  - 条件：`not s.network`、非 dead、有动画状态机、complete、`local_count[world] == 1`（R:5037）。
  - 取最近的 health > 0 对象 best_id；margin = 其他活体 avatar 到它的最小距离 − best。
  - 门槛：对象不是自有、没被别人认领、best ≤ 0.5、次近 − best > 0.5、margin > 0.5（R:5050）。
  - 连续性：同一个 best_id、上一轮也命中（`poll - 1`）、间隔 ≤ 0.75 s 就 hits + 1；`hits >= 4` 时认定 `network = best_id; owner = false; match_method = 'stable-mutual'`，并标记已认领（R:5051–5057）。任何一个条件不满足，hits 清零（R:5058–5059）。认领表先用已有的 network 填好（R:5033–5034）。
- role（R:5024–5075）：`local_count[world]` 统计 owner==true、非 dead、有状态机的 avatar 数。
  - dead → 3
  - `owner==true and local_count==1` → 1
  - `owner==false`，并且（`local_count==1` 或 `local_count==nil and friendly`），并且 health > 0、ownership_complete、对象不是自有、距离 ≤ 1.0 → 2，同时设 `friendly=true`
- 尚未核对：avatar 的 `p` 在 IAS 那边是怎么取点的（我们用 `roster_point(unit, 1)`）。阈值 0.05 / 0.5 / 1.0 m 的含义依赖它。

## 6. 尸体副本定格

由三个模块组成：`__hd2ir_shared_body_lifecycle`（R:5190–5246）、`__hd2ir_corpse_motion`（R:5248–5429）、`__hd2ir_corpse_freeze`（R:5432–6002）。

### 6.1 活着时采集形状
- 入口在驱动写骨时：R:4046、R:4705 调 `freezer.queue_input / observe`（外面包 pcall）。前提是 `__hd2ir_corpse_freeze_active == true`，且不是 preview_update。
- `observe`（R:5602）要求：原生 `corpse_freeze_enabled()==1`；profile 的 gender ∈ {1,2}，authored/active，并且不是 observe_only/pose_only。role 为 1 或 2 才采集。
- 如果骨已经脱开（相邻骨距离 > `max(.12, rest*.4)`，或任一骨离胸部 > 3.5），或者没有动画状态机，就不采集。
- 节奏：`bucket = floor(os.clock()*4)`，即每 0.25 s 一桶，同一个 unit 同一桶只采一次；缓存最多 256 个 unit；超过 30 s 的缓存作废（R:5552、5595）。
- 缓存结构：`live_cache[slot] = {shape, bucket, time, world, native, up, owner, hash, count, offset, joints, core}`。shape 由 `capture()`（R:5525）逐节点调 `world_pose`，换算成相对 hips 的 `{q,p}`；joints 由 target_flags 的第 10 位（1024）决定；`up` 是 hips→head 方向在根局部坐标系里的单位向量。
- 同一个 owner 的所有件都成功，才整组提交（R:5866–5868）。

### 6.2 判定死亡并触发复制
- 死亡时钟：`dead_age(unit) = os.clock() - dead_since`（R:5182）。
- `dead_life`（R:5121）还要求：owner==false、friendly、有 network id、仍在同一个会话、实例槽位没变、`U.alive`，并且 health ≤ 0 或没有动画状态机。network id 被复用时不继承尸体租约（R:5118–5120）。
- 触发（`F.flush`，R:5850 起）：死亡已满 4 s（`age >= 4`），或者骨已经脱开 / 只能用缓存（early）。没有死亡时钟就不复制。
- `prepare`（R:5635）要求：`num_scene_graph_items == count`，`2 <= count <= 256`，`StingrayEntityRoot` 节点等于 offset（0 或 1）；资源用 `U.name_hash(unit)`。

### 6.3 生成副本
- `W.spawn_unit(world, resource, core.p, core.q)`（R:5662），core 是 hips 的世界位姿；累计最多生成 256 个，失败也计数。
- 生成后立刻 `set_unit_visibility(copy, false)`，再检查：name_hash 相同、节点数相同、根节点相同、没有动画状态机、`num_actors == 0`。
- 重建场景图：`W.unlink_unit(world, copy)`；根节点 `scene_graph_link(copy, offset, nil)`，其他节点都挂到根下；然后 `set_local_rotation / set_local_position` 写入（根用 core，其他节点用 shape）；`W.update_unit`；最后回读校验。
- **原件的场景图一点不改**，只用 `set_unit_visibility(original, false)` 把它藏起来。
- 提交顺序：apply → 原生 `freeze_corpse_physics(profile_id, instance_id, avatar_id)`，必须返回 1，否则整组回滚（R:5923）→ 副本设为可见 → 原件隐藏。回滚时：原件恢复可见，`W.destroy_unit` 删掉副本（删不掉就隔离），这个 owner 记入失败名单，每具尸体只失败一次。
- 登记：`clones[copy_slot] = {world, owner, hash, count, root, original, clone, guard, shape, motion_shape, motion_owners, fixed_core}`，槽位写进 `__hd2ir_corpse_slots`，发现阶段会把它排除。

### 6.4 下落与定格（`__hd2ir_corpse_motion`）
- 保护期：`until_time = now + max(0, 4 - age)`（R:5914）；只有走缓存路径时才开始下落模拟。
- 固定步长 1/120 s，最多 480 步（4 s）；`advance` 每次最多追 32 步（R:5387）。
- Verlet 积分：阻尼 .992，重力 9.82（沿 -z），约束迭代 16 次。
  - 质量：hips/chest 4，head 2，其他 1。
  - 半径：hips/chest .065，head .047，其他 .019，都乘以身高。
  - 地面：`min(l_foot.z, r_foot.z) - .025*height`；接触地面时切向阻尼 .12。
- 初速度：活体 core 的位移速度（上限 4 m/s），加上 `right×(p-hips)*1.6`，再加 `forward*.3`。
- 约束：15 条固定长度的身体边，另有 4 条肢段弦长的上下限。
- 每帧 `update_motion`（R:5775）：按初始向量到当前向量的旋转求每个节点的 q，用肩线重算躯干扭转，再相对 `fixed_core` 写回副本。
- 下落结束：`guard = nil; shape = nil; live_cache[original] = nil`，记 `settled-` 日志。

### 6.5 每帧过滤与清理（`F.filter`，R:5954，由 R:6681–6682 调用）
- 有保护期的组调 `finish_guard`（R:5811）。没有下落模拟时，等到 until_time 才收尾：能用活体 settled 姿势的就用（`c.up` 加 `|axis.z| < .7` 判定"真的躺平了"），否则用 `fallen_rotation` 当刚体倒下。
- 每个副本：校验副本和原件都在，且 hash 相同。通过的话，`set(root, fixed_core)` + `W.update_unit`，副本可见、原件隐藏；不通过就隐藏副本、恢复原件。
- `skip[original] = not guard or guard.motion ~= nil`：原件从护甲名单里剔除。
- 正常流程里**没有** `destroy_unit`（只在回滚时用，R:5939）。
- **已查实（第二轮侦察）：IAS 正常情况下从不销毁尸体副本**，也从不清 `clones[...]`。切世界时，`F.filter` 只处理 `r.world == wid` 的副本（R:5965、5973），其他世界的副本只是跳过，不销毁。复活也没有专门处理，owner 找不到时副本只会被跳过或隐藏（R:5993–5994）。副本数量靠 `created < 256` 封顶（R:5663），生成失败也计数。
  - `ias-runtime-lifecycle-map-20261123.md` 写的"清理时 destroy_unit 副本（5936–5940）"指的是**失败回滚**路径，不是正常清理，那份文档写错了。
- `native_handoff_clean`（R:5538–5544）只检查 frozen / clones / pending 是否为空，或是否和 scope 相关；`reseed`（R:6454–6456）要求它返回 true。R:7160–7162 是 `finish` 的 scope 判定。

### 6.8 原生部分（查不到实现）
- `freeze_corpse_physics(profile_id, instance_id, avatar_id)` 的绑定在 R:643，唯一调用点 R:5923，可用性检查在 R:5463。
- 已有反编译材料里**没有它的实现**。查过的有：`tmp/ias-lab/decomp/` 的 report-*.md、`native_bridge/_strings.txt`、`_functions.txt`、bin 符号；e10 的 Ghidra 导出；e5/e8/e9/e11/e12 的产物。工作区里也没有 `HD2NativeHandoffFull_w64_release.dll`。
- 唯一线索是 `decomp/native_bridge/_strings.txt:1887` 的字符串 "Scene corpse freeze"，附近还有 "Scene corpse witness/network"、"Scene retirement state"、"Scene physics"。
- 从调用位置推测，它的作用是**让这具尸体那套件的物理停止演算或写回**，免得原件的物理覆盖副本的姿势，同时省开销。这只是推测，没有代码证据。
- `corpse_freeze_context(frame, state)`：`F.begin` 传 (tick, 0)（R:5547），`control_context` 传 (tick, 1|2)（R:5580）。判据（R:5555–5586）：只有一个 World；场景里同时有 democracy-officer（15678e360612b805）和 ship-display（5b390845a8e3fc29）；Avatar World 里有存活的 ship avatar。不满足时返回 2（锁定）。
- `shared_next_incarnation / forget_shared_avatar / set_avatar_incarnation`（R:630–632，R:6974）：只能看到 Lua 侧怎么用，原生做了什么不知道。
- 白名单（`C:\Users\q1243\Downloads\hd2pbx_api.txt`）：spawn_unit、unlink_unit、update_unit、destroy_unit、name_hash、scene_graph_link/parent、set_local_*、set_unit_visibility、world_pose、has_animation_state_machine 都在里面；`Unit.num_actors` 不在（IAS 本来就把它当可选项，R:5672）。死亡时钟用到的 Network/GameSession 不在白名单，但用户已经同意 IAS 用的 API 可以照用，身份判定也已经在用了。
- 物理类别（`set_avatar_physics_class`，0/1/2/3/4）在原生求解器里改变什么，没有任何文档。我们目前对所有类别一视同仁。

### 6.6 同一个 avatar 的多条命（R:5190–5246）
- `M.observe(avatars, api, evidence)`：对每个 instance_slot 取 `{epoch, world, network, kind}`，其中任一项变了，就调 `api.forget_shared_avatar(id, old.token)`；新出现的用 `api.shared_next_incarnation()` 分配 token。

### 6.7 这一段用到的 API
- World：`spawn_unit, destroy_unit, unlink_unit, update_unit`
- Unit：`alive, name_hash, num_scene_graph_items, has_node, node, scene_graph_link, scene_graph_parent, set_local_rotation, set_local_position, set_unit_visibility, has_animation_state_machine, num_actors`
- GameSession：`in_session, game_object_exists, game_object_field(health|position), game_object_owner`；Network：`game_session`
- 原生（我们没有，需要找 Lua 的对应做法，或者报告用户）：`freeze_corpse_physics, corpse_freeze_enabled, corpse_freeze_context, shared_next_incarnation, forget_shared_avatar, set_avatar_incarnation, shared_body_profile, shared_body_solves`
- 这些 API 大多不在白名单内，用户同意 IAS 在用的可以照用，逐个列出即可。

## 7. 待确认清单（照搬前要实读）
1. `world_pose`（R:397、430 一带）内部用了哪些 getter，用了几次。
2. IAS 里 avatar 的 `p` 怎么取点（身份阈值依赖它）。
3. `__hd2ir_refine_authored_owner`（R:3093）的完整规则。
4. 副本的最终销毁和切世界时的清理（R:6000 以后，R:6456，R:7162）。
5. `ARMOUR_MARKER_IDS` 的数量（R:871）和 marker 每帧的开销。
6. 只有物理、没有独立骨的 profile，原生 `bind_unit` 接不接受。
