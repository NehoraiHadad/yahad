"""Joined transport + signature supervisor + inherited-lock child, all synthetic."""
import importlib.util
import json
import os
import pathlib
import pwd
import unittest
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[1]
def module(name, file):
    spec = importlib.util.spec_from_file_location(name, file)
    value = importlib.util.module_from_spec(spec); spec.loader.exec_module(value)
    return value
fixture_module = module('joined_runtime_fixtures', ROOT / 'tests/test_updater_supervisor.py')
transport = module('joined_runtime_transport', ROOT / 'deploy/updater-transport.py')


class RuntimeUpgradeIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): fixture_module.SupervisorTests.setUpClass()
    @classmethod
    def tearDownClass(cls): fixture_module.SupervisorTests.tearDownClass()
    def setUp(self):
        self.fixture = fixture_module.SupervisorTests('test_real_signature_activation_dispatch_and_no_privileged_configuration_changes')
        self.fixture.setUp(); self.addCleanup(self.fixture.doCleanups)
        self.supervisor = self.fixture.supervisor
        self.supervisor.runner = fixture_module.s.fixed_process
        root = self.supervisor.root
        self.config = dict(stateDirectory='/var/lib/yachad-updates', dataDirectory='/data',
                           releaseTrust='github-https', enabled=True, mode='notify',
                           appUser=pwd.getpwuid(os.getuid()).pw_name, operator={'familyId':'family','userId':'parent'},
                           installVersionPin='0.4.1')
        fixture_module.write(self.supervisor.config_path, json.dumps(self.config).encode())
        fixture_module.write(root / 'etc/yachad/updater-source.json', json.dumps(dict(format=1,enabled=True,
                             repository='NehoraiHadadad/yahad',channel='stable',checkHours=6)).encode())
        self.queue = root / 'data/update-request.json'
        self.request = dict(id=str(uuid.uuid4()),action='install',familyId='family',userId='parent',createdAt=1000000)
        fixture_module.write(self.queue, json.dumps(self.request).encode())
        source = self.fixture.source + ("\nfrom pathlib import Path\n"
                 "if sys.argv[1]=='requests':\n"
                 " root=Path(" + repr(str(root)) + ")\n"
                 " queue=root/'data/update-request.json'\n"
                 " request=json.loads(queue.read_bytes())\n"
                 " assert request['action']=='install'\n"
                 " separate=os.open(root/'var/lib/yachad-updates/worker.lock',os.O_RDWR)\n"
                 " try:\n"
                 "  try: fcntl.flock(separate,fcntl.LOCK_EX|fcntl.LOCK_NB)\n"
                 "  except BlockingIOError: pass\n"
                 "  else: raise RuntimeError('supervisor lock was not retained')\n"
                 " finally: os.close(separate)\n"
                 " manifest=json.loads((Path.cwd()/'manifest.json').read_bytes())\n"
                 " (root/'data/application-install.json').write_text(json.dumps({'id':request['id'],'runtimeVersion':manifest['version']}))\n"
                 " queue.unlink()\n").encode()
        self.bundle = self.fixture.bundle('1.1.0', source=source)
        self.calls = []
        self.release = dict(tag_name='v0.4.1', draft=False, prerelease=False,
                            assets=[dict(name=name,url=f'https://api.github.com/repos/NehoraiHadadad/yahad/releases/assets/{i}')
                                    for i,name in enumerate(transport.ASSETS)])
    def fetch(self, url, *args):
        self.calls.append(url)
        if url.endswith('/latest'): return json.dumps(self.release).encode()
        return self.bundle[int(url.rsplit('/',1)[1])]
    def test_notify_explicit_install_authenticates_new_worker_then_dispatches_same_queued_intent(self):
        before_config = self.supervisor.config_path.read_bytes()
        result = transport.refresh(self.supervisor,'requests',fetch=self.fetch,now=lambda:1000)
        self.assertEqual(result['result'],'updated')
        self.assertEqual(self.supervisor.journal()['active']['version'],'1.1.0')
        self.assertEqual(json.loads(self.queue.read_bytes()),self.request)
        self.assertFalse((self.supervisor.root/'data/application-install.json').exists())
        self.supervisor.dispatch('requests')
        self.assertEqual(json.loads((self.supervisor.root/'data/application-install.json').read_bytes()),
                         {'id':self.request['id'],'runtimeVersion':'1.1.0'})
        self.assertFalse(self.queue.exists())
        self.assertEqual(self.supervisor.config_path.read_bytes(),before_config)
    def test_bad_signature_never_executes_candidate_and_existing_runtime_still_dispatches(self):
        self.bundle = (self.bundle[0],bytes(64),self.bundle[2])
        result = transport.refresh(self.supervisor,'requests',fetch=self.fetch,now=lambda:1000)
        self.assertEqual(result['error'],'signature')
        self.assertEqual(self.supervisor.journal()['active']['version'],'1.0.0')
        self.assertEqual(json.loads(self.queue.read_bytes()),self.request)
        self.supervisor.dispatch('requests')
        self.assertFalse((self.supervisor.root/'data/application-install.json').exists())

if __name__=='__main__':unittest.main()
