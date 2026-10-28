"""Target-bound local request fixtures: no actual host/provider/family interaction."""
import ast
import contextlib
import fcntl
import hashlib
import importlib.util
import io
import json
import os
import pathlib
import stat
import subprocess
import sys
import tempfile
import time
import types
import unittest
import uuid
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('github_request', ROOT / 'deploy/request-update-github.py')
u = importlib.util.module_from_spec(spec); spec.loader.exec_module(u)


def launcher():
    for node in ast.parse((ROOT / 'deploy/upgrade-worker-github.py').read_bytes()).body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'LAUNCHER' for t in node.targets):
            return ast.literal_eval(node.value)
    raise AssertionError('Missing launcher')


class PinTests(unittest.TestCase):
    def test_request_helper_pins_match_reviewed_sources(self):
        for pin, name in [(u.BASE_SHA256, 'request-update-manual.py'), (u.SUPERVISOR_SHA256, 'updater-supervisor.py'),
                          (u.TRANSPORT_SHA256, 'updater-transport.py')]:
            self.assertEqual(pin, hashlib.sha256((ROOT / 'deploy' / name).read_bytes()).hexdigest(), name)
        self.assertEqual(u.LAUNCHER_SHA256, hashlib.sha256(launcher()).hexdigest())


@unittest.skipUnless(os.name == 'posix', 'Linux ownership/flock fixtures')
class RequestTests(unittest.TestCase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory(); self.addCleanup(tmp.cleanup); self.root = pathlib.Path(tmp.name)
        self.stack = contextlib.ExitStack(); self.addCleanup(self.stack.close)
        self.config = dict(enabled=True, mode='notify', service='yachad.service', stateDirectory=u.STATE,
                           releaseTrust='github-https', appUser='yachad', repository='NehoraiHadad/yahad',
                           operator={'familyId':'10203040-5060-4070-8090-001122334455','userId':'aabbccdd-eeff-4011-8233-445566778899'},
                           dataDirectory='/var/lib/yachad/live', installVersionPin=None, publicKeyFile=None, tokenFile=None,
                           healthUrl='http://127.0.0.1:3000/health/ready')
        self.selected = {'version':'0.6.0','fingerprint':'a'*64}
        self.state = dict(phase='available', currentVersion='0.5.0', latestVersion='0.6.0', installTarget=self.selected,
                          operator=self.config['operator'], releaseTrust='github-https', error=None)
        self.policy = dict(format=2, trust='github-https', enabled=True, repository='NehoraiHadad/yahad', channel='stable',checkHours=6)
        self.runtime = dict(format=1, phase='stable', active=dict(version='1.0.0',sha256='b'*64,manifestSha256='c'*64))
        self.events, self.health_calls = [], []
        self.helper = u.GithubRequest(self.root, writer=self.writer, start=self.start, health=self.health,
                                      ask=lambda _: 'INSTALL 0.6.0', now=lambda:1234567)
        for name, data, mode in [(u.CONFIG,json.dumps(self.config).encode(),0o600),
                                (u.STATE+'/status.json',json.dumps(self.state).encode(),0o640),
                                (u.STATE+'/worker.lock',b'',0o600),
                                (u.POLICY,json.dumps(self.policy).encode(),0o600),
                                (u.STATE+'/updater-runtime.json',json.dumps(self.runtime).encode(),0o600),
                                (u.DIRECTORY+'updater.py',launcher(),0o644),
                                (u.DIRECTORY+'updater-supervisor.py',(ROOT/'deploy/updater-supervisor.py').read_bytes(),0o644),
                                (u.DIRECTORY+'updater-transport.py',(ROOT/'deploy/updater-transport.py').read_bytes(),0o644)]:
            path=self.helper.path(name);path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(data);path.chmod(mode)
        original_lstat,original_fstat=pathlib.Path.lstat,os.fstat
        def root_owner(info):
            fields=list(info);fields[4]=0;return os.stat_result(fields)
        self.stack.enter_context(patch.object(pathlib.Path,'lstat',lambda path:root_owner(original_lstat(path))))
        self.stack.enter_context(patch.object(os,'fstat',lambda fd:root_owner(original_fstat(fd))))
        self.stack.enter_context(patch.object(u.pwd,'getpwnam',return_value=types.SimpleNamespace(pw_uid=1001)))
        self.stack.enter_context(patch.object(u,'SUPERVISOR_SHA256',hashlib.sha256((ROOT/'deploy/updater-supervisor.py').read_bytes()).hexdigest()))
        self.stack.enter_context(patch.object(u,'TRANSPORT_SHA256',hashlib.sha256((ROOT/'deploy/updater-transport.py').read_bytes()).hexdigest()))

    def save_status(self): self.helper.path(u.STATE+'/status.json').write_text(json.dumps(self.state))
    def save_config(self): self.helper.path(u.CONFIG).write_text(json.dumps(self.config))
    def health(self,*args): self.health_calls.append(args);return True
    def writer(self,config,payload):
        with self.helper.path(u.STATE+'/worker.lock').open('rb') as other:
            with self.assertRaises(BlockingIOError): fcntl.flock(other,fcntl.LOCK_EX|fcntl.LOCK_NB)
        self.events.append(('publish',json.loads(payload)))
    def start(self):
        with self.helper.path(u.STATE+'/worker.lock').open('rb') as other:
            fcntl.flock(other,fcntl.LOCK_EX|fcntl.LOCK_NB)
        self.events.append(('start',))
    def queue(self,*args,**kwargs):
        with contextlib.redirect_stdout(io.StringIO()) as output: result=self.helper.queue(*args,**kwargs)
        self.assertIn('REQUEST_ID='+result[0],output.getvalue());return result
    def completed_start(self):
        self.start()
        value=next(event[1] for event in reversed(self.events) if event[0]=='publish')
        request=dict(id=value['id'],action=value['action'],state='succeeded',error=None)
        if value['action']=='install':
            request['target']=value['target']
            self.state.update(currentVersion=value['target']['version'],phase='healthy',targetDatabaseSchema=3,error=None)
        self.state['lastRequest']=request;self.save_status()

    def test_check_then_exact_confirmation_bound_install_and_schema_readiness(self):
        self.helper.start=self.completed_start
        before=self.helper.path(u.CONFIG).read_bytes()
        with contextlib.redirect_stdout(io.StringIO()) as output: result=self.helper.install(0)
        self.assertIn('INSTALL_HEALTHY',result);self.assertIn('Release fingerprint: '+'a'*64,output.getvalue())
        values=[event[1] for event in self.events if event[0]=='publish']
        self.assertEqual([v['action'] for v in values],['check','install'])
        self.assertNotIn('target',values[0]);self.assertEqual(values[1]['target'],self.selected)
        self.assertEqual(self.health_calls,[(self.config['healthUrl'],'0.6.0',3)])
        self.assertEqual(self.helper.path(u.CONFIG).read_bytes(),before)

    def test_cancel_after_check_never_queues_install(self):
        self.helper.start=self.completed_start;self.helper.ask=lambda _:'y'
        with contextlib.redirect_stdout(io.StringIO()): result=self.helper.install(0)
        self.assertIn('CANCELED',result)
        self.assertEqual([e[1]['action'] for e in self.events if e[0]=='publish'],['check'])

    def test_changed_target_after_confirmation_never_substitutes(self):
        self.helper.start=self.completed_start
        def ask(_):
            self.state['installTarget']={'version':'0.7.0','fingerprint':'d'*64};self.save_status()
            return 'INSTALL 0.6.0'
        self.helper.ask=ask
        with contextlib.redirect_stdout(io.StringIO()),self.assertRaisesRegex(u.RequestError,'TARGET_CHANGED'):self.helper.install(0)
        self.assertEqual([e[1]['action'] for e in self.events if e[0]=='publish'],['check'])

    def test_config_or_enrollment_change_after_confirmation_never_queues_install(self):
        self.helper.start=self.completed_start
        def ask(_):
            self.config['mode']='auto';self.save_config();return 'INSTALL 0.6.0'
        self.helper.ask=ask
        with contextlib.redirect_stdout(io.StringIO()),self.assertRaisesRegex(u.RequestError,'TARGET_CHANGED'):self.helper.install(0)
        self.assertEqual([e[1]['action'] for e in self.events if e[0]=='publish'],['check'])

    def test_existing_auto_check_may_install_and_helper_never_duplicates(self):
        self.config['mode']='auto';self.save_config()
        def start():
            self.completed_start()
            self.state.update(currentVersion='0.6.0',latestVersion='0.6.0',phase='healthy',targetDatabaseSchema=3)
            self.save_status()
        self.helper.start=start;self.helper.ask=lambda _:self.fail('Already installed must not ask')
        with contextlib.redirect_stdout(io.StringIO()) as output:result=self.helper.install(0)
        self.assertIn('auto mode may already install',output.getvalue());self.assertIn('CURRENT_HEALTHY',result)
        self.assertEqual([e[1]['action'] for e in self.events if e[0]=='publish'],['check'])
        self.assertEqual(self.health_calls,[(self.config['healthUrl'],'0.6.0',3)])

    def test_check_timeout_is_not_cancellation_or_install(self):
        with contextlib.redirect_stdout(io.StringIO()):result=self.helper.install(0)
        self.assertIn('PENDING_OR_UNCONFIRMED',result)
        self.assertEqual([e[1]['action'] for e in self.events if e[0]=='publish'],['check'])

    def test_disabled_unknown_signed_or_pending_enrollment_refuses(self):
        self.policy['enabled']=False;self.helper.path(u.POLICY).write_text(json.dumps(self.policy))
        with self.assertRaises(u.RequestError):self.queue('check')
        self.policy['enabled']=True;self.helper.path(u.POLICY).write_text(json.dumps(self.policy))
        self.helper.path(u.KEY).write_bytes(b'synthetic pin')
        with self.assertRaises(u.RequestError):self.queue('check')
        self.helper.path(u.KEY).unlink()
        self.runtime['phase']='recovery_required';self.helper.path(u.STATE+'/updater-runtime.json').write_text(json.dumps(self.runtime))
        with self.assertRaises(u.RequestError):self.queue('check')
        self.assertFalse(self.events)

    def test_targetless_wrong_target_and_conflicting_pin_refuse(self):
        for selected in (None,{'version':'0.6.0','fingerprint':'d'*64}):
            with self.assertRaises(u.RequestError):self.queue('install',selected=selected)
        self.config['installVersionPin']='0.5.0';self.save_config()
        with self.assertRaisesRegex(u.RequestError,'VERSION_PIN'):self.queue('install',selected=self.selected)
        self.assertFalse(self.events)

    def test_produced_install_request_is_accepted_by_actual_root_transport(self):
        spec=importlib.util.spec_from_file_location('actual_transport',ROOT/'deploy/updater-transport.py')
        transport=importlib.util.module_from_spec(spec);spec.loader.exec_module(transport)
        request_id,_=self.queue('install',selected=self.selected)
        value=next(e[1] for e in self.events if e[0]=='publish')
        raw=json.dumps(value,separators=(',',':')).encode()
        queue=self.helper.path(self.config['dataDirectory'])/'update-request.json'
        queue.parent.mkdir(parents=True);queue.write_bytes(raw);queue.chmod(0o600)
        supervisor=types.SimpleNamespace(path=self.helper.path,app_status=lambda:self.state,trust_profile=lambda:'github-https')
        original=os.fstat
        def app_owner(fd):
            info=original(fd);fields=list(info)
            if pathlib.Path(os.readlink('/proc/self/fd/'+str(fd)))==queue:fields[4]=1001
            return os.stat_result(fields)
        with patch.object(os,'fstat',app_owner):
            self.assertEqual(transport.install_intent(supervisor,self.config,1234567000),hashlib.sha256(raw).hexdigest())
        self.assertEqual(value['target'],self.selected);self.assertNotIn('installTarget',value)

    def test_install_refuses_stale_target_outside_available_phase(self):
        self.state['phase']='healthy';self.save_status()
        with self.assertRaisesRegex(u.RequestError,'TARGET_CHANGED'):self.queue('install',selected=self.selected)
        self.assertFalse(self.events)

    def test_publication_uncertainty_prints_id_and_does_not_start_or_retry(self):
        calls=[]
        def writer(*args):calls.append(args);raise u.RequestError('REQUEST_EXISTS')
        self.helper.writer=writer
        with contextlib.redirect_stdout(io.StringIO()) as output,self.assertRaisesRegex(u.RequestError,'REQUEST_EXISTS'):self.helper.queue('check')
        self.assertIn('REQUEST_ID=',output.getvalue());self.assertEqual(len(calls),1);self.assertFalse(self.events)

    def test_observe_requires_uuid_target_version_schema_and_health(self):
        request_id=str(uuid.uuid4())
        self.state.update(phase='healthy',currentVersion='0.6.0',targetDatabaseSchema=3,
                          lastRequest=dict(id=request_id,action='install',state='succeeded',target=self.selected))
        self.save_status()
        self.assertIsNotNone(self.helper.observe(request_id,self.config,0,action='install',selected=self.selected))
        for key,value in [('currentVersion','0.7.0'),('targetDatabaseSchema',True),('phase','recovery_required')]:
            previous=self.state[key];self.state[key]=value;self.save_status()
            with self.assertRaisesRegex(u.RequestError,'HEALTH_UNCONFIRMED'):self.helper.observe(request_id,self.config,0)
            self.state[key]=previous;self.save_status()
        with self.assertRaisesRegex(u.RequestError,'TARGET_CHANGED'):
            self.helper.observe(request_id,self.config,0,selected={'version':'0.6.0','fingerprint':'d'*64})
        self.helper.health=lambda *args:False
        with self.assertRaisesRegex(u.RequestError,'HEALTH_UNCONFIRMED'):self.helper.observe(request_id,self.config,0)

    def test_follow_is_read_only_even_if_updates_disabled(self):
        request_id=str(uuid.uuid4())
        self.state['lastRequest']=dict(id=request_id,action='check',state='succeeded');self.save_status()
        self.config['enabled']=False;self.save_config()
        self.helper.writer=lambda *args:self.fail('No publication');self.helper.start=lambda:self.fail('No wake')
        self.assertIn('CHECK_COMPLETED',self.helper.follow(request_id,0))
        self.assertFalse(self.events)

    def test_target_changed_failure_preserves_state_and_never_probes_health(self):
        request_id=str(uuid.uuid4())
        self.state['lastRequest']=dict(id=request_id,action='install',state='failed',error='target_changed',target=self.selected)
        self.save_status();before=self.helper.path(u.STATE+'/status.json').read_bytes()
        with self.assertRaisesRegex(u.RequestError,'TARGET_CHANGED'):self.helper.observe(request_id,self.config,0)
        self.assertEqual(self.helper.path(u.STATE+'/status.json').read_bytes(),before);self.assertFalse(self.health_calls)


@unittest.skipUnless(os.name=='posix' and os.geteuid()!=0,'Actual unprivileged atomic queue writer')
class WriterTests(unittest.TestCase):
    def setUp(self):
        temporary=tempfile.TemporaryDirectory();self.addCleanup(temporary.cleanup);self.root=pathlib.Path(temporary.name)
    def write(self,value):
        return subprocess.run([sys.executable,'-I','-B','-c',u.WRITER,str(self.root)],input=json.dumps(value).encode(),capture_output=True,timeout=5)
    def value(self,action):
        value=dict(id=str(uuid.uuid4()),action=action,familyId='f',userId='p',createdAt=int(time.time()*1000))
        if action=='install':value['target']={'version':'0.6.0','fingerprint':'a'*64}
        return value
    def test_private_target_bound_install_and_existing_queue_preserved(self):
        value=self.value('install');self.assertEqual(self.write(value).returncode,0)
        queue=self.root/'update-request.json';before=queue.read_bytes()
        self.assertEqual(json.loads(before),value);self.assertEqual(stat.S_IMODE(queue.stat().st_mode),0o600)
        self.assertEqual(self.write(self.value('check')).returncode,20);self.assertEqual(queue.read_bytes(),before)
    def test_check_allowed_but_targetless_install_and_extra_fields_refused(self):
        value=self.value('install');value.pop('target');self.assertNotEqual(self.write(value).returncode,0)
        value=self.value('check');value['url']='https://evil.invalid';self.assertNotEqual(self.write(value).returncode,0)
        self.assertFalse((self.root/'update-request.json').exists())
        self.assertEqual(self.write(self.value('check')).returncode,0)


class CliTests(unittest.TestCase):
    def test_read_only_status_needs_no_owner_tty_and_does_not_install(self):
        request_id=str(uuid.uuid4());helper=types.SimpleNamespace(follow=lambda *args:'OBSERVED',install=lambda *args:self.fail('Must not install'))
        with patch.object(u.sys,'argv',['request-update-github.py','--status',request_id,'--wait-seconds','0']), \
             patch.object(u.os,'geteuid',return_value=0),patch.object(u,'GithubRequest',return_value=helper), \
             patch.object(u.sys.stdin,'isatty',return_value=False),contextlib.redirect_stdout(io.StringIO()) as output:u.main()
        self.assertEqual(output.getvalue(),'OBSERVED\n')
    def test_noninteractive_mutation_refused_before_helper_construction(self):
        with patch.object(u.sys,'argv',['request-update-github.py']),patch.object(u.os,'geteuid',return_value=0), \
             patch.dict(u.os.environ,{'SUDO_UID':'1000'}),patch.object(u.pwd,'getpwuid',return_value=types.SimpleNamespace(pw_name='owner')), \
             patch.object(u.sys.stdin,'isatty',return_value=False),patch.object(u,'GithubRequest',side_effect=AssertionError('Must not construct')):
            with self.assertRaisesRegex(u.RequestError,'Interactive'):u.main()


if __name__=='__main__':unittest.main()
