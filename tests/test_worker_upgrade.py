"""Rootless synthetic worker-bootstrap tests: no real systemd/provider calls."""
import contextlib
import hashlib
import importlib.util
import io
import zipfile
import subprocess
import json
import os
import pathlib
import stat
import tempfile
import unittest
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('worker_bootstrap', ROOT / 'deploy/upgrade-worker.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class PinnedSourceTests(unittest.TestCase):
    def test_bootstrap_pin_matches_exact_new_source(self):
        self.assertEqual(hashlib.sha256((ROOT / 'deploy/updater-supervisor.py').read_bytes()).hexdigest(), u.SUPERVISOR_SHA256,
                         'Review supervisor changes and refresh the explicit bootstrap pin together')
        self.assertEqual(hashlib.sha256((ROOT / 'deploy/updater-transport.py').read_bytes()).hexdigest(), u.TRANSPORT_SHA256,
                         'Review immutable transport changes and refresh the bootstrap pin together')
        self.assertEqual(hashlib.sha256((ROOT / 'tests/fixtures/updater-direct-0.5.txt').read_bytes()).hexdigest(), u.DIRECT_WORKER_SHA256,
                         'Review the fresh-install direct worker enrollment pin together')
        self.assertEqual(u.OLD_SHA256, '6b1523a207ecff1ad7bff19c80d85809f4e453125bf5b5ecece64463465a6cad')


@unittest.skipUnless(os.name == 'posix', 'Linux filesystem/flock bootstrap contract')
class BootstrapTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.stack = contextlib.ExitStack()
        self.addCleanup(self.stack.close)
        self.old = b'# exact reviewed old worker\n'
        self.new = u.LAUNCHER
        supervisor_source = (ROOT / 'deploy/updater-supervisor.py').read_bytes()
        self.source = self.root / 'reviewed/updater-supervisor.py'
        transport_source = (ROOT / 'deploy/updater-transport.py').read_bytes()
        self.target = self.path(u.TARGET)
        self.config = self.path(u.CONFIG)
        self.status = self.path(u.STATE) / 'status.json'
        self.lock = self.path(u.STATE) / 'worker.lock'
        for path, data, mode in [(self.source, supervisor_source, 0o644),
                                (self.source.with_name('updater-transport.py'), transport_source, 0o644), (self.target, self.old, 0o640),
                                (self.config, json.dumps({'stateDirectory': u.STATE, 'service': 'yachad.service',
                                 'releaseTrust': 'signature', 'enabled': True, 'mode': 'auto',
                                 'dataDirectory': '/var/lib/yachad',
                                 'operator': {'familyId': 'f', 'userId': 'p'}}).encode(), 0o600),
                                (self.status, b'{"phase":"healthy","currentVersion":"0.4.0"}', 0o640),
                                (self.lock, b'', 0o600)]:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            path.chmod(mode)
        self.path('/etc/systemd/system').mkdir(parents=True)
        self.bundle = self.root / 'signed-bundle'; self.bundle.mkdir()
        self.private_key = self.root / 'signing.pem'
        self.public_key = self.root / 'signing.pub'
        subprocess.run(['openssl', 'genpkey', '-algorithm', 'ED25519', '-out', str(self.private_key)], check=True, capture_output=True)
        subprocess.run(['openssl', 'pkey', '-in', str(self.private_key), '-pubout', '-out', str(self.public_key)], check=True, capture_output=True)
        der = subprocess.run(['openssl', 'pkey', '-pubin', '-in', str(self.public_key), '-outform', 'DER'],
                             check=True, capture_output=True).stdout
        self.fingerprint = hashlib.sha256(der).hexdigest()
        capabilities = dict(supervisorProtocol=1, workerProtocol=2, helperProtocol=1,
                            configFormat=1, stateFormat=1, appManifestFormats=[1, 2])
        self.runtime_source = ('import json\nprint(json.dumps(' + repr(dict(capabilities, databaseSchema=1)) + '))\n').encode()
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, 'w') as package:
            entry = zipfile.ZipInfo('updater.py'); entry.external_attr = (stat.S_IFREG | 0o600) << 16
            package.writestr(entry, self.runtime_source)
        payload = archive.getvalue()
        self.runtime_manifest = dict(format=1, kind='yachad-updater', version='1.0.0', archive='yachad-updater.zip',
                                     bytes=len(payload), sha256=hashlib.sha256(payload).hexdigest(), **capabilities)
        (self.bundle / 'yachad-updater.json').write_text(json.dumps(self.runtime_manifest))
        (self.bundle / 'yachad-updater.zip').write_bytes(payload)
        subprocess.run(['openssl', 'pkeyutl', '-sign', '-inkey', str(self.private_key), '-rawin',
                        '-in', str(self.bundle / 'yachad-updater.json'), '-out', str(self.bundle / 'yachad-updater.sig')],
                       check=True, capture_output=True)
        self.unsafe_owner = set()
        original_lstat = pathlib.Path.lstat
        original_fstat = os.fstat
        def owned(value, unsafe=False):
            fields = list(value)
            fields[4], fields[5] = (1001 if unsafe else 0), 0
            return os.stat_result(fields)
        self.stack.enter_context(patch.object(pathlib.Path, 'lstat', lambda path: owned(original_lstat(path), path in self.unsafe_owner)))
        self.stack.enter_context(patch.object(u.os, 'fstat', lambda fd: owned(original_fstat(fd), pathlib.Path(os.readlink('/proc/self/fd/' + str(fd))) in self.unsafe_owner)))
        self.stack.enter_context(patch.object(u.os, 'fchown', lambda fd, uid, gid: None))
        self.stack.enter_context(patch.object(u.os, 'chown', lambda path, uid, gid, **kwargs: None))
        self.stack.enter_context(patch.object(u, 'OLD_SHA256', hashlib.sha256(self.old).hexdigest()))
        self.stack.enter_context(patch.object(u, 'SUPERVISOR_SHA256', hashlib.sha256(supervisor_source).hexdigest()))
        self.stack.enter_context(patch.object(u, 'TRANSPORT_SHA256', hashlib.sha256(transport_source).hexdigest()))
        self.states = {unit: dict(LoadState='loaded', ActiveState='inactive', SubState='dead', MainPID='0', ControlPID='0')
                       for unit in (*u.QUIET_UNITS, u.RECOVERY_UNIT)}
        self.states[u.RECOVERY_UNIT].update(ActiveState='active', SubState='exited')
        for unit in u.SCHEDULERS:
            self.states[unit].update(ActiveState='active', SubState='waiting')
        self.actions = []
        self.worker = u.Bootstrap(self.root, self.source, inspect_units=lambda: self.states,
                                  scheduler=self.scheduler, ask=lambda _: 'y', runtime_dir=self.bundle,
                                  public_key=self.public_key, key_fingerprint=self.fingerprint,
                                  apply_boundary=lambda: None)
        self.original_config = self.config.read_bytes()
        self.original_status = self.status.read_bytes()

    def path(self, value):
        return self.root / value.lstrip('/')

    def scheduler(self, action, unit):
        self.actions.append((action, unit))
        self.states[unit].update(ActiveState='active' if action == 'start' else 'inactive',
                                 SubState='waiting' if action == 'start' else 'dead')

    def run_bootstrap(self, **kwargs):
        with contextlib.redirect_stdout(io.StringIO()):
            return self.worker.run(**kwargs)

    def sign_bundle(self, source, version='1.0.0', **changes):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            item = zipfile.ZipInfo('updater.py'); item.external_attr = (stat.S_IFREG | 0o600) << 16
            archive.writestr(item, source)
        payload = stream.getvalue()
        manifest = dict(self.runtime_manifest, version=version, bytes=len(payload),
                        sha256=hashlib.sha256(payload).hexdigest(), **changes)
        (self.bundle / 'yachad-updater.json').write_text(json.dumps(manifest))
        (self.bundle / 'yachad-updater.zip').write_bytes(payload)
        subprocess.run(['openssl', 'pkeyutl', '-sign', '-inkey', str(self.private_key), '-rawin',
                        '-in', str(self.bundle / 'yachad-updater.json'), '-out', str(self.bundle / 'yachad-updater.sig')],
                       check=True, capture_output=True)

    def assert_preserved(self):
        self.assertEqual(self.config.read_bytes(), self.original_config)
        self.assertEqual(self.status.read_bytes(), self.original_status)
        self.assertEqual(stat.S_IMODE(self.target.stat().st_mode), 0o640)
        self.assertFalse(list(self.target.parent.glob('.updater-reviewed-*')))

    def test_atomic_replacement_preserves_metadata_policy_journal_and_scheduler_states(self):
        original_inode = self.target.stat().st_ino
        original_replace = os.replace
        def replace(source, target):
            self.assertEqual(self.target.read_bytes(), self.old)
            if pathlib.Path(target) == self.target:
                self.assertEqual(pathlib.Path(source).read_bytes(), self.new)
            self.assertTrue(all(self.states[x]['ActiveState'] == 'inactive' for x in u.SCHEDULERS))
            original_replace(source, target)
        with patch.object(u.os, 'replace', replace):
            self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assertEqual(self.target.read_bytes(), self.new)
        self.assertNotEqual(self.target.stat().st_ino, original_inode)
        self.assertEqual(self.actions, [('stop', x) for x in u.SCHEDULERS] + [('start', x) for x in u.SCHEDULERS])
        self.assertTrue(all(self.states[x]['ActiveState'] == 'active' for x in u.SCHEDULERS))
        self.assert_preserved()

    def test_check_only_and_blank_cancel_are_read_only(self):
        original_inode = self.target.stat().st_ino
        self.assertIn('CHECK_OK', self.run_bootstrap(check_only=True))
        self.worker.ask = lambda _: ''
        self.assertIn('CANCELED', self.run_bootstrap())
        self.assertFalse(self.actions)
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertEqual(self.target.stat().st_ino, original_inode)
        self.assertFalse(self.path(u.KEY).exists())
        self.assertFalse(self.path(u.SUPERVISOR).exists())
        self.assertFalse(self.path(u.SOURCE_POLICY).exists())
        self.assertFalse((self.path(u.STATE) / 'runtime').exists())
        self.assert_preserved()

    def test_signed_bootstrap_provisions_transport_default_disabled_without_network(self):
        self.run_bootstrap()
        policy = json.loads(self.path(u.SOURCE_POLICY).read_bytes())
        self.assertEqual(policy, dict(format=1, enabled=False, repository='NehoraiHadad/yahad', channel='stable', checkHours=6))
        inputs = self.worker.inputs()
        supervisor = inputs['module'].Supervisor(self.root)
        self.assertEqual(inputs['transport_module'].refresh(supervisor, fetch=lambda *_: self.fail('no root downloads authorized')),
                         {'result': 'disabled'})
        self.assertEqual(self.path(u.TRANSPORT).read_bytes(), inputs['transport'])
        self.assertEqual(self.path(u.KEY).read_bytes(), self.public_key.read_bytes())
        journal = supervisor.journal()
        self.assertEqual(journal['phase'], 'stable')
        self.assertEqual(journal['active']['version'], '1.0.0')
        self.assertEqual(supervisor.pointer(), 'versions/v1.0.0')
        for path in self.worker.boundary_paths():
            self.assertEqual(path.read_bytes(), u.BOUNDARY)
            self.assertIn(u.SOURCE_POLICY.encode(), path.read_bytes())
            self.assertIn(u.TRANSPORT.encode(), path.read_bytes())
        self.assert_preserved()

    def test_updater_source_requires_explicit_opt_in_and_rerun_preserves_existing_policy(self):
        self.worker.enable_updater_updates = True
        self.run_bootstrap()
        self.assertIs(json.loads(self.path(u.SOURCE_POLICY).read_bytes())['enabled'], True)
        self.worker.enable_updater_updates = False
        self.actions.clear()
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap())
        self.assertIs(json.loads(self.path(u.SOURCE_POLICY).read_bytes())['enabled'], True)
        self.assertFalse(self.actions)

    def test_wrong_independent_fingerprint_and_existing_different_key_fail_before_changes(self):
        self.worker.key_fingerprint = '0' * 64
        with self.assertRaisesRegex(u.UpgradeError, 'fingerprint'):
            self.run_bootstrap(check_only=True)
        self.assertFalse(self.path(u.KEY).exists())
        self.worker.key_fingerprint = self.fingerprint
        self.path(u.KEY).write_bytes(b'An existing unrelated trust anchor')
        with self.assertRaisesRegex(u.UpgradeError, 'Existing bootstrap file differs'):
            self.run_bootstrap(check_only=True)
        self.assertEqual(self.path(u.KEY).read_bytes(), b'An existing unrelated trust anchor')
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertFalse(self.actions)

    def test_unsigned_tampered_or_wrong_protocol_bundle_never_provisions_root_files(self):
        archive = self.bundle / 'yachad-updater.zip'
        original = archive.read_bytes()
        archive.write_bytes(original + b'tampered')
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        archive.write_bytes(original)
        signature = self.bundle / 'yachad-updater.sig'
        original_signature = signature.read_bytes()
        signature.write_bytes(b'x' * 64)
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        signature.write_bytes(original_signature)
        self.sign_bundle(self.runtime_source, supervisorProtocol=2)
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.assertFalse(self.path(u.KEY).exists())
        self.assertFalse(self.path(u.SUPERVISOR).exists())
        self.assertFalse(self.actions)
        self.assertEqual(self.target.read_bytes(), self.old)

    def test_bootstrap_then_signed_transport_upgrade_dispatch_and_old_bundle_rerun(self):
        self.worker.enable_updater_updates = True
        self.run_bootstrap()
        initial = {name: (self.bundle / name).read_bytes() for name in
                   ('yachad-updater.json', 'yachad-updater.sig', 'yachad-updater.zip')}
        self.sign_bundle(self.runtime_source + b'# upgraded synthetic runtime\n', version='2.0.0')
        inputs = self.worker.inputs()
        supervisor = inputs['module'].Supervisor(self.root)
        repository = 'NehoraiHadad/yahad'
        urls = {name: f'https://api.github.com/repos/{repository}/releases/assets/{index}'
                for index, name in enumerate(initial, 1)}
        release = dict(tag_name='v0.4.1', draft=False, prerelease=False,
                       assets=[dict(name=name, url=url) for name, url in urls.items()])
        fetched = []
        payload = {url: (self.bundle / name).read_bytes() for name, url in urls.items()}
        def fetch(url, *_):
            fetched.append(url)
            return json.dumps(release).encode() if url.endswith('/releases/latest') else payload[url]
        result = inputs['transport_module'].refresh(supervisor, fetch=fetch, now=lambda: 1_800_000_000)
        self.assertEqual(result['result'], 'updated')
        self.assertEqual(supervisor.journal()['active']['version'], '2.0.0')
        self.assertEqual(supervisor.journal()['previous']['version'], '1.0.0')
        self.assertEqual(supervisor.pointer(), 'versions/v2.0.0')
        self.assertEqual(len(fetched), 4)
        self.assertEqual(json.loads(supervisor.dispatch('run'))['databaseSchema'], 1)
        journal = (self.path(u.STATE) / 'updater-runtime.json').read_bytes()
        for name, raw in initial.items():
            (self.bundle / name).write_bytes(raw)
        self.actions.clear()
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap())
        self.assertEqual((self.path(u.STATE) / 'updater-runtime.json').read_bytes(), journal)
        self.assertFalse(self.actions)
        self.assert_preserved()

    def test_failed_signed_probe_keeps_old_launcher_and_restores_schedulers(self):
        self.sign_bundle(b'raise SystemExit(1)\n')
        with self.assertRaises(Exception):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'active' for x in u.SCHEDULERS))
        self.assert_preserved()

    def test_exact_partial_provision_is_resumable_without_replacing_trust(self):
        self.worker.apply_boundary = lambda: (_ for _ in ()).throw(u.UpgradeError('synthetic reload interruption'))
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        key_inode = self.path(u.KEY).stat().st_ino
        self.worker.apply_boundary = lambda: None
        self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assertEqual(self.path(u.KEY).stat().st_ino, key_inode)
        self.assert_preserved()

    def test_probe_mutating_app_journal_does_not_restart_updater_schedulers(self):
        source = ("from pathlib import Path\nPath(" + repr(str(self.status)) + ").write_text('{\"phase\":\"recovery_required\"}')\n").encode()
        self.sign_bundle(source + self.runtime_source)
        with self.assertRaisesRegex(u.UpgradeError, 'schedulers remain paused'):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertEqual(json.loads(self.status.read_bytes())['phase'], 'recovery_required')
        self.assertTrue(all(self.states[x]['ActiveState'] == 'inactive' for x in u.SCHEDULERS))

    def test_already_current_is_idempotent_and_does_not_toggle_schedulers(self):
        self.run_bootstrap()
        self.actions.clear()
        inode = self.target.stat().st_ino
        self.worker.ask = lambda _: self.fail('already current must not ask')
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap())
        self.assertEqual(self.target.stat().st_ino, inode)
        self.assertFalse(self.actions)
        self.assert_preserved()

    def test_originally_inactive_schedulers_stay_inactive(self):
        self.states[u.SCHEDULERS[0]].update(ActiveState='inactive', SubState='dead')
        self.run_bootstrap()
        self.assertEqual(self.actions, [('stop', u.SCHEDULERS[1]), ('start', u.SCHEDULERS[1])])
        self.assertEqual(self.states[u.SCHEDULERS[0]]['ActiveState'], 'inactive')

    def test_exact_reviewed_current_direct_worker_from_fresh_install_can_enroll(self):
        self.target.write_bytes((ROOT / 'tests/fixtures/updater-direct-0.5.txt').read_bytes())
        self.assertIn('WORKER_UPGRADED', self.run_bootstrap())
        self.assertEqual(self.target.read_bytes(), u.LAUNCHER)
        self.assert_preserved()

    def test_unknown_source_or_target_refused_before_any_runtime_mutation(self):
        for target in (self.source, self.target):
            with self.subTest(target=target):
                previous = target.read_bytes()
                target.write_bytes(previous + b'# unexpected change\n')
                with self.assertRaises(u.UpgradeError):
                    self.run_bootstrap()
                self.assertFalse(self.actions)
                target.write_bytes(previous)
        self.assert_preserved()

    def test_pending_recovery_mode_or_request_journals_are_preserved(self):
        journals = [{'phase': phase} for phase in ('stopping', 'backing_up', 'activating', 'checking_health',
                    'rolling_back', 'recovery_required', 'rollback_pending_health', 'checking', 'building')]
        journals += [dict(phase='healthy', modeChangePending={}),
                     dict(phase='healthy', lastModeRequest={'state': 'pending'}),
                     dict(phase='healthy', lastRequest={'state': 'running'})]
        for journal in journals:
            with self.subTest(journal=journal):
                raw = json.dumps(journal).encode()
                self.status.write_bytes(raw)
                with self.assertRaises(u.UpgradeError):
                    self.run_bootstrap()
                self.assertEqual(self.status.read_bytes(), raw)
                self.assertEqual(self.target.read_bytes(), self.old)
                self.assertFalse(self.actions)

    def test_busy_lock_or_running_old_worker_is_refused(self):
        with self.lock.open('rb') as locked:
            u.fcntl.flock(locked, u.fcntl.LOCK_EX | u.fcntl.LOCK_NB)
            with self.assertRaisesRegex(u.UpgradeError, 'busy'):
                self.run_bootstrap()
        self.states[u.WORKERS[0]].update(ActiveState='activating', SubState='start', MainPID='123')
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.assertFalse(self.actions)
        self.assertEqual(self.target.read_bytes(), self.old)

    def test_confirmation_retains_worker_lock_and_releases_it_after_cancel(self):
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

    def test_old_worker_activated_while_pausing_is_never_replaced(self):
        def scheduler(action, unit):
            self.scheduler(action, unit)
            if action == 'stop':
                self.states[u.WORKERS[1]].update(ActiveState='activating', SubState='start', MainPID='123')
        self.worker.scheduler = scheduler
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'active' for x in u.SCHEDULERS))

    def test_atomic_failure_restores_schedulers_and_leaves_old_worker(self):
        with patch.object(u.os, 'replace', side_effect=OSError('synthetic replace failure')):
            with self.assertRaises(u.UpgradeError):
                self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'active' for x in u.SCHEDULERS))
        self.assert_preserved()

    def test_post_replace_durability_failure_is_explicit_not_blindly_reverted(self):
        original_sync = os.fsync
        def sync(fd):
            if self.target.read_bytes() == self.new and stat.S_ISDIR(os.fstat(fd).st_mode):
                raise OSError('synthetic directory sync failure')
            original_sync(fd)
        with patch.object(u.os, 'fsync', sync):
            with self.assertRaisesRegex(u.UpgradeError, 'replacement occurred'):
                self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.new)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'active' for x in u.SCHEDULERS))
        self.assert_preserved()

    def test_scheduler_stop_and_restore_failures_are_reported_without_worker_change(self):
        def scheduler(action, unit):
            if action == 'stop':
                self.scheduler(action, unit)
                raise u.UpgradeError('synthetic uncertain stop')
            self.scheduler(action, unit)
        self.worker.scheduler = scheduler
        with self.assertRaisesRegex(u.UpgradeError, 'uncertain stop'):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'active' for x in u.SCHEDULERS))
        self.actions.clear()
        def cannot_restore(action, unit):
            if action == 'start':
                raise u.UpgradeError('synthetic start failure')
            self.scheduler(action, unit)
        self.worker.scheduler = cannot_restore
        with self.assertRaisesRegex(u.UpgradeError, 'Scheduler restoration is unconfirmed'):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.new)

    def test_unknown_owner_writable_ancestor_or_symlink_is_refused(self):
        for path in (self.target, self.config, self.status, self.lock, self.target.parent):
            with self.subTest(unsafe_owner=path):
                self.unsafe_owner.add(path)
                with self.assertRaises(u.UpgradeError):
                    self.run_bootstrap()
                self.unsafe_owner.clear()
        self.target.parent.chmod(0o777)
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.target.parent.chmod(0o755)
        self.target.unlink()
        self.target.symlink_to(self.source)
        with self.assertRaises((u.UpgradeError, OSError)):
            self.run_bootstrap()
        self.assertEqual(hashlib.sha256(self.source.read_bytes()).hexdigest(), u.SUPERVISOR_SHA256)
        self.assertFalse(self.actions)

    def test_hardlinked_target_or_public_configuration_is_refused(self):
        alias = self.target.with_name('alias.py')
        os.link(self.target, alias)
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        alias.unlink()
        self.config.chmod(0o644)
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertFalse(self.actions)

    def test_missing_lock_is_never_created(self):
        self.lock.unlink()
        with self.assertRaises(FileNotFoundError):
            self.run_bootstrap()
        self.assertFalse(self.lock.exists())
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertFalse(self.actions)

    def test_source_symlink_is_refused_even_when_bytes_match(self):
        actual = self.source.with_name('actual.py')
        self.source.rename(actual)
        self.source.symlink_to(actual)
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertFalse(self.actions)

    def test_changed_config_during_confirmation_is_not_overwritten(self):
        changed = b'{"changed":"synthetic"}'
        def ask(_):
            self.config.write_bytes(changed)
            return 'y'
        self.worker.ask = ask
        with self.assertRaises(u.UpgradeError):
            self.run_bootstrap()
        self.assertEqual(self.config.read_bytes(), changed)
        self.assertEqual(self.target.read_bytes(), self.old)
        self.assertFalse(self.actions)


class UnitCommandTests(unittest.TestCase):
    def test_direct_execution_requires_isolation_before_sibling_imports(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = pathlib.Path(temporary)
            helper = directory / 'upgrade-worker.py'
            helper.write_bytes((ROOT / 'deploy/upgrade-worker.py').read_bytes())
            marker = directory / 'untrusted-import'
            (directory / 'hashlib.py').write_text('from pathlib import Path\nPath(' + repr(str(marker)) + ").write_text('unsafe')\n")
            refused = subprocess.run([u.sys.executable, str(helper), '--help'], capture_output=True, text=True)
            self.assertNotEqual(refused.returncode, 0)
            self.assertIn('isolated Python', refused.stderr)
            self.assertFalse(marker.exists())
            isolated = subprocess.run([u.sys.executable, '-I', '-B', str(helper), '--help'], capture_output=True, text=True)
            self.assertEqual(isolated.returncode, 0, isolated.stderr)
            self.assertIn('--public-key-sha256', isolated.stdout)
            self.assertFalse(marker.exists())

    def test_reload_verifies_explicit_key_policy_code_readonly_boundary_only(self):
        calls = []
        def command(args, **kwargs):
            calls.append(args)
            output = b'' if args[1] == 'daemon-reload' else ('ReadOnlyPaths=' + ' '.join(u.READ_ONLY) + '\nNeedDaemonReload=no\n').encode()
            return type('Result', (), {'returncode': 0, 'stdout': output})()
        with patch.object(u.subprocess, 'run', command):
            u.Bootstrap().reload_boundary()
        self.assertEqual(calls[0], ['/usr/bin/systemctl', 'daemon-reload'])
        self.assertEqual(len(calls), 4)
        self.assertTrue(all(args[1] == 'show' for args in calls[1:]))
        with patch.object(u.subprocess, 'run', return_value=type('Result', (), {'returncode': 0,
                          'stdout': b'ReadOnlyPaths=/unrelated\nNeedDaemonReload=no\n'})()):
            with self.assertRaises(u.UpgradeError):
                u.Bootstrap().reload_boundary()

    def test_inspection_requests_only_available_properties_and_does_not_mutate_services(self):
        calls = []
        def command(args, **kwargs):
            calls.append(args)
            properties = args[-1].removeprefix('--property=').split(',')
            unit = args[2]
            expected = dict(LoadState='loaded', ActiveState='inactive', SubState='dead', MainPID='0', ControlPID='0')
            if unit in u.SCHEDULERS:
                self.assertNotIn('MainPID', properties)
            return type('Result', (), {'returncode': 0, 'stdout': ''.join(k + '=' + expected[k] + '\n' for k in properties).encode()})()
        with patch.object(u.subprocess, 'run', command):
            self.assertEqual(set(u.unit_states()), set((*u.QUIET_UNITS, u.RECOVERY_UNIT)))
        self.assertTrue(all(call[:2] == ['/usr/bin/systemctl', 'show'] for call in calls))

    def test_scheduler_mutation_is_allowlisted_not_arbitrary_systemctl(self):
        with patch.object(u.subprocess, 'run', side_effect=AssertionError('must refuse first')):
            for action, unit in [('enable', u.SCHEDULERS[0]), ('stop', 'yachad.service'), ('start', u.WORKERS[0])]:
                with self.assertRaises(u.UpgradeError):
                    u.scheduler_action(action, unit)


if __name__ == '__main__':
    unittest.main()
