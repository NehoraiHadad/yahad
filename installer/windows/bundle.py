"""Deterministic offline app-private Windows candidate; no host installation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import uuid
import zipfile
from urllib.parse import urlsplit
from . import pe, dependencies

class BundleError(ValueError): pass

# One self-contained ESM file for the single process entry; no source tree, loader or node_modules.
SERVER = ('app/server/main.mjs',)
# The app carries no native code of its own: the Platform helper is infrastructure,
# packaged beside the host and Updater (setup_package.py), so app/native/ is undeclared.
APP_ROOTS = ('app/server/', 'app/dist/', 'app/fixtures/', 'app/deploy/')
BASE_REQUIRED = ('app/package.json', *SERVER, 'app/dist/index.html', 'runtime/node/node.exe', 'legal/runtime-lock.json',
    'legal/SBOM.json', 'legal/server-bundle.json', 'legal/ThirdPartyNotices-server.txt',
    'legal/Node-LICENSE.txt', 'legal/DotNet-LICENSE.txt', 'legal/DotNet-ThirdPartyNotices.txt')
FULL_REQUIRED = ('runtime/caddy/caddy.exe', 'legal/Caddy-LICENSE.txt', 'legal/Caddy-NOTICES.txt')
MAX_FILES, MAX_BYTES = 50000, 2 * 1024 ** 3

def object_file(path):
    value = json.loads(path.read_text(encoding='utf-8-sig'))
    if not isinstance(value, dict): raise BundleError('JSON object required')
    return value

def digest(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()

def inventory(root):
    result, pending, folded = {}, [root], set()
    while pending:
        directory = pending.pop()
        if directory.is_symlink() or getattr(directory.lstat(), 'st_file_attributes', 0) & 0x400: raise BundleError('Linked directory')
        for path in sorted(directory.iterdir()):
            name = path.relative_to(root).as_posix()
            if name.casefold() in folded: raise BundleError('Case collision')
            folded.add(name.casefold())
            if path.is_dir() and name in ('app','runtime','legal'):
                pending.append(path); continue
            if len(name) > 240 or not name.startswith(('app/', 'runtime/', 'legal/')): raise BundleError('Package path')
            for part in name.split('/'):
                if part in ('', '.', '..') or part.casefold() == '.git' or part.casefold() in ('data', 'backups', 'secrets', 'updatestate') or part.casefold().startswith('.env') or part.endswith(('.', ' ')) or ':' in part or '\\' in part or re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?', part): raise BundleError('Private or unsafe path')
            info = path.lstat()
            if path.is_symlink() or getattr(info, 'st_file_attributes', 0) & 0x400: raise BundleError('Linked path')
            if path.is_dir(): pending.append(path); continue
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > 512 * 1024 ** 2 or re.search(r'\.(key|pem|sqlite(?:-wal|-shm)?|backup)$', name, re.I): raise BundleError('Unsafe package input')
            result[name] = path
            if len(result) > MAX_FILES: raise BundleError('File count')
    return result

def validate(prepared, profile='full'):
    if profile not in ('full', 'core-fixture'): raise BundleError('Unsupported profile')
    prepared = Path(prepared).absolute()
    for ancestor in (prepared, *prepared.parents):
        if ancestor.is_symlink() or getattr(ancestor.lstat(), 'st_file_attributes', 0) & 0x400: raise BundleError('Linked source root')
    files = inventory(prepared)
    for name in (*BASE_REQUIRED, *(FULL_REQUIRED if profile == 'full' else ())):
        if name not in files: raise BundleError('MISSING_PAYLOAD: ' + name)
    runtime_prefixes = ('runtime/node/',) if profile == 'core-fixture' else ('runtime/node/', 'runtime/caddy/')
    undeclared = sorted(name for name in files if name.startswith('runtime/') and not name.startswith(runtime_prefixes))
    if undeclared: raise BundleError('Undeclared runtime namespace: ' + undeclared[0])
    if any(name.startswith('app/') and name != 'app/package.json' and not name.startswith(APP_ROOTS) for name in files) or {name for name in files if name.startswith('app/server/')} != set(SERVER): raise BundleError('Undeclared app namespace')
    package = object_file(files['app/package.json'])
    if package.get('engines', {}).get('node') != '>=24.0.0 <25' or not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?', package.get('version', '')): raise BundleError('App package identity')
    # The bundler's record pins each shipped entry and names every bundled package.
    server = object_file(files['legal/server-bundle.json']); outputs = server.get('outputs'); packages = server.get('packages')
    if (server.get('format') != 'yahad-server-bundle' or server.get('schema') != 1 or not isinstance(outputs, list) or not all(isinstance(o, dict) for o in outputs) or
            sorted(o.get('path') for o in outputs) != list(SERVER) or not isinstance(packages, list) or
            not all(isinstance(p, dict) and isinstance(p.get('name'), str) and isinstance(p.get('version'), str) and isinstance(p.get('noticeFiles'), list) and
                re.fullmatch(r'(?:node_modules/(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+/)*node_modules/(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+', str(p.get('path'))) for p in packages)): raise BundleError('Server bundle contract')
    for output in outputs:
        if output.get('size') != files[output['path']].stat().st_size or output.get('sha256') != digest(files[output['path']]): raise BundleError('Server bundle checksum')
    runtime_lock = object_file(files['legal/runtime-lock.json'])
    expected = {'node':'runtime/node/node.exe'}
    if profile == 'full': expected.update(caddy='runtime/caddy/caddy.exe')
    components = runtime_lock.get('components', [])
    if runtime_lock.get('schema') != 1 or runtime_lock.get('target') != 'win-x64' or not isinstance(components, list) or not all(isinstance(c,dict) for c in components) or len(components) != len(expected) or {c.get('id') for c in components} != set(expected): raise BundleError('Runtime lock contract')
    # A component claiming its pinned vendor origin must be the exact staged pinned closure.
    pinned_sources, pinned_components = dependencies.pins()['sources'], {}
    if any(c.get('id') in pinned_sources and c.get('origin') == pinned_sources[c['id']]['origin'] for c in components):
        try: pinned_components = dependencies.validate_staged(files)
        except dependencies.DependencyError as error: raise BundleError('Pinned vendor dependency verification') from error
    for component in components:
        name = expected[component['id']]
        source = urlsplit(component.get('sourceUrl', ''))
        if component.get('path') != name or digest(files[name]) != component.get('sha256') or source.scheme != 'https' or not source.hostname or source.username or source.query or source.fragment: raise BundleError('Runtime checksum/provenance')
        if component['id'] == 'node' and (source.hostname != 'nodejs.org' or not re.fullmatch(r'24\.\d+\.\d+', component.get('version',''))): raise BundleError('Official pinned Node 24 required')
        if component['id'] == 'caddy' and (source.hostname != 'github.com' or not source.path.startswith('/caddyserver/caddy/releases/download/')): raise BundleError('Official Caddy required')
        if component['id'] in pinned_components and component != pinned_components[component['id']]: raise BundleError('Pinned vendor component identity')
    object_file(files['legal/SBOM.json'])
    modules = {}
    for name, path in files.items():
        if path.suffix.lower() in ('.exe','.dll','.node'):
            try: modules[name] = pe.inspect(path)
            except pe.PEError as error: raise BundleError('PE inspection failed: ' + name) from error
    folded_files = {name.casefold(): name for name in files}
    unresolved = []
    for name, module in modules.items():
        for imported in module['imports']:
            if pe.is_system(imported): continue
            sibling = name.rsplit('/',1)[0] + '/' + imported
            # No developer PATH, global .NET, registry or arbitrary whole-tree DLL search.
            if sibling.casefold() not in folded_files: unresolved.append(name + ' -> ' + imported)
    if unresolved: raise BundleError('MISSING_PRIVATE_DLL: ' + '; '.join(unresolved[:8]))
    total = sum(path.stat().st_size for path in files.values())
    if total > MAX_BYTES: raise BundleError('Payload size')
    records = [{'path':name,'size':path.stat().st_size,'sha256':digest(path)} for name,path in sorted(files.items())]
    return {'schema':1,'kind':'windows-runtime-candidate','target':'win-x64','profile':profile,
        'version':package['version'],'machineAcceptance':'not-performed',
        'familyInstallationReady':False,'launch':{'node':'runtime/node/node.exe','entry':'app/server/main.mjs',
        'caddy':'runtime/caddy/caddy.exe' if profile == 'full' else None},
        'files':records,'peModules':modules}

def build(prepared, output, profile='full'):
    prepared, output = Path(prepared).absolute(), Path(output).absolute()
    if output.exists() or output.is_relative_to(prepared): raise BundleError('Output must be new and outside input')
    manifest = validate(prepared, profile)
    output.parent.mkdir(parents=True, exist_ok=True); partial = output.with_name(output.name + '.partial-' + uuid.uuid4().hex)
    try:
        with partial.open('xb') as stream:
            with zipfile.ZipFile(stream,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
                def entry(name):
                    info = zipfile.ZipInfo(name,(2026,10,28,0,0,0)); info.create_system=3; info.external_attr=(stat.S_IFREG | 0o644)<<16; info.compress_type=zipfile.ZIP_DEFLATED; return info
                archive.writestr(entry('bundle.json'),json.dumps(manifest,sort_keys=True,separators=(',',':'))+'\n')
                for record in manifest['files']:
                    path = prepared/record['path']; hashed=hashlib.sha256(); count=0
                    with path.open('rb') as source, archive.open(entry(record['path']),'w') as destination:
                        while chunk := source.read(1024*1024):
                            count += len(chunk)
                            if count > record['size']: raise BundleError('Input changed')
                            hashed.update(chunk); destination.write(chunk)
                    if count != record['size'] or hashed.hexdigest()!=record['sha256']: raise BundleError('Input changed')
            stream.flush(); os.fsync(stream.fileno())
        os.link(partial,output); return manifest
    finally: partial.unlink(missing_ok=True)

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument('--prepared',required=True,type=Path); parser.add_argument('--output',required=True,type=Path); parser.add_argument('--profile',choices=('full','core-fixture'),default='full'); args=parser.parse_args()
    try:
        result=build(args.prepared,args.output,args.profile); print(json.dumps({'kind':result['kind'],'profile':result['profile'],'files':len(result['files']),'familyInstallationReady':False}))
    except (ValueError,OSError,TypeError,KeyError,AttributeError): raise SystemExit('Windows candidate refused; existing installation/output preserved')
