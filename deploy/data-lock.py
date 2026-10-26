#!/usr/bin/env python3
"""Hold a kernel lock across exec. Run as the data owner, never as root."""
import fcntl
import os
import pathlib
import stat
import sys

if __name__ == '__main__':
    try:
        if os.geteuid() == 0 or len(sys.argv) < 4:
            raise ValueError('Run as the data owner')
        path = pathlib.Path(sys.argv[1]) / 'process.guard'
        fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or info.st_mode & 0o077:
            raise ValueError('Private owner lock required')
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        os.set_inheritable(fd, True)
        os.execvp(sys.argv[2], sys.argv[2:])
    except Exception:
        sys.stderr.write('YACHAD_DATA_LOCK_UNAVAILABLE\n')
        sys.exit(1)
