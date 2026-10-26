import io
import os
import pathlib
import pwd
import subprocess
import sys
import tempfile
import unittest
import zipfile

@unittest.skipUnless(os.name == 'posix', 'Requires Linux no-follow opens and unprivileged export')
class ExportTests(unittest.TestCase):
    def test_export_is_unprivileged_and_contains_only_regular_dependencies(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp); root.chmod(0o755)
            build = root / 'build'; build.mkdir(mode=0o755)
            package = build / 'node_modules/pkg'; package.mkdir(parents=True)
            (package/'index.js').write_text('signed dependency fixture')
            (build/'src').mkdir(); (build/'src/secret.txt').write_text('must not export app source')
            (package/'link').symlink_to('/etc/shadow')
            prefix = ['/usr/sbin/runuser','-u','nobody','--'] if os.geteuid() == 0 else []
            helper = pathlib.Path(__file__).parents[1] / 'deploy/export-dependencies.py'
            result = subprocess.run(prefix + [sys.executable,str(helper),str(build)], capture_output=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr)
            with zipfile.ZipFile(io.BytesIO(result.stdout)) as archive:
                self.assertEqual(archive.namelist(), ['node_modules/pkg/index.js'])
                self.assertEqual(archive.read(archive.namelist()[0]), b'signed dependency fixture')

if __name__ == '__main__': unittest.main()
