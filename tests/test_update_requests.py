"""Immediate request orchestration: temporary files/flock; no real services/network.

Root ownership is simulated here so event/race checks also run as an ordinary
development user. These are not native-systemd sandbox acceptance tests.
"""
import configparser
import importlib.util
import json
import os
import pathlib
import pwd
import tempfile
import threading
import time
import unittest
import uuid
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('request_worker', pathlib.Path(__file__).parents[1] / 'deploy/updater.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class RequestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = pathlib.Path(self.tmp.name)
        for name in ('data', 'state', 'current'):
            (self.root / name).mkdir()
        (self.root / 'current/package.json').write_text('{"version":"0.4.0"}')
        self.config_path = self.root / 'config.json'
        self.queue_path = self.root / 'data/update-request.json'
        self.config = dict(enabled=True, mode='notify', allowUiModeControl=True,
                           operator=dict(familyId='fixture-family', userId='fixture-parent'),
                           appUser=pwd.getpwuid(os.getuid()).pw_name,
                           dataDirectory=str(self.root / 'data'), stateDirectory=str(self.root / 'state'))
        self.config_path.write_text(json.dumps(self.config))
        self.effects = []
        self.block_recovery = False
        outer = self

        class Worker(u.Worker):
            def recover(self, boot=False):
                return not outer.block_recovery
            def current(self):
                return outer.root / 'current'
            def release(self, token):
                outer.effects.append(('release', self.c['mode']))
                return {'tag_name': 'v0.4.1'}, {}
            def install(self, *args):
                outer.effects.append(('install', self.c['mode']))
            def command(self, *args, **kwargs):
                raise AssertionError('No real command permitted in request fixture')

        self.Worker = Worker
        def atomic(path, value, group=None):
            path = pathlib.Path(path)
            self.assertTrue(path.is_relative_to(self.root))
            temporary = path.with_suffix('.fixture-tmp')
            temporary.write_text(json.dumps(value))
            temporary.chmod(0o600)
            temporary.replace(path)
        def private(path):
            path = pathlib.Path(path)
            self.assertTrue(path.is_relative_to(self.root))
            return path.read_bytes()
        for name, value in [('CONFIG', self.config_path), ('Worker', Worker), ('atomic_json', atomic), ('private_file', private)]:
            p = patch.object(u, name, value)
            p.start()
            self.addCleanup(p.stop)

    def queue(self, action='check', **changes):
        value = dict(id=str(uuid.uuid4()), action=action, familyId='fixture-family',
                     userId='fixture-parent', createdAt=int(time.time() * 1000))
        value.update(changes)
        self.queue_path.write_text(json.dumps(value))
        self.queue_path.chmod(0o600)
        return value

    def set_mode(self, mode):
        self.config['mode'] = mode
        self.config_path.write_text(json.dumps(self.config))

    def run_requests(self):
        u.run_requests(self.root / 'state')

    def test_missing_or_deleted_queue_event_does_not_check_or_install_in_auto(self):
        self.set_mode('auto')
        self.queue(); self.queue_path.unlink()
        self.run_requests()
        self.Worker(dict(self.config)).run(request_only=True)
        self.assertEqual(self.effects, [])
        self.assertFalse((self.root / 'state/status.json').exists())

    def test_invalid_foreign_expired_request_never_becomes_scheduled_auto_check(self):
        self.set_mode('auto')
        for values in ({'userId': 'other'}, {'action': 'arbitrary-command'}, {'createdAt': 0}):
            self.queue(**values)
            self.run_requests()
            self.assertFalse(self.queue_path.exists())
        self.assertEqual(self.effects, [])

    def test_expired_authenticated_check_and_install_are_acknowledged_without_fetch(self):
        self.set_mode('auto')
        for action in ('check', 'install'):
            for request_only in (True, False):
                intent = self.queue(action, createdAt=int(time.time()*1000)-600001)
                if request_only: self.run_requests()
                else: self.Worker(dict(self.config)).run()
                request = json.loads((self.root / 'state/status.json').read_text())['lastRequest']
                self.assertEqual(request['id'], intent['id']); self.assertEqual(request['action'], action)
                self.assertEqual(request['state'], 'failed'); self.assertEqual(request['error'], 'expired')
                self.assertEqual(request['startedAt'], request['finishedAt'])
                self.assertFalse(self.queue_path.exists()); self.assertEqual(self.effects, [])

    def test_expiry_ack_write_failure_preserves_queue_and_foreign_expiry_has_no_journal(self):
        self.queue('install', createdAt=0)
        with patch.object(self.Worker, 'save', side_effect=OSError('synthetic expiry journal failure')):
            with self.assertRaises(OSError): self.run_requests()
        self.assertTrue(self.queue_path.exists()); self.assertEqual(self.effects, [])
        self.queue('install', createdAt=0, userId='foreign-parent')
        self.run_requests()
        self.assertFalse(self.queue_path.exists())
        self.assertFalse((self.root / 'state/status.json').exists())

    def test_notify_check_is_prompt_even_inside_check_interval_without_install(self):
        worker = self.Worker(dict(self.config))
        worker.save(checkedAt=int(time.time() * 1000))
        self.queue()
        self.run_requests()
        self.assertEqual(self.effects, [('release', 'notify')])
        self.assertFalse(self.queue_path.exists())
        self.assertEqual(json.loads(worker.status_path.read_text())['latestVersion'], '0.4.1')
        request = json.loads(worker.status_path.read_text())['lastRequest']
        self.assertEqual(request['action'], 'check'); self.assertEqual(request['state'], 'succeeded')
        self.assertGreaterEqual(request['finishedAt'], request['startedAt'])

    def test_running_is_journalled_before_queue_removal_and_failure_is_not_success(self):
        intent = self.queue('install')
        def failure(worker, token):
            self.assertFalse(self.queue_path.exists())
            journal = json.loads(worker.status_path.read_text())
            self.assertEqual(journal['lastRequest']['id'], intent['id'])
            self.assertEqual(journal['lastRequest']['state'], 'running')
            self.assertEqual(journal['phase'], 'checking')
            raise u.UpdateError('network')
        with patch.object(self.Worker, 'release', failure): self.run_requests()
        journal = json.loads((self.root / 'state/status.json').read_text())
        self.assertEqual(journal['lastRequest']['state'], 'failed')
        self.assertEqual(journal['lastRequest']['error'], 'network')
        self.assertEqual(journal['phase'], 'failed')

    def test_failed_running_journal_preserves_queued_request_for_retry(self):
        self.queue()
        with patch.object(self.Worker, 'save', side_effect=OSError('synthetic journal failure')):
            with self.assertRaises(OSError): self.run_requests()
        self.assertTrue(self.queue_path.exists()); self.assertEqual(self.effects, [])

    def test_orphaned_running_request_is_failed_by_next_serialized_timer_not_claimed_successful(self):
        self.queue('install')
        worker = self.Worker(dict(self.config)); worker.request()
        worker.c['enabled'] = False
        worker.run()
        self.assertEqual(worker.status['lastRequest']['state'], 'failed')
        self.assertEqual(worker.status['lastRequest']['error'], 'internal')
        self.assertEqual(self.effects, [])

    def test_stop_post_recovery_closes_an_orphaned_running_journal_promptly(self):
        self.queue('install'); self.Worker(dict(self.config)).request()
        original_lstat = pathlib.Path.lstat
        def root_state(path):
            info = original_lstat(path)
            if path == self.root / 'state':
                fields = list(info); fields[4] = 0; return os.stat_result(fields)
            return info
        with patch.object(u.os, 'geteuid', return_value=0), patch.object(u.sys, 'argv', ['updater.py', 'recover']), \
             patch.object(pathlib.Path, 'lstat', root_state):
            u.main()
        request = json.loads((self.root / 'state/status.json').read_text())['lastRequest']
        self.assertEqual(request['state'], 'failed'); self.assertEqual(request['error'], 'internal')
        self.assertEqual(self.effects, [])

    def test_auto_check_retains_explicit_existing_auto_install_semantics(self):
        self.set_mode('auto'); self.queue(); self.run_requests()
        self.assertEqual(self.effects, [('release', 'auto'), ('install', 'auto')])

    def test_mode_only_never_fetches_and_follow_on_check_uses_new_policy(self):
        self.set_mode('auto')
        self.queue('set_mode', mode='notify')
        original = self.Worker.finish_mode_request
        def finish(worker, *args, **kwargs):
            original(worker, *args, **kwargs)
            self.assertEqual(self.effects, [])
            self.queue('check')  # A second click during the first service run.
        with patch.object(self.Worker, 'finish_mode_request', finish):
            self.run_requests()
        self.assertEqual(self.effects, [('release', 'notify')])
        self.assertFalse(self.queue_path.exists())
        self.assertEqual(json.loads(self.config_path.read_text())['mode'], 'notify')

    def test_unchanged_disabled_recovery_and_pending_config_failures_do_not_spin(self):
        for blocked in ('disabled', 'recovery', 'configuration'):
            with self.subTest(blocked=blocked):
                self.config['enabled'] = blocked != 'disabled'
                self.config_path.write_text(json.dumps(self.config))
                self.block_recovery = blocked == 'recovery'
                self.queue('check')
                worker = self.Worker(dict(self.config))
                if blocked == 'configuration':
                    pending = dict(id=str(uuid.uuid4()), mode='auto', previousMode='notify', operator=self.config['operator'])
                    worker.save(modeChangePending=pending)
                original = self.Worker.run
                calls = []
                def counted(worker, *args, **kwargs):
                    calls.append(True)
                    return original(worker, *args, **kwargs)
                original_private = u.private_file
                def fail(path):
                    if blocked == 'configuration' and calls:
                        raise OSError('synthetic config write/read failure')
                    return original_private(path)
                with patch.object(self.Worker, 'run', counted), patch.object(u, 'private_file', fail):
                    self.run_requests()
                self.assertEqual(len(calls), 1)
                self.assertTrue(self.queue_path.exists())
                self.assertEqual(self.effects, [])

    def test_malicious_continuous_replacement_has_bounded_drain(self):
        self.queue()
        calls = []
        def replace(worker, request_only=False):
            self.assertTrue(request_only)
            calls.append(True)
            self.queue_path.unlink(); self.queue()
        with patch.object(self.Worker, 'run', replace):
            self.run_requests()
        self.assertEqual(len(calls), 16)
        self.assertTrue(self.queue_path.exists())  # Timer fallback, never drop intent.

    def test_real_flock_wait_rereads_replaced_queue_and_changed_mode(self):
        self.set_mode('auto')
        self.queue('install')
        loaded = threading.Event(); errors = []
        original_load = u.load_config
        def load():
            result = original_load(); loaded.set(); return result
        original_lstat = pathlib.Path.lstat
        def root_state(path):
            st = original_lstat(path)
            if path == self.root / 'state':
                fields = list(st); fields[4] = 0; return os.stat_result(fields)
            return st
        def target():
            try: u.main()
            except BaseException as error: errors.append(error)
        with (self.root / 'state/worker.lock').open('a') as held:
            u.fcntl.flock(held, u.fcntl.LOCK_EX)
            with patch.object(u.os, 'geteuid', return_value=0), patch.object(u.sys, 'argv', ['updater.py', 'requests']), \
                 patch.object(u, 'load_config', load), patch.object(pathlib.Path, 'lstat', root_state):
                thread = threading.Thread(target=target, daemon=True); thread.start()
                self.assertTrue(loaded.wait(2))
                self.assertTrue(thread.is_alive())
                self.queue_path.unlink(); self.queue('check'); self.set_mode('notify')
                u.fcntl.flock(held, u.fcntl.LOCK_UN)
                thread.join(3)
                self.assertFalse(thread.is_alive())
        self.assertEqual(errors, [])
        self.assertEqual(self.effects, [('release', 'notify')])
        self.assertFalse(self.queue_path.exists())

    def test_state_directory_change_after_lock_is_rejected(self):
        self.config['stateDirectory'] = str(self.root / 'elsewhere')
        self.config_path.write_text(json.dumps(self.config))
        with self.assertRaises(u.UpdateError): self.run_requests()


class UnitContractTests(unittest.TestCase):
    def unit(self, name):
        parser = configparser.ConfigParser(interpolation=None)
        parser.read(pathlib.Path(__file__).parents[1] / 'deploy' / name)
        return parser

    def test_latched_event_and_request_only_service_keep_timer_and_hardening(self):
        path = self.unit('yachad-update-request.path')
        service = self.unit('yachad-update-request.service')
        timer = self.unit('yachad-update.timer')
        self.assertEqual(path['Path']['Unit'], 'yachad-update-request.service')
        self.assertTrue(path['Path']['PathExists'].endswith('/update-request.wake'))
        self.assertTrue(path['Path']['PathChanged'].endswith('/update-request.json'))
        self.assertNotIn('DirectoryNotEmpty', path['Path'])
        self.assertTrue(service['Service']['ExecStart'].endswith('updater.py requests'))
        self.assertTrue(service['Service']['ExecStartPre'].endswith('/update-request.wake'))
        self.assertNotIn('update-request.wake', service['Service']['ExecStopPost'])
        self.assertEqual(service['Service']['ProtectSystem'], 'strict')
        self.assertEqual(service['Service']['NoNewPrivileges'], 'true')
        self.assertEqual(timer['Timer']['Unit'], 'yachad-update.service')
        self.assertEqual(timer['Timer']['OnUnitInactiveSec'], '5min')

    def test_latch_arriving_at_shutdown_stays_level_triggered_but_blocked_queue_does_not(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory); queue = root / 'update-request.json'; latch = root / 'update-request.wake'
            queue.write_text('synthetic queued request'); os.link(queue, latch)
            latch.unlink()  # ExecStartPre, not the worker's exit path.
            self.assertTrue(queue.exists()); self.assertFalse(latch.exists())
            # Simulate a click in the service's final-check/exit/re-arm window.
            queue.unlink(); queue.write_text('next synthetic request'); os.link(queue, latch)
            self.assertTrue(latch.exists())
            latch.unlink(); queue.unlink()  # Next activation consumes it once.
            self.assertFalse(latch.exists()); self.assertFalse(queue.exists())


if __name__ == '__main__': unittest.main()
