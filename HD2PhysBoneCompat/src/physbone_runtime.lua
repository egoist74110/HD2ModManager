-- HD2 物理骨骼运行时（生产入口）
--
-- 这是**真正部署给玩家**的那一份。调试探针（tmp/lua-boot/probe.lua）保留在开发侧，
-- 两者共用同一个求解器库 `physbone_compat.lua`，区别只在"发现目标 + 调度"这一层。
--
-- 两种部署形态，同一个文件：
--
--   A. 内置 lua（独占 boot）
--      管理器的 boot 文档 `require` 本模块并调用 `install()`。此时我们**拥有** boot，
--      init/update/render 都是我们的。
--
--   B. 外置 lua 兼容（独立 patch 模块）
--      本文件作为一个普通 Lua 资源投放，由第三方加载器 `require` 或直接执行。
--      此时我们**不拥有**任何东西：`update`/`render` 可能是别人包装过的，
--      也可能被调用多次。所以：
--        · 绝不假设 `_G` 是我们的（不写全局，除一个安装标记）；
--        · 挂钩子一律"接在已有函数后面"，并把已有函数还回去；
--        · 所有状态挂在模块表上，重复 install 直接返回同一个实例。
--
-- 设计约束（都是实机踩出来的，改之前先看 docs/physbone-status.md）：
--   · 引擎调用是主要成本 ⇒ 每帧只做必要的事，姿态没变就不写；
--   · 不能缓存引擎返回的句柄（0.1 秒就变 inf）；
--   · 对已销毁 Unit 调任何方法 = 原生 AV ⇒ 每帧先 `Unit.alive`；
--   · 场景图重建只看 `num_scene_graph_items`（`has_node` 会假翻）。
--
-- 观测（v1.1.0 起）：这一层必须能自证"我到底做了什么"。
-- 2026-11-01 那次"物理完全没生效"排查了整整两轮，卡点全在"日志里没有事实"：
-- 分不清是没进驱动块、进了但没写、写了但写错骨、还是根本没挂上。
-- 所以下面每一处"静默 return"都配了一条原因日志，并且**只在原因变化时**落盘
-- （每 20 帧一条同样的等待原因是刷屏，不是信息）。

local M = {}

-- 2.0.4：快照**探针**。按骨名采回来 13 个全"缺"，而同一套 API 在求解器里是好的 ——
--        光看一个"缺 13"分不清是 has_node 假、node 假还是读位置假。请求加 `probe = 1`：
--        前 4 个 id 逐步把 类型/返回值 打进日志（`[快照][探针]`）；另外数字 id 没命中时
--        退回字符串名字再试一次（id32 只是名字的哈希，未必等价）。
--
-- 2.0.3：快照的**取证能力**补强（实机第一次按 F9 就把游戏读崩了，而日志里没有现场）。
--        · `[快照]` 归入"永远落盘"的那几类，预算用尽也留得下
--        · 每帧**先**打一行"开始第 k/M 帧、N 个节点"再采 —— 崩了就知道崩在第几帧
--        · 每帧单独落盘（snapshot-<seq>-s<k>.json），崩了前面的数据还在
--        · 新增请求项 `limit=N`：只采前 N 个节点，先探路再放量
--        一次按键读 685 个节点 ≈ 2700 次引擎调用，这个量本身就是风险。
--
-- 2.0.2：**姿态快照**。按一下热键，把当前玩家那具骨架的世界变换写到
--        `%LOCALAPPDATA%\hd2physbone-snap\snapshot-*.json`（外部工具写请求文件触发，
--        因为游戏的输入走 Raw Input，Lua 侧读不到键）。为什么需要：穿模只在动起来时
--        看得见，而离线只有 bind 姿态；实测绑定时链根离髋部表面 +4.6~+9.9 cm（全在外面），
--        真正插进去的是皮面，所以必须拿到"那一帧的骨变换"，在 PC 侧蒙皮还原网格再判。
--
-- 2.0.1：**日志瘦身**（只动日志，不动求解）。心跳 120 帧 → 600 帧；打开时旧文件超
--        512 KiB 就轮转成 `.1`；一次启动最多写 128 KiB，用满之后只留错误/失败/警告/驱动失效
--        四类。目的一句话：日志是事后唯一的证据，但它不该成为磁盘上的负担，也不该因为
--        "玩家这局玩了多久"而决定自己有多大。
--
-- 2.0.0：**多人局**。以前整条调度只认"一个玩家 avatar"，多出来的候选一律当成尸体；
--        于是队友一出现就被判成"玩家换人了"，会话全丢、重新挂到队友身上，自己身上反而没物理。
--        现在按玩家分组：每个活动玩家各自持有自己的会话，谁死了只收谁。
--        附：`World.units_by_resource` 直接列出所有玩家 avatar（一次调用，替代扫 2290 个 unit）；
--        `World.debug_camera_pose` 给出相机位置，用来排序（最近的那个就是自己）；
--        链数超预算时非首位玩家按 2 帧一次驱动（dt 累积补偿，不丢时间）。
--
-- 1.9.1：容器扫描窗口改成"按实际部署的最大 patch 序号 + 5"（以前写死 0..63，
--        装一堆 mod 时排在 63 之后的物理 mod 会被漏掉，日志还误报成"没做 embed"）。
-- 2.1.39：配合转换器"每个 unit 只带自己用得到的那几根物理骨"。
--        ① 判据从"命中 ≥10% 的骨"降到"**命中 ≥1 根**"——一件只带 2 条链（4 根骨）的
--           unit 在 57 骨的容器上只有 7%，按旧门槛会被判"不可用"直接丢掉（腿上的缎带就没物理了）。
--           真正该不该挂由 `Compat.attach` 决定：它逐链解析，缺节点的链跳过，产不出链就当场放弃。
--        ② `已挂载` 汇总行带上**每件的链数**，这样一次进游戏就能看出"是不是真的按件切开了"
--           （改前每件都是 39，改后各件各不相同、加起来远小于 件数×39）。
--        ③ 预筛（`probe_carriers`，默认关）改成问**每一条链的链根**而不是第 1 根骨：
--           按件切骨之后"第 1 根骨"的命中与否不再说明任何事，那样判会把真护甲挡在外面。
-- 2.1.40：裙摆路径补两道"生命周期"闸（2026-11-12 军械库/债券页必崩）。
--        ① `skirt_apply` 遍历会话时**先判活**再碰 unit：开页面时引擎会连续创建/销毁展示件，
--           而裙摆在 `drive` 末尾每帧跑一遍，一帧内被销毁的件正好落在这条路上
--           （对已销毁 unit 调 `num_scene_graph_items` / `world_position` 是原生 AV，
--           转储形态 `0xc0000005 / 参数 0x0 0x0`，pcall 拦不住）。
--        ② `skirt_refs` 缓存的是**节点下标**：unit 被重建后下标会指向别的节点甚至越界。
--           现在每帧比一次节点数，变了就重算（`session_health` 每 30 帧才比一次，中间是盲窗）。
-- 2.1.43：把同一道指针闸也套在 **avatar 名单**上。真凶（17:58 相位日志定位）：
--        `refresh_avatar_distances` 每帧对每个 avatar 句柄调 `Unit.world_position(avatar.unit, 1)`，
--        而名单是上一帧的；债券页快切时引擎连续重建 avatar（展台上的角色就是 avatar 资源），
--        句柄一失效这一行就是野读 ⇒ 原生 AV。相位日志死在「refresh_avatar_distances 前」，
--        没有「后」，一锤定音。
-- 2.1.41：判活改成**不 deref 句柄**的指针成员检查（2026-11-12 债券页快切必崩）。
--        现场：慢切不崩、快切必崩；`skirt = 0` 与 2.1.40 都无效 ⇒ 枪口不在裙摆，
--        而在 `pruneSessions → Unit.alive`：句柄被销毁之后，**任何**引擎调用（含 alive）
--        都是原生 AV（转储 `0x3e564b`、参数 `0x0 0x0`），pcall 拦不住。
--        现在每帧先问 `World.units(world)` 要"活体列表"，只在 Lua 里按指针比：
--        不在列表里 ⇒ 判死、整组丢弃，一个 Unit API 都不调；世界本身没了（`Application.worlds()`
--        里找不到我们那个 world）⇒ 整组丢弃且不碰句柄。
-- 2.1.44：驱动心跳**不再被补挂清零**（2026-11-13 实机：挂完 13 件后跑了 2 分钟，一行心跳都没有）。
--        `tryAttach` 每次成功都把 `driveFrames` 归零，而补挂（reconcile）在挂满之后仍每 1~2 秒
--        成功一次（"本次新挂 0、已挂过 13"）⇒ 计数永远攒不到 600，心跳里的"偏离绑定峰值"
--        —— 判断"求解器在动但画面不动"还是"求解器根本没推开骨"的唯一数字 —— 从来打不出来。
--        现在心跳/逐链明细改用独立计数 `beatFrames`（只在安装时归零）；
--        调参文件可写 `heartbeatFrames = N` 改间隔、`chainLog = 1` 打开逐链峰值（按件列出）。
-- 2.1.45：**只约束一个轴的链，另一个轴不再被锁死**（physbone_compat.gains_of）。
--        flags=0x35（yawCon + angleCon，没有 pitchCon）的长发链：pitch 区间留着初值 [0, 0]，
--        而求解器在 axisLimits 打开时两个分量都夹 ⇒ 前后甩被锁在 0°，长发看着像一根焊丝。
--        现在没约束的轴按"不限"处理，总偏角仍由锥兜住。
M.VERSION = "2.1.61"

--- 安装标记用的全局键。只写这一个全局，且值就是模块本身（便于外部检查是否已装）。
local INSTALL_KEY = "__hd2physbone_runtime"

local tune_values     -- 调参文件读数（定义在后面，tryAttach 也要用 ⇒ 必须先声明）
local Compat          -- physbone_compat 模块（require 或注入）
local Roster = {}     -- IAS 式每帧枚举（见 Roster 段）
local S               -- stingray 命名空间
local state = nil     -- 运行状态；nil = 未安装
local originalRender  -- 挂载前的 render（链式调用，不吞掉别人）

-- ---------------------------------------------------------------------------
-- 小工具
-- ---------------------------------------------------------------------------

--- 默认日志路径。`options.logPath` 可覆盖，`options.log == false` 可彻底静默。
local function defaultLogPath(options)
	local path = options and options.logPath
	if path ~= nil then return path end
	if options and options.log == false then return nil end
	local base = os.getenv("LOCALAPPDATA")
	if type(base) == "string" and #base > 0 then return base .. "\\hd2physbone.log" end
	return nil
end

--- 一次启动最多往盘上写多少字节（默认 128 KiB）；用满之后只留"出事了"的行。
local DEFAULT_LOG_MAX_BYTES = 128 * 1024

--- 打开日志时如果已有文件超过这个大小就轮转成 `.1`（默认 512 KiB）。
local DEFAULT_LOG_ROTATE_BYTES = 512 * 1024

--- 预算用尽之后**仍然**要落盘的标记。
--- <para>
--- 只留这几类，是因为它们本身就是"出事了"的证据；心跳、等待、预算、逐链明细
--- 这类常规行才是吃字节的大头，也是最没必要的长期留存。
--- </para>
local ALWAYS_LOG_TAGS = { "[错误]", "[失败]", "[警告]", "[驱动失效]", "[快照]", "[裙摆]" }

--- 造一个 logger。返回 `log, context`。
--- <para>
--- 两个刻意的选择：
---   ① **追加**而不是覆盖。覆盖式会把上一次启动的证据抹掉，于是"这次和上次哪里不一样"
---      永远无从对比——2026-11-01 排查就卡在这里（日志只剩最后 30 秒）。
---   ② 每行带**时间戳 + 帧号**。没有帧号，跨会话的"30 帧后全灭"这种时序规律看不出来。
--- 帧号由 `drive` 每帧写进 `context.frame`：compat 会直接调 `session.logger`，
--- 只能靠这个共享表把上下文带进去。
--- </para>
--- <para>
--- 光"追加"会有两个副作用，所以这里还加了两道闸：
---   * **跨会话轮转**：打开时文件已经超过 <c>logRotateBytes</c> 就改名成 `.1`。
---     否则这个文件会随每次启动单调长大，几百 MB 之后连打开都费劲。
---   * **单次启动预算**：写满 <c>logMaxBytes</c> 就停常规行，并落一行说明。
---     心跳这类周期行的总量因此有了硬上限，不再取决于玩家玩了多久。
--- 刻意**没有**加"开局 N 秒后停止录制"的时间窗：玩到第 40 分钟才出现的毛病
--- 恰好会落在窗外，而这正是最需要日志的时候。要省字节，砍周期行比砍时间窗安全。
--- </para>
local function makeLogger(options)
	local handle = nil
	local path = defaultLogPath(options)
	local maxBytes = (options and options.logMaxBytes) or DEFAULT_LOG_MAX_BYTES
	-- **开发模式**：把预算整个关掉，全量落盘。
	-- 为什么需要：预算用尽后只留错误类，于是"我加的那行诊断"被静默丢掉——
	-- 2026-11-09 就因为这个白测了一轮（以为没效果，其实是没有观测数据）。
	-- 开关走调参文件（`devLog = 1`）或环境变量 `HD2_PHYSBONE_DEV=1`：两者都在启动时读一次，
	-- 不影响运行期性能。产品环境不设它 ⇒ 行为与之前完全一致。
	if (options and options.devLog == true) or os.getenv("HD2_PHYSBONE_DEV") == "1" then
		maxBytes = math.huge
	elseif io and type(io.open) == "function" then
		local tunePath = (options and options.tunePath) or ((os.getenv("LOCALAPPDATA") or "") .. "\\hd2pbx_tune.txt")
		local ok, handle = pcall(io.open, tunePath, "r")
		if ok and handle ~= nil then
			local okRead, text = pcall(handle.read, handle, "*a")
			pcall(handle.close, handle)
			if okRead and type(text) == "string" and text:find("devLog%s*=%s*1") ~= nil then
				maxBytes = math.huge
			end
		end
	end
	local rotateBytes = (options and options.logRotateBytes) or DEFAULT_LOG_ROTATE_BYTES
	if path and io and type(io.open) == "function" then
		-- 轮转：先量旧文件，超阈值就挪到 `.1`（覆盖上一次的 `.1`）。
		pcall(function()
			local probe = io.open(path, "r")
			if probe == nil then return end
			local size = probe:seek("end") or 0
			probe:close()
			if size <= rotateBytes then return end
			os.remove(path .. ".1")
			os.rename(path, path .. ".1")
		end)
		local ok, h = pcall(io.open, path, "a")
		if ok then handle = h end
	end
	local debugEnabled = options ~= nil and options.debug == true
	local context = { frame = 0 }
	local used, lines, exhausted = 0, 0, false
	local function writeLine(text)
		local ok = pcall(function() handle:write(text, "\n") handle:flush() end)
		if ok then lines = lines + 1 end
		return ok
	end
	local function alwaysKept(text)
		for index = 1, #ALWAYS_LOG_TAGS do
			if string.find(text, ALWAYS_LOG_TAGS[index], 1, true) ~= nil then return true end
		end
		return false
	end
	local function emit(text)
		if handle ~= nil then
			local size = #text + 1
			if exhausted then
				-- 预算用尽：常规行丢掉，出事的行照写。
				if alwaysKept(text) then used = used + size; writeLine(text) end
			elseif used + size > maxBytes then
				exhausted = true
				used = used + size
				writeLine(string.format(
					"[physbone] 日志已达上限 %d KiB（此前 %d 行）⇒ 本次启动后续只保留错误/失败/警告类",
					math.floor(maxBytes / 1024), lines))
			else
				used = used + size
				writeLine(text)
			end
		end
		if debugEnabled then
			local real = rawget(_G, "print")
			if type(real) == "function" then pcall(real, text) end
		end
	end
	local function log(fmt, ...)
		if handle == nil and not debugEnabled then return end
		local body = select("#", ...) > 0 and string.format(fmt, ...) or fmt
		emit(string.format("%s f%-7d %s", os.date("%H:%M:%S"), context.frame, body))
	end
	return log, context
end

local function try(...)
	local ok, a, b, c = pcall(...)
	if ok then return a, b, c end
	return nil, tostring(a)
end

-- ---------------------------------------------------------------------------
-- 分项计数（E12，开销全清单 §5）：每项记"引擎调用数 + os.clock 相对耗时"，每 600 帧一行汇总。
-- 调用数按**最内层**分项归属（排他），耗时同样排他（减去子分项）。os.clock 只能看相对量。
-- 关掉：调参文件 `perfCounters = 0` 或 install 选项 `perfCounters = false`（关掉后不包装任何引擎函数）。
-- ---------------------------------------------------------------------------
local Perf = { on = false, active = false, api = {}, cur = nil, n = {}, calls = {}, clock = {}, events = {}, frames = 0, totalCalls = 0, window = 600,
	order = { "F1", "F2", "F3", "F4", "F4p", "F4a", "F5", "F6", "F7", "F8", "F9", "P1", "P2", "P3", "P4", "P5", "P6", "P7", "P8", "OTHER" } }

function Perf.reset()
	Perf.n, Perf.calls, Perf.clock, Perf.events, Perf.frames, Perf.totalCalls, Perf.api = {}, {}, {}, {}, 0, 0, {}
	Perf.firstAttachCalls = nil
end
Perf.reset()

function Perf.count(name) -- 计一次"发生"（F2 的查找次数、P4/P5/P6 的日志事件）
	if Perf.on then Perf.n[name] = (Perf.n[name] or 0) + 1 end
end

function Perf.event(name, amount)
	Perf.events[name] = (Perf.events[name] or 0) + (amount or 1)
end

--- 包装一个函数：进入时压栈，退出时按排他口径记账。错误穿过去时栈会错位，drive 入口会重置。
function Perf.wrap(name, fn)
	if type(fn) ~= "function" then return fn end
	local function leave(frame, ...)
		local spent = os.clock() - frame.t0
		Perf.n[name] = (Perf.n[name] or 0) + 1
		Perf.clock[name] = (Perf.clock[name] or 0) + spent - frame.child
		if frame.prev then frame.prev.child = frame.prev.child + spent end
		Perf.cur = frame.prev
		return ...
	end
	return function(...)
		if not Perf.on then return fn(...) end
		local frame = { name = name, t0 = os.clock(), child = 0, prev = Perf.cur }
		Perf.cur = frame
		return leave(frame, fn(...))
	end
end

--- 给引擎命名空间里的函数套计数壳（原地替换；只替换一次）。
function Perf.instrument(stingray)
	for _, space in ipairs({ "Unit", "World", "Application", "Matrix4x4", "Quaternion", "Vector3", "Keyboard" }) do
		local table_ = type(stingray) == "table" and stingray[space] or nil
		if type(table_) == "table" and rawget(table_, "__perfWrapped") == nil then
			local entries = {}
			for key, value in pairs(table_) do
				if type(value) == "function" then entries[key] = value end
			end
			for key, fn in pairs(entries) do
				table_[key] = function(...)
					-- 只数**我们自己的回调**里发出的调用（Perf.active）：引擎命名空间是全局共享的，
					-- 其他 mod 的调用也会经过这里。2.1.58 没有这个门，OTHER≈1750 次/帧其实是别人的调用。
					if Perf.on and Perf.active then
						local current = Perf.cur
						local bucket = current and current.name or "OTHER"
						Perf.calls[bucket] = (Perf.calls[bucket] or 0) + 1
						Perf.totalCalls = Perf.totalCalls + 1
						local per = Perf.api[bucket]
						if per == nil then per = {}; Perf.api[bucket] = per end
						local name = space .. "." .. key
						per[name] = (per[name] or 0) + 1
					end
					return fn(...)
				end
			end
			rawset(table_, "__perfWrapped", true)
		end
	end
end

function Perf.summary(runtime, extra)
	local frames = math.max(1, Perf.frames)
	local parts = {}
	for _, name in ipairs(Perf.order) do
		local calls, spent, count = Perf.calls[name] or 0, Perf.clock[name] or 0, Perf.n[name] or 0
		if calls > 0 or spent > 0 or count > 0 then
			parts[#parts + 1] = string.format("%s=%.1f次/%.3fms(调%d)", name, calls / frames, spent * 1000 / frames, count)
		end
	end
	local events = {}
	for key, value in pairs(Perf.events) do events[#events + 1] = key .. "=" .. tostring(value) end
	table.sort(events)
	for _, bucket in ipairs({ "F4", "F6", "F7", "P2", "OTHER" }) do
		local per = Perf.api[bucket]
		if per ~= nil then
			local rows = {}
			for name, count in pairs(per) do rows[#rows + 1] = { name, count } end
			table.sort(rows, function(a, b) return a[2] > b[2] end)
			local text = {}
			for index = 1, math.min(5, #rows) do text[#text + 1] = string.format("%s=%.1f", rows[index][1], rows[index][2] / frames) end
			parts[#parts + 1] = bucket .. "明细[" .. table.concat(text, " ") .. "]"
		end
	end
	runtime.logger("[physbone][PERF] 窗口=%d帧 世界unit=%d 总引擎调用=%.1f/帧 ｜ %s ｜ 事件 %s",
		Perf.frames, runtime.liveUnitCount or 0, Perf.totalCalls / frames, table.concat(parts, " "),
		#events > 0 and table.concat(events, " ") or "无")
end

local function readVector(S_, vector)
	if vector == nil then return nil end
	local ok, x, y, z = pcall(function() return S_.Vector3.to_elements(vector) end)
	if ok and type(x) == "number" and type(y) == "number" and type(z) == "number"
		and math.abs(x) < math.huge and math.abs(y) < math.huge and math.abs(z) < math.huge then return x, y, z end
	return nil
end

local function id32(S_, text)
	local ok, value = pcall(function() return S_.Application.string_to_id32(text) end)
	if ok then return value end
	return nil
end

--- `Unit.debug_name` 返回形如 `#ID[a0cc3d2318d9e237]` 的**实例**标识。
--- 这是运行时唯一可靠的"是不是同一个 unit"判据（`Unit.id` 对大量 unit 返回 nil）。
--- ⚠️ 只能对活着的 unit 调：销毁之后再读就是原生 AV，所以挂载时就要记下来。
local function unit_tag(S_, unit)
	local text = try(S_.Unit.debug_name, unit)
	if type(text) ~= "string" then return "?" end
	return text
end

-- ---------------------------------------------------------------------------
-- 世界发现
-- ---------------------------------------------------------------------------

--- 玩家 avatar 的资源路径。同一个角色的所有玩家共用这一个资源，所以
--- `World.units_by_resource(world, 路径)` **一次调用**就能把场上所有玩家列出来，
--- 不必再"扫 2290 个 unit 猜谁有动画状态机"。
---
--- 这也是"多人局只给一个人挂物理"的正解：我们从来不需要知道**哪个是我**，
--- 需要的是**哪些是玩家**——这个资源名就是那个答案。谁是第一位（自己）交给相机距离。
local DEFAULT_AVATAR_RESOURCE = "content/fac_helldivers/cha_avatar/avatar_helldiver"

--- 相机位置。只用于两件事：给玩家排序（最近的那个＝自己）、给驱动预算定优先级。
--- 读不到**不影响功能**（只是"谁是第一位"退回按 unit 顺序），所以任何一步失败都安静返回 nil，
--- 并记住别再试——每帧问一次注定失败的引擎调用纯属浪费。
local function cameraPosition(S_, world, cache)
	if cache.cameraUsable == false then return nil end
	local matrix = S_.Matrix4x4
	if type(matrix) ~= "table" or type(matrix.translation) ~= "function" then
		cache.cameraUsable = false
		return nil
	end
	local pose = try(S_.World.debug_camera_pose, world)
	if pose == nil then
		cache.cameraUsable = false
		return nil
	end
	local ok, value = pcall(matrix.translation, pose)
	if not ok then
		cache.cameraUsable = false
		return nil
	end
	local x, y, z = readVector(S_, value)
	if x == nil then
		cache.cameraUsable = false
		return nil
	end
	cache.cameraUsable = true
	return x, y, z
end

--- 本帧的相机位置：physics_sync 已经读过就直接用（IAS 每帧只读一次相机，R:1335），否则才问引擎。
local function frameCamera(S_, runtime)
	local cached = runtime.poseCamera
	if cached ~= nil and cached.frame == runtime.frame and cached.z ~= nil and runtime.world == cached.world then
		return cached.x, cached.y, cached.z
	end
	return cameraPosition(S_, runtime.world, runtime.cache or {})
end

--- 本帧内缓存的"有没有动画状态机"（每个 unit 每帧至多问一次）。
local function frame_animation_machine(runtime, unit)
	local cache = runtime.frameAnimation
	if not cache then
		cache = {}
		runtime.frameAnimation = cache
	end
	local animation = cache[unit]
	if animation then return animation, 0 end
	animation = { active = try(S.Unit.has_animation_state_machine, unit) == true }
	cache[unit] = animation
	return animation, 1
end

--- 从部署后的 patch 尾部读物理容器。返回 { { rig = ..., path = ... }, ... }。
--- 路径是"猜"的（运行时不知道 data 的绝对路径），所以多前缀都试一遍，成功后按相对名去重。
---
--- **扫描窗口按实际部署算，不写死。** patch 序号 = 这个 mod 在配置里的载入顺序：
--- 单独装一个物理 mod 时它是 `patch_0`，装了一大堆之后它可能是 `patch_87`。
--- 以前这里写死 `0..63`，于是"装一堆 mod 就找不到任何容器"——排在 63 之后的容器全被漏掉，
--- 日志还写着"来源模型可能没做 embed"，把问题指到了完全错误的方向。
---
--- 现在：先定出能打开的前缀 → 用倍增+二分问出最大序号（O(log N) 次探测）→ 扫 `0..最大+5`。
--- 那 +5 是余量：万一部署序列中间出现空洞（理论上不会，管理器每次从 0 连续重编），
--- 二分可能低估，多探五格兜住。
local function loadCarriers(S_, cache, options, logger)
	if not io or type(io.open) ~= "function" then return {} end
	local archives = options.archiveId and { options.archiveId }
		or { "9ba626afa44a3aa3", "59d238094a164c88", "d2fde496226be96f" }
	local carriers = {}
	local seen = {}

	-- 老版本 compat 没有探测函数（外置包里两个文件总是一起生成，但玩家可能只更新了其中一个）。
	-- 缺了它就退回原来的固定窗口，至少不至于整条物理链路报错。
	local canProbe = type(Compat.detect_prefix) == "function"
		and type(Compat.find_max_patch_index) == "function"

	local prefixes = { "data/", "data\\", "./data/", "../data/", "..\\data\\" }
	local poked = {}
	local scannedTo = -1
	for _, archive in ipairs(archives) do
	for _, prefix in ipairs(prefixes) do
		if not canProbe or Compat.detect_prefix(archive, { prefix }) ~= nil then
			local maxIndex = options.maxPatchIndex
			if maxIndex == nil then
				local last = canProbe and Compat.find_max_patch_index(prefix, archive) or -1
				if last < 0 then
					maxIndex = canProbe and 5 or 63     -- 探不到就用老的固定窗口
				else
					maxIndex = last + 5
				end
			end
			if maxIndex > scannedTo then scannedTo = maxIndex end

			for index = 0, maxIndex do
				local relative = archive .. ".patch_" .. index .. ".gpu_resources"
				if seen[relative] == nil then
					local handle = io.open(prefix .. relative, "rb")
					if handle ~= nil then
						handle:close()
						seen[relative] = true
						poked[#poked + 1] = prefix .. relative
						local rig = Compat.load_rig({ prefix .. relative })
						if rig ~= nil and type(rig.bones) == "table" and #rig.bones > 0 then
							carriers[#carriers + 1] = { rig = rig, path = prefix .. relative }
						end
					end
				end
			end
		end
	end
	end

	if #carriers > 0 then
		logger(string.format("[physbone] 读到 %d 份物理容器（扫了 patch 0..%d）", #carriers, scannedTo))
	elseif scannedTo >= 0 then
		-- **把"扫了多大范围"写进日志。** 以前只有一句"可能没做 embed"，而真正的原因
		-- 往往在"扫的窗口不够大"：说了范围，下一次一眼就能分辨是哪种。
		logger(string.format(
			"[physbone] 没找到任何物理容器（扫了 patch 0..%d，%d 个 .gpu_resources 存在但都不含容器）",
			scannedTo, #poked))
	else
		logger("[physbone] 没找到任何物理容器（data 目录下没有这个归档的 patch 文件——游戏是否还没部署过？）")
	end
	return carriers
end

-- Both discovery paths must filter before scoring or resolving any nodes.
local function filterCarriers(carriers, mask)
	local filtered, removed = {}, 0
	for _, carrier in ipairs(carriers) do
		local rig, count = Compat.filter_ias_groups(carrier.rig, mask)
		removed = removed + count
		if #rig.bones > 0 then
			local entry = {}
			for key, value in pairs(carrier) do entry[key] = value end
			entry.rig = rig
			filtered[#filtered + 1] = entry
		end
	end
	return filtered, removed
end

-- ---------------------------------------------------------------------------
-- 挂载 / 卸载
-- ---------------------------------------------------------------------------

--- 身份链探针：**这件护甲 unit 与玩家 avatar 在场景图里有没有结构关系**。
---
--- 为什么值得单独探（用户 2026-11-12 定的方向）：现在的"这是玩家身上的护甲"是**启发式**
--- （离哪个 avatar 最近 + 会不会跟着动）。于是军械库/债券页里那些**展示用**的 unit 也会被挂上，
--- 既白算物理，又制造了"页面快切时句柄被销毁"的崩溃燃料。
--- 如果引擎能直接回答"这个 unit 是那个 avatar 的子/被链接者"，身份就从猜测变成**结构事实**：
--- 展台、预览件、尸体都会被自然排除。
---
--- 只读、默认关、崩了能续：调参文件写 `probeIdentityLink = 0` 关闭（默认），
--- 写 `1` 或具体步号表示从该步开始。每一步**先落盘再调用** —— 崩在下一行时，
--- 下次启动就知道是哪个 API 干的（`%LOCALAPPDATA%\hd2pbx_ident_probe.txt`）。
--- ⚠️ 用独立文件，不要和 `hd2pbx_probe.txt` 混用（那个已经被另一个一致性电池占用）。
local IDENT_PROBE_FILE = "hd2pbx_ident_probe.txt"

local function ident_probe_state_path(runtime)
	-- 允许 options 覆盖（测试用；产品里永远走 LOCALAPPDATA）
	local override = runtime ~= nil and runtime.options ~= nil and runtime.options.identProbePath or nil
	if type(override) == "string" and #override > 0 then return override end
	local base = os.getenv("LOCALAPPDATA")
	if type(base) ~= "string" or #base == 0 then return nil end
	return base .. "\\" .. IDENT_PROBE_FILE
end

local function ident_probe_read_next(runtime)
	local path = ident_probe_state_path(runtime)
	if path == nil then return 1 end
	local ok, handle = pcall(io.open, path, "r")
	if not ok or handle == nil then return 1 end
	local text = handle:read("*a")
	handle:close()
	local value = tonumber(type(text) == "string" and text:match("next%s*=%s*(%d+)") or nil)
	return value or 1
end

local function ident_probe_write_next(runtime, value)
	local path = ident_probe_state_path(runtime)
	if path == nil then return end
	local ok, handle = pcall(io.open, path, "w")
	if not ok or handle == nil then return end
	pcall(handle.write, handle, string.format("next = %d\n", value))
	pcall(handle.close, handle)
end

--- 跑一轮身份链探针。只在"已经有会话"时调用（那时我们手上正好有一件**穿着的**护甲 + 它的 owner）。
local function probe_identity_link(runtime, S_)
	if runtime.identProbeDone then return end
	local session = runtime.sessions ~= nil and runtime.sessions[1] or nil
	if session == nil or session.unit == nil or session.owner == nil then return end
	runtime.identProbeDone = true

	local logger = runtime.logger
	local unit, owner = session.unit, session.owner
	local steps = {
		{ "debug_name(护甲)（对照，已知可用）", function() return S_.Unit.debug_name(unit) end },
		{ "is_child(护甲, avatar)", function() return S_.Unit.is_child(unit, owner) end },
		{ "is_child(avatar, 护甲)", function() return S_.Unit.is_child(owner, unit) end },
		{ "scene_graph_parent(护甲, 1)", function() return S_.Unit.scene_graph_parent(unit, 1) end },
		{ "scene_graph_parent(avatar, 1)", function() return S_.Unit.scene_graph_parent(owner, 1) end },
		{ "id(护甲)", function() return S_.Unit.id(unit) end },
		{ "id(avatar)", function() return S_.Unit.id(owner) end },
	}

	local function repr(value)
		local kind = type(value)
		if value == nil then return "nil" end
		if kind == "boolean" or kind == "number" or kind == "string" then
			return tostring(value):sub(1, 60)
		end
		if kind == "userdata" or kind == "table" then
			local okName, name = pcall(function() return S_.Unit.debug_name(value) end)
			if okName and name ~= nil then return tostring(name):sub(1, 60) end
		end
		return kind
	end

	local forced = tune_values(runtime).probeIdentityLink
	local startAt = ident_probe_read_next(runtime)
	-- 语义：`= 1` 只是"打开"，**从断点文件续**；`>= 2` 才表示"强制从第 N 步开始"。
	-- （写成"1 也强制从第 1 步"会让每次启动都重跑一遍已经验过的步骤，白白多崩几次。）
	if type(forced) == "number" and forced >= 2 then startAt = math.floor(forced) end
	if startAt > #steps then
		logger("[physbone][身份链] 全部跑完（%d 项），不再探", #steps)
		return
	end
	logger("[physbone][身份链] 共 %d 项，从 #%d 开始（护甲 %s / avatar %s）",
		#steps, startAt, tostring(session.unitTag or "?"), tostring(session.ownerTag or "?"))
	for index = startAt, #steps do
		local step = steps[index]
		ident_probe_write_next(runtime, index + 1)      -- 先落盘：崩在下一行也能续
		logger("[physbone][身份链] #%d 开始 %s", index, step[1])
		local ok, value = pcall(step[2])
		if not ok then
			local text = tostring(value):gsub("^.*:%d+: ", "")
			logger("[physbone][身份链] #%d ⇒ 抛错(%s)", index, text:sub(1, 70))
		else
			logger("[physbone][身份链] #%d ⇒ %s %s", index, type(value), repr(value))
		end
	end
	ident_probe_write_next(runtime, #steps + 1)
	logger("[physbone][身份链] 全部 %d 项跑完", #steps)
end

--- 相位标记（诊断）：调参文件 `phaseLog = 1` 打开。
--- 只在排查崩溃时开 —— 打开后每帧会多出十来行（attach/写骨/裙摆各一段）。
--- 用途：崩了之后看最后一条 `[相位]` 就知道死在**哪个阶段**，不用猜。
local function phase(runtime, name)
	if (tune_values(runtime).phaseLog or 0) == 0 then return end
	runtime.logger("[physbone][相位] %s", name)
end

--- 用**指针相等**判断"会话的 unit 还在不在"，全程**不 deref 句柄**。
---
--- 为什么必须这样（2026-11-12 债券页快切必崩，转储 `0x3e564b` / 参数 `0x0 0x0`）：
--- 句柄被销毁之后，**任何**引擎调用都会 deref 那块已经还回去的内存 —— `Unit.alive` 也一样，
--- 而 `pcall` 拦不住原生 AV。过去的写法是"每帧对每个会话问一次 `Unit.alive`"，
--- 于是"引擎刚刚销毁这批 unit"的那一帧就是必死：慢速切页还能被"加载空档"那道闸（整组丢弃、
--- 一条都不查）救下来，快速切页没有空档，直接踩中。
---
--- 这里的判据换了方向：**问引擎要"当前活体列表"，然后在 Lua 里按指针比**。
--- `World.units(world)` 返回的是引擎自己现存的对象，拿它做**相等比较**不 deref 任何东西。
---@return string "ok"（拿到可信列表，`runtime.liveUnits` 有效）| "world-gone"（我们那个世界没了）
---          | "unavailable"（引擎没这两个 API ⇒ 不据此判死，退回旧行为）
local function refresh_live_units(runtime, S_)
	local worlds = try(S_.Application.worlds)
	if type(worlds) ~= "table" then return "unavailable" end
	local found = false
	for index = 1, #worlds do
		if worlds[index] == runtime.world then found = true break end
	end
	if not found then return "world-gone" end
	local units = try(S_.World.units, runtime.world)
	if type(units) ~= "table" then
		runtime.liveUnits = nil
		runtime.liveStamp = nil
		return "unavailable"
	end
	runtime.liveUnits = units
	runtime.liveUnitsWorld = runtime.world
	runtime.liveUnitCount = #units
	-- F2：判活由"每次线性比较整张表"改成"一次建索引、之后 O(1)"。
	-- 索引用帧戳（不必每帧清表）：live[unit] == 本帧戳 才算活。每 600 帧整体重建一次，防止死句柄越积越多。
	local stamp = (runtime.frame or 0) + 1
	local live = runtime.liveIndex
	if live == nil or (runtime.liveBuilt or 0) + 600 <= stamp then
		live = {}
		runtime.liveIndex, runtime.liveBuilt = live, stamp
	end
	for index = 1, #units do live[units[index]] = stamp end
	runtime.liveStamp, runtime.liveIndexUnits = stamp, units
	return "ok"
end

--- 会话的 unit 是否仍在活体列表里（**只做指针比较**）。
--- `runtime.liveUnits == nil` 表示"没有可信列表"⇒ 不据此判死（保持旧行为）。
local function session_unit_live(runtime, unit)
	if Roster.isLive(runtime, unit) then return true end
	local units = runtime.liveUnits
	if units == nil then return true end
	Perf.count("F2")
	local live = runtime.liveIndex
	if live ~= nil and runtime.liveIndexUnits == units then
		return live[unit] == runtime.liveStamp
	end
	-- 外部直接塞了 liveUnits（测试 / 旧调用方）：没有索引，退回线性比较。
	for index = 1, #units do
		if units[index] == unit then return true end
	end
	return false
end


-- ---------------------------------------------------------------------------
-- 抬升诊断（默认关闭，靠一个外部文本文件打开）
-- ---------------------------------------------------------------------------
--
-- 只回答一个问题：**把 avatar 整体抬高，摄像机会不会跟着走。**
--
-- 为什么这条决定一切：护甲网格是按骨名从 avatar 取世界变换渲染的（本仓库实测：往护甲
-- 自己的 `chest` 写 pose 能留住 450 帧，子节点世界坐标一点都不动），枪挂在 avatar 的
-- `attach_hand_r` / `r_hand` 上。所以抬 avatar ⇒ 网格和枪**一定**一起走，相对关系不变。
-- 唯一的未知量是摄像机：它若从 avatar 取，视线跟着抬 ⇒ 脚落地、手和枪对齐、瞄准不变，
-- 这条路就是完整的解；它若不跟，网格和枪升上去而视线留在原地 ⇒ 瞄准对不上，
-- 那这条路直接作废（那时只剩"只压腿"一条）。
--
-- 开关刻意放在**外部文本文件**里（`%LOCALAPPDATA%\hd2pbx_tune.txt`）：
--   lift=0.09        抬多少米（正 = 向上）；不给 / 给 0 / 没有文件 ⇒ 这一段完全不执行
--   lift_bone=chest  改抬 **avatar 上某一根骨**（不给 = 抬 avatar 根节点）
--   lift_armor=1     对照实验：连护甲件的根节点也一起抬
--   lift_log_frames  每多少帧打一行（默认 60）
-- 不放进打包配置，是因为诊断要能**就地改数值、就地关掉**，不该逼着重新打包或重建管理器。
--
-- **实机结论（2026-11-05，用户测试）**：
--   · `lift_armor=1` 与 `=0` **完全一样** ⇒ 护甲件的根节点不参与渲染
--     （它的网格只认被重定向的骨，这条和 §B2.3 的实测一致）；
--   · 写 avatar 的 node 1 ⇒ **只有摄像机跟着动，网格一点不动**。
--     也就是说 unit 根节点对相机有效、对网格的变换链无效（网格的蒙皮来自动画烘焙，不吃 node 1）。
-- ⇒ 下一个要试的杠杆是**骨**：`lift_bone` 写 avatar 某根骨的 local position，然后读回
--    ① 这根骨自己的世界 z（杠杆存不存在）、② 头骨的世界 z（子节点跟不跟）、
--    ③ 护甲上同名骨的世界 z（网格会不会跟）。三个数在同一行里。
--
-- ⚠️ 边界：这一段挂在 `drive()` 里，所以**只有挂着物理会话时才会执行**。诊断足够用；
-- 若哪天要把它做成正式功能（不只是诊断），必须挪到独立的 render 钩子，否则
-- "没物理骨的模型 / 关掉物理的玩家"都不会被抬。

--- 读调参文件（缓存，默认 2 秒重读一次）。
--- 数字进表本身（`lift` 等），字符串原文进 `values.text`（`lift_bone` 这类骨名）。
tune_values = function(runtime)
	local frame = runtime.frame or 0
	local cached = runtime.tune
	local refresh = (runtime.options and runtime.options.tuneRefreshFrames) or 30
	if cached ~= nil and (frame - cached.frame) < refresh then return cached.values end

	local values = { text = {} }
	local path = runtime.options and runtime.options.tunePath
	if path == nil then
		local base = os.getenv("LOCALAPPDATA")
		if type(base) == "string" and #base > 0 then path = base .. "\\hd2pbx_tune.txt" end
	end
	if path ~= nil and io and type(io.open) == "function" then
		local ok, handle = pcall(io.open, path, "r")
		if ok and handle ~= nil then
			local okRead, text = pcall(handle.read, handle, "*a")
			pcall(handle.close, handle)
			if okRead and type(text) == "string" then
				for line in text:gmatch("[^\r\n]+") do
					-- `#` 开头的行整行跳过；值只取到空白或 `#` 为止（行尾可以写说明）
					local key, raw = line:match("^%s*([%w_]+)%s*=%s*([^%s#]+)")
					if key ~= nil then
						local number = tonumber(raw)
						if number ~= nil then values[key] = number end
						values.text[key] = raw
					end
				end
			end
		end
	end
	runtime.tune = { frame = frame, values = values }
	return values
end

--- 给一个 unit 的根节点（node 1）加一个垂直偏移。返回 `wrote, kept`。
--- <para>
--- **绝不能累加**。规则是"先读、再判断"：
---   · 读到的值等于我们上次写进去的 ⇒ 引擎这一帧没重设 ⇒ 不再加（否则每帧 +9 cm，会飞）；
---   · 否则说明引擎把它设回了基线 ⇒ 在**当前值**上重加一次。
--- 根 unit 的 local == world，玩家一动引擎就重设 ⇒ 正常路径是"每帧重写一次"；
--- 被父节点带着走的 unit（local 恒定）⇒ 路径是"写一次后一直跳过"。两种情况都稳。
--- </para>
local function lift_unit(S_, world, unit, state, metres)
	local current = try(S_.Unit.local_position, unit, 1)
	if current == nil then return false, false end
	local cx, cy, cz = readVector(S_, current)
	if cx == nil then return false, false end
	local written = state.written
	if written ~= nil
		and math.abs(cx - written[1]) < 1e-4
		and math.abs(cy - written[2]) < 1e-4
		and math.abs(cz - written[3]) < 1e-4 then
		return false, true
	end
	local ok = pcall(function()
		S_.Unit.set_local_position(unit, 1, S_.Vector3(cx, cy, cz + metres))
	end)
	if not ok then return false, false end
	if world ~= nil then pcall(function() S_.World.update_unit(world, unit) end) end
	state.written = { cx, cy, cz + metres }
	state.base = { cx, cy, cz }
	return true, false
end

--- unit 还活着吗。诊断这条路上不能依赖 `Compat` 已注入（单测会直接调它），
--- 所以没有 Compat 时退回裸 `Unit.alive`。
local function lift_unit_ok(api, unit)
	if unit == nil then return false end
	if Compat ~= nil and type(Compat.unit_alive) == "function" then
		return Compat.unit_alive(api, unit) and true or false
	end
	return try(api.Unit.alive, unit) == true
end

--- 读某个 unit 上某根骨的世界 z（不存在的骨返回 nil）。
local function bone_world_z(S_, unit, id)
	if unit == nil or id == nil or not lift_unit_ok(S_, unit) then return nil end
	if try(S_.Unit.has_node, unit, id) ~= true then return nil end
	local node = try(S_.Unit.node, unit, id)
	if type(node) ~= "number" then return nil end
	local _, _, z = readVector(S_, try(S_.Unit.world_position, unit, node))
	return z
end

--- 抬 **avatar 上的一根骨**（`lift_bone`）。
--- <para>
--- 为什么必须直接量三个数才能下结论：
---   ① 这根骨自己的世界 z —— 写进去之后**变了**，才说明"写骨"这个杠杆存在；
---   ② 头骨的世界 z —— 子节点跟没跟。跟了才说明整条骨链都能被抬；
---   ③ 护甲上**同名骨**的世界 z —— 网格渲不渲染就看它（渲染读的是这个值）。
--- 根节点那次的教训就是只看了"写了没报错"：结果相机动了、网格没动。
--- </para>
local function lift_bone(S_, runtime, name, metres)
	local avatar = runtime.avatar
	if avatar == nil or not lift_unit_ok(S_, avatar) then return nil end
	local id = name   -- 引擎调用一律传名字（IdString32 会失效）
	local node = try(S_.Unit.node, avatar, id)
	if type(node) ~= "number" or try(S_.Unit.has_node, avatar, id) ~= true then return nil end

	local state = runtime.boneLift or {}
	runtime.boneLift = state
	local entry = state[name] or {}

	local before = bone_world_z(S_, avatar, id)
	local cx, cy, cz = readVector(S_, try(S_.Unit.local_position, avatar, node))
	local wrote, kept = false, false
	if cx ~= nil then
		local written = entry.written
		if written ~= nil
			and math.abs(cx - written[1]) < 1e-4
			and math.abs(cy - written[2]) < 1e-4
			and math.abs(cz - written[3]) < 1e-4 then
			kept = true
		else
			local ok = pcall(function()
				S_.Unit.set_local_position(avatar, node, S_.Vector3(cx, cy, cz + metres))
			end)
			if ok then
				if runtime.world ~= nil then
					pcall(function() S_.World.update_unit(runtime.world, avatar) end)
				end
				entry.written = { cx, cy, cz + metres }
				entry.base = entry.base or { cx, cy, cz }
				wrote = true
			end
		end
	end
	state[name] = entry

	local after = bone_world_z(S_, avatar, id)
	local headZ = bone_world_z(S_, avatar, "head")
	-- 相机也得读：它的成败和网格是两件事（根节点那次就是"相机动、网格不动"）。
	local cameraZ = nil
	if runtime.world ~= nil and runtime.cache ~= nil then
		local cx, _, cz = cameraPosition(S_, runtime.world, runtime.cache)
		if cx ~= nil then cameraZ = cz end
	end
	local armorZ = nil
	for index = 1, #(runtime.sessions or {}) do
		local armor = runtime.sessions[index].unit
		if armor ~= nil then
			armorZ = bone_world_z(S_, armor, id)
			if armorZ ~= nil then break end
		end
	end
	return { before = before, after = after, head = headZ, camera = cameraZ, armor = armorZ,
		wrote = wrote, kept = kept }
end

--- 每帧调一次。没有调参文件 / `lift` 为 0 时什么都不做（**连读都不读**，只多一次缓存判断）。
--- <param name="S_">引擎命名空间；省略时用安装时注入的那份（单测会显式传）。</param>
local function apply_lift(runtime, S_)
	local api = S_ or S
	if api == nil or runtime == nil then return end
	local values = tune_values(runtime)
	local metres = values.lift
	local every = values.lift_log_frames or (runtime.options and runtime.options.liftLogFrames) or 60
	local boneName = values.text and values.text.lift_bone
	local boneMode = boneName ~= nil and boneName ~= "" and boneName ~= "0" and boneName ~= "none"
		and metres ~= nil and metres ~= 0

	-- 关掉时把写过的值还回去一次。根 unit 会被引擎每帧重设，所以只有"写得住"的
	-- （被父节点带着走的）那类需要显式还原——但两种都还一次更省心，代价是一次写。
	if metres == nil or metres == 0 then
		local boneStates = runtime.boneLift
		if boneStates ~= nil then
			local avatar = runtime.avatar
			if avatar ~= nil and lift_unit_ok(api, avatar) then
				for name, entry in pairs(boneStates) do
					if entry.written ~= nil and entry.base ~= nil then
						local id = name   -- 同上：名字不会失效
						local node = id ~= nil and try(api.Unit.node, avatar, id) or nil
						if type(node) == "number" then
							pcall(function()
								api.Unit.set_local_position(avatar, node,
									api.Vector3(entry.base[1], entry.base[2], entry.base[3]))
							end)
						end
					end
				end
				if runtime.world ~= nil then
					pcall(function() api.World.update_unit(runtime.world, avatar) end)
				end
			end
			runtime.boneLift = nil
			runtime.logger("[physbone][抬升] 已关闭，写过的骨位置还回去了")
		end
		local states = runtime.liftState
		if states ~= nil then
			for index = 1, #states do
				local state = states[index]
				if state ~= nil and state.written ~= nil and state.base ~= nil then
					local unit = state.unit
					if unit ~= nil and lift_unit_ok(api, unit) then
						pcall(function()
							api.Unit.set_local_position(unit, 1,
								api.Vector3(state.base[1], state.base[2], state.base[3]))
						end)
						if runtime.world ~= nil then
							pcall(function() api.World.update_unit(runtime.world, unit) end)
						end
					end
				end
			end
			runtime.liftState = nil
			runtime.logger("[physbone][抬升] 已关闭，写过位置的目标都还原了一次")
		end
		return
	end

	-- ── 骨模式：只写 avatar 的一根骨，并把三个读数打在同一行 ────────────────
	if boneMode then
		if runtime.liftMode ~= ("bone:" .. boneName) then
			runtime.liftMode = "bone:" .. boneName
			runtime.logger("[physbone][抬升] 模式=写骨 %s（+%.3f m；unit 根节点不再动）", boneName, metres)
		end
		local result = lift_bone(api, runtime, boneName, metres)
		if ((runtime.driveFrames or 0) % every) == 0 then
			if result == nil then
				runtime.logger("[physbone][抬升-骨] %s：avatar 上找不到这根骨（或读写失败）", boneName)
			else
				runtime.logger(
					"[physbone][抬升-骨] %s +%.3f 写入=%s 保持=%s | 该骨世界 z %s→%s | 头骨 z %s | 相机 z %s | 护甲同名骨 z %s",
					boneName, metres,
					result.wrote and "是" or "否", result.kept and "是" or "否",
					result.before ~= nil and string.format("%.3f", result.before) or "?",
					result.after ~= nil and string.format("%.3f", result.after) or "?",
					result.head ~= nil and string.format("%.3f", result.head) or "?",
					result.camera ~= nil and string.format("%.3f", result.camera) or "?",
					result.armor ~= nil and string.format("%.3f", result.armor) or "?")
			end
		end
		return
	end

	local world = runtime.world
	local states = runtime.liftState or {}
	runtime.liftState = states
	local wrote, kept, failed = 0, 0, 0

	local function apply(unit, index, tag)
		local state = states[index]
		if state == nil then
			state = { unit = unit }
			states[index] = state
		end
		state.unit = unit
		state.tag = tag
		local didWrite, didKeep = lift_unit(api, world, unit, state, metres)
		if didWrite then
			wrote = wrote + 1
		elseif didKeep then
			kept = kept + 1
		else
			failed = failed + 1
		end
	end

	for index = 1, #(runtime.avatars or {}) do
		local avatar = runtime.avatars[index]
		if avatar ~= nil and avatar.unit ~= nil and lift_unit_ok(api, avatar.unit) then
			apply(avatar.unit, index, "avatar")
		end
	end
	if values.lift_armor ~= nil and values.lift_armor ~= 0 then
		for index = 1, #(runtime.sessions or {}) do
			local session = runtime.sessions[index]
			if session ~= nil and session.unit ~= nil and lift_unit_ok(api, session.unit) then
				apply(session.unit, #(runtime.avatars or {}) + index, "armor")
			end
		end
	end

	-- 首次生效时说一次，免得"日志里没动静"分不清是没打开还是没生效。
	if runtime.liftActive ~= metres then
		runtime.liftActive = metres
		runtime.logger("[physbone][抬升] 已打开：每个目标根节点 +%.3f m（护甲件%s）；"
			.. "看三件事——视线有没有跟着抬、枪和手有没有错位、脚下还陷不陷",
			metres, values.lift_armor ~= nil and values.lift_armor ~= 0 and "也抬" or "不动")
	end

	local every = values.lift_log_frames or (runtime.options and runtime.options.liftLogFrames) or 60
	if ((runtime.driveFrames or 0) % every) ~= 0 then return end

	-- 关键那一行：相机 z 与 avatar 根 / 头骨的 z 放在一起。
	-- 相机跟着走 ⇒ 「相机−头骨」恒为常数；不跟 ⇒ 相机 z 原地不动、差值随抬升变大。
	local cameraZ = nil
	if runtime.world ~= nil and runtime.cache ~= nil then
		local cx, _, cz = cameraPosition(api, runtime.world, runtime.cache)
		if cx ~= nil then cameraZ = cz end
	end
	local rootZ, headZ = nil, nil
	local avatarUnit = runtime.avatar
	if avatarUnit ~= nil and lift_unit_ok(api, avatarUnit) then
		local _, _, rz = readVector(api, try(api.Unit.world_position, avatarUnit, 1))
		rootZ = rz
		local headId = "head"   -- 名字，不是 id（IdString32 会失效）
		if try(api.Unit.has_node, avatarUnit, headId) == true then
			local node = try(api.Unit.node, avatarUnit, headId)
			if type(node) == "number" then
				local _, _, hz = readVector(api, try(api.Unit.world_position, avatarUnit, node))
				headZ = hz
			end
		end
	end
	runtime.logger(
		"[physbone][抬升] +%.3f m 写入=%d 保持=%d 失败=%d | avatar 根 z=%s 头骨 z=%s 相机 z=%s 相机−头骨=%s",
		metres, wrote, kept, failed,
		rootZ ~= nil and string.format("%.3f", rootZ) or "?",
		headZ ~= nil and string.format("%.3f", headZ) or "?",
		cameraZ ~= nil and string.format("%.3f", cameraZ) or "?",
		(cameraZ ~= nil and headZ ~= nil) and string.format("%+.3f", cameraZ - headZ) or "?")
end

-- ---------------------------------------------------------------------------
-- 调度器：谁算"自己"、以及什么时候重新判定
-- ---------------------------------------------------------------------------
--
-- 语义只有一条：**相机附近的那个人拿最高物理权**。
-- 这条语义同时覆盖了三种情况，所以不需要分别处理：
--   ① 第一/第三人称正常游玩 —— 相机在自己身上，"自己"永远排第一；
--   ② 观战/过场把相机拉到队友身上 —— 被观看的人自动变成热点，
--      用户在任何视角下都不会看到"我在看的那个人物理像 PPT"；
--   ③ 身份判错（相机读不到时退化成按节点数排）—— 队友跑远 1 秒后自动纠正。
--
-- 关键约束：重置**只重排、不重挂**。改 owner/ownerOrder 不释放会话、不重新 attach，
-- 所以不会闪、不会掉物理、也不需要重新读容器（那一步是几千次引擎调用）。

--- 调度器参数（走调参文件，可运行时改）
local function sched_settings(runtime)
	local values = tune_values(runtime) or {}
	return {
		fallback = (values.schedFallback or 1) ~= 0,
		metres = values.schedFallbackMetres or 6,
		seconds = values.schedFallbackSeconds or 1,
		minInterval = values.schedMinIntervalSeconds or 1,
		pollFrames = math.max(1, math.floor(values.schedPollFrames or 30)),
		-- 手动重设通道（见 sched_tune_poll / sched_key_poll）
		resetToken = values.schedReset,
		hotkey = (values.schedHotkey or 1) ~= 0,
		hotkeyName = (values.text and values.text.schedHotkeyName) or "f8",
		-- 第一人称探针的键（默认 f11；写 none 可关掉）
		fpProbeKey = (values.text and values.text.fpProbeKey) or "f11",
		-- 第一人称锁定：水平距离阈值（米）与确认帧数
		fpLock = (values.fpLock or 1) ~= 0,
		fpLockMetres = values.fpLockMetres or 0.35,
		fpLockFrames = math.max(1, math.floor(values.fpLockFrames or 3)),
	}
end

--- 把一个会话重新归属到"离它最近的 avatar"。
--- 会话是护甲件，玩家那套护甲挂在 avatar 骨架上、位置几乎重合，所以这是一个可靠判据。
local function nearest_avatar(S_, avatars, session)
	local sx, sy, sz = readVector(S_, try(S_.Unit.world_position, session.unit, 1))
	if sx == nil then return nil end
	local best, bestDistance = nil, math.huge
	for index = 1, #avatars do
		local avatar = avatars[index]
		if avatar.x ~= nil then
			local dx, dy, dz = sx - avatar.x, sy - avatar.y, sz - avatar.z
			local distance = dx * dx + dy * dy + dz * dz
			if distance < bestDistance then best, bestDistance = avatar, distance end
		end
	end
	return best
end

--- 按相机距离重排玩家顺序，并把每个会话重新归属。
---@param forcePrimary any|nil 强制排第一的 avatar unit（第一人称锁定用）
---@return number changed 归属真正发生变化的会话数
local function redistribute(S_, runtime, why, forcePrimary)
	local avatars = runtime.avatars
	if avatars == nil or #avatars == 0 then return 0 end
	local camX, camY, camZ = cameraPosition(S_, runtime.world, runtime.cache or {})
	local haveCamera = camX ~= nil
	for index = 1, #avatars do
		local avatar = avatars[index]
		local ax, ay, az = readVector(S_, try(S_.Unit.world_position, avatar.unit, 1))
		avatar.x, avatar.y, avatar.z = ax, ay, az
		if haveCamera and ax ~= nil then
			local dx, dy, dz = ax - camX, ay - camY, az - camZ
			avatar.cameraDistance = dx * dx + dy * dy + dz * dz
		else
			avatar.cameraDistance = nil
		end
	end
	table.sort(avatars, function(a, b)
		local da, db = a.cameraDistance, b.cameraDistance
		if da ~= nil and db ~= nil then return da < db end
		if da ~= nil then return true end
		if db ~= nil then return false end
		return (a.nodes or 0) > (b.nodes or 0)
	end)
	-- 强制置顶：第一人称锁定的证据（水平距离≈0）比"总距离最近"强得多——
	-- 队友完全可以比你离相机更近，但他的水平分量不可能≈0。
	--
	-- **学到过"哪个是自己"就一直沿用。** 第三人称的相机在你身后约 4.5 m，
	-- 队友只要站得比相机离你更近，就会把你顶到 order>1，你的护甲被按 4~8 帧节流，
	-- 看起来就是"队友有物理、我没有"。而 F8 只是重跑同一个判据，所以按了没效果
	-- （2026-11-11 实机）。身份一旦被第一人称锁定学到，就让 F8 / 兜底重排都用它，
	-- 这两条路径才真正能把你恢复成最高权。
	local forced = forcePrimary
	if forced == nil and runtime.selfUnit ~= nil then forced = runtime.selfUnit end
	if forced ~= nil then
		for index = 1, #avatars do
			if avatars[index].unit == forced and index > 1 then
				local forcedAvatar = table.remove(avatars, index)
				table.insert(avatars, 1, forcedAvatar)
				break
			end
		end
	end
	for index = 1, #avatars do avatars[index].order = index end

	-- 最高权换人时说一声（只在真正的换人时打，避免刷屏）。
	-- 这段时间的排障全靠这一行：能直接看出"最高权是队友还是自己"。
	local primary = avatars[1]
	if primary ~= nil and runtime.primaryTag ~= primary.tag then
		runtime.primaryTag = primary.tag
		runtime.logger("[physbone][调度] 最高权=%s（相机距离 %.2f m%s）",
			tostring(primary.tag or "?"),
			primary.cameraDistance ~= nil and math.sqrt(primary.cameraDistance) or -1,
			(primary.unit == runtime.selfUnit) and "，已识别为自己" or "")
	end

	local changed, moved = 0, {}
	for _, session in ipairs(runtime.sessions or {}) do
		local best = nearest_avatar(S_, avatars, session)
		if best ~= nil then
			if session.owner ~= best.unit then
				changed = changed + 1
				moved[#moved + 1] = string.format("%s→%s", tostring(session.unitTag or "?"),
					tostring(best.tag or "?"))
				session.owner = best.unit
				session.ownerTag = best.tag or unit_tag(S_, best.unit)
			end
			session.ownerOrder = best.order
		end
	end
	-- 会话顺序也按新归属排：drive 里"第一位玩家不参与分帧"，靠的就是这个顺序
	table.sort(runtime.sessions or {}, function(a, b)
		return (a.ownerOrder or 99) < (b.ownerOrder or 99)
	end)

	runtime.avatars = avatars
	runtime.schedResets = (runtime.schedResets or 0) + 1
	runtime.schedLastResetFrame = runtime.frame
	-- 只在**真变了**的时候打日志：自由相机那种"附近谁都没有"的状态下，
	-- 这条规则会每秒触发一次，每次打一行就会把日志刷满，而且那些行没有任何信息量。
	if changed > 0 then
		runtime.logger("[physbone][调度] 重新分配（%s）：%d 个会话换了主人 %s%s",
			why, changed, table.concat(moved, " "),
			haveCamera and "" or "（相机不可读 ⇒ 只按节点数排，判据退化）")
	else
		runtime.schedNoop = (runtime.schedNoop or 0) + 1
	end
	return changed
end

--- 第一人称锁定：相机在自己根节点的**正上方** ⇒ **水平距离 ≈ 0**。
--- <para>
--- 实测（2026-11-11）：第一人称下"相机到自己根节点"的总距离稳定在 1.717~1.727 m
--- （±5 mm），第三人称是 4.50~4.52 m。但**总距离不能当判据**：
--- 队友站在你面前 1.7 m 处，总距离也是 1.7 m（相机在眼高、他的根在地面）。
--- 真正伪造不了的是水平分量——别人的身体不可能站在你相机的那个点上（会碰撞）。
--- 垂直分量对所有站在同层地面的人都约等于眼高，因此没有区分能力。
--- </para>
--- 语义：水平 < fpLockMetres 且连续 fpLockFrames 帧 ⇒ 锁定为"自己"；
--- 若它当前不是最高权目标，立刻重排（不等 1 秒兜底）。
local function fp_lock_watch(S_, runtime)
	local settings = sched_settings(runtime)
	if not settings.fpLock then return end
	local avatars = runtime.avatars
	if avatars == nil or #avatars == 0 then return end
	local camX, camY = frameCamera(S_, runtime)
	if camX == nil then return end
	local lockAvatar, lockH = nil, nil
	for index = 1, #avatars do
		local avatar = avatars[index]
		if avatar.x ~= nil then
			local dx, dy = avatar.x - camX, avatar.y - camY
			local h = math.sqrt(dx * dx + dy * dy)
			if h < settings.fpLockMetres and (lockH == nil or h < lockH) then
				lockAvatar, lockH = avatar, h
			end
		end
	end
	if lockAvatar == nil then
		runtime.fpLockFrames = 0
		runtime.fpLockUnit = nil
		return
	end
	if runtime.fpLockUnit ~= lockAvatar.unit then
		runtime.fpLockUnit = lockAvatar.unit
		runtime.fpLockFrames = 1
		runtime.fpLockH = lockH
		return
	end
	runtime.fpLockFrames = (runtime.fpLockFrames or 0) + 1
	runtime.fpLockH = lockH
	-- 只在"恰好达到帧数"时动作一次；动作后清零，这样万一又被判错还能再纠正。
	if runtime.fpLockFrames ~= settings.fpLockFrames then return end
	-- 学到"自己是谁"。**即使它已经是 order==1 也要记下来**：第三人称下队友可能把你顶掉，
	-- 之后 F8 / 兜底重排要靠这个身份把你抢回来（见 redistribute 里的沿用逻辑）。
	if runtime.selfUnit ~= lockAvatar.unit then
		runtime.selfUnit = lockAvatar.unit
		runtime.logger("[physbone][调度] 记住自己=%s（水平 %.3f m）⇒ 之后 F8 与兜底重排优先它",
			tostring(lockAvatar.tag or "?"), lockH)
	end
	if (lockAvatar.order or 99) == 1 then return end
	runtime.logger("[physbone][调度] 第一人称锁定（水平 %.3f m，%s）⇒ 立即重排",
		lockH, tostring(lockAvatar.tag or "?"))
	redistribute(S_, runtime, "第一人称锁定", lockAvatar.unit)
	runtime.fpLockFrames = 0
end

--- 兜底：**最高物理权单位**离开相机超过 `metres` 连续 `seconds` 秒 ⇒ 立刻重排。
--- <para>
--- 为什么用"它离相机远"而不是"别人离相机近"：前者只需要一次距离读取，
--- 而且它把"观战即热点"这件事变成同一条规则的自然结果（相机换目标 ⇒ 1 秒后热点换人）。
--- </para>
--- 限流：最短间隔 `minInterval` 秒。**不做**"连续触发就停用"——持续触发恰恰说明
--- 附近确实没有玩家（自由相机），此时每秒重排一次的代价（约 50 次引擎调用）
--- 远小于它带来的正确性。连续没有变化时会自动退避，见下。
local function scheduler_watch(S_, runtime, dt)
	local settings = sched_settings(runtime)
	if not settings.fallback then return end
	local avatars = runtime.avatars
	if avatars == nil or #avatars == 0 then return end
	local primary = avatars[1]
	if primary == nil or primary.unit == nil then return end
	local camX, camY, camZ = frameCamera(S_, runtime)
	if camX == nil then return end
	-- 位置同样优先取本帧 Roster 已读到的（physics_sync 填进 runtime.avatars）。
	local ax, ay, az
	if primary.positionFrame == runtime.frame and primary.x ~= nil then ax, ay, az = primary.x, primary.y, primary.z end
	if ax == nil then ax, ay, az = readVector(S_, try(S_.Unit.world_position, primary.unit, 1)) end
	if ax == nil then return end
	local dx, dy, dz = ax - camX, ay - camY, az - camZ
	local distance = math.sqrt(dx * dx + dy * dy + dz * dz)

	if distance <= settings.metres then
		if (runtime.schedFar or 0) > 0 then runtime.schedFar = 0 end
		return
	end
	runtime.schedFar = (runtime.schedFar or 0) + (dt or (1 / 60))
	-- 退避期：判据仍然有效，但降到 1/10 的频率（附近确实没人时别每秒重排）。
	local need = settings.seconds
	if runtime.schedBackoffUntil ~= nil and runtime.frame < runtime.schedBackoffUntil then
		need = need * 10
	end
	if runtime.schedFar < need then return end

	-- 到点了。限流：上一次重置太近就再等等（保留累积值，下一帧会再试）。
	local last = runtime.schedLastResetFrame
	local since = last ~= nil and (runtime.frame - last) * (dt or (1 / 60)) or math.huge
	if since < settings.minInterval then return end

	runtime.schedFar = 0
	local changed = redistribute(S_, runtime, string.format("最高权单位距相机 %.1f m > %.0f m 持续 %.1f s",
		distance, settings.metres, settings.seconds))
	if changed == 0 then
		-- 连续多次重排都没有任何变化 ⇒ 附近确实没人（自由相机/空镜头）。
		-- 退避到 10 倍间隔，既保住正确性又不做无意义的每秒重排；有变化时立刻恢复。
		if (runtime.schedNoop or 0) >= 3 then
			runtime.schedBackoffUntil = runtime.frame
				+ math.floor(settings.minInterval * 10 * 120)
		end
	else
		runtime.schedNoop = 0
	end
end

--- 每帧只刷新"每个玩家到相机的距离"，**不重排、不重新归属**。
--- 为什么要单独这一步：分档要看新鲜距离（一秒钟前的距离可能差出十几米），
--- 但重排是"事件级"的事（见 scheduler_watch）。这里只有 ≤4 次引擎调用。
local function refresh_avatar_distances(S_, runtime)
	local avatars = runtime.avatars
	if avatars == nil or #avatars == 0 then return end
	-- physics_sync 本帧已经按 Roster 的枚举结果填好位置与到相机的距离：不再重读。
	if runtime.avatarsFreshFrame == runtime.frame then return end
	local camX, camY, camZ = cameraPosition(S_, runtime.world, runtime.cache or {})
	runtime.poseCamera = { frame = runtime.frame, x = camX, y = camY, z = camZ, world = runtime.world }
	if camX == nil then return end
	for index = 1, #avatars do
		local avatar = avatars[index]
		-- 防御：名单可能被别处复用；这里再按指针确认一次。
		if avatar.unit ~= nil and session_unit_live(runtime, avatar.unit) then
			local ax, ay, az = readVector(S_, try(S_.Unit.world_position, avatar.unit, 1))
			if ax ~= nil then
				avatar.x, avatar.y, avatar.z, avatar.positionFrame = ax, ay, az, runtime.frame
				local dx, dy, dz = ax - camX, ay - camY, az - camZ
				avatar.cameraDistance = dx * dx + dy * dy + dz * dz
			end
		end
	end
end

--- 会话所属玩家到相机的距离平方。只查上面刷新的缓存，**不**调引擎。
local function owner_distance_sq(runtime, session)
	local owner = session.owner
	if owner == nil then return math.huge end
	local avatars = runtime.avatars
	if avatars == nil then return math.huge end
	for index = 1, #avatars do
		if avatars[index].unit == owner then return avatars[index].cameraDistance or math.huge end
	end
	return math.huge
end

--- 预算 → 两档步长（纯函数，可离线单测）。
--- <para>
--- 语义：**自己永远每帧**（不参与节流，这一条不可协商）；
--- 6 m 内先保证 2 帧一次，链数真的很多时才退到 3 帧一次；
--- 6 m 外从 4 帧起，吃紧时最多 8 帧一次。
--- 估算方式按"链数/步长"分摊到每帧，和实测的"约 3.5 次引擎调用/链"线性对应。
--- </para>
---@return number nearStride, number farStride
local function schedule_budget(primaryChains, nearChains, farChains, budget)
	if primaryChains + nearChains + farChains <= budget then return 1, 1 end
	local nearStride, farStride = 2, 4
	local function cost()
		return primaryChains + nearChains / nearStride + farChains / farStride
	end
	-- 阶梯加长（2026-11-12）：原来 far 最多 8、near 最多 3。四人局里自己那条动不了
	-- （primary 不参与节流），只靠 near≤3 / far≤8 压不到预算内 —— 船上全员 6 m 内时
	-- 实测会到 ~10 ms。把阶梯放长到 near≤6 / far≤16，才能把总链数拉回预算附近，
	-- 代价是远处队友的物理更稀（他们本来就看不见）。
	while cost() > budget do
		if farStride < 16 then
			farStride = math.min(16, farStride * 2)
		elseif nearStride < 6 then
			nearStride = math.min(6, nearStride + 1)
		else
			break
		end
	end
	-- 近处本来就没几条链时不必节流：让 6 m 内的队友完全每帧（那是他们最被看见的时候）
	if nearChains <= budget * 0.25 then nearStride = 1 end
	return nearStride, farStride
end


--- 手动重设的两条通道。**产品路径不得依赖外部脚本**（这套物理要随管理器分发给大量玩家），
--- 所以按"零依赖优先"排列：
---
---   ① 调参文件里的 `schedReset`：写成**任意一个新的数字**就等于按了一次重置。
---      零依赖、纯 Lua（读调参文件本来就有），玩家改一个数字即可，不需要任何额外程序。
---   ② Stingray 自带的 `Keyboard` API（如果这个版本暴露了）——真正的一键热键。
---      是否可用只能**实机探测**，探测结果打一行日志（见 probe_input）。
--- 外部脚本写文件那条通道**已经删除**：玩家机器上不会有我们开发用的监听器。

--- ① 调参文件通道：值变化即触发（首次读到只记录，不触发——启动时刚排好序，没必要）。
local function sched_tune_poll(S_, runtime)
	local settings = sched_settings(runtime)
	if settings.resetToken == nil then return end
	local seen = runtime.schedResetSeen
	runtime.schedResetSeen = settings.resetToken
	if seen == nil or seen == settings.resetToken then return end
	redistribute(S_, runtime, string.format("手动重设（调参 schedReset=%s）",
		tostring(settings.resetToken)))
	runtime.schedNoop = 0
	runtime.schedBackoffUntil = nil
end

--- 读一个键，返回"这一帧刚按下"（上升沿）。绑定/失败都只记一次日志。
--- <para>
--- ⚠️ 实机实测（2026-11-11）：`Keyboard.button` 返回的是 **`0` / `1`，不是布尔**。
--- Lua 里 `0` 是**真值**，所以写成 `if value then` 会变成"永远按住"——每帧都会触发。
--- 这里必须同时接受 `true` 和"数字非 0"两种语义。
--- </para>
local function key_edge(S_, runtime, name)
	if type(name) ~= "string" or #name == 0 then return false end
	local kb = S_.Keyboard
	if type(kb) ~= "table" or type(kb.button) ~= "function" then return false end
	local slots = runtime.keySlots
	if slots == nil then slots = {}; runtime.keySlots = slots end
	local slot = slots[name]
	if slot == nil then
		slot = { mode = nil }
		slots[name] = slot
		-- 键名拼写各版本不一致（"f8" / "F8"），调用约定也不一致（code / 名字），两种都试。
		local candidates = { name, string.lower(name), string.upper(name) }
		for _, spelling in ipairs(candidates) do
			if slot.mode == nil and type(kb.button_index) == "function" then
				local okIndex, code = pcall(kb.button_index, spelling)
				if okIndex and code ~= nil and pcall(kb.button, code) then
					slot.mode, slot.code, slot.spelling = "code", code, spelling
				end
			end
			if slot.mode == nil and pcall(kb.button, spelling) then
				slot.mode, slot.spelling = "name", spelling
			end
		end
		if slot.mode == nil then
			slot.broken = true
			runtime.logger("[physbone][热键] 试过 %s 仍读不到（Keyboard 存在但调用不通）⇒ 停用",
				table.concat(candidates, "/"))
			return false
		end
		runtime.logger("[physbone][热键] 已绑定 %s（拼写=%s，调用约定=%s，code=%s）",
			name, tostring(slot.spelling), slot.mode, tostring(slot.code))
	end
	if slot.broken then return false end
	local ok, value
	if slot.mode == "code" then
		ok, value = pcall(kb.button, slot.code)
	else
		ok, value = pcall(kb.button, slot.spelling)
	end
	if not ok then
		slot.broken = true
		runtime.logger("[physbone][热键] 读取 %s 时抛错 ⇒ 停用", name)
		return false
	end
	local isDown = (value == true) or (type(value) == "number" and value ~= 0)
	local wasDown = slot.down == true
	slot.down = isDown
	if isDown == wasDown then return false end
	if not isDown then
		runtime.logger("[physbone][热键] %s 松开", name)
		return false
	end
	runtime.keyEvents = (runtime.keyEvents or 0) + 1
	return true
end

--- ② 手动重设热键（默认 F8）：按下沿触发一次重排。
local function sched_key_poll(S_, runtime)
	local settings = sched_settings(runtime)
	if not settings.hotkey then return end
	if not key_edge(S_, runtime, settings.hotkeyName) then return end
	runtime.logger("[physbone][热键] %s 按下 ⇒ 重排（玩家 %d 个，会话 %d 个）",
		settings.hotkeyName, #(runtime.avatars or {}), #(runtime.sessions or {}))
	redistribute(S_, runtime, "手动重设（热键 " .. settings.hotkeyName .. "）")
	-- E12e：发现/补挂/退避重试（reconcile、scoreAttemptAt、negativeRetryAt）都已按 IAS 删除——
	-- 每帧枚举本来就每帧重新发现并绑定，热键只需要重排一次。
	runtime.schedNoop = 0
	runtime.schedBackoffUntil = nil
end

--- 第一人称探针（默认 F11）：按一次打**一行**——相机位置 + 每个玩家到相机的距离。
--- <para>
--- 要回答的问题：第一人称下"自己与相机的距离"是不是固定且接近 0？
--- 如果是（且第三人称是 2~4 m），那它就比"离相机最近的那个"可靠得多，
--- 可以用来做**精确身份判据**（锁定 ⇒ 3 帧内立即重排，不等 1 秒兜底）。
--- 采用按键触发而不是定时打日志：只要切换前后的两个瞬时值，不需要刷屏。
--- </para>
local function fp_probe_poll(S_, runtime)
	local settings = sched_settings(runtime)
	if not settings.fpProbeKey then return end
	if not key_edge(S_, runtime, settings.fpProbeKey) then return end
	local avatars = runtime.avatars or {}
	local camX, camY, camZ = cameraPosition(S_, runtime.world, runtime.cache or {})
	local parts = {}
	for index = 1, #avatars do
		local avatar = avatars[index]
		local ax, ay, az = readVector(S_, try(S_.Unit.world_position, avatar.unit, 1))
		local text = "?"
		if camX ~= nil and ax ~= nil then
			local dx, dy, dz = ax - camX, ay - camY, az - camZ
			local horizontal = math.sqrt(dx * dx + dy * dy)
			text = string.format("%.4fm(h=%.4f,v=%+.4f)",
				math.sqrt(dx * dx + dy * dy + dz * dz), horizontal, dz)
		end
		parts[#parts + 1] = string.format("%s=%s%s", tostring(avatar.tag or "?"), text,
			(avatar.order or 99) == 1 and "(主)" or "")
	end
	local chains = 0
	for _, session in ipairs(runtime.sessions or {}) do
		chains = chains + #(session.sessions or {})
	end
	runtime.logger(
		"[physbone][FP探针] 相机=(%.3f,%.3f,%.3f) 玩家=%d 会话=%d 链=%d 距离: %s",
		camX or 0, camY or 0, camZ or 0, #avatars, #(runtime.sessions or {}), chains,
		#parts > 0 and table.concat(parts, " ") or "（没有玩家）")
end

--- ③ 已删除：外部脚本写文件的通道。
--- 这套物理要随管理器分发给大量玩家，产品路径不能依赖任何外部程序
--- （玩家机器上不会有我们开发用的 Python 监听器）。上面两条已经够用：
--- 原生键盘能用就一键，不能用就改调参文件里的一个数字。

--- 输入 API 一次性探测：只**读类型**（不调有副作用的函数），把结果打进日志。
--- 为什么要它：能不能绑原生热键完全取决于这个版本暴露了什么，
--- 而"猜一个 API 名字然后静默失败"是这一整层最贵的错误类型。
local function probe_input(S_, runtime)
	if runtime.inputProbed == true then return end
	runtime.inputProbed = true
	local kb = S_.Keyboard
	local parts = { "Keyboard=" .. type(kb) }
	if type(kb) == "table" then
		for _, name in ipairs({ "button", "button_index", "keystroke", "any_button" }) do
			parts[#parts + 1] = name .. "=" .. type(kb[name])
		end
	end
	parts[#parts + 1] = "InputManager=" .. type(S_.InputManager)
	parts[#parts + 1] = "Window=" .. type(S_.Window)
	runtime.logger("[physbone][探测] 输入 API：%s", table.concat(parts, " "))
	if type(kb) == "table" and type(kb.button_index) == "function" and type(kb.button) == "function" then
		local okCode, code = pcall(kb.button_index, "f8")
		local down = nil
		if okCode and code ~= nil then
			local okDown, value = pcall(kb.button, code)
			if okDown then down = value end
		end
		runtime.logger("[physbone][探测] F8 试读：code=%s down=%s", tostring(code), tostring(down))
	end
end


--- 每帧统一入口。成本：一次调参比对 + 两个键各读一次（`Keyboard.button` 是纯查询）。
local function sched_poll(S_, runtime)
	probe_input(S_, runtime)
	sched_tune_poll(S_, runtime)
	sched_key_poll(S_, runtime)
	fp_probe_poll(S_, runtime)
end




-- ---------------------------------------------------------------------------
-- 姿态快照：按一下键，把当前玩家这一具骨架的世界变换写到盘上
--
-- 为什么需要它：穿模这件事**只在动起来时看得见**，而离线只有 bind 姿态。
-- 2026-11-09 的实测把这一点钉死了：绑定时 7 个挂在 hips 上的链根离髋部表面
-- +4.6~+9.9 cm（全在外面），真正插进身体的是**皮面**（裙子上沿，最深 −1.5 cm）。
-- 也就是说"看骨点有没有越界"这个判据永远不成立，必须拿到那一帧的骨变换，
-- 在 PC 侧把网格蒙皮还原出来再判。
--
-- 为什么是"外部写请求、Lua 轮询"而不是在 Lua 里读键盘：
-- 这个游戏的输入走 Raw Input（见 tools/game-input.py 的注释），Lua 侧没有可信的按键 API；
-- 而"请求文件 → 轮询 → 应答文件"这条路在本仓已经有成熟先例
-- （HD2RuntimeArmorOverride/src/armor_refresh_hotkey.lua 就是这套）。
--
-- 请求文件是**按行的纯文本**，不是 JSON：解析器只需要认得 `key=value`，
-- 少一层转义就少一类静默失败。节点用 32 位哈希给（引擎的 IdString32），
-- 因为转换产物里没有 Bone 资源、节点名根本不存在，只有哈希。
-- ---------------------------------------------------------------------------
local SNAPSHOT_MAX_NODES = 1024
local SNAPSHOT_MAX_FRAMES = 32
local SNAPSHOT_POLL_FRAMES = 30

--- 快照目录。默认 `%LOCALAPPDATA%\hd2physbone-snap`，`options.snapshotDir` 可覆盖，
--- `options.snapshot == false` 可彻底关掉。
local function snapshot_dir(options)
	local dir = options and options.snapshotDir
	if type(dir) == "string" and #dir > 0 then return dir end
	if options and options.snapshot == false then return nil end
	local base = os.getenv("LOCALAPPDATA")
	if type(base) == "string" and #base > 0 then return base .. "\\hd2physbone-snap" end
	return nil
end

--- 解析请求文本。任何一行看不懂就跳过，不因此整份作废——
--- 拿到半份数据也比"按一下什么都没发生"强。
local function parse_snapshot_request(text)
	if type(text) ~= "string" or #text == 0 then return nil end
	local request = { frames = 8, stride = 2, limit = 0, nodes = {}, names = {}, seq = nil }
	for rawLine in text:gmatch("[^\r\n]+") do
		local line = rawLine:gsub("^%s+", ""):gsub("%s+$", "")
		if #line > 0 and line:sub(1, 1) ~= "#" then
			local key, value = line:match("^([%w_]+)%s*=%s*(.-)$")
			if key ~= nil then
				key = key:lower()
				if key == "frames" then
					local count = tonumber(value)
					if count ~= nil then
						request.frames = math.max(1, math.min(SNAPSHOT_MAX_FRAMES, math.floor(count)))
					end
				elseif key == "stride" then
					local stride = tonumber(value)
					if stride ~= nil then
						request.stride = math.max(1, math.min(30, math.floor(stride)))
					end
				elseif key == "auto" then
					-- 自动清单：用运行时自己正在驱动的链骨名 + 固定关节名。换护甲也不用改请求。
					request.auto = (value == "1" or value == "true")
				elseif key == "probe" then
					-- 探针模式：把前几个 id 的每一步结果（类型 + 返回值）打出来。
					-- 为什么需要：按骨名采回来 13 个全"缺"，而同一套 API 在求解器里是好的，
					-- 光看"缺 13"根本分不清是 has_node 假、node 假还是读位置假。
					request.probe = (value == "1" or value == "true")
				elseif key == "limit" then
					-- 只采前 N 个节点。为什么需要：一次按键里读 685 个节点 ≈ 2700 次引擎调用，
					-- 2026-11-09 实测直接把游戏读崩了。先用小的探路，再放量。
					local count = tonumber(value)
					if count ~= nil then request.limit = math.max(0, math.min(SNAPSHOT_MAX_NODES, math.floor(count))) end
				elseif key == "seq" then
					request.seq = value
				elseif key == "node" then
					local hash = tonumber((value:gsub("^0[xX]", "")), 16)
					if hash ~= nil and #request.nodes < SNAPSHOT_MAX_NODES then
						request.nodes[#request.nodes + 1] = hash
					end
				elseif key == "name" then
					if #value > 0 and #request.names < 128 then
						request.names[#request.names + 1] = value
					end
				end
			end
		end
	end
	if #request.nodes == 0 and #request.names == 0 and request.auto ~= true then return nil end
	return request
end

--- 把请求里的"哈希 + 名字"并成一张 id 表（名字按引擎的 IdString32 现算）。
local function snapshot_ids(S_, request)
	local ids, seen = {}, {}
	local limit = request.limit or 0
	local function push(id, label, name)
		if id == nil and name == nil then return end
		local key = id or ("name:" .. name)
		if seen[key] then return end
		if limit > 0 and #ids >= limit then return end
		seen[key] = true
		ids[#ids + 1] = { id = id, label = label, name = name }
	end
	for _, hash in ipairs(request.nodes or {}) do push(hash, string.format("0x%08x", hash)) end
	-- 名字同时留着原文：万一某些引擎版本 has_node 只认字符串，还能退回去再试一次。
	for _, name in ipairs(request.names or {}) do push(id32(S_, name), name, name) end
	return ids
end

	-- 自动清单要用的固定关节名。这些就是 compat 的 anchor 表里那些 HD2 原生骨名。
local SNAPSHOT_JOINTS =
{
	"hips", "spine", "spine1", "spine2", "chest", "neck", "head",
	"l_thigh", "r_thigh", "l_knee", "r_knee", "l_foot", "r_foot", "l_toe", "r_toe",
	"l_shoulder", "r_shoulder", "l_elbow", "r_elbow", "l_hand", "r_hand",
}

--- 自动清单：**运行时自己知道它在驱动哪些骨**，不需要外部把名字喂进来。
--- 2026-11-09 实测：请求里的名字是"上一局那件护甲"的，换一件就全落空；
--- 而链骨名字每帧都在 `session.sessions[i].chain` 里，直接抄下来最可靠。
local function snapshot_auto_ids(runtime)
	local ids, seen = {}, {}
	local function pushName(name)
		if type(name) ~= "string" or #name == 0 or seen[name] then return end
		if #ids >= SNAPSHOT_MAX_NODES then return end
		seen[name] = true
		ids[#ids + 1] = { name = name, label = name }
	end
	for _, name in ipairs(SNAPSHOT_JOINTS) do pushName(name) end
	for _, session in ipairs(runtime.sessions or {}) do
		for _, item in ipairs(session.sessions or {}) do
			for _, bone in ipairs(item.chain or {}) do pushName(bone.name) end
		end
	end
	return ids
end

--- 拍一帧：逐节点读世界位置 + 世界旋转，顺带把每条链的弹簧状态抄下来。
--- 所有引擎调用都包 pcall：一根骨读不出来不能让整次快照作废。
---
--- `units` 是**一串**候选 unit（不是单个）：一具护甲被拆成头盔/胸/髋/腿…好几个 unit，
--- 同一个骨名只在其中某一个上存在。只试第一个 unit 就会全军覆没——
--- 2026-11-09 实测：`hips` 和求解器正在驱动的链骨都报"缺"，就因为只试了头盔那一个。
local function snapshot_frame(S_, runtime, units, ids, frame)
	local nodeRows, missing = {}, 0
	local probe = runtime.snapshotState ~= nil and runtime.snapshotState.request ~= nil
		and runtime.snapshotState.request.probe == true
	for index = 1, #ids do
		local entry = ids[index]
		local detail, row, hitUnit = nil, nil, nil
		if probe then detail = "id=" .. tostring(entry.id) .. "(" .. type(entry.id) .. ") " end
		for unitIndex = 1, #units do
			local unit = units[unitIndex].unit
			-- ⚠️ 一律**先用名字字符串**：实测 `Application.string_to_id32` 返回的是
			-- `IdString32` userdata，而且它是**会失效的**（探针日志里直接印出 `[Stale IdString32]`）。
			-- 把它存起来跨帧用，`has_node` 一律返回 false —— 这正是"按哈希采回来 0 个"的原因。
			-- 名字字符串没有这个问题，`has_node(unit, "hips")` 稳定为 true。
			local key = entry.name
			local okHas, has = false, false
			if key ~= nil then
				okHas, has = pcall(S_.Unit.has_node, unit, key)
			end
			if not (okHas and has == true) and entry.id ~= nil then
				-- 只有哈希、没有名字的条目：现算一次 id32 再用（不跨帧缓存）。
				okHas, has = pcall(S_.Unit.has_node, unit, entry.id)
				if okHas and has == true then key = entry.id end
			end
			if probe then detail = detail .. string.format("u%d=%s ", unitIndex, tostring(has)) end
			if okHas and has == true then
				local okNode, node = pcall(function() return S_.Unit.node(unit, key) end)
				if probe then detail = detail .. "node=" .. tostring(node) .. "(" .. type(node) .. ") " end
				if okNode and node ~= nil then
					local okPos, px, py, pz = pcall(function()
						return S_.Vector3.to_elements(S_.Unit.world_position(unit, node))
					end)
					-- 四元数必须与 world_rotation 在同一次调用内取完（引擎句柄寿命问题，见 compat 的 read_quat4）
					local okRot, qx, qy, qz, qw = pcall(function()
						return S_.Quaternion.to_elements(S_.Unit.world_rotation(unit, node))
					end)
					if probe then
						detail = detail .. string.format("pos=%s/%s rot=%s/%s ", tostring(okPos),
							type(px) == "number" and string.format("%.3f", px) or tostring(px),
							tostring(okRot), type(qx) == "number" and string.format("%.3f", qx) or tostring(qx))
					end
					if okPos and type(px) == "number" and okRot and type(qx) == "number" then
						row = {
							id = entry.label,
							unit = unitIndex,
							x = px, y = py, z = pz,
							qx = qx, qy = qy, qz = qz, qw = qw,
						}
						hitUnit = unitIndex
						break
					end
				end
			end
		end
		if row ~= nil then
			nodeRows[#nodeRows + 1] = row
		else
			missing = missing + 1
		end
		if probe and index <= 6 then
			runtime.logger("[physbone][快照][探针] #%d %s⇒ %s", index, tostring(entry.name or entry.label),
				tostring(detail) .. (hitUnit ~= nil and ("命中 u" .. hitUnit) or "全未命中"))
		end
	end

	local chains = {}
	for _, session in ipairs(runtime.sessions or {}) do
		for _, item in ipairs(session.sessions or {}) do
			local bones = {}
			local springs = item.springs or {}
			local chain = item.chain or {}
			for i = 1, #chain do
				local spring = springs[i]
				-- 弹簧角度在求解器里是**弧度**；写出去换成度，省得读的人再做一次换算
				-- （已经因为"限位打印 0°"这种事白查过一轮）。
				bones[#bones + 1] = {
					name = tostring(chain[i] and chain[i].name or "?"),
					yawDeg = spring ~= nil and (spring.yaw or 0) * 180 / math.pi or 0,
					pitchDeg = spring ~= nil and (spring.pitch or 0) * 180 / math.pi or 0,
				}
			end
			chains[#chains + 1] = {
				root = tostring(chain[1] and chain[1].name or "?"),
				anchor = tostring(item.anchorNode ~= nil and "resolved" or "?"),
				bones = bones,
			}
		end
	end

	return { frame = frame, nodes = nodeRows, missing = missing, chains = chains }
end

local function json_number(value)
	if type(value) ~= "number" or value ~= value or value == math.huge or value == -math.huge then
		return "null"
	end
	return string.format("%.6f", value)
end

local function json_string(value)
	local text = tostring(value):gsub('[%z\1-\31\\"]', function(character)
		return string.format("\\u%04x", character:byte())
	end)
	return '"' .. text .. '"'
end

--- 把几帧拼成一份自描述的 JSON。字段名写清楚，是为了**半年后**还能读懂。
local function snapshot_json(runtime, request, unitTag, frames, capturedUtc)
	local parts = {}
	parts[#parts + 1] = string.format(
		'{"schema":"hd2physbone.snap/1","runtime":"%s","seq":%s,"captured":"%s","unit":%s,'
			.. '"framesRequested":%d,"stride":%d,"frames":[',
		tostring(M.VERSION), request.seq ~= nil and json_string(request.seq) or "null",
		capturedUtc, json_string(unitTag), request.frames, request.stride)
	for frameIndex, frame in ipairs(frames) do
		if frameIndex > 1 then parts[#parts + 1] = "," end
		parts[#parts + 1] = string.format('{"frame":%d,"missingNodes":%d,"nodes":[', frame.frame, frame.missing)
		for nodeIndex, node in ipairs(frame.nodes) do
			if nodeIndex > 1 then parts[#parts + 1] = "," end
			parts[#parts + 1] = string.format('{"id":"%s","x":%s,"y":%s,"z":%s,"qx":%s,"qy":%s,"qz":%s,"qw":%s}',
				node.id:gsub('"', ""), json_number(node.x), json_number(node.y), json_number(node.z),
				json_number(node.qx), json_number(node.qy), json_number(node.qz), json_number(node.qw))
		end
		parts[#parts + 1] = '],"chains":['
		for chainIndex, chain in ipairs(frame.chains) do
			if chainIndex > 1 then parts[#parts + 1] = "," end
			parts[#parts + 1] = string.format('{"root":%s,"bones":[', json_string(chain.root))
			for boneIndex, bone in ipairs(chain.bones) do
				if boneIndex > 1 then parts[#parts + 1] = "," end
				parts[#parts + 1] = string.format('{"name":%s,"yawDeg":%s,"pitchDeg":%s}',
					json_string(bone.name), json_number(bone.yawDeg), json_number(bone.pitchDeg))
			end
			parts[#parts + 1] = "]}"
		end
		parts[#parts + 1] = "]}"
	end
	parts[#parts + 1] = string.format('],"driveFrames":%d,"dt":%s}', runtime.driveFrames or 0, json_number(runtime.dt))
	return table.concat(parts)
end

--- 轮询一次请求。只有目录存在、请求内容变了才动手；平时每 30 帧一次 io.open，成本可忽略。
local function snapshot_poll(S_, runtime, options)
	if not io or type(io.open) ~= "function" then return end
	local dir = snapshot_dir(options)
	if dir == nil then return end
	local state = runtime.snapshotState
	if state == nil then
		state = { seq = 0, signature = nil, framesLeft = 0, skip = 0, frames = {}, ids = nil, reported = false }
		runtime.snapshotState = state
	end

	-- ① 采集中：按 stride 每帧记一张，够数就落盘
	if state.framesLeft > 0 then
		if state.skip > 0 then
			state.skip = state.skip - 1
			return
		end
		-- 一具护甲是**一堆 unit**（头盔/胸/髋/腿…），同一个骨名只在其中一个上存在。
		-- 所以这里把所有会话的 unit 都收集起来，逐个试，第一个命中就用它。
		local units, tags = {}, {}
		for _, session in ipairs(runtime.sessions or {}) do
			if session.unit ~= nil then
				units[#units + 1] = { unit = session.unit, tag = tostring(session.unitTag or "?") }
				tags[#tags + 1] = tostring(session.unitTag or "?")
			end
		end
		if #units == 0 then
			-- 还没有挂上任何会话（读图/换甲的空窗期）：**不许消耗帧数**，
			-- 否则会写出一份 0 帧的残文件，看起来像"采到了"，实际什么都没有。
			state.waitFrames = (state.waitFrames or 0) + 1
			if state.waitFrames > (options.snapshotWaitFrames or 600) then
				runtime.logger("[physbone][快照] 等了 %d 帧仍然没有可采的会话，放弃这次请求", state.waitFrames)
				state.framesLeft = 0
				state.frames = {}
				state.waitFrames = 0
			end
			return
		end
		state.waitFrames = 0
		state.skip = (state.request and state.request.stride or 2) - 1
		local sampleIndex = (state.request.frames - state.framesLeft) + 1
		local unitTag = #tags > 0 and string.format("%s 等 %d 个", tags[1], #tags) or "?"
		-- 自动模式下**每帧现算**清单：护甲可能刚换过，链骨名跟着变。
		local ids = state.ids
		if state.request ~= nil and state.request.auto == true then ids = snapshot_auto_ids(runtime) end
		-- 每帧**先**打一行再采：万一这一帧把游戏读崩了，日志里最后一行就是崩在哪一帧、
		-- 采了多少个节点。没有这一行，"闪退"在现场只能靠猜（2026-11-09 就猜了一轮）。
		runtime.logger("[physbone][快照] 开始第 %d/%d 帧：%d 个节点、%d 个候选 unit",
			sampleIndex, state.request.frames, #(ids or {}), #units)
		local sample = snapshot_frame(S_, runtime, units, ids or {}, runtime.frame or 0)
		state.lastTag = unitTag
		state.framesLeft = state.framesLeft - 1
		-- 每帧单独落盘：游戏要是在后面的帧上崩了，前面采到的还在；
		-- "最后写出的那个文件"也直接告诉我们崩在第几帧。
		local name = string.format("snapshot-%04d-s%02d.json", state.seq, sampleIndex)
		local path = dir .. "\\" .. name
		local text = snapshot_json(runtime, state.request, unitTag, { sample }, os.date("%Y-%m-%dT%H:%M:%S"))
		local ok, handle = pcall(io.open, path, "wb")
		if ok and handle ~= nil then
			handle:write(text)
			handle:close()
			runtime.logger("[physbone][快照] 已写出 %s（%d 个节点，缺 %d）", name, #sample.nodes, sample.missing)
		else
			runtime.logger("[physbone][快照] 写不出 %s（目录是否存在？）", path)
		end
		if state.framesLeft > 0 then return end
		state.frames = {}
		local okDone, done = pcall(io.open, dir .. "\\done.txt", "wb")
		if okDone and done ~= nil then
			done:write(string.format("seq=%d frames=%d\n", state.seq, state.request.frames))
			done:close()
		end
		return
	end

	-- ② 空闲：每 N 帧看一次请求文件
	if ((runtime.driveFrames or 0) % (options.snapshotPollFrames or SNAPSHOT_POLL_FRAMES)) ~= 0 then return end
	local okOpen, handle = pcall(io.open, dir .. "\\request.txt", "rb")
	if not okOpen or handle == nil then return end
	local text = handle:read("*a") or ""
	handle:close()
	if #text == 0 or text == state.signature then return end

	local request = parse_snapshot_request(text)
	if request == nil then
		state.signature = text
		if not state.reported then
			state.reported = true
			runtime.logger("[physbone][快照] 请求里没有 node/name 行，忽略")
		end
		return
	end
	state.signature = text
	state.request = request
	state.ids = snapshot_ids(S_, request)
	state.seq = state.seq + 1
	state.frames = {}
	state.framesLeft = request.frames
	state.skip = 0
	runtime.logger("[physbone][快照] 收到请求 seq=%s：%d 帧 × 每 %d 帧一张、%d 个节点",
		tostring(request.seq or "?"), request.frames, request.stride, #state.ids)
end

-- ---------------------------------------------------------------------------
-- 裙摆外推（keep-out）
--
-- 依据（2026-11-09 实机 96 帧采样）：静止时后裙骨点离髋部身体表面 +0.8～+5 cm，
-- 跑动时最深 **−8.2 cm**（几乎总是右后那条 `source_extra_120`）。摆幅约 13 cm。
--
-- 做法：标定时记下"静止时的半径"，之后低于它就把链根往外转一点转回来。
-- 只动**挂在 hips 上的链的链根**——整片布靠链根朝向摆，逐节动会把布扯变形。
-- 参数全走调参文件（`%LOCALAPPDATA%\hd2pbx_tune.txt`，2 秒重读一次），所以
-- 在游戏里改数字就生效，不用重启、不用重新部署。
--
--   skirt        = 1     0=关，1=开
--   skirtcal     = 1     置 1 且**站着**，运行时就地记下当前半径当基准；之后置回 0
--   skirtGain    = 0.35  每帧纠正亏损量的比例（1 = 一步到位，容易抖）
--   skirtMaxDeg  = 12    单帧最多转多少度（保底，防止瞬间甩飞）
--   skirtMargin  = 0.005 额外留白（米）
--   skirtArm     = 0.30  力臂（米）：布大概从骨点往外延伸多远，用来把"距离"换成"角度"
-- ---------------------------------------------------------------------------
local SKIRT_DEFAULTS = {
	gain = 0.35, maxDeg = 12, margin = 0.005, arm = 0.30,
	-- 两处平滑的时间常数（秒）。它们存在的唯一目的是**去掉帧间抖动**：
	-- 权重和"正后方"轴如果每帧按当前姿态重算，就会跟着步频一起摆。
	-- weight 的语义其实是**静态**的（这根骨在裙子的哪一侧），所以宁慢不抖：1.2 秒。
	weightSeconds = 1.2, backSeconds = 0.5,
	-- 自适应上限（临时值，按布料相对自己基线的垂度反馈）：
	--   · tilt（骨方向与世界水平的夹角，取绝对值）比该链观察到的**峰值**低 capDropDelta 以上
	--     ⇒ 判定被推高/被甩高，每帧把上限降 capDropStep 度；
	--   · tilt 回到峰值 capRiseDelta 以内 ⇒ 每 capRiseFrames 帧恢复 capRiseStep 度；
	--   · 两者之间是**死区**，不动（没有死区就会在阈值上来回跳 = 慢速脉动）。
	-- 为什么用"相对峰值"而不是绝对角度：实测自然下垂的链只有 41~61°，
	-- 写死 45/65 会把大量自然下垂的链也判成被推高（2.1.11 就这么整片失效的）。
	-- tiltDecay：峰值每秒回落多少度，让裙子换了/姿态变了之后能重新学习。
	-- tiltBase 只在**没在推**的时候学（见 skirt_chain）。基线用来防误伤：
	-- 自然就只有 41~61° 的链不该被当成"被推高"。
	capDropDeg = 45, capRiseDeg = 65, capDropDelta = 5, capRiseDelta = 3,
	capDropStep = 1, capRiseStep = 1, capRiseFrames = 3,
	capFloor = 0, capSmoothSeconds = 0.15,
	-- 上限的硬性下限（相对手调值的比例）：保证"永远不会被压到 0"。
	capFloorRatio = 0.35,
}

--- 预设枚举：把"旋转加在哪个坐标系、正负号"这 8 种可能一次列全，让实机去分辨。
--- <para>
--- 为什么用枚举而不是推理：`write_pose` 把 (轴, 角) 解释成什么（父空间增量？局部增量？
--- 绝对旋转？）读代码看不出来，猜错的代价是"画面毫无变化"，而那时根本分不清是
--- 轴错了、帧错了、还是没写进去。8 个预设、每个都恒定 60°，一眼就能分出对错。
--- </para>
--- <para>
--- 预设**不看** keep-out、不做亏损判断：站着不动时也要一眼看得出效果，
--- 否则"没反应"会被误读成预设失效。这是诊断模式，不是最终行为。
--- </para>
local PRESET_DEGREES = 60
local PRESETS =
{
	-- { 模式, 符号 }  模式 1/2 = 前乘（轴：1=父空间、2=世界）；3/4 = 后乘（同理）
	{ 1, 1 }, { 1, -1 }, { 2, 1 }, { 2, -1 },
	{ 3, 1 }, { 3, -1 }, { 4, 1 }, { 4, -1 },
}

local function preset_label(index)
	local preset = PRESETS[index]
	if preset == nil then return "无" end
	local frame = (preset[1] == 1 or preset[1] == 3) and "父空间轴" or "世界轴"
	local order = (preset[1] <= 2) and "前乘" or "后乘"
	return string.format("%d/%d：%s %s %s%.0f°", index, #PRESETS, frame, order,
		preset[2] > 0 and "+" or "−", PRESET_DEGREES)
end

local function skirt_settings(runtime)
	local values = tune_values(runtime) or {}
	return {
		enabled = (values.skirt or 0) ~= 0,
		calibrate = (values.skirtcal or 0) ~= 0,
		gain = values.skirtGain or SKIRT_DEFAULTS.gain,
		maxDeg = values.skirtMaxDeg or SKIRT_DEFAULTS.maxDeg,
		margin = values.skirtMargin or SKIRT_DEFAULTS.margin,
		arm = values.skirtArm or SKIRT_DEFAULTS.arm,
		-- 手调角度（度）：>0 时**只要这条链有亏损**就用这个角度，不再按"亏损/力臂"推。
		-- 为什么需要它：力臂是估的，实测偏小会让算出来的角度只有 3~4°（肉眼等于没动）。
		-- 手调扫一遍，用眼睛定一个"多数裙子都好看又不穿"的值，比按力矩推可靠得多。
		manualDeg = values.skirtDeg or 0,
		-- 没手调时按"每 1 cm 亏损转多少度"换算（缺省 6°/cm）。
		degPerCm = values.skirtDegPerCm or 6,
		-- 运动斜坡：静止 0°、跑起来满额。实测"静止 0 度就够用，只有奔跑才需要角度"。
		idleSpeed = values.skirtIdleSpeed or 0.5,     -- m/s，以下视为静止
		runSpeed = values.skirtRunSpeed or 2.0,       -- m/s，以上视为满额
		rampSeconds = values.skirtRampSeconds or 0.25,
		-- "方向权重"收敛的多快。调小 = 更跟手但更抖；调大 = 更稳。
		weightSeconds = values.skirtWeightSeconds or SKIRT_DEFAULTS.weightSeconds,
		-- "正后方"轴收敛的多快。跑步时两条大腿前后交替，(l_thigh − r_thigh)
		-- 每一步都在转，不快不行；但又不能太快，否则权重跟着摆。
		backSeconds = values.skirtBackSeconds or SKIRT_DEFAULTS.backSeconds,
		-- 自适应上限：绝对阈值（capDropDeg/capRiseDeg）为主，
		-- 再加"相对自己历史峰值"的保险（capDropDelta/capRiseDelta），避免自然就趴着的链被误伤。
		capDropDeg = values.skirtCapDropDeg or SKIRT_DEFAULTS.capDropDeg,
		capRiseDeg = values.skirtCapRiseDeg or SKIRT_DEFAULTS.capRiseDeg,
		capDropDelta = values.skirtCapDropDelta or SKIRT_DEFAULTS.capDropDelta,
		capRiseDelta = values.skirtCapRiseDelta or SKIRT_DEFAULTS.capRiseDelta,
		capDropStep = values.skirtCapDropStep or SKIRT_DEFAULTS.capDropStep,
		capRiseStep = values.skirtCapRiseStep or SKIRT_DEFAULTS.capRiseStep,
		capRiseFrames = math.max(1, math.floor(values.skirtCapRiseFrames or SKIRT_DEFAULTS.capRiseFrames)),
		capFloor = values.skirtCapFloor or SKIRT_DEFAULTS.capFloor,
		capFloorRatio = values.skirtCapFloorRatio or SKIRT_DEFAULTS.capFloorRatio,
		capSmoothSeconds = values.skirtCapSmoothSeconds or SKIRT_DEFAULTS.capSmoothSeconds,
		-- 方向加权的手性开关：叉乘出来的"后"可能是前，翻号即可。
		backSign = (values.skirtBackSign or 1) < 0 and -1 or 1,
		-- sign 只为"方向验证"存在：置 -1 就把纠正方向反过来，不必改代码重新部署。
		sign = (values.skirtSign or 1) < 0 and -1 or 1,
		-- 预设枚举（诊断）：>0 时完全接管，恒定角度、不看 keep-out。
		preset = math.floor(values.preset or 0),
		all = (values.skirtAll or 0) ~= 0,
	}
end

--- 会话级的两个参照点：hips（圆心）与 chest（上方向）。解析不到就放弃这一帧。
---
--- ⚠️ **节点数变了就必须重算。** 缓存的是**节点下标**，而 unit 被重建（换装 / 切 LOD /
--- 军械库换预览件）之后同样的下标指向别的节点，甚至越界 —— 对越界下标读 `world_position`
--- 是原生 AV（`0xc0000005 / 0x0 0x0`），`pcall` 拦不住。
--- `session_health` 只在**每 30 帧**比一次节点数，中间那一段窗口正是踩雷区，
--- 所以这里每帧都问一次（一个会话一次调用，相对裙摆本身每帧 4 次取位可以忽略）。
local function skirt_refs(S_, session)
	if S_.Unit == nil or session.unit == nil then return nil end
	local okCount, count = pcall(function() return S_.Unit.num_scene_graph_items(session.unit) end)
	if not okCount or type(count) ~= "number" then return nil end
	local refs = session.skirtRefs
	if refs ~= nil and session.skirtRefsCount == count then return refs end
	local function nodeOf(name)
		local okHas, has = pcall(S_.Unit.has_node, session.unit, name)
		if not okHas or has ~= true then return nil end
		local okNode, node = pcall(function() return S_.Unit.node(session.unit, name) end)
		if okNode and type(node) == "number" and node >= 1 and node <= count then return node end
		return nil
	end
	local hipsNode, chestNode = nodeOf("hips"), nodeOf("chest")
	if hipsNode == nil then return nil end
	refs = { hips = hipsNode, chest = chestNode, lThigh = nodeOf("l_thigh"), rThigh = nodeOf("r_thigh") }
	session.skirtRefs = refs
	session.skirtRefsCount = count   -- 记下这份下标属于哪个节点数（变了就作废，见函数头注释）
	return refs
end

local function worldPos(S_, unit, node)
	local ok, x, y, z = pcall(function()
		return S_.Vector3.to_elements(S_.Unit.world_position(unit, node))
	end)
	if ok and type(x) == "number" then return x, y, z end
	return nil
end

--- 哪些链算"裙摆/连衣裙布料"。
---
--- 原来只认父骨名含 `hips`，于是**挂在胸口背后的连衣裙骨被整条漏掉**
--- （实机反馈：有些裙子是连衣裙做法，骨在胸口那一层）。
--- 现在两条判据，命中任一即可；结果**缓存**在 `item.skirtEligible`（躯干带相对身体是不变的，
--- 只有名字认不出时才需要一次世界坐标查询）。
---
--- 安全边界不在"选中"，而在权重：正前/侧面的 cos(夹角) ≤ 0 ⇒ 一根都不会动。
--- 所以放宽选中范围不会让乳房/小腹的骨被推动。
local SKIRT_PARENT_HINTS = { "hips", "pelvis", "spine", "chest", "torso", "waist" }

---@return boolean|nil true/false = 已确定；nil = 这一帧还没法判断（等下次）
local function skirt_eligible(S_, session, item, body)
	if item.skirtEligible ~= nil then return item.skirtEligible end
	local parent = item.chain and item.chain[1] and item.chain[1].parentName
	local lower = type(parent) == "string" and parent:lower() or ""
	for _, hint in ipairs(SKIRT_PARENT_HINTS) do
		if lower:find(hint, 1, true) ~= nil then
			item.skirtEligible = true
			item.skirtEligibleWhy = "name:" .. hint
			return true
		end
	end
	-- 名字认不出（`source_extra_*` 这类）：用高度带兜底。
	-- 带 = [hips 下方 10 cm, chest 上方 15 cm]：兜住挂在胸口/腰上的布料，
	-- 同时把头/头发（在 chest 上方更远）和腿（在 hips 下方）排除。
	if body ~= nil and body.hz ~= nil and body.cz ~= nil and item.nodes ~= nil and item.nodes[1] ~= nil then
		local _, _, rz = worldPos(S_, session.unit, item.nodes[1])
		if rz ~= nil then
			local inside = rz >= (body.hz - 0.10) and rz <= (body.cz + 0.15)
			item.skirtEligible = inside
			item.skirtEligibleWhy = inside and "height" or "outside"
			return inside
		end
	end
	return nil
end

--- 对一条链做"标定"或"纠正"。返回是否改动了 pushOut。
local function skirt_chain(S_, runtime, session, item, settings, calibrating, logger)
	local refs = skirt_refs(S_, session)
	if refs == nil then return false end
	local nodes = item.nodes
	if nodes == nil or #nodes == 0 then return false end
	local deep = nodes[#nodes]
	local dx, dy, dz = worldPos(S_, session.unit, deep)
	if dx == nil then return false end
	local hx, hy, hz = worldPos(S_, session.unit, refs.hips)
	if hx == nil then return false end
	-- 上方向：優先 chest−hips，拿不到就用世界 +Z（HD2 是 Z 向上）
	local ux, uy, uz = 0, 0, 1
	if refs.chest ~= nil then
		local cx, cy, cz = worldPos(S_, session.unit, refs.chest)
		if cx ~= nil then
			local vx, vy, vz = cx - hx, cy - hy, cz - hz
			local len = math.sqrt(vx * vx + vy * vy + vz * vz)
			if len > 0.05 then ux, uy, uz = vx / len, vy / len, vz / len end
		end
	end
	-- 相对 hips 的位移，去掉沿上方向的分量 ⇒ 水平径向
	local px, py, pz = dx - hx, dy - hy, dz - hz
	local along = px * ux + py * uy + pz * uz
	local rx, ry, rz = px - along * ux, py - along * uy, pz - along * uz
	local radius = math.sqrt(rx * rx + ry * ry + rz * rz)
	if radius < 1e-4 then return false end
	local rnx, rny, rnz = rx / radius, ry / radius, rz / radius

	-- "这块布有多垂" = **这根骨自己的方向**（链根 → 链尾）与世界水平面的夹角，取绝对值。
	--
	-- 两个坑（都是实测踩的，别再踩）：
	--   ① 不能用 hips → 链尾：链尾在世界 z 上可能比 hips 还高，算出来恒为负，
	--      于是每一帧都被判成"太横"，上限几帧内归零 ⇒ 整个功能等于关掉。
	--   ② 不能依赖符号：z 轴朝上还是朝下、链的节点顺序从腰到摆还是从摆到腰，
	--      从运行时代码里看不出来。取绝对值后这些都不影响。
	--
	-- 角度本身也不适合写死阈值：实测"垂着"的链只有 41~61°，不是直觉上的 60~90°，
	-- 所以绝对阈值 45/65 会把大量自然下垂的链也判成"被推高"。改成相对各自基线（见下）。
	local tilt = 0
	do
		local r0x, r0y, r0z = worldPos(S_, session.unit, nodes[1])
		if r0x ~= nil then
			local vx, vy, vz = dx - r0x, dy - r0y, dz - r0z
			local horiz = math.sqrt(vx * vx + vy * vy)
			tilt = math.abs(math.atan2(vz, horiz)) * 180 / math.pi
		end
	end
	do
		local sk = (runtime.dt or (1 / 60)) / math.max(settings.capSmoothSeconds, 0.02)
		if sk > 1 then sk = 1 end
		if item.skirtSag == nil then item.skirtSag = tilt
		else item.skirtSag = item.skirtSag + (tilt - item.skirtSag) * sk end
	end
	item.skirtSagRaw = tilt

	if calibrating then
		if item.keepOut == nil then
			item.keepOut = radius
			logger("[physbone][裙摆][标定] %s r=%.3f m", tostring(item.chain[1] and item.chain[1].name), radius)
		end
		return false
	end
	-- 没标定过就用当前半径当基准：亏损算出来是 0，只影响下面的"按亏换算角度"那一档，
	-- 不影响手调角度。
	local keepOut = item.keepOut or radius
	local deficit = keepOut + settings.margin - radius
	-- **这里原本是致命的一处开关**（2.1.8 及更早）：deficit <= 0 就 pushOut=nil 并 return，
	-- 于是后面那句"无条件加"永远走不到。实机证据（同局日志）：180 条 hips 链只有 26~51
	-- 条被推，且每几帧在 26/34/35/43/51 之间跳；跑动时角度确实到 29~30° ⇒ 不是算小了，
	-- 是多数帧**根本没加**。用户看到的就是"大多被拉回，少数保持角度"。
	-- 现在亏损只用于报数和"按亏损换算角度"那一档，不再当开关。
	if settings.preset > 0 then
		-- 诊断模式：不看 keep-out、不算亏损，恒定 60° 往外摆（或往里，看符号）。
		local preset = PRESETS[settings.preset]
		if preset == nil then return false end
		local ax, ay, az = rny * uz - rnz * uy, rnz * ux - rnx * uz, rnx * uy - rny * ux
		local alen = math.sqrt(ax * ax + ay * ay + az * az)
		if alen < 1e-6 then return false end
		item.pushOut = { ax / alen, ay / alen, az / alen,
			PRESET_DEGREES * math.pi / 180 * preset[2], preset[1] }
		item.pushOutAll = settings.all
		return true
	end
		-- 把"要补的距离"换成"要转的角度"：θ ≈ 亏损 / 力臂
	-- 角度：手调优先（度），否则按"每厘米亏损多少度"换算。
	-- 上一次的教训：`gain × 亏损 / 力臂` 在 deficit≈2 cm、arm=0.3 m 时只有 3.8°，
	-- 而日志打的是**上限** 60°，于是我自己得出"推满了也没用"的错误结论。
	-- ① 方向权重：只给"朝后"的那部分。夹角按**角度线性**拆（45° 给一半 15°），
	--    这是用户明确要的语义：侧面/正前一点都不分。
	local back = session.skirtBack
	local liveWeight = 1.0
	if back ~= nil then
		local dot = rnx * back[1] + rny * back[2] + rnz * back[3]
		if dot < -1 then dot = -1 elseif dot > 1 then dot = 1 end
		local deviation = math.acos(dot) * 180 / math.pi      -- 与"正后方"的夹角
		-- **权重 = cos(夹角)**（= dot 本身），偏前一律 0。
		--
		-- 为什么不用线性 `1 − θ/90`：
		--   d/dθ[1 − θ/90] = 常数 ⇒ 侧面附近仍然按同样速度给角度，
		--   于是"差一点没到侧面"的骨头也拿得到可观的角度，前方布料一被物理甩到后面
		--   就会被我们顺势推进小腹（实机反馈）。
		--   d/dθ[cos θ] = −sin θ ⇒ 正后方导数为 0（平坦、稳），越靠侧面掉得越快，
		--   90° 处正好掉到 0。用户给的形状描述就是"半个椭圆"。
		-- 附带好处：cos 不需要再算 acos，dot 直接就是权重。
		liveWeight = dot > 0 and dot or 0
		item.skirtDeviation = deviation
	end
	-- 权重按时间常数平滑（不是每帧直接用瞬时值）。
	-- 为什么：瞬时权重 = f(当前姿态)，而姿态又被我们自己的偏移改变 ⇒ 反馈回路，
	-- 表现为同一块布的角度每帧变（实测整片在 13°↔30° 之间集体摆）。
	-- 平滑之后每条骨拿到的角度基本恒定 = 用户要的"恒定偏移"。
	local weight = item.skirtWeight
	if weight == nil then
		weight = liveWeight
	else
		local wdt = runtime.dt or (1 / 60)
		local k = wdt / math.max(settings.weightSeconds, 0.02)
		if k > 1 then k = 1 end
		weight = weight + (liveWeight - weight) * k
	end
	item.skirtWeight = weight

	-- ② 迟滞的"需不需要保护"：避免在边界上每帧翻转（翻转就是抖动）。
	local margin = settings.margin
	if deficit > margin then
		item.skirtNeed = 1
	elseif deficit < -margin then
		item.skirtNeed = nil
	end

	-- ③ 平滑追值：目标 = 方向权重 × 运动斜坡。
	--    **不再乘"需要标记"**：那个标记跟着链骨半径过门槛翻转，翻转就是闪烁。
	--    叠加关系，不是竞争关系：物理怎么摆，我们都在它的结果上多给一个恒定偏移。
	local target = weight * (session.skirtMotion or 0)
	local level = item.skirtLevel or 0
	local dt = runtime.dt or (1 / 60)
	local step = dt / math.max(settings.rampSeconds, 0.02)
	if level < target then
		level = math.min(target, level + step)
	elseif level > target then
		level = math.max(target, level - step)
	end
	item.skirtLevel = level
	item.skirtDebug = { weight = weight, level = level, deviation = back ~= nil and weight or nil }

	-- 按亏损换算的那一档必须夹住：deficit 现在可能为负（半径比标定值大），
	-- 直接乘会得到一个负角度 ⇒ 往里推 ⇒ 正好推进屁股里。
	local cm = deficit > 0 and deficit * 100 or 0
	local degrees = settings.manualDeg > 0 and settings.manualDeg or settings.degPerCm * cm
	local magnitude = degrees * level

	-- ③ 自适应上限（临时值）：按这块布**有多垂**反馈。
	--    基线 tiltBase 只在**没在推的时候**学（level < 0.05 ⇒ 这块布此刻的角度就是它自然的角度）。
	--    为什么不能在推的时候学：推本身就在压低 tilt，学出来的"基线"会跟着上限一起往下跑，
	--    系统会收敛到接近 0（离线模拟验证过，这是第一版相对阈值的致命缺陷）。
	--    判定：绝对阈值为主（用户给的规则），基线只用来**防误伤**——
	--    自然就只有 41~61° 的链不该被当成"被推高"，它要求比自己自然状态再低 capDropDelta 才算。
	local ceiling = settings.manualDeg > 0 and settings.manualDeg or settings.maxDeg
	local cap = item.skirtCap
	if cap == nil then cap = ceiling end
	local tiltSmooth = item.skirtSag or tilt
	if level < 0.05 then
		-- 自然状态：可能是站着、也可能是刚松开跑动。按 0.3 秒平滑学一次。
		local bk = dt / 0.3
		if bk > 1 then bk = 1 end
		if item.skirtTiltBase == nil then item.skirtTiltBase = tiltSmooth
		else item.skirtTiltBase = item.skirtTiltBase + (tiltSmooth - item.skirtTiltBase) * bk end
	end
	local base = item.skirtTiltBase
	local tooHigh = tiltSmooth < settings.capDropDeg
		and (base == nil or tiltSmooth < base - settings.capDropDelta)
	local recovered = tiltSmooth > settings.capRiseDeg
		or (base ~= nil and tiltSmooth > base - settings.capRiseDelta)
	if tooHigh then
		item.skirtCapRise = 0
		if magnitude > 0.5 then cap = cap - settings.capDropStep end
	elseif recovered then
		item.skirtCapRise = (item.skirtCapRise or 0) + 1
		if item.skirtCapRise >= settings.capRiseFrames then
			item.skirtCapRise = 0
			cap = cap + settings.capRiseStep
		end
	else
		item.skirtCapRise = 0
	end
	if cap > ceiling then cap = ceiling
	elseif cap < settings.capFloor then cap = settings.capFloor end
	-- 兜底：上限永远不低于 ceiling 的 capFloorRatio 倍。
	-- 为什么需要它：2.1.11 上限被压到 0，整个功能等于关掉（实机反馈"变成主线的物理一样"）。
	-- 有了这条，无论如何都还留着一部分推力，最坏情况是"推得不够"，不会是"完全没推"。
	local ratioFloor = ceiling * settings.capFloorRatio
	if cap < ratioFloor then cap = ratioFloor end
	item.skirtCap = cap
	item.skirtCapActive = magnitude > 0.5
	if magnitude > cap then magnitude = cap end

	local angle = settings.sign * magnitude * math.pi / 180
	local maxAngle = settings.maxDeg * math.pi / 180
	if angle > maxAngle then angle = maxAngle elseif angle < -maxAngle then angle = -maxAngle end
	if math.abs(angle) < 1e-5 then
		item.pushOut = nil
		return false
	end
	-- 轴 = r̂ × up：这样绕它转时，向下垂的布会朝 +r̂ 方向摆出去
	local ax, ay, az = rny * uz - rnz * uy, rnz * ux - rnx * uz, rnx * uy - rny * ux
	local alen = math.sqrt(ax * ax + ay * ay + az * az)
	if alen < 1e-6 then return false end
	item.pushOut = { ax / alen, ay / alen, az / alen, angle }
	item.pushOutAll = settings.all
	return true
end

--- 每帧调一次：遍历所有会话里"挂在 hips 上"的链。
local function skirt_apply(S_, runtime)
	local settings = skirt_settings(runtime)
	if not settings.enabled and not settings.calibrate and settings.preset == 0 then return end
	local allNodes = (tune_values(runtime).skirtAll or 0) ~= 0
	local pushed, total, weighted = 0, 0, 0
	local motionNow = 0
	for _, session in ipairs(runtime.sessions or {}) do
		-- ⚠️ **先判活，再碰 unit。** 这里的每一处引擎调用（`num_scene_graph_items` /
		-- `world_position`）都是**原生 AV 的来源**：unit 被销毁后再读它，进程直接死，
		-- 而且 `pcall` 拦不住（本文件 §"必须先过 unit_alive" 早就写过同一条，
		-- 转储形态就是 `0xc0000005 / 参数 0x0 0x0`）。
		--
		-- 为什么裙摆这条路特别容易踩到（2026-11-12 军械库/债券页崩溃）：开页面时引擎会
		-- **连续创建/销毁**一大堆展示用的护甲件，而裙摆是**每帧遍历所有会话**的、并且在
		-- `drive` 的最末尾跑（pruneSessions 之后）——一帧之内被销毁的件就落在这条路上。
		local alive = session.unit ~= nil
			and session.dead == nil
			and type(Compat.unit_alive) == "function"
			and Compat.unit_alive(S_, session.unit) == true
		if alive then
			-- 会话级准备：① 骨盆速度 → 运动斜坡；② "正后方"轴（世界坐标）。
			local refs = skirt_refs(S_, session)
			if refs ~= nil then
				local hx, hy, hz = worldPos(S_, session.unit, refs.hips)
				if hx ~= nil then
					local dt = runtime.dt or (1 / 60)
					local last = session.skirtPelvis
					local speed = 0
					if last ~= nil and dt > 1e-4 then
						local dx, dy, dz = hx - last[1], hy - last[2], hz - last[3]
						speed = math.sqrt(dx * dx + dy * dy + dz * dz) / dt
					end
					session.skirtPelvis = { hx, hy, hz }
					-- 目标 0~1：静止 0、跑起来 1；再按时间常数追值，避免跳变。
					local raw = (speed - settings.idleSpeed) / math.max(settings.runSpeed - settings.idleSpeed, 0.1)
					if raw < 0 then raw = 0 elseif raw > 1 then raw = 1 end
					local motion = session.skirtMotion or 0
					local step = dt / math.max(settings.rampSeconds, 0.02)
					if motion < raw then motion = math.min(raw, motion + step)
					elseif motion > raw then motion = math.max(raw, motion - step) end
					session.skirtMotion = motion

					-- 正后方 = up × lateral（手性可能反，用 skirtBackSign 翻）
					if refs.lThigh ~= nil and refs.rThigh ~= nil and refs.chest ~= nil then
						local lx, ly, lz = worldPos(S_, session.unit, refs.lThigh)
						local rx2, ry2, rz2 = worldPos(S_, session.unit, refs.rThigh)
						local cx, cy, cz = worldPos(S_, session.unit, refs.chest)
						if lx ~= nil and rx2 ~= nil and cx ~= nil then
							-- 躯干高度带：给"名字认不出的连衣裙骨"兜底用（见 skirt_eligible）
							session.skirtBody = { hz = hz, cz = cz }
							-- lateral(points left) = l − r；up = chest − hips
							local ax, ay, az = lx - rx2, ly - ry2, lz - rz2
							local ux, uy, uz = cx - hx, cy - hy, cz - hz
							local bx, by, bz = uy * az - uz * ay, uz * ax - ux * az, ux * ay - uy * ax
							local blen = math.sqrt(bx * bx + by * by + bz * bz)
							if blen > 1e-6 then
								local nx, ny, nz = bx / blen * settings.backSign, by / blen * settings.backSign,
									bz / blen * settings.backSign
								-- 平滑追值：跑步时 l_thigh/r_thigh 前后交替，"l−r" 每一步都在转，
								-- 直接用瞬时值会让所有链的权重跟着步频摆（实测 13°↔30°）。
								local prev = session.skirtBack
								if prev == nil then
									session.skirtBack = { nx, ny, nz }
								else
									local k = dt / math.max(settings.backSeconds, 0.02)
									if k > 1 then k = 1 end
									local mx = prev[1] + (nx - prev[1]) * k
									local my = prev[2] + (ny - prev[2]) * k
									local mz = prev[3] + (nz - prev[3]) * k
									local mlen = math.sqrt(mx * mx + my * my + mz * mz)
									if mlen > 1e-6 then
										session.skirtBack = { mx / mlen, my / mlen, mz / mlen }
									end
								end
							end
						end
					end
				end
			end
			-- 会话级一次性诊断：把所有链的**父骨名字**统计出来。
			-- 为什么要它：连衣裙的骨挂在胸口那一层，名字是 `source_extra_*`，
			-- 只有看到真实的父骨名，才知道该把哪些名字加进 SKIRT_PARENT_HINTS。
			if session.skirtCensusDone ~= true then
				session.skirtCensusDone = true
				local counts, order = {}, {}
				for _, item in ipairs(session.sessions or {}) do
					local parent = item.chain and item.chain[1] and item.chain[1].parentName
					local key = type(parent) == "string" and parent or "?"
					if counts[key] == nil then
						counts[key] = 0
						order[#order + 1] = key
					end
					counts[key] = counts[key] + 1
				end
				local parts = {}
				for _, key in ipairs(order) do
					parts[#parts + 1] = string.format("%s×%d", key, counts[key])
				end
				runtime.logger("[physbone][裙摆][父骨] unit=%s 共 %d 条链：%s",
					tostring(session.unitTag or "?"), #order, table.concat(parts, " "))
			end
			for _, item in ipairs(session.sessions or {}) do
				-- 选中判据见 skirt_eligible：名字 hinted 命中，或链根落在躯干高度带里。
				local eligible = skirt_eligible(S_, session, item, session.skirtBody)
				if eligible == true then
					total = total + 1
					-- 标定只在**第一次**做：同一个会话里不重复覆盖（否则跑着跑着又标一次）
					local calibrating = settings.calibrate and not session.skirtCalibrated
					if skirt_chain(S_, runtime, session, item, settings, calibrating, runtime.logger) then
						pushed = pushed + 1
					end
					-- 诊断：有多少条链"有方向权重"（= 该加的那部分）。
					-- 推的条数应当≈这个数（跑动时）；差得多就说明还有别的门在挡。
					if (item.skirtWeight or 0) > 0.01 then weighted = weighted + 1 end
				end
			end
			motionNow = session.skirtMotion or motionNow
			if settings.calibrate then session.skirtCalibrated = true end
		end
	end
	local state = runtime.skirtState
	if state == nil then state = { reported = nil, preset = nil }; runtime.skirtState = state end
	if settings.preset > 0 and state.preset ~= settings.preset then
		state.preset = settings.preset
		runtime.logger("[physbone][裙摆][预设] %s（推了 %d 条 hips 链）",
			preset_label(settings.preset), pushed)
	end
	local message = string.format("%d/%d", pushed, total)
	-- 每秒报一次**实际用到的角度**（不是上限）。上限骗过一次人，不能再犯。
	local frameNow = runtime.driveFrames or 0
	if state.reported ~= message or frameNow % 120 == 0 then
		if state.reported ~= message or (state.lastLogFrame == nil or frameNow - state.lastLogFrame >= 120) then
			state.reported = message
			state.lastLogFrame = frameNow
			local maxDeg = 0
			local degSum, degN = 0, 0
			local bestWeight = 0
			local capMin, capLimited, capN = nil, 0, 0
			local sagSum, sagN = 0, 0
			local peakSum = 0
			local buckets = { 0, 0, 0, 0, 0, 0 }
			local ceilingNow = settings.manualDeg > 0 and settings.manualDeg or settings.maxDeg
			for _, session in ipairs(runtime.sessions or {}) do
				for _, item in ipairs(session.sessions or {}) do
					local push = item.pushOut
					if push ~= nil then
						local deg = math.abs(push[4]) * 180 / math.pi
						if deg > maxDeg then maxDeg = deg end
						degSum = degSum + deg
						degN = degN + 1
						-- 自适应上限：看它压到多少、有多少条被压过。
						local cap = item.skirtCap
						if cap ~= nil then
							capN = capN + 1
							if capMin == nil or cap < capMin then capMin = cap end
							if cap < ceilingNow - 0.5 then capLimited = capLimited + 1 end
						end
					end
					-- tilt 与基线：**不看是否在推**，否则站着（没有 pushOut）就永远是 0、看不到数据。
					local tiltNow = item.skirtSag
					if tiltNow ~= nil then
						sagSum = sagSum + tiltNow
						peakSum = peakSum + (item.skirtTiltBase or tiltNow)
						sagN = sagN + 1
					end
					-- 方向分布：只统计"会被我们考虑的"链（权重已算过），按偏离正后方每 30° 一格。
					-- 用途：判断"正后方"轴是否偏。环形裙子应当左右对称且最靠后那根接近 0°；
					-- 若最近的一根也在 30~60°，说明轴偏了或裙子本身没有正中骨。
					local dev = item.skirtDeviation
					if dev ~= nil and (item.skirtWeight or 0) > 0 then
						local index = math.floor(dev / 30) + 1
						if index < 1 then index = 1 elseif index > 6 then index = 6 end
						buckets[index] = buckets[index] + 1
						if (item.skirtWeight or 0) > bestWeight then bestWeight = item.skirtWeight end
					end
				end
			end
			-- 均值比最大值有用：最大值只能证明"有一条推满了"，均值才说明"整片有没有动"。
			runtime.logger(
				"[physbone][裙摆] 推 %d/%d 条（权重>0 %d）· 角度 均值 %.1f° / 最大 %.1f° · 斜坡 %.2f（手调 %.0f°）· 权重最大 %.2f · 上限 最小 %.0f°（被压 %d/%d）· tilt 均值 %.0f° / 基线 %.0f° · 分布 %d/%d/%d/%d/%d/%d",
				pushed, total, weighted, degN > 0 and degSum / degN or 0, maxDeg, motionNow,
				settings.manualDeg, bestWeight,
				capMin or ceilingNow, capLimited, capN,
				sagN > 0 and sagSum / sagN or 0, sagN > 0 and peakSum / sagN or 0,
				buckets[1], buckets[2], buckets[3], buckets[4], buckets[5], buckets[6])
		end
	end
end

--- 诊断开关（`dumpApi = 1`）：把引擎 Lua 命名空间的**全部成员名**写成清单。
--- <para>
--- 目的（2026-11-12）：查"有没有批量写骨/姿态的 API"。当前每根骨每帧 2 次引擎调用
--- （`Quaternion.from_elements` + `Unit.set_local_rotation`），约 5 μs/次 ⇒ 818 链就已经 5 ms。
--- 要让四名玩家的护甲全部挂上还守住 5 ms，只有三条路：
--- ① 找到批量写端口（一次写多根骨）；② 找到不需要构造调用的四元数写法；③ 降低链数。
--- 这份清单是判断 ① / ② 是否存在的唯一可靠依据（旧探测用的清单已过时，它连
--- `set_local_rotation` 都报缺失，而实际是可用的）。
--- </para>
--- 只读枚举、不改任何东西，一个进程只写一次。
local function dump_api_surface(runtime)
	local logger = runtime.logger
	local base = os.getenv("LOCALAPPDATA")
	if type(base) ~= "string" or #base == 0 then base = "." end
	local path = (runtime.options and runtime.options.apiDumpPath) or (base .. "\\hd2pbx_api.txt")
	local handle = io.open(path, "w")
	if handle == nil then
		logger("[physbone][诊断] 写不出 API 清单：%s", path)
		return
	end
	handle:write(("physbone runtime %s · 引擎 Lua 命名空间清单\n"):format(tostring(M.VERSION)))
	-- 只列**和写骨/构造值有关**的命名空间：清单越长越难读，而这些之外的东西用不上。
	local namespaces = { "Unit", "Quaternion", "Matrix4x4", "Vector3", "World", "Application" }
	for _, name in ipairs(namespaces) do
		local namespace = rawget(S, name)
		local lines = {}
		if type(namespace) == "table" then
			for key, value in pairs(namespace) do
				if type(key) == "string" then
					lines[#lines + 1] = ("%s = %s"):format(key, type(value))
				end
			end
		end
		table.sort(lines)
		handle:write(("[%s] %d 项\n"):format(name, #lines))
		for _, line in ipairs(lines) do handle:write("  " .. line .. "\n") end
	end
	handle:close()
	logger("[physbone][诊断] 引擎 API 清单已写到 %s（共 %d 个命名空间）", path, #namespaces)
end

local function arm_pose_enabled(runtime)
	local tune = tune_values(runtime)
	if tune.armPose ~= nil then return tune.armPose ~= 0 end
	if tune.text ~= nil and tune.text.armPose ~= nil then
		return tune.text.armPose == "true" or tune.text.armPose == "1"
	end
	return runtime.options ~= nil and runtime.options.armPose == true
end

local function arm_pose_options(runtime)
	local opts = runtime.options or {}
	local tune = tune_values(runtime)
	local mode = (tune.text and tune.text.armMode) or opts.armMode or "auto"
	return {
		armMode = mode,
		-- 不在这里填默认值：有没有肩关节目标会改变默认（见 Compat.apply_arm_pose）。
		armAdductionDeg = tune.armAdductionDeg or opts.armAdductionDeg,
		armForearmCompDeg = tune.armForearmCompDeg or opts.armForearmCompDeg,
		armShoulderTarget = opts.armShoulderTarget,
		legJointTarget = opts.legJointTarget,
		armShoulderNarrow = tune.armShoulderNarrow or opts.armShoulderNarrow or 0.025,
		armIkPull = tune.armIkPull or opts.armIkPull or 0.09,
		armBlendTime = tune.armBlendTime or opts.armBlendTime or 0.12,
	}
end

local function resolve_local_avatar_for_arm(runtime)
	-- E12d：本人 = IAS 的 role 1（Network/GameSession 身份，每帧 Roster 给出）。
	-- 没有网络身份可用时没有本人（IAS 同样没有回退）；测试/外部直接设置的 `runtime.avatar` 原样保留。
	local roster = runtime.roster
	if roster ~= nil and roster.identity then
		local unit, world = Roster.localAvatar(runtime)
		runtime.avatar = unit
		if unit ~= nil then runtime.cache.world, runtime.world = world, world end
		return unit
	end
	return runtime.avatar
end

-- ---------------------------------------------------------------------------
-- 独立骨（2026-11-22，实验 1，见 docs/agent/ias-independent-bones-plan-20261122.md）
--
-- 实机证实（护甲探针 1、2）：护甲网格里和 avatar 同名的骨，画面直接用角色动画，
-- 运行时写 avatar 或护甲上的同名骨都不认。转换器把要换转轴的骨改了名（`ias_` + 原名），
-- 它们就成了普通场景节点。以模型关节为转轴，复制同父链的 avatar 局部旋转。
--
-- 选项 `independentBones = { { "ias_l_shoulder", "l_shoulder" }, ... }`（父骨在前）。
-- `independentTranslations` 按件给出平移骨的潜兵/模型静止局部位置。
-- ---------------------------------------------------------------------------
-- ---------------------------------------------------------------------------
-- IAS 1.6.4 生命周期（E12d）：每帧枚举 avatar 和装甲件，当帧按距离绑定，不保存跨帧结论。
-- 行为对照 docs/agent/ias-runtime-lifecycle-map-20261123.md：
--   avatar 枚举          IAS runtime.lua 1344–1360   每回调 World.units_by_resource(world, AVATAR_RESOURCE)
--   装甲件发现           IAS 1500–1620               每个 profile 一次 units_by_resource(Id64.from_hex(哈希))，
--                                                    全量 World.units 只在 60 帧 / 战斗世界 avatar 集合变化或每 2 s /
--                                                    某 profile 命中数下降时兜底；空结果只缓存 0.25 s
--   归属                 IAS 6690–6830               根探针 {1,0} 离最近 avatar，d2 < 9.0，次近比最近远出 0.20 才绑
--   本人/队友/死亡       IAS 4718–5100 physics_identity（Network/GameSession，4 Hz；先清缺席再节流）
-- 本模块是 IAS 这些做法的行为复刻（代码自己写）；没有 IAS 没有的缓存、额度、退避。
-- ---------------------------------------------------------------------------
Roster.INTERVAL = 60
Roster.AMBIGUITY = 0.20
Roster.worlds = {}
Roster.states = setmetatable({}, { __mode = "k" })
Roster.known, Roster.session, Roster.epoch = {}, nil, 0
Roster.nextPoll, Roster.ownedCursor, Roster.cursor = 0, 0, 0
Roster.extendedCursor, Roster.nearCursor, Roster.observedHigh = 4096, 0, 4095
Roster.remoteCursors, Roster.poll = {}, 0

--- IAS profile 表：一件转换后的护甲 = 一个 profile，哈希 = 16 位十六进制资源 id（IAS 行 757–844，启动时装载一次）。
--- 转换器写出 `profileHashes`（独立骨件与物理件都在内）；retarget 与物理共用这一份名单。
--- 兼容垫片（不是 IAS 机制）：旧版 joint-pose 数据没有 `profileHashes`，退回 `independentPieces` 的键。
local function roster_hashes(runtime)
	local cached = runtime.rosterHashes
	if cached ~= nil then return cached end
	local list, seen = {}, {}
	local function add(hash)
		if type(hash) ~= "string" then return end
		hash = hash:lower()
		if #hash == 16 and hash:match("^%x+$") and not seen[hash] then seen[hash] = true; list[#list + 1] = hash end
	end
	local options = runtime.options or {}
	for _, hash in ipairs(options.profileHashes or {}) do add(hash) end
	for tag in pairs(options.independentPieces or {}) do add(tostring(tag):match("^#ID%[(%x+)%]$")) end
	table.sort(list)
	runtime.rosterHashes = list
	return list
end

--- IAS `__hd2ir_unit_instance_slot`（行 925–945）：句柄 → 稳定小整数，隔 120 帧清掉 2 帧没见过的。
--- 只用来当"身份"，从不拿它去调引擎。
Roster.slots, Roster.slotSeen, Roster.nextSlot, Roster.slotPruned = {}, {}, 1, -1
function Roster.instanceSlot(unit, frame)
	if unit == nil then return 0 end
	if frame % 120 == 0 and Roster.slotPruned ~= frame then
		Roster.slotPruned = frame
		for key, last in pairs(Roster.slotSeen) do
			if frame - last > 2 then Roster.slots[key], Roster.slotSeen[key] = nil, nil end
		end
	end
	local slot = Roster.slots[unit]
	if slot == nil then
		slot = Roster.nextSlot
		Roster.nextSlot = slot + 1
		Roster.slots[unit] = slot
	end
	Roster.slotSeen[unit] = frame
	return slot
end

local function roster_key(hash)
	local space = S.IdString64
	if type(space) ~= "table" or type(space.from_hex) ~= "function" then return nil end
	return try(space.from_hex, hash)
end

local function roster_point(unit, node)
	local x, y, z = readVector(S, try(S.Unit.world_position, unit, node))
	if x ~= nil then return { x, y, z } end
end

local function roster_distance(a, b)
	if not a or not b then return math.huge end
	local dx, dy, dz = a[1] - b[1], a[2] - b[2], a[3] - b[3]
	return math.sqrt(dx * dx + dy * dy + dz * dz)
end

--- IAS discover_armour_units：返回本世界当前的装甲件 { unit, hash }。
local function roster_discover(runtime, world, frame, unitCount, signature, gameplay)
	local hashes = roster_hashes(runtime)
	-- IAS 没有 profile 表就不会有任何发现（也就没有全量扫描）。
	if #hashes == 0 then return {} end
	local record = Roster.worlds[world]
	if record == nil then
		record = { last = -1000000, counts = {}, negative = { signature = nil, untilTime = {}, nextFull = 0 } }
		Roster.worlds[world] = record
	end
	local now = os.clock()
	local previous = record.counts or {}
	local negative = record.negative
	local actorChanged = negative.signature ~= signature
	if actorChanged then negative = { signature = signature, untilTime = {}, nextFull = 0 }; record.negative = negative end
	local validCount = type(unitCount) == "number" and unitCount == unitCount and unitCount >= 0
	local populationChanged = not gameplay and validCount and unitCount <= 512 and unitCount ~= previous.__unit_count
	local needScan = frame - record.last >= Roster.INTERVAL
	if gameplay then needScan = actorChanged or now >= (negative.nextFull or 0) end
	if populationChanged then needScan = true end
	local discovered, seen, fast = {}, {}, {}
	for _, hash in ipairs(hashes) do
		local due = needScan or populationChanged or not signature or (previous[hash] or 0) > 0
			or now >= (negative.untilTime[hash] or 0)
		local units = {}
		if due then
			local key = roster_key(hash)
			units = key ~= nil and try(S.World.units_by_resource, world, key) or nil
			negative.untilTime[hash] = (type(units) == "table" and #units == 0) and now + (Roster.negativeSeconds or 0.25) or nil
			if type(units) ~= "table" then units = {} end
		end
		local count = 0
		for index = 1, #units do
			local unit = units[index]
			local name = try(S.Unit.debug_name, unit)
			if type(name) == "string" and name:find(hash, 1, true) and not seen[unit] then
				seen[unit] = true
				count = count + 1
				discovered[#discovered + 1] = { unit = unit, hash = hash }
			end
		end
		fast[hash] = count
		if count < (previous[hash] or 0) then needScan = true end
	end
	if needScan then
		local all = try(S.World.units, world)
		if type(all) == "table" then
			local wanted = {}
			for _, hash in ipairs(hashes) do wanted[hash] = true end
			local names = {}
			for index = 1, #all do
				local unit = all[index]
				local name = try(S.Unit.debug_name, unit)
				if type(name) == "string" then
					local matched = names[name]
					if matched == nil then
						matched = false
						for run in name:gmatch("%x+") do
							for start = 1, #run - 15 do
								local piece = run:sub(start, start + 15):lower()
								if wanted[piece] then matched = piece break end
							end
							if matched then break end
						end
						names[name] = matched
					end
					if matched and not seen[unit] then
						seen[unit] = true
						discovered[#discovered + 1] = { unit = unit, hash = matched }
					end
				end
			end
			local counts = {}
			for _, row in ipairs(discovered) do counts[row.hash] = (counts[row.hash] or 0) + 1 end
			if validCount then counts.__unit_count = unitCount end
			record.counts = counts
			if gameplay then negative.nextFull = now + 2.0 end
		end
		record.last = frame
	else
		fast.__unit_count = previous.__unit_count
		record.counts = fast
	end
	-- IAS 行 1651–1656：600 帧没扫过的世界记录清掉。
	for key, other in pairs(Roster.worlds) do
		if frame - other.last > 600 then Roster.worlds[key] = nil end
	end
	return discovered
end

--- IAS physics_identity.update：本人 / 队友 / 死亡（Network + GameSession，4 Hz）。
--- IAS __hd2ir_unit_instance_slot：同一个 unit 值映射到单调递增的槽号；超过 2 帧没出现的键每 120 帧清一次。
--- 只做表查询，不调引擎；必须每帧对在场 avatar 调用，否则 4 Hz 的判定轮询会把键清掉造成假“换实例”。
local function roster_instance_slot(value, frame)
	-- 与 Roster.instanceSlot 共用同一张槽位表（IAS 只有一张）。
	return Roster.instanceSlot(value, frame)
end

--- IAS solo_discovery_allowed（行 4756–4789）：只跳过"猜测式"发现（低位扫描）；已知对象刷新照常。
--- 独自一人且已验证：唯一 avatar 的网络对象仍存在、类型对、血量与位置吻合；两份成员名单（peers / other_peers）都只含自己。
local function roster_solo_allowed(S, GS, Net, states, known, slots, avatars, session, peer, owned, complete)
	if #avatars ~= 1 or not complete or type(peer) ~= "string" or #peer == 0 or #peer > 128 then return false end
	local avatar = avatars[1]
	if type(avatar) ~= "table" or not avatar.unit or not avatar.world then return false end
	local state = states[avatar.unit]
	if not state or state.owner ~= true or state.dead or not state.network or state.world ~= avatar.world
		or not known[state.network] or not owned[state.network] or Roster.session ~= session then return false end
	for _, name in ipairs({ "peers", "other_peers" }) do
		if type(GS[name]) ~= "function" then return false end
		local rows = try(GS[name], session)
		if type(rows) ~= "table" then return false end
		local count = 0
		for _, member in pairs(rows) do
			count = count + 1
			if count > 32 or type(member) ~= "string" or #member == 0 or #member > 128 or member ~= peer then return false end
		end
	end
	if try(Net.game_session) ~= session or try(Net.peer_id) ~= peer or try(GS.in_session, session) ~= true
		or try(GS.game_object_exists, session, state.network) ~= true
		or try(GS.game_object_is_type, session, state.network, "gl7xl2w") ~= true
		or try(S.Unit.alive, avatar.unit) ~= true
		or try(S.Unit.has_animation_state_machine, avatar.unit) ~= true then return false end
	local health = try(GS.game_object_field, session, state.network, "health")
	local x, y, z = readVector(S, try(GS.game_object_field, session, state.network, "position"))
	return type(health) == "number" and health == health and health > 0 and health < 1e9
		and x ~= nil and roster_distance(avatar.p, { x, y, z }) <= 1.0
end

--- IAS squad_discovery_allowed（行 4794–4846）：2–4 个 avatar 各有新鲜、唯一的网络 / owner 证明，
--- 且两份成员名单一致、与可见 avatar 的 owner 集合完全吻合。只跳过猜测式 id 发现与远端枚举。
local function roster_squad_allowed(S, GS, Net, states, known, slots, avatars, session, peer, owned, complete)
	if #avatars < 2 or #avatars > 4 or not complete or type(peer) ~= "string" or #peer == 0 or #peer > 128
		or type(GS.game_object_owner) ~= "function" then return false end
	local members
	for _, name in ipairs({ "peers", "other_peers" }) do
		if type(GS[name]) ~= "function" then return false end
		local list = try(GS[name], session)
		if type(list) ~= "table" then return false end
		local found, count = {}, 0
		for _, owner in pairs(list) do
			count = count + 1
			if count > 4 or type(owner) ~= "string" or #owner == 0 or #owner > 128 then return false end
			if owner ~= peer then
				if found[owner] then return false end
				found[owner] = true
			end
		end
		if members then
			for owner in pairs(found) do if not members[owner] then return false end end
			for owner in pairs(members) do if not found[owner] then return false end end
		else members = found end
	end
	local seen, networks, world, selfCount = {}, {}, nil, 0
	for _, avatar in ipairs(avatars) do
		local state = states[avatar.unit]
		if not state or state.dead or not state.network or not known[state.network] or state.world ~= avatar.world
			or state.instance ~= slots[avatar.unit] or networks[state.network] or (world and world ~= avatar.world)
			or try(S.Unit.alive, avatar.unit) ~= true
			or try(S.Unit.has_animation_state_machine, avatar.unit) ~= true then return false end
		world = avatar.world; networks[state.network] = true
		if try(GS.game_object_exists, session, state.network) ~= true
			or try(GS.game_object_is_type, session, state.network, "gl7xl2w") ~= true then return false end
		local owner = try(GS.game_object_owner, session, state.network)
		if type(owner) ~= "string" or #owner == 0 or #owner > 128 or seen[owner] then return false end
		seen[owner] = true
		if owner == peer then
			if state.owner ~= true or not owned[state.network] then return false end
			selfCount = selfCount + 1
		elseif not members[owner] or state.owner ~= false or state.friendly ~= true or owned[state.network] then return false end
		local health = try(GS.game_object_field, session, state.network, "health")
		local x, y, z = readVector(S, try(GS.game_object_field, session, state.network, "position"))
		if type(health) ~= "number" or health ~= health or health <= 0 or health >= 1e9 or x == nil
			or roster_distance(avatar.p, { x, y, z }) > 1.0 then return false end
	end
	for owner in pairs(members) do if not seen[owner] then return false end end
	return selfCount == 1 and try(Net.game_session) == session and try(Net.peer_id) == peer
		and try(GS.in_session, session) == true
end

local function roster_identity(runtime, avatars)
	local Net, GS = S.Network, S.GameSession
	if type(avatars) ~= "table" or #avatars > 32 then return end   -- IAS 行 4841
	local states = Roster.states
	local frame = runtime.frame or 0
	local slots = {}
	for _, avatar in ipairs(avatars) do slots[avatar.unit] = roster_instance_slot(avatar.unit, frame) end
	local present = {}
	for _, avatar in ipairs(avatars) do present[avatar.unit] = avatar.world end
	-- 先作废缺席再节流：拒绝快速指针复用（IAS 行 4745–4747）
	for unit, state in pairs(states) do if present[unit] ~= state.world then states[unit] = nil end end
	local now = os.clock()
	if now < Roster.nextPoll then return end
	Roster.nextPoll = now + (Roster.pollInterval or 0.25)
	for _, state in pairs(states) do state.role = 0 end
	if type(Net) ~= "table" or type(GS) ~= "table" then return end
	local session = try(Net.game_session)
	if not session or try(GS.in_session, session) ~= true then
		Roster.known, Roster.session = {}, nil
		Roster.extendedCursor, Roster.nearCursor, Roster.observedHigh, Roster.poll = 4096, 0, 4095, 0
		Roster.remoteCursors = {}
		Roster.states = setmetatable({}, { __mode = "k" })
		return
	end
	if Roster.session ~= session then
		Roster.epoch = Roster.epoch + 1
		Roster.known, Roster.session = {}, session
		Roster.extendedCursor, Roster.nearCursor, Roster.observedHigh, Roster.poll = 4096, 0, 4095, 0
		Roster.remoteCursors = {}
		Roster.states = setmetatable({}, { __mode = "k" })
		states = Roster.states
	end
	local peer = try(Net.peer_id)
	local owned, complete, ownedIds = {}, false, {}
	local list = try(GS.objects_owned_by, session, peer)
	if type(list) == "table" then
		complete = true
		local count = 0
		for _, id in pairs(list) do
			count = count + 1
			if count > 8192 then complete = false break end
			if type(id) == "number" and id >= 0 and id <= 4294967295 and id == math.floor(id) then
				if not owned[id] then owned[id] = true; ownedIds[#ownedIds + 1] = id end
			else complete = false end
		end
	end
	table.sort(ownedIds, function(a, b) return a > b end)
	local considered = {}
	local known = Roster.known
	local function consider(id, refresh)
		if not refresh and considered[id] ~= nil then return end
		considered[id] = true
		if try(GS.game_object_exists, session, id) == true and try(GS.game_object_is_type, session, id, "gl7xl2w") == true then
			known[id] = true
		else
			known[id] = nil
		end
	end
	-- IAS 行 4879–4889：solo / squad 已验证时跳过本地低位扫描（只跳过发现，已知对象刷新照常）
	local solo = roster_solo_allowed(S, GS, Net, states, known, slots, avatars, session, peer, owned, complete)
	local squad = not solo and roster_squad_allowed(S, GS, Net, states, known, slots, avatars, session, peer, owned, complete)
	if not solo and not squad then
		for index = 1, math.min(16, #ownedIds) do consider(ownedIds[index]) end
		for _ = 1, math.min(64, #ownedIds) do
			Roster.ownedCursor = Roster.ownedCursor % #ownedIds + 1
			consider(ownedIds[Roster.ownedCursor])
		end
		for _ = 1, 64 do consider(Roster.cursor); Roster.cursor = (Roster.cursor + 1) % 4096 end
	end
	-- IAS 行 4909–4951：枚举真实的远端 owner（GS.peers 与 GS.other_peers 合并，各读至多 32 个，去掉自己），
	-- 排序后取前 4 个，每个 owner 考察前 8 个 id + 16 个游标 id。
	if #avatars > 1 and not squad and type(peer) == "string" then
		local members = {}
		for _, name in ipairs({ "peers", "other_peers" }) do
			local values = type(GS[name]) == "function" and try(GS[name], session)
			if type(values) == "table" then
				local count = 0
				for _, member in pairs(values) do
					count = count + 1
					if count > 32 then break end
					if type(member) == "string" and #member > 0 and #member <= 128 and member ~= peer then members[member] = true end
				end
			end
		end
		local order = {}
		for member in pairs(members) do order[#order + 1] = member end
		table.sort(order)
		local active = {}
		for index = 1, math.min(4, #order) do
			local remote = order[index]
			active[remote] = true
			local values = try(GS.objects_owned_by, session, remote)
			if type(values) == "table" then
				local set, found, count = {}, {}, 0
				for _, id in pairs(values) do
					count = count + 1
					if count > 8192 then break end
					if type(id) == "number" and id >= 0 and id <= 4294967295 and id == math.floor(id) and not set[id] then
						set[id] = true; found[#found + 1] = id
					end
				end
				table.sort(found, function(a, b) return a > b end)
				for j = 1, math.min(8, #found) do consider(found[j]) end
				local cursor = Roster.remoteCursors[remote] or 0
				for _ = 1, math.min(16, #found) do
					cursor = cursor % #found + 1
					consider(found[cursor])
				end
				Roster.remoteCursors[remote] = cursor
			end
		end
		for member in pairs(Roster.remoteCursors) do if not active[member] then Roster.remoteCursors[member] = nil end end
	end
	local unresolved = false
	for _, avatar in ipairs(avatars) do
		local state = states[avatar.unit]
		if not state or not state.network then unresolved = true break end
	end
	Roster.observedHigh = math.max(Roster.observedHigh, ownedIds[1] or 0)
	if #avatars > 1 and unresolved and Roster.observedHigh >= 4096 then
		local upper = math.min(4294967295, Roster.observedHigh + 256)
		local lower = math.max(4096, Roster.observedHigh - 512)
		for _ = 1, 16 do consider(lower + Roster.nearCursor % (upper - lower + 1)); Roster.nearCursor = Roster.nearCursor + 1 end
		for _ = 1, 16 do
			if Roster.extendedCursor > upper then Roster.extendedCursor = 4096 end
			consider(Roster.extendedCursor); Roster.extendedCursor = Roster.extendedCursor + 1
		end
	end
	local ids = {}
	for id in pairs(known) do ids[#ids + 1] = id end
	if #ids > 32 then Roster.known, known, ids = {}, {}, {} end
	local objects = {}
	for _, id in ipairs(ids) do
		consider(id, true)
		if known[id] then
			local x, y, z = readVector(S, try(GS.game_object_field, session, id, "position"))
			local health = try(GS.game_object_field, session, id, "health")
			if x ~= nil and type(health) == "number" and health == health and math.abs(health) < 1e9 then
				objects[id] = { p = { x, y, z }, health = health, owned = owned[id] == true, complete = complete }
			end
		end
	end
	local live = {}
	for _, avatar in ipairs(avatars) do
		if try(S.Unit.alive, avatar.unit) == true then
			local state = states[avatar.unit]
			local instance = slots[avatar.unit]
			if not state or state.world ~= avatar.world or state.instance ~= instance then state = { role = 0, instance = instance } end
			states[avatar.unit] = state
			state.world = avatar.world
			local machine = try(S.Unit.has_animation_state_machine, avatar.unit)
			live[#live + 1] = { a = avatar, s = state, machine = machine }
			local object = state.network and objects[state.network]
			if object and object.health <= 0 then state.dead = true; state.deadSince = state.deadSince or now end
		end
	end
	for id, object in pairs(objects) do
		if object.health > 0 then
			local chosen, best, second = nil, math.huge, math.huge
			for _, item in ipairs(live) do
				if not item.s.dead and item.machine == true then
					local d = roster_distance(item.a.p, object.p)
					if d < best then second, best, chosen = best, d, item elseif d < second then second = d end
				end
			end
			if chosen then
				local state = chosen.s
				local threshold = state.network == id and 1.0 or 0.05
				if best <= threshold and second - best > 0.25 and (not state.network or state.network == id) then
					state.network = id
					if object.owned then state.owner = true
					elseif object.complete and state.owner ~= true then state.owner = false end
				end
			end
		end
	end
	local localCount = {}
	for _, item in ipairs(live) do
		local state = item.s
		if state.owner == true and not state.dead and item.machine == true then
			localCount[state.world] = (localCount[state.world] or 0) + 1
		end
	end
	-- IAS 行 5029–5060：稳定互斥回退。只给还没连上网络对象的远端 avatar；
	-- 需连续 4 次轮询都是同一个对象、误差 <= 0.5 m、次近 - 最近 > 0.5 m、其他活体 avatar 的间隔 > 0.5 m。
	Roster.poll = Roster.poll + 1
	local poll = Roster.poll
	local claimed = {}
	for _, item in ipairs(live) do if item.s.network then claimed[item.s.network] = true end end
	for _, item in ipairs(live) do
		local state = item.s
		if not state.network and not state.dead and item.machine == true and complete and localCount[state.world] == 1 then
			local bestId, best, second = nil, math.huge, math.huge
			for id, o in pairs(objects) do
				if o.health > 0 then
					local d = roster_distance(item.a.p, o.p)
					if d < best then second, best, bestId = best, d, id elseif d < second then second = d end
				end
			end
			local o = bestId and objects[bestId]
			local margin = math.huge
			if o then
				for _, other in ipairs(live) do
					if other ~= item and not other.s.dead and other.machine == true then
						margin = math.min(margin, roster_distance(other.a.p, o.p) - best)
					end
				end
			end
			if o and not o.owned and not claimed[bestId] and best <= 0.5 and second - best > 0.5 and margin > 0.5 then
				if state.fallbackId == bestId and state.fallbackPoll == poll - 1 and now - state.fallbackAt <= 0.75 then
					state.fallbackHits = state.fallbackHits + 1
				else state.fallbackHits = 1 end
				state.fallbackId, state.fallbackPoll, state.fallbackAt = bestId, poll, now
				if state.fallbackHits >= 4 then
					state.network, state.owner, state.matchMethod = bestId, false, "stable-mutual"
					claimed[bestId] = true
				end
			else state.fallbackHits = 0; state.fallbackId = nil end
		else state.fallbackHits = 0; state.fallbackId = nil end
	end
	for _, item in ipairs(live) do
		local state = item.s
		state.role = 0
		if state.dead then state.role = 3
		elseif item.machine == true then
			if state.owner == true and localCount[state.world] == 1 then state.role = 1
			elseif state.owner == false and (localCount[state.world] == 1 or (localCount[state.world] == nil and state.friendly == true)) then
				local object = state.network and objects[state.network]
				if object and object.health > 0 and object.complete and not object.owned
					and roster_distance(item.a.p, object.p) <= 1.0 then state.role = 2; state.friendly = true end
			end
		end
	end
end

function Roster.role(unit)
	local state = Roster.states[unit]
	return state and state.role or 0
end

function Roster.dead(unit)
	local state = Roster.states[unit]
	return state ~= nil and state.dead == true
end

--- 每帧：枚举 avatar、装甲件，求当帧归属。结果只保留到下一帧。
function Roster.update(runtime)
	local worlds = try(S.Application.worlds)
	if type(worlds) ~= "table" then runtime.roster = nil return end
	local frame = runtime.frame or 0
	local resource = (runtime.options or {}).avatarResource or DEFAULT_AVATAR_RESOURCE
	local roster = { frame = frame, byWorld = {}, live = {}, localAvatar = nil,
		identity = type(S.Network) == "table" and type(S.GameSession) == "table" }
	local allAvatars = {}
	local perWorld = {}
	for _, world in ipairs(worlds) do
		local units = try(S.World.units_by_resource, world, resource)
		local avatars = {}
		if type(units) == "table" then
			for index = 1, #units do
				local unit = units[index]
				local count = try(S.Unit.num_scene_graph_items, unit)
				if type(count) == "number" and count > 1 then
					local p = roster_point(unit, 1)
					if p ~= nil then
						local item = { unit = unit, world = world, p = p }
						avatars[#avatars + 1] = item
						allAvatars[#allAvatars + 1] = item
						roster.live[unit] = true
					end
				end
			end
		end
		perWorld[world] = avatars
	end
	local okIdentity, identityError = pcall(roster_identity, runtime, allAvatars)
	if not okIdentity and not runtime.identityErrorLogged then
		runtime.identityErrorLogged = true
		runtime.logger('[physbone][错误] 身份判定失败：%s', tostring(identityError))
	end
	for _, world in ipairs(worlds) do
		local avatars = perWorld[world]
		local slots, gameplay = {}, false
		for _, item in ipairs(avatars) do
			slots[#slots + 1] = Roster.instanceSlot(item.unit, frame)
			local role = Roster.role(item.unit)
			if role == 1 or role == 2 then gameplay = true end
			if role == 1 and roster.localAvatar == nil then roster.localAvatar = item.unit; roster.localWorld = world end
		end
		table.sort(slots)
		local count = try(S.World.num_units, world)
		-- IAS 行 1386–1392：只扫 active 世界。我们能判的只有"有 avatar 且单位数 > 0"
		-- （IAS 另有 scene_sources / 第一人称图两个条件，本运行时没有对应数据）。
		local active = #avatars > 0 and not (type(count) == "number" and count <= 0)
		local armour = active and roster_discover(runtime, world, frame, count, table.concat(slots, ":"), gameplay) or {}
		local owner = {}
		for _, row in ipairs(armour) do
			roster.live[row.unit] = true
			local nodes = try(S.Unit.num_scene_graph_items, row.unit)
			if type(nodes) == "number" and nodes > 1 then
				-- IAS 行 6702–6725：每个探针 {1,0} 都算，每个 avatar 取各探针里的最小距离，再比 avatar。
				local distance = {}
				for _, probe in ipairs({ 1, 0 }) do
					if probe < nodes then
						local p = roster_point(row.unit, probe)
						if p ~= nil then
							for index, item in ipairs(avatars) do
								local d = roster_distance(p, item.p)
								if distance[index] == nil or d < distance[index] then distance[index] = d end
							end
						end
					end
				end
				local best, second, bestD = nil, math.huge, math.huge
				for index, item in ipairs(avatars) do
					local d = distance[index]
					if d ~= nil then
						if d < bestD then second, bestD, best = bestD, d, item
						elseif d < second then second = d end
					end
				end
				if best ~= nil and bestD < 3.0 and second - bestD > Roster.AMBIGUITY then owner[row.unit] = best.unit end
			end
		end
		roster.byWorld[world] = { avatars = avatars, armour = armour, owner = owner }
	end
	runtime.roster = roster
	return roster
end

function Roster.reset()
	Roster.worlds = {}
	Roster.states = setmetatable({}, { __mode = "k" })
	Roster.known, Roster.session = {}, nil
	Roster.nextPoll, Roster.ownedCursor, Roster.cursor = 0, 0, 0
	Roster.extendedCursor, Roster.nearCursor, Roster.observedHigh = 4096, 0, 4095
	Roster.slots, Roster.slotSeen, Roster.nextSlot, Roster.slotPruned = {}, {}, 1, -1
	Roster.remoteCursors, Roster.poll = {}, 0
end

--- 本回调枚举里存在的 unit（含 avatar 与装甲件）。
function Roster.isLive(runtime, unit)
	local roster = runtime.roster
	return roster ~= nil and roster.frame == (runtime.frame or 0) and roster.live[unit] == true
end

--- IAS 的"本人"：role == 1。没有网络身份可用时没有本人（IAS 同样没有回退）。
function Roster.localAvatar(runtime)
	local roster = runtime.roster
	if roster == nil then return nil end
	return roster.localAvatar, roster.localWorld
end

local INDEPENDENT_LOG_FRAMES = { [1] = true, [600] = true }

local function safe_node(S_, unit, name)
	if try(S_.Unit.has_node, unit, name) ~= true then return nil end
	local id = try(S_.Unit.node, unit, name)
	local count = try(S_.Unit.num_scene_graph_items, unit)
	if type(id) ~= "number" or id < 1 or (type(count) == "number" and id > count) then return nil end
	return id
end

local function vec_dist(ax, ay, az, bx, by, bz)
	if ax == nil or bx == nil then return nil end
	local dx, dy, dz = ax - bx, ay - by, az - bz
	return math.sqrt(dx * dx + dy * dy + dz * dz)
end

local function format_mm(value)
	if value == nil then return "?" end
	return string.format("%.1fmm", value * 1000)
end

local function pack_animation_state(ok, ...)
	if ok then return { n = select("#", ...), ... } end
end

local function frame_animation_state(animation, avatar)
	if animation.readState then return animation.tuple, 0 end
	animation.readState = true
	animation.tuple = pack_animation_state(pcall(S.Unit.animation_get_state, avatar))
	return animation.tuple, 1
end

local function frame_animation_layer(animation, avatar)
	if animation.readLayer then return animation.time, animation.length, 0 end
	animation.readLayer = true
	animation.time, animation.length = try(S.Unit.animation_layer_info, avatar, 11)
	return animation.time, animation.length, 1
end

local function independent_visibility(piece, show)
	local ok, result = pcall(S.Unit.set_unit_visibility, piece.unit, show)
	if ok and result ~= false then piece.hidden = not show end
	return 1
end

local IndependentLink = {}

-- IAS 1.6.4 layer roles plus the locally observed ship/prone empty states.
-- Reload lengths are in 60 Hz clip ticks. The game clock reports seconds.
IndependentLink.reloadClips = {
	[159]=170, [168]=90, [446]=180, [475]=185, [476]=135, [477]=200, [478]=115, [479]=150,
	[488]=185, [489]=185, [490]=135, [491]=135, [494]=90, [499]=200, [500]=200, [501]=130, [502]=130,
	[510]=170, [511]=170, [573]=170, [586]=90, [592]=180, [593]=120, [594]=120, [595]=180,
	[596]=220, [597]=220, [600]=299, [601]=250, [612]=60, [633]=180, [634]=215, [635]=175,
	[636]=110, [637]=50, [638]=40, [639]=50, [640]=30, [665]=105, [666]=105, [681]=180,
	[686]=40, [687]=50, [691]=154, [698]=190, [699]=135, [700]=90, [701]=70, [702]=90, [703]=70,
	[704]=190, [705]=190, [706]=120, [707]=120, [725]=120, [732]=200, [733]=130, [744]=130,
	[745]=90, [748]=130, [749]=90, [767]=190, [768]=135, [774]=210, [775]=125, [781]=150,
	[799]=180, [800]=130, [865]=180,
}
IndependentLink.motionGrips = {
	r = { p = { 0.051216014, -0.006265312, -0.055564534 }, q = { -0.579370737, -0.437321216, -0.442053050, 0.526942849 } },
	l = { p = { 0.059206679, -0.001960278, -0.046801239 }, q = { 0.386549324, 0.487733185, -0.622131407, 0.475024790 } },
}

-- Asset IDs, scene-graph sizes, root slots, parent signatures and sidearm
-- flags from the IAS 1.6.4 weapon catalogue. Optional bounds cover the
-- native moving left-control sweep in the right-control frame.
IndependentLink.weaponGraphs = {
	["03e67a19b07c6523"] = { 38, 2, 1819903690, false },
	["05d8d8c073b9d502"] = { 51, 2, 274910700, false, { 0.2235870, 0.0559892, -0.2017267, 0.3600964, 0.0718231, -0.1779306 } },
	["05e4e5c2db6e44a2"] = { 33, 2, 1561093617, true },
	["076dd5d4f4360204"] = { 32, 2, 128809422, false, { 0.3606843, 0.0153683, -0.1502686, 0.4710091, 0.0159076, -0.1402258 } },
	["0b882808c6f498e8"] = { 39, 2, 378417319, true },
	["0f83639ab8c86165"] = { 50, 15, 787771201, false },
	["11ec8e2296a3d662"] = { 74, 2, 870721566, false, { 0.2411331, 0.0333919, -0.1703917, 0.3778624, 0.0494076, -0.1465957 } },
	["14d5d4506056c7a4"] = { 43, 2, 1117374565, true },
	["186ea95de7306b1a"] = { 36, 2, 1978993991, false },
	["1a437158e1b8d2a1"] = { 36, 2, 1710260533, true },
	["1abbff60d26ba391"] = { 30, 2, 1247580091, false },
	["27ee1ed8f6fb6356"] = { 40, 3, 1361605581, false },
	["295beb26dc4f8ff1"] = { 39, 2, 1367166588, false },
	["2b28e17ffed05f7c"] = { 43, 2, 858667509, true },
	["30061f91af477f5e"] = { 36, 3, 476231973, false },
	["3575aabc5f1f9326"] = { 35, 3, 1966170913, true },
	["3c86e871923f3970"] = { 45, 2, 93729406, false },
	["3f92ba65ef65cca9"] = { 33, 2, 83927365, true },
	["416d053372c4e433"] = { 32, 2, 768516475, true },
	["41eac4a03987faa0"] = { 75, 2, 787402567, false, { 0.2235870, 0.0559892, -0.2017267, 0.3603163, 0.0720048, -0.1779306 } },
	["46183b50961d1328"] = { 37, 3, 1428336574, false },
	["4ba41b6f9f405cc2"] = { 40, 3, 1310516119, false },
	["4d58c77087b774c5"] = { 36, 2, 1710260535, true },
	["4dbd74f49c8ffc13"] = { 36, 2, 171269624, false },
	["4e4a613eb9bf5c24"] = { 38, 2, 1217661892, false },
	["4fb0f8c02f55c82b"] = { 30, 2, 1839515245, false },
	["52e4334e6a128caf"] = { 32, 2, 47759735, true },
	["53eebe75cd6e26df"] = { 75, 2, 787402567, false, { 0.2235870, 0.0559892, -0.2017267, 0.3603163, 0.0720048, -0.1779306 } },
	["5fecab819f96a3e8"] = { 37, 2, 1891200416, false },
	["708ea298c82093d0"] = { 38, 2, 639684404, false },
	["72170a55a1f37ff1"] = { 38, 2, 1646819641, false, { 0.2423786, 0.0466107, -0.1496554, 0.2506835, 0.0657484, -0.1487711 } },
	["7b06196e90154c88"] = { 34, 3, 1302795119, true },
	["7b75e5132ffd4ca6"] = { 42, 2, 450936345, false, { 0.2017704, 0.0965996, -0.1967479, 0.4060056, 0.1558298, -0.1227190 } },
	["7e3145a5baa4b948"] = { 32, 2, 2116111373, false },
	["80f1a156d9fa1e36"] = { 38, 3, 1105069466, false },
	["84354339522c932d"] = { 36, 2, 1918168807, false },
	["8645f167b3c813a2"] = { 37, 2, 389142160, false },
	["8a307bd1811a5fe9"] = { 34, 2, 1407847902, false },
	["8d3d52a3b2f19402"] = { 60, 3, 103015341, true },
	["8dc91f277c6096ee"] = { 38, 3, 1276517676, false },
	["90ddc374f4e3d756"] = { 38, 2, 1159570608, false, { 0.2740060, 0.0123188, -0.1606764, 0.3630209, 0.0226438, -0.1451595 } },
	["94bd931b5fb4ee95"] = { 39, 2, 290697657, false },
	["9571ca51f0daf35b"] = { 35, 2, 1244341504, false },
	["968211c0033dce64"] = { 43, 2, 1365212662, false },
	["9eb160830321bfd6"] = { 33, 3, 755676466, true },
	["a6a735accb4a327f"] = { 41, 2, 2075574723, false },
	["a7ee1ebf58fcdf1f"] = { 43, 2, 683329037, false },
	["aa69a60d74a3ec54"] = { 43, 3, 338970381, true },
	["bc29613666df696b"] = { 38, 2, 1601355231, false },
	["be70ee0d8d44028e"] = { 35, 2, 80160173, false, { 0.1960675, 0.0005412, -0.1625393, 0.2044715, 0.0630153, -0.1589530 } },
	["c12a34f375bd5a87"] = { 39, 3, 1246493056, false },
	["c4232a0e62166d91"] = { 38, 2, 123780754, false },
	["c780bcd79547da0f"] = { 44, 2, 934893741, true },
	["cdf28be026bb7d84"] = { 44, 3, 393988844, false },
	["ce063aa33d95a812"] = { 38, 2, 431026281, false },
	["cf8934ff6567a42d"] = { 44, 2, 949681621, true },
	["d323de60855898ac"] = { 75, 2, 787402567, false, { 0.2235870, 0.0559892, -0.2017267, 0.3603163, 0.0720048, -0.1779306 } },
	["d6b1fb05b9109353"] = { 46, 2, 290653440, true },
	["dbb6c961c59fadc1"] = { 32, 2, 758111369, true },
	["dcd1c835407ef7ba"] = { 38, 2, 1437803423, false, { 0.2065672, -0.0005330, -0.1740642, 0.2848286, 0.0085447, -0.1604218 } },
	["e5796355a8fd67e0"] = { 39, 2, 200464068, false },
	["e6d932be83729076"] = { 41, 2, 508840821, false },
	["eea5e3cef1e12c14"] = { 46, 2, 1830706571, false },
	["efdcef306cea63fe"] = { 47, 2, 1030894062, false },
	["f0338468dcdb6a6c"] = { 35, 2, 326188192, false },
	["f49227a0630a3f7f"] = { 50, 2, 601786065, false },
	["f992ce97577c8a7f"] = { 40, 2, 1567577924, false, { 0.3563594, 0.0170015, -0.1217884, 0.3817801, 0.0530078, -0.1166465 } },
	["02eecd0b1fa49630"] = { 40, 2, 1575613387, false },
	["11c27d3babb38956"] = { 46, 2, 696751716, false },
	["2152d5147b0ac418"] = { 39, 2, 1326004856, false },
	["25aa2fd4643cf4ee"] = { 32, 2, 1430532530, false },
	["26e40437ea275296"] = { 38, 2, 1168239596, false },
	["2e9d0bdc48b09e60"] = { 39, 3, 1270607482, false },
	["35a61296619cc47e"] = { 41, 2, 503420083, false },
	["3828e2051aa9e897"] = { 47, 2, 788655962, false },
	["39ab99895147a3bf"] = { 33, 3, 1860855984, false },
	["43a58cb89cfa197c"] = { 33, 2, 1185781154, false },
	["52071f49263415e4"] = { 35, 2, 1460748206, false },
	["5990123d142b16cb"] = { 42, 2, 1026156458, false },
	["6cfcc7f8801a0266"] = { 36, 2, 290032866, false },
	["7617642765ac38c7"] = { 32, 2, 263546244, false },
	["78a8185f63a70795"] = { 31, 2, 1574060098, false },
	["80932fa0ed6901d3"] = { 33, 2, 2072574442, false },
	["88c2d09ad85a7c9f"] = { 38, 2, 848326378, false },
	["89c5493e08ca4207"] = { 39, 2, 1602264935, false },
	["96de9cd50f7306e6"] = { 44, 2, 1508873121, false },
	["9f80d67a12a7e40f"] = { 42, 2, 1597238702, false },
	["a8cffb316f0b5c5f"] = { 63, 3, 283677617, false },
	["b16c9d490aa59b77"] = { 51, 2, 125009885, false },
	["b2b5e0d185605f9e"] = { 40, 2, 1462781686, false },
	["cc786f6491fe7e65"] = { 39, 2, 320010747, false },
	["d54b9505c0f72873"] = { 37, 2, 1469459401, false },
	["e8d5f49ad7780e54"] = { 37, 2, 1869403091, false },
	["fe3b29b2cfa63f9b"] = { 55, 2, 511412428, false },
	["a8a91eb54892b6b2"] = { 43, 2, 706204382, false },
	["e91f569c2ad8af01"] = { 36, 2, 511394286, true },
	["c85f576d5e086147"] = { 48, 2, 1019708248, false },
}

-- Resource/count pairs identify candidate child Units for the optional root trial.
IndependentLink.weaponMembers = {
	["03e67a19b07c6523"] = "07d605bd50c98461:16 44a2a59ca08ac865:12 642f2be0417d0beb:15 6d54652ee217d712:12 6e68194b95d60145:38 7b153e02ad22d12c:12 7fdd884ecd57231b:6 9b30d4ac39ddff10:16 bd4245dcfd4b832f:11 bdbefa6543a75942:22 e5796355a8fd67e0:39",
	["05d8d8c073b9d502"] = "0c7314742b88806c:13 aca180fc6f846411:12 f9df21c8e52cac95:13 fa6f926a77b78b55:12",
	["05e4e5c2db6e44a2"] = "0ec5ef9ec5cbd1b2:5 309f7e645a8d2b56:14 4851d75853622a47:15 4e4a613eb9bf5c24:38 4f839e923046e631:15 52071f49263415e4:35 6b88fea13ec2d78e:12 6b9572ac0f687394:6 77c5d934407114f4:17 8d3d52a3b2f19402:60 9dbc8aa8702ac9e1:12 b5b4395852e871bc:6 b96cc3b704a76464:7 e6b6467135035059:13 e71dbcb6a50f321b:158",
	["076dd5d4f4360204"] = "9dbc8aa8702ac9e1:12 e307b6e78b0a8573:3",
	["0b882808c6f498e8"] = "45be635fb904f4dc:12 7441b048b94491fc:12 bb8e970248330bdb:12 e2177e205d9a7a55:26 e98a9be765e0e236:5",
	["0f83639ab8c86165"] = "44a2a59ca08ac865:12 7fdd884ecd57231b:6 89cce4d15db88aa8:16 a9a19cffe985c468:12 bf9504e95c0103a1:12",
	["11ec8e2296a3d662"] = "3d58ea61b2b485cc:12 7f19d05cd188c768:6",
	["14d5d4506056c7a4"] = "350168e3fd02b325:9 6b9572ac0f687394:6",
	["186ea95de7306b1a"] = "309f7e645a8d2b56:14 7b153e02ad22d12c:12 8f141212b0f6f515:13 9d5e0d88df992aa6:6 9dbc8aa8702ac9e1:12",
	["1a437158e1b8d2a1"] = "005c2262f672caf4:18 309f7e645a8d2b56:14 6b9572ac0f687394:6",
	["1abbff60d26ba391"] = "3a8ad42985c52dc7:14 8a7631bbb4f6f3d8:10",
	["27ee1ed8f6fb6356"] = "129e5fb1b13c2018:12 309f7e645a8d2b56:14 50cd7e9f909fe5a1:12 cebd25f8778bfdee:14",
	["295beb26dc4f8ff1"] = "129e5fb1b13c2018:12 80e8335ab4042fcf:12",
	["2b28e17ffed05f7c"] = "7f19d05cd188c768:6",
	["30061f91af477f5e"] = "7301b3dc5b1bffb8:17 dd8cbbac49f7c628:15",
	["3575aabc5f1f9326"] = "78829731a0563576:13 9d5e0d88df992aa6:6 c891acf9e8dbda97:12 e6b6467135035059:13",
	["3c86e871923f3970"] = "309f7e645a8d2b56:14 3d58ea61b2b485cc:12 b1bc2b7252ccbc03:12",
	["3f92ba65ef65cca9"] = "a134e110ef4ef865:7",
	["416d053372c4e433"] = "50cd7e9f909fe5a1:12 78829731a0563576:13 7b06196e90154c88:34 e53e4b0c20563d21:5 e6b6467135035059:13",
	["41eac4a03987faa0"] = "7f19d05cd188c768:6 8f6f7c8c1e4b9b6d:11 9dbc8aa8702ac9e1:12",
	["46183b50961d1328"] = "309f7e645a8d2b56:14 3ddb9d36da22555d:36 45be635fb904f4dc:12 6228d0242bde56b6:38 68b8d040421e8a49:25 8cd335a5f711bb59:12 9dbc8aa8702ac9e1:12 b7cd78c71e0a48ed:6 d8cf7fe059b4bf1e:12",
	["4ba41b6f9f405cc2"] = "48d2ddc8e9a80b1e:14 6b9572ac0f687394:6",
	["4d58c77087b774c5"] = "4ffb842ce1093f3f:17 6b9572ac0f687394:6 e69567209a83df39:11",
	["4dbd74f49c8ffc13"] = "0040c16bd48573e2:10 7f49b46efdd850ef:16 7fdd884ecd57231b:6",
	["4e4a613eb9bf5c24"] = "05e4e5c2db6e44a2:33 0ec5ef9ec5cbd1b2:5 309f7e645a8d2b56:14 4851d75853622a47:15 4f839e923046e631:15 52071f49263415e4:35 6b88fea13ec2d78e:12 6b9572ac0f687394:6 77c5d934407114f4:17 8d3d52a3b2f19402:60 9dbc8aa8702ac9e1:12 b5b4395852e871bc:6 b96cc3b704a76464:7 e6b6467135035059:13 e71dbcb6a50f321b:158",
	["4fb0f8c02f55c82b"] = "511910c41a70122d:14",
	["52e4334e6a128caf"] = "5f4b129d947d79d7:13 78829731a0563576:13",
	["53eebe75cd6e26df"] = "3d58ea61b2b485cc:12 8f6f7c8c1e4b9b6d:11 e6caddc3ed05dd7e:4",
	["5fecab819f96a3e8"] = "129e5fb1b13c2018:12 7fdd884ecd57231b:6 c245c696e80677ed:20 ffc18b2ce10ca381:11",
	["708ea298c82093d0"] = "45be635fb904f4dc:12 7441b048b94491fc:12 7be1e04a7738e673:18 7fdd884ecd57231b:6",
	["72170a55a1f37ff1"] = "91eb62e0a02302ba:5",
	["7b06196e90154c88"] = "416d053372c4e433:32 50cd7e9f909fe5a1:12 78829731a0563576:13 e53e4b0c20563d21:5 e6b6467135035059:13",
	["7b75e5132ffd4ca6"] = "206cb0dd1696ceaa:5 7fdd884ecd57231b:6 8a35c1dc19f41870:10 a9a19cffe985c468:12",
	["7e3145a5baa4b948"] = "129e5fb1b13c2018:12 309f7e645a8d2b56:14 7dc6dda584cc5223:11 d44c25420aa3e2ff:26",
	["80f1a156d9fa1e36"] = "1ec5f54265a91c83:16 309f7e645a8d2b56:14 3d58ea61b2b485cc:12 ab9e1ad1597d5d9e:6",
	["84354339522c932d"] = "5d268a6f943792d4:10 963aca558cd906bc:17 cf907add37bfa9be:6",
	["8645f167b3c813a2"] = "129e5fb1b13c2018:12 e3541de70b7ec1c0:13",
	["8a307bd1811a5fe9"] = "1b1fcc0e025c1f7d:14 6b9572ac0f687394:6 72fe4b13f726c344:24 7b153e02ad22d12c:12 d5cb9b5401b261f9:11",
	["8d3d52a3b2f19402"] = "05e4e5c2db6e44a2:33 0ec5ef9ec5cbd1b2:5 309f7e645a8d2b56:14 4851d75853622a47:15 4e4a613eb9bf5c24:38 4f839e923046e631:15 52071f49263415e4:35 6b88fea13ec2d78e:12 6b9572ac0f687394:6 77c5d934407114f4:17 9dbc8aa8702ac9e1:12 b5b4395852e871bc:6 b96cc3b704a76464:7 e6b6467135035059:13 e71dbcb6a50f321b:158",
	["8dc91f277c6096ee"] = "029cfc4c193211c3:16 6d54652ee217d712:12 719f42b7d137789c:11 ab9e1ad1597d5d9e:6",
	["90ddc374f4e3d756"] = "0040c16bd48573e2:10 36e235816805c17e:5",
	["94bd931b5fb4ee95"] = "16051937941bb709:11 309f7e645a8d2b56:14 6b9572ac0f687394:6 7372947782b91123:17 fa6f926a77b78b55:12",
	["9571ca51f0daf35b"] = "309f7e645a8d2b56:14 7b153e02ad22d12c:12 9d5e0d88df992aa6:6 9dbc8aa8702ac9e1:12 be4590c2f615c441:14",
	["968211c0033dce64"] = "129e5fb1b13c2018:12 309f7e645a8d2b56:14 756e3fdaca41cfca:12 7be1e04a7738e673:18 7fdd884ecd57231b:6 a1a026c8bb37a96e:19 cb8d6dbe2ad74be4:58",
	["9eb160830321bfd6"] = "9dbc8aa8702ac9e1:12 b3bf071f477fc155:3",
	["a6a735accb4a327f"] = "028a1ed2394fb808:6 309f7e645a8d2b56:14 45be635fb904f4dc:12 73a5ecad32ba599b:35 756e3fdaca41cfca:12 7fdd884ecd57231b:6",
	["a7ee1ebf58fcdf1f"] = "2d761a80bcbfe792:18 309f7e645a8d2b56:14 45be635fb904f4dc:12 756e3fdaca41cfca:12 7fdd884ecd57231b:6",
	["aa69a60d74a3ec54"] = "aca180fc6f846411:12",
	["bc29613666df696b"] = "16ab263bc03b4f96:18 309f7e645a8d2b56:14 756e3fdaca41cfca:12 7fdd884ecd57231b:6 9dbc8aa8702ac9e1:12 bfe35746f5084222:3",
	["be70ee0d8d44028e"] = "0040c16bd48573e2:10 43639dc374b75c5f:15",
	["c12a34f375bd5a87"] = "309f7e645a8d2b56:14 3ddb9d36da22555d:36 b7cd78c71e0a48ed:6 cdf733b0106a23c3:11 fa6f926a77b78b55:12",
	["c4232a0e62166d91"] = "309f7e645a8d2b56:14 6b88fea13ec2d78e:12 6b9572ac0f687394:6 9dbc8aa8702ac9e1:12 fa6c1158a93b8ec4:17",
	["c780bcd79547da0f"] = "6b9572ac0f687394:6 7441b048b94491fc:12 bcbbe9bb1a49c7fd:5",
	["cdf28be026bb7d84"] = "7f0e355dedbdf3d5:17 7fdd884ecd57231b:6 83970097f3d997ad:12 e6b6467135035059:13",
	["ce063aa33d95a812"] = "129e5fb1b13c2018:12 2383b0439f0bc465:11 309f7e645a8d2b56:14 7fdd884ecd57231b:6 89cce4d15db88aa8:16",
	["cf8934ff6567a42d"] = "11eccafa4b56a49c:6 6b9572ac0f687394:6 fc1f0a3b3fc97238:15",
	["d323de60855898ac"] = "9dbc8aa8702ac9e1:12 e0e097b347bdfe6c:11 f401191f6b5a7879:5",
	["d6b1fb05b9109353"] = "4431931242cbcd54:5 7de84a343186671f:11 e98a9be765e0e236:5",
	["dbb6c961c59fadc1"] = "20e05bca458a39e8:5 619795f79ceda1b3:15",
	["dcd1c835407ef7ba"] = "36e235816805c17e:5 8a35c1dc19f41870:10 f40dea68b213b005:11",
	["e5796355a8fd67e0"] = "03e67a19b07c6523:38 07d605bd50c98461:16 44a2a59ca08ac865:12 642f2be0417d0beb:15 6d54652ee217d712:12 6e68194b95d60145:38 7b153e02ad22d12c:12 7fdd884ecd57231b:6 9b30d4ac39ddff10:16 bd4245dcfd4b832f:11 bdbefa6543a75942:22",
	["e6d932be83729076"] = "44a2a59ca08ac865:12 cda482684a202fd2:4",
	["eea5e3cef1e12c14"] = "193becdf64aa66a9:12 309f7e645a8d2b56:14 3d58ea61b2b485cc:12 80f0a339e905d2d3:12 bc9182001d0312bd:13 efdcef306cea63fe:47",
	["efdcef306cea63fe"] = "193becdf64aa66a9:12 309f7e645a8d2b56:14 3d58ea61b2b485cc:12 80f0a339e905d2d3:12 bc9182001d0312bd:13 eea5e3cef1e12c14:46",
	["f0338468dcdb6a6c"] = "44a2a59ca08ac865:12 462fc89d4903b33d:18 7441b048b94491fc:12 7fdd884ecd57231b:6",
	["f49227a0630a3f7f"] = "3d58ea61b2b485cc:12 820ac4b5db9b2585:20 8f063c5d3929316e:6",
	["f992ce97577c8a7f"] = "129e5fb1b13c2018:12 618c00f2f115c1fe:26",
	["02eecd0b1fa49630"] = "68513d59bafa8c7a:6 b98f2b3d1bde24b0:48 fa6f926a77b78b55:12",
	["11c27d3babb38956"] = "3d58ea61b2b485cc:12 66cb402846eaaa31:6 7b153e02ad22d12c:12 7fdd884ecd57231b:6 9d81323b153dc429:43",
	["2152d5147b0ac418"] = "085c1edb038ec24e:39 17c6e9bafcd646d2:6 45be635fb904f4dc:12 66cb402846eaaa31:6 b7cd35e0edc15cd7:38",
	["25aa2fd4643cf4ee"] = "1f012c3a0fa74fe2:10 48fd06b465e8b681:15 8666e5f49f440d44:12 8ec3026b5f2e579a:47",
	["26e40437ea275296"] = "2457b559aa341cc1:12 36ab9a3dcae3d562:7",
	["2e9d0bdc48b09e60"] = "503c0e01fbafa86a:15 b2d5147255a12545:6 e8ffad77b73c221c:7 f5e1e4bd4ecdc192:13",
	["35a61296619cc47e"] = "2457b559aa341cc1:12 612f7f6193fb1b2b:9 b2d5147255a12545:6 d54b9505c0f72873:37",
	["3828e2051aa9e897"] = "3d58ea61b2b485cc:12 503c0e01fbafa86a:15 f21e8dd5e27fef7c:3",
	["39ab99895147a3bf"] = "444880f62ca7eae8:40 9c22a90da9c79de3:11",
	["43a58cb89cfa197c"] = "056de1c5e21e723e:27 66cb402846eaaa31:6 7fdd884ecd57231b:6 9ba9f700137c6325:26",
	["52071f49263415e4"] = "0ec5ef9ec5cbd1b2:5",
	["5990123d142b16cb"] = "2457b559aa341cc1:12 afd03c31434b6ee5:12",
	["6cfcc7f8801a0266"] = "076692fd83a83c5c:10 2457b559aa341cc1:12",
	["7617642765ac38c7"] = "0511db0bca94fa78:7",
	["78a8185f63a70795"] = "23d09a05d1574ec9:27 43eb1c3c1a1860e0:26",
	["80932fa0ed6901d3"] = "692eb345969d368e:5",
	["88c2d09ad85a7c9f"] = "bb4b15b588f974fd:27 bf7082f0287c254f:27 cba5983612dace3f:5",
	["89c5493e08ca4207"] = "17c6e9bafcd646d2:6 6d54652ee217d712:12 6e165f05a49233f6:14",
	["96de9cd50f7306e6"] = "f5e1e4bd4ecdc192:13",
	["9f80d67a12a7e40f"] = "2457b559aa341cc1:12 db50297b40133d12:13",
	["a8cffb316f0b5c5f"] = "2457b559aa341cc1:12 9bc319730887bdd8:5 a91e4527859ada75:5",
	["b2b5e0d185605f9e"] = "2457b559aa341cc1:12",
	["cc786f6491fe7e65"] = "46427f2630a80d88:12 6a4c2138009b625e:18 b8feb0cc191c1ef3:30 be0ac4e655b2e19f:19 ffb0f21dcc7b5e15:10",
	["d54b9505c0f72873"] = "2457b559aa341cc1:12 35a61296619cc47e:41 612f7f6193fb1b2b:9 b2d5147255a12545:6",
	["e8d5f49ad7780e54"] = "2457b559aa341cc1:12 52ff81fcba0c059d:12",
	["fe3b29b2cfa63f9b"] = "adc87d576c20ad57:6 b98f2b3d1bde24b0:48 fa6f926a77b78b55:12",
	["a8a91eb54892b6b2"] = "129e5fb1b13c2018:12 20c03774d6e1d87f:13 7f19d05cd188c768:6 b9c209b4f99b5335:24",
	["e91f569c2ad8af01"] = "6b9572ac0f687394:6 ccf18b6315a3d67f:7 fc1f0a3b3fc97238:15",
	["c85f576d5e086147"] = "129e5fb1b13c2018:12 6600b5e755a21f1f:12 679e174bb3bcca68:10 a188cbb5913fbd63:10",
}
IndependentLink.weaponAccessories =
	"07d605bd50c98461:16 0aae32c4479eaa26:12 129e5fb1b13c2018:12 13a5fc03ec21e8fb:14 16ab263bc03b4f96:18 1b1fcc0e025c1f7d:14 1ec5f54265a91c83:16 2383b0439f0bc465:11 " ..
	"2457b559aa341cc1:12 26ec4f9513fc6525:12 28aee9c6a2282aa3:13 2d761a80bcbfe792:18 2e7f91374c0b471c:12 2ed76d29ce509a74:12 309f7e645a8d2b56:14 316a396d1939a997:12 " ..
	"369b53e850e72ba9:18 37c2891774b38c87:18 3ae1f60c5efd2b43:14 3ae8ba6377efe3ae:12 3d58ea61b2b485cc:12 411c4fd07740a7dd:13 44a2a59ca08ac865:12 45be635fb904f4dc:12 " ..
	"4851d75853622a47:15 4f839e923046e631:15 5c1e075cba68340b:12 6b88fea13ec2d78e:12 6b9572ac0f687394:6 6d54652ee217d712:12 6f5afa606a4400d1:17 7301b3dc5b1bffb8:17 " ..
	"74b9289347cc3503:12 756e3fdaca41cfca:12 75713afac474d9c4:12 77c5d934407114f4:17 78829731a0563576:13 7b153e02ad22d12c:12 7be1e04a7738e673:18 8039834a4b7489b9:11 " ..
	"8cd335a5f711bb59:12 9237ccec8fe86d22:14 9549ff64ba3fd5a1:14 963aca558cd906bc:17 990b06bf5c26bd01:12 9dbc8aa8702ac9e1:12 a0b52d203e689d18:13 a4fa3ef43a8cecaf:18 " ..
	"a9a19cffe985c468:12 aca180fc6f846411:12 b191a4f26558a9cc:12 b1bc2b7252ccbc03:12 b571e0428ca83b9e:12 bd4245dcfd4b832f:11 be4590c2f615c441:14 bfe35746f5084222:3 " ..
	"c70345c3342ca205:12 c891acf9e8dbda97:12 cc4cd9593abb87be:17 d8cf7fe059b4bf1e:12 e6b6467135035059:13 e8de5e95b9635c21:18 ec824b9d8b7f7b58:13 ee89d98cbe33e976:18 " ..
	"f40dea68b213b005:11 f9c6092b665018b8:12 f9df21c8e52cac95:13 f9f37dcce764fad6:13 fa6f926a77b78b55:12 ffc18b2ce10ca381:11"

function IndependentLink.action(avatar, animation)
	if not animation.active or try(S.Unit.alive, avatar) ~= true then return 0, false, 1 end
	local tuple, calls = frame_animation_state(animation, avatar)
	if not tuple or tuple.n ~= 31 then return 0, false, calls + 1 end
	local base, upper, main = tonumber(tuple[3]), tonumber(tuple[4]), tonumber(tuple[11])
	if not base or not upper or not main or base%1 ~= 0 or upper%1 ~= 0 or main%1 ~= 0
		or upper == 76 or upper == 77 then return 0, false, calls + 1 end
	local time, length, clipCalls = frame_animation_layer(animation, avatar)
	calls = calls + clipCalls + 1
	local validClip = type(time) == "number" and type(length) == "number" and time >= 0
		and length > 0 and length < math.huge and time <= length+0.025
	local empty = main == 585 or ((main == 230 or main == 650) and validClip and length > 1000)
	if (main == 230 or main == 650) and not empty then return 0, false, calls end
	local expected = IndependentLink.reloadClips[main]
	-- The captured ship catalogue also uses 640 and 681 for short transitions.
	local observedTransition = (main == 636 and validClip and math.abs(length-0.500) <= 0.005)
		or (main == 640 and validClip and math.abs(length-0.650) <= 0.005)
		or (main == 681 and validClip and math.abs(length-29/60) <= 0.005)
	if expected and not observedTransition then
		if validClip and math.abs(length-expected/60) <= 0.005 then return 2, false, calls end
		return 0, false, calls
	end
	local lowered = upper == 8 or upper == 18 or upper == 67
	local raised = (upper >= 0 and upper <= 7) or upper == 11 or upper == 12 or upper == 17
		or (upper >= 19 and upper <= 66) or upper == 70 or upper == 71
	local weaponTree = (base >= 1 and base <= 7) or base == 10 or base == 11
		or (base >= 13 and base <= 58) or (base >= 60 and base <= 71)
		or (empty and (base == 8 or base == 72 or base == 73))
	if not raised and not weaponTree then return 0, false, calls end
	if try(S.Unit.animation_has_variable, avatar, "weapon_selected") ~= true then return 0, false, calls + 1 end
	local id = try(S.Unit.animation_find_variable, avatar, "weapon_selected")
	if type(id) ~= "number" or id < 0 or id%1 ~= 0 then return 0, false, calls + 2 end
	local selected = tonumber(try(S.Unit.animation_get_variable, avatar, id))
	if not selected or not (selected > 0 and selected < math.huge) then return 0, false, calls + 3 end
	if empty and (raised or lowered) then return 1, true, calls + 3 end
	return 2, false, calls + 3
end

function IndependentLink.contacts(action, readPose)
	local V = Compat.contact_math
	local contacts = {}
	for _, side in ipairs({ "r", "l" }) do
		local anchor = readPose("attach_hand_" .. side)
		if anchor and (action == 2 or side == "r") then
			local hand = readPose(side .. "_hand")
			local grip = IndependentLink.motionGrips[side]
			local native = hand and Compat.independent_fk(hand, grip.q, grip.p)
			if action == 2 then
				anchor = native
			elseif native then
				local r = V.length(grip.p)
				local gap = V.length(V.sub(anchor.p, hand.p))
				if gap > 3*r then anchor = V.blend(anchor, native, V.smooth((gap-3*r)/r)) end
			end
		end
		contacts[side] = anchor
	end
	return contacts
end

function IndependentLink.pose_matches(a, b, metres, degrees)
	if not a or not b then return false end
	local x, y = a.q, b.q
	local dot = math.abs(x[1]*y[1]+x[2]*y[2]+x[3]*y[3]+x[4]*y[4])
	return dot >= math.cos(math.rad(degrees)*0.5)
		and Compat.contact_math.length(Compat.contact_math.sub(a.p, b.p)) <= metres
end

function IndependentLink.local_pose(unit, node)
	local x, y, z = readVector(S, try(S.Unit.local_position, unit, node))
	local ok, qx, qy, qz, qw = pcall(S.Quaternion.to_elements, try(S.Unit.local_rotation, unit, node))
	local q = ok and Compat.independent_quaternion(qx, qy, qz, qw)
	if x and q then return { p = { x, y, z }, q = q } end
end

function IndependentLink.weapon_graph(unit, spec, attachment)
	local U, V = S.Unit, Compat.contact_math
	if try(U.num_scene_graph_items, unit) ~= spec[1] or try(U.has_node, unit, "StingrayEntityRoot") ~= true then return end
	local base = try(U.node, unit, "StingrayEntityRoot")
	if base ~= 0 and base ~= 1 then return end
	local signature, owned = 0, false
	for i = 0, spec[1]-1 do
		local node = i+base
		local ok, parent = pcall(U.scene_graph_parent, unit, node)
		if not ok then return end
		local external = parent == nil or parent == -1 or (base == 1 and parent == 0)
		if not external and (type(parent) ~= "number" or parent%1 ~= 0 or parent < base or parent > node
			or (i > 0 and parent == node)) then return end
		local value = i == 0 and 0 or (external and 65535 or parent-base)
		signature = (signature*257+value+1)%2147483647
		if external or i == 0 then
			local worldPose = Compat.independent_world_pose(S, unit, node)
			local localPose = IndependentLink.local_pose(unit, node)
			if worldPose and localPose then
				local inv = V.inverse(localPose)
				local projected = Compat.independent_fk(worldPose, inv.q, inv.p)
				owned = owned or IndependentLink.pose_matches(projected, attachment, 0.002, 0.15)
			end
		end
	end
	if owned and signature == spec[3] then return spec[2]+base end
end

-- F3：找武器。改前：持枪时**每帧**遍历全世界 unit，对每个调 `Unit.alive` + `Unit.debug_name`（2×N 次引擎调用）。
-- 改后（对应 IAS 只在身份/武器变化时才重新枚举的做法）：
--   ① 判活不再调 `Unit.alive`：unit 在本帧 `World.units` 的索引里就是活的（`runtime.liveIndex`）；
--   ② `debug_name` 结果按 unit 缓存（"不是武器"的缓存 180 帧过期，防指针复用）；
--   ③ 找到过武器就先只验证缓存的那一件（索引里还在 + 武器图仍与右手挂点吻合），完整扫描 20 帧一次；
--      没找到时 10 帧一次。
-- `runtime` 缺省（老调用/测试）时退回原来的全量写法，行为不变。
function IndependentLink.find_weapon(units, attachment, graphs, runtime)
	local function inspect(unit, tag)
		local spec = tag and graphs[tag:lower()]
		local root = spec and IndependentLink.weapon_graph(unit, spec, attachment)
		if root then
			return { unit = unit, node = root, sidearm = spec[4], envelope = spec[5], spec = spec, id = tag:lower() }
		end
	end
	if runtime == nil then
		local found
		for _, unit in ipairs(units or {}) do
			if Compat.unit_alive(S, unit) then
				local tag = unit_tag(S, unit):match("^#ID%[([%da-fA-F]+)%]$")
				local hit = inspect(unit, tag)
				if hit then
					if found then return end
					found = hit
				end
			end
		end
		return found
	end
	local frame = runtime.frame or 0
	local cache = runtime.weaponCache
	if cache == nil then cache = { tags = {}, tagsBuilt = frame }; runtime.weaponCache = cache end
	local live, stamp = runtime.liveIndex, runtime.liveStamp
	if runtime.liveIndexUnits ~= units then live = nil end
	if cache.unit ~= nil then
		if live ~= nil and live[cache.unit] == stamp then
			local hit = inspect(cache.unit, cache.tag)
			if hit and frame < (cache.nextScan or 0) then return hit end
			if not hit then cache.unit, cache.nextScan = nil, 0 end
		else
			cache.unit, cache.nextScan = nil, 0
		end
	end
	if cache.unit == nil and frame < (cache.nextScan or 0) then return nil end
	if frame - cache.tagsBuilt >= 180 then cache.tags, cache.tagsBuilt = {}, frame end
	local found, tags = nil, cache.tags
	for index = 1, #(units or {}) do
		local unit = units[index]
		local tag = tags[unit]
		if tag == nil then
			if live ~= nil then
				tag = unit_tag(S, unit):match("^#ID%[([%da-fA-F]+)%]$") or false
			elseif Compat.unit_alive(S, unit) then
				tag = unit_tag(S, unit):match("^#ID%[([%da-fA-F]+)%]$") or false
			else
				tag = false
			end
			tags[unit] = tag
		end
		if tag then
			local hit = inspect(unit, tag)
			if hit then
				if found then cache.unit, cache.nextScan = nil, frame + 10; return end
				found = hit
				found.cacheTag = tag
			end
		end
	end
	if found then
		cache.unit, cache.tag, cache.nextScan = found.unit, found.cacheTag, frame + 20
	else
		cache.unit, cache.nextScan = nil, frame + 10
	end
	return found
end

function IndependentLink.support_weight(weapon, left, palm, right)
	local V = Compat.contact_math
	if not weapon.left or not left then return false, 0 end
	local control = weapon.left.p
	local gap = V.length(V.sub(left.p, control))
	local near = gap < 0.03
	if weapon.sidearm then return near, near and 1 or 0 end
	local weight = V.smooth((0.06-gap)/0.04)
	local function envelope_distance(p)
		if not weapon.envelope or not right then return math.huge end
		local relative = Compat.independent_relative(right, { p = p, q = right.q })
		local box = weapon.envelope
		local x = math.max(box[1]-relative[1], relative[1]-box[4], 0)
		local y = math.max(box[2]-relative[2], relative[2]-box[5], 0)
		local z = math.max(box[3]-relative[3], relative[3]-box[6], 0)
		return math.sqrt(x*x+y*y+z*z)
	end
	local envelope = envelope_distance(left.p)
	weight = math.max(weight, V.smooth((0.06-envelope)/0.04))
	near = near or envelope < 0.03
	if palm then
		local reach = V.length(V.sub(palm[2].p, palm[1].p))
		if reach >= 0.02 and reach <= 0.2 then
			local distance = math.min(V.length(V.sub(palm[1].p, control)), envelope_distance(palm[1].p))
			near = near or distance <= reach
			weight = math.max(weight, V.smooth((reach+0.04-distance)/0.04))
		end
	end
	return near, weight
end

function IndependentLink.held_weapon(runtime, state, readPose)
	local root = readPose("root")
	local camera = runtime.poseCamera
	local right = state.contacts.r and readPose("attach_hand_r")
	if not right or not root or not camera or not camera.z then return end
	local V = Compat.contact_math
	if V.length(V.sub(root.p, { camera.x, camera.y, camera.z })) <= 1 then return end
	local weapon = IndependentLink.find_weapon(runtime.liveUnits, right, IndependentLink.weaponGraphs, runtime)
	if not weapon then return end
	local lnode = safe_node(S, weapon.unit, "IK_left")
	local rnode = safe_node(S, weapon.unit, "IK_right")
	weapon.left = lnode and Compat.independent_world_pose(S, weapon.unit, lnode)
	if not weapon.sidearm and not (weapon.left and state.contacts.l) then return end
	local hand, finger = readPose("l_hand"), readPose("l_middle_finger1")
	local palm = hand and finger and { hand, finger }
	local rightControl = rnode and Compat.independent_world_pose(S, weapon.unit, rnode)
	weapon.support, weapon.weight = IndependentLink.support_weight(weapon, state.contacts.l, palm, rightControl)
	return weapon
end

function IndependentLink.weapon_snapshot(unit, count)
	local U, V = S.Unit, Compat.contact_math
	if try(U.num_scene_graph_items, unit) ~= count or try(U.has_node, unit, "StingrayEntityRoot") ~= true then return end
	local base = try(U.node, unit, "StingrayEntityRoot")
	if base ~= 0 and base ~= 1 then return end
	local nodes = {}
	for node = base, base+count-1 do
		local ok, parent = pcall(U.scene_graph_parent, unit, node)
		if not ok then return end
		local external = parent == nil or parent == -1 or node == base or (base == 1 and parent == 0)
		if not external and not nodes[parent] then return end
		local localPose = IndependentLink.local_pose(unit, node)
		local worldPose = V.world_pose(S, unit, node)
		if not localPose or not worldPose then return end
		local row = { localPose = localPose, worldPose = worldPose }
		if external then
			local inv = V.inverse(localPose)
			row.parentFrame = Compat.independent_fk(worldPose, inv.q, inv.p)
		else
			row.parent = parent
		end
		nodes[node] = row
	end
	return { unit = unit, count = count, base = base, nodes = nodes, tag = unit_tag(S, unit) }
end

function IndependentLink.mark_weapon_descendants(graph, roots)
	graph.affected = roots
	for node = graph.base, graph.base+graph.count-1 do
		local parent = graph.nodes[node].parent
		if parent and roots[parent] then roots[node] = true end
	end
end

function IndependentLink.weapon_followers(units, weapon)
	local members = IndependentLink.weaponMembers[weapon.id]
	local wanted = {}
	for id, count in ((members or "") .. " " .. IndependentLink.weaponAccessories):gmatch("(%x+):(%d+)") do
		count = tonumber(count)
		if wanted[id] and wanted[id] ~= count then return end
		wanted[id] = count
	end
	local root = IndependentLink.weapon_snapshot(weapon.unit, weapon.spec[1])
	if not root or not root.nodes[weapon.node] then return end
	IndependentLink.mark_weapon_descendants(root, { [weapon.node] = true })
	local pending, ordered, groups = {}, { root }, {}
	for _, unit in ipairs(units) do
		if unit ~= weapon.unit and Compat.unit_alive(S, unit) then
			local id = unit_tag(S, unit):match("^#ID%[(%x+)%]$")
			id = id and id:lower()
			if id and wanted[id] then
				groups[id] = (groups[id] or 0)+1
				if groups[id] > 16 or #pending >= 48 then return end
				local graph = IndependentLink.weapon_snapshot(unit, wanted[id])
				if not graph then return end
				pending[#pending+1] = graph
			end
		end
	end
	for _ = 1, 8 do
		local nextPending = {}
		for _, child in ipairs(pending) do
			local roots, owner, ambiguous = {}, nil, false
			for node, row in pairs(child.nodes) do
				if row.parentFrame then
					for _, candidate in ipairs(ordered) do
						for bone in pairs(candidate.affected) do
							if IndependentLink.pose_matches(row.parentFrame, candidate.nodes[bone].worldPose, 0.0002, 0.05) then
								ambiguous = ambiguous or (owner ~= nil and owner ~= candidate)
								owner, roots[node] = candidate, true
							end
						end
					end
				end
			end
			if owner and not ambiguous then
				IndependentLink.mark_weapon_descendants(child, roots)
				ordered[#ordered+1] = child
			else
				nextPending[#nextPending+1] = child
			end
		end
		if #nextPending == #pending then break end
		pending = nextPending
	end
	if not members or #ordered > 1 then return ordered end
end

function IndependentLink.verify_weapon_shift(world, graphs, delta)
	local V = Compat.contact_math
	for index, graph in ipairs(graphs or {}) do
		if index > 1 then
			assert(Compat.unit_alive(S, graph.unit) and try(S.Unit.num_scene_graph_items, graph.unit) == graph.count)
			assert(S.World.update_unit(world, graph.unit) ~= false)
		end
		for node, row in pairs(graph.nodes) do
			local p = graph.affected[node] and V.add(row.worldPose.p, delta) or row.worldPose.p
			assert(IndependentLink.pose_matches(V.world_pose(S, graph.unit, node), { p = p, q = row.worldPose.q }, 0.001, 0.05))
			if index > 1 then
				assert(IndependentLink.pose_matches(IndependentLink.local_pose(graph.unit, node), row.localPose, 0.00001, 0.001))
			end
		end
	end
end

function IndependentLink.restore_weapon(runtime)
	local shift = runtime.weaponShift
	if not shift then return true end
	local worlds = try(S.Application.worlds)
	if type(worlds) ~= "table" then return false end
	local worldLive = false
	for _, world in ipairs(worlds) do if world == shift.world then worldLive = true; break end end
	if not worldLive then runtime.weaponShift = nil; return true end
	local units = try(S.World.units, shift.world)
	if type(units) ~= "table" then return false end
	local live = {}
	for _, unit in ipairs(units) do live[unit] = true end
	local unit = shift.unit
	if live[unit] and Compat.unit_alive(S, unit) and unit_tag(S, unit) == shift.tag
		and try(S.Unit.num_scene_graph_items, unit) == shift.count then
		local pose = IndependentLink.local_pose(unit, shift.node)
		if not pose then return false end
		if IndependentLink.pose_matches(pose, shift.target, 0.00001, 0.001) then
			local ok = Compat.write_independent_position(S, unit, shift.node, shift.original.p, 1)
			if not ok then return false end
			shift.refresh = true
		end
		if shift.refresh then
			local ok, result = pcall(S.World.update_unit, shift.world, unit)
			if not ok or result == false then return false end
			shift.refresh = false
		end
	end
	for _, child in ipairs(shift.followers) do
		if live[child.unit] and Compat.unit_alive(S, child.unit) and unit_tag(S, child.unit) == child.tag
			and try(S.Unit.num_scene_graph_items, child.unit) == child.count then
			local ok, result = pcall(S.World.update_unit, shift.world, child.unit)
			if not ok or result == false then return false end
		end
	end
	runtime.weaponShift = nil
	return true
end

function IndependentLink.enable_weapon_trial(runtime)
	if runtime.weaponUpdate then return true end
	local update = rawget(_G, "update")
	if type(update) ~= "function" then return false end
	runtime.originalWeaponUpdate = update
	runtime.weaponUpdate = function(...)
		if not IndependentLink.restore_weapon(runtime) and not runtime.weaponRestoreLogged then
			runtime.weaponRestoreLogged = true
			runtime.logger("[physbone][握枪] 武器根还原失败")
		end
		return update(...)
	end
	rawset(_G, "update", runtime.weaponUpdate)
	return true
end

function IndependentLink.shift_weapon(runtime, world, weapon, delta, graphs)
	local V = Compat.contact_math
	if runtime.weaponShift or runtime.weaponTrialDisabled or not IndependentLink.enable_weapon_trial(runtime) then return false end
	local localPose = IndependentLink.local_pose(weapon.unit, weapon.node)
	local worldPose = Compat.independent_world_pose(S, weapon.unit, weapon.node)
	if not localPose or not worldPose then return false end
	local inv = V.inverse(localPose)
	local parent = Compat.independent_fk(worldPose, inv.q, inv.p)
	local shifted = V.add(localPose.p, V.rotate(V.inverse(parent).q, delta))
	local followers = {}
	for i = 2, #(graphs or {}) do
		local child = graphs[i]
		followers[#followers+1] = { unit = child.unit, count = child.count, tag = child.tag }
	end
	local saved = { world = world, unit = weapon.unit, node = weapon.node, count = weapon.spec[1],
		tag = unit_tag(S, weapon.unit), original = localPose, target = { p = shifted, q = localPose.q }, followers = followers }
	runtime.weaponShift = saved
	local ok = pcall(function()
		assert(Compat.write_independent_position(S, weapon.unit, weapon.node, shifted, 1))
		assert(S.World.update_unit(world, weapon.unit) ~= false)
		local desired = { q = worldPose.q, p = V.add(worldPose.p, delta) }
		assert(IndependentLink.pose_matches(Compat.independent_world_pose(S, weapon.unit, weapon.node), desired, 0.001, 0.05))
		IndependentLink.verify_weapon_shift(world, graphs, delta)
	end)
	if not ok then
		local current = IndependentLink.local_pose(weapon.unit, weapon.node)
		if current then saved.target = current end
		IndependentLink.restore_weapon(runtime)
		runtime.weaponTrialDisabled = true
	end
	return ok
end

--- 缩放判定（E12c）。
--- 引擎会主动把某些骨（头盔/头，第一人称、死亡、空投期间）缩成 0 来"隐藏"它；那不是姿势异常。
--- 旧写法一律要求缩放 == 1：挂接断言失败 → 件被隔离并整件隐藏 ⇒ 实机截图里"角色整个透明，只剩武器挂件"。
--- 现在：全部分量 ≈ 1 → 正常；全部分量 ≈ 0（< 0.01）→ 引擎有意隐藏，放行（不隔离、不隐藏整件）；
--- 其余（非均匀 / 拉伸）才算异常。
function IndependentLink.scale_state(x, y, z)
	if x == nil then return "missing" end
	if math.abs(x-1) <= 1e-6 and math.abs(y-1) <= 1e-6 and math.abs(z-1) <= 1e-6 then return "one" end
	if math.abs(x) < 0.01 and math.abs(y) < 0.01 and math.abs(z) < 0.01 then return "hidden" end
	return "fault"
end

function IndependentLink.restore(state, piece, layoutChanged)
	local link = piece.link
	if not link then return true end
	if not link.changed then
		piece.link = nil
		return true
	end
	local ok = pcall(function()
		local unit = piece.unit
		assert(S.World.unlink_unit(state.world, unit) ~= false, "unlink_unit")
		if layoutChanged then return end
		for _, b in ipairs(link.order) do
			local row, node = link.rows[b], piece.nodes[b]
			if row.attempted then
				if S.Unit.scene_graph_parent(unit, node) ~= row.parent then
					assert(S.Unit.scene_graph_link(unit, node, row.parent) ~= false, "scene_graph_link")
					assert(S.Unit.scene_graph_parent(unit, node) == row.parent, "restore parent")
				end
				assert(Compat.write_independent_position(S, unit, node, row.originalP, 1), "restore position")
				assert(Compat.write_independent_rotation(S, unit, node, row.originalQ, true), "restore rotation")
			end
		end
		assert(S.World.update_unit(state.world, unit) ~= false, "restore update")
	end)
	if ok then piece.link = nil end
	return ok
end

function IndependentLink.fail(runtime, state, piece, reason)
	local restored = IndependentLink.restore(state, piece)
	if not piece.linkFailed then
		runtime.logger("[physbone][独立骨][警告][挂接回退] %s %s%s", piece.tag, tostring(reason),
			restored and "" or "，待恢复原骨链")
	end
	piece.linkFailed = true
	-- 瞬时原因（缩放异常 / 件或 avatar 刚消失 / 原父节点还在）不应让件永远停在 E6 回退路径：
	-- 60 帧后清掉标记重试挂接（重试期间件仍可见、仍按旋转复制跟随）。
	local text = tostring(reason)
	if text:find("缩放", 1, true) or text:find("消失", 1, true) or text:find("原父节点", 1, true)
		or text:find("缺少节点", 1, true) then
		piece.linkRetryAt = (runtime.frame or 0) + 60
	end
	return restored
end

function IndependentLink.attach(runtime, state, piece)
	local U, W = S.Unit, S.World
	local unit, avatar = piece.unit, state.avatar
	local link = { rows = {}, order = piece.bones, byNode = {} }
	local ok, reason = pcall(function()
		assert(type(W.link_unit) == "function" and type(W.unlink_unit) == "function"
			and type(U.scene_graph_parent) == "function" and type(U.scene_graph_link) == "function", "挂接 API")
		assert(session_unit_live(runtime, unit) and session_unit_live(runtime, avatar)
			and U.alive(unit) == true and U.alive(avatar) == true, "件或 avatar 已消失")
		for _, b in ipairs(piece.bones) do
			local node, source = piece.nodes[b], state.sources[b]
			assert(node and source and state.rest[b], "缺少节点 " .. state.list[b][1])
			local x, y, z = readVector(S, U.local_scale(unit, node))
			local scaleState = IndependentLink.scale_state(x, y, z)
			assert(scaleState == "one" or scaleState == "hidden", "缩放 " .. state.list[b][1])
			local px, py, pz = readVector(S, U.local_position(unit, node))
			local q = Compat.independent_quaternion(S.Quaternion.to_elements(U.local_rotation(unit, node)))
			local parent = U.scene_graph_parent(unit, node)
			assert(px and q and type(parent) == "number" and parent >= 0 and parent ~= node,
				"原骨链 " .. state.list[b][1])
			link.rows[b] = { parent = parent, originalP = { px, py, pz }, originalQ = q }
			link.byNode[node] = b
		end
		piece.link = link
		for _, b in ipairs(piece.bones) do
			local row, node = link.rows[b], piece.nodes[b]
			local called, result = pcall(W.link_unit, state.world, unit, node, avatar, state.sources[b])
			row.attempted = called and result ~= false
			link.changed = link.changed or row.attempted
			local parent = U.scene_graph_parent(unit, node)
			row.attempted = row.attempted or parent ~= row.parent
			link.changed = link.changed or row.attempted
			assert(called and result ~= false, "link_unit " .. state.list[b][1])
			assert(parent ~= row.parent and (parent == nil or parent == -1 or parent == 0),
				"原父节点仍在 " .. state.list[b][1])
			row.q = Compat.independent_quaternion(S.Quaternion.to_elements(U.local_rotation(unit, node)))
			assert(row.q, "挂接局部旋转")
		end
	end)
	if not ok then
		IndependentLink.fail(runtime, state, piece, reason)
		return false
	end
	return true
end

function IndependentLink.same(a, b, rotation)
	if not a or not b then return false end
	local sign = rotation and (a[1]*b[1] + a[2]*b[2] + a[3]*b[3] + a[4]*b[4]) < 0 and -1 or 1
	return math.abs(a[1]-sign*b[1]) <= 1e-6 and math.abs(a[2]-sign*b[2]) <= 1e-6
		and math.abs(a[3]-sign*b[3]) <= 1e-6 and (not rotation or math.abs(a[4]-sign*b[4]) <= 1e-6)
end

function IndependentLink.write(state, piece, values, objects)
	local written, calls = 0, 0
	for b, value in pairs(values) do
		local row, node = piece.link.rows[b], piece.nodes[b]
		local p, q = value.p, value.q
		if not IndependentLink.same(p, row.p) then
			local cached = objects[b] or {}
			if not IndependentLink.same(p, cached.p) then
				cached.p, cached.vector = p, S.Vector3(p[1], p[2], p[3])
				calls = calls + 1
				objects[b] = cached
			end
			assert(cached.vector, "offset vector")
			assert(S.Unit.set_local_position(piece.unit, node, cached.vector) ~= false, "offset position")
			row.p, written, calls = p, written + 1, calls + 1
		end
		if not IndependentLink.same(q, row.q, true) then
			local cached = objects[b] or {}
			if not IndependentLink.same(q, cached.q, true) then
				local cost
				cached.rotation, cost = Compat.independent_rotation_object(S, q, true)
				calls = calls + cost
				cached.q, objects[b] = q, cached
			end
			assert(cached.rotation and S.Unit.set_local_rotation(piece.unit, node, cached.rotation) ~= false, "offset rotation")
			row.q, written, calls = q, written + 1, calls + 1
		end
	end
	if written > 0 then
		assert(S.World.update_unit(state.world, piece.unit) ~= false, "offset update")
		calls = calls + 1
	end
	return written, calls
end

function IndependentLink.is_gait(name)
	local suffix = name:match("^[lr]_(.+)$")
	return name == "hips" or suffix == "thigh" or suffix == "knee" or suffix == "foot" or suffix == "ball" or suffix == "toe"
end

function IndependentLink.first_person(runtime, state)
	local cache = runtime.poseCamera
	if not cache or cache.frame ~= runtime.frame then
		local x, y, z = cameraPosition(S, state.world, runtime.cache)
		cache = { frame = runtime.frame, x = x, y = y, z = z }
		runtime.poseCamera = cache
	end
	if cache.x == nil then return false end
	local x, y
	for _, owner in ipairs(runtime.avatars or {}) do
		if owner.unit == state.avatar and owner.positionFrame == runtime.frame then
			x, y = owner.x, owner.y
			break
		end
	end
	if x == nil then x, y = readVector(S, try(S.Unit.world_position, state.avatar, 1)) end
	if x == nil then return false end
	local dx, dy = x-cache.x, y-cache.y
	local threshold = sched_settings(runtime).fpLockMetres
	return dx*dx + dy*dy < threshold*threshold
end

function IndependentLink.first_person_action(state, readPose)
	local graphs = try(S.World.units_by_resource, state.world, "content/fac_helldivers/fp_cha_avatar/fp_cha_avatar")
	if type(graphs) ~= "table" or #graphs == 0 then return 0 end
	for _, piece in ipairs(state.pieces) do
		if Compat.unit_alive(S, piece.unit) then
			local native = {}
			for _, side in ipairs({ "r_", "l_" }) do
				local arm = {}
				for _, joint in ipairs({ "shoulder", "elbow", "hand", "shoulder_twist", "hand_twist" }) do
					local name = side .. joint
					local node = safe_node(S, piece.unit, name)
					arm[name] = node and Compat.independent_world_pose(S, piece.unit, node)
				end
				if arm[side.."shoulder"] and arm[side.."elbow"] and arm[side.."hand"] then
					for name, pose in pairs(arm) do native[name] = pose end
				end
			end
			if next(native) then
				local function matches(read)
					for name, pose in pairs(native) do
						if not IndependentLink.pose_matches(pose, read(name), 0.002, 0.5) then return false end
					end
					return true
				end
				if not matches(readPose) then
					local count = 0
					for _, unit in ipairs(graphs) do
						if Compat.unit_alive(S, unit) and matches(function(name)
							local node = safe_node(S, unit, name)
							return node and Compat.independent_world_pose(S, unit, node)
						end) then count = count + 1 end
					end
					if count == 1 then return 3 end
				end
			end
		end
	end
	return 0
end

function IndependentLink.move_subtree(piece, fk, bone, pose)
	local prior = fk[bone]
	local moved = { [bone] = true }
	for _, b in ipairs(piece.bones) do
		if moved[piece.link.byNode[piece.link.rows[b].parent]] then
			local p, q = Compat.independent_relative(prior, fk[b])
			fk[b] = Compat.independent_fk(pose, q, p)
			moved[b] = true
		end
	end
	fk[bone] = pose
end

function IndependentLink.rotate_about(pose, pivot, delta)
	local V = Compat.contact_math
	local p = V.add(pivot, V.rotate(delta, V.sub(pose.p, pivot)))
	return Compat.independent_fk({ p = p, q = delta }, pose.q, { 0, 0, 0 })
end

function IndependentLink.calibrate_thumb(state, piece, fk, side, readPose)
	local V = Compat.contact_math
	local function bone(name) return state.boneBySource[side .. "_" .. name] end
	local hand, index, pinky = bone("hand"), bone("index_finger1"), bone("pinky_finger1")
	local thumb = { bone("thumb_finger1"), bone("thumb_finger2"), bone("thumb_finger3") }
	if not (fk[hand] and fk[index] and fk[pinky] and fk[thumb[1]] and fk[thumb[2]] and fk[thumb[3]]) then return end
	local function source(b) return readPose(state.list[b][2]) end
	local srcHand, srcIndex, srcPinky = source(hand), source(index), source(pinky)
	local srcThumb = { source(thumb[1]), source(thumb[2]), source(thumb[3]) }
	if not (srcHand and srcIndex and srcPinky and srcThumb[1] and srcThumb[2] and srcThumb[3]) then return end
	local modelPalm = Compat.contact_palm(fk[hand], fk[index], fk[pinky])
	local sourcePalm = Compat.contact_palm(srcHand, srcIndex, srcPinky)
	if not modelPalm or not sourcePalm then return end
	local palmInverse = { -sourcePalm[1], -sourcePalm[2], -sourcePalm[3], sourcePalm[4] }
	local normal = V.rotate(modelPalm, { 0, 0, 1 })
	for i = 1, 3 do
		local b, nextBone = thumb[i], thumb[i+1]
		local from, to
		if nextBone then
			from = V.sub(fk[nextBone].p, fk[b].p)
			to = V.sub(srcThumb[i+1].p, srcThumb[i].p)
		else
			local row = piece.link.rows[b]
			local q = row.originalQ
			local axis = V.rotate({ -q[1], -q[2], -q[3], q[4] }, piece.bind[b] or row.originalP)
			from = V.rotate(fk[b].q, axis)
			to = V.rotate(srcThumb[i].q, { 1, 0, 0 })
		end
		local delta = V.arc(from, V.rotate(modelPalm, V.rotate(palmInverse, to)), normal)
		IndependentLink.move_subtree(piece, fk, b,
			Compat.independent_fk({ p = fk[b].p, q = delta }, fk[b].q, { 0, 0, 0 }))
	end
end

function IndependentLink.solve_contacts(runtime, state, piece, fk, readPose)
	local grips = runtime.options.independentGrips and runtime.options.independentGrips[piece.tag]
	if not grips then return end
	local V = Compat.contact_math
	for _, side in ipairs({ "r", "l" }) do
		local grip = grips[side] and V.matrix_pose(grips[side])
		local sh = state.boneBySource[side .. "_shoulder"]
		local el = state.boneBySource[side .. "_elbow"]
		local ha = state.boneBySource[side .. "_hand"]
		if grip and fk[sh] and fk[el] and fk[ha] then
			local source = { readPose(side .. "_shoulder"), readPose(side .. "_elbow"), readPose(side .. "_hand") }
			if source[1] and source[2] and source[3] then
				local contact = state.contacts[side]
				local inverse = V.inverse(grip)
				local wrist = contact and Compat.independent_fk(contact, inverse.q, inverse.p)
					or { p = source[3].p, q = fk[ha].q }
				local lengths = { V.length(piece.bind[el] or piece.link.rows[el].originalP),
					V.length(piece.bind[ha] or piece.link.rows[ha].originalP) }
				local single = state.singleHand and side == "r"
				local factor = 1
				local chest = fk[state.boneBySource.chest]
				local clavicle = state.boneBySource[side .. "_clavicle"]
				if contact and chest and fk[clavicle] and (single or (side == "l" and not state.singleHand)) then
					local delta
					delta, factor = Compat.contact_girdle(chest, fk[sh], wrist, lengths, source, single)
					if delta then
						IndependentLink.move_subtree(piece, fk, clavicle, IndependentLink.rotate_about(fk[clavicle], chest.p, delta))
					end
				end
				local freeHand = side == "l" and state.action == 1 and state.contacts.r and not contact
				local shoulder, elbow, hand = Compat.contact_arm({ fk[sh], fk[el], fk[ha] }, source, wrist, lengths, factor, freeHand)
				if shoulder then
					IndependentLink.move_subtree(piece, fk, sh, shoulder)
					IndependentLink.move_subtree(piece, fk, el, elbow)
					IndependentLink.move_subtree(piece, fk, ha, hand)
					for _, suffix in ipairs({ "_shoulder_twist", "_hand_twist" }) do
						local b = state.boneBySource[side .. suffix]
						if fk[b] then
							local row = piece.link.rows[b]
							local parent = fk[piece.link.byNode[row.parent]]
							local length = V.length(piece.bind[b] or row.originalP) * factor
							local axis = parent and V.normal(V.sub(fk[b].p, parent.p))
							if axis then
								IndependentLink.move_subtree(piece, fk, b, { q = fk[b].q, p = V.add(parent.p, V.scale(axis, length)) })
							end
						end
					end
					if contact then IndependentLink.calibrate_thumb(state, piece, fk, side, readPose) end
				end
			end
		end
	end
end

function IndependentLink.targets(runtime, state, piece, sample, vectors)
	local link = piece.link
	local fk, roots = {}, {}
	local function target(b)
		if fk[b] then return fk[b] end
		local row = link.rows[b]
		local source = assert(sample(b), "源骨姿势 " .. state.list[b][2])
		local parent = link.byNode[row.parent]
		if parent then
			parent = target(parent)
		else
			if not roots[row.parent] then
				local pose, cost = Compat.independent_world_pose(S, piece.unit, row.parent)
				vectors.calls = vectors.calls + cost
				roots[row.parent] = assert(pose, "模型父节点")
			end
			parent = roots[row.parent]
		end
		local p = piece.bind[b] or state.rest[b][4] or row.originalP
		local translation = not state.remote and piece.translations[b]
		if translation then
			local a, m, v = translation[1], translation[2], assert(sample(b, true).p, "源骨平移")
			p = { m[1]+v[1]-a[1], m[2]+v[2]-a[2], m[3]+v[3]-a[3] }
		end
		fk[b] = Compat.independent_fk(parent, source.q, p)
		return fk[b]
	end
	local legs = runtime.options.independentLegs and runtime.options.independentLegs[piece.tag]
	if legs then
		local hips = state.boneBySource.hips
		for side, data in pairs(legs) do
			local thigh = state.boneBySource[side .. "_thigh"]
			local knee = state.boneBySource[side .. "_knee"]
			local foot = state.boneBySource[side .. "_foot"]
			if link.rows[hips] and link.rows[thigh] and link.rows[knee] and link.rows[foot] then
				local a, b, c = Compat.calibrate_leg(target(hips), target(thigh), target(knee), target(foot),
					sample(hips).world, sample(thigh).world, sample(foot).world, data[1], data[2])
				if a then fk[thigh], fk[knee], fk[foot] = a, b, c end
			end
		end
	end
	for _, b in ipairs(piece.bones) do target(b) end
	return fk
end

function IndependentLink.weapon_trial(runtime, state, weapon, sample, vectors, readPose)
	if runtime.weaponShift or not state.contacts.r then return end
	local V = Compat.contact_math
	local sh, el, ha, chest = state.boneBySource.r_shoulder, state.boneBySource.r_elbow,
		state.boneBySource.r_hand, state.boneBySource.chest
	local sourceSh, sourceEl, sourceHa = readPose("r_shoulder"), readPose("r_elbow"), readPose("r_hand")
	if not sourceSh or not sourceEl or not sourceHa then return end
	local upper = V.normal(V.sub(sourceEl.p, sourceSh.p))
	local lower = V.normal(V.sub(sourceHa.p, sourceEl.p))
	if not upper or not lower then return end
	local shared, chestAt
	for _, piece in ipairs(state.pieces) do
		local grips = runtime.options.independentGrips[piece.tag]
		local grip = grips and grips.r and V.matrix_pose(grips.r)
		if grip then
			local link = piece.link
			if not link or not link.rows[sh] or not link.rows[el] or not link.rows[ha] or not link.rows[chest] then return end
			local ok, fk = pcall(IndependentLink.targets, runtime, state, piece, sample, vectors)
			if not ok then return end
			if chestAt and V.length(V.sub(chestAt, fk[chest].p)) > 0.0002 then return end
			chestAt = chestAt or fk[chest].p
			local inv = V.inverse(grip)
			local contact = state.contacts.r
			local wrist = Compat.independent_fk(contact, inv.q, inv.p)
			local ray = V.sub(wrist.p, fk[sh].p)
			local distance = V.length(ray)
			local a = V.length(piece.bind[el] or link.rows[el].originalP)
			local b = V.length(piece.bind[ha] or link.rows[ha].originalP)
			local chord = math.sqrt(math.max(0, a*a+b*b+2*a*b*V.dot(upper, lower)))
			if distance < 1e-6 or distance-chord > 0.75*(a+b) then return end
			local delta = V.scale(ray, math.max(-0.18, math.min(0, 0.99999*(a+b)-distance))/distance)
			if shared and V.length(V.sub(shared, delta)) > 0.0003 then return end
			shared = shared or delta
		end
	end
	if not shared or runtime.weaponTrialDisabled then return end
	local graphs = runtime.liveUnits and IndependentLink.weapon_followers(runtime.liveUnits, weapon)
	if not graphs then return end
	if V.length(shared) > 1e-6 and not IndependentLink.shift_weapon(runtime, state.world, weapon, shared, graphs) then return end
	local right, left = state.contacts.r, state.contacts.l
	state.contacts.r = { q = right.q, p = V.add(right.p, shared) }
	if left and weapon.left and (weapon.weight > 0 or not weapon.sidearm) then
		state.contacts.l = { q = left.q, p = V.add(left.p, V.scale(shared, weapon.weight)) }
	else
		state.contacts.l = nil
	end
	state.singleHand = false
end

function IndependentLink.present(runtime, state, piece, due, now, blend, sample, vectors, readPose)
	local link = piece.link
	if link.pending and (due or not blend or now + (runtime.dt or 0)*0.5 + 1e-6 >= link.finishAt) then
		local n, cost = IndependentLink.write(state, piece, link.pending, vectors.finish)
		link.pending = nil
		if not due then return n, cost end
		vectors.written, vectors.calls = vectors.written + n, vectors.calls + cost
	end
	if not due then return 0, 0 end

	local fk = IndependentLink.targets(runtime, state, piece, sample, vectors)
	local display, pending = {}, {}
	if state.action == 1 or state.action == 2 then
		IndependentLink.solve_contacts(runtime, state, piece, fk, readPose)
	end
	for _, b in ipairs(piece.bones) do
		local row = link.rows[b]
		local desired = fk[b]
		local p, q = Compat.independent_relative(sample(b).world, desired)
		local value, previous = { p = p, q = q }, row.endpoint
		if blend and previous and IndependentLink.is_gait(state.list[b][2]) and
			(not IndependentLink.same(p, previous.p) or not IndependentLink.same(q, previous.q, true)) then
			local oldP, oldQ = previous.p, Compat.independent_hemisphere(previous.q, q)
			display[b] = { p = { (p[1]+oldP[1])*0.5, (p[2]+oldP[2])*0.5, (p[3]+oldP[3])*0.5 },
				q = Compat.independent_quaternion(q[1]+oldQ[1], q[2]+oldQ[2], q[3]+oldQ[3], q[4]+oldQ[4]) }
			pending[b] = value
		else
			display[b] = value
		end
		row.endpoint = value
	end
	link.pending = next(pending) and pending or nil
	link.finishAt = (now + state.nextCorrection)*0.5
	local written, calls = IndependentLink.write(state, piece, display, vectors.display)
	local count, cost = IndependentLink.present_clothing(runtime, state, piece, fk)
	return written + count, calls + cost
end

function IndependentLink.physics(runtime, session, schedule, operation, dt)
	local presentation = session.independentPresentation
	local written, presentationCalls = 0, 0
	if presentation then
		local fraction
		if not schedule or schedule.due or not schedule.blend or schedule.paused then
			fraction = 1
		else
			local elapsed = (schedule.now - presentation.start) / presentation.period
			local step = math.min(4, 1 + math.floor(4*elapsed + 1e-8))
			if step > presentation.step then
				fraction = math.min(1, presentation.lead + elapsed)
				if step == 4 or (fraction >= 0.75 and schedule.now + runtime.dt >= schedule.nextAt) then
					fraction = 1
				end
				presentation.step = step
			end
		end
		if fraction then
			written = Compat.present_independent_physics(session, fraction)
			presentationCalls = session.engineCalls or 0
		end
	end
	if not operation or session.independentPresentation then
		if not schedule or schedule.paused then session.independentHistory = nil end
		return written, false
	end
	local sample
	if schedule and schedule.blend and operation == Compat.update and session.solver ~= "angle" then
		local period = schedule.nextAt - schedule.now
		sample = { start = schedule.now, period = period, step = 1, rows = {},
			lead = math.max(0.25, math.min(0.5, runtime.dt / period)), history = session.independentHistory }
	end
	session.independentSample = sample
	session.engineCalls = 0
	local ok, out = pcall(operation, session, dt)
	session.independentSample = nil
	session.engineCalls = presentationCalls + (session.engineCalls or 0)
	session.independentHistory = ok and sample ~= nil or nil
	if sample and next(sample.rows) then session.independentPresentation = sample end
	if not ok then error(out, 0) end
	return written + (type(out) == "number" and out or 0), true
end

function IndependentLink.present_clothing(runtime, state, piece, solved)
	local rules = runtime.options.independentPoseRules and runtime.options.independentPoseRules[piece.tag]
	if not rules then return 0, 0 end
	local calls, written, poses, desired = 0, 0, {}, {}
	local U = S.Unit
	piece.clothingRest = piece.clothingRest or {}
	local function world_rotation(name, offset)
		if poses[name] == nil then
			local node = safe_node(S, piece.unit, name)
			local pose = solved and solved[piece.link.byNode[node]]
			if not pose and node then
				local cost
				pose, cost = Compat.independent_world_pose(S, piece.unit, node)
				calls = calls + cost
			end
			poses[name] = pose and pose.q or false
		end
		local q = poses[name]
		return q and Compat.independent_fk({ p = { 0, 0, 0 }, q = q }, offset, { 0, 0, 0 }).q
	end
	local function driver_angles(driver)
		local source = world_rotation(driver.bone, driver.offset)
		local reference
		if driver.reference then
			reference = world_rotation(driver.reference, driver.referenceOffset)
		else
			reference = world_rotation("root", { -driver.referenceOffset[1], -driver.referenceOffset[2],
				-driver.referenceOffset[3], driver.referenceOffset[4] })
		end
		if source and reference then return Compat.clothing_driver_angles(source, reference, driver.neutral) end
	end
	for _, rule in ipairs(rules) do
		local node = safe_node(S, piece.unit, rule.target)
		local primary = driver_angles(rule.primary)
		local secondary = rule.secondary and driver_angles(rule.secondary)
		if node and primary and (not rule.secondary or secondary) then
			local rest = piece.clothingRest[rule.target]
			if not rest or rest.node ~= node then
				local raw = try(U.local_rotation, piece.unit, node)
				local ok, x, y, z, w = pcall(S.Quaternion.to_elements, raw)
				local q = ok and Compat.independent_quaternion(x, y, z, w)
				calls = calls + 2
				rest = q and { node = node, q = q }
				piece.clothingRest[rule.target] = rest
			end
			if rest then desired[#desired + 1] = { node, Compat.clothing_rotation(rule, primary, secondary, rest.q) } end
		end
	end
	for _, row in ipairs(desired) do
		local ok, cost = Compat.write_independent_rotation(S, piece.unit, row[1], row[2], true)
		calls = calls + cost
		if ok then written = written + 1 end
	end
	if written > 0 then
		try(S.World.update_unit, state.world, piece.unit)
		calls = calls + 1
	end
	return written, calls
end

function IndependentLink.restore_clothing(state, piece)
	if not piece.clothingRest then return end
	for name, rest in pairs(piece.clothingRest) do
		if safe_node(S, piece.unit, name) == rest.node then
			Compat.write_independent_rotation(S, piece.unit, rest.node, rest.q, true)
		end
	end
	try(S.World.update_unit, state.world, piece.unit)
	piece.clothingRest = nil
end

local function independent_release(runtime, state)
	if not state or state.world ~= runtime.world or refresh_live_units(runtime, S) ~= "ok" then return end
	for _, piece in ipairs(state.pieces) do
		if (piece.link or piece.hidden or piece.positionDirty or piece.clothingRest) and session_unit_live(runtime, piece.unit)
			and Compat.unit_alive(S, piece.unit) then
			local restored = IndependentLink.restore(state, piece)
			IndependentLink.restore_clothing(state, piece)
			if restored and piece.positionDirty then
				for b, translation in pairs(piece.translations) do
					if safe_node(S, piece.unit, state.list[b][1]) == piece.nodes[b] then
						Compat.write_independent_position(S, piece.unit, piece.nodes[b], translation[2], 1)
					end
				end
				try(S.World.update_unit, state.world, piece.unit)
			end
			if piece.hidden then independent_visibility(piece, true) end
		end
	end
end

function IndependentLink.release_all(runtime)
	independent_release(runtime, runtime.independent)
	for _, state in pairs(runtime.independentRemotes or {}) do independent_release(runtime, state) end
	runtime.independent, runtime.independentRemotes = nil, nil
end

local function independent_scan(runtime, state, avatar, world, list)
	-- E12d：照 IAS 每帧枚举 + 当帧归属（IAS runtime.lua 1500–1620、6690–6830）。
	-- 件来自本帧 Roster 的装甲件枚举，归属 = 离最近 avatar（3 m、歧义裕度），不保存跨帧结论、
	-- 没有扫描间隔 / 退避 / 缓存 / 额度。
	local roster = runtime.roster and runtime.roster.frame == (runtime.frame or 0) and runtime.roster.byWorld[world]
	if not roster then return false end
	local options = runtime.options
	local manifest, slots = options.independentPieces, options.independentPieceSlots or {}
	state.owners = roster.avatars

	-- 本帧不再属于本 avatar 的件：放掉（原件仍活着才恢复）。
	local present, claimed, seen = {}, {}, {}
	for _, row in ipairs(roster.armour) do present[row.unit] = row end
	for i = #state.pieces, 1, -1 do
		local piece = state.pieces[i]
		local row = present[piece.unit]
		if row ~= nil and roster.owner[piece.unit] == avatar then
			seen[piece.unit], claimed[piece.slot] = true, true
		else
			if row ~= nil and piece.link then IndependentLink.restore(state, piece, true) end
			if row ~= nil then IndependentLink.restore_clothing(state, piece) end
			if row ~= nil and piece.hidden then independent_visibility(piece, true) end
			table.remove(state.pieces, i)
		end
	end
	local expected = {}
	for tag in pairs(manifest or {}) do
		local slot = slots[tag] or tag
		local alternatives = expected[slot] or {}
		alternatives[#alternatives + 1] = tag
		expected[slot] = alternatives
	end
	local missing = {}
	for slot in pairs(expected) do if not claimed[slot] then missing[slot] = true end end
	for _, row in ipairs(roster.armour) do
		local unit = row.unit
		if not seen[unit] and unit ~= avatar and roster.owner[unit] == avatar then
			local tag = "#ID[" .. row.hash .. "]"
			local slot = slots[tag] or tag
			local names = manifest and manifest[tag]
			if names and missing[slot] then
				do
					local nodes, bones, selected = {}, {}, {}
					for _, name in ipairs(names or {}) do selected[name] = true end
					local complete = true
					for b, bone in ipairs(list) do
						if not manifest or selected[bone[1]] then
							local node = safe_node(S, unit, bone[1])
							if node then
								nodes[b], bones[#bones + 1] = node, b
							elseif manifest then
								complete = false
							end
						end
					end
					if complete and #bones > 0 then
						local piece = { unit = unit, nodes = nodes, bones = bones, wrists = {}, parents = {},
							tag = tag, slot = slot, translations = {}, bind = {} }
						local translations = options.independentTranslations and options.independentTranslations[tag]
						piece.bodyBind = translations ~= nil
						for _, b in ipairs(bones) do
							local source = list[b][2]
							if translations then
								piece.translations[b] = translations[list[b][1]]
								local x, y, z = readVector(S, try(S.Unit.local_position, unit, nodes[b]))
								if x ~= nil then piece.bind[b] = { x, y, z } end
							end
							if source == "l_hand" or source == "r_hand" then piece.wrists[#piece.wrists + 1] = b end
							local parent = state.parents[b]
							if parent and (not parent.bone or not nodes[parent.bone]) then
								piece.parents[b] = safe_node(S, unit, parent.name)
							end
						end
						state.pieces[#state.pieces + 1] = piece
						missing[slot] = nil
					end
				end
			end
		end
	end

	-- 有源骨暂时读不到时，保留件缓存，待下一帧重新解析源节点。
	local sourceMissing = false
	for b, bone in ipairs(list) do
		state.sources[b] = safe_node(S, avatar, bone[2])
		if state.sources[b] == nil then sourceMissing = true end
	end
	local tags, absent, total = {}, {}, 0
	for _, piece in ipairs(state.pieces) do tags[#tags + 1] = piece.tag end
	for slot, alternatives in pairs(expected) do
		total = total + 1
		if missing[slot] then
			table.sort(alternatives)
			absent[#absent + 1] = table.concat(alternatives, "/")
		end
	end
	table.sort(absent)
	local detail = #absent > 0 and table.concat(absent, "、") or "无"
	local signature = total .. "|" .. #tags .. "|" .. detail
	if state.lastScanSignature ~= signature then
		state.lastScanSignature = signature
		runtime.logger("[physbone][独立骨] 清单 %d 件 / 找到 %d 件 / 缺 %s ｜ %s",
			total, #tags, detail, table.concat(tags, "、"))
	end
	local frame = runtime.frame or 0
	state.rescanAt = frame + 1   -- 每帧枚举（IAS 每回调都做）
	return true
end

local function independent_state(runtime, avatar, world, list)
	local rest = runtime.options.independentRest
	local state = { avatar = avatar, world = world, rest = rest, list = list, pieces = {}, sources = {}, parents = {},
		probes = {}, frames = 0, normals = {}, rescanAt = 0 }
	local sources = {}
	for b, bone in ipairs(list) do sources[bone[2]] = b end
	state.boneBySource = sources
	for b, data in ipairs(rest or {}) do
		state.parents[b] = { name = data[1], bone = sources[data[1]] }
	end
	for _, side in ipairs({ "l", "r" }) do
		local sh, el, ha = sources[side .. "_shoulder"], sources[side .. "_elbow"], sources[side .. "_hand"]
		if sh then state.probes[#state.probes + 1] = sh end
		if sh and el and ha then
			if not rest then
				state.parents[sh] = { name = side .. "_clavicle" }
				state.parents[el] = { name = side .. "_shoulder", bone = sh }
				state.parents[ha] = { name = side .. "_elbow", bone = el }
			end
		end
	end
	for _, source in ipairs({ "hips", "spine1" }) do
		if sources[source] then table.insert(state.probes, 1, sources[source]) end
	end
	return state
end

local function independent_frame(runtime, avatar, world, list, state, remote)
	if world == nil then return 0, 0, state end
	local frame = runtime.frame or 0
	if not state or state.avatar ~= avatar or state.world ~= world then
		independent_release(runtime, state)
		state = independent_state(runtime, avatar, world, list)
	end
	state.remote = remote
	state.frame = frame
	local tune = tune_values(runtime)
	local linkOption = tune.independentLink
	if linkOption == nil then linkOption = runtime.options.independentLink end
	local linkEnabled = linkOption ~= 0 and linkOption ~= false
	if state.linkEnabled ~= linkEnabled then
		for _, piece in ipairs(state.pieces) do piece.linkFailed = nil end
		state.linkEnabled, state.nextCorrection = linkEnabled, nil
	end
	if frame >= state.rescanAt and not independent_scan(runtime, state, avatar, world, list) then
		state.rescanAt = frame + 30
	end
	if #state.pieces == 0 then
		state.hasLinks, state.linkedUnits, state.nextCorrection = false, {}, nil
		return 0, 0, state
	end

	local calls, written = 0, 0
	local rotations, positions = {}, {}
	local function rotation_components(raw)
		local ok, x, y, z, w = pcall(S.Quaternion.to_elements, raw)
		calls = calls + 1
		return ok and Compat.independent_quaternion(x, y, z, w) or nil
	end
	local function read_rotation(node, numeric)
		if node == nil then return nil end
		if rotations[node] == nil then
			local raw = try(S.Unit.local_rotation, avatar, node)
			calls = calls + 1
			local valid, q
			if raw ~= nil then
				if not numeric and type(S.Quaternion.is_valid) == "function" then
					valid = try(S.Quaternion.is_valid, raw)
					calls = calls + 1
				else
					q = rotation_components(raw)
					valid = q ~= nil
				end
			end
			rotations[node] = valid == true and { raw = raw, q = q } or false
		end
		return rotations[node] or nil
	end
	local function read_position(node)
		if node == nil then return nil end
		if positions[node] == nil then
			local x, y, z = readVector(S, try(S.Unit.local_position, avatar, node))
			calls = calls + (x ~= nil and 2 or 1)
			positions[node] = x and x == x and y == y and z == z
				and math.abs(x) < math.huge and math.abs(y) < math.huge and math.abs(z) < math.huge
				and { x, y, z } or false
		end
		return positions[node] or nil
	end

	local hz = tonumber(tune.poseHz or runtime.options.poseHz) or 30
	if hz ~= hz then hz = 30 end
	local everyFrame = not remote and runtime.options.poseEveryFrame == true
	local period = 1 / (remote and 20 or math.max(15, math.min(60, hz)))
	local blendOption = tune.poseBlend
	if blendOption == nil then blendOption = runtime.options.poseBlend end
	local blend = not remote and not everyFrame and blendOption ~= 0 and blendOption ~= false
	local now = (state.poseTime or 0) + (runtime.dt or 1/60)
	state.poseTime = now
	-- Remote callbacks may be skipped by the distance stride. Use the same
	-- engine clock as local callbacks when available, including across skips.
	now = runtime.lastTime or now
	if state.period ~= period then state.nextCorrection, state.period = nil, period end
	local deadline = state.nextCorrection
	local due = everyFrame or not deadline or now + 1e-6 >= deadline
	local paused = false
	local sourcePoses = {}
	local function readPose(name)
		if sourcePoses[name] == nil then
			local node = state.sources[state.boneBySource[name]] or safe_node(S, avatar, name)
			local pose, cost
			if node then pose, cost = Compat.contact_math.world_pose(S, avatar, node) end
			calls = calls + (cost or 0)
			sourcePoses[name] = pose or false
		end
		return sourcePoses[name] or nil
	end
	local weapon, viewFirstPerson, presentationAction
	if linkEnabled and state.rest and (state.hasLinks or due) then
		local animation, cost = frame_animation_machine(runtime, avatar)
		calls = calls + cost
		paused = not animation.active
		if not remote and not paused then
			viewFirstPerson = IndependentLink.first_person(runtime, state)
			if runtime.options.independentGrips and (due or viewFirstPerson) then
				presentationAction = IndependentLink.first_person_action(state, readPose)
			end
			paused = viewFirstPerson or presentationAction == 3
			if due then
				local tuple, animationCost = frame_animation_state(animation, avatar)
				calls = calls + animationCost
				if not runtime.options.independentGrips then
					paused = paused or tuple ~= nil and (tuple[4] == 76 or tuple[4] == 77)
				end
			end
		end
		if paused then
			state.action, state.contacts, state.singleHand = presentationAction or 0, {}, false
		elseif due and not remote and runtime.options.independentGrips then
			local eligible, actionCost
			state.action, eligible, actionCost = IndependentLink.action(avatar, animation)
			calls = calls + actionCost
			state.contacts = state.action > 0 and IndependentLink.contacts(state.action, readPose) or {}
			state.singleHand = false
			if eligible then
				weapon = IndependentLink.held_weapon(runtime, state, readPose)
				state.singleHand = weapon ~= nil and (weapon.sidearm or not weapon.support)
				-- E12c：空主动作 + 武器姿势树但**找不到手里的武器**（没拿枪 / 武器未识别 / 复活后还没生成）：
				-- 不能把双手拉去握点（那里没有枪）：双手改跟原生手轨迹（与 action=2 的接触同口径），手臂照抄动画。
				if weapon == nil then
					state.contacts, state.singleHand = IndependentLink.contacts(2, readPose), false
				end
			end
		end
	end
	if paused then
		due, state.nextCorrection = false, nil
	elseif due then
		if deadline and not everyFrame then
			state.nextCorrection = deadline + (math.floor((now + 1e-6 - deadline) / period) + 1) * period
		else
			state.nextCorrection = now + period
		end
	end
	local samples, sourceWorlds = {}, {}
	local vectors = { finish = {}, display = {}, calls = 0, written = 0 }
	local function sample(b, position)
		local node = state.sources[b]
		local cached = samples[b]
		if cached then
			if position then cached.p = read_position(node) end
			return cached
		end
		local rotation = read_rotation(node, true)
		if not rotation then return nil end
		rotation.q = rotation.q or rotation_components(rotation.raw)
		if sourceWorlds[node] == nil then
			local pose, cost = Compat.independent_world_pose(S, avatar, node)
			sourceWorlds[node] = pose or false
			calls = calls + cost
		end
		if rotation.q and sourceWorlds[node] then
			local value = { q = rotation.q, world = sourceWorlds[node] }
			samples[b] = value
			return value
		end
	end
	if weapon and (tune.weaponRootTrial == 1 or (tune.weaponRootTrial == nil and runtime.options.weaponRootTrial == true))
		and type(rawget(_G, "update")) == "function" then
		IndependentLink.weapon_trial(runtime, state, weapon, sample, vectors, readPose)
	end

	-- Each normal frame samples a bounded slice of one piece. An isolated piece
	-- gets priority on intervening frames so the rest of the set is still checked.
	if not state.sampleRound or (not state.samplePiece and state.frames >= state.sampleRound + 30) then
		local bones = 0
		for _, piece in ipairs(state.pieces) do bones = bones + #piece.bones end
		state.sampleChunk = math.max(6, math.ceil(bones / math.max(1, 30 - #state.pieces)))
		state.sampleRound, state.samplePiece, state.sampleBone = state.frames, 1, 1
	end
	local routine = state.pieces[state.samplePiece or 0]
	local sampled, first, last, checkWrist
	if not routine or state.frames % 2 == 0 then
		for _, piece in ipairs(state.pieces) do
			if piece.recheck and (not sampled or piece.lastSample < sampled.lastSample) then sampled = piece end
		end
	end
	if sampled then
		first = sampled.scaleFault
		last, checkWrist = first, not remote
	elseif routine then
		sampled, first = routine, state.sampleBone
		last = math.min(first + state.sampleChunk - 1, #routine.bones)
		checkWrist = last == #routine.bones and not remote
		if last == #routine.bones then
			state.samplePiece, state.sampleBone = state.samplePiece + 1, 1
		else
			state.sampleBone = last + 1
		end
		if not state.pieces[state.samplePiece] then state.samplePiece = nil end
	end

	local function check_scale(piece, from, to)
		for index = from, to do
			local b = piece.bones[index]
			local x, y, z = readVector(S, try(S.Unit.local_scale, piece.unit, piece.nodes[b]))
			calls = calls + (x ~= nil and 2 or 1)
			if x == nil then return index end
			if not (math.abs(x-1) <= 0.0001 and math.abs(y-1) <= 0.0001 and math.abs(z-1) <= 0.0001) then
				if IndependentLink.scale_state(x, y, z) ~= "hidden" then return index end
			end
		end
	end

	local function pose(piece)
		local healthy, fallback, posed = true, true, {}
		local translations = {}
		for _, b in ipairs(piece.bones) do
			local source = read_rotation(state.sources[b])
			posed[b] = source
			if source then
				local ok, result = pcall(S.Unit.set_local_rotation, piece.unit, piece.nodes[b], source.raw)
				calls = calls + 1
				if ok and result ~= false then written = written + 1 else fallback = false end
			else
				fallback = false
			end
		end
		if not remote and fallback and not piece.scaleFault then
			for b, translation in pairs(piece.translations) do
				local p = read_position(state.sources[b])
				if p == nil then healthy = false break end
				local a, m = translation[1], translation[2]
				translations[b] = { m[1]+p[1]-a[1], m[2]+p[2]-a[2], m[3]+p[3]-a[3] }
			end
			if healthy then
				for b, value in pairs(translations) do
					piece.positionDirty = true
					local ok, cost = Compat.write_independent_position(S, piece.unit, piece.nodes[b], value, 1)
					calls = calls + cost
					if not ok then healthy = false break end
				end
			end
		end
		if (not healthy or not fallback or piece.scaleFault or remote) and piece.positionDirty then
			local restored = true
			for b, translation in pairs(piece.translations) do
				local ok, cost = Compat.write_independent_position(S, piece.unit, piece.nodes[b], translation[2], 1)
				calls = calls + cost
				restored = restored and ok
			end
			piece.positionDirty = not restored
			fallback = fallback and restored
			translations = {}
		end
		local ok, result = pcall(S.World.update_unit, world, piece.unit)
		calls = calls + 1
		fallback = fallback and ok and result ~= false and piece.scaleFault == nil
		if piece == sampled and fallback and state.rest and (checkWrist or (last and piece.bodyBind and not remote)) then
			local fk, roots = {}, {}
			local function bone_pose(b)
				if fk[b] then return fk[b] end
				local parent = state.parents[b]
				local parentPose
				if parent.bone and piece.nodes[parent.bone] then
					parentPose = bone_pose(parent.bone)
				elseif piece.parents[b] then
					local node = piece.parents[b]
					if roots[node] == nil then
						local cost
						roots[node], cost = Compat.independent_world_pose(S, piece.unit, node)
						calls = calls + cost
						roots[node] = roots[node] or false
					end
					parentPose = roots[node]
				end
				local position = translations[b] or piece.bind[b] or state.rest[b][4]
				local rotation = posed[b]
				if parentPose and rotation then
					-- Numeric components are only needed for the sampled FK check.
					rotation.q = rotation.q or rotation_components(rotation.raw)
					if rotation.q then fk[b] = Compat.independent_fk(parentPose, rotation.q, position) end
				end
				return fk[b]
			end
			local function check_position(b)
				local expected = bone_pose(b)
				local x, y, z = readVector(S, try(S.Unit.world_position, piece.unit, piece.nodes[b]))
				calls = calls + (x ~= nil and 2 or 1)
				local p = expected and expected.p
				local distance = p and vec_dist(x, y, z, p[1], p[2], p[3])
				return distance and distance <= 0.02
			end
			if last and piece.bodyBind and not remote then fallback = check_position(piece.bones[last]) end
			if checkWrist and fallback then
				for _, b in ipairs(piece.wrists) do
					if not check_position(b) then fallback = false break end
				end
			end
		end
		return healthy and fallback, fallback
	end

	local function legacy_piece(piece)
		local sampledNow = piece == sampled
		if sampledNow then
			if first then
				local fault = check_scale(piece, first, last)
				if fault or (piece.scaleFault and piece.scaleFault >= first and piece.scaleFault <= last) then
					piece.scaleFault = fault
				end
			end
			piece.lastSample = state.frames
		end
		local healthy, fallback = pose(piece)
		if not healthy and piece.scaleFault == nil then
			piece.scaleFault = check_scale(piece, 1, #piece.bones)
			fallback = fallback and piece.scaleFault == nil
		end
		local checked = sampledNow and (checkWrist or piece.scaleFault ~= nil or remote)
		if checked then piece.recheck = not healthy or piece.isolated end
		if healthy and (checked or not piece.recheck) then
			piece.failures = 0
			piece.successes = (piece.successes or 0) + 1
			if piece.isolated and piece.successes >= 3 then
				piece.isolated, piece.recheck = false, nil
				runtime.logger("[physbone][独立骨] 恢复 %s", piece.tag)
			end
		elseif not healthy and (piece.scaleFault == nil or checked) then
			piece.successes = 0
			piece.failures = (piece.failures or 0) + 1
			if not piece.isolated and piece.failures >= 3 then
				piece.isolated = true
				runtime.logger("[physbone][独立骨][警告] 隔离 %s", piece.tag)
			end
		end
		if piece.isolated and not fallback and not piece.hidden then calls = calls + independent_visibility(piece, false) end
		if not piece.isolated and healthy and piece.hidden then calls = calls + independent_visibility(piece, true) end
	end

	state.hasLinks, state.linkedUnits = false, {}
	state.correction = { due = due, now = now, nextAt = state.nextCorrection, blend = blend, paused = paused }
	for index = #state.pieces, 1, -1 do
		local piece = state.pieces[index]
		if not session_unit_live(runtime, piece.unit) or not Compat.unit_alive(S, piece.unit) then
			table.remove(state.pieces, index)
			state.samplePiece, state.sampleRound = nil, nil
			state.rescanAt = frame + 1
		else
			if piece.linkFailed and not piece.link and piece.linkRetryAt and frame >= piece.linkRetryAt then
				piece.linkFailed, piece.linkRetryAt = nil, nil
			end
			local linked = piece.link and not piece.linkFailed
			local pieceDue = due or not piece.link
			if piece.link and (not linkEnabled or piece.linkFailed) then
				if not IndependentLink.restore(state, piece) then pieceDue = false end
				linked = false
			end
			if linkEnabled and state.rest and pieceDue and not paused and not piece.link and not piece.linkFailed then
				linked = IndependentLink.attach(runtime, state, piece)
			end
			if linked then
				local ok, n, cost = pcall(IndependentLink.present, runtime, state, piece, pieceDue, now,
					blend and not paused, sample, vectors, readPose)
				if ok then
					written, calls = written + n, calls + cost
					state.hasLinks, state.linkedUnits[piece.unit] = true, true
					if paused then
						for _, row in pairs(piece.link.rows) do row.endpoint = nil end
					end
					if piece.hidden then calls = calls + independent_visibility(piece, true) end
				else
					IndependentLink.fail(runtime, state, piece, n)
					if runtime.weaponShift then
						IndependentLink.restore_weapon(runtime)
						state.nextCorrection = nil
					end
					linked = false
				end
			end
			if not linked and not piece.link and (not paused or piece.linkFailed) then
				legacy_piece(piece)
			end
			if not linked and not piece.link and pieceDue and not paused and not piece.hidden then
				local count, cost = IndependentLink.present_clothing(runtime, state, piece)
				written, calls = written + count, calls + cost
			end
		end
	end
	written, calls = written + vectors.written, calls + vectors.calls
	state.frames = state.frames + 1
	if INDEPENDENT_LOG_FRAMES[state.frames] then
		runtime.logger("[physbone][独立骨] 第%d帧 %d件 写入=%d 调用=%d", state.frames, #state.pieces, written, calls)
	end
	return written, calls, state
end

local function sample_action_state(runtime, avatar)
	local tune = tune_values(runtime)
	local enabled = tune.stateLog
	local cache = runtime.actionStateLog
	if enabled == nil and tune.text ~= nil then
		if tune.text.stateLog == "false" then enabled = 0 end
		if tune.text.stateLog == "true" then enabled = 1 end
	end
	if enabled == 0 or (enabled == nil and (runtime.options or {}).stateLog ~= true) then
		if cache ~= nil then
			cache.avatar, cache.world, cache.previous = nil, nil, nil
		end
		return
	end

	local log = runtime.logger
	local second = os.time()
	if cache == nil then
		cache = { second = second, lines = 0, skipped = 0, starts = {} }
		runtime.actionStateLog = cache
	elseif cache.second ~= second then
		if cache.skipped > 0 then log("[physbone][动作状态] 省略 %d 次变化", cache.skipped) end
		cache.second, cache.lines, cache.skipped = second, 0, 0
	end

	local unit = S.Unit
	if avatar == nil or try(unit.alive, avatar) ~= true then avatar = nil end
	local world = runtime.world
	if cache.avatar ~= avatar or cache.world ~= world then
		cache.avatar, cache.world, cache.previous = avatar, world, nil
		if avatar ~= nil then cache.starts[#cache.starts + 1] = tostring(avatar) end
	end
	-- 每个自然秒留一行给上一秒的省略汇总；换人通知也占本秒额度。
	while cache.lines < 19 and #cache.starts > 0 do
		log("[physbone][动作状态] 开始采集 avatar=%s", table.remove(cache.starts, 1))
		cache.lines = cache.lines + 1
	end

	-- 缺少状态机时两个动画读取接口都不能进；pcall 无法拦住原生崩溃。
	if avatar == nil then return end
	local animation = frame_animation_machine(runtime, avatar)
	if not animation.active then return end
	local current = frame_animation_state(animation, avatar)
	if current == nil then return end
	local previous = cache.previous
	local changed = previous == nil or previous.n ~= current.n
	if not changed then
		for index = 1, current.n do
			if previous[index] ~= current[index] then changed = true; break end
		end
	end
	cache.previous = current
	if not changed then return end
	if cache.lines >= 19 then
		cache.skipped = cache.skipped + 1
		return
	end

	local layers, values = {}, {}
	if previous ~= nil then
		for index = 1, math.max(previous.n, current.n) do
			if previous[index] ~= current[index] or (index <= previous.n) ~= (index <= current.n) then
				layers[#layers + 1] = string.format("%d:%s→%s", index - 1,
					tostring(previous[index]), tostring(current[index]))
			end
		end
	end
	for index = 1, current.n do values[index] = tostring(current[index]) end
	local time, length = frame_animation_layer(animation, avatar)
	local clip = "?"
	if type(time) == "number" and time >= 0 and time < math.huge
		and type(length) == "number" and length > 0 and length < math.huge then
		clip = string.format("%.3fs/t=%.2fs", length, time)
	end
	log("[physbone][动作状态] n=%d 变化层=%s L11片段=%s 全部=[%s]", current.n,
		previous == nil and "初始" or table.concat(layers, ","), clip, table.concat(values, ","))
	cache.lines = cache.lines + 1
end

local function apply_arm_pose_frame(runtime)
	if type(Compat.apply_arm_pose) ~= "function" then return 0, 0 end
	-- 本帧枚举（drive 已做则同帧复用）：本帧用到的句柄都来自这一次枚举。
	if runtime.roster == nil or runtime.roster.frame ~= (runtime.frame or 0) then
		local okRoster = pcall(Roster.update, runtime)
		if okRoster and runtime.roster ~= nil and runtime.roster.identity then resolve_local_avatar_for_arm(runtime) end
	end
	local unit = runtime.avatar
	if unit ~= nil and not session_unit_live(runtime, unit) then
		runtime.avatar, unit = nil, nil
	end
	if unit == nil then
		unit = resolve_local_avatar_for_arm(runtime)
	end
	sample_action_state(runtime, unit)
	if unit == nil then
		runtime.armState = nil
		IndependentLink.release_all(runtime)
		runtime.armCalls = 0
		-- 一次性：开着却一直找不到玩家角色（实机排查"到底跑没跑"用）。
		runtime.armMissFrames = (runtime.armMissFrames or 0) + 1
		if runtime.armMissFrames == 600 and not runtime.armMissLogged then
			runtime.armMissLogged = true
			runtime.logger("[physbone][警告] 姿态修正：开着但 600 帧内没找到玩家角色（avatar），一次都没写")
		end
		return 0, 0
	end
	if not Compat.unit_alive(S, unit) then
		runtime.avatar = nil
		runtime.avatarTag = nil
		runtime.avatars = nil
		runtime.armState = nil
		runtime.armScanAt = nil
		IndependentLink.release_all(runtime)
		runtime.armCalls = 0
		return 0, 0
	end
	local world = runtime.cache and runtime.cache.world or nil
	local independent = runtime.options and runtime.options.independentBones
	if type(independent) == "table" and #independent > 0 then
		-- 给了独立骨就只驱动独立骨：写 avatar 同名骨画面不认（实机证实），白花调用。
		local written, calls, state = independent_frame(runtime, unit, world, independent, runtime.independent, false)
		runtime.independent = state
		local remoteStates = runtime.independentRemotes or {}
		runtime.independentRemotes = {}
		local tune = tune_values(runtime)
		local remoteEnabled = runtime.options.independentTeammates == true
		if tune.text and tune.text.independentTeammates ~= nil then
			remoteEnabled = tune.text.independentTeammates == "true" or tune.text.independentTeammates == "1"
		end
		if remoteEnabled and state then
			local metres = sched_settings(runtime).metres
			local frame = runtime.frame or 0
			for index, owner in ipairs(runtime.avatars or state.owners or {}) do
				if owner.unit ~= unit and session_unit_live(runtime, owner.unit) and Compat.unit_alive(S, owner.unit) then
					local distance = owner.cameraDistance or math.huge
					local stride = distance <= metres * metres and 1 or 4
					local remote = remoteStates[owner.unit]
					remoteStates[owner.unit] = nil
					if (frame + index) % stride == 0 then
						local n, cost
						n, cost, remote = independent_frame(runtime, owner.unit, world, independent, remote, true)
						written, calls = written + n, calls + cost
					end
					runtime.independentRemotes[owner.unit] = remote
				end
			end
		end
		for _, remote in pairs(remoteStates) do independent_release(runtime, remote) end
		if not runtime.armFirstLogged and written > 0 then
			runtime.armFirstLogged = true
			runtime.logger("[physbone] 独立骨首次生效 unit=%s 写入=%d 调用=%d",
				tostring(unit_tag(S, unit)), written, calls)
		end
		runtime.armCalls = calls
		runtime.armCallsAccum = (runtime.armCallsAccum or 0) + calls
		runtime.armFrames = (runtime.armFrames or 0) + 1
		runtime.armAlpha = 0
		return written, calls
	end
	local written, calls, alpha, _, resolved = Compat.apply_arm_pose(
		S, world, unit, runtime.armState, runtime.dt, arm_pose_options(runtime))
	runtime.armState = resolved
	-- 一次性：第一次真正写进去 / 腿骨查不到。只记一行，用来在实机日志里确认"跑起来了没有"。
	if not runtime.armFirstLogged and (written or 0) > 0 then
		runtime.armFirstLogged = true
		runtime.logger("[physbone] 姿态修正首次生效 unit=%s 写入=%d 调用=%d 腿=%s",
			tostring(unit_tag(S, unit)), written or 0, calls or 0,
			resolved ~= nil and (resolved.legNodes == false and "缺骨" or resolved.legNodes ~= nil and "已写" or "无目标") or "-")
	end
	if resolved ~= nil and resolved.legNodes == false and not runtime.legMissLogged then
		runtime.legMissLogged = true
		runtime.logger("[physbone][警告] 姿态修正：给了腿关节目标，但这个 unit 找不到腿骨，腿不修")
	end
	runtime.armCalls = calls or 0
	runtime.armCallsAccum = (runtime.armCallsAccum or 0) + (calls or 0)
	runtime.armFrames = (runtime.armFrames or 0) + 1
	runtime.armAlpha = alpha or 0
	return written or 0, calls or 0
end

-- ---------------------------------------------------------------------------
-- E12e：物理会话生命周期（照 IAS：每帧按 Roster 结果以"实例槽位 + profile"绑定，不跨帧持有名单）。
-- IAS 对应：runtime.lua 6860 一带 `C.bind_unit(profile.id, instance_id, avatar.id, root)` 每帧一次；
-- 帧号/时间跳变、传送、布局变化由原生直接重置、不追赶。
-- 我们的 Lua 求解器必须自己持有链状态：状态按 unit 句柄（弱键）存，**只对本帧枚举里存在的 unit 有效**，
-- 本帧枚举里没有它 → 状态直接丢弃（不碰句柄）；没有 discover/打分额度/负缓存/退避/整组重挂。
-- ---------------------------------------------------------------------------
local function physics_rig(runtime, hash, unit, carriers)
	local rows = runtime.hashRig
	if rows == nil then rows = {}; runtime.hashRig = rows end
	local row = rows[hash]
	if row ~= nil and row.rig ~= nil then return row end
	local frame = runtime.frame or 0
	if row ~= nil and frame < (row.retryAt or 0) then return nil end
	-- profile 表：哈希 → 容器，每个哈希只匹配一次（IAS 的 profile 是启动时装载的静态数据）。
	local best = Compat.select_rig(S, { unit }, carriers)
	local allowPartial = runtime.options.allowPartial == true
	-- 与 2.1.60 一致：按件切骨的转换器产物只带容器的一部分骨，部分命中（score >= 1）是正常情形。
	if best ~= nil and (best.full or allowPartial or (best.score or 0) >= 1) then
		row = { rig = best.rig, path = best.path }
		rows[hash] = row
		return row
	end
	rows[hash] = { retryAt = frame + Roster.INTERVAL }   -- 取 IAS 的 DISCOVERY_INTERVAL 节奏
	return nil
end

local function physics_sync(runtime)
	local roster = runtime.roster
	local frame = runtime.frame or 0
	if roster == nil or roster.frame ~= frame then return end
	local options = runtime.options
	local carriers = runtime.carriers
	if carriers == nil then
		local raw = options.carriers
		if raw == nil or #raw == 0 then raw = loadCarriers(S, runtime.cache, options, runtime.logger) end
		local removed
		carriers, removed = filterCarriers(raw, runtime.iasPhysicsGroups)
		if #raw > 0 then runtime.carriers = carriers end
		if removed > 0 and not runtime.iasFilterLogged then
			runtime.logger("[physbone] IAS 可选组掩码=%d，排除 %d 条骨记录", runtime.iasPhysicsGroups, removed)
			runtime.iasFilterLogged = true
		end
	end
	if carriers == nil or #carriers == 0 then return end

	-- 玩家名单：本帧枚举，按到相机的距离排序（第一位 = 离相机最近 = 自己的调度优先级）。
	local list, ownerOf = {}, {}
	local cameras = {}
	for world, row in pairs(roster.byWorld) do
		local cx, cy, cz = cameraPosition(S, world, runtime.cache)
		cameras[world] = { cx, cy, cz }
		for _, item in ipairs(row.avatars) do
			local entry = { unit = item.unit, world = world, x = item.p[1], y = item.p[2], z = item.p[3], positionFrame = frame }
			if cx ~= nil then
				local dx, dy, dz = item.p[1] - cx, item.p[2] - cy, item.p[3] - cz
				entry.cameraDistance = dx * dx + dy * dy + dz * dz
			end
			list[#list + 1] = entry
		end
	end
	table.sort(list, function(a, b)
		local da, db = a.cameraDistance, b.cameraDistance
		if da ~= nil and db ~= nil then return da < db end
		if da ~= nil then return true end
		if db ~= nil then return false end
		return tostring(a.unit) < tostring(b.unit)
	end)
	local previousTags = {}
	for _, old in ipairs(runtime.avatars or {}) do previousTags[old.unit] = old.tag end
	for index, entry in ipairs(list) do
		entry.order = index
		entry.tag = previousTags[entry.unit]
		ownerOf[entry.unit] = entry
	end
	runtime.avatars = #list > 0 and list or nil
	if #list > 0 then
		runtime.world = list[1].world
		-- 本帧已读过相机与各 avatar 根位置（IAS 同样每帧只读一次，R:1335 / R:6621-6626）：
		-- refresh_avatar_distances 直接复用，不再重读。
		local camera = cameras[list[1].world]
		if camera ~= nil and camera[1] ~= nil then
			runtime.poseCamera = { frame = frame, x = camera[1], y = camera[2], z = camera[3], world = list[1].world }
			runtime.avatarsFreshFrame = frame
		else
			runtime.avatarsFreshFrame = nil
		end
		if not (roster.identity and arm_pose_enabled(runtime)) then runtime.avatar = list[1].unit end
	end

	local byUnit = runtime.sessionByUnit
	if byUnit == nil then byUnit = setmetatable({}, { __mode = "k" }); runtime.sessionByUnit = byUnit end
	local desired, wanted = {}, {}
	for world, row in pairs(roster.byWorld) do
		for _, armour in ipairs(row.armour) do
			local ownerUnit = row.owner[armour.unit]
			if ownerUnit ~= nil and ownerOf[ownerUnit] ~= nil then
				desired[armour.unit] = { hash = armour.hash, owner = ownerOf[ownerUnit], world = world }
				wanted[#wanted + 1] = armour.unit
			end
		end
	end
	-- 本帧枚举里没有（或不再归属任何 avatar）的 unit：丢掉状态，不碰句柄。
	for unit in pairs(byUnit) do
		if desired[unit] == nil then byUnit[unit] = nil end
	end
	table.sort(wanted, function(a, b)
		local oa, ob = desired[a].owner.order, desired[b].owner.order
		if oa ~= ob then return oa < ob end
		return desired[a].hash < desired[b].hash
	end)
	local attachOptions = {
		logger = runtime.logger, strictIdentity = options.strictIdentity == true,
		fastWrite = options.fastWrite ~= false, solver = options.solver, bendBlend = options.bendBlend,
	}
	local attached = 0
	for _, unit in ipairs(wanted) do
		local want = desired[unit]
		local session = byUnit[unit]
		if session == nil then
			local rig = physics_rig(runtime, want.hash, unit, carriers)
			if rig ~= nil then
				attachOptions.world = want.world
				session = Compat.attach(S, unit, rig.rig, attachOptions)
				session.rigSource, session.unitTag, session.attachFrame = rig.path, "#ID[" .. want.hash .. "]", frame
				if #session.sessions > 0 then
					byUnit[unit] = session
					attached = attached + 1
				else
					runtime.emptyLogged = runtime.emptyLogged or setmetatable({}, { __mode = "k" })
					if runtime.emptyLogged[unit] == nil then
						runtime.emptyLogged[unit] = true
						runtime.logger("[physbone][失败] unit=%s 挂载完成但产出 0 条链（容器 %s）", tostring(session.unitTag), tostring(rig.path))
					end
					session = nil
				end
			end
		end
		if session ~= nil then
			session.owner, session.ownerOrder, session.ownerTag = want.owner.unit, want.owner.order, want.owner.tag
		end
	end
	local sessions = {}
	for _, unit in ipairs(wanted) do
		if byUnit[unit] ~= nil then sessions[#sessions + 1] = byUnit[unit] end
	end
	if attached > 0 then
		local chains = 0
		local counts = {}
		for _, session in ipairs(sessions) do chains = chains + #session.sessions; counts[#counts + 1] = #session.sessions end
		table.sort(counts, function(a, b) return a > b end)
		-- 每件的链数（降序）：多于一件时才附带，用来确认"按件切骨"生效（各件不同、合计远小于全量）。
		runtime.logger("[physbone] 已挂载 %d 个 unit / %d 条链（玩家 %d 个）；本帧新挂 %d%s", #sessions, chains, #list, attached,
			#counts > 1 and ("；每件链数 " .. table.concat(counts, ",")) or "")
		if runtime.attachedFrame == nil then
			runtime.attachedFrame, runtime.driveFrames, runtime.callsAccum = frame, 0, 0
			runtime.peakAngle, runtime.repeatCalls, runtime.zeroFrames = 0, 0, 0
		end
	end
	runtime.sessions = #sessions > 0 and sessions or nil
end

local function drive(runtime)
	local frame = runtime.frame
	local options = runtime.options
	local logger = runtime.logger
	runtime.logContext.frame = frame

	-- 诊断开关（`hardOff = 1`）：运行时**加载了但一个引擎调用都不发**。
	-- 用途（2026-11-11）：确认"仅仅是这份 runtime 存在"是否已经足够引发崩溃。
	-- 放在任何引擎调用之前，所以连 Compat.begin_frame 都不跑。
	if (tune_values(runtime).hardOff or 0) ~= 0 then
		if not runtime.hardOffLogged then
			runtime.hardOffLogged = true
			logger("[physbone][诊断] hardOff=1 ⇒ 运行时不动手（不发任何引擎调用）")
		end
		return
	end

	-- 诊断开关（`dumpApi = 1`）：导出引擎命名空间清单，用来判断有没有批量写骨 API。
	if (tune_values(runtime).dumpApi or 0) ~= 0 and runtime.apiDumped ~= true then
		runtime.apiDumped = true
		local okDump, dumpError = pcall(dump_api_surface, runtime)
		if not okDump then
			logger("[physbone][诊断] API 清单导出失败：%s", tostring(dumpError))
		end
	end

	Compat.begin_frame(frame)
	-- E12d：IAS 式每帧枚举（avatar / 装甲件 / 本人身份）。本帧用到的独立骨句柄都来自这一次枚举。
	local rosterOk = false
	if arm_pose_enabled(runtime) or options.carriers == nil or #options.carriers > 0 then
		local okRoster, rosterError = pcall(Roster.update, runtime)
		rosterOk = okRoster and runtime.roster ~= nil
		if not okRoster and not runtime.rosterErrorLogged then
			runtime.rosterErrorLogged = true
			logger("[physbone][错误] 每帧枚举失败：%s", tostring(rosterError))
		end
		if rosterOk and runtime.roster.identity and arm_pose_enabled(runtime) then resolve_local_avatar_for_arm(runtime) end
	end
	if runtime.independent and not arm_pose_enabled(runtime) then IndependentLink.release_all(runtime) end
	local driveClock = os.clock()   -- 这一拍驱动块占用的 CPU 时间（心跳里报出来）

	-- E12e：IAS 没有"先判死"这一步：本帧用到的句柄都来自本帧 Roster 的枚举（docs/agent/ias-runtime-lifecycle-map）。
	-- 物理会话在下面的 physics_sync 里按"本帧枚举 + 实例槽位"建立/丢弃，不跨帧持有名单。

	phase(runtime, "drive 开始")

	-- 身份链探针（**默认关**，调参文件写 `probeIdentityLink = 1` 打开）。
	-- 只读、一次性、崩了能续；结果只写日志，绝不参与任何判定。
	local identSwitch = tune_values(runtime).probeIdentityLink
	if identSwitch ~= nil and identSwitch ~= 0
		and runtime.sessions ~= nil and #runtime.sessions > 0 then
		pcall(probe_identity_link, runtime, S)
	end

	-- ---------- 手动重设通道：**必须放在所有 early return 之前** ----------
	--
	-- 2026-11-12 实机抓到（用户"按 F8 一点反应都没有"）：这一句原来在 `drive` 的**后半段**，
	-- 而"一个会话都没有"（正是最需要 F8 的时刻）会在中途 `return`，于是**根本没机会读键**。
	-- 结果是：能自愈的时候不需要按，需要按的时候读不到 —— 热键等于从未存在过。
	-- 现在提到最前面，代价是每帧两次 `Keyboard.button` 纯查询。
	pcall(sched_poll, S, runtime)

	-- dt 用引擎自己的时间差，不写死 1/60。
	-- 实测回调约 120 次/秒（不是 70，也不是 375），写死 1/60 会让物理比真实时间快/慢一截，
	-- 观感是"回弹拖沓"或"甩得太开"，很容易被误判成阻尼/刚度调错了。
	--
	-- ⚠️ `Application.time_since_launch` **每帧只更新一次**，所以：
	--   ① 只能在**帧之间**做差（在一次回调内部取首尾差值恒为 0，踩过）；
	--   ② 一帧里被调用多次时，后面那几次拿到的差值是 0 —— 这时**绝不能再用上一次的 dt 积分**，
	--      否则一帧就积了 N 倍的时间，物理快 N 倍、永远停不下来（表现就是"飘逸过头"）。
	--      正确做法是：时钟没前进 ⇒ 这一拍不是新的时间步 ⇒ 什么都不做。
	local now = try(S.Application.time_since_launch)
	local advanced = true
	if type(now) == "number" and now == now then
		local previous = runtime.lastTime
		runtime.lastTime = now
		if previous == nil then
			advanced = true                      -- 首帧：没有差值可比，照常驱动
		elseif now > previous then
			local delta = now - previous
			-- 上限 0.25 与 compat 的稳定性闸一致：卡顿帧不该被当成"过了 0.5 秒"去积分。
			runtime.rawDelta = delta               -- 限幅前的真实跨度，供"加载空档"判据用
			if delta > 0.25 then delta = 0.25 end
			if delta >= 0.001 then runtime.dt = delta end
			advanced = true
		else
			advanced = false                     -- 同一时间步内的重复调用
			runtime.repeatCalls = (runtime.repeatCalls or 0) + 1
			-- 兜底：引擎时钟**一直不动**（老版本没有这个量 / 被谁包了一层）时不能永远不动。
			-- 连续太多次没前进就退回"按上一次的 dt 照常积分"，并大声说一次——
			-- 宁可物理快一点，也不要静默冻住（静默失败是这一整层最贵的一类问题）。
			runtime.stuckClock = (runtime.stuckClock or 0) + 1
			if runtime.stuckClock >= (options.stuckClockLimit or 600) then
				if not runtime.stuckClockLogged then
					runtime.stuckClockLogged = true
					logger("[physbone][警告] 引擎时钟连续 %d 次没有前进（time_since_launch 不可用？）"
						.. "⇒ 退回固定 dt=%.4f 继续驱动", runtime.stuckClock, runtime.dt or (1 / 60))
				end
				runtime.stuckClock = 0
				advanced = true
			end
		end
	end
	if not advanced then return end

	-- 帧间隔过大（加载/切场景）只让物理重置；IAS：帧号/时间跳变原生直接重置、不追赶（行 6455 一带 lua-recovery）。
	local gap = runtime.rawDelta
	local resetPhysics = gap ~= nil and gap > 0.25
	runtime.rawDelta = nil

	local function drive_arm_only_and_beat()
		if not arm_pose_enabled(runtime) then return end
		local armWritten, armCalls = apply_arm_pose_frame(runtime)
		runtime.beatFrames = (runtime.beatFrames or 0) + 1
		local tune = tune_values(runtime)
		local heartbeat = math.floor(tune.heartbeatFrames or options.heartbeatFrames or 600)
		if heartbeat < 1 then heartbeat = 0 end
		if heartbeat > 0 and (runtime.beatFrames % heartbeat) == 0 then
			local avgArm = (runtime.armFrames or 0) > 0 and ((runtime.armCallsAccum or 0) / runtime.armFrames) or 0
			logger("[physbone] 手臂修正心跳 unit=%s 写入=%d 手臂调用=%d(%.0f/帧) alpha=%.2f",
				tostring(runtime.avatarTag or "-"), armWritten, armCalls, avgArm, runtime.armAlpha or 0)
		end
	end

	-- E12e：物理会话 = 本帧 Roster 枚举 ∩ 已有容器（IAS bind_unit：实例槽位 + profile，每帧）。
	physics_sync(runtime)
	if runtime.sessions == nil or #runtime.sessions == 0 then
		runtime.sessions = nil
		drive_arm_only_and_beat()
		return
	end

	-- ---------- 调度（谁拿最高物理权） ----------
	-- 放在驱动之前：这一帧的驱动顺序就用新排好的结果。
	-- 顺序有依赖：先把"每个玩家到相机的距离"刷新（≤4 次调用），
	-- 第一人称锁定、预算分档都用这一份新鲜值。
	-- （热键/调参通道已经在 `drive` 开头轮询过了 —— 那里不会被 early return 挡住。）
	phase(runtime, "refresh_avatar_distances 前")
	refresh_avatar_distances(S, runtime)
	phase(runtime, "refresh_avatar_distances 后")
	fp_lock_watch(S, runtime)
	phase(runtime, "scheduler_watch 前")
	scheduler_watch(S, runtime, runtime.dt)
	phase(runtime, "scheduler_watch 后")

	-- ---------- 驱动 ----------
	-- 计时不可靠（`Application.time_since_launch` 每帧只更新一次、`os.clock()` 被引擎其它
	-- 线程污染），所以性能只报**引擎调用次数**——唯一不会被环境噪声污染的口径。
	--
	-- 预算分配的目标不是"省"，而是**把清晰度按距离给出去**：
	--   · 自己（相机旁的那位）永远每帧，不参与任何节流；
	--   · 6 m 内的队友：每 2 帧一次（≈35~60 Hz 采样，肉眼基本看不出来）；
	--   · 6 m 外的队友：每 4 帧起，链数吃紧时最多退到每 8 帧一次。
	-- 老实现是"所有队友共用一个步长"，于是 6 m 内的队友和 60 m 外的一样稀疏，
	-- 而且 4 人局步长一律顶到上限 16（≈6 Hz）——这就是"队友物理像 PPT"的量化来源。
	--
	-- 被跳过的帧都把 dt **累积**到下一次（compat 内部再拆子步），
	-- 所以物理速度不变，变的只是采样率。

	-- 距离已经在这一帧开头刷新过了（见调度块），这里不再重复读。

	-- 每帧链数预算：给**队友**分档用的软目标（自己永不分帧，见下）。
	-- 160 → 600：160 会让 4 件/玩家都触发队友节流，而实测 600 链 ≈ 1200 次调用 ≈ 4.5 ms，
	-- 仍在用户的 5 ms 线内，且自己的物理不用降刷新率。
	local budget = options.maxChainsPerFrame or 600
	-- 分档边界与兜底阈值共用同一个数（用户要的就是"6 m 这个清晰边界"）：
	-- 近 = 会被看清的那个范围，远 = 只有轮廓的范围。
	local nearMetres = sched_settings(runtime).metres
	local nearLimit2 = nearMetres * nearMetres
	local primaryChains, nearChains, farChains = 0, 0, 0
	for index = 1, #runtime.sessions do
		local session = runtime.sessions[index]
		local chains = #(session.sessions or {})
		if (session.ownerOrder or 99) <= 1 then
			primaryChains = primaryChains + chains
		else
			local ownerDistance = owner_distance_sq(runtime, session)
			if ownerDistance <= nearLimit2 then
				nearChains = nearChains + chains
			else
				farChains = farChains + chains
			end
		end
	end
	local nearStride, farStride = schedule_budget(primaryChains, nearChains, farChains, budget)
	-- ---------- 自己**永不分帧**（2026-11-12 实机修正） ----------
	--
	-- 上一版（2.1.33）我让"自己的链数超预算时也按比例分帧"，结果用户立刻看到
	-- **头发/裙子在不停闪烁**：被跳过的帧里引擎把这些骨重新置回动画/绑定姿态，
	-- 于是画面在"写进去的物理姿态"和"绑定姿态"之间以 30/20 Hz 来回跳。
	--
	-- 物理的分帧只在"没有别的东西每帧覆盖这些骨"时才成立；HD2 的护甲骨**每帧都被骨架覆盖**，
	-- 所以对自己分帧 = 必然闪烁。省钱只能靠**认对目标**（少挂几件），
	-- 不能靠降刷新率 —— 那是用户能直接看见的。
	local primaryStride = 1
	local throttled = (nearStride > 1) or (farStride > 1)
	local changed = (nearStride ~= runtime.nearStride) or (farStride ~= runtime.farStride)
	runtime.nearStride, runtime.farStride = nearStride, farStride
	runtime.primaryStride = primaryStride
	if throttled and changed then
		logger("[physbone][预算] 自己 %d 链/帧（不分帧）· %.0f m 内队友 %d 链（每 %d 帧）· 远处 %d 链（每 %d 帧）· 每帧预算 %d",
			primaryChains, nearMetres, nearChains, nearStride, farChains, farStride, budget)
	end
	if primaryChains > budget and runtime.budgetOverLogged ~= true then
		runtime.budgetOverLogged = true
		logger("[physbone][预算] 自己身上 %d 链已超过每帧预算 %d：**不分帧**（分帧会让自己的物理闪烁）",
			primaryChains, budget)
	end

	local written, skippedSame, engineCalls, staticChains = 0, 0, 0, 0
	local armCalls
	if arm_pose_enabled(runtime) and runtime.options.independentBones then
		local _
		_, armCalls = apply_arm_pose_frame(runtime)
	end
	local attempted = 0
	local dt = runtime.dt or (1 / 60)
	local ownerStates = {}
	local ownerRoots = runtime.physicsOwnerRoots or {}
	runtime.physicsOwnerRoots = {}
	phase(runtime, "写骨 前（" .. tostring(#runtime.sessions) .. " 个会话）")
	for index = 1, #runtime.sessions do
		local session = runtime.sessions[index]
		-- compat 只在正常返回时刷新这两个计数；先清零，免得沿用上一帧的值把看门狗骗过去。
		session.skippedSame, session.engineCalls = 0, 0

		-- 时间**永远**先累积：没被驱动的这一帧不是"时间不存在"，攒到下一次一起积。
		-- 丢了它，物理会比真实时间慢 —— 表现是"队友的头发像在水里"。
		local pending = (session.pendingDt or 0) + dt
		local order = session.ownerOrder or 99
		session.skipOptionalIas = order > 1 and runtime.options.independentTranslations ~= nil
		local due = true
		local owner = session.owner
		local poseState = runtime.independent and runtime.independent.avatar == owner and runtime.independent
			or (runtime.independentRemotes or {})[owner]
		local schedule = poseState and poseState.linkedUnits and poseState.linkedUnits[session.unit] and poseState.correction
		if schedule and (schedule.paused or not schedule.due or poseState.frame ~= frame) then due = false end
		if schedule and schedule.paused then
			pending, session.resetPhysics, session.independentHistory = 0, true, nil
		end
		local ownerState = owner and ownerStates[owner]
		if owner ~= nil and ownerState == nil and session_unit_live(runtime, owner) then
			local active = Compat.unit_alive(S, owner) and frame_animation_machine(runtime, owner).active
			ownerState = { active = active }
			ownerStates[owner] = ownerState
			if active then
				-- Roster 本帧已读过 avatar 根位置（physics_sync 写进 runtime.avatars）：直接取，不再读引擎。
				local x, y, z
				for _, entry in ipairs(runtime.avatars or {}) do
					if entry.unit == owner and entry.positionFrame == frame then x, y, z = entry.x, entry.y, entry.z break end
				end
				if x == nil then x, y, z = readVector(S, try(S.Unit.world_position, owner, 1)) end
				if x ~= nil then
					local previous = ownerRoots[owner]
					ownerState.teleport = previous ~= nil and vec_dist(x, y, z, previous[1], previous[2], previous[3]) > 2
					runtime.physicsOwnerRoots[owner] = { x, y, z }
				end
			end
		end
		local frozen = owner ~= nil and (ownerState == nil or not ownerState.active)
		if frozen then due, pending, session.resetPhysics = false, 0, true end
		if resetPhysics or (ownerState and ownerState.teleport) then session.resetPhysics = true end
		if order > 1 then
			local stride = owner_distance_sq(runtime, session) <= nearLimit2 and nearStride or farStride
			-- 用**会话下标**错峰，而不是玩家序号：同一个玩家的若干护甲件如果同帧，
			-- 每 stride 帧就会出现一次成本尖峰（帧时间抖动比平均成本更影响手感）。
			if stride > 1 and ((frame + index) % stride) ~= 0 then due = false end
		end
		-- E12e：原来这里有"会话死亡后停写 N 帧"的拆解窗口（driveHoldUntil）。IAS 没有：它不跨帧持有句柄，
		-- 不在本帧枚举里的 unit 本帧压根碰不到，其余的照常处理（R:1417-1428、R:6862-6867、R:7004）。窗口已删除。
		-- 诊断开关（调参文件 `writeOff = 1`）：发现 / 挂载 / 调度 / 判活全都照旧，**只是不写骨**。
		-- 用途（2026-11-11）：物理 + 软映射同时开、多人局换场景必崩，而崩溃时我们的 Lua 根本没在跑
		-- （加载期间不回调，回来之前就崩了）。于是只剩两种可能，这个开关能把它们分开：
		--   不崩 ⇒ 触发点是"写骨"这条路径（在写入侧加固）；
		--   还崩 ⇒ 只要"挂着并持有 unit 句柄"就足够（要在换人/换场景时尽早放手）。
		if due and (tune_values(runtime).writeOff or 0) ~= 0 then
			if not runtime.writeOffLogged then
				runtime.writeOffLogged = true
				logger("[physbone][诊断] writeOff=1 ⇒ 只挂不写（用于分辨崩溃是否由写骨触发）")
			end
			due = false
		end
		-- 写骨前再复查一次存活：pruneSessions 只在**帧首**判过，
		-- 而场景拆解会在本帧中途继续释放 unit —— 那一小段窗口正是"写进野对象"的来源。
		-- unit_alive 是便宜的引擎调用，比赌一次野指针便宜得多。
		if due and session.unit ~= nil and type(Compat.unit_alive) == "function" then
			local okAlive, alive = pcall(Compat.unit_alive, S, session.unit)
			if okAlive and alive == false then due = false end
		end
		if due or session.independentPresentation and session_unit_live(runtime, session.unit)
			and (tune_values(runtime).writeOff or 0) == 0 then
			local operation = due and (session.resetPhysics and Compat.reset_physics or Compat.update) or nil
			local ok, out, solved = pcall(IndependentLink.physics, runtime, session, schedule, operation, pending)
			if solved or due and not ok then
				pending = 0
				attempted = attempted + 1
				session.resetPhysics = nil
			end
			if ok then
				if type(out) == "number" then written = written + out end
			elseif not session.updateErrorLogged then
				-- 以前这里是裸 pcall：一条 unit 每帧都抛错，日志里却什么也没有。
				session.updateErrorLogged = true
				logger("[physbone][错误] unit=%s 驱动抛错：%s", tostring(session.unitTag or "?"), tostring(out))
			end
			skippedSame = skippedSame + (session.skippedSame or 0)
			engineCalls = engineCalls + (session.engineCalls or 0)
		end
		session.pendingDt = pending

		for _, item in ipairs(session.sessions) do
			if (item.staticFrames or 0) > 0 then staticChains = staticChains + 1 end
		end
	end
	runtime.driveFrames = (runtime.driveFrames or 0) + 1
	-- 心跳专用计数：`driveFrames` 每次 tryAttach 成功都会归零（补挂在挂满后仍每 1~2 秒成功一次），
	-- 拿它取模，心跳就永远打不出来。这个只在安装时归零。
	runtime.beatFrames = (runtime.beatFrames or 0) + 1

	-- 抬升诊断（默认关；没有调参文件时这里只多一次缓存判断）。
	-- 放在物理写入之后：这一帧渲染用的就是"抬过"的 avatar 根。
	apply_lift(runtime)

	local armSuffix = ""
	if arm_pose_enabled(runtime) then
		if armCalls == nil then
			local _
			_, armCalls = apply_arm_pose_frame(runtime)
		end
		local avgArm = (runtime.armFrames or 0) > 0 and ((runtime.armCallsAccum or 0) / runtime.armFrames) or 0
		armSuffix = string.format(" 手臂调用=%d(%.0f/帧) alpha=%.2f", armCalls, avgArm, runtime.armAlpha or 0)
	end

	runtime.callsAccum = (runtime.callsAccum or 0) + engineCalls
	runtime.staticChains = staticChains
	runtime.lastChainTotal = primaryChains + nearChains + farChains
	runtime.lastAttempted = attempted
	-- 这一帧一条都没驱动（stride 分帧的正常现象）时不能让"零帧看门狗"累加，
	-- 否则分帧本身会被误判成"驱动失效"→ 重挂 → 分帧 → 再判失效，无限循环。
	local attemptedAny = attempted > 0

	-- 峰值：**偏离绑定方向**的最大角，也就是"离模型原本的形状有多远"。
	--
	-- ⚠️ 它**不是**单节转角，所以拿它跟 `angleLimit` 比是错的（上一版就这么错了）：
	-- 求解器夹的是"相对父节"的偏转，而多节链上第 k 节的"偏离绑定"会把前面每一节的偏转累加进去
	-- ——5 节 × 25° 累到 100° 完全合法。上一版把它算成"超限骨"，报了 28~77，纯属误报。
	-- 判"限位有没有生效"要看相对父节的偏转，那是求解器内部量，不在这一层。
	-- 骨名取 item.chain[index].name，限位取 item.bones[index].gains —— **两个不同的对象**。
	-- 混用过一次，于是限位永远打印 0°，看着像"限位没生效"，白查一轮。
	local framePeak, worstName, worstLimit = 0, nil, 0
	for _, session in ipairs(runtime.sessions) do
		for _, item in ipairs(session.sessions) do
			local rotations = item.lastRotations
			if rotations ~= nil then
				local simBones = item.tip and item.tip.bones or item.bones
				for index = 1, #rotations do
					local angle = rotations[index][4]
					if angle > framePeak then
						framePeak = angle
						local chainBone = item.chain and item.chain[index]
						worstName = chainBone and chainBone.name or nil
						local simBone = simBones and simBones[index]
						local gains = simBone and simBone.gains
						worstLimit = (gains and gains.limit or 0) * 180 / math.pi
					end
				end
			end
		end
	end
	local framePeakDeg = framePeak * 180 / math.pi
	if framePeakDeg > (runtime.peakAngle or 0) then runtime.peakAngle = framePeakDeg end

	-- 阶段心跳：每 N 帧一行，证明驱动块确实被进入过，并给出当帧的关键事实。
	-- 上一轮出现过"phase 明明是 live 但 update 从不执行"，没有这条就无法区分
	-- "没进块"和"进了块但走了别的分支"。
	-- 600 帧（60 fps 下 10 秒）足够回答这个问题；120 帧是每秒 2 行，一场长局光心跳就上百 KB。
	local tune = tune_values(runtime)
	-- 调参文件 `heartbeatFrames = N` 优先，其次安装参数，最后 600。
	-- **<= 0 = 关掉心跳**（调参文件里 0 一律表示关，`devLog` / `skirt` 都是这个约定）；
	-- `0 < N < 1` 取整后同样是关——亚帧间隔没有意义。
	-- 不能写成 `math.max(1, …)`：那会把 0 和负数都抬成 1，也就是「每帧一条」，
	-- 一场长局刷出几万行，正好烧光心跳本来要省的那点日志预算。
	local heartbeat = math.floor(tune.heartbeatFrames or options.heartbeatFrames or 600)
	if heartbeat < 1 then heartbeat = 0 end
	if heartbeat > 0 and (runtime.beatFrames % heartbeat) == 0 then
		local chains = 0
		for _, session in ipairs(runtime.sessions) do chains = chains + #session.sessions end
		logger("[physbone] 驱动心跳 unit=%d 链=%d 玩家=%d 分帧=%d/%d 本帧驱动=%d 驱动=%.2f ms dt=%.4f 本帧 写入=%d 跳过=%d 引擎调用=%d 累计/帧=%.0f 静止链=%d 偏离绑定峰值=%.1f°(史上%.1f°) 最离谱=%s(限%.0f°) 同拍重复=%d%s",
			#runtime.sessions, chains, #(runtime.avatars or {}), runtime.nearStride or 1, runtime.farStride or 1, attempted,
			(os.clock() - driveClock) * 1000,
			runtime.dt or 0, written, skippedSame, engineCalls,
			runtime.callsAccum / runtime.driveFrames, staticChains, framePeakDeg,
			runtime.peakAngle or 0,
			worstName ~= nil and tostring(worstName) or "-", worstLimit,
			runtime.repeatCalls or 0, armSuffix)
	end

	-- 逐链明细（默认关）：区分"求解器在动"和"参数算出来就是 0"——后者在画面上就是钢板。
	-- 调参文件 `chainLog = 1` 也能打开（不必重新打包成 debug 版）。
	-- 按**件**列：每件一段「unit 链数 件峰值 + 峰值最大的 3 条链」。旧版只列全场前 6 条链，
	-- 头部那件有 17 条链时，前 6 条全是单节前发，长发根本轮不到。
	local chainLog = options.debug or (tune.chainLog or 0) ~= 0
	-- 间隔同样按 "<= 0 = 关"：不能靠 `n % 0 = nan` 静默失效 —— 那是依赖底层取模行为，
	-- 而且负间隔会变成"反向周期"，语义更说不清。
	local detailEvery = math.floor(options.debugDetailFrames or 240)
	if detailEvery < 1 then detailEvery = 0 end
	if chainLog and detailEvery > 0 and (runtime.beatFrames % detailEvery) == 0 then
		local peak, pieces = 0, {}
		for _, session in ipairs(runtime.sessions) do
			local chainPeaks = {}
			local sessionPeak = 0
			for _, item in ipairs(session.sessions) do
				local rotations = item.lastRotations
				local itemPeak = 0
				if rotations ~= nil then
					for index = 1, #rotations do
						local angle = rotations[index][4] * 180 / math.pi
						if angle > itemPeak then itemPeak = angle end
					end
				end
				if itemPeak > sessionPeak then sessionPeak = itemPeak end
				chainPeaks[#chainPeaks + 1] = {
					name = tostring(item.chain and item.chain[1] and item.chain[1].name or "?"),
					peak = itemPeak,
					live = rotations ~= nil,
				}
			end
			if sessionPeak > peak then peak = sessionPeak end
			table.sort(chainPeaks, function(a, b) return a.peak > b.peak end)
			local top = {}
			for index = 1, math.min(3, #chainPeaks) do
				local entry = chainPeaks[index]
				top[#top + 1] = string.format("%s=%s", entry.name,
					entry.live and string.format("%.1f°", entry.peak) or "未驱动")
			end
			pieces[#pieces + 1] = string.format("%s[%d链 峰%.1f° %s]",
				tostring(session.unitTag or "?"), #session.sessions, sessionPeak, table.concat(top, " "))
		end
		logger("[physbone][debug] 逐件峰值（全体 %.1f°）%s", peak, table.concat(pieces, " "))
	end

	-- ⚠️ 判"驱动失效"不能用"这一帧写了几个骨"：加了"姿态没变就跳过"之后，
	-- 站着不动时 written 天然就是 0，用它判失效会让静止的角色每 45 帧重挂一次（踩过）。
	-- 真正的判据是"求解器有没有在处理链"：写了 + 因没变而跳过 = 处理过的骨数。
	--
	-- 多人分帧之后还要再排除一种假信号：**这一帧按 stride 谁都不该被驱动**。
	-- 那种帧一条骨都不会被处理，但这不是故障——它是节流本身。判据要挂"有没有尝试过"。
	phase(runtime, "写骨 后")
	local handled = written + skippedSame
	if not attemptedAny then
		-- 分帧跳过的帧：不算数，也不清零（清零会让"卡住"永远攒不到上限）
	elseif handled == 0 then
		runtime.zeroFrames = (runtime.zeroFrames or 0) + 1
	else
		runtime.zeroFrames = 0
	end
	local tripped = false
	for _, session in ipairs(runtime.sessions) do
		for _, item in ipairs(session.sessions) do
			if item.disabled == true then tripped = true end
		end
	end
	if runtime.zeroFrames >= (options.zeroLimit or 45) or tripped then
		logger("[physbone][驱动失效] 连续 %d 帧一条骨都没处理（护栏=%s）⇒ 退回等待重挂",
			runtime.zeroFrames, tostring(tripped))
		runtime.sessions = nil
		runtime.zeroFrames = 0
	end

	-- 姿态快照：外部按下热键 ⇒ 写请求文件 ⇒ 这里把骨架采下来。
	-- 放在**物理写完、渲染之前**：采到的就是这一帧画到屏幕上的姿态。
	snapshot_poll(S, runtime, options)

	-- 裙摆外推：用这一帧的姿态算**下一帧**要叠的旋转（1 帧延迟，肉眼不可见）。
	-- 放在 update 之后是因为要用到这一帧写完之后的世界位置。
	phase(runtime, "裙摆 前")
	skirt_apply(S, runtime)
	phase(runtime, "裙摆 后")
end

--- 安装运行时。可重复调用：第二次直接返回同一个实例。
---@param stingray table 引擎命名空间
---@param options table { compat, logPath, debug, archiveId, ... }
---@return table runtime
-- 分项计数的包装（见 Perf）。所有被包装的都是上面已经定义好的局部函数 / 表字段；
-- 局部函数是按上值被别的函数引用的，所以在这里重新赋值，之前定义的调用方也会走包装。
local perfWrapped = false
local function perf_wrap_all()
	if perfWrapped then return end
	perfWrapped = true
	refresh_live_units = Perf.wrap("F1", refresh_live_units)
	refresh_avatar_distances = Perf.wrap("F8", refresh_avatar_distances)
	fp_lock_watch = Perf.wrap("F8", fp_lock_watch)
	scheduler_watch = Perf.wrap("F8", scheduler_watch)
	drive = Perf.wrap("F9", drive)   -- drive 本体剩余部分（时钟、生命周期、门控）：未归入子项的调用都记在这里
	sched_poll = Perf.wrap("F9", sched_poll)
	independent_frame = Perf.wrap("F4", independent_frame)
	apply_arm_pose_frame = Perf.wrap("F4", apply_arm_pose_frame)
	sample_action_state = Perf.wrap("F5", sample_action_state)
	frame_animation_machine = Perf.wrap("F5", frame_animation_machine)
	independent_scan = Perf.wrap("P3", independent_scan)
	IndependentLink.find_weapon = Perf.wrap("F3", IndependentLink.find_weapon)
	IndependentLink.physics = Perf.wrap("F6", IndependentLink.physics)
	IndependentLink.present = Perf.wrap("F4p", IndependentLink.present)
	IndependentLink.attach = Perf.wrap("F4a", IndependentLink.attach)
	IndependentLink.held_weapon = Perf.wrap("F3", IndependentLink.held_weapon)
	IndependentLink.action = Perf.wrap("F5", IndependentLink.action)
	M.independent_physics_ = IndependentLink.physics
end

function M.install(stingray, options)
	local existing = rawget(_G, INSTALL_KEY)
	if existing ~= nil then return existing end

	S = stingray
	options = options or {}
	local compat = options.compat
	if compat == nil then
		-- 外置模式下加载器可能把 compat 作为同名模块提供；找不到就明确报错，
		-- 而不是静默什么都不做（静默是上一轮排查最久的一类问题）。
		error("physbone runtime: 缺少 physbone_compat 模块（install 的 options.compat）", 2)
	end
	Compat = compat

	local logger, logContext = makeLogger(options)
	-- 分项计数：默认开（用户下次实机要一次拿全分项）；`perfCounters = false` 关。
	Roster.reset()
	Roster.negativeSeconds = tonumber(options.rosterNegativeSeconds) or 0.25   -- IAS 行 1553；仅测试用选项可调
	Roster.pollInterval = tonumber(options.identityPollSeconds) or 0.25   -- IAS：4 Hz（行 4747）；仅测试用选项可调
	Perf.reset()
	Perf.on = options.perfCounters ~= false
	if Perf.on then
		Perf.window = math.floor(tonumber(options.perfWindow) or 600)
		Perf.instrument(S)
		for _, name in ipairs({ "update", "attach", "session_health", "unit_alive" }) do
			if type(Compat[name]) == "function" and not rawget(Compat, "__perf_" .. name) then
				local bucket = (name == "update" and "F6") or (name == "attach" and "P7") or "F7"
				Compat[name] = Perf.wrap(bucket, Compat[name])
				rawset(Compat, "__perf_" .. name, true)
			end
		end
		perf_wrap_all()
		local rawLogger = logger
		logger = function(format, ...)
			if type(format) == "string" then
				if format:find("有新玩家", 1, true) then Perf.event("P4") end
				if format:find("跳过 %d 个离它所属玩家", 1, true) or format:find("跳过 %d", 1, true) and format:find("尸体", 1, true) then Perf.event("P5") end
				if format:find("玩家动它不动", 1, true) or format:find("没有跟着动", 1, true) then Perf.event("P6") end
				if format:find("[独立骨] 清单", 1, true) then Perf.event("P3log") end
			end
			return rawLogger(format, ...)
		end
	end

	state = {
		options = options,
		iasPhysicsGroups = math.floor(tonumber(options.iasPhysicsGroups) or 0) % 16,
		logger = logger,
		logContext = logContext,
		cache = {},
		carriers = nil,
		sessions = nil,
		-- 场上的玩家 avatar 名单（每个玩家一项）。会话按 `session.owner` 归属到其中一项上，
		-- 名单变化只影响"补挂/收尾"，不会推翻别人的会话。
		avatars = nil,
		nearStride = 1,      -- 6 m 内队友的分帧步长（1 = 每帧）
		farStride = 1,       -- 6 m 外队友的分帧步长
		frame = 0,
		dt = options.dt or (1 / 60),
		driveFrames = 0,
		beatFrames = 0,
		callsAccum = 0,
		zeroFrames = 0,
		waitReason = nil,
		waitRepeat = 0,
		installed = true,
	}
	rawset(_G, INSTALL_KEY, state)

	local driver = state
	originalRender = rawget(_G, "render")
	rawset(_G, "render", function(...)
		-- 先让原来的 render 跑完（别的 mod 可能也在包装），再写我们的姿态：
		-- 这样引擎已经算完动画、还没画，我们写进去的 local pose 会直接用于本帧蒙皮。
		local result
		if driver.weaponShift then Perf.active = Perf.on; IndependentLink.restore_weapon(driver); Perf.active = false end
		if originalRender ~= nil then result = originalRender(...) end
		if driver.installed then
			driver.frame = driver.frame + 1
			local calls0 = Perf.totalCalls
			Perf.cur = nil
			Perf.active = Perf.on
			local ok, err = pcall(drive, driver)
			Perf.active = false
			if Perf.on then
				Perf.cur = nil
				Perf.frames = Perf.frames + 1
				local spent = Perf.totalCalls - calls0
				if spent > (Perf.events.maxFrameCalls or 0) then Perf.events.maxFrameCalls = spent end
				if Compat.perf_resync then Perf.events.P1 = Compat.perf_resync end
				if Perf.frames >= Perf.window then
					pcall(Perf.summary, driver)
					local keepResync = Perf.events.P1
					Perf.reset()
					Perf.events.P1 = keepResync
				end
			end
			driver.frameAnimation = nil
			if not ok and driver.weaponShift then IndependentLink.restore_weapon(driver) end
			if not ok and not driver.driveErrorLogged then
				driver.driveErrorLogged = true
				driver.logger("[physbone][错误] 驱动失败：%s", tostring(err))
			end
		end
		return result
	end)

	logger("[physbone] 运行时 %s 已安装（%s）启动于 %s",
		M.VERSION,
		options.mode == "boot" and "独占 boot" or "外置模块",
		os.date("%Y-%m-%d %H:%M:%S"))
	if options.iasPhysicsGroups ~= nil then
		logger("[physbone] IAS 可选组掩码=%d（刘海/呆毛=1 挂件=2 胸部=4 臀部=8）", state.iasPhysicsGroups)
	end
	if options.armPose == true then
		local legs = Compat.leg_joint_targets ~= nil and Compat.leg_joint_targets(options.legJointTarget) or nil
		logger("[physbone] 姿态修正已开：肩目标=%s 腿目标=%s 独立骨=%s",
			Compat.arm_shoulder_targets(options.armShoulderTarget) ~= nil and "有" or (options.armShoulderTarget ~= nil and "无效" or "无"),
			legs ~= nil and (#legs .. " 根") or (options.legJointTarget ~= nil and "无效" or "无"),
			type(options.independentBones) == "table" and (#options.independentBones .. " 根") or "无")
	end
	return state
end

--- 卸载：把 render 还回去，并尽量把姿态交还给引擎。
function M.uninstall()
	local runtime = state
	if runtime == nil then return false end
	if not IndependentLink.restore_weapon(runtime) then return false end
	runtime.installed = false
	IndependentLink.release_all(runtime)
	for _, session in ipairs(runtime.sessions or {}) do
		pcall(Compat.release, session)
	end
	runtime.sessions = nil
	rawset(_G, "render", originalRender)
	if runtime.weaponUpdate and rawget(_G, "update") == runtime.weaponUpdate then
		rawset(_G, "update", runtime.originalWeaponUpdate)
	end
	rawset(_G, INSTALL_KEY, nil)
	state = nil
	return true
end

--- 运行中改参数（热调）。
function M.setOptions(patch)
	if state == nil or type(patch) ~= "table" then return false end
	for key, value in pairs(patch) do state.options[key] = value end
	return true
end

M.install_ = M.install
M.perf_ = Perf
M.roster_ = Roster
M.set_engine_ = function(engine) S = engine end   -- 仅测试：不经 install 直接给 Roster 一个引擎

-- 测试钩子：抬升探针的两块纯逻辑（读调参文件 / 给一个 unit 加偏移）。
-- 单独暴露出来，是因为"不累加、引擎重设后能重写、关掉时能还原"这三条
-- 只要错一条就会让角色每帧往上飞 —— 这种判据必须能被单测直接钉住，
-- 而不是靠进游戏看。
M.tune_values_ = tune_values
M.lift_unit_ = lift_unit
M.lift_bone_ = lift_bone
M.apply_lift_ = apply_lift

-- 测试钩子：日志闸门（轮转 + 单次启动预算 + 用尽后只留错误类）。
-- 这三条判据都只能看**落盘结果**，所以必须能直接造一个 logger 往临时文件里写，
-- 而不是靠进游戏观察。
M.make_logger_ = makeLogger

-- 测试钩子：姿态快照。解析、拼 JSON、轮询三段都能离线钉住——
-- 否则"按了键但什么都没写出来"这种问题只能靠用户再进一次游戏才发现。
M.snapshot_parse_ = parse_snapshot_request
M.snapshot_ids_ = snapshot_ids
M.snapshot_json_ = snapshot_json
M.arm_pose_enabled_ = arm_pose_enabled
M.apply_arm_pose_frame_ = apply_arm_pose_frame
M.independent_physics_ = IndependentLink.physics
M.independent_contact_ = IndependentLink
M.snapshot_poll_ = snapshot_poll
M.snapshot_dir_ = snapshot_dir

-- 测试钩子：调度器。重排与兜底都是纯逻辑（给定相机和一堆位置就能算），
-- 所以可以离线钉住"位置互换后归属必须跟着换""限流必须挡住连续触发"。
M.redistribute_ = redistribute
M.scheduler_watch_ = scheduler_watch
M.sched_poll_ = sched_poll
M.sched_settings_ = sched_settings
M.schedule_budget_ = schedule_budget
M.key_edge_ = key_edge
M.fp_probe_poll_ = fp_probe_poll
M.fp_lock_watch_ = fp_lock_watch
M.probe_identity_link_ = probe_identity_link

return M









