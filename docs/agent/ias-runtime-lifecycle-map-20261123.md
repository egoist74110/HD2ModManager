# IAS 1.6.4 运行时生命周期对照表（2026-11-23，E12d 第 1 步，只读、未改代码）

依据：`tmp/ias-lab/upstream/ias-1.6.4/Generator/runtime/lua/lua/runtime.lua`（7833 行）。行号都指这份文件。
代码只学行为，不抄。我们的现状指 `HD2ModManager-ias-e12` 的 `HD2PhysBoneCompat/src/physbone_runtime.lua`（2.1.59）。

## 0. 结论先行

1. **IAS 没有"发现—打分—挂载—退避"这一套。** 它每个渲染回调都从引擎重新枚举（avatar、装甲件），当帧按距离把件绑给 avatar，不保存任何跨帧的 Unit 句柄、不保存"这件属于谁"的结论。死亡、复活、换甲、换场景没有各自的特殊流程，因为下一帧的枚举自然给出新的结果。
2. 我们的 30 秒 T 字，根因就是我们自己发明的这一层：件要经过"物理 discover（总数必须稳定 + 每拍 2 件打分额度 + 600 帧负缓存 + 退避重试）"才能进入 `runtime.avatars`，独立骨又依赖这份名单和 `runtime.avatar` 判归属。IAS 里没有这些环节。
3. **IAS 的崩溃安全不靠"World.units 指针判活"，靠"不跨帧持有句柄"。** 本帧用到的每个句柄都来自本帧的枚举（IAS 注释原话意思："句柄只在当前渲染回调内有效，不放进注册表"）。我们那道每帧拉全量 `World.units` 的闸，是因为我们跨帧持有会话/件/avatar 句柄而补的。
4. **IAS 的身份/死亡判定依赖 `Network`、`GameSession` 命名空间（`game_object_owner`、`health` 等，第 53 行取得）。这两个命名空间不在 `hd2pbx_api.txt` 白名单里**，我们不能用。死亡、本人、队友的判据必须换成白名单内的替代（见 §3.4、§4 的待决项）。
5. IAS 的尸体做法：**不动原件**，生成视觉副本（`World.spawn_unit`），隐藏原件，副本自己做 4 秒布娃娃式下落后冻结。`spawn_unit`、`destroy_unit`、`unlink_unit`、`set_unit_visibility` 都在白名单里。

## 1. 发现频率总表

| 项 | IAS | 我们（2.1.59） |
| --- | --- | --- |
| avatar 枚举 | **每个回调**：`World.units_by_resource(world, AVATAR_RESOURCE)`（行 1344–1360），1 次调用/世界 | 物理 discover 才枚举，间隔 20–60 帧并受总数稳定闸；独立骨另有 30 帧一次的 `findAvatars` |
| 装甲件枚举 | 每个 profile 一次 `units_by_resource(world, 资源哈希)`，**每个回调**，约 19 次调用（行 1500–1560）；仅当"到期"才查：上帧有命中、或否定期 0.25 s 过了 | 全量 `World.units`（1.5 万个）每帧一次 + 独立骨每 15–300 帧再扫一遍 + 物理 discover 全量扫 |
| 全量 `World.units` 扫描 | 兜底，`DISCOVERY_INTERVAL = 60` 帧（行 1421）；**战斗世界**改为"avatar 集合变了，或每 2.0 s"（行 1521–1523）；某 profile 的索引命中数低于上次立即补扫（行 1581） | 每帧（F1） |
| 否定结果缓存 | 仅"索引查询得到空表"才缓存，0.25 s（行 1553）；失败的查询立刻重试 | 按指针缓存 600 帧（可能被新 unit 复用指针） |
| 身份判定轮询 | `physics_identity.update`，`next_poll = now + .25`（4 Hz，行 4747），但**先**清掉本帧不存在的指针状态（"先作废缺席再节流，拒绝快速指针复用"，行 4745–4747） | — |

## 2. 子系统对照

### 2.1 找件：怎么认出哪件属于哪个玩家

**IAS**（行 6690–6830）：
- 件来自上面的资源索引，`debug_name` 含 profile 哈希才算（行 1560–1565）；全量兜底时用哈希表直接匹配名字（行 1590–1620），不逐 profile 比。
- 归属**每帧重算，不保存**：取件的根探针节点（`{1, 0}`，行 6700）世界位置，到本世界每个 avatar 的根位置求距离平方，取最近者 `best` 和次近者 `second`。
- 条件：`best.d2 < 9.0`（3 m 内，行 6780 一带）；`second_distance - best_distance > BINDING_AMBIGUITY_MARGIN` 才绑（不够就记"歧义"，本帧不绑，也不借用别人）；某件已经在本回调被"租约"给某 avatar（`carrier_units`）则优先该 avatar，哪怕尸体更近。
- 尸体也是 avatar（只要 `units_by_resource` 还给得出来），所以同 ID 件挂在尸体身上时，它离尸体最近，不会被新 avatar 抢走。
- 没有"总数必须稳定"、没有"每拍 N 件额度"、没有"距离 5 m 以外的尸体/人台"这类单独分类。

**我们现在**：`discover`（行 ~524）总数不变闸 + 候选 128 上限 + 打分额度 `scoreUnitsPerFrame`；`independent_scan`（行 ~5130）用"件上 ias_ 肩骨 vs 各 avatar 同名源骨，2 m 内且最近"判归属；`tryAttach` 的"跳过 N 个离玩家超过 5 m 的件（尸体/人台）"、"兜底留最近一件"。

**替换（IAS 做法）**：行 1500–1620 的 `discover_armour_units`（资源索引 + 60 帧/2 s 兜底扫描 + 名字哈希匹配）加行 6690–6830 的当帧归属（根探针最近 avatar、`d2 < 9.0`、`BINDING_AMBIGUITY_MARGIN`、租约优先），结果不入库。IAS 无"打分额度"、"总数稳定闸"、"尸体/人台距离分类"，这些在我们这边对应的机制 IAS 无对应做法。

### 2.2 avatar 列表与"本人"

**IAS**：
- 本人/队友由 `physics_identity`（行 4718 起）判定，依据 `Network.peer_id`、`GameSession.objects_owned_by`、`game_object_owner/health/position`，把 avatar 位置与网络对象位置在 5 cm 内、次近 25 cm 以上才认；4 Hz。**这些 API 不在白名单。**
- 与身份无关的"摄像机最近"标记（行 6600–6650，`camera_nearest`）和第一人称判定（根离相机 ≤ 0.35 m 进入、> 1 m 且只剩一个世界才退出，行 6527–6580）用的是 `World.debug_camera_pose` 与根位置，白名单内。

**我们现在**：`runtime.avatars` 由物理 discover 填，`resolve_local_avatar_for_arm` 在有会话时直接取 `runtime.avatars[1]`——死亡后这份名单要等物理重新发现才更新，这是 T 字的直接原因之一。

**替换**：IAS 的"本人/队友/死亡"判定（`physics_identity`，行 4718 起）依赖 `Network`/`GameSession`，白名单没有这两个命名空间——**IAS 对白名单内的 API 没有对应做法，只写事实，不提方案，留给用户决定（§4-1）**。IAS 里不依赖网络、可在白名单内照搬的只有两件：每帧 `units_by_resource(world, AVATAR_RESOURCE)` 枚举 avatar（行 1344–1360），以及 `camera_nearest` 标记（行 6600–6650，仅用于排序/第一人称门控，**IAS 并不拿它判本人**）。

### 2.3 死亡 / 复活 / 尸体

**IAS**：
- 死亡判据：`health <= 0`（网络），或 avatar 已不在枚举里（行 4985–4990、5170 起）；角色 3 = 尸体，`dead_since` 记时。
- 复活：新 avatar 是**新 unit**，状态按 unit 指针建；`update` 开头先清掉"本帧不在枚举里"的状态（行 4745），所以指针复用不会继承旧身份。
- **尸体不动原件**（行 5440 注释："视觉副本，永不重挂、销毁或编辑原生 Avatar/护甲图"）：用 `World.spawn_unit` 在尸体核心位姿生成副本（行 5665），原件 `set_unit_visibility(false)`（行 5926），副本跑 4 秒、120 Hz 的 Verlet 铰接下落（行 5236–5440，15 个点、20 条距离约束 + 4 条折叠约束、16 次迭代、地面平面按最后双脚高度估计），480 步后冻结；清理时 `destroy_unit` 副本并恢复原件可见（行 5936–5940）。
- 死亡全过程件始终有着落（原件被原生动画驱动，副本被 Lua 驱动），不会回绑定姿势。

**我们现在**：avatar 一旦不在 `World.units` 里 → `release_all` 把件解挂并恢复 `originalQ`（绑定姿势）→ 尸体 T 字；`dropAllOnDeath=1` 整组释放物理会话；新 avatar 要等 discover。

**替换**：IAS 对尸体的做法只有一种——视觉副本冻结（`F.begin` 行 5513 起、副本生成行 5665、隐藏原件行 5926、销毁行 5939；铰接下落行 5236–5440）。IAS 没有"保持原件当前姿态"之类的中间做法。是否整套照搬由用户决定（§4-4）。IAS 的死亡判据依赖网络 `health`（行 4985–4990），白名单内无对应，见 §4-1。

### 2.4 换甲

**IAS**：换甲 = 资源集合变了。下一次到期的 profile 索引查询（或 `need_scan`）自然发现新件；旧件不在枚举里了就不再绑定；`__hd2ir_scene_restore.restore_pending`（行 6860 一带）负责把被改过的原件恢复。无"换装"专用流程。预览世界（≤512 个 unit）另有"总数变化立即扫"（行 1519）。

**我们现在**：`reconcile` 补挂、`avatarSetChanged` 每 60 帧校验、`validateFrames`。

**替换（IAS 做法）**：IAS 无换甲专用流程：下一次到期的资源索引查询（行 1500 起）或 `need_scan`（行 1516–1523）自然发现新件；预览世界（≤512 个 unit）总数变化立即扫描（行 1519）；原件恢复由 `__hd2ir_scene_restore.restore_pending`（行 6860 一带）负责。

### 2.5 加载与换场景

**IAS**：
- 世界对象换了 → `update_lifecycle` 开新"世界代"（行 1909–1925）：清失败闩锁、清绑定键。
- 没有 avatar → `update_lifecycle_gap`（行 1983）只记日志，什么都不做。
- Lua 恢复：`__hd2ir_lua_recovery` 在原生请求时清空发现缓存（`clear(discovery_negative_deadlines)` 等，行 6455 一带）。
- 没有"加载空档计时器 / 整组重挂"，因为没有跨帧保存的句柄。

**我们现在**：`loadGapSeconds=0.5` 整组重挂、世界消失整组丢弃、`dropAllOnDeath`。

**替换（IAS 做法）**：世界变化开新世界代（`update_lifecycle`，行 1909–1925）；无 avatar 时只记日志（`update_lifecycle_gap`，行 1983–1990）；`__hd2ir_lua_recovery`（行 6455 一带）按原生请求清空发现缓存。IAS 无"加载空档计时器/整组重挂"。

### 2.6 判活与崩溃安全（债券页快切）

**IAS**：
- 不跨帧持有 Unit/World 句柄。注释（行 5245）："调用方必须提供本渲染回调内新枚举出来的 Unit"；行 6997："帧内序号会让头盔物理状态在发现间隔切换，所以用持久的 unit 实例槽位（弱键）做身份"——槽位只用于识别，**从不通过它去调用引擎**。
- 对本帧枚举出的 avatar 仍然调 `Unit.alive`（行 4980），说明 IAS 认为"同帧枚举出的句柄"可以安全 deref。
- IAS **没有** 对应我们"每帧 `World.units` 指针比较"的措施，也没有看到专门针对债券页快切的代码。**如果我们改成"每帧所有用到的句柄都来自本帧的 `units_by_resource` 枚举"，它与 IAS 同构；但这是否足以避免 2026-11-12 的崩溃，IAS 没有提供证据，只能由实机验证。**

**我们现在**：`refresh_live_units` 每帧全量 `World.units` + 帧戳索引，所有会话/件/avatar 用它判活。

**替换（IAS 做法）**：句柄只来自本回调的枚举（行 5245、6997 注释）；全量 `World.units` 只在 `discover_armour_units` 的兜底节奏（行 1421、1521–1523、1581）。IAS 无专门针对债券页快切的措施，**这是否足以避免 2026-11-12 的崩溃没有 IAS 证据，由用户决定是否采用（§4-2）**。

### 2.7 物理会话的生命周期

**IAS**：原生按"实例槽位 + avatar id"每帧 `bind_unit`（行 6860 一带 `C.bind_unit(profile.id, instance_id, avatar.id, root)`）；原生状态由实例槽位持有；帧号或时间跳变、传送、布局变化原生直接重置，不追赶；共享身体用"化身令牌"（incarnation token）区分同一 avatar 的多条生命（行 5260 `__hd2ir_shared_body_lifecycle`）。Lua 侧没有"会话"对象需要建/销。

**我们现在**：`attach` 一次、`sessions` 跨帧持有句柄、`pruneSessions`、`dropAllOnDeath`、`reconcile`。

**替换（IAS 做法）**：原生按实例槽位每帧 `bind_unit`（行 6860 一带），状态由槽位持有；帧号/时间跳变、传送、布局变化直接重置，不追赶；同一 avatar 多条生命用 `__hd2ir_shared_body_lifecycle`（行 5260）的 incarnation token 区分。Lua 侧没有会话对象。IAS 的这套在原生 DLL 里，**我们的 Lua 物理会话无法逐行照搬，IAS 无对应的 Lua 做法**，具体改法留给用户决定。

### 2.8 挂接时序

**IAS**：不使用 `link_unit` 做骨架挂接；原生把重定向结果按帧写到匹配的件上（`pose_batch`，行 6860 以后），携带件的生成/显隐由 `update_carrier_units`（行 1100–1330）管。第一人称时暂停（`suppress_all_carriers`）。
**我们现在**：独立骨走 `World.link_unit`（E7）。IAS 没有对应物，这一部分不是"照搬"的对象，保留，但"何时挂/何时解"改由每帧枚举驱动：件不在枚举里 → 不碰；avatar 变了 → 解旧挂新（旧 avatar 已消失则不写回绑定）。

## 3. 我们将被删除的机制清单（这些都是我们自己的机制，IAS 里没有对应物）

| 机制 | 位置（2.1.59） | 为什么删 |
| --- | --- | --- |
| discover 总数稳定闸、候选上限、打分额度、预筛、"装填中" | `discover`、`tryAttach` | IAS 无；是 T 字空档的主因 |
| 指针否定缓存（600 帧）、`debug_name` 缓存（180 帧）、帧戳活体索引 | `discover`、`find_weapon`、`refresh_live_units` | 跨帧保存指针结论；IAS 只信本帧枚举 |
| `dropAllOnDeath`、`loadGapSeconds` 整组重挂、`reconcile`、`avatarSetChanged` | `drive` | IAS 无 |
| "跳过 N 个离玩家 5 m 的件（尸体/人台）"、"兜底留最近一件"、"玩家动它不动" | `tryAttach`、`identity` | IAS 用最近 avatar + 3 m + 歧义裕度 |
| 独立骨自己的归属判据（ias_ 肩骨 2 m） | `independent_scan` | 与物理名单分叉；统一到 roster |
| 缺件快扫/退避 | `independent_scan` | 枚举每帧发生，无需调度 |
| `release_all` 死亡时恢复 `originalQ` | `apply_arm_pose_frame` | 造成尸体 T 字 |

## 4. 需要用户拍板的点（IAS 没有对应做法的，只写现状，不给我的方案）

1. **本人 / 队友 / 死亡的判据**：IAS 用 `Network`、`GameSession`（行 53 取得；`physics_identity` 行 4718 起，`game_object_owner`、`health`、`position`）。这两个命名空间不在 `hd2pbx_api.txt` 白名单。**IAS 对白名单内的 API 无对应做法。** 现状：我们靠物理 discover 填的 `runtime.avatars` 和 `resolve_local_avatar_for_arm` 取 `avatars[1]`。
2. **崩溃安全模型**：IAS 靠"句柄只来自本回调枚举"（行 5245、6997），没有"每帧 `World.units` 指针判活"，也没有针对债券页快切的专门措施。是否采用 IAS 这个模型、以及它能否防住 2026-11-12 的崩溃，没有 IAS 证据，要实机验证。
3. **按哈希取装甲件**：IAS 用 `Id64.from_hex(哈希)` 作 `units_by_resource` 的键（行 1553 一带）。`Id64` 不在白名单，我们能否用现有的键形式取到装甲件**未验证**。IAS 的另一条路径是全量扫描 + 名字哈希匹配（行 1590–1620），节奏 60 帧，战斗世界 2 秒（行 1521–1523）。按 IAS 的节奏，新 avatar 的件最长可能要 2 秒才被发现，达不到"1 秒内"，需要你定。
4. **尸体**：IAS 的做法是视觉副本冻结（行 5445–5990 + 5236–5440），需要 `spawn_unit/destroy_unit`（白名单内有）。IAS 没有更轻的做法。是否整套照搬由用户决定。现状：avatar 消失后 `release_all` 把件恢复到 `originalQ`（绑定姿势）。

## 5. 替换计划（等确认后执行，确认前不改任何代码）

按 §2 里每条"替换（IAS 做法）"逐项执行：1) 新增每帧枚举与当帧归属（行 1344–1360、1500–1620、6690–6830）；2) 独立骨改用同一归属；3) 删除 §3 表里的机制；4) §4 的四项由用户决定后再做；5) 回归用例：avatar 死亡→新 avatar，同 ID 的 8 件新件出现，旧件留在尸体上，世界里另有 70 个同类件干扰，要求 60 帧内新件全部被驱动、旧件不被误选；6) 运行时 2.1.60，结果写规划 §20。
