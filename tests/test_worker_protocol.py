"""Strict supervisor/worker interface; no app code, service or network calls."""
import contextlib
import importlib.util
import io
import json
import os
import pathlib
import tempfile
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('protocol_worker', pathlib.Path(__file__).resolve().parents[1] / 'deploy/updater.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class WorkerProtocolTests(unittest.TestCase):
    def test_unknown_command_or_arguments_reject_before_config_or_execution(self):
        for args in (['capabilities'], ['self-check', 'run'], ['run', '--runtime-digest', 'a' * 64],
                     ['run', '--supervisor-lock-fd', '-1', '--runtime-digest', 'a' * 64],
                     ['run', '--supervisor-lock-fd', '1', '--runtime-digest', 'A' * 64],
                     ['run', '--supervisor-lock-fd', '1', '--runtime-digest', 'a' * 63],
                     ['run', '--supervisor-lock-fd', '1', '--runtime-digest', 'a' * 64, 'extra']):
            with self.subTest(args=args), patch.object(u.sys, 'argv', ['updater.py', *args]), \
                 patch.object(u, 'load_config', side_effect=AssertionError('must not load config')):
                with self.assertRaises(u.UpdateError): u.main()
        for command in ('run', 'requests', 'recover', 'boot-recover', 'self-check'):
            self.assertEqual(u.parse_command([command]), (command, None, None))
            self.assertEqual(u.parse_command([command, '--supervisor-lock-fd', '7', '--runtime-digest', 'a' * 64]),
                             (command, 7, 'a' * 64))

    def test_capability_probe_is_read_only_and_reports_future_actual_schema(self):
        class Worker:
            def schema(self): return 3
            def current(self): return '/synthetic/current'
            def app_schema(self, path):
                self.path = path
                return 3
            def __getattr__(self, name): raise AssertionError('Probe tried '+name)
        worker = Worker()
        self.assertEqual(u.self_check(worker), dict(supervisorProtocol=1, workerProtocol=2, helperProtocol=1,
                         configFormat=1, stateFormat=1, appManifestFormats=[1, 2], databaseSchema=3))
        self.assertEqual(worker.path, '/synthetic/current')
        worker.app_schema = lambda path: 2
        with self.assertRaises(u.UpdateError): u.self_check(worker)

    def test_unlocked_read_only_probe_does_not_create_a_worker_lock(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            original = pathlib.Path.lstat
            def root_owner(path):
                value = list(original(path)); value[4] = 0
                return os.stat_result(value)
            worker = types.SimpleNamespace(schema=lambda: 1, current=lambda: '/fixture', app_schema=lambda p: 1)
            with patch.object(u.sys, 'argv', ['updater.py', 'self-check']), patch.object(u.os, 'geteuid', return_value=0), \
                 patch.object(u, 'load_config', return_value={'stateDirectory': tmp}), \
                 patch.object(pathlib.Path, 'lstat', root_owner), patch.object(u, 'Worker', return_value=worker), \
                 contextlib.redirect_stdout(io.StringIO()) as output:
                u.main()
            self.assertEqual(json.loads(output.getvalue())['databaseSchema'], 1)
            self.assertEqual(list(root.iterdir()), [])

    @unittest.skipUnless(os.name == 'posix', 'Linux descriptor/flock fixture')
    def test_inherited_descriptor_and_runtime_digest_are_verified(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            lock = root / 'worker.lock'; lock.touch(mode=0o600)
            other = root / 'other'; other.touch(mode=0o600)
            original_lstat, original_fstat = pathlib.Path.lstat, os.fstat
            def owner(info):
                values = list(info); values[4] = 0
                return os.stat_result(values)
            state = {'phase': 'stable', 'active': {'sha256': 'a' * 64}, 'candidate': {'sha256': 'b' * 64}}
            with lock.open('a') as output, other.open('a') as wrong, \
                 patch.object(pathlib.Path, 'lstat', lambda p: owner(original_lstat(p))), \
                 patch.object(u.os, 'fstat', lambda fd: owner(original_fstat(fd))), \
                 patch.object(u, 'private_file', side_effect=lambda p: json.dumps(state).encode()):
                u.supervisor_lock(output.fileno(), root, 'a' * 64, 'run')
                u.supervisor_lock(output.fileno(), root, 'b' * 64, 'self-check')
                with self.assertRaises(u.UpdateError): u.supervisor_lock(wrong.fileno(), root, 'a' * 64, 'run')
                with self.assertRaises(u.UpdateError): u.supervisor_lock(output.fileno(), root, 'b' * 64, 'run')
                with self.assertRaises(u.UpdateError): u.supervisor_lock(output.fileno(), root, 'c' * 64, 'self-check')
                state['phase'] = 'probing'
                with self.assertRaises(u.UpdateError): u.supervisor_lock(output.fileno(), root, 'a' * 64, 'run')
                u.supervisor_lock(output.fileno(), root, 'b' * 64, 'self-check')


if __name__ == '__main__': unittest.main()
