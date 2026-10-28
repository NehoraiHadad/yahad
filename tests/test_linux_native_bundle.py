"""Cross-platform packaging fixtures, not Linux runtime acceptance."""
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import tarfile
import tempfile
import unittest

MODULE_PATH = Path(__file__).resolve().parents[1] / 'installer/linux/package_bundle.py'
spec = importlib.util.spec_from_file_location('linux_candidate', MODULE_PATH)
bundle = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bundle)

class CandidateTests(unittest.TestCase):
    def setUp(self):
        # Retain only synthetic fixtures for inspection; no recursive cleanup.
        self.root = Path(tempfile.mkdtemp(prefix='yahad-linux-package-'))
        self.prepared, self.engine = self.root/'prepared', self.root/'engine'
        self.prepared.mkdir(); self.engine.mkdir()
        for name in ('install.py', 'updater.py'): (self.engine/name).write_text('# synthetic fixture only\n')
        self.populate('linux-x64')

    def put(self, name, value):
        path = self.prepared/name; path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value.encode() if isinstance(value, str) else value)

    def populate(self, target):
        machine, esbuild = bundle.TARGETS[target]
        header = bytearray(64); header[:6] = b'\x7fELF\x02\x01'; struct.pack_into('<HH', header, 16, 2, machine)
        for name in (*bundle.RUNTIMES, f'app/node_modules/@esbuild/{esbuild}/bin/esbuild'): self.put(name, header)
        for name in bundle.REQUIRED: self.put(name, 'synthetic fixture')
        package = {'version': '0.5.2', 'engines': {'node': '>=24.0.0 <25'}, 'dependencies': {'tsx': '4.20.6'}}
        self.put('app/package.json', json.dumps(package))
        self.put('app/package-lock.json', json.dumps({'packages': {'': package}}))
        self.put('runtime/runtime-lock.json', json.dumps({'schema':1, 'target': target, 'components':[
            {'path': name, 'version': '24.22.2' if '/node/' in name else '3.13.0', 'sourceUrl':'https://example.invalid/runtime.tar.xz', 'sha256':hashlib.sha256(header).hexdigest()}
            for name in bundle.RUNTIMES]}))
        self.put('legal/SBOM.json', json.dumps({'kind': 'synthetic-test-only'}))
        self.put('legal/ffmpeg-build.json', json.dumps({'configure': ['--disable-gpl'], 'sourceUrl': 'https://example.invalid/ffmpeg.tar.xz', 'sourceSha256': '0'*64}))

    def build(self, name='output.tar.gz', target='linux-x64'):
        return bundle.build(self.prepared, self.root/name, target, self.engine)

    def test_deterministic_candidate_inventory_and_modes(self):
        manifest = self.build(); self.build('second.tar.gz')
        self.assertEqual((self.root/'output.tar.gz').read_bytes(), (self.root/'second.tar.gz').read_bytes())
        self.assertEqual(manifest['runtimeAcceptance'], 'not-performed')
        with tarfile.open(self.root/'output.tar.gz') as archive:
            actual = json.load(archive.extractfile('bundle.json'))
            self.assertEqual(actual['kind'], 'native-runtime-candidate')
            for record in actual['files']:
                info = archive.getmember(record['path'])
                self.assertTrue(info.isfile()); self.assertEqual(info.mode, record['mode'])
                self.assertEqual(hashlib.sha256(archive.extractfile(info).read()).hexdigest(), record['sha256'])
            self.assertEqual(archive.getmember('runtime/node/bin/node').mode, 0o755)
            self.assertEqual(archive.getmember('engine/deploy/install.py').mode, 0o644)

    def test_arm64_uses_separate_executable_architecture(self):
        self.prepared = self.root/'prepared-arm64'; self.prepared.mkdir()
        self.populate('linux-arm64'); self.build(target='linux-arm64')

    def test_mixed_native_dependency_architectures_refused(self):
        header = bytearray(64); header[:6] = b'\x7fELF\x02\x01'; struct.pack_into('<HH', header, 16, 3, 183)
        self.put('app/node_modules/extra/library.so', header)
        with self.assertRaises(bundle.PackageError): self.build()

    def test_architecture_mismatch_refused(self):
        with self.assertRaises(bundle.PackageError): self.build(target='linux-arm64')

    def test_windows_runtime_refused(self):
        self.put('runtime/node/bin/node', b'MZ' + bytes(100))
        with self.assertRaises(bundle.PackageError): self.build()

    def test_missing_dependency_refused(self):
        (self.prepared/'runtime/python/bin/python3').unlink()
        with self.assertRaises(bundle.PackageError): self.build()

    def test_lockfile_drift_refused(self):
        self.put('app/package-lock.json', '{"packages":{"":{"version":"9.0.0"}}}')
        with self.assertRaises(bundle.PackageError): self.build()

    def test_runtime_checksum_drift_refused(self):
        path = self.prepared/'runtime/node/bin/node'
        path.write_bytes(path.read_bytes() + b'changed')
        with self.assertRaises(bundle.PackageError): self.build()

    def test_existing_output_preserved(self):
        path = self.root/'output.tar.gz'; path.write_bytes(b'preserve')
        with self.assertRaises(bundle.PackageError): self.build()
        self.assertEqual(path.read_bytes(), b'preserve')

    def test_private_file_refused(self):
        self.put('app/.env', 'synthetic only')
        with self.assertRaises(bundle.PackageError): self.build()

    def test_nonfree_ffmpeg_refused(self):
        self.put('legal/ffmpeg-build.json', json.dumps({'configure':['--enable-nonfree'], 'sourceUrl':'https://example.invalid/ffmpeg.tar.xz', 'sourceSha256':'0'*64}))
        with self.assertRaises(bundle.PackageError): self.build()

    def test_hardlink_input_refused(self):
        os.link(self.prepared/'app/src/main.ts', self.prepared/'app/src/alias.ts')
        with self.assertRaises(bundle.PackageError): self.build()

    def test_unknown_target_refused(self):
        with self.assertRaises(bundle.PackageError): self.build(target='linux-armhf')

    def test_output_inside_input_refused(self):
        with self.assertRaises(bundle.PackageError): bundle.build(self.prepared, self.prepared/'candidate.tar.gz', 'linux-x64', self.engine)

if __name__ == '__main__': unittest.main(verbosity=2)
