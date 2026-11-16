"""The app tree a release ships (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.1, 5.2).

JavaScript, HTML and data under app/, run by the infrastructure's Node as the
restricted Runtime account: one self-contained server bundle (no source tree,
loader or node_modules), the built web assets, fixtures, the public LAN help and
the app's own legal records. It carries no native code at all: no PE image under
any name and no executable or script file name. Platform, Node and Caddy are
infrastructure (infra_package.py). These are the rules AppPackage applies when it
extracts yahad-windows-app-<v>.zip; the writer refuses what the reader would.
"""
import hashlib
import json
from pathlib import Path
import re
import stat


class BundleError(ValueError): pass


# One self-contained ESM file for the single process entry; never split into chunks.
SERVER = ('app/server/main.mjs',)
APP_ROOTS = ('app/server/', 'app/dist/', 'app/fixtures/', 'app/deploy/', 'app/legal/')
# What the server build writes (scripts/build-server.mjs) and what the app staging
# adds beside it (prepare_core_fixture.py): the app track's own legal records.
APP_LEGAL = ('app/legal/server-bundle.json', 'app/legal/ThirdPartyNotices-server.txt', 'app/legal/build-inputs.json',
             'app/legal/SBOM.json', 'app/legal/Yahad-LICENSE.txt')
REQUIRED = ('app/package.json', *SERVER, 'app/dist/index.html', 'app/deploy/lan-capabilities.json', 'app/deploy/lan-help.html', *APP_LEGAL)
REFUSED_DIRECTORIES = ('app/src/', 'app/node_modules/', 'app/native/')
REFUSED_FILES = ('app/package-lock.json',)
REFUSED_SUFFIXES = ('.node', '.exe', '.dll', '.ps1', '.cmd', '.bat')
MAX_FILES, MAX_FILE, MAX_BYTES = 50000, 512 * 1024 ** 2, 2 * 1024 ** 3
_PACKAGE_PATH = re.compile(r'(?:node_modules/(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+/)*node_modules/(?:@[a-zA-Z0-9._-]+/)?[a-zA-Z0-9._-]+')


def digest(path):
    with Path(path).open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()


def object_file(path):
    value = json.loads(Path(path).read_text(encoding='utf-8'))
    if not isinstance(value, dict): raise BundleError('JSON object required')
    return value


def refused_member(name, head):
    """Why an app member is refused (5.2), or None. name is tree-relative; head
    holds the member's first bytes."""
    folded = name.casefold()
    if any(folded.startswith(prefix) for prefix in REFUSED_DIRECTORIES) or folded in REFUSED_FILES:
        return 'Application source, dependency tree or native helper'
    if folded.endswith(REFUSED_SUFFIXES): return 'Executable or script member name'
    if head[:2] == b'MZ': return 'PE image member'
    return None


def inventory(root):
    """{tree-relative name: file} of a prepared app tree: app/ and nothing else."""
    root = Path(root)
    result, pending, folded = {}, [root], set()
    while pending:
        directory = pending.pop()
        if directory.is_symlink() or getattr(directory.lstat(), 'st_file_attributes', 0) & 0x400: raise BundleError('Linked directory')
        for path in sorted(directory.iterdir()):
            name = path.relative_to(root).as_posix()
            if name.casefold() in folded: raise BundleError('Case collision')
            folded.add(name.casefold())
            if name == 'app' and path.is_dir() and not path.is_symlink(): pending.append(path); continue
            if len(name) > 240 or not name.startswith('app/'): raise BundleError('Undeclared app namespace: '+name)
            for part in name.split('/'):
                if (part in ('', '.', '..') or part.casefold() in ('.git', 'data', 'backups', 'secrets', 'updatestate') or part.casefold().startswith('.env') or
                        part.endswith(('.', ' ')) or ':' in part or '\\' in part or re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?', part)):
                    raise BundleError('Private or unsafe path')
            info = path.lstat()
            if path.is_symlink() or getattr(info, 'st_file_attributes', 0) & 0x400: raise BundleError('Linked path')
            if path.is_dir(): pending.append(path); continue
            if (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > MAX_FILE or
                    re.search(r'\.(key|pem|sqlite(?:-wal|-shm)?|backup)$', name, re.I)):
                raise BundleError('Unsafe package input')
            result[name] = path
            if len(result) > MAX_FILES: raise BundleError('File count')
    return result


def validate(prepared):
    """The verified app tree: {'version', 'files': [{path, size, sha256}]} in path order."""
    prepared = Path(prepared).absolute()
    for ancestor in (prepared, *prepared.parents):
        if ancestor.is_symlink() or getattr(ancestor.lstat(), 'st_file_attributes', 0) & 0x400: raise BundleError('Linked source root')
    files = inventory(prepared)
    for name in REQUIRED:
        if name not in files: raise BundleError('MISSING_PAYLOAD: ' + name)
    for name, path in files.items():
        if name != 'app/package.json' and not name.startswith(APP_ROOTS): raise BundleError('Undeclared app namespace: '+name)
        with path.open('rb') as stream: reason = refused_member(name, stream.read(2))
        if reason: raise BundleError(reason+': '+name)
    if {name for name in files if name.startswith('app/server/')} != set(SERVER): raise BundleError('Undeclared app namespace: app/server/')
    package = object_file(files['app/package.json'])
    if package.get('engines', {}).get('node') != '>=24.0.0 <25' or not re.fullmatch(r'\d+\.\d+\.\d+', str(package.get('version', ''))): raise BundleError('App package identity')
    # The bundler's record pins each shipped entry and names every bundled package.
    server = object_file(files['app/legal/server-bundle.json']); outputs = server.get('outputs'); packages = server.get('packages')
    if (server.get('format') != 'yahad-server-bundle' or server.get('schema') != 1 or server.get('notices') != 'app/legal/ThirdPartyNotices-server.txt' or
            not isinstance(outputs, list) or not all(isinstance(o, dict) for o in outputs) or
            sorted(o.get('path') for o in outputs) != list(SERVER) or not isinstance(packages, list) or
            not all(isinstance(p, dict) and isinstance(p.get('name'), str) and isinstance(p.get('version'), str) and isinstance(p.get('noticeFiles'), list) and
                _PACKAGE_PATH.fullmatch(str(p.get('path'))) for p in packages)): raise BundleError('Server bundle contract')
    for output in outputs:
        if output.get('size') != files[output['path']].stat().st_size or output.get('sha256') != digest(files[output['path']]): raise BundleError('Server bundle checksum')
    sbom = object_file(files['app/legal/SBOM.json'])
    if sbom.get('format') != 'yahad-app-inventory' or not isinstance(sbom.get('serverBundleDependencies'), list): raise BundleError('App SBOM contract')
    if sorted(item.get('path') for item in sbom['serverBundleDependencies'] if isinstance(item, dict)) != sorted(p['path'] for p in packages):
        raise BundleError('App SBOM differs from the server bundle')
    if sum(path.stat().st_size for path in files.values()) > MAX_BYTES: raise BundleError('Payload size')
    records = [{'path':name, 'size':path.stat().st_size, 'sha256':digest(path)} for name, path in sorted(files.items())]
    return {'version':package['version'], 'files':records}
