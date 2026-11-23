"""Compare the E6 and E7 Lua paths on the E6 eight-piece fixture at 100 fps."""
from collections import Counter
import json
import math
from pathlib import Path
import runpy
import statistics
import sys

from lupa.luajit21 import LuaRuntime
import numpy as np
from scipy.spatial.transform import Rotation, Slerp

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tmp/ias-to-patch/e6"
sys.dont_write_bytecode = True
E6 = runpy.run_path(str(FIXTURE / "replay.py"))
DATA = E6["DATA"]
Unit, hash32, matrix_elements = (E6[key] for key in ("Unit", "hash32", "matrix_elements"))
qmul, qrot = (E6["old"][key] for key in ("qmul", "qrot"))


class LinkedUnit(Unit):
    def __init__(self, *args):
        super().__init__(*args)
        self.links = {}

    def pose(self, index):
        if index not in self.links:
            return super().pose(index)
        if index not in self.world:
            avatar, node = self.links[index]
            parent_p, parent_q = avatar.pose(node)
            p, q = self.local[index]
            self.world[index] = tuple(a + b for a, b in zip(parent_p, qrot(parent_q, p))), qmul(parent_q, q)
        return self.world[index]


def samples(clip):
    times = np.array(clip["times"])
    at = np.arange(math.floor(times[-1] * 100 + 1e-8) + 1) / 100
    positions = np.array([[p for p, _ in frame] for frame in clip["frames"]])
    rotations = np.array([[q for _, q in frame] for frame in clip["frames"]])
    for bone, name in enumerate(DATA["order"]):
        p = np.array([np.interp(at, times, positions[:, bone, axis]) for axis in range(3)]).T
        q = Slerp(times, Rotation.from_quat(rotations[:, bone]))(at).as_quat()
        yield name, list(zip(p, q))


def replay(frames, *, link, blend, physics):
    mod = FIXTURE / "mod"
    packed = E6["read_json"](mod / "joint-pose.json")
    rows = E6["read_json"](FIXTURE / "preview/patch-preview.json")
    records = [record for path in mod.rglob("physbone-rig.bin") for record in E6["old"]["rig_records"](path)]
    names = {**E6["NAME"], **{hash32(name): name for row in records for name in row[:2]},
             **{hash32(row["bone"]): row["bone"] for row in packed["independentBones"]}}
    matrices = {row["hash"]: np.array(row["values"]).reshape(4, 4) for row in rows["bones"]}
    parents = {row["hash"]: rows["bones"][row["parent"]]["hash"] for row in rows["bones"]}
    armor_rows = [next(row for row in rows["units"] if row["fileId"] == key
                       and row["option"] in ("", "coat/on", "shoes/on")) for key in E6["PIECES"]]
    avatar = LinkedUnit("avatar", list(matrices), matrices, parents, names)
    armor = [LinkedUnit(f'#ID[{row["fileId"]}]', *E6["transforms"](mod / row["patch"])[row["fileId"]], names)
             for row in armor_rows]
    units = [avatar, *armor]
    lua = LuaRuntime(unpack_returned_tuples=True)
    table = lua.table_from
    compat, runtime = [
        lua.execute((ROOT / f"HD2PhysBoneCompat/src/physbone_{name}.lua").read_text(encoding="utf-8"))
        for name in ("compat", "runtime")
    ]
    lua.globals().io = None  # Keep local tuning files out of the replay.
    world = lua.table()
    handles = [lua.table(index=i) for i in range(len(units))]
    count = Counter()
    wrap = lua.eval("function(f) return function(...) return f(...) end end")
    now = 0

    def api(**entries):
        def counted(name, call):
            def invoke(*args):
                count[name] += 1
                return call(*args)
            return wrap(invoke)
        return lua.table(**{name: counted(name, call) for name, call in entries.items()})

    def unit_api(**entries):
        def bind(call):
            return lambda handle, *args: call(units[handle.index], *args)
        return api(**{name: bind(call) for name, call in entries.items()})

    def elements(v, size):
        return tuple(v[i] for i in range(1, size + 1))

    def pose(p, q):
        return table(matrix_elements(q) + list(p))

    def write(unit, bone, v, position):
        assert unit is not avatar and bone > 0
        p, q = unit.local[bone]
        unit.local[bone] = (elements(v, 3), q) if position else (p, elements(v, 4))

    def attach(_, handle, bone, owner, source):
        unit = units[handle.index]
        assert units[owner.index] is avatar and unit is not avatar
        unit.links[bone] = (avatar, source)
        unit.world.clear()

    def unlink(_, handle):
        unit = units[handle.index]
        unit.links.clear()
        unit.world.clear()

    env = lua.table(
        Unit=unit_api(alive=lambda _: True, debug_name=lambda u: u.tag,
                      num_scene_graph_items=lambda u: len(u.names) - 1,
                      has_animation_state_machine=lambda u: u is avatar,
                      animation_get_state=lambda _: (0,) * 31,
                      has_node=lambda u, n: n in u.ids, node=lambda u, n: u.ids[n],
                      world_pose=lambda u, i: pose(*u.pose(i)), local_pose=lambda u, i: pose(*u.local[i]),
                      world_position=lambda u, i: table(u.pose(i)[0]),
                      world_rotation=lambda u, i: table(u.pose(i)[1]),
                      local_position=lambda u, i: table(u.local[i][0]),
                      local_rotation=lambda u, i: table(u.local[i][1]),
                      local_scale=lambda *_: lua.table(1, 1, 1),
                      set_local_position=lambda u, i, v: write(u, i, v, True),
                      set_local_rotation=lambda u, i, v: write(u, i, v, False),
                      set_unit_visibility=lambda u, visible: setattr(u, "hidden", not visible),
                      scene_graph_parent=lambda u, i: None if i in u.links else u.parents[i],
                      scene_graph_link=lambda u, i, p: u.parents.__setitem__(i, p)),
        World=api(units=lambda _: table(handles), units_by_resource=lambda *_: table(handles[:1]),
                  update_unit=lambda _, h: units[h.index].world.clear(), link_unit=attach, unlink_unit=unlink),
        Application=api(worlds=lambda: lua.table(world), time_since_launch=lambda: now),
        Vector3=api(to_elements=lambda v: elements(v, 3)),
        Quaternion=api(is_valid=lambda q: all(math.isfinite(v) for v in elements(q, 4)),
                       to_elements=lambda q: elements(q, 4), from_elements=lambda *a: lua.table(*a),
                       axis_angle=lambda axis, angle: lua.table(
                           *(axis[i] * math.sin(angle / 2) for i in range(1, 4)), math.cos(angle / 2))),
        Matrix4x4=api(to_elements=lambda m: elements(m, 12)),
    )
    lua.eval("function(t, f) setmetatable(t, {__call = f}) end")(
        env.Vector3, api(vector=lambda _, *coords: lua.table(*coords)).vector)
    opts = lua.table(compat=compat, armPose=True, carriers=lua.table(), log=False,
                     independentLink=int(link), poseBlend=int(blend), iasPhysicsGroups=15)
    opts.independentBones = table([lua.table(r["bone"], r["source"]) for r in packed["independentBones"]])
    opts.independentRest = table([lua.table(r["parent"], table(r["avatar"]), table(r["model"]), table(r["position"]))
                                  for r in packed["independentRest"]])
    active = {unit.tag for unit in armor}
    opts.independentPieces = table({tag: table(packed["independentPieces"][tag]) for tag in active})
    opts.independentPieceSlots = table({tag: packed["independentPieceRoutes"][tag]["slot"] for tag in active})
    opts.independentTranslations = table({
        tag: table({name: lua.table(table(v["avatar"]), table(v["model"])) for name, v in values.items()})
        for tag, values in packed["independentTranslations"].items() if tag in active
    })
    state = runtime.install(env, opts)
    state.avatar, state.world, state.cache.world = handles[0], world, world
    state.liveUnits, state.liveUnitsWorld = table(handles), world
    state.avatars = lua.table(lua.table(unit=handles[0], cameraDistance=0))
    log = []
    state.logger = wrap(lambda fmt, *args: log.append(fmt % args if args else fmt))
    sessions = []
    if physics:
        for i, row in enumerate(armor_rows, 1):
            path = mod / row["option"] / "physics" / Path(row["patch"]).stem.split(".")[0] / "physbone-rig.bin"
            if path.exists():
                rig = compat.parse_rig(path.read_bytes())
                session = compat.attach(env, handles[i], rig, lua.table(world=world, fastWrite=True, logger=state.logger))
                if len(session.sessions):
                    sessions.append((i, session))

    results = []
    pending = 0
    joints = [(unit, unit.ids[name], name) for unit in armor for name in packed["independentPieces"][unit.tag]]
    for index, frame in enumerate(zip(*(values for _, values in frames))):
        now = index / 100
        state.frame, state.dt, state.lastTime = index + 1, 0.01, now
        state.frameAnimation = None
        compat.begin_frame(index + 1)
        for unit in units:
            unit.world.clear()
            for (name, _), transform in zip(frames, frame):
                if name in unit.ids:
                    unit.local[unit.ids[name]] = transform
        count.clear()
        runtime.apply_arm_pose_frame_(state)
        state_pose = state.independent
        assert len(state_pose.pieces) == 8, log
        assert sum(len(unit.links) for unit in armor) == (len(joints) if link else 0), log
        due = bool(state_pose.correction.due)
        pending += 0.01
        if physics:
            for i, session in sessions:
                session.engineCalls = 0
                schedule = state_pose.correction if state_pose.linkedUnits[handles[i]] else None
                runtime.independent_physics_(state, session, schedule, compat.update if due or not link else None, pending)
            if due or not link:
                pending = 0
        results.append((due, count.total(), np.array([unit.pose(node)[0] for unit, node, _ in joints])))

    runtime.uninstall()
    assert all(not unit.links for unit in armor), log
    return results, [(u.tag, name) for u, _, name in joints], [len(sessions), sum(len(s.sessions) for _, s in sessions)]


def main():
    totals = {key: {True: [], False: []} for key in ("e6", "e6_physics", "e7_off", "e7_on", "e7_physics")}
    errors = {key: {True: [], False: []} for key in ("e7_off", "e7_on")}
    for clip, data in DATA["clips"].items():
        frames = list(samples(data))
        e6, _, _ = replay(frames, link=False, blend=False, physics=False)
        e6_physics, _, _ = replay(frames, link=False, blend=False, physics=True)
        for key, blend, physics in (("e7_off", False, False), ("e7_on", True, False), ("e7_physics", True, True)):
            e7, names, chains = replay(frames, link=True, blend=blend, physics=physics)
            for frame, ((due, calls, actual), (_, baseline, expected)) in enumerate(zip(e7, e6)):
                if frame == 0:  # Cold discovery and link setup, reported separately below.
                    print(f"{clip} {key} cold calls={calls} / E6 off={baseline} all={e6_physics[0][1]}",
                          file=sys.stderr)
                    continue
                totals[key][due].append(calls)
                if key == "e7_on":
                    totals["e6"][due].append(baseline)
                    totals["e6_physics"][due].append(e6_physics[frame][1])
                if key in errors:
                    distance = np.linalg.norm(actual - expected, axis=1) * 1000
                    worst = int(distance.argmax())
                    errors[key][due].append((float(distance[worst]), clip, frame, names[worst],
                                             float(distance.sum()), len(distance)))
    summary = {"physics_sessions_chains": chains, "pose_errors_mm": {}, "calls": {}}
    for key, by_frame in errors.items():
        summary["pose_errors_mm"][key] = {}
        for due, records in by_frame.items():
            summary["pose_errors_mm"][key]["correction" if due else "following"] = {
                "worst": max(records)[:4], "mean": sum(r[4] for r in records) / sum(r[5] for r in records),
                "joint_samples": sum(r[5] for r in records),
            }
    for key, by_frame in totals.items():
        summary["calls"][key] = {"per_second": statistics.mean(by_frame[True] + by_frame[False]) * 100}
        for due, values in by_frame.items():
            summary["calls"][key]["correction" if due else "following"] = {
                "frames": len(values), "min": min(values), "mean": statistics.mean(values),
                "p50": statistics.median(values), "max": max(values),
            }
    assert summary["pose_errors_mm"]["e7_off"]["correction"]["worst"][0] < 0.1
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
