-- E12d：IAS 式每帧枚举（Roster）回归。按 IAS 的行为写：
--   · 死亡后新 avatar 与它的件在新 avatar 被身份判定后的下一帧就被绑定；
--   · 旧件留在尸体上，世界里还有 70 个同 ID 的干扰件，都不会被误选；
--   · 本人判定用 Network/GameSession（IAS physics_identity），死亡 = 网络 health <= 0。
-- 跑法：luajit HD2PhysBoneCompat/test/ias-roster-test.lua

local here = arg and arg[0] and arg[0]:gsub("[^/\\]*$", "") or ""
package.path = here .. "../src/?.lua;" .. package.path
local runtimeModule = dofile(here .. "../src/physbone_runtime.lua")
local Roster = runtimeModule.roster_

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

local AVATAR = "content/fac_helldivers/cha_avatar/avatar_helldiver"
local HASHES = {}
for i = 1, 8 do HASHES[i] = string.format("a%015x", i) end
local manifest = {}
for _, hash in ipairs(HASHES) do manifest["#ID[" .. hash .. "]"] = { "ias_hips" } end

-- ---------------------------------------------------------------- 假引擎
local world = { name = "world" }
local units = {}          -- 世界里所有 unit
local object = { id = 5000, x = 0, y = 0, z = 0, health = 100 }
local calls = { units = 0, byResource = 0 }

local function newUnit(name, x, kind)
	local unit = { name = name, x = x, y = 0, z = 0, kind = kind, alive = true }
	units[#units + 1] = unit
	return unit
end
local function destroy(unit)
	unit.alive = false
	for index = #units, 1, -1 do if units[index] == unit then table.remove(units, index) end end
end

local S = {
	Application = { worlds = function() return { world } end },
	IdString64 = { from_hex = function(hash) return { hex = hash } end },
	World = {
		units = function() calls.units = calls.units + 1; return units end,
		num_units = function() return #units end,
		units_by_resource = function(_, key)
			calls.byResource = calls.byResource + 1
			local out = {}
			if type(key) == "table" then
				for _, unit in ipairs(units) do if unit.name:find(key.hex, 1, true) then out[#out + 1] = unit end end
			elseif key == AVATAR then
				for _, unit in ipairs(units) do if unit.kind == "avatar" then out[#out + 1] = unit end end
			end
			return out
		end,
	},
	Unit = {
		alive = function(unit) return unit.alive == true end,
		debug_name = function(unit) return unit.name end,
		num_scene_graph_items = function() return 20 end,
		world_position = function(unit) return { unit.x, unit.y, unit.z } end,
		has_animation_state_machine = function(unit) return unit.kind == "avatar" end,
	},
	Vector3 = { to_elements = function(v) return v[1], v[2], v[3] end },
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
runtimeModule.set_engine_(S)

local runtime = { frame = 0, options = { independentPieces = manifest }, logger = function() end }
local function step()
	runtime.frame = runtime.frame + 1
	return Roster.update(runtime)
end
Roster.reset()
Roster.pollInterval, Roster.negativeSeconds = 0, 0   -- 测试里帧只隔几毫秒；IAS 默认 4 Hz / 0.25 s

local function ownedBy(avatar)
	local roster = runtime.roster.byWorld[world]
	local count = 0
	for _, row in ipairs(roster.armour) do if roster.owner[row.unit] == avatar then count = count + 1 end end
	return count
end

io.stdout:write("\n[E12d IAS 生命周期] 死亡 -> 新 avatar -> 同 ID 新件；旧件留在尸体上；世界里 70 个同类干扰件\n")
local avatarA = newUnit("avatarA", 0, "avatar")
local ownA = {}
for i, hash in ipairs(HASHES) do ownA[i] = newUnit("#ID[" .. hash .. "]", 0) end
-- 70 个同 ID 干扰件（人台/队友的备份）远在 50 m 外
local decoys = {}
for i = 1, 70 do decoys[i] = newUnit("#ID[" .. HASHES[(i - 1) % 8 + 1] .. "]", 50 + i * 0.01) end
-- 几千个无关 unit
for _ = 1, 3000 do newUnit("#ID[ffffffffffffffff]", 100) end

step()
local roster = runtime.roster
check(roster.localAvatar == avatarA, "网络身份：本人 = 与网络对象位置重合、owner=true、有状态机的 avatar")
check(ownedBy(avatarA) == 8, "8 件件属于本人，70 个 50 m 外的同 ID 干扰件不属于任何人", ownedBy(avatarA))

-- 死亡：avatar 的网络对象 health<=0（IAS 行 4985），随后 avatar unit 从枚举里消失（被销毁），旧件留在原地（尸体）
object.health = 0
step()
check(Roster.dead(avatarA) or Roster.role(avatarA) == 3, "health<=0 判定为死亡（角色 3），不再是本人", Roster.role(avatarA))
destroy(avatarA)
step()
check(runtime.roster.localAvatar == nil, "死亡期间没有本人（没有 IAS 以外的回退）")

-- 复活：新 avatar 在 20 m 外出现，同 ID 的 8 件新件跟着出现；网络对象换成新的、重新有血量
local avatarB = newUnit("avatarB", 20, "avatar")
local ownB = {}
for i, hash in ipairs(HASHES) do ownB[i] = newUnit("#ID[" .. hash .. "]", 20) end
object.x, object.health = 20, 100
local frames = 0
repeat
	step(); frames = frames + 1
until (runtime.roster.localAvatar == avatarB and ownedBy(avatarB) == 8) or frames >= 60
check(runtime.roster.localAvatar == avatarB, "新 avatar 被识别为本人", frames)
check(frames <= 2, "新 avatar 的 8 件在 2 帧内全部被绑定（IAS 每帧枚举，无额度/退避）", frames)
local roster2 = runtime.roster.byWorld[world]
local wrong = 0
for i = 1, 8 do if roster2.owner[ownA[i]] ~= nil then wrong = wrong + 1 end end
for i = 1, 70 do if roster2.owner[decoys[i]] ~= nil then wrong = wrong + 1 end end
check(wrong == 0, "尸体上的旧 8 件与 70 个干扰件都不被误选", wrong)
check(ownedBy(avatarB) == 8, "归属给新 avatar 的恰好是它自己的 8 件", ownedBy(avatarB))

-- IAS 的发现节奏：稳定后不再每帧全量 World.units（兜底只在 60 帧 / 战斗世界 2 s / 命中数下降时）
local before = calls.units
for _ = 1, 30 do step() end
check(calls.units - before <= 1, "稳定后 30 帧内全量 World.units 至多 1 次（IAS 兜底节奏）", calls.units - before)

io.stdout:write(string.format("\n检查 %d 项，失败 %d\n", checks, failures))
os.exit(failures == 0 and 0 or 1)
