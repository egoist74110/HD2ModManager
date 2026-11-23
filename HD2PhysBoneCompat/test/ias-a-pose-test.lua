local Compat = dofile(arg[1])
local failures = 0
local function check(label, ok)
	print((ok and "PASS " or "FAIL ") .. label)
	if not ok then failures = failures + 1 end
end
local function close(a, b)
	return math.abs(a-b) < 1e-6
end
local identity = { 0, 0, 0, 1 }
local function rotation(degrees)
	local a = degrees*math.pi/360
	return { math.sin(a), 0, 0, math.cos(a) }
end

local angles = Compat.clothing_driver_angles(rotation(35), rotation(10), rotation(5))
check("HD2POSE1: reference and neutral rotations are removed", close(angles[1], 20)
	and close(angles[2], 0) and close(angles[3], 0))
local zero = { flags = 0, secondaryAxis = 0, points = { { -180, 0, 0, 0 }, { 180, 0, 0, 0 } } }
local axis = { flags = 2, secondaryAxis = 2, points = { { 0, 0, 0, 0 }, { 60, 60, 0, 0 } } }
local rule = { basis = identity, axes = { axis, zero, zero }, influence = 0.5 }
for mode, expected in pairs({ [0] = 10, [1] = 20, [2] = 10, [3] = 30 }) do
	rule.mode = mode
	local q = Compat.clothing_rotation(rule, { 20, 0, 0 }, { 90, 0, -40 }, identity)
	check("HD2POSE1: interpolation, secondary axis, combination " .. mode .. " and influence",
		close(q[1], rotation(expected)[1]) and close(q[4], rotation(expected)[4]))
end
rule.mode, rule.influence, axis.flags = 0, 1, 1
local clamped = Compat.clothing_rotation(rule, { -80, 0, 0 }, nil, identity)
check("HD2POSE1: inversion and clamped end of curve", close(clamped[1], rotation(60)[1]))

local function pose(x, y, z, q)
	return { p = { x, y, z }, q = q or identity }
end
local footRotation = rotation(25)
local a, b, c = Compat.calibrate_leg(pose(0, 0, 0), pose(0, 0, 0), pose(0, 1, 0), pose(0, 1, 1, footRotation),
	pose(0, 0, 0), pose(0, 0, 0), pose(0, 1, 0.5), 1, { 1, 0, 0 })
check("Leg calibration changes the lateral ankle position and preserves its world rotation",
	close(c.p[1], 0) and close(c.p[2], 1) and close(c.p[3], 0.5) and c.q == footRotation)
local rebuiltKnee = Compat.independent_fk(a, identity, { 0, 1, 0 }).p
local rebuiltAnkle = Compat.independent_fk(b, identity, { 0, 0, 1 }).p
local match = true
for i = 1, 3 do match = match and close(rebuiltKnee[i], b.p[i]) and close(rebuiltAnkle[i], c.p[i]) end
check("Leg calibration preserves both segment lengths and rotates both bones toward the solution", match)
check("Leg calibration skips a collapsed chain", not Compat.calibrate_leg(pose(0, 0, 0),
	pose(0, 0, 0), pose(0, 0, 0), pose(0, 1, 1), pose(0, 0, 0), pose(0, 0, 0), pose(0, 1, 1), 1, { 1, 0, 0 }))
os.exit(failures == 0 and 0 or 1)
