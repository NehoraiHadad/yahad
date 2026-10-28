"""Offline no-key runtime/bootstrap packages; no publication or host execution."""
import hashlib
import importlib.util
import json
import pathlib
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value)
    return value


@unittest.skipUnless(shutil.which('node') and shutil.which('python3'), 'local packaging tools')
class RuntimePackageTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)
        (self.root / 'deploy').mkdir()
        (self.root / 'deploy/updater.py').write_bytes(b'# synthetic reviewed source\n')

    def package(self, *extra, version='1.0.0'):
        return subprocess.run(['node', str(ROOT / 'scripts/release-updater.mjs'), 'package-github', version,
                               str(self.root / 'out'), *extra], cwd=self.root, capture_output=True, text=True)

    def test_github_package_has_two_assets_no_signature_or_key(self):
        result = self.package(); self.assertEqual(result.returncode, 0, result.stderr)
        output = self.root / 'out'
        self.assertEqual({p.name for p in output.iterdir()}, {'yachad-updater.json', 'yachad-updater.zip'})
        manifest = json.loads((output / 'yachad-updater.json').read_bytes())
        self.assertEqual(manifest['workerProtocol'], 3)
        self.assertEqual(manifest['kind'], 'yachad-updater')
        self.assertEqual(manifest['sha256'], hashlib.sha256((output / 'yachad-updater.zip').read_bytes()).hexdigest())
        with zipfile.ZipFile(output / 'yachad-updater.zip') as archive:
            self.assertEqual(archive.namelist(), ['updater.py'])
        self.assertNotEqual(self.package().returncode, 0)

    def test_github_mode_rejects_keys_extra_arguments_and_noncanonical_versions_before_output(self):
        for extra, version in [(('/nonexistent/key',), '1.0.0'), ((), '01.0.0'), ((), '1.0.0-rc1')]:
            with self.subTest(extra=extra, version=version):
                self.assertNotEqual(self.package(*extra, version=version).returncode, 0)
                self.assertFalse((self.root / 'out').exists())

    def test_noncanonical_source_refused(self):
        (self.root / 'deploy/updater.py').write_bytes(b'# fixture\r\n')
        self.assertNotEqual(self.package().returncode, 0)
        self.assertFalse((self.root / 'out').exists())


class BootstrapPackageTests(unittest.TestCase):
    def setUp(self):
        self.packager = module('github_offline_packager', ROOT / 'scripts/package-github-upgrade.py')
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)

    def test_exact_reviewed_standalone_delivery_is_deterministic_and_contains_no_signatures(self):
        filename, first = self.packager.package(self.root / 'one')
        other_name, second = self.packager.package(self.root / 'two')
        self.assertEqual((filename, first), (other_name, second))
        with zipfile.ZipFile(self.root / 'one' / filename) as archive:
            self.assertEqual(set(archive.namelist()), {*self.packager.SOURCES, 'yachad-updater.json', 'yachad-updater.zip'})
            self.assertFalse(any(name.endswith(('.sig', '.pem', '.pub')) for name in archive.namelist()))
            manifest = json.loads(archive.read('yachad-updater.json'))
            self.assertEqual(manifest['sha256'], hashlib.sha256(archive.read('yachad-updater.zip')).hexdigest())
        self.assertEqual((self.root / 'one/SHA256SUMS').read_text(), first + '  ' + filename + '\n')
        with self.assertRaises(ValueError): self.packager.package(self.root / 'one')

    @unittest.skipUnless(shutil.which('node') and shutil.which('python3'), 'local packaging tools')
    def test_bootstrap_runtime_is_byte_identical_to_release_packager(self):
        filename, _ = self.packager.package(self.root / 'bootstrap')
        result = subprocess.run(['node', str(ROOT / 'scripts/release-updater.mjs'), 'package-github', '1.0.1',
                                 str(self.root / 'runtime')], cwd=ROOT, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.root / 'bootstrap' / filename) as archive:
            for name in ('yachad-updater.json', 'yachad-updater.zip'):
                self.assertEqual(archive.read(name), (self.root / 'runtime' / name).read_bytes(), name)

    def test_changed_reviewed_source_is_not_packaged(self):
        fake = self.root / 'source'; (fake / 'deploy').mkdir(parents=True)
        for name in (*self.packager.SOURCES, 'updater.py'):
            shutil.copy(ROOT / 'deploy' / name, fake / 'deploy' / name)
        (fake / 'deploy/updater-transport.py').write_bytes(b'# unexpected source\n')
        self.packager.ROOT = fake
        with self.assertRaisesRegex(ValueError, 'reviewed bootstrap pin'):
            self.packager.package(self.root / 'out')
        self.assertFalse((self.root / 'out').exists())


if __name__ == '__main__': unittest.main()
