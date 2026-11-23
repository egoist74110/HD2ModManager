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
	local self = setmetatable({ units = {}, nextIndex = 1, clock = 0 }, Fake)
	-- 噪声 unit：节点数不够，必须被判"不是目标"，用来确认扫描没有把所有人都当候选。
	for _ = 1, 40 do self:addUnit({ nodes = 10 }) end
	return self
end

function Fake:addUnit(spec)
	local unit = {
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

function Fake:playerAt(name, x, chains)
	local avatar = self:addUnit({ name = name .. "-avatar", avatar = true, position = vector(x, 0, 0) })
	local armor = self:addUnit({ name = name .. "-armor", anim = false, mg = true, position = vector(x + 0.1, 0, 0), chains = chains })
	return avatar, armor
end

function Fake:stingray()
	local self_ = self
	local id32 = function(text) return text end
	local Unit = {
		alive = function(unit) return unit ~= nil and unit.alive == true end,
		num_scene_graph_items = function(unit) return unit.nodes end,
		has_animation_state_machine = function(unit) return unit.anim end,
		has_node = function(unit, _) return unit.mg end,
		world_position = function(unit, _) return unit.position end,
		debug_name = function(unit) return "#ID[" .. tostring(unit.id) .. "]" end,
	}
	local World = {
		units = function(_) return self_.units end,
		num_units = function(_) return #self_.units end,
		units_by_resource = function(_, resource)
			local found = {}
			for index = 1, #self_.units do
				local unit = self_.units[index]
				if unit.alive and unit.avatarResource and resource == "content/fac_helldivers/cha_avatar/avatar_helldiver" then
					found[#found + 1] = unit
				end
			end
			return found
		end,
		-- 相机固定在原点：于是 (0,0,0) 那个玩家就是"自己"（第一位）。
		debug_camera_pose = function(_) return { pose = true } end,
		update_unit = function() end,
	}
	return {
		Application = {
			worlds = function() return { 1 } end,
			string_to_id32 = id32,
			time_since_launch = function() return self_.clock end,
		},
		World = World,
		Unit = Unit,
		Vector3 = { to_elements = function(v) return v.x, v.y, v.z end },
		Matrix4x4 = { translation = function(_) return vector(0, 0, 0) end },
	}
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
io.stdout:write("\n[E12 战斗场景] 世界 15000 个 unit + 4 名玩家\n")

local function counted(fake)
	local S = fake:stingray()
	local counter = { total = 0, by = {} }
	for _, space in ipairs({ "Unit", "World", "Application", "Matrix4x4", "Vector3" }) do
		for key, fn in pairs(S[space]) do
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

do
	local fake = newFake()
	for _ = 1, 14960 do fake:addUnit({ nodes = 10 }) end
	local compat = newCompat()
	for index = 1, 4 do fake:playerAt("p" .. index, (index - 1) * 10) end
	local S, counter = counted(fake)
	rawset(_G, "__hd2physbone_runtime", nil)
	rawset(_G, "render", function() end)
	runtimeModule.install(S, { compat = compat, log = false, carriers = { { rig = { bones = { { name = "hair" } } }, path = "fake" } },
		settleFrames = 1, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt", pollFrames = 1, validateFrames = 3,
		heartbeatFrames = 1000000, minNodes = 90, perfWindow = 1000000 })
	local runtime = rawget(_G, "__hd2physbone_runtime")
	fake:advance(60)
	check(#(runtime.sessions or {}) == 4, "战斗场景下玩家护甲正常挂上", #(runtime.sessions or {}))
	counter.total, counter.by = 0, {}
	local c0 = os.clock()
	fake:advance(300)
	local elapsed = os.clock() - c0
	local perFrame = counter.total / 300
	io.stdout:write(string.format("  [数据] 稳态每帧引擎调用 %.2f（World.units 每帧 %.2f 次）；300 帧 Lua 耗时 %.1f ms\n",
		perFrame, (counter.by["World.units"] or 0) / 300, elapsed * 1000))
	local keys = {}
	for key in pairs(counter.by) do keys[#keys + 1] = key end
	table.sort(keys)
	for _, key in ipairs(keys) do io.stdout:write(string.format("    %-28s %.2f/帧\n", key, counter.by[key] / 300)) end
	local perf = runtimeModule.perf_
	if perf ~= nil then
		local rows = {}
		for name, calls in pairs(perf.calls) do rows[#rows + 1] = string.format("%s=%.1f", name, calls / math.max(1, perf.frames)) end
		for _, bucket in ipairs({ "OTHER", "P2", "F8" }) do
			local per, list = perf.api[bucket] or {}, {}
			for name, calls in pairs(per) do list[#list + 1] = string.format("%s=%.1f", name, calls / math.max(1, perf.frames)) end
			table.sort(list)
			io.stdout:write("  [明细 " .. bucket .. "] " .. table.concat(list, " ") .. "\n")
		end
		table.sort(rows)
		io.stdout:write("  [分项] " .. table.concat(rows, " ") .. "\n")
		check((perf.calls.OTHER or 0) / math.max(1, perf.frames) < 1, "OTHER 接近 0（计数覆盖全部引擎调用）", (perf.calls.OTHER or 0) / math.max(1, perf.frames))
	end
	check(perFrame < 40, "世界 15000 个 unit、4 人时稳态每帧引擎调用 < 40", perFrame)
	check((counter.by["Unit.alive"] or 0) / 300 < 1, "稳态不再逐 unit 调 Unit.alive", (counter.by["Unit.alive"] or 0) / 300)
end

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
	local graphs = { [weaponId:lower()] = { 2, 1, 258, true } }
	-- 帧驱动：每帧先刷新活体列表（真实运行时在 drive 里做；这里用假引擎同样的入口）
	runtime.world = 1
	local function refresh()
		runtime.frame = (runtime.frame or 0) + 1
		runtime.liveUnits = S.World.units(1)
		runtime.liveUnitCount = #runtime.liveUnits
		-- 与运行时同款的索引：在没有改动的版本里没有这几个字段，find_weapon 也不会用它
		local live = runtime.liveIndex or {}
		runtime.liveIndex = live
		runtime.liveStamp = runtime.frame
		runtime.liveIndexUnits = runtime.liveUnits
		for i = 1, #runtime.liveUnits do live[runtime.liveUnits[i]] = runtime.frame end
	end
	refresh()
	counter.total, counter.by = 0, {}
	local c0 = os.clock()
	local hits = 0
	for _ = 1, 120 do
		refresh()
		if Link.find_weapon(runtime.liveUnits, { p = { 0, 0, 0 }, q = { 0, 0, 0, 1 } }, graphs, runtime) then hits = hits + 1 end
	end
	local elapsed = os.clock() - c0
	counter.total = counter.total - 0
	local alive, names = counter.by["Unit.alive"] or 0, counter.by["Unit.debug_name"] or 0
	io.stdout:write(string.format("  [数据] 找武器 120 帧：Unit.alive %.1f/帧、Unit.debug_name %.1f/帧；Lua 耗时 %.1f ms\n",
		alive / 120, names / 120, elapsed * 1000))
	check(hits == 120, "每帧都找到那件武器", hits)
	check((alive + names) / 120 < 150, "找武器稳态每帧引擎调用 < 150（改前为 15000×2；15000 个 unit 的 debug_name 缓存 180 帧才重建一次）", (alive + names) / 120)
	Link.weapon_graph = original
end

io.stdout:write(string.format("\n检查 %d 项，失败 %d\n", checks, failures))
os.exit(failures == 0 and 0 or 1)
