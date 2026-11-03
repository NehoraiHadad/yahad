"""Offline build-machine packager for the single-service Windows installer.

Produces the Format 1 install package Setup embeds: install.zip with only
payload/ (the verified application payload) and host/ (the published
Yahad.ServiceHost directory), plus install-manifest.json written last.
Never installs or fabricates release receipts; only audit-only --describe entry
points of verified local project builds run. Outputs remain private candidates.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import tempfile
import zipfile

from . import bundle, pe, verify_archive
from .application_version import source_release_version, require_native_build_identity, describe_build_artifact


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
REQUIRED_MEMBERS = frozenset({'payload/bundle.json','payload/runtime/node/node.exe','payload/runtime/ffmpeg/ffmpeg.exe',
    'payload/runtime/caddy/caddy.exe','payload/app/server/main.mjs','payload/app/dist/index.html','payload/app/native/Yahad.Platform.exe',
    'host/Yahad.ServiceHost.exe','host/Yahad.ServiceHost.runtimeconfig.json'})
MAX_FILE, MAX_TOTAL, MAX_FILES = 512 * 1024**2, 2 * 1024**3, 50000
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


def _source_identity(repo):
    repo = _unlinked(repo)
    def git(*args): return subprocess.check_output(['git', '-C', str(repo), *args])
    if git('status', '--porcelain', '--untracked-files=normal').strip(): raise PackageError('Clean committed source required')
    source = git('rev-parse', 'HEAD').decode('ascii').strip()
    if not re.fullmatch('[a-f0-9]{40}', source): raise PackageError('Source commit required')
    packager = _unlinked(Path(__file__), file=True)
    if packager != repo/'installer/windows/setup_package.py' or git('show', 'HEAD:installer/windows/setup_package.py') != packager.read_bytes():
        raise PackageError('Executing packager must match committed source')
    tracked = git('ls-files', '-z', '--', 'src', 'package.json', 'package-lock.json').decode('utf-8').split('\0')
    return source_release_version(repo), source, [name for name in tracked if name]


def source_schema(repo):
    text = _unlinked(Path(repo)/'src/database-schema.ts', file=True).read_text(encoding='utf-8')
    schema = re.findall(r'^export const DATABASE_SCHEMA\s*=\s*([0-9]+);', text, re.M)
    sources = re.findall(r'^export const DATABASE_MIGRATIONS_FROM\s*=\s*\[([0-9,\s]*)\]\s*as const;', text, re.M)
    if len(schema)!=1 or len(sources)!=1: raise PackageError('Source schema declaration required')
    target = int(schema[0]); migrations = [int(v.strip()) for v in sources[0].split(',') if v.strip()]
    if not 1<=target<=64 or len(migrations)>1024 or migrations!=sorted(set(migrations)) or any(not 1<=v<target for v in migrations):
        raise PackageError('Source migration declaration invalid')
    return target, migrations


def validate_payload_source(repo, prepared, manifest, version, source, tracked):
    repo, prepared = Path(repo), Path(prepared)
    if (type(manifest.get('schema')) is not int or manifest['schema']!=1 or manifest.get('version')!=version or manifest.get('profile')!='full' or manifest.get('target')!='win-x64' or
            manifest.get('kind')!='windows-runtime-candidate'):
        raise PackageError('Application identity differs')
    records = {r['path']:r for r in manifest.get('files', [])}; _closed_names(records)
    # The payload runs the bundle only: no source tree, lockfile or node_modules ships.
    if any(name.startswith(('app/src/', 'app/node_modules/')) or name=='app/package-lock.json' for name in records):
        raise PackageError('Application source or dependency tree in payload')
    if _unlinked(repo/'package.json', file=True).read_bytes()!=_unlinked(prepared/'app/package.json', file=True).read_bytes():
        raise PackageError('Application package bytes differ')
    build = _json((prepared/'legal/build-inputs.json').read_bytes(), 65536)
    if any(build.get(field)!=source for field in ('appSourceCommit', 'helperSourceCommit', 'packagerSourceCommit')):
        raise PackageError('Mixed source build inputs')
    schema, migrations = source_schema(repo)
    if (type(build.get('databaseSchema')) is not int or build['databaseSchema']!=schema or
            build.get('databaseMigrationsFrom')!=migrations or any(type(v) is not int for v in build['databaseMigrationsFrom'])):
        raise PackageError('Mixed source database schema')
    # The bundler's record ties every shipped entry to tracked source bytes and to
    # non-development lockfile packages; the payload records pin the entry bytes.
    server = _json((prepared/'legal/server-bundle.json').read_bytes())
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


def validate_native(root, artifact, version, source, repo):
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
    require_native_build_identity(describe_build_artifact(files[entry], '--describe', repo), artifact, version, source)
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


def _write_archive(output, entries, expected=None):
    _closed_names(entries)
    for name in entries:
        if name=='payload/bundle.json' or name.startswith(('payload/app/','payload/runtime/','payload/legal/')): continue
        if not name.startswith('host/'): raise PackageError('Undeclared archive namespace')
        _safe_path(name[len('host/'):],component=True)
    expected=expected if expected is not None else {name:_record(name,value) for name,value in entries.items()}
    if set(expected)!=set(entries) or len(entries)>MAX_FILES or any(not 0<=r['size']<=MAX_FILE for r in expected.values()) or sum(r['size'] for r in expected.values())>MAX_TOTAL:
        raise PackageError('Archive bound')
    records = []
    with Path(output).open('xb') as stream:
        with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=False) as archive:
            for name, value in sorted(entries.items()):
                record = expected[name]
                if _record(name,value)!=record: raise PackageError('Verified archive input changed')
                info=zipfile.ZipInfo(name,(2026,10,29,0,0,0));info.create_system=3
                info.external_attr=(stat.S_IFREG|0o644)<<16;info.compress_type=zipfile.ZIP_DEFLATED
                # ZipFile's level is not applied to a caller-supplied ZipInfo.
                # Use the public3.13 property or its3.12 backing field.
                if hasattr(info,'compress_level'): info.compress_level=9
                else: info._compresslevel=9
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


def require_members(names):
    names = set(names)
    if not REQUIRED_MEMBERS<=names: raise PackageError('Required package member missing')


def build(repo, payload_zip, runtime_host, output):
    repo, payload_zip, runtime_host = map(_unlinked,(repo,payload_zip,runtime_host))
    output = Path(output).absolute()
    if output.exists() or output==repo or output.is_relative_to(runtime_host):
        raise PackageError('New output outside input closures required')
    for ancestor in output.parents:
        if ancestor.exists(): _unlinked(ancestor)
    version, source, tracked = _source_identity(repo)
    if not re.fullmatch(r'(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})',version): raise PackageError('Package version bound')
    output.mkdir(parents=True,exist_ok=False)
    with tempfile.TemporaryDirectory(prefix='yahad-install-inputs-', dir=output) as scratch:
        prepared=Path(scratch)/'payload';verify_archive.verify(_unlinked(payload_zip,file=True),prepared,retain_manifest=True)
        application=(prepared/'bundle.json').read_bytes();manifest=_json(application)
        validate_payload_source(repo,prepared,manifest,version,source,tracked)
        # Platform is part of the verified application, not another caller input.
        validate_native(prepared/'app/native','Yahad.Platform',version,source,repo)
        host=validate_native(runtime_host,'Yahad.ServiceHost',version,source,repo)
        runtime_lock=_json((prepared/'legal/runtime-lock.json').read_bytes(),65536)
        platforms=[c for c in runtime_lock['components'] if c.get('id')=='platform']
        if len(platforms)!=1 or platforms[0].get('sourceCommit')!=source: raise PackageError('Mixed Platform runtime-lock source')
        entries={'payload/bundle.json':application};expected={'payload/bundle.json':_record('payload/bundle.json',application)}
        for record in manifest['files']:
            name='payload/'+record['path'];entries[name]=prepared/record['path'];expected[name]=dict(record,path=name)
        for name,path in host.items():
            entries['host/'+name]=path;expected['host/'+name]=dict(host.records[name],path='host/'+name)
        require_members(entries)
        archive=output/'install.zip';records=_write_archive(archive,entries,expected)
        saved=dict(Format=1,Version=version,SourceCommit=source,ZipSha256=bundle.digest(archive),
                   PayloadDigest=_hash(application),Files=records)
        metadata=_encode(saved)
        if len(metadata)>16*1024**2: raise PackageError('Installer manifest bound')
        host.require_unchanged()
        if _source_identity(repo)!=(version,source,tracked): raise PackageError('Build source changed')
        # Last output is the success prerequisite for Setup's embedded input.
        with (output/'install-manifest.json').open('xb') as destination: destination.write(metadata)
    return dict(output=str(output),version=version,sourceCommit=source,installSha256=saved['ZipSha256'],
                files=len(records),machineAcceptance=False,releaseSignatureVerified=False)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('repo','payload-zip','runtime-host','output'):
        parser.add_argument('--'+name,required=True,type=Path)
    try: print(json.dumps(build(**{k.replace('-','_'):v for k,v in vars(parser.parse_args()).items()}),sort_keys=True))
    except (ValueError,OSError,TypeError,KeyError,AttributeError,subprocess.SubprocessError,zipfile.BadZipFile,zipfile.LargeZipFile) as error:
        raise SystemExit('Install package refused; no installation or publication performed: '+str(error))


if __name__=='__main__': main()
