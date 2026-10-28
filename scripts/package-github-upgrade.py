#!/usr/bin/env python3
"""Deterministic, offline delivery of the reviewed HTTPS-runtime bootstrap.

No signatures, credentials, network, publication or installer execution. This
artifact merely carries reviewed code; only local owner confirmation enrolls it.
"""
import argparse
import ast
import hashlib
import io
import json
import pathlib
import stat
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCES = ('upgrade-worker-github.py', 'upgrade-worker-manual.py', 'request-update-github.py',
           'request-update-manual.py', 'updater-supervisor.py', 'updater-transport.py')
PIN_FILES = {'BASE_SHA256': 'upgrade-worker-manual.py', 'SUPERVISOR_SHA256': 'updater-supervisor.py',
             'TRANSPORT_SHA256': 'updater-transport.py', 'INITIAL_WORKER_SHA256': 'updater.py'}


def literals(raw):
    result = {}
    for node in ast.parse(raw).body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name):
                    try: result[target.id] = ast.literal_eval(node.value)
                    except (ValueError, TypeError): pass
    return result


def zip_bytes(contents):
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, raw in contents.items():
            entry = zipfile.ZipInfo(name, date_time=(2026, 10, 28, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | 0o644) << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, raw, compresslevel=9)
    return stream.getvalue()


def package(output):
    output = pathlib.Path(output)
    if output.exists() or output.is_symlink():
        raise ValueError('Choose a new output directory; existing artifacts are preserved')
    sources = {}
    for name in (*SOURCES, 'updater.py'):
        path = ROOT / 'deploy' / name
        if path.is_symlink() or not stat.S_ISREG(path.lstat().st_mode):
            raise ValueError('Expected regular reviewed source: ' + name)
        raw = path.read_bytes()
        if not raw or len(raw) > 1024 * 1024 or b'\r' in raw:
            raise ValueError('Expected bounded canonical LF source: ' + name)
        compile(raw, name, 'exec', dont_inherit=True)
        sources[name] = raw
    settings = literals(sources['upgrade-worker-github.py'])
    for pin, name in PIN_FILES.items():
        if settings.get(pin) != hashlib.sha256(sources[name]).hexdigest():
            raise ValueError('Source differs from the reviewed bootstrap pin: ' + name)
    request = literals(sources['request-update-github.py'])
    if (request.get('BASE_SHA256') != hashlib.sha256(sources['request-update-manual.py']).hexdigest() or
            request.get('SUPERVISOR_SHA256') != settings['SUPERVISOR_SHA256'] or
            request.get('TRANSPORT_SHA256') != settings['TRANSPORT_SHA256'] or
            request.get('LAUNCHER_SHA256') != hashlib.sha256(settings['LAUNCHER']).hexdigest()):
        raise ValueError('Request helper differs from the reviewed immutable boundary')
    protocol = literals(sources['updater.py']).get('UPDATER_PROTOCOL')
    if type(protocol) is not int or protocol < 3:
        raise ValueError('HTTPS joined-release bootstrap requires worker protocol 3 or newer')
    version = settings['INITIAL_RUNTIME_VERSION']
    archive = zip_bytes({'updater.py': sources['updater.py']})
    manifest = dict(format=1, kind='yachad-updater', version=version, archive='yachad-updater.zip',
                    bytes=len(archive), sha256=hashlib.sha256(archive).hexdigest(), supervisorProtocol=1,
                    workerProtocol=protocol, helperProtocol=1, configFormat=1, stateFormat=1,
                    appManifestFormats=[1, 2])
    contents = {name: sources[name] for name in SOURCES}
    contents['yachad-updater.json'] = (json.dumps(manifest, separators=(',', ':')) + '\n').encode()
    contents['yachad-updater.zip'] = archive
    filename = 'yachad-github-updater-bootstrap-' + version + '.zip'
    payload = zip_bytes(contents)
    output.mkdir(mode=0o700)
    (output / filename).write_bytes(payload)
    digest = hashlib.sha256(payload).hexdigest()
    (output / 'SHA256SUMS').write_text(digest + '  ' + filename + '\n', encoding='ascii')
    (output / 'FILES.sha256').write_text(''.join(hashlib.sha256(raw).hexdigest() + '  ' + name + '\n'
                                              for name, raw in contents.items()), encoding='ascii')
    return filename, digest


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', help='New local output directory; never published')
    args = parser.parse_args()
    name, checksum = package(args.output)
    print(checksum + '  ' + name)
