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

-- corpseFreeze = false 的理由（逐项保留，不是为了藏成本）：本文件逐条断言"身份轮询自己"的存在性查询次数（R:4973-4983、R:4794-4846 的
-- 捷径），而尸体定格开着时 IAS 每帧另外为化身号做 shared_life（R:5097-5119，game_object_exists 等，R:6917-6919），
-- 会让这些计数多出一项与身份轮询无关的查询。开着时的整体成本由 perf-combat-test.lua（L/C/A/S 项）与
-- corpse-integration-test.lua（逐阶段）按 IAS 逐项对账。
local runtime = { frame = 0, options = { independentPieces = manifest, corpseFreeze = false }, logger = function() end }
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

-- ===================================================================== E12e-B2 身份（IAS 1.6.4 对照）
-- 新场景：清空世界，用表驱动的多对象 / 多 owner 假引擎。
io.stdout:write("\n[E12e 身份] >32 提前返回 / 实例槽 / 远端 peer 枚举 / 稳定互斥回退\n")
for index = #units, 1, -1 do units[index] = nil end
local objs = {}          -- id -> { x, y, z, health }
local ownership = { me = {} }
local peersList, otherPeersList = nil, nil
local sessionCalls = 0
S.Network.game_session = function() sessionCalls = sessionCalls + 1; return 1 end
local GSm = S.GameSession
GSm.objects_owned_by = function(_, peer)
	local out = {}
	for _, id in ipairs(ownership[peer] or {}) do out[#out + 1] = id end
	return out
end
GSm.game_object_exists = function(_, id) return objs[id] ~= nil end
GSm.game_object_field = function(_, id, field)
	local o = objs[id]
	if field == "health" then return o.health end
	return { o.x, o.y, o.z }
end
GSm.peers = function() return peersList end
GSm.other_peers = function() return otherPeersList end
local function hardReset()
	Roster.reset(); runtime.roster = nil
	for index = #units, 1, -1 do units[index] = nil end
end

-- (1) #avatars > 32：直接返回，什么都不碰（IAS 行 4841）
hardReset()
local crowd = {}
for i = 1, 33 do crowd[i] = newUnit("crowd" .. i, i * 10, "avatar") end
local before32 = sessionCalls
step()
check(sessionCalls == before32, "33 个 avatar：身份判定提前返回，不读网络会话", sessionCalls - before32)
for i = 1, 33 do destroy(crowd[i]) end

-- (2) 实例槽变化 -> 新建 state，复活不再继承 dead（IAS 行 4989-4990；没有 dead 清零）
hardReset()
objs = { [5000] = { x = 0, y = 0, z = 0, health = 100 } }
ownership = { me = { 5000 } }
local me = newUnit("me", 0, "avatar")
step()
check(Roster.role(me) == 1, "本人角色 1", Roster.role(me))
objs[5000].health = 0
step()
check(Roster.dead(me) and Roster.role(me) == 3, "health<=0 -> dead，角色 3")
step()
check(Roster.dead(me), "同一实例下 dead 不会被清回（IAS 没有 dead->alive 重置）")
Roster.slots[me] = 987654   -- 模拟实例槽被换发（同一个 unit 值、新的渲染实例）
objs[5000] = { x = 0, y = 0, z = 0, health = 100 }
step()
check(not Roster.dead(me) and Roster.role(me) == 1, "实例槽变了：state 重建，复活后不再是 dead", Roster.role(me))
check(Roster.states[me].instance == 987654, "state 记录了实例槽")

-- (3) 远端 peer 只能经 peers 枚举找到（id 远在低位扫描和近邻窗口之外）
hardReset()
objs = { [5000] = { x = 0, y = 0, z = 0, health = 100 }, [7000000] = { x = 10, y = 0, z = 0, health = 100 } }
ownership = { me = { 5000 }, peer2 = { 7000000 } }
peersList, otherPeersList = nil, nil
local a = newUnit("a", 0, "avatar")
local b = newUnit("b", 10, "avatar")
for _ = 1, 4 do step() end
check(Roster.role(a) == 1, "本人仍是角色 1", Roster.role(a))
check(Roster.role(b) == 0, "没有 peers 名单时，远端 id 7000000 找不到，队友未识别", Roster.role(b))
peersList = { "me", "peer2" }
for _ = 1, 4 do step() end
check(Roster.role(b) == 2, "GS.peers 里有 peer2：枚举其对象，队友被识别为角色 2", Roster.role(b))
check(Roster.states[b].network == 7000000 and Roster.states[b].owner == false, "队友绑定到 peer2 的对象，owner=false")
peersList, otherPeersList = nil, { "peer2", "me" }
Roster.reset()
for _ = 1, 4 do step() end
check(Roster.role(b) == 2, "只有 other_peers 提供名单时同样有效（两个来源合并，去掉自己）", Roster.role(b))

-- (4) 稳定互斥回退：连续 4 次轮询才认定，中途断掉就清零
hardReset()
objs = { [5000] = { x = 0, y = 0, z = 0, health = 100 }, [7000001] = { x = 30.3, y = 0, z = 0, health = 100 } }
ownership = { me = { 5000 }, peer2 = { 7000001 } }   -- 7000001 差 0.3 m：达不到 0.05 m 的直接绑定
peersList, otherPeersList = { "me", "peer2" }, nil
local host = newUnit("host", 0, "avatar")
local mate = newUnit("mate", 30, "avatar")
for _ = 1, 3 do step() end
check(Roster.states[mate].network == nil and Roster.states[mate].fallbackHits == 3, "3 次轮询：还没认定", Roster.states[mate].fallbackHits)
step()
check(Roster.states[mate].network == 7000001 and Roster.states[mate].matchMethod == "stable-mutual"
	and Roster.states[mate].owner == false, "第 4 次轮询：认定 stable-mutual")
local mate2 = newUnit("mate2", 60, "avatar")
ownership.peer2 = { 7000001, 7000002 }
objs[7000002] = { x = 60.3, y = 0, z = 0, health = 100 }
for _ = 1, 3 do step() end
check(Roster.states[mate2].fallbackHits == 3 and Roster.states[mate2].network == nil, "mate2 连续 3 次命中", Roster.states[mate2].fallbackHits)
objs[7000002].x = 60.8
step()
check(Roster.states[mate2].fallbackHits == 0 and Roster.states[mate2].fallbackId == nil, "目标超出 0.5 m：计数清零")
objs[7000002].x = 60.3
for _ = 1, 3 do step() end
check(Roster.states[mate2].network == nil and Roster.states[mate2].fallbackHits == 3, "清零后重新数：3 次仍不认定", Roster.states[mate2].fallbackHits)
step()
check(Roster.states[mate2].network == 7000002 and Roster.states[mate2].matchMethod == "stable-mutual", "清零后连续第 4 次才认定")

-- ===================================================================== E12e-B2b solo / squad 发现捷径（IAS 行 4756–4905）
io.stdout:write("\n[E12e 捷径] solo / squad 验证后跳过低位扫描与远端枚举；条件破坏后恢复\n")
local cnt = { exists = 0, owned = 0 }
local realExists, realOwned = GSm.game_object_exists, GSm.objects_owned_by
GSm.game_object_exists = function(...) cnt.exists = cnt.exists + 1; return realExists(...) end
GSm.objects_owned_by = function(...) cnt.owned = cnt.owned + 1; return realOwned(...) end
GSm.game_object_owner = function(_, id)
	for peerName, ids in pairs(ownership) do for _, v in ipairs(ids) do if v == id then return peerName end end end
end
local function poll()
	cnt.exists, cnt.owned = 0, 0
	step()
	return cnt.exists, cnt.owned
end

-- solo：poll1 未验证（全量扫描 1+64+刷新1），poll2 起验证通过（捷径 1 + 刷新 1）
hardReset()
objs = { [5000] = { x = 0, y = 0, z = 0, health = 100 } }
ownership = { me = { 5000 } }
peersList, otherPeersList = { "me" }, { "me" }
local solo = newUnit("solo", 0, "avatar")
local e1, o1 = poll()
check(e1 == 66 and o1 == 1, "solo 验证前：存在性查询 66（1 个已有 + 64 游标 + 1 刷新），自己的 owned 列表 1 次", e1)
check(Roster.role(solo) == 1, "solo 已识别为本人")
local cursorAfter1 = Roster.cursor
local e2 = poll()
check(e2 == 2, "solo 验证后：存在性查询 2（捷径 1 + 已知对象刷新 1），不再扫低位", e2)
check(Roster.cursor == cursorAfter1, "solo 验证后：低位游标不再前进", Roster.cursor)
poll()
check(Roster.role(solo) == 1, "solo 捷径下角色保持 1")
peersList = { "me", "peer9" }
local e3 = poll()
check(e3 > 64, "solo 条件破坏（peers 里出现别人）：恢复低位扫描", e3)
peersList = { "me" }
local e4 = poll()
check(e4 == 2, "名单恢复干净：捷径重新生效", e4)
local extra = newUnit("extra", 40, "avatar")
local e5 = poll()
check(e5 > 64, "第二个 avatar 出现：不再是 solo，恢复低位扫描", e5)

-- squad：poll1 未验证（全量扫描 + 远端枚举），poll2 起验证通过（每人 1 次捷径 + 1 次刷新，不枚举远端）
hardReset()
objs = { [5000] = { x = 0, y = 0, z = 0, health = 100 }, [7000001] = { x = 10, y = 0, z = 0, health = 100 } }
ownership = { me = { 5000 }, peer2 = { 7000001 } }
peersList, otherPeersList = { "me", "peer2" }, { "me", "peer2" }
local sa = newUnit("sa", 0, "avatar")
local sb = newUnit("sb", 10, "avatar")
local se1, so1 = poll()
check(se1 > 64 and so1 == 2, "squad 验证前：低位扫描 + 远端枚举（owned 列表 2 次：自己 + peer2）", se1 .. "/" .. so1)
check(Roster.role(sa) == 1 and Roster.role(sb) == 2, "squad 两人角色 1 / 2", Roster.role(sa) .. "/" .. Roster.role(sb))
local sc1 = Roster.cursor
local se2, so2 = poll()
check(se2 == 4 and so2 == 1, "squad 验证后：存在性查询 4（2 人 x（捷径 1 + 刷新 1）），owned 列表仅自己 1 次", se2 .. "/" .. so2)
check(Roster.cursor == sc1, "squad 验证后：低位游标不再前进")
check(Roster.role(sa) == 1 and Roster.role(sb) == 2, "squad 捷径下角色保持")
otherPeersList = { "me" }
local se3 = poll()
check(se3 > 64, "squad 条件破坏（两份名单不一致）：恢复扫描", se3)
otherPeersList = { "me", "peer2" }
local se4, so4 = poll()
check(se4 == 4 and so4 == 1, "名单一致：捷径重新生效", se4 .. "/" .. so4)
local sc = newUnit("sc", 50, "avatar")
local se5, so5 = poll()
check(se5 > 64 and so5 == 2, "新 avatar 出现（尚无网络对象）：恢复扫描与远端枚举", se5 .. "/" .. so5)
destroy(sc)
poll()
objs[7000001].x = 12
local se6 = poll()
check(se6 > 64, "队友对象与 avatar 位置偏差 > 1 m：捷径失效，恢复扫描", se6)

-- E12f-C3：本人 avatar 只认本帧枚举（枚举失败 / 没跑时 runtime.roster 可能是上一帧的，句柄不跨帧）
do
	local mine = { name = "me" }
	local rt = { frame = 10, roster = { frame = 10, live = { [mine] = true }, localAvatar = mine, localWorld = "w" } }
	local u, w = Roster.localAvatar(rt)
	check(u == mine and w == "w", "localAvatar：本帧枚举给出本人")
	rt.frame = 11
	check(Roster.localAvatar(rt) == nil, "localAvatar：枚举不是本帧的（旧帧名单）⇒ nil，不返回旧句柄")
	check(Roster.isLive(rt, mine) == false, "isLive：旧帧名单同样判不在")
end

io.stdout:write(string.format("\n检查 %d 项，失败 %d\n", checks, failures))
os.exit(failures == 0 and 0 or 1)
