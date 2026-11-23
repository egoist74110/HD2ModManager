-- E12f-C2：尸体副本（CorpseFreeze）回归。照 IAS 1.6.4 `__hd2ir_corpse_freeze` / `__hd2ir_shared_body_lifecycle`（R:5190-6002）的行为写：
--   · 活体每 0.25 s 一个样本（os.clock 桶）、≤256 个；
--   · 死后不到 4 s 不复制，满 4 s 复制；副本先隐藏、提交后才显示、同时隐藏原件；
--   · 布局校验失败 → 回滚（恢复原件、销毁副本、一具尸体只试一次、失败的 spawn 也计数）；
--   · 满 256 个不再复制；复制成功后原件的 Lua 物理停掉；
--   · filter：原件消失 → 副本藏起来、复活后恢复；世界切换后副本仍在、不销毁；
--   · 撕裂（关节飞散）→ 用活体缓存提前复制，走受保护的铰接下落（这里用假 CorpseMotion，真的在 C1）。
-- 引擎是假的（带一棵可写的节点树）；CorpseMotion 也是假的，只实现 IAS 导出的那几个函数名。
-- 跑法：python tools/physbone-harness/run_physbone_regressions.py --filter corpse-freeze

local runtimePath = arg and arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local compatPath = "HD2PhysBoneCompat/src/physbone_compat.lua"

-- 单独在本分支上 CorpseMotion 是全局（C1 合入后是运行时里的局部表）；先放一张空表给 set_corpse_motion_ 用。
rawset(_G, "CorpseMotion", rawget(_G, "CorpseMotion") or {})

local failures, checks = 0, 0
local function check(condition, message, detail)
	checks = checks + 1
	if not condition then
		failures = failures + 1
		print(string.format("  %-4s %s%s", "FAIL", message, detail ~= nil and ("  [" .. tostring(detail) .. "]") or ""))
	else
		print(string.format("  %-4s %s%s", "PASS", message, detail ~= nil and ("  [" .. tostring(detail) .. "]") or ""))
	end
end

local Compat = assert(assert(loadfile(compatPath))())
local Runtime = assert(assert(loadfile(runtimePath))())
local Roster, Freeze = Runtime.roster_, Runtime.corpse_
assert(Roster and Freeze and Freeze.state, "运行时没有导出 corpse_")

-- 时钟：IAS 全程用 os.clock（桶、死亡时钟、30 s 过期）。测试里手动推进。
local clock = 100.0
os.clock = function() return clock end

-- ---------------------------------------------------------------- 假 CorpseMotion（只实现 IAS 导出的名字）
local motionCalls = { new = 0, advance = 0, frames = 0, deform = 0 }
local fakeMotion = { names = { "hips", "chest", "neck", "head" } }
local motionNames = {}
for _, name in ipairs(fakeMotion.names) do motionNames[name] = true end
function fakeMotion.new(anchors, velocity)
	motionCalls.new = motionCalls.new + 1
	for _, name in ipairs(fakeMotion.names) do assert(anchors[name], "缺锚点 " .. name) end
	return { anchors = anchors, velocity = velocity, steps = 0, points = { hips = { p = { anchors.hips.p[1], anchors.hips.p[2], anchors.hips.p[3] } } } }
end
function fakeMotion.advance(state, elapsed)
	motionCalls.advance = motionCalls.advance + 1
	state.steps = state.steps + 1
	state.elapsed = elapsed
	local a = state.anchors.hips.p
	state.points.hips.p = { a[1], a[2], a[3] - 0.05 * math.min(elapsed, 4) }
	return elapsed >= 4
end
function fakeMotion.frames(state)
	motionCalls.frames = motionCalls.frames + 1
	local out = {}
	for _, name in ipairs(fakeMotion.names) do
		local o = state.anchors[name].p
		out[name] = { q = { 0, 0, 0, 1 }, p = { o[1], o[2], state.points.hips.p[3] - state.anchors.hips.p[3] + o[3] }, origin = o }
	end
	return out
end
function fakeMotion.deform(frames, owner, pose)
	motionCalls.deform = motionCalls.deform + 1
	local f = assert(frames[owner], "unknown articulated owner")
	return { q = pose.q, p = { pose.p[1] + f.p[1] - f.origin[1], pose.p[2] + f.p[2] - f.origin[2], pose.p[3] + f.p[3] - f.origin[3] } }
end
function fakeMotion.owner(name) if motionNames[name] then return name end end
Runtime.set_corpse_motion_(fakeMotion)

-- ---------------------------------------------------------------- 数学（只给测试用）
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
local OFFSET, COUNT = 1, 5
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
	local jointDefs = {
		{ { "ias_hips", 2, "hips", { 0, 0, 1.0 } }, { "ias_chest", 3, "chest", { 0, 0, 1.3 } } },
		{ { "ias_neck", 2, "neck", { 0, 0, 1.45 } }, { "ias_head", 3, "head", { 0, 0, 1.6 } } },
		{},
	}
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
	S.Unit.num_scene_graph_items = function(u) count("num_scene_graph_items") return u.count end
	S.Unit.has_animation_state_machine = function(u) count("has_animation_state_machine") return u.kind == "avatar" end
	S.Unit.has_node = function(u, name) count("has_node") return u.names[name] ~= nil end
	S.Unit.node = function(u, name) count("node") return u.names[name] end
	S.Unit.num_actors = function() return 0 end
	S.Unit.world_position = function(u, n) count("world_position") local w = worldOf(u, n) return w and { w.p[1], w.p[2], w.p[3] } or nil end
	S.Unit.world_pose = function(u, n) count("world_pose") local w = worldOf(u, n) return w and { pose = w } or nil end
	S.Unit.set_local_rotation = function(u, n, q) count("set_local_rotation") local node = u.nodes[n]; node.lq = { q.x, q.y, q.z, q.w }; node.world = nil end
	S.Unit.set_local_position = function(u, n, v) count("set_local_position") local node = u.nodes[n]; node.lp = { v[1], v[2], v[3] }; node.world = nil end
	S.Unit.scene_graph_link = function(u, n, parent) count("scene_graph_link") u.nodes[n].parent = parent end
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

local function ownedByLocal(E)
	local roster = E.runtime.roster.byWorld[E.world]
	local n = 0
	for _, row in ipairs(roster.armour) do if roster.owner[row.unit] == E.avatar then n = n + 1 end end
	return n
end

-- ===================================================================== 0 前置：坐标往返
io.stdout:write("\n[0 前置] 假引擎的世界姿态能经 Compat 往返\n")
do
	local E = scene()
	local q = { 0, 0, math.sin(0.4), math.cos(0.4) }
	E.pieces[1].nodes[2] = { world = { q = q, p = { 1, 2, 3 } } }
	local pose = Compat.independent_world_pose(E.S, E.pieces[1], 2)
	check(pose ~= nil and math.abs(pose.q[3] - q[3]) < 1e-9 and math.abs(pose.q[4] - q[4]) < 1e-9 and math.abs(pose.p[2] - 2) < 1e-9,
		"world_pose -> 四元数 / 位置还原一致")
	Runtime.uninstall()
end

-- ===================================================================== 1 死亡时钟（R:4998、R:5118-5135、R:5182）
io.stdout:write("\n[1 死亡时钟] dead_since / dead_age / dead_life\n")
do
	local E = scene()
	E.step()
	check(Roster.role(E.avatar) == 1 and ownedByLocal(E) == 3, "本人 role=1，三件都归他", Roster.role(E.avatar))
	check(Roster.deadAge(E.avatar) == nil, "活着没有死亡年龄")
	E.die()
	E.step()
	check(Roster.role(E.avatar) == 3 and Roster.states[E.avatar].deadSince == clock, "health<=0：role=3 并记下 dead_since", tostring(Roster.states[E.avatar].deadSince))
	local first = Roster.states[E.avatar].deadSince
	E.run(1.0)
	check(Roster.states[E.avatar].deadSince == first, "dead_since 只记第一次（`or now`）")
	check(math.abs(Roster.deadAge(E.avatar) - 1.0) < 0.05, "dead_age = os.clock() - dead_since", string.format("%.3f", Roster.deadAge(E.avatar) or -1))
	-- 实例槽换发：新状态，不继承死亡
	Roster.slots[E.avatar] = 987654
	E.object.health = 100
	E.step()
	check(Roster.deadAge(E.avatar) == nil and Roster.role(E.avatar) == 1, "实例槽变了：不继承 dead（网络 id 复用不继承）")

	-- dead_life：直接给状态，验 IAS 的每个条件
	local U = E.avatar
	local slot = Roster.instanceSlot(U, E.runtime.frame)
	Roster.session = 1
	Roster.states[U] = { role = 3, dead = true, deadSince = 1, owner = false, friendly = true, network = 5000, world = E.world, instance = slot }
	E.object.health = 0
	local life = Roster.deadLife(U, E.world, E.runtime.frame)
	check(life ~= nil and life.kind == "friendly" and life.network == 5000 and life.instance == slot, "dead_life：死者自己的一条命")
	Roster.states[U].owner = true
	check(Roster.deadLife(U, E.world, E.runtime.frame) == nil, "dead_life：自己（owner~=false）不算")
	Roster.states[U].owner = false
	E.object.health = 100   -- 网络 id 被复用给活人
	check(Roster.deadLife(U, E.world, E.runtime.frame) == nil, "dead_life：id 被复用且单位仍有动画状态机 -> 不继承（活着的复用者）")
	E.avatar.kind = "dead-avatar"   -- 没有动画状态机了
	check(Roster.deadLife(U, E.world, E.runtime.frame) ~= nil, "dead_life：id 被复用但旧单位已无动画状态机 -> 仍是它的死亡")
	Roster.states[U].instance = slot + 1
	check(Roster.deadLife(U, E.world, E.runtime.frame) == nil, "dead_life：实例槽不一致 -> 不返回")
	Runtime.uninstall()
end

-- ===================================================================== 2 活体采样（R:5602-5634、R:5555）
io.stdout:write("\n[2 活体采样] 0.25 s 桶 / 稳态引擎调用 / 缓存上限与过期\n")
do
	local E = scene()
	E.step()
	local st = Freeze.state
	E.reset_calls()
	local buckets, sampleFrames, steadyMax = {}, 0, 0
	local lastBucket = nil
	for _ = 1, 120 do
		E.reset_calls()
		E.step(1 / 60)
		local entry = st.liveCache[Roster.instanceSlot(E.pieces[1], E.runtime.frame)]
		local b = entry and entry.bucket
		if b ~= lastBucket then sampleFrames = sampleFrames + 1; lastBucket = b; buckets[#buckets + 1] = b
		else steadyMax = math.max(steadyMax, E.freezeCalls()) end
	end
	check(sampleFrames >= 8 and sampleFrames <= 9, "2 s 内约 8 个 0.25 s 桶（每桶一次整组样本）", sampleFrames)
	check(steadyMax == 0, "两次采样之间的稳态帧：尸体副本零引擎调用", steadyMax)
	local key1 = Roster.instanceSlot(E.pieces[1], E.runtime.frame)
	local e1 = st.liveCache[key1]
	check(e1 ~= nil and e1.count == COUNT and e1.offset == OFFSET and e1.up ~= nil and math.abs(e1.up[3] - 1) < 1e-6 and e1.hash == HASHES[1],
		"样本：节点数 / 根偏移 / 头轴 up / hash 都记了")
	check(e1.joints[2] == "hips" and e1.joints[3] == "chest" and e1.native.hips ~= nil and e1.native.chest ~= nil,
		"样本里的关节来自 independentPieces + independentBones（零额外读）")
	local row = e1.shape[4]
	check(row ~= nil and math.abs(row.p[3] - (0.8 - 1.0)) < 1e-6, "形状相对 hips：节点 4 的 z = 0.8 - 1.0", row and row.p[3])
	check(Freeze.enabled and Freeze.active, "开关开着：enabled / active")

	-- 30 s 过期
	E.avatar.kind = "ghost"   -- 没有动画状态机，不再产生新样本
	E.run(31, 1.0)
	check(next(st.liveCache) == nil, "30 s 没刷新：缓存过期清掉")

	-- 上限 256：塞满，新 key 不再进
	Runtime.uninstall()
	E = scene()
	st = Freeze.state
	for i = 1, 256 do st.liveCache[100000 + i] = { time = clock, bucket = -1 } end
	E.run(0.5)
	check(st.liveCache[Roster.instanceSlot(E.pieces[1], E.runtime.frame)] == nil, "缓存已满 256：新单位不再进缓存")
	Runtime.uninstall()

	-- 撕裂的样本不进缓存
	E = scene()
	E.step()
	E.run(0.6)
	st = Freeze.state
	local before = st.liveCache[Roster.instanceSlot(E.pieces[2], E.runtime.frame)]
	E.pieces[2].nodes[3] = { world = { q = IDENT, p = { 6, 0, 1.6 } } }   -- 头飞出去 6 m
	E.run(0.6)
	local after = st.liveCache[Roster.instanceSlot(E.pieces[2], E.runtime.frame)]
	check(before ~= nil and after == before, "关节飞散（离 hips > 3.5 m）：这一桶的样本被丢掉，缓存保持上一份完整样本")
	Runtime.uninstall()

	-- 开关关：零成本、什么都不做
	E = scene({ enabled = false })
	E.reset_calls()
	E.run(1.0)
	check(not Freeze.enabled and not Freeze.active and E.freezeCalls() == 0 and next(Freeze.state.liveCache) == nil,
		"开关关（默认）：没有任何尸体副本相关的引擎调用、没有缓存")
	Runtime.uninstall()
end

-- ===================================================================== 3 复制时机、提交顺序、物理停写（R:5850-5950）
io.stdout:write("\n[3 复制] 死后 4 s 才复制；先隐藏副本、提交后才显示并隐藏原件；原件物理停写\n")
do
	local E = scene()
	E.run(1.0)
	for _, u in ipairs(E.pieces) do E.runtime.sessionByUnit[u] = { fake = true } end
	local st = Freeze.state
	E.die()
	E.run(3.9)
	check(#E.spawned == 0 and st.created == 0, "死后 3.9 s：没有副本、没有 spawn", #E.spawned)
	check(E.pieces[1].visible and E.pieces[2].visible and E.pieces[3].visible, "此时原件仍可见")
	E.run(0.2)
	check(#E.spawned == 3 and st.created == 3, "死后 > 4 s：三件各生成一个副本（created=3）", #E.spawned)
	local c1, c2, c3 = E.copyOf(1), E.copyOf(2), E.copyOf(3)
	check(c1.visible and c2.visible and c3.visible, "副本最终可见")
	check(not E.pieces[1].visible and not E.pieces[2].visible and not E.pieces[3].visible, "原件被隐藏")
	-- 事件顺序：副本先 false（spawn 后立刻），再全部 true，之后才隐藏原件
	local firstTrue, lastCopyFalse, firstOriginalFalse
	for i, ev in ipairs(E.events) do
		if ev.u.kind == "copy" and ev.visible == false then lastCopyFalse = i end
		if ev.u.kind == "copy" and ev.visible == true and not firstTrue then firstTrue = i end
		if ev.u.kind == "armour" and ev.visible == false and not firstOriginalFalse then firstOriginalFalse = i end
	end
	check(lastCopyFalse ~= nil and firstTrue ~= nil and firstOriginalFalse ~= nil and lastCopyFalse < firstTrue and firstTrue < firstOriginalFalse,
		"可见性顺序：副本 spawn 即隐藏 -> 提交后才显示 -> 然后才隐藏原件", string.format("%s/%s/%s", tostring(lastCopyFalse), tostring(firstTrue), tostring(firstOriginalFalse)))
	-- 布局：副本节点树被压平在根下，根 = hips 世界姿态，其余 = 相对 hips 的形状
	local root = E.worldOf(c1, OFFSET)
	check(math.abs(root.p[3] - 1.0) < 1e-6, "副本根节点 = 死者 hips 的世界姿态", root.p[3])
	local w4 = E.worldOf(c1, 4)
	check(c1.nodes[2].parent == OFFSET and c1.nodes[4].parent == OFFSET and math.abs(w4.p[3] - 0.8) < 1e-6,
		"副本的其余节点链在根下，世界姿态与原件一致", w4.p[3])
	check(E.runtime.sessionByUnit[E.pieces[1]] == nil and E.runtime.sessionByUnit[E.pieces[2]] == nil and E.runtime.sessionByUnit[E.pieces[3]] == nil,
		"原件的 Lua 物理会话停掉（替代原生 freeze_corpse_physics）")
	check(Freeze.physicsStopped(E.pieces[1], E.runtime.frame) and not Freeze.physicsStopped(E.avatar, E.runtime.frame),
		"physics_sync 用 physicsStopped 判定：原件停写，别的不受影响")
	-- 原件移出名单，副本不进名单
	E.run(0.2)
	local roster = E.runtime.roster.byWorld[E.world]
	local listed = {}
	for _, row in ipairs(roster.armour) do listed[row.unit] = true end
	check(not listed[c1] and not listed[c2] and not listed[c3], "副本不在装甲名单里")
	check(not listed[E.pieces[1]] and not listed[E.pieces[2]] and not listed[E.pieces[3]], "原件不在装甲名单里（没有活体缓存的普通定格：skip = not guard）")
	local pinned = 0
	for _ in pairs(Roster.corpsePins) do pinned = pinned + 1 end
	check(pinned >= 3, "副本 / 原件 / 世界 / 主人的槽位被钉住（槽位表清理时不删）", pinned)
	check(E.runtime.sessionByUnit[E.pieces[1]] == nil and #(E.logs) >= 0 and (function()
		for _, line in ipairs(E.logs) do if line:find("frozen owner=", 1, true) then return true end end
	end)(), "日志里有 frozen owner=...")
	Runtime.uninstall()
end

-- ===================================================================== 4 回滚（R:5928-5950）
io.stdout:write("\n[4 回滚] 布局校验失败 / 隐藏原件失败：恢复原件、销毁副本、只试一次、失败的 spawn 也计数\n")
do
	local E = scene()
	E.copyCount = COUNT - 1   -- 副本节点数不对 -> apply 的布局断言失败
	E.run(1.0)
	E.die()
	E.run(4.3)
	local st = Freeze.state
	check(#E.spawned >= 1 and #E.destroyed == #E.spawned, "布局不符：已 spawn 的副本全部销毁", string.format("spawn=%d destroy=%d", #E.spawned, #E.destroyed))
	check(E.pieces[1].visible and E.pieces[2].visible and E.pieces[3].visible, "原件仍可见（回滚没把它藏起来）")
	check(next(st.clones) == nil and next(st.frozen) == nil, "没有登记副本")
	check(st.failed[Roster.instanceSlot(E.avatar, E.runtime.frame)] == true, "这具尸体记为失败，不再重试")
	local created = st.created
	E.run(1.0)
	check(st.created == created and #E.spawned == 1, "之后不再尝试（一具尸体只试一次）", string.format("created=%d spawned=%d", st.created, #E.spawned))
	check(created >= 1, "失败的 spawn 也计数（created）", created)
	Runtime.uninstall()

	-- 隐藏第二件原件时失败：副本全部销毁、已隐藏的原件重新显示
	E = scene()
	E.failHide = function(u) return u == E.pieces[2] end
	E.run(1.0)
	E.die()
	E.run(4.3)
	check(#E.spawned == 3 and #E.destroyed == 3, "提交中途失败：三个副本都销毁", string.format("spawn=%d destroy=%d", #E.spawned, #E.destroyed))
	check(E.pieces[1].visible and E.pieces[2].visible and E.pieces[3].visible, "已隐藏的原件重新显示")
	check(next(Freeze.state.clones) == nil, "没有登记副本")
	Runtime.uninstall()

	-- 销毁也失败：副本被隔离登记，不回到普通发现里
	E = scene()
	E.copyCount = COUNT - 1
	E.failDestroy = true
	E.run(1.0)
	E.die()
	E.run(4.3)
	local quarantined = 0
	for _, record in pairs(Freeze.state.clones) do if record.quarantine then quarantined = quarantined + 1 end end
	check(quarantined >= 1, "销毁失败的副本登记为隔离", quarantined)
	local copy = E.spawned[1]
	local roster = E.runtime.roster.byWorld[E.world]
	local listed = false
	for _, row in ipairs(roster.armour) do if row.unit == copy then listed = true end end
	check(not listed and not copy.visible, "隔离的副本不进装甲名单、保持隐藏")
	Runtime.uninstall()
end

-- ===================================================================== 5 上限 256（R:5663、R:5894）
io.stdout:write("\n[5 上限] 一个会话最多 256 个副本\n")
do
	local E = scene()
	Freeze.state.created = 254   -- 三件需要 3 个：254 + 3 > 256
	E.run(1.0)
	E.die()
	E.run(4.5)
	check(#E.spawned == 0 and Freeze.state.created == 254, "254 + 3 > 256：整组不复制，不 spawn", #E.spawned)
	local noted = false
	for _, line in ipairs(E.logs) do if line:find("copy lifetime limit", 1, true) then noted = true end end
	check(noted, "日志记了 copy lifetime limit")
	Runtime.uninstall()
	E = scene()
	Freeze.state.created = 253   -- 253 + 3 = 256：恰好够
	E.run(1.0)
	E.die()
	E.run(4.5)
	check(#E.spawned == 3 and Freeze.state.created == 256, "253 + 3 = 256：恰好够", Freeze.state.created)
	Runtime.uninstall()
end

-- ===================================================================== 6 撕裂：提前复制 + 受保护的铰接下落（R:5602、R:5723-5810、R:5811-5849）
io.stdout:write("\n[6 撕裂] 关节飞散 -> 用活体缓存提前复制；保护期里跟随铰接帧；4 s 后定格\n")
do
	local E = scene()
	E.run(1.0)
	E.die()
	E.run(0.5)
	check(#E.spawned == 0, "没撕裂：0.5 s 不复制")
	-- 头被打飞：头节点离 hips 6 m
	E.pieces[2].nodes[3] = { world = { q = IDENT, p = { 6, 0, 1.6 } } }
	E.run(0.6)
	check(#E.spawned == 3, "撕裂（早退）：不等 4 s，用活体缓存立刻复制三件", #E.spawned)
	check(motionCalls.new >= 1, "受保护复制：启动 CorpseMotion.new")
	local st = Freeze.state
	local guarded = 0
	for _, record in pairs(st.clones) do if record.guard then guarded = guarded + 1 end end
	check(guarded == 3, "三件副本都带保护期（guard）", guarded)
	-- 保护期：原件被排除（motion 启动）、副本跟随
	E.run(0.2)
	local roster = E.runtime.roster.byWorld[E.world]
	local listed = false
	for _, row in ipairs(roster.armour) do if row.unit == E.pieces[1] then listed = true end end
	check(not listed, "铰接下落期间原件不在名单里（skip = guard.motion ~= nil）")
	check(motionCalls.advance >= 1 and motionCalls.deform >= 1, "每帧推进 CorpseMotion 并把 deform 结果写到副本", string.format("advance=%d deform=%d", motionCalls.advance, motionCalls.deform))
	local c1 = E.copyOf(1)
	local before = E.worldOf(c1, 2).p[3]
	E.run(1.5)
	local mid = E.worldOf(c1, 2).p[3]
	check(mid < before, "铰接帧作用到副本：节点随 hips 下沉", string.format("%.3f -> %.3f", before, mid))
	E.run(3.0)
	local still = 0
	for _, record in pairs(st.clones) do if record.guard then still = still + 1 end end
	check(still == 0, "motion 完成：guard / shape / live_cache 清掉", still)
	local last = E.worldOf(c1, 2).p[3]
	E.run(0.5)
	check(math.abs(E.worldOf(c1, 2).p[3] - last) < 1e-6, "定格后副本不再动（固定核心）")
	local ownerSlot = Roster.instanceSlot(E.avatar, E.runtime.frame)
	local anyCache = false
	for key in pairs(st.liveCache) do anyCache = true end
	check(not anyCache, "定格后活体缓存清空")
	Runtime.uninstall()
end

-- ===================================================================== 7 filter：副本跟随 / 原件消失 / 复活 / 世界切换（R:5954-6000）
io.stdout:write("\n[7 filter] 原件消失 -> 副本藏起来；回来 -> 恢复；换世界 -> 副本仍在且不销毁\n")
do
	local E = scene()
	E.run(1.0)
	E.die()
	E.run(4.4)
	local c1 = E.copyOf(1)
	local st = Freeze.state
	check(#E.spawned == 3 and c1.visible, "前置：已复制并显示副本")
	-- 死者移动：副本跟着核心走
	E.avatar.nodes[2] = { world = { q = IDENT, p = { 0, 0, 1.0 } } }
	E.run(0.1)
	-- 原件从世界里消失（被引擎回收）：副本必须藏起来
	E.pieces[1].world = world2
	E.run(0.1)
	check(not c1.visible, "原件不在枚举里：副本被隐藏")
	E.pieces[1].world = E.world
	E.run(0.1)
	check(c1.visible and not E.pieces[1].visible, "原件回来：副本重新显示、原件保持隐藏（IAS：副本不销毁、可复活）")
	-- 世界切换：枚举里没有这个世界
	local destroyedBefore = #E.destroyed
	E.currentWorld = { name = "other" }
	E.run(0.3)
	local cloneCount = 0
	for _ in pairs(st.clones) do cloneCount = cloneCount + 1 end
	check(cloneCount == 3 and #E.destroyed == destroyedBefore, "换了世界：clones 不清空、不销毁副本（IAS 常态不删）", string.format("clones=%d destroy=%d", cloneCount, #E.destroyed - destroyedBefore))
	check(Freeze.active, "有副本时 active 保持（开关关了也会继续过滤）")
	E.currentWorld = E.world
	E.run(0.2)
	check(c1.visible and cloneCount == 3, "回到原世界：副本继续跟随并可见")
	-- 稳态引擎调用（按帧）：每个副本固定写入 + 校验
	E.reset_calls()
	E.step()
	local perFrame = E.freezeCalls()
	local detail = {}
	for _, name in ipairs({ "world_pose", "to_elements", "set_local_rotation", "set_local_position", "from_elements", "Vector3", "update_unit",
		"set_unit_visibility", "has_node", "node" }) do detail[#detail + 1] = name .. "=" .. (E.calls[name] or 0) end
	print("  INFO 稳态每帧（3 个副本、已定格）尸体副本引擎调用 " .. perFrame .. "：" .. table.concat(detail, " "))
	check(perFrame > 0 and perFrame <= 3 * 12 + 16, "稳态每帧调用有界：每副本约 12 次 + 每个主人一次 core 读取", perFrame)
	-- 开关关后：已有副本继续维护，但不再新建
	Runtime.setOptions({ corpseFreeze = false })
	E.run(0.2)
	check(not Freeze.enabled and Freeze.active and c1.visible, "开关关了：已有副本继续由 filter 维护")
	Runtime.uninstall()
end

-- ===================================================================== 8 共享身体化身号（R:5190-5246）
io.stdout:write("\n[8 化身号] epoch / world / network / kind 变了才换号\n")
do
	local E = scene()
	E.run(0.3)
	local life = Freeze.lifecycle
	local slot = Roster.instanceSlot(E.avatar, E.runtime.frame)
	local t1 = life.token(slot)
	check(t1 ~= nil, "本人拿到化身号")
	E.run(0.3)
	check(life.token(slot) == t1, "同一条命：号不变")
	E.object.health = 0
	E.run(0.3)
	check(life.token(slot) == nil and life.forgotten >= 1, "死亡：证据没了，忘掉旧号")
	Runtime.uninstall()
end

-- ===================================================================== 9 挂钩：真正的 drive 入口
io.stdout:write("\n[9 挂钩] render -> drive：没有物理会话的帧也要跑尸体副本；写骨之后还有一处\n")
do
	local E = scene({ viaRender = true })
	E.run(1.0)
	local st = Freeze.state
	check(next(st.liveCache) ~= nil, "经 render/drive：活体样本在采（Roster.update 带 begin，drive 带 corpse_frame）")
	E.die()
	E.run(4.3)
	check(#E.spawned == 3 and not E.pieces[1].visible and E.copyOf(1).visible, "经 render/drive：死后 4 s 复制并替换", #E.spawned)
	Runtime.uninstall()
	local file = assert(io.open(runtimePath, "rb"))
	local text = file:read("*a")
	file:close()
	local _, hooks = text:gsub("corpse_frame%(runtime%)", "")
	local anchor = text:find('phase(runtime, "写骨 后")', 1, true)
	local hook = anchor and text:find("corpse_frame(runtime)", anchor, true)
	check(hooks >= 3 and hook ~= nil and hook - anchor < 400, "drive 源码：早退分支与『写骨 后』各有一处 corpse_frame 调用", hooks)
	check(text:find("CorpseFreeze.physicsStopped(armour.unit, frame)", 1, true) ~= nil, "physics_sync 源码：已停物理的原件不再绑定")
end

io.stdout:write(string.format("\n检查 %d 项，失败 %d\n", checks, failures))
os.exit(failures == 0 and 0 or 1)
