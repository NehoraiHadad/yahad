import contextlib, importlib.util, io, os, pathlib, stat, tempfile, types, unittest, sqlite3, hashlib, shutil, json, zipfile
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('installer',pathlib.Path(__file__).parents[1]/'deploy/install-updater.py')
u=importlib.util.module_from_spec(spec);spec.loader.exec_module(u)

class FixtureFiles(u.Files):
    def __init__(self,root): self.root=root
    def ancestors(self,path):
        for parent in (path,*path.parents):
            if parent==self.root.parent: break
            if parent.exists() or parent.is_symlink():
                info=self.info(parent)
                if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0 or info.st_mode&0o022: raise ValueError('Unsafe fixture ancestor')

class System:
    def __init__(self,root):
        self.files=FixtureFiles(root);self.commands=[];self.healths=[]
        self.app=types.SimpleNamespace(pw_uid=1001,pw_gid=1001)
        self.owner=types.SimpleNamespace(pw_name='synthetic-owner');self.home=root/'owner'
    def health(self,version): self.healths.append(version)
    def run(self,args,**options): self.commands.append((args,options));return b''

class GateGuide(u.Guide):
    def note(self,**updates):self.receipt.update(updates)
    def set_mode(self,enabled,mode):self.s.commands.append((['mode',enabled,mode],{}))
    def backup_drill(self):self.s.commands.append((['drill'],{}));self.note(backupVerified=True,exportPath='/owner/encrypted.backup')
    def first_install(self):self.s.commands.append((['manual-install'],{}));self.note(manualHealthy=True)

@unittest.skipUnless(os.geteuid()==0,'POSIX root ownership tested only inside temporary Linux fixtures')
class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.tmp.name);self.root.chmod(0o700)
        self.system=System(self.root)
    def tearDown(self):self.tmp.cleanup()
    def guide(self,answers):
        replies=iter(answers);g=GateGuide(self.system,self.root,ask=lambda _:next(replies));g.receipt={'prepared':True};return g
    def test_umask_does_not_make_public_helpers_unreadable(self):
        previous=os.umask(0o077)
        try:self.system.files.install(self.root/'helper.py',b'public',0,0,0o644)
        finally:os.umask(previous)
        self.assertEqual(stat.S_IMODE((self.root/'helper.py').stat().st_mode),0o644)
        self.system.files.install(self.root/'helper.py',b'public',0,0,0o644)
    def test_existing_conflict_and_symlink_are_never_overwritten(self):
        target=self.root/'managed';self.system.files.install(target,b'first',0,0,0o644)
        with self.assertRaises(ValueError):self.system.files.install(target,b'second',0,0,0o644)
        self.assertEqual(target.read_bytes(),b'first')
        link=self.root/'link';link.symlink_to(target)
        with self.assertRaises(ValueError):self.system.files.install(link,b'first',0,0,0o644)
    def test_privileged_parent_symlink_refused(self):
        folder=self.root/'folder';folder.mkdir();link=self.root/'alias';link.symlink_to(folder,target_is_directory=True)
        with self.assertRaises(ValueError):self.system.files.install(link/'file',b'no',0,0,0o644)
        self.assertFalse((folder/'file').exists())
    def test_fifo_input_refused_without_blocking(self):
        path=self.root/'fifo';os.mkfifo(path,0o600)
        with self.assertRaises(ValueError):u.regular_bytes(path,8192)
    def test_default_confirmation_stops_before_service_changes(self):
        self.guide(['']).continue_guided();self.assertEqual(self.system.commands,[])
    def test_offdevice_gate_prevents_install_and_auto(self):
        g=self.guide(['MAINTENANCE','']);g.continue_guided()
        self.assertEqual([x[0] for x in self.system.commands],[['drill']]);self.assertFalse(g.receipt.get('autoEnabled',False))
    def test_acceptance_gate_leaves_auto_off(self):
        g=self.guide(['MAINTENANCE','COPIED','']);g.continue_guided()
        self.assertFalse(any(x[0][0]=='mode' for x in self.system.commands));self.assertTrue(g.receipt['manualHealthy'])
    def test_all_gates_enable_auto_and_verify_both_timers(self):
        g=self.guide(['MAINTENANCE','COPIED','ACCEPTED']);g.continue_guided()
        self.assertTrue(g.receipt['autoEnabled']);self.assertIn((['mode',True,'auto'],{}),self.system.commands)
        for name in ('yachad-update.timer','yachad-update-backup-expiry.timer'):
            self.assertTrue(any(c[0]==['/usr/bin/systemctl','is-enabled','--quiet',name] for c in self.system.commands))
    def test_missing_drill_receipt_cannot_be_replaced_by_owner_acceptance(self):
        g=self.guide(['MAINTENANCE','COPIED','ACCEPTED']);g.backup_drill=lambda:None
        g.receipt['exportPath']='/synthetic'
        with self.assertRaises(ValueError):g.continue_guided()
        self.assertFalse(any(x[0][0]=='mode' for x in self.system.commands))
    def test_backup_failure_cannot_enable_auto(self):
        g=self.guide(['MAINTENANCE']);g.backup_drill=lambda:(_ for _ in ()).throw(ValueError('synthetic'))
        with self.assertRaises(ValueError):g.continue_guided()
        self.assertEqual(self.system.commands,[])
    def test_stop_timeout_still_requests_original_service_start(self):
        g=u.Guide(self.system,self.root)
        for name in (u.DATA,u.BACKUPS):g.p(name).mkdir(parents=True)
        g.p(u.SECRET).parent.mkdir(parents=True);g.p(u.SECRET).write_bytes(b'synthetic backup phrase 123456')
        def fail_stop(args,**kwargs):
            self.system.commands.append((args,kwargs))
            if args[1]=='stop':raise ValueError('uncertain stop timeout')
            return b''
        self.system.run=fail_stop
        with self.assertRaises(ValueError):g.backup_drill()
        self.assertTrue(any(c[0]==['/usr/bin/systemctl','start','yachad.service'] for c in self.system.commands))
    def test_secret_is_hidden_and_requires_explicit_escrow(self):
        value='synthetic escrow phrase 12345';answers=iter([value,value]);printed=io.StringIO()
        g=u.Guide(self.system,self.root,ask=lambda _:'SAVED',hidden=lambda _:next(answers))
        with contextlib.redirect_stdout(printed):self.assertEqual(g.passphrase(),value.encode())
        self.assertNotIn(value,printed.getvalue())
    def test_installer_embeds_exact_reviewed_stage_source(self):
        self.assertEqual(u.hashlib.sha256(u.source).hexdigest(),u.STAGE_SHA)
        self.assertEqual(u.verifier.REPOSITORY,'NehoraiHadad/yahad')
        self.assertEqual(set(u.verifier.ASSETS),{'yachad-release.json','yachad-app.zip'})
    def test_draft_delivery_cannot_provision_obsolete_worker(self):
        with patch.object(u,'Linux',side_effect=AssertionError('No platform/provisioning access allowed')):
            with contextlib.redirect_stdout(io.StringIO()) as output:u.main()
        self.assertIn('DRAFT_ONLY',output.getvalue())
    def test_site_override_blocks_restart_after_reload(self):
        g=self.guide([]);g.p('/etc/systemd/system').mkdir(parents=True);g.p(u.STATE).mkdir(parents=True)
        g.p(u.STATE+'/pending-app-integration.conf').write_text('[Service]\nExecStart=\nExecStart=/signed/guard\n')
        def run(args,**options):
            self.system.commands.append((args,options))
            return b'ExecStart={ argv[]=/usr/bin/env node --import tsx src/main.ts ; }\nRequires=yachad-update-recover.service\nAfter=yachad-update-recover.service\n' if 'show' in args else b''
        self.system.run=run
        with self.assertRaisesRegex(ValueError,'Effective service'):u.Guide.first_install(g)
        self.assertFalse(any(any(x in c[0] for x in ('restart','start')) for c in self.system.commands))
    def test_backup_export_streams_immutable_snapshot_and_never_root_opens_app_path(self):
        g=self.guide([])
        for name in (u.DATA,u.BACKUPS,u.STATE):g.p(name).mkdir(parents=True)
        g.p(u.SECRET).parent.mkdir(parents=True);g.p(u.SECRET).write_bytes(b'synthetic backup phrase 123456')
        source=g.p(u.BACKUPS+'/yachad-123.backup');original_open=pathlib.Path.open;exported=[]
        class NoRead:
            def __init__(self,file):self.file=file
            def read(self,*args):raise AssertionError('Guide must not buffer the backup')
            def __enter__(self):return self
            def __exit__(self,*args):self.file.close()
            def fileno(self):return self.file.fileno()
        def opened(path,mode='r',*args,**kwargs):
            if path==source and mode=='rb':raise AssertionError('Root must not open app-controlled source')
            file=original_open(path,mode,*args,**kwargs)
            return NoRead(file) if path.name=='archive.backup' and mode=='rb' else file
        def run(args,**options):
            self.system.commands.append((args,options))
            if 'scripts/update-backup.ts' in args:
                source.write_bytes(b'encrypted fixture');return b'BACKUP_OK\n'
            if options.get('stdout_file') is not None:
                self.assertEqual(options['user'],'yachad');options['stdout_file'].write(b'authenticated snapshot fixture');return b''
            if 'tempfile.mkdtemp' in ' '.join(args):return (u.BACKUPS+'/restore-drill-fixture\n').encode()
            if '/usr/local/bin/node' in args:return b'RESTORE_OK\n'
            if options.get('stdin_file') is not None:
                self.assertEqual(options['user'],'synthetic-owner');self.assertIsNone(options.get('input'));exported.append(options['stdin_file']);return b''
            return b''
        self.system.run=run
        with patch.object(pathlib.Path,'open',opened):u.Guide.backup_drill(g)
        self.assertEqual(len(exported),1);self.assertTrue(g.receipt['backupVerified']);self.assertFalse(any(g.p(u.STATE).glob('drill-input-*')))
    def test_https_preparation_is_idempotent_preserves_live_fixture_and_pauses(self,pin=False):
        home=self.root/'owner';home.mkdir();self.system.home=home
        fixtures=self.root/'fixture-release';fixtures.mkdir()
        source=pathlib.Path(__file__).parents[1]
        with zipfile.ZipFile(fixtures/'yachad-app.zip','w')as archive:
            archive.writestr('package.json',json.dumps({'version':u.verifier.VERSION}))
            for name in ('dist/index.html','src/main.ts','scripts/update-backup.ts','scripts/backup-lib.ts'):archive.writestr(name,'synthetic test fixture; never executed')
            for name in ('updater.py','data-lock.py','export-dependencies.py','updates.json.example','update-recovery.conf','yachad.service',*u.UNITS):archive.write(source/'deploy'/name,'deploy/'+name)
        content=(fixtures/'yachad-app.zip').read_bytes()
        (fixtures/'yachad-release.json').write_text(json.dumps({'format':1,'version':u.verifier.VERSION,'databaseSchema':1,'archive':'yachad-app.zip','bytes':len(content),'sha256':hashlib.sha256(content).hexdigest()}))
        assets=[{'name':name,'url':f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{i+1}','size':(fixtures/name).stat().st_size}for i,name in enumerate(u.verifier.ASSETS)]
        metadata=json.dumps({'tag_name':'v'+u.verifier.VERSION,'draft':False,'prerelease':False,'assets':assets}).encode()
        responses={a['url']:(fixtures/a['name']).read_bytes()for a in assets}
        responses[f'https://api.github.com/repos/NehoraiHadad/yahad/releases/tags/v{u.verifier.VERSION}']=metadata
        for name in ('/usr/local/lib','/var/backups','/etc/systemd/system','/opt/yachad/releases/v0.2.0','/etc/yachad'):
            (self.root/name.lstrip('/')).mkdir(parents=True,exist_ok=True)
        (self.root/'opt/yachad/current').symlink_to(self.root/'opt/yachad/releases/v0.2.0',target_is_directory=True)
        key=self.root/u.KEY.lstrip('/');key.write_bytes(b'EXISTING_SYNTHETIC_DATA_KEY');os.chown(key,1001,1001);key.chmod(0o600)
        env=self.root/'etc/yachad/yachad.env';env.write_text('DATA_DIR='+u.DATA+'\nMASTER_KEY_FILE='+u.KEY+'\nCONNECTOR=baileys\n')
        data=self.root/u.DATA.lstrip('/');data.mkdir(parents=True);os.chown(data,1001,1001);data.chmod(0o700)
        with sqlite3.connect(data/'app.sqlite') as db:
            db.executescript("CREATE TABLE schema_version(version INTEGER);INSERT INTO schema_version VALUES(1);CREATE TABLE users(family_id TEXT,id TEXT,username TEXT,role TEXT);INSERT INTO users VALUES('f','p','parent','parent');")
        original={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in (key,env,data/'app.sqlite')}
        self.system.builder=lambda create=False:types.SimpleNamespace(pw_uid=1002,pw_gid=1002) if create else None
        def run(args,**options):
            self.system.commands.append((args,options))
            return (b'User=yachad\nGroup=yachad\nWorkingDirectory=/opt/yachad/current\nExecStart={ argv[]=/usr/bin/env node --import tsx src/main.ts ; }\nEnvironmentFiles=/etc/yachad/yachad.env (ignore_errors=no)\n'
                    if 'yachad.service' in args else b'LoadState=not-found\nActiveState=inactive\nUnitFileState=\n')
        self.system.run=run
        if pin:(self.root/'etc/yachad/release-signing.pub').write_bytes(b'EXISTING_PUBLIC_PIN')
        phrase='synthetic escrow phrase 12345';entries=iter([phrase,phrase]);guide=u.Guide(self.system,self.root,ask=lambda prompt:'SAVED' if 'SAVED' in prompt else '',hidden=lambda _:next(entries))
        with tempfile.TemporaryDirectory() as work, contextlib.redirect_stdout(io.StringIO()),patch.object(u.verifier,'download',side_effect=lambda url,*_:responses[url]):
            release,public=guide.verified_release(pathlib.Path(work))
            if pin:
                with self.assertRaisesRegex(ValueError,'Signature-pinned'):guide.prepare(release,public)
                self.assertEqual((self.root/'etc/yachad/release-signing.pub').read_bytes(),b'EXISTING_PUBLIC_PIN');return
            guide.prepare(release,public);guide.continue_guided();guide.prepare(release,public)
        self.assertEqual({str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in (key,env,data/'app.sqlite')},original)
        config=json.loads((self.root/u.CONFIG.lstrip('/')).read_text());self.assertFalse(config['enabled']);self.assertFalse(config['allowUiModeControl'])
        self.assertEqual(config['dataDirectory'],u.DATA);self.assertEqual(config['masterKeyFile'],u.KEY)
        self.assertEqual(config['installVersionPin'],u.verifier.VERSION)
        self.assertEqual(config['releaseTrust'],'github-https');self.assertIsNone(config['publicKeyFile']);self.assertIsNone(config['tokenFile'])
        self.assertFalse((self.root/'etc/yachad/release-signing.pub').exists())
        self.assertEqual(stat.S_IMODE((self.root/u.HELPERS.lstrip('/')/'data-lock.py').stat().st_mode),0o644)
        self.assertFalse(any(any(action in command for action in ('start','stop','restart','daemon-reload','enable')) for command,_ in self.system.commands))
    def test_existing_signature_pin_is_preserved_and_blocks_https_setup(self):
        self.test_https_preparation_is_idempotent_preserves_live_fixture_and_pauses(pin=True)
    def test_https_staging_rejects_foreign_assets_size_and_hash_inconsistency(self):
        archive=b'synthetic archive'
        manifest=json.dumps({'format':1,'version':u.verifier.VERSION,'databaseSchema':1,'archive':'yachad-app.zip','bytes':len(archive),'sha256':hashlib.sha256(archive).hexdigest()}).encode()
        base=[{'name':'yachad-release.json','url':'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/1','size':len(manifest)}, {'name':'yachad-app.zip','url':'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/2','size':len(archive)}]
        for problem in ('foreign','size','hash'):
            assets=[dict(x)for x in base]
            if problem=='foreign':assets[1]['url']='https://api.github.com/repos/other/repo/releases/assets/2'
            if problem=='size':assets[1]['size']+=1
            payload=archive if problem!='hash'else b'changed archive!!'
            if problem=='hash':assets[1]['size']=len(payload)
            metadata=json.dumps({'tag_name':'v'+u.verifier.VERSION,'draft':False,'prerelease':False,'assets':assets}).encode()
            def download(url,*args):
                if '/tags/'in url:return metadata
                return manifest if url.endswith('/1')else payload
            with tempfile.TemporaryDirectory()as work,patch.object(u.verifier,'download',side_effect=download):
                with self.assertRaises(ValueError):u.verifier.fetch_release(pathlib.Path(work))
    def test_cleanup_failure_cannot_skip_original_service_restart(self):
        with patch.object(u.shutil,'rmtree',side_effect=OSError('synthetic cleanup failure')):
            with self.assertRaises(OSError):self.test_backup_export_streams_immutable_snapshot_and_never_root_opens_app_path()
        self.assertTrue(any(c[0]==['/usr/bin/systemctl','start','yachad.service'] for c in self.system.commands))
    def test_root_auto_method_itself_requires_acceptance_before_granting_ui_and_clearing_pin(self):
        g=u.Guide(self.system,self.root);g.p('/etc/yachad').mkdir(parents=True)
        config={'enabled':False,'mode':'notify','allowUiModeControl':False,'installVersionPin':'0.3.2','preserve':'ROOT_PATHS'}
        self.system.files.install(g.p(u.CONFIG),json.dumps(config).encode(),0,0,0o600)
        with self.assertRaises(ValueError):g.set_mode(True,'auto')
        self.assertEqual(json.loads(g.p(u.CONFIG).read_text()),config)
        g.receipt={name:True for name in ('backupVerified','offDeviceBackup','manualHealthy','ownerAcceptance')};g.set_mode(True,'auto')
        result=json.loads(g.p(u.CONFIG).read_text());self.assertTrue(result['allowUiModeControl']);self.assertIsNone(result['installVersionPin']);self.assertEqual(result['preserve'],'ROOT_PATHS')

if __name__=='__main__':unittest.main()
