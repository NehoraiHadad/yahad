"""Joined HTTPS transport, supervisor, sealed selection and synthetic SQLite child.

The child is a deliberately tiny app-work fixture, not a real service/provider.
Real application migration correctness remains covered by schema/account tests.
"""
import importlib.util
import hashlib
import io
import json
import os
import pathlib
import pwd
import sqlite3
import stat
import unittest
import uuid
import zipfile
from unittest.mock import patch


ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('joined_https_fixtures', ROOT / 'tests/test_github_runtime.py')
f = importlib.util.module_from_spec(spec)
spec.loader.exec_module(f)
s, t = f.s, f.t


class GithubRuntimeIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = f.HttpsRuntimeFixture(self, runner=s.fixed_process)
        self.u = self.fixture.supervisor
        self.root = self.u.root
        self.config = dict(self.fixture.config, mode='notify', dataDirectory='/data',
                           appUser=pwd.getpwuid(os.getuid()).pw_name,
                           operator={'familyId':'family', 'userId':'parent'})
        f.write(self.u.config_path, f.json_bytes(self.config))
        self.request = dict(id=str(uuid.uuid4()), action='install', familyId='family', userId='parent', createdAt=1000000)
        self.queue = self.root / 'data/update-request.json'
        f.write(self.queue, f.json_bytes(self.request))
        self.database = self.root / 'data/app.sqlite'
        with sqlite3.connect(self.database) as db:
            db.executescript('CREATE TABLE schema_version(version INTEGER NOT NULL); '
                             'INSERT INTO schema_version VALUES(2); '
                             'CREATE TABLE preserved(key TEXT PRIMARY KEY,value BLOB); '
                             "INSERT INTO preserved VALUES('auth','synthetic-encrypted-auth'),"
                             "('message','synthetic-encrypted-message');")
        f.write(self.root / 'data/app-version', b'0.5.0')
        source = self.fixture.source + self.child_source()
        self.bundle = self.fixture.bundle('1.1.0', source=source)
        self.app_bundle = f.app_bundle()
        names = ['yachad-release.json', 'yachad-app.zip', 'yachad-updater.json', 'yachad-updater.zip']
        self.assets = [dict(name=name, url=f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{index}')
                       for index, name in enumerate(names, 1)]
        self.release = dict(id=41, tag_name='v0.6.0', draft=False, prerelease=False, assets=self.assets)
        self.request['target'] = t.release_target(self.release, 'NehoraiHadad/yahad')
        f.write(self.queue, f.json_bytes(self.request))
        f.write(self.u.state / 'status.json', f.json_bytes(dict(phase='available', releaseTrust='github-https',
                installTarget=self.request['target'], currentVersion='0.5.0', latestVersion='0.6.0')))
        self.calls = []

    def child_source(self):
        # No shell/network/build execution. This real child checks the inherited
        # lock and sealed selected-release descriptor before a SQLite transaction.
        return ("\nimport errno,sqlite3\nfrom pathlib import Path\n"
                "root=Path(" + repr(str(self.root)) + ")\n"
                "if sys.argv[1] in ('run','requests'):\n"
                " selection_fd=int(sys.argv[sys.argv.index('--release-selection-fd')+1])\n"
                " required=fcntl.F_SEAL_WRITE|fcntl.F_SEAL_GROW|fcntl.F_SEAL_SHRINK|fcntl.F_SEAL_SEAL\n"
                " assert fcntl.fcntl(selection_fd,fcntl.F_GET_SEALS)&required==required\n"
                " selected=json.loads(os.pread(selection_fd,8193,0))\n"
                " assert selected['repository']=='NehoraiHadad/yahad'\n"
                " try: os.write(selection_fd,b'x')\n"
                " except OSError as e: assert e.errno in (errno.EPERM,errno.EBADF)\n"
                " else: raise RuntimeError('release selection is writable')\n"
                " journal=json.loads((root/'var/lib/yachad-updates/updater-runtime.json').read_bytes())\n"
                " assert journal['phase']=='stable' and journal['active']['version']=='1.1.0'\n"
                " digest=sys.argv[sys.argv.index('--runtime-digest')+1]\n"
                " assert journal['active']['sha256']==digest\n"
                " separate=os.open(root/'var/lib/yachad-updates/worker.lock',os.O_RDONLY)\n"
                " try:\n"
                "  try: fcntl.flock(separate,fcntl.LOCK_EX|fcntl.LOCK_NB)\n"
                "  except BlockingIOError: pass\n"
                "  else: raise RuntimeError('worker lock was not inherited')\n"
                " finally: os.close(separate)\n"
                " request=json.loads((root/'data/update-request.json').read_bytes())\n"
                " assert request['action']=='install'\n"
                " events=['runtime_committed','selection_checked']\n"
                " database=root/'data/app.sqlite'\n"
                " with sqlite3.connect(database) as db:\n"
                "  before=db.execute('SELECT version FROM schema_version').fetchone()[0]\n"
                "  assert before==2\n"
                "  with sqlite3.connect(root/'data/synthetic-backup.sqlite') as backup: db.backup(backup)\n"
                "  events.append('backup')\n"
                "  status=dict(phase='activating',runtimeDigest=digest,previousDatabaseSchema=2,targetDatabaseSchema=3)\n"
                "  status_path=root/'var/lib/yachad-updates/status.json'\n"
                "  status_path.write_text(json.dumps(status))\n"
                "  if (root/'data/fail-before-migration').exists():\n"
                "   status['phase']='rolled_back'; status_path.write_text(json.dumps(status)); raise SystemExit(31)\n"
                "  db.execute('UPDATE schema_version SET version=3'); db.commit()\n"
                "  events.append('migration')\n"
                " (root/'data/app-version').write_text(selected['release']['tag_name'][1:])\n"
                " if (root/'data/fail-after-migration').exists():\n"
                "  status['phase']='recovery_required'; status_path.write_text(json.dumps(status)); raise SystemExit(32)\n"
                " status['phase']='healthy'; status_path.write_text(json.dumps(status))\n"
                " events.append('ready')\n"
                " (root/'data/application-install.json').write_text(json.dumps(dict(id=request['id'],runtimeVersion=journal['active']['version'],releaseId=selected['release']['id'],version=selected['release']['tag_name'],events=events)))\n"
                " (root/'data/update-request.json').unlink()\n"
                "elif sys.argv[1] in ('recover','boot-recover'):\n"
                " assert '--release-selection-fd' not in sys.argv\n"
                " status=json.loads((root/'var/lib/yachad-updates/status.json').read_bytes())\n"
                " if status['phase']=='recovery_required': raise SystemExit(33)\n").encode()

    def fetch(self, url, *args):
        self.calls.append(url)
        if url.endswith('/latest'): return f.json_bytes(self.release)
        index = int(url.rsplit('/', 1)[1])
        return {1:self.app_bundle[0], 2:self.app_bundle[1], 3:self.bundle[0], 4:self.bundle[2]}[index]

    def acquire(self):
        result = t.refresh(self.u, 'requests', fetch=self.fetch, now=lambda:1000)
        self.assertEqual(result['result'], 'updated')
        self.fixture.stable('1.1.0')
        self.assertEqual(json.loads(self.queue.read_bytes()), self.request)
        return result['selection']

    def assert_preserved(self):
        with sqlite3.connect(self.database) as db:
            self.assertEqual(db.execute('SELECT key,value FROM preserved ORDER BY key').fetchall(),
                             [('auth','synthetic-encrypted-auth'), ('message','synthetic-encrypted-message')])
            return db.execute('SELECT version FROM schema_version').fetchone()[0]

    def test_updater_commit_precedes_selected_app_and_sqlite_migration(self):
        old_config = self.u.config_path.read_bytes()
        selection = self.acquire()
        self.assertEqual(self.assert_preserved(), 2, 'transport must not migrate app data')
        self.u.dispatch('requests', selection=selection)
        receipt = json.loads((self.root / 'data/application-install.json').read_bytes())
        self.assertEqual(receipt, dict(id=self.request['id'], runtimeVersion='1.1.0', releaseId=41, version='v0.6.0',
                                      events=['runtime_committed','selection_checked','backup','migration','ready']))
        self.assertEqual(self.assert_preserved(), 3)
        self.assertFalse(self.queue.exists())
        self.assertEqual(self.u.config_path.read_bytes(), old_config)
        with sqlite3.connect(self.root / 'data/synthetic-backup.sqlite') as db:
            self.assertEqual(db.execute('SELECT version FROM schema_version').fetchone()[0], 2)

    def test_changed_latest_cannot_mix_the_new_worker_with_another_app_release(self):
        selection = self.acquire()
        self.release = dict(self.release, id=42, tag_name='v0.7.0')
        calls = list(self.calls)
        self.u.dispatch('requests', selection=selection)
        receipt = json.loads((self.root / 'data/application-install.json').read_bytes())
        self.assertEqual((receipt['releaseId'], receipt['version']), (41, 'v0.6.0'))
        self.assertEqual(self.calls, calls, 'the committed worker consumes the original selection, not latest again')

    def test_pre_migration_failure_keeps_previous_schema_but_committed_new_worker(self):
        f.write(self.root / 'data/fail-before-migration', b'synthetic')
        selection = self.acquire()
        with self.assertRaises(s.SupervisorError): self.u.dispatch('requests', selection=selection)
        self.assertEqual(self.assert_preserved(), 2)

        self.assertEqual((self.root / 'data/app-version').read_text(), '0.5.0')
        self.fixture.stable('1.1.0')
        self.assertEqual(json.loads((self.u.state / 'status.json').read_bytes())['phase'], 'rolled_back')

    def test_post_migration_failure_keeps_new_schema_and_worker_and_blocks_old_app(self):
        f.write(self.root / 'data/fail-after-migration', b'synthetic')
        selection = self.acquire()
        with self.assertRaises(s.SupervisorError): self.u.dispatch('requests', selection=selection)
        self.assertEqual(self.assert_preserved(), 3)
        self.assertEqual((self.root / 'data/app-version').read_text(), '0.6.0')
        self.fixture.stable('1.1.0')
        status = json.loads((self.u.state / 'status.json').read_bytes())
        self.assertEqual(status['phase'], 'recovery_required')
        self.assertEqual(status['runtimeDigest'], self.u.journal()['active']['sha256'])
        with self.assertRaises(s.SupervisorError): self.u.dispatch('boot-recover')
        self.assertEqual(self.assert_preserved(), 3)
        self.fixture.stable('1.1.0')

    def test_recovery_is_offline_and_does_not_accept_release_selection(self):
        selection = self.acquire()
        for command in ('recover', 'boot-recover'):
            self.assertEqual(t.refresh(self.u, command, fetch=lambda *_: self.fail('recovery attempted network')), {'result':'skipped'})
            with self.assertRaises(s.SupervisorError): self.u.dispatch(command, selection=selection)
            self.u.dispatch(command)
        self.assertEqual(self.assert_preserved(), 2)


class ProductionWorkerJoinedTests(unittest.TestCase):
    """Real production Worker lifecycle; injected systemd/network/build effects.

Unlike the small subprocess contract fixture above, all application release
selection/install/schema/backup/rollback decisions use deploy/updater.py itself.
No root services or provider are invoked, and SQLite contains synthetic bytes.
"""
    def setUp(self):
        spec = importlib.util.spec_from_file_location('joined_production_schema_fixture', ROOT / 'tests/test_schema_upgrade.py')
        self.schema_module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.schema_module)
        self.w = self.schema_module.u
        self.runtime = f.HttpsRuntimeFixture(self)
        self.u = self.runtime.supervisor
        f.write(self.u.config_path, f.json_bytes(dict(self.runtime.config, dataDirectory='/data',
                appUser=pwd.getpwuid(os.getuid()).pw_name, operator={'familyId':'family','userId':'parent'})))
        self.schema = self.schema_module.SchemaLifecycleTests('test_schema1_to2_runs_unprivileged_smoke_and_backup_before_start')
        self.schema.setUp()
        self.addCleanup(self.schema.doCleanups)
        self.worker = self.schema.worker
        self.worker.status_path = self.u.state / 'status.json'
        self.worker.c['mode'] = 'auto'
        self.schema.original_schema = 2
        self.schema.target_schema = 3
        self.version = '0.6.0'
        self.schema.old.joinpath('package.json').write_text('{"version":"0.5.0"}')
        with sqlite3.connect(self.schema.path) as db:
            db.execute('UPDATE schema_version SET version=2')
        previous = dict(format=2, minimumUpdaterProtocol=2, version='0.5.0', databaseSchema=2,
                        databaseMigrationsFrom=[1])
        self.schema.old.joinpath(self.w.RELEASE_METADATA).write_text(json.dumps(previous))
        self.worker.status.update(phase='healthy', currentVersion='0.5.0')
        f.write(self.u.state / 'status.json', f.json_bytes(self.worker.status))
        original_save = self.worker.save
        def save(**changes):
            original_save(**changes)
            f.write(self.u.state / 'status.json', f.json_bytes(self.worker.status))
        self.worker.save = save
        def health(expected=None, expected_schema=None):
            self.schema.effects.append(('health', expected, expected_schema))
            if self.worker.schema() != expected_schema or self.schema.fail_health and expected == self.version:
                raise self.w.UpdateError('health')
        self.worker.health = health
        self.worker.release = lambda token: self.w.Worker.release(self.worker, token)
        self.calls = []
        self.application_calls = []
        source = self.runtime.source + b'# fixture candidate delegates to production worker runner\n'
        self.bundle = self.runtime.bundle('1.1.0', source=source)
        names = ['yachad-release.json', 'yachad-app.zip', 'yachad-updater.json', 'yachad-updater.zip']
        self.assets = [dict(name=name, url=f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{index}')
                       for index, name in enumerate(names, 1)]
        self.release = dict(id=41, tag_name='v0.6.0', draft=False, prerelease=False, assets=self.assets)
        self.u.runner = self.runner
        content = io.BytesIO()
        with zipfile.ZipFile(content, 'w') as archive:
            for name in ('package-lock.json', 'dist/index.html', 'src/main.ts', 'scripts/update-smoke.ts', 'scripts/update-backup.ts'):
                archive.writestr(name, 'synthetic fixture')
            archive.writestr('package.json', json.dumps(dict(version=self.version)))
        archive = content.getvalue()
        manifest = dict(format=2, minimumUpdaterProtocol=3, databaseMigrationsFrom=[2], version=self.version,
                        databaseSchema=3, archive='yachad-app.zip', bytes=len(archive), sha256=hashlib.sha256(archive).hexdigest())
        self.schema.assets = {self.assets[0]['url']:f.json_bytes(manifest), self.assets[1]['url']:archive}
        def download(url, *args):
            self.application_calls.append(url)
            self.assertNotIn('/latest', url, 'real Worker must not refetch latest after updater commit')
            return self.schema.assets[url]
        self.download_patch = patch.object(self.w, 'download', side_effect=download)
        self.download_patch.start()
        self.addCleanup(self.download_patch.stop)

    def runner(self, args, **kwargs):
        command, lock_fd, digest, selection_fd = self.w.parse_command(args[4:])
        self.assertIn(lock_fd, kwargs['pass_fds'])
        if command == 'self-check':
            return f.json_bytes(self.w.self_check(self.worker))
        self.assertEqual(self.u.journal()['active']['version'], getattr(self, 'expected_runtime', '1.1.0'),
                         'selected compatible runtime must be stable before real app work')
        self.worker.runtime_digest = digest
        self.worker.require_release_selection = True
        if selection_fd is not None:
            # memfd is real/sealed; only ownership is injected because this
            # suite runs unprivileged. Root-only ownership rejection is tested
            # separately and production always expects uid 0.
            actual_fstat = self.w.os.fstat
            def root_descriptor(fd):
                info = actual_fstat(fd)
                if fd == selection_fd:
                    values = list(info); values[4] = 0
                    return os.stat_result(values)
                return info
            with patch.object(self.w.os, 'fstat', side_effect=root_descriptor):
                self.worker.release_selection = self.w.read_release_selection(selection_fd, self.worker.c)
        else:
            self.worker.release_selection = None
        if command == 'run':
            self.worker.run()
        elif command in ('recover', 'boot-recover'):
            if not self.worker.recover(boot=command == 'boot-recover'):
                raise s.SupervisorError('runtime')
        else:
            self.fail('unexpected dispatch command')
        return b''

    def fetch(self, url, *args):
        self.calls.append(url)
        if url.endswith('/latest'): return f.json_bytes(self.release)
        return {1:self.schema.assets[self.assets[0]['url']], 2:self.schema.assets[self.assets[1]['url']],
                3:self.bundle[0], 4:self.bundle[2]}[int(url.rsplit('/', 1)[1])]

    def acquire(self):
        result = t.refresh(self.u, 'run', fetch=self.fetch, now=lambda:1000)
        self.assertEqual(result['result'], 'updated')
        self.runtime.stable('1.1.0')
        self.assertEqual(self.worker.schema(), 2)
        self.assertEqual(self.schema.effects, [], 'updater preflight cannot stop/build/backup/migrate app')
        return result['selection']

    def preserved(self):
        with sqlite3.connect(self.schema.path) as db:
            self.assertEqual(db.execute('SELECT * FROM preserved').fetchall(),
                             [('whatsapp','encrypted-auth'), ('message','encrypted-message')])

    def test_production_worker_updates_050_schema2_to_060_schema3_after_runtime_commit(self):
        selection = self.acquire()
        self.release = dict(self.release, id=42, tag_name='v0.7.0')
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.assertEqual(self.worker.schema(), 3)
        self.assertEqual(self.worker.status['runtimeDigest'], self.u.journal()['active']['sha256'])
        self.assertEqual(self.worker.status['previousDatabaseSchema'], 2)
        self.assertEqual(self.worker.status['targetDatabaseSchema'], 3)
        self.assertEqual(self.application_calls, [self.assets[0]['url'], self.assets[1]['url']])
        commands = [effect[1] for effect in self.schema.effects if effect[0] == 'command']
        smoke = next(i for i, cmd in enumerate(commands) if 'scripts/update-smoke.ts' in cmd)
        backup = next(i for i, cmd in enumerate(commands) if 'scripts/update-backup.ts' in cmd)
        start = next(i for i, cmd in enumerate(commands) if cmd[:2] == ['/usr/bin/systemctl','start'])
        self.assertLess(smoke, backup); self.assertLess(backup, start)
        self.assertEqual(commands[smoke][-4:], ['--database-schema','3','--database-migrations-from','[2]'])
        self.runtime.stable('1.1.0')
        self.preserved()

    def test_production_worker_health_failure_after_migration_keeps_new_runtime_and_db(self):
        self.schema.fail_health = True
        selection = self.acquire()
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assertEqual(self.worker.schema(), 3)
        self.assertEqual(self.schema.link.resolve().name, 'v0.6.0')
        self.runtime.stable('1.1.0')
        self.schema.effects.clear()
        with self.assertRaises(s.SupervisorError): self.u.dispatch('boot-recover')
        self.assertEqual(self.schema.effects, [], 'boot recovery must not start previous schema-2 code')
        self.assertEqual(self.worker.schema(), 3)
        self.runtime.stable('1.1.0')
        self.preserved()

    def test_production_worker_failed_migration_can_rollback_app_not_committed_runtime(self):
        self.schema.migrates = False
        self.schema.fail_health = True
        selection = self.acquire()
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'rolled_back')
        self.assertEqual(self.worker.schema(), 2)
        self.assertEqual(self.schema.link.resolve(), self.schema.old)
        self.assertEqual(json.loads((self.schema.old / 'package.json').read_bytes())['version'], '0.5.0')
        self.runtime.stable('1.1.0')
        self.preserved()

    def queue_production_install(self, *, include_target=True, phase='available', changed_status=False):
        self.worker.c['mode'] = 'notify'
        self.worker.request = lambda: self.w.Worker.request(self.worker)
        target = self.w.release_target(self.release, self.worker.c['repository'])
        self.worker.save(phase=phase, installTarget=dict(target, fingerprint='b'*64) if changed_status else target)
        value = dict(id=str(uuid.uuid4()), action='install', familyId='f', userId='p',
                     createdAt=self.w.time.time()*1000)
        if include_target: value['target'] = target
        path = self.schema.root / 'data/update-request.json'
        f.write(path, f.json_bytes(value))
        return value

    def assert_no_application_work(self):
        self.assertEqual(self.application_calls, [])
        self.assertEqual(self.schema.effects, [], 'refused target cannot build, stop, back up or activate app')
        self.assertEqual(self.worker.schema(), 2)
        self.assertEqual(self.schema.link.resolve(), self.schema.old)
        self.runtime.stable('1.1.0')
        self.preserved()

    def test_production_notify_request_binds_displayed_target_and_correlated_success(self):
        selection = self.acquire()
        request = self.queue_production_install()
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['lastRequest']['id'], request['id'])
        self.assertEqual(self.worker.status['lastRequest']['state'], 'succeeded')
        self.assertEqual(self.worker.status['lastRequest']['target'], request['target'])
        self.assertEqual(self.worker.schema(), 3)
        self.preserved()

    def test_production_notify_missing_target_refuses_before_any_app_work(self):
        selection = self.acquire()
        self.queue_production_install(include_target=False)
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['error'], 'target_changed')
        self.assertEqual(self.worker.status['lastRequest']['state'], 'failed')
        self.assert_no_application_work()

    def test_production_notify_changed_root_displayed_target_refuses_before_any_app_work(self):
        selection = self.acquire()
        self.queue_production_install(changed_status=True)
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['error'], 'target_changed')
        self.assert_no_application_work()

    def test_production_notify_nonavailable_status_refuses_even_matching_target(self):
        selection = self.acquire()
        self.queue_production_install(phase='healthy')
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['error'], 'target_changed')
        self.assert_no_application_work()

    def test_production_notify_changed_selected_release_assets_refuse_before_download(self):
        selection = self.acquire()
        self.queue_production_install()
        changed = json.loads(json.dumps(selection))
        asset = next(item for item in changed['release']['assets'] if item['name']=='yachad-updater.zip')
        asset['url'] = 'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/99'
        self.u.dispatch('run', selection=changed)
        self.assertEqual(self.worker.status['error'], 'target_changed')
        self.assert_no_application_work()

    def test_auto_app_compatible_without_optional_updater_assets_uses_existing_runtime(self):
        self.expected_runtime = '1.0.0'
        self.release['assets'] = self.assets[:2]
        result = t.refresh(self.u, 'run', fetch=self.fetch, now=lambda:1000)
        self.assertEqual(result['result'], 'unavailable')
        self.assertEqual(self.calls, ['https://api.github.com/repos/NehoraiHadad/yahad/releases/latest',
                                      self.assets[0]['url'], self.assets[1]['url']])
        self.runtime.stable('1.0.0')
        self.u.dispatch('run', selection=result['selection'])
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.schema(), 3)
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.runtime.stable('1.0.0')
        self.preserved()

    def test_production_check_in_auto_mode_can_install_selected_app_without_target(self):
        selection = self.acquire()
        request = self.queue_production_install(include_target=False)
        request['action'] = 'check'
        f.write(self.schema.root / 'data/update-request.json', f.json_bytes(request))
        self.worker.c['mode'] = 'auto'
        self.worker.save(phase='healthy', installTarget=None)
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.assertEqual(self.worker.status['lastRequest']['action'], 'check')
        self.assertEqual(self.worker.status['lastRequest']['state'], 'succeeded')
        self.assertEqual(self.worker.schema(), 3)
        self.preserved()

    def test_joined_selection_overrides_fresh_legacy_app_cache(self):
        self.worker.save(checkedAt=int(self.w.time.time()*1000), failedVersion=None)
        selection = self.acquire()
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.assertEqual(self.worker.schema(), 3)
        self.preserved()

    def test_absent_auto_selection_does_not_poison_cache_or_failed_version_and_later_installs(self):
        selection = self.acquire()
        self.worker.save(checkedAt=1, failedVersion='0.4.9')
        before = (self.worker.status['checkedAt'], self.worker.status['failedVersion'])
        self.u.dispatch('run')
        self.assertEqual((self.worker.status['checkedAt'], self.worker.status['failedVersion']), before)
        self.assert_no_application_work()
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.assertEqual(self.worker.schema(), 3)
        self.preserved()

    def test_timer_pending_notify_install_stays_joined_through_real_worker(self):
        request = self.queue_production_install()
        config = json.loads(self.u.config_path.read_bytes())
        config.update(mode='notify', operator=self.worker.c['operator'])
        f.write(self.u.config_path, f.json_bytes(config))
        transport_data = self.u.root / 'data'
        transport_data.mkdir(mode=0o700)
        # The production components use one queue; the two fixture roots expose
        # the same inode through this bounded publication-style second link.
        queue = self.schema.root / 'data/update-request.json'
        os.link(queue, transport_data / 'update-request.json')
        result = t.refresh(self.u, 'run', fetch=self.fetch, now=self.w.time.time)
        self.assertEqual(result['result'], 'updated')
        self.assertEqual(json.loads(queue.read_bytes()), request)
        self.assertEqual(self.schema.effects, [])
        self.u.dispatch('run', selection=result['selection'])
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['lastRequest']['id'], request['id'])
        self.assertEqual(self.worker.status['lastRequest']['state'], 'succeeded')
        self.assertFalse(queue.exists())
        self.assertEqual(self.worker.schema(), 3)
        self.runtime.stable('1.1.0')
        self.preserved()

    def test_app_manifest_second_download_must_equal_preflight_bytes_before_build_or_downtime(self):
        selection = self.acquire()
        self.worker.save(failedVersion='0.4.9')
        self.schema.assets[self.assets[0]['url']] += b'\n'
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'failed')
        self.assertEqual(self.worker.status['error'], 'target_changed')
        self.assertEqual(self.worker.status['failedVersion'], '0.4.9')
        self.assertEqual(self.schema.effects, [])
        self.assertEqual(self.worker.schema(), 2)
        self.assertEqual(self.schema.link.resolve(), self.schema.old)
        self.runtime.stable('1.1.0')
        self.preserved()

    def test_stale_displayed_target_through_supervisor_main_returns_correlated_failure_without_work(self):
        self.expected_runtime = '1.0.0'
        request = self.queue_production_install()
        self.worker.save(checkedAt=123, failedVersion='0.4.9')
        config = json.loads(self.u.config_path.read_bytes())
        config.update(mode='notify', operator=self.worker.c['operator'])
        f.write(self.u.config_path, f.json_bytes(config))
        transport_data = self.u.root / 'data'
        transport_data.mkdir(mode=0o700)
        os.link(self.schema.root / 'data/update-request.json', transport_data / 'update-request.json')
        self.release = dict(self.release, id=42, tag_name='v0.7.0')
        with patch.object(s.os, 'geteuid', return_value=0), \
                patch.object(s.sys, 'argv', ['supervisor','run']), \
                patch.object(s, 'Supervisor', return_value=self.u), \
                patch.object(s, 'refresh_runtime', side_effect=lambda supervisor,command:
                             t.refresh(supervisor,command,fetch=self.fetch,now=self.w.time.time)):
            s.main()
        self.assertEqual(self.worker.status['phase'], 'failed')
        self.assertEqual(self.worker.status['error'], 'target_changed')
        self.assertEqual(self.worker.status['lastRequest']['id'], request['id'])
        self.assertEqual(self.worker.status['lastRequest']['state'], 'failed')
        self.assertEqual(self.worker.status['lastRequest']['error'], 'target_changed')
        self.assertEqual(self.worker.status['lastRequest']['target'], request['target'])
        self.assertEqual(self.worker.status['checkedAt'], 123)
        self.assertEqual(self.worker.status['failedVersion'], '0.4.9')
        self.assertEqual(self.calls, ['https://api.github.com/repos/NehoraiHadad/yahad/releases/latest'])
        self.assertEqual(self.application_calls, [])
        self.assertEqual(self.schema.effects, [])
        self.assertEqual(self.worker.schema(), 2)
        self.runtime.stable('1.0.0')
        self.preserved()

    def test_new_committed_runtime_can_retry_previous_runtime_incompatible_app_once(self):
        old_runtime = self.u.journal()['active']['sha256']
        self.worker.save(failedVersion='0.6.0', failedRuntimeDigest=old_runtime)
        selection = self.acquire()
        self.assertNotEqual(self.u.journal()['active']['sha256'], old_runtime)
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.schema(), 3)
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.preserved()

    def test_same_failed_runtime_and_version_are_not_retried_automatically(self):
        selection = self.acquire()
        self.worker.save(failedVersion='0.6.0', failedRuntimeDigest=self.u.journal()['active']['sha256'])
        self.u.dispatch('run', selection=selection)
        self.assertEqual(self.worker.status['phase'], 'failed')
        self.assert_no_application_work()


if __name__ == '__main__':
    unittest.main()
