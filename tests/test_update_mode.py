import importlib.util,json,os,pathlib,pwd,tempfile,time,unittest,uuid
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('mode_worker',pathlib.Path(__file__).parents[1]/'deploy/updater.py')
u=importlib.util.module_from_spec(spec);spec.loader.exec_module(u)

@unittest.skipUnless(os.geteuid()==0,'Root-owned temporary config fixture requires Linux root')
class ModeTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.tmp.name)
        (self.root/'data').mkdir();(self.root/'state').mkdir();self.config_path=self.root/'config.json'
        self.config={'enabled':True,'mode':'notify','allowUiModeControl':True,'operator':{'familyId':'family','userId':'parent'},'appUser':pwd.getpwuid(os.getuid()).pw_name,'dataDirectory':str(self.root/'data'),'stateDirectory':str(self.root/'state'),'repository':'NehoraiHadad/yahad','publicKeyFile':'PRESERVE_KEY_PATH'}
        self.config_path.write_text(json.dumps(self.config));self.config_path.chmod(0o600)
        self.config_patch=patch.object(u,'CONFIG',self.config_path);self.config_patch.start()
        class Worker(u.Worker):
            def recover(self,boot=False):return not getattr(self,'recovery_blocked',False)
            def release(self,token):raise AssertionError('Mode request must not fetch or install a release')
        self.Worker=Worker;self.worker=Worker(dict(self.config))
    def tearDown(self):self.config_patch.stop();self.tmp.cleanup()
    def queue(self,**patches):
        value={'id':str(uuid.uuid4()),'action':'set_mode','mode':'auto','familyId':'family','userId':'parent','createdAt':int(time.time()*1000)};value.update(patches)
        self.path=self.root/'data/update-request.json';self.path.write_text(json.dumps(value));self.path.chmod(0o600);return value
    def fresh(self):return self.Worker(json.loads(self.config_path.read_text()))
    def test_mode_only_write_preserves_root_fields_and_acknowledges(self):
        request=self.queue();self.worker.run();expected=dict(self.config,mode='auto')
        self.assertEqual(json.loads(self.config_path.read_text()),expected);self.assertEqual(self.config_path.stat().st_mode&0o777,0o600)
        self.assertEqual(self.worker.status['lastModeRequest'],{'id':request['id'],'mode':'auto','state':'applied','error':None});self.assertFalse(self.path.exists())
    def test_local_gate_rejects_and_disabled_gate_cannot_be_bypassed(self):
        self.worker.c['allowUiModeControl']=False;self.queue();self.worker.run()
        self.assertEqual(self.worker.status['lastModeRequest']['error'],'local_setup');self.assertEqual(json.loads(self.config_path.read_text())['mode'],'notify')
        self.worker.c['enabled']=False;self.queue();self.worker.run();self.assertTrue(self.path.exists())
    def test_invalid_foreign_future_and_nonfinite_requests_do_not_change_mode(self):
        for patch_value in ({'familyId':'other'},{'userId':'child'},{'mode':'off'},{'createdAt':True},{'createdAt':float('nan')},{'createdAt':float('inf')},{'createdAt':time.time()*1000+700000},{'repository':'attacker/repo'}):
            self.queue(**patch_value);self.assertIn(self.worker.request(),(None,'set_mode'));self.worker.apply_mode_request()
            self.assertEqual(json.loads(self.config_path.read_text())['mode'],'notify')
    def test_expired_and_replayed_are_rejected(self):
        request=self.queue(createdAt=time.time()*1000-700000);self.worker.run();self.assertEqual(self.worker.status['lastModeRequest']['error'],'expired')
        request=self.queue();self.worker.run();self.assertEqual(self.worker.status['lastModeRequest']['state'],'applied')
        self.queue(**request);self.worker.run();self.assertEqual(self.worker.status['lastModeRequest']['error'],'replayed')
    def test_intent_failure_leaves_queue_for_retry(self):
        self.queue()
        with patch.object(self.worker,'save',side_effect=OSError('synthetic')):
            with self.assertRaises(OSError):self.worker.request()
        self.assertTrue(self.path.exists());self.assertEqual(json.loads(self.config_path.read_text())['mode'],'notify')
    def test_interruption_after_intent_before_queue_removal_is_reconciled(self):
        self.queue();original=pathlib.Path.unlink
        def fail(path,*args,**kwargs):
            if path==self.path:raise OSError('synthetic interrupted unlink')
            return original(path,*args,**kwargs)
        with patch.object(pathlib.Path,'unlink',fail):
            with self.assertRaises(OSError):self.worker.request()
        self.assertTrue(self.path.exists());resumed=self.fresh();resumed.run();self.assertFalse(self.path.exists());self.assertEqual(resumed.status['lastModeRequest']['state'],'applied')
    def test_interruption_after_config_commit_before_ack_is_idempotent(self):
        self.queue();self.worker.request()
        with patch.object(self.worker,'finish_mode_request',side_effect=SystemExit('synthetic kill')):
            with self.assertRaises(SystemExit):self.worker.apply_mode_request()
        self.assertEqual(json.loads(self.config_path.read_text())['mode'],'auto');resumed=self.fresh();resumed.run()
        self.assertEqual(resumed.status['lastModeRequest']['state'],'applied');self.assertIsNone(resumed.status['modeChangePending'])
    def test_write_failure_remains_pending_then_retries(self):
        self.queue();self.worker.request();original=u.atomic_json
        def fail(path,*args,**kwargs):
            if path==self.config_path:raise OSError('synthetic private error')
            return original(path,*args,**kwargs)
        with patch.object(u,'atomic_json',fail):self.worker.apply_mode_request()
        self.assertEqual(self.worker.status['lastModeRequest']['state'],'pending');self.assertEqual(json.loads(self.config_path.read_text())['mode'],'notify')
        resumed=self.fresh();resumed.run();self.assertEqual(resumed.status['lastModeRequest']['state'],'applied')
    def test_recovery_blocks_pending_mode_and_never_installs(self):
        self.queue();self.worker.request();self.worker.recovery_blocked=True;self.worker.run()
        self.assertEqual(self.worker.status['lastModeRequest']['state'],'pending');self.assertEqual(json.loads(self.config_path.read_text())['mode'],'notify')
    def test_changed_designated_operator_cannot_inherit_old_pending_authority(self):
        self.queue();self.worker.request();fresh=json.loads(self.config_path.read_text());fresh['operator']={'familyId':'another','userId':'new-parent'};self.config_path.write_text(json.dumps(fresh));self.config_path.chmod(0o600)
        resumed=self.fresh();resumed.run();self.assertEqual(resumed.status['lastModeRequest']['error'],'operator_changed');self.assertEqual(json.loads(self.config_path.read_text())['mode'],'notify')
    def test_ack_persistence_failure_keeps_root_intent_and_recovers(self):
        self.queue();self.worker.request();original=u.atomic_json;failed=False
        def fail(path,value,*args,**kwargs):
            nonlocal failed
            if path==self.worker.status_path and value.get('lastModeRequest',{}).get('state')=='applied' and not failed:
                failed=True;raise OSError('synthetic acknowledgement write failure')
            return original(path,value,*args,**kwargs)
        with patch.object(u,'atomic_json',fail):self.worker.apply_mode_request()
        self.assertEqual(self.worker.status['lastModeRequest']['state'],'pending');self.assertIsNotNone(self.worker.status['modeChangePending'])
        self.assertEqual(self.worker.status['mode'],'notify');self.assertEqual(json.loads(self.config_path.read_text())['mode'],'auto')
        resumed=self.fresh();resumed.run();self.assertEqual(resumed.status['lastModeRequest']['state'],'applied')
        self.assertEqual(resumed.status['mode'],'auto')
    def test_replay_window_does_not_evict_live_ids_at_capacity(self):
        now=int(time.time()*1000);old=str(uuid.uuid4())
        self.worker.status['modeRequestSeen']=[{'id':old,'at':now}]+[{'id':str(uuid.uuid4()),'at':now} for _ in range(63)]
        self.queue();self.worker.run();self.assertEqual(self.worker.status['lastModeRequest']['error'],'busy')
        self.queue(id=old);self.worker.run();self.assertEqual(self.worker.status['lastModeRequest']['error'],'replayed');self.assertEqual(json.loads(self.config_path.read_text())['mode'],'notify')
    def test_local_first_install_pin_refuses_a_changed_latest_before_preparation(self):
        old=self.root/'old';old.mkdir();(old/'package.json').write_text('{"version":"0.3.1"}')
        self.worker.c['installVersionPin']='0.3.2';self.queue(action='install')
        with patch.object(self.worker,'current',return_value=old),patch.object(self.worker,'release',return_value=({'tag_name':'v0.3.3'},{})),patch.object(self.worker,'install') as install:
            self.worker.run();install.assert_not_called()
        self.assertEqual(self.worker.status['phase'],'incompatible');self.assertEqual(self.worker.status['error'],'incompatible')
    def test_symlink_and_public_request_file_are_not_consumed_as_commands(self):
        self.queue();self.path.chmod(0o644);self.assertIsNone(self.worker.request())
        self.path.symlink_to(self.config_path);self.assertIsNone(self.worker.request());self.assertTrue(self.config_path.exists())

if __name__=='__main__':unittest.main()
