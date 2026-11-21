"""Pack converted model resources and IAS 1.6.3 rig profiles."""
from __future__ import annotations

import json
import zipfile
from dataclasses import dataclass
from pathlib import Path
from uuid import UUID

from archive_payloads import decode, encode  # type: ignore
from hd2_system import (  # type: ignore
    armor_cleanup,
    independent_packaging as ipkg,
    resource_isolation as riso,
    rig_format,
    runtime_group,
    runtime_manifest as rman,
    sdk_adapter,
    unit_rig_profiles as urig,
)

UNIT_TYPE = 0xE0A48D0BE9A7453F
MATERIAL_TYPE = 0xEAC0B497876ADEDF
COMPANION_NAME_LIMIT = 80
COMPANION_FILENAME = "IAS_Companion.hd2irpack.zip"


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


def build_domain_package(
    *,
    project_id: str,
    domain_meta: dict,
    authoring_project: dict,
    avatar_source_bones: list[dict],
    stock_lookup: dict[tuple[int, int], SimpleEntry],
    material_alias_map: dict[int, int],
    staging_dir: Path,
    rig_gender: str = "FEMALE",
) -> str:
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
    for (f_id, t_id), st_entry in stock_lookup.items():
        if t_id != UNIT_TYPE:
            candidate_entries.append(riso.clone_entry(st_entry))

    unit_snapshots = []
    weighted_by_unit: dict[int, list[str]] = {}
    required_rigonly_by_unit: dict[int, list[str]] = {}

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
        required_rigonly_by_unit[pub_id] = list(row.get("extraBones", []))

    for c_row in cleanup_rows:
        native_id = int(c_row["native_unit_id"])
        pt_id = int(c_row["point_unit_id"])
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

    member_rigonly_zip = staging_dir / f"_member_rigonly_{archive_hex}.zip"
    rigbin_name = f"HD2IndependentRig/{project_id}.{archive_hex}.rigbin"
    iso_name = f"HD2IndependentRig/{isolation_doc['file_name']}"
    with zipfile.ZipFile(member_rigonly_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.writestr(rigbin_name, rigbin_bytes)
        if diff_doc is not None:
            zf.writestr(f"HD2IndependentRig/{diff_doc['file_name']}", diff_doc["text"].encode("utf-8"))
        zf.writestr(iso_name, isolation_doc["text"].encode("utf-8"))

    return str(member_rigonly_zip)


def pack(raw_dir: Path, out_dir: Path, staging: Path, model_id: UUID, display_name: str, rig_gender: str) -> None:
    project_id = model_id.hex
    authored_manifest = json.loads((raw_dir / "authored_manifest.json").read_text(encoding="utf-8"))
    authoring_project = json.loads((raw_dir / "authoring_project.json").read_text(encoding="utf-8"))
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

    if isolated_mats:
        entries_to_patch_files(isolated_mats, staging / "main_mod" / "9ba626afa44a3aa3.patch_2")

    companion_members = []
    for key, stock in (("body", stock_body), ("helmet", stock_helmet)):
        domain = authored_manifest.get(key)
        if domain and domain["authoredUnits"]:
            companion_members.append(build_domain_package(
                project_id=project_id,
                domain_meta=domain,
                authoring_project=authoring_project,
                avatar_source_bones=avatar_source_bones,
                stock_lookup=stock,
                material_alias_map=mat_alias_map,
                staging_dir=staging,
                rig_gender=rig_gender,
            ))

    if not companion_members:
        raise ValueError("No model geometry matched IAS equipment slots.")

    # IAS limits the Companion's visible name independently of the main mod title.
    companion_name = "".join(" " if ord(c) < 32 or ord(c) == 127 else c for c in display_name).strip()
    if len(companion_name) > COMPANION_NAME_LIMIT:
        companion_name = companion_name[:COMPANION_NAME_LIMIT - 1].rstrip() + "…"

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
        "Description": "Model resources for the accompanying IAS 1.6.3 Companion.",
        "Options": [{"Name": display_name, "Include": ["Files"]}],
    }
    with zipfile.ZipFile(out_dir / "IAS_MainMod.zip", "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        for file in sorted((staging / "main_mod").iterdir()):
            archive.write(file, f"Files/{file.name}")
