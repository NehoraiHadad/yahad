"""The infrastructure build cache: built once per version, reused only as its own
build, audited by bytes. Fixture executables are never invoked; Git and the .NET
describe audit are stubbed at their boundaries."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from installer.windows import infra_package as cache, setup_package as full, dependencies
from installer.windows.application_version import Infrastructure
from tests.test_windows_setup_package import aot, image, infra_entry, put, INFRA, INFRA_SOURCE

DIGEST, KEY = 'd'*64, '0123456789abcdef'


class CacheTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='yahad-infra-cache-'); self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name); self.cache = self.root/'cache'; self.source = self.root/'source'; self.source.mkdir()
        self.state = (INFRA_SOURCE, INFRA, DIGEST)
        patcher = patch.object(cache, 'committed_source', side_effect=lambda source: self.state); patcher.start(); self.addCleanup(patcher.stop)

    def plan(self, key=None): return cache.plan(self.cache, self.source, key or 'none')

    def test_no_entry_plans_a_build_of_the_source_identity(self):
        self.assertEqual(self.plan(KEY), dict(action='build', infrastructureVersion='1.0.0', contractMin=1, contractMax=1,
                                              sourceCommit=INFRA_SOURCE, inputDigest=DIGEST, releaseKeyId=KEY))

    def test_own_build_is_reused_after_a_byte_audit(self):
        entry = infra_entry(self.cache, KEY)
        planned = self.plan(KEY)
        self.assertEqual(planned['action'], 'reuse')
        self.assertEqual((planned['sourceCommit'], planned['inputDigest'], planned['releaseKeyId']), (INFRA_SOURCE, DIGEST, KEY))
        self.assertEqual(planned['treeSha256'], hashlib.sha256((entry/'tree'/full.INFRA_TREE_NAME).read_bytes()).hexdigest())

    def test_same_version_with_other_inputs_requires_a_bump(self):
        infra_entry(self.cache)
        self.state = (INFRA_SOURCE, INFRA, 'e'*64)
        with self.assertRaisesRegex(cache.InfraError, 'INFRA_BUMP_REQUIRED'): self.plan()
        # Equal inputs under another contract range are not the same infrastructure either.
        self.state = (INFRA_SOURCE, Infrastructure('1.0.0', 1, 2), DIGEST)
        with self.assertRaisesRegex(cache.InfraError, 'INFRA_BUMP_REQUIRED'): self.plan()

    def test_same_inputs_from_another_commit_must_publish_from_the_cached_commit(self):
        infra_entry(self.cache)
        self.state = ('f'*40, INFRA, DIGEST)
        with self.assertRaisesRegex(cache.InfraError, 'INFRA_SOURCE_MISMATCH'): self.plan()

    def test_release_key_is_part_of_the_identity(self):
        infra_entry(self.cache, KEY)
        for key in ('none', 'fedcba9876543210'):
            with self.subTest(key=key), self.assertRaisesRegex(cache.InfraError, 'INFRA_KEY_MISMATCH'): self.plan(key)
        for key in ('ABCDEF0123456789', '0123', None, ''):
            with self.subTest(key=key), self.assertRaises(cache.InfraError): cache.plan(self.cache, self.source, key)

    def test_cached_reads_the_validated_record_of_a_version_or_none(self):
        self.assertIsNone(cache.cached(self.cache, '1.0.0'))
        entry = infra_entry(self.cache, KEY)
        self.assertEqual(cache.cached(self.cache, '1.0.0'), json.loads((entry/'infra-build.json').read_bytes()))
        self.assertIsNone(cache.cached(self.cache, '1.0.1'))
        for version in ('../1.0.0', '1.0', '01.0.0', '', None):
            with self.subTest(version=version), self.assertRaisesRegex(cache.InfraError, 'Infrastructure version required'): cache.cached(self.cache, version)
        # The record is validated, and it must be the record of the version it is filed under.
        moved = entry.rename(self.cache/'1.0.1')
        with self.assertRaisesRegex(cache.InfraError, 'INFRA_CACHE_CORRUPT: entry version'): cache.cached(self.cache, '1.0.1')
        value = json.loads((moved/'infra-build.json').read_bytes()); value['sourceCommit'] = 'F'*40
        put(moved, 'infra-build.json', json.dumps(value))
        with self.assertRaisesRegex(cache.InfraError, 'INFRA_CACHE_CORRUPT: build record'): cache.cached(self.cache, '1.0.1')

    def test_cached_command_prints_one_json_line(self):
        def run(version):
            done = subprocess.run([sys.executable, '-m', 'installer.windows.infra_package', 'cached', '--cache', str(self.cache), '--version', version],
                                  cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True)
            return done.returncode, done.stdout.splitlines(), done.stderr
        self.assertEqual(run('1.0.0')[:2], (0, ['null']))
        entry = infra_entry(self.cache, KEY)
        code, lines, _ = run('1.0.0')
        self.assertEqual((code, len(lines)), (0, 1))
        self.assertEqual(json.loads(lines[0]), json.loads((entry/'infra-build.json').read_bytes()))
        code, lines, error = run('../x')
        self.assertNotEqual(code, 0); self.assertEqual(lines, []); self.assertIn('Infrastructure refused: Infrastructure version required', error)

    def test_corrupt_entries_are_refused_by_the_audit(self):
        def changed_member(entry): (entry/'tree/runtime/node/node.exe').write_bytes(image()+b'changed')
        def extra_member(entry): put(entry/'tree', 'host/extra.dll', image())
        def missing_member(entry): (entry/'tree/legal/SBOM.json').unlink()
        def extra_entry_file(entry): put(entry, 'notes.txt', 'x')
        def changed_tree_record(entry): (entry/'tree'/full.INFRA_TREE_NAME).write_bytes((entry/'tree'/full.INFRA_TREE_NAME).read_bytes()+b' ')
        def record(**changed):
            def apply(entry):
                path = entry/'infra-build.json'; value = json.loads(path.read_bytes()); value.update(changed); path.write_text(json.dumps(value))
            return apply
        for corrupt in (changed_member, extra_member, missing_member, extra_entry_file, changed_tree_record, record(format=True),
                        record(treeSha256='0'*64), record(extra=1), record(sourceCommit='F'*40), record(releaseKeyId='xyz')):
            with self.subTest(corrupt=getattr(corrupt, '__name__', corrupt)):
                shutil.rmtree(self.cache, ignore_errors=True); entry = infra_entry(self.cache); corrupt(entry)
                with self.assertRaisesRegex(cache.InfraError, 'INFRA_CACHE_CORRUPT'): cache.verify(entry)
                with self.assertRaisesRegex(cache.InfraError, 'INFRA_CACHE_CORRUPT'): self.plan()

    def test_entry_directory_must_name_its_version_and_hold_a_required_closure(self):
        entry = infra_entry(self.cache); moved = entry.rename(self.cache/'1.0.1')
        with self.assertRaisesRegex(cache.InfraError, 'entry version'): cache.verify(moved)
        shutil.rmtree(self.cache)
        # A self-consistent tree that lacks a required member is still refused.
        entry = infra_entry(self.cache); (entry/'tree/setup/Yahad.Setup.exe').unlink(); tree = entry/'tree'
        (tree/full.INFRA_TREE_NAME).unlink()
        data = full.tree_record(INFRA.version, INFRA_SOURCE, full._VerifiedFiles(full._inventory(tree)).records, full.INFRA_PREFIXES)
        put(tree, full.INFRA_TREE_NAME, data)
        value = json.loads((entry/'infra-build.json').read_bytes()); value['treeSha256'] = hashlib.sha256(data).hexdigest()
        put(entry, 'infra-build.json', json.dumps(value))
        with self.assertRaisesRegex(cache.InfraError, 'required member'): cache.verify(entry)


class BuildTests(unittest.TestCase):
    """build() with the .NET images' describe audit, Git and the pinned Node and
    Caddy checks stubbed at their boundaries; everything written is real."""
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='yahad-infra-build-'); self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name); self.cache = self.root/'cache'; self.source = self.root/'source'
        put(self.source, 'LICENSE', 'Yahad license\n')
        self.components = {}
        for artifact in ('Yahad.ServiceHost', 'Yahad.Updater', 'Yahad.Platform'):
            root = self.root/artifact
            put(root, artifact+'.exe', aot())  # a NativeAOT publish: the image alone
            self.components[artifact] = root
        self.engine = put(self.root/'engine', 'Yahad.Setup.exe', image())
        self.node = self.root/'node'
        node_files = {'runtime/node/node.exe':put(self.node, 'node.exe', image()), 'legal/Node-LICENSE.txt':put(self.node, 'LICENSE', 'node license\n')}
        self.vendors = self.root/'vendors'
        for name in ('runtime/caddy/caddy.exe', 'legal/Caddy-LICENSE.txt', 'legal/Caddy-README.md', 'legal/dependency-proof.json'):
            put(self.vendors, name, image() if name.endswith('.exe') else 'fixture '+name)
        self.dotnet = self.root/'dotnet'
        for name in ('LICENSE.txt', 'ThirdPartyNotices.txt'): put(self.dotnet, name, 'dotnet '+name)
        self.state = [(INFRA_SOURCE, INFRA, DIGEST)]
        caddy = dict(id='caddy', path='runtime/caddy/caddy.exe', sha256='0'*64, version='2', origin='official-vendor', publisher='Caddy',
                     sourceUrl='https://example.invalid/caddy.zip', archiveSha256='1'*64, license='Apache-2.0')
        native = lambda root, artifact, infrastructure, commit, source: full._VerifiedFiles(full._inventory(root), root=root)
        engine = lambda path, infrastructure, commit, source: full._VerifiedFiles({'Yahad.Setup.exe':path})
        for target, value in ((cache, dict(committed_source=lambda source: self.state[0] if len(self.state) == 1 else self.state.pop(0),
                                            require_executing_packager=lambda source, commit: None, validate_native=native, validate_engine=engine)),
                              (dependencies, dict(validate_node=lambda directory: node_files, validate_staged=lambda files: {'caddy':caddy}))):
            for name, replacement in value.items():
                patcher = patch.object(target, name, side_effect=replacement); patcher.start(); self.addCleanup(patcher.stop)
        # infra_package never sees a link in a temporary directory either.

    def build(self, key='none'):
        c = self.components
        return cache.build(self.cache, self.source, key, c['Yahad.ServiceHost'], c['Yahad.Updater'], c['Yahad.Platform'], self.engine,
                           self.node, self.dotnet, self.vendors)

    def test_build_writes_a_verified_entry_that_the_next_plan_reuses(self):
        built = self.build(KEY)
        self.assertEqual(sorted(p.name for p in self.cache.iterdir()), ['1.0.0'])
        self.assertEqual(cache.describe(built), cache.describe(cache.verify(self.cache/'1.0.0')))
        self.assertEqual((built.source_commit, built.input_digest, built.release_key_id), (INFRA_SOURCE, DIGEST, KEY))
        self.assertEqual(cache.plan(self.cache, self.source, KEY)['action'], 'reuse')
        record = (self.cache/'1.0.0/infra-build.json').read_bytes()
        self.assertTrue(record.endswith(b'\n')); self.assertNotIn(b'\r', record)
        with self.assertRaisesRegex(cache.InfraError, 'INFRA_CACHE_PRESENT'): self.build(KEY)

    def test_tree_is_the_infrastructure_and_nothing_of_the_app(self):
        tree = self.cache/'1.0.0/tree'; self.build()
        names = {p.relative_to(tree).as_posix() for p in tree.rglob('*') if p.is_file()}
        self.assertTrue(full.INFRA_REQUIRED <= names)
        self.assertTrue(all(n == full.INFRA_TREE_NAME or n.startswith(full.INFRA_PREFIXES) for n in names))
        for prefix, artifact in full.COMPONENTS:
            self.assertEqual({n for n in names if n.startswith(prefix)}, {prefix+p.name for p in self.components[artifact].iterdir()})
        self.assertEqual({n for n in names if n.startswith('setup/')}, {'setup/Yahad.Setup.exe'})
        self.assertEqual({n for n in names if n.startswith('runtime/')}, {'runtime/node/node.exe', 'runtime/caddy/caddy.exe'})
        self.assertFalse(any(n.startswith('app/') or n in ('legal/server-bundle.json', 'legal/ThirdPartyNotices-server.txt', 'legal/build-inputs.json') for n in names))
        self.assertEqual((tree/'legal/Caddy-NOTICES.txt').read_bytes(), dependencies.CADDY_NOTICES)
        self.assertEqual((tree/'legal/Yahad-LICENSE.txt').read_bytes(), (self.source/'LICENSE').read_bytes())

    def test_infrastructure_sbom_and_runtime_lock_name_node_caddy_and_dotnet_only(self):
        tree = self.cache/'1.0.0/tree'; self.build()
        lock = json.loads((tree/'legal/runtime-lock.json').read_bytes()); sbom = json.loads((tree/'legal/SBOM.json').read_bytes())
        self.assertEqual([c['id'] for c in lock['components']], ['node', 'caddy'])
        self.assertEqual(lock['components'][0], dependencies.node_component())
        self.assertEqual(sbom['format'], 'yahad-infrastructure-inventory')
        self.assertEqual((sbom['infrastructureVersion'], sbom['sourceCommit']), ('1.0.0', INFRA_SOURCE))
        self.assertEqual([r['id'] for r in sbom['runtimes']], ['node', 'caddy'])
        self.assertEqual(sbom['dotnet']['runtimeVersion'], full.RUNTIME_VERSION)
        self.assertNotIn('selfContained', sbom['dotnet'])
        self.assertEqual(sbom['dotnet']['deployment'], {'host/Yahad.ServiceHost.exe':'native-aot', 'updater/Yahad.Updater.exe':'native-aot',
                                                        'platform/Yahad.Platform.exe':'native-aot', 'setup/Yahad.Setup.exe':'self-contained-single-file'})
        self.assertEqual(sorted(sbom['dotnet']['deployment']), sorted(sbom['dotnet']['images']))
        self.assertTrue(all((tree/name).is_file() for name in sbom['dotnet']['images']+sbom['dotnet']['notices']))
        self.assertNotIn('serverBundleDependencies', sbom)

    def test_failed_or_changed_builds_leave_no_entry(self):
        with patch.object(dependencies, 'validate_node', side_effect=dependencies.DependencyError('NODE_PIN')), self.assertRaises(dependencies.DependencyError):
            self.build()
        self.assertFalse(self.cache.exists() and any(self.cache.iterdir()))
        self.state[:] = [(INFRA_SOURCE, INFRA, DIGEST), ('f'*40, INFRA, DIGEST)]
        with self.assertRaisesRegex(cache.InfraError, 'source changed'): self.build()
        self.assertEqual(list(self.cache.iterdir()), [])

    def test_a_member_declared_twice_is_refused(self):
        put(self.vendors, 'legal/Node-LICENSE.txt', 'shadow')
        with patch.object(dependencies, 'pins', return_value=dict(dependencies.pins(), sources=dict(x=dict(files=[dict(path='legal/Node-LICENSE.txt')])))):
            with self.assertRaisesRegex(cache.InfraError, 'declared twice'): self.build()


class ExecutingPackagerTests(unittest.TestCase):
    def test_running_packager_must_be_the_infrastructure_sources_bytes(self):
        running = Path(cache.__file__).resolve().parent
        files = sorted(p for p in running.rglob('*') if p.is_file() and '__pycache__' not in p.parts)
        def oid(path): data = path.read_bytes(); return hashlib.sha1(b'blob %d\0' % len(data) + data).hexdigest()
        entries = [('installer/windows/'+p.relative_to(running).as_posix(), '100644', oid(p)) for p in files]
        with patch.object(cache.infra_inputs, 'entries', return_value=entries): cache.require_executing_packager(Path('.'), 'a'*40)
        for changed in (entries[1:], entries+[('installer/windows/extra.py', '100644', '0'*40)], [(entries[0][0], '100644', '0'*40)]+entries[1:]):
            with self.subTest(count=len(changed)), patch.object(cache.infra_inputs, 'entries', return_value=changed), self.assertRaisesRegex(cache.InfraError, 'Executing packager'):
                cache.require_executing_packager(Path('.'), 'a'*40)


if __name__ == '__main__': unittest.main(verbosity=2)
