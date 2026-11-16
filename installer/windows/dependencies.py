"""Offline, pinned private vendor inputs. Never downloads or executes an input."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import zipfile


class DependencyError(ValueError): pass


# Yahad's own attribution beside the pinned Caddy files (not an upstream file).
CADDY_NOTICES = ('Yahad packaging attribution; this is not an upstream NOTICE file.\n'
    'Caddy: https://github.com/caddyserver/caddy — Apache-2.0, included Caddy-LICENSE.txt.\n'
    'Upstream README is included as Caddy-README.md. Transitive license inventory/review remains required before distribution.\n').encode('utf-8')


def digest(path, algorithm='sha256'):
    with Path(path).open('rb') as source: return hashlib.file_digest(source, algorithm).hexdigest()


def pins():
    return json.loads(Path(__file__).with_name('dependency-pins.json').read_text(encoding='utf-8'))


def proof(spec):
    # The staged vendor closure only: Node is pinned beside it (node_component) but
    # comes from the build's own Node directory, never from the vendor staging.
    staged = {key:spec[key] for key in ('schema', 'target', 'sources')}
    encoded = json.dumps(staged, sort_keys=True, separators=(',', ':')).encode()
    return {'schema':1, 'kind':'pinned-private-dependencies', 'target':'win-x64',
        'sourceSpecSha256':hashlib.sha256(encoded).hexdigest(), 'familyInstallationReady':False,
        'sources':spec['sources']}


def component(name, spec):
    source = spec['sources'][name]
    executable = next(f for f in source['files'] if f['path'] == f'runtime/{name}/{name}.exe')
    return {'id':name, 'path':executable['path'], 'sha256':executable['sha256'],
        'version':source['version'], 'origin':source['origin'], 'publisher':source['publisher'],
        'sourceUrl':source['archive']['url'], 'archiveSha256':source['archive']['sha256'],
        'license':source['license']}


def verify_inputs(archive, checksums, source):
    archive, checksums = Path(archive), Path(checksums)
    if digest(archive) != source['archive']['sha256']: raise DependencyError('ARCHIVE_CHECKSUM')
    if digest(checksums) != source['checksums']['sha256']: raise DependencyError('CHECKSUM_FILE_CHECKSUM')
    expected = digest(archive, source['checksums']['algorithm'])
    records = []
    for line in checksums.read_text(encoding='utf-8-sig').splitlines():
        columns = line.split()
        if len(columns) == 2 and columns[1].lstrip('*') == source['archive']['name']: records.append(columns[0])
    if records != [expected]: raise DependencyError('PUBLISHER_CHECKSUM_RECORD')
    with zipfile.ZipFile(archive) as opened:
        infos = opened.infolist()
        if len(infos) > 4096 or sum(i.file_size for i in infos) > 1024**3: raise DependencyError('ARCHIVE_BOUNDS')
        seen = set()
        for info in infos:
            name = info.filename.rstrip('/')
            parts = name.split('/')
            if not name or name.startswith('/') or '\\' in name or len(name)>240 or any(p in ('', '.', '..') or ':' in p or p.endswith(('.', ' ')) or re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?',p) for p in parts): raise DependencyError('ARCHIVE_PATH')
            if name.casefold() in seen: raise DependencyError('ARCHIVE_CASE_COLLISION')
            seen.add(name.casefold())
            mode = stat.S_IFMT(info.external_attr >> 16)
            if mode not in (0, stat.S_IFDIR if info.is_dir() else stat.S_IFREG): raise DependencyError('ARCHIVE_LINK')
        lookup = {i.filename:i for i in infos if not i.is_dir()}
        for record in source['files']:
            info = lookup.get(record['archivePath'])
            if info is None or info.file_size != record['size']: raise DependencyError('PINNED_MEMBER_MISSING')
            with opened.open(info) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != record['sha256']: raise DependencyError('PINNED_MEMBER_CHECKSUM')


def validate_staged(files, spec=None):
    spec = pins() if spec is None else spec
    try:
        recorded = json.loads(Path(files['legal/dependency-proof.json']).read_text(encoding='utf-8'))
        if recorded != proof(spec): raise DependencyError('DEPENDENCY_PROVENANCE')
        required = {r['path']:r for s in spec['sources'].values() for r in s['files']}
        runtime = {name for name in files if name.startswith(tuple(f'runtime/{source}/' for source in spec['sources']))}
        if runtime != {name for name in required if name.startswith('runtime/')}: raise DependencyError('DEPENDENCY_FILE_SET')
        for name, record in required.items():
            path = Path(files[name])
            if path.stat().st_size != record['size'] or digest(path) != record['sha256']: raise DependencyError('DEPENDENCY_FILE_CHECKSUM')
    except (KeyError, TypeError, OSError, json.JSONDecodeError) as error:
        raise DependencyError('DEPENDENCY_INPUT') from error
    return {name:component(name,spec) for name in spec['sources']}


def add_license_texts(output):
    """Build-only new legal files; refuses overwriting any existing artifact."""
    output=Path(output); source_root=Path(__file__).with_name('licenses')
    records=json.loads((source_root/'sources.json').read_text(encoding='utf-8'))
    for record in records:
        source=source_root/Path(record['path']).name
        if source.stat().st_size!=record['size'] or digest(source)!=record['sha256']: raise DependencyError('LICENSE_SOURCE_CHECKSUM')
        target=output/record['path']; target.parent.mkdir(parents=True,exist_ok=True)
        with source.open('rb') as source_stream,target.open('xb') as destination: shutil.copyfileobj(source_stream,destination)
    with (output/'legal/supplement-sources.json').open('x',encoding='utf-8',newline='\n') as destination: json.dump(records,destination,indent=2)


def stage(archives, checksum_files, output, spec=None):
    spec = pins() if spec is None else spec
    output = Path(output).absolute()
    if output.exists(): raise DependencyError('NEW_OUTPUT_REQUIRED')
    # Verify every complete input and every selected member before any output.
    for name, source in spec['sources'].items(): verify_inputs(archives[name], checksum_files[name], source)
    output.mkdir(parents=True, exist_ok=False)
    for name, source in spec['sources'].items():
        with zipfile.ZipFile(archives[name]) as archive:
            for record in source['files']:
                relative = PurePosixPath(record['path'])
                if relative.is_absolute() or '..' in relative.parts: raise DependencyError('PIN_PATH')
                target = output/record['path']; target.parent.mkdir(parents=True, exist_ok=True)
                hashed = hashlib.sha256(); size = 0
                with archive.open(record['archivePath']) as source_stream, target.open('xb') as destination:
                    while chunk := source_stream.read(1024*1024):
                        size += len(chunk)
                        if size > record['size']: raise DependencyError('INPUT_CHANGED')
                        hashed.update(chunk); destination.write(chunk)
                if size != record['size'] or hashed.hexdigest()!=record['sha256']: raise DependencyError('INPUT_CHANGED')
    legal = output/'legal'
    (legal/'dependency-proof.json').write_text(json.dumps(proof(spec), indent=2), encoding='utf-8', newline='\n')
    (legal/'Caddy-NOTICES.txt').write_bytes(CADDY_NOTICES)
    add_license_texts(output)
    validate_staged({p.relative_to(output).as_posix():p for p in output.rglob('*') if p.is_file()}, spec)
    return proof(spec)


def node_component(spec=None):
    """The pinned official Node runtime (dependency-pins.json "node"), as runtime-lock lists it."""
    spec = pins() if spec is None else spec
    node = spec['node']
    executable = next(f for f in node['files'] if f['path'] == 'runtime/node/node.exe')
    return {'id':'node', 'path':executable['path'], 'sha256':executable['sha256'],
        'version':node['version'], 'origin':node['origin'], 'publisher':node['publisher'],
        'sourceUrl':node['archive']['url'], 'archiveSha256':node['archive']['sha256'],
        'license':node['license']}


def validate_node(directory, spec=None):
    """{infrastructure member: file} of the pinned Node in an extracted official
    distribution: node.exe and its LICENSE, exactly the pinned bytes. The build
    tool and the shipped runtime are the same pinned image."""
    spec = pins() if spec is None else spec
    directory = Path(directory)
    result = {}
    try:
        for record in spec['node']['files']:
            path = directory/record['directoryPath']
            if path.is_symlink() or not path.is_file() or path.stat().st_size != record['size'] or digest(path) != record['sha256']:
                raise DependencyError('NODE_PIN: '+record['directoryPath'])
            result[record['path']] = path
    except (KeyError, TypeError, OSError) as error:
        raise DependencyError('NODE_PIN') from error
    return result


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); sub=parser.add_subparsers(dest='operation',required=True)
    staging=sub.add_parser('stage')
    for name in ('caddy-archive','caddy-checksums','output'): staging.add_argument('--'+name,required=True,type=Path)
    # Read-only checks of build inputs (installer/package.ps1).
    sub.add_parser('verify-node').add_argument('--directory',required=True,type=Path)
    sub.add_parser('verify-vendors').add_argument('--directory',required=True,type=Path)
    args=parser.parse_args()
    try:
        if args.operation=='stage':
            result=stage({'caddy':args.caddy_archive}, {'caddy':args.caddy_checksums}, args.output)
            print(json.dumps({'kind':result['kind'],'familyInstallationReady':False}))
        elif args.operation=='verify-node':
            validate_node(args.directory); print(json.dumps({'node':node_component()['version']}))
        else:
            root=args.directory
            print(json.dumps(validate_staged({p.relative_to(root).as_posix():p for p in root.rglob('*') if p.is_file()}), sort_keys=True))
    except (DependencyError, OSError) as error: raise SystemExit('Dependency refused: '+str(error))
