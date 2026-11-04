#!/usr/bin/env python3
"""A root updater's runtime pair: yachad-updater.zip holding updater.py, and its manifest.

Offline and deterministic for one zlib. Both the standalone bootstrap delivery and
install.py build the initial pair here; authority over the worker bytes stays with
the bootstrap's pin, which never covers the archive bytes.
"""
import hashlib
import io
import json
import stat
import zipfile


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


def runtime_pair(worker, version, protocol):
    archive = zip_bytes({'updater.py': worker})
    manifest = dict(format=1, kind='yachad-updater', version=version, archive='yachad-updater.zip',
                    bytes=len(archive), sha256=hashlib.sha256(archive).hexdigest(), supervisorProtocol=1,
                    workerProtocol=protocol, helperProtocol=1, configFormat=1, stateFormat=1,
                    appManifestFormats=[1, 2])
    return (json.dumps(manifest, separators=(',', ':')) + '\n').encode(), archive
