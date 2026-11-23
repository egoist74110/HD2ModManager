-- P3 手臂姿态修正自检（1:1 离线验证 compat + runtime 行为与调用预算）
-- 用法：luajit HD2PhysBoneCompat/test/arm-pose-test.lua

local Compat = assert(assert(loadfile("HD2PhysBoneCompat/src/physbone_compat.lua"))())
local Runtime = assert(assert(loadfile("HD2PhysBoneCompat/src/physbone_runtime.lua"))())

local failures = 0
local function check(name, ok, detail)
	print(string.format("  %-4s %-56s %s", ok and "PASS" or "FAIL", name, detail or ""))
	if not ok then failures = failures + 1 end
end

local function qmul(a, b)
	return {
		a[4]*b[1] + a[1]*b[4] + a[2]*b[3] - a[3]*b[2],
		a[4]*b[2] - a[1]*b[3] + a[2]*b[4] + a[3]*b[1],
		a[4]*b[3] + a[1]*b[2] - a[2]*b[1] + a[3]*b[4],
		a[4]*b[4] - a[1]*b[1] - a[2]*b[2] - a[3]*b[3],
	}
end

local function qrot(q, v)
	local tx = 2 * (q[2] * v[3] - q[3] * v[2])
	local ty = 2 * (q[3] * v[1] - q[1] * v[3])
	local tz = 2 * (q[1] * v[2] - q[2] * v[1])
	return {
		v[1] + q[4] * tx + (q[2] * tz - q[3] * ty),
		v[2] + q[4] * ty + (q[3] * tx - q[1] * tz),
		v[3] + q[4] * tz + (q[1] * ty - q[2] * tx),
	}
end

local function q_to_mat3_colmajor(q)
	local x, y, z, w = q[1], q[2], q[3], q[4]
	local xx, yy, zz = x*x, y*y, z*z
	local xy, xz, yz = x*y, x*z, y*z
	local wx, wy, wz = w*x, w*y, w*z
	return {
		1 - 2*(yy + zz), 2*(xy + wz),     2*(xz - wy),
		2*(xy - wz),     1 - 2*(xx + zz), 2*(yz + wx),
		2*(xz + wy),     2*(yz - wx),     1 - 2*(xx + yy),
	}
end

local function make_rig(mode)
	local bones = {}
	local names = {
		"root", "hips", "chest",
		"l_clavicle", "l_shoulder", "l_elbow", "l_hand",
		"r_clavicle", "r_shoulder", "r_elbow", "r_hand",
		"support_mg",
	}
	local parents = {
		hips = "root", chest = "hips",
		l_clavicle = "chest", l_shoulder = "l_clavicle", l_elbow = "l_shoulder", l_hand = "l_elbow",
		r_clavicle = "chest", r_shoulder = "r_clavicle", r_elbow = "r_shoulder", r_hand = "r_elbow",
		support_mg = "chest",
	}
	local locals
	if mode == "rifle" then
		-- chest 已经把 +Z 转到 +Y（前方），所以 shoulder 绕局部 X 负转即可把手臂抬向前上方
		locals = {
			root       = { p = { 0, 0, 0 },       q = { 0, 0, 0, 1 } },
			hips       = { p = { 0, 0, 1.0 },     q = { 0, 0, 0, 1 } },
			chest      = { p = { 0, 0, 0.35 },    q = { 0, 0, 0, 1 } },
			l_clavicle = { p = { -0.10, 0, 0.15 }, q = { math.sqrt(0.5), 0, 0, math.sqrt(0.5) } },
			l_shoulder = { p = { -0.12, 0, 0.00 }, q = { 0.65, 0.15, 0.15, 0.73 } },
			l_elbow    = { p = { 0, 0, -0.28 },   q = { 0.45, 0, 0, 0.89 } },
			l_hand     = { p = { 0, 0, -0.26 },   q = { 0, 0, 0, 1 } },
			r_clavicle = { p = { 0.10, 0, 0.15 },  q = { math.sqrt(0.5), 0, 0, math.sqrt(0.5) } },
			r_shoulder = { p = { 0.12, 0, 0.00 },  q = { 0.65, -0.15, -0.15, 0.73 } },
			r_elbow    = { p = { 0, 0, -0.28 },   q = { 0.50, 0, 0, 0.866 } },
			r_hand     = { p = { 0, 0, -0.26 },   q = { 0, 0, 0, 1 } },
			support_mg = { p = { 0, 0, 0 },       q = { 0, 0, 0, 1 } },
		}
	else
		locals = {
			root       = { p = { 0, 0, 0 },       q = { 0, 0, 0, 1 } },
			hips       = { p = { 0, 0, 1.0 },     q = { 0, 0, 0, 1 } },
			chest      = { p = { 0, 0, 0.35 },    q = { 0, 0, 0, 1 } },
			l_clavicle = { p = { -0.10, 0, 0.15 }, q = { 0, 0, 0, 1 } },
			l_shoulder = { p = { -0.12, 0, 0.00 }, q = { 0, 0, 0, 1 } },
			l_elbow    = { p = { 0, 0, -0.28 },   q = { 0, 0, 0, 1 } },
			l_hand     = { p = { 0, 0, -0.26 },   q = { 0, 0, 0, 1 } },
			r_clavicle = { p = { 0.10, 0, 0.15 },  q = { 0, 0, 0, 1 } },
			r_shoulder = { p = { 0.12, 0, 0.00 },  q = { 0, 0, 0, 1 } },
			r_elbow    = { p = { 0, 0, -0.28 },   q = { 0, 0, 0, 1 } },
			r_hand     = { p = { 0, 0, -0.26 },   q = { 0, 0, 0, 1 } },
			support_mg = { p = { 0, 0, 0 },       q = { 0, 0, 0, 1 } },
		}
	end
	-- 腿：hips → thigh → knee → foot → ball（局部位置取潜兵的大致尺寸）
	for _, side in ipairs({ "l", "r" }) do
		local sx = side == "l" and -1 or 1
		local chain = {
			{ side .. "_thigh", "hips", { sx * 0.09, -0.02, -0.03 } },
			{ side .. "_knee", side .. "_thigh", { sx * 0.06, -0.02, -0.43 } },
			{ side .. "_foot", side .. "_knee", { sx * 0.05, -0.08, -0.41 } },
			{ side .. "_ball", side .. "_foot", { 0, 0.13, -0.10 } },
		}
		for _, item in ipairs(chain) do
			names[#names + 1] = item[1]
			parents[item[1]] = item[2]
			locals[item[1]] = { p = item[3], q = { 0, 0, 0, 1 } }
		end
	end
	for i, name in ipairs(names) do
		local q = locals[name].q
		local len = math.sqrt(q[1]^2 + q[2]^2 + q[3]^2 + q[4]^2)
		locals[name].q = { q[1]/len, q[2]/len, q[3]/len, q[4]/len }
		bones[name] = i
		bones[i] = name
	end
	-- chest 朝向：col2 (+Z in chest local) 朝世界 +Y (前)
	locals.chest.q = { -math.sqrt(0.5), 0, 0, math.sqrt(0.5) }
	if mode ~= "rifle" then
		-- 重设手臂，使它们在世界空间自然下垂（抵消 chest 的旋转）
		local cInv = { -locals.chest.q[1], -locals.chest.q[2], -locals.chest.q[3], locals.chest.q[4] }
		locals.l_clavicle.q = cInv
		locals.r_clavicle.q = cInv
	end

	local unit = {
		alive = true,
		nodeCount = 120,
		names = bones,
		parents = parents,
		locals = locals,
		worldPoses = {},
	}
	local function update_world()
		for _, name in ipairs(names) do
			local par = parents[name]
			local lp = locals[name]
			local link = unit.links and unit.links[name]
			if par == nil and not link then
				unit.worldPoses[name] = { p = { lp.p[1], lp.p[2], lp.p[3] }, q = { lp.q[1], lp.q[2], lp.q[3], lp.q[4] } }
			else
				local pw = link and link.owner.worldPoses[link.source] or unit.worldPoses[par]
				local rp = qrot(pw.q, lp.p)
				unit.worldPoses[name] = {
					p = { pw.p[1] + rp[1], pw.p[2] + rp[2], pw.p[3] + rp[3] },
					q = qmul(pw.q, lp.q),
				}
			end
		end
	end
	update_world()
	unit.update_world = update_world
	return unit
end

local function make_env(unit, opts)
	opts = opts or {}
	local engineCalls = 0
	local setLocalRotCalls = 0
	local setLocalPoseCalls = 0
	local now = 1.0
	local S = {}
	S.Vector3 = setmetatable({
		to_elements = function(v) engineCalls = engineCalls + 1; return v[1], v[2], v[3] end,
	}, {
		__call = function(_, x, y, z) engineCalls = engineCalls + 1; return { x, y, z } end,
	})
	S.Quaternion = {
		axis_angle = function(axis, angle)
			engineCalls = engineCalls + 1
			local l = math.sqrt(axis[1]^2 + axis[2]^2 + axis[3]^2)
			if l < 1e-9 then return { 0, 0, 0, 1 } end
			local s = math.sin(angle * 0.5) / l
			return { axis[1]*s, axis[2]*s, axis[3]*s, math.cos(angle * 0.5) }
		end,
		from_elements = function(x, y, z, w)
			engineCalls = engineCalls + 1
			return { x, y, z, w }
		end,
		to_elements = function(q)
			engineCalls = engineCalls + 1
			return q[1], q[2], q[3], q[4]
		end,
	}
	S.Matrix4x4 = {
		to_elements = function(m)
			engineCalls = engineCalls + 1
			return m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12]
		end,
		from_quaternion_position = function(q, p)
			engineCalls = engineCalls + 1
			local c = q_to_mat3_colmajor(q)
			return { c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], p[1], p[2], p[3] }
		end,
	}
	S.Unit = {
		alive = function(u) engineCalls = engineCalls + 1; return u.alive == true end,
		num_scene_graph_items = function(u) engineCalls = engineCalls + 1; return u.nodeCount end,
		has_animation_state_machine = function(u) engineCalls = engineCalls + 1; return u.machine ~= false end,
		animation_get_state = function(u)
			engineCalls = engineCalls + 1
			assert(u.machine ~= false)
			local tuple = {}
			for i = 1, 31 do tuple[i] = 0 end
			tuple[3], tuple[4], tuple[11] = u.weaponLayer or 67, u.menu and 76 or (u.upperLayer or 67), u.mainLayer or 0
			return unpack(tuple)
		end,
		animation_layer_info = function(u, layer)
			engineCalls = engineCalls + 1
			assert(u.machine ~= false and layer == 11)
			return u.clipTime or 0, u.clip or 1e37
		end,
		animation_has_variable = function(u, name) return name == "weapon_selected" and u.weaponSelected ~= nil end,
		animation_find_variable = function() return 14 end,
		animation_get_variable = function(u, id) assert(id == 14); return u.weaponSelected end,
		has_node = function(u, name) engineCalls = engineCalls + 1; return u.names[name] ~= nil end,
		node = function(u, name) engineCalls = engineCalls + 1; return u.names[name] or 1 end,
		local_pose = function(u, node)
			engineCalls = engineCalls + 1
			local name = u.names[node]
			local lp = u.locals[name]
			local c = q_to_mat3_colmajor(lp.q)
			return { c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], lp.p[1], lp.p[2], lp.p[3] }
		end,
		local_position = function(u, node)
			engineCalls = engineCalls + 1
			local p = u.locals[u.names[node]].p
			return { p[1], p[2], p[3] }
		end,
		local_rotation = function(u, node)
			engineCalls = engineCalls + 1
			local name = u.names[node]
			local q = u.locals[name].q
			return { q[1], q[2], q[3], q[4] }
		end,
		local_scale = function(u, node) engineCalls = engineCalls + 1; return u.scale or { 1, 1, 1 } end,
		world_rotation = function(u, node)
			engineCalls = engineCalls + 1
			return u.worldPoses[u.names[node]].q
		end,
		set_unit_visibility = function(u, visible) engineCalls = engineCalls + 1; u.visible = visible end,
		world_pose = function(u, node)
			engineCalls = engineCalls + 1
			local name = u.names[node]
			local wp = u.worldPoses[name]
			local c = q_to_mat3_colmajor(wp.q)
			return { c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], wp.p[1], wp.p[2], wp.p[3] }
		end,
		world_position = function(u, node)
			engineCalls = engineCalls + 1
			local name = type(node) == "number" and u.names[node] or "root"
			local wp = u.worldPoses[name] or { p = { 0, 0, 0 } }
			return { wp.p[1], wp.p[2], wp.p[3] }
		end,
		set_local_position = function(u, node, v)
			engineCalls = engineCalls + 1
			local name = u.names[node]
			u.locals[name].p = { v[1], v[2], v[3] }
		end,
		set_local_rotation = function(u, node, q)
			engineCalls = engineCalls + 1
			setLocalRotCalls = setLocalRotCalls + 1
			local name = u.names[node]
			u.locals[name].q = { q[1], q[2], q[3], q[4] }
			if opts.setLocalRotClearsTrans then
				u.locals[name].p = { 0, 0, 0 }
			end
		end,
		set_local_pose = function(u, node, m)
			engineCalls = engineCalls + 1
			setLocalPoseCalls = setLocalPoseCalls + 1
			local name = u.names[node]
			u.locals[name].p = { m[10], m[11], m[12] }
			-- 从列主序 3x3 还原四元数：测试桩直接复用 mat3->quat 逻辑
			local tr = m[1] + m[5] + m[9]
			local qx, qy, qz, qw
			if tr > 0 then
				local s = math.sqrt(tr + 1) * 2
				qw, qx, qy, qz = 0.25*s, (m[6]-m[8])/s, (m[7]-m[3])/s, (m[2]-m[4])/s
			elseif m[1] > m[5] and m[1] > m[9] then
				local s = math.sqrt(1 + m[1] - m[5] - m[9]) * 2
				qw, qx, qy, qz = (m[6]-m[8])/s, 0.25*s, (m[4]+m[2])/s, (m[7]+m[3])/s
			elseif m[5] > m[9] then
				local s = math.sqrt(1 + m[5] - m[1] - m[9]) * 2
				qw, qx, qy, qz = (m[7]-m[3])/s, (m[4]+m[2])/s, 0.25*s, (m[8]+m[6])/s
			else
				local s = math.sqrt(1 + m[9] - m[1] - m[5]) * 2
				qw, qx, qy, qz = (m[2]-m[4])/s, (m[7]+m[3])/s, (m[8]+m[6])/s, 0.25*s
			end
			u.locals[name].q = { qx, qy, qz, qw }
		end,
		debug_name = function(u) return u.tag or "#ID[avatar]" end,
	}
	local world = {}
	S.World = {
		units = function() engineCalls = engineCalls + 1; return { unit } end,
		units_by_resource = function() engineCalls = engineCalls + 1; return { unit } end,
		update_unit = function(w, u) engineCalls = engineCalls + 1; u.update_world() end,
	}
	S.Application = {
		worlds = function() engineCalls = engineCalls + 1; return { world } end,
		main_world = function() engineCalls = engineCalls + 1; return world end,
		time_since_launch = function() now = now + (1/60); return now end,
	}
	return S, world, function() return engineCalls end, function() engineCalls = 0 end,
		function() return setLocalRotCalls, setLocalPoseCalls end,
		function() setLocalRotCalls = 0; setLocalPoseCalls = 0 end
end

-- 1) 无武器内收验证（含左右肩世界距对称收窄 + fastWrite 校验通过）
do
	Compat._fastWrite = nil
	Compat._fastWriteVerified = nil
	local u = make_rig("unarmed")
	local S, world = make_env(u)
	local lElBefore = u.worldPoses.l_elbow.p[1]
	local rElBefore = u.worldPoses.r_elbow.p[1]
	local shDistBefore = math.abs(u.worldPoses.r_shoulder.p[1] - u.worldPoses.l_shoulder.p[1])
	local written, calls, alpha, targetAlpha, state = Compat.apply_arm_pose(S, world, u, nil, 1/60, { armMode = "auto" })
	local shDistAfter = math.abs(u.worldPoses.r_shoulder.p[1] - u.worldPoses.l_shoulder.p[1])
	check("无武器判定 targetAlpha = 0", targetAlpha == 0 and alpha == 0)
	check("无武器写入 4 根骨 (l/r shoulder+elbow)", written == 4, "written=" .. tostring(written))
	check("无武器引擎调用 <= 34", calls <= 34, "calls=" .. tostring(calls))
	check("无武器左右肩世界距对称收窄", math.abs((shDistBefore - shDistAfter) - 0.05) < 1e-4,
		string.format("%.3f -> %.3f", shDistBefore, shDistAfter))
	check("无武器左手肘向内(+X)移动", u.worldPoses.l_elbow.p[1] > lElBefore + 0.04,
		string.format("%.3f -> %.3f", lElBefore, u.worldPoses.l_elbow.p[1]))
	check("无武器右手肘向内(-X)移动", u.worldPoses.r_elbow.p[1] < rElBefore - 0.04,
		string.format("%.3f -> %.3f", rElBefore, u.worldPoses.r_elbow.p[1]))
end

-- 2) 持枪 2-bone IK 验证（fastWrite 校验通过：<=34 次/帧，手腕误差 < 1mm）
do
	local u = make_rig("rifle")
	local S, world = make_env(u)
	local lHaBefore = { u.worldPoses.l_hand.p[1], u.worldPoses.l_hand.p[2], u.worldPoses.l_hand.p[3] }
	local rHaBefore = { u.worldPoses.r_hand.p[1], u.worldPoses.r_hand.p[2], u.worldPoses.r_hand.p[3] }
	local lQBefore = { u.worldPoses.l_hand.q[1], u.worldPoses.l_hand.q[2], u.worldPoses.l_hand.q[3], u.worldPoses.l_hand.q[4] }
	local written, calls, alpha, targetAlpha = Compat.apply_arm_pose(S, world, u, { alpha = 1.0 }, 1/60, { armMode = "auto" })
	local dl = math.sqrt((u.worldPoses.l_hand.p[1]-lHaBefore[1])^2 + (u.worldPoses.l_hand.p[2]-lHaBefore[2])^2 + (u.worldPoses.l_hand.p[3]-lHaBefore[3])^2)
	local dr = math.sqrt((u.worldPoses.r_hand.p[1]-rHaBefore[1])^2 + (u.worldPoses.r_hand.p[2]-rHaBefore[2])^2 + (u.worldPoses.r_hand.p[3]-rHaBefore[3])^2)
	local qdot = math.abs(u.worldPoses.l_hand.q[1]*lQBefore[1] + u.worldPoses.l_hand.q[2]*lQBefore[2] + u.worldPoses.l_hand.q[3]*lQBefore[3] + u.worldPoses.l_hand.q[4]*lQBefore[4])
	local rotErr = 2 * math.acos(math.min(1, qdot))
	check("持枪判定 targetAlpha = 1", targetAlpha == 1.0)
	check("持枪写入 6 根骨", written == 6, "written=" .. tostring(written))
	check("持枪引擎调用 <= 34 (fastWrite)", calls <= 34, "calls=" .. tostring(calls))
	check("持枪左手世界位置误差 < 1mm", dl < 1e-3, string.format("%.5f m", dl))
	check("持枪右手世界位置误差 < 1mm", dr < 1e-3, string.format("%.5f m", dr))
	check("持枪左手世界旋转误差 < 1mrad", rotErr < 1e-3, string.format("%.5f rad", rotErr))
end

-- 2a) 肩关节目标位置：只挪关节、不额外转角；持枪时从挪过的肩解 IK，手腕仍锁在原位
-- 往里收 6 cm、往上抬 2 cm（测试骨架的锁骨局部 +Y 指向世界上方）。
local SHOULDER_TARGET = { l = { -0.06, 0.02, 0.0 }, r = { 0.06, 0.02, 0.0 } }
local function expected_shoulder(u, side)
	local clav = u.worldPoses[side .. "_clavicle"]
	local o = qrot(clav.q, SHOULDER_TARGET[side])
	return { clav.p[1] + o[1], clav.p[2] + o[2], clav.p[3] + o[3] }
end
local function dist(a, b) return math.sqrt((a[1]-b[1])^2 + (a[2]-b[2])^2 + (a[3]-b[3])^2) end
local function qangle(a, b)
	local d = math.abs(a[1]*b[1] + a[2]*b[2] + a[3]*b[3] + a[4]*b[4])
	return 2 * math.acos(math.min(1, d))
end
do
	Compat._fastWrite = nil
	Compat._fastWriteVerified = nil
	local u = make_rig("unarmed")
	local S, world = make_env(u)
	local function copy4(q) return { q[1], q[2], q[3], q[4] } end
	local lQ = copy4(u.worldPoses.l_shoulder.q)
	local lElQ = copy4(u.worldPoses.l_elbow.q)
	local written, calls, alpha = Compat.apply_arm_pose(S, world, u, nil, 1/60,
		{ armMode = "auto", armShoulderTarget = SHOULDER_TARGET })
	local dl = dist(u.worldPoses.l_shoulder.p, expected_shoulder(u, "l"))
	local dr = dist(u.worldPoses.r_shoulder.p, expected_shoulder(u, "r"))
	check("肩目标·无武器：左肩落到目标位置", dl < 1e-4, string.format("%.5f m", dl))
	check("肩目标·无武器：右肩落到目标位置", dr < 1e-4, string.format("%.5f m", dr))
	check("肩目标·无武器：不再额外内收（上臂朝向不变）", qangle(u.worldPoses.l_shoulder.q, lQ) < 1e-4,
		string.format("%.5f rad", qangle(u.worldPoses.l_shoulder.q, lQ)))
	check("肩目标·无武器：前臂朝向不变", qangle(u.worldPoses.l_elbow.q, lElQ) < 1e-4)
	check("肩目标·无武器：调用 <= 34", alpha == 0 and written == 4 and calls <= 34, "calls=" .. tostring(calls))
end
do
	local u = make_rig("rifle")
	local S, world = make_env(u)
	local lHa = { u.worldPoses.l_hand.p[1], u.worldPoses.l_hand.p[2], u.worldPoses.l_hand.p[3] }
	local rHa = { u.worldPoses.r_hand.p[1], u.worldPoses.r_hand.p[2], u.worldPoses.r_hand.p[3] }
	local written, calls = Compat.apply_arm_pose(S, world, u, { alpha = 1.0 }, 1/60,
		{ armMode = "ik", armShoulderTarget = SHOULDER_TARGET })
	check("肩目标·持枪：左肩落到目标位置", dist(u.worldPoses.l_shoulder.p, expected_shoulder(u, "l")) < 1e-4)
	check("肩目标·持枪：左手腕误差 < 1mm", dist(u.worldPoses.l_hand.p, lHa) < 1e-3,
		string.format("%.5f m", dist(u.worldPoses.l_hand.p, lHa)))
	check("肩目标·持枪：右手腕误差 < 1mm", dist(u.worldPoses.r_hand.p, rHa) < 1e-3,
		string.format("%.5f m", dist(u.worldPoses.r_hand.p, rHa)))
	check("肩目标·持枪：写 6 根骨、调用 <= 40", written == 6 and calls <= 40, "calls=" .. tostring(calls))
end
do
	check("肩目标：缺一侧时整体无效", Compat.arm_shoulder_targets({ l = { 0, 0, 0 } }) == nil)
	check("肩目标：非数/越界时无效", Compat.arm_shoulder_targets({ l = { 0, 0, 0 / 0 }, r = { 0, 0, 0 } }) == nil
		and Compat.arm_shoulder_targets({ l = { 0, 0, 2 }, r = { 0, 0, 0 } }) == nil)
	check("肩目标：合法时原样返回", Compat.arm_shoulder_targets(SHOULDER_TARGET) ~= nil)
end

-- 2d) 腿关节归位：每帧把 thigh/knee/foot/ball 的局部位置写成目标值，旋转不动
local LEG_TARGET = {
	l_thigh = { -0.08, 0.02, 0.09 }, l_knee = { 0.0, 0.0, -0.44 }, l_foot = { 0.0, -0.04, -0.50 }, l_ball = { 0, 0.14, -0.06 },
	r_thigh = { 0.08, 0.02, 0.09 }, r_knee = { 0.0, 0.0, -0.44 }, r_foot = { 0.0, -0.04, -0.50 }, r_ball = { 0, 0.14, -0.06 },
}
do
	Compat._fastWrite = nil
	Compat._fastWriteVerified = nil
	local u = make_rig("unarmed")
	local S, world = make_env(u)
	local kneeQ = { u.locals.l_knee.q[1], u.locals.l_knee.q[2], u.locals.l_knee.q[3], u.locals.l_knee.q[4] }
	local written, calls = Compat.apply_arm_pose(S, world, u, nil, 1/60,
		{ armMode = "auto", armShoulderTarget = SHOULDER_TARGET, legJointTarget = LEG_TARGET })
	local ok = true
	for name, t in pairs(LEG_TARGET) do
		if dist(u.locals[name].p, t) > 1e-9 then ok = false end
	end
	check("腿目标：8 根腿骨局部位置都写成目标值", ok)
	check("腿目标：旋转不动", qangle(u.locals.l_knee.q, kneeQ) < 1e-9)
	-- 世界位置随父骨链传下来：knee 落在 thigh + 目标（测试骨架腿上没有旋转）
	local th, kn = u.worldPoses.l_thigh.p, u.worldPoses.l_knee.p
	check("腿目标：update_unit 后世界位置随之更新", math.abs(kn[3] - (th[3] - 0.44)) < 1e-6,
		string.format("%.4f vs %.4f", kn[3], th[3] - 0.44))
	check("腿目标·无武器：写 4 + 8 根骨", written == 12, "written=" .. tostring(written))
	check("腿目标·首帧调用（含查节点）<= 34 + 32", calls <= 66, "calls=" .. tostring(calls))
	-- 第二帧：节点已缓存，只剩 8 ×（Vector3 + set_local_position）
	local _, calls2 = Compat.apply_arm_pose(S, world, u, select(5, Compat.apply_arm_pose(S, world, u, nil, 1/60,
		{ armMode = "auto", armShoulderTarget = SHOULDER_TARGET, legJointTarget = LEG_TARGET })), 1/60,
		{ armMode = "auto", armShoulderTarget = SHOULDER_TARGET, legJointTarget = LEG_TARGET })
	local _, armOnly = Compat.apply_arm_pose(S, world, u, nil, 1/60, { armMode = "auto", armShoulderTarget = SHOULDER_TARGET })
	print(string.format("       （参考）无武器：只肩 %d 次，肩 + 腿（缓存后）%d 次", armOnly, calls2))
end
do
	local u = make_rig("rifle")
	local S, world = make_env(u)
	local opts = { armMode = "ik", armShoulderTarget = SHOULDER_TARGET, legJointTarget = LEG_TARGET }
	local _, _, _, _, st = Compat.apply_arm_pose(S, world, u, { alpha = 1.0 }, 1/60, opts)
	local lHa = { u.worldPoses.l_hand.p[1], u.worldPoses.l_hand.p[2], u.worldPoses.l_hand.p[3] }
	local written, calls = Compat.apply_arm_pose(S, world, u, st, 1/60, opts)
	check("腿目标·持枪：写 6 + 8 根骨", written == 14, "written=" .. tostring(written))
	check("腿目标·持枪：手腕仍锁住", dist(u.worldPoses.l_hand.p, lHa) < 1e-3)
	print(string.format("       （参考）持枪：肩 + 腿（缓存后）%d 次", calls))
end
do
	local bad = {}
	for k, v in pairs(LEG_TARGET) do bad[k] = v end
	bad.r_ball = nil
	check("腿目标：缺一根骨整体无效", Compat.leg_joint_targets(bad) == nil)
	bad.r_ball = { 0, 0, 0 / 0 }
	check("腿目标：非数无效", Compat.leg_joint_targets(bad) == nil)
	local list = Compat.leg_joint_targets(LEG_TARGET)
	check("腿目标：顺序 thigh → knee → foot → ball", list ~= nil and list[1][1] == "l_thigh" and list[4][1] == "l_ball"
		and list[5][1] == "r_thigh")
	-- 没有腿骨的 unit：一根都不写，也不报错
	local u = make_rig("unarmed")
	u.names.l_knee = nil
	local S, world = make_env(u)
	local written = Compat.apply_arm_pose(S, world, u, nil, 1/60, { armMode = "auto", legJointTarget = LEG_TARGET })
	check("腿目标：缺腿骨时整条腿都不写", written == 4, "written=" .. tostring(written))
end

-- 2b) verify_fast_write 失败时安全退回 set_local_pose（绝不调用 set_local_rotation）
do
	Compat._fastWrite = nil
	Compat._fastWriteVerified = nil
	local u = make_rig("rifle")
	local S, world, _, _, getWriteCounts, resetWriteCounts = make_env(u, { setLocalRotClearsTrans = true })
	local lHaBefore = { u.worldPoses.l_hand.p[1], u.worldPoses.l_hand.p[2], u.worldPoses.l_hand.p[3] }
	local written, calls = Compat.apply_arm_pose(S, world, u, { alpha = 1.0 }, 1/60, { armMode = "ik" })
	local rot1, pose1 = getWriteCounts()
	resetWriteCounts()
	local written2 = Compat.apply_arm_pose(S, world, u, { alpha = 1.0 }, 1/60, { armMode = "ik" })
	local rot2, pose2 = getWriteCounts()
	local dl = math.sqrt((u.worldPoses.l_hand.p[1]-lHaBefore[1])^2 + (u.worldPoses.l_hand.p[2]-lHaBefore[2])^2 + (u.worldPoses.l_hand.p[3]-lHaBefore[3])^2)
	check("verify_fast_write 失败后 _fastWrite=false", Compat._fastWrite == false and Compat._fastWriteVerified == true)
	check("回退路径第1帧使用 set_local_pose 且第2帧零 set_local_rotation",
		written == 6 and pose1 == 6 and rot1 == 1 and written2 == 6 and pose2 == 6 and rot2 == 0,
		string.format("rot1=%d pose1=%d rot2=%d pose2=%d", rot1, pose1, rot2, pose2))
	check("回退矩阵路径手腕位置误差 < 1mm", dl < 1e-3, string.format("%.5f m", dl))
	Compat._fastWrite = nil
	Compat._fastWriteVerified = nil
end

-- 2c) Unit.has_node 守卫与 1-based 节点下标边界校验
do
	local u = make_rig("unarmed")
	local S = make_env(u)
	u.names.l_hand = nil -- has_node 返回 false，即使 node() 返回 1 也必须拒绝
	check("has_node=false 时 resolve_arm_bones 返回 nil", Compat.resolve_arm_bones(S, u, nil) == nil)
	local u2 = make_rig("unarmed")
	local S2 = make_env(u2)
	u2.names.l_hand = u2.nodeCount + 1
	check("节点下标 > nodeCount 时 resolve_arm_bones 返回 nil", Compat.resolve_arm_bones(S2, u2, nil) == nil)
end

-- 3) 平滑过渡 alpha ∈ [0, 1]
do
	local u = make_rig("rifle")
	local S, world = make_env(u)
	local state = Compat.resolve_arm_bones(S, u, nil)
	state.alpha = 0
	local _, _, a1 = Compat.apply_arm_pose(S, world, u, state, 0.06, { armMode = "ik", armBlendTime = 0.12 })
	check("平滑过渡中途 alpha ≈ 0.5", math.abs(a1 - 0.5) < 1e-6, string.format("alpha=%.3f", a1))
	local _, _, a2 = Compat.apply_arm_pose(S, world, u, state, 0.06, { armMode = "ik", armBlendTime = 0.12 })
	check("平滑过渡完成 alpha = 1.0", math.abs(a2 - 1.0) < 1e-6, string.format("alpha=%.3f", a2))
end

-- 4) Runtime 默认关闭（armPose=false）=> 0 额外引擎调用
do
	local u = make_rig("unarmed")
	local S, _, getCalls, resetCalls = make_env(u)
	local logs = {}
	_G.render = nil
	local state = Runtime.install(S, {
		compat = Compat,
		armPose = false,
		carriers = {},
		pollFrames = 20,
		tunePath = "nonexistent-hd2pbx-tune.txt",
		log = function(line) logs[#logs + 1] = line end,
	})
	resetCalls()
	for _ = 1, 5 do _G.render() end
	check("默认 armPose=false 在无物理退避帧不发手臂调用", Runtime.arm_pose_enabled_(state) == false and state.armCalls == nil and getCalls() == 0,
		"calls=" .. tostring(getCalls()))
	Runtime.uninstall()
end

-- 5) Runtime 零物理护甲兼容（armPose=true 且 sessions=nil）+ 节点重建自动刷新
do
	local u = make_rig("unarmed")
	local S = make_env(u)
	local logPath = (os.getenv("TEMP") or os.getenv("TMP") or ".") .. "\\hd2-arm-pose-test.log"
	os.remove(logPath)
	os.remove(logPath .. ".1")
	_G.render = nil
	local state = Runtime.install(S, {
		compat = Compat,
		armPose = true,
		carriers = {},
		pollFrames = 20,
		heartbeatFrames = 3,
		logPath = logPath,
		tunePath = "nonexistent-hd2pbx-tune.txt",
	})
	for _ = 1, 4 do _G.render() end
	local f = io.open(logPath, "r")
	local logText = f and f:read("*a") or ""
	if f then f:close() end
	os.remove(logPath)
	local beatSeen = string.find(logText, "手臂修正心跳", 1, true) ~= nil
	check("零物理护甲 armPose=true 仍能发现 avatar 并驱动", (state.armFrames or 0) >= 3 and (state.armCalls or 0) <= 40,
		string.format("frames=%d calls=%d", state.armFrames or 0, state.armCalls or 0))
	check("零物理护甲 armPose=true 输出手臂心跳", beatSeen)
	-- 模拟场景图重建
	local oldNodeCount = state.armState.nodeCount
	u.nodeCount = oldNodeCount + 5
	_G.render()
	check("num_scene_graph_items 变化自动重建骨骼缓存", state.armState.nodeCount == oldNodeCount + 5)
	-- 模拟 unit 死亡
	u.alive = false
	_G.render()
	check("Unit.alive=false 自动清理手臂缓存", state.armState == nil and state.avatar == nil)
	-- 模拟多人联机已有 2 个 avatars + sessions：armPose 不得覆盖 runtime.avatars
	local teammate = make_rig("unarmed")
	u.alive = true
	state.sessions = { { unit = u, sessions = {} } }
	state.avatars = { { unit = u, tag = "p1" }, { unit = teammate, tag = "p2" } }
	state.avatar = nil
	Runtime.apply_arm_pose_frame_(state)
	check("多人 runtime.avatars 不被手臂修正覆盖",
		#state.avatars == 2 and state.avatars[1].unit == u and state.avatars[2].unit == teammate and state.avatar == u)
	Runtime.uninstall()
end

-- 动作状态日志通过真实 render 入口采集，假引擎检查调用顺序和目标。
do
	local avatar = make_rig("rifle")
	local S, world = make_env(avatar)
	local teammate = make_rig("rifle")
	S.World.units = function() return { avatar, teammate } end
	local calls, order, logs = {}, {}, {}
	local otherCalls = 0
	local hasMachine, layerAvailable = false, true
	local tuple = {}
	for i = 1, 31 do tuple[i] = i end
	tuple[5], tuple[12] = 67, 585
	local second, realTime = 100, os.time
	os.time = function() return second end
	local function record(api, unit)
		if unit ~= avatar then
			otherCalls = otherCalls + 1
			error("动作采集读到了其它 unit")
		end
		calls[api] = (calls[api] or 0) + 1
		order[#order + 1] = api
	end
	local alive = S.Unit.alive
	S.Unit.alive = function(unit)
		if unit == avatar then record("alive", unit) end
		return alive(unit)
	end
	S.Unit.has_animation_state_machine = function(unit)
		record("machine", unit)
		return hasMachine
	end
	S.Unit.animation_get_state = function(unit)
		record("state", unit)
		assert(order[#order - 2] == "alive" and order[#order - 1] == "machine" and hasMachine)
		return unpack(tuple)
	end
	S.Unit.animation_layer_info = function(unit, layer)
		record("layer", unit)
		assert(layer == 11)
		if not layerAvailable then error("missing clip") end
		return 0.12, 2.917
	end
	_G.render = nil
	local state = Runtime.install(S, {
		compat = Compat, armPose = true, stateLog = true, carriers = {}, pollFrames = 1000,
		log = false, tunePath = "nonexistent-hd2pbx-tune.txt",
	})
	state.avatar, state.world = avatar, world
	state.logger = function(fmt, ...)
		if fmt:find("[动作状态]", 1, true) then logs[#logs + 1] = string.format(fmt, ...) end
	end
	_G.render()
	check("动作日志：活体无状态机时不读状态", calls.machine == 1 and calls.state == nil)
	hasMachine = true
	_G.render()
	_G.render()
	check("动作日志：显式打开，首个元组写一次且相同时不读片段",
		#logs == 2 and (logs[1] or ""):find("开始采集 avatar=", 1, true)
		and (logs[2] or ""):find("n=31", 1, true) and calls.state == 2 and calls.layer == 1)
	tuple[5], tuple[12] = 76, 636
	_G.render()
	check("动作日志：输出零基层号、片段时间和完整元组",
		(logs[3] or ""):find("变化层=4:67→76,11:585→636 L11片段=2.917s/t=0.12s 全部=["
			.. table.concat(tuple, ",") .. "]", 1, true))
	layerAvailable = false
	tuple[31] = nil
	_G.render()
	check("动作日志：保留非 31 项，片段读取抛错时回退为问号",
		(logs[4] or ""):find("n=30 变化层=30:31→nil L11片段=? 全部=[" .. table.concat(tuple, ",") .. "]", 1, true))
	for i = 1, 25 do
		tuple[1] = i + 100
		_G.render()
	end
	check("动作日志：限流时仍采状态，跳过片段查询", #logs == 19 and calls.state == 29 and calls.layer == 18)
	second = second + 1
	_G.render()
	check("动作日志：下一秒合并省略数量，无变化时没有补写元组",
		#logs == 20 and (logs[20] or ""):find("省略 10 次变化", 1, true))
	local sampled, machineChecks = calls.state, calls.machine
	avatar.alive = false
	_G.render()
	check("动作日志：死体先停在 alive，不读状态机", calls.machine == machineChecks and calls.state == sampled)
	avatar = make_rig("rifle")
	state.avatar = avatar
	_G.render()
	check("动作日志：avatar 更换时重新宣告并记录首个元组",
		#logs == 22 and (logs[21] or ""):find("开始采集 avatar=", 1, true)
		and (logs[22] or ""):find("变化层=初始", 1, true))
	calls, order = {}, {}
	state.options.stateLog = false
	_G.render()
	check("动作日志：选项关闭时仅保留原有手臂 alive 调用",
		calls.alive == 1 and calls.machine == nil and calls.state == nil and calls.layer == nil and #logs == 22,
		"alive=" .. tostring(calls.alive))
	state.options.stateLog = true
	state.tune = { frame = state.frame, values = { stateLog = 0 } }
	calls, order = {}, {}
	_G.render()
	check("动作日志：调参 stateLog=0 优先关闭且零额外引擎调用",
		calls.alive == 1 and calls.machine == nil and calls.state == nil and calls.layer == nil and #logs == 22,
		"alive=" .. tostring(calls.alive))
	state.tune = nil
	for i = 1, 25 do
		tuple[1] = i + 200
		_G.render()
	end
	avatar = make_rig("rifle")
	state.avatar = avatar
	_G.render()
	check("动作日志：同秒汇总、重新打开和换 avatar 合计最多 20 行", #logs == 39)
	second = second + 1
	_G.render()
	check("动作日志：满额时将换 avatar 通知延至下一秒",
		#logs == 41 and (logs[40] or ""):find("省略 10 次变化", 1, true)
		and (logs[41] or ""):find("开始采集 avatar=" .. tostring(avatar), 1, true))
	check("动作日志：从未读取队友的状态机、元组或片段", otherCalls == 0)
	Runtime.uninstall()
	os.time = realTime
end

-- 6) 独立骨：旧数据可继续使用旋转拷贝。
do
	Compat._fastWrite = nil
	Compat._fastWriteVerified = nil
	local u = make_rig("rifle")
	local armor = make_rig("unarmed")
	-- 护甲上的改名骨：名字不同，节点和原骨同一个（测试桩按节点号回查骨名）。
	for _, name in ipairs({ "l_shoulder", "l_elbow", "l_hand" }) do
		armor.names["ias_" .. name] = armor.names[name]
	end
	local S = make_env(u)
	S.World.units = function() return { u, armor } end
	local logPath = (os.getenv("TEMP") or os.getenv("TMP") or ".") .. "\\hd2-independent-test.log"
	os.remove(logPath)
	local function copy(v) return { v[1], v[2], v[3], v[4] } end
	local avatarShoulderP = { u.locals.l_shoulder.p[1], u.locals.l_shoulder.p[2], u.locals.l_shoulder.p[3] }
	local armorShoulderP = { armor.locals.l_shoulder.p[1], armor.locals.l_shoulder.p[2], armor.locals.l_shoulder.p[3] }
	local armorRightQ = copy(armor.locals.r_shoulder.q)
	_G.render = nil
	local state = Runtime.install(S, {
		compat = Compat,
		armPose = true,
		independentIk = false,
		independentBones = { { "ias_l_shoulder", "l_shoulder" }, { "ias_l_elbow", "l_elbow" }, { "ias_l_hand", "l_hand" } },
		carriers = {},
		pollFrames = 20,
		logPath = logPath,
		tunePath = "nonexistent-hd2pbx-tune.txt",
	})
	for _ = 1, 4 do _G.render() end
	Runtime.uninstall()
	local f = io.open(logPath, "r")
	local text = f and f:read("*a") or ""
	if f then f:close() end
	local same = true
	for _, name in ipairs({ "l_shoulder", "l_elbow", "l_hand" }) do
		if qangle(armor.locals[name].q, u.locals[name].q) > 1e-9 then same = false end
	end
	check("独立骨：护甲改名骨的旋转 = avatar 源骨", same)
	check("独立骨：护甲改名骨的位置不动", dist(armor.locals.l_shoulder.p, armorShoulderP) < 1e-12)
	check("独立骨：护甲没列出的骨不动", qangle(armor.locals.r_shoulder.q, armorRightQ) < 1e-12)
	check("独立骨：不再写 avatar", dist(u.locals.l_shoulder.p, avatarShoulderP) < 1e-12)
	check("独立骨：日志报告找到 1 件", string.find(text, "找到 1 件 / 缺 无", 1, true) ~= nil, text)
	check("独立骨：日志报告写入数", string.find(text, "1件 写入=3", 1, true) ~= nil, text)
	check("独立骨：首次生效", string.find(text, "独立骨首次生效", 1, true) ~= nil, text)
	-- 无抽样帧：读旋转并拆分 6 + 写 3 + update 1。
	check("独立骨：旧数据无抽样帧 10 次调用", state.armCalls == 10, "calls=" .. tostring(state.armCalls))
end

-- 7) 持枪时也只驱动局部旋转。
do
	local function run(armScale, ik, fastWrite, partial, withRest)
		local avatar, armor = make_rig("rifle"), make_rig("rifle")
		local map, rest = {}, {}
		for _, side in ipairs({ "l", "r" }) do
			for _, joint in ipairs({ "shoulder", "elbow", "hand" }) do
				local name = side .. "_" .. joint
				if not partial or side == "r" or joint == "elbow" then
					armor.names["ias_" .. name] = armor.names[name]
				end
				map[#map + 1] = { "ias_" .. name, name }
				if joint ~= "shoulder" then armor.locals[name].p[3] = armor.locals[name].p[3] * armScale end
				rest[#rest + 1] = { armor.parents[name], { 0, 0, 0, 1 }, { 0, 0, 0, 1 }, armor.locals[name].p }
			end
		end
		armor.update_world()
		local shoulders = { l = armor.worldPoses.l_shoulder.p, r = armor.worldPoses.r_shoulder.p }
		local S, _, calls, resetCalls = make_env(avatar)
		S.World.units = function() return { avatar, armor } end
		local avatarWrites = 0
		for _, setter in ipairs({ "set_local_position", "set_local_rotation", "set_local_pose" }) do
			local write = S.Unit[setter]
			S.Unit[setter] = function(unit, ...)
				if unit == avatar then avatarWrites = avatarWrites + 1 end
				return write(unit, ...)
			end
		end
		_G.render = nil
		local state = Runtime.install(S, {
			compat = Compat, armPose = true, independentIk = ik, fastWrite = fastWrite, independentBones = map, carriers = {},
			independentRest = withRest and rest or nil,
			pollFrames = 20, log = false, tunePath = "nonexistent-hd2pbx-tune.txt",
		})
		for _ = 1, 3 do _G.render() end
		resetCalls()
		_G.render()
		local engineCalls = calls()
		local length = dist(armor.worldPoses.l_shoulder.p, armor.worldPoses.l_elbow.p)
		local forearm = dist(armor.worldPoses.l_elbow.p, armor.worldPoses.l_hand.p)
		Runtime.uninstall()
		return avatar, armor, shoulders, avatarWrites, state.armCalls, engineCalls, length, forearm
	end

	local avatar, armor, shoulders, writes, calls, engineCalls = run(0.96, true)
	for _, side in ipairs({ "l", "r" }) do
		local a, b = avatar.worldPoses[side .. "_hand"], armor.worldPoses[side .. "_hand"]
		check("独立骨：持枪不拉手 " .. side, dist(a.p, b.p) > 0.001 and qangle(a.q, b.q) < 1e-6)
		check("独立骨：肩的位置固定 " .. side, dist(armor.worldPoses[side .. "_shoulder"].p, shoulders[side]) < 1e-12)
	end
	check("独立骨：avatar 零写入", writes == 0)
	local _, _, _, _, disabledCalls, disabledEngineCalls = run(0.96, false)
	check("独立骨：旧 IK 选项不改变调用数", calls == disabledCalls and engineCalls == disabledEngineCalls)
	local _, _, _, _, _, _, upper, lower = run(0.4, true, nil, nil, true)
	check("独立骨：不可达时仍保持绑定长度", math.abs(upper - 0.28*0.4) < 1e-6
		and math.abs(lower - 0.26*0.4) < 1e-6)
	local straight = make_rig("unarmed")
	local straightEnv = make_env(straight)
	local previous = { 0, 1, 0 }
	local target = Compat.independent_ik_target(straightEnv, straight, straight.names.l_shoulder,
		straight.names.l_elbow, straight.names.l_hand, previous)
	check("独立骨 IK：肘平面退化时沿用上一帧法线", target and target.normal == previous)
end

-- 8) 新清单、逐件健康与生命周期均经过 render。
do
	local avatar, partner = make_rig("rifle"), make_rig("rifle")
	local function piece(id, x)
		local armor = make_rig("rifle")
		armor.tag = id
		for _, name in ipairs({ "hips", "chest", "l_thigh", "l_shoulder", "l_elbow", "l_hand" }) do
			armor.names["ias_" .. name] = armor.names[name]
		end
		armor.locals.root.p[1] = x
		armor.update_world()
		return armor
	end
	local first = piece("#ID[0000000000000001]", 0)
	local late = piece("#ID[0000000000000002]", -0.7)
	local remote = piece(first.tag, 1.5)
	local tiny = { alive = true, names = {}, nodeCount = 0 }
	partner.locals.root.p[1] = 1.6
	partner.update_world()
	local S, world = make_env(avatar)
	local units = { avatar, partner, remote, first, tiny }
	S.World.units = function() return units end
	S.World.units_by_resource = function() return { avatar, partner } end
	local position = S.Unit.world_position
	S.Unit.world_position = function(u, node)
		assert(u ~= tiny, "must filter shoulder before world_position")
		return position(u, node)
	end
	local expected = { "ias_hips", "ias_chest", "ias_l_thigh", "ias_l_shoulder", "ias_l_elbow", "ias_l_hand" }
	local map, rest = {}, {}
	local identity = { 0, 0, 0, 1 }
	for i, name in ipairs(expected) do
		local source = name:sub(5)
		map[i] = { name, source }
		rest[i] = { first.parents[source], identity, identity, first.locals[source].p }
	end
	_G.render = nil
	local state = Runtime.install(S, { compat = Compat, armPose = true, carriers = {}, pollFrames = 1000, independentMissRescanFrames = 30,
		independentPieces = { [first.tag] = expected, [late.tag] = expected }, independentBones = map, independentRest = rest,
		independentTranslations = { [first.tag] = { ias_l_elbow = { avatar.locals.l_elbow.p, first.locals.l_elbow.p } } },
		log = false, tunePath = "nonexistent-hd2pbx-tune.txt" })
	state.avatar, state.world, state.cache.world = avatar, world, world
	local log = {}
	state.logger = function(fmt, ...) log[#log + 1] = string.format(fmt, ...) end
	local function render(n) for _ = 1, n do _G.render() end end
	avatar.locals.hips.q, avatar.locals.l_thigh.q = { 0, 0, 0.6, 0.8 }, { 0.6, 0, 0, 0.8 }
	avatar.update_world()
	render(1)
	local tracked = state.independent.pieces[1]
	check("清单：同 ID 的队友件在本地件之前也不会错绑", tracked and tracked.unit == first and #state.independent.pieces == 1)
	check("动作日志默认关闭", state.actionStateLog == nil)
	check("全身：躯干、腿和手臂在同一帧复制旋转",
		qangle(first.locals.hips.q, avatar.locals.hips.q) < 1e-6
		and qangle(first.locals.chest.q, avatar.locals.chest.q) < 1e-6
		and qangle(first.locals.l_thigh.q, avatar.locals.l_thigh.q) < 1e-6
		and qangle(first.locals.l_shoulder.q, avatar.locals.l_shoulder.q) < 1e-6)
	check("清单：首次明确记录缺件", table.concat(log, "\n"):find("缺 " .. late.tag, 1, true) ~= nil)
	units[#units + 1] = late
	first.locals.root.p[1] = -2
	first.update_world()
	render(29)
	check("清单：未到 30 帧不重扫", #state.independent.pieces == 1)
	render(1)
	check("清单：30 帧补齐，存活件越过旧距离阈值仍保留", #state.independent.pieces == 2 and state.independent.pieces[1] == tracked)
	check("清单：肩骨离本地根超过 1 m 仍可找到", state.independent.pieces[2].unit == late)

	state.options.independentGrips, state.options.poseEveryFrame = {}, true
	avatar.mainLayer, avatar.clip = 865, 3
	render(1)
	check("E11 动作：实测换弹状态及片段匹配", state.independent.action == 2)
	avatar.clip = 3.006
	render(1)
	check("E11 动作：换弹片段误差超过 5 ms 时不介入", state.independent.action == 0)
	avatar.clip, avatar.clipTime = 3, 3.026
	render(1)
	check("E11 动作：越过片段 25 ms 时不介入", state.independent.action == 0)
	avatar.mainLayer, avatar.clip, avatar.clipTime, avatar.weaponSelected = 650, 1e37, 0, 1
	render(1)
	check("E11 动作：验证飞船持枪树、空状态和武器变量", state.independent.action == 1)
	avatar.mainLayer, avatar.clip = 868, 29/60
	render(1)
	check("E11 动作：短主动作跟原生手部轨迹", state.independent.action == 2)
	avatar.mainLayer, avatar.clip, avatar.weaponSelected = 585, 1e37, math.huge
	render(1)
	check("E11 动作：无效武器变量关闭求解", state.independent.action == 0)
	avatar.weaponSelected = 1
	avatar.upperLayer, avatar.weaponLayer = 75, 99
	render(1)
	check("E11 动作：未知武器树关闭求解", state.independent.action == 0)
	avatar.upperLayer, avatar.weaponLayer = 59, 67
	avatar.menu = true
	render(1)
	check("E11 动作：菜单恢复基础姿势", state.independent.action == 0 and state.independent.correction.due)
	avatar.menu, avatar.clip = false, 1e37
	render(1)
	check("E11 动作：任务空主层 585 使用持枪", state.independent.action == 1)
	avatar.machine = false
	render(1)
	check("E11 动作：状态机消失清除分类", state.independent.action == 0 and next(state.independent.contacts) == nil)
	avatar.machine = true
	local layer = S.Unit.animation_layer_info
	S.Unit.animation_layer_info = function() error("clip unavailable") end
	avatar.mainLayer = 650
	render(1)
	check("E11 动作：飞船空状态无片段证据时不介入", state.independent.action == 0)
	S.Unit.animation_layer_info = layer

	state.options.independentGrips, state.options.poseEveryFrame = nil, nil
	local actionCalls, onlyLocal = {}, true
	for _, name in ipairs({ "has_animation_state_machine", "animation_get_state", "animation_layer_info" }) do
		local original = S.Unit[name]
		S.Unit[name] = function(u, ...)
			if u == avatar then actionCalls[name] = (actionCalls[name] or 0) + 1
			elseif name ~= "has_animation_state_machine" then onlyLocal = false end
			return original(u, ...)
		end
	end
	state.options.stateLog = true
	state.independent.rescanAt = state.frame + 1
	render(1)
	check("动作：同帧补扫与日志复用本地 avatar 状态",
		onlyLocal and actionCalls.has_animation_state_machine == 1
		and actionCalls.animation_get_state == 1 and actionCalls.animation_layer_info == 1)
	state.options.stateLog = false

	local localPosition = S.Unit.local_position
	local elbowBind = avatar.locals.l_elbow.p
	avatar.locals.l_elbow.p = { elbowBind[1] + 0.01, elbowBind[2], elbowBind[3] }
	avatar.update_world()
	render(1)
	check("平移骨：模型静止位置加源骨变化", math.abs(first.locals.l_elbow.p[1] - elbowBind[1] - 0.01) < 1e-9)
	S.Unit.local_position = function(u, node)
		if u == avatar and node == avatar.names.l_elbow then return nil end
		return localPosition(u, node)
	end
	render(2)
	check("隔离：平移失败时恢复绑定平移并继续复制旋转", not tracked.isolated and first.visible ~= false
		and math.abs(first.locals.l_elbow.p[1] - elbowBind[1]) < 1e-9
		and qangle(first.locals.l_shoulder.q, avatar.locals.l_shoulder.q) < 1e-6)
	render(1)
	check("隔离：连续三次平移失败，仅隔离该件", tracked.isolated and not state.independent.pieces[2].isolated
		and first.visible ~= false)
	local writeRotation = S.Unit.set_local_rotation
	S.Unit.set_local_rotation = function(u, ...)
		if u == first then return false end
		return writeRotation(u, ...)
	end
	render(1)
	check("隔离：旋转兜底也失败则隐藏本件", first.visible == false and late.visible ~= false)
	S.Unit.local_position, S.Unit.set_local_rotation = localPosition, writeRotation
	render(2)
	check("隔离：两帧正常仍隐藏", first.visible == false)
	render(3)
	check("隔离：三次健康检查重新显示", first.visible == true and not tracked.isolated)

	state.avatars = { { unit = avatar, cameraDistance = 0 }, { unit = partner, cameraDistance = 64 } }
	local beforeRemote = remote.locals.l_elbow.p
	local remoteUpdates = 0
	local update = S.World.update_unit
	S.World.update_unit = function(w, u)
		if u == remote then remoteUpdates = remoteUpdates + 1 end
		return update(w, u)
	end
	render(4)
	check("队友：默认关闭", remoteUpdates == 0)
	state.options.independentTeammates = true
	render(8)
	check("队友：远处每四帧更新一次且不写平移", remoteUpdates == 2
		and dist(beforeRemote, remote.locals.l_elbow.p) < 1e-9)
	state.options.independentTeammates = false
	state.avatars = nil
	S.World.update_unit = update

	S.Unit.world_position = function(u, node)
		local p = position(u, node)
		if u == first and node == first.names.l_hand then p[1] = p[1] + 0.021 end
		return p
	end
	for _ = 1, 30 do
		render(1)
		if tracked.failures == 1 then break end
	end
	check("隔离：30 帧内采样发现 21 mm 世界残差", tracked.failures == 1 and state.independent.pieces[2].failures == 0)
	render(4)
	check("隔离：持续回读超差会隔离本件", tracked.isolated and not state.independent.pieces[2].isolated)
	S.Unit.world_position = position
	render(4)
	check("隔离：回读恢复后本件重新显示", first.visible == true and not tracked.isolated)

	local readScale = S.Unit.local_scale
	local piecesPerFrame, scaleReads = 0, {}
	S.Unit.local_scale = function(u, node)
		if not scaleReads[u] then piecesPerFrame = piecesPerFrame + 1 end
		scaleReads[u] = true
		return readScale(u, node)
	end
	first.scale = { 1, 1, 2 }
	local maxSamplePieces = 0
	for _ = 1, 34 do
		piecesPerFrame, scaleReads = 0, {}
		render(1)
		maxSamplePieces = math.max(maxSamplePieces, piecesPerFrame)
		if tracked.isolated then break end
	end
	check("隔离：仅缩放异常也会在轮询后隔离本件", tracked.isolated and first.visible == false and late.visible ~= false)
	check("隔离：常规抽样和缩放复查每帧最多一件", maxSamplePieces == 1)
	first.scale = nil
	render(4)
	check("隔离：缩放恢复后本件重新显示", first.visible == true and not tracked.isolated)
	S.Unit.local_scale = readScale

	local write = S.Unit.set_local_rotation
	S.Unit.set_local_rotation = function(u, node, value)
		if u == first then return false end
		return write(u, node, value)
	end
	render(3)
	check("隔离：setter 返回 false 也算写入失败", tracked.isolated and first.visible == false)
	S.Unit.set_local_rotation = write
	render(3)

	local before = state.independent
	local newWorld = {}
	S.Application.worlds = function() return { newWorld } end
	state.world, state.cache.world = newWorld, newWorld
	render(1)
	check("生命周期：世界变化重建独立件状态", state.independent ~= before and state.independent.world == newWorld)
	before = state.independent
	state.avatar = partner
	render(1)
	check("生命周期：avatar 变化重建动作与件状态", state.independent ~= before and state.independent.avatar == partner)
	Runtime.uninstall()
end

-- 9) 局部复制直接转发四元数。
do
	local avatar, armor = make_rig("unarmed"), make_rig("unarmed")
	local s = math.sqrt(0.5)
	local qx, qy, qz = { s, 0, 0, s }, { 0, s, 0, s }, { 0, 0, s, s }
	avatar.locals.l_shoulder.q = qz
	avatar.locals.l_elbow.q = qy
	armor.locals.l_clavicle.q = qmul(armor.locals.l_clavicle.q, qy)
	armor.names.ias_l_shoulder = armor.names.l_shoulder
	armor.names.ias_l_elbow = armor.names.l_elbow
	avatar.update_world()
	armor.update_world()
	local S, world = make_env(avatar)
	S.World.units = function() return { avatar, armor } end
	_G.render = nil
	local options = { compat = Compat, armPose = true, independentIk = false, carriers = {}, log = false,
		independentBones = { { "ias_l_shoulder", "l_shoulder" }, { "ias_l_elbow", "l_elbow" } },
		independentRest = { { "l_clavicle", { 0, 0, 0, 1 }, qx, armor.locals.l_shoulder.p },
			{ "l_shoulder", { 0, 0, 0, 1 }, qz, armor.locals.l_elbow.p } },
		tunePath = "nonexistent-hd2pbx-tune.txt" }
	local state = Runtime.install(S, options)
	state.avatar, state.world, state.cache.world = avatar, world, world
	_G.render()
	check("局部复制：肩肘直接使用源局部旋转", qangle(armor.locals.l_shoulder.q, qz) < 1e-6
		and qangle(armor.locals.l_elbow.q, qy) < 1e-6)
	local raw, received, validations, componentReads = nil, nil, 0, 0
	local read, write = S.Unit.local_rotation, S.Unit.set_local_rotation
	local toElements = S.Quaternion.to_elements
	S.Quaternion.is_valid = function(value)
		validations = validations + 1
		return value ~= nil
	end
	S.Quaternion.to_elements = function(value)
		componentReads = componentReads + 1
		return toElements(value)
	end
	S.Unit.local_rotation = function(u, node)
		local value = read(u, node)
		if u == avatar and node == avatar.names.l_shoulder then raw = value end
		return value
	end
	S.Unit.set_local_rotation = function(u, node, value)
		if u == armor and node == armor.names.l_shoulder then received = value end
		return write(u, node, value)
	end
	_G.render()
	check("局部复制：传给 setter 的对象与读取对象相同", raw ~= nil and raw == received)
	check("局部复制：正常帧校验原对象，无需拆分", validations == 2 and componentReads == 0)
	Runtime.uninstall()
end

-- 10) Native links use the live source as a cross-unit parent, even when Lua never writes.
do
	local avatar, nextAvatar = make_rig("unarmed"), make_rig("unarmed")
	local armor, failed = make_rig("unarmed"), make_rig("unarmed")
	armor.tag, failed.tag = "#ID[e700000000000001]", "#ID[e700000000000002]"
	local expected = { "ias_hips", "ias_l_thigh", "ias_l_knee" }
	local map, rest = {}, {}
	for index, name in ipairs(expected) do
		local source = name:sub(5)
		map[index] = { name, source }
		rest[index] = { armor.parents[source], avatar.locals[source].q, armor.locals[source].q, armor.locals[source].p }
		for _, piece in ipairs({ armor, failed }) do piece.names[name] = piece.names[source] end
	end
	armor.locals.l_thigh.p = { -0.20, 0, 0 }
	armor.update_world()
	local S, world = make_env(avatar)
	local units, owners = { avatar, armor, failed }, { avatar }
	local replacement, peerPiece
	S.World.units = function() return units end
	S.World.units_by_resource = function() return owners end
	local links, unlinks, poseWrites, refreshes = 0, 0, 0, 0
	local setPosition, update = S.Unit.set_local_position, S.World.update_unit
	S.Unit.set_local_position = function(unit, node, p)
		poseWrites = poseWrites + 1
		return setPosition(unit, node, p)
	end
	S.World.update_unit = function(w, unit)
		if unit == armor then refreshes = refreshes + 1 end
		return update(w, unit)
	end
	S.Unit.scene_graph_parent = function(unit, node)
		return unit.names[unit.parents[unit.names[node]]]
	end
	S.Unit.scene_graph_link = function(unit, node, parent)
		unit.parents[unit.names[node]] = unit.names[parent]
	end
	S.World.link_unit = function(_, unit, node, owner, source)
		assert(unit.alive and owner.alive)
		links = links + 1
		local name = unit.names[node]
		if unit == failed and name == "l_thigh" then return false end
		unit.links = unit.links or {}
		unit.links[name] = { owner = owner, source = owner.names[source] }
		unit.parents[name] = nil
		unit.locals[name].q = { 0, 0, 0, 1 }
	end
	S.World.unlink_unit = function(_, unit)
		assert(unit.alive and (unit == armor or unit == failed or unit == replacement or unit == peerPiece))
		unlinks = unlinks + 1
		unit.links = {}
	end
	local camera = { 0, -4, 2 }
	S.World.debug_camera_pose = function() return camera end
	S.Matrix4x4.translation = function(p) return p end
	local opts = { compat = Compat, armPose = true, carriers = {}, log = false, poseHz = 30,
		independentPieces = { [armor.tag] = expected, [failed.tag] = expected },
		independentBones = map, independentRest = rest }
	local savedIo = _G.io
	_G.io = nil
	local state = Runtime.install(S, opts)
	state.avatar, state.world, state.cache.world = avatar, world, world
	state.avatars = { { unit = avatar } }
	local logs = {}
	state.logger = function(fmt, ...) logs[#logs + 1] = string.format(fmt, ...) end
	local function tick(time)
		state.frame, state.lastTime, state.dt = state.frame + 1, time, 0.01
		state.frameAnimation, state.liveUnits, state.liveUnitsWorld = nil, units, world
		for _, unit in ipairs(owners) do unit.update_world() end
		for _, unit in ipairs(units) do unit.update_world() end
		Runtime.apply_arm_pose_frame_(state)
	end
	tick(0)
	check("E7：无 io 可启动且默认挂接", #state.independent.pieces == 2
		and armor.links.hips.owner == avatar and armor.links.l_knee.source == "l_knee" and links == 5)
	check("E7：挂接到一半失败，仅本件恢复父链并记日志", unlinks == 1 and next(failed.links) == nil
		and failed.parents.hips == "root" and failed.parents.l_thigh == "hips"
		and table.concat(logs):find("挂接回退", 1, true) ~= nil)
	local p = armor.worldPoses.l_thigh.p[1]
	avatar.locals.root.p[1] = 0.1
	avatar.locals.hips.q = { 0, 0, 0.6, 0.8 }
	local oldRefresh = refreshes
	tick(0.01)
	check("E7：跟随帧由引擎更新，不刷新已挂件", not state.independent.correction.due
		and refreshes == oldRefresh and math.abs(armor.worldPoses.l_thigh.p[1] - p) > 0.01)
	check("E7：失败件继续走 E6 逐帧旋转", qangle(failed.locals.hips.q, avatar.locals.hips.q) < 1e-6)
	tick(0.04)
	local linkedPiece = state.independent.pieces[1]
	check("E7：默认 30 Hz，步态在修正帧展示中点", state.independent.correction.due
		and linkedPiece.link.pending ~= nil and dist(armor.locals.l_thigh.p, linkedPiece.link.rows[2].endpoint.p) > 1e-4)
	tick(0.05)
	check("E7：到半周期写回精确终点", linkedPiece.link.pending == nil
		and dist(armor.locals.l_thigh.p, linkedPiece.link.rows[2].endpoint.p) < 1e-9)
	oldRefresh = refreshes
	tick(0.07)
	check("E7：修正值相同跳过 update_unit", state.independent.correction.due and refreshes == oldRefresh)
	camera = { 0.1, 0, 1.72 }
	tick(0.11)
	check("E7：第一人称暂停修正", state.independent.correction.paused and not state.independent.correction.due)
	camera = { 0, -4, 2 }
	avatar.menu = true
	tick(0.12)
	check("E7：菜单暂停修正", state.independent.correction.paused)
	avatar.menu = false
	tick(0.13)
	check("E7：回第三人称立即修正", state.independent.correction.due)
	opts.independentLink = 0
	tick(0.14)
	check("E7：independentLink=0 原地解挂并回到 E6", next(armor.links) == nil and armor.parents.l_thigh == "hips"
		and qangle(armor.locals.hips.q, avatar.locals.hips.q) < 1e-6)
	opts.independentLink, opts.poseBlend, opts.poseHz = 1, 0, 60
	tick(0.15)
	tick(0.17)
	check("E7：60 Hz 和关闭插帧生效", state.independent.correction.due and linkedPiece.link.pending == nil)
	opts.poseEveryFrame, opts.poseBlend = true, 1
	tick(0.171)
	check("E10：每帧修正绕过计时与展示插帧", state.independent.correction.due and not state.independent.correction.blend)
	opts.poseEveryFrame = nil
	state.avatar, state.avatars = nextAvatar, { { unit = nextAvatar } }
	units, owners = { nextAvatar, armor }, { nextAvatar }
	avatar.alive = false
	tick(0.18)
	check("E7：avatar 更换时同一件重挂到新主人", armor.links.hips.owner == nextAvatar)
	units = { nextAvatar }
	local oldUnlinks = unlinks
	armor.alive = false
	tick(0.19)
	check("E7：件消失时放弃句柄，不向死件解挂或写入", #state.independent.pieces == 0
		and unlinks == oldUnlinks and state.independent.rescanAt == state.frame + 1)
	replacement = make_rig("unarmed")
	replacement.tag = armor.tag
	for _, name in ipairs(expected) do replacement.names[name] = replacement.names[name:sub(5)] end
	units = { nextAvatar, replacement }
	tick(0.20)
	check("E7：换装后扫描重新挂接同槽新件", replacement.links.hips.owner == nextAvatar)
	nextAvatar.alive = false
	tick(0.21)
	check("E7：avatar 死亡解挂仍活着的件", next(replacement.links) == nil and state.independent == nil
		and replacement.parents.l_thigh == "hips")

	local peer = make_rig("unarmed")
	peerPiece = make_rig("unarmed")
	peerPiece.tag = failed.tag
	for _, name in ipairs(expected) do peerPiece.names[name] = peerPiece.names[name:sub(5)] end
	peer.locals.root.p[1], peerPiece.locals.root.p[1] = 4, 4
	nextAvatar.alive = true
	state.avatar, state.avatars = nextAvatar, { { unit = nextAvatar }, { unit = peer, cameraDistance = 16 } }
	units, owners = { nextAvatar, replacement, peer, peerPiece }, { nextAvatar, peer }
	tick(0.22)
	check("E7：队友默认关，不建立原生挂接", peerPiece.links == nil)
	opts.independentTeammates = true
	local near, far = 0, 0
	for i = 0, 59 do
		tick(0.25 + i/60)
		local remote = state.independentRemotes[peer]
		if remote.correction.due then near = near + 1 end
	end
	check("E7：近处队友 20 Hz，始终关插帧", near == 20
		and not state.independentRemotes[peer].correction.blend and peerPiece.links.hips.owner == peer)
	state.avatars[2].cameraDistance = 100
	for i = 0, 59 do
		tick(1.25 + i/60)
		local remote = state.independentRemotes[peer]
		if remote.frame == state.frame and remote.correction.due then far = far + 1 end
	end
	check("E7：6 m 外沿用每四帧检查，60 fps 下最多每秒 15 次", far == 15)
	Runtime.uninstall()
	_G.io = savedIo
end

-- 11) Physics presentation keeps its exact endpoint available to the solver.
do
	local armor = make_rig("unarmed")
	local S, world = make_env(armor)
	local state = Runtime.install(S, { compat = Compat, log = false })
	local node = armor.names.hips
	local session = { S = S, unit = armor, world = world, sessions = {} }
	local row = { from = { 0, 0, 1, 0 }, to = { 0, 0, 1, 1 }, base = { px = 0, py = 0, pz = 1 } }
	session.independentPresentation = { start = 0, period = 0.04, step = 1, lead = 0.25, rows = { [node] = row } }
	state.dt = 0.005
	local schedule = { due = false, now = 0, nextAt = 0.04, blend = true }
	Compat.present_independent_physics(session, 0.25)
	local function display(t)
		schedule.now = t
		session.engineCalls = 0
		return Runtime.independent_physics_(state, session, schedule, nil, 0)
	end
	check("E7 物理：四分之一周期内跳过重复展示", display(0.005) == 0 and display(0.010) == 1 and display(0.015) == 0)
	check("E7 物理：周期内三个中间值，第四步到终点", display(0.020) == 1 and display(0.025) == 0
		and display(0.030) == 1 and session.independentPresentation == nil)
	session.independentPresentation = { rows = { [node] = row } }
	Compat.present_independent_physics(session, 0.5)
	schedule.due = true
	local observed
	local _, solved = Runtime.independent_physics_(state, session, schedule, function()
		observed = armor.locals.hips.q
	end, 0.04)
	check("E7 物理：提前求解也先恢复精确终点", solved and qangle(observed, { 0, 0, math.sin(0.5), math.cos(0.5) }) < 1e-6)
	Runtime.uninstall()
end

-- 12) Contact input, girdle limits, and the local weapon transaction.
do
	local Link, V = Runtime.independent_contact_, Compat.contact_math
	local q = { 0, 0, 0, 1 }
	local function pose(x, y, z) return { p = { x, y, z }, q = q } end
	local native = { r_hand = pose(0, 0, 0), l_hand = pose(0, 1, 0),
		attach_hand_r = pose(0.1, 0, 0), attach_hand_l = pose(0.67, 1, 0) }
	local function read(name) return native[name] end
	local input = Link.contacts(1, read)
	check("E11 握点：普通右握点和左侧原生挂点原样保留",
		input.r == native.attach_hand_r and input.l == native.attach_hand_l)
	local grip = Link.motionGrips.r
	local radius = V.length(grip.p)
	native.attach_hand_r = pose(3.5*radius, 0, 0)
	input = Link.contacts(1, read)
	check("E11 握点：长武器在 3r 到 4r 之间按 smoothstep 混合",
		dist(input.r.p, V.scale(V.add(native.attach_hand_r.p, grip.p), 0.5)) < 1e-8)
	native.attach_hand_r = pose(0.8, 0, 0)
	input = Link.contacts(1, read)
	check("E11 握点：长武器过 4r 时恢复原生手轨迹", dist(input.r.p, grip.p) < 1e-9)
	input = Link.contacts(2, read)
	check("E11 握点：换弹两侧都用手骨生成握点",
		dist(input.r.p, grip.p) < 1e-9 and dist(input.l.p, V.add(native.l_hand.p, Link.motionGrips.l.p)) < 1e-9)
	native.l_hand = nil
	check("E11 握点：换弹缺少手骨时不借用武器挂点", Link.contacts(2, read).l == nil)
	local valid = { 1,0,0,0.08, 0,1,0,0, 0,0,1,0 }
	local author = V.matrix_pose(valid)
	local inverse = V.inverse(author)
	local target = Compat.independent_fk(pose(0.3, 0, 0), inverse.q, inverse.p)
	check("E11 握点：世界接触框乘作者握点逆得到手腕", dist(target.p, { .22, 0, 0 }) < 1e-9)
	valid[11] = -1
	check("E11 握点：拒绝镜像矩阵", V.matrix_pose(valid) == nil)

	local pistol, rifle = { sidearm = true, left = pose(0, 0, 0) }, { left = pose(0, 0, 0) }
	local _, close = Link.support_weight(pistol, pose(0.029, 0, 0))
	local _, far = Link.support_weight(pistol, pose(0.030, 0, 0))
	check("E11 左手权重：手枪分界为 3 cm", close == 1 and far == 0)
	local _, middle = Link.support_weight(rifle, pose(0.04, 0, 0))
	check("E11 左手权重：主武器在 2 到 6 cm 之间平滑变化", math.abs(middle-0.5) < 1e-9)
	rifle.envelope = { 0.2, -0.02, -0.02, 0.3, 0.02, 0.02 }
	local near, envelope = Link.support_weight(rifle, pose(0.25, 0, 0), nil, pose(0, 0, 0))
	check("E11 左手权重：控制框扫掠覆盖左手时保留支撑", near and envelope == 1)

	local model = { pose(0.25, 0, 0), pose(0.55, 0, 0), pose(0.55, 0.25, 0) }
	local lengths, farWrist = { 0.30, 0.25 }, pose(-0.25, 1, 0)
	local delta, factor = Compat.contact_girdle(pose(0, 0, 0), model[1], farWrist, lengths, model, true)
	local twoHand = Compat.contact_girdle(pose(0, 0, 0), model[1], farWrist, lengths, model, false)
	check("E11 单手：肩带 15°、拉伸最高 5%", qangle(delta, q) <= math.rad(15)+1e-7 and factor == 1.05)
	check("E11 双手：左肩带最多 25°", qangle(twoHand, q) <= math.rad(25)+1e-7)
	local a, b, c = Compat.contact_arm(model, model, farWrist, lengths, factor, false)
	check("E11 两骨：不可达手腕裁剪到伸展上限且保留朝向",
		math.abs(dist(a.p, b.p)-0.30*1.05) < 1e-7 and math.abs(dist(b.p, c.p)-0.25*1.05) < 1e-7
		and math.abs(dist(a.p, c.p)-0.55*1.05*0.99999) < 1e-7 and c.q == q)

	local avatar = make_rig("unarmed")
	local weapon = make_rig("unarmed")
	local magazine = make_rig("unarmed")
	local fp = make_rig("rifle")
	local twin = make_rig("rifle")
	weapon.tag, weapon.nodeCount, weapon.names.StingrayEntityRoot = "#ID[1111111111111111]", 2, 1
	local S, world = make_env(avatar)
	magazine.tag, magazine.nodeCount, magazine.names.StingrayEntityRoot = "#ID[2222222222222222]", 2, 1
	magazine.links = { root = { owner = weapon, source = "hips" } }
	magazine.update_world()
	local units, graphs = { avatar, weapon, magazine }, { fp }
	S.World.units = function() return units end
	S.World.units_by_resource = function() return graphs end
	S.Unit.scene_graph_parent = function(unit, node) return unit.names[unit.parents[unit.names[node]]] end
	local oldUpdate = _G.update
	local original = weapon.locals.hips.p
	local observed
	local function update() observed = weapon.locals.hips.p end
	_G.update = update
	local runtime = Runtime.install(S, { compat = Compat, log = false })
	local spec = { 2, 1, 258, true }
	local found = Link.find_weapon(units, pose(0, 0, 0), { ["1111111111111111"] = spec })
	check("E11 武器归属：核对资源、拓扑和原生右手父框", found and found.node == 2 and found.sidearm)
	check("E11 武器归属：远离或重复匹配时不使用",
		not Link.find_weapon(units, pose(0.003, 0, 0), { ["1111111111111111"] = spec })
		and not Link.find_weapon({ weapon, weapon }, pose(0, 0, 0), { ["1111111111111111"] = spec }))
	local accessoryPos = magazine.worldPoses.root.p
	Link.weaponMembers[found.id] = "2222222222222222:2"
	local followers = Link.weapon_followers(units, found)
	check("E11 拉枪：独立弹匣按原生父框随武器根走",
		followers and #followers == 2 and followers[2].unit == magazine
		and Link.weapon_followers({ weapon }, found) == nil)
	local shifted = Link.shift_weapon(runtime, world, found, { 0.18, 0, 0 }, followers)
	check("E11 拉枪：武器根以原生父框换算世界偏移",
		shifted and dist(weapon.locals.hips.p, V.add(original, { 0.18, 0, 0 })) < 1e-9
		and dist(magazine.worldPoses.root.p, V.add(accessoryPos, { 0.18, 0, 0 })) < 1e-9)
	_G.update()
	check("E11 拉枪：先还原根和弹匣再执行原生 update", dist(observed, original) < 1e-9
		and dist(magazine.worldPoses.root.p, accessoryPos) < 1e-9 and runtime.weaponShift == nil)
	Link.shift_weapon(runtime, world, found, { 0.1, 0, 0 })
	weapon.locals.hips.p = { 0, 0, 2 }
	_G.update()
	check("E11 拉枪：引擎已更新的位置继续保留", observed[3] == 2 and runtime.weaponShift == nil)
	weapon.locals.hips.p = original
	weapon.update_world()
	magazine.update_world()
	local updateUnit = S.World.update_unit
	S.World.update_unit = function(w, unit)
		if unit == magazine then return false end
		return updateUnit(w, unit)
	end
	followers = Link.weapon_followers(units, found)
	check("E11 拉枪：子件更新失败撤回根位移并禁用试验",
		not Link.shift_weapon(runtime, world, found, { 0.1, 0, 0 }, followers)
		and runtime.weaponTrialDisabled and dist(weapon.locals.hips.p, original) < 1e-9)
	S.World.update_unit = updateUnit
	_G.update()
	check("E11 拉枪：子件可刷新后清除还原记录", runtime.weaponShift == nil)
	Link.weaponMembers[found.id] = nil
	local tied = { world = world, pieces = { { unit = twin } } }
	check("E11 第一人称：当前原生手臂唯一匹配才判 3",
		Link.first_person_action(tied, function(n) return avatar.worldPoses[n] end) == 3)
	tied.pieces[1].unit = avatar
	check("E11 第一人称：第三人称匹配优先",
		Link.first_person_action(tied, function(n) return avatar.worldPoses[n] end) == 0)
	graphs = { fp, twin }
	tied.pieces[1].unit = twin
	check("E11 第一人称：重复匹配关闭求解",
		Link.first_person_action(tied, function(n) return avatar.worldPoses[n] end) == 0)
	check("E11 拉枪：卸载恢复原 update", Runtime.uninstall() and _G.update == update)
	_G.update = oldUpdate
end

print(string.format("\n失败 %d 项", failures))
os.exit(failures == 0 and 0 or 1)
