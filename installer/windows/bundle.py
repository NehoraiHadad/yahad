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
from . import pe

class BundleError(ValueError): pass

BASE_REQUIRED = ('app/package.json', 'app/package-lock.json', 'app/src/main.ts', 'app/dist/index.html',
    'app/node_modules/tsx/package.json', 'app/node_modules/@esbuild/win32-x64/esbuild.exe',
    'runtime/node/node.exe', 'app/native/Yahad.Platform.exe', 'app/native/Yahad.Platform.runtimeconfig.json',
    'app/native/Yahad.Platform.deps.json', 'app/native/coreclr.dll', 'app/native/hostfxr.dll',
    'app/native/hostpolicy.dll', 'app/native/System.Private.CoreLib.dll', 'legal/runtime-lock.json',
    'legal/SBOM.json', 'legal/Node-LICENSE.txt', 'legal/DotNet-LICENSE.txt', 'legal/DotNet-ThirdPartyNotices.txt')
FULL_REQUIRED = ('runtime/ffmpeg/ffmpeg.exe', 'runtime/caddy/caddy.exe', 'legal/FFmpeg-LICENSE.txt',
    'legal/ffmpeg-build.json', 'legal/Caddy-LICENSE.txt', 'legal/Caddy-NOTICES.txt')
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
            if path.is_dir() and name in ('app','runtime','legal'):
                pending.append(path); continue
            if len(name) > 240 or not name.startswith(('app/', 'runtime/', 'legal/')): raise BundleError('Package path')
            for part in name.split('/'):
                if part in ('', '.', '..') or part.casefold() == '.git' or (not name.startswith('app/node_modules/') and part.casefold() in ('data', 'backups', 'secrets', 'updatestate')) or part.casefold().startswith('.env') or part.endswith(('.', ' ')) or ':' in part or '\\' in part or re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?', part): raise BundleError('Private or unsafe path')
            info = path.lstat()
            if path.is_symlink() or getattr(info, 'st_file_attributes', 0) & 0x400: raise BundleError('Linked path')
            if path.is_dir(): pending.append(path); continue
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > 512 * 1024 ** 2 or re.search(r'\.(key|pem|sqlite(?:-wal|-shm)?|backup)$', name, re.I): raise BundleError('Unsafe package input')
            if name.casefold() in folded: raise BundleError('Case collision')
            folded.add(name.casefold()); result[name] = path
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
    runtime_prefixes = ('runtime/node/',) if profile == 'core-fixture' else ('runtime/node/', 'runtime/ffmpeg/', 'runtime/caddy/')
    if any(name.startswith('runtime/') and not name.startswith(runtime_prefixes) for name in files): raise BundleError('Undeclared runtime namespace')
    package, lock = object_file(files['app/package.json']), object_file(files['app/package-lock.json'])
    root_lock = lock.get('packages', {}).get('', {})
    if not isinstance(root_lock, dict) or package.get('engines', {}).get('node') != '>=24.0.0 <25' or not re.fullmatch(r'\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?', package.get('version', '')) or root_lock.get('version') != package['version'] or root_lock.get('dependencies') != package.get('dependencies'): raise BundleError('App lock mismatch')
    for name in package.get('dependencies', {}):
        if not re.fullmatch(r'(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+', name) or f'app/node_modules/{name}/package.json' not in files: raise BundleError('Production dependency missing')
    runtimeconfig = object_file(files['app/native/Yahad.Platform.runtimeconfig.json']).get('runtimeOptions', {})
    if runtimeconfig.get('framework') or runtimeconfig.get('frameworks') or not any(f.get('name') == 'Microsoft.NETCore.App' and str(f.get('version', '')).startswith('10.') for f in runtimeconfig.get('includedFrameworks', [])): raise BundleError('Helper must have private self-contained .NET')
    deps = object_file(files['app/native/Yahad.Platform.deps.json'])
    target = deps.get('runtimeTarget', {}).get('name', '')
    if not target.endswith('/win-x64') or target not in deps.get('targets', {}): raise BundleError('Private .NET runtime dependency target')
    for library in deps['targets'][target].values():
        for category in ('runtime','native'):
            for asset in library.get(category, {}):
                leaf = asset.rsplit('/',1)[-1]
                if not leaf or '\\' in leaf or 'app/native/' + leaf not in files: raise BundleError('Missing private .NET dependency')
    runtime_lock = object_file(files['legal/runtime-lock.json'])
    expected = {'node':'runtime/node/node.exe', 'platform':'app/native/Yahad.Platform.exe'}
    if profile == 'full': expected.update(ffmpeg='runtime/ffmpeg/ffmpeg.exe', caddy='runtime/caddy/caddy.exe')
    components = runtime_lock.get('components', [])
    if runtime_lock.get('schema') != 1 or runtime_lock.get('target') != 'win-x64' or not isinstance(components, list) or not all(isinstance(c,dict) for c in components) or len(components) != len(expected) or {c.get('id') for c in components} != set(expected): raise BundleError('Runtime lock contract')
    for component in components:
        name = expected[component['id']]
        source = urlsplit(component.get('sourceUrl', ''))
        if component.get('path') != name or digest(files[name]) != component.get('sha256') or source.scheme != 'https' or not source.hostname or source.username or source.query or source.fragment: raise BundleError('Runtime checksum/provenance')
        if component['id'] == 'node' and (source.hostname != 'nodejs.org' or not re.fullmatch(r'24\.\d+\.\d+', component.get('version',''))): raise BundleError('Official pinned Node 24 required')
        if component['id'] == 'caddy' and (source.hostname != 'github.com' or not source.path.startswith('/caddyserver/caddy/releases/download/')): raise BundleError('Official Caddy required')
        if component['id'] == 'platform' and (component.get('origin') != 'project-built' or not re.fullmatch('[a-f0-9]{40}', component.get('sourceCommit',''))): raise BundleError('Project build identity required')
        if component['id'] == 'ffmpeg':
            # FFmpeg publishes source, not Windows binaries. The strict policy
            # cannot silently relabel a community build as vendor-official.
            build = object_file(files['legal/ffmpeg-build.json'])
            if component.get('origin') != 'project-built-from-official-source' or source.hostname != 'ffmpeg.org' or not source.path.startswith('/releases/') or not re.fullmatch('[a-f0-9]{64}', build.get('sourceSha256','')) or not re.fullmatch('[a-f0-9]{64}', build.get('buildRecipeSha256','')) or not isinstance(build.get('configure'), list) or any(x in build['configure'] for x in ('--enable-nonfree','--enable-gpl')): raise BundleError('Official-source LGPL FFmpeg build/provenance required')
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
            # Native Node addons resolve Node exports from the fixed executable
            # already hosting them, never from a DLL search or developer PATH.
            if imported == 'node.exe' and name.endswith('.node') and 'runtime/node/node.exe' in modules: continue
            sibling = name.rsplit('/',1)[0] + '/' + imported
            # No developer PATH, global .NET, registry or arbitrary whole-tree DLL search.
            if sibling.casefold() not in folded_files: unresolved.append(name + ' -> ' + imported)
    if unresolved: raise BundleError('MISSING_PRIVATE_DLL: ' + '; '.join(unresolved[:8]))
    total = sum(path.stat().st_size for path in files.values())
    if total > MAX_BYTES: raise BundleError('Payload size')
    records = [{'path':name,'size':path.stat().st_size,'sha256':digest(path)} for name,path in sorted(files.items())]
    return {'schema':1,'kind':'windows-runtime-candidate','target':'win-x64','profile':profile,
        'version':package['version'],'authority':'application-updater','machineAcceptance':'not-performed',
        'familyInstallationReady':False,'launch':{'node':'runtime/node/node.exe','entry':'app/src/main.ts',
        'helper':'app/native/Yahad.Platform.exe','ffmpeg':'runtime/ffmpeg/ffmpeg.exe' if profile == 'full' else None},
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
