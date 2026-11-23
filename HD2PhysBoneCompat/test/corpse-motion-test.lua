-- E12f-C1：CorpseMotion（镜像 IAS __hd2ir_corpse_motion，R:5248-5429）回归。
-- 跑法：luajit HD2PhysBoneCompat/test/corpse-motion-test.lua
local here = arg and arg[0] and arg[0]:gsub("[^/\\]*$", "") or ""
package.path = here .. "../src/?.lua;" .. package.path
local runtimeModule = dofile(here .. "../src/physbone_runtime.lua")
local CM = runtimeModule.corpseMotion_

local failures, checks = 0, 0
local function check(condition, message, detail)
	checks = checks + 1
	if not condition then
		failures = failures + 1
		print(string.format("  %-4s %s%s", "FAIL", message, detail ~= nil and ("  [" .. tostring(detail) .. "]") or ""))
	else
		print(string.format("  %-4s %s", "PASS", message))
	end
end

-- 站立骨架（z 向上，人物面向 +y：右肩 = +x，up = +z，forward = right x up = (0*1-0*0, 0*0-1*1, 0)=(-?)... 由模块自己算）
local function standing()
	local P = {
		hips = { 0, 0, 1.0 }, chest = { 0, 0, 1.35 }, head = { 0, 0, 1.7 },
		l_shoulder = { -0.2, 0, 1.45 }, r_shoulder = { 0.2, 0, 1.45 },
		l_elbow = { -0.25, 0, 1.2 }, r_elbow = { 0.25, 0, 1.2 },
		l_hand = { -0.25, 0.2, 1.1 }, r_hand = { 0.25, 0.2, 1.1 },
		l_thigh = { -0.1, 0, 0.95 }, r_thigh = { 0.1, 0, 0.95 },
		l_knee = { -0.1, 0, 0.5 }, r_knee = { 0.1, 0, 0.5 },
		l_foot = { -0.1, 0, 0.05 }, r_foot = { 0.1, 0, 0.05 },
	}
	local a = {}
	for name, p in pairs(P) do a[name] = { p = { p[1], p[2], p[3] }, q = { 0, 0, 0, 1 } } end
	return a
end
local function dist(a, b)
	return math.sqrt((a[1] - b[1]) ^ 2 + (a[2] - b[2]) ^ 2 + (a[3] - b[3]) ^ 2)
end

print("== API 形状")
check(#CM.names == 15 and CM.names[1] == "hips" and CM.names[15] == "r_foot", "names 15 个且顺序同 IAS")
for _, f in ipairs({ "new", "advance", "frames", "deform", "owner" }) do
	check(type(CM[f]) == "function", "公开函数 " .. f)
end

print("== 常数 / 初始化")
local anchors = standing()
local s = CM.new(anchors)
local height = dist(anchors.head.p, anchors.hips.p) + .5 * (dist(anchors.l_foot.p, anchors.hips.p) + dist(anchors.r_foot.p, anchors.hips.p))
check(math.abs(s.floor - (0.05 - .025 * height)) < 1e-12, "floor = min(足 z) - .025*height", s.floor)
check(#s.links == 24, "20 条定长边 + 4 条肢弦界 = 24", #s.links)
check(math.abs(s.points.hips.radius - .065 * height) < 1e-12 and math.abs(s.points.head.radius - .047 * height) < 1e-12
	and math.abs(s.points.l_hand.radius - .019 * height) < 1e-12, "半径 .065/.047/.019 * height")
check(s.points.hips.weight == .25 and s.points.chest.weight == .25 and s.points.head.weight == .5 and s.points.l_foot.weight == 1,
	"质量 4/4/2/1 → weight 倒数")
-- 手算首步初速：速度 0 → speed = right x (p-hips)*1.6 + forward*.3；previous = p - speed/120
do
	local right = { 1, 0, 0 }
	local up = { 0, 0, 1 }
	local forward = { right[2] * up[3] - right[3] * up[2], right[3] * up[1] - right[1] * up[3], right[1] * up[2] - right[2] * up[1] } -- (0,-1,0)
	local p = anchors.head.p
	local d = { p[1] - 0, p[2] - 0, p[3] - 1.0 } -- (0,0,.7)
	local ang = { right[2] * d[3] - right[3] * d[2], right[3] * d[1] - right[1] * d[3], right[1] * d[2] - right[2] * d[1] } -- (0,-.7,0)
	local expect = { p[1] - (ang[1] * 1.6 + forward[1] * .3) / 120, p[2] - (ang[2] * 1.6 + forward[2] * .3) / 120,
		p[3] - (ang[3] * 1.6 + forward[3] * .3) / 120 }
	local got = s.points.head.previous
	check(dist(got, expect) < 1e-12, "head 初速 = right×(p-hips)*1.6 + forward*.3（手算）", got[2])
end
-- 核心速度上限 4 m/s
do
	local s2 = CM.new(standing(), { 40, 0, 0 })
	local s3 = CM.new(standing(), { 4, 0, 0 })
	check(dist(s2.points.hips.previous, s3.points.hips.previous) < 1e-12, "核心速度夹到 4 m/s")
end
-- 手算第一步：hips 在无约束力的纯 Verlet 位置 x+(x-b)*.992 - g*dt²
do
	local s4 = CM.new(standing())
	local h = s4.points.hips
	local p0 = { h.p[1], h.p[2], h.p[3] }
	local b0 = { h.previous[1], h.previous[2], h.previous[3] }
	local pred = { p0[1] + (p0[1] - b0[1]) * .992, p0[2] + (p0[2] - b0[2]) * .992,
		p0[3] + (p0[3] - b0[3]) * .992 - 9.82 / 14400 }
	CM.advance(s4, 1 / 120)
	check(s4.steps == 1, "advance(1/120) 走 1 步")
	-- 约束修正会移动它，但偏移应远小于一步位移尺度（<1mm 量级的几何修正之上）
	check(dist(h.p, pred) < 0.01, "第 1 步 hips 接近纯 Verlet 预测", dist(h.p, pred))
end

print("== advance 追赶上限 / 完成标志")
do
	local s5 = CM.new(standing())
	local done = CM.advance(s5, 1e6)
	check(s5.steps == 32 and done == false, "巨大 elapsed 单次只追 32 步", s5.steps)
	for _ = 1, 14 do done = CM.advance(s5, 1e6) end
	check(s5.steps == 480 and done == true, "15 次共 480 步，done = true", s5.steps)
	CM.advance(s5, 1e6)
	check(s5.steps == 480, "满 480 步后不再推进")
	local s6 = CM.new(standing())
	CM.advance(s6, 0.5) -- floor(60)
	check(s6.steps == 32, "elapsed=0.5 → wanted 60，仍被 32 步封顶")
	CM.advance(s6, 0.5)
	check(s6.steps == 60, "再推进补到 wanted=60", s6.steps)
	local ok = pcall(CM.advance, s6, -1)
	check(not ok, "负 elapsed 断言失败")
end

print("== 480 步后倒地 / 约束 / 确定性")
local function run()
	local st = CM.new(standing(), { 0.5, 0.2, 0 })
	local steps = 0
	local done
	repeat
		done = CM.advance(st, 1e6); steps = steps + 1
	until done or steps > 100
	return st
end
local a1, a2 = run(), run()
check(a1.steps == 480, "走满 480 步", a1.steps)
local lowest, maxErr = 1e9, 0
for _, name in ipairs(CM.names) do
	local pt = a1.points[name]
	lowest = math.min(lowest, pt.p[3] - pt.radius - a1.floor)
end
check(lowest > -1e-6, "没有节点低于 floor（含半径）", lowest)
check(a1.points.hips.p[3] < 0.25, "hips 躺到地面附近", a1.points.hips.p[3])
check(a1.points.head.p[3] < 0.3 and a1.points.l_foot.p[3] < 0.3, "head / 脚都贴近地面")
for _, link in ipairs(a1.links) do
	local d = dist(a1.points[link.a].p, a1.points[link.b].p)
	local err = math.max(link.min - d, d - link.max, 0)
	if err > maxErr then maxErr = err end
end
-- IAS 每步末尾地面接触在约束之后，故允许小残差；这里用 2% 肢长作容差
check(maxErr < 0.02, "边长 / 弦界误差在 IAS 约束容差内", maxErr)
local identical = true
for _, name in ipairs(CM.names) do
	for i = 1, 3 do
		if a1.points[name].p[i] ~= a2.points[name].p[i] then identical = false end
	end
end
check(identical, "两次运行逐位一致（确定性）")

print("== frames / deform / owner")
local fr = CM.frames(a1)
local n = 0
for _ in pairs(fr) do n = n + 1 end
check(n == 15, "frames 覆盖 15 个节点", n)
local q = fr.hips.q
local norm = math.sqrt(q[1] ^ 2 + q[2] ^ 2 + q[3] ^ 2 + q[4] ^ 2)
check(math.abs(norm - 1) < 1e-9, "hips 帧四元数单位化", norm)
check(fr.chest.q == fr.hips.q and fr.l_hand.q == fr.l_elbow.q and fr.r_foot.q == fr.r_knee.q, "chest=hips、末端继承父帧")
-- 初始姿态下 frames 应为恒等：未推进的 session
do
	local s7 = CM.new(standing())
	local f0 = CM.frames(s7)
	local qq = f0.hips.q
	check(math.abs(math.abs(qq[4]) - 1) < 1e-9, "未推进时 hips 帧为恒等旋转", qq[4])
	local pose = { q = { 0, 0, 0, 1 }, p = { 0.1, 0, 1.0 } }
	local d = CM.deform(f0, "hips", pose)
	check(dist(d.p, pose.p) < 1e-9, "恒等帧下 deform 不改位置")
	local bad = pcall(CM.deform, f0, "nope", pose)
	check(not bad, "未知 owner 断言失败")
end
check(CM.owner("neck") == "head" and CM.owner("spine2") == "chest" and CM.owner("l_clavicle") == "chest"
	and CM.owner("r_shoulder_twist") == "r_shoulder" and CM.owner("l_hand_twist") == "l_elbow"
	and CM.owner("l_finger_index1") == "l_hand" and CM.owner("r_toe") == "r_foot" and CM.owner("l_ball") == "l_foot"
	and CM.owner("boss") == "hips" and CM.owner("root") == "hips" and CM.owner("l_knee") == "l_knee"
	and CM.owner("xyz") == nil, "owner 映射同 IAS")

print(string.format("\n%d checks, %d failures", checks, failures))
os.exit(failures == 0 and 0 or 1)
