"""Runtime transport policy/network fixtures; no real requests or root execution."""
import contextlib
import importlib.util
import json
import pathlib
import os
import pwd
import uuid
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('runtime_transport', ROOT / 'deploy/updater-transport.py')
u = importlib.util.module_from_spec(spec); spec.loader.exec_module(u)


class TransportTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(); self.addCleanup(temporary.cleanup)
        self.root = pathlib.Path(temporary.name)
        self.calls = []; self.installed = []
        self.policy = dict(format=1, enabled=True, repository='NehoraiHadad/yahad', channel='stable', checkHours=6)
        self.app = dict(enabled=True, mode='auto', dataDirectory='/data', appUser=pwd.getpwuid(os.getuid()).pw_name, operator={'familyId':'f','userId':'p'}, currentVersion='0.4.1', installVersionPin='0.4.1')
        self.before = dict(version='1.0.0', sha256='a'*64)
        self.after = dict(version='1.1.0', sha256='b'*64)
        self.journal = {'phase':'stable', 'active':self.before}
        self.status = {}; self.app_journal = {}; self.quiet = True; self.locked = False; self.fail_install = None
        fixture = self
        class Supervisor:
            state = fixture.root / 'state'
            config_path = fixture.root / 'config.json'
            def path(self, value): return fixture.root / str(value).lstrip('/')
            def read(self, path, limit, **kwargs):
                if path == self.path(u.POLICY):
                    if fixture.policy is None: raise FileNotFoundError()
                    return json.dumps(fixture.policy).encode()
                if path == self.config_path: return json.dumps(fixture.app).encode()
                if path.name == 'updater-transport.json':
                    if not fixture.status: raise FileNotFoundError()
                    return json.dumps(fixture.status).encode()
                raise AssertionError('Unexpected read')
            def atomic(self, path, raw):
                fixture.assertEqual(path, self.state / 'updater-transport.json')
                fixture.status = json.loads(raw)
            @contextlib.contextmanager
            def locked(self):
                fixture.assertFalse(fixture.locked)
                fixture.locked = True
                try: yield
                finally: fixture.locked = False
            def journal(self): return fixture.journal
            def app_status(self): return fixture.app_journal
            def require_quiet_app(self):
                if not fixture.quiet: raise ValueError('app_busy')
            def install_bundle(self, *payload, guard=None):
                fixture.assertFalse(fixture.locked, 'Public install_bundle takes its own single lock')
                with self.locked():
                    if guard: guard()
                    fixture.installed.append(payload)
                    if fixture.fail_install: raise ValueError(fixture.fail_install)
                    fixture.journal['active'] = fixture.after
                    return fixture.after
        self.supervisor = Supervisor()
        (self.root / 'data').mkdir()
        self.assets = [{'name': name, 'url': f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{index}'}
                       for index, name in enumerate(u.ASSETS, 1)]
        self.release = dict(tag_name='v0.4.1', draft=False, prerelease=False, assets=self.assets)
        self.payload = [b'manifest', b'signature', b'archive']
    def fetch(self, url, maximum, *args):
        self.calls.append((url, maximum, args))
        if url.endswith('/releases/latest'): return json.dumps(self.release).encode()
        return self.payload[int(url.rsplit('/', 1)[1])-1]
    def refresh(self, **kwargs):
        return u.refresh(self.supervisor, fetch=self.fetch, now=lambda:1000, **kwargs)

    def test_disabled_missing_notify_and_recovery_never_contact_provider_or_install(self):
        for command in ('requests', 'recover', 'boot-recover', 'unknown'):
            self.assertEqual(self.refresh(command=command), {'result':'skipped'})
        self.policy['enabled'] = False
        self.assertEqual(self.refresh(), {'result':'disabled'})
        self.policy['enabled'] = True; self.app['mode'] = 'notify'
        self.assertEqual(self.refresh(), {'result':'notify'})
        self.app['mode'] = 'auto'; self.app['enabled'] = False
        self.assertEqual(self.refresh(), {'result':'app_disabled'})
        self.policy = None
        self.assertEqual(self.refresh(), {'result':'not_configured'})
        self.assertEqual(self.calls, []); self.assertEqual(self.installed, []); self.assertEqual(self.status, {})

    def queue_install(self, **changes):
        request = dict(id=str(uuid.uuid4()), action='install', familyId='f', userId='p', createdAt=1000000)
        request.update(changes)
        path = self.root / 'data/update-request.json'
        path.write_text(json.dumps(request)); path.chmod(0o600)
        return path, request

    def test_notify_explicit_valid_install_can_upgrade_root_then_keeps_app_queue_intact(self):
        self.app['mode'] = 'notify'
        path, request = self.queue_install()
        self.assertEqual(self.refresh(command='requests')['result'], 'updated')
        self.assertEqual(self.installed, [tuple(self.payload)])
        self.assertEqual(json.loads(path.read_bytes()), request)
        self.assertEqual(self.app['mode'], 'notify')

    def test_notify_check_foreign_expired_malformed_or_public_queue_never_authorizes_root_install(self):
        self.app['mode'] = 'notify'
        for values in ({'action':'check'}, {'userId':'child'}, {'familyId':'other'}, {'createdAt':0},
                       {'createdAt':True}, {'id':'not-uuid'}, {'url':'https://evil.example/code'}):
            path, request = self.queue_install(**values)
            self.assertEqual(self.refresh(command='requests'), {'result':'skipped'})
            self.assertEqual(json.loads(path.read_bytes()), request)
        path, _ = self.queue_install(); path.chmod(0o644)
        self.assertEqual(self.refresh(command='requests'), {'result':'skipped'})
        self.assertEqual(self.installed, []); self.assertEqual(self.calls, [])

    def test_replayed_terminal_install_id_never_fetches_or_upgrades_root(self):
        self.app['mode'] = 'notify'
        path, request = self.queue_install()
        for key in ('lastRequest', 'lastModeRequest'):
            self.app_journal = {key: {'id':request['id'], 'state':'succeeded'}}
            self.assertEqual(self.refresh(command='requests'), {'result':'skipped'})
        self.app_journal = {'modeRequestSeen':[{'id':request['id'], 'at':1000000}]}
        self.assertEqual(self.refresh(command='requests'), {'result':'skipped'})
        self.assertEqual(self.calls, []); self.assertEqual(self.installed, [])
        self.assertEqual(json.loads(path.read_bytes()), request)

    def test_policy_and_intent_are_rechecked_inside_install_lock_after_network(self):
        self.app['mode'] = 'notify'
        path, request = self.queue_install()
        def fetch(url, *args):
            if url.endswith('/releases/assets/3'):
                path.write_text(json.dumps(dict(request, action='check')))
            return self.fetch(url, *args)
        self.assertEqual(u.refresh(self.supervisor, command='requests', fetch=fetch, now=lambda:1000)['result'], 'deferred')
        self.assertEqual(self.installed, [])
        self.status = {}; self.app['mode'] = 'auto'; path.unlink()
        def change_policy(url, *args):
            if url.endswith('/releases/assets/3'): self.policy['enabled'] = False
            return self.fetch(url, *args)
        self.assertEqual(u.refresh(self.supervisor, fetch=change_policy, now=lambda:1000)['result'], 'deferred')
        self.assertEqual(self.installed, [])

    def test_older_attempt_cannot_overwrite_newer_transport_outcome(self):
        def fetch(url, *args):
            if url.endswith('/releases/assets/3'):
                self.status.update(attempt='c'*32, checkedAt=2000000, result='unchanged')
            return self.fetch(url, *args)
        self.assertEqual(u.refresh(self.supervisor, fetch=fetch, now=lambda:1000), {'result':'superseded'})
        self.assertEqual(self.status['attempt'], 'c'*32)
        self.assertEqual(self.status['checkedAt'], 2000000)

    def test_same_app_version_and_app_pin_do_not_hide_new_signed_worker(self):
        original = dict(self.app)
        self.assertEqual(self.refresh()['result'], 'updated')
        self.assertEqual(self.installed, [tuple(self.payload)])
        self.assertEqual(self.app, original)
        self.assertEqual([item[1] for item in self.calls], [1024*1024, 8192, 64, 2*1024*1024])
        self.assertEqual(self.status['version'], '1.1.0')
        self.assertEqual(self.refresh(), {'result':'cached'})
        self.assertEqual(len(self.installed), 1)

    def test_optional_assets_absent_keep_existing_runtime_and_partial_is_rejected(self):
        self.release['assets'] = [{'name':'yachad-app.zip', 'url':'unused'}]
        self.assertEqual(self.refresh()['result'], 'unavailable')
        self.assertEqual(self.installed, [])
        self.status = {}; self.release['assets'] = self.assets[:1]
        self.assertEqual(self.refresh()['error'], 'invalid_release')
        self.assertEqual(self.installed, []); self.assertEqual(self.journal['active'], self.before)

    def test_no_install_when_busy_or_request_queued_and_queue_is_preserved(self):
        self.quiet = False
        self.assertEqual(self.refresh()['result'], 'deferred')
        self.assertEqual(self.calls, [])
        self.quiet = True; self.status = {}
        queue = self.root / 'data/update-request.json'; queue.write_text('synthetic queued request')
        self.assertEqual(self.refresh()['result'], 'deferred')
        self.assertEqual(self.calls, []); self.assertEqual(queue.read_text(), 'synthetic queued request')
        queue.unlink(); self.journal['phase'] = 'prepared'
        self.assertEqual(self.refresh()['result'], 'deferred')
        self.assertEqual(self.installed, [])

    def test_request_arriving_during_fetch_is_not_consumed_or_bypassed(self):
        def fetch(url, *args):
            if not url.endswith('/releases/latest'):
                (self.root / 'data/update-request.json').write_text('synthetic')
            return self.fetch(url, *args)
        result = u.refresh(self.supervisor, fetch=fetch, now=lambda:1000)
        self.assertEqual(result['result'], 'deferred'); self.assertEqual(self.installed, [])
        self.assertTrue((self.root / 'data/update-request.json').exists())

    def test_signature_protocol_replay_and_activation_failures_never_claim_update(self):
        for error in ('signature', 'protocol', 'downgrade', 'runtime', 'attacker text/private material'):
            self.status = {}; self.fail_install = error
            result = self.refresh()
            self.assertEqual(result['result'], 'failed')
            self.assertEqual(result['error'], error if error in u.ERRORS else 'activation')
            self.assertEqual(self.journal['active'], self.before)
            self.assertNotIn('attacker', json.dumps(result))

    def test_network_failure_keeps_active_runtime_and_sanitizes_error(self):
        result = u.refresh(self.supervisor, fetch=lambda *a:(_ for _ in ()).throw(u.TransportError('network')), now=lambda:1000)
        self.assertEqual(result['error'], 'network'); self.assertEqual(self.installed, [])
        self.assertEqual(self.journal['active'], self.before)

    def test_untrusted_release_cannot_select_external_or_other_repo_root_artifact(self):
        for url in ('https://evil.example/x', 'https://api.github.com/repos/other/repo/releases/assets/1',
                    'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/1?key=ignored'):
            self.release['assets'] = [dict(self.assets[0], url=url), *self.assets[1:]]
            with self.assertRaises(u.TransportError): u.release_assets(json.dumps(self.release).encode(), self.policy['repository'])
        for change in ({'draft':True}, {'prerelease':True}, {'tag_name':'updater-1.1.0'}, {'assets':self.assets+self.assets[:1]}):
            with self.assertRaises(u.TransportError): u.release_assets(json.dumps(dict(self.release, **change)).encode(), self.policy['repository'])

    def test_policy_does_not_accept_key_url_or_unbounded_values(self):
        for change in ({'enabled':1}, {'format':True}, {'repository':'../escape'}, {'channel':'branch'},
                       {'checkHours':0}, {'checkHours':True}, {'checkHours':169}, {'key':'app-key'}):
            with self.subTest(change=change):
                previous = self.policy; self.policy = dict(previous, **change)
                self.assertEqual(self.refresh(), {'result':'failed', 'error':'configuration'})
                self.policy = previous
        self.assertEqual(self.calls, []); self.assertEqual(self.installed, [])

    def test_https_hosts_redirects_and_duplicate_json_are_rejected(self):
        for url in ('http://github.com/x', 'https://evil.example/x', 'https://secret@api.github.com/x',
                    'https://github.com:8443/x', 'https://api.github.com/x#hidden'):
            with self.assertRaises(u.TransportError): u.safe_url(url)
        u.safe_url('https://release-assets.githubusercontent.com/x')
        with self.assertRaises(u.TransportError): u.strict_json(b'{"format":1,"format":2}')

if __name__ == '__main__': unittest.main()
