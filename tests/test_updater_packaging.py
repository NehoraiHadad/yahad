"""Offline signed root-runtime packaging; ephemeral fixture keys only."""
import hashlib
import json
import pathlib
import shutil
import stat
import subprocess
import tempfile
import unittest
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]

@unittest.skipUnless(shutil.which('node') and shutil.which('python3') and shutil.which('openssl'), 'local packaging tools')
class UpdaterPackagingTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)
        (self.root / 'deploy').mkdir()
        self.source = b'# synthetic fixture only\n'
        (self.root / 'deploy/updater.py').write_bytes(self.source)
        self.key = self.root / 'fixture-only.pem'
        self.public = self.root / 'fixture-only.pub'
        subprocess.run(['openssl', 'genpkey', '-algorithm', 'ED25519', '-out', str(self.key)], check=True, capture_output=True)
        self.key.chmod(0o600)
        subprocess.run(['openssl', 'pkey', '-in', str(self.key), '-pubout', '-out', str(self.public)], check=True, capture_output=True)

    def package(self):
        return subprocess.run(['node', str(ROOT / 'scripts/release-updater.mjs'), 'package', '1.0.0',
                               str(self.root / 'out'), str(self.key)], cwd=self.root, capture_output=True, text=True)

    def test_exact_root_only_manifest_archive_and_real_ed25519_signature(self):
        result = self.package(); self.assertEqual(result.returncode, 0, result.stderr)
        output = self.root / 'out'
        self.assertEqual({path.name for path in output.iterdir()}, {'yachad-updater.zip', 'yachad-updater.json', 'yachad-updater.sig'})
        manifest = json.loads((output / 'yachad-updater.json').read_bytes())
        content = (output / 'yachad-updater.zip').read_bytes()
        self.assertEqual(manifest, dict(format=1, kind='yachad-updater', version='1.0.0', archive='yachad-updater.zip',
                         bytes=len(content), sha256=hashlib.sha256(content).hexdigest(), supervisorProtocol=1,
                         workerProtocol=2, helperProtocol=1, configFormat=1, stateFormat=1, appManifestFormats=[1, 2]))
        self.assertEqual(len((output / 'yachad-updater.sig').read_bytes()), 64)
        subprocess.run(['openssl', 'pkeyutl', '-verify', '-pubin', '-inkey', str(self.public), '-rawin',
                        '-in', str(output / 'yachad-updater.json'), '-sigfile', str(output / 'yachad-updater.sig')], check=True, capture_output=True)
        with zipfile.ZipFile(output / 'yachad-updater.zip') as archive:
            self.assertEqual(archive.namelist(), ['updater.py'])
            self.assertEqual(archive.read('updater.py'), self.source)
            self.assertEqual(archive.getinfo('updater.py').external_attr >> 16, stat.S_IFREG | 0o644)
        self.assertNotEqual(self.package().returncode, 0)

    def test_noncanonical_source_or_public_private_key_mode_fails_before_output(self):
        (self.root / 'deploy/updater.py').write_bytes(b'# fixture\r\n')
        self.assertNotEqual(self.package().returncode, 0)
        self.assertFalse((self.root / 'out').exists())
        (self.root / 'deploy/updater.py').write_bytes(self.source)
        self.key.chmod(0o644)
        self.assertNotEqual(self.package().returncode, 0)
        self.assertFalse((self.root / 'out').exists())

if __name__ == '__main__': unittest.main()
