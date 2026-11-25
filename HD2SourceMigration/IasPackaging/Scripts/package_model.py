"""Pack converted model resources and IAS rig profiles."""
from __future__ import annotations

import copy
import inspect
import json
import math
import zipfile
from dataclasses import dataclass
from pathlib import Path
from time import perf_counter
from types import SimpleNamespace
from uuid import UUID

from archive_payloads import decode, encode  # type: ignore
from hd2_system import (  # type: ignore
    armor_cleanup,
    author_credit,
    clothing_pose,
    physics_compiler,
    physics_packaging,
    preview_transport,
    shared_physics,
    independent_packaging as ipkg,
    resource_isolation as riso,
    rig_format,
    runtime_group,
    runtime_manifest as rman,
    sdk_adapter,
    unit_rig_profiles as urig,
)

from ias_physics.format_v1 import build as physbone_build
from project_identity import owner_id, point_id, project_filename

UNIT_TYPE = 0xE0A48D0BE9A7453F
MATERIAL_TYPE = 0xEAC0B497876ADEDF
COMPANION_NAME_LIMIT = 80
# 游戏全局常驻的 Archive：共用材质/贴图放这里，任意一件装备单独穿都能找到。
SHARED_RESOURCE_ARCHIVE = "9ba626afa44a3aa3"
COMPANION_FILENAME = "IAS_Companion.hd2irpack.zip"
IAS_DEFAULT_FIXED_DT = 1.0 / 60.0  # AQ SDK physics_packaging 的默认步长


def check_sdk(with_physics: bool) -> None:
    def require(module, name, positional, keywords=()):
        try:
            function = getattr(module, name)
            inspect.signature(function).bind(*([None] * positional), **dict.fromkeys(keywords))
        except (AttributeError, TypeError, ValueError) as error:
            raise ValueError(f"缺少兼容的 {module.__name__}.{name}（{error}）") from None

    require(ipkg, "load_part_catalog", 0)
    ipkg.load_part_catalog()
    require(ipkg, "build_package_plan", 0, ("project_name", "archive_name", "active_unit_ids", "objects", "project_id"))
    require(sdk_adapter, "build_save_jobs", 1)
    require(armor_cleanup, "build_cleanup_plan", 1, ("active_unit_ids", "saved_unit_ids"))
    require(riso, "clone_entry", 1)
    require(riso, "isolate_model_entries", 3, ("material_aliases",))
    require(riso, "isolate_material_entries", 3)
    require(riso, "binding_for_plan", 1, ("rig_profile_unit_ids",))
    require(urig, "load_avatar_source", 0)
    require(urig, "build_rig_document", 2, ("required_bones_by_unit", "runtime_source_bones", "weighted_bones_by_unit", "rig_gender"))
    require(rig_format, "build", 1)
    require(rman, "build_runtime_difference_manifest", 1, ("rig_profile_unit_ids",))
    require(runtime_group, "write_group", 2, ("identity", "name", "overwrite"))
    for module, names in ((ipkg, ("PROP_PART_SLOT", "PROP_VARIANT_KIND", "PROP_BASE_GROUP", "VARIANT_BASE")),
                          (riso, ("POLICY",))):
        for name in names:
            if not isinstance(getattr(module, name, None), str):
                raise ValueError(f"缺少字符串常量 {module.__name__}.{name}")
    if with_physics:
        require(physics_packaging, "required_profile_bones_by_unit", 3, ("solver_scope", "weighted_bones_by_unit"))
        require(shared_physics, "compile_shared_physics_pack", 6, ("runtime_capabilities", "weighted_bones_by_unit"))
        require(preview_transport, "compile_preview", 5)
        require(physics_compiler, "compile_physics_pack", 5)
        require(author_credit, "attach", 5)
        for module, name in ((shared_physics, "CAPABILITY_RUNTIME"), (clothing_pose, "CAPABILITY")):
            if not isinstance(getattr(module, name, None), str):
                raise ValueError(f"缺少字符串常量 {module.__name__}.{name}")


@dataclass
class SimpleEntry:
    """Minimal AQSDK TocEntry duck-type consumed by hd2_system/resource_isolation.py."""
    FileID: int
    TypeID: int
    TocData: bytearray
    GpuData: bytearray
    StreamData: bytearray
    IsLoaded: bool = False
    LoadedData: object = None
    IsModified: bool = False


def load_stock_lookup(stock_dirs: list[Path]) -> dict[tuple[int, int], SimpleEntry]:
    table: dict[tuple[int, int], SimpleEntry] = {}
    for sdir in stock_dirs:
        idx_file = sdir / "index.json"
        if not idx_file.exists():
            continue
        rows = json.loads(idx_file.read_text(encoding="utf-8"))
        for row in rows:
            t_id = int(row["typeHex"], 16)
            f_id = int(row["fileHex"], 16)
            if row.get("payloadOmitted"):
                table[(f_id, t_id)] = SimpleEntry(f_id, t_id, bytearray(), bytearray(), bytearray())
                continue
            prefix = f"{row['typeHex']}_{row['fileHex']}"
            toc = (sdir / f"{prefix}.toc").read_bytes()
            gpu_p = sdir / f"{prefix}.gpu"
            str_p = sdir / f"{prefix}.stream"
            gpu = gpu_p.read_bytes() if gpu_p.exists() else b""
            stm = str_p.read_bytes() if str_p.exists() else b""
            table[(f_id, t_id)] = SimpleEntry(f_id, t_id, bytearray(toc), bytearray(gpu), bytearray(stm))
    return table


def entries_to_patch_files(entries: list[SimpleEntry], out_base: Path) -> None:
    payload_map: dict[tuple[int, int], tuple[bytes, bytes, bytes]] = {}
    for e in entries:
        key = (int(e.FileID), int(e.TypeID))
        payload_map[key] = (bytes(e.TocData), bytes(e.GpuData), bytes(e.StreamData))
    main_b, gpu_b, stm_b = encode(payload_map)
    if decode(main_b, gpu_b, stm_b) != payload_map:
        raise RuntimeError(f"Archive readback mismatch for {out_base}")
    out_base.parent.mkdir(parents=True, exist_ok=True)
    out_base.write_bytes(main_b)
    Path(str(out_base) + ".gpu_resources").write_bytes(gpu_b)
    Path(str(out_base) + ".stream").write_bytes(stm_b)


def build_domain_chains(
    jiggle_rows: list[dict],
    weighted_bones_in_domain: set[str],
    domain_label: str,
    fixed_dt: float = IAS_DEFAULT_FIXED_DT,
) -> list[dict]:
    """Decompose jiggle bone forest consumed by this domain into disjoint HD2PHY1 chains."""
    by_bone = {row["bone"]: row for row in jiggle_rows}
    children_all: dict[str, list[str]] = {row["bone"]: [] for row in jiggle_rows}
    for row in jiggle_rows:
        p = row.get("parentInExtra")
        if p and p in children_all:
            children_all[p].append(row["bone"])

    # Keep jiggle bones that are directly weighted in this domain OR have a weighted descendant
    active: set[str] = set()

    def mark_active(b: str) -> bool:
        has_w = b in weighted_bones_in_domain
        for c in children_all.get(b, ()):
            if mark_active(c):
                has_w = True
        if has_w:
            active.add(b)
        return has_w

    for row in jiggle_rows:
        if not row.get("parentInExtra") or row["parentInExtra"] not in by_bone:
            mark_active(row["bone"])

    if not active:
        return []

    children_dom = {b: [c for c in children_all[b] if c in active] for b in active}
    roots: list[str] = []
    for b in sorted(active):
        p = by_bone[b].get("parentInExtra")
        if not p or p not in active:
            roots.append(b)
        else:
            sibs = children_dom[p]
            if len(sibs) >= 2 and sibs[0] != b:
                roots.append(b)

    chains: list[dict] = []
    for idx, root_bone in enumerate(roots):
        seq = [root_bone]
        cur = root_bone
        while children_dom.get(cur):
            nxt = children_dom[cur][0]
            seq.append(nxt)
            cur = nxt

        # Confirm at least one bone in seq is directly weighted in this domain
        if not any(b in weighted_bones_in_domain for b in seq):
            continue

        tip_name = by_bone[seq[-1]]["tipBone"]
        full_bones = seq + [tip_name]

        avg_stiff = sum(float(by_bone[b]["sourceStiffness"]) for b in seq) / len(seq)
        avg_damp = sum(float(by_bone[b]["sourceDamping"]) for b in seq) / len(seq)
        ias_stiff = max(4.0, min(28.0, avg_stiff * 0.10))
        ias_damp = max(0.15, min(0.65, avg_damp * 0.08))
        local_bend = len(seq) >= 8
        if local_bend:
            ias_stiff, ias_damp = 12.0, 0.80

        joints = []
        for j_i, b_name in enumerate(full_bones):
            progress = j_i / max(1, len(full_bones) - 1)
            src_row = by_bone.get(b_name, by_bone[seq[-1]])
            lim_rad = float(src_row.get("angleLimitRad", math.pi / 6.0))
            joints.append({
                "bone": b_name,
                "radius": 0.0 if j_i == 0 else 0.005,
                "stiffness_scale": 0.0 if j_i == 0 else round(1.5 - 0.5 * progress, 4),
                "damping_scale": 0.0 if j_i == 0 else 1.0,
                "limit_kind": "NONE" if j_i == 0 else "CONE",
                "limit_angle": round(lim_rad, 5),
                "limit_angle2": round(lim_rad, 5),
                "limit_axis": [1.0, 0.0, 0.0],
            })

        chain = {
            "name": f"{domain_label}_chain_{idx:02d}",
            "stiffness": round(ias_stiff, 4),
            "damping": round(ias_damp, 4),
            "gravity": [0.0, 0.0, -9.81],
            "wind": [0.0, 0.0, 0.0],
            "group_mask": 1,
            "collision_mask": 1 << 30,
            "root_swing": True,
            "local_bend": local_bend,
            "joints": joints,
        }
        if domain_label == "helmet":
            # Source 角向阻尼是每秒的系数，IAS 阻尼是每个固定步的速度衰减比例。
            # 底座弹簧的平移阻尼不参与旋转。没有角向数据时交给 HD2PHY1 的默认值。
            angular = [float(by_bone[b]["sourceAngularDamping"]) for b in seq
                       if by_bone[b].get("sourceAngularDamping") is not None]
            if angular:
                chain["damping"] = -math.expm1(-fixed_dt * max(0.0, sum(angular) / len(angular)))
            else:
                del chain["damping"]
        chains.append(chain)

    return chains


def build_domain_package(
    *,
    project_id: str,
    domain_meta: dict,
    authoring_project: dict,
    avatar_source_bones: list[dict],
    stock_lookup: dict[tuple[int, int], SimpleEntry],
    material_alias_map: dict[int, int],
    staging_dir: Path,
    rig_gender: str,
    jiggle_rows: list[dict],
    domain_kind: str,
) -> tuple[str, bool]:
    archive_hex = str(domain_meta["archive"]).lower()
    active_unit_ids = [int(x) for x in domain_meta["stockUnitIdsDecimal"]]
    authored_units = domain_meta["authoredUnits"]
    point_units_by_native = {int(row["nativeUnitIdDecimal"]): row for row in domain_meta["pointUnits"]}

    seen_slots: dict[str, str] = {}
    objects = []
    for row in authored_units:
        slot = row["partSlot"]
        if slot not in seen_slots:
            obj_name = f"IAS_{slot}"
            seen_slots[slot] = obj_name
            objects.append((
                obj_name,
                {
                    ipkg.PROP_PART_SLOT: slot,
                    ipkg.PROP_VARIANT_KIND: ipkg.VARIANT_BASE,
                    ipkg.PROP_BASE_GROUP: "基础组",
                },
            ))

    plan = ipkg.build_package_plan(
        project_name=project_id,
        archive_name=archive_hex,
        active_unit_ids=active_unit_ids,
        objects=objects,
        project_id=project_id,
    )
    plan["resource_target_policy"] = riso.POLICY
    plan["rig_gender"] = rig_gender

    jobs = sdk_adapter.build_save_jobs(plan)
    native_to_published = {int(j.native_unit_id): int(j.published_unit_id) for j in jobs}
    saved_published_ids = set(native_to_published.values())

    cleanup_rows = armor_cleanup.build_cleanup_plan(
        plan,
        active_unit_ids=active_unit_ids,
        saved_unit_ids=saved_published_ids,
    )
    plan["armor_cleanup"] = cleanup_rows

    candidate_entries: list[SimpleEntry] = []

    unit_snapshots = []
    weighted_by_unit: dict[int, list[str]] = {}
    required_rigonly_by_unit: dict[int, list[str]] = {}

    weighted_by_part: dict[str, set[str]] = {}
    for row in authored_units:
        native_id = int(row["nativeUnitIdDecimal"])
        if native_id not in native_to_published:
            continue
        pub_id = native_to_published[native_id]
        toc_bytes = Path(row["tocPath"]).read_bytes()
        gpu_bytes = Path(row["gpuPath"]).read_bytes()
        candidate_entries.append(SimpleEntry(pub_id, UNIT_TYPE, bytearray(toc_bytes), bytearray(gpu_bytes), bytearray()))

        nodes = row["snapshotNodes"]
        unit_snapshots.append({
            "unit_id": pub_id,
            "palette_slots": len(nodes),
            "nodes": nodes,
            "source_label": f"{archive_hex}:{row['partSlot']}",
        })
        w_bones = list(row["weightedBones"])
        weighted_by_unit[pub_id] = w_bones
        weighted_by_part.setdefault(row["partSlot"], set()).update(w_bones)
        required_rigonly_by_unit[pub_id] = list(row.get("extraBones", []))

    for c_row in cleanup_rows:
        native_id = int(c_row["native_unit_id"])
        pt_id = int(c_row["point_unit_id"])
        if pt_id != point_id(project_id, archive_hex, native_id):
            raise ValueError(f"IAS 点网格资源 ID 与项目身份不符：{native_id:016x}")
        pt_src = point_units_by_native[native_id]
        candidate_entries.append(
            SimpleEntry(
                pt_id,
                UNIT_TYPE,
                bytearray(Path(pt_src["tocPath"]).read_bytes()),
                bytearray(Path(pt_src["gpuPath"]).read_bytes()),
                bytearray(),
            )
        )

    isolated_entries = riso.isolate_model_entries(
        plan,
        candidate_entries,
        lambda fid, tid: stock_lookup.get((int(fid), int(tid))),
        material_aliases=material_alias_map,
    )

    entries_to_patch_files(isolated_entries, staging_dir / "main_mod" / f"{archive_hex}.patch_0")

    # 1. Rig-Only Companion Member ZIP
    rig_doc = urig.build_rig_document(
        authoring_project,
        unit_snapshots,
        required_bones_by_unit=required_rigonly_by_unit,
        runtime_source_bones=avatar_source_bones,
        weighted_bones_by_unit=weighted_by_unit,
        rig_gender=rig_gender,
    )
    rigbin_bytes, _ = rig_format.build(rig_doc)
    rig_unit_ids = [int(p["unit_id"], 16) for p in rig_doc["profiles"]]
    diff_doc = rman.build_runtime_difference_manifest(plan, rig_profile_unit_ids=rig_unit_ids)
    isolation_doc = riso.binding_for_plan(plan, rig_profile_unit_ids=rig_unit_ids)
    if isolation_doc["file_name"] != f"{owner_id(project_id, archive_hex)}.isolation.ini":
        raise ValueError(f"IAS 隔离清单与项目身份不符：{archive_hex}")

    member_rigonly_zip = staging_dir / f"_member_rigonly_{archive_hex}.zip"
    rigbin_name = f"HD2IndependentRig/{project_filename(project_id, archive_hex, 'rigbin')}"
    iso_name = f"HD2IndependentRig/{isolation_doc['file_name']}"
    with zipfile.ZipFile(member_rigonly_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.writestr(rigbin_name, rigbin_bytes)
        if diff_doc is not None:
            zf.writestr(f"HD2IndependentRig/{diff_doc['file_name']}", diff_doc["text"].encode("utf-8"))
        zf.writestr(iso_name, isolation_doc["text"].encode("utf-8"))

    if jiggle_rows:
        all_domain_weighted = set().union(*weighted_by_part.values()) if weighted_by_part else set()
        fixed_dt = float(authoring_project.get("fixed_dt", IAS_DEFAULT_FIXED_DT))
        dom_chains = build_domain_chains(jiggle_rows, all_domain_weighted, domain_kind.lower(), fixed_dt)
        if dom_chains:
            phys_authoring = copy.deepcopy(authoring_project)
            phys_authoring["fixed_dt"] = fixed_dt
            phys_authoring["chains"] = dom_chains
            phys_authoring["colliders"] = []
            phys_authoring["chain_links"] = []

            solver_scope = "shared_body_v2" if domain_kind == "BODY" else "single_unit"
            req_phys_by_unit = physics_packaging.required_profile_bones_by_unit(
                plan,
                phys_authoring,
                {k: sorted(v) for k, v in weighted_by_part.items()},
                solver_scope=solver_scope,
                weighted_bones_by_unit=weighted_by_unit,
            )
            merged_req = {
                uid: sorted(set(required_rigonly_by_unit.get(uid, ())) | set(req_phys_by_unit.get(uid, ())))
                for uid in weighted_by_unit
            }
            phys_rig_doc = urig.build_rig_document(
                phys_authoring,
                unit_snapshots,
                required_bones_by_unit=merged_req,
                runtime_source_bones=avatar_source_bones,
                weighted_bones_by_unit=weighted_by_unit,
                rig_gender=rig_gender,
            )
            if domain_kind == "BODY":
                compiled_phys = shared_physics.compile_shared_physics_pack(
                    plan,
                    phys_authoring,
                    {k: sorted(v) for k, v in weighted_by_part.items()},
                    {k: sorted(v) for k, v in weighted_by_part.items()},
                    phys_rig_doc,
                    physbone_build,
                    runtime_capabilities=(shared_physics.CAPABILITY_RUNTIME, clothing_pose.CAPABILITY),
                    weighted_bones_by_unit=weighted_by_unit,
                )
                # 作者声明不会进入客户端的运行时 rig 目录。共享物理沿用
                # SDK 的预览配套，把项目身份和最终模型、消费者骨架一起绑定。
                units = {entry.FileID: entry for entry in isolated_entries if entry.TypeID == UNIT_TYPE}
                preview_models = {}
                for row in authored_units:
                    uid = native_to_published.get(int(row["nativeUnitIdDecimal"]))
                    if uid is not None:
                        entry = units[uid]
                        with Path(row["previewModelPath"]).open(encoding="utf-8") as metadata:
                            entry.LoadedData = json.load(metadata, object_hook=lambda fields: SimpleNamespace(**fields))
                        preview_models[uid] = entry
                rig_name = compiled_phys["manifest"]["rig"]["file"]
                preview = preview_transport.compile_preview(
                    plan, phys_rig_doc, preview_models, rig_name, compiled_phys["files"][rig_name],
                )
                if preview is None or preview[0] != project_filename(project_id, archive_hex, "hd2preview"):
                    raise ValueError(f"IAS 共享物理无法生成项目预览配套：{archive_hex}")
                compiled_phys["files"][preview[0]] = preview[1]
            else:
                compiled_phys = physics_compiler.compile_physics_pack(
                    plan,
                    phys_authoring,
                    {k: sorted(v) for k, v in weighted_by_part.items()},
                    phys_rig_doc,
                    physbone_build,
                )

            runtime_payloads = {
                fname: fbytes
                for fname, fbytes in compiled_phys["files"].items()
                if not fname.endswith(".hd2physpack.json")
            }
            if diff_doc is not None:
                runtime_payloads[diff_doc["file_name"]] = diff_doc["text"].encode("utf-8")
            runtime_payloads[isolation_doc["file_name"]] = isolation_doc["text"].encode("utf-8")
            author_credit.attach(compiled_phys, plan, runtime_payloads, "HD2ModManager", "")

            member_phys_zip = staging_dir / f"_member_iasphys_{archive_hex}.zip"
            with zipfile.ZipFile(member_phys_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
                for fname, fbytes in sorted(compiled_phys["files"].items()):
                    if fname.endswith(".hd2physpack.json"):
                        continue
                    zf.writestr(f"HD2IndependentRig/{fname}", fbytes)
                if diff_doc is not None:
                    zf.writestr(f"HD2IndependentRig/{diff_doc['file_name']}", diff_doc["text"].encode("utf-8"))
                zf.writestr(iso_name, isolation_doc["text"].encode("utf-8"))

            return str(member_phys_zip), True

    return str(member_rigonly_zip), False


def pack(raw_dir: Path, out_dir: Path, staging: Path, model_id: UUID, display_name: str, rig_gender: str, with_physics: bool) -> list[str]:
    started = perf_counter()
    project_id = model_id.hex
    authored_manifest = json.loads((raw_dir / "authored_manifest.json").read_text(encoding="utf-8"))
    authoring_project = json.loads((raw_dir / "authoring_project.json").read_text(encoding="utf-8"))
    jiggle_path = raw_dir / "jiggle_facts.json"
    jiggle_rows = json.loads(jiggle_path.read_text(encoding="utf-8")) if with_physics and jiggle_path.is_file() else []
    avatar_source_bones = urig.load_avatar_source()

    stock_body = load_stock_lookup([raw_dir / "stock_body"]) if (raw_dir / "stock_body").exists() else {}
    stock_helmet = load_stock_lookup([raw_dir / "stock_helmet"]) if (raw_dir / "stock_helmet").exists() else {}
    all_stock = {**stock_body, **stock_helmet}

    mat_alias_map: dict[int, int] = {}
    isolated_mats: list[SimpleEntry] = []

    mat_toc_str = authored_manifest.get("materialPackTocPath")
    raw_mat_dir_str = authored_manifest.get("rawMaterialsDir")
    raw_mat_entries: list[SimpleEntry] = []
    if mat_toc_str:
        mat_toc_path = Path(mat_toc_str)
        mat_gpu_path = Path(str(mat_toc_path) + ".gpu_resources")
        mat_stm_path = Path(str(mat_toc_path) + ".stream")
        raw_mat_decoded = decode(
            mat_toc_path.read_bytes(),
            mat_gpu_path.read_bytes() if mat_gpu_path.exists() else b"",
            mat_stm_path.read_bytes() if mat_stm_path.exists() else b"",
        )
        raw_mat_entries = [
            SimpleEntry(fid, tid, bytearray(m), bytearray(g), bytearray(s))
            for (fid, tid), (m, g, s) in sorted(raw_mat_decoded.items())
        ]
    elif raw_mat_dir_str:
        raw_mat_entries = [
            v for _, v in sorted(load_stock_lookup([Path(raw_mat_dir_str)]).items())
        ]

    if raw_mat_entries:
        isolated_mats, mat_records = riso.isolate_material_entries(
            project_id,
            raw_mat_entries,
            lambda fid, tid: all_stock.get((int(fid), int(tid))),
        )
        mat_alias_map = {
            int(r["source"], 16): int(r["published"], 16)
            for r in mat_records
            if int(r["type"], 16) == MATERIAL_TYPE
        }

    companion_members = []
    physics_emitted = False
    for key, stock in (("body", stock_body), ("helmet", stock_helmet)):
        domain = authored_manifest.get(key)
        if domain and domain["authoredUnits"]:
            member, has_physics = build_domain_package(
                project_id=project_id,
                domain_meta=domain,
                authoring_project=authoring_project,
                avatar_source_bones=avatar_source_bones,
                stock_lookup=stock,
                material_alias_map=mat_alias_map,
                staging_dir=staging,
                rig_gender=rig_gender,
                jiggle_rows=jiggle_rows,
                domain_kind=key.upper(),
            )
            companion_members.append(member)
            physics_emitted |= has_physics

    if not companion_members:
        raise ValueError("No model geometry matched IAS equipment slots.")
    if isolated_mats:
        # 共用材质/贴图放进全局常驻的 9ba626afa44a3aa3（官方 IAS 包同样做法）。
        # 放进某件装备自己的 Archive 时，只穿另一件（如只戴头盔）该 Archive 不加载，材质就缺失。
        entries_to_patch_files(isolated_mats, staging / "main_mod" / f"{SHARED_RESOURCE_ARCHIVE}.patch_2")
    print(f"IAS_TIMING\tAQ SDK 资源隔离、骨架与配套编译\t{round((perf_counter() - started) * 1000)}")

    # IAS limits the Companion's visible name independently of the main mod title.
    companion_name = "".join(" " if ord(c) < 32 or ord(c) == 127 else c for c in display_name).strip()
    if len(companion_name) > COMPANION_NAME_LIMIT:
        companion_name = companion_name[:COMPANION_NAME_LIMIT - 1].rstrip() + "…"

    started = perf_counter()
    out_dir.mkdir()
    runtime_group.write_group(
        companion_members,
        out_dir / COMPANION_FILENAME,
        identity=project_id,
        name=companion_name or f"IAS {project_id}",
        overwrite=False,
    )
    manifest = {
        "Version": 1,
        "Guid": str(model_id),
        "Name": f"{display_name} (IAS)",
        "Description": "Model resources for the accompanying IAS Companion.",
        "Options": [{"Name": display_name, "Include": ["Files"]}],
    }
    # BC7 GPU 载荷已经压缩，快速 DEFLATE 保留相同的文件内容和 ZIP 兼容性。
    with zipfile.ZipFile(out_dir / "IAS_MainMod.zip", "w", compression=zipfile.ZIP_DEFLATED, compresslevel=1) as archive:
        archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        for file in sorted((staging / "main_mod").iterdir()):
            archive.write(file, f"Files/{file.name}")
    print(f"IAS_TIMING\t压缩输出\t{round((perf_counter() - started) * 1000)}")

    return ["输入中没有可编译的 IAS 物理链，配套包仅包含骨架和绑定。"] if with_physics and not physics_emitted else []
