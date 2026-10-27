import contextlib, hashlib, importlib.util, io, json, pathlib, sqlite3, tempfile, types, unittest, os, stat, zipfile
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('guided_install',pathlib.Path(__file__).resolve().parents[1]/'deploy/install.py')
u=importlib.util.module_from_spec(spec);spec.loader.exec_module(u)

class System:
    def __init__(self):self.calls=[];self.fail=None
    def run(self,args,**kw):
        self.calls.append(args)
        if self.fail and self.fail(args):raise u.InstallError('injected interruption')
        return b''

class Worker:
    calls=[]
    result=b'BACKUP_OK'
    def __init__(self,c):self.c=c
    def health(self,v):self.calls.append(('health',v))
    def command(self,args,**kw):self.calls.append(('backup',args));return self.result
    def save(self,**kw):self.calls.append(('status',kw))

class GuidedInstallLogicTests(unittest.TestCase):
    """Pure fixture checks also run as an ordinary Linux development user."""
    def test_custom_request_paths_and_secret_boundary_are_derived_together(self):
        installer=u.Installer(System());writes={};directories=[]
        installer.directory=lambda path:directories.append(path)
        installer.write=lambda path,content:writes.update({path:content.decode()})
        config={'dataDirectory':'/var/lib/yachad/custom-live','backupPassphraseFile':'/etc/yachad/backup',
                'publicKeyFile':'/etc/yachad/signing.pub','tokenFile':'/etc/yachad/token'}
        env={'DATA_DIR':config['dataDirectory'],'MASTER_KEY_FILE':'/etc/yachad/custom.key'}
        installer.mode_control_boundary(config,env)
        boundary=writes['/etc/systemd/system/yachad-update-request.service.d/40-mode-control.conf']
        self.assertEqual(boundary,writes['/etc/systemd/system/yachad-update.service.d/40-mode-control.conf'])
        for secret in (u.ENV,env['MASTER_KEY_FILE'],config['backupPassphraseFile'],config['publicKeyFile'],config['tokenFile']):
            self.assertIn(secret,boundary)
        watch=writes['/etc/systemd/system/yachad-update-request.path.d/40-data-directory.conf']
        self.assertIn('PathChanged=\nPathExists=\n',watch)
        self.assertIn('PathExists=/var/lib/yachad/custom-live/update-request.wake',watch)
        self.assertIn('PathChanged=/var/lib/yachad/custom-live/update-request.json',watch)
        self.assertIn('/var/lib/yachad/custom-live/update-request.wake',writes['/etc/systemd/system/yachad-update-request.service.d/40-data-directory.conf'])
        writes.clear();directories.clear()
        with self.assertRaises(u.InstallError):installer.mode_control_boundary(dict(config,dataDirectory='/var/lib/yachad/other'),env)
        self.assertFalse(writes);self.assertFalse(directories)
    def test_only_exact_reviewed_crlf_helper_aliases_are_accepted(self):
        for name,(lf,crlf) in u.HISTORICAL_CRLF.items():
            expected=(u.HERE/name).read_bytes()
            self.assertEqual(hashlib.sha256(expected).hexdigest(),lf)
            historic=expected.replace(b'\n',b'\r\n')
            self.assertEqual(hashlib.sha256(historic).hexdigest(),crlf)
            root='/usr/local/lib/yachad-updater/' if name.endswith('.py') else '/etc/systemd/system/'
            self.assertTrue(u.same_managed_source(root+name,historic,expected))
            self.assertFalse(u.same_managed_source(root+name,historic+b'# changed',expected))
            self.assertFalse(u.same_managed_source(root+name,historic,expected+b'# new version'))
            self.assertFalse(u.same_managed_source('/unreviewed/'+name,historic,expected))
        current=(u.HERE/'updater.py').read_bytes()
        self.assertFalse(u.same_managed_source('/usr/local/lib/yachad-updater/updater.py',current.replace(b'\n',b'\r\n'),current))
    def test_effective_updater_guard_rejects_override_and_stale_loaded_units(self):
        config={'dataDirectory':'/var/lib/yachad/live','backupPassphraseFile':'/etc/yachad/backup'}
        env={'DATA_DIR':config['dataDirectory'],'MASTER_KEY_FILE':'/etc/yachad/key'}
        system=System();installer=u.Installer(system);installer.verify_managed=lambda files:None
        def output(args,**kwargs):
            unit=args[2];drops=[p for p in installer.boundary_files(config,env) if '/'+unit+'.d/' in p]
            return ('LoadState=loaded\nFragmentPath=/etc/systemd/system/'+unit+'\nDropInPaths='+' '.join(drops)+'\nNeedDaemonReload=no\n').encode()
        system.run=output;installer.verify_effective_updates(config,env)
        for old,new in ((b'NeedDaemonReload=no',b'NeedDaemonReload=yes'),
                        (b'FragmentPath=/etc/',b'FragmentPath=/run/'),
                        (b'DropInPaths=',b'DropInPaths=/run/systemd/system/service.d/override.conf ')):
            system.run=lambda args,old=old,new=new:output(args).replace(old,new)
            with self.assertRaisesRegex(u.InstallError,'Effective updater unit'):installer.verify_effective_updates(config,env)
    def test_unfinished_status_is_not_silently_marked_healthy(self):
        with tempfile.TemporaryDirectory() as tmp:
            installer=u.Installer(System(),pathlib.Path(tmp));installer.secure=lambda path:None
            p=installer.p('/state/status.json');p.parent.mkdir()
            for phase in ('preparing','rollback_pending_health','recovery_required','checking','unexpected'):
                p.write_text(json.dumps({'phase':phase}))
                with self.assertRaises(u.InstallError):installer.verify_update_state({'stateDirectory':'/state'})
            p.write_text('{"phase":"healthy"}');installer.verify_update_state({'stateDirectory':'/state'})
    def test_cancel_has_distinct_failure_contract_without_side_effects(self):
        system=System();installer=u.Installer(system,ask=lambda _: '')
        installer.load=lambda:None;installer.prerequisites=lambda:('/node','/npm')
        installer.paths=lambda _:({},False)
        with self.assertRaises(u.InstallCanceled):installer.run()
        self.assertFalse(system.calls)
    def test_environment_parser_and_paths(self):
        self.assertEqual(u.environment('DATA_DIR=/var/lib/yachad/custom\nCONNECTOR=baileys'),
                         {'DATA_DIR':'/var/lib/yachad/custom','CONNECTOR':'baileys'})
        for text in ('DATA_DIR=/a\nDATA_DIR=/b','export DATA_DIR=/a','EXAMPLE=$(touch /secret)'):
            with self.assertRaises(u.InstallError):u.environment(text)
        for data in ('/var/lib/yachad-other/data','/var/lib/yachad/../escape'):
            with self.assertRaises(u.InstallError):u.safe_paths(data,'/etc/yachad/key')
        self.assertEqual(u.origin('https://192.168.1.2'),'https://192.168.1.2')
        for value in ('http://192.168.1.2','https://8.8.8.8','https://127.0.0.1','https://192.168.1.2/path'):
            with self.assertRaises(u.InstallError):u.origin(value)
    def test_existing_environment_accepts_only_private_or_correct_service_group(self):
        installer=u.Installer(System())
        def path(mode,uid=0,gid=1001):
            return types.SimpleNamespace(lstat=lambda:types.SimpleNamespace(st_mode=stat.S_IFREG|mode,st_uid=uid,st_gid=gid))
        with patch.object(u.pwd,'getpwnam',return_value=types.SimpleNamespace(pw_gid=1001)):
            installer.secure_environment(path(0o600,gid=0))
            installer.secure_environment(path(0o640))
            for item in (path(0o640,gid=1002),path(0o644),path(0o660),path(0o600,uid=1001)):
                with self.assertRaises(u.InstallError):installer.secure_environment(item)
            # Accepting the historical environment must not relax secret checks.
            with self.assertRaises(u.InstallError):installer.secure(path(0o640),mode=0o600)
    @unittest.skipUnless(os.name=='posix','Linux account lookup')
    def test_existing_accounts_cannot_retain_supplementary_or_wrong_groups(self):
        user=types.SimpleNamespace(pw_uid=1001,pw_gid=1001,pw_shell='/usr/sbin/nologin')
        with patch.object(u.pwd,'getpwnam',return_value=user), patch('grp.getgrnam',return_value=types.SimpleNamespace(gr_gid=1001)):
            with patch.object(u.os,'getgrouplist',return_value=[1001]):
                self.assertIs(u.Linux().account('yachad-update'),user)
            for groups in ([1001,0],[1001,1002]):
                with patch.object(u.os,'getgrouplist',return_value=groups):
                    with self.assertRaises(u.InstallError):u.Linux().account('yachad-update')
            with patch.object(u.os,'getgrouplist',return_value=[1001]), patch('grp.getgrnam',return_value=types.SimpleNamespace(gr_gid=1002)):
                with self.assertRaises(u.InstallError):u.Linux().account('yachad-update')
    def test_app_and_builder_cannot_share_uid_or_primary_gid(self):
        for app,builder in ((types.SimpleNamespace(pw_uid=1001,pw_gid=1001),types.SimpleNamespace(pw_uid=1001,pw_gid=1002)),
                            (types.SimpleNamespace(pw_uid=1001,pw_gid=1001),types.SimpleNamespace(pw_uid=1002,pw_gid=1001))):
            system=System();system.account=lambda name:app if name=='yachad' else builder
            installer=u.Installer(system,ask=lambda _:'y');installer.load=lambda:None
            installer.prerequisites=lambda:('/usr/bin/node','/usr/bin/npm');installer.paths=lambda _:({},False)
            with self.assertRaisesRegex(u.InstallError,'separate users and groups'):installer.run()
            self.assertFalse(system.calls)
    def test_interrupted_backup_starts_only_its_stopped_matching_service(self):
        for service_state in (b'inactive',b'failed',b'active'):
            system=System();installer=u.Installer(system)
            installer.receipt={'backupStarting':True};installer.active=lambda _:'0.4.0'
            installer.note=lambda **kw:installer.receipt.update(kw)
            def run(args,**kw):
                system.calls.append(args)
                return service_state if 'show' in args else b''
            system.run=run
            with patch.object(u.worker,'Worker',Worker):installer.resume_backup({}, {}, '0.4.0')
            self.assertEqual(any('start' in call for call in system.calls),service_state!=b'active')
            self.assertFalse(any('restart' in call for call in system.calls))
            self.assertFalse(installer.receipt['backupStarting'])
    def test_interrupted_backup_rejects_changed_release_or_transitioning_service(self):
        system=System();installer=u.Installer(system);installer.receipt={'backupStarting':True}
        installer.active=lambda _:'0.4.1'
        with self.assertRaises(u.InstallError):installer.resume_backup({}, {}, '0.4.0')
        self.assertFalse(system.calls)
        installer.active=lambda _:'0.4.0';system.run=lambda args:b'deactivating'
        with self.assertRaises(u.InstallError):installer.resume_backup({}, {}, '0.4.0')
        self.assertTrue(installer.receipt['backupStarting'])

@unittest.skipUnless(os.name=='posix' and pathlib.Path('/proc/self/fd').is_dir(),'Linux orchestration fixtures')
class GuidedInstallRunTests(unittest.TestCase):
    """Real temporary files/flock/SQLite; synthetic ownership, services and I/O.

    These are end-to-end installer orchestration tests, NOT root/systemd, npm,
    Caddy, TLS handshake or backup/restore integration acceptance.
    """
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=pathlib.Path(self.temp.name);self.root.chmod(0o700)
        self.stack=contextlib.ExitStack();self.addCleanup(self.stack.close)
        self.owners={};self.calls=[];self.enabled=set();self.active=set();self.https_calls=[]
        self.clock=0;self.fail_https=False;self.create_parent=True
        self.accounts={name:types.SimpleNamespace(pw_uid=uid,pw_gid=uid,pw_shell='/usr/sbin/nologin')
                       for name,uid in [('yachad',1001),('yachad-update',1002),('caddy',1003)]}
        original_lstat=pathlib.Path.lstat;original_fstat=os.fstat
        def fixture_path(path):
            path=pathlib.Path(path)
            if not path.is_relative_to(self.root):raise AssertionError('Mutation outside fixture: '+str(path))
            return path
        def with_owner(info,path):
            if not path.is_relative_to(self.root):return info
            fields=list(info);fields[4:6]=self.owners.get(path,(0,0));return os.stat_result(fields)
        def fd_path(fd):return pathlib.Path(os.readlink('/proc/self/fd/'+str(fd)))
        self.stack.enter_context(patch.object(pathlib.Path,'lstat',lambda p:with_owner(original_lstat(p),p)))
        self.stack.enter_context(patch.object(os,'fstat',lambda fd:with_owner(original_fstat(fd),fd_path(fd))))
        self.stack.enter_context(patch.object(os,'chown',lambda p,uid,gid,**kw:self.owners.update({fixture_path(p):(uid,gid)})))
        self.stack.enter_context(patch.object(os,'fchown',lambda fd,uid,gid:self.owners.update({fixture_path(fd_path(fd)):(uid,gid)})))
        self.stack.enter_context(patch.object(u.pwd,'getpwnam',lambda name:self.accounts[name]))
        self.stack.enter_context(patch.object(u.shutil,'which',lambda name:'/usr/bin/'+name))
        self.stack.enter_context(patch.object(u.time,'monotonic',lambda:self.clock))
        for name in ('etc/systemd/system','etc/caddy','usr/local/lib','opt','var/lib','var/backups'):
            self.p('/'+name).mkdir(parents=True,exist_ok=True)
        ca=self.p('/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt')
        ca.parent.mkdir(parents=True);ca.write_bytes(b'synthetic-public-CA');ca.chmod(0o644)
        self.owners[ca]=(1003,1003)
        context=u.ssl.SSLContext(u.ssl.PROTOCOL_TLS_CLIENT)
        def ssl_context(*,cafile):
            self.assertEqual(cafile,str(ca));self.assertTrue(context.check_hostname)
            self.assertEqual(context.verify_mode,u.ssl.CERT_REQUIRED);return context
        self.stack.enter_context(patch.object(u.ssl,'create_default_context',ssl_context))
        def build_opener(*handlers):
            self.assertEqual(next(h for h in handlers if isinstance(h,u.urllib.request.ProxyHandler)).proxies,{})
            self.assertIsNone(next(h for h in handlers if isinstance(h,u.urllib.request.HTTPRedirectHandler)).redirect_request(None,None,None,None,None,None))
            self.assertIs(next(h for h in handlers if isinstance(h,u.urllib.request.HTTPSHandler))._context,context)
            return types.SimpleNamespace(open=self.https_open)
        self.stack.enter_context(patch.object(u.urllib.request,'build_opener',build_opener))
        self.system=types.SimpleNamespace(account=lambda name:self.accounts[name],run=self.run_command)
        self.installer=u.Installer(self.system,self.root,ask=lambda _:'y',sleep=self.sleep)
        self.build_archive()
        original_worker=u.worker.Worker;fixture=self
        class FixtureWorker:
            def __init__(self,config):
                self.c=config;self.group=1001;self.status_path=fixture.p(config['stateDirectory'])/'status.json'
                self.status=json.loads(self.status_path.read_bytes()) if self.status_path.exists() else {}
            def release(self,token):
                return {'tag_name':'v0.4.0'},{name:{'url':'https://github.com/synthetic/'+name} for name in fixture.assets}
            def health(self,version):
                fixture.assertIn('yachad.service',fixture.active)
                fixture.assertEqual(json.loads((fixture.p('/opt/yachad/current')/'package.json').read_bytes())['version'],version)
                with sqlite3.connect(fixture.database()) as db:
                    fixture.assertEqual(db.execute('SELECT version FROM schema_version').fetchone(),(1,))
            def command(self,args,**kwargs):
                fixture.calls.append(args)
                if 'ci' in args:return b''
                if 'scripts/update-smoke.ts' in args:return b''
                if '/usr/local/lib/yachad-updater/export-dependencies.py' in args:return fixture.dependencies
                if 'scripts/update-backup.ts' in args:
                    fixture.assertNotIn('yachad.service',fixture.active)
                    fixture.assertIn('--verify',args)
                    fixture.p(self.c['backupDirectory']+'/yachad-123.backup').write_bytes(b'synthetic-encrypted-backup')
                    return b'BACKUP_OK'
                raise AssertionError('Unexpected worker command: '+repr(args))
            save=original_worker.save
        self.stack.enter_context(patch.object(u.worker,'Worker',FixtureWorker))
        self.stack.enter_context(patch.object(u.worker,'download',lambda url,*args:self.assets[url.rsplit('/',1)[1]]))
    def p(self,path):return self.root/path.lstrip('/')
    def build_archive(self):
        stream=io.BytesIO()
        files={'package.json':json.dumps({'version':'0.4.0'}),'package-lock.json':'{}','dist/index.html':'synthetic UI',
               'src/main.ts':'','src/bootstrap.ts':'','scripts/update-smoke.ts':'','scripts/update-backup.ts':''}
        with zipfile.ZipFile(stream,'w') as archive:
            for name,value in files.items():archive.writestr(name,value)
        archive=stream.getvalue();manifest={'format':1,'version':'0.4.0','databaseSchema':1,'archive':'yachad-app.zip',
                                         'bytes':len(archive),'sha256':hashlib.sha256(archive).hexdigest()}
        self.assets={'yachad-app.zip':archive,'yachad-release.json':json.dumps(manifest).encode()}
        stream=io.BytesIO()
        with zipfile.ZipFile(stream,'w') as archive:
            entry=zipfile.ZipInfo('node_modules/synthetic/index.js');entry.external_attr=(stat.S_IFREG|0o644)<<16
            archive.writestr(entry,'synthetic dependency')
        self.dependencies=stream.getvalue()
    def database(self):
        env=u.environment(self.p(u.ENV).read_text());return self.p(env['DATA_DIR'])/'app.sqlite'
    def seed_database(self,parent=False):
        path=self.database()
        with sqlite3.connect(path) as db:
            db.executescript('CREATE TABLE IF NOT EXISTS schema_version(version INTEGER); CREATE TABLE IF NOT EXISTS users(family_id TEXT,id TEXT,role TEXT);')
            if not db.execute('SELECT 1 FROM schema_version').fetchone():db.execute('INSERT INTO schema_version VALUES(1)')
            if parent and not db.execute('SELECT 1 FROM users').fetchone():db.execute("INSERT INTO users VALUES('family','parent','parent')")
        self.owners[path]=(1001,1001)
    def run_command(self,args,**kwargs):
        self.calls.append(args)
        if args==['/usr/bin/cat','/proc/1/comm']:return b'systemd\n'
        if args==['/usr/bin/node','--version']:return b'v24.0.0\n'
        if args[0] in ('ffmpeg','openssl','caddy'):return b'synthetic version'
        self.assertEqual(args[0],'/usr/bin/systemctl')
        action=args[1];unit=args[-1]
        if action=='daemon-reload':return b''
        if action=='show':
            if '--property=LoadState,FragmentPath,DropInPaths,NeedDaemonReload' in args:
                target=args[2];fragment='/etc/systemd/system/'+target
                if not self.p(fragment).exists():return b'LoadState=not-found\n'
                drops=sorted(str(p.relative_to(self.root)) for p in self.p(fragment+'.d').glob('*.conf'))
                return ('LoadState=loaded\nFragmentPath='+fragment+'\nDropInPaths='+' '.join('/'+p for p in drops)+'\nNeedDaemonReload=no\n').encode()
            if '--value' in args:return b'active' if 'yachad.service' in self.active else b'inactive'
            self.assertTrue(self.p('/etc/systemd/system/yachad.service.d/50-guided-install.conf').exists())
            return (b'User=yachad\nGroup=yachad\nWorkingDirectory=/opt/yachad/current\n'
                    b'ExecStart={ argv[]=/usr/bin/python3 /usr/local/lib/yachad-updater/data-lock.py ${DATA_DIR} /usr/bin/env node --import tsx src/main.ts ; }\n'
                    b'EnvironmentFiles=/etc/yachad/yachad.env (ignore_errors=no)\n'
                    b'Requires=yachad-update-recover.service\nAfter=yachad-update-recover.service\n')
        if action=='stop':self.active.discard(unit);return b''
        if action in ('enable','start','restart'):
            if action=='enable':self.enabled.add(unit)
            if unit=='yachad-update-recover.service':
                with self.p('/var/lib/yachad-updates/worker.lock').open('a') as lock:
                    u.fcntl.flock(lock,u.fcntl.LOCK_EX|u.fcntl.LOCK_NB)
            if unit=='yachad.service':self.seed_database()
            self.active.add(unit);return b''
        if action in ('is-active','is-enabled'):
            if unit not in (self.active if action=='is-active' else self.enabled):raise u.InstallError('Synthetic inactive service')
            return b''
        raise AssertionError('Unexpected system command: '+repr(args))
    def https_open(self,url,timeout):
        self.https_calls.append(url);self.assertEqual(url,'https://192.168.1.23/api/setup/status')
        if self.fail_https:raise u.ssl.SSLCertVerificationError('synthetic TLS rejection')
        with sqlite3.connect(self.database()) as db:complete=bool(db.execute('SELECT 1 FROM users').fetchone())
        state='complete' if complete else 'ready' if self.p('/etc/yachad/setup-capability.json').exists() else 'closed'
        return contextlib.closing(types.SimpleNamespace(status=200,read=lambda n:json.dumps({'state':state}).encode(),close=lambda:None))
    def sleep(self,seconds):
        self.clock+=seconds
        if self.create_parent and not self.fail_https and self.p('/etc/yachad/setup-capability.json').exists():self.seed_database(parent=True)
    def install(self,origin='https://192.168.1.23'):
        with contextlib.redirect_stdout(io.StringIO()) as output:self.installer.run(origin)
        return output.getvalue()
    def test_fresh_run_completes_only_after_https_parent_backup_and_timers(self):
        output=self.install();config=json.loads(self.p(u.CONFIG).read_bytes())
        self.assertIn('COMPLETE:',output);self.assertTrue(self.installer.receipt['complete'])
        self.assertEqual(config['operator'],{'familyId':'family','userId':'parent'})
        self.assertEqual(config['mode'],'auto');self.assertTrue(config['enabled']);self.assertTrue(config['allowUiModeControl'])
        self.assertEqual(len(self.https_calls),2)
        self.assertTrue(self.p('/var/backups/yachad-updates/yachad-123.backup').exists())
        self.assertTrue({'yachad.service','yachad-update-request.path','yachad-update.timer','yachad-update-backup-expiry.timer'}<=self.enabled)
        watch=self.p('/etc/systemd/system/yachad-update-request.path.d/40-data-directory.conf').read_text()
        self.assertIn('PathChanged='+config['dataDirectory']+'/update-request.json',watch)
        self.assertIn('PathExists='+config['dataDirectory']+'/update-request.wake',watch)
        latch=self.p('/etc/systemd/system/yachad-update-request.service.d/40-data-directory.conf').read_text()
        self.assertIn('ExecStartPre=/usr/bin/rm -f -- '+config['dataDirectory']+'/update-request.wake',latch)
        first=self.p('/etc/systemd/system/yachad-update.service.d/40-mode-control.conf').read_bytes()
        self.assertEqual(first,self.p('/etc/systemd/system/yachad-update-request.service.d/40-mode-control.conf').read_bytes())
    def test_existing_run_preserves_0640_environment_custom_store_and_key(self):
        env={'DATA_DIR':'/var/lib/yachad/custom-live','MASTER_KEY_FILE':'/etc/yachad/custom.key','APP_ORIGIN':'https://192.168.1.23','CONNECTOR':'baileys'}
        for path,mode,owner in (('/etc/yachad',0o750,(0,1001)),('/var/lib/yachad',0o755,(0,1001)),
                                (env['DATA_DIR'],0o700,(1001,1001)),('/opt/yachad',0o750,(0,1001)),('/opt/yachad/releases',0o750,(0,1001))):
            self.p(path).mkdir(mode=mode);self.owners[self.p(path)]=owner
        text=''.join(k+'='+v+'\n' for k,v in env.items());self.p(u.ENV).write_text(text);self.p(u.ENV).chmod(0o640);self.owners[self.p(u.ENV)]=(0,1001)
        key=self.p(env['MASTER_KEY_FILE']);key.write_bytes(b'synthetic-existing-key');key.chmod(0o600);self.owners[key]=(1001,1001)
        release=self.p('/opt/yachad/releases/v0.4.0');release.mkdir();(release/'package.json').write_text('{"version":"0.4.0"}')
        self.p('/opt/yachad/current').symlink_to(release);self.seed_database(parent=True);self.active.add('yachad.service')
        output=self.install(None)
        self.assertIn('COMPLETE:',output);self.assertEqual(self.p(u.ENV).read_text(),text);self.assertEqual(key.read_bytes(),b'synthetic-existing-key')
        self.assertEqual(stat.S_IMODE(self.p(u.ENV).stat().st_mode),0o640);self.assertFalse(self.https_calls)
        self.assertFalse(self.p('/etc/caddy/yachad.Caddyfile').exists());self.assertFalse(self.p('/var/lib/yachad/store').exists())
    def test_fresh_run_without_valid_https_never_enables_updates_or_reports_complete(self):
        self.fail_https=True
        with contextlib.redirect_stdout(io.StringIO()) as output:
            with self.assertRaisesRegex(u.InstallError,'HTTPS readiness failed'):self.installer.run('https://192.168.1.23')
        self.assertNotIn('COMPLETE:',output.getvalue());self.assertNotIn('private setup link',output.getvalue())
        self.assertFalse(self.installer.receipt.get('complete'));self.assertNotIn('yachad-update.timer',self.enabled)
        self.assertNotIn('yachad-update-request.path',self.enabled)
        self.assertFalse(self.p(u.CONFIG).exists())
    def test_missing_local_ca_cannot_fall_back_to_insecure_https(self):
        self.p('/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt').unlink()
        with contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(u.InstallError,'HTTPS readiness failed'):self.installer.run('https://192.168.1.23')
        self.assertFalse(self.https_calls);self.assertNotIn('yachad-update.timer',self.enabled)
        self.assertFalse(self.installer.receipt.get('complete'))
    def test_https_lost_after_parent_creation_blocks_final_auto_activation(self):
        def sleep_then_lose_https(seconds):
            self.sleep(seconds);self.fail_https=True
        self.installer.sleep=sleep_then_lose_https
        with contextlib.redirect_stdout(io.StringIO()) as output:
            with self.assertRaisesRegex(u.InstallError,'HTTPS readiness failed'):self.installer.run('https://192.168.1.23')
        self.assertIn('private setup link',output.getvalue());self.assertNotIn('COMPLETE:',output.getvalue())
        self.assertTrue(self.installer.receipt['backupVerified']);self.assertFalse(self.installer.receipt.get('complete'))
        self.assertNotIn('yachad-update.timer',self.enabled);self.assertFalse(self.p(u.CONFIG).exists())
    def test_completed_fresh_rerun_checks_https_and_preserves_notify_preference(self):
        self.install();config=json.loads(self.p(u.CONFIG).read_bytes());config['mode']='notify'
        u.worker.atomic_json(self.p(u.CONFIG),config)
        before=sum('scripts/update-backup.ts' in call for call in self.calls)
        secret=self.p(config['backupPassphraseFile']).read_bytes()
        self.installer=u.Installer(self.system,self.root,ask=lambda _:'y',sleep=self.sleep)
        output=self.install(None)
        self.assertIn('ALREADY_COMPLETE:',output);self.assertEqual(json.loads(self.p(u.CONFIG).read_bytes())['mode'],'notify')
        self.assertEqual(self.p(config['backupPassphraseFile']).read_bytes(),secret)
        self.assertEqual(sum('scripts/update-backup.ts' in call for call in self.calls),before)
        self.assertEqual(len(self.https_calls),3)

    def test_existing_mixed_crlf_install_preserves_bytes_policy_and_pin(self):
        self.install();env=u.environment(self.p(u.ENV).read_text())
        original={}
        for name in u.HISTORICAL_CRLF:
            root='/usr/local/lib/yachad-updater/' if name.endswith('.py') else '/etc/systemd/system/'
            p=self.p(root+name);p.write_bytes((u.HERE/name).read_bytes().replace(b'\n',b'\r\n'));original[p]=p.read_bytes()
        config=json.loads(self.p(u.CONFIG).read_bytes());config.update(mode='notify',enabled=False,allowUiModeControl=False,installVersionPin='0.4.0')
        u.worker.atomic_json(self.p(u.CONFIG),config)
        u.worker.atomic_json(self.p(u.RECEIPT),{'newInstall':False,'integrationHealthy':True})
        self.installer=u.Installer(self.system,self.root,ask=lambda _:'y',sleep=self.sleep)
        before=len(self.calls);output=self.install(None)
        self.assertIn('existing update policy',output)
        after=json.loads(self.p(u.CONFIG).read_bytes())
        for key in ('mode','enabled','allowUiModeControl','installVersionPin'):self.assertEqual(config[key],after[key])
        for p,raw in original.items():self.assertEqual(p.read_bytes(),raw)
        self.assertFalse(any(call[1]=='enable' for call in self.calls[before:] if call[0]=='/usr/bin/systemctl'))
    def test_completed_rerun_reports_disabled_path_without_reenabling(self):
        self.install();self.enabled.discard('yachad-update-request.path')
        self.installer=u.Installer(self.system,self.root,ask=lambda _:'y',sleep=self.sleep)
        before=len(self.calls)
        with self.assertRaisesRegex(u.InstallError,'preference preserved, owner repair'):self.install(None)
        self.assertFalse(any(call[1]=='enable' for call in self.calls[before:] if call[0]=='/usr/bin/systemctl'))
    def test_completed_rerun_rejects_changed_worker_without_modifying_it(self):
        self.install();p=self.p('/usr/local/lib/yachad-updater/updater.py');p.write_bytes(b'unknown worker')
        self.installer=u.Installer(self.system,self.root,ask=lambda _:'y',sleep=self.sleep)
        with self.assertRaisesRegex(u.InstallError,'reviewed helper migration'):self.install(None)
        self.assertEqual(p.read_bytes(),b'unknown worker')

@unittest.skipUnless(hasattr(os,'geteuid') and os.geteuid()==0,'isolated Linux ownership fixtures require root')
class GuidedInstallTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name);self.root.chmod(0o700)
        self.s=System();self.i=u.Installer(self.s,self.root);self.i.app=types.SimpleNamespace(pw_uid=1001,pw_gid=1001)
        for d in ('etc/yachad','var/lib/yachad-updates','var/backups/yachad-updates'):(self.root/d).mkdir(parents=True,exist_ok=True)
        self.op={'familyId':'f','userId':'p'};self.i.parent=lambda env:self.op;self.i.verify_effective_updates=lambda *args,**kwargs:None
        self.env={'DATA_DIR':'/var/lib/yachad/custom-live','MASTER_KEY_FILE':'/etc/yachad/custom.key'}
        self.c=json.loads((u.HERE/'updates.json.example').read_bytes());self.c.update(dataDirectory=self.env['DATA_DIR'],masterKeyFile=self.env['MASTER_KEY_FILE'])
        self.i.p(self.c['backupPassphraseFile']).write_bytes(b'synthetic local backup phrase only');self.i.p(self.c['backupPassphraseFile']).chmod(0o600)
        Worker.calls=[];Worker.result=b'BACKUP_OK';self.mock=patch.object(u.worker,'Worker',Worker);self.mock.start()
    def tearDown(self):self.mock.stop();self.temp.cleanup()
    def test_environment_preserves_custom_live_and_key(self):
        env=dict(self.env,APP_ORIGIN='https://192.168.1.23',CONNECTOR='baileys')
        p=self.i.p(u.ENV);p.write_text(''.join(k+'='+v+'\n' for k,v in env.items()));p.chmod(0o600)
        actual,fresh=self.i.paths();self.assertFalse(fresh);self.assertEqual(actual,env)
        with self.assertRaises(u.InstallError):u.safe_paths('/var/lib/yachad-other/data','/etc/yachad/key')
        with self.assertRaises(u.InstallError):u.safe_paths('/var/lib/yachad/../escape','/etc/yachad/key')
    def test_duplicate_env_and_shell_syntax_not_executed(self):
        with self.assertRaises(u.InstallError):u.environment('DATA_DIR=/a\nDATA_DIR=/b')
        with self.assertRaises(u.InstallError):u.environment('export DATA_DIR=/a')
        with self.assertRaises(u.InstallError):u.environment('EXAMPLE=$(touch /secret)')
    def test_only_lan_https_origin(self):
        self.assertEqual(u.origin('https://192.168.1.2'),'https://192.168.1.2')
        for value in ['http://192.168.1.2','https://8.8.8.8','https://127.0.0.1','https://192.168.1.2/path','https://user@192.168.1.2']:
            with self.assertRaises(u.InstallError):u.origin(value)
    def test_managed_files_refuse_symlink_conflict_and_keep_key(self):
        p=self.i.p('/etc/yachad/existing');self.i.write('/etc/yachad/existing',b'original',mode=0o600)
        with self.assertRaises(u.InstallError):self.i.write('/etc/yachad/existing',b'new',mode=0o600)
        self.assertEqual(p.read_bytes(),b'original')
        link=self.i.p('/etc/yachad/alias');link.symlink_to(p)
        with self.assertRaises(u.InstallError):self.i.write('/etc/yachad/alias',b'original',mode=0o600)
    def test_signature_configuration_retained_without_policy_downgrade(self):
        self.i.c=self.c;self.c.update(releaseTrust='signature',publicKeyFile='/etc/yachad/release-signing.pub',tokenFile=None)
        p=self.i.p(u.CONFIG);p.write_text(json.dumps(self.c));p.chmod(0o600)
        actual=self.i.config(self.env,self.c['node'],self.c['npm'])
        self.assertEqual(actual,self.c);self.assertEqual(actual['releaseTrust'],'signature')
    def test_success_checks_backup_restore_and_health_before_auto(self):
        self.i.receipt['newInstall']=True
        self.i.finish(self.c,self.env,'0.4.0')
        c=json.loads(self.i.p(u.CONFIG).read_bytes());self.assertTrue(c['enabled']);self.assertEqual(c['mode'],'auto');self.assertTrue(c['allowUiModeControl'])
        self.assertTrue(self.i.receipt['backupVerified']);self.assertTrue(self.i.receipt['complete'])
        call=next(x for x in Worker.calls if x[0]=='backup');self.assertEqual(call[1][-1],'--verify')
        self.assertIn(['/usr/bin/systemctl','start','yachad.service'],self.s.calls)
        for timer in ('yachad-update.timer','yachad-update-backup-expiry.timer'):
            self.assertIn(['/usr/bin/systemctl','is-active','--quiet',timer],self.s.calls)
    def test_backup_failure_restores_service_and_never_enables_timers(self):
        Worker.result=b'not verified'
        with self.assertRaises(u.InstallError):self.i.finish(self.c,self.env,'0.4.0')
        self.assertIn(['/usr/bin/systemctl','start','yachad.service'],self.s.calls)
        self.assertFalse(self.i.receipt.get('backupVerified'));self.assertFalse(any('enable' in c for c in self.s.calls))
    def test_changed_config_and_operator_preserved(self):
        p=self.i.p(u.CONFIG);p.write_text('{}');p.chmod(0o600);self.i.receipt['configHash']='changed'
        with self.assertRaises(u.InstallError):self.i.finish(self.c,self.env,'0.4.0')
        self.assertEqual(p.read_bytes(),b'{}');self.assertFalse(self.s.calls)
        self.i.receipt={};self.c['operator']={'familyId':'f','userId':'other'}
        with self.assertRaises(u.InstallError):self.i.finish(self.c,self.env,'0.4.0')
    def test_mode_control_boundary_protects_secret_and_existing_env(self):
        (self.root/'etc/systemd/system').mkdir(parents=True)
        self.i.mode_control_boundary(self.c,self.env)
        content=self.i.p('/etc/systemd/system/yachad-update.service.d/40-mode-control.conf').read_text()
        self.assertIn('ReadWritePaths=/etc/yachad',content)
        self.assertIn(self.env['MASTER_KEY_FILE'],content);self.assertIn(u.ENV,content)
        self.assertIn(self.c['backupPassphraseFile'],content)
    def test_recovery_unit_acquires_real_lock_while_installer_yields(self):
        path=self.i.p('/var/lib/yachad-updates/worker.lock')
        fd=os.open(path,os.O_RDWR|os.O_CREAT,0o600)
        self.i.receipt['newInstall']=True
        acquired=[]
        def start(args,**kw):
            if args[1]=='start':
                other=os.open(path,os.O_RDWR)
                try:
                    u.fcntl.flock(other,u.fcntl.LOCK_EX|u.fcntl.LOCK_NB);acquired.append(True)
                finally:os.close(other)
            return b''
        self.s.run=start
        try:
            u.fcntl.flock(fd,u.fcntl.LOCK_EX|u.fcntl.LOCK_NB)
            self.i.ensure_boot_guard(self.c,self.env,'0.4.0',fd)
            self.assertEqual(acquired,[True])
            other=os.open(path,os.O_RDWR)
            try:
                with self.assertRaises(BlockingIOError):u.fcntl.flock(other,u.fcntl.LOCK_EX|u.fcntl.LOCK_NB)
            finally:os.close(other)
        finally:os.close(fd)
    def test_cancel_does_not_create_accounts_files_or_services(self):
        i=u.Installer(self.s,self.root,ask=lambda _: '')
        i.prerequisites=lambda:('/usr/bin/node','/usr/bin/npm');i.fetch_fresh=lambda:None
        with self.assertRaises(u.InstallCanceled):i.run('https://192.168.1.2')
        self.assertFalse(self.s.calls);self.assertFalse(i.p(u.RECEIPT).exists());self.assertFalse(i.p(u.ENV).exists())
    def test_unpublished_fresh_release_fails_before_provisioning(self):
        i=u.Installer(self.s,self.root,ask=lambda _: 'y')
        i.prerequisites=lambda:('/usr/bin/node','/usr/bin/npm')
        i.fetch_fresh=lambda:(_ for _ in ()).throw(u.InstallError('unpublished'))
        with self.assertRaises(u.InstallError):i.run('https://192.168.1.2')
        self.assertFalse(self.s.calls);self.assertFalse(i.p(u.ENV).exists())
    def test_corrupt_receipt_and_symlink_rejected(self):
        p=self.i.p(u.RECEIPT);p.write_text('not-json');p.chmod(0o600)
        with self.assertRaises(ValueError):self.i.load()
        p.unlink();p.symlink_to(self.i.p(self.c['backupPassphraseFile']))
        with self.assertRaises(u.InstallError):self.i.load()
    def test_partial_timer_activation_resumes_without_duplicate_backup(self):
        self.i.receipt['newInstall']=True
        self.s.fail=lambda args:'enable' in args and 'yachad-update.timer' in args
        with self.assertRaises(u.InstallError):self.i.finish(self.c,self.env,'0.4.0')
        self.assertTrue(self.i.receipt['autoStarting']);self.assertFalse(self.i.receipt.get('complete'))
        backups=len([x for x in Worker.calls if x[0]=='backup']);self.s.fail=None
        self.i.finish(json.loads(self.i.p(u.CONFIG).read_bytes()),self.env,'0.4.0')
        self.assertEqual(len([x for x in Worker.calls if x[0]=='backup']),backups);self.assertTrue(self.i.receipt['complete'])

if __name__=='__main__':unittest.main()
