"""Windows adapter for pure logic/crypto tests; never simulates Linux acceptance.

Linux ownership, flock, runuser and systemd integration tests remain skipped.
No production module is made portable or less strict by this harness.
"""
import os
import pathlib
import subprocess
import sys
import types
import unittest
from unittest.mock import patch

if os.name != 'nt':
    raise SystemExit('Use unittest discovery directly on Linux')
sys.modules['pwd'] = types.SimpleNamespace(getpwuid=lambda _: types.SimpleNamespace(pw_name='windows-test'),
    getpwnam=lambda _: types.SimpleNamespace(pw_gid=0))
sys.modules['fcntl'] = types.SimpleNamespace(LOCK_EX=2, LOCK_NB=4)
os.getuid = lambda: -1
openssl = pathlib.Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'Git/usr/bin/openssl.exe'
original = subprocess.run
def run(args, *a, **kw):
    if args[0] in ('openssl', '/usr/bin/openssl'):
        args = [str(openssl), *args[1:]]
    return original(args, *a, **kw)
with patch.object(subprocess, 'run', run):
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.discover(str(pathlib.Path(__file__).parent), 'test_*.py'))
sys.exit(not result.wasSuccessful())
