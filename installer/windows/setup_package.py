"""Offline build-machine packager of one release's two Windows artifacts.

From a verified cached infrastructure tree (infra_package.py) and this checkout's
staged app tree (prepare_core_fixture.py) it writes:
- install.zip and install-manifest.json, the Format 2 package the distributable
  Setup carries (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.1, 5.3): infra/infra-tree.json
  and the infrastructure members exactly as cached (host/, updater/, platform/,
  runtime/, legal/, setup/), and apps/app-tree.json with apps/app/... The manifest
  lists every member; the archive holds every member but the engine
  (infra/setup/Yahad.Setup.exe), whose bytes are the Setup image's own prefix;
- Yahad.Setup.exe, the distributable Setup: the cached payload-free engine, then
  install.zip, install-manifest.json and a 40-byte trailer (write_image);
- yahad-windows-app-<v>.zip, the app-only release asset (5.2): exactly
  app-tree.json and app/..., its app-tree.json the same bytes as the install
  package's apps/app-tree.json.
Each half carries its own identity: the infrastructure its version, contract
range and infra source commit (from the cache record); the app package.json's
version, its contract and the release commit (this checkout's HEAD).
The Setup image is written last. Never installs or fabricates release
receipts; only audit-only --describe entry points of verified local project
builds run. Outputs remain private candidates.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import subprocess
import zipfile

from . import bundle, pe
from .application_version import (source_release_version, source_infrastructure_identity, package_infrastructure_contract,
    package_minimum_from, require_native_build_identity, describe_build_artifact)
from .database_schema import require_build_schema


class PackageError(ValueError): pass


class _VerifiedFiles(dict):
    def __init__(self, files, root=None):
        super().__init__(files)
        self.root = root
        self.records = {name:_record(name,path) for name,path in files.items()}

    def require_unchanged(self):
        current = _inventory(self.root) if self.root is not None else self
        if {name:_record(name,path) for name,path in current.items()}!=self.records:
            raise PackageError('Verified closure changed')


RUNTIME_VERSION = '10.0.14'
INFRA, APPS = 'infra/', 'apps/'
INFRA_TREE_NAME, APP_TREE_NAME = 'infra-tree.json', 'app-tree.json'
INFRA_TREE, APP_TREE = INFRA+INFRA_TREE_NAME, APPS+APP_TREE_NAME
# TreeRecord.InfrastructurePrefixes / AppPrefixes, relative to each tree root.
INFRA_PREFIXES = ('host/', 'updater/', 'platform/', 'runtime/', 'legal/', 'setup/')
APP_PREFIXES = ('app/',)
ENGINE = 'Yahad.Setup.exe'
# The infrastructure tree's own legal records (infra_package.py) and the app's (bundle.APP_LEGAL).
INFRA_LEGAL = ('legal/runtime-lock.json', 'legal/SBOM.json', 'legal/Node-LICENSE.txt', 'legal/DotNet-LICENSE.txt',
               'legal/DotNet-ThirdPartyNotices.txt', 'legal/Caddy-LICENSE.txt', 'legal/Yahad-LICENSE.txt')
INFRA_REQUIRED = frozenset({'host/Yahad.ServiceHost.exe','host/Yahad.ServiceHost.runtimeconfig.json',
    'updater/Yahad.Updater.exe','updater/Yahad.Updater.runtimeconfig.json',
    'platform/Yahad.Platform.exe','platform/Yahad.Platform.runtimeconfig.json',
    'runtime/node/node.exe','runtime/caddy/caddy.exe','setup/'+ENGINE, *INFRA_LEGAL})
# The engine member is not an archive entry: the Setup image's prefix is its bytes.
ENGINE_MEMBER = INFRA+'setup/'+ENGINE
# The Setup image trailer (installer/Yahad.Setup/InstallPackage.cs SetupImage): the magic,
# then the engine, archive and manifest lengths, little-endian int64.
IMAGE_MAGIC = b'Yahad.SetupImage'
IMAGE_TRAILER = struct.Struct('<16sqqq')
MAX_MANIFEST = 16*1024**2
APP_REQUIRED = frozenset(bundle.REQUIRED)
REQUIRED_MEMBERS = frozenset({INFRA_TREE, APP_TREE, *(INFRA+name for name in INFRA_REQUIRED), *(APPS+name for name in APP_REQUIRED)})
COMPONENTS = (('host/','Yahad.ServiceHost'), ('updater/','Yahad.Updater'), ('platform/','Yahad.Platform'))
MAX_FILE, MAX_TOTAL, MAX_FILES = 512 * 1024**2, 2 * 1024**3, 50000
# zlib level of every archive member. Measured on infrastructure 1.0.0 (455 MB raw):
# level 6 deflates it in 45 s to 220.1 MB, level 9 in 119 s to 219.5 MB (0.29% less).
ARCHIVE_LEVEL = 6
PRIVATE = {'.git', '.local', '.agents', '.codex', '.authority', 'data', 'datasets', 'backups', 'secrets',
           'enrollment.json', 'installation.json', 'update-authority.json', 'activation.json',
           'bootstrap.json', 'master-key', 'update-policy', 'update-pin'}


def _json(data, maximum=8*1024**2):
    if len(data) > maximum: raise PackageError('JSON bound')
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result: raise PackageError('Duplicate JSON field')
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=unique)


def _encode(value): return (json.dumps(value, sort_keys=True, separators=(',', ':'))+'\n').encode('utf-8')
def _hash(value): return hashlib.sha256(value).hexdigest()
# .NET string.CompareOrdinal order: UTF-16 code units, which big-endian bytes keep.
def _ordinal(name): return name.encode('utf-16-be')


def _safe_path(name, *, component=False):
    if not isinstance(name, str) or len(name) not in range(1, 201 if component else 241) or '\\' in name or ':' in name:
        raise PackageError('Unsafe member path')
    parts = name.split('/')
    for part in parts:
        if (part in ('', '.', '..') or part.endswith(('.', ' ')) or any(ord(c)<32 or c in '<>"|?*' for c in part) or
                re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\..*)?', part)):
            raise PackageError('Unsafe member path')
        if part.casefold().startswith('.env') or part.casefold() in PRIVATE:
            raise PackageError('Private member path')
    if re.search(r'\.(?:pdb|key|pem|pfx|p12|db|sqlite(?:-wal|-shm|-journal)?|backup|bak)$', name, re.I):
        raise PackageError('Private or debug member')
    return name


def _unlinked(path, *, file=False):
    path = Path(path).absolute()
    for current in (path, *path.parents):
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise PackageError('Linked input or ancestor')
    if file:
        info = path.lstat()
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_size > MAX_FILE:
            raise PackageError('Unsafe input file')
    return path


def _closed_names(names, *, component=False):
    files, directories = {}, {}
    for name in names:
        _safe_path(name, component=component); folded = name.casefold()
        if folded in files: raise PackageError('Duplicate or case-colliding member')
        files[folded] = name
        parts = name.split('/')
        for count in range(1, len(parts)):
            directory = '/'.join(parts[:count]); key = directory.casefold()
            if key in directories and directories[key] != directory: raise PackageError('Case-colliding directory')
            directories[key] = directory
    if files.keys() & directories.keys(): raise PackageError('File/directory collision')


def _inventory(root):
    root = _unlinked(root)
    if not root.is_dir(): raise PackageError('Component directory required')
    result, pending = {}, [root]
    while pending:
        directory = pending.pop(); _unlinked(directory)
        for path in sorted(directory.iterdir()):
            name = path.relative_to(root).as_posix(); _safe_path(name, component=True); _unlinked(path)
            if path.is_dir(): pending.append(path)
            else: result[name] = _unlinked(path, file=True)
            if len(result)>MAX_FILES: raise PackageError('Component file count')
    _closed_names(result, component=True)
    if sum(p.stat().st_size for p in result.values())>MAX_TOTAL: raise PackageError('Component byte bound')
    return result


def _record(name, value):
    if isinstance(value, bytes): return dict(path=name, size=len(value), sha256=_hash(value))
    path = _unlinked(value, file=True)
    return dict(path=name, size=path.stat().st_size, sha256=bundle.digest(path))


def _committed_source(repo):
    repo = _unlinked(repo)
    def git(*args): return subprocess.check_output(['git', '-C', str(repo), *args])
    if git('status', '--porcelain', '--untracked-files=normal').strip(): raise PackageError('Clean committed source required')
    source = git('rev-parse', 'HEAD').decode('ascii').strip()
    if not re.fullmatch('[a-f0-9]{40}', source): raise PackageError('Source commit required')
    packager = _unlinked(Path(__file__), file=True)
    if packager != repo/'installer/windows/setup_package.py' or git('show', 'HEAD:installer/windows/setup_package.py') != packager.read_bytes():
        raise PackageError('Executing packager must match committed source')
    tracked = git('ls-files', '-z', '--', 'src', 'package.json', 'package-lock.json').decode('utf-8').split('\0')
    return source, [name for name in tracked if name]


def app_identity(repo, infrastructure):
    """(app version, its required contract, minimumFrom) of one checkout, against the
    infrastructure it ships with; that infrastructure must be the checkout's own."""
    repo = _unlinked(repo)
    if source_infrastructure_identity(repo) != infrastructure: raise PackageError('Infrastructure identity differs from the checkout')
    package = _unlinked(repo/'package.json', file=True).read_bytes()
    version = source_release_version(repo)
    return version, package_infrastructure_contract(package, infrastructure), package_minimum_from(package, version, infrastructure.version)


def _source_identity(repo, infrastructure):
    return (*app_identity(repo, infrastructure), *_committed_source(repo))


def validate_payload_source(repo, prepared, records, version, source, tracked):
    """The staged app tree is this checkout's: its package.json bytes, its build
    record's commits and schema, and the bundler's record of tracked source bytes
    and locked production packages. Returns (schema, migrations)."""
    repo, prepared = Path(repo), Path(prepared)
    records = {r['path']:r for r in records}; _closed_names(records)
    if _unlinked(repo/'package.json', file=True).read_bytes()!=_unlinked(prepared/'app/package.json', file=True).read_bytes():
        raise PackageError('Application package bytes differ')
    if bundle.object_file(prepared/'app/package.json').get('version')!=version: raise PackageError('Application identity differs')
    build = _json((prepared/'app/legal/build-inputs.json').read_bytes(), 65536)
    if any(build.get(field)!=source for field in ('appSourceCommit', 'packagerSourceCommit')):
        raise PackageError('Mixed source build inputs')
    schema, migrations = require_build_schema(build, repo)
    # The bundler's record ties every shipped entry to tracked source bytes and to
    # non-development lockfile packages; the tree records pin the entry bytes.
    server = _json((prepared/'app/legal/server-bundle.json').read_bytes())
    lock = _json(_unlinked(repo/'package-lock.json', file=True).read_bytes(), 64*1024**2).get('packages', {})
    sources, packages = server.get('sources'), server.get('packages')
    if not isinstance(sources, dict) or not sources or not isinstance(packages, list): raise PackageError('Server bundle record')
    for name, digest in sources.items():
        if not name.startswith('src/') or name not in tracked or _hash(_unlinked(repo/name, file=True).read_bytes())!=digest:
            raise PackageError('Application source bytes differ')
    for item in packages:
        locked = lock.get(item.get('path')) if isinstance(item, dict) else None
        if not isinstance(locked, dict) or locked.get('dev') or locked.get('version')!=item.get('version'): raise PackageError('Bundled package not locked')
    outputs = {o.get('path'):o for o in server.get('outputs', []) if isinstance(o, dict)}
    if set(outputs)!=set(bundle.SERVER): raise PackageError('Server bundle record')
    for name, output in outputs.items():
        record, entry = records.get(name), 'src/'+PurePosixPath(name).stem+'.ts'
        if (not record or record['sha256']!=output.get('sha256') or record['size']!=output.get('size') or output.get('entry')!=entry or
                not isinstance(output.get('sources'), list) or entry not in output['sources'] or not set(output['sources'])<=sources.keys()):
            raise PackageError('Server bundle record differs')
    return schema, migrations


def validate_native(root, artifact, infrastructure, source, repo):
    files = _VerifiedFiles(_inventory(root),root=root); entry = artifact+'.exe'; stem = artifact
    required = {entry, stem+'.dll', stem+'.runtimeconfig.json', stem+'.deps.json',
                'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll'}
    if not required<=files.keys(): raise PackageError('Incomplete private .NET runtime')
    runtime = _json(files[stem+'.runtimeconfig.json'].read_bytes(), 65536)['runtimeOptions']
    expected = {'Microsoft.NETCore.App'}
    frameworks = runtime.get('includedFrameworks', [])
    if ('framework' in runtime or 'frameworks' in runtime or not isinstance(frameworks, list) or
            len(frameworks)!=len(expected) or {f.get('name') for f in frameworks}!=expected or any(f.get('version')!=RUNTIME_VERSION for f in frameworks)):
        raise PackageError('Private .NET 10.0.14 framework closure required')
    deps = _json(files[stem+'.deps.json'].read_bytes()); target = deps.get('runtimeTarget', {}).get('name', '')
    if not target.endswith('/win-x64') or target not in deps.get('targets', {}): raise PackageError('Private .NET dependency target')
    for identifier,library in deps['targets'][target].items():
        if any(name+'.Runtime.' in identifier for name in expected) and identifier.rsplit('/',1)[-1]!=RUNTIME_VERSION:
            raise PackageError('Mixed private .NET runtime version')
        for category in ('runtime', 'native', 'resources', 'runtimeTargets'):
            for asset, metadata in library.get(category, {}).items():
                _safe_path(asset, component=True); leaf = asset.rsplit('/', 1)[-1]
                if category=='resources':
                    locale = metadata.get('locale'); _safe_path(locale, component=True); leaf = locale+'/'+leaf
                if category=='runtimeTargets' and metadata.get('rid') not in ('win-x64', 'win', 'any'):
                    raise PackageError('Foreign private .NET dependency')
                if leaf not in files: raise PackageError('Missing private .NET dependency')
    _inspect(files,native_entry=entry)
    require_native_build_identity(describe_build_artifact(files[entry], '--describe', repo), artifact, infrastructure, source)
    files.require_unchanged()
    return files


def validate_engine(path, infrastructure, source, repo):
    # The payload-free Setup (4.4) installed as infra/setup/: one self-contained
    # single-file x64 image that loads nothing beside itself and embeds no package.
    path = _unlinked(path, file=True)
    if path.name != ENGINE: raise PackageError('Setup engine image required')
    files = _VerifiedFiles({ENGINE:path})
    _inspect(files, native_entry=ENGINE)
    description = require_native_build_identity(describe_build_artifact(path, '--describe', repo), 'Yahad.Setup', infrastructure, source)
    if description.get('embeddedPackage') is not False: raise PackageError('Setup engine must be payload-free')
    files.require_unchanged()
    return files


def _inspect(files, *, native_entry=None):
    folded = {name.casefold() for name in files}
    for name, path in files.items():
        if Path(name).suffix.casefold() not in ('.exe', '.dll', '.node'): continue
        module = pe.inspect(_unlinked(path, file=True))
        if name==native_entry and (module['managed'] or module['machine']!='x64'): raise PackageError('Native x64 entry required')
        for imported in module['imports']:
            sibling = (PurePosixPath(name).parent/imported).as_posix().casefold()
            if not pe.is_system(imported) and sibling not in folded: raise PackageError('Missing private native dependency: '+name+' -> '+imported)


def _app_member(member):
    # One app tree member by name (5.2): under app/, never a refused path or name.
    if not member.startswith('app/'): raise PackageError('Undeclared archive namespace')
    _safe_path(member[len('app/'):], component=True)
    reason = bundle.refused_member(member, b'')
    if reason: raise PackageError(reason)
    return member


def _tree_member(name):
    # The tree-relative member an install.zip name maps to; None for a tree record itself.
    if name in (INFRA_TREE, APP_TREE): return None
    if name.startswith(INFRA):
        member = name[len(INFRA):]
        prefix = next((p for p in INFRA_PREFIXES if member.startswith(p)), None)
        if prefix is not None:
            _safe_path(member[len(prefix):], component=True)
            return member
    elif name.startswith(APPS):
        return _app_member(name[len(APPS):])
    raise PackageError('Undeclared archive namespace')


def _app_archive_member(name):
    # yahad-windows-app-<v>.zip: exactly app-tree.json and app/... (5.2).
    return None if name==APP_TREE_NAME else _app_member(name)


def tree_record(version, source, members, prefixes):
    """infra-tree.json / app-tree.json (TreeRecord format 1): every member of the
    tree except the record itself, tree-relative, in ordinal path order. The
    writer refuses what the reader (parse_tree_record) would."""
    files = [dict(path=name, size=record['size'], sha256=record['sha256'])
             for name, record in sorted(members.items(), key=lambda item:_ordinal(item[0]))]
    data = _encode(dict(format=1, version=version, sourceCommit=source, files=files))
    if not files or len(data) > 16*1024**2: raise PackageError('Tree record bound')
    parse_tree_record(data, prefixes)
    return data


def parse_tree_record(data, prefixes):
    """TreeRecord.Parse: strict, no BOM, ordinal order, unique ignoring case, bound."""
    if not 1<=len(data)<=16*1024**2 or data.startswith(b'\xef\xbb\xbf'): raise PackageError('Tree record invalid')
    value = _json(data, 16*1024**2)
    if not isinstance(value, dict) or sorted(value)!=['files', 'format', 'sourceCommit', 'version']: raise PackageError('Tree record invalid')
    files = value['files']
    if (type(value['format']) is not int or value['format']!=1 or not isinstance(value['version'], str) or
            not re.fullmatch(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', value['version']) or
            not isinstance(value['sourceCommit'], str) or not re.fullmatch('[a-f0-9]{40}', value['sourceCommit']) or
            not isinstance(files, list) or not 1<=len(files)<=MAX_FILES):
        raise PackageError('Tree record invalid')
    previous, total = None, 0
    for item in files:
        if (not isinstance(item, dict) or sorted(item)!=['path', 'sha256', 'size'] or not isinstance(item['path'], str) or
                not item['path'].startswith(prefixes) or type(item['size']) is not int or not 0<=item['size']<=MAX_FILE or
                not isinstance(item['sha256'], str) or not re.fullmatch('[a-f0-9]{64}', item['sha256']) or
                previous is not None and _ordinal(previous)>=_ordinal(item['path'])):
            raise PackageError('Tree record invalid')
        previous = item['path']; total += item['size']
    _closed_names([item['path'] for item in files])
    if total>MAX_TOTAL: raise PackageError('Tree record invalid')
    return value


def _write_archive(output, entries, expected=None, member=_tree_member):
    _closed_names(entries)
    for name in entries: member(name)
    expected=expected if expected is not None else {name:_record(name,value) for name,value in entries.items()}
    if set(expected)!=set(entries) or len(entries)>MAX_FILES or any(not 0<=r['size']<=MAX_FILE for r in expected.values()) or sum(r['size'] for r in expected.values())>MAX_TOTAL:
        raise PackageError('Archive bound')
    records = []
    with Path(output).open('xb') as stream:
        with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=ARCHIVE_LEVEL, allowZip64=False) as archive:
            for name, value in sorted(entries.items(), key=lambda item:_ordinal(item[0])):
                record = expected[name]
                info=zipfile.ZipInfo(name,(2026,10,29,0,0,0));info.create_system=3
                info.external_attr=(stat.S_IFREG|0o644)<<16;info.compress_type=zipfile.ZIP_DEFLATED
                # ZipFile's level is not applied to a caller-supplied ZipInfo.
                # Use the public3.13 property or its3.12 backing field.
                if hasattr(info,'compress_level'): info.compress_level=ARCHIVE_LEVEL
                else: info._compresslevel=ARCHIVE_LEVEL
                # The one read of each input: the bytes written are hashed as they are
                # written and must be the verified record, or the archive is refused.
                hashed=hashlib.sha256();size=0
                with archive.open(info,'w') as destination:
                    if isinstance(value,bytes): destination.write(value);hashed.update(value);size=len(value)
                    else:
                        with _unlinked(value,file=True).open('rb') as source_file:
                            while chunk:=source_file.read(1024*1024):
                                size+=len(chunk)
                                if size>record['size']: raise PackageError('Input changed during archive')
                                hashed.update(chunk);destination.write(chunk)
                if size!=record['size'] or hashed.hexdigest()!=record['sha256']: raise PackageError('Input changed during archive')
                records.append(dict(Path=name,Size=size,Sha256=record['sha256']))
        stream.flush();os.fsync(stream.fileno())
    if Path(output).stat().st_size>MAX_TOTAL: raise PackageError('Compressed archive bound')
    return records


def _copy_verified(source, destination, size, sha256):
    # One read of a verified input, hashed as it is copied.
    hashed=hashlib.sha256();copied=0
    with _unlinked(source,file=True).open('rb') as stream:
        while chunk:=stream.read(1024*1024):
            copied+=len(chunk)
            if copied>size: raise PackageError('Input changed during image')
            hashed.update(chunk);destination.write(chunk)
    if copied!=size or hashed.hexdigest()!=sha256: raise PackageError('Input changed during image')


def write_image(output, engine, engine_record, archive, archive_sha256, metadata):
    """[engine][install.zip][install-manifest.json][trailer], each part proven as it is written."""
    archive_size=Path(archive).stat().st_size
    if not (1<=engine_record['size']<=MAX_FILE and 1<=archive_size<=MAX_TOTAL and 1<=len(metadata)<=MAX_MANIFEST): raise PackageError('Setup image bound')
    with Path(output).open('xb') as destination:
        _copy_verified(engine,destination,engine_record['size'],engine_record['sha256'])
        _copy_verified(archive,destination,archive_size,archive_sha256)
        destination.write(metadata);destination.write(IMAGE_TRAILER.pack(IMAGE_MAGIC,engine_record['size'],archive_size,len(metadata)))
        destination.flush();os.fsync(destination.fileno())
    if Path(output).stat().st_size!=engine_record['size']+archive_size+len(metadata)+IMAGE_TRAILER.size: raise PackageError('Setup image changed')


def read_image(path):
    """The three parts' lengths of a Setup image, refusing a trailer that does not describe the file."""
    data=Path(path).read_bytes()
    if len(data)<IMAGE_TRAILER.size: raise PackageError('Setup image trailer')
    magic,engine,archive,manifest=IMAGE_TRAILER.unpack(data[-IMAGE_TRAILER.size:])
    if (magic!=IMAGE_MAGIC or not 1<=engine<=MAX_FILE or not 1<=archive<=MAX_TOTAL or not 1<=manifest<=MAX_MANIFEST or
            engine+archive+manifest+IMAGE_TRAILER.size!=len(data)): raise PackageError('Setup image trailer')
    return dict(engine=data[:engine],archive=data[engine:engine+archive],manifest=data[engine+archive:engine+archive+manifest])


def require_members(names):
    names = set(names)
    if not REQUIRED_MEMBERS<=names: raise PackageError('Required package member missing')


def app_archive_name(version): return 'yahad-windows-app-'+version+'.zip'


def build(repo, app_stage, infra_entry, output, app_archive):
    from . import infra_package
    repo, app_stage, infra_entry = map(_unlinked,(repo,app_stage,infra_entry))
    output, app_archive = Path(output).absolute(), Path(app_archive).absolute()
    if (output.exists() or app_archive.exists() or output==repo or
            any(path.is_relative_to(closure) for path in (output, app_archive) for closure in (app_stage, infra_entry))):
        raise PackageError('New output outside input closures required')
    for ancestor in (*output.parents, *app_archive.parents):
        if ancestor.exists(): _unlinked(ancestor)
    infra = infra_package.verify(infra_entry)
    identity = _source_identity(repo, infra.identity); version, contract, minimum, source, tracked = identity
    if app_archive.name!=app_archive_name(version): raise PackageError('App archive name')
    bound = r'(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})'
    if not re.fullmatch(bound,version) or not re.fullmatch(bound,infra.identity.version): raise PackageError('Package version bound')
    app = bundle.validate(app_stage)
    if app['version']!=version: raise PackageError('Application identity differs')
    schema,migrations=validate_payload_source(repo,app_stage,app['files'],version,source,tracked)
    app_files=_VerifiedFiles({record['path']:app_stage/record['path'] for record in app['files']},root=app_stage)
    if [app_files.records[r['path']] for r in app['files']]!=app['files']: raise PackageError('Verified closure changed')
    app_tree=tree_record(version,source,app_files.records,APP_PREFIXES)
    entries,expected={INFRA_TREE:infra.tree},{INFRA_TREE:_record(INFRA_TREE,infra.tree)}
    for name,path in infra.files.items(): entries[INFRA+name]=path;expected[INFRA+name]=dict(infra.files.records[name],path=INFRA+name)
    entries[APP_TREE]=app_tree;expected[APP_TREE]=_record(APP_TREE,app_tree)
    for name,path in app_files.items(): entries[APPS+name]=path;expected[APPS+name]=dict(app_files.records[name],path=APPS+name)
    require_members(entries)
    # The engine travels as the Setup image's prefix, never as an archive entry.
    engine,engine_record=entries.pop(ENGINE_MEMBER),expected.pop(ENGINE_MEMBER)
    output.mkdir(parents=True,exist_ok=False)
    archive=output/'install.zip';records=_write_archive(archive,entries,expected)
    records=sorted([*records,dict(Path=ENGINE_MEMBER,Size=engine_record['size'],Sha256=engine_record['sha256'])],key=lambda r:_ordinal(r['Path']))
    # The app-only asset: the same verified members and the same app-tree.json bytes.
    shipped={APP_TREE_NAME:app_tree,**app_files}
    _write_archive(app_archive,shipped,{APP_TREE_NAME:_record(APP_TREE_NAME,app_tree),**app_files.records},member=_app_archive_member)
    saved=dict(Format=2,
               Infrastructure=dict(Version=infra.identity.version,ContractMin=infra.identity.contract_min,ContractMax=infra.identity.contract_max,
                                   SourceCommit=infra.source_commit,TreeSha256=infra.tree_sha256),
               App=dict(Version=version,SourceCommit=source,RequiresContract=contract,TreeSha256=_hash(app_tree),
                        DatabaseSchema=schema,DatabaseMigrationsFrom=migrations),
               MinimumFrom=dict(App=minimum['app'],Infrastructure=minimum['infrastructure']),
               ZipSha256=bundle.digest(archive),Files=records)
    metadata=_encode(saved)
    if len(metadata)>MAX_MANIFEST: raise PackageError('Installer manifest bound')
    # Every archived member was proven against its verified record as it was written.
    if _source_identity(repo,infra.identity)!=identity: raise PackageError('Build source changed')
    with (output/'install-manifest.json').open('xb') as destination: destination.write(metadata)
    # Last output, the success prerequisite: the Setup image.
    image=output/ENGINE;write_image(image,engine,engine_record,archive,saved['ZipSha256'],metadata)
    return dict(output=str(output),setup=str(image),setupSha256=bundle.digest(image),appArchive=str(app_archive),infrastructureVersion=infra.identity.version,infraSourceCommit=infra.source_commit,
                infraTreeSha256=infra.tree_sha256,appVersion=version,sourceCommit=source,appTreeSha256=saved['App']['TreeSha256'],
                installSha256=saved['ZipSha256'],files=len(records),machineAcceptance=False,releaseSignatureVerified=False)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('repo','app-stage','infra-entry','output','app-archive'):
        parser.add_argument('--'+name,required=True,type=Path)
    try: print(json.dumps(build(**{k.replace('-','_'):v for k,v in vars(parser.parse_args()).items()}),sort_keys=True))
    except (ValueError,OSError,TypeError,KeyError,AttributeError,subprocess.SubprocessError,zipfile.BadZipFile,zipfile.LargeZipFile) as error:
        raise SystemExit('Install package refused; no installation or publication performed: '+str(error))


if __name__=='__main__': main()
