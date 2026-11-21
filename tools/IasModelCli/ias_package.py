"""Package a model extracted by HD2ModManager using the official IAS Python runtime."""
import argparse
import sys
from pathlib import Path
from uuid import uuid4


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--raw", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--ias", type=Path, required=True)
    parser.add_argument("--aqsdk", type=Path, required=True)
    parser.add_argument("--name", required=True)
    args = parser.parse_args()

    sys.dont_write_bytecode = True
    sys.path[:0] = [
        str(Path(__file__).resolve().parent),
        str(args.aqsdk),
        str(args.ias / "Generator" / "runtime" / "companion"),
    ]
    from package_model import pack

    pack(args.raw, args.out, args.raw.parent / "package", uuid4(), args.name, "FEMALE")


if __name__ == "__main__":
    main()
