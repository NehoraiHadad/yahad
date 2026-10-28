"""Schema-2 upgrade/rollback contract; temp SQLite, mocked services/build/network.

No root privileges, real provider, systemd, household store or OS changes.
"""
import contextlib
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import pwd
import sqlite3
import stat
import tempfile
import types
import unittest
import zipfile
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('schema_worker', ROOT / 'deploy/updater.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class SchemaContractTests(unittest.TestCase):
    def test_format2_contract_rejects_ambiguous_migrations_and_future_worker_protocol(self):
        base = dict(format=2, databaseSchema=3, databaseMigrationsFrom=[1, 2], minimumUpdaterProtocol=2)
        self.assertEqual(u.manifest_contract(base), (3, [1, 2]))
        for changes in ({'format': True}, {'format': 3}, {'databaseMigrationsFrom': None},
                        {'databaseMigrationsFrom': [1, 1]}, {'databaseMigrationsFrom': [3]},
                        {'databaseMigrationsFrom': [4]}, {'databaseMigrationsFrom': [True]},
                        {'databaseMigrationsFrom': ['1']}, {'databaseMigrationsFrom': [0]},
                        {'databaseMigrationsFrom': list(range(1, 34)), 'databaseSchema': 40},
                        {'minimumUpdaterProtocol': True}, {'minimumUpdaterProtocol': 0},
                        {'minimumUpdaterProtocol': 4}, {'minimumUpdaterProtocol': '2'}):
            with self.subTest(changes=changes), self.assertRaises(u.UpdateError):
                u.manifest_contract(dict(base, **changes))
        self.assertEqual(u.manifest_contract({'format': 1, 'databaseSchema': 1}), (1, []))
        with self.assertRaises(u.UpdateError): u.manifest_contract({'format': 1, 'databaseSchema': 2})

    def test_manifest_accepts_only_explicit_known_integer_schemas(self):
        archive = b'synthetic archive'
        base = dict(format=2, minimumUpdaterProtocol=2, databaseMigrationsFrom=[], version='0.4.1', archive=u.ASSETS[2], bytes=len(archive),
                    sha256=hashlib.sha256(archive).hexdigest())
        for schema in (1, 2, 3, 1000000):
            manifest = dict(base, databaseSchema=schema)
            self.assertEqual(u.verify_manifest(json.dumps(manifest).encode(), archive), manifest)
        for schema in (None, True, False, '2', 2.0, 0, 1000001):
            with self.subTest(schema=schema), self.assertRaises(u.UpdateError):
                u.verify_manifest(json.dumps(dict(base, databaseSchema=schema)).encode(), archive)

    def test_forward_migration_only_and_no_downgrade_or_unknown_schema(self):
        for old, new in ((1, 1), (1, 2), (2, 2)):
            u.schema_transition(old, new, [1, 2])
        for old, new in ((2, 1), (0, 2), (1, 3), (True, 2), (1, '2')):
            with self.subTest(old=old, new=new), self.assertRaises(u.UpdateError):
                u.schema_transition(old, new)

    def test_schema_reader_rejects_missing_multiple_and_unknown_versions(self):
        with tempfile.TemporaryDirectory() as tmp:
            worker = object.__new__(u.Worker)
            worker.c = {'dataDirectory': tmp}
            with sqlite3.connect(pathlib.Path(tmp) / 'app.sqlite') as db:
                db.execute('CREATE TABLE schema_version(version INTEGER)')
            for rows in ([], [1], [2], [3], [1, 2], [1000001]):
                with sqlite3.connect(pathlib.Path(tmp) / 'app.sqlite') as db:
                    db.execute('DELETE FROM schema_version')
                    db.executemany('INSERT INTO schema_version VALUES(?)', [(row,) for row in rows])
                if rows in ([1], [2], [3]):
                    self.assertEqual(worker.schema(), rows[0])
                else:
                    with self.assertRaises(u.UpdateError): worker.schema()

    def test_health_requires_exact_version_payload_schema_and_actual_schema(self):
        worker = object.__new__(u.Worker)
        worker.c = {'healthTimeoutSeconds': 1, 'healthUrl': 'http://127.0.0.1:3000/health/ready', 'service': 'fixture'}
        worker.command = lambda *a, **kw: b''
        for body, actual, succeeds in [
            ({'ok': True, 'version': '0.4.1', 'databaseSchema': 2}, 2, True),
            ({'ok': True, 'version': '0.4.0', 'databaseSchema': 2}, 2, False),
            ({'ok': True, 'version': '0.4.1', 'databaseSchema': 1}, 2, False),
            ({'ok': True, 'version': '0.4.1', 'databaseSchema': 2}, 1, False),
            ({'ok': True, 'version': '0.4.1', 'databaseSchema': True}, 1, False),
        ]:
            worker.schema = lambda: actual
            response = contextlib.nullcontext(io.BytesIO(json.dumps(body).encode()))
            opener = types.SimpleNamespace(open=lambda *a, **kw: response)
            with patch.object(u.urllib.request, 'build_opener', return_value=opener), \
                 patch.object(u.time, 'monotonic', side_effect=[0, 0, 2]), patch.object(u.time, 'sleep'):
                if succeeds: worker.health('0.4.1', expected_schema=2)
                else:
                    with self.assertRaises(u.UpdateError): worker.health('0.4.1', expected_schema=2)


@unittest.skipUnless(os.name == 'posix', 'Linux path/ownership fixture')
class SchemaLifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='yachad-schema-fixture-')
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.effects = []
        self.original_schema = 1
        self.target_schema = 2
        self.migrates = True
        self.fail_health = False
        self.fail_backup = False
        self.fail_smoke = False
        for name in ('releases', 'state', 'build', 'data', 'backups'):
            (self.root / name).mkdir()
        self.old = self.root / 'releases/v0.4.0'
        self.old.mkdir()
        (self.old / 'package.json').write_text('{"version":"0.4.0"}')
        self.link = self.root / 'current'
        self.link.symlink_to(self.old)
        self.path = self.root / 'data/app.sqlite'
        with sqlite3.connect(self.path) as db:
            db.executescript('CREATE TABLE schema_version(version INTEGER); INSERT INTO schema_version VALUES(1); '
                             'CREATE TABLE preserved(key TEXT, value BLOB); '
                             "INSERT INTO preserved VALUES('whatsapp','encrypted-auth'),('message','encrypted-message');")
        account = pwd.getpwuid(os.getuid())
        config = dict(enabled=True, mode='auto', repository=u.PUBLIC_REPOSITORY, releaseTrust='github-https',
                      publicKeyFile=None, tokenFile=None, operator={'familyId': 'f', 'userId': 'p'},
                      stateDirectory=str(self.root / 'state'), buildDirectory=str(self.root / 'build'),
                      dataDirectory=str(self.root / 'data'), backupDirectory=str(self.root / 'backups'),
                      backupPassphraseFile=str(self.root / 'secret'), masterKeyFile=str(self.root / 'master'),
                      currentLink=str(self.link), releasesDirectory=str(self.root / 'releases'),
                      appUser=account.pw_name, builderUser=account.pw_name, minimumFreeMiB=0,
                      service='fixture.service', node='node', npm='npm')
        fixture = self
        class Worker(u.Worker):
            def __init__(self):
                self.c = config
                self.state_dir = fixture.root / 'state'
                self.status = {'releaseTrust': 'github-https'}
                self.group = account.pw_gid
            def save(self, **changes): self.status.update(changes)
            def current(self): return fixture.link.resolve(strict=True)
            def switch(self, target):
                fixture.effects.append(('switch', str(target)))
                fixture.link.unlink()
                fixture.link.symlink_to(target)
            def command(self, args, **kwargs):
                fixture.effects.append(('command', args, kwargs))
                if '/usr/local/lib/yachad-updater/export-dependencies.py' in args:
                    stream = io.BytesIO()
                    with zipfile.ZipFile(stream, 'w') as archive:
                        entry = zipfile.ZipInfo('node_modules/fixture/index.js')
                        entry.external_attr = (stat.S_IFREG | 0o644) << 16
                        archive.writestr(entry, '// synthetic')
                    return stream.getvalue()
                if 'scripts/update-smoke.ts' in args and fixture.fail_smoke: raise u.UpdateError('build')
                if 'scripts/update-backup.ts' in args and fixture.fail_backup: raise u.UpdateError('backup')
                if 'scripts/update-backup.ts' in args:
                    backup = fixture.root / 'backups/yachad-12345.backup'
                    backup.write_bytes(b'synthetic encrypted fixture'); backup.chmod(0o600)
                if args[:2] == ['/usr/bin/systemctl', 'start'] and self.current() != fixture.old and fixture.migrates:
                    with sqlite3.connect(fixture.path) as db:
                        db.execute('UPDATE schema_version SET version=?', (fixture.target_schema,))
                return b'OK'
            def health(self, expected=None, expected_schema=None):
                fixture.effects.append(('health', expected, expected_schema))
                if self.schema() != expected_schema or (fixture.fail_health and expected == '0.4.1'):
                    raise u.UpdateError('health')
            def request(self): return None
            def release(self, token):
                return {'tag_name': 'v0.4.1'}, {name: {'url': name} for name in fixture.assets}
        self.worker = Worker()
        stack = contextlib.ExitStack()
        self.addCleanup(stack.close)
        original_lstat = pathlib.Path.lstat
        def synthetic_root(path):
            result = original_lstat(path)
            if path.is_relative_to(self.root) and path.parent != self.root / 'backups':
                values = list(result); values[4] = 0
                return os.stat_result(values)
            return result
        stack.enter_context(patch.object(pathlib.Path, 'lstat', synthetic_root))
        def no_chown(path, *a, **kw):
            self.assertTrue(pathlib.Path(path).is_relative_to(self.root), 'Mutation outside fixture')
        stack.enter_context(patch.object(u.os, 'chown', no_chown))
        stack.enter_context(patch.object(u.os, 'lchown', no_chown))
        stack.enter_context(patch.object(u, 'private_file', return_value=b'synthetic-long-backup-secret'))
        stack.enter_context(patch.object(u, 'download', side_effect=lambda url, *a: self.assets[url]))

    def run_upgrade(self):
        with sqlite3.connect(self.path) as db:
            db.execute('UPDATE schema_version SET version=?', (self.original_schema,))
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            for name in ('package-lock.json', 'dist/index.html', 'src/main.ts', 'scripts/update-smoke.ts', 'scripts/update-backup.ts'):
                archive.writestr(name, 'synthetic')
            archive.writestr('package.json', '{"version":"0.4.1"}')
        content = stream.getvalue()
        manifest = dict(format=2, minimumUpdaterProtocol=2, databaseMigrationsFrom=list(range(1, self.target_schema)), version='0.4.1', databaseSchema=self.target_schema, archive=u.ASSETS[2],
                        bytes=len(content), sha256=hashlib.sha256(content).hexdigest())
        self.assets = {u.ASSETS[0]: json.dumps(manifest).encode(), u.ASSETS[2]: content}
        if self.original_schema != 1:
            previous = dict(manifest, version='0.4.0', databaseSchema=self.original_schema, databaseMigrationsFrom=list(range(1, self.original_schema)))
            (self.old / u.RELEASE_METADATA).write_text(json.dumps(previous))
        self.worker.run()
        with sqlite3.connect(self.path) as db:
            self.assertEqual(db.execute('SELECT * FROM preserved').fetchall(),
                             [('whatsapp', 'encrypted-auth'), ('message', 'encrypted-message')])
        return [effect[1] for effect in self.effects if effect[0] == 'command']

    def test_schema1_to2_runs_unprivileged_smoke_and_backup_before_start(self):
        commands = self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['previousDatabaseSchema'], 1)
        self.assertEqual(self.worker.status['targetDatabaseSchema'], 2)
        self.assertEqual(self.worker.status['backupId'], 'yachad-12345.backup')
        smoke = next(i for i, cmd in enumerate(commands) if 'scripts/update-smoke.ts' in cmd)
        backup = next(i for i, cmd in enumerate(commands) if 'scripts/update-backup.ts' in cmd)
        start = next(i for i, cmd in enumerate(commands) if cmd[:2] == ['/usr/bin/systemctl', 'start'])
        self.assertLess(smoke, backup); self.assertLess(backup, start)
        self.assertEqual(commands[smoke][-4:], ['--database-schema', '2', '--database-migrations-from', '[1]'])
        for _, cmd, kwargs in [e for e in self.effects if e[0] == 'command']:
            if 'scripts/update-smoke.ts' in cmd or 'scripts/update-backup.ts' in cmd:
                self.assertEqual(kwargs['user'], self.worker.c['appUser'])

    def test_failed_health_after_migration_stops_without_old_code_or_data_restore(self):
        self.fail_health = True
        commands = self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assertEqual(self.worker.schema(), 2)
        self.assertEqual(self.link.resolve().name, 'v0.4.1')
        self.assertEqual(commands[-1], ['/usr/bin/systemctl', 'stop', 'fixture.service'])
        self.assertEqual(sum(e[0] == 'switch' for e in self.effects), 1)
        self.effects.clear()
        self.assertFalse(self.worker.recover(boot=True))
        self.assertEqual(self.effects, [])

    def test_failure_before_migration_can_restart_original_schema1(self):
        self.fail_health = True; self.migrates = False
        self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'rolled_back')
        self.assertEqual(self.worker.schema(), 1)
        self.assertEqual(self.link.resolve(), self.old)
        self.assertIn(('health', '0.4.0', 1), self.effects)

    def test_same_schema2_failure_can_roll_back_code_without_db_restore(self):
        self.original_schema = 2; self.fail_health = True
        self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'rolled_back')
        self.assertEqual(self.link.resolve(), self.old)
        self.assertEqual(self.worker.schema(), 2)
        self.assertIn(('health', '0.4.0', 2), self.effects)

    def test_backup_failure_never_activates_or_migrates(self):
        self.fail_backup = True
        self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'rolled_back')
        self.assertEqual(self.worker.schema(), 1)
        self.assertEqual(self.link.resolve(), self.old)
        self.assertTrue(all(e[1] == str(self.old) for e in self.effects if e[0] == 'switch'))

    def test_missing_or_ambiguous_backup_receipt_never_activates(self):
        observed = iter([set(), {'yachad-1.backup', 'yachad-2.backup'}])
        self.worker.backup_inventory = lambda: next(observed)
        self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'rolled_back')
        self.assertIsNone(self.worker.status['backupId'])
        self.assertEqual(self.link.resolve(), self.old)
        self.assertEqual(self.worker.schema(), 1)

    def test_backup_receipt_rejects_public_or_linked_archive_without_reading_content(self):
        path = self.root / 'backups/yachad-123.backup'
        path.write_bytes(b'synthetic encrypted fixture'); path.chmod(0o644)
        with self.assertRaises(u.UpdateError): self.worker.backup_inventory()
        path.unlink(); path.symlink_to(self.path)
        with self.assertRaises(u.UpdateError): self.worker.backup_inventory()

    def test_smoke_failure_happens_before_any_downtime_or_backup(self):
        self.fail_smoke = True
        commands = self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'failed')
        self.assertFalse(any('systemctl' in cmd[0] or 'scripts/update-backup.ts' in cmd for cmd in commands))
        self.assertEqual(self.link.resolve(), self.old)
        self.assertEqual(self.worker.schema(), 1)

    def test_schema2_to1_rejected_before_build_or_downtime(self):
        self.original_schema = 2; self.target_schema = 1
        commands = self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'incompatible')
        self.assertEqual(commands, [])
        self.assertEqual(self.link.resolve(), self.old)

    def test_future_declared_schema2_to3_requires_no_new_worker_code(self):
        self.original_schema = 2; self.target_schema = 3
        commands = self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.schema(), 3)
        self.assertEqual(next(cmd for cmd in commands if 'scripts/update-smoke.ts' in cmd)[-4:],
                         ['--database-schema', '3', '--database-migrations-from', '[1,2]'])

    def test_missing_or_wrong_previous_metadata_does_not_infer_support_from_db(self):
        self.original_schema = 2
        original = self.worker.app_schema
        self.worker.app_schema = lambda _path: 1
        commands = self.run_upgrade()
        self.assertEqual(self.worker.status['phase'], 'incompatible')
        self.assertEqual(commands, [])
        self.worker.app_schema = original

    def test_runtime_digest_mismatch_blocks_recovery_before_old_code_starts(self):
        self.run_upgrade()
        self.worker.status.update(phase='checking_health', runtimeDigest='a' * 64)
        self.worker.runtime_digest = 'b' * 64
        self.effects.clear()
        self.assertFalse(self.worker.recover())
        self.assertEqual(self.worker.status['runtimeDigest'], 'a' * 64)
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assertEqual(self.effects, [('command', ['/usr/bin/systemctl', 'stop', 'fixture.service'], {})])

    def test_runtime_digest_is_bound_to_application_maintenance_journal(self):
        self.worker.runtime_digest = 'a' * 64
        self.run_upgrade()
        self.assertEqual(self.worker.status['runtimeDigest'], 'a' * 64)

    def test_unknown_or_invalid_rollback_schema_never_starts_code(self):
        self.run_upgrade()
        self.worker.status.update(phase='checking_health', previousDatabaseSchema=True)
        self.effects.clear()
        self.assertFalse(self.worker.recover())
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assertEqual(self.effects, [('command', ['/usr/bin/systemctl', 'stop', 'fixture.service'], {})])


if __name__ == '__main__': unittest.main()
