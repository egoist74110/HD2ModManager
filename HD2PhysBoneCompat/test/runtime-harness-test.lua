-- 生产运行时（physbone_runtime.lua）集成自检
--
-- 这是**要部署给玩家**的那一份，所以必须验证它自己能把整条链路跑起来：
-- install → 发现 → 就绪 → 逐个 unit 选容器 → attach → 每帧驱动 → 换甲重扫 → uninstall 还回去。
--
-- 用法：luajit tmp/lua-check/runtime-harness-test.lua [runtime] [compat]

local runtimePath = arg[1] or "HD2PhysBoneCompat/src/physbone_runtime.lua"
local compatPath = arg[2] or "HD2PhysBoneCompat/src/physbone_compat.lua"

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-52s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local Compat = assert(assert(loadfile(compatPath))())

-- 认得"玩家"用的挂点名（实机里按名字问，不按 id）
local SUPPORT_MG = "support_mg"

-- ---------------------------------------------------------------------------
-- 假引擎
-- ---------------------------------------------------------------------------
local AVATAR_NODES, ARMOR_NODES = 107, { 171, 148, 132, 121, 108, 96 }
local FILLER = 2400

local engine = { alive = {}, nodes = {}, anim = {}, pos = {}, time = 10.0, writes = 0 }
local world = { name = "w" }

-- IAS 的 profile 表只认 16 位十六进制资源 id：装甲件的 id 一律是 16 位 hex，名单是装载时就定死的静态池。
-- （旧写法 "#ID[t6a1]" 这类名字不是资源 id，每帧枚举按 hash 找就永远找不到它们。）
local HEX_ID = "^#ID%[%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x%x%]$"
local PROFILE_POOL, nextProfile = {}, 0
for index = 1, 160 do PROFILE_POOL[index] = string.format("%016x", 0xd0000000 + index) end

local function newUnit(tag, nodeCount, animated, resource)
	if not animated and resource ~= nil and not resource:match(HEX_ID) then
		nextProfile = nextProfile + 1
		assert(nextProfile <= #PROFILE_POOL, "profile 池不够用")
		resource = "#ID[" .. PROFILE_POOL[nextProfile] .. "]"
	end
	local unit = { tag = tag, resource = resource or ("#ID[" .. tag .. "]"), ids = {}, nodeOf = {} }
	engine.alive[unit] = true
	engine.nodes[unit] = nodeCount
	engine.anim[unit] = animated
	return unit
end

-- 骨名（带命名空间，模拟新转换器产物）
local BONE_NAMES = {}
for index = 1, 4 do BONE_NAMES[index] = string.format("jabcdef012345/source_extra_%d", index) end

local RIG = (function()
	local params = { tipMass = 200, yawStiffness = 50, yawDamping = 7,
		pitchStiffness = 50, pitchDamping = 7, angleLimit = 0.611, length = 30 }
	local bones = {}
	for index = 1, 4 do
		bones[index] = { index = index, name = BONE_NAMES[index],
			parent = (index <= 2) and (index - 1) or 0,
			parentName = (index == 1 or index == 3) and "head" or BONE_NAMES[index - 1],
			pos = { 0, -0.1 * (index - 1), 0 }, params = params, flags = 0x31 }
	end
	return { bones = bones }
end)()

local S = { Application = {}, Unit = {}, World = {}, Vector3 = {}, Quaternion = {}, Matrix4x4 = {} }
local pose, vector, quat = { kind = "pose" }, { kind = "vector" }, { kind = "quat" }

-- 逐 unit 的引擎调用计数（T23 用）：验证"unit 死掉之后不再碰它"。
-- 只数**读骨架/写姿态**这几类；`alive` 不数 —— 判活本来就是允许做的动作。
engine.hits = {}
local function hit(unit, kind)
	if unit == nil then return end
	local row = engine.hits[unit]
	if row == nil then row = {}; engine.hits[unit] = row end
	row[kind] = (row[kind] or 0) + 1
end

S.Application.time_since_launch = function() return engine.time end
S.Application.worlds = function() return { world } end
S.Application.string_to_id32 = function(text)
	local sum = 0
	for index = 1, #text do sum = sum + text:byte(index) * index end
	return sum * 1000 + #text
end
S.World.units = function() return engine.unitsArray() end
S.World.update_unit = function() end
S.Unit.alive = function(unit) return engine.alive[unit] == true end
S.Unit.num_scene_graph_items = function(unit) hit(unit, "nodes") return engine.nodes[unit] or 0 end
S.Unit.has_animation_state_machine = function(unit) return engine.anim[unit] == true end
-- ⚠️ 键是**骨名**，不是 id：实机里 `string_to_id32` 返回的 `IdString32` 会失效
-- （我们自己的 snapshot 探针印过 `[Stale IdString32]`），所以匹配一律按名字问。
-- `engine.namesOnly` 打开时，传数字（id）会直接抛错 —— T18 用它来钉死"别再退回按 id 匹配"。
S.Unit.has_node = function(unit, key)
	if engine.alive[unit] ~= true then return false end
	if engine.namesOnly == true and type(key) ~= "string" then
		error("[Stale IdString32] 只能按骨名问")
	end
	hit(unit, "has")
	return unit.ids[key] == true
end
S.Unit.node = function(unit, key) hit(unit, "node") return unit.nodeOf[key] end
S.Unit.debug_name = function(unit) return unit.resource end
S.Unit.name_hash = function(unit) return unit.resource end
S.Unit.local_pose = function(unit) hit(unit, "pose") return pose end
S.Unit.local_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
S.Unit.world_position = function(u) hit(u, "world") return { unit = u } end
S.Unit.world_rotation = function() return { x = 0, y = 0, z = 0, w = 1 } end
S.Unit.set_local_pose = function(unit) hit(unit, "write") engine.writes = engine.writes + 1 end
S.Unit.set_local_rotation = function(unit) hit(unit, "write") engine.writes = engine.writes + 1 end
-- 位置按 unit 分别给出（"离玩家多远"要真的能量出来，否则没法验尸体排除）
S.Vector3.to_elements = function(v)
	local p = engine.pos[v and v.unit] or { 0, 0, 0 }
	return p[1], p[2], p[3]
end
S.Quaternion.to_elements = function(q) return q.x, q.y, q.z, q.w end
S.Matrix4x4.to_elements = function() return 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.2, 0.3 end
S.Matrix4x4.is_valid = function() return true end
S.Matrix4x4.from_quaternion_position = function() return pose end
S.Quaternion.axis_angle = function(axis, angle)
	local s = math.sin(angle / 2)
	return { x = 0, y = 0, z = s, w = math.cos(angle / 2) }
end
S.Quaternion.from_elements = function(x, y, z, w) return { x = x, y = y, z = z, w = w } end
setmetatable(S.Vector3, { __call = function() return vector end })

-- 护甲 unit：装上骨名（**按名字存**，和实机的匹配口径一致），节点下标从 10 起
local function dress(unit)
	local nextNode = 10
	for _, name in ipairs(BONE_NAMES) do
		unit.ids[name] = true
		unit.nodeOf[name] = nextNode
		nextNode = nextNode + 1
	end
	unit.ids["head"] = true
	unit.nodeOf["head"] = 9
end

local avatar = newUnit("avatar", AVATAR_NODES, true, "#ID[avatar]")
-- avatar 的指纹：带 support_mg 挂点（发现逻辑靠"有动画状态机 + 有 support_mg"认它）
do
	local mgName = SUPPORT_MG
	avatar.ids[mgName] = true
	avatar.nodeOf[mgName] = 5
end
local armors = {}
for index, count in ipairs(ARMOR_NODES) do
	armors[index] = newUnit("armor" .. index, count, false, string.format("#ID[c%015x]", index))
	dress(armors[index])
end
local fillers = {}
for index = 1, FILLER do fillers[index] = newUnit("f" .. index, 5 + (index % 20), false) end

-- `engine.hidden`：把某个 unit 从 `World.units()` 的返回里摘掉，但**不改** `engine.alive`。
-- 现实里对应的就是"引擎已经释放了它，而我们的句柄还指向那块旧内存"—— T24/T26 用它模拟。
engine.hidden = {}
function engine.unitsArray()
	local list = {}
	if avatar ~= nil and engine.hidden[avatar] ~= true then list[#list + 1] = avatar end
	-- 被销毁的 unit（alive=false）不会再出现在真引擎的枚举里；IAS 的每帧枚举也没有"先判活"这一步。
	for _, unit in ipairs(armors) do
		if engine.hidden[unit] ~= true and engine.alive[unit] ~= false then list[#list + 1] = unit end
	end
	for _, unit in ipairs(fillers) do
		if engine.hidden[unit] ~= true then list[#list + 1] = unit end
	end
	return list
end

rawset(_G, "stingray", S)

-- ---------------------------------------------------------------------------
local Runtime = assert(assert(loadfile(runtimePath))())
-- E12d/e：照 IAS，avatar 与装甲件都是每帧从引擎枚举出来的：
--   avatar = World.units_by_resource(world, 资源路径)；装甲件 = units_by_resource(world, IdString64.from_hex(哈希))。
S.IdString64 = { from_hex = function(hash) return { hex = hash } end }
S.World.num_units = function() return #engine.unitsArray() end
S.World.units_by_resource = function(_, key)
	local out = {}
	if type(key) == "table" then
		-- 资源索引：只列还活着、引擎还认的 unit（被销毁的不会再出现，和真引擎一样）。
		for _, unit in ipairs(armors) do
			if engine.alive[unit] == true and engine.hidden[unit] ~= true and unit.resource:find(key.hex, 1, true) then
				out[#out + 1] = unit
			end
		end
		return out
	end
	for _, unit in ipairs(engine.unitsArray()) do
		if engine.anim[unit] == true and unit.ids[SUPPORT_MG] then out[#out + 1] = unit end
	end
	return out
end
do
	local rawInstall = Runtime.install
	Runtime.install = function(stingray, options)
		options = options or {}
		if options.independentPieces == nil then
			options.independentPieces = {}
			for index = 1, #ARMOR_NODES do options.independentPieces[string.format("#ID[c%015x]", index)] = { "x" } end
		end
		if options.profileHashes == nil then options.profileHashes = PROFILE_POOL end
		if options.identityPollSeconds == nil then options.identityPollSeconds = 0 end
		if options.rosterNegativeSeconds == nil then options.rosterNegativeSeconds = 0 end
		return rawInstall(stingray, options)
	end
end
local logPath = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime.txt"

print("T1 install 后能自己发现并挂上")
do
	local originalRender = function() end
	rawset(_G, "render", originalRender)

	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
	})
	check("install 返回运行时状态", type(runtime) == "table" and runtime.installed == true)
	check("install 是幂等的", Runtime.install(S, { compat = Compat }) == runtime)
	check("挂上了 render 钩子", rawget(_G, "render") ~= originalRender)

	local probeRender = rawget(_G, "render")
	local writesBefore = engine.writes
	for _ = 1, 60 do
		engine.time = engine.time + 1 / 72
		probeRender()
	end
	check("自动完成了 attach 并写了姿态", engine.writes > writesBefore,
		string.format("写入 %d → %d", writesBefore, engine.writes))
	check("会话数 = 候选护甲件数", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s 期望=%d", tostring(runtime.sessions and #runtime.sessions), #ARMOR_NODES))
	-- dt 必须跟随引擎时间（这里每帧推进 1/72），不能停在写死的 1/60：
	-- 回调实测约 70 次/秒，写死 1/60 会让物理比真实时间慢 15~30%。
	check("dt 跟随引擎时间", math.abs((runtime.dt or 0) - 1 / 72) < 1e-9,
		string.format("dt=%.6f 期望=%.6f", runtime.dt or 0, 1 / 72))

	print("T2 换甲（护甲 unit 全部销毁）⇒ 自动重扫重挂")
	for _, unit in ipairs(armors) do engine.alive[unit] = false end
	local writesAtKill = engine.writes
	for _ = 1, 30 do engine.time = engine.time + 1 / 72; probeRender() end
	check("停手后不再写死 Unit", engine.writes == writesAtKill,
		string.format("新增写入 %d", engine.writes - writesAtKill))

	local armors2 = {}
	for index, count in ipairs(ARMOR_NODES) do
		armors2[index] = newUnit("b" .. index, count, false, string.format("#ID[c%015x]", index))
		dress(armors2[index])
	end
	armors = armors2
	for _ = 1, 120 do engine.time = engine.time + 1 / 72; probeRender() end
	check("重挂成功", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	check("重挂后继续驱动", engine.writes > writesAtKill, string.format("总写入 %d", engine.writes))

	print("T3 uninstall 把 render 还回去")
	check("uninstall 成功", Runtime.uninstall() == true)
	check("render 已还原", rawget(_G, "render") == originalRender)
	check("安装标记已清除", rawget(_G, "__hd2physbone_runtime") == nil)
end

-- ---------------------------------------------------------------------------
print("T4 缺少 compat 模块时明确报错（不静默）")
do
	local ok, err = pcall(Runtime.install, S, {})
	check("install 抛错而不是静默", ok == false and tostring(err):find("physbone_compat") ~= nil,
		tostring(err))
end

-- `Application.time_since_launch` **每帧只更新一次**，而 render 钩子实测每帧被调用多次。
-- 如果后面那几次沿用上一次的 dt 再积一遍，一帧就积了 N 倍的时间：物理快 N 倍、
-- 永远停不下来。实机表现就是"头发飘逸过头"——所以这条必须钉死。
print("T6 同一时间步内的重复调用不重复积分")
do
	local logPath3 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t6.txt"
	os.remove(logPath3)
	local armorsB = {}
	for index, count in ipairs(ARMOR_NODES) do
		armorsB[index] = newUnit("t6" .. index, count, false, "#ID[t6" .. index .. "]")
		dress(armorsB[index])
	end
	armors = armorsB
	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath3,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
		heartbeatFrames = 10,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 40 do engine.time = engine.time + 1 / 120; probeRender() end
	check("先挂上", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	local driveBefore = runtime.driveFrames
	local repeatBefore = runtime.repeatCalls or 0
	for _ = 1, 5 do probeRender() end
	check("同拍重复调用不推进驱动", runtime.driveFrames == driveBefore,
		string.format("driveFrames %d → %d", driveBefore, runtime.driveFrames))
	check("同拍重复调用被计数", (runtime.repeatCalls or 0) == repeatBefore + 5,
		string.format("repeatCalls %d → %d", repeatBefore, runtime.repeatCalls or 0))

	engine.time = engine.time + 1 / 120
	probeRender()
	check("时钟前进才推进驱动", runtime.driveFrames == driveBefore + 1,
		string.format("driveFrames=%d", runtime.driveFrames))

	-- 时钟永久卡住时不能静默冻住：兜底恢复驱动，并大声说一次。
	runtime.options.stuckClockLimit = 5
	for _ = 1, 5 do probeRender() end
	check("时钟卡死后兜底恢复驱动", runtime.driveFrames == driveBefore + 2,
		string.format("driveFrames=%d", runtime.driveFrames))
	local handle = io.open(logPath3, "r")
	local log = handle and handle:read("*a") or ""
	if handle then handle:close() end
	check("兜底时明确告警", log:find("引擎时钟连续", 1, true) ~= nil)

	Runtime.uninstall()
end


-- ---------------------------------------------------------------------------
-- T7：阵亡 → 尸体留在原地 → 新玩家实体出现。
--
-- 这是实机反馈的原始问题：玩家阵亡后尸体**不会被销毁**，`pruneSessions` 只看
-- `Unit.alive` / 节点数，判不出"人已经换了"，于是我们一直驱动着尸体，
-- 而新玩家的护甲 unit 从来没被挂过 —— 表现就是"复活之后没有物理"。
--
-- ⚠️ **2.0.0 之后这个场景的模型变了，断言也跟着变。**
-- 1.9.x 只有一个"玩家"概念，靠"出现新的 avatar 候选"判换人；多人局里那条判据是**错的**
-- （队友一进房间就把我的会话全丢、重挂到队友身上）。2.0.0 改成：
--   每套护甲归属**离它最近的那个玩家**，会话记住主人；主人不在才释放。
-- 所以"换人"不再是一个判定事件，而是两件独立的事：
--   ① 尸体不再算玩家（HD2 里尸体是 ragdoll，没有动画状态机）⇒ 它那套护甲**失去主人**；
--   ② 失去主人的护甲离"最近的玩家"很远 ⇒ 被 `maxArmorDistance` 过滤掉，不会被挂回去。
-- 这里验的仍然是最初那三件事，只是换成新模型的等价形式：
--   ① 尸体那套护甲失去主人并被释放（而不是"检测到换人"）；
--   ② 尸体被**交还基准姿态**（不留在最后写入的姿势，像被冻住）；
--   ③ 新玩家的护甲被挂上，而尸体那套（离新玩家 50 m）不再被驱动。
print("T7 阵亡→复活：释放尸体、重挂新玩家")
do
	local logPath7 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t7.txt"
	os.remove(logPath7)
	local function readLog7()
		local handle = io.open(logPath7, "r")
		if not handle then return "" end
		local text = handle:read("*a")
		handle:close()
		return text or ""
	end

	-- 玩家 A 在原点，带 6 件护甲（都贴着玩家）
	local avatarA = newUnit("t7avatarA", AVATAR_NODES, true, "#ID[t7avatarA]")
	do
		local mgName = SUPPORT_MG
		avatarA.ids[mgName] = true
		avatarA.nodeOf[mgName] = 5
	end
	local corpseArmors = {}
	for index, count in ipairs(ARMOR_NODES) do
		corpseArmors[index] = newUnit("t7c" .. index, count, false, "#ID[t7c" .. index .. "]")
		dress(corpseArmors[index])
		engine.pos[corpseArmors[index]] = { 0, 0, 0 }
	end
	avatar = avatarA
	armors = corpseArmors

	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath7,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1,
		validateFrames = 1,      -- 每帧都校验，测试才跑得快
		maxArmorDistance = 3.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("先挂上尸体那套", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	local writesBeforeDeath = engine.writes

	-- 阵亡：玩家 A 的 unit **仍然活着**（尸体就是它，这是 HD2 的实际情况），护甲也留着。
	-- 但尸体是 ragdoll ⇒ **不再有动画状态机** ⇒ `findAvatars` 不再把它算作玩家。
	-- 这一步是 2.0.0 判"主人不在了"的**唯一**依据（旧版靠"出现新候选"判换人，多人局会误判）。
	engine.anim[avatarA] = false

	-- 新玩家 B 在 50 米外出现。死掉那套护甲留在原点，离 B 有 50 m，
	-- 应当被 `maxArmorDistance` 挡在门外，绝不重新驱动。
	local avatarB = newUnit("t7avatarB", AVATAR_NODES, true, "#ID[t7avatarB]")
	do
		local mgName = SUPPORT_MG
		avatarB.ids[mgName] = true
		avatarB.nodeOf[mgName] = 5
	end
	engine.pos[avatarB] = { 50, 0, 0 }
	local newArmors = {}
	for index, count in ipairs(ARMOR_NODES) do
		newArmors[index] = newUnit("t7n" .. index, count, false, "#ID[t7n" .. index .. "]")
		dress(newArmors[index])
		engine.pos[newArmors[index]] = { 50, 0, 0 }
	end
	avatar = avatarB
	local combined = {}
	for _, unit in ipairs(corpseArmors) do combined[#combined + 1] = unit end
	for _, unit in ipairs(newArmors) do combined[#combined + 1] = unit end
	armors = combined

	for _ = 1, 120 do engine.time = engine.time + 1 / 120; probeRender() end
	local text = readLog7()
	check("尸体那套护甲失去主人并被释放", text:find("释放了", 1, true) ~= nil,
		"主人（尸体）不再算玩家时应当整组释放")
	check("尸体被交还基准姿态", text:find("已交还", 1, true) ~= nil)
	check("重挂到了新玩家身上",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
	local allNew = true
	for _, session in ipairs(runtime.sessions or {}) do
		if not tostring(session.unitTag):find("t7n", 1, true) then allNew = false end
	end
	check("挂的是新玩家那套，不是尸体", allNew,
		tostring(runtime.sessions and runtime.sessions[1] and runtime.sessions[1].unitTag))
	check("尸体那套护甲没有被重新挂上",
		(text:find("离它所属玩家超过", 1, true) ~= nil), "应记录被距离过滤掉的尸体件")

	-- 死亡之后没再往尸体写姿态（只在新玩家身上写）。
	check("新玩家有物理写入", engine.writes > writesBeforeDeath,
		string.format("%d → %d", writesBeforeDeath, engine.writes))

	-- 关键：尸体一直活着、护甲也一直在 ⇒ 不能每 1.2 秒又判一次"主人不在"再释放一遍。
	-- 反复判 = 反复 release + 重挂（几百次引擎调用 + 姿态跳变），实机就是"特别卡顿 + 没物理"。
	local function countOf(text, needle)
		local count, pos = 0, 1
		while true do
			local found = string.find(text, needle, pos, true)
			if not found then break end
			count = count + 1
			pos = found + #needle
		end
		return count
	end
	for _ = 1, 600 do engine.time = engine.time + 1 / 120; probeRender() end
	text = readLog7()
	-- 数的是"整组释放"那一条：它每轮只该出现一次，之后再多的帧都不该重复触发。
	check("释放只发生一次（不会反复重挂）", countOf(text, "释放了") == 1,
		string.format("释放 %d 次", countOf(text, "释放了")))
	check("之后仍挂在新玩家身上",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES
			and tostring(runtime.sessions[1].unitTag):find("t7n", 1, true) ~= nil,
		tostring(runtime.sessions and runtime.sessions[1] and runtime.sessions[1].unitTag))

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T8 会话死亡后的停写窗口（多人局换场景不再把骨写到已释放对象上）")
do
	rawset(_G, "render", function() end)

	local t8Avatar = newUnit("t8avatar", AVATAR_NODES, true, "#ID[t8avatar]")
	do
		local mgName = SUPPORT_MG
		t8Avatar.ids[mgName] = true
		t8Avatar.nodeOf[mgName] = 5
	end
	local t8Armors = {}
	for index, count in ipairs(ARMOR_NODES) do
		t8Armors[index] = newUnit("t8a" .. index, count, false, "#ID[t8a" .. index .. "]")
		dress(t8Armors[index])
		engine.pos[t8Armors[index]] = { 0, 0, 0 }
	end
	avatar = t8Avatar
	armors = t8Armors
	engine.pos[t8Avatar] = { 0, 0, 0 }

	local logPath8 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t8death.txt"
	pcall(os.remove, logPath8)

	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath8,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 1, validateFrames = 0,   -- 关掉名单校验，隔离出"死亡停写"这一条
		maxArmorDistance = 10.0,
		deathGraceFrames = 20,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 先全部挂上", runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	-- ① 正常帧确实在写骨（否则下面的"停写"断言是空转）
	local baseline = engine.writes
	for _ = 1, 5 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 正常帧会写骨", engine.writes > baseline,
		string.format("写入=%d", engine.writes - baseline))

	-- ② 停写窗口内**一个骨都不写**。直接设窗口，把"是谁设的"和"设了之后怎样"分开测。
	local heldFrom = runtime.frame
	runtime.driveHoldUntil = heldFrom + 20
	local writesAtHold = engine.writes
	for _ = 1, 15 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 窗口内不写骨", engine.writes == writesAtHold,
		string.format("窗口内写入=%d", engine.writes - writesAtHold))
	check("T8 窗口内确实过了帧", runtime.frame > heldFrom,
		string.format("frame=%d→%d", heldFrom, runtime.frame))

	-- ③ 过了窗口必须恢复写入：停写不等于永久停手
	for _ = 1, 40 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T8 窗口过后恢复写骨", engine.writes > writesAtHold,
		string.format("恢复后写入=%d", engine.writes - writesAtHold))

	-- ④ 真让一个会话死掉：pruneSessions 必须自己把窗口设上。
	--    这是"多人局换场景"的入口条件——单人局是整组一起死，走的是另一条分支。
	local beforeDeath = runtime.frame
	engine.alive[t8Armors[1]] = false
	engine.time = engine.time + 1 / 120; probeRender()
	check("T8 会话死亡会设置停写窗口", (runtime.driveHoldUntil or 0) > beforeDeath,
		string.format("holdUntil=%s frame=%s",
			tostring(runtime.driveHoldUntil), tostring(runtime.frame)))

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T10 诊断开关 writeOff：只挂不写")
--
-- 用途：把"崩溃是否由写骨触发"与"仅仅持有句柄就够"分开。
-- 这个开关**不能**影响别的行为：发现/挂载/调度/判活全都照旧，只是不写骨。
do
	rawset(_G, "render", function() end)

	local t10Avatar = newUnit("t10avatar", AVATAR_NODES, true, "#ID[t10avatar]")
	do
		local mgName = SUPPORT_MG
		t10Avatar.ids[mgName] = true
		t10Avatar.nodeOf[mgName] = 5
	end
	local t10Armors = {}
	for index, count in ipairs(ARMOR_NODES) do
		t10Armors[index] = newUnit("t10a" .. index, count, false, "#ID[t10a" .. index .. "]")
		dress(t10Armors[index])
		engine.pos[t10Armors[index]] = { 0, 0, 0 }
	end
	avatar = t10Avatar
	armors = t10Armors
	engine.pos[t10Avatar] = { 0, 0, 0 }

	local tunePath10 = (os.getenv("TEMP") or ".") .. "\\physbone-t10-writeoff-tune.txt"
	do
		local handle = io.open(tunePath10, "w")
		handle:write("writeOff = 1\n")
		handle:close()
	end
	local logPath10 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t10writeoff.txt"
	pcall(os.remove, logPath10)

	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath10,
		settleFrames = 2, snapshot = false, tunePath = tunePath10,
		pollFrames = 1, validateFrames = 0,
		maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T10 照样挂上（开关不影响挂载）",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	local writesBefore = engine.writes
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T10 一帧都不写", engine.writes == writesBefore,
		string.format("写入=%d", engine.writes - writesBefore))
	check("T10 会话仍在（不是把会话丢了）",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES)

	local text10 = ""
	do
		local handle = io.open(logPath10, "r")
		if handle then text10 = handle:read("*a") or ""; handle:close() end
	end
	check("T10 明确说出生效", text10:find("writeOff=1", 1, true) ~= nil)
	pcall(os.remove, tunePath10)

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T11 细分开关 discoverOnly / hardOff")
--
-- 实机已达成的结论：写骨不是触发点（writeOff=1 仍崩）。
-- 于是继续往上游劈：① 只扫描不挂载；② 连扫描都不做。
do
	local function scenario(tag, tuneLine, expectLog)
		rawset(_G, "render", function() end)
		local av = newUnit(tag .. "avatar", AVATAR_NODES, true, "#ID[" .. tag .. "avatar]")
		do
			local mgName = SUPPORT_MG
			av.ids[mgName] = true
			av.nodeOf[mgName] = 5
		end
		local gear = {}
		for index, count in ipairs(ARMOR_NODES) do
			gear[index] = newUnit(tag .. "a" .. index, count, false, "#ID[" .. tag .. "a" .. index .. "]")
			dress(gear[index])
			engine.pos[gear[index]] = { 0, 0, 0 }
		end
		avatar = av
		armors = gear
		engine.pos[av] = { 0, 0, 0 }

		local tunePath = (os.getenv("TEMP") or ".") .. "\\physbone-" .. tag .. "-tune.txt"
		do
			local handle = io.open(tunePath, "w")
			handle:write(tuneLine .. "\n")
			handle:close()
		end
		local logPath = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_" .. tag .. ".txt"
		pcall(os.remove, logPath)

		local runtime = Runtime.install(S, {
			compat = Compat,
			maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
			carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
			logPath = logPath,
			settleFrames = 2, snapshot = false, tunePath = tunePath,
			pollFrames = 1, validateFrames = 0,
			maxArmorDistance = 10.0,
		})
		local probeRender = rawget(_G, "render")
		for _ = 1, 90 do engine.time = engine.time + 1 / 120; probeRender() end

		local text = ""
		do
			local handle = io.open(logPath, "r")
			if handle then text = handle:read("*a") or ""; handle:close() end
		end
		check(tag .. " 不挂载（会话保持 nil）", runtime.sessions == nil,
			string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))
		check(tag .. " 说出生效", text:find(expectLog, 1, true) ~= nil)
		pcall(os.remove, tunePath)
		Runtime.uninstall()
	end

	scenario("discoverOnly", "discoverOnly = 1", "discoverOnly=1")
	scenario("hardOff", "hardOff = 1", "hardOff=1")
end

-- ---------------------------------------------------------------------------
print("T13 dumpApi：导出引擎命名空间清单（找批量写骨 API 的依据）")
do
	rawset(_G, "render", function() end)

	local tunePath13 = (os.getenv("TEMP") or ".") .. "\\physbone-t13-dumpapi-tune.txt"
	do
		local handle = io.open(tunePath13, "w")
		handle:write("dumpApi = 1\n")
		handle:close()
	end
	local dumpPath13 = (os.getenv("TEMP") or ".") .. "\\physbone-t13-api.txt"
	pcall(os.remove, dumpPath13)
	local logPath13 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t13dumpapi.txt"
	pcall(os.remove, logPath13)

	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath13,
		settleFrames = 2, snapshot = false, tunePath = tunePath13,
		apiDumpPath = dumpPath13,
		pollFrames = 1, validateFrames = 0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 10 do engine.time = engine.time + 1 / 120; probeRender() end

	local dump = ""
	do
		local handle = io.open(dumpPath13, "r")
		if handle then dump = handle:read("*a") or ""; handle:close() end
	end
	check("T13 写出了清单", #dump > 0, string.format("长度=%d", #dump))
	check("T13 含 Unit 命名空间", dump:find("[Unit]", 1, true) ~= nil)
	-- 假引擎里 Unit.alive / Unit.world_position 都是表成员，必须被列出来
	check("T13 列出了 Unit 的真实成员", dump:find("alive = function", 1, true) ~= nil)
	check("T13 带运行时版本", dump:find("physbone runtime", 1, true) ~= nil)
	pcall(os.remove, tunePath13)
	pcall(os.remove, dumpPath13)

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T18 端到端：id 会失效的引擎（只认骨名）也必须能完整挂上")
--
-- 实机 2026-11-12：`string_to_id32` 的 `IdString32` 会失效，而我们 2.1.25 把它缓存了，
-- 于是"进程刚启动时能挂、之后所有候选 0/68"。这个用例把 `has_node` 改成**只认骨名**
-- （传数字直接抛错），从发现 → 匹配 → attach 整条链走一遍，任何一处退回按 id 都会红。
do
	rawset(_G, "render", function() end)
	engine.namesOnly = true

	local av18 = newUnit("t18avatar", AVATAR_NODES, true, "#ID[t18avatar]")
	do
		local mgName = SUPPORT_MG
		av18.ids[mgName] = true
		av18.nodeOf[mgName] = 5
	end
	engine.pos[av18] = { 0, 0, 0 }

	local armors18 = {}
	for index, count in ipairs(ARMOR_NODES) do
		armors18[index] = newUnit("t18" .. index, count, false, "#ID[t18" .. index .. "]")
		dress(armors18[index])
		engine.pos[armors18[index]] = { 0.2, 0, 0 }
	end
	avatar = av18
	armors = armors18

	local tunePath18 = (os.getenv("TEMP") or ".") .. "\\physbone-t18-name-tune.txt"
	do
		local handle = io.open(tunePath18, "w")
		handle:write("writeOff = 0\n")
		handle:close()
	end

	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t18names.txt",
		settleFrames = 2, snapshot = false, tunePath = tunePath18,
		pollFrames = 2, validateFrames = 0,
		maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end

	check("T18 只认骨名的引擎上也能全部挂上",
		runtime.sessions ~= nil and #runtime.sessions == #ARMOR_NODES,
		string.format("会话=%s 期望=%d", tostring(runtime.sessions and #runtime.sessions), #ARMOR_NODES))
	check("T18 没有退回按 id 问（否则 has_node 会抛错）",
		runtime.sessions ~= nil and #runtime.sessions > 0)

	pcall(os.remove, tunePath18)
	engine.namesOnly = false
	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T19 没有会话时，F8 仍必须被读到（自愈通道不能被 early return 挡住）")
--
-- 实机现场（2026-11-12，用户："按 F8 一点反应都没有"）：热键轮询原来在 `drive` 的**后半段**，
-- 而"一个会话都没有"会在中途 `return` —— 于是最需要 F8 的时刻恰恰读不到键，
-- 整个日志历史里一条 `[热键] f8 按下` 都没有。这条用例把"轮询在 early return 之前"钉死。
do
	rawset(_G, "render", function() end)

	-- 没有 avatar、没有候选 ⇒ discover 永远失败 ⇒ sessions 恒为 nil ⇒ drive 会早退
	avatar = nil
	armors = {}

	local state = { down = 0 }
	S.Keyboard = {
		button_index = function(name) return (name == "f8" or name == "F8") and 119 or nil end,
		button = function(code) if code == 119 then return state.down end return 0 end,
	}

	local logPath19 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t19hotkey.txt"
	os.remove(logPath19)
	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath19,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 2, validateFrames = 0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 6 do engine.time = engine.time + 1 / 120; probeRender() end

	local text = ""
	do
		local handle = io.open(logPath19, "r")
		if handle ~= nil then text = handle:read("*a") or ""; handle:close() end
	end
	check("T19 先确认确实没有会话（否则这条用例没意义）", runtime.sessions == nil,
		tostring(runtime.sessions and #runtime.sessions))

	state.down = 1               -- 按下
	for _ = 1, 3 do engine.time = engine.time + 1 / 120; probeRender() end
	do
		local handle = io.open(logPath19, "r")
		if handle ~= nil then text = handle:read("*a") or ""; handle:close() end
	end
	check("T19 无会话时按 F8 能落「按下」日志", text:find("[热键] f8 按下", 1, true) ~= nil)

	state.down = 0               -- 松开：下一次按下还要能触发（不是"永远按住"）
	for _ = 1, 3 do engine.time = engine.time + 1 / 120; probeRender() end
	state.down = 1
	for _ = 1, 3 do engine.time = engine.time + 1 / 120; probeRender() end
	do
		local handle = io.open(logPath19, "r")
		if handle ~= nil then text = handle:read("*a") or ""; handle:close() end
	end
	local _, presses = text:gsub("%[热键%] f8 按下", "")
	check("T19 松开再按能再次触发（上升沿，不是电平）", presses == 2, string.format("%d 次", presses))

	S.Keyboard = nil
	pcall(os.remove, logPath19)
	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T20 去掉上限之后：未知阶段不猜测，玩家一动就收敛到「穿在身上」的那件")
--
-- 用户 2026-11-12 定调：件数闸/距离闸这些"限制"掩盖了真正的问题（目标错了）。
-- 所以这里验的是**没有上限**时的行为：
--   · 玩家不动 ⇒ 没有活动量数据 ⇒ 同款 20 件**全部挂上**（宁可短暂多驱动，也不靠猜测否决目标）；
--   · 玩家走起来 ⇒ 只有穿在身上的那件跟着动 ⇒ 其余 19 件被 `identity_watch` 放掉。
do
	rawset(_G, "render", function() end)

	local av20 = newUnit("t20avatar", AVATAR_NODES, true, "#ID[t20avatar]")
	do
		local mgName = SUPPORT_MG
		av20.ids[mgName] = true
		av20.nodeOf[mgName] = 5
	end
	engine.pos[av20] = { 0, 0, 0 }

	-- 20 件同款护甲：第 1 件贴着玩家（会跟着走），其余摆在 1.5~4 m（人台、不动）
	local units20 = {}
	for index = 1, 20 do
		local unit = newUnit("t20dup" .. index, 171, false, "#ID[t20dup" .. index .. "]")
		dress(unit)
		local offset = index == 1 and 0.2 or (1.5 + index * 0.12)
		engine.pos[unit] = { offset, 0, 0 }
		units20[index] = unit
	end
	avatar = av20
	armors = units20

	local logPath20 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t20cap.txt"
	os.remove(logPath20)
	local tunePath20 = (os.getenv("TEMP") or ".") .. "\\physbone-t20-tune.txt"
	do
		local handle = io.open(tunePath20, "w")
		handle:write("writeOff = 0\n")
		handle:close()
	end
	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath20,
		settleFrames = 2, snapshot = false, tunePath = tunePath20,
		pollFrames = 2, validateFrames = 0,
		maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 80 do engine.time = engine.time + 1 / 120; probeRender() end

	-- 玩家不动 ⇒ 没有活动量数据 ⇒ **不猜测**：20 件同款全部先挂上（"去掉上限"的直接证据）。
	local count = runtime.sessions and #runtime.sessions or 0
	check("T20 未知阶段不猜测：同一款 20 件全部挂上", count == 20, string.format("会话=%d", count))

	-- 玩家走起来：只有"穿在身上"的那件会跟着动 ⇒ 其余 19 件必须被放掉。
	for step = 1, 60 do
		local x = step * 0.05
		engine.pos[av20] = { x, 0, 0 }
		engine.pos[units20[1]] = { x + 0.2, 0, 0 }
		engine.time = engine.time + 1 / 120
		probeRender()
	end
	local settled = runtime.sessions and #runtime.sessions or 0
	local keptTags = {}
	for _, session in ipairs(runtime.sessions or {}) do keptTags[#keptTags + 1] = tostring(session.unitTag) end
	check("T20 玩家动过之后就只剩跟随的那件", settled == 1,
		string.format("会话=%d %s", settled, table.concat(keptTags, ",")))
	check("T20 留下的正是穿在身上那件", settled == 1 and runtime.sessions[1].unitTag == "#ID[t20dup1]",
		table.concat(keptTags, ","))

	pcall(os.remove, tunePath20)
	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T21 身份判据（行为）：玩家动它才动的才是玩家那件，静止的人台要放掉")
--
-- 用户 2026-11-12 点出的方向：算力不是瓶颈，"驱动了不该驱动的模型"才是。
-- 而同资源的护甲件**骨头完全一样**，只能靠行为分辨：玩家身上那件挂在玩家骨架上，
-- 玩家一动它就动；军械库里的人台是死的。这条用例把"跟随判据 + 事后纠正"钉死。
--
-- ⚠️ 这里故意关掉距离闸（attachMaxDistance=0），于是**只有行为判据能筛**：
-- 4 件都贴着玩家、骨头都一样，区别只在"动不动"。
do
	rawset(_G, "render", function() end)

	local av21 = newUnit("t21avatar", AVATAR_NODES, true, "#ID[t21avatar]")
	do
		local mgName = SUPPORT_MG
		av21.ids[mgName] = true
		av21.nodeOf[mgName] = 5
	end
	engine.pos[av21] = { 0, 0, 0 }

	-- 1 件"穿在玩家身上"的（会跟着动）+ 3 件人台（不动），全部贴身、同款
	local worn = newUnit("t21worn", 171, false, "#ID[t21worn]")
	dress(worn)
	local stands = {}
	for index = 1, 3 do
		local unit = newUnit("t21stand" .. index, 171, false, "#ID[t21stand" .. index .. "]")
		dress(unit)
		engine.pos[unit] = { 0.2, 0, 0 }
		stands[index] = unit
	end
	engine.pos[worn] = { 0.2, 0, 0 }

	avatar = av21
	armors = { worn, stands[1], stands[2], stands[3] }

	local logPath21 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t21ident.txt"
	os.remove(logPath21)
	local tunePath21 = (os.getenv("TEMP") or ".") .. "\\physbone-t21-tune.txt"
	do
		local handle = io.open(tunePath21, "w")
		handle:write("writeOff = 0\n")
		handle:close()
	end
	local runtime = Runtime.install(S, {
		compat = Compat,
		maxMatchedPerOwner = 0,   -- 这些用例验的是匹配/公平性，不是"每玩家最多几件"的上限
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath21,
		settleFrames = 2, snapshot = false, tunePath = tunePath21,
		pollFrames = 2, validateFrames = 0,
		maxArmorDistance = 10.0,
		attachMaxDistance = 0,      -- 关掉距离闸：只让行为判据说话
		maxMatchedPerOwner = 8,
	})
	local probeRender = rawget(_G, "render")

	-- 玩家走动：玩家和"身上那件"同步位移，人台不动。
	for step = 1, 60 do
		local x = step * 0.05
		engine.pos[av21] = { x, 0, 0 }
		engine.pos[worn] = { x + 0.2, 0, 0 }
		engine.time = engine.time + 1 / 120
		probeRender()
	end

	local text = ""
	do
		local handle = io.open(logPath21, "r")
		if handle ~= nil then text = handle:read("*a") or ""; handle:close() end
	end
	local count = runtime.sessions and #runtime.sessions or 0
	local tags = {}
	for _, session in ipairs(runtime.sessions or {}) do tags[#tags + 1] = tostring(session.unitTag) end
	check("T21 最后只剩「跟着玩家动」的那一件", count == 1, string.format("会话=%d %s", count, table.concat(tags, ",")))
	check("T21 留下的正是那件（不是人台）",
		count == 1 and runtime.sessions[1].unitTag == "#ID[t21worn]", table.concat(tags, ","))
	check("T21 识别日志里有「静止」的件", text:find("静止=[1-9]", 1) ~= nil)

	pcall(os.remove, tunePath21)
	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T22 按件切骨：只带部分骨的 unit 必须照样挂上，且链数各不相同")
--
-- 2026-11-12 转换器侧的改法：每个 unit 只带**自己蒙到**的那几根物理骨
-- （`ExtraBoneSubset`，判据是顶点影响里的骨名哈希）。于是运行时必须满足两件事：
--   ① 一件只带 2 条链的 unit 在 40 骨的容器上只有 5% 的骨 —— 旧门槛"覆盖 ≥10% 才算可用"
--      会把它判死，实机上就是"那块布永远没有物理"；
--   ② 每件产出的链数**各不相同**，加起来远小于"件数 × 容器全量" —— 这就是省下来的那部分。
-- 这条用例把两个数字都钉死：小件 1 条链、大件 4 条链。
do
	rawset(_G, "render", function() end)

	-- 20 条链 × 2 节 = 40 根骨（容器全量）。链根挂 head，子节挂在链根上。
	local RIG40 = (function()
		local params = RIG.bones[1].params
		local bones = {}
		for chain = 1, 20 do
			local root = string.format("jabcdef012345/source_extra_%d", chain * 2 - 1)
			local tip = string.format("jabcdef012345/source_extra_%d", chain * 2)
			bones[#bones + 1] = { index = chain * 2 - 1, name = root, parent = 0,
				parentName = "head", pos = { 0, -0.1 * chain, 0 }, params = params, flags = 0x31 }
			bones[#bones + 1] = { index = chain * 2, name = tip, parent = chain * 2 - 1,
				parentName = root, pos = { 0, -0.1 * chain - 0.05, 0 }, params = params, flags = 0x31 }
		end
		return { bones = bones }
	end)()

	-- 只给这个 unit 挂上**部分骨名**（模拟转换器裁完之后的骨架）。
	local function dressSubset(unit, chains)
		local nextNode = 10
		for _, chain in ipairs(chains) do
			for _, offset in ipairs({ 0, 1 }) do
				local name = string.format("jabcdef012345/source_extra_%d", (chain - 1) * 2 + 1 + offset)
				unit.ids[name] = true
				unit.nodeOf[name] = nextNode
				nextNode = nextNode + 1
			end
		end
		unit.ids["head"] = true
		unit.nodeOf["head"] = 9
	end

	local av22 = newUnit("t22avatar", AVATAR_NODES, true, "#ID[t22avatar]")
	do
		local mgName = SUPPORT_MG
		av22.ids[mgName] = true
		av22.nodeOf[mgName] = 5
	end
	engine.pos[av22] = { 0, 0, 0 }

	-- 小件：只带第 1 条链（2 根骨 = 5%）；大件：带第 1~4 条链（8 根骨 = 20%）。
	local small = newUnit("t22small", 171, false, "#ID[t22small]")
	dressSubset(small, { 1 })
	local big = newUnit("t22big", 200, false, "#ID[t22big]")
	dressSubset(big, { 1, 2, 3, 4 })
	engine.pos[small] = { 0.2, 0, 0 }
	engine.pos[big] = { 0.3, 0, 0 }

	avatar = av22
	armors = { small, big }

	local logPath22 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t22subset.txt"
	os.remove(logPath22)
	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG40, path = "subset.patch_1.gpu_resources" } },
		logPath = logPath22,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.json",
		pollFrames = 2, validateFrames = 0,
		maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 80 do engine.time = engine.time + 1 / 120; probeRender() end

	local byTag = {}
	for _, session in ipairs(runtime.sessions or {}) do
		byTag[tostring(session.unitTag)] = #session.sessions
	end
	-- unitTag 现在就是资源 id（IAS 的 profile 哈希），不再是测试里随手起的名字。
	check("T22 只带 5% 骨的小件也挂上了", byTag[small.resource] == 1,
		string.format("链=%s", tostring(byTag[small.resource])))
	check("T22 带 20% 骨的大件挂上了它自己那 4 条链", byTag[big.resource] == 4,
		string.format("链=%s", tostring(byTag[big.resource])))
	local total = 0
	for _, count in pairs(byTag) do total = total + count end
	check("T22 合计链数远小于「两件都带全量」（5 ≪ 40）", total == 5, string.format("合计=%d", total))

	local text22 = ""
	do
		local handle = io.open(logPath22, "r")
		if handle ~= nil then text22 = handle:read("*a") or ""; handle:close() end
	end
	check("T22 汇总行里带「每件链数」", text22:find("每件链数", 1, true) ~= nil)
	check("T22 「每件链数」显示的是 4 和 1", text22:find("每件链数 4,1", 1, true) ~= nil,
		text22:match("每件链数[^；]*") or "（没有这一行）")

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T23 裙摆也必须在 unit 死后停手（2026-11-12 军械库/债券页必崩）")
--
-- 现场：开债券页时引擎**连续创建/销毁**一堆展示用的护甲件，而裙摆是「每帧遍历所有会话、
-- 且跑在 drive 最末尾（pruneSessions 之后）」的那一段。对已销毁的 unit 调
-- `num_scene_graph_items` / `world_position` 是**原生 AV**（0xc0000005 / 参数 0x0 0x0），
-- Lua 的 pcall 拦不住 —— 所以必须**先判活**，而不是靠 pcall 兜。
--
-- 这条用例把两件事钉死：
--   ① unit 死掉之后，它的骨头**一次都不许再碰**（按引擎调用计数验证）；
--   ② 裙摆这条路径确实在跑（否则用例根本没咬到）。
do
	rawset(_G, "render", function() end)

	-- 裙摆默认是关的；用调参文件打开，并让它对这个 rig 真的有事可做。
	local tunePath23 = (os.getenv("TEMP") or ".") .. "\\physbone-t23-skirt-tune.txt"
	do
		local handle = io.open(tunePath23, "w")
		handle:write("skirt = 1\nskirtDeg = 30\ndevLog = 1\n")
		handle:close()
	end
	local logPath23 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t23skirt.txt"
	os.remove(logPath23)

	-- 链根挂在 **hips** 上，裙摆才会认这些链（默认 anchor map 里 hips 是躯干锚点）。
	local RIG23 = (function()
		local params = RIG.bones[1].params
		local bones = {}
		for index = 1, 4 do
			bones[index] = { index = index, name = BONE_NAMES[index],
				parent = (index <= 2) and (index - 1) or 0,
				parentName = (index == 1 or index == 3) and "hips" or BONE_NAMES[index - 1],
				pos = { 0, -0.1 * (index - 1), 0 }, params = params, flags = 0x31 }
		end
		return { bones = bones }
	end)()

	-- 裙摆要 4 个参照点：hips / chest / l_thigh / r_thigh。
	local function dressForSkirt(unit)
		local nextNode = 10
		for _, name in ipairs(BONE_NAMES) do
			unit.ids[name] = true
			unit.nodeOf[name] = nextNode
			nextNode = nextNode + 1
		end
		for _, name in ipairs({ "head", "hips", "chest", "l_thigh", "r_thigh" }) do
			unit.ids[name] = true
			unit.nodeOf[name] = nextNode
			nextNode = nextNode + 1
		end
	end

	local av23 = newUnit("t23avatar", AVATAR_NODES, true, "#ID[t23avatar]")
	do
		local mgName = SUPPORT_MG
		av23.ids[mgName] = true
		av23.nodeOf[mgName] = 5
	end
	engine.pos[av23] = { 0, 0, 0 }

	local doomed = newUnit("t23doomed", 171, false, "#ID[t23doomed]")
	dressForSkirt(doomed)
	engine.pos[doomed] = { 0.2, 0, 0 }
	local survivor = newUnit("t23survivor", 200, false, "#ID[t23survivor]")
	dressForSkirt(survivor)
	engine.pos[survivor] = { 0.3, 0, 0 }

	avatar = av23
	armors = { doomed, survivor }

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG23, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath23,
		settleFrames = 2, snapshot = false, tunePath = tunePath23,
		pollFrames = 2, validateFrames = 0, maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	-- 走起来：裙摆要有速度才肯推（静止时斜坡为 0）
	for step = 1, 60 do
		local x = step * 0.02
		engine.pos[av23] = { x, 0, 0 }
		engine.pos[doomed] = { x + 0.2, 0, 0 }
		engine.pos[survivor] = { x + 0.3, 0, 0 }
		engine.time = engine.time + 1 / 120
		probeRender()
	end
	local attached = runtime.sessions and #runtime.sessions or 0
	check("T23 两件都挂上了（否则没东西可杀）", attached == 2, string.format("会话=%d", attached))

	local text23 = ""
	do
		local handle = io.open(logPath23, "r")
		if handle ~= nil then text23 = handle:read("*a") or ""; handle:close() end
	end
	check("T23 裙摆这条路径确实在跑", text23:find("[physbone][裙摆]", 1, true) ~= nil)

	-- 杀掉 doomed，记下它此刻被碰过多少次，再跑一段。
	engine.alive[doomed] = false
	local before = {}
	for kind, count in pairs(engine.hits[doomed] or {}) do before[kind] = count end
	for _ = 1, 40 do
		engine.time = engine.time + 1 / 120
		probeRender()
	end
	local after = engine.hits[doomed] or {}
	local touched = {}
	for _, kind in ipairs({ "nodes", "world", "pose", "write", "has", "node" }) do
		local delta = (after[kind] or 0) - (before[kind] or 0)
		if delta > 0 then touched[#touched + 1] = kind .. "+" .. delta end
	end
	check("T23 unit 死掉之后一次都没再碰它的骨头", #touched == 0, table.concat(touched, " "))
	local alive23 = runtime.sessions and #runtime.sessions or 0
	check("T23 死掉的那件已被丢弃（退出驱动）", alive23 <= 1, string.format("会话=%d", alive23))

	-- 第二段：**scene graph 被重建**（军械库换预览件就是这种）。
	-- 裙摆缓存的是节点下标；节点数一变，旧下标就可能指向别的节点甚至越界 ——
	-- 对这种下标读 world_position 是原生 AV，而 session_health 每 30 帧才比一次节点数。
	-- 判据：节点数变了之后，裙摆必须**重新解析**参照点（会再问一次 has_node("hips")）。
	local surviveHitsBefore = (engine.hits[survivor] or {}).has or 0
	local newCount = (engine.nodes[survivor] or 200) + 40
	engine.nodes[survivor] = newCount
	for _, name in ipairs({ "hips", "chest", "l_thigh", "r_thigh" }) do
		survivor.nodeOf[name] = newCount - 2      -- 挪到新的位置：旧下标已经不对了
	end
	for _ = 1, 4 do
		engine.time = engine.time + 1 / 120
		probeRender()
	end
	local surviveHitsAfter = (engine.hits[survivor] or {}).has or 0
	check("T23 节点数变了之后裙摆重新解析参照点（不复用旧下标）",
		surviveHitsAfter > surviveHitsBefore,
		string.format("has_node 调用 %d → %d", surviveHitsBefore, surviveHitsAfter))

	pcall(os.remove, tunePath23)
	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T24 判活不许 deref 句柄：unit 已从活体列表消失时，一次都不许碰它")
--
-- 2026-11-12 债券页快切的现场：句柄被销毁之后，**任何**引擎调用（包括 `Unit.alive`）
-- 都是原生 AV（转储 0x3e564b / 参数 0x0 0x0），pcall 拦不住。
-- 所以判据不能是"问它还在不在"，只能是"**它还在不在引擎的活体列表里**"（纯指针比较）。
--
-- 这条用例故意让 `Unit.alive` **说谎**（仍返回 true），但把 unit 从
-- `World.units()` 的返回里摘掉 —— 真实情形就是"引擎已经释放了它，而我们的句柄仍指向那块内存"。
-- 旧实现会继续驱动它（= 实机崩溃），新实现必须先按指针判死。
do
	rawset(_G, "render", function() end)

	local av24 = newUnit("t24avatar", AVATAR_NODES, true, "#ID[t24avatar]")
	do
		local mgName = SUPPORT_MG
		av24.ids[mgName] = true
		av24.nodeOf[mgName] = 5
	end
	engine.pos[av24] = { 0, 0, 0 }

	local vanished = newUnit("t24gone", 171, false, "#ID[t24gone]")
	dress(vanished)
	engine.pos[vanished] = { 0.2, 0, 0 }
	local kept24 = newUnit("t24kept", 200, false, "#ID[t24kept]")
	dress(kept24)
	engine.pos[kept24] = { 0.3, 0, 0 }

	avatar = av24
	armors = { vanished, kept24 }

	local logPath24 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t24gone.txt"
	os.remove(logPath24)
	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath24,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 2, validateFrames = 0, maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 60 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T24 两件都挂上了（否则没东西可消失）",
		runtime.sessions ~= nil and #runtime.sessions == 2,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	-- 关键一步：把它从活体列表里摘掉，但**不**改 `engine.alive`（模拟引擎已释放、句柄仍指向旧内存）。
	armors = { kept24 }
	local before = {}
	for kind, count in pairs(engine.hits[vanished] or {}) do before[kind] = count end
	for _ = 1, 5 do engine.time = engine.time + 1 / 120; probeRender() end
	local after = engine.hits[vanished] or {}
	local touched = {}
	for _, kind in ipairs({ "nodes", "world", "pose", "write", "has", "node" }) do
		local delta = (after[kind] or 0) - (before[kind] or 0)
		if delta > 0 then touched[#touched + 1] = kind .. "+" .. delta end
	end
	check("T24 已消失的 unit 一次都没再被碰（Unit.alive 说谎也不上当）", #touched == 0,
		table.concat(touched, " "))
	check("T24 它的会话被丢弃", (runtime.sessions and #runtime.sessions or 0) <= 1,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions or 0)))

	local text24 = ""
	do
		local handle = io.open(logPath24, "r")
		if handle ~= nil then text24 = handle:read("*a") or ""; handle:close() end
	end
	check("T24 日志里写明「指针判死」这一类", text24:find("活体列表", 1, true) ~= nil)

	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T25 身份链探针：一次只跑一遍、先落盘再调用、崩了能续")
--
-- 背景：现在"这是玩家身上的护甲"是启发式（离谁近 + 会不会跟着动），于是军械库/债券页的
-- 展台件也会被挂上 —— 既白算，又制造"页面快切时句柄被销毁"的崩溃燃料。
-- 要做成结构判据，得先问引擎：这件护甲和 avatar 在场景图里有没有父子/链接关系。
-- 这些 API **签名未知、且曾经在默认路径上把游戏打死**（Unit.bones/is_child/…，2026-11-12），
-- 所以探针必须是：默认关、只读、结果只进日志、每一步**先落盘再调用**。
do
	rawset(_G, "render", function() end)

	-- 给假引擎补上被探的三个 API（真机上它们是"未知签名"，这里只是让探针有东西可调）
	local childCalls, idCalls = 0, 0
	S.Unit.is_child = function(a, b)
		childCalls = childCalls + 1
		return a == b
	end
	S.Unit.scene_graph_parent = function(unit, node) return node + 1 end
	S.Unit.id = function(unit) idCalls = idCalls + 1 return unit.tag end

	local av25 = newUnit("t25avatar", AVATAR_NODES, true, "#ID[t25avatar]")
	do
		local mgName = SUPPORT_MG
		av25.ids[mgName] = true
		av25.nodeOf[mgName] = 5
	end
	engine.pos[av25] = { 0, 0, 0 }
	local worn = newUnit("t25worn", 171, false, "#ID[t25worn]")
	dress(worn)
	engine.pos[worn] = { 0.2, 0, 0 }
	avatar = av25
	armors = { worn }

	local tunePath25 = (os.getenv("TEMP") or ".") .. "\\physbone-t25-ident-tune.txt"
	do
		local handle = io.open(tunePath25, "w")
		handle:write("probeIdentityLink = 1\n")
		handle:close()
	end
	local probeState25 = (os.getenv("TEMP") or ".") .. "\\physbone-t25-ident-state.txt"
	os.remove(probeState25)
	local logPath25 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t25ident.txt"
	os.remove(logPath25)

	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath25,
		settleFrames = 2, snapshot = false, tunePath = tunePath25,
		pollFrames = 2, validateFrames = 0, maxArmorDistance = 10.0,
		identProbePath = probeState25,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 30 do engine.time = engine.time + 1 / 120; probeRender() end

	local text25 = ""
	do
		local handle = io.open(logPath25, "r")
		if handle ~= nil then text25 = handle:read("*a") or ""; handle:close() end
	end
	check("T25 探针跑起来了（#1 对照项落盘）", text25:find("[身份链] #1 开始", 1, true) ~= nil)
	check("T25 七项都跑完", text25:find("全部 7 项跑完", 1, true) ~= nil)
	check("T25 真的调到了 is_child", childCalls > 0, tostring(childCalls))
	check("T25 每一项都有结果行", select(2, text25:gsub("⇒ ", "")) >= 7,
		tostring(select(2, text25:gsub("⇒ ", ""))))

	local stateText = ""
	do
		local handle = io.open(probeState25, "r")
		if handle ~= nil then stateText = handle:read("*a") or ""; handle:close() end
	end
	check("T25 断点文件写成 next = 8", stateText:find("next = 8", 1, true) ~= nil, stateText)

	-- 再装一次（同一个断点文件）：应当直接判定"全部跑完"，不重复调用引擎 API。
	local callsBefore = childCalls
	Runtime.uninstall()
	local logPath25b = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t25ident_b.txt"
	os.remove(logPath25b)
	local runtimeB = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath25b,
		settleFrames = 2, snapshot = false, tunePath = tunePath25,
		pollFrames = 2, validateFrames = 0, maxArmorDistance = 10.0,
		identProbePath = probeState25,
	})
	local probeRenderB = rawget(_G, "render")
	for _ = 1, 30 do engine.time = engine.time + 1 / 120; probeRenderB() end
	check("T25 已经跑完就不再重复调引擎 API", childCalls == callsBefore,
		string.format("%d → %d", callsBefore, childCalls))

	pcall(os.remove, tunePath25)
	pcall(os.remove, probeState25)
	Runtime.uninstall()
end

-- ---------------------------------------------------------------------------
print("T26 avatar 也会失效：判活闸必须同时管 avatar 名单")
--
-- 2026-11-12 17:58 实测真凶：`refresh_avatar_distances` 每帧对**每个 avatar 句柄**调
-- `Unit.world_position(avatar.unit, 1)`，而 `runtime.avatars` 是上一帧的名单。
-- 债券页快速切页时引擎会连续重建 avatar（展台上那个角色本身就是 avatar 资源），
-- 句柄一失效这一行就是野读 → 原生 AV（0x3e564b / 参数 0x0 0x0），Lua 层完全看不出来。
--
-- 这条用例让 avatar 从活体列表里消失、但 `Unit.alive` 仍返回 true（模拟"已释放 + 句柄还指着旧内存"）。
-- 旧实现会继续每帧对它取世界坐标（= 实机崩溃），新实现必须先在指针层面把它摘掉。
do
	rawset(_G, "render", function() end)

	local av26 = newUnit("t26avatar", AVATAR_NODES, true, "#ID[t26avatar]")
	do
		local mgName = SUPPORT_MG
		av26.ids[mgName] = true
		av26.nodeOf[mgName] = 5
	end
	engine.pos[av26] = { 0, 0, 0 }
	local wear = newUnit("t26wear", 171, false, "#ID[t26wear]")
	dress(wear)
	engine.pos[wear] = { 0.2, 0, 0 }
	avatar = av26
	armors = { wear }

	-- 让 cameraPosition 真的能用：`refresh_avatar_distances` 在拿不到相机时会**整段早退**，
	-- 那样这条用例就没咬到"每帧对 avatar 取世界坐标"这个真凶。
	local savedDebugPose = S.World.debug_camera_pose
	local savedTranslation = S.Matrix4x4.translation
	S.World.debug_camera_pose = function() return pose end
	S.Matrix4x4.translation = function() return { kind = "vector" } end

	local logPath26 = (os.getenv("LOCALAPPDATA") or ".") .. "\\hd2pbx_runtime_t26avatar.txt"
	os.remove(logPath26)
	local runtime = Runtime.install(S, {
		compat = Compat,
		carriers = { { rig = RIG, path = "fake.patch_1.gpu_resources" } },
		logPath = logPath26,
		settleFrames = 2, snapshot = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		pollFrames = 2, validateFrames = 0, maxArmorDistance = 10.0,
	})
	local probeRender = rawget(_G, "render")
	for _ = 1, 40 do engine.time = engine.time + 1 / 120; probeRender() end
	check("T26 先确认已经挂上了（同一个 avatar 上）",
		runtime.sessions ~= nil and #runtime.sessions == 1,
		string.format("会话=%s", tostring(runtime.sessions and #runtime.sessions)))

	-- avatar 从活体列表消失，但 alive 仍说谎
	engine.hidden[av26] = true
	local before = {}
	for kind, count in pairs(engine.hits[av26] or {}) do before[kind] = count end
	for _ = 1, 10 do engine.time = engine.time + 1 / 120; probeRender() end
	local after = engine.hits[av26] or {}
	local touched = {}
	for _, kind in ipairs({ "nodes", "world", "pose", "write", "has", "node" }) do
		local delta = (after[kind] or 0) - (before[kind] or 0)
		if delta > 0 then touched[#touched + 1] = kind .. "+" .. delta end
	end
	check("T26 失效的 avatar 一次都没再被碰（不再每帧取它世界坐标）", #touched == 0,
		table.concat(touched, " "))

	local text26 = ""
	do
		local handle = io.open(logPath26, "r")
		if handle ~= nil then text26 = handle:read("*a") or ""; handle:close() end
	end
	check("T26 日志里写明 avatar 被指针判死", text26:find("avatar 已不在活体列表里", 1, true) ~= nil)

	engine.hidden[av26] = nil
	S.World.debug_camera_pose = savedDebugPose
	S.Matrix4x4.translation = savedTranslation
	Runtime.uninstall()
end

print("T27 IAS 可选链在探测和挂载前过滤")
do
	local entries = { { "Back", 0 }, { "Bang", 1 }, { "Ahoge", 1 },
		{ "ACC", 2 }, { "Breast", 3 }, { "Butt", 4 } }
	local rig = { version = 2, bones = {} }
	local names = {}
	local wear = newUnit("t27wear", 171, false, "#ID[t27wear]")
	wear.ids.head, wear.nodeOf.head = true, 9
	for _, entry in ipairs(entries) do
		local short, group = entry[1], entry[2]
		local name = "ias_phys_fixture/" .. short
		local index = #rig.bones + 1
		names[name], names[S.Application.string_to_id32(name)] = group, group
		wear.ids[name], wear.nodeOf[name] = true, 9 + index
		rig.bones[index] = { name = name, index = index, parent = 0, parentName = "head",
			pos = { 0, 0, 0 }, flags = 0x1021 + 256 * group, params = RIG.bones[1].params }
	end
	armors = { wear }
	local hasNode = S.Unit.has_node
	for _, case in ipairs({ { expected = 1 }, { mask = 4, expected = 2 }, { mask = 15, expected = 6 } }) do
		local forbidden = 0
		local function enabled(group)
			return group == 0 or math.floor((case.mask or 0) / 2 ^ (group - 1)) % 2 == 1
		end
		S.Unit.has_node = function(unit, key)
			local group = names[key]
			if group ~= nil and not enabled(group) then forbidden = forbidden + 1 end
			return hasNode(unit, key)
		end
		local runtime = Runtime.install(S, {
			compat = Compat, iasPhysicsGroups = case.mask,
			carriers = { { rig = rig, path = "ias.patch_1.gpu_resources" } },
			log = false, snapshot = false, settleFrames = 1, pollFrames = 1,
			tunePath = "nonexistent-hd2pbx-tune.txt",
		})
		for _ = 1, 20 do engine.time = engine.time + 1 / 60; render() end
		local chains = runtime.sessions and runtime.sessions[1] and runtime.sessions[1].sessions or {}
		local allowed = #chains == case.expected
		for _, chain in ipairs(chains) do
			local name = chain.chain[1].name
			allowed = allowed and enabled(names[name]) and chain.nodes[1] == wear.nodeOf[name]
		end
		check("T27 mask=" .. tostring(case.mask) .. " 只挂启用组且不查询关闭组", allowed and forbidden == 0,
			string.format("链=%d 禁用查询=%d", #chains, forbidden))
		Runtime.uninstall()
	end
	S.Unit.has_node = hasNode
end

print("")
if failures == 0 then
	print("=== 全部通过 ===")
else
	print(string.format("=== %d 项失败 ===", failures))
	os.exit(1)
end
