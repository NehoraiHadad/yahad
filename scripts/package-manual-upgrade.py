#!/usr/bin/env python3
"""Offline deterministic package for the explicit owner-run manual transition.

No keys, signatures, downloads, publishing or installer execution. The archive
contains only the bootstrap, its pinned direct worker and explicit request helper.
"""
import argparse
import ast
import hashlib
import pathlib
import stat
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
FILES = ('upgrade-worker-manual.py', 'updater.py', 'request-update-manual.py')
ARCHIVE = 'yachad-manual-upgrade-0.5.0.zip'


def source_file(name):
    # The historical 0.5.0 manual transition stays byte-identical after the
    # development worker moves to later protocols. Never silently repin it.
    return ROOT / ('tests/fixtures/updater-direct-0.5.txt' if name == 'updater.py' else 'deploy/' + name)


def package(output):
    output = pathlib.Path(output)
    if output.exists() or output.is_symlink():
        raise ValueError('Choose a new output directory; existing artifacts are preserved')
    contents = {}
    for name in FILES:
        source = source_file(name)
        if source.is_symlink() or not stat.S_ISREG(source.lstat().st_mode):
            raise ValueError('Only regular source files may be packaged')
        raw = source.read_bytes()
        if not raw or len(raw) > 1024 * 1024 or b'\r' in raw:
            raise ValueError('Expected bounded canonical LF source: ' + name)
        compile(raw, name, 'exec', dont_inherit=True)
        contents[name] = raw
    tree = ast.parse(contents['upgrade-worker-manual.py'])
    pins = [ast.literal_eval(node.value) for node in tree.body
            if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'DIRECT_WORKER_SHA256' for t in node.targets)]
    if pins != [hashlib.sha256(contents['updater.py']).hexdigest()]:
        raise ValueError('Direct worker differs from reviewed bootstrap pin')
    output.mkdir(mode=0o700)
    archive = output / ARCHIVE
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as target:
        for name, raw in contents.items():
            info = zipfile.ZipInfo(name, date_time=(2026, 10, 28, 0, 0, 0))
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            target.writestr(info, raw, compresslevel=9)
    checksum = hashlib.sha256(archive.read_bytes()).hexdigest()
    (output / 'SHA256SUMS').write_text(checksum + '  ' + ARCHIVE + '\n', encoding='ascii')
    (output / 'FILES.sha256').write_text(''.join(hashlib.sha256(raw).hexdigest() + '  ' + name + '\n'
                                              for name, raw in contents.items()), encoding='ascii')
    return checksum


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', help='New local output directory (never published)')
    args = parser.parse_args()
    print(package(args.output) + '  ' + ARCHIVE)
