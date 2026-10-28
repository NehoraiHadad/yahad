"""Synthetic public-release trust policy; no live GitHub/provider calls."""
import hashlib, importlib.util, json, pathlib, tempfile, unittest
from unittest.mock import patch
spec=importlib.util.spec_from_file_location('release_trust',pathlib.Path(__file__).parents[1]/'deploy/updater.py')
u=importlib.util.module_from_spec(spec);spec.loader.exec_module(u)

class TrustTests(unittest.TestCase):
    def config(self,**changes):
        return dict(repository='NehoraiHadad/yahad',releaseTrust='github-https',publicKeyFile=None,tokenFile=None,**changes)
    def worker(self):
        w=object.__new__(u.Worker);w.c=self.config();w.status={};return w
    def test_explicit_public_policy_needs_no_key_or_credentials_and_old_configs_remain_signed(self):
        self.assertEqual(u.release_policy({}),'signature')
        with patch.object(pathlib.Path,'exists',return_value=False):
            self.assertEqual(u.release_assets(self.config()),('yachad-release.json','yachad-app.zip'))
            self.assertEqual(u.release_assets({}),u.ASSETS)
            for field,value in [('repository','other/repo'),('tokenFile','/token'),('publicKeyFile','/key'),('releaseTrust','none')]:
                c=self.config();c[field]=value
                with self.assertRaises(u.UpdateError):u.release_policy(c)
    def test_existing_pin_or_legacy_or_signed_state_cannot_silently_change_trust(self):
        with patch.object(pathlib.Path,'exists',return_value=True):
            with self.assertRaises(u.UpdateError):u.release_policy(self.config())
        with patch.object(pathlib.Path,'exists',return_value=False):
            for status in [{'phase':'idle'},{'releaseTrust':'signature'}]:
                with self.assertRaises(u.UpdateError):u.release_policy(self.config(),status)
            self.assertEqual(u.release_policy(self.config(),{'releaseTrust':'github-https'}),'github-https')
    def test_manifest_checks_integrity_schema_version_and_bounds_without_claiming_authenticity(self):
        archive=b'synthetic archive';m={'format':1,'version':'0.3.2','databaseSchema':1,'archive':'yachad-app.zip','bytes':len(archive),'sha256':hashlib.sha256(archive).hexdigest()}
        self.assertEqual(u.verify_manifest(json.dumps(m).encode(),archive),m)
        for changes in [{'sha256':'0'*64},{'bytes':True},{'bytes':len(archive)+1},{'databaseSchema':3},{'version':'latest'},{'archive':'other.zip'}]:
            with self.assertRaises(u.UpdateError):u.verify_manifest(json.dumps(dict(m,**changes)).encode(),archive)
        for raw in [b'[]',b'null',b'x'*8193]:
            with self.assertRaises(u.UpdateError):u.verify_manifest(raw,archive)
    def test_only_stable_latest_from_fixed_repo_and_repo_scoped_asset_urls(self):
        w=self.worker();assets=[{'name':name,'url':f'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/{i+1}'}for i,name in enumerate(u.release_assets(w.c))]
        release={'tag_name':'v0.3.2','draft':False,'prerelease':False,'assets':assets}
        with patch.object(u,'download',return_value=json.dumps(release).encode())as download:
            self.assertEqual(w.release(None)[0]['tag_name'],'v0.3.2')
            self.assertEqual(download.call_args.args[0],'https://api.github.com/repos/NehoraiHadad/yahad/releases/latest')
            self.assertIsNone(download.call_args.args[2])
        for change in [{'draft':True},{'prerelease':True},{'tag_name':'v0.3.2-rc1'},{'assets':assets+[assets[0]]},{'assets':[dict(assets[0],url='https://api.github.com/repos/other/repo/releases/assets/1'),assets[1]]}]:
            with patch.object(u,'download',return_value=json.dumps(dict(release,**change)).encode()):
                with self.assertRaises(u.UpdateError):w.release(None)
    def test_https_and_redirect_policy_never_accepts_http_or_external_hosts(self):
        for url in ['http://github.com/NehoraiHadad/yahad','https://evil.example/x','https://api.github.com@evil.example/x']:
            with self.assertRaises(u.UpdateError):u.safe_url(url)
        req=u.urllib.request.Request('https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/1')
        with self.assertRaises(u.UpdateError):u.Redirect().redirect_request(req,None,302,'',{},'http://github.com/x')
