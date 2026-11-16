"""The infrastructure input digest (docs/WINDOWS-APP-PACKAGE-PLAN.md section 10).

One hex SHA-256 over the Git object ids of every committed input of the
infrastructure track, so two commits with the same digest build the same
infrastructure, whatever else differs between them. Used by installer/package.ps1
(whether a cached infrastructure build may be reused) and by
scripts/release-windows.mjs (the version bump rule, comparing HEAD against the
latest release's infrastructure.sourceCommit without a checkout of it).

Inputs (INPUTS below), each read from `git ls-tree -r` of the commit:
- the .NET projects of the track, whole directories: installer/Yahad.ServiceHost,
  Yahad.Updater, Yahad.Platform, Yahad.Setup, Yahad.Install.Core. Test projects
  are their own directories (Yahad.*.Tests) and are not inputs;
- installer/Directory.Build.props (the infrastructure identity and contract range)
  and installer/NuGet.Config (the package source);
- installer/package.ps1 (it pins the .NET runtime version, RuntimeFrameworkVersion)
  and installer/windows/ (the packagers, dependency-pins.json with the Node and
  Caddy pins, and the license supplements);
- installer/release-key.spki, the committed release key compiled into the images.

The digested text is a fixed prefix, then one line per input file in ordinal path
order: `<mode> <blob id> <path>`. A missing input, a submodule or a link is
refused rather than skipped.

CLI: python installer/windows/infra_inputs.py digest [--commit <sha>] [--repo <checkout>]
Without --commit it digests HEAD and refuses uncommitted changes to any input.
"""
import argparse
import hashlib
from pathlib import Path
import re
import subprocess

PREFIX = b'yahad-infrastructure-inputs-v1\n'
DIRECTORIES = ('installer/Yahad.ServiceHost/', 'installer/Yahad.Updater/', 'installer/Yahad.Platform/',
               'installer/Yahad.Setup/', 'installer/Yahad.Install.Core/', 'installer/windows/')
FILES = ('installer/Directory.Build.props', 'installer/NuGet.Config', 'installer/package.ps1', 'installer/release-key.spki')
INPUTS = DIRECTORIES + FILES
_COMMIT = re.compile(r'[a-f0-9]{40}')
_ENTRY = re.compile(rb'(100644|100755) blob ([a-f0-9]{40}|[a-f0-9]{64})\t(.+)', re.S)


class InputError(ValueError): pass


def _git(repo, *args):
    return subprocess.check_output(['git', '-c', 'core.quotepath=off', '-C', str(repo), *args])


def resolve(repo, commit=None):
    """The full commit id digested: --commit as given (a full id, no ref), else HEAD."""
    if commit is not None:
        if not _COMMIT.fullmatch(commit): raise InputError('A full 40-hex commit id required')
        if _git(repo, 'cat-file', '-t', commit).strip() != b'commit': raise InputError('Not a commit: '+commit)
        return commit
    head = _git(repo, 'rev-parse', '--verify', 'HEAD^{commit}').decode('ascii').strip()
    if not _COMMIT.fullmatch(head): raise InputError('Committed Git checkout required')
    # HEAD's digest describes a checkout only when its inputs are as committed.
    if _git(repo, 'status', '--porcelain', '--untracked-files=all', '--', *INPUTS).strip():
        raise InputError('Uncommitted changes to infrastructure inputs')
    return head


def entries(repo, commit):
    """[(path, mode, object id)] of every input file at commit, in ordinal path order."""
    result = []
    for line in _git(repo, 'ls-tree', '-r', '-z', '--full-tree', commit, '--', *INPUTS).split(b'\0'):
        if not line: continue
        match = _ENTRY.fullmatch(line)
        if not match: raise InputError('Unsupported infrastructure input entry: '+line.decode('utf-8', 'replace'))
        mode, oid, path = (part.decode('utf-8') for part in match.groups())
        if not path.startswith(DIRECTORIES) and path not in FILES: raise InputError('Undeclared infrastructure input: '+path)
        result.append((path, mode, oid))
    result.sort(key=lambda entry: entry[0].encode('utf-16-be'))
    present = {path for path, _, _ in result}
    for name in FILES:
        if name not in present: raise InputError('Missing infrastructure input: '+name)
    for name in DIRECTORIES:
        if not any(path.startswith(name) for path in present): raise InputError('Missing infrastructure input: '+name)
    return result


def digest(repo, commit=None):
    repo = Path(repo)
    commit = resolve(repo, commit)
    hashed = hashlib.sha256(PREFIX)
    for path, mode, oid in entries(repo, commit): hashed.update(f'{mode} {oid} {path}\n'.encode('utf-8'))
    return hashed.hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='operation', required=True)
    computing = sub.add_parser('digest')
    computing.add_argument('--commit')
    computing.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    try: print(digest(args.repo, args.commit))
    except (InputError, subprocess.CalledProcessError, OSError) as error: raise SystemExit('INFRA_INPUTS_REFUSED: '+str(error))


if __name__ == '__main__': main()
