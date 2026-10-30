"""Build-time application identity from the checked-in package and lockfile."""
import json
import re
import os
import subprocess
from pathlib import Path

_STABLE = re.compile(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', re.ASCII)


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise ValueError('Duplicate package identity field')
        result[key] = value
    return result


def package_release_version(data):
    if len(data) > 65536: raise ValueError('Package identity too large')
    package = json.loads(data, object_pairs_hook=_object)
    version = package.get('version') if isinstance(package, dict) else None
    if not isinstance(version, str) or len(version) > 40 or not _STABLE.fullmatch(version):
        raise ValueError('Canonical stable application version required')
    return version


def source_release_version(repo):
    repo = Path(repo)
    version = package_release_version((repo/'package.json').read_bytes())
    lock = json.loads((repo/'package-lock.json').read_bytes(), object_pairs_hook=_object)
    packages = lock.get('packages') if isinstance(lock, dict) else None
    root_package = packages.get('') if isinstance(packages, dict) else None
    if (not isinstance(lock, dict) or lock.get('version') != version or
            not isinstance(root_package, dict) or root_package.get('version') != version):
        raise ValueError('Source package and lockfile versions differ')
    return version


def require_source_release_version(version, repo):
    expected = source_release_version(repo)
    if version != expected: raise ValueError('Payload version differs from source')
    return expected


def require_native_build_identity(description, artifact, version, source_commit):
    if (not isinstance(description, dict) or description.get('artifact') != artifact or
            description.get('applicationVersion') != version or description.get('sourceCommit') != source_commit or
            not isinstance(description.get('version'), str) or description['version'].split('+')[-1] != source_commit):
        raise ValueError('Native artifact release/source identity differs')
    return description


def describe_build_artifact(executable, mode, cwd):
    # Audit-only entry points on locally built self-contained artifacts. No
    # PATH/global runtime/credential injection; no installer or SCM entry mode.
    environment = {key:os.environ[key] for key in ('SystemRoot','WINDIR','TEMP','TMP','APPDATA','LOCALAPPDATA') if key in os.environ}
    environment.update(PATH='',DOTNET_EnableDiagnostics='0')
    return json.loads(subprocess.check_output([str(Path(executable).resolve()), mode], cwd=cwd,
        env=environment, text=True, timeout=30), object_pairs_hook=_object)
