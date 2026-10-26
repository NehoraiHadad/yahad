#!/usr/bin/env python3
"""Export only regular dependency files, as the unprivileged builder.

Root never opens builder-controlled paths. ZIP paths, types and sizes are checked
again by the receiver. npm's optional .bin symlinks are not needed at runtime.
"""
import os
import pathlib
import stat
import sys
import tempfile
import zipfile

def export(directory, output):
    root = pathlib.Path(directory) / 'node_modules'
    size = count = 0
    with tempfile.TemporaryFile() as spool:
        with zipfile.ZipFile(spool, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            for base, dirs, files in os.walk(root, followlinks=False):
                dirs[:] = [d for d in dirs if not (pathlib.Path(base) / d).is_symlink()]
                for name in files:
                    path = pathlib.Path(base) / name
                    if path.is_symlink():
                        continue
                    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
                    with os.fdopen(fd, 'rb') as source:
                        info = os.fstat(source.fileno())
                        size += info.st_size
                        count += 1
                        if not stat.S_ISREG(info.st_mode) or size > 512 * 1024 * 1024 or count > 30000:
                            raise ValueError('Dependency export limit')
                        entry = zipfile.ZipInfo(path.relative_to(root.parent).as_posix())
                        entry.compress_type = zipfile.ZIP_DEFLATED
                        entry.external_attr = (stat.S_IFREG | (0o755 if info.st_mode & 0o111 else 0o644)) << 16
                        with archive.open(entry, 'w') as target:
                            remaining = info.st_size
                            while remaining:
                                chunk = source.read(min(1048576, remaining))
                                if not chunk: raise ValueError('Dependency changed')
                                target.write(chunk)
                                remaining -= len(chunk)
                        if spool.tell() > 128 * 1024 * 1024:
                            raise ValueError('Dependency export limit')
        if spool.tell() > 128 * 1024 * 1024 or not count:
            raise ValueError('Dependency export limit')
        spool.seek(0)
        while True:
            chunk = spool.read(1048576)
            if not chunk: break
            output.write(chunk)

if __name__ == '__main__':
    try:
        if os.geteuid() == 0: raise ValueError('Run as builder')
        export(sys.argv[1], sys.stdout.buffer)
    except Exception:
        sys.stderr.write('DEPENDENCY_EXPORT_FAILED\n')
        sys.exit(1)
