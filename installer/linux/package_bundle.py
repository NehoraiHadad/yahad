#!/usr/bin/env python3
"""Build-machine-only Linux runtime candidate. Does not install or execute payloads."""
import argparse
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import struct
import tarfile
import uuid
from urllib.parse import urlsplit

TARGETS = {'linux-x64': (62, 'linux-x64'), 'linux-arm64': (183, 'linux-arm64')}
RUNTIMES = ('runtime/node/bin/node', 'runtime/python/bin/python3', 'runtime/ffmpeg/bin/ffmpeg',
            'runtime/caddy/caddy', 'runtime/openssl/bin/openssl')
REQUIRED = ('app/package.json', 'app/package-lock.json', 'app/src/main.ts', 'app/dist/index.html',
            'app/node_modules/tsx/package.json', 'runtime/runtime-lock.json', 'legal/SBOM.json', 'legal/Node-LICENSE.txt',
            'legal/Python-LICENSE.txt', 'legal/FFmpeg-LICENSE.txt', 'legal/ffmpeg-build.json',
            'legal/Caddy-LICENSE.txt', 'legal/OpenSSL-LICENSE.txt')
MAX_FILES, MAX_BYTES = 50000, 2 * 1024 ** 3

class PackageError(Exception): pass

def safe_file(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > 256 * 1024 ** 2:
        raise PackageError('Nonregular, linked or oversized build input')
    for parent in (path, *path.parents):
        if parent.is_symlink() or getattr(parent.lstat(), 'st_file_attributes', 0) & 0x400:
            raise PackageError('Linked build path refused')
    return info

def inventory(root, prefix=''):
    if root.is_symlink() or not root.is_dir(): raise PackageError('Prepared directory required')
    pending, result = [root], {}
    while pending:
        directory = pending.pop()
        for path in sorted(directory.iterdir()):
            if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
                raise PackageError('Linked build path refused')
            relative = prefix + path.relative_to(root).as_posix()
            parts = relative.split('/')
            if any(p in ('', '.', '..', '.git', 'data', 'backups') or p == '.env' or p.startswith('.env.') for p in parts) or re.search(r'\.(key|pem|sqlite(?:-wal|-shm)?|backup)$', relative, re.I):
                raise PackageError('Private or deployment-state input refused')
            if path.is_dir(): pending.append(path)
            else: safe_file(path); result[relative] = path
            if len(result) > MAX_FILES: raise PackageError('Too many build files')
    return result

def elf(path, machine):
    with path.open('rb') as stream: header = stream.read(64)
    if len(header) != 64 or header[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', header, 16)[0] not in (2, 3) or struct.unpack_from('<H', header, 18)[0] != machine:
        raise PackageError('Expected target-specific 64-bit ELF executable')

def build(prepared, output, target, engine_source):
    if target not in TARGETS: raise PackageError('Unsupported target')
    prepared, output, engine_source = Path(prepared).absolute(), Path(output).absolute(), Path(engine_source).absolute()
    if output.exists(): raise PackageError('Output already exists')
    if output.is_relative_to(prepared) or output.is_relative_to(engine_source): raise PackageError('Output must be outside inputs')
    files = inventory(prepared)
    if any(not name.startswith(('app/', 'runtime/', 'legal/')) for name in files): raise PackageError('Unexpected top-level input')
    machine, esbuild_target = TARGETS[target]
    executable = (*RUNTIMES, f'app/node_modules/@esbuild/{esbuild_target}/bin/esbuild')
    if any(name not in files for name in (*REQUIRED, *executable)): raise PackageError('Missing runtime/app/license dependency')
    for name in executable: elf(files[name], machine)
    for path in files.values():
        with path.open('rb') as stream: magic = stream.read(4)
        if magic == b'\x7fELF': elf(path, machine)
    runtime_lock = json.loads(files['runtime/runtime-lock.json'].read_text(encoding='utf-8'))
    components = runtime_lock.get('components', [])
    if runtime_lock.get('schema') != 1 or runtime_lock.get('target') != target or not isinstance(components, list) or len(components) != len(RUNTIMES) or not all(isinstance(c, dict) for c in components) or {c.get('path') for c in components} != set(RUNTIMES):
        raise PackageError('Exact target runtime lock required')
    for component in components:
        source = urlsplit(component.get('sourceUrl', ''))
        if source.scheme != 'https' or not source.hostname or source.username or source.query or source.fragment or not isinstance(component.get('version'), str):
            raise PackageError('Runtime provenance required')
        if component['path'] == RUNTIMES[0] and not re.fullmatch(r'24\.\d+\.\d+', component['version']):
            raise PackageError('Pinned Node 24 runtime required')
        with files[component['path']].open('rb') as stream: actual_hash = hashlib.file_digest(stream, 'sha256').hexdigest()
        if actual_hash != component.get('sha256'): raise PackageError('Runtime checksum mismatch')
    package = json.loads(files['app/package.json'].read_text(encoding='utf-8'))
    lock = json.loads(files['app/package-lock.json'].read_text(encoding='utf-8'))
    root_lock = lock.get('packages', {}).get('', {})
    if package.get('engines', {}).get('node') != '>=24.0.0 <25' or not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?', package.get('version', '')):
        raise PackageError('Unexpected app runtime contract')
    if root_lock.get('version') != package['version'] or root_lock.get('dependencies') != package.get('dependencies'):
        raise PackageError('App and lockfile disagree')
    for dependency in package.get('dependencies', {}):
        if not re.fullmatch(r'(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+', dependency) or f'app/node_modules/{dependency}/package.json' not in files:
            raise PackageError('Production dependency missing')
    ffmpeg = json.loads(files['legal/ffmpeg-build.json'].read_text(encoding='utf-8'))
    source = urlsplit(ffmpeg.get('sourceUrl', ''))
    if not isinstance(ffmpeg.get('configure'), list) or not all(isinstance(arg, str) for arg in ffmpeg['configure']) or not re.fullmatch('[a-f0-9]{64}', ffmpeg.get('sourceSha256', '')) or source.scheme != 'https' or not source.hostname or source.username or source.query or source.fragment:
        raise PackageError('FFmpeg provenance required')
    if any(arg == '--enable-nonfree' for arg in ffmpeg['configure']): raise PackageError('Nonredistributable FFmpeg candidate')
    if not isinstance(json.loads(files['legal/SBOM.json'].read_text(encoding='utf-8')), dict): raise PackageError('SBOM required')
    engine = inventory(engine_source, 'engine/deploy/')
    if 'engine/deploy/install.py' not in engine or 'engine/deploy/updater.py' not in engine: raise PackageError('Reviewed Linux engine missing')
    files.update(engine)
    records, total = [], 0
    for name, path in sorted(files.items()):
        size = safe_file(path).st_size; total += size
        if total > MAX_BYTES or len(files) > MAX_FILES: raise PackageError('Bundle limit exceeded')
        with path.open('rb') as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest()
        records.append({'path': name, 'size': size, 'sha256': digest,
                        'mode': 0o755 if name in executable else 0o644})
    manifest = {'schema': 1, 'kind': 'native-runtime-candidate', 'target': target, 'version': package['version'],
                'runtimeAcceptance': 'not-performed', 'authority': 'application-updater', 'files': records}
    manifest_bytes = (json.dumps(manifest, sort_keys=True, separators=(',', ':')) + '\n').encode()
    output.parent.mkdir(parents=True, exist_ok=True)
    partial = output.with_name(output.name + '.partial-' + uuid.uuid4().hex)
    try:
        with partial.open('xb') as raw:
            with gzip.GzipFile(filename='', fileobj=raw, mode='wb', mtime=0) as zipped:
                with tarfile.open(fileobj=zipped, mode='w|', format=tarfile.PAX_FORMAT) as archive:
                    def add(name, size, mode, stream):
                        info = tarfile.TarInfo(name); info.size = size; info.mode = mode; info.mtime = 0
                        info.uid = info.gid = 0; info.uname = info.gname = ''
                        archive.addfile(info, stream)
                    add('bundle.json', len(manifest_bytes), 0o644, io.BytesIO(manifest_bytes))
                    for record in records:
                        path = files[record['path']]; safe_file(path)
                        with path.open('rb') as stream:
                            reader = HashReader(stream)
                            add(record['path'], record['size'], record['mode'], reader)
                            if reader.count != record['size'] or reader.digest.hexdigest() != record['sha256']:
                                raise PackageError('Build input changed while packaging')
            raw.flush(); os.fsync(raw.fileno())
        # No overwrite of existing artifact, including a file created during the build.
        os.link(partial, output)
        return manifest
    finally:
        partial.unlink(missing_ok=True)

class HashReader:
    def __init__(self, stream): self.stream, self.digest, self.count = stream, hashlib.sha256(), 0
    def read(self, size=-1):
        value = self.stream.read(size); self.digest.update(value); self.count += len(value); return value

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prepared', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--target', choices=TARGETS, required=True)
    parser.add_argument('--engine-source', type=Path, default=Path(__file__).resolve().parents[2] / 'deploy')
    args = parser.parse_args()
    try:
        result = build(args.prepared, args.output, args.target, args.engine_source)
        print(json.dumps({'kind': result['kind'], 'target': result['target'], 'files': len(result['files']), 'runtimeAcceptance': result['runtimeAcceptance']}))
    except (PackageError, OSError, ValueError):
        raise SystemExit('Candidate packaging refused; existing outputs and live installations were not changed')
