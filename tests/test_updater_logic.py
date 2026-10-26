"""Failure-state tests independent of systemd, ownership and package installation."""
import importlib.util
import json
import io
import zipfile
import pathlib
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('worker_logic', pathlib.Path(__file__).parents[1] / 'deploy/updater.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)

class LogicTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.old = pathlib.Path(self.tmp.name)
        (self.old / 'package.json').write_text('{"version":"0.2.0"}')
        outer = self
        class Worker(u.Worker):
            def __init__(self):
                self.c = {'enabled': False, 'service': 'yachad.service'}
                self.status = {'phase':'checking_health', 'previousPath':str(outer.old),
                    'currentVersion':'0.2.0', 'rollbackVersion':'0.2.0', 'latestVersion':'0.3.0'}
                self.effects = []
                self.db_schema = 1
                self.fail_health = False
            def save(self, **changes): self.status.update(changes)
            def command(self, args, **kw): self.effects.append(args[1])
            def schema(self): return self.db_schema
            def switch(self, path): self.effects.append('switch')
            def health(self, expected=None):
                self.effects.append(('health', expected))
                if self.fail_health: raise u.UpdateError('health')
            def request(self): raise AssertionError('Must not consume requests during failed recovery')
        self.worker = Worker()

    def tearDown(self): self.tmp.cleanup()

    def test_recovery_orders_stop_schema_switch_start_and_version_health(self):
        w = self.worker
        self.assertTrue(w.recover())
        self.assertEqual(w.effects, ['stop','switch','start',('health','0.2.0')])
        self.assertEqual(w.status['phase'], 'rolled_back')
        self.assertEqual(w.status['failedVersion'], '0.3.0')

    def test_schema_change_stops_and_requires_owner_never_downgrades_database(self):
        w = self.worker; w.db_schema = 2
        w.run()
        self.assertEqual(w.effects, ['stop'])
        self.assertEqual(w.status['phase'], 'recovery_required')
        w.run()
        self.assertEqual(w.status['phase'], 'recovery_required')

    def test_failed_rollback_health_blocks_even_disabled_updates(self):
        w = self.worker; w.fail_health = True
        w.run()
        self.assertEqual(w.status['phase'], 'recovery_required')
        self.assertNotIn('idle', w.status.values())

    def test_boot_switches_without_service_commands_and_defers_health(self):
        w = self.worker
        self.assertTrue(w.recover(boot=True))
        self.assertEqual(w.effects, ['switch'])
        self.assertEqual(w.status['phase'], 'rollback_pending_health')
        w.run()
        self.assertEqual(w.effects[-1], ('health','0.2.0'))
        self.assertEqual(w.status['phase'], 'rolled_back')

    def test_wrong_previous_version_is_never_started(self):
        w = self.worker; w.status['rollbackVersion'] = '0.1.0'
        self.assertFalse(w.recover())
        self.assertEqual(w.effects, ['stop'])
        self.assertEqual(w.status['phase'], 'recovery_required')

    def test_crash_at_each_maintenance_phase_can_recover(self):
        for phase in u.PHASES:
            with self.subTest(phase=phase):
                self.worker.status['phase'] = phase
                self.assertTrue(self.worker.recover())
                self.assertEqual(self.worker.status['phase'], 'rolled_back')

    def test_idle_recovery_has_no_side_effects(self):
        self.worker.status['phase'] = 'healthy'
        self.assertTrue(self.worker.recover())
        self.assertEqual(self.worker.effects, [])

    def test_dependency_stream_cannot_replace_signed_source_or_escape(self):
        def stream(name, mode=0o100644):
            out = io.BytesIO()
            with zipfile.ZipFile(out, 'w') as archive:
                entry = zipfile.ZipInfo(name)
                entry.filename = name
                entry.external_attr = mode << 16
                archive.writestr(entry, 'fixture')
            return out.getvalue()
        for name, mode in [('src/main.ts',0o100644), ('../escape',0o100644),
                           ('node_modules/../escape',0o100644), ('node_modules/link',0o120777),
                           ('node_modules\\escape',0o100644), ('/node_modules/escape',0o100644)]:
            with self.subTest(name=name):
                with self.assertRaises(u.UpdateError): u.extract_dependencies(stream(name,mode), self.old)
        u.extract_dependencies(stream('node_modules/fixture/index.js'), self.old)
        self.assertEqual((self.old/'node_modules/fixture/index.js').read_text(), 'fixture')
        self.assertEqual(json.loads((self.old/'package.json').read_text())['version'], '0.2.0')

if __name__ == '__main__': unittest.main()
