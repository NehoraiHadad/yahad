"""Explicit GitHub-HTTPS runtime enrollment; entirely synthetic local fixtures.

No network, service or host installation is performed. These tests deliberately
create no signing credentials: the publisher trust is the enrolled HTTPS source,
not the SHA-256 values in the release envelope.
"""
import fcntl
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import pwd
import stat
import tempfile
import unittest
import uuid
import zipfile
from unittest.mock import patch


ROOT = pathlib.Path(__file__).resolve().parents[1]


def module(name, file):
    spec = importlib.util.spec_from_file_location(name, file)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


s = module('github_runtime_supervisor', ROOT / 'deploy/updater-supervisor.py')
t = module('github_runtime_transport', ROOT / 'deploy/updater-transport.py')
w = module('github_runtime_worker', ROOT / 'deploy/updater.py')


def write(path, raw, mode=0o600):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    path.write_bytes(raw)
    path.chmod(mode)


def json_bytes(value):
    return json.dumps(value, separators=(',', ':')).encode()


def policy(**changes):
    value = dict(format=2, trust='github-https', enabled=True,
                 repository='NehoraiHadad/yahad', channel='stable', checkHours=6)
    value.update(changes)
    return value


def app_bundle(version='0.6.0'):
    """Small real ZIP with a format-2 app envelope; contains no live data."""
    out = io.BytesIO()
    with zipfile.ZipFile(out, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('package.json', json.dumps(dict(version=version)))
        archive.writestr('src/main.ts', '// synthetic application fixture')
    archive = out.getvalue()
    manifest = dict(format=2, version=version, archive='yachad-app.zip', bytes=len(archive),
                    sha256=hashlib.sha256(archive).hexdigest(), databaseSchema=3,
                    databaseMigrationsFrom=[2], minimumUpdaterProtocol=3)
    return json_bytes(manifest), archive


class HttpsRuntimeFixture:
    """Reusable fixture also consumed by the joined real-child integration."""

    def __init__(self, test, *, runner=None):
        self.test = test
        temporary = tempfile.TemporaryDirectory(prefix='yachad-https-runtime-')
        test.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name) / 'installed'
        self.root.mkdir(mode=0o700)
        self.calls = []
        self.capabilities = dict(s.CAPABILITIES, workerProtocol=3)
        self.source = ("import fcntl,json,os,sys\n"
                       "fd=int(sys.argv[sys.argv.index('--supervisor-lock-fd')+1])\n"
                       "os.fstat(fd)\n"
                       "fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)\n"
                       "if sys.argv[1]=='self-check':\n print(" +
                       repr(json.dumps(dict(self.capabilities, databaseSchema=2))) + ")\n"
                       "elif sys.argv[1] not in ('run','requests','recover','boot-recover'): raise SystemExit(1)\n").encode()
        self.config = dict(stateDirectory='/var/lib/yachad-updates', releaseTrust='github-https',
                           enabled=True, mode='auto')
        write(self.root / 'etc/yachad/updates.json', json_bytes(self.config))
        write(self.root / 'etc/yachad/updater-source.json', json_bytes(policy()))
        write(self.root / 'usr/local/lib/yachad-updater/updater-supervisor.py', b'immutable test sentinel')
        self.bundle1 = self.bundle('1.0.0')
        self.bundle2 = self.bundle('1.1.0', source=self.source + b'# new runtime\n')
        runtime = self.root / 'var/lib/yachad-updates/runtime'
        initial = runtime / 'versions/v1.0.0'
        raw, _, archive = self.bundle1
        write(initial / 'manifest.json', raw)
        write(initial / 'payload.zip', archive)
        write(initial / 'updater.py', self.source)
        reference = dict(version='1.0.0', sha256=hashlib.sha256(archive).hexdigest(),
                         manifestSha256=hashlib.sha256(raw).hexdigest())
        write(runtime.parent / 'updater-runtime.json', json_bytes(dict(format=1, phase='stable', active=reference,
              previous=None, candidate=None, maxAcceptedVersion='1.0.0')))
        write(runtime.parent / 'status.json', b'{"phase":"healthy","releaseTrust":"github-https"}')
        (runtime / 'current').symlink_to('versions/v1.0.0')
        self.supervisor = s.Supervisor(self.root, owner_uid=os.getuid(), runner=runner or self.runner)

    def bundle(self, version='1.1.0', *, source=None, changes=None, entries=None):
        out = io.BytesIO()
        with zipfile.ZipFile(out, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            for name, content, mode in entries or [('updater.py', self.source if source is None else source, stat.S_IFREG | 0o644)]:
                item = zipfile.ZipInfo(name)
                item.create_system = 3
                item.external_attr = mode << 16
                item.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(item, content)
        archive = out.getvalue()
        value = dict(format=1, kind='yachad-updater', version=version, archive='yachad-updater.zip',
                     bytes=len(archive), sha256=hashlib.sha256(archive).hexdigest(), **self.capabilities)
        value.update(changes or {})
        return json_bytes(value), None, archive

    def runner(self, args, **kwargs):
        self.calls.append((list(args), kwargs))
        self.test.assertEqual(args[:3], ['/usr/bin/python3', '-I', '-B'])
        self.test.assertIn(args[4], {'self-check', *s.COMMANDS})
        fd = int(args[args.index('--supervisor-lock-fd') + 1])
        self.test.assertIn(fd, kwargs['pass_fds'])
        os.fstat(fd)
        separate = os.open(self.supervisor.state / 'worker.lock', os.O_RDONLY)
        try:
            with self.test.assertRaises(BlockingIOError):
                fcntl.flock(separate, fcntl.LOCK_EX | fcntl.LOCK_NB)
        finally:
            os.close(separate)
        manifest = json.loads((pathlib.Path(args[3]).parent / 'manifest.json').read_bytes())
        return json_bytes(dict({key: manifest[key] for key in s.CAPABILITIES}, databaseSchema=2))

    def stable(self, expected):
        u = self.supervisor
        journal = u.journal()
        self.test.assertEqual(journal['phase'], 'stable')
        self.test.assertEqual(journal['active']['version'], expected)
        self.test.assertEqual(u.pointer(), 'versions/v' + expected)
        u.verify_runtime(journal['active'])
        return journal


class GithubRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.fixture = HttpsRuntimeFixture(self)
        self.u = self.fixture.supervisor

    def test_explicit_profile_installs_without_keys_and_preserves_boundary(self):
        u = self.u
        untouched = {path: path.read_bytes() for path in [u.config_path,
                     u.root / 'etc/yachad/updater-source.json',
                     u.root / 'usr/local/lib/yachad-updater/updater-supervisor.py', u.state / 'status.json']}
        u.install_bundle(*self.fixture.bundle2)
        journal = self.fixture.stable('1.1.0')
        self.assertEqual(journal['previous']['version'], '1.0.0')
        u.dispatch('run')
        self.assertEqual([call[0][4] for call in self.fixture.calls], ['self-check', 'self-check', 'run'])
        self.assertEqual(untouched, {path: path.read_bytes() for path in untouched})
        self.assertFalse((u.root / 'etc/yachad/updater-signing.pub').exists())
        self.assertEqual({p.name for p in (u.versions / 'v1.1.0').iterdir()},
                         {'updater.py', 'manifest.json', 'payload.zip'})

    def test_app_https_trust_alone_does_not_authorize_unsigned_runtime(self):
        path = self.u.root / 'etc/yachad/updater-source.json'
        path.unlink()
        with self.assertRaises((s.SupervisorError, OSError)):
            self.u.install_bundle(*self.fixture.bundle2)
        self.assertEqual(self.fixture.calls, [])
        self.assertEqual(json.loads(self.u.journal_path.read_bytes())['active']['version'], '1.0.0')

    def test_legacy_signed_policy_does_not_fall_back_to_https(self):
        signed = policy(format=1)
        del signed['trust']
        write(self.u.root / 'etc/yachad/updater-source.json', json_bytes(signed))
        with self.assertRaises((s.SupervisorError, OSError)):
            self.u.install_bundle(*self.fixture.bundle2)
        self.assertEqual(self.fixture.calls, [])
        self.assertEqual(json.loads(self.u.journal_path.read_bytes())['active']['version'], '1.0.0')

    def test_existing_signing_pin_blocks_unsigned_profile(self):
        write(self.u.root / 'etc/yachad/updater-signing.pub', b'existing pin must not be ignored', 0o644)
        with self.assertRaises((s.SupervisorError, OSError)):
            self.u.install_bundle(*self.fixture.bundle2)
        self.assertEqual(self.fixture.calls, [])
        self.assertEqual(json.loads(self.u.journal_path.read_bytes())['active']['version'], '1.0.0')

    def test_five_minute_https_policy_is_valid(self):
        write(self.u.root / 'etc/yachad/updater-source.json', json_bytes(policy(checkHours=1/12)))
        self.assertEqual(self.u.trust_profile(), 'github-https')
        self.assertEqual(t.policy(self.u)['checkHours'] * 3600, 300)

    def test_profile_is_exact_fixed_source_not_a_general_url_or_script(self):
        path = self.u.root / 'etc/yachad/updater-source.json'
        original = path.read_bytes()
        changes = [{'repository':'other/repository'}, {'repository':'NehoraiHadad/yahad/extra'},
                   {'format':True}, {'trust':'none'}, {'trust':'signature'}, {'enabled':1},
                   {'channel':'nightly'}, {'checkHours':1/13}, {'checkHours':float('inf')}, {'checkHours':float('nan')}, {'checkHours':True}, {'checkHours':0}, {'checkHours':169},
                   {'url':'https://github.com/attacker/release'}, {'command':'run.sh'},
                   {'publicKeyFile':'/attacker/key'}]
        for change in changes:
            with self.subTest(change=change):
                write(path, json_bytes(policy(**change)))
                with self.assertRaises((s.SupervisorError, OSError)): self.u.install_bundle(*self.fixture.bundle2)
        write(path, original)
        self.assertEqual(self.fixture.calls, [])
        self.fixture.stable('1.0.0')

    def test_hash_and_size_are_mandatory_integrity_checks(self):
        raw, _, archive = self.fixture.bundle2
        cases = [(raw, None, archive + b'bad'),
                 self.fixture.bundle(changes={'sha256':'a'*64}),
                 self.fixture.bundle(changes={'bytes':1}),
                 self.fixture.bundle(changes={'bytes':True})]
        for bundle in cases:
            with self.subTest(manifest=bundle[0][:70]), self.assertRaises(s.SupervisorError):
                self.u.install_bundle(*bundle)
        self.assertEqual(self.fixture.calls, [])
        self.fixture.stable('1.0.0')

    def test_unsigned_envelope_cannot_add_privileged_scope(self):
        for change in ({'kind':'yachad-app'}, {'command':'sh'}, {'url':'https://github.com/x'},
                       {'path':'/etc/systemd/system/x'}, {'publicKeyFile':'/tmp/key'},
                       {'helperProtocol':2}, {'supervisorProtocol':2}, {'configFormat':2},
                       {'stateFormat':2}, {'workerProtocol':True}):
            with self.subTest(change=change), self.assertRaises(s.SupervisorError):
                self.u.install_bundle(*self.fixture.bundle(changes=change))
        raw, _, archive = self.fixture.bundle2
        with self.assertRaises(s.SupervisorError):
            self.u.install_bundle(raw[:-1] + b',"format":1}', None, archive)
        self.assertEqual(self.fixture.calls, [])

    def test_zip_path_link_duplicate_and_extra_file_are_refused_before_execution(self):
        cases = [[('../updater.py', b'pass', stat.S_IFREG | 0o644)],
                 [('/updater.py', b'pass', stat.S_IFREG | 0o644)],
                 [('updater.py', b'pass', stat.S_IFLNK | 0o777)],
                 [('updater.py', b'pass', stat.S_IFREG | 0o666)],
                 [('updater.py', b'pass', stat.S_IFREG | 0o4644)],
                 [('updater.py', b'pass', stat.S_IFREG | 0o644), ('hook.sh', b'pass', stat.S_IFREG | 0o644)],
                 [('updater.py', b'pass', stat.S_IFREG | 0o644)] * 2]
        for entries in cases:
            with self.subTest(names=[item[0] for item in entries]), self.assertRaises(s.SupervisorError):
                self.u.install_bundle(*self.fixture.bundle(entries=entries))
        self.assertEqual(self.fixture.calls, [])

    def test_monotonic_versions_and_same_version_identity(self):
        self.u.install_bundle(*self.fixture.bundle2)
        self.assertEqual(self.u.install_bundle(*self.fixture.bundle2), self.u.journal()['active'])
        for bundle in [self.fixture.bundle1, self.fixture.bundle('1.1.0', source=b'raise SystemExit(77)\n')]:
            with self.assertRaises(s.SupervisorError): self.u.install_bundle(*bundle)
        self.fixture.stable('1.1.0')

    def test_future_worker_protocol_can_advance_inside_existing_boundary(self):
        self.u.install_bundle(*self.fixture.bundle(changes={'workerProtocol':4, 'appManifestFormats':[1, 2, 3]}))
        self.fixture.stable('1.1.0')

    def test_unstartable_runtime_restores_old_without_application_work(self):
        self.u.runner = s.fixed_process
        with self.assertRaises(s.SupervisorError):
            self.u.install_bundle(*self.fixture.bundle(source=b'raise SystemExit(77)\n'))
        self.fixture.stable('1.0.0')
        self.u.dispatch('recover')

    def test_accepted_runtime_files_are_rechecked_offline(self):
        self.u.install_bundle(*self.fixture.bundle2)
        directory = self.u.versions / 'v1.1.0'
        path = directory / 'updater.py'
        path.write_bytes(path.read_bytes() + b'# changed after acceptance\n')
        before = len(self.fixture.calls)
        with self.assertRaises(s.SupervisorError): self.u.dispatch('run')
        self.assertEqual(len(self.fixture.calls), before)

    def test_manifest_mutation_and_signature_file_mixing_are_refused_offline(self):
        directory = self.u.versions / 'v1.0.0'
        manifest = directory / 'manifest.json'
        original = manifest.read_bytes()
        # Even metadata changes which retain the archive/version must not be
        # reinterpreted as the exact manifest accepted from GitHub over HTTPS.
        write(manifest, json_bytes(dict(json.loads(original), workerProtocol=4)))
        with self.assertRaises(s.SupervisorError): self.u.dispatch('run')
        write(manifest, original)
        write(directory / 'manifest.sig', bytes(64))
        with self.assertRaises(s.SupervisorError): self.u.dispatch('run')
        self.assertEqual(self.fixture.calls, [])

    def test_read_only_probe_mutation_blocks_install_and_recovery(self):
        def mutate(args, **kwargs):
            write(self.u.state / 'status.json', b'{"phase":"healthy","unexpectedMutation":true}')
            return self.fixture.runner(args, **kwargs)
        self.u.runner = mutate
        with self.assertRaises(s.SupervisorError): self.u.install_bundle(*self.fixture.bundle2)
        self.assertEqual(self.u.journal()['phase'], 'recovery_required')
        self.assertEqual(self.u.pointer(), 'versions/v1.0.0')

    def test_committed_worker_is_not_rolled_back_when_app_database_work_fails(self):
        self.u.install_bundle(*self.fixture.bundle2)
        active = self.u.journal()['active']
        write(self.u.state / 'status.json', json_bytes(dict(phase='recovery_required', runtimeDigest=active['sha256'],
              previousDatabaseSchema=1, targetDatabaseSchema=2)))
        def app_failure(args, **kwargs):
            if args[4] != 'self-check': raise s.SupervisorError('runtime')
            return self.fixture.runner(args, **kwargs)
        self.u.runner = app_failure
        with self.assertRaises(s.SupervisorError): self.u.dispatch('recover')
        self.assertEqual(self.u.journal()['active'], active)
        self.assertEqual(self.u.pointer(), 'versions/v1.1.0')

    def test_https_crash_boundaries_recover_complete_old_or_new_runtime(self):
        class Crash(BaseException): pass
        def attempt(fail_at=None):
            fixture = HttpsRuntimeFixture(self)
            u = fixture.supervisor
            count = 0
            def wrap(fn):
                def call(*args, **kwargs):
                    nonlocal count
                    result = fn(*args, **kwargs)
                    count += 1
                    if count == fail_at: raise Crash()
                    return result
                return call
            original = s.os.fsync, s.os.replace, s.os.rename, u.probe
            with patch.object(s.os, 'fsync', wrap(original[0])), patch.object(s.os, 'replace', wrap(original[1])), \
                    patch.object(s.os, 'rename', wrap(original[2])), patch.object(u, 'probe', wrap(original[3])):
                try: u.install_bundle(*fixture.bundle2)
                except Crash: pass
            restarted = s.Supervisor(u.root, owner_uid=os.getuid(), runner=fixture.runner)
            fixture.supervisor = restarted
            restarted.recover()
            state = restarted.journal()
            self.assertIn(state['active']['version'], ('1.0.0', '1.1.0'))
            fixture.stable(state['active']['version'])
            self.assertTrue(all(call[0][4] == 'self-check' for call in fixture.calls))
            return count
        count = attempt()
        self.assertGreater(count, 15)
        for index in range(1, count + 1):
            with self.subTest(crash_boundary=index): attempt(index)


class GithubTransportTests(unittest.TestCase):
    def setUp(self):
        self.fixture = HttpsRuntimeFixture(self)
        self.u = self.fixture.supervisor
        self.config = dict(self.fixture.config, dataDirectory='/data',
                           appUser=pwd.getpwuid(os.getuid()).pw_name,
                           operator={'familyId':'family', 'userId':'parent'})
        write(self.u.config_path, json_bytes(self.config))
        (self.u.root / 'data').mkdir(mode=0o700)
        self.calls = []
        self.names = ['yachad-release.json', 'yachad-app.zip', 'yachad-updater.json', 'yachad-updater.zip']
        self.assets = [dict(name=name, url=f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{index}')
                       for index, name in enumerate(self.names, 1)]
        self.release = dict(id=41, tag_name='v0.6.0', draft=False, prerelease=False, assets=self.assets)
        app_manifest, app_archive = app_bundle()
        self.payloads = {1:app_manifest, 2:app_archive, 3:self.fixture.bundle2[0], 4:self.fixture.bundle2[2]}
        self.target = t.release_target(self.release, 'NehoraiHadad/yahad')
        write(self.u.state / 'status.json', json_bytes(dict(phase='available', releaseTrust='github-https',
              installTarget=self.target, currentVersion='0.5.0', latestVersion='0.6.0')))

    def fetch(self, url, maximum, *args):
        self.calls.append(url)
        if url.endswith('/latest'): return json_bytes(self.release)
        return self.payloads[int(url.rsplit('/', 1)[1])]

    def refresh(self, command='run', fetch=None):
        return t.refresh(self.u, command, fetch=fetch or self.fetch, now=lambda:1000)

    def queue(self, **changes):
        value = dict(id=str(uuid.uuid4()), action='install', familyId='family', userId='parent', createdAt=1000000,
                     target=self.target)
        value.update(changes)
        path = self.u.root / 'data/update-request.json'
        write(path, json_bytes(value))
        return path, value

    def test_healthy_same_target_checks_only_metadata(self):
        self.refresh()
        write(self.u.state / 'status.json', json_bytes(dict(phase='healthy', releaseTrust='github-https',
              installTarget=self.target, currentVersion='0.6.0', latestVersion='0.6.0')))
        self.calls.clear()
        result = t.refresh(self.u, fetch=self.fetch, now=lambda:23000)
        self.assertEqual(result['result'], 'unchanged')
        self.assertNotIn('selection', result)
        self.assertEqual(self.calls, ['https://api.github.com/repos/NehoraiHadad/yahad/releases/latest', self.assets[2]['url']])

    def test_changed_target_does_not_skip_app_preflight(self):
        self.refresh()
        write(self.u.state / 'status.json', json_bytes(dict(phase='healthy', releaseTrust='github-https',
              installTarget=self.target, currentVersion='0.6.0', latestVersion='0.6.0')))
        self.release['id'] += 1
        self.calls.clear()
        result = t.refresh(self.u, fetch=self.fetch, now=lambda:23000)
        self.assertIn('selection', result)
        self.assertIn(self.assets[1]['url'], self.calls)

    def test_changed_runtime_manifest_keeps_full_preflight(self):
        self.refresh()
        write(self.u.state / 'status.json', json_bytes(dict(phase='healthy', releaseTrust='github-https',
              installTarget=self.target, currentVersion='0.6.0', latestVersion='0.6.0')))
        self.payloads[3] += b' '
        self.calls.clear()
        result = t.refresh(self.u, fetch=self.fetch, now=lambda:23000)
        self.assertIn(self.assets[1]['url'], self.calls)
        self.assertEqual(result['error'], 'downgrade')
        self.assertNotIn('selection', result)

    def test_pending_request_during_metadata_shortcut_is_not_consumed(self):
        self.refresh()
        status = dict(phase='healthy', releaseTrust='github-https', installTarget=self.target,
                      currentVersion='0.6.0', latestVersion='0.6.0')
        write(self.u.state / 'status.json', json_bytes(status))
        self.calls.clear()
        def raced_fetch(url, *args):
            value = self.fetch(url, *args)
            if url == self.assets[2]['url']: self.queue(action='check')
            return value
        result = t.refresh(self.u, fetch=raced_fetch, now=lambda:23000)
        self.assertEqual(result['result'], 'deferred')
        self.assertNotIn('selection', result)
        self.assertNotIn(self.assets[1]['url'], self.calls)
        self.assertTrue((self.u.root / 'data/update-request.json').exists())

    def test_https_downloads_two_runtime_assets_and_returns_compact_same_release_selection(self):
        result = self.refresh()
        self.assertEqual(result['result'], 'updated')
        self.assertEqual(self.calls, ['https://api.github.com/repos/NehoraiHadad/yahad/releases/latest',
                                      self.assets[0]['url'], self.assets[1]['url'],
                                      self.assets[2]['url'], self.assets[3]['url']])
        selection = result['selection']
        self.assertEqual(selection, dict(format=1, repository='NehoraiHadad/yahad',
                         release=dict(id=41, tag_name='v0.6.0', draft=False, prerelease=False,
                                      assets=sorted(self.assets, key=lambda item:item['name'])),
                         appManifestSha256=hashlib.sha256(self.payloads[1]).hexdigest(),
                         runtimeSha256=self.u.journal()['active']['sha256']))
        self.assertLessEqual(len(json_bytes(selection)), 8192)
        self.assertNotIn('selection', json.loads((self.u.state / 'updater-transport.json').read_bytes()),
                         'selection is invocation-local, never a stale persisted queue')
        self.fixture.stable('1.1.0')

    def test_no_optional_runtime_keeps_current_but_retains_exact_application_selection(self):
        self.release['assets'] = self.assets[:2]
        result = self.refresh()
        self.assertEqual(result['result'], 'unavailable')
        self.assertEqual(result['selection']['release']['id'], 41)
        self.assertEqual(self.calls, ['https://api.github.com/repos/NehoraiHadad/yahad/releases/latest',
                                      self.assets[0]['url'], self.assets[1]['url']])
        self.fixture.stable('1.0.0')

    def test_partial_runtime_assets_fail_without_candidate_execution_or_selection(self):
        self.release['assets'] = self.assets[:3]
        result = self.refresh()
        self.assertEqual(result['result'], 'failed')
        self.assertEqual(result['error'], 'invalid_release')
        self.assertNotIn('selection', result)
        self.assertEqual(self.fixture.calls, [])
        self.fixture.stable('1.0.0')

    def test_notify_needs_fresh_designated_install_and_preserves_queue(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        self.assertEqual(self.refresh(), {'result':'notify'})
        for changes in ({'action':'check'}, {'userId':'child'}, {'familyId':'other'}, {'createdAt':0},
                        {'id':'invalid'}, {'url':'https://attacker.example/worker'}):
            path, value = self.queue(**changes)
            self.assertEqual(self.refresh('requests'), {'result':'skipped'})
            self.assertEqual(json.loads(path.read_bytes()), value)
        path, value = self.queue()
        result = self.refresh('requests')
        self.assertEqual(result['result'], 'updated')
        self.assertEqual(json.loads(path.read_bytes()), value)
        self.fixture.stable('1.1.0')

    def test_intent_change_while_downloading_prevents_runtime_activation(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        path, value = self.queue()
        def change_intent(url, *args):
            data = self.fetch(url, *args)
            if url == self.assets[3]['url']: write(path, json_bytes(dict(value, action='check')))
            return data
        result = self.refresh('requests', fetch=change_intent)
        self.assertEqual(result['result'], 'deferred')
        self.assertNotIn('selection', result)
        self.fixture.stable('1.0.0')

    def test_tampered_runtime_payload_never_executes_and_no_signature_fallback(self):
        self.payloads[4] += b'tampered'
        result = self.refresh()
        self.assertEqual(result['result'], 'failed')
        self.assertNotIn('selection', result)
        self.assertEqual(self.fixture.calls, [])
        self.fixture.stable('1.0.0')

    def test_source_repository_or_asset_substitution_is_rejected(self):
        raw = json_bytes(self.release)
        for repo in ('other/repository', 'NehoraiHadad/yahad/extra', 'NehoraiHadadad/yahad'):
            with self.subTest(repo=repo), self.assertRaises(t.TransportError):
                t.release_selection(raw, repo)
        for url in ('http://api.github.com/repos/NehoraiHadad/yahad/releases/assets/1',
                    'https://api.github.com/repos/other/repo/releases/assets/1',
                    'https://evil.example/asset',
                    self.assets[0]['url'] + '?redirect=evil',
                    self.assets[0]['url'] + '#fragment'):
            invalid = dict(self.release, assets=[dict(self.assets[0], url=url), *self.assets[1:]])
            with self.subTest(url=url), self.assertRaises(t.TransportError):
                t.release_selection(json_bytes(invalid), 'NehoraiHadad/yahad')

    def test_release_selection_requires_stable_identity_unique_assets_and_integer_id(self):
        for changes in ({'id':True}, {'id':0}, {'id':2**63}, {'id':'41'}, {'draft':True},
                        {'prerelease':True}, {'tag_name':'v01.2.3'}, {'tag_name':'updater-1.0.0'},
                        {'assets':self.assets[1:]}, {'assets':self.assets + self.assets[:1]}):
            with self.subTest(changes=changes), self.assertRaises(t.TransportError):
                t.release_selection(json_bytes(dict(self.release, **changes)), 'NehoraiHadad/yahad')

    def test_https_transport_and_recovery_do_not_relax_tls_hosts(self):
        for url in ('http://github.com/x', 'https://evil.example/x', 'https://secret@api.github.com/x',
                    'https://github.com:8443/x', 'https://api.github.com/x#hidden'):
            with self.assertRaises(t.TransportError): t.safe_url(url)
        for command in ('recover', 'boot-recover'):
            self.assertEqual(self.refresh(command), {'result':'skipped'})
        self.assertEqual(self.calls, [])

    def test_notify_missing_or_mismatched_displayed_target_never_fetches_or_executes(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        for target in (None, {}, dict(self.target, fingerprint='b'*64), dict(self.target, version='0.7.0')):
            path, value = self.queue(target=target)
            self.assertEqual(self.refresh('requests'), {'result':'skipped'})
            self.assertEqual(json.loads(path.read_bytes()), value)
        path, value = self.queue()
        del value['target']
        write(path, json_bytes(value))
        self.assertEqual(self.refresh('requests'), {'result':'skipped'})
        self.assertEqual(self.calls, [])
        self.assertEqual(self.fixture.calls, [])
        self.fixture.stable('1.0.0')

    def test_notify_target_requires_available_root_status_before_network(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        path, value = self.queue()
        for status in (dict(phase='healthy', installTarget=self.target), dict(phase='available'),
                       dict(phase='available', installTarget=dict(self.target, fingerprint='b'*64))):
            write(self.u.state / 'status.json', json_bytes(status))
            self.assertEqual(self.refresh('requests'), {'result':'skipped'})
        self.assertEqual(self.calls, [])
        self.assertEqual(self.fixture.calls, [])
        self.assertEqual(json.loads(path.read_bytes()), value)

    def test_changed_latest_or_app_or_updater_assets_refuse_before_root_candidate_execution(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        path, value = self.queue()
        original = self.release
        changes = [dict(original, id=42), dict(original, tag_name='v0.7.0')]
        for index in (0, 1, 2, 3):
            assets = [dict(item) for item in self.assets]
            assets[index]['url'] = f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{100+index}'
            changes.append(dict(original, assets=assets))
        for release in changes:
            with self.subTest(release_id=release['id'], tag=release['tag_name'], assets=release['assets']):
                self.calls.clear()
                self.release = release
                result = self.refresh('requests')
                self.assertEqual(result['result'], 'failed')
                self.assertEqual(result['error'], 'target_changed')
                self.assertNotIn('selection', result)
                self.assertEqual(self.calls, ['https://api.github.com/repos/NehoraiHadad/yahad/releases/latest'])
                self.assertEqual(self.fixture.calls, [], 'stale displayed intent must not execute privileged candidate probes')
                self.assertEqual(json.loads(path.read_bytes()), value)
                self.fixture.stable('1.0.0')
        self.release = original

    def test_root_displayed_target_change_during_download_prevents_root_execution(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        path, value = self.queue()
        def change_status(url, *args):
            data = self.fetch(url, *args)
            if url == self.assets[3]['url']:
                write(self.u.state / 'status.json', json_bytes(dict(phase='available',
                      installTarget=dict(self.target, fingerprint='b'*64))))
            return data
        result = self.refresh('requests', fetch=change_status)
        self.assertEqual(result['result'], 'deferred')
        self.assertNotIn('selection', result)
        self.assertEqual(self.fixture.calls, [])
        self.assertEqual(json.loads(path.read_bytes()), value)
        self.fixture.stable('1.0.0')

    def test_updater_signature_only_is_partial_not_absent(self):
        self.release['assets'] = self.assets[:2] + [dict(name='yachad-updater.sig',
            url='https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/5')]
        result = self.refresh()
        self.assertEqual(result['result'], 'failed')
        self.assertEqual(result['error'], 'invalid_release')
        self.assertNotIn('selection', result)
        self.assertEqual(self.fixture.calls, [])
        self.fixture.stable('1.0.0')

    def test_worker_transport_target_fingerprints_match_and_bind_all_known_assets(self):
        full = dict(self.release, assets=self.assets + [
            dict(name='yachad-release.sig', url='https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/5'),
            dict(name='yachad-updater.sig', url='https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/6')])
        expected = t.release_target(full, 'NehoraiHadad/yahad')
        self.assertEqual(expected, w.release_target(full, 'NehoraiHadad/yahad'))
        shuffled = dict(full, assets=list(reversed(full['assets'])))
        self.assertEqual(expected, t.release_target(shuffled, 'NehoraiHadad/yahad'))
        self.assertEqual(expected, w.release_target(shuffled, 'NehoraiHadad/yahad'))
        extra = dict(full, assets=full['assets'] + [dict(name='README.txt', url='ignored-non-code-url')])
        self.assertEqual(expected, t.release_target(extra, 'NehoraiHadad/yahad'))
        self.assertEqual(expected, w.release_target(extra, 'NehoraiHadad/yahad'))
        selected = t.release_selection(json_bytes(full), 'NehoraiHadad/yahad')
        self.assertEqual(len(selected['release']['assets']), 6)
        self.assertEqual(expected, w.release_target(selected['release'], selected['repository']))
        for index in range(6):
            changed = [dict(item) for item in full['assets']]
            changed[index]['url'] = f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{100+index}'
            value = dict(full, assets=changed)
            self.assertNotEqual(expected, t.release_target(value, 'NehoraiHadad/yahad'))
            self.assertEqual(t.release_target(value, 'NehoraiHadad/yahad'), w.release_target(value, 'NehoraiHadad/yahad'))
        for changes in ({'id':True}, {'id':0}, {'tag_name':'v01.2.3'}, {'draft':True},
                        {'assets':full['assets'] + full['assets'][:1]}):
            value = dict(full, **changes)
            self.assertIsNone(t.release_target(value, 'NehoraiHadad/yahad'))
            self.assertIsNone(w.release_target(value, 'NehoraiHadad/yahad'))

    def test_auto_mode_check_uses_existing_auto_authority_without_displayed_target(self):
        path, value = self.queue(action='check')
        del value['target']
        write(path, json_bytes(value))
        write(self.u.state / 'status.json', json_bytes(dict(phase='healthy', releaseTrust='github-https')))
        result = self.refresh('requests')
        self.assertEqual(result['result'], 'updated')
        self.assertIn('selection', result)
        self.assertEqual(json.loads(path.read_bytes()), value)
        self.fixture.stable('1.1.0')

    def test_auto_check_changed_to_notify_during_fetch_never_executes_candidate(self):
        path, value = self.queue(action='check')
        del value['target']
        write(path, json_bytes(value))
        def change_mode(url, *args):
            data = self.fetch(url, *args)
            if url == self.assets[3]['url']:
                self.config['mode'] = 'notify'
                write(self.u.config_path, json_bytes(self.config))
            return data
        result = self.refresh('requests', fetch=change_mode)
        self.assertEqual(result['result'], 'deferred')
        self.assertNotIn('selection', result)
        self.assertEqual(self.fixture.calls, [])
        self.assertEqual(json.loads(path.read_bytes()), value)
        self.fixture.stable('1.0.0')

    def test_auto_check_still_requires_fresh_designated_operator_intent(self):
        for changes in ({'userId':'child'}, {'familyId':'other'}, {'createdAt':0}, {'id':'not-a-uuid'}):
            path, value = self.queue(action='check', **changes)
            del value['target']
            write(path, json_bytes(value))
            self.assertEqual(self.refresh('requests'), {'result':'skipped'})
        self.assertEqual(self.calls, [])
        self.assertEqual(self.fixture.calls, [])

    def test_app_preflight_verifies_bounded_bytes_before_any_root_candidate_execution(self):
        bounds = []
        def bounded(url, maximum, *args):
            self.assertEqual(self.fixture.calls, [], 'all selected app/root downloads precede privileged probes')
            bounds.append(maximum)
            return self.fetch(url, maximum, *args)
        result = self.refresh(fetch=bounded)
        self.assertEqual(result['result'], 'updated')
        self.assertEqual(bounds, [1024*1024, 8192, 64*1024*1024, 8192, 2*1024*1024])
        self.assertEqual(result['selection']['appManifestSha256'], hashlib.sha256(self.payloads[1]).hexdigest())

    def test_bad_app_tag_hash_size_or_json_never_executes_root_candidate(self):
        original = self.payloads[1]
        manifest = json.loads(original)
        invalid = [json_bytes(dict(manifest, version='0.7.0')), json_bytes(dict(manifest, sha256='b'*64)),
                   json_bytes(dict(manifest, bytes=True)), json_bytes(dict(manifest, bytes=1)),
                   json_bytes(dict(manifest, bytes=64*1024*1024+1)),
                   json_bytes(dict(manifest, archive='other.zip')), b'{bad-json',
                   original[:-1] + b',"version":"0.6.0"}']
        for raw in invalid:
            self.payloads[1] = raw
            self.calls.clear()
            (self.u.state / 'updater-transport.json').unlink(missing_ok=True)
            with self.subTest(raw=raw[:60]):
                result = self.refresh()
                self.assertEqual(result['result'], 'failed')
                self.assertEqual(result['error'], 'invalid_release')
                self.assertNotIn('selection', result)
                self.assertEqual(self.fixture.calls, [])
                self.assertNotIn(self.assets[2]['url'], self.calls)
                self.assertNotIn(self.assets[3]['url'], self.calls)
                self.fixture.stable('1.0.0')
        self.payloads[1] = original

    def test_app_archive_or_manifest_download_failure_never_executes_root_candidate(self):
        for failed_url in (self.assets[0]['url'], self.assets[1]['url']):
            (self.u.state / 'updater-transport.json').unlink(missing_ok=True)
            self.calls.clear()
            def unavailable(url, maximum, *args):
                if url == failed_url: raise t.TransportError('network')
                return self.fetch(url, maximum, *args)
            result = self.refresh(fetch=unavailable)
            self.assertEqual(result, dict(result='failed', error='network', version=None,
                format=1, checkedAt=1000000, attempt=result['attempt']))
            self.assertEqual(self.fixture.calls, [])
            self.fixture.stable('1.0.0')

    def test_tampered_app_archive_never_executes_root_candidate(self):
        self.payloads[2] += b'tampered-app-payload'
        result = self.refresh()
        self.assertEqual(result['result'], 'failed')
        self.assertEqual(result['error'], 'invalid_release')
        self.assertNotIn('selection', result)
        self.assertEqual(self.fixture.calls, [])
        self.fixture.stable('1.0.0')

    def test_timer_run_routes_a_pending_notify_install_through_bound_selection(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        path, value = self.queue()
        result = self.refresh('run')
        self.assertEqual(result['result'], 'updated')
        self.assertEqual(result['selection']['release']['id'], 41)
        self.assertEqual(json.loads(path.read_bytes()), value)
        self.fixture.stable('1.1.0')

    def test_https_publication_hardlinks_two_and_three_are_bounded_and_supported(self):
        self.config['mode'] = 'notify'
        write(self.u.config_path, json_bytes(self.config))
        path, value = self.queue()
        aliases = []
        for links in (2, 3):
            alias = path.with_name('publication-' + str(links))
            os.link(path, alias); aliases.append(alias)
            self.assertEqual(path.stat().st_nlink, links)
            result = self.refresh('requests')
            self.assertIn(result['result'], ('updated','unchanged'))
            self.assertEqual(json.loads(path.read_bytes()), value)
        alias = path.with_name('unexpected-fourth-link')
        os.link(path, alias)
        before_calls = len(self.calls)
        self.assertEqual(self.refresh('requests'), {'result':'skipped'})
        self.assertEqual(len(self.calls), before_calls)

    def test_signed_profile_keeps_single_link_only_request_contract(self):
        legacy = module('signed_link_boundary_fixtures', ROOT / 'tests/test_updater_transport.py')
        fixture = legacy.TransportTests('test_notify_explicit_valid_install_can_upgrade_root_then_keeps_app_queue_intact')
        fixture.setUp()
        self.addCleanup(fixture.doCleanups)
        path, value = fixture.queue_install()
        os.link(path, path.with_name('publication-link'))
        self.assertEqual(fixture.refresh(command='requests'), {'result':'skipped'})
        self.assertEqual(fixture.calls, [])
        self.assertEqual(fixture.installed, [])

    def test_selection_cannot_dispatch_after_another_runtime_commits(self):
        result = self.refresh()
        self.assertEqual(result['result'], 'updated')
        self.u.install_bundle(*self.fixture.bundle('1.2.0', source=self.fixture.source + b'# different committed runtime\n'))
        before = len(self.fixture.calls)
        with self.assertRaises(s.SupervisorError): self.u.dispatch('run', selection=result['selection'])
        self.assertEqual(len(self.fixture.calls), before, 'stale selection cannot execute an app worker')
        self.fixture.stable('1.2.0')

    def test_target_helper_parity_supports_configured_signed_repo_without_enabling_https_source_change(self):
        repository = 'ExampleOwner/signed-family'
        release = dict(self.release, assets=[dict(item, url=item['url'].replace('NehoraiHadad/yahad', repository))
                                            for item in self.assets])
        target = t.release_target(release, repository)
        self.assertIsNotNone(target)
        self.assertEqual(target, w.release_target(release, repository))
        with self.assertRaises(t.TransportError): t.release_selection(json_bytes(release), repository)


class SealedReleaseSelectionTests(unittest.TestCase):
    def setUp(self):
        self.fixture = HttpsRuntimeFixture(self)
        self.u = self.fixture.supervisor
        self.config = dict(repository='NehoraiHadad/yahad', releaseTrust='github-https')
        self.value = dict(format=1, repository='NehoraiHadad/yahad', release=dict(
            id=41, tag_name='v0.6.0', draft=False, prerelease=False,
            assets=[dict(name=name, url=f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{index}')
                    for index, name in enumerate(('yachad-release.json','yachad-app.zip'), 1)]))

    def read(self, fd, *, owner=0, config=None):
        original = w.os.fstat
        def owned(descriptor):
            info = original(descriptor)
            if descriptor == fd:
                values = list(info); values[4] = owner
                return os.stat_result(values)
            return info
        with patch.object(w.os, 'fstat', side_effect=owned):
            return w.read_release_selection(fd, self.config if config is None else config)

    def fd(self, raw, sealed=True):
        fd = os.memfd_create('synthetic-yachad-release', os.MFD_CLOEXEC | os.MFD_ALLOW_SEALING)
        self.addCleanup(os.close, fd)
        os.write(fd, raw)
        os.lseek(fd, 0, os.SEEK_SET)
        if sealed: fcntl.fcntl(fd, s.F_ADD_SEALS, s.SELECTION_SEALS)
        return fd

    def test_real_kernel_seals_prevent_rewriting_selected_release(self):
        with self.u.selected_release(self.value) as fd:
            self.assertEqual(self.read(fd), self.value)
            self.assertEqual(fcntl.fcntl(fd, s.F_GET_SEALS) & s.SELECTION_SEALS, s.SELECTION_SEALS)
            with self.assertRaises(OSError): os.pwrite(fd, b'x', 0)
            with self.assertRaises(OSError): os.ftruncate(fd, 0)
            self.assertEqual(self.read(fd), self.value)

    def test_selection_preserves_all_six_known_asset_identities_without_new_scope(self):
        names = ('yachad-release.json','yachad-release.sig','yachad-app.zip',
                 'yachad-updater.json','yachad-updater.sig','yachad-updater.zip')
        value = dict(self.value, release=dict(self.value['release'], assets=[
            dict(name=name, url=f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{index}')
            for index, name in enumerate(names, 1)]))
        fd = self.fd(json_bytes(value))
        self.assertEqual(self.read(fd), value)
        extra = dict(value, release=dict(value['release'], assets=value['release']['assets'] + [
            dict(name='hook.py', url='https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/7')]))
        with self.assertRaises(w.UpdateError): self.read(self.fd(json_bytes(extra)))

    def test_unsealed_partial_seal_and_nonroot_descriptor_are_refused(self):
        raw = json_bytes(self.value)
        fd = self.fd(raw, sealed=False)
        with self.assertRaises(w.UpdateError): self.read(fd)
        fcntl.fcntl(fd, s.F_ADD_SEALS, 0x0001)
        with self.assertRaises(w.UpdateError): self.read(fd)
        fd = self.fd(raw)
        with self.assertRaises(w.UpdateError): self.read(fd, owner=12345)

    def test_selection_cannot_choose_another_trust_repository_path_or_command(self):
        invalid = [dict(self.value, repository='other/repository'), dict(self.value, format=True),
                   dict(self.value, path='/tmp/evil'), dict(self.value, command='sh'),
                   dict(self.value, url='https://github.com/evil')]
        for selected in invalid:
            with self.subTest(selected=selected), self.assertRaises(w.UpdateError):
                self.read(self.fd(json_bytes(selected)))
        fd = self.fd(json_bytes(self.value))
        for config in (dict(self.config, repository='other/repository'), dict(self.config, releaseTrust='signature')):
            with self.assertRaises(w.UpdateError): self.read(fd, config=config)

    def test_selection_rejects_nonstable_identity_duplicate_and_foreign_assets(self):
        release = self.value['release']
        invalid = [dict(release, id=True), dict(release, id=0), dict(release, draft=True),
                   dict(release, prerelease=True), dict(release, tag_name='branch'),
                   dict(release, assets=release['assets'] * 2),
                   dict(release, assets=[release['assets'][0], release['assets'][0]]),
                   dict(release, assets=[dict(release['assets'][0], url='https://api.github.com/repos/other/repo/releases/assets/1'), release['assets'][1]]),
                   dict(release, assets=[dict(release['assets'][0], command='sh'), release['assets'][1]])]
        for value in invalid:
            with self.subTest(release=value), self.assertRaises(w.UpdateError):
                self.read(self.fd(json_bytes(dict(self.value, release=value))))

    def test_duplicate_json_trailing_bytes_and_oversize_are_refused(self):
        raw = json_bytes(self.value)
        for content in (raw[:-1] + b',"format":1}', raw + b'x', b' ' * 8193, b''):
            with self.subTest(size=len(content)), self.assertRaises(w.UpdateError):
                self.read(self.fd(content))

    def test_only_supervised_run_or_requests_can_carry_a_distinct_selection_descriptor(self):
        flags = ['--supervisor-lock-fd','4','--runtime-digest','a'*64,'--release-selection-fd','5']
        for command in ('run','requests'):
            self.assertEqual(w.parse_command([command,*flags]), (command,4,'a'*64,5))
        for command in ('recover','boot-recover','self-check'):
            with self.assertRaises(w.UpdateError): w.parse_command([command,*flags])
        for flags in (['--release-selection-fd','5'],
                      ['--supervisor-lock-fd','4','--runtime-digest','a'*64,'--release-selection-fd','4'],
                      ['--supervisor-lock-fd','4','--runtime-digest','a'*64,'--release-selection-file','/tmp/file']):
            with self.assertRaises(w.UpdateError): w.parse_command(['run',*flags])


if __name__ == '__main__':
    unittest.main()
