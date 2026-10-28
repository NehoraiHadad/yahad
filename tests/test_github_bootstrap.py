"""Rootless HTTPS enrollment fixtures: no real service, network or family data."""
import contextlib
import fcntl
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import socket
import stat
import subprocess
import tempfile
import unittest
import zipfile
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('github_bootstrap', ROOT / 'deploy/upgrade-worker-github.py')
u = importlib.util.module_from_spec(spec); spec.loader.exec_module(u)
LEGACY = (ROOT / 'tests/fixtures/updater-legacy-0.4.txt').read_bytes()
DIRECT = (ROOT / 'tests/fixtures/updater-direct-0.5.txt').read_bytes()


def write(path, raw, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(raw); path.chmod(mode)


class PinTests(unittest.TestCase):
    def test_reviewed_source_pins(self):
        for pin, name in [(u.BASE_SHA256, 'upgrade-worker-manual.py'), (u.SUPERVISOR_SHA256, 'updater-supervisor.py'),
                          (u.TRANSPORT_SHA256, 'updater-transport.py'), (u.INITIAL_WORKER_SHA256, 'updater.py')]:
            self.assertEqual(pin, hashlib.sha256((ROOT / 'deploy' / name).read_bytes()).hexdigest(), name)
        self.assertEqual(u.OLD_SHA256, hashlib.sha256(LEGACY).hexdigest())
        self.assertEqual(u.DIRECT_WORKER_SHA256, hashlib.sha256(DIRECT).hexdigest())


@unittest.skipUnless(os.name == 'posix', 'Linux flock/filesystem contract')
class BootstrapTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)
        self.stack = contextlib.ExitStack(); self.addCleanup(self.stack.close)
        self.target = self.path(u.TARGET); self.config = self.path(u.CONFIG)
        self.status = self.path(u.STATE) / 'status.json'
        operator = dict(familyId='10203040-5060-4070-8090-001122334455', userId='aabbccdd-eeff-4011-8233-445566778899')
        config = json.loads((ROOT / 'deploy/updates.json.example').read_bytes())
        config.update(operator=operator, enabled=True, mode='auto')
        write(self.config, json.dumps(config, indent=2).encode())
        write(self.status, json.dumps(dict(phase='healthy', currentVersion='0.5.0', operator=operator, releaseTrust='github-https')).encode(), 0o640)
        write(self.target, LEGACY, 0o640)
        write(self.path(u.STATE) / 'worker.lock', b'')
        for name in u.HELPER_SHA256: write(self.target.with_name(name), (ROOT / 'deploy' / name).read_bytes(), 0o644)
        self.source = self.root / 'reviewed/updater-supervisor.py'
        self.supervisor_raw = (ROOT / 'deploy/updater-supervisor.py').read_bytes()
        self.transport_raw = (ROOT / 'deploy/updater-transport.py').read_bytes()
        write(self.source, self.supervisor_raw, 0o644)
        write(self.source.with_name('updater-transport.py'), self.transport_raw, 0o644)
        self.capabilities = dict(supervisorProtocol=1, workerProtocol=3, helperProtocol=1, configFormat=1, stateFormat=1, appManifestFormats=[1, 2])
        self.runtime_source = ('import json\nprint(json.dumps(' + repr(dict(self.capabilities, databaseSchema=2)) + '))\n').encode()
        self.bundle(self.runtime_source)
        self.path('/etc/systemd/system').mkdir(parents=True)
        self.saved_config, self.saved_status = self.config.read_bytes(), self.status.read_bytes()
        self.states = {unit: dict(LoadState='loaded', ActiveState='inactive', SubState='dead', MainPID='0', ControlPID='0', Job='0', NeedDaemonReload='no')
                       for unit in (*u.base.QUIET_UNITS, u.RECOVERY_UNIT)}
        self.states[u.RECOVERY_UNIT].update(ActiveState='active', SubState='exited')
        for unit in u.SCHEDULERS: self.states[unit].update(ActiveState='active', SubState='waiting')
        self.actions, self.probes, self.reloads = [], [], []
        original_lstat, original_fstat = pathlib.Path.lstat, os.fstat
        def owned(info):
            fields = list(info); fields[4] = 0; fields[5] = 0
            return os.stat_result(fields)
        self.stack.enter_context(patch.object(pathlib.Path, 'lstat', lambda path: owned(original_lstat(path))))
        self.stack.enter_context(patch.object(os, 'fstat', lambda fd: owned(original_fstat(fd))))
        self.stack.enter_context(patch.object(os, 'fchown', lambda *args: None))
        self.stack.enter_context(patch.object(os, 'chown', lambda *args, **kwargs: None))
        self.stack.enter_context(patch.object(u, 'SUPERVISOR_SHA256', hashlib.sha256(self.supervisor_raw).hexdigest()))
        self.stack.enter_context(patch.object(u, 'TRANSPORT_SHA256', hashlib.sha256(self.transport_raw).hexdigest()))
        self.stack.enter_context(patch.object(u, 'INITIAL_WORKER_SHA256', hashlib.sha256(self.runtime_source).hexdigest()))
        self.stack.enter_context(patch.object(socket, 'socket', side_effect=AssertionError('No networking in bootstrap')))
        self.stack.enter_context(patch.object(subprocess, 'run', side_effect=AssertionError('No real systemctl/provider/worker subprocess in this fixture')))
        self.worker = u.Bootstrap(self.root, self.source, inspect_units=lambda: self.states,
                                  scheduler=self.scheduler, ask=lambda _: u.CONFIRMATION,
                                  apply_boundary=lambda: self.reloads.append('reload'))
        original_inputs = self.worker.inputs
        def inputs():
            result = original_inputs()
            def probe(supervisor, reference):
                supervisor.verify_runtime(reference)
                self.probes.append(reference)
            result['module'].Supervisor.probe = probe
            return result
        self.worker.inputs = inputs

    def path(self, absolute): return self.root / absolute.lstrip('/')

    def bundle(self, source, **changes):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            entry = zipfile.ZipInfo('updater.py'); entry.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(entry, source)
        payload = stream.getvalue()
        manifest = dict(format=1, kind='yachad-updater', version=u.INITIAL_RUNTIME_VERSION,
                        archive='yachad-updater.zip', bytes=len(payload), sha256=hashlib.sha256(payload).hexdigest(), **self.capabilities)
        manifest.update(changes)
        write(self.source.with_name('yachad-updater.json'), (json.dumps(manifest) + '\n').encode())
        write(self.source.with_name('yachad-updater.zip'), payload)

    def scheduler(self, action, unit):
        self.actions.append((action, unit))
        self.states[unit].update(ActiveState='active' if action == 'start' else 'inactive', SubState='waiting' if action == 'start' else 'dead')

    def run_bootstrap(self, **kwargs):
        with contextlib.redirect_stdout(io.StringIO()): return self.worker.run(**kwargs)

    def preserved(self):
        self.assertEqual(self.config.read_bytes(), self.saved_config)
        self.assertEqual(self.status.read_bytes(), self.saved_status)
        self.assertEqual(stat.S_IMODE(self.target.stat().st_mode), 0o640)
        self.assertFalse(self.path(u.KEY).exists())

    def test_enrolls_exact_legacy_worker_no_key_with_launcher_last(self):
        original = os.replace
        def replace(source, destination):
            if pathlib.Path(destination) == self.target:
                self.assertTrue(self.probes); self.assertTrue(self.reloads)
                self.assertEqual(self.target.read_bytes(), LEGACY)
                self.assertTrue(all(self.states[x]['ActiveState'] == 'inactive' for x in u.SCHEDULERS))
            return original(source, destination)
        with patch.object(os, 'replace', replace): self.assertIn('GITHUB_UPDATER_ENROLLED', self.run_bootstrap())
        self.assertEqual(self.target.read_bytes(), u.LAUNCHER)
        policy = json.loads(self.path(u.SOURCE_POLICY).read_bytes())
        self.assertEqual(policy['trust'], 'github-https'); self.assertIs(policy['enabled'], True)
        runtime = self.path(u.STATE) / 'runtime/versions/v1.0.0'
        self.assertEqual({x.name for x in runtime.iterdir()}, u.RUNTIME_FILES)
        reference = json.loads((self.path(u.STATE) / 'updater-runtime.json').read_bytes())['active']
        self.assertEqual(reference['manifestSha256'], hashlib.sha256((runtime / 'manifest.json').read_bytes()).hexdigest())
        self.assertEqual(self.actions, [('stop', x) for x in u.SCHEDULERS] + [('start', x) for x in u.SCHEDULERS])
        self.preserved()

    def test_current_direct_worker_is_supported_by_exact_known_hash(self):
        self.target.write_bytes(DIRECT)
        self.assertIn('GITHUB_UPDATER_ENROLLED', self.run_bootstrap())
        self.preserved()

    def test_check_and_cancel_are_read_only(self):
        for answer in ('', 'y', 'enable github updater'):
            self.worker.ask = lambda _, answer=answer: answer
            self.assertIn('CANCELED', self.run_bootstrap())
        self.assertIn('CHECK_OK', self.run_bootstrap(check_only=True))
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assertFalse(self.path(u.SOURCE_POLICY).exists()); self.assertFalse(self.actions); self.assertFalse(self.probes)
        self.preserved()

    def test_idempotence_preserves_disabled_policy_and_does_not_probe_or_toggle(self):
        self.run_bootstrap()
        policy = json.loads(self.path(u.SOURCE_POLICY).read_bytes()); policy['enabled'] = False
        self.path(u.SOURCE_POLICY).write_text(json.dumps(policy))
        self.actions.clear(); self.probes.clear(); self.reloads.clear()
        self.worker.ask = lambda _: self.fail('No repeated confirmation')
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap())
        self.assertIn('ALREADY_CURRENT', self.run_bootstrap(check_only=True))
        self.assertFalse(json.loads(self.path(u.SOURCE_POLICY).read_bytes())['enabled'])
        self.assertFalse(self.actions); self.assertFalse(self.probes); self.assertFalse(self.reloads)

    def test_signed_pins_and_signed_policy_never_convert(self):
        for pin in (u.KEY, u.APP_KEY):
            write(self.path(pin), b'synthetic public key')
            with self.assertRaises(u.UpgradeError): self.run_bootstrap()
            self.path(pin).unlink()
        write(self.path(u.SOURCE_POLICY), b'{"format":1,"enabled":true}')
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY); self.assertFalse(self.actions)

    def test_signed_app_configuration_is_not_rewritten(self):
        settings = json.loads(self.config.read_bytes()); settings['releaseTrust'] = 'signature'
        self.config.write_text(json.dumps(settings)); expected = self.config.read_bytes()
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertEqual(self.config.read_bytes(), expected); self.assertFalse(self.actions)

    def test_unknown_worker_helper_and_initial_runtime_fail_before_changes(self):
        self.target.write_bytes(b'# unknown worker')
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.target.write_bytes(LEGACY)
        helper = self.target.with_name('data-lock.py'); helper.write_bytes(b'# unknown helper')
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        helper.write_bytes((ROOT / 'deploy/data-lock.py').read_bytes())
        self.bundle(b'# unknown runtime')
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_present_signature_or_old_protocol_bundle_refused(self):
        write(self.source.with_name('yachad-updater.sig'), bytes(64))
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.source.with_name('yachad-updater.sig').unlink()
        self.bundle(self.runtime_source, workerProtocol=2)
        with self.assertRaises(Exception): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_pending_recovery_or_queued_systemd_job_refused(self):
        status = json.loads(self.status.read_bytes()); status['phase'] = 'recovery_required'
        self.status.write_text(json.dumps(status))
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.status.write_bytes(self.saved_status)
        self.states[u.WORKERS[0]]['Job'] = '15'
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_changed_confirmation_keeps_old_worker(self):
        def ask(_):
            self.target.write_bytes(b'# concurrent admin change')
            return u.CONFIRMATION
        self.worker.ask = ask
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), b'# concurrent admin change'); self.assertFalse(self.actions)

    def test_probe_failure_keeps_direct_worker_and_exact_partial_is_resumable(self):
        original = self.worker.inputs
        def failing():
            inputs = original()
            def probe(*_): raise RuntimeError('synthetic broken initial runtime')
            inputs['module'].Supervisor.probe = probe
            return inputs
        self.worker.inputs = failing
        with self.assertRaises(RuntimeError): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'active' for x in u.SCHEDULERS))
        self.worker.inputs = original
        self.assertIn('GITHUB_UPDATER_ENROLLED', self.run_bootstrap())

    def test_probe_mutation_leaves_schedulers_paused(self):
        original = self.worker.inputs
        def mutating():
            inputs = original()
            def probe(*_): self.status.write_bytes(b'{"phase":"recovery_required"}')
            inputs['module'].Supervisor.probe = probe
            return inputs
        self.worker.inputs = mutating
        with self.assertRaisesRegex(u.UpgradeError, 'schedulers remain paused'): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), LEGACY)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'inactive' for x in u.SCHEDULERS))

    def test_ambiguous_launcher_durability_leaves_schedulers_paused(self):
        original = self.worker.sync
        def sync(path):
            if path == self.target.parent and self.target.read_bytes() == u.LAUNCHER:
                raise OSError('synthetic directory sync failure')
            return original(path)
        self.worker.sync = sync
        with self.assertRaisesRegex(u.UpgradeError, 'schedulers remain paused'): self.run_bootstrap()
        self.assertEqual(self.target.read_bytes(), u.LAUNCHER)
        self.assertTrue(all(self.states[x]['ActiveState'] == 'inactive' for x in u.SCHEDULERS))

    def test_inactive_scheduler_is_never_enabled_or_started(self):
        self.states[u.SCHEDULERS[0]].update(ActiveState='inactive', SubState='dead')
        self.run_bootstrap()
        self.assertNotIn(('stop', u.SCHEDULERS[0]), self.actions)
        self.assertNotIn(('start', u.SCHEDULERS[0]), self.actions)

    def test_busy_lock_and_stale_units_are_refused(self):
        with (self.path(u.STATE) / 'worker.lock').open('rb') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaisesRegex(u.UpgradeError, 'busy'): self.run_bootstrap()
        self.states[u.WORKERS[0]]['NeedDaemonReload'] = 'yes'
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_extra_runtime_versions_are_not_adopted(self):
        (self.path(u.STATE) / 'runtime/versions/v9.0.0').mkdir(parents=True)
        with self.assertRaisesRegex(u.UpgradeError, 'Unknown prior runtime'): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_symlinked_reviewed_source_and_managed_path_are_refused(self):
        target = self.source.with_name('real-supervisor.py')
        self.source.rename(target); self.source.symlink_to(target)
        with self.assertRaises(ValueError): self.run_bootstrap()
        self.source.unlink(); target.rename(self.source)
        helper = self.target.with_name('data-lock.py')
        original = helper.with_name('real-helper.py')
        helper.rename(original); helper.symlink_to(original)
        with self.assertRaises(OSError): self.run_bootstrap()
        self.assertFalse(self.actions)

    def test_conflicting_partial_enrollment_is_preserved(self):
        write(self.path(u.SUPERVISOR), b'# signed or conflicting supervisor')
        with self.assertRaises(u.UpgradeError): self.run_bootstrap()
        self.assertEqual(self.path(u.SUPERVISOR).read_bytes(), b'# signed or conflicting supervisor')
        self.assertFalse(self.actions)


class CliTests(unittest.TestCase):
    def test_direct_execution_requires_isolation_before_sibling_loading(self):
        result = subprocess.run(['python3', '-B', str(ROOT / 'deploy/upgrade-worker-github.py'), '--check-only'],
                                capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Use isolated Python', result.stderr)

    def test_owner_guard_precedes_any_bootstrap_action(self):
        with patch.object(u.sys, 'argv', ['upgrade-worker-github.py']), \
             patch.object(u.base, 'owner_guard', side_effect=u.UpgradeError('owner confirmation required')), \
             patch.object(u, 'Bootstrap', side_effect=AssertionError('must not construct before guard')):
            with self.assertRaisesRegex(u.UpgradeError, 'owner confirmation required'): u.main()


if __name__ == '__main__': unittest.main()
