"""Crash after readiness: adopt only the exact healthy app/runtime/schema pair.

Real production Worker decisions with temporary SQLite and injected service,
build and health effects. No deployment or live recovery is performed.
"""
import importlib.util
import json
import pathlib
import unittest
import uuid


ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('adoption_joined_fixture', ROOT / 'tests/test_github_runtime_integration.py')
j = importlib.util.module_from_spec(spec)
spec.loader.exec_module(j)


class PowerCut(BaseException):
    """Bypasses ordinary worker exception recovery, like abrupt process death."""


class RecoveryAdoptionTests(unittest.TestCase):
    setUp = j.ProductionWorkerJoinedTests.setUp
    runner = j.ProductionWorkerJoinedTests.runner
    fetch = j.ProductionWorkerJoinedTests.fetch
    acquire = j.ProductionWorkerJoinedTests.acquire
    preserved = j.ProductionWorkerJoinedTests.preserved
    queue_production_install = j.ProductionWorkerJoinedTests.queue_production_install

    def crash_after_readiness(self, *, with_request=False):
        selection = self.acquire()
        if with_request:
            self.bound_request = self.queue_production_install()
        original = self.worker.save
        cut = False
        def power_cut(**changes):
            nonlocal cut
            if changes.get('phase') == 'healthy' or cut:
                # A real abrupt death cannot persist an acknowledgement from a
                # Python finally clause after the first interrupted write.
                cut = True
                raise PowerCut()
            return original(**changes)
        self.worker.save = power_cut
        try:
            with self.assertRaises(PowerCut): self.u.dispatch('run', selection=selection)
        finally:
            self.worker.save = original
        self.assertEqual(self.worker.status['phase'], 'checking_health')
        self.assertEqual(self.worker.schema(), 3)
        self.assertEqual(self.schema.link.resolve().name, 'v0.6.0')
        self.assertIn(('health','0.6.0',3), self.schema.effects)
        self.assertEqual(self.worker.status['runtimeDigest'], self.u.journal()['active']['sha256'])
        self.schema.effects.clear()

    def assert_no_old_code_or_restore(self):
        self.assertEqual(self.worker.schema(), 3)
        self.assertEqual(self.schema.link.resolve().name, 'v0.6.0')
        self.runtime.stable('1.1.0')
        self.preserved()
        for effect in self.schema.effects:
            self.assertNotEqual(effect[0], 'switch', 'new schema must not be paired with old app code')
            if effect[0] == 'command':
                self.assertNotIn('restore', effect[1])
                self.assertNotIn('scripts/update-backup.ts', effect[1])

    def test_live_exact_healthy_candidate_is_adopted_without_stopping_service(self):
        self.crash_after_readiness()
        self.assertTrue(self.worker.recover())
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.assertIn(('health','0.6.0',3), self.schema.effects)
        self.assertFalse(any(effect[0] == 'command' and effect[1][:2] == ['/usr/bin/systemctl','stop']
                             for effect in self.schema.effects), 'successful adoption must not interrupt healthy app')
        self.assert_no_old_code_or_restore()

    def test_boot_pairs_candidate_without_starting_app_then_next_run_verifies_health(self):
        self.crash_after_readiness()
        self.assertTrue(self.worker.recover(boot=True))
        self.assertEqual(self.worker.status['phase'], 'checking_health')
        self.assertFalse(any(effect[0] == 'health' or effect[0] == 'command' and 'systemctl' in effect[1][0]
                             for effect in self.schema.effects), 'boot guard must not create a service ordering cycle')
        self.schema.effects.clear()
        self.u.dispatch('run')
        self.assertEqual(self.worker.status['phase'], 'healthy')
        self.assertEqual(self.worker.status['currentVersion'], '0.6.0')
        self.assertIn(('health','0.6.0',3), self.schema.effects)
        self.assert_no_old_code_or_restore()

    def test_boot_pending_health_failure_keeps_new_database_and_fails_closed(self):
        self.crash_after_readiness()
        self.assertTrue(self.worker.recover(boot=True))
        self.schema.fail_health = True
        self.schema.effects.clear()
        self.u.dispatch('run')
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assertTrue(any(effect[0] == 'command' and effect[1][:2] == ['/usr/bin/systemctl','stop']
                            for effect in self.schema.effects))
        self.assert_no_old_code_or_restore()

    def test_live_failed_health_is_not_adopted_and_never_restores_old_database(self):
        self.crash_after_readiness()
        self.schema.fail_health = True
        self.assertFalse(self.worker.recover())
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assert_no_old_code_or_restore()

    def test_previously_recorded_health_failure_is_not_adopted_as_crash_recovery(self):
        self.crash_after_readiness()
        self.worker.save(error='health')
        self.schema.effects.clear()
        self.assertFalse(self.worker.recover())
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assertFalse(any(effect[0] == 'health' for effect in self.schema.effects),
                         'known failure is not the ambiguous post-readiness crash case')
        self.assert_no_old_code_or_restore()

    def test_tampered_exact_manifest_is_not_adopted_even_when_schema_matches(self):
        self.crash_after_readiness()
        path = self.schema.link.resolve() / self.w.RELEASE_METADATA
        # A semantically equivalent whitespace mutation still differs from the
        # exact manifest bytes durably recorded before activation.
        path.write_bytes(path.read_bytes() + b'\n')
        self.assertFalse(self.worker.recover())
        self.assertEqual(self.worker.status['phase'], 'recovery_required')
        self.assert_no_old_code_or_restore()

    def test_runtime_or_target_version_schema_path_mismatch_is_not_adopted(self):
        changes = [dict(runtimeDigest='b'*64), dict(previousDatabaseSchema=True), dict(targetVersion='0.7.0'),
                   dict(targetDatabaseSchema=4), dict(targetManifestSha256='b'*64),
                   dict(targetPath=str(ROOT / 'outside-fixture'))]
        # Fresh isolated test fixture per mismatch so one fail-closed result
        # cannot make later cases pass merely because recovery is already closed.
        for change in changes:
            case = RecoveryAdoptionTests('test_live_exact_healthy_candidate_is_adopted_without_stopping_service')
            case.setUp()
            try:
                case.crash_after_readiness()
                case.worker.save(**change)
                case.schema.effects.clear()
                with self.subTest(change=change):
                    self.assertFalse(case.worker.recover())
                    self.assertEqual(case.worker.status['phase'], 'recovery_required')
                    case.assert_no_old_code_or_restore()
            finally:
                case.doCleanups()

    def test_exact_bound_install_is_completed_after_orphan_marking_and_live_adoption(self):
        self.crash_after_readiness(with_request=True)
        request = self.bound_request
        self.assertEqual(self.worker.status['activationRequestId'], request['id'])
        self.assertEqual(self.worker.status['activationRequestTarget'], request['target'])
        self.assertEqual(self.worker.status['lastRequest']['state'], 'running')
        self.worker.finish_request('failed', 'internal')
        self.assertTrue(self.worker.recover())
        final = self.worker.status['lastRequest']
        self.assertEqual((final['id'], final['target'], final['state'], final['error']),
                         (request['id'], request['target'], 'succeeded', None))
        self.assertIsInstance(final['finishedAt'], int)
        self.assert_no_old_code_or_restore()

    def test_boot_static_pairing_does_not_acknowledge_until_real_health_check(self):
        self.crash_after_readiness(with_request=True)
        self.worker.finish_request('failed', 'internal')
        before = dict(self.worker.status['lastRequest'])
        self.assertTrue(self.worker.recover(boot=True))
        self.assertEqual(self.worker.status['lastRequest'], before)
        self.assertEqual(self.worker.status['phase'], 'checking_health')
        self.assertFalse(any(effect[0] == 'health' for effect in self.schema.effects))
        self.u.dispatch('run')
        self.assertEqual(self.worker.status['lastRequest']['state'], 'succeeded')
        self.assertEqual(self.worker.status['lastRequest']['id'], self.bound_request['id'])
        self.assertIn(('health','0.6.0',3), self.schema.effects)
        self.assert_no_old_code_or_restore()

    def test_newer_request_other_target_or_known_request_health_error_is_not_acknowledged(self):
        for mismatch in ('id','target','health_error'):
            case = RecoveryAdoptionTests('test_live_exact_healthy_candidate_is_adopted_without_stopping_service')
            case.setUp()
            try:
                case.crash_after_readiness(with_request=True)
                case.worker.finish_request('failed', 'internal')
                request = dict(case.worker.status['lastRequest'])
                if mismatch == 'id': request['id'] = str(uuid.uuid4())
                elif mismatch == 'target': request['target'] = dict(request['target'], fingerprint='b'*64)
                else: request['error'] = 'health'
                case.worker.save(lastRequest=request)
                with self.subTest(mismatch=mismatch):
                    self.assertTrue(case.worker.recover())
                    self.assertEqual(case.worker.status['phase'], 'healthy')
                    self.assertEqual(case.worker.status['lastRequest'], request)
                    case.assert_no_old_code_or_restore()
            finally:
                case.doCleanups()


if __name__ == '__main__':
    unittest.main()
