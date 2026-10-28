"""Offline packaging fixtures; never runs package code, installs or publishes."""
import json,pathlib,shutil,subprocess,tempfile,unittest,zipfile
ROOT=pathlib.Path(__file__).resolve().parents[1]
@unittest.skipUnless(shutil.which('node') and shutil.which('python3'),'local packaging runtimes required')
class ReleaseLineEndings(unittest.TestCase):
 def package(self,newline):
  temp=tempfile.TemporaryDirectory();self.addCleanup(temp.cleanup);root=pathlib.Path(temp.name)
  for folder in ('dist','deploy','scripts'):(root/folder).mkdir()
  for file in ('package-lock.json','README.md','LICENSE','tsconfig.json','vite.config.ts','.env.example'):(root/file).write_text('synthetic\n')
  (root/'package.json').write_text(json.dumps({'version':'0.4.0'}));(root/'dist/index.html').write_text('synthetic\n')
  (root/'deploy/updater.py').write_bytes(b'# synthetic'+newline)
  result=subprocess.run(['node',str(ROOT/'scripts/release.mjs'),'package-https',str(root/'out')],cwd=root,capture_output=True,text=True)
  return root,result
 def test_canonical_lf_is_preserved_in_archive(self):
  root,result=self.package(b'\n');self.assertEqual(result.returncode,0,result.stderr)
  with zipfile.ZipFile(root/'out/yachad-app.zip') as archive:self.assertEqual(archive.read('deploy/updater.py'),b'# synthetic\n')
  manifest=json.loads((root/'out/yachad-release.json').read_bytes())
  self.assertEqual((manifest['format'],manifest['databaseSchema'],manifest['databaseMigrationsFrom'],manifest['minimumUpdaterProtocol']),(2,2,[1],2))
 def test_crlf_privileged_source_rejected_before_output(self):
  root,result=self.package(b'\r\n');self.assertNotEqual(result.returncode,0)
  self.assertIn('Noncanonical deploy line endings',result.stderr);self.assertFalse((root/'out').exists())
if __name__=='__main__':unittest.main()
