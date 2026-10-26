import os
import pathlib
import pwd
import signal
import subprocess
import sys
import shutil
import tempfile
import unittest

@unittest.skipUnless(os.name == 'posix', 'Requires Linux kernel flock and ownership')
class DataLockTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.tmp.name)
        self.root.chmod(0o755)
        self.data = self.root / 'data'
        self.data.mkdir(mode=0o700)
        self.prefix = []
        if os.geteuid() == 0:
            user = pwd.getpwnam('nobody')
            os.chown(self.data, user.pw_uid, user.pw_gid)
            self.prefix = ['/usr/sbin/runuser', '-u', 'nobody', '--']
        self.helper = pathlib.Path(__file__).parents[1] / 'deploy/data-lock.py'

    def tearDown(self): self.tmp.cleanup()

    def command(self, code):
        return self.prefix + [sys.executable, str(self.helper), str(self.data), sys.executable, '-c', code]

    def test_kernel_lock_prevents_concurrent_process_and_releases_after_crash(self):
        child = subprocess.Popen(self.command("import time; print('READY',flush=True); time.sleep(300)"),
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        try:
            self.assertEqual(child.stdout.readline().strip(), b'READY')
            second = subprocess.run(self.command("print('SECOND')"), capture_output=True, timeout=10)
            self.assertNotEqual(second.returncode, 0)
            self.assertNotIn(b'SECOND', second.stdout)
        finally:
            os.killpg(child.pid, signal.SIGKILL)
            child.communicate(timeout=10)
        third = subprocess.run(self.command("print('AFTER_CRASH')"), capture_output=True, timeout=10)
        self.assertEqual(third.returncode, 0, third.stderr)
        self.assertIn(b'AFTER_CRASH', third.stdout)

    def test_symlink_guard_is_refused(self):
        outside = self.root / 'outside'
        outside.write_text('preserved')
        (self.data / 'process.guard').symlink_to(outside)
        result = subprocess.run(self.command("print('BAD')"), capture_output=True, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(outside.read_text(), 'preserved')

    def test_node_service_retains_the_inherited_kernel_lock(self):
        command = self.prefix + [sys.executable, str(self.helper), str(self.data), shutil.which('node'),
            '-e', "console.log('READY'); setInterval(()=>{},1000)"]
        child = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
        try:
            self.assertEqual(child.stdout.readline().strip(), b'READY')
            second = subprocess.run(self.command("print('UNEXPECTED')"), capture_output=True, timeout=10)
            self.assertNotEqual(second.returncode, 0)
        finally:
            os.killpg(child.pid, signal.SIGKILL)
            child.communicate(timeout=10)

if __name__ == '__main__': unittest.main()
