-- E12f-D1：尸体定格整合测试。**真的 CorpseMotion + 真的 CorpseFreeze**，假引擎带 Network/GameSession 与可写节点树。
--   场景 A（普通死亡）：活着（采样）→ health 0 → 死后 4 s 生成副本、显示、隐藏原件、停原件物理 → 之后定格不动；
--   场景 B（撕裂：头飞出）：提前复制 → 真 CorpseMotion 推进约 4 s、倒在地上 → 保护期结束后定格不动；
--   每个阶段统计每帧引擎调用；副本跨世界切换 / 复活不销毁（IAS 常态不删）；
--   scene_graph_link：只在刚 spawn、且通过身份/布局断言的副本上调用，从不对 nil/无效 unit 调用（裙摆护栏的 0xc0000005 教训）。
-- 跑法：python tools/physbone-harness/run_physbone_regressions.py --filter corpse-integration

local runtimePath = arg and arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local compatPath = "HD2PhysBoneCompat/src/physbone_compat.lua"

local failures, checks = 0, 0
local function check(condition, message, detail)
	checks = checks + 1
	if not condition then failures = failures + 1 end
	print(string.format("  %-4s %s%s", condition and "PASS" or "FAIL", message, detail ~= nil and ("  [" .. tostring(detail) .. "]") or ""))
end

local Compat = assert(assert(loadfile(compatPath))())
local Runtime = assert(assert(loadfile(runtimePath))())
local Roster, Freeze, Motion = Runtime.roster_, Runtime.corpse_, Runtime.corpseMotion_
assert(Roster and Freeze and Freeze.state and Motion and type(Motion.new) == "function", "运行时没有导出真 CorpseMotion")

local clock = 100.0
os.clock = function() return clock end

-- 尸体定格模块自己的引擎调用：包住它被 Roster.update / drive 调用的四个入口（begin / 化身号簿记 / filter / 逐帧采样与 flush），
-- 按"总引擎调用计数器"的差值累计到 corpseMeter.frame。入口之间互不嵌套，所以不会重复计。
-- 每次 scene() 换一个新的 E.engine（currentEngine），包装器只装一次。
local currentEngine
local corpseMeter = { frame = 0 }
for _, name in ipairs({ "begin", "lifecycleObserve", "filter", "frame" }) do
	local raw = Runtime.corpse_[name]
	Runtime.corpse_[name] = function(...)
		local before = currentEngine.total
		local a, b = raw(...)
		corpseMeter.frame = corpseMeter.frame + (currentEngine.total - before)
		return a, b
	end
end

local function qmul(a, b)
	return { a[4] * b[1] + a[1] * b[4] + a[2] * b[3] - a[3] * b[2], a[4] * b[2] - a[1] * b[3] + a[2] * b[4] + a[3] * b[1],
		a[4] * b[3] + a[1] * b[2] - a[2] * b[1] + a[3] * b[4], a[4] * b[4] - a[1] * b[1] - a[2] * b[2] - a[3] * b[3] }
end
local function qrot(q, v)
	local x, y, z, w = q[1], q[2], q[3], q[4]
	local tx, ty, tz = 2 * (y * v[3] - z * v[2]), 2 * (z * v[1] - x * v[3]), 2 * (x * v[2] - y * v[1])
	return { v[1] + w * tx + (y * tz - z * ty), v[2] + w * ty + (z * tx - x * tz), v[3] + w * tz + (x * ty - y * tx) }
end
local function compose(parent, lq, lp)
	local r = qrot(parent.q, lp)
	return { q = qmul(parent.q, lq), p = { parent.p[1] + r[1], parent.p[2] + r[2], parent.p[3] + r[3] } }
end

-- ---------------------------------------------------------------- 场景
local AVATAR = "content/fac_helldivers/cha_avatar/avatar_helldiver"
local HASHES = { "a000000000000001", "a000000000000002", "a000000000000003" }
local OFFSET, COUNT = 1, 12
local IDENT = { 0, 0, 0, 1 }

-- 每次新建一整个世界（引擎表 + 运行时），互不污染。
local function scene(opts)
	opts = opts or {}
	local Runtime_ = Runtime
	Runtime_.uninstall()
	local world = { name = "world" }
	local world2 = { name = "world2" }
	local units = {}
	local object = { id = 5000, x = 0, y = 0, z = 0, health = 100 }
	local calls, events = {}, {}
	local E = { world = world, units = units, object = object, calls = calls, events = events, spawned = {}, destroyed = {} }
	E.currentWorld = world
	E.links = {}

	local function newUnit(name, kind, count, offset)
		local unit = { name = name, kind = kind, alive = true, count = count, offset = offset, nodes = {}, names = {}, visible = true, world = world }
		units[#units + 1] = unit
		return unit
	end
	local function fixed(unit, n, q, p) unit.nodes[n] = { world = { q = q, p = p } } end
	local function worldOf(unit, n)
		local node = unit.nodes[n]
		if not node then return nil end
		if node.world then return node.world end
		local parentPose = node.parent and worldOf(unit, node.parent) or { q = IDENT, p = { 0, 0, 0 } }
		if node.parent == nil and node.root then return { q = node.lq, p = node.lp } end
		return compose(parentPose, node.lq, node.lp)
	end
	E.worldOf = worldOf

	-- 人
	local avatar = newUnit("avatarA", "avatar", 10, 1)
	for n = 1, 10 do fixed(avatar, n, IDENT, { 0, 0, 0.1 * n }) end
	fixed(avatar, 1, IDENT, { 0, 0, 0 })     -- 根（网络对象就在这里）
	fixed(avatar, 2, IDENT, { 0, 0, 1.0 })   -- hips
	fixed(avatar, 3, IDENT, { 0, 0, 1.6 })   -- head
	avatar.names = { hips = 2, head = 3 }
	E.avatar = avatar

	-- 件：三件，节点 1..5，1 = StingrayEntityRoot
	local JOINTS = {
		{ "hips", { 0, 0, 1.0 } }, { "chest", { 0, 0, 1.35 } }, { "head", { 0, 0, 1.7 } },
		{ "l_shoulder", { -0.2, 0, 1.45 } }, { "r_shoulder", { 0.2, 0, 1.45 } },
		{ "l_elbow", { -0.25, 0, 1.2 } }, { "r_elbow", { 0.25, 0, 1.2 } },
		{ "l_hand", { -0.25, 0.2, 1.1 } }, { "r_hand", { 0.25, 0.2, 1.1 } },
		{ "l_thigh", { -0.1, 0, 0.95 } }, { "r_thigh", { 0.1, 0, 0.95 } },
		{ "l_knee", { -0.1, 0, 0.5 } }, { "r_knee", { 0.1, 0, 0.5 } },
		{ "l_foot", { -0.1, 0, 0.05 } }, { "r_foot", { 0.1, 0, 0.05 } },
	}
	local split = { { 1, 5 }, { 6, 9 }, { 10, 15 } }   -- 每件拿哪几个关节
	local jointDefs = {}
	E.jointNode = {}   -- 关节名 -> { 件序号, 节点号 }
	for index, range in ipairs(split) do
		jointDefs[index] = {}
		for i = range[1], range[2] do
			local node = 2 + (i - range[1])
			jointDefs[index][#jointDefs[index] + 1] = { "ias_" .. JOINTS[i][1], node, JOINTS[i][1], JOINTS[i][2] }
			E.jointNode[JOINTS[i][1]] = { index, node }
		end
	end
	E.pieces = {}
	local pieces, bones = {}, {}
	for index, hash in ipairs(HASHES) do
		local u = newUnit("#ID[" .. hash .. "]", "armour", COUNT, OFFSET)
		u.hash = hash
		u.names = { StingrayEntityRoot = 1 }
		for n = 1, COUNT do fixed(u, n, IDENT, { 0.0, 0.0, 0.2 * n }) end
		local names = {}
		for _, j in ipairs(jointDefs[index]) do
			u.names[j[1]] = j[2]
			fixed(u, j[2], IDENT, j[4])
			names[#names + 1] = j[1]
			bones[#bones + 1] = { j[1], j[3] }
		end
		pieces["#ID[" .. hash .. "]"] = names
		E.pieces[index] = u
	end

	local function count(name) calls[name] = (calls[name] or 0) + 1 end

	local S = {
		Application = { worlds = function() return { E.currentWorld } end, time_since_launch = function() return clock end },
		IdString64 = { from_hex = function(hash) return { hex = hash } end },
		World = {}, Unit = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {},
		Network = { game_session = function() return 1 end, peer_id = function() return "me" end },
		GameSession = {
			in_session = function() return true end,
			objects_owned_by = function() return { object.id } end,
			game_object_exists = function(_, id) return id == object.id end,
			game_object_is_type = function() return true end,
			game_object_field = function(_, _, field)
				if field == "health" then return object.health end
				return { object.x, object.y, object.z }
			end,
		},
	}
	local function inWorld(unit) return unit.world == E.currentWorld end
	S.World.units = function() local out = {} for _, u in ipairs(units) do if u.alive and inWorld(u) then out[#out + 1] = u end end return out end
	S.World.num_units = function() return #S.World.units() end
	S.World.units_by_resource = function(w, key)
		count("units_by_resource")
		local out = {}
		for _, unit in ipairs(units) do
			if unit.alive and unit.world == w then
				if type(key) == "table" then
					if unit.name:find(key.hex, 1, true) then out[#out + 1] = unit end
				elseif key == AVATAR and unit.kind == "avatar" then out[#out + 1] = unit end
			end
		end
		return out
	end
	S.World.spawn_unit = function(w, resource, position, rotation)
		count("spawn_unit")
		if E.failSpawn then error("spawn refused") end
		local src
		for _, u in ipairs(units) do if u.name == resource then src = u end end
		local copy = newUnit(resource .. "|copy", "copy", E.copyCount or src.count, src.offset)
		copy.world, copy.resource = w, resource
		copy.names = { StingrayEntityRoot = src.offset }
		for n = 1, copy.count do copy.nodes[n] = { parent = nil, lq = IDENT, lp = { 0, 0, 0 }, root = n == src.offset } end
		copy.nodes[src.offset].lq, copy.nodes[src.offset].lp = rotation, position
		copy.visible = true
		E.spawned[#E.spawned + 1] = copy
		return copy
	end
	S.World.unlink_unit = function() count("unlink_unit") end
	S.World.update_unit = function() count("update_unit") end
	S.World.destroy_unit = function(_, unit)
		count("destroy_unit")
		if E.failDestroy then error("destroy refused") end
		unit.alive = false
		E.destroyed[#E.destroyed + 1] = unit
	end
	S.Unit.alive = function(u) count("alive") return u.alive == true end
	S.Unit.debug_name = function(u) count("debug_name") return u.name end
	S.Unit.name_hash = function(u) count("name_hash") return u.resource or u.name end
	S.Unit.num_scene_graph_items = function(u) count("num_scene_graph_items") u.checkedItems = true return u.count end
	S.Unit.has_animation_state_machine = function(u) count("has_animation_state_machine") return u.kind == "avatar" end
	S.Unit.has_node = function(u, name) count("has_node") return u.names[name] ~= nil end
	S.Unit.node = function(u, name) count("node") if u.kind == "copy" and u.checkedItems and name == "StingrayEntityRoot" then u.verified = true; u.verifiedFrame = E.runtime and E.runtime.frame end return u.names[name] end
	S.Unit.num_actors = function() return 0 end
	S.Unit.world_position = function(u, n) count("world_position") local w = worldOf(u, n) return w and { w.p[1], w.p[2], w.p[3] } or nil end
	S.Unit.world_pose = function(u, n) count("world_pose") local w = worldOf(u, n) return w and { pose = w } or nil end
	S.Unit.set_local_rotation = function(u, n, q) count("set_local_rotation") local node = u.nodes[n]; node.lq = { q.x, q.y, q.z, q.w }; node.world = nil end
	S.Unit.set_local_position = function(u, n, v) count("set_local_position") local node = u.nodes[n]; node.lp = { v[1], v[2], v[3] }; node.world = nil end
	S.Unit.scene_graph_link = function(u, n, parent)
		count("scene_graph_link")
		local rec = { unit = u, node = n, parent = parent }
		E.links[#E.links + 1] = rec
		-- 真引擎对 nil / 已销毁 unit 调这个会 0xc0000005：记下每次调用时的状态让测试断言
		rec.valid = type(u) == "table" and u.alive == true and u.nodes ~= nil and u.nodes[n] ~= nil
		rec.copy = type(u) == "table" and u.kind == "copy"
		rec.sameFrame = type(u) == "table" and u.verifiedFrame ~= nil and E.runtime ~= nil and u.verifiedFrame == E.runtime.frame
		rec.verified = type(u) == "table" and u.verified == true   -- 身份 / 布局断言（alive、name_hash、节点数、根节点）已被运行时读过
		rec.parentOk = parent == nil or (type(u) == "table" and u.nodes[parent] ~= nil)
		u.nodes[n].parent = parent
	end
	S.Unit.scene_graph_parent = function(u, n) count("scene_graph_parent") return u.nodes[n] and u.nodes[n].parent end
	S.Unit.set_unit_visibility = function(u, v)
		count("set_unit_visibility")
		if E.failHide and u.kind == "armour" and v == false and E.failHide(u) then error("hide refused") end
		u.visible = v
		events[#events + 1] = { u = u, visible = v }
	end
	setmetatable(S.Vector3, { __call = function(_, x, y, z) count("Vector3") return { x, y, z } end })
	S.Vector3.to_elements = function(v) return v[1], v[2], v[3] end
	S.Quaternion.from_elements = function(x, y, z, w) count("from_elements") return { x = x, y = y, z = z, w = w } end
	S.Quaternion.to_elements = function(q) return q.x, q.y, q.z, q.w end
	S.Matrix4x4.to_elements = function(m)
		count("to_elements")
		local q, p = m.pose.q, m.pose.p
		local x, y, z, w = q[1], q[2], q[3], q[4]
		local r00, r01, r02 = 1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)
		local r10, r11, r12 = 2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)
		local r20, r21, r22 = 2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)
		return r00, r10, r20, r01, r11, r21, r02, r12, r22, p[1], p[2], p[3]
	end

	-- 全部引擎调用的总数（每个命名空间的每个函数都包一层）；尸体定格模块自己的份额由下面 Freeze.* 的包装器按差值统计。
	-- 注意：不并入 E.calls（那是按名字分项、给 freezeCalls 用的），这里只要一个总数。
	E.engine = { total = 0 }
	currentEngine = E.engine
	for _, space in ipairs({ "Application", "World", "Unit", "Quaternion", "Matrix4x4", "Network", "GameSession", "IdString64" }) do
		for key, fn in pairs(S[space]) do
			if type(fn) == "function" then
				S[space][key] = function(...) E.engine.total = E.engine.total + 1 return fn(...) end
			end
		end
	end
	do
		local meta = getmetatable(S.Vector3)
		local call = meta.__call
		meta.__call = function(...) E.engine.total = E.engine.total + 1 return call(...) end
		local toElements = S.Vector3.to_elements
		S.Vector3.to_elements = function(...) E.engine.total = E.engine.total + 1 return toElements(...) end
	end

	local runtime = Runtime_.install(S, {
		compat = Compat, perfCounters = false, log = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		identityPollSeconds = 0, rosterNegativeSeconds = 0,
		profileHashes = HASHES, corpseProfiles = { [HASHES[1]] = true, [HASHES[2]] = true, [HASHES[3]] = true },
		independentPieces = pieces, independentBones = bones,
		corpseFreeze = opts.enabled ~= false,
	})
	local logs = {}
	runtime.logger = function(format, ...) logs[#logs + 1] = select("#", ...) > 0 and string.format(format, ...) or format end
	runtime.sessionByUnit = setmetatable({}, { __mode = "k" })
	E.runtime, E.logs, E.S = runtime, logs, S
	Roster.pollInterval, Roster.negativeSeconds = 0, 0

	function E.step(dt)
		clock = clock + (dt or 1 / 60)
		if opts.viaRender then
			rawget(_G, "render")()   -- 真正的 drive 入口（没有物理会话的那条早退分支）
			return
		end
		runtime.frame = runtime.frame + 1
		Roster.update(runtime)   -- 里面带 begin / filter（drive 的第一步）
		Freeze.frame(runtime)    -- drive 的尾部 corpse_frame
	end
	function E.run(seconds, dt)
		dt = dt or 1 / 60
		for _ = 1, math.floor(seconds / dt + 0.5) do E.step(dt) end
	end
	function E.die()
		object.health = 0
		E.deadAt = clock
	end
	function E.copyOf(index)
		local original = E.pieces[index]
		for _, copy in ipairs(E.spawned) do if copy.resource == original.name then return copy end end
	end
	-- 只统计尸体副本独有的引擎调用（Roster 不用的那些）
	function E.freezeCalls()
		local total = 0
		for _, name in ipairs({ "world_pose", "to_elements", "has_node", "node", "set_unit_visibility", "spawn_unit", "name_hash", "scene_graph_link",
			"scene_graph_parent", "set_local_rotation", "set_local_position", "from_elements", "Vector3", "update_unit", "unlink_unit", "destroy_unit" }) do
			total = total + (calls[name] or 0)
		end
		return total
	end
	function E.reset_calls() for key in pairs(calls) do calls[key] = nil end end
	return E
end

-- 统计每帧引擎调用总数（全部、尸体副本独有）
local function totalCalls(E) local t = 0 for _, v in pairs(E.calls) do t = t + v end return t end
-- model：该阶段 IAS 每帧为尸体定格付的引擎调用数（数字，或 function(i, now) 返回数字）；见下面"IAS 1.6.4 各阶段"。
-- 第 4 个返回值是我们尸体定格模块的 { mean, peak }，第 5 个是 IAS 的 { mean, peak }。
local function profile(E, seconds, dt, model)
	local maxAll, maxFreeze, sumAll, n = 0, 0, 0, 0
	local oursSum, oursPeak, iasSum, iasPeak = 0, 0, 0, 0
	for i = 1, math.floor(seconds / (dt or 1 / 60) + 0.5) do
		E.reset_calls()
		corpseMeter.frame = 0
		E.step(dt)
		local all, fz = totalCalls(E), E.freezeCalls()
		maxAll, maxFreeze, sumAll, n = math.max(maxAll, all), math.max(maxFreeze, fz), sumAll + all, n + 1
		oursSum, oursPeak = oursSum + corpseMeter.frame, math.max(oursPeak, corpseMeter.frame)
		local ias = type(model) == "function" and model(i, clock) or model or 0
		iasSum, iasPeak = iasSum + ias, math.max(iasPeak, ias)
	end
	n = math.max(n, 1)
	return maxAll, maxFreeze, sumAll / n, { mean = oursSum / n, peak = oursPeak }, { mean = iasSum / n, peak = iasPeak }
end

-- ---------------------------------------------------------------- IAS 1.6.4 各阶段每帧引擎调用（尸体定格相关）
-- 用户规则：我们的引擎调用不得超过 IAS 同场景。下面按 IAS 源码（行号指
-- tmp/ias-lab/upstream/ias-1.6.4/Generator/runtime/lua/lua/runtime.lua）把"为尸体定格付的"调用逐阶段算出来，
-- 与我们尸体定格模块的实测（四个入口的差值，见 corpseMeter）逐阶段比：均值与峰值都不得超过。
-- 场景形状：1 个 avatar（活着 role 1，死后 role 3）、P=3 件、每件 C=12 个节点（根节点 offset=1）。
-- 只数 Lua 侧引擎 API（Unit/World/Network/GameSession/Matrix4x4/Vector3/Quaternion）；原生 ABI（freeze_corpse_physics R:5923、
-- corpse_freeze_context、shared_next_incarnation 等）不是 Lua 引擎 API，不计。
-- 原子操作的 IAS 调用数：
--   world_pose  = 2：Unit.world_pose + Matrix4x4.to_elements（R:430 -> R:376-386 pose_capture.matrix_read；稳态，前 128 次的
--                 legacy 对拍读取 R:392-404 是启动期一次性的，不计）。
--   engine_vector = 1（Vector3 构造 R:313-317）；engine_quaternion = 3（Vector3 构造 + Quaternion.axis_angle + Quaternion.is_valid，R:327-351）。
--   set(unit,node,pose) = 6：set_local_rotation 1 + engine_quaternion 3 + set_local_position 1 + engine_vector 1（R:5658-5661）。
--   core(avatar) = 10：Unit.alive、has_node('hips')、node('hips')、num_scene_graph_items 各 1（R:5475-5477）+ world_pose 2（R:5479）+ has_node('head')、
--                 node('head') 各 1（R:5482-5483）+ world_pose 2（R:5485）；每个 avatar 每帧一次（`cores` 在 F.begin R:5546 清空、R:5474 命中）。
--   shared_life(role 1) = 5：Unit.alive（R:5099-5100）、Network.game_session（R:5108）、GameSession.in_session（R:5109）、game_object_exists（R:5110）、
--                 game_object_field（R:5111）。
local P, C = #HASHES, COUNT
local IAS_WP, IAS_SET, IAS_CORE, IAS_LIFE1 = 2, 6, 10, 5
local IAS = {}
-- 活人阶段每帧：L 化身号证据 5（R:6917-6919 observe -> R:5097-5119，role 1）+ C core 10（F.observe R:5602-5606，先于节流）
-- + A has_animation_state_machine 每件 1（R:5613，在节流之前）+ S 采样帧（桶变了，R:5614-5618 `bucket=floor(now*4)`）每件 capture C*2（R:5525-5544）。
function IAS.alive()
	local last
	return function(_, now)
		local bucket = math.floor(now * 4)
		local sample = last ~= bucket
		last = bucket
		return IAS_LIFE1 + IAS_CORE + P + (sample and P * C * IAS_WP or 0)
	end
end
-- 已死未满 4 s（role 3，未撕裂）：shared_life 在 R:5099 因 s.dead 直接返回（0）；queue_input（R:5589-5601）与 observe（R:5602-5606）
-- 都要 core（R:5598、R:5607），同帧缓存 => 10；flush（R:5850-5949）`age>=4 or use_cache` 不成立 => 0。
IAS.dead = IAS_CORE
-- prepare（R:5635-5657）：Unit.alive、num_scene_graph_items、has_node、node 各 1（R:5639-5640），name_hash 1（R:5646）；非缓存路径 capture C*2（R:5648），
-- 缓存路径（撕裂）改读 parents：scene_graph_parent C-1 次（R:5650-5653）。
function IAS.prepare(cache) return 4 + 1 + (cache and (C - 1) or C * IAS_WP) end
-- apply（R:5662-5686）：spawn（engine_vector 1 + engine_quaternion 3 + spawn_unit 1 = 5，R:5665）、set_unit_visibility 1（R:5668）、
-- 身份/布局断言 alive、name_hash、num_scene_graph_items、has_node、node 各 1（R:5669-5670）、has_animation_state_machine 1（R:5671）、
-- num_actors 1（R:5672）、unlink_unit 1（R:5673）、scene_graph_link C（R:5674-5675）、set C 次（R:5676-5677）、update_unit 1（R:5678）、
-- 回读 world_pose C 次 + scene_graph_parent C-1 次（R:5679-5685）。
IAS.apply = 5 + 1 + 5 + 1 + 1 + 1 + C + C * IAS_SET + 1 + C * IAS_WP + (C - 1)
-- 复制那一帧：observe 的 core 10 + 每件 prepare + 每件 apply + 显示副本 / 隐藏原件各 1 次 set_unit_visibility（R:5925-5926）。
function IAS.copyFrame(cache) return IAS_CORE + P * IAS.prepare(cache) + P * IAS.apply + 2 * P end
-- filter 对每个副本的跟随（R:5978-5990）：alive 1 + num_scene_graph_items 1 + set（根）6 + update_unit 1 + world_pose 2 + set_unit_visibility 2。
IAS.follow = 1 + 1 + IAS_SET + 1 + IAS_WP + 2
-- 定格稳态（没有 fixed_core，R:5981 每帧 core(owner) 一次）：core 10 + 每件 follow。
IAS.frozen = IAS_CORE + P * IAS.follow
-- 铰接下落的每一帧（R:5964-5970 filter 先对每组 guard 调 core(owner) 10 -> finish_guard R:5811-5849：每件 alive(副本) alive(原件)
-- num_scene_graph_items(副本) num_scene_graph_items(原件) 共 4（R:5819-5820）-> update_motion R:5775-5810：每件 rewrite_copy（R:5711-5719）
-- = set C 次 + update_unit 1 + 回读 (C-1)*2 -> 之后 fixed_core 已有，follow 不再 core）。
IAS.fall = IAS_CORE + P * 4 + P * (C * IAS_SET + 1 + (C - 1) * IAS_WP) + P * IAS.follow
-- 铰接下落结束后的稳态：fixed_core 已写（R:5791），没有 core => 只有每件 follow。
IAS.settled = P * IAS.follow

local costRows = {}
local function costRow(phase, ours, ias)
	costRows[#costRows + 1] = { phase = phase, ours = ours, ias = ias }
	print(string.format("  COST %-30s 我们 均值 %7.1f / 峰值 %4d | IAS 均值 %7.1f / 峰值 %4d", phase, ours.mean, ours.peak, ias.mean, ias.peak))
	check(ours.mean <= ias.mean + 1e-9, phase .. "：尸体定格每帧引擎调用均值 <= IAS", string.format("%.1f <= %.1f", ours.mean, ias.mean))
	check(ours.peak <= ias.peak, phase .. "：尸体定格每帧引擎调用峰值 <= IAS", string.format("%d <= %d", ours.peak, ias.peak))
end
local function snapshot(E, copy)
	local out = {}
	for n = 1, copy.count do local w = E.worldOf(copy, n); out[n] = { w.p[1], w.p[2], w.p[3] } end
	return out
end
local function maxDrift(a, b)
	local d = 0
	for n, p in pairs(a) do d = math.max(d, math.abs(p[1] - b[n][1]), math.abs(p[2] - b[n][2]), math.abs(p[3] - b[n][3])) end
	return d
end
-- 给真 CorpseMotion 的函数套计数器（仍调用原函数）
local motionCalls = { new = 0, advance = 0, frames = 0, deform = 0 }
for name in pairs(motionCalls) do
	local original = Motion[name]
	Motion[name] = function(...) motionCalls[name] = motionCalls[name] + 1 return original(...) end
end
local function linkAudit(E, label)
	local bad, nonCopy, unverified, total = 0, 0, 0, #E.links
	for _, r in ipairs(E.links) do
		if not r.valid or not r.parentOk then bad = bad + 1 end
		if not r.copy then nonCopy = nonCopy + 1 end
		if not r.verified or not r.sameFrame then unverified = unverified + 1 end
	end
	check(total > 0 and bad == 0 and nonCopy == 0 and unverified == 0,
		label .. "：scene_graph_link 共 " .. total .. " 次，全部落在刚 spawn 且同帧已通过身份/布局断言的副本上，无 nil/无效 unit",
		string.format("bad=%d 非副本=%d 未核对=%d", bad, nonCopy, unverified))
	return total
end
local function cloneCount()
	local n = 0
	for _ in pairs(Freeze.state.clones) do n = n + 1 end
	return n
end

-- ===================================================================== A 普通死亡：4 s 复制并定格
io.stdout:write("\n[A 普通死亡] 活着采样 -> health 0 -> 4 s 复制 -> 定格；各阶段每帧引擎调用\n")
do
	local E = scene()
	for _, u in ipairs(E.pieces) do E.runtime.sessionByUnit[u] = { fake = true } end
	E.step()
	local aliveModel = IAS.alive()
	aliveModel(0, clock)   -- 预热那一帧已经落在当前桶里：先记住桶号，窗口内只有桶变了的帧才算 IAS 的采样帧
	local aliveMax, aliveFz, aliveAvg, aliveOurs, aliveIas = profile(E, 2.0, nil, aliveModel)
	print(string.format("  INFO 阶段 活着：每帧总调用 最大 %d / 平均 %.1f，其中尸体副本独有 最大 %d", aliveMax, aliveAvg, aliveFz))
	costRow("A 活着（采样+化身号+core）", aliveOurs, aliveIas)
	E.die()
	motionCalls.new, motionCalls.advance = 0, 0
	local deadMax, deadAvg, deadFz, deadOurs, deadIas = 0, 0, 0
	deadMax, deadFz, deadAvg, deadOurs, deadIas = profile(E, 3.8, nil, IAS.dead)
	print(string.format("  INFO 阶段 已死未满 4 s：每帧总调用 最大 %d / 平均 %.1f，其中尸体副本独有 最大 %d", deadMax, deadAvg, deadFz))
	costRow("A 已死未满 4 s", deadOurs, deadIas)
	check(#E.spawned == 0 and E.pieces[1].visible, "死后 < 4 s：不复制、原件可见", #E.spawned)
	local spawnFrameCalls, spawnFrameFz, spawnOurs
	for _ = 1, 40 do
		E.reset_calls()
		corpseMeter.frame = 0
		E.step()
		if (E.calls.spawn_unit or 0) > 0 then spawnFrameCalls, spawnFrameFz, spawnOurs = totalCalls(E), E.freezeCalls(), corpseMeter.frame break end
	end
	if spawnOurs then
		local ias = IAS.copyFrame(false)
		costRow("A 复制那一帧（三件）", { mean = spawnOurs, peak = spawnOurs }, { mean = ias, peak = ias })
	end
	check(spawnFrameCalls ~= nil, "死后满 4 s 的那一帧：生成副本（spawn_unit 被调用）")
	print(string.format("  INFO 阶段 复制那一帧（三件）：总调用 %d，其中尸体副本独有 %d", spawnFrameCalls or -1, spawnFrameFz or -1))
	local c1, c2, c3 = E.copyOf(1), E.copyOf(2), E.copyOf(3)
	check(#E.spawned == 3 and c1 and c1.visible and c2.visible and c3.visible, "三个副本都生成并可见")
	check(not E.pieces[1].visible and not E.pieces[2].visible and not E.pieces[3].visible, "三件原件被隐藏")
	check(E.runtime.sessionByUnit[E.pieces[1]] == nil and Freeze.physicsStopped(E.pieces[2], E.runtime.frame), "原件的 Lua 物理会话停掉并保持停")
	check(motionCalls.new == 0 and motionCalls.advance == 0, "普通死亡（没撕裂）不走 CorpseMotion，与 IAS 一致", motionCalls.advance)
	linkAudit(E, "A")
	local before = snapshot(E, c1)
	local steadyMax, steadyFz, steadyAvg, steadyOurs, steadyIas = profile(E, 1.0, nil, IAS.frozen)
	print(string.format("  INFO 阶段 定格后稳态（3 个副本）：每帧总调用 最大 %d / 平均 %.1f，其中尸体副本独有 最大 %d", steadyMax, steadyAvg, steadyFz))
	costRow("A 定格稳态", steadyOurs, steadyIas)
	check(steadyFz <= 3 * 12 + 16, "定格后稳态每帧尸体副本引擎调用有界", steadyFz)
	E.run(10)
	check(maxDrift(before, snapshot(E, c1)) < 1e-6, "再过 10 s：副本一动不动（定格）")
	check(#E.destroyed == 0, "全程没有销毁任何副本（IAS 常态不删）", #E.destroyed)
	local oldWorld = E.currentWorld
	E.currentWorld = { name = "mission2" }
	E.run(1.0)
	E.currentWorld = oldWorld
	E.object.health = 100   -- 复活
	E.run(1.0)
	check(cloneCount() == 3 and #E.destroyed == 0 and maxDrift(before, snapshot(E, c1)) < 1e-6,
		"世界切换 + 复活后：副本仍登记、没销毁、位置不变", string.format("clones=%d destroyed=%d", cloneCount(), #E.destroyed))
	Runtime.uninstall()
end

-- ===================================================================== B 撕裂：提前复制 + 真 CorpseMotion 倒地 + 定格
io.stdout:write("\n[B 撕裂倒地] 头飞出 -> 提前复制 -> 真 CorpseMotion 约 4 s 倒地 -> 定格\n")
do
	local E = scene()
	for _, u in ipairs(E.pieces) do E.runtime.sessionByUnit[u] = { fake = true } end
	E.run(1.5)
	E.die()
	local _, _, _, tearDeadOurs, tearDeadIas = profile(E, 0.5, nil, IAS.dead)
	costRow("B 已死 0.5 s（未撕裂）", tearDeadOurs, tearDeadIas)
	check(#E.spawned == 0, "死后 0.5 s、没撕裂：不复制")
	local headRef = E.jointNode.head
	E.pieces[headRef[1]].nodes[headRef[2]] = { world = { q = IDENT, p = { 6, 0, 1.7 } } }   -- 头离 hips 6 m：撕裂
	motionCalls.new, motionCalls.advance, motionCalls.frames, motionCalls.deform = 0, 0, 0, 0
	local spawnFrame
	for i = 1, 60 do
		E.reset_calls()
		corpseMeter.frame = 0
		E.step()
		if (E.calls.spawn_unit or 0) > 0 then spawnFrame = { i = i, all = totalCalls(E), fz = E.freezeCalls(), ours = corpseMeter.frame } break end
	end
	if spawnFrame then
		local ias = IAS.copyFrame(true)
		costRow("B 撕裂复制那一帧（三件）", { mean = spawnFrame.ours, peak = spawnFrame.ours }, { mean = ias, peak = ias })
	end
	check(spawnFrame ~= nil and #E.spawned == 3, "撕裂：不等 4 s，提前复制三件", spawnFrame and spawnFrame.i)
	print(string.format("  INFO 阶段 撕裂复制那一帧：总调用 %d，其中尸体副本独有 %d", spawnFrame and spawnFrame.all or -1, spawnFrame and spawnFrame.fz or -1))
	check(motionCalls.new == 1, "启动一次真 CorpseMotion.new（15 个关节锚点齐全）", motionCalls.new)
	local c1 = E.copyOf(1)
	local hipsRef, headR = E.jointNode.hips, E.jointNode.head
	local function hipsZ() return E.worldOf(E.copyOf(hipsRef[1]), hipsRef[2]).p[3] end
	local function headZ() return E.worldOf(E.copyOf(headR[1]), headR[2]).p[3] end
	local z0, h0 = hipsZ(), headZ()
	motionCalls.advance, motionCalls.frames, motionCalls.deform = 0, 0, 0
	local motionMax, motionFz, motionAvg, motionOurs, motionIas = profile(E, 1.0, nil, IAS.fall)
	costRow("B 铰接下落（CorpseMotion）", motionOurs, motionIas)
	print(string.format("  INFO 阶段 倒地中（真 CorpseMotion）：每帧总调用 最大 %d / 平均 %.1f，其中尸体副本独有 最大 %d；1 s 内 advance=%d frames=%d deform=%d",
		motionMax, motionAvg, motionFz, motionCalls.advance, motionCalls.frames, motionCalls.deform))
	check(motionCalls.advance >= 55 and motionCalls.advance <= 65, "倒地期间每帧推进一次 CorpseMotion.advance", motionCalls.advance)
	check(hipsZ() < z0 - 0.02, "副本随 CorpseMotion 下沉", string.format("%.3f -> %.3f", z0, hipsZ()))
	E.run(4.0)
	local stillGuard = 0
	for _, r in pairs(Freeze.state.clones) do if r.guard then stillGuard = stillGuard + 1 end end
	check(stillGuard == 0, "约 4 s 后保护期结束（guard 全清）", stillGuard)
	local z1, h1 = hipsZ(), headZ()
	print(string.format("  INFO 终态：hips z=%.3f（站立 %.3f），head z=%.3f（站立 %.3f），head-hips 高差 %.3f", z1, z0, h1, h0, h1 - z1))
	check(z1 < 0.5 and h1 < 0.6, "躺在地上：hips 与头都贴近地面（z < 0.5 / 0.6）", string.format("hips=%.3f head=%.3f", z1, h1))
	check(math.abs(h1 - z1) < 0.5, "身体放倒：头与 hips 高差小于站立时的 0.7", string.format("%.3f", h1 - z1))
	local before = snapshot(E, c1)
	motionCalls.advance = 0
	local sMax, sFz, sAvg, settledOurs, settledIas = profile(E, 1.0, nil, IAS.settled)
	costRow("B 倒地定格稳态", settledOurs, settledIas)
	print(string.format("  INFO 阶段 倒地定格后稳态：每帧总调用 最大 %d / 平均 %.1f，其中尸体副本独有 最大 %d", sMax, sAvg, sFz))
	check(motionCalls.advance == 0 and sFz <= 3 * 12 + 16, "定格后不再推进 CorpseMotion，稳态引擎调用有界", string.format("advance=%d fz=%d", motionCalls.advance, sFz))
	E.run(10)
	check(maxDrift(before, snapshot(E, c1)) < 1e-6, "再过 10 s：保持定格")
	check(#E.destroyed == 0, "全程没有销毁副本", #E.destroyed)
	local oldWorld = E.currentWorld
	E.currentWorld = { name = "mission2" }
	E.run(1.0)
	E.currentWorld = oldWorld
	E.object.health = 100
	E.run(1.0)
	check(cloneCount() == 3 and #E.destroyed == 0 and maxDrift(before, snapshot(E, c1)) < 1e-6,
		"世界切换 + 复活后：副本仍在、没销毁、位置不变", string.format("clones=%d destroyed=%d", cloneCount(), #E.destroyed))
	linkAudit(E, "B")
	Runtime.uninstall()
end

-- ===================================================================== C 失败路径与 scene_graph_link 护栏
io.stdout:write("\n[C scene_graph_link 护栏] 布局不符 / spawn 失败时一次也不调用 scene_graph_link\n")
do
	local E = scene()
	E.copyCount = COUNT - 1   -- 副本节点数不对：身份/布局断言先失败
	E.run(1.0)
	E.die()
	E.run(4.5)
	check(#E.spawned >= 1 and #E.links == 0, "布局不符：断言先于任何 scene_graph_link，零次调用", #E.links)
	Runtime.uninstall()
	E = scene()
	E.failSpawn = true   -- spawn_unit 抛错：没有 unit 可链
	E.run(1.0)
	E.die()
	E.run(4.5)
	check(#E.spawned == 0 and #E.links == 0, "spawn 失败：没有副本句柄，零次 scene_graph_link", #E.links)
	Runtime.uninstall()
	local file = assert(io.open(runtimePath, "rb"))
	local text = file:read("*a")
	file:close()
	local from = text:find("-- E12f-C2：尸体副本（CorpseFreeze）", 1, true)
	local to = text:find("function CorpseFreeze.flush", 1, true)
	local section = text:sub(from, to)
	local _, linkCalls = section:gsub("scene_graph_link%(", "")
	local applyAt = section:find("local function cz_apply", 1, true)
	local assertAt = section:find("copy identity/layout mismatch", applyAt, true)
	local linkAt = section:find("U.scene_graph_link(u, r.offset, nil)", applyAt, true)
	check(linkCalls == 2 and assertAt and linkAt and assertAt < linkAt, "源码：CorpseFreeze 只有 cz_apply 里 2 处 scene_graph_link 调用，且在身份/布局断言之后", linkCalls)
end

io.stdout:write(string.format("\n检查 %d 项，失败 %d\n", checks, failures))
os.exit(failures == 0 and 0 or 1)
