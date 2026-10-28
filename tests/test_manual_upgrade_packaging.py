"""Offline exact, deterministic manual-transition package; no publication."""
import hashlib
import contextlib
import io
import os
import subprocess
import sys
import types
import importlib.util
import pathlib
import shutil
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('manual_packaging', ROOT / 'scripts/package-manual-upgrade.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


class ManualPackagingTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)

    def test_exact_needed_files_reproducible_hashes_no_signature_or_supervisor(self):
        first, second = self.root / 'first', self.root / 'second'
        checksum = p.package(first)
        self.assertEqual(checksum, p.package(second))
        self.assertEqual((first / p.ARCHIVE).read_bytes(), (second / p.ARCHIVE).read_bytes())
        self.assertEqual((first / 'SHA256SUMS').read_text(), checksum + '  ' + p.ARCHIVE + '\n')
        with zipfile.ZipFile(first / p.ARCHIVE) as archive:
            self.assertEqual(archive.namelist(), list(p.FILES))
            for name in p.FILES:
                self.assertEqual(archive.read(name), p.source_file(name).read_bytes())
                self.assertEqual(archive.getinfo(name).external_attr >> 16, stat.S_IFREG | 0o644)
        for line in (first / 'FILES.sha256').read_text().splitlines():
            digest, name = line.split('  ')
            self.assertEqual(digest, hashlib.sha256(p.source_file(name).read_bytes()).hexdigest())
        with self.assertRaises(ValueError):
            p.package(first)

    def test_extracted_standalone_exact_owner_commands_parse_with_synthetic_boundaries(self):
        output, extracted = self.root / 'package', self.root / 'extracted'
        p.package(output)
        with zipfile.ZipFile(output / p.ARCHIVE) as archive:
            archive.extractall(extracted)
        for name in ('upgrade-worker-manual.py', 'request-update-manual.py'):
            help_result = subprocess.run([sys.executable, '-I', '-B', str(extracted / name), '--help'], capture_output=True, text=True)
            self.assertEqual(help_result.returncode, 0, help_result.stderr)
            self.assertNotIn('--expected-version', help_result.stdout)
        def module(name):
            spec = importlib.util.spec_from_file_location(name.replace('-', '_'), extracted / name)
            value = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(value)
            return value
        bootstrap = module('upgrade-worker-manual.py')
        calls = []
        fixture = types.SimpleNamespace(run=lambda check_only=False: calls.append(check_only) or 'SYNTHETIC_CHECK')
        for arguments in (['--check-only'], []):
            with patch.object(bootstrap.sys, 'argv', ['upgrade-worker-manual.py', *arguments]), \
                 patch.object(bootstrap, 'owner_guard'), patch.object(bootstrap, 'Bootstrap', return_value=fixture), \
                 patch.object(bootstrap.signal, 'signal'), contextlib.redirect_stdout(io.StringIO()):
                bootstrap.main()
        self.assertEqual(calls, [True, False])
        request = module('request-update-manual.py')
        identity = '10203040-5060-4070-8090-001122334455'
        calls = []
        fixture = types.SimpleNamespace(queue=lambda: calls.append('queue') or (identity, {}),
                                        config=lambda **kw: calls.append(('config', kw)) or {},
                                        wait=lambda *args: calls.append(('wait', args)) or 'SYNTHETIC_STATUS')
        for arguments in ([], ['--status', identity]):
            with patch.object(request.sys, 'argv', ['request-update-manual.py', *arguments]), \
                 patch.object(request.os, 'geteuid', return_value=0), \
                 patch.dict(request.os.environ, {'SUDO_UID': '1000'}), \
                 patch.object(request.pwd, 'getpwuid', return_value=types.SimpleNamespace(pw_name='owner')), \
                 patch.object(request.sys.stdin, 'isatty', return_value=True), \
                 patch.object(request.sys.stderr, 'isatty', return_value=True), \
                 patch.object(request, 'ManualRequest', return_value=fixture), contextlib.redirect_stdout(io.StringIO()):
                request.main()
        self.assertEqual(calls, ['queue', ('wait', (identity, {}, 4200)),
                                 ('config', {'require_enabled': False}), ('wait', (identity, {}, 4200))])

    def test_unknown_worker_and_noncanonical_source_fail_before_output(self):
        deploy = self.root / 'source/deploy'
        deploy.mkdir(parents=True)
        for name in p.FILES:
            target = deploy.parent / ('tests/fixtures/updater-direct-0.5.txt' if name == 'updater.py' else 'deploy/' + name)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(p.source_file(name), target)
        output = self.root / 'output'
        with patch.object(p, 'ROOT', deploy.parent):
            worker = deploy.parent / 'tests/fixtures/updater-direct-0.5.txt'
            worker.write_bytes(worker.read_bytes() + b'# tampered\n')
            with self.assertRaisesRegex(ValueError, 'pin'):
                p.package(output)
            self.assertFalse(output.exists())
            shutil.copyfile(ROOT / 'tests/fixtures/updater-direct-0.5.txt', worker)
            bootstrap = deploy / 'upgrade-worker-manual.py'
            bootstrap.write_bytes(bootstrap.read_bytes().replace(b'\n', b'\r\n'))
            with self.assertRaisesRegex(ValueError, 'canonical'):
                p.package(output)
            self.assertFalse(output.exists())


if __name__ == '__main__': unittest.main()
