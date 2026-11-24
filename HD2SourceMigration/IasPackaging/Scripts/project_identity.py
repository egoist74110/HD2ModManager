"""Project-scoped names and routes for the IAS companion format."""

import hashlib


def project_filename(project: str, archive: str, suffix: str) -> str:
    return f"{project}.{archive}.{suffix}"


def owner_id(project: str, archive: str) -> str:
    return "owned_" + hashlib.blake2s(f"{project}\0{archive}".encode(), digest_size=12).hexdigest()


def point_id(project: str, archive: str, source: int) -> int:
    data = f"HD2ArmorPoint1\0{project}\0{archive}\0{source}".encode()
    return int.from_bytes(hashlib.blake2b(data, digest_size=8, person=b"HD2Point").digest(), "little")
