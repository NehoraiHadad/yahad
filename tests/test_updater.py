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
from unittest.mock import patch

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
                z.writestr(*extra)
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
            path.write_text(json.dumps({**base,**change}))
            self.assertIsNone(worker.request())
        path.write_text(json.dumps(base)); self.assertEqual(worker.request(),'install')
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

    def lifecycle(self, fail_health=False, fail_backup=False):
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
        class FakeWorker(u.Worker):
            def command(self,args,**kwargs):
                effects.append((args,kwargs))
                if fail_backup and 'scripts/update-backup.ts' in args:
                    raise u.UpdateError('backup')
                return b'OK'
            def schema(self): return 1
            def health(self,expected=None):
                effects.append(('health',expected))
                if fail_health and expected=='0.2.0': raise u.UpdateError('health')
        worker=FakeWorker(c)
        m,s,a=self.package()
        remote=dict(zip(u.ASSETS,[m,s,a]))
        assets={name:{'url':name} for name in u.ASSETS}
        with patch.object(worker,'release',return_value=({'tag_name':'v0.2.0'},assets)), patch.object(u,'download',side_effect=lambda url,*args:remote[url]):
            worker.run()
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

    @unittest.skipUnless(os.getuid()==0,'Root ownership recovery simulated in isolated temp tree only')
    def test_interrupted_maintenance_recovers(self):
        worker,effects=self.lifecycle()
        old=self.root/'releases/v0.1.0'
        worker.save(phase='checking_health',currentVersion='0.1.0',previousPath=str(old))
        worker.recover()
        self.assertEqual((self.root/'current').resolve(),old)
        self.assertEqual(worker.status['phase'],'rolled_back')


if __name__=='__main__': unittest.main()
