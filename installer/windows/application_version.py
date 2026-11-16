"""Build-time release identities of the two tracks (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.3).

The app track is the checked-in package.json and lockfile; the infrastructure
track is installer/Directory.Build.props, compiled into every native image.
"""
from collections import namedtuple
import json
import re
import os
import subprocess
from pathlib import Path
import xml.etree.ElementTree as ElementTree

_STABLE = re.compile(r'(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', re.ASCII)
# InfrastructureRelease.IsContract: canonical digits, 1..1024.
_CONTRACT = re.compile(r'[1-9][0-9]{0,3}', re.ASCII)
_COMMIT = re.compile(r'[a-f0-9]{40}', re.ASCII)
PROPS = 'installer/Directory.Build.props'

Infrastructure = namedtuple('Infrastructure', ('version', 'contract_min', 'contract_max'))


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise ValueError('Duplicate package identity field')
        result[key] = value
    return result


def _stable(version):
    return isinstance(version, str) and len(version) <= 40 and _STABLE.fullmatch(version) is not None


def _contract(value):
    return type(value) is int and 1 <= value <= 1024


def _package(data):
    if len(data) > 65536: raise ValueError('Package identity too large')
    package = json.loads(data, object_pairs_hook=_object)
    if not isinstance(package, dict): raise ValueError('Package identity object required')
    return package


def package_release_version(data):
    version = _package(data).get('version')
    if not _stable(version): raise ValueError('Canonical stable application version required')
    return version


def package_infrastructure_contract(data, infrastructure):
    # The one contract the app is built against (plan section 6): an exact integer
    # inside the range the infrastructure built from the same source serves.
    contract = _package(data).get('yahadInfrastructureContract')
    if not _contract(contract): raise ValueError('Application infrastructure contract required')
    if not infrastructure.contract_min <= contract <= infrastructure.contract_max:
        raise ValueError('Application infrastructure contract outside the infrastructure range')
    return contract


def _order(version): return tuple(int(part) for part in version.split('.'))


def package_minimum_from(data, app_version, infrastructure_version):
    # The oldest installed pair a release upgrades (ReleaseManifest.MinimumFrom,
    # docs/WINDOWS-APP-PACKAGE-PLAN.md 5.1): a release policy declared once, beside
    # the app version it belongs to. Exactly {app, infrastructure}, each canonical
    # and never newer than the release's own version of that track.
    value = _package(data).get('yahadWindowsMinimumFrom')
    if not isinstance(value, dict) or sorted(value) != ['app', 'infrastructure'] or not all(_stable(v) for v in value.values()):
        raise ValueError('Windows minimumFrom declaration required')
    if _order(value['app']) > _order(app_version) or _order(value['infrastructure']) > _order(infrastructure_version):
        raise ValueError('Windows minimumFrom newer than the release')
    return dict(app=value['app'], infrastructure=value['infrastructure'])


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


def infrastructure_identity_from_props(data):
    # The exact MSBuild properties InfrastructureRelease reads back as assembly
    # metadata: each defined once, literal (no property reference), canonical.
    if len(data) > 65536 or data.startswith(b'\xef\xbb\xbf'): raise ValueError('Infrastructure identity file')
    root = ElementTree.fromstring(data)
    if root.tag != 'Project': raise ValueError('Infrastructure identity file')
    def value(name):
        found = [element for element in root.iter(name)]
        if len(found) != 1 or len(found[0]) or found[0].attrib or not isinstance(found[0].text, str):
            raise ValueError('Infrastructure identity property: '+name)
        return found[0].text
    version, low, high = (value('YahadInfrastructure'+name) for name in ('Version', 'ContractMin', 'ContractMax'))
    if not _stable(version) or not _CONTRACT.fullmatch(low) or not _CONTRACT.fullmatch(high):
        raise ValueError('Infrastructure identity value')
    identity = Infrastructure(version, int(low), int(high))
    if not _contract(identity.contract_min) or not _contract(identity.contract_max) or identity.contract_min > identity.contract_max:
        raise ValueError('Infrastructure contract range')
    return identity


def source_infrastructure_identity(repo):
    return infrastructure_identity_from_props((Path(repo)/PROPS).read_bytes())


def require_native_build_identity(description, artifact, infrastructure, source_commit):
    # Native images are infrastructure: they carry its identity and never the app's.
    if (not isinstance(description, dict) or description.get('artifact') != artifact or 'applicationVersion' in description or
            description.get('infrastructureVersion') != infrastructure.version or
            type(description.get('contractMin')) is not int or description['contractMin'] != infrastructure.contract_min or
            type(description.get('contractMax')) is not int or description['contractMax'] != infrastructure.contract_max or
            not isinstance(source_commit, str) or not _COMMIT.fullmatch(source_commit) or description.get('sourceCommit') != source_commit or
            description.get('version') != infrastructure.version+'+'+source_commit):
        raise ValueError('Native artifact infrastructure/source identity differs')
    return description


def describe_build_artifact(executable, mode, cwd):
    # Audit-only entry points on locally built self-contained artifacts. No
    # PATH/global runtime/credential injection; no installer or SCM entry mode.
    environment = {key:os.environ[key] for key in ('SystemRoot','WINDIR','TEMP','TMP','APPDATA','LOCALAPPDATA') if key in os.environ}
    # Yahad.Setup's manifest requires administrator rights. RunAsInvoker runs
    # these unprivileged audits with the caller's token instead of failing with
    # ERROR_ELEVATION_REQUIRED (740); it never prompts and never elevates.
    environment.update(PATH='',DOTNET_EnableDiagnostics='0',__COMPAT_LAYER='RunAsInvoker')
    return json.loads(subprocess.check_output([str(Path(executable).resolve()), mode], cwd=cwd,
        env=environment, text=True, timeout=30), object_pairs_hook=_object)
