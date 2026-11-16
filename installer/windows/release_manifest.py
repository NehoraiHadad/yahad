"""The unsigned format-2 release manifest, yahad-windows-release-<v>.json
(docs/WINDOWS-APP-PACKAGE-PLAN.md 5.1; installer/Yahad.Install.Core/ReleaseManifest.cs).

Every identity field is copied from the install manifest the distributable Setup
embeds, so the signed release and the embedded package cannot disagree; the
files are described by their own bytes. The result is checked with the rules of
ReleaseManifest.Parse/Valid before it is written. Signing is the maintainer's own
step (scripts/windows-release-key.mjs sign).
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import zipfile

from .setup_package import _json, _unlinked, app_archive_name, APP_TREE_NAME

FORMAT, PRODUCT, MAXIMUM_BYTES = 2, 'yahad-windows-release', 4096
MAXIMUM_SETUP, MAXIMUM_APP_ARCHIVE, MAXIMUM_SOURCES = 536870912, 2147483648, 32
_VERSION = re.compile(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)')
_COMMIT, _DIGEST, _KEY = re.compile('[a-f0-9]{40}'), re.compile('[a-f0-9]{64}'), re.compile('[a-f0-9]{16}')


class ReleaseError(ValueError): pass


def manifest_name(version): return 'yahad-windows-release-'+version+'.json'
def setup_name(version): return 'Yahad.Setup-'+version+'.exe'


def _version(value): return isinstance(value, str) and len(value) <= 40 and _VERSION.fullmatch(value) is not None
def _order(value): return tuple(int(part) for part in value.split('.'))
def _int(value): return type(value) is int
def _contract(value): return _int(value) and 1 <= value <= 1024


def _file(value, name, maximum):
    return (isinstance(value, dict) and list(value) == ['name', 'sha256', 'bytes'] and value['name'] == name and
            isinstance(value['sha256'], str) and _DIGEST.fullmatch(value['sha256']) is not None and _int(value['bytes']) and 1 <= value['bytes'] <= maximum)


def validate(data):
    """ReleaseManifest.Parse + Valid: strict camelCase shape and the bounds. Returns the value."""
    if not 1 <= len(data) <= MAXIMUM_BYTES or data.startswith(b'\xef\xbb\xbf'): raise ReleaseError('RELEASE_MANIFEST_INVALID: size')
    value = _json(data, MAXIMUM_BYTES)
    shape = {'': ['format', 'product', 'version', 'sourceCommit', 'keyId', 'createdUnix', 'infrastructure', 'app', 'minimumFrom'],
             'infrastructure': ['version', 'contractMin', 'contractMax', 'sourceCommit', 'treeSha256', 'setup'],
             'app': ['archive', 'treeSha256', 'requiresContract', 'databaseSchema', 'databaseMigrationsFrom'],
             'minimumFrom': ['app', 'infrastructure']}
    if not isinstance(value, dict) or list(value) != shape['']: raise ReleaseError('RELEASE_MANIFEST_INVALID: fields')
    for name in ('infrastructure', 'app', 'minimumFrom'):
        if not isinstance(value[name], dict) or list(value[name]) != shape[name]: raise ReleaseError('RELEASE_MANIFEST_INVALID: '+name+' fields')
    i, a, m = value['infrastructure'], value['app'], value['minimumFrom']
    version = value['version']
    valid = (_int(value['format']) and value['format'] == FORMAT and value['product'] == PRODUCT and _version(version) and
        isinstance(value['sourceCommit'], str) and _COMMIT.fullmatch(value['sourceCommit']) and isinstance(value['keyId'], str) and
        _KEY.fullmatch(value['keyId']) and _int(value['createdUnix']) and value['createdUnix'] >= 1 and
        _version(i['version']) and _contract(i['contractMin']) and _contract(i['contractMax']) and i['contractMin'] <= i['contractMax'] and
        isinstance(i['sourceCommit'], str) and _COMMIT.fullmatch(i['sourceCommit']) and isinstance(i['treeSha256'], str) and
        _DIGEST.fullmatch(i['treeSha256']) and _file(i['setup'], setup_name(version), MAXIMUM_SETUP) and
        _file(a['archive'], app_archive_name(version), MAXIMUM_APP_ARCHIVE) and isinstance(a['treeSha256'], str) and
        _DIGEST.fullmatch(a['treeSha256']) and _contract(a['requiresContract']) and _int(a['databaseSchema']) and
        1 <= a['databaseSchema'] <= 1024 and isinstance(a['databaseMigrationsFrom'], list) and
        len(a['databaseMigrationsFrom']) <= MAXIMUM_SOURCES and all(_int(s) and 1 <= s < a['databaseSchema'] for s in a['databaseMigrationsFrom']) and
        len(set(a['databaseMigrationsFrom'])) == len(a['databaseMigrationsFrom']) and
        _version(m['app']) and _version(m['infrastructure']))
    if not valid or _order(m['app']) > _order(version) or _order(m['infrastructure']) > _order(i['version']):
        raise ReleaseError('RELEASE_MANIFEST_INVALID: values')
    return value


def _described(path, name, maximum):
    # The asset bound is the manifest's own (the app archive may exceed one member's bound).
    path = _unlinked(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or not 1 <= info.st_size <= maximum: raise ReleaseError('Unsafe release file: '+name)
    if path.name != name: raise ReleaseError('Release file name: expected '+name)
    with path.open('rb') as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    if path.lstat().st_size != info.st_size: raise ReleaseError('Release file changed: '+name)
    return dict(name=name, sha256=digest, bytes=info.st_size)


def compose(install_manifest, setup, app_archive, key_id, created_unix):
    """The manifest bytes: compact, in the field order of the plan's example."""
    package = _json(Path(install_manifest).read_bytes(), 16*1024**2)
    infra, app, minimum = package['Infrastructure'], package['App'], package['MinimumFrom']
    version = app['Version']
    archive = _described(app_archive, app_archive_name(version), MAXIMUM_APP_ARCHIVE)
    # The asset's app-tree.json is the embedded package's: the digest both name.
    with zipfile.ZipFile(app_archive) as opened:
        if hashlib.sha256(opened.read(APP_TREE_NAME)).hexdigest() != app['TreeSha256']: raise ReleaseError('App archive tree differs from the install package')
    value = dict(format=FORMAT, product=PRODUCT, version=version, sourceCommit=app['SourceCommit'], keyId=key_id, createdUnix=created_unix,
                 infrastructure=dict(version=infra['Version'], contractMin=infra['ContractMin'], contractMax=infra['ContractMax'],
                                     sourceCommit=infra['SourceCommit'], treeSha256=infra['TreeSha256'], setup=_described(setup, setup_name(version), MAXIMUM_SETUP)),
                 app=dict(archive=archive, treeSha256=app['TreeSha256'], requiresContract=app['RequiresContract'],
                          databaseSchema=app['DatabaseSchema'], databaseMigrationsFrom=app['DatabaseMigrationsFrom']),
                 minimumFrom=dict(app=minimum['App'], infrastructure=minimum['Infrastructure']))
    data = json.dumps(value, separators=(',', ':'), ensure_ascii=True).encode('ascii')
    validate(data)
    return data


def write(install_manifest, setup, app_archive, key_id, created_unix, output):
    data = compose(install_manifest, setup, app_archive, key_id, created_unix)
    output = Path(output)
    if output.name != manifest_name(json.loads(data)['version']): raise ReleaseError('Release manifest name')
    with output.open('xb') as destination: destination.write(data)
    return json.loads(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('install-manifest', 'setup', 'app-archive', 'output'): parser.add_argument('--'+name, required=True, type=Path)
    parser.add_argument('--key-id', required=True); parser.add_argument('--created-unix', required=True, type=int)
    args = parser.parse_args()
    try: print(json.dumps(write(args.install_manifest, args.setup, args.app_archive, args.key_id, args.created_unix, args.output)))
    except (ValueError, OSError, KeyError, TypeError, zipfile.BadZipFile) as error: raise SystemExit('Release manifest refused: '+str(error))


if __name__ == '__main__': main()
