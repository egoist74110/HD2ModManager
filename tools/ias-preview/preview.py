"""Render the meshes read back by hd2mod ias preview, with native Avatar clips."""
from argparse import ArgumentParser
from bisect import bisect_right
from collections import defaultdict
import json
from math import ceil, radians
from pathlib import Path
import struct
import subprocess
import sys
from types import ModuleType

import bpy
from mathutils import Matrix, Quaternion, Vector

SDK = Path(__file__).resolve().parents[2] / "tools/ref/HD2SDK-CommunityEdition"
TEXCONV = SDK / "deps/texconv.exe"
sdk_package = ModuleType("preview_sdk")
sdk_package.__path__ = [str(SDK)]
sys.modules[sdk_package.__name__] = sdk_package
# Blender must not create __pycache__ beside the SDK.
sys.dont_write_bytecode = True
from preview_sdk.stingray.animation import StingrayAnimation
from preview_sdk.stingray.material import StingrayMaterial
from preview_sdk.stingray.texture import StingrayTexture
from preview_sdk.utils.memoryStream import MemoryStream

AVATAR = "4d1c334d294dfa97"
MAT_PATCH = "9ba626afa44a3aa3.patch_2"
SLOT_NAR = 0x4c567810
SLOT_ILLUMINATE = 0x90b84a53
SLOT_ALBEDO_EMISSIVE = 0xe67ac0c7
SLOT_ALPHA_CUTOUT = 0xa72cb013


def matrix(values):
    return Matrix([values[i:i + 4] for i in range(0, 16, 4)]).transposed()


def read_native_rig(directory):
    blob = (directory / f"unit_0x{AVATAR}.toc").read_bytes()
    transform = struct.unpack_from("<I", blob, 0x34)[0]
    count = struct.unpack_from("<I", blob, transform)[0]
    matrices = transform + 16 + count * 64
    parents = matrices + count * 64
    hashes = parents + count * 4
    rows = [
        dict(values=struct.unpack_from("<16f", blob, matrices + 64 * i),
             parent=struct.unpack_from("<H", blob, parents + 4 * i + 2)[0],
             hash=struct.unpack_from("<I", blob, hashes + 4 * i)[0])
        for i in range(count)
    ]
    data = (directory / f"bones_0x{AVATAR}.toc").read_bytes()
    names_count, lod_count = struct.unpack_from("<II", data)
    keys = struct.unpack_from(f"<{names_count}I", data, 8 + 4 * lod_count)
    names = data[8 + 8 * lod_count + 4 * names_count:].rstrip(b"\0").decode().split("\0")
    if len(names) != names_count:
        raise ValueError("Avatar Bones string count differs from its hash count")
    return rows, dict(zip(keys, names)), keys


def clip_data(path):
    clip = StingrayAnimation()
    clip.load(MemoryStream(path.read_bytes()))
    return clip


def quaternion(values):
    x, y, z, w = values
    q = Quaternion((w, x, y, z))
    q.normalize()
    return q


def clip_tracks(clip):
    channels = defaultdict(list)
    for index, state in enumerate(clip.initial_bone_states):
        channels[index, "position"].append((0, Vector(state.position)))
        channels[index, "rotation"].append((0, quaternion(state.rotation)))
    for entry in clip.entries:
        if entry.type == 2 or (entry.type == 0 and entry.subtype == 4):
            channels[entry.bone, "position"].append((entry.time, Vector(entry.data2)))
        if entry.type == 3 or (entry.type == 0 and entry.subtype == 5):
            channels[entry.bone, "rotation"].append((entry.time, quaternion(entry.data2)))
    return {key: sorted(rows, key=lambda item: item[0]) for key, rows in channels.items()}


def interpolate(keys, time, rotation):
    index = bisect_right(keys, time, key=lambda key: key[0])
    if index == 0:
        return keys[0][1]
    if index == len(keys):
        return keys[-1][1]
    before, first = keys[index - 1]
    after, last = keys[index]
    ratio = (time - before) / (after - before)
    return first.slerp(last, ratio) if rotation else first.lerp(last, ratio)


def native_frames(rows, keys, clip, seconds):
    tracks = clip_tracks(clip)
    indexes = {hash_value: i for i, hash_value in enumerate(keys)}
    rest = [matrix(row["values"]) for row in rows]
    model_space = {}
    for i, row in enumerate(rows):
        parent = row["parent"]
        root = parent == i or parent >= len(rows)
        bind_local = rest[i] if root else rest[parent].inverted() @ rest[i]
        index = indexes.get(row["hash"])
        if index is None or index >= clip.bone_count:
            local = bind_local
        else:
            t = interpolate(tracks[index, "position"], seconds * 1000, False)
            q = interpolate(tracks[index, "rotation"], seconds * 1000, True)
            pose = Matrix.LocRotScale(t, q, None)
            local = bind_local @ pose if clip.is_additive_animation else pose
        model_space[i] = local if root else model_space[parent] @ local
    return model_space


def probe(root, rows, names, keys):
    by_name = {names.get(row["hash"]): i for i, row in enumerate(rows)}
    print("AVATAR", len(rows), "ANIMATION BONES", len(keys), "NAMES", list(names.values())[:12])
    features = ("root", "boss", "head", "l_hand", "r_hand", "l_foot", "r_foot", "weapon")
    for path in sorted((root / "native").glob("animation_*.toc")):
        clip = clip_data(path)
        times = [0, clip.animation_length * .45]
        print(path.stem, "length", round(clip.animation_length, 2), "tracks", len(clip.entries),
              "bones", clip.bone_count, "additive", clip.is_additive_animation)
        for t in times:
            frames = native_frames(rows, keys, clip, t)
            print({name: tuple(round(value, 2) for value in frames[by_name[name]].translation)
                   for name in features if name in by_name})


def build_rig(rows, names):
    data = bpy.data.armatures.new("Native Helldiver Avatar")
    rig = bpy.data.objects.new("DP-8 · Native Avatar", data)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bone_names = [names.get(row["hash"], f'{row["hash"]:08x}') for row in rows]
    for i, row in enumerate(rows):
        bone = data.edit_bones.new(bone_names[i])
        bone.head, bone.tail = (0, 0, 0), (0, .035, 0)
        bone.matrix = matrix(row["values"])
        bone.length = .035
        parent = row["parent"]
        if parent != i and parent < len(rows):
            bone.parent = data.edit_bones[bone_names[parent]]
    bpy.ops.object.mode_set(mode="OBJECT")
    rig.animation_data_create()
    for bone in rig.pose.bones:
        bone.rotation_mode = "QUATERNION"
    return rig, bone_names


def build_action(rig, bone_names, rows, keys, clip, label):
    action = bpy.data.actions.new(label)
    action.use_fake_user = True
    rig.animation_data.action = action
    tracks = clip_tracks(clip)
    indexes = {hash_value: i for i, hash_value in enumerate(keys)}
    for row, name in zip(rows, bone_names):
        index = indexes.get(row["hash"])
        if index is None or index >= clip.bone_count:
            continue
        bone = rig.pose.bones[name]
        rest = bone.bone.matrix_local
        local = bone.parent.bone.matrix_local.inverted() @ rest if bone.parent else rest
        inverse = local.inverted()
        position_keys = tracks[index, "position"]
        rotation_keys = tracks[index, "rotation"]
        times = sorted({t for t, _ in position_keys + rotation_keys})
        last = None
        for time in times:
            position = interpolate(position_keys, time, False)
            rotation = interpolate(rotation_keys, time, True)
            pose = Matrix.LocRotScale(position, rotation, None)
            local_pose = pose if clip.is_additive_animation else inverse @ pose
            location, orientation, _ = local_pose.decompose()
            if last is not None:
                orientation.make_compatible(last)
            last = orientation.copy()
            bone.location, bone.rotation_quaternion = location, orientation
            frame = time * .03
            bone.keyframe_insert(data_path="location", frame=frame, group=name)
            bone.keyframe_insert(data_path="rotation_quaternion", frame=frame, group=name)
    for curve in action.fcurves:
        for key in curve.keyframe_points:
            key.interpolation = "LINEAR"
    return action


PALETTE = {
    "Head": (.58, .48, .42, 1), "Torso": (.45, .58, .67, 1), "Hips": (.32, .42, .56, 1),
    "Torso_Armor": (.42, .57, .46, 1), "LeftArm": (.48, .61, .72, 1), "RightArm": (.48, .61, .72, 1),
    "LeftLeg": (.52, .49, .62, 1), "RightLeg": (.52, .49, .62, 1),
}


def extract_patch_materials(mod_dir, tex_dir):
    patch = mod_dir / MAT_PATCH
    if not patch.is_file():
        return {}, {}
    toc = patch.read_bytes()
    gpu = (patch.parent / (patch.name + ".gpu_resources")).read_bytes()
    tn, en = struct.unpack_from("<II", toc, 4)
    eoff = 72 + tn * 32
    materials = {}
    textures = {}
    tex_dir.mkdir(parents=True, exist_ok=True)
    for k in range(en):
        f = struct.unpack_from("<QQQQQQQIIIIII", toc, eoff + k * 80)
        fid, tid, toc_off, gpu_off, toc_sz, gpu_sz = f[0], f[1], f[2], f[4], f[7], f[9]
        tdata = toc[toc_off:toc_off + toc_sz]
        gdata = gpu[gpu_off:gpu_off + gpu_sz] if gpu_sz else b""
        if tid == 0xeac0b497876adedf:
            mat = StingrayMaterial()
            mat.Serialize(MemoryStream(tdata, IOMode="read"))
            materials[f"{fid:016x}"] = dict(zip(mat.TexUnks, (f"{t:016x}" for t in mat.TexIDs)))
        elif tid == 0xcd4238c6a0c69e32:
            tex_id = f"{fid:016x}"
            png = tex_dir / f"{tex_id}.png"
            if not png.is_file():
                tx = StingrayTexture()
                tx.Serialize(MemoryStream(tdata, IOMode="read"),
                             MemoryStream(gdata, IOMode="read"),
                             MemoryStream(b"", IOMode="read"))
                dds = tex_dir / f"{tex_id}.dds"
                dds.write_bytes(tx.ToDDS())
                subprocess.run([str(TEXCONV), "-nologo", "-ft", "png", "-m", "1", "-y", "-o", str(tex_dir), str(dds)],
                               check=True, capture_output=True)
                dds.unlink(missing_ok=True)
            textures[tex_id] = png
    return materials, textures


def load_image(path, non_color=False):
    key = f"{path.stem}_{'nc' if non_color else 'srgb'}"
    if key in bpy.data.images:
        return bpy.data.images[key]
    img = bpy.data.images.load(str(path), check_existing=True)
    if non_color:
        img = img.copy()
        img.colorspace_settings.name = "Non-Color"
    img.name = key
    return img


def get_blender_material(mat_id, mat_defs, tex_paths, fallback_color):
    name = f"Mat_{mat_id or 'default'}"
    if name in bpy.data.materials:
        return bpy.data.materials[name]
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    mat.use_backface_culling = True
    nodes = mat.node_tree.nodes
    links = mat.node_tree.links
    bsdf = nodes.get("Principled BSDF")
    slots = mat_defs.get(mat_id, {}) if mat_id else {}
    albedo_id = slots.get(SLOT_ALBEDO_EMISSIVE)
    if albedo_id and albedo_id in tex_paths:
        tex_node = nodes.new("ShaderNodeTexImage")
        tex_node.image = load_image(tex_paths[albedo_id], non_color=False)
        links.new(tex_node.outputs["Color"], bsdf.inputs["Base Color"])
        links.new(tex_node.outputs["Color"], bsdf.inputs["Emission Color"])
        links.new(tex_node.outputs["Alpha"], bsdf.inputs["Emission Strength"])
    else:
        bsdf.inputs["Base Color"].default_value = fallback_color
        bsdf.inputs["Roughness"].default_value = 0.55

    illum_id = slots.get(SLOT_ILLUMINATE)
    if illum_id and illum_id in tex_paths:
        illum_node = nodes.new("ShaderNodeTexImage")
        illum_node.image = load_image(tex_paths[illum_id], non_color=True)
        sep = nodes.new("ShaderNodeSeparateColor")
        links.new(illum_node.outputs["Color"], sep.inputs["Color"])
        links.new(sep.outputs["Red"], bsdf.inputs["Metallic"])

    nar_id = slots.get(SLOT_NAR)
    if nar_id and nar_id in tex_paths:
        nar_node = nodes.new("ShaderNodeTexImage")
        nar_node.image = load_image(tex_paths[nar_id], non_color=True)
        links.new(nar_node.outputs["Alpha"], bsdf.inputs["Roughness"])
    elif albedo_id and albedo_id in tex_paths:
        bsdf.inputs["Roughness"].default_value = 0.65

    alpha_id = slots.get(SLOT_ALPHA_CUTOUT)
    if alpha_id and alpha_id in tex_paths:
        alpha_node = nodes.new("ShaderNodeTexImage")
        alpha_node.image = load_image(tex_paths[alpha_id], non_color=True)
        clip = nodes.new("ShaderNodeMath")
        clip.operation = "GREATER_THAN"
        clip.inputs[1].default_value = 0.5
        links.new(alpha_node.outputs["Alpha"], clip.inputs[0])
        links.new(clip.outputs["Value"], bsdf.inputs["Alpha"])
        mat.surface_render_method = "DITHERED"
    return mat


def build_mesh(unit, source, rig, names_by_hash, mat_defs, tex_paths):
    label = (unit["part"] + " " + unit["body"] + " " + unit["option"]).strip()
    sections = source["sections"]
    faces = [face for section in sections for face in section["faces"]]
    mesh = bpy.data.meshes.new(label)
    mesh.from_pydata(source["positions"], [], faces)
    obj = bpy.data.objects.new(label, mesh)
    bpy.context.collection.objects.link(obj)
    fallback = PALETTE.get(unit["part"], (.45, .48, .50, 1))
    obj.color = fallback
    obj["DP-8 Unit"] = unit["fileId"]
    obj["Patch"] = unit["patch"]
    obj["TOC SHA256"] = unit["patchSha256"]
    mesh.normals_split_custom_set_from_vertices(source["normals"])
    uv = mesh.uv_layers.new(name="HD2 UV0")
    # AGENTS.md: HD2 textures are V-top-down, Blender UV v=0 is bottom -> flip V (1 - v).
    uv.uv.foreach_set("vector", [
        coord
        for loop in mesh.loops
        for coord in (source["uv"][loop.vertex_index][0], 1.0 - source["uv"][loop.vertex_index][1])
    ])
    mesh.polygons.foreach_set("use_smooth", [True] * len(faces))
    poly_idx = 0
    for sec_idx, section in enumerate(sections):
        mat = get_blender_material(section.get("material"), mat_defs, tex_paths, fallback)
        obj.data.materials.append(mat)
        for _ in section["faces"]:
            mesh.polygons[poly_idx].material_index = sec_idx
            poly_idx += 1
    groups = {}
    for vertex, weights in enumerate(source["weights"]):
        for row in weights:
            name = names_by_hash[row["hash"]]
            if name not in groups:
                groups[name] = obj.vertex_groups.new(name=name)
            groups[name].add([vertex], row["weight"], "REPLACE")
    modifier = obj.modifiers.new("Native skin", "ARMATURE")
    modifier.object = rig
    obj.parent = rig
    return obj


def geometry_checks(objects):
    graph = bpy.context.evaluated_depsgraph_get()
    corners = []
    max_stretch = 0
    stretched_edges = 0
    outliers = []
    for obj in objects:
        evaluated = obj.evaluated_get(graph)
        mesh = evaluated.to_mesh()
        try:
            corners.extend(evaluated.matrix_world @ Vector(corner) for corner in evaluated.bound_box)
            for edge in obj.data.edges:
                a, b = edge.vertices
                span = (obj.data.vertices[a].co - obj.data.vertices[b].co).length
                if span < .0005:
                    continue
                length = (mesh.vertices[a].co - mesh.vertices[b].co).length
                max_stretch = max(max_stretch, length / span)
                if length > max(.01, span * 3):
                    stretched_edges += 1
                    outliers.append({
                        "mesh": obj.name, "edge": [a, b], "bindLength": span, "posedLength": length,
                        "posedCenter": list((mesh.vertices[a].co + mesh.vertices[b].co) * .5),
                    })
        finally:
            evaluated.to_mesh_clear()
    minimum = [min(p[i] for p in corners) for i in range(3)]
    maximum = [max(p[i] for p in corners) for i in range(3)]
    return {"min": minimum, "max": maximum, "maxEdgeStretch": max_stretch, "stretchedEdges": stretched_edges,
            "longestOutliers": sorted(outliers, key=lambda edge: -edge["posedLength"])[:8]}


def setup_studio_lighting(scene):
    world = bpy.data.worlds.new("Neutral studio")
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs["Color"].default_value = (.16, .18, .22, 1)
        bg.inputs["Strength"].default_value = 0.75
    scene.world = world

    def add_light(name, ltype, energy, loc, rot_deg, size=2.0):
        ldata = bpy.data.lights.new(name=name, type=ltype)
        ldata.energy = energy
        if ltype == "AREA":
            ldata.size = size
        elif ltype == "SUN":
            ldata.angle = radians(15)
        lobj = bpy.data.objects.new(name=name, object_data=ldata)
        bpy.context.collection.objects.link(lobj)
        lobj.location = loc
        lobj.rotation_euler = tuple(radians(a) for a in rot_deg)

    add_light("Key Sun", "SUN", 2.8, (2.0, 3.0, 4.0), (-45, 0, 155))
    add_light("Fill Front", "AREA", 45.0, (-1.8, 2.5, 1.8), (70, 0, -145), size=2.5)
    add_light("Rim Back", "AREA", 60.0, (0.5, -2.5, 2.2), (65, 0, 15), size=2.5)


def render(root, rows, names, keys, stand, rifle, resolution):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    data = json.loads((root / "patch-preview.json").read_text())
    if [row["hash"] for row in data["bones"]] != [row["hash"] for row in rows]:
        raise ValueError("Extracted Avatar order differs from the patch read-back rig")
    mod_dir = root.parent / "mod"
    mat_defs, tex_paths = extract_patch_materials(mod_dir, root / "textures")
    scene = bpy.context.scene
    rig, bone_names = build_rig(rows, names)
    by_hash = dict(zip((row["hash"] for row in rows), bone_names))
    groups = defaultdict(list)
    for unit in data["units"]:
        for source in unit["meshes"]:
            if unit["body"] != "Stocky" and not source["isHidden"]:
                groups[unit["option"]].append(build_mesh(unit, source, rig, by_hash, mat_defs, tex_paths))
    vanilla_objects = []
    vanilla_json = root / "vanilla-preview.json"
    if vanilla_json.is_file():
        vdata = json.loads(vanilla_json.read_text())
        for unit in vdata["units"]:
            for source in unit["meshes"]:
                if not source["isHidden"]:
                    obj = build_mesh(unit, source, rig, by_hash, {}, {})
                    obj.hide_render = True
                    obj.hide_set(True)
                    vanilla_objects.append(obj)
    stand_clip, rifle_clip = (clip_data(root / "native" / f"animation_0x{id}.toc") for id in (stand, rifle))
    actions = [
        build_action(rig, bone_names, rows, keys, stand_clip, f"Standing · {stand}"),
        build_action(rig, bone_names, rows, keys, rifle_clip, f"Rifle · {rifle}"),
    ]
    camera = bpy.data.objects.new("Comparison camera", bpy.data.cameras.new("Comparison camera"))
    scene.collection.objects.link(camera)
    scene.camera = camera
    camera.data.type = "ORTHO"
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = resolution
    scene.render.resolution_y = resolution
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.fps = 30
    scene.view_settings.view_transform = "AgX"
    setup_studio_lighting(scene)
    metrics = {}

    def shot(label, action, frame, direction, coat=True, shoes=True, match=None, custom_active=None):
        rig.animation_data.action = action
        if action is None:
            for bone in rig.pose.bones:
                bone.matrix_basis = Matrix.Identity(4)
        scene.frame_set(frame)
        active = custom_active if custom_active is not None else (
            groups[""] + groups["coat/on" if coat else "coat/off"] + groups["shoes/on" if shoes else "shoes/off"]
        )
        for objects in list(groups.values()) + [vanilla_objects]:
            for obj in objects:
                obj.hide_render = obj not in active
                obj.hide_set(obj not in active)
        bpy.context.view_layer.update()
        evidence = geometry_checks(active)
        metrics[label] = evidence
        framing = evidence if match is None else metrics[match]
        low, high = Vector(framing["min"]), Vector(framing["max"])
        center = (low + high) * .5
        camera.location = center + Vector(direction) * 4
        camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
        camera.data.ortho_scale = max(2.15, high.z - low.z + .17)
        scene.render.filepath = str(root / f"{label}.png")
        bpy.ops.render.render(write_still=True)

    front, side, back = (0, 1, .02), (1, 0, .02), (0, -1, .02)
    standing_frame = round(stand_clip.animation_length * 30 * .45)
    rifle_frame = round(rifle_clip.animation_length * 30 * .45)
    shot("bind-front", None, 0, front)
    shot("standing-front", actions[0], standing_frame, front)
    shot("standing-side", actions[0], standing_frame, side)
    shot("standing-back", actions[0], standing_frame, back)
    shot("rifle-front", actions[1], rifle_frame, (.32, 1, .05))
    shot("coat-off", actions[0], standing_frame, front, coat=False, match="standing-front")
    shot("shoes-off", actions[0], standing_frame, front, shoes=False, match="standing-front")
    if vanilla_objects:
        shot("vanilla-dp8-rifle", actions[1], rifle_frame, (.32, 1, .05), custom_active=vanilla_objects)
    scene.frame_start = 0
    scene.frame_end = ceil(stand_clip.animation_length * 30)
    rig.animation_data.action = actions[0]
    scene.frame_set(standing_frame)
    for obj in groups[""] + groups["shoes/on"] + groups["coat/on"]:
        obj.hide_render = False
        obj.hide_set(False)
    for obj in groups["coat/off"] + groups["shoes/off"] + vanilla_objects:
        obj.hide_render = True
        obj.hide_set(True)
    scene.render.filepath = "//standing-front.png"
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                area.spaces.active.region_3d.view_perspective = "CAMERA"
                area.spaces.active.shading.type = "MATERIAL"
                area.spaces.active.overlay.show_overlays = False
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(root / "ias-dp8.blend"))
    (root / "render-checks.json").write_text(json.dumps(metrics, indent=2), encoding="utf8")


if __name__ == "__main__":
    arguments = ArgumentParser()
    arguments.add_argument("output", type=Path)
    arguments.add_argument("--probe", action="store_true")
    arguments.add_argument("--stand")
    arguments.add_argument("--rifle")
    arguments.add_argument("--resolution", type=int, default=960)
    args = arguments.parse_args(sys.argv[sys.argv.index("--") + 1:])
    args.output = args.output.resolve()
    rig_rows, name_map, animation_keys = read_native_rig(args.output / "native")
    if args.probe:
        probe(args.output, rig_rows, name_map, animation_keys)
    else:
        if not args.stand or not args.rifle:
            arguments.error("rendering requires --stand and --rifle native animation IDs")
        render(args.output, rig_rows, name_map, animation_keys, args.stand, args.rifle, args.resolution)
