"""Real crypto/archive verification; service/build/network effects injected in lifecycle tests."""
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import pwd
import subprocess
import tempfile
import time
import unittest
import zipfile
from unittest.mock import patch, Mock

spec = importlib.util.spec_from_file_location('updater', pathlib.Path(__file__).parents[1] / 'deploy/updater.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class UpdaterTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='yachad-update-test-')
        self.root = pathlib.Path(self.temp.name)
        self.key = self.root / 'signing.pem'
        self.pub = self.root / 'signing.pub'
        subprocess.run(['openssl','genpkey','-algorithm','ED25519','-out',str(self.key)],check=True,capture_output=True)
        subprocess.run(['openssl','pkey','-in',str(self.key),'-pubout','-out',str(self.pub)],check=True,capture_output=True)

    def tearDown(self):
        self.temp.cleanup()

    def package(self, extra=None):
        out = io.BytesIO()
        with zipfile.ZipFile(out,'w') as z:
            for name in ['package-lock.json','dist/index.html','src/main.ts','scripts/update-smoke.ts','scripts/update-backup.ts']:
                z.writestr(name,'test fixture')
            z.writestr('package.json',json.dumps({'version':'0.2.0'}))
            if extra:
                # Preserve malicious backslashes on Windows too.
                info = zipfile.ZipInfo(extra[0])
                info.filename = extra[0]
                z.writestr(info, extra[1])
        archive = out.getvalue()
        manifest = json.dumps({'format':1,'version':'0.2.0','databaseSchema':1,'archive':u.ASSETS[2],
                               'bytes':len(archive),'sha256':hashlib.sha256(archive).hexdigest()}).encode()
        (self.root/'manifest').write_bytes(manifest)
        subprocess.run(['openssl','pkeyutl','-sign','-inkey',str(self.key),'-rawin','-in',str(self.root/'manifest'),
                        '-out',str(self.root/'sig')],check=True,capture_output=True)
        return manifest,(self.root/'sig').read_bytes(),archive

    def test_signatures_hash_and_unsafe_archives(self):
        m,s,a = self.package()
        self.assertEqual(u.verify_release(m,s,a,self.pub,self.root)['version'],'0.2.0')
        for payload in [(m+b' ',s,a),(m,s[:-1]+bytes([s[-1]^1]),a),(m,s,a+b'changed')]:
            with self.assertRaises(u.UpdateError):
                u.verify_release(*payload,self.pub,self.root)
        for filename in ['../escape','/absolute','src/../../escape','C:/escape','.env','src\\escape']:
            _,_,bad = self.package((filename,'bad'))
            archive=self.root/'bad.zip'; archive.write_bytes(bad)
            target=self.root/'extract'; target.mkdir(exist_ok=True)
            with self.assertRaises(u.UpdateError):
                u.extract(archive,target)
            import shutil
            shutil.rmtree(target)
        self.assertFalse((self.root.parent/'escape').exists())

    def test_request_authentication_and_url_policy(self):
        username=pwd.getpwuid(os.getuid()).pw_name
        c={'stateDirectory':str(self.root),'dataDirectory':str(self.root),'appUser':username,'operator':{'familyId':'a','userId':'p'}}
        worker=u.Worker(c)
        path=self.root/'update-request.json'
        base={'action':'install','familyId':'a','userId':'p','createdAt':int(time.time()*1000)}
        for change in [{'familyId':'b'},{'userId':'child'},{'action':'shell'},{'createdAt':0}]:
            path.write_text(json.dumps({**base,**change})); path.chmod(0o600)
            self.assertIsNone(worker.request())
        path.write_text(json.dumps(base)); path.chmod(0o600); self.assertEqual(worker.request(),'install')
        for url in ['http://github.com/x','https://evil.example/x','https://github.com:8443/x','https://secret@api.github.com/x']:
            with self.assertRaises(u.UpdateError): u.safe_url(url)
        u.safe_url('https://release-assets.githubusercontent.com/asset')
        req=u.urllib.request.Request('https://api.github.com/asset',headers={'Authorization':'Bearer PRIVATE'})
        redirected=u.Redirect().redirect_request(req,None,302,'',{},'https://release-assets.githubusercontent.com/asset')
        self.assertNotIn('Authorization',redirected.headers)

    @unittest.skipUnless(os.getuid()==0,'Root-owned worker configuration exercised only under root in temp directory')
    def test_another_worker_cannot_acquire_the_same_lock(self):
        import fcntl
        username=pwd.getpwuid(os.getuid()).pw_name
        config=self.root/'config.json'
        config.write_text(json.dumps({'enabled':False,'mode':'notify','operator':{'familyId':'a','userId':'p'},
                                     'appUser':username,'stateDirectory':str(self.root)}))
        config.chmod(0o600)
        with (self.root/'worker.lock').open('a') as lock:
            fcntl.flock(lock,fcntl.LOCK_EX | fcntl.LOCK_NB)
            with patch.object(u,'CONFIG',config):
                u.main()
            self.assertFalse((self.root/'status.json').exists())

    def test_fractional_hour_direct_schedule_checks_at_five_minutes(self):
        worker = object.__new__(u.Worker)
        worker.c = dict(enabled=True, mode='notify', checkHours=1/12, repository='owner/repo')
        worker.status = dict(phase='idle', checkedAt=1000000)
        worker.finish_request = Mock()
        worker.recover = Mock(return_value=True)
        worker.request = Mock(return_value=None)
        worker.save = lambda **values: worker.status.update(values)
        (self.root / 'package.json').write_text('{"version":"0.2.0"}')
        worker.current = Mock(return_value=self.root)
        with patch.object(worker, 'release', return_value=({'tag_name':'v0.2.0'}, {})) as release:
            with patch.object(u.time, 'time', return_value=1299.999): worker.run()
            release.assert_not_called()
            with patch.object(u.time, 'time', return_value=1300): worker.run()
            release.assert_called_once()
        self.assertEqual(worker.status['phase'], 'idle')

    def test_scheduled_no_selection_keeps_controls_live_without_claiming_app_check(self):
        for selection in (None, {'error':'network'}):
            with self.subTest(selection=selection):
                worker = object.__new__(u.Worker)
                worker.c = dict(enabled=True, mode='auto', checkHours=1/12,
                                operator={'familyId':'family', 'userId':'parent'})
                worker.status = dict(phase='healthy', checkedAt=1000, updatedAt=1000,
                                     currentVersion='0.5.1', error=None)
                worker.group = 0
                worker.status_path = self.root / 'status.json'
                worker.require_release_selection = True
                worker.release_selection = selection
                worker.finish_request = Mock()
                worker.recover = Mock(return_value=True)
                worker.request = Mock(return_value=None)
                worker.release = Mock()
                with patch.object(u, 'atomic_json') as saved, patch.object(u.time, 'time', return_value=100000):
                    worker.run()
                saved.assert_called_once()
                worker.release.assert_not_called()
                self.assertEqual(worker.status['updatedAt'], 100000000)
                self.assertEqual(worker.status['checkedAt'], 1000)
                self.assertEqual(worker.status['phase'], 'healthy')
                self.assertIsNone(worker.status['error'])

    def lifecycle(self, fail_health=False, fail_backup=False, https=False):
        username=pwd.getpwuid(os.getuid()).pw_name
        releases=self.root/'releases'; releases.mkdir()
        old=releases/'v0.1.0'; old.mkdir(); (old/'package.json').write_text('{"version":"0.1.0"}')
        current=self.root/'current'; current.symlink_to(old)
        state=self.root/'state'; state.mkdir()
        build=self.root/'build'; build.mkdir()
        data=self.root/'data'; data.mkdir()
        secret=self.root/'backup-secret'; secret.write_text('UNIQUE-LOCAL-ENCRYPTED-BACKUP-PASSWORD'); secret.chmod(0o600)
        c={'enabled':True,'repository':'owner/repo','mode':'auto','operator':{'familyId':'a','userId':'p'},'checkHours':6,
           'stateDirectory':str(state),'buildDirectory':str(build),'dataDirectory':str(data),'masterKeyFile':str(self.root/'master'),
           'backupDirectory':str(self.root/'backups'),'backupPassphraseFile':str(secret),'appUser':username,'builderUser':username,
           'publicKeyFile':str(self.pub),'currentLink':str(current),'releasesDirectory':str(releases),'minimumFreeMiB':0,
           'service':'yachad.service','node':'node','npm':'npm','tokenFile':None}
        effects=[]
        if https:
            c.update(repository=u.PUBLIC_REPOSITORY, releaseTrust='github-https', publicKeyFile=None)
        class FakeWorker(u.Worker):
            def command(self,args,**kwargs):
                effects.append((args,kwargs))
                if '/usr/local/lib/yachad-updater/export-dependencies.py' in args:
                    out = io.BytesIO()
                    with zipfile.ZipFile(out, 'w') as archive:
                        info = zipfile.ZipInfo('node_modules/fixture/package.json')
                        info.external_attr = 0o100644 << 16
                        archive.writestr(info, '{}')
                    return out.getvalue()
                if fail_backup and 'scripts/update-backup.ts' in args:
                    raise u.UpdateError('backup')
                if 'scripts/update-backup.ts' in args:
                    directory=pathlib.Path(c['backupDirectory']);directory.mkdir(exist_ok=True)
                    backup=directory/'yachad-12345.backup';backup.write_bytes(b'synthetic encrypted fixture');backup.chmod(0o600)
                return b'OK'
            def schema(self): return 1
            def health(self,expected=None,expected_schema=None):
                effects.append(('health',expected))
                if fail_health and expected=='0.2.0': raise u.UpdateError('health')
        worker=FakeWorker(c)
        m,s,a=self.package()
        remote=dict(zip(u.ASSETS,[m,s,a]))
        assets={name:{'url':name} for name in u.ASSETS}
        with patch.object(worker,'release',return_value=({'tag_name':'v0.2.0'},assets)), patch.object(u,'download',side_effect=lambda url,*args:remote[url]) as fetched:
            worker.run()
        if https:
            self.assertNotIn('yachad-release.sig', [call.args[0] for call in fetched.call_args_list])
        if fail_health or fail_backup:
            self.assertEqual(current.resolve(),old)
            self.assertEqual(worker.status['phase'],'rolled_back')
        else:
            self.assertEqual(current.resolve(),releases/'v0.2.0')
            self.assertEqual(worker.status['phase'],'healthy')
        commands=[x[0] for x in effects if isinstance(x[0],list)]
        self.assertLess(next(i for i,x in enumerate(commands) if 'scripts/update-backup.ts' in x),next(i for i,x in enumerate(commands) if x[:2]==['/usr/bin/systemctl','start']))
        self.assertTrue(any('--ignore-scripts' in x for x in commands))
        self.assertNotIn('UNIQUE-LOCAL',json.dumps(worker.status))
        return worker,effects

    @unittest.skipUnless(os.getuid()==0,'Root ownership lifecycle simulated in isolated temp tree only')
    def test_success_backup_precedes_activation(self): self.lifecycle()

    @unittest.skipUnless(os.getuid()==0,'Root ownership lifecycle simulated in isolated temp tree only')
    def test_failed_health_restores_previous_code(self): self.lifecycle(fail_health=True)

    @unittest.skipUnless(os.getuid()==0,'Root ownership lifecycle simulated in isolated temp tree only')
    def test_backup_failure_never_activates_new_code(self): self.lifecycle(fail_backup=True)

    @unittest.skipUnless(os.getuid()==0,'Root ownership lifecycle simulated in isolated temp tree only')
    def test_https_success_still_backs_up_without_fetching_signature(self): self.lifecycle(https=True)

    @unittest.skipUnless(os.getuid()==0,'Root ownership lifecycle simulated in isolated temp tree only')
    def test_https_failed_health_still_rolls_back(self): self.lifecycle(fail_health=True,https=True)

    @unittest.skipUnless(os.getuid()==0,'Root ownership lifecycle simulated in isolated temp tree only')
    def test_https_backup_failure_still_never_activates(self): self.lifecycle(fail_backup=True,https=True)

    @unittest.skipUnless(os.getuid()==0,'Root ownership recovery simulated in isolated temp tree only')
    def test_interrupted_maintenance_recovers(self):
        worker,effects=self.lifecycle()
        old=self.root/'releases/v0.1.0'
        worker.save(phase='checking_health',currentVersion='0.1.0',previousPath=str(old))
        worker.recover()
        self.assertEqual((self.root/'current').resolve(),old)
        self.assertEqual(worker.status['phase'],'rolled_back')


if __name__=='__main__': unittest.main()
