"""The infrastructure tree, built once per infrastructure version and cached
(docs/WINDOWS-APP-PACKAGE-PLAN.md section 10, decision 7).

A cache entry is <cache>/<infrastructure version>/ holding exactly:
- tree/: the infra\\<v>\\ tree an install receives: host/ (Yahad.ServiceHost),
  updater/ (Yahad.Updater), platform/ (Yahad.Platform), setup/Yahad.Setup.exe
  (the payload-free engine, 4.4), runtime/node/ and runtime/caddy/ (pinned in
  dependency-pins.json), legal/ (runtime-lock.json, the infrastructure SBOM of
  Node, Caddy and .NET, their license texts and the Yahad license) and
  infra-tree.json, the builder's member record of it;
- infra-build.json: {format, infrastructureVersion, contractMin, contractMax,
  sourceCommit, inputDigest, treeSha256, releaseKeyId}. sourceCommit is the
  infrastructure source commit every image reports; inputDigest is
  infra_inputs.py's digest of it; releaseKeyId the key compiled into the images.

An entry is written under a partial name and renamed into place whole, never
replaced. Reuse is decided by plan(): an entry of the same version is reused only
when its input digest, source commit and release key equal the source's, and only
after every member is audited by bytes against infra-tree.json (verify()). The
same version with another input digest is INFRA_BUMP_REQUIRED: one infrastructure
version names exactly one tree.

CLI (installer/package.ps1; cached: scripts/release-windows.mjs):
  plan   --cache C --source S --release-key-id K|none
  cached --cache C --version V   the validated build record of C/<V>, or null
  build  --cache C --source S --release-key-id K|none --host H --updater U --platform P
         --engine E --node N --dotnet D --vendors V
  verify --entry C/<v>
"""
import argparse
from collections import namedtuple
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import uuid

from . import dependencies, infra_inputs
from .application_version import source_infrastructure_identity, Infrastructure, _stable
from .setup_package import (PackageError, _VerifiedFiles, _inventory, _json, _record, _hash, _unlinked, _inspect,
    validate_native, validate_engine, tree_record, parse_tree_record, COMPONENTS, ENGINE, INFRA_PREFIXES, INFRA_REQUIRED,
    INFRA_TREE_NAME, RUNTIME_VERSION)

BUILD_RECORD, TREE = 'infra-build.json', 'tree'
_RECORD_FIELDS = ['contractMax', 'contractMin', 'format', 'infrastructureVersion', 'inputDigest', 'releaseKeyId', 'sourceCommit', 'treeSha256']
Entry = namedtuple('Entry', ('record', 'identity', 'source_commit', 'input_digest', 'tree_sha256', 'release_key_id', 'tree', 'files'))


class InfraError(PackageError): pass


def _key_id(value):
    if value == 'none': return None
    if not isinstance(value, str) or not re.fullmatch('[a-f0-9]{16}', value): raise InfraError('Release key id: 16 lowercase hex or none')
    return value


def committed_source(source):
    """(HEAD, identity, input digest) of a clean infrastructure source checkout."""
    source = _unlinked(source)
    def git(*args): return subprocess.check_output(['git', '-C', str(source), *args])
    if git('status', '--porcelain', '--untracked-files=normal').strip(): raise InfraError('Clean committed infrastructure source required')
    commit = git('rev-parse', 'HEAD').decode('ascii').strip()
    if not re.fullmatch('[a-f0-9]{40}', commit): raise InfraError('Infrastructure source commit required')
    return commit, source_infrastructure_identity(source), infra_inputs.digest(source, commit)


def require_executing_packager(source, commit):
    """The running installer/windows/ is the infrastructure source's, byte for byte,
    so the cached tree is what that commit's packager writes."""
    running = Path(__file__).resolve().parent
    tracked = {path[len('installer/windows/'):]:oid for path, _, oid in infra_inputs.entries(source, commit) if path.startswith('installer/windows/')}
    present = {path.relative_to(running).as_posix() for path in running.rglob('*') if path.is_file() and '__pycache__' not in path.parts}
    if present != set(tracked): raise InfraError('Executing packager differs from the infrastructure source')
    for name, oid in tracked.items():
        data = (running/name).read_bytes()
        algorithm = hashlib.sha1 if len(oid) == 40 else hashlib.sha256
        if algorithm(b'blob %d\0' % len(data) + data).hexdigest() != oid: raise InfraError('Executing packager differs from the infrastructure source')


def read_record(entry):
    record = _json((Path(entry)/BUILD_RECORD).read_bytes(), 65536)
    if (not isinstance(record, dict) or sorted(record) != _RECORD_FIELDS or type(record['format']) is not int or record['format'] != 1 or
            not isinstance(record['infrastructureVersion'], str) or any(type(record[f]) is not int for f in ('contractMin', 'contractMax')) or
            not isinstance(record['sourceCommit'], str) or not re.fullmatch('[a-f0-9]{40}', record['sourceCommit']) or
            any(not isinstance(record[f], str) or not re.fullmatch('[a-f0-9]{64}', record[f]) for f in ('inputDigest', 'treeSha256')) or
            record['releaseKeyId'] is not None and (not isinstance(record['releaseKeyId'], str) or not re.fullmatch('[a-f0-9]{16}', record['releaseKeyId']))):
        raise InfraError('INFRA_CACHE_CORRUPT: build record')
    return record


def verify(entry):
    """Audit one cache entry by bytes: exactly its record and tree; the tree record's
    bytes are the recorded digest and name the recorded identity; the tree holds
    exactly the recorded members, each with its recorded size and SHA-256."""
    entry = _unlinked(entry)
    try:
        if sorted(path.name for path in entry.iterdir()) != [BUILD_RECORD, TREE]: raise InfraError('INFRA_CACHE_CORRUPT: entry layout')
        record = read_record(entry)
        if record['infrastructureVersion'] != entry.name: raise InfraError('INFRA_CACHE_CORRUPT: entry version')
        identity = Infrastructure(record['infrastructureVersion'], record['contractMin'], record['contractMax'])
        found = _inventory(entry/TREE)
        tree = _unlinked(found.pop(INFRA_TREE_NAME), file=True).read_bytes() if INFRA_TREE_NAME in found else None
        if tree is None or _hash(tree) != record['treeSha256']: raise InfraError('INFRA_CACHE_CORRUPT: tree record digest')
        parsed = parse_tree_record(tree, INFRA_PREFIXES)
        if parsed['version'] != identity.version or parsed['sourceCommit'] != record['sourceCommit']: raise InfraError('INFRA_CACHE_CORRUPT: tree identity')
        files = _VerifiedFiles(found)
        if files.records != {item['path']:item for item in parsed['files']}: raise InfraError('INFRA_CACHE_CORRUPT: tree members')
        if not INFRA_REQUIRED <= files.keys(): raise InfraError('INFRA_CACHE_CORRUPT: required member missing')
    except (OSError, ValueError, KeyError, TypeError) as error:
        if isinstance(error, InfraError): raise
        raise InfraError('INFRA_CACHE_CORRUPT: '+str(error)) from error
    return Entry(record, identity, record['sourceCommit'], record['inputDigest'], record['treeSha256'], record['releaseKeyId'], tree, files)


def describe(entry):
    return dict(infrastructureVersion=entry.identity.version, contractMin=entry.identity.contract_min, contractMax=entry.identity.contract_max,
                sourceCommit=entry.source_commit, inputDigest=entry.input_digest, treeSha256=entry.tree_sha256, releaseKeyId=entry.release_key_id)


def cached(cache, version):
    """The build record of the cache entry for infrastructure `version`, or None
    when the cache has none. Read-only: it says which commit and inputs that
    version was built from, so a release command can hand package.ps1 the
    -InfraSource plan() requires; plan() and verify() still audit the tree."""
    if not _stable(version): raise InfraError('Infrastructure version required: '+repr(version))
    entry = Path(cache).absolute()/version
    if not entry.exists(): return None
    record = read_record(_unlinked(entry))
    if record['infrastructureVersion'] != version: raise InfraError('INFRA_CACHE_CORRUPT: entry version')
    return record


def plan(cache, source, release_key_id):
    """'build' when the cache has no entry for the source's version, 'reuse' when its
    entry is this source's own build; anything else refuses."""
    key = _key_id(release_key_id)
    commit, identity, digest = committed_source(source)
    entry = Path(cache).absolute()/identity.version
    if not entry.exists():
        return dict(action='build', infrastructureVersion=identity.version, contractMin=identity.contract_min, contractMax=identity.contract_max,
                    sourceCommit=commit, inputDigest=digest, releaseKeyId=key)
    record = read_record(_unlinked(entry))
    if record['inputDigest'] != digest:
        raise InfraError(f'INFRA_BUMP_REQUIRED: infrastructure {identity.version} is cached from inputs {record["inputDigest"]}; '
                         f'this source has {digest}. Raise YahadInfrastructureVersion in installer/Directory.Build.props.')
    if record['sourceCommit'] != commit:
        raise InfraError(f'INFRA_SOURCE_MISMATCH: infrastructure {identity.version} was built at {record["sourceCommit"]}; '
                         f'publish the Setup from a checkout of that commit (-InfraSource).')
    if record['releaseKeyId'] != key:
        raise InfraError(f'INFRA_KEY_MISMATCH: infrastructure {identity.version} trusts key {record["releaseKeyId"]}, not {key}.')
    verified = verify(entry)
    if verified.identity != identity: raise InfraError('INFRA_BUMP_REQUIRED: cached contract range differs from the source')
    return dict(action='reuse', **describe(verified))


def _copy(source, target, record):
    target.parent.mkdir(parents=True, exist_ok=True)
    with _unlinked(source, file=True).open('rb') as reading, target.open('xb') as writing: shutil.copyfileobj(reading, writing, 1024*1024)
    if _record(record['path'], target) != record: raise InfraError('Infrastructure input changed while copied')


def _write(target, data):
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open('xb') as writing: writing.write(data)


def legal_records(identity, commit, node, caddy):
    """runtime-lock.json and the infrastructure SBOM: Node, Caddy and the .NET runtime."""
    lock = dict(schema=1, target='win-x64', components=[node, caddy])
    supplements = json.loads(Path(__file__).with_name('licenses').joinpath('sources.json').read_text(encoding='utf-8'))
    sbom = dict(format='yahad-infrastructure-inventory', schema=1, infrastructureVersion=identity.version, sourceCommit=commit,
                runtimes=[node, caddy],
                dotnet=dict(runtimeVersion=RUNTIME_VERSION, selfContained=True, license='MIT',
                            images=['host/Yahad.ServiceHost.exe', 'updater/Yahad.Updater.exe', 'platform/Yahad.Platform.exe', 'setup/'+ENGINE],
                            notices=['legal/DotNet-LICENSE.txt', 'legal/DotNet-ThirdPartyNotices.txt']),
                supplements=supplements, distributionLegalReview='pending-corresponding-sources-and-transitive-notices')
    encode = lambda value: (json.dumps(value, indent=2, sort_keys=True)+'\n').encode('utf-8')
    return {'legal/runtime-lock.json':encode(lock), 'legal/SBOM.json':encode(sbom)}


def build(cache, source, release_key_id, host, updater, platform, engine, node, dotnet, vendors):
    key = _key_id(release_key_id)
    commit, identity, digest = committed_source(source)
    source = _unlinked(source)
    require_executing_packager(source, commit)
    cache = Path(cache).absolute()
    entry = cache/identity.version
    if entry.exists(): raise InfraError('INFRA_CACHE_PRESENT: '+str(entry))
    # Every input verified before any output: the .NET images report this source's
    # infrastructure identity and commit; Node and Caddy are the pinned bytes.
    components = [(prefix, validate_native(root, artifact, identity, commit, source)) for (prefix, artifact), root in zip(COMPONENTS, (host, updater, platform))]
    components.append(('setup/', validate_engine(engine, identity, commit, source)))
    node_files = dependencies.validate_node(node)
    vendor_files = _inventory(vendors)
    caddy = dependencies.validate_staged(vendor_files)['caddy']
    pinned = {record['path'] for source_spec in dependencies.pins()['sources'].values() for record in source_spec['files']}
    dotnet = _unlinked(dotnet)
    inputs = {}
    for prefix, files in components:
        for name, path in files.items(): inputs[prefix+name] = (path, dict(files.records[name], path=prefix+name))
    # A list, not a merged dict: two inputs naming one member must be seen, not overwritten.
    for name, path in [*node_files.items(), *((name, vendor_files[name]) for name in sorted(pinned)),
                       ('legal/dependency-proof.json', vendor_files['legal/dependency-proof.json']),
                       ('legal/DotNet-LICENSE.txt', dotnet/'LICENSE.txt'), ('legal/DotNet-ThirdPartyNotices.txt', dotnet/'ThirdPartyNotices.txt'),
                       ('legal/Yahad-LICENSE.txt', source/'LICENSE')]:
        if name in inputs: raise InfraError('Infrastructure member declared twice: '+name)
        inputs[name] = (path, _record(name, path))
    generated = {'legal/Caddy-NOTICES.txt':dependencies.CADDY_NOTICES, **legal_records(identity, commit, dependencies.node_component(), caddy)}
    cache.mkdir(parents=True, exist_ok=True); _unlinked(cache)
    partial = cache/('.partial-'+identity.version+'-'+uuid.uuid4().hex)
    tree = partial/TREE
    tree.mkdir(parents=True)
    try:
        for name, (path, record) in sorted(inputs.items()): _copy(path, tree/name, record)
        for name, data in generated.items():
            if name in inputs: raise InfraError('Infrastructure member declared twice: '+name)
            _write(tree/name, data)
        dependencies.add_license_texts(tree)
        files = _VerifiedFiles(_inventory(tree), root=tree)
        for name, (_, record) in inputs.items():
            if files.records.get(name) != record: raise InfraError('Infrastructure input changed while copied')
        if not INFRA_REQUIRED <= files.keys() or any(not name.startswith(INFRA_PREFIXES) for name in files): raise InfraError('Infrastructure tree members')
        _inspect(files)
        data = tree_record(identity.version, commit, files.records, INFRA_PREFIXES)
        files.require_unchanged()
        _write(tree/INFRA_TREE_NAME, data)
        record = dict(format=1, infrastructureVersion=identity.version, contractMin=identity.contract_min, contractMax=identity.contract_max,
                      sourceCommit=commit, inputDigest=digest, treeSha256=_hash(data), releaseKeyId=key)
        _write(partial/BUILD_RECORD, (json.dumps(record, indent=2, sort_keys=True)+'\n').encode('utf-8'))
        for _, component in components: component.require_unchanged()
        if committed_source(source) != (commit, identity, digest): raise InfraError('Infrastructure source changed during the build')
        partial.rename(entry)
    except BaseException:
        shutil.rmtree(partial, ignore_errors=True); raise
    return verify(entry)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='operation', required=True)
    planning = sub.add_parser('plan'); building = sub.add_parser('build'); verifying = sub.add_parser('verify'); reading = sub.add_parser('cached')
    for parser_ in (planning, building):
        for name in ('cache', 'source'): parser_.add_argument('--'+name, required=True, type=Path)
        parser_.add_argument('--release-key-id', required=True)
    for name in ('host', 'updater', 'platform', 'engine', 'node', 'dotnet', 'vendors'): building.add_argument('--'+name, required=True, type=Path)
    verifying.add_argument('--entry', required=True, type=Path)
    reading.add_argument('--cache', required=True, type=Path); reading.add_argument('--version', required=True)
    args = vars(parser.parse_args()); operation = args.pop('operation')
    try:
        if operation == 'plan': result = plan(**args)
        elif operation == 'build': result = describe(build(**args))
        elif operation == 'cached': result = cached(**args)
        else: result = describe(verify(args['entry']))
        print(json.dumps(result, sort_keys=True))
    except (ValueError, OSError, TypeError, KeyError, subprocess.SubprocessError) as error:
        raise SystemExit('Infrastructure refused: '+str(error))


if __name__ == '__main__': main()
