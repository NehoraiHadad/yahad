"""Rootless app-request fixtures; no real systemd/provider/household changes."""
import contextlib
import fcntl
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import pwd
import stat
import subprocess
import sys
import tempfile
import time
import types
import unittest
import uuid
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('manual_app_request', ROOT / 'deploy/request-update-manual.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


@unittest.skipUnless(os.name == 'posix', 'Linux flock/ownership contract')
class ManualRequestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.config = dict(enabled=True, mode='notify', service='yachad.service',
                           stateDirectory=u.STATE, releaseTrust='github-https', appUser='fixture', repository='NehoraiHadad/yahad',
                           operator={'familyId': '10203040-5060-4070-8090-001122334455', 'userId': 'aabbccdd-eeff-4011-8233-445566778899'},
                           dataDirectory='/var/lib/yachad/live', installVersionPin=None,
                           healthUrl='http://127.0.0.1:3000/health/ready')
        self.state = dict(phase='incompatible', currentVersion='0.4.0', failedVersion='0.5.0',
                          checkedAt=int(time.time()*1000), error='incompatible', operator=self.config['operator'], releaseTrust='github-https')
        self.events = []
        self.helper = u.ManualRequest(self.root, writer=self.writer, start=self.start,
                                      health=lambda _url: True, now=lambda: 1234567)
        for name, content, mode in ((u.CONFIG, json.dumps(self.config).encode(), 0o600),
                                    (u.STATE+'/status.json', json.dumps(self.state).encode(), 0o640),
                                    (u.STATE+'/worker.lock', b'', 0o600),
                                    (u.WORKER, (ROOT/'tests/fixtures/updater-direct-0.5.txt').read_bytes(), 0o644)):
            path = self.helper.path(name); path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content); path.chmod(mode)
        original_lstat, original_fstat = pathlib.Path.lstat, os.fstat
        def root_owner(value):
            data = list(value); data[4] = 0
            return os.stat_result(data)
        stack = contextlib.ExitStack(); self.addCleanup(stack.close)
        stack.enter_context(patch.object(pathlib.Path, 'lstat', lambda p: root_owner(original_lstat(p))))
        stack.enter_context(patch.object(u.os, 'fstat', lambda fd: root_owner(original_fstat(fd))))
        stack.enter_context(patch.object(u.pwd, 'getpwnam', return_value=types.SimpleNamespace(pw_uid=1000)))

    def writer(self, config, payload):
        self.events.append(('publish', config, payload))
        # Publication occurs under the worker lock.
        with self.helper.path(u.STATE+'/worker.lock').open('r') as other:
            with self.assertRaises(BlockingIOError):
                fcntl.flock(other, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def start(self):
        self.events.append(('start',))
        # Service dispatch must occur AFTER release of the same flock.
        with self.helper.path(u.STATE+'/worker.lock').open('r') as other:
            fcntl.flock(other, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def save_config(self):
        self.helper.path(u.CONFIG).write_text(json.dumps(self.config))

    def save_status(self):
        self.helper.path(u.STATE+'/status.json').write_text(json.dumps(self.state))

    def queue(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            value = self.helper.queue()
        self.assertIn('REQUEST_ID='+value[0], output.getvalue())
        return value

    def test_pin_tracks_exact_reviewed_direct_worker(self):
        self.assertEqual(hashlib.sha256((ROOT/'tests/fixtures/updater-direct-0.5.txt').read_bytes()).hexdigest(), u.WORKER_SHA256)

    def test_explicit_install_ignores_old_cache_and_failed_version_without_mutating_policy(self):
        before = {name: self.helper.path(name).read_bytes() for name in (u.CONFIG, u.STATE+'/status.json', u.WORKER)}
        request_id, config = self.queue()
        payload = json.loads(self.events[0][2])
        self.assertEqual(payload, dict(id=request_id, action='install', **self.config['operator'], createdAt=1234567000))
        self.assertLessEqual(len(self.events[0][2]), 512)
        self.assertEqual([event[0] for event in self.events], ['publish', 'start'])
        self.assertEqual(str(uuid.UUID(request_id)), request_id)
        self.assertEqual(config['mode'], 'notify')
        for name, content in before.items(): self.assertEqual(self.helper.path(name).read_bytes(), content)

    def test_disabled_different_pin_bad_paths_and_root_user_refuse_before_publish(self):
        for key, value in [('enabled', False), ('installVersionPin', '0.4.0'), ('dataDirectory', '/tmp/other'),
                           ('dataDirectory', '/var/lib/yachad/../other'), ('healthUrl', 'https://example.org/health/ready'),
                           ('appUser', '-u'), ('operator', {'familyId': '', 'userId': 'p'})]:
            old = self.config[key]; self.config[key] = value; self.save_config()
            with self.subTest(key=key, value=value), self.assertRaises(u.RequestError): self.queue()
            self.assertEqual(self.events, [])
            self.config[key] = old; self.save_config()
        with patch.object(u.pwd, 'getpwnam', return_value=types.SimpleNamespace(pw_uid=0)), self.assertRaises(u.RequestError):
            self.queue()

    def test_unfinished_or_signed_runtime_journal_refuses(self):
        for change in ({'phase': 'recovery_required'}, {'phase': 'building'}, {'phase': 'rollback_pending_health'},
                       {'runtimeDigest': 'a'*64}, {'modeChangePending': {}},
                       {'lastModeRequest': {'state': 'pending'}}, {'lastRequest': {'state': 'running'}}):
            previous = dict(self.state); self.state.update(change); self.save_status()
            with self.subTest(change=change), self.assertRaises(u.RequestError): self.queue()
            self.assertEqual(self.events, [])
            self.state = previous; self.save_status()

    def test_busy_worker_unknown_source_and_linked_private_files_refuse(self):
        with self.helper.path(u.STATE+'/worker.lock').open('r') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaisesRegex(u.RequestError, 'BUSY'): self.queue()
        worker = self.helper.path(u.WORKER); source = worker.read_bytes(); worker.write_bytes(b'# unknown\n')
        with self.assertRaisesRegex(u.RequestError, 'DIRECT_WORKER'): self.queue()
        worker.write_bytes(source)
        os.link(self.helper.path(u.CONFIG), self.root/'linked-config')
        with self.assertRaisesRegex(u.RequestError, 'UNSAFE_ROOT_FILE'): self.queue()
        self.assertEqual(self.events, [])

    def test_noncanonical_or_large_operator_identity_is_refused(self):
        self.config['operator'] = {'familyId': '\u05d0'*128, 'userId': '\u05d1'*128}; self.save_config()
        with self.assertRaisesRegex(u.RequestError, 'INVALID_OPERATOR'): self.queue()
        self.assertEqual(self.events, [])

    def test_signed_enrollment_and_policy_mismatch_refuse(self):
        for value in u.SIGNED_FOOTPRINT:
            path = self.helper.path(value); path.parent.mkdir(parents=True, exist_ok=True)
            path.symlink_to('/nonexistent-fixture')
            with self.subTest(value=value), self.assertRaisesRegex(u.RequestError, 'SIGNED_ENROLLMENT'): self.queue()
            path.unlink()
        for key, value in (('operator', {}), ('releaseTrust', 'signature')):
            previous = self.state[key]; self.state[key] = value; self.save_status()
            with self.assertRaisesRegex(u.RequestError, 'STATUS_POLICY_MISMATCH'): self.queue()
            self.state[key] = previous; self.save_status()
        self.assertEqual(self.events, [])

    def test_readonly_followup_can_observe_after_policy_disabled(self):
        self.config.update(enabled=False, installVersionPin='0.6.0'); self.save_config()
        self.assertFalse(self.helper.config(require_enabled=False)['enabled'])

    def test_failed_publication_never_starts_service_or_requeues(self):
        calls = []
        def failed(config, payload):
            calls.append(payload); raise u.RequestError('REQUEST_EXISTS')
        self.helper.writer = failed
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(u.RequestError, 'EXISTS'): self.helper.queue()
        self.assertEqual(len(calls), 1); self.assertEqual(self.events, [])

    def test_correlated_terminal_success_requires_exact_version_phase_and_readiness(self):
        request_id = str(uuid.uuid4())
        self.state.update(phase='healthy', currentVersion='0.5.0', error=None,
                          lastRequest={'id': request_id, 'action': 'install', 'state': 'succeeded'})
        self.save_status()
        self.assertIn('INSTALL_HEALTHY', self.helper.wait(request_id, self.config, 0))
        for change in ({'currentVersion': '0.5.1'}, {'phase': 'recovery_required'}, {'error': 'health'}):
            previous = dict(self.state); self.state.update(change); self.save_status()
            with self.assertRaisesRegex(u.RequestError, 'HEALTH_UNCONFIRMED'): self.helper.wait(request_id, self.config, 0)
            self.state = previous; self.save_status()
        self.helper.health = lambda _: False
        with self.assertRaisesRegex(u.RequestError, 'HEALTH_UNCONFIRMED'): self.helper.wait(request_id, self.config, 0)

    def test_unrelated_request_or_running_times_out_without_duplicate_or_false_success(self):
        request_id = str(uuid.uuid4())
        for request in ({'id': str(uuid.uuid4()), 'action': 'install', 'state': 'succeeded'},
                        {'id': request_id, 'action': 'install', 'state': 'running'},
                        {'id': request_id, 'action': 'check', 'state': 'succeeded'}):
            self.state['lastRequest'] = request; self.save_status()
            self.assertIn('PENDING_OR_UNCONFIRMED', self.helper.wait(request_id, self.config, 0))
        self.assertEqual(self.events, [])

    def test_correlated_failure_preserves_recovery_and_never_checks_health(self):
        request_id = str(uuid.uuid4())
        self.state.update(phase='recovery_required', lastRequest={'id': request_id, 'action': 'install', 'state': 'failed'})
        self.save_status(); before = self.helper.path(u.STATE+'/status.json').read_bytes()
        self.helper.health = lambda _: self.fail('must not probe failed recovery')
        with self.assertRaisesRegex(u.RequestError, 'INSTALL_FAILED'): self.helper.wait(request_id, self.config, 0)
        self.assertEqual(self.helper.path(u.STATE+'/status.json').read_bytes(), before)

    def test_service_start_is_fixed_nonblocking_and_logs_no_subprocess_output(self):
        with patch.object(u.subprocess, 'run', return_value=types.SimpleNamespace(returncode=0)) as run:
            u.service_start()
        self.assertEqual(run.call_args.args[0], ['/usr/bin/systemctl', 'start', '--no-block', u.SERVICE])
        self.assertTrue(run.call_args.kwargs['capture_output'])

    def test_publish_always_uses_fixed_isolated_app_user_child(self):
        with patch.object(u.subprocess, 'run', return_value=types.SimpleNamespace(returncode=0)) as run:
            u.publish(self.config, b'fixture')
        args = run.call_args.args[0]
        self.assertEqual(args[:9], ['/usr/sbin/runuser', '-u', 'fixture', '--', '/usr/bin/python3', '-I', '-B', '-c', u.WRITER])
        self.assertEqual(args[9], self.config['dataDirectory'])
        self.assertEqual(run.call_args.kwargs['input'], b'fixture')


@unittest.skipUnless(os.name == 'posix' and os.geteuid() != 0, 'Actual non-root queue writer fixture')
class AtomicWriterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.payload = json.dumps(dict(id=str(uuid.uuid4()), action='install', familyId='f', userId='p',
                                       createdAt=int(time.time()*1000))).encode()

    def run_writer(self, path=None, payload=None):
        return subprocess.run([sys.executable, '-I', '-B', '-c', u.WRITER, str(path or self.root)],
                              input=self.payload if payload is None else payload, capture_output=True, timeout=5)

    def test_atomic_private_publication_and_existing_queue_preserved(self):
        result = self.run_writer(); self.assertEqual(result.returncode, 0)
        queue, wake = self.root/'update-request.json', self.root/'update-request.wake'
        self.assertEqual(queue.read_bytes(), self.payload)
        self.assertEqual(queue.stat().st_ino, wake.stat().st_ino)
        self.assertEqual(stat.S_IMODE(queue.stat().st_mode), 0o600)
        self.assertEqual(queue.stat().st_uid, os.getuid())
        self.assertEqual(self.run_writer().returncode, 20)
        self.assertEqual(queue.read_bytes(), self.payload)
        self.assertFalse(list(self.root.glob('.manual-update-*')))

    def test_symlink_collision_never_follows_or_overwrites_target(self):
        other = self.root/'preserved'; other.write_bytes(b'unchanged')
        (self.root/'update-request.json').symlink_to(other)
        self.assertEqual(self.run_writer().returncode, 20)
        self.assertEqual(other.read_bytes(), b'unchanged')
        self.assertTrue((self.root/'update-request.json').is_symlink())

    def test_wake_failure_keeps_published_authoritative_queue(self):
        (self.root/'update-request.wake').mkdir()
        self.assertEqual(self.run_writer().returncode, 0)
        self.assertEqual((self.root/'update-request.json').read_bytes(), self.payload)
        self.assertTrue((self.root/'update-request.wake').is_dir())

    def test_stale_large_and_unsafe_directory_refuse_without_queue(self):
        stale = json.loads(self.payload); stale['createdAt'] -= 20000
        for payload in (json.dumps(stale).encode(), b'x'*513):
            self.assertNotEqual(self.run_writer(payload=payload).returncode, 0)
            self.assertFalse((self.root/'update-request.json').exists())
        self.root.chmod(0o777)
        self.assertNotEqual(self.run_writer().returncode, 0)
        self.assertFalse((self.root/'update-request.json').exists())


class CommandTests(unittest.TestCase):
    def invoke(self, arguments, owner=1000, terminal=True):
        request_id = '10203040-5060-4070-8090-001122334455'
        def queue():
            if '--status' in arguments: self.fail('Read-only CLI must never queue')
            return request_id, {}
        helper = types.SimpleNamespace(queue=queue, config=lambda **kw: {},
                                       wait=lambda *args: 'TEST_RESULT')
        with patch.object(u.sys, 'argv', ['request-update-manual.py', *arguments]), \
             patch.object(u.os, 'geteuid', return_value=0), patch.dict(u.os.environ, {'SUDO_UID': str(owner)}), \
             patch.object(u.pwd, 'getpwuid', return_value=types.SimpleNamespace(pw_name='fixture-owner')), \
             patch.object(u.sys.stdin, 'isatty', return_value=terminal), \
             patch.object(u.sys.stderr, 'isatty', return_value=terminal), \
             patch.object(u, 'ManualRequest', return_value=helper) as factory, \
             contextlib.redirect_stdout(io.StringIO()) as output:
            u.main()
        self.assertEqual(output.getvalue(), 'TEST_RESULT\n')
        return factory

    def test_exact_documented_default_command_and_bounded_wait(self):
        self.invoke([])
        self.invoke(['--wait-seconds', '0'])

    def test_readonly_status_needs_no_owner_tty_and_never_queues(self):
        request_id = '10203040-5060-4070-8090-001122334455'
        self.invoke(['--status', request_id, '--wait-seconds', '0'], owner=0, terminal=False)

    def test_queue_refuses_nonowner_or_noninteractive_context(self):
        for owner, terminal in ((0, True), (999, True), (1000, False)):
            with self.subTest(owner=owner, terminal=terminal), self.assertRaises(u.RequestError):
                self.invoke([], owner=owner, terminal=terminal)


if __name__ == '__main__': unittest.main()
