"""Isolated signed-runtime tests. Temporary Ed25519 keys only; no host/network/services."""
import fcntl
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import stat
import subprocess
import tempfile
import unittest
import zipfile
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('updater_supervisor', ROOT / 'deploy/updater-supervisor.py')
s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s)


def write(path, content, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    path.write_bytes(content)
    path.chmod(mode)


class Crash(BaseException):
    pass


class SupervisorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.keys = tempfile.TemporaryDirectory(prefix='yachad-ephemeral-runtime-keys-')
        cls.keydir = pathlib.Path(cls.keys.name)
        cls.private = cls.keydir / 'test-only.pem'
        cls.other_private = cls.keydir / 'other-test-only.pem'
        for private in (cls.private, cls.other_private):
            subprocess.run(['/usr/bin/openssl', 'genpkey', '-algorithm', 'ED25519', '-out', str(private)], check=True,
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.public = subprocess.check_output(['/usr/bin/openssl', 'pkey', '-in', str(cls.private), '-pubout'])

    @classmethod
    def tearDownClass(cls): cls.keys.cleanup()

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='yachad-runtime-fixture-')
        self.addCleanup(self.temporary.cleanup)
        self.home = pathlib.Path(self.temporary.name)
        self.source = ("import fcntl,json,os,sys\n"
                       "fd=int(sys.argv[sys.argv.index('--supervisor-lock-fd')+1])\n"
                       "os.fstat(fd)\n"
                       "fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)\n"
                       "if sys.argv[1]=='self-check':\n print(" + repr(json.dumps(dict(s.CAPABILITIES, databaseSchema=1))) + ")\n"
                       "elif sys.argv[1] not in ('run','requests','recover','boot-recover'): raise SystemExit(1)\n").encode()
        self.calls = []
        self.bundle1 = self.bundle('1.0.0')
        self.bundle2 = self.bundle('1.1.0', source=self.source + b'# newer runtime\n')
        self.supervisor = self.fixture(self.home / 'installed')

    def sign(self, raw, private=None):
        with tempfile.TemporaryDirectory(dir=self.home) as temporary:
            path = pathlib.Path(temporary) / 'manifest'
            path.write_bytes(raw)
            return subprocess.check_output(['/usr/bin/openssl', 'pkeyutl', '-sign', '-inkey', str(private or self.private),
                                            '-rawin', '-in', str(path)])

    def bundle(self, version='1.1.0', *, source=None, changes=None, entries=None, private=None):
        out = io.BytesIO()
        with zipfile.ZipFile(out, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            for name, content, mode in entries or [('updater.py', source or self.source, stat.S_IFREG | 0o644)]:
                item = zipfile.ZipInfo(name)
                item.create_system = 3
                item.external_attr = mode << 16
                item.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(item, content)
        archive = out.getvalue()
        manifest = dict(format=1, kind='yachad-updater', version=version, archive='yachad-updater.zip',
                        bytes=len(archive), sha256=hashlib.sha256(archive).hexdigest(), **s.CAPABILITIES)
        manifest.update(changes or {})
        raw = json.dumps(manifest, separators=(',', ':')).encode()
        return raw, self.sign(raw, private), archive

    def runner(self, args, **kwargs):
        self.calls.append((list(args), kwargs))
        self.assertEqual(args[:3], ['/usr/bin/python3', '-I', '-B'])
        self.assertIn(args[4], {'self-check', *s.COMMANDS})
        self.assertEqual(args[5], '--supervisor-lock-fd')
        self.assertEqual(kwargs['pass_fds'], (int(args[6]),))
        self.assertEqual(args[7], '--runtime-digest')
        os.fstat(int(args[6]))
        return json.dumps(dict(s.CAPABILITIES, databaseSchema=1)).encode()

    def fixture(self, root, runner=None):
        root.mkdir(mode=0o700)
        config = dict(stateDirectory='/var/lib/yachad-updates', releaseTrust='github-https', mode='auto')
        write(root / 'etc/yachad/updates.json', json.dumps(config).encode())
        write(root / 'etc/yachad/updater-signing.pub', self.public, 0o644)
        write(root / 'usr/local/lib/yachad-updater/updater-supervisor.py', b'immutable supervisor sentinel')
        runtime = root / 'var/lib/yachad-updates/runtime'
        directory = runtime / 'versions/v1.0.0'
        directory.mkdir(parents=True, mode=0o700)
        raw, signature, archive = self.bundle1
        write(directory / 'manifest.json', raw)
        write(directory / 'manifest.sig', signature)
        write(directory / 'payload.zip', archive)
        write(directory / 'updater.py', self.source)
        reference = dict(version='1.0.0', sha256=hashlib.sha256(archive).hexdigest())
        write(runtime.parent / 'updater-runtime.json', json.dumps(dict(format=1, phase='stable', active=reference,
              previous=None, candidate=None, maxAcceptedVersion='1.0.0')).encode())
        write(runtime.parent / 'status.json', b'{"phase":"healthy","releaseTrust":"github-https"}')
        (runtime / 'current').symlink_to('versions/v1.0.0')
        return s.Supervisor(root, owner_uid=os.getuid(), runner=runner or self.runner)

    def stable(self, supervisor, expected=None):
        journal = supervisor.journal()
        self.assertEqual(journal['phase'], 'stable')
        self.assertEqual(supervisor.pointer(), 'versions/v' + journal['active']['version'])
        supervisor.verify_runtime(journal['active'])
        if expected: self.assertEqual(journal['active']['version'], expected)
        return journal

    def test_real_signature_activation_dispatch_and_no_privileged_configuration_changes(self):
        u = self.supervisor
        untouched = {path: path.read_bytes() for path in [u.config_path, u.key,
                     u.root / 'usr/local/lib/yachad-updater/updater-supervisor.py', u.state / 'status.json']}
        u.stage(*self.bundle2)
        self.assertEqual(u.journal()['phase'], 'prepared')
        self.assertEqual(u.pointer(), 'versions/v1.0.0')
        u.activate()
        journal = self.stable(u, '1.1.0')
        self.assertEqual(journal['previous']['version'], '1.0.0')
        u.dispatch('run')
        self.assertEqual([call[0][4] for call in self.calls], ['self-check', 'self-check', 'run'])
        self.assertEqual(untouched, {path: path.read_bytes() for path in untouched})
        self.assertEqual(len(s.public_key_der(self.public, u.runtime)), 44)

    def test_tampering_wrong_key_app_kind_and_unknown_fields_reject_before_runtime_execution(self):
        raw, signature, archive = self.bundle2
        invalid = [(raw + b' ', signature, archive), (raw, bytes(64), archive), (raw, signature, archive + b'x'),
                   self.bundle(private=self.other_private), self.bundle(changes={'kind': 'yachad-app'}),
                   self.bundle(changes={'publicKeyFile': '/attacker/key'}), self.bundle(changes={'command': 'shell'})]
        for bundle in invalid:
            with self.subTest(bundle=bundle[0][:80]), self.assertRaises(s.SupervisorError):
                self.supervisor.stage(*bundle)
        self.assertEqual(self.calls, [])
        self.stable(self.supervisor, '1.0.0')

    def test_strict_protocol_versions_types_and_duplicate_fields(self):
        for change in [{'supervisorProtocol': 2}, {'workerProtocol': 1}, {'helperProtocol': 2}, {'configFormat': 2},
                       {'stateFormat': 2}, {'appManifestFormats': [1]}, {'appManifestFormats': [True, 2]},
                       {'workerProtocol': True}, {'version': 'latest'}, {'version': '../1.1.0'}, {'format': True}]:
            with self.subTest(change=change), self.assertRaises(s.SupervisorError): self.supervisor.stage(*self.bundle(changes=change))
        raw, _, archive = self.bundle2
        raw = raw[:-1] + b',"format":1}'
        with self.assertRaises(s.SupervisorError): self.supervisor.stage(raw, self.sign(raw), archive)
        self.assertEqual(self.calls, [])

    def test_future_signed_worker_capabilities_upgrade_without_changing_immutable_supervisor_abi(self):
        u = self.supervisor
        def future_runner(args, **kwargs):
            self.runner(args, **kwargs)
            manifest = json.loads((pathlib.Path(args[3]).parent / 'manifest.json').read_bytes())
            return json.dumps(dict({key: manifest[key] for key in s.CAPABILITIES}, databaseSchema=1_000_000)).encode()
        u.runner = future_runner
        u.install_bundle(*self.bundle(changes={'workerProtocol': 3, 'appManifestFormats': [1, 2, 3]}))
        self.stable(u, '1.1.0')

    def test_malformed_or_incompatible_probe_never_activates_candidate(self):
        u = self.supervisor
        invalid = [b'x' * 8193, b'[]', b'{}', json.dumps(dict(s.CAPABILITIES, databaseSchema=True)).encode(),
                   json.dumps(dict(s.CAPABILITIES, databaseSchema=1, workerProtocol=3)).encode(),
                   json.dumps(dict(s.CAPABILITIES, databaseSchema=1, appManifestFormats=[True, 2])).encode()]
        for bad in invalid:
            def runner(args, **kwargs):
                return bad if '/v1.1.0/' in args[3] else self.runner(args, **kwargs)
            u.runner = runner
            with self.subTest(bad=bad[:40]), self.assertRaises(s.SupervisorError): u.install_bundle(*self.bundle2)
            self.stable(u, '1.0.0')

    def test_probe_config_or_status_mutation_stops_for_review_even_if_probe_also_fails(self):
        for fails in (False, True):
            u = self.fixture(self.home / ('probe-mutation-' + str(fails)))
            def mutating(args, **kwargs):
                write(u.state / 'status.json', b'{"phase":"healthy","unexpectedMutation":true}')
                if fails: raise s.SupervisorError('runtime')
                return self.runner(args, **kwargs)
            u.runner = mutating
            with self.assertRaises(s.SupervisorError): u.install_bundle(*self.bundle2)
            self.assertEqual(u.journal()['phase'], 'recovery_required')
            self.assertEqual(u.pointer(), 'versions/v1.0.0')
            with self.assertRaises(s.SupervisorError): u.dispatch('run')

    def test_zip_allowlist_path_links_duplicates_modes_and_size_are_checked_before_execution(self):
        for entries in [[('../updater.py', b'pass', stat.S_IFREG | 0o644)],
                        [('/updater.py', b'pass', stat.S_IFREG | 0o644)],
                        [('updater.py', b'pass', stat.S_IFLNK | 0o777)],
                        [('updater.py', b'pass', stat.S_IFREG | 0o666)],
                        [('updater.py', b'pass', stat.S_IFREG | 0o4644)],
                        [('updater.py', b'pass', stat.S_IFREG | 0o644), ('arbitrary-hook.py', b'pass', stat.S_IFREG | 0o644)],
                        [('updater.py', b'pass', stat.S_IFREG | 0o644)] * 2,
                        [('updater.py', b'#' * (s.MAX_SOURCE + 1), stat.S_IFREG | 0o644)]]:
            with self.subTest(entries=[entry[0] for entry in entries]), self.assertRaises(s.SupervisorError):
                self.supervisor.stage(*self.bundle(entries=entries))
        self.assertEqual(self.calls, [])

    def test_replay_and_same_version_replacement_are_distinct_from_local_previous_recovery(self):
        u = self.supervisor
        u.stage(*self.bundle2); u.activate()
        self.assertEqual(u.stage(*self.bundle2), u.journal()['active'], 'identical accepted runtime is idempotent')
        for bundle in [self.bundle1, self.bundle('1.1.0', source=b'raise SystemExit(1)\n')]:
            with self.assertRaises(s.SupervisorError): u.stage(*bundle)
        u.stage(*self.bundle('1.2.0', source=self.source + b'# third\n'))
        u.recover()  # Local authenticated previous is legal, not a network downgrade.
        self.stable(u, '1.1.0')
        self.assertTrue((u.versions / 'v1.0.0').exists())

    def test_signed_unstartable_candidate_restores_old_and_never_runs_app_work(self):
        u = self.supervisor
        u.runner = s.fixed_process
        u.stage(*self.bundle(source=b'raise SystemExit(77)\n'))
        with self.assertRaises(s.SupervisorError): u.activate()
        self.stable(u, '1.0.0')
        u.dispatch('recover')  # Real Python process receives the inherited locked fd.

    def test_committed_runtime_never_rolls_back_for_later_application_database_failure(self):
        u = self.supervisor
        u.stage(*self.bundle2); u.activate()
        current = u.journal()['active']
        def failing(args, **kwargs):
            write(u.state / 'status.json', json.dumps(dict(phase='recovery_required', runtimeDigest=current['sha256'],
                  previousDatabaseSchema=1, targetDatabaseSchema=2)).encode())
            raise s.SupervisorError('app_failed_after_schema_commit')
        u.runner = failing
        with self.assertRaises(s.SupervisorError): u.dispatch('run')
        self.stable(u, '1.1.0')
        u.recover()
        self.stable(u, '1.1.0')
        with self.assertRaises(s.SupervisorError): u.stage(*self.bundle('1.2.0'))

    def test_app_pending_work_blocks_switch_and_mismatching_recovery_digest_blocks_dispatch(self):
        u = self.supervisor
        for status in [dict(phase=phase) for phase in ['preparing', 'checking', 'stopping', 'backing_up', 'activating',
                      'checking_health', 'rolling_back', 'rollback_pending_health', 'recovery_required', 'unknown']] + [
                      dict(phase='healthy', modeChangePending={}), dict(phase='healthy', lastRequest={'state': 'running'})]:
            write(u.state / 'status.json', json.dumps(status).encode())
            with self.subTest(status=status), self.assertRaises(s.SupervisorError): u.stage(*self.bundle2)
        write(u.state / 'status.json', b'{"phase":"recovering","runtimeDigest":"wrong"}')
        with self.assertRaises(s.SupervisorError): u.dispatch('recover')
        write(u.state / 'status.json', json.dumps(dict(phase='unknown', runtimeDigest=u.journal()['active']['sha256'])).encode())
        with self.assertRaises(s.SupervisorError): u.dispatch('boot-recover')
        for key in ('lastRequest', 'lastModeRequest'):
            for malformed in ('pending', [], {'state': True}, {'state': 'unknown'}, {}):
                write(u.state / 'status.json', json.dumps(dict(phase='healthy', **{key: malformed})).encode())
                with self.assertRaises(s.SupervisorError): u.stage(*self.bundle2)
        self.assertEqual(self.calls, [])

    def test_crash_during_first_check_uses_newly_bound_digest_for_boot_recovery(self):
        u = self.supervisor
        def checking(args, **kwargs):
            self.runner(args, **kwargs)
            if args[4] == 'run':
                write(u.state / 'status.json', json.dumps(dict(phase='checking', runtimeDigest=args[8])).encode())
                raise s.SupervisorError('early_worker_crash')
            return b''
        u.runner = checking
        with self.assertRaises(s.SupervisorError): u.dispatch('run')
        u.dispatch('boot-recover')
        self.assertEqual([call[0][4] for call in self.calls], ['run', 'boot-recover'])
        self.stable(u, '1.0.0')

    def test_missing_unsafe_symlink_hardlink_paths_keys_and_tampered_runtime_fail_closed(self):
        u = self.supervisor
        for target in [u.key, u.config_path, u.journal_path]:
            original = target.read_bytes()
            target.chmod(0o666)
            with self.subTest(target=target), self.assertRaises((s.SupervisorError, OSError)):
                (s.Supervisor(u.root, owner_uid=os.getuid()) if target == u.config_path else u.stage(*self.bundle2))
            target.chmod(0o600)
            saved = target.with_name(target.name + '.saved'); target.rename(saved); target.symlink_to(saved)
            with self.assertRaises((s.SupervisorError, OSError)):
                (s.Supervisor(u.root, owner_uid=os.getuid()) if target == u.config_path else u.stage(*self.bundle2))
            target.unlink(); saved.rename(target)
            self.assertEqual(target.read_bytes(), original)
        u.runtime.chmod(0o777)
        with self.assertRaises(s.SupervisorError): u.stage(*self.bundle2)
        u.runtime.chmod(0o700)
        active = u.versions / 'v1.0.0/updater.py'
        original = active.read_bytes(); active.write_bytes(b'pass # tampered\n')
        with self.assertRaises(s.SupervisorError): u.dispatch('run')
        active.write_bytes(original)
        os.link(u.key, u.key.with_name('linked-key'))
        with self.assertRaises(s.SupervisorError): u.stage(*self.bundle2)
        self.assertEqual(self.calls, [])

    def test_lock_serializes_contenders_and_inherited_descriptor_remains_locked_until_exit(self):
        u = self.supervisor
        other = s.Supervisor(u.root, owner_uid=os.getuid(), runner=self.runner)
        with u.locked():
            with self.assertRaises(s.SupervisorError): other.stage(*self.bundle2)
            fd = os.open(u.state / 'worker.lock', os.O_RDWR)
            try:
                with self.assertRaises(BlockingIOError): fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            finally: os.close(fd)
        other.stage(*self.bundle2)
        other.recover()
        self.stable(other, '1.0.0')

    def test_trusted_transport_guard_revalidates_intent_inside_the_activation_lock(self):
        u = self.supervisor
        checked = []
        def guard():
            self.assertIsNotNone(u.lock_fd)
            self.assertEqual(u.journal()['phase'], 'stable')
            fd = os.open(u.state / 'worker.lock', os.O_RDWR)
            try:
                with self.assertRaises(BlockingIOError): fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            finally: os.close(fd)
            checked.append(True)
            return False
        with self.assertRaises(s.SupervisorError): u.install_bundle(*self.bundle2, guard=guard)
        self.assertEqual(checked, [True])
        self.assertEqual(self.calls, [])
        self.assertFalse((u.versions / 'v1.1.0').exists())
        self.stable(u, '1.0.0')
        u.install_bundle(*self.bundle2, guard=lambda: True)
        self.stable(u, '1.1.0')

    def test_changed_intent_after_either_probe_rolls_back_before_any_application_work(self):
        for cancel_at in (2, 3):
            u = self.fixture(self.home / ('changed-intent-' + str(cancel_at)))
            phases = []
            def guard():
                phases.append(u.journal()['phase'])
                return len(phases) != cancel_at
            with self.assertRaises(s.SupervisorError): u.install_bundle(*self.bundle2, guard=guard)
            self.stable(u, '1.0.0')
            self.assertEqual(phases, ['stable', 'prepared', 'probing'][:cancel_at])
        self.assertTrue(all(call[0][4] == 'self-check' for call in self.calls))

    def test_transport_is_only_the_checked_immutable_fixed_path_not_an_app_import(self):
        u = self.supervisor
        u.events = []
        path = u.path(s.TRANSPORT)
        write(path, b"def refresh(supervisor, command):\n supervisor.events.append(command)\n return {'result':'synthetic'}\n")
        self.assertEqual(s.refresh_runtime(u), {'result': 'synthetic'})
        self.assertEqual(u.events, ['run'])
        path.chmod(0o666)
        with self.assertRaises(s.SupervisorError): s.refresh_runtime(u)
        path.chmod(0o600)
        saved = path.with_suffix('.saved'); path.rename(saved); path.symlink_to(saved)
        with self.assertRaises(OSError): s.refresh_runtime(u)
        self.assertEqual(u.events, ['run'], 'unsafe transport must not execute')

    def test_main_checks_run_and_request_intent_but_keeps_recovery_dispatch_offline(self):
        u = self.supervisor
        events = []
        for command in sorted(s.COMMANDS):
            events.clear()
            with patch.object(s.os, 'geteuid', return_value=0), patch.object(s.sys, 'argv', ['supervisor', command]), \
                    patch.object(s, 'Supervisor', return_value=u), patch.object(s, 'refresh_runtime', side_effect=lambda _, value: events.append('refresh-' + value)), \
                    patch.object(u, 'dispatch', side_effect=lambda value: events.append(value)):
                s.main()
            self.assertEqual(events, ['refresh-' + command, command] if command in ('run', 'requests') else [command])
        events.clear()
        with patch.object(s.os, 'geteuid', return_value=0), patch.object(s.sys, 'argv', ['supervisor', 'run']), \
                patch.object(s, 'Supervisor', return_value=u), patch.object(s, 'refresh_runtime', side_effect=RuntimeError('synthetic transport failure')), \
                patch.object(u, 'dispatch', side_effect=lambda value: events.append(value)), patch.object(s.sys, 'stderr', io.StringIO()) as error:
            s.main()
        self.assertEqual(events, ['run'])
        self.assertEqual(error.getvalue().strip(), 'YACHAD_UPDATER_RUNTIME_CHECK_FAILED')

    def test_crash_after_every_fsync_rename_journal_and_probe_keeps_complete_old_or_new_runtime(self):
        def attempt(root, fail_at=None):
            u = self.fixture(root)
            count = 0
            def checkpoint():
                nonlocal count
                count += 1
                if count == fail_at: raise Crash()
            def wrap(fn):
                def called(*args, **kwargs):
                    value = fn(*args, **kwargs); checkpoint(); return value
                return called
            originals = s.os.fsync, s.os.replace, s.os.rename, u.probe
            with patch.object(s.os, 'fsync', wrap(originals[0])), patch.object(s.os, 'replace', wrap(originals[1])), \
                    patch.object(s.os, 'rename', wrap(originals[2])), patch.object(u, 'probe', wrap(originals[3])):
                try: u.stage(*self.bundle2); u.activate()
                except Crash: pass
            restarted = s.Supervisor(root, owner_uid=os.getuid(), runner=self.runner)
            restarted.recover()
            result = self.stable(restarted)
            self.assertIn(result['active']['version'], ('1.0.0', '1.1.0'))
            self.assertTrue(all(call[0][4] == 'self-check' for call in self.calls), 'no app work before runtime commit')
            return count
        total = attempt(self.home / 'count-boundaries')
        self.assertGreater(total, 15)
        for boundary in range(1, total + 1):
            with self.subTest(boundary=boundary): attempt(self.home / ('crash-' + str(boundary)), boundary)


if __name__ == '__main__': unittest.main()
