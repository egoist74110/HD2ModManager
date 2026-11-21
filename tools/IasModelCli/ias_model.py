"""Convert HD2 patch models and L4D2 VPK models for official IAS 1.6.3."""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory
from uuid import UUID, uuid4


TOOL_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOL_DIR.parent.parent
BONE_HASH = REPO_ROOT / "HD2ModManager" / "Resources" / "AssetMetadata" / "bonehash.txt"


def archive_id(value: str) -> str:
    if not re.fullmatch(r"[0-9a-fA-F]{16}", value):
        raise argparse.ArgumentTypeError("expected a 16-digit hex archive ID")
    return value.lower()


def existing(path: Path, *, directory: bool = False) -> Path:
    if not (path.is_dir() if directory else path.is_file()):
        raise ValueError(f"{'Directory' if directory else 'File'} not found: {path}")
    return path.resolve()


def parse_args() -> argparse.Namespace:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("source", type=Path, help="patch directory (hd2) or model VPK (l4d2)")
    common.add_argument("--game-data", required=True, type=Path, help="Helldivers 2 data directory")
    common.add_argument("--ias", required=True, type=Path, help="extracted official IAS 1.6.3 root")
    common.add_argument("--aqsdk", required=True, type=Path, help="HD2SDK-AQ-Edition checkout")
    common.add_argument("--out", required=True, type=Path, help="new output directory")
    common.add_argument("--name", help="model display name (defaults to the input name)")
    common.add_argument("--id", type=UUID, help="reuse an ID when rebuilding the same model")
    common.add_argument("--rig-gender", choices=("FEMALE", "MALE"), default="FEMALE")
    common.add_argument("--bone-hash", type=Path, default=BONE_HASH)

    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="kind", required=True)
    sub.add_parser("hd2", parents=[common], help="convert an ordinary Helldivers 2 equipment patch")
    l4d2 = sub.add_parser("l4d2", parents=[common], help="convert a Left 4 Dead 2 model VPK")
    l4d2.add_argument("--body-archive", type=archive_id, default="58e4bd4b2278d15c")
    l4d2.add_argument("--helmet-archive", type=archive_id, default="a856edff49cfdd95")
    l4d2.add_argument("--model-stem", help="model path inside a VPK with several models")
    l4d2.add_argument("--l4d2-root", type=Path, help="left4dead2 directory for shared materials")
    l4d2.add_argument("--texconv", type=Path, help="path to texconv.exe")
    return parser.parse_args()


def convert(args: argparse.Namespace) -> None:
    is_hd2 = args.kind == "hd2"
    source = existing(args.source, directory=is_hd2)
    game_data = existing(args.game_data, directory=True)
    ias = existing(args.ias, directory=True)
    aqsdk = existing(args.aqsdk, directory=True)
    out = args.out.resolve()
    model_id = args.id or uuid4()
    default_name = source.name if is_hd2 else source.stem
    display_name = args.name if args.name is not None else default_name
    if not display_name.strip():
        raise ValueError("Model display name must not be blank.")
    if os.path.lexists(args.out) or out.exists():
        raise FileExistsError(f"Output already exists: {out}")
    input_dirs = [game_data, ias, aqsdk]
    if is_hd2:
        input_dirs.append(source)
    if any(out.is_relative_to(directory) for directory in input_dirs):
        raise ValueError("Use an output directory outside the game data, SDK and model input directories.")

    companion_runtime = ias / "Generator" / "runtime" / "companion"
    existing(companion_runtime / "archive_payloads.py")
    sys.dont_write_bytecode = True
    sys.path[:0] = [str(aqsdk), str(companion_runtime)]
    from package_model import COMPANION_FILENAME, pack

    part_catalog = existing(aqsdk / "hd2_system" / "armor_table" / "armor_part_unit_ids.json")
    bindings = existing(aqsdk / "hd2_system" / "resource_binding_targets.json")
    options = {
        "--mod-dir" if is_hd2 else "--vpk": source,
        "--game-data": game_data,
        "--bone-hash": existing(args.bone_hash),
        "--part-catalog": part_catalog,
        "--binding-catalog": bindings,
    }
    if not is_hd2:
        if source.suffix.lower() != ".vpk":
            raise ValueError("The L4D2 input must be a .vpk file.")
        options["--body-archive"] = args.body_archive
        options["--helmet-archive"] = args.helmet_archive
        if args.model_stem:
            options["--model-stem"] = args.model_stem
        if args.l4d2_root:
            l4d2_root = existing(args.l4d2_root, directory=True)
            existing(l4d2_root / "pak01_dir.vpk")
            if out.is_relative_to(l4d2_root):
                raise ValueError("Use an output directory outside the L4D2 installation.")
            options["--l4d2-root"] = l4d2_root
        if args.texconv:
            options["--texconv"] = existing(args.texconv)

    out.parent.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix=".ias-model-", dir=out.parent) as temporary:
        work = Path(temporary)
        options["--out"] = raw = work / "raw"
        command = ["dotnet", "run", "--project", str(TOOL_DIR / "IasModelCli.csproj"), "--", args.kind]
        for key, value in options.items():
            command.extend((key, str(value)))
        subprocess.run(command, cwd=REPO_ROOT, check=True)
        packaged = work / "output"
        pack(raw, packaged, work / "package", model_id, display_name, args.rig_gender)
        if os.path.lexists(out):
            raise FileExistsError(f"Output already exists: {out}")
        packaged.rename(out)

    print(f"Model ID: {model_id}")
    print(out / "IAS_MainMod.zip")
    print(out / COMPANION_FILENAME)


def main() -> int:
    args = parse_args()
    try:
        convert(args)
    except subprocess.CalledProcessError as error:
        print(f"Model extraction exited with code {error.returncode}.", file=sys.stderr)
        return 1
    except (ImportError, KeyError, OSError, RuntimeError, ValueError) as error:
        print(f"Conversion failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
