"""Check patch layout, lossless material payloads and decoded skinning."""
from argparse import ArgumentParser
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
from struct import unpack_from

UNIT = 0xe0a48d0be9a7453f
MATERIAL = 0xeac0b497876adedf
TEXTURE = 0xcd4238c6a0c69e32
BODY = "d2fde496226be96f.patch_0"
HEAD = "59d238094a164c88.patch_0"
COMMON = "9ba626afa44a3aa3.patch_2"


def toc_entries(path, validate=True):
    data = path.read_bytes()
    magic, type_count, entry_count = unpack_from("<III", data)
    assert magic == 0xf0000011, path
    types = dict(unpack_from("<QQ", data, 72 + 32 * i + 8) for i in range(type_count))
    entries = []
    for i in range(entry_count):
        offset = 72 + 32 * type_count + 80 * i
        name, kind, toc, stream, gpu = unpack_from("<5Q", data, offset)
        toc_size, stream_size, gpu_size = unpack_from("<III", data, offset + 56)
        assert toc + toc_size <= len(data), path
        if validate:
            assert not gpu_size or gpu % 64 == 0, (path, name, gpu)
        entries.append(((kind, name), (toc, toc_size), (stream, stream_size), (gpu, gpu_size)))
    if validate:
        assert {kind: count for kind, count in types.items() if count} == Counter(e[0][0] for e in entries), path
    return entries


def payload_hashes(path, entry):
    rows = []
    for suffix, (offset, size) in zip(("", ".stream", ".gpu_resources"), entry[1:]):
        if size == 0:
            rows.append(None)
            continue
        digest = sha256()
        with path.with_name(path.name + suffix).open("rb") as source:
            source.seek(offset)
            while size:
                part = source.read(min(size, 1024 * 1024))
                assert part, (path, entry[0])
                digest.update(part)
                size -= len(part)
        rows.append(digest.hexdigest())
    return rows


def verify(root, source):
    mod = root / "mod"
    parts = {"": {BODY: 9, HEAD: 1}, "coat/on": {BODY: 2}, "coat/off": {BODY: 2},
             "shoes/on": {BODY: 1}, "shoes/off": {BODY: 1}}
    ids = {}
    for folder, archives in parts.items():
        ids[folder] = set()
        for archive, count in archives.items():
            path = mod / folder / archive
            entries = toc_entries(path)
            assert len(entries) == count and all(e[0][0] == UNIT for e in entries), path
            ids[folder].update(e[0][1] for e in entries)
    assert ids["coat/on"] == ids["coat/off"] == {0xe3c2ff7999bd66ad, 0xd438f72d512e082e}
    assert ids["shoes/on"] == ids["shoes/off"] == {0xd939d51b56980ee7}
    assert not ids[""] & (ids["coat/on"] | ids["shoes/on"])
    assert not ids["coat/on"] & ids["shoes/on"]
    assert len(set.union(*ids.values())) == 13

    originals = {}
    for name in ("84fd7aab7cf6f62d.patch_0", COMMON):
        path, = (source / "main").glob(f"**/{name}")
        for entry in toc_entries(path, validate=False):
            originals[entry[0]] = (path, entry)
    bytes_checked = Counter()
    for entry in toc_entries(mod / COMMON):
        key = entry[0]
        assert key[0] in (MATERIAL, TEXTURE), key
        path, original = originals[key]
        assert payload_hashes(mod / COMMON, entry) == payload_hashes(path, original), key
        bytes_checked["textures" if key[0] == TEXTURE else "materials"] += 1

    readback = json.loads((root / "blender/patch-preview.json").read_text())
    counts = Counter()
    max_bones = 0
    max_error = 0.
    max_weights = 0
    four_weights = 0
    for unit in readback["units"]:
        max_bones = max(max_bones, unit["transformCount"])
        path = mod / unit["patch"]
        assert sha256(path.read_bytes()).hexdigest() == unit["patchSha256"].lower()
        for mesh in unit["meshes"]:
            if unit["option"] == "coat/off":
                assert mesh["isHidden"] and len(mesh["positions"]) == 3
                p, q, r = mesh["positions"]
                assert max(sum((a-b)**2 for a, b in zip(x, y)) for x, y in ((p, q), (p, r), (q, r))) < .0002**2
                counts["hidden"] += 1
            else:
                assert not mesh["isHidden"]
                counts["visible"] += 1
                for weights in mesh["weights"]:
                    max_weights = max(max_weights, len(weights))
                    four_weights += len(weights) == 4
                    max_error = max(max_error, abs(sum(w["weight"] for w in weights) - 1))
    assert max_bones <= 256 and max_weights == 4 and counts["hidden"] == 2
    assert max_error <= .002 and four_weights > 0
    return {
        "distinctTargets": 13, "outputEntriesPerFolder": {folder: len(values) for folder, values in ids.items()},
        "lod0Meshes": counts, "maximumTransforms": max_bones, "maximumInfluences": max_weights,
        "verticesWithFourInfluences": four_weights, "maxWeightSumError": max_error,
        "byteIdenticalDependencies": bytes_checked,
    }


if __name__ == "__main__":
    parser = ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("sample", type=Path)
    args = parser.parse_args()
    result = json.dumps(verify(args.output, args.sample), indent=2)
    (args.output / "package-checks.json").write_text(result + "\n", encoding="utf-8")
    print(result)
