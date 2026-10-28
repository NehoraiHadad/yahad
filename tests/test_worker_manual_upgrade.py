"""Exact-byte, rootless manual-transition fixtures; never touch a host/provider."""
import builtins
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import socket
import sqlite3
import stat
import subprocess
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('manual_worker_bootstrap', ROOT / 'deploy/upgrade-worker-manual.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)
LEGACY = (ROOT / 'tests/fixtures/updater-legacy-0.4.txt').read_bytes()
CURRENT = (ROOT / 'tests/fixtures/updater-direct-0.5.txt').read_bytes()


class ExactSourceTests(unittest.TestCase):
    def test_real_legacy_current_and_helper_pins_are_not_mocked(self):
        self.assertEqual(hashlib.sha256(LEGACY).hexdigest(), u.OLD_SHA256)
        self.assertEqual(hashlib.sha256(CURRENT).hexdigest(), u.DIRECT_WORKER_SHA256)
        self.assertNotEqual(LEGACY, CURRENT)
        for name, digests in u.HELPER_SHA256.items():
            raw = (ROOT / 'deploy' / name).read_bytes()
            self.assertNotIn(b'\r', raw)
            self.assertEqual(hashlib.sha256(raw).hexdigest(), digests[0])
            self.assertEqual(hashlib.sha256(raw.replace(b'\n', b'\r\n')).hexdigest(), digests[1])


@unittest.skipUnless(os.name == 'posix', 'Linux filesystem and flock contract')
class ManualBootstrapTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)
        self.stack = contextlib.ExitStack()
        self.addCleanup(self.stack.close)
        self.target, self.config = self.path(u.TARGET), self.path(u.CONFIG)
        self.status = self.path(u.STATE) / 'status.json'
        self.lock = self.path(u.STATE) / 'worker.lock'
        self.source = self.root / 'download' / 'updater.py'
        self.operator = {'familyId': '10203040-5060-4070-8090-001122334455',
                         'userId': 'aabbccdd-eeff-4011-8233-445566778899'}
        settings = json.loads((ROOT / 'deploy/updates.json.example').read_bytes())
        settings.update(operator=self.operator, enabled=True, mode='auto')
        journal = dict(phase='healthy', currentVersion='0.4.0', operator=self.operator,
                       releaseTrust=settings['releaseTrust'])
        files = [(self.source, CURRENT, 0o644), (self.target, LEGACY, 0o640),
                 (self.config, json.dumps(settings, indent=2).encode(), 0o600),
                 (self.status, json.dumps(journal, indent=2).encode(), 0o640),
                 (self.lock, b'', 0o600)]
        self.protected = [self.path('/etc/yachad/yachad.env'), self.path('/etc/yachad/master.key'),
                          self.path('/var/lib/yachad/app.sqlite'), self.path('/var/lib/yachad/app.sqlite-wal'),
                          self.path('/var/lib/yachad/app.sqlite-shm'), self.path('/var/lib/yachad/auth/credentials')]
        for index, path in enumerate(self.protected):
            files.append((path, ('synthetic protected sentinel ' + str(index)).encode(), 0o600))
        for name in u.HELPER_SHA256:
            files.append((self.target.with_name(name), (ROOT / 'deploy' / name).read_bytes(), 0o644))
        for path, content, mode in files:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
            path.chmod(mode)
        self.expected = {path: (path.read_bytes(), path.stat().st_ino, stat.S_IMODE(path.stat().st_mode))
                         for path, _, _ in files if path != self.target}
        self.unsafe_owner = set()
        original_lstat, original_fstat = pathlib.Path.lstat, os.fstat
        def owned(info, unsafe=False):
            fields = list(info)
            fields[4] = 1001 if unsafe else 0
            return os.stat_result(fields)
        self.stack.enter_context(patch.object(pathlib.Path, 'lstat', lambda path: owned(original_lstat(path), path in self.unsafe_owner)))
        self.stack.enter_context(patch.object(u.os, 'fstat', lambda fd: owned(original_fstat(fd), pathlib.Path(os.readlink('/proc/self/fd/' + str(fd))) in self.unsafe_owner)))
        self.chowns = []
        self.stack.enter_context(patch.object(u.os, 'fchown', lambda fd, uid, gid: self.chowns.append((uid, gid))))
        self.stack.enter_context(patch.object(u.subprocess, 'run', side_effect=AssertionError('No real systemctl, worker execution or network subprocess allowed')))
        self.stack.enter_context(patch.object(sqlite3, 'connect', side_effect=AssertionError('Bootstrap must never open a database')))
        self.stack.enter_context(patch.object(socket, 'create_connection', side_effect=AssertionError('Bootstrap must never connect to network')))
        self.states = {unit: dict(LoadState='loaded', ActiveState='inactive', SubState='dead', MainPID='0', ControlPID='0', Job='0', NeedDaemonReload='no')
                       for unit in (*u.QUIET_UNITS, u.RECOVERY_UNIT)}
        self.states[u.RECOVERY_UNIT].update(ActiveState='active', SubState='exited')
        for unit in u.SCHEDULERS:
            self.states[unit].update(ActiveState='active', SubState='waiting')
        self.actions = []
        self.worker = u.Bootstrap(self.root, self.source, inspect_units=lambda: self.states,
                                  scheduler=self.scheduler, ask=lambda _: 'y')

    def path(self, value):
        return self.root / value.lstrip('/')

    def scheduler(self, action, unit):
        self.assertIn(action, ('start', 'stop'))
        self.assertIn(unit, u.SCHEDULERS)
        self.actions.append((action, unit))
        self.states[unit].update(ActiveState='active' if action == 'start' else 'inactive',
                                 SubState='waiting' if action == 'start' else 'dead')

    def run_bootstrap(self, **kwargs):
        # Fail on attempts to read the environment, key, database, WAL/SHM or auth.
        original_open, builtin_open, io_open = os.open, builtins.open, io.open
        def guarded_open(path, *args, **options):
            if isinstance(path, (str, bytes, os.PathLike)):
                self.assertNotIn(pathlib.Path(path), self.protected, 'No protected data reads allowed')
            return original_open(path, *args, **options)
        def guarded_stream(opener):
            def incoming(path, *args, **options):
                if isinstance(path, (str, bytes, os.PathLike)):
                    self.assertNotIn(pathlib.Path(path), self.protected, 'No protected data reads allowed')
                return opener(path, *args, **options)
            return incoming
        with contextlib.redirect_stdout(io.StringIO()), patch.object(u.os, 'open', guarded_open), \
             patch.object(builtins, 'open', guarded_stream(builtin_open)), patch.object(io, 'open', guarded_stream(io_open)), \
             patch.object(socket, 'socket', side_effect=AssertionError('Bootstrap must never create network sockets')):
            return self.worker.run(**kwargs)

    def assert_preserved(self):
        for path, expected in self.expected.items():
            self.assertEqual((path.read_bytes(), path.stat().st_ino, stat.S_IMODE(path.stat().st_mode)), expected, str(path))
        self.assertEqual(stat.S_IMODE(self.target.stat().st_mode), 0o640)
        self.assertFalse(list(self.target.parent.glob('.updater-manual-*')))

    def assert_schedulers(self, active=True):
        self.assertTrue(all(self.states[unit]['ActiveState'] == ('active' if active else 'inactive') for unit in u.SCHEDULERS))

    def update_status(self, **values):
        journal = json.loads(self.status.read_bytes())
        journal.update(values)
        self.status.write_text(json.dumps(journal))

    def test_exact_historical_transition_is_atomic_and_preserves_everything_else(self):
        inode = self.target.stat().st_ino
        old_replace = os.replace
        def replace(source, target):
            self.assertEqual(pathlib.Path(target), self.target)
            self.assertEqual(self.target.read_bytes(), LEGACY)
            self.assertEqual(pathlib.Path(source).read_bytes(), CURRENT)
            self.assert_schedulers(False)
            old_replace(source, target)
        with patch.object(u.os, 'replace', replace):
            self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assertEqual(self.target.read_bytes(), CURRENT)
        self.assertNotEqual(self.target.stat().st_ino, inode)
        self.assertTrue(self.chowns)
        self.assertEqual(self.chowns[0][0], 0)
        self.assertEqual(self.actions, [('stop', x) for x in u.SCHEDULERS] + [('start', x) for x in u.SCHEDULERS])
        self.assert_schedulers()
        self.assert_preserved()
        for value in u.SIGNED_FOOTPRINT:
            self.assertFalse(self.path(value).exists())

    def test_check_only_and_blank_confirmation_are_read_only(self):
        inode = self.target.stat().st_ino
        self.assertIn('CHECK_OK', self.run_bootstrap(check_only=True))
        self.worker.ask = lambda _: ''
        self.assertIn('CANCELED', self.run_bootstrap())
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assertEqual(self.target.stat().st_ino, inode)
        self.assertFalse(self.actions)
        self.assertFalse(self.chowns)
        self.assert_preserved()

    def test_exact_current_direct_worker_is_noop_without_confirmation(self):
        self.target.write_bytes(CURRENT)
        inode = self.target.stat().st_ino
        self.worker.ask = lambda _: self.fail('Current worker must not ask')
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap())
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap(check_only=True))
        self.assertEqual(self.target.stat().st_ino, inode)
        self.assertFalse(self.actions)
        self.assert_preserved()

    def test_existing_signed_app_trust_and_disabled_notify_policy_are_preserved(self):
        config = json.loads(self.config.read_bytes())
        config.update(releaseTrust='signature', enabled=False, mode='notify',
                      publicKeyFile='/etc/yachad/release-signing.pub', tokenFile='/etc/yachad/github-token')
        self.config.write_text(json.dumps(config))
        self.update_status(releaseTrust='signature')
        for path in (self.path('/etc/yachad/release-signing.pub'), self.path('/etc/yachad/github-token')):
            path.write_bytes(b'synthetic existing secret/trust; must not read')
            path.chmod(0o600)
            self.protected.append(path)
            self.expected[path] = (path.read_bytes(), path.stat().st_ino, stat.S_IMODE(path.stat().st_mode))
        for path in (self.config, self.status):
            self.expected[path] = (path.read_bytes(), path.stat().st_ino, stat.S_IMODE(path.stat().st_mode))
        self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assert_preserved()

    def test_already_current_with_pending_recovery_is_not_reported_ready(self):
        self.target.write_bytes(CURRENT)
        self.update_status(phase='recovery_required')
        with self.assertRaisesRegex(u.UpgradeError, 'unfinished'): self.run_bootstrap(check_only=True)
        self.assertFalse(self.actions)

    def test_unknown_source_target_and_helper_bytes_are_refused(self):
        paths = [self.source, self.target, *(self.target.with_name(name) for name in u.HELPER_SHA256)]
        for path in paths:
            with self.subTest(path=path):
                original = path.read_bytes()
                path.write_bytes(original + b'# unreviewed alteration\n')
                with self.assertRaises(u.UpgradeError): self.run_bootstrap()
                self.assertFalse(self.actions)
                self.assertEqual(path.read_bytes(), original + b'# unreviewed alteration\n')
                path.write_bytes(original)

    def test_reviewed_crlf_helpers_are_accepted_without_rewriting(self):
        for name in u.HELPER_SHA256:
            path = self.target.with_name(name)
            path.write_bytes(path.read_bytes().replace(b'\n', b'\r\n'))
            self.expected[path] = (path.read_bytes(), path.stat().st_ino, stat.S_IMODE(path.stat().st_mode))
        self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assert_preserved()

    def test_worker_and_source_crlf_are_not_silently_normalized(self):
        for path in (self.source, self.target):
            with self.subTest(path=path):
                original = path.read_bytes()
                path.write_bytes(original.replace(b'\n', b'\r\n'))
                with self.assertRaises(u.UpgradeError): self.run_bootstrap()
                path.write_bytes(original)
        self.assertFalse(self.actions)

    def test_every_signed_footprint_including_broken_symlink_refuses_downgrade(self):
        for value in u.SIGNED_FOOTPRINT:
            with self.subTest(value=value):
                path = self.path(value)
                path.parent.mkdir(parents=True, exist_ok=True)
                path.symlink_to(self.root / 'absent-signed-file')
                with self.assertRaisesRegex(u.UpgradeError, 'Signed updater'): self.run_bootstrap()
                self.assertTrue(path.is_symlink())
                path.unlink()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assertFalse(self.actions)

    def test_pending_recovery_runtime_and_requests_are_refused(self):
        original = self.status.read_bytes()
        cases = [dict(phase=phase) for phase in ('recovery_required', 'rollback_pending_health', 'checking', 'building', 'stopping', 'activating', 'checking_health')]
        cases += [dict(runtimeDigest='a' * 64), dict(modeChangePending={}),
                  dict(lastModeRequest={'state': 'pending'}), dict(lastRequest={'state': 'running'})]
        for values in cases:
            with self.subTest(values=values):
                self.update_status(**values)
                with self.assertRaisesRegex(u.UpgradeError, 'unfinished'): self.run_bootstrap()
                self.status.write_bytes(original)
        self.assertFalse(self.actions)
        self.assertEqual(self.target.read_bytes(), LEGACY)

    def test_operator_and_configuration_guards_refuse_without_repairing(self):
        original = self.config.read_bytes()
        for values in (dict(operator={'familyId': 'placeholder', 'userId': 'placeholder'}),
                       dict(operator={}), dict(operator=dict(self.operator, userId='FFFFFFFF-FFFF-4FFF-8FFF-FFFFFFFFFFFF')),
                       dict(appUser='root'), dict(builderUser='root'), dict(service='other.service'),
                       dict(stateDirectory='/tmp/elsewhere'), dict(enabled='true'), dict(mode='anything'),
                       dict(repository='other/repository'), dict(releaseTrust='unsigned')):
            with self.subTest(values=values):
                config = json.loads(original); config.update(values)
                changed = json.dumps(config).encode(); self.config.write_bytes(changed)
                with self.assertRaises(u.UpgradeError): self.run_bootstrap()
                self.assertEqual(self.config.read_bytes(), changed)
        self.config.write_bytes(original)
        self.update_status(operator=dict(self.operator, userId='ffffffff-ffff-4fff-8fff-ffffffffffff'))
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_busy_lock_running_worker_and_pending_systemd_jobs_are_refused(self):
        with self.lock.open('rb') as locked:
            u.fcntl.flock(locked, u.fcntl.LOCK_EX | u.fcntl.LOCK_NB)
            with self.assertRaisesRegex(u.UpgradeError, 'busy'): self.run_bootstrap()
        for values in (dict(MainPID='123'), dict(ControlPID='321'), dict(ActiveState='activating', SubState='start'),
                       dict(Job='123 /org/freedesktop/systemd1/job/123'), dict(NeedDaemonReload='yes')):
            original = dict(self.states[u.WORKERS[0]])
            self.states[u.WORKERS[0]].update(values)
            with self.subTest(values=values), self.assertRaises(u.UpgradeError): self.run_bootstrap()
            self.states[u.WORKERS[0]] = original
        self.assertFalse(self.actions)
        self.assertEqual(self.target.read_bytes(), LEGACY)

    def test_confirmation_holds_lock_and_cancel_releases_it(self):
        def ask(_):
            with self.lock.open('rb') as competitor:
                with self.assertRaises(BlockingIOError):
                    u.fcntl.flock(competitor, u.fcntl.LOCK_EX | u.fcntl.LOCK_NB)
            return 'n'
        self.worker.ask = ask
        self.assertIn('CANCELED', self.run_bootstrap())
        with self.lock.open('rb') as competitor:
            u.fcntl.flock(competitor, u.fcntl.LOCK_EX | u.fcntl.LOCK_NB)
        self.assertFalse(self.actions)

    def test_missing_and_unsafe_lock_are_never_repaired(self):
        self.lock.chmod(0o640)
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.lock.unlink()
        with self.assertRaises(FileNotFoundError): self.run_bootstrap()
        self.assertFalse(self.lock.exists())
        self.assertFalse(self.actions)

    def test_nonroot_owner_writable_ancestors_and_public_config_refuse(self):
        for path in (self.target, self.config, self.status, self.lock, self.target.parent,
                     *(self.target.with_name(name) for name in u.HELPER_SHA256)):
            self.unsafe_owner.add(path)
            with self.subTest(path=path), self.assertRaises(u.UpgradeError): self.run_bootstrap()
            self.unsafe_owner.clear()
        for path, mode in ((self.target.parent, 0o777), (self.target, 0o666), (self.config, 0o644)):
            old = stat.S_IMODE(path.stat().st_mode); path.chmod(mode)
            with self.subTest(path=path), self.assertRaises(u.UpgradeError): self.run_bootstrap()
            path.chmod(old)
        self.assertFalse(self.actions)

    def test_hardlinks_symlinks_and_setid_target_are_refused(self):
        alias = self.target.with_name('alias')
        os.link(self.target, alias)
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        alias.unlink()
        self.target.chmod(0o4640)
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.target.chmod(0o640)
        self.target.unlink(); self.target.symlink_to(self.source)
        with self.assertRaises((u.UpgradeError, OSError)): self.run_bootstrap()
        self.assertEqual(self.source.read_bytes(), CURRENT)
        self.assertFalse(self.actions)

    def test_source_symlink_is_refused_even_with_reviewed_bytes(self):
        actual = self.source.with_name('actual.py'); self.source.rename(actual)
        self.source.symlink_to(actual)
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_initially_inactive_schedulers_remain_inactive(self):
        self.states[u.SCHEDULERS[0]].update(ActiveState='inactive', SubState='dead')
        self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assertEqual(self.actions, [('stop', u.SCHEDULERS[1]), ('start', u.SCHEDULERS[1])])
        self.assertEqual(self.states[u.SCHEDULERS[0]]['ActiveState'], 'inactive')
        self.assert_preserved()

    def test_stop_failure_restores_prior_scheduler_states(self):
        def scheduler(action, unit):
            self.scheduler(action, unit)
            if action == 'stop': raise u.UpgradeError('synthetic uncertain stop')
        self.worker.scheduler = scheduler
        with self.assertRaisesRegex(u.UpgradeError, 'uncertain stop'): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers()
        self.assert_preserved()

    def test_uncertain_start_is_checked_against_actual_state(self):
        def scheduler(action, unit):
            self.scheduler(action, unit)
            if action == 'start': raise OSError('synthetic timeout after start')
        self.worker.scheduler = scheduler
        self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assert_schedulers()
        self.assert_preserved()

    def test_failed_restore_is_explicit_and_does_not_revert_verified_worker(self):
        def scheduler(action, unit):
            if action == 'start': raise OSError('synthetic unavailable scheduler')
            self.scheduler(action, unit)
        self.worker.scheduler = scheduler
        with self.assertRaisesRegex(u.UpgradeError, 'restoration is unconfirmed'): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), CURRENT)
        self.assert_schedulers(False)
        self.assert_preserved()

    def test_worker_waking_during_quiescence_prevents_replacement(self):
        def scheduler(action, unit):
            self.scheduler(action, unit)
            if action == 'stop': self.states[u.WORKERS[1]].update(ActiveState='activating', SubState='start', MainPID='123')
        self.worker.scheduler = scheduler
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers()
        self.assert_preserved()

    def test_atomic_replace_failure_keeps_old_worker_and_restores_schedulers(self):
        with patch.object(u.os, 'replace', side_effect=OSError('synthetic pre-rename failure')):
            with self.assertRaises(OSError): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers()
        self.assert_preserved()

    def test_staging_fsync_failure_keeps_original_and_restores_schedulers(self):
        with patch.object(u.os, 'fsync', side_effect=OSError('synthetic staging write failure')):
            with self.assertRaises(OSError): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers()
        self.assert_preserved()

    def test_interrupt_after_first_scheduler_stop_restores_both_states(self):
        def scheduler(action, unit):
            self.scheduler(action, unit)
            if action == 'stop': raise KeyboardInterrupt()
        self.worker.scheduler = scheduler
        with self.assertRaises(KeyboardInterrupt): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers()
        self.assert_preserved()

    def test_concurrent_target_change_is_not_overwritten_and_stays_paused(self):
        unexpected = b'# concurrent unreviewed root edit\n'
        def scheduler(action, unit):
            self.scheduler(action, unit)
            if action == 'stop': self.target.write_bytes(unexpected)
        self.worker.scheduler = scheduler
        with self.assertRaisesRegex(u.UpgradeError, 'remain paused'): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), unexpected)
        self.assert_schedulers(False)

    def test_precommit_interrupt_cleans_staging_and_restores_schedulers(self):
        with patch.object(u.tempfile, 'mkstemp', side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers()
        self.assert_preserved()

    def test_interrupt_immediately_after_replace_is_ambiguous_and_stays_paused(self):
        original = os.replace
        def replace(source, target):
            original(source, target)
            raise KeyboardInterrupt()
        with patch.object(u.os, 'replace', replace):
            with self.assertRaisesRegex(u.UpgradeError, 'remain paused'): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), CURRENT)
        self.assert_schedulers(False)
        self.assert_preserved()
        self.actions.clear()
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap(check_only=True))
        self.assertFalse(self.actions)
        self.assert_schedulers(False)

    def test_postcommit_directory_sync_failure_keeps_schedulers_paused(self):
        original = os.fsync
        def sync(fd):
            if self.target.read_bytes() == CURRENT and stat.S_ISDIR(os.fstat(fd).st_mode):
                raise OSError('synthetic durability failure')
            return original(fd)
        with patch.object(u.os, 'fsync', sync):
            with self.assertRaisesRegex(u.UpgradeError, 'remain paused'): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), CURRENT)
        self.assert_schedulers(False)
        self.assert_preserved()

    def test_staging_cleanup_failure_still_restores_known_safe_scheduler_states(self):
        original_unlink = pathlib.Path.unlink
        def unlink(path, *args, **kwargs):
            if path.name.startswith('.updater-manual-'):
                raise OSError('synthetic cleanup failure')
            return original_unlink(path, *args, **kwargs)
        with patch.object(pathlib.Path, 'unlink', unlink), patch.object(u.os, 'replace', side_effect=OSError('synthetic precommit failure')):
            with self.assertRaisesRegex(u.UpgradeError, 'staging cleanup is incomplete'):
                self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers()
        for path in self.target.parent.glob('.updater-manual-*'):
            path.unlink()
        self.assert_preserved()

    def test_changed_config_during_confirmation_is_preserved_without_mutations(self):
        changed = self.config.read_bytes() + b'\n'
        def ask(_):
            self.config.write_bytes(changed)
            return 'y'
        self.worker.ask = ask
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertEqual(self.config.read_bytes(), changed)
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assertFalse(self.actions)

    def test_changed_journal_during_pause_is_never_rolled_back_or_resumed(self):
        def scheduler(action, unit):
            self.scheduler(action, unit)
            if action == 'stop': self.update_status(phase='recovery_required')
        self.worker.scheduler = scheduler
        with self.assertRaisesRegex(u.UpgradeError, 'remain paused'): self.run_bootstrap()
        self.assertEqual(json.loads(self.status.read_bytes())['phase'], 'recovery_required')
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assert_schedulers(False)
        self.assertFalse(any(action == 'start' for action, _ in self.actions))


class OwnerAndSystemdTests(unittest.TestCase):
    def test_root_check_and_interactive_sudo_owner_guards(self):
        with patch.object(u.os, 'geteuid', return_value=1000):
            with self.assertRaises(u.UpgradeError): u.owner_guard(True)
        owner = types.SimpleNamespace(pw_name='owner')
        with patch.object(u.os, 'geteuid', return_value=0), patch.object(u.pwd, 'getpwuid', return_value=owner), \
             patch.object(u.sys.stdin, 'isatty', return_value=True), patch.object(u.sys.stderr, 'isatty', return_value=True):
            with patch.dict(u.os.environ, {'SUDO_UID': '1000'}): u.owner_guard(False)
            for uid in ('0', '999', 'not-a-number'):
                with patch.dict(u.os.environ, {'SUDO_UID': uid}), self.assertRaises(u.UpgradeError): u.owner_guard(False)
            with patch.dict(u.os.environ, {'SUDO_UID': '1000'}), patch.object(u.sys.stdin, 'isatty', return_value=False):
                with self.assertRaises(u.UpgradeError): u.owner_guard(False)
            with patch.dict(u.os.environ, {'SUDO_UID': '1000'}), patch.object(u.pwd, 'getpwuid', return_value=types.SimpleNamespace(pw_name='yachad')):
                with self.assertRaises(u.UpgradeError): u.owner_guard(False)
            with patch.dict(u.os.environ, {}, clear=True): u.owner_guard(True)

    def test_direct_invocation_requires_isolation_before_sibling_import(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = pathlib.Path(temporary)
            script = directory / 'upgrade-worker-manual.py'
            script.write_bytes((ROOT / 'deploy/upgrade-worker-manual.py').read_bytes())
            marker = directory / 'untrusted-import'
            (directory / 'hashlib.py').write_text('from pathlib import Path\nPath(' + repr(str(marker)) + ").write_text('unsafe')\n")
            result = subprocess.run([u.sys.executable, str(script), '--help'], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('isolated Python', result.stderr)
            self.assertFalse(marker.exists())
            result = subprocess.run([u.sys.executable, '-I', '-B', str(script), '--help'], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('--check-only', result.stdout)
            self.assertNotIn('--public-key', result.stdout)
            self.assertFalse(marker.exists())

    def test_systemd_inspection_is_strictly_read_only_and_checks_jobs_reload(self):
        calls = []
        def command(args, **kwargs):
            calls.append(args)
            self.assertEqual(args[:2], ['/usr/bin/systemctl', 'show'])
            properties = args[-1].removeprefix('--property=').split(',')
            self.assertIn('Job', properties)
            self.assertIn('NeedDaemonReload', properties)
            if args[2] in u.SCHEDULERS: self.assertNotIn('MainPID', properties)
            values = dict(LoadState='loaded', ActiveState='inactive', SubState='dead', Job='0', NeedDaemonReload='no', MainPID='0', ControlPID='0')
            return types.SimpleNamespace(returncode=0, stdout=''.join(k + '=' + values[k] + '\n' for k in properties).encode())
        with patch.object(u.subprocess, 'run', command):
            self.assertEqual(set(u.unit_states()), set((*u.QUIET_UNITS, u.RECOVERY_UNIT)))
        self.assertEqual(len(calls), 5)
        with patch.object(u.subprocess, 'run', return_value=types.SimpleNamespace(returncode=0, stdout=b'LoadState=loaded\nLoadState=loaded\n')):
            with self.assertRaises(u.UpgradeError): u.unit_states()

    def test_scheduler_actions_are_restricted_to_start_stop_timer_path(self):
        with patch.object(u.subprocess, 'run', side_effect=AssertionError('must reject without command')):
            for action, unit in (('enable', u.SCHEDULERS[0]), ('stop', 'yachad.service'), ('start', u.WORKERS[0]), ('daemon-reload', '')):
                with self.assertRaises(u.UpgradeError): u.scheduler_action(action, unit)


if __name__ == '__main__': unittest.main()
