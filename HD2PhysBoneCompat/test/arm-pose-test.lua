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
			if par == nil then
				unit.worldPoses[name] = { p = { lp.p[1], lp.p[2], lp.p[3] }, q = { lp.q[1], lp.q[2], lp.q[3], lp.q[4] } }
			else
				local pw = unit.worldPoses[par]
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
		has_animation_state_machine = function() engineCalls = engineCalls + 1; return true end,
		has_node = function(u, name) engineCalls = engineCalls + 1; return u.names[name] ~= nil end,
		node = function(u, name) engineCalls = engineCalls + 1; return u.names[name] or 1 end,
		local_pose = function(u, node)
			engineCalls = engineCalls + 1
			local name = u.names[node]
			local lp = u.locals[name]
			local c = q_to_mat3_colmajor(lp.q)
			return { c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], lp.p[1], lp.p[2], lp.p[3] }
		end,
		local_rotation = function(u, node)
			engineCalls = engineCalls + 1
			local name = u.names[node]
			local q = u.locals[name].q
			return { q[1], q[2], q[3], q[4] }
		end,
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
		debug_name = function() return "#ID[avatar]" end,
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

print(string.format("\n失败 %d 项", failures))
os.exit(failures == 0 and 0 or 1)
