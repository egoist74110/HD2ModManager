-- 多人局调度层离线测试（luajit 跑，不需要游戏）。
--
-- 为什么值得写：这一层改的是"谁该有物理"，而它的失败现象全都**没有报错**——
-- 只有一个人有头发飘动、或者所有人闪一下没有物理。等实机发现，代价是一轮"重启游戏试试"。
-- 这个壳把 S（引擎命名空间）和 Compat（求解器）都换成假的，于是"4 个人各自挂上"
-- "中途有人加入""有人退出"这些只能在多人局里出现的情形，可以在这台机器上逐条断言。
--
-- 跑法：luajit HD2PhysBoneCompat/test/mp_harness.lua

local here = arg and arg[0] and arg[0]:gsub("[^/\\]*$", "") or ""
package.path = here .. "../src/?.lua;" .. package.path

local runtimeModule = dofile(here .. "../src/physbone_runtime.lua")
-- 真求解器库：假求解器只替换"会话"那几个入口；尸体定格读 / 写节点用的是真库里的节点读写函数。
local realCompat = dofile(here .. "../src/physbone_compat.lua")

local failures, checks = 0, 0
local function check(condition, message, detail)
	checks = checks + 1
	-- 输出统一成别的 harness 那套 `PASS` / `FAIL` 行：跑套件时要能被
	-- tools/physbone-harness/run_physbone_regressions.py 数到，否则汇总里这一整
	-- 文件看起来是 0 项检查。
	if not condition then
		failures = failures + 1
		print(string.format("  %-4s %s%s", "FAIL", message, detail ~= nil and ("  [" .. tostring(detail) .. "]") or ""))
	else
		print(string.format("  %-4s %s", "PASS", message))
	end
end

-- ---------------------------------------------------------------- 假引擎
local function vector(x, y, z) return { x = x, y = y, z = z } end

local Fake = {}
Fake.__index = Fake

local function newFake()
	local self = setmetatable({ units = {}, nextIndex = 1, clock = 0, hashes = {}, nextHash = 0 }, Fake)
	-- 噪声 unit：节点数不够，必须被判"不是目标"，用来确认扫描没有把所有人都当候选。
	for _ = 1, 40 do self:addUnit({ nodes = 10 }) end
	return self
end

-- 每件转换后的护甲有一个 16 位十六进制资源 id（IAS 的 profile 哈希）；名单在 install 时就定死。
function Fake:newHash()
	self.nextHash = self.nextHash + 1
	local hash = string.format("%016x", 0xa5000000 + self.nextHash)
	self.hashes[#self.hashes + 1] = hash
	return hash
end

function Fake:addUnit(spec)
	local unit = {
		hash = spec.armour and (spec.hash or self:newHash()) or nil,
		id = self.nextIndex,
		nodes = spec.nodes or 110,
		alive = true,
		avatarResource = spec.avatar or false,
		anim = spec.anim ~= false,
		mg = spec.mg ~= false,
		position = spec.position or vector(0, 0, 0),
		chains = spec.chains or 6,
		name = spec.name or ("unit" .. self.nextIndex),
	}
	self.nextIndex = self.nextIndex + 1
	self.units[#self.units + 1] = unit
	return unit
end

function Fake:removeUnit(unit)
	for index = #self.units, 1, -1 do
		if self.units[index] == unit then table.remove(self.units, index) end
	end
	unit.alive = false
end

function Fake:playerAt(name, x, chains, hash)
	local avatar = self:addUnit({ name = name .. "-avatar", avatar = true, position = vector(x, 0, 0) })
	local armor = self:addUnit({ name = name .. "-armor", anim = false, mg = true, position = vector(x + 0.1, 0, 0), chains = chains,
		armour = true, hash = hash })
	return avatar, armor
end

function Fake:stingray(squad)
	local self_ = self
	local id32 = function(text) return text end
	local Unit = {
		alive = function(unit) return unit ~= nil and unit.alive == true end,
		num_scene_graph_items = function(unit) return unit.nodes end,
		has_animation_state_machine = function(unit) return unit.anim end,
		has_node = function(unit, _) return unit.mg end,
		world_position = function(unit, _) return unit.position end,
		-- 真引擎的名字里带资源 id（16 位十六进制）；非护甲 unit 没有。
		debug_name = function(unit) return "#ID[" .. (unit.hash or tostring(unit.id)) .. "]" end,
		-- 尸体定格（默认开）要读的节点：StingrayEntityRoot=1、hips=2、head=3；世界姿态 = 单位位置 + 0.3 m * 节点号（头与 hips 隔 0.3 m，
		-- 满足 core 对"头相对 hips 的轴"的 0.1~2 m 条件），旋转恒为单位阵。
		node = function(_, name) if name == "StingrayEntityRoot" then return 1 elseif name == "hips" then return 2 elseif name == "head" then return 3 end end,
		world_pose = function(unit, node) return { unit = unit, node = node } end,
		-- 以下只为让 CorpseFreeze 的 API 可用性检查（R:5461-5470）通过；战斗场景里没人死，不会被真正调用。
		name_hash = function(unit) return unit.hash or tostring(unit.id) end,
		set_unit_visibility = function() end,
		scene_graph_link = function() end,
		scene_graph_parent = function() end,
	}
	local World = {
		units = function(_) return self_.units end,
		num_units = function(_) return #self_.units end,
		units_by_resource = function(_, resource)
			local found = {}
			for index = 1, #self_.units do
				local unit = self_.units[index]
				if unit.alive then
					if type(resource) == "table" then
						-- IdString64.from_hex(哈希) 的键：按护甲的资源 id 匹配。
						if unit.hash ~= nil and resource.hex == unit.hash then found[#found + 1] = unit end
					elseif unit.avatarResource and resource == "content/fac_helldivers/cha_avatar/avatar_helldiver" then
						found[#found + 1] = unit
					end
				end
			end
			return found
		end,
		-- 相机固定在原点：于是 (0,0,0) 那个玩家就是"自己"（第一位）。
		debug_camera_pose = function(_) return { pose = true } end,
		update_unit = function() end,
		spawn_unit = function() end,
		destroy_unit = function() end,
		unlink_unit = function() end,
	}
	local engine = {
		Application = {
			worlds = function() return { 1 } end,
			string_to_id32 = id32,
			time_since_launch = function() return self_.clock end,
		},
		World = World,
		Unit = Unit,
		IdString64 = { from_hex = function(hex) return { hex = hex } end },
		Vector3 = { to_elements = function(v) return v.x, v.y, v.z end },
		Matrix4x4 = {
			translation = function(_) return vector(0, 0, 0) end,
			to_elements = function(m)
				local p = m.unit.position
				return 1, 0, 0, 0, 1, 0, 0, 0, 1, p.x, p.y, p.z + 0.3 * (m.node or 0)
			end,
		},
	}
	if squad ~= nil then
		-- 战斗局身份：Network / GameSession（IAS physics_identity 的数据来源）。
		-- squad.objects[id] = { unit = avatar 的 unit, owner = peer 名 }；对象位置跟随 avatar，血量 100。
		local function ownedBy(peer)
			local out = {}
			for id, object in pairs(squad.objects) do if object.owner == peer then out[#out + 1] = id end end
			table.sort(out)
			return out
		end
		local members = {}
		for _, object in pairs(squad.objects) do members[#members + 1] = object.owner end
		table.sort(members)
		engine.Network = { game_session = function() return 1 end, peer_id = function() return squad.me end }
		engine.GameSession = {
			in_session = function() return true end,
			objects_owned_by = function(_, peer) return ownedBy(peer) end,
			game_object_exists = function(_, id) return squad.objects[id] ~= nil end,
			game_object_is_type = function() return true end,
			game_object_owner = function(_, id) return squad.objects[id] and squad.objects[id].owner end,
			game_object_field = function(_, id, field)
				local object = squad.objects[id]
				if field == "health" then return object.health or 100 end
				return object.unit.position
			end,
			peers = function() return members end,
			other_peers = function() return members end,
		}
	end
	return engine
end

function Fake:advance(frames, step)
	step = step or 0.01
	for _ = 1, frames do
		self.clock = self.clock + step
		rawget(_G, "render")()
	end
end

-- ---------------------------------------------------------------- 假求解器
local function newCompat()
	local compat = { attached = {}, released = {}, dtByUnit = {}, calls = {} }
	compat.unit_alive = function(_, unit) return unit ~= nil and unit.alive == true end
	for _, name in ipairs({ "independent_world_pose", "independent_rotation_object", "write_independent_rotation", "write_independent_position" }) do
		compat[name] = realCompat[name]
	end
	compat.begin_frame = function() end
	compat.filter_ias_groups = function(rig) return rig, 0 end
	compat.select_rig = function(_, units, _)
		local unit = units[1]
		return { rig = { bones = { { name = "hair" } } }, path = "data/fake.gpu_resources", full = true, score = 1, total = #unit.name }
	end
	-- 便宜预筛（runtime 会先问它"有没有可能全命中"再决定要不要真打分）。
	-- 这个壳里所有候选都是"能命中"的，所以一律放行。
	compat.probe_carriers = function() return true end
	compat.attach = function(_, unit, _, _)
		local session = {
			unit = unit,
			S = nil,
			sessions = {},
			logger = function() end,
			engineCalls = 0,
			skippedSame = 0,
		}
		for index = 1, unit.chains do
			session.sessions[#session.sessions + 1] = { chain = { { name = "c" .. index } }, staticFrames = 0 }
		end
		compat.attached[#compat.attached + 1] = unit.name
		return session
	end
	compat.update = function(session, dt, _)
		compat.dtByUnit[#compat.dtByUnit + 1] = { unit = session.unit.name, dt = dt }
		session.engineCalls = 2 * #session.sessions
		session.skippedSame = 1
		return 1
	end
	compat.release = function(session)
		compat.released[#compat.released + 1] = session.unit.name
		return 1
	end
	compat.session_health = function(session)
		if session.unit == nil or session.unit.alive ~= true then return "dead" end
		return "ok"
	end
	return compat
end

-- ---------------------------------------------------------------- 场景
local function freshRuntime(fake, compat, options)
	rawset(_G, "__hd2physbone_runtime", nil)
	rawset(_G, "render", function() end)
	local opts = {
		compat = compat,
		log = false,
		carriers = { { rig = { bones = { { name = "hair" } } }, path = "fake" } },
		profileHashes = fake.hashes,
		settleFrames = 1, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
		validateFrames = 3,
		heartbeatFrames = 1000000,
		minNodes = 90,
	}
	for key, value in pairs(options or {}) do opts[key] = value end
	runtimeModule.install(fake:stingray(), opts)
	return rawget(_G, "__hd2physbone_runtime")
end

local function sessionsOf(runtime)
	local list = {}
	for _, session in ipairs(runtime.sessions or {}) do
		list[#list + 1] = { unit = session.unit.name, owner = session.owner ~= nil and session.owner.name or nil, order = session.ownerOrder }
	end
	table.sort(list, function(a, b) return a.unit < b.unit end)
	return list
end

local function sessionUnits(runtime)
	local set = {}
	for _, session in ipairs(runtime.sessions or {}) do set[session.unit] = session end
	return set
end


-- ================================================================ E12 战斗场景：世界里 2000 个 unit
-- 目的：量"与世界 unit 数成正比"的几项（F1 活体列表、F2 判活、F3 找武器）每帧的引擎调用数。
-- 这份用例在改动前后的运行时上都能跑：数字来自假引擎自己的计数器，不依赖运行时里的 Perf。

local function counted(fake, squad)
	local S = fake:stingray(squad)
	local counter = { total = 0, by = {} }
	for _, space in ipairs({ "Unit", "World", "Application", "Matrix4x4", "Vector3", "Network", "GameSession" }) do
		for key, fn in pairs(S[space] or {}) do
			if type(fn) == "function" then
				S[space][key] = function(...)
					counter.total = counter.total + 1
					local name = space .. "." .. key
					counter.by[name] = (counter.by[name] or 0) + 1
					return fn(...)
				end
			end
		end
	end
	return S, counter
end

-- ---------------------------------------------------------------- IAS 1.6.4 预算
-- 用户硬规则：我们的引擎调用不得超过 IAS 在同一场景里的调用数。门槛不再是拍脑袋的"每帧 < 40"，
-- 而是按 IAS 的规则把同一场景的调用数算出来（行号均指
-- tmp/ias-lab/upstream/ias-1.6.4/Generator/runtime/lua/lua/runtime.lua，口径见
-- docs/agent/ias-reference-roster-physics-corpse-identity-20261123.md）。
--
-- 场景形状：W=1 个世界，P 个 avatar（P=4），K 个 profile（每人一件护甲，K=A=4），世界共 U 个 unit，
-- 每件护甲 2 个根探针（{1,0}，R:6702-6704）。只数引擎调用
-- （Unit/World/Application/Matrix4x4/Vector3/Network/GameSession）。
--
-- 常规帧（每个渲染回调）：
--   1            Application.worlds                               R:1345
--   1            World.num_units                                  R:1363
--   1            World.units_by_resource(avatar 资源)             R:1365
--   2            World.units_by_resource(两个场景源 hash)         R:1373（IAS 独有，我们没有）
--   1            World.units_by_resource(fp_cha_avatar)           R:441 经 R:1383（IAS 独有，我们没有）
--   3            相机：World.debug_camera_pose + Matrix4x4.translation + Vector3.to_elements（每世界每帧，R:1335-1340，用于 R:6600）
--   3P           每个 avatar：num_scene_graph_items 1 + world_pose 2（Unit.world_pose + Matrix4x4.to_elements）
--                                                                 R:6621、R:6626、R:385-386
--   K+A          每个到期 profile：units_by_resource 1（上一帧有命中 => 每帧都到期，R:1537-1538、R:1543）
--                + 对每个返回的 unit 调 debug_name 1（R:1553）
--   5A           每件护甲：num_scene_graph_items 1（R:6697）+ 2 个探针 x world_pose 2（R:6707）
-- 身份轮询（4 Hz，R:4853 `next_poll = now + .25`），squad 已验证后
-- （R:4794-4846 squad_discovery_allowed 通过，R:4909 起的远端枚举和低位扫描都被跳过）：
--   4            Network.game_session、GameSession.in_session、Network.peer_id、GameSession.objects_owned_by   R:4857-4874
--   2+8P+3       squad_discovery_allowed：peers/other_peers 各 1；每个 avatar 8 次
--                （Unit.alive、has_animation_state_machine、game_object_exists、is_type、owner、health、
--                  position 字段、Vector3.to_elements）R:4794-4840；末尾 game_session/peer_id/in_session 3 次
--   5P           已知对象刷新：每个 id exists、is_type、position 字段 + to_elements、health  R:4973-4983、R:4889-4890
--   2P           live 循环：每个 avatar Unit.alive + has_animation_state_machine            R:4987-4992
-- 全量 World.units 兜底扫描：1 + U（World.units 1 次 + 对每个 unit 调 debug_name，R:1576、R:1610）
--   战斗世界（有角色 1/2）：每 2.0 s 一次（R:1523、R:1644）；非战斗世界：每 60 帧一次（R:1516）。
local function iasBudget(shape)
	local P, K, A, U = shape.avatars, shape.profiles, shape.armour, shape.units
	local ordinary = 1 + 1 + 1 + 2 + 1 + 3 + 3 * P + (K + A) + 5 * A
	local identity = shape.gameplay and (4 + (2 + 8 * P + 3) + 5 * P + 2 * P) or 0
	local scan = 1 + U
	local seconds = shape.frames / shape.hz
	-- 次数取"窗口内可能出现的最大次数"（与相位无关的上界）。
	local polls = shape.gameplay and (math.floor(seconds / 0.25) + 1) or 0
	local scans = shape.gameplay and (math.floor(seconds / 2.0) + 1) or (math.floor(shape.frames / 60) + 1)
	return {
		ordinary = ordinary, identity = identity, scan = scan, polls = polls, scans = scans,
		total = shape.frames * ordinary + polls * identity + scans * scan,
		peak = ordinary + identity + scan,
	}
end

-- ---------------------------------------------------------------- IAS 1.6.4 尸体定格开着时的每帧开销（战斗世界、活人阶段）
-- 用户规则：尸体定格按用户实际运行的默认（开）来测，成本不得超过 IAS 同场景。下面把 IAS 在这个场景里
-- **为尸体定格付的**引擎调用逐项列出（只数 Lua 侧的 Unit/World/Network/GameSession/Matrix4x4 调用；
-- 原生 ABI `api.corpse_freeze_context` / `corpse_freeze_enabled`、`shared_next_incarnation` 等不是 Lua 引擎 API，不计）。
-- 行号同上，指 runtime.lua：
--   L  共享身体化身号证据（每帧，不论 corpse_freeze 开关）：R:6917-6919 每个回调对在场 avatar 调
--      `__hd2ir_shared_body_lifecycle.observe`（R:5190-5246，本体零引擎调用）→ evidence = `physics_identity.shared_life`
--      （R:5097-5119）：role 1/2 且未死且有网络号才往下读：Unit.alive R:5099-5100；role 2 另加 has_animation_state_machine R:5104-5105；
--      Network.game_session R:5108；GameSession.in_session R:5109；game_object_exists R:5110；game_object_field(health) R:5111。
--      => role 1：5 次；role 2：6 次。role 0（非战斗世界）/ role 3（死者）在 R:5099 直接返回，0 次。
--      （`identity().role` R:5180 只读 Lua 表，0 次；presentation_lives/worlds R:5139-5170 在单个世界 `#contexts<2` 时 R:5145-5146 直接返回。）
--   C  `core(avatar)`（R:5472-5493）：F.observe（R:5602）在 role 1/2/3 时（R:5605-5606）、**先于任何采样节流**（R:5614 的 bucket 判断）就调（R:5607），
--      每个 avatar 每帧一次（`cores` 每帧在 F.begin R:5546 清空、R:5474 命中缓存）：
--      Unit.alive、has_node('hips')、node('hips')、num_scene_graph_items 各 1（R:5475-5477），world_pose 2
--      （Unit.world_pose + Matrix4x4.to_elements，R:430、R:384-386；R:5479），has_node('head')、node('head') 各 1（R:5482-5483），
--      world_pose(head) 2（R:5485）=> 10 次 / avatar / 帧。
--   A  F.observe 对活体（role 1/2）每个 binding 每帧一次 Unit.has_animation_state_machine（R:5613，在 R:5614 节流之前）。
--   S  采样：每 0.25 s 一个桶（R:5614 `bucket=floor(now*4)`、`old.bucket==bucket` 则跳过；首次 old==nil 立刻采），
--      每个 binding 整件 capture（R:5525-5544）：每个节点 world_pose 2 次 => node_count*2 次 / binding / 桶。
--   F.flush（R:5850）对活体全是纯 Lua（`ready[key]` 为空 => 'incomplete corrected group' R:5890-5893，不碰引擎）。
-- 没有 IAS 对应物、本表不计的项（只会让 IAS 更大）：F.control_context（R:5555-5588，每帧：舰上场景的 alive/has_asm 读取）。
local function iasCorpseBudget(shape)
	local life, core, asm = 0, 0, 0
	for _, role in ipairs(shape.roles) do
		if role == 1 then life = life + 5 end
		if role == 2 then life = life + 6 end
		if role == 1 or role == 2 or role == 3 then core = core + 10 end
		if role == 1 or role == 2 then asm = asm + shape.pieces end
	end
	local sample = 0
	for _, role in ipairs(shape.roles) do
		if role == 1 or role == 2 then sample = sample + shape.pieces * shape.nodes * 2 end
	end
	local seconds = shape.frames / shape.hz
	local buckets = (life + core + asm) > 0 and (math.floor(seconds * 4) + 1) or 0   -- 次数取窗口内可能出现的最大次数
	local aliveReads = 0
	for _, role in ipairs(shape.roles) do
		if role == 1 or role == 2 then aliveReads = aliveReads + 2 end   -- L 的 Unit.alive + C 的 Unit.alive
	end
	return {
		life = life, core = core, asm = asm, sample = sample, buckets = buckets, aliveReads = aliveReads,
		perFrame = life + core + asm,
		total = shape.frames * (life + core + asm) + buckets * sample,
		peak = life + core + asm + sample,
	}
end

-- 尸体定格模块自己的引擎调用（只统计这四个入口里的调用：begin / 化身号簿记 / filter / 逐帧采样与 flush）。
local function wrapCorpse(counter)
	local freeze = runtimeModule.corpse_
	local meter = { frame = 0, total = 0, peak = 0, raw = {} }
	for _, name in ipairs({ "begin", "lifecycleObserve", "filter", "frame" }) do
		local raw = freeze[name]
		meter.raw[name] = raw
		freeze[name] = function(...)
			local before = counter.total
			local a, b = raw(...)
			meter.frame = meter.frame + (counter.total - before)
			return a, b
		end
	end
	function meter.endFrame()
		meter.total = meter.total + meter.frame
		meter.peak = math.max(meter.peak, meter.frame)
		meter.frame = 0
	end
	function meter.restore() for name, raw in pairs(meter.raw) do freeze[name] = raw end end
	return meter
end

local function combatScenario(label, gameplay)
	io.stdout:write(string.format("\n[E12 %s] 世界 15000 个 unit + 4 名玩家\n", label))
	local fake = newFake()
	for _ = 1, 14960 do fake:addUnit({ nodes = 10 }) end
	local compat = newCompat()
	local players = {}
	for index = 1, 4 do
		local avatarUnit = fake:playerAt("p" .. index, (index - 1) * 10)
		players[index] = avatarUnit
	end
	local squad = nil
	if gameplay then
		-- 4 人小队：本人 p1 + 3 名队友，网络对象与 avatar 位置重合（IAS 行 5015-5016 的 0.05 m 直连条件）。
		squad = { me = "p1", objects = {} }
		for index, avatarUnit in ipairs(players) do
			squad.objects[index == 1 and 5000 or (7000000 + index)] = { unit = avatarUnit, owner = "p" .. index }
		end
	end
	local S, counter = counted(fake, squad)
	rawset(_G, "__hd2physbone_runtime", nil)
	rawset(_G, "render", function() end)
	-- Roster 用 os.clock() 做 2.0 s / 4 Hz / 0.25 s 节流（和 IAS 一样）。测试里帧只隔几毫秒真实时间，
	-- 所以把 os.clock 换成假引擎的时钟（120 Hz，和实机回调频率一致）；耗时统计用真时钟。
	local realClock = os.clock
	local hz = 120
	os.clock = function() return fake.clock end
	local Roster = runtimeModule.roster_
	local rosterCalls, rawUpdate = 0, Roster.update
	Roster.update = function(...)
		local before = counter.total
		local out = rawUpdate(...)
		rosterCalls = rosterCalls + (counter.total - before)
		return out
	end
	runtimeModule.install(S, { compat = compat, log = false, carriers = { { rig = { bones = { { name = "hair" } } }, path = "fake" } },
		profileHashes = fake.hashes, settleFrames = 1, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt", pollFrames = 1, validateFrames = 3,
		heartbeatFrames = 1000000, minNodes = 90, perfWindow = 1000000 })   -- 尸体定格按用户实际运行的默认（开）测，不关
	local runtime = rawget(_G, "__hd2physbone_runtime")
	local function frame()
		fake.clock = fake.clock + 1 / hz
		rawget(_G, "render")()
	end
	for _ = 1, 240 do frame() end   -- 暖机 2 s：挂上、身份验证、首次全量扫描
	check(#(runtime.sessions or {}) == 4, label .. "：玩家护甲正常挂上", #(runtime.sessions or {}))
	local roles = {}
	for index, avatarUnit in ipairs(players) do roles[index] = Roster.role(avatarUnit) end
	if gameplay then
		check(roles[1] == 1 and roles[2] == 2 and roles[3] == 2 and roles[4] == 2,
			label .. "：本人角色 1、队友角色 2（IAS 战斗世界判据 R:6672-6678）", table.concat(roles, ","))
	else
		check(roles[1] == 0 and roles[4] == 0, label .. "：没有网络身份，角色全是 0（非战斗世界）", table.concat(roles, ","))
	end
	counter.total, counter.by, rosterCalls = 0, {}, 0
	local frames = 1200
	local corpse = wrapCorpse(counter)
	check(runtimeModule.corpse_.enabled == true, label .. "：尸体定格默认开着（按用户实际运行的配置测，不关开关）", runtimeModule.corpse_.enabled)
	local c0 = realClock()
	local peakRoster, peakTotal = 0, 0
	for _ = 1, frames do
		local beforeTotal, beforeRoster = counter.total, rosterCalls
		frame()
		corpse.endFrame()
		peakTotal = math.max(peakTotal, counter.total - beforeTotal)
		peakRoster = math.max(peakRoster, rosterCalls - beforeRoster)
	end
	corpse.restore()
	local elapsed = realClock() - c0
	local perFrame = counter.total / frames
	local rosterPerFrame = rosterCalls / frames
	io.stdout:write(string.format("  [数据] 稳态每帧引擎调用 %.2f（其中枚举/归属/身份 %.2f，其余 %.2f 为调度与求解侧）；峰值帧 %d（枚举侧 %d）；World.units 每帧 %.3f 次；1200 帧 Lua 耗时 %.1f ms\n",
		perFrame, rosterPerFrame, perFrame - rosterPerFrame, peakTotal, peakRoster, (counter.by["World.units"] or 0) / frames, elapsed * 1000))
	local keys = {}
	for key in pairs(counter.by) do keys[#keys + 1] = key end
	table.sort(keys)
	for _, key in ipairs(keys) do io.stdout:write(string.format("    %-34s %.2f/帧\n", key, counter.by[key] / frames)) end
	local units = #fake.units
	local budget = iasBudget({ avatars = 4, profiles = #fake.hashes, armour = 4, units = units, frames = frames, hz = hz, gameplay = gameplay })
	-- 尸体定格开着的 IAS 项（见 iasCorpseBudget 的逐项来源）。L 在 IAS 里不受开关控制，所以进总预算；C/A/S 受 corpse_freeze_enabled 控制，
	-- 默认开（native_bridge/preferences.py:20 freeze=True），也进总预算。
	local roleList = {}
	for index in ipairs(players) do roleList[index] = roles[index] end
	local cb = iasCorpseBudget({ roles = roleList, pieces = 1, nodes = 110, frames = frames, hz = hz })
	budget.corpse = cb
	budget.total = budget.total + cb.total
	budget.peak = budget.peak + cb.peak
	io.stdout:write(string.format("  [IAS 尸体项] L(化身号证据)=%d C(core)=%d A(has_asm)=%d => %d/帧；采样帧 +%d x %d 桶；合计 %d / %d 帧 = %.2f/帧；峰值帧 %d\n",
		cb.life, cb.core, cb.asm, cb.perFrame, cb.sample, cb.buckets, cb.total, frames, cb.total / frames, cb.peak))
	io.stdout:write(string.format("  [我们 尸体项] 尸体定格模块合计 %d / %d 帧 = %.2f/帧；峰值帧 %d\n", corpse.total, frames, corpse.total / frames, corpse.peak))
	io.stdout:write(string.format("  [IAS 预算] 常规帧 %d + 身份 %d x %d 次 + 全量扫描 (1+%d) x %d 次 = %d 次 / %d 帧 = %.2f/帧；峰值帧 %d\n",
		budget.ordinary, budget.identity, budget.polls, units, budget.scans, budget.total, frames, budget.total / frames, budget.peak))
	local perf = runtimeModule.perf_
	if perf ~= nil then
		local rows = {}
		for name, calls in pairs(perf.calls) do rows[#rows + 1] = string.format("%s=%.1f", name, calls / math.max(1, perf.frames)) end
		table.sort(rows)
		io.stdout:write("  [分项] " .. table.concat(rows, " ") .. "\n")
	end
	-- 门槛：我们的"枚举 + 归属 + 身份"调用数不得超过 IAS 同场景的总预算；峰值帧（全量扫描 + 身份轮询同帧）
	-- 同样不得超过 IAS 的峰值帧。
	check(rosterCalls <= budget.total, label .. "：枚举/归属/身份调用数 <= IAS 预算", string.format("%d <= %d", rosterCalls, budget.total))
	check(peakRoster <= budget.peak, label .. "：峰值帧（全量扫描帧）<= IAS 峰值帧", string.format("%d <= %d", peakRoster, budget.peak))
	-- 整帧口径：枚举侧之外，调度（相机/到相机距离）与 Lua 求解器自己的调用在 IAS 里没有对应物
	-- （IAS 的求解在原生 DLL 里），但整帧合计仍不得超过 IAS 预算——否则就是我们多付了 IAS 没付的钱。
	check(counter.total <= budget.total, label .. "：整帧合计调用数 <= IAS 预算", string.format("%d <= %d", counter.total, budget.total))
	-- 峰值帧按 IAS 峰值帧 + "没有 IAS 对应物的求解侧调用"：Application.time_since_launch 1 次（Lua 求解器要自己的 dt，
	-- IAS 的求解在原生 DLL 里），以及每个 avatar 1 次 has_animation_state_machine（物理会话的"主人是否还活动"闸，
	-- IAS 用的是 4 Hz 的身份判定 role/dead，没有逐帧读取）。这两项列在报告里留给用户决定（见 docs/agent §21）。
	local solverSide = 1 + 4
	check(peakTotal <= budget.peak + solverSide, label .. "：整帧峰值 <= IAS 峰值帧 + 求解侧无对应物 " .. solverSide,
		string.format("%d <= %d + %d", peakTotal, budget.peak, solverSide))
	-- Unit.alive 只剩逐 avatar 的判活（IAS 同样有：L 的 R:5099-5100 + C 的 R:5475，每个 avatar 每帧各 1 次）；不再逐 unit（15000 个）调。
	check((counter.by["Unit.alive"] or 0) / frames <= cb.aliveReads + 0.5, label .. "：稳态 Unit.alive 只剩逐 avatar 判活（<= IAS 的 L+C 项 " .. cb.aliveReads .. "/帧），不逐 unit",
		(counter.by["Unit.alive"] or 0) / frames)
	-- 尸体定格自己的成本：均值与峰值都不得超过 IAS 同场景为它付的引擎调用（L/C/A/S 四项）。
	check(corpse.total <= cb.total, label .. "：尸体定格模块调用数 <= IAS 尸体项（均值口径）", string.format("%d <= %d", corpse.total, cb.total))
	check(corpse.peak <= cb.peak, label .. "：尸体定格模块峰值帧 <= IAS 尸体项峰值帧", string.format("%d <= %d", corpse.peak, cb.peak))
	if not gameplay then
		check(corpse.total == 0, label .. "：非战斗世界（role 0）尸体定格 0 次引擎调用（IAS R:5099 / R:5606 同样直接返回）", corpse.total)
	end
	-- 战斗世界里 World.units（全量）只能是 IAS 的 2.0 s 节奏；非战斗世界是 60 帧一次。
	local fullScans = (counter.by["World.units"] or 0)
	check(fullScans <= budget.scans, label .. "：全量 World.units 次数 <= IAS 节奏", string.format("%d <= %d", fullScans, budget.scans))
	Roster.update = rawUpdate
	os.clock = realClock
end

combatScenario("战斗场景（本人 + 3 队友，角色 1/2）", true)
combatScenario("非战斗世界（无网络身份）", false)

-- F3：找武器。15000 个 unit 里 1 件是武器，右手挂点命中它。
do
	local fake = newFake()
	for _ = 1, 14960 do fake:addUnit({ nodes = 10 }) end
	local weapon = fake:addUnit({ nodes = 10 })
	weapon.isWeapon = true
	local S, counter = counted(fake)
	rawset(_G, "__hd2physbone_runtime", nil)
	rawset(_G, "render", function() end)
	runtimeModule.install(S, { compat = newCompat(), log = false, carriers = {}, tunePath = "nonexistent-hd2pbx-tune.txt",
		perfWindow = 1000000 })
	local runtime = rawget(_G, "__hd2physbone_runtime")
	local Link = runtimeModule.independent_contact_
	local original = Link.weapon_graph
	Link.weapon_graph = function(unit, spec) if unit.isWeapon then return 2 end end
	-- 武器用的标识：假引擎的 debug_name 是 #ID[<id>]
	local weaponId = tostring(weapon.id)
	weapon.hash = weaponId:lower()   -- 假引擎的 units_by_resource 按 unit.hash 匹配 Id64 键
	local graphs = { [weaponId:lower()] = { 2, 1, 258, true } }
	-- E12e-B4：IAS 每帧对目录里每个武器 hash 各问一次 units_by_resource（R:3669-3670），不扫全世界、不读 debug_name、
	-- 不调 Unit.alive。没有"活体列表"可刷新，也没有跨帧缓存。
	runtime.world = 1
	counter.total, counter.by = 0, {}
	local c0 = os.clock()
	local hits = 0
	for _ = 1, 120 do
		runtime.frame = (runtime.frame or 0) + 1
		if Link.find_weapon(1, { p = { 0, 0, 0 }, q = { 0, 0, 0, 1 } }, graphs) then hits = hits + 1 end
	end
	local elapsed = os.clock() - c0
	local alive, names = counter.by["Unit.alive"] or 0, counter.by["Unit.debug_name"] or 0
	local byResource, fullScan = counter.by["World.units_by_resource"] or 0, counter.by["World.units"] or 0
	io.stdout:write(string.format("  [数据] 找武器 120 帧（15000 个无关 unit）：units_by_resource %.1f/帧、World.units %.1f/帧、Unit.alive %.1f/帧、Unit.debug_name %.1f/帧；Lua 耗时 %.1f ms\n",
		byResource / 120, fullScan / 120, alive / 120, names / 120, elapsed * 1000))
	check(hits == 120, "每帧都找到那件武器（只经 units_by_resource）", hits)
	check(alive == 0 and names == 0 and fullScan == 0, "找武器：不调 Unit.alive / debug_name / World.units（IAS R:3669-3671）",
		string.format("alive=%d names=%d units=%d", alive, names, fullScan))
	local catalog = 0
	for _ in pairs(Link.weaponGraphs) do catalog = catalog + 1 end
	check(byResource / 120 == 1, "找武器：每个目录 hash 每帧恰好 1 次 units_by_resource（与 IAS 同量；真实目录 " .. catalog .. " 项）", byResource / 120)
	Link.weapon_graph = original
end

io.stdout:write(string.format("\n检查 %d 项，失败 %d\n", checks, failures))
os.exit(failures == 0 and 0 or 1)
