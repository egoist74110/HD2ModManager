"""已发布 IAS 两件套的身体槽位规划与配套改写。仅使用官方 Generator Python。"""
import hashlib
import json
from pathlib import Path
import re
import struct
import sys
import uuid
import zipfile
import zlib

sys.path.insert(0, str(Path(__file__).resolve().parent))
from project_identity import owner_id, point_id

UNIT = 0xe0a48d0be9a7453f
PACKAGE = 0xad9c6d9ed1e5e77a
MATERIALS = {0xeac0b497876adedf, 0xcd4238c6a0c69e32}
RIG = 'HD2IndependentRig/'
HEX = re.compile('[0-9a-f]{16}')
PATCH = re.compile(r'([0-9a-f]{16})\.patch_\d+')


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def sha(blob):
    return hashlib.sha256(blob).hexdigest()


def key(value):
    return f'{int(value, 16):016x}'


def ini_id(value):
    return '0x' + value.upper()


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def save(path, doc):
    path.write_text(json.dumps(doc, ensure_ascii=False, sort_keys=True, indent=2) + '\n', encoding='utf-8')


def write_zip(path, files):
    with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_DEFLATED) as result:
        for name, data in sorted(files.items()):
            result.writestr(zipfile.ZipInfo(name), data, compress_type=zipfile.ZIP_DEFLATED)
    with zipfile.ZipFile(path) as result:
        require(set(result.namelist()) == set(files) and result.testzip() is None, '配套 ZIP 回读失败')


def parse_ini(blob):
    return [tuple(line.split('=', 1)) for line in blob.decode('utf-8-sig').splitlines()
            if line.strip() and not line.startswith(('#', ';'))]


def emit_ini(lines):
    return (''.join(f'{name}={val}\n' for name, val in lines)).encode()


def profile_ids(blob):
    from hd2_system import rig_format as rig
    magic, version, header, source_count, profiles, crc, extensions = rig.HEADER.unpack_from(blob)
    require((magic, version, header) == (rig.MAGIC, rig.VERSION, rig.HEADER.size) and not extensions & ~3 and
            zlib.crc32(blob[header:]) == crc, 'Rig 版本或 CRC 不符')
    at = header + source_count * rig.SOURCE.size
    result = {}
    for _ in range(profiles):
        uid, count, needle_bytes, _, palette, flags = rig.PROFILE.unpack_from(blob, at)
        start = at + rig.PROFILE.size
        end = start + count * rig.TARGET.size
        require(uid not in result and end <= len(blob), 'Rig profile 重复或截断')
        result[uid] = (at, hashlib.sha256(struct.pack('<QIII', uid, palette, flags, count) +
                                         blob[start:end]).digest())
        at = end + needle_bytes
    if extensions & 1:
        magic, grips, reserved = struct.unpack_from('<8sII', blob, at)
        require(magic == b'HD2GRIP1' and reserved == 0 and grips > 0, 'Rig 抓握扩展不符')
        at += 16 + grips * rig.GRIP.size
    require(at == len(blob), 'Rig profile 尾部不符')
    return result


def with_id(blob, at, old, new):
    require(struct.unpack_from('<Q', blob, at)[0] == old, '配套的 Unit 身份不符')
    out = bytearray(blob)
    struct.pack_into('<Q', out, at, new)
    return out


def relocate_pose(blob, old_unit, new_unit, old_digest, new_digest):
    from hd2_system import clothing_pose
    head = clothing_pose.HEADER.unpack_from(blob)
    require(head[:4] == (clothing_pose.MAGIC, 1, clothing_pose.HEADER.size, len(blob)) and
            head[4] == zlib.crc32(blob[head[2]:]) and head[8] == old_digest, 'Pose 版本、CRC 或 Rig 摘要不符')
    out = with_id(blob, 32, old_unit, new_unit)
    out[40:72] = new_digest
    return bytes(out)


def reference(name, blob):
    from hd2_system.shared_physics import REFERENCE
    return REFERENCE.pack(name.encode(), len(blob), hashlib.sha256(blob).digest())


def relocate_preview(blob, project, old, target, files, renamed, profiles):
    from hd2_system import preview_transport as preview
    h = list(preview.HEADER.unpack_from(blob))
    require(h[:4] == [b'HD2PRV1\0', 1, preview.HEADER.size, len(blob)] and
            h[4] == zlib.crc32(blob[h[2]:]) and h[-2].rstrip(b'\0') == project.encode() and
            h[-1] == int(old, 16), 'Preview 版本、CRC 或原 Archive 不符')
    name = preview.REFERENCE.unpack_from(blob, h[2])[0].rstrip(b'\0').decode('ascii')
    start = h[2] + preview.REFERENCE.size
    require(name in renamed and blob[h[2]:start] == reference(name, files[name]), 'Preview 的 Rig 引用不符')
    new_name = renamed[name]
    output = bytearray(blob)
    output[h[2]:start] = reference(new_name, files[name])
    at = start
    for _ in range(h[5]):
        row = preview.SPEC.unpack_from(output, at)
        require(row[0] == h[-1] and row[1] in profiles, 'Preview 引用了非作者 Unit')
        struct.pack_into('<Q', output, at, int(target, 16))
        at += preview.SPEC.size + row[-1] * preview.BONE.size
    require(at == len(blob), 'Preview 尾部不符')
    h[-1] = int(target, 16)
    h[4] = zlib.crc32(output[h[2]:])
    return preview.HEADER.pack(*h) + output[h[2]:]


def relocate_shared(files, project, old, target, native, authored):
    from hd2_system.shared_physics import CONSUMER, HEADER, REFERENCE, SELECTION, MAGIC
    from ias_physics.format_v1 import parse
    def identity(archive):
        raw = project.encode().ljust(64, b'\0') + struct.pack('<Q', int(archive, 16))
        uid = int.from_bytes(hashlib.sha256(b'HD2_SHARED_CANONICAL_V1\0' + raw).digest()[:8], 'little')
        return raw, 'shared_' + sha(raw)[:24], uid

    before, stem, old_uid = identity(old)
    after, new_stem, new_uid = identity(target)
    shared_name = stem + '.hd2shared'
    blob = files[shared_name]
    h = list(HEADER.unpack_from(blob))
    version = h[1]
    require(h[0] == MAGIC and version in (2, 3) and h[2:4] == [HEADER.size, len(blob)] and h[5] == 1 and
            (h[6] == 0 if version == 2 else h[6] in (2, 3)) and blob[32:112] == before + struct.pack('<Q', old_uid)
            and h[4] == zlib.crc32(blob[HEADER.size:]) and
            h[10] == hashlib.sha256(blob[32:112] + blob[HEADER.size:]).digest(),
            '共享物理版本、CRC 或旧身份不符')
    require(new_uid and new_uid not in authored, '目标 canonical ID 与作者 Unit 冲突')
    rewritten, renamed, refs = {}, {}, []
    suffixes = ['.canonical.rigbin', '.canonical.hd2phys', '.consumers.rigbin']
    if version == 3:
        suffixes.append('.canonical.hd2pose')
    for index, suffix in enumerate(suffixes):
        name, new_name = stem + suffix, new_stem + suffix
        at = HEADER.size + index * REFERENCE.size
        ref = blob[at:at + REFERENCE.size]
        if index == 1 and version == 3 and not h[6] & 1:
            require(ref == bytes(REFERENCE.size) and name not in files, '共享物理空引用不符')
            refs.append(ref)
            continue
        original = files[name]
        require(ref == reference(name, original), '共享物理文件长度或 SHA 不符')
        out = original
        if index == 0:
            profiles = profile_ids(original)
            require(set(profiles) == {old_uid}, '共享 canonical Rig profile 不符')
            out = with_id(original, profiles[old_uid][0], old_uid, new_uid)
            struct.pack_into('<I', out, 24, zlib.crc32(out[32:]))
            old_digest, new_digest = profiles[old_uid][1], profile_ids(out)[new_uid][1]
        elif index == 1:
            out = with_id(original, 24, old_uid, new_uid)
            require(parse(original) | {'unit_id': f'{new_uid:016x}'} == parse(out), '共享物理除 unit_id 外有变化')
        elif index == 2:
            consumers = profile_ids(original)
            require(set(consumers) <= authored, '共享 Rig 引用了非作者 Unit')
        elif index == 3:
            out = relocate_pose(original, old_uid, new_uid, old_digest, new_digest)
        rewritten[new_name] = bytes(out)
        renamed[name] = new_name
        refs.append(reference(new_name, out))
    rows = bytearray(blob[HEADER.size + len(refs) * REFERENCE.size:])
    at = 0
    seen = set()
    sources = set()
    for _ in range(h[11]):
        published, slot, count, reserved, *_ = CONSUMER.unpack_from(rows, at)
        source, _choice = SELECTION.unpack_from(rows, at + CONSUMER.size)
        source = f'{source:016x}'
        require(published in consumers and published not in seen and slot < 7 and reserved == 0 and
                source in native, '共享消费者缺少作者 Unit 或原版路由')
        seen.add(published)
        sources.add(source)
        struct.pack_into('<Q', rows, at + CONSUMER.size, int(native[source], 16))
        at += CONSUMER.size + SELECTION.size + count * 4
    require(at == len(rows) and seen == set(consumers), '共享消费者表不符')
    payload = b''.join(refs) + rows
    h[4] = zlib.crc32(payload)
    h[8:11] = [int(target, 16), new_uid,
               hashlib.sha256(after + struct.pack('<Q', new_uid) + payload).digest()]
    rewritten[new_stem + '.hd2shared'] = HEADER.pack(*h) + payload
    renamed[shared_name] = new_stem + '.hd2shared'
    guard = f'.requires-shared-v{version}.rigbin'
    require(files[stem + guard] == f'HD2SHARED_REQUIRES_V{version}\0'.encode(), '共享物理版本 guard 不符')
    rewritten[new_stem + guard] = files[stem + guard]
    renamed[stem + guard] = new_stem + guard
    return rewritten, renamed, set(consumers), sources


class Catalog:
    def __init__(self, meta):
        self.stock = {row['archive']: row for row in load(meta / 'stock-assembly.json')['stock_contracts']
                      if row['domain'] == 'body'}
        self.bindings = load(meta / 'bindings.json')['targets']
        self.equipment = load(meta / 'equipment.json')
        self.sources = load(meta / 'source_catalog.json')['stock_source_archives']
        self.native = {uid for row in self.bindings.values() for item in row['equipment'] for uid in item['unit_ids']}

    def slots(self, archive):
        require(archive in self.stock and archive in self.bindings, '当前 IAS 目录缺少身体装备闭包')
        stock = self.stock[archive]
        equipment, = self.bindings[archive]['equipment']
        require(any(row['archive'] == archive and row['key'] == equipment['key']
                    for row in self.equipment['body']), '装备与身体 Archive 不符')
        units = {row['source']: row for row in stock['units']}
        slots = {}
        for recipe in stock['recipes']:
            require(recipe['key'] == equipment['key'], '原版 recipe 对应多套外观')
            for part in recipe['parts']:
                pos = (recipe['shape'], part['cell'], part['variant'])
                uid = part['resource']
                require(pos not in slots and uid in units and part['nodes'] == units[uid]['source_nodes'],
                        '原版槽位重复或节点数不符')
                slots[pos] = uid
        require(slots and set(slots.values()) <= set(equipment['unit_ids']), '原版 recipe 超出装备闭包')
        return equipment, slots, units


def patch_keys(path):
    from collections import Counter
    with path.open('rb') as stream:
        header = stream.read(72)
        magic, types, count = struct.unpack_from('<III', header)
        require(magic == 0xf0000011 and 0 < types <= 64 and 0 < count <= 1000000, '主包 patch 格式不支持')
        table = [struct.unpack('<4Q', stream.read(32)) for _ in range(types)]
        require(all(zero == 0 and flags == 0x0000004000000010 for zero, _, _, flags in table),
                '主包 patch 类型表格式不支持')
        minimum = 72 + types * 32 + count * 80
        limits = [path.stat().st_size] + [(Path(str(path) + suffix).stat().st_size
                  if Path(str(path) + suffix).is_file() else 0) for suffix in ('.stream', '.gpu_resources')]
        rows = []
        for _ in range(count):
            uid, kind, toc, side, gpu, size, side_size, gpu_size = struct.unpack('<5Q16x3I12x', stream.read(80))
            require(toc >= minimum and toc + size <= limits[0] and size > 0 and
                    (not side_size or side + side_size <= limits[1]) and
                    (not gpu_size or gpu + gpu_size <= limits[2]), '主包 patch 载荷偏移越界')
            rows.append((uid, kind))
    require(len({kind for _, kind, _, _ in table}) == types and
            Counter(kind for _, kind in rows) == {kind: number for _, kind, number, _ in table}, '主包 patch 类型计数不符')
    require(len(set(rows)) == count, 'patch 包含重复资源键')
    return rows


class Pair:
    def __init__(self, source, catalog, work):
        from hd2_system import runtime_group
        self.catalog = catalog
        self.main = source / 'main'
        self.companion = source / 'companion'
        manifest_path = self.main / 'manifest.json'
        self.manifest = load(manifest_path) if manifest_path.is_file() else None
        author = self.manifest.get('Author') if isinstance(self.manifest, dict) else None
        self.author = author.strip() if isinstance(author, str) and author.strip() else None
        self.group = load(self.companion / 'mod_group.json')
        require(self.group['schema'] == 'HD2RuntimeGroup1' and re.fullmatch('[0-9a-f]{32}', self.group['id']),
                '配套组版本或身份不支持')
        self.members = {}
        self.files = {}
        self.icons = {}
        for index, member in enumerate(self.group['members']):
            archive, project = member['archive'], member['project']
            require(HEX.fullmatch(archive) and archive not in self.members and re.fullmatch('[0-9a-f]{32}', project),
                    '组成员身份无效')
            files = {}
            for entry in member['files']:
                path = entry['path']
                require(path.startswith(RIG) and runtime_group.LEAF.fullmatch(path[len(RIG):]) and
                        path.lower() not in self.files and not path.endswith('.hd2morph'), '存在重复路径或不支持的 Morph 配套')
                data = (self.companion / path).read_bytes()
                require(sha(data) == entry['sha256'], '原配套组 SHA 不符')
                files[path] = data
                self.files[path.lower()] = data
            member_path = work / f'member-{index}.zip'
            write_zip(member_path, files)
            read = runtime_group.read_member(member_path)
            require((read['project'], read['archive'], read['files']) == (project, archive, files), 'SDK 原成员回读不符')
            self.members[archive] = project, files
        for path in self.companion.rglob('*'):
            if not path.is_file():
                continue
            name = path.relative_to(self.companion).as_posix()
            if name in runtime_group.mod_icon.MEMBER_NAMES:
                require(not self.icons, '配套存在多个图标')
                self.icons[name] = runtime_group.mod_icon.validate_member(name, path.read_bytes())
            else:
                require(name == 'mod_group.json' or name.lower() in self.files, '配套组有未知文件')
        body = [archive for archive in self.members if archive in catalog.stock]
        helmets = {row['archive'] for row in catalog.equipment['helmet']}
        require(len(body) == 1 and set(self.members) - set(body) <= helmets, '只支持一套已知身体及原头盔配套')
        self.old, = body
        self.project, body_files = self.members[self.old]
        self.body = {name[len(RIG):]: blob for name, blob in body_files.items()}
        self.equipment, self.old_slots, self.old_units = catalog.slots(self.old)
        owner_name = owner_id(self.project, self.old) + '.isolation.ini'
        self.isolation = parse_ini(self.body[owner_name])
        iso = dict(self.isolation)
        require(iso.get('isolation_version') == '1' and iso.get('isolation_id') == owner_name.removesuffix('.isolation.ini') and
                iso.get('domain') == 'body' and key(iso['archive']) == self.old and
                [int(v, 16) for n, v in self.isolation if n == 'equipment'] == [int(self.equipment['key'], 16)] and
                set(iso) <= {'isolation_version', 'isolation_id', 'domain', 'archive', 'equipment', 'unit', 'rig_profile'},
                '原身体隔离版本、域或外观不符')
        routes = [tuple(map(key, val.split(','))) for name, val in self.isolation if name == 'unit']
        self.routes = dict(routes)
        require(len(routes) == len(self.routes) == len(set(self.routes.values())) and
                set(self.routes) == set(self.old_slots.values()), '原身体隔离与原版槽位闭包不符')
        self.points = {uid for uid, alias in routes if point_id(self.project, self.old, int(uid, 16)) == int(alias, 16)}
        self.point_aliases = {int(self.routes[uid], 16) for uid in self.points}
        self.patch, self.authored, self.has_package = self.read_main()
        self.difference = []
        self.difference_name = None
        self.visible_uses = set(self.routes) - self.points
        self.read_difference()
        require(all(int(v, 16) in self.authored for n, v in self.isolation if n == 'rig_profile'),
                '隔离 Rig 引用了非作者 Unit')
        self.relocate_companions(self.old, {uid: uid for uid in self.routes})
        for archive, (project, files) in self.members.items():
            if archive == self.old:
                continue
            rig = f'{RIG}{project}.{archive}.rigbin'
            profiles = profile_ids(files[rig])
            from ias_physics.format_v1 import parse
            for path, blob in files.items():
                if path.endswith('.hd2phys'):
                    uid = parse(blob)['unit_id']
                    require(int(uid, 16) in profiles and path.endswith(f'.{uid}.hd2phys'), '原头盔物理版本或身份不符')
                elif path.endswith('.hd2pose'):
                    uid = int(path.split('.')[-2], 16)
                    require(uid in profiles, '原头盔 Pose 缺少 Rig profile')
                    relocate_pose(blob, uid, uid, profiles[uid][1], profiles[uid][1])
                elif path.endswith('.difference.ini'):
                    require(dict(parse_ini(blob)).get('manifest_version') == '6', '头盔含不支持的 GROUP 或未知差分')
                elif path.endswith('.isolation.ini'):
                    require(dict(parse_ini(blob)).get('isolation_version') == '1', '头盔隔离版本不符')
                elif path.endswith('.hd2preview'):
                    relocate_preview(blob, project, archive, archive, {Path(p).name: b for p, b in files.items()},
                                     {Path(rig).name: Path(rig).name}, set(profiles))
                else:
                    require(path == rig or path == f'{RIG}{project}.{archive}.hd2author' or
                            (path.endswith('.requires-clothing-v1.rigbin') and blob == b'HD2POSE_REQUIRES_V1\0'),
                            '头盔存在未知配套格式或版本')

    def read_main(self):
        body = []
        for path in self.main.rglob('*'):
            if not path.is_file():
                continue
            match = PATCH.fullmatch(path.name)
            require(match or '.patch_' not in path.name or
                    (path.suffix in ('.gpu_resources', '.stream') and
                     path.with_suffix('').is_file() and PATCH.fullmatch(path.stem)), '主包存在未知 patch 文件')
            if not match:
                continue
            archive, = match.groups()
            archive_id = int(archive, 16)
            rows = patch_keys(path)
            require(all(typ in MATERIALS or (typ == UNIT and archive in self.members) or
                        (typ == PACKAGE and uid == archive_id and archive in self.members) for uid, typ in rows),
                    '主包含不支持的 Composite 或未知资源类型')
            require(all(typ != UNIT or f'{uid:016x}' not in self.catalog.native for uid, typ in rows),
                    '主包覆盖原版 Unit ID')
            if archive == self.old:
                require(all(typ == UNIT or (typ == PACKAGE and uid == archive_id) for uid, typ in rows),
                        '身体 patch 混有非 Unit/Package 资源')
                has_package = any(typ == PACKAGE for _, typ in rows)
                body.append((path, {uid for uid, typ in rows if typ == UNIT}, has_package))
        require(len(body) == 1, '只支持单个隔离身体 patch')
        patch, all_units, has_package = body[0]
        require({int(v, 16) for v in self.routes.values()} <= all_units, '身体 patch 的隔离 Unit 悬空')
        return patch, all_units - self.point_aliases, has_package

    def read_difference(self):
        from hd2_system.runtime_manifest import _stable_label
        names = [name for name in self.body if name.endswith('.difference.ini')]
        if not names:
            require(self.authored == {int(v, 16) for k, v in self.routes.items() if k not in self.points},
                    '身体 patch 有未声明的 Unit')
            return
        stem = _stable_label('hd2free', self.project, self.old, digits=16)
        require(names == [stem + '.difference.ini'], '只支持 FREE v6，GROUP 差分不支持')
        self.difference_name = names[0]
        self.difference = parse_ini(self.body[self.difference_name])
        doc = dict(self.difference)
        require(doc.get('manifest_version') == '6' and doc.get('difference_logic') == 'free_parts' and
                doc.get('manifest_id') == stem and doc.get('domain') == 'body' and key(doc['archive']) == self.old and
                int(doc['equipment'], 16) == int(self.equipment['key'], 16), '只支持匹配源身体的 FREE v6')
        used = {int(v, 16) for k, v in self.routes.items() if k not in self.points}
        for n, v in self.difference:
            if n == 'free_route':
                _, _, source, default, selected = v.split(',')
                require(self.routes.get(key(source)) == key(default) and int(selected, 16) in self.authored
                        and int(default, 16) in self.authored, 'FREE 差分原版、默认或选择 Unit 悬空')
                self.visible_uses.add(key(source))
                used.add(int(selected, 16))
            elif n == 'free_rig_profile':
                require(int(v.split(',')[-1], 16) in self.authored, 'FREE Rig profile 悬空')
            else:
                require(n in {'manifest_version', 'manifest_id', 'difference_logic', 'domain', 'archive', 'equipment',
                              'free_part', 'free_part_display_name', 'free_option', 'free_option_display_name'},
                        '存在未知 FREE v6 指令')
        require(used <= self.authored, '身体 patch 缺少已声明的作者 Unit')

    def plan(self, target):
        equipment, slots, units = self.catalog.slots(target)
        targets, reverse = {}, {}
        for pos in sorted(self.old_slots.keys() & slots.keys()):
            before, after = self.old_slots[pos], slots[pos]
            require(targets.setdefault(before, after) == after and reverse.setdefault(after, before) == before,
                    '目标体型合并／拆分')
        require(all(pos in slots or self.old_slots[pos] not in self.visible_uses for pos in self.old_slots),
                '目标缺少作者可见部件或差分槽')
        require(not slots.keys() - self.old_slots.keys(), '目标多出语义未经确认的原版槽')
        require(set(targets.values()) <= set(equipment['unit_ids']), '目标路由超出官方装备闭包')
        donors = []
        for uid in sorted(set(slots.values())):
            row = units[uid]
            archive = self.catalog.sources.get(uid, target)
            require(isinstance(archive, str) and HEX.fullmatch(archive), '原版 Unit 来源 Archive 无效')
            point = f'{point_id(self.project, target, int(uid, 16)):016x}' if reverse[uid] in self.points else None
            donors.append(dict(Unit=uid, Archive=archive, Sha=row['source_sha256'], Nodes=row['source_nodes'], Point=point))
        return equipment, targets, donors

    def relocate_companions(self, target, native):
        from ias_physics.format_v1 import parse
        base = f'{self.project}.{self.old}'
        new_base = f'{self.project}.{target}'
        shared = [name for name in self.body if name.endswith('.hd2shared')]
        files, renamed = {}, {}
        if shared:
            require(len(shared) == 1, '存在多套共享身体物理')
            files, renamed, profiles, sources = relocate_shared(self.body, self.project, self.old, target, native, self.authored)
            self.visible_uses.update(sources)
        else:
            name = base + '.rigbin'
            blob = self.body[name]
            ordinary = profile_ids(blob)
            profiles = set(ordinary)
            require(profiles <= self.authored, '普通 Rig 引用了非作者 Unit')
            new_name = new_base + '.rigbin'
            renamed[name] = new_name
            files[new_name] = blob
            for name, blob in self.body.items():
                match = re.fullmatch(re.escape(base) + r'\.([A-Za-z0-9_]+)\.([0-9a-f]{16})\.hd2phys', name)
                pose_match = re.fullmatch(re.escape(base) + r'\.([0-9a-f]{16})\.(hd2pose|requires-clothing-v1\.rigbin)', name)
                if pose_match:
                    uid, file_type = pose_match.groups()
                    unit = int(uid, 16)
                    require(unit in ordinary, 'Pose 引用了非作者 Unit')
                    if file_type == 'hd2pose':
                        digest = ordinary[unit][1]
                        relocate_pose(blob, unit, unit, digest, digest)
                        require(base + f'.{uid}.requires-clothing-v1.rigbin' in self.body, 'Pose 缺少版本 guard')
                    else:
                        require(blob == b'HD2POSE_REQUIRES_V1\0' and base + f'.{uid}.hd2pose' in self.body,
                                'Pose 版本 guard 不符')
                    new_name = f'{new_base}.{uid}.{file_type}'
                    renamed[name] = new_name
                    files[new_name] = blob
                    continue
                if not match:
                    continue
                slot, uid = match.groups()
                unit = int(uid, 16)
                require(unit in ordinary and parse(blob)['unit_id'] == uid, '普通物理 unit_id 或 Rig profile 不符')
                new_name = f'{new_base}.{slot}.{uid}.hd2phys'
                renamed[name] = new_name
                files[new_name] = blob
        preview_name = base + '.hd2preview'
        if preview_name in self.body:
            new_name = new_base + '.hd2preview'
            files[new_name] = relocate_preview(self.body[preview_name], self.project, self.old,
                                               target, self.body, renamed, profiles)
            renamed[preview_name] = new_name
        known = set(renamed) | {base + '.hd2author', owner_id(self.project, self.old) + '.isolation.ini',
                               self.difference_name}
        require(self.body.keys() <= known, '身体存在未知配套格式或版本')
        return files

    def build_companion(self, target, equipment, native, work):
        from hd2_system import runtime_group
        from hd2_system.runtime_manifest import _stable_label
        body = self.relocate_companions(target, native)
        owner = owner_id(self.project, target)
        iso = [('isolation_version', '1'), ('isolation_id', owner), ('domain', 'body'),
               ('archive', ini_id(target)), ('equipment', ini_id(equipment['key']))]
        points = {after: f'{point_id(self.project, target, int(after, 16)):016x}'
                  for before, after in native.items() if before in self.points}
        routes = {after: points.get(after, self.routes[before]) for before, after in native.items()}
        iso.extend(('unit', f'{ini_id(uid)},{ini_id(alias)}') for uid, alias in sorted(routes.items()))
        iso.extend((name, val) for name, val in self.isolation if name == 'rig_profile')
        body[owner + '.isolation.ini'] = emit_ini(iso)
        if self.difference:
            stem = _stable_label('hd2free', self.project, target, digits=16)
            changes = dict(manifest_id=stem, archive=ini_id(target), equipment=ini_id(equipment['key']))
            free = []
            for name, val in self.difference:
                if name == 'free_route':
                    fields = val.split(',')
                    fields[2] = ini_id(native[key(fields[2])])
                    val = ','.join(fields)
                free.append((name, changes.get(name, val)))
            body[stem + '.difference.ini'] = emit_ini(free)
        group_id = uuid.uuid5(uuid.NAMESPACE_URL, f"hd2mm/retarget/{self.group['id']}/body/{target}")
        name = self.group['name'][:65] + '（由管理器换护甲）'
        group_files = dict(self.icons)
        members = []
        for archive, (project, payload) in self.members.items():
            if archive == self.old:
                archive, payload = target, {RIG + name: blob for name, blob in body.items()}
            if archive == target:
                test = work / 'retargeted-member.zip'
                write_zip(test, payload)
                check = runtime_group.read_member(test)
                require((check['project'], check['archive'], check['files']) == (project, archive, payload),
                        'SDK 成员回读不符')
            require(group_files.keys().isdisjoint(payload), '目标配套路径与原头盔冲突')
            group_files.update(payload)
            members.append(dict(archive=archive, project=project,
                                files=[dict(path=p, sha256=sha(b)) for p, b in sorted(payload.items())]))
        # SDK write_group 使用当前时间。保留官方 schema，并固定 ZIP 时间和清单时间，保证可复现。
        group = dict(schema='HD2RuntimeGroup1', id=group_id.hex, name=name,
                     exported_at=self.group.get('exported_at', '1980-01-01T00:00:00+00:00'),
                     members=sorted(members, key=lambda item: (item['project'], item['archive'])))
        group_files['mod_group.json'] = (json.dumps(group, ensure_ascii=False, sort_keys=True, indent=2) + '\n').encode()
        result = work / 'IAS_Companion.hd2irpack.zip'
        write_zip(result, group_files)
        with zipfile.ZipFile(result) as zipped:
            read = json.loads(zipped.read('mod_group.json'))
            require(read == group and all(sha(zipped.read(row['path'])) == row['sha256']
                    for item in read['members'] for row in item['files']), '新配套组 SHA 回读不符')
        if isinstance(self.manifest, dict):
            manifest = dict(self.manifest)
            author_note = f'原作者：{self.author}。' if self.author else ''
            base_name = manifest.get('Name') or self.group['name']
            manifest.update(Guid=str(group_id), Name=f"{base_name}（由管理器换护甲）",
                            Description=f"{author_note}由管理器换护甲。\n{manifest.get('Description', '')}".rstrip())
            save(work / 'manifest.json', manifest)


def main():
    request = load(Path(sys.argv[1]))
    work = Path(sys.argv[1]).parent
    sys.path[:0] = [request['Sdk'], str(Path(request['Generator']) / 'runtime/companion')]
    result = {}
    try:
        pair = Pair(Path(request['Source']), Catalog(Path(request['Metadata'])), work)
        _, _, baseline = pair.plan(pair.old)
        result.update(OriginalArchive=pair.old, Author=pair.author, Archives=sorted(pair.members), SourceStock=baseline,
                      Patch=pair.patch.relative_to(pair.main).as_posix(),
                      OldPoints=[f'{uid:016x}' for uid in sorted(pair.point_aliases)],
                      HasPackage=pair.has_package)
        fits, stock = {}, {}
        for target in request['Targets']:
            if target == pair.old:
                fits[target] = None
                continue
            try:
                item, native, donors = pair.plan(target)
                pair.relocate_companions(target, native)
                stock[target] = donors
                fits[target] = None
                if request['Build'] == target:
                    pair.build_companion(target, item, native, work)
                    result['Donors'] = donors
            except (ValueError, KeyError, IndexError, struct.error) as error:
                fits[target] = str(error) if isinstance(error, ValueError) else f'目标闭包或配套格式不匹配（{type(error).__name__}: {error}）'
        result.update(Fits=fits, Stock=stock)
    except (ValueError, OSError, KeyError, TypeError, IndexError, ImportError, struct.error) as error:
        result['Error'] = str(error) if isinstance(error, ValueError) else f'缺少兼容的官方目录或配套格式（{type(error).__name__}: {error}）'
    save(work / 'result.json', result)


if __name__ == '__main__':
    main()
