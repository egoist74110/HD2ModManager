"""Build the pair of IAS archives from the manager's extracted resources."""
import argparse
import inspect
import json
import sys
from time import perf_counter
from importlib import import_module
from pathlib import Path
from uuid import UUID


def check_generator(version: str, with_physics: bool) -> None:
    def require(module_name, member, *arguments):
        try:
            function = getattr(import_module(module_name), member)
            inspect.signature(function).bind(*arguments)
            return function
        except (ImportError, AttributeError, TypeError, ValueError) as error:
            raise ValueError(
                f"此 IAS Generator 版本 {version} 的接口有变化："
                f"缺少兼容的 {module_name}.{member}，请等待管理器更新。{error}"
            ) from None

    sample = {(1, 2): (b"toc", b"gpu", b"stream")}
    encode = require("archive_payloads", "encode", sample)
    decode = require("archive_payloads", "decode", b"", b"", b"")
    require("library_catalog", "materialize", None, None, None, None)
    if with_physics:
        require("library_catalog", "physical_nodes", {}, {})
    try:
        if decode(*encode(sample)) != sample:
            raise ValueError("TOC、GPU 或 stream 载荷回读不一致")
    except Exception as error:
        raise ValueError(
            f"此 IAS Generator 版本 {version} 的接口有变化："
            f"缺少可用的 archive_payloads.encode/decode，请等待管理器更新。{error}"
        ) from None


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    for option in ("generator", "aqsdk"):
        parser.add_argument(f"--{option}", type=Path, required=True)
    for option in ("raw", "out", "warnings"):
        parser.add_argument(f"--{option}", type=Path)
    parser.add_argument("--name")
    parser.add_argument("--id", type=UUID)
    parser.add_argument("--rig-gender", choices=("FEMALE", "MALE"))
    parser.add_argument("--generator-version", default="未知")
    parser.add_argument("--check-interface", action="store_true")
    parser.add_argument("--with-physics", action="store_true")
    args = parser.parse_args()
    if not args.check_interface and any(value is None for value in
            (args.raw, args.out, args.warnings, args.name, args.id, args.rig_gender)):
        parser.error("打包需要 --raw、--out、--warnings、--name、--id 和 --rig-gender")

    sys.path[:0] = [
        str(Path(__file__).resolve().parent),
        str(args.generator / "runtime" / "companion"),
        str(args.aqsdk),
    ]
    started = perf_counter()
    try:
        check_generator(args.generator_version, args.with_physics)
        try:
            from package_model import pack, check_sdk
            check_sdk(args.with_physics)
        except (ImportError, AttributeError, TypeError, ValueError) as error:
            raise ValueError(f"AQ SDK 接口不兼容：{error}。请指定包含兼容接口与护甲数据表的 HD2SDK-AQ-Edition。") from None
    except ValueError as error:
        parser.exit(2, str(error) + "\n")
    print(f"IAS_TIMING\tAQ SDK 与 Generator 接口检查\t{round((perf_counter() - started) * 1000)}")
    if args.check_interface:
        return

    warnings = pack(args.raw, args.out, args.raw.parent / "package", args.id,
                    args.name, args.rig_gender, args.with_physics)
    args.warnings.write_text(json.dumps(warnings, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
