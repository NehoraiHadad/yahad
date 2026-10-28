#!/usr/bin/env python3
"""Owner-enrolled GitHub HTTPS updater runtime; no signing keys or live downloads.

Only exact known direct-worker installations can enroll. The owner authorizes
future root-worker code from NehoraiHadad/yahad stable Releases in one local,
interactive bootstrap. Existing app trust/configuration/data are preserved.
Signed installations are never converted. Supervisor/helpers/units remain an
immutable boundary requiring another reviewed local bootstrap to change.
"""
import sys
if __name__ == '__main__' and not sys.flags.isolated:
    raise SystemExit('Use isolated Python: sudo python3 -I -B upgrade-worker-github.py [--check-only]')

import argparse
import fcntl
import hashlib
import json
import os
import pathlib
import signal
import stat
import subprocess
import tempfile
import types
import uuid

BASE_SHA256 = 'f269352cadc965237f5e48ab2c7c9e5fdddb2ae6482bedb273f8afb9f4df78e5'
SUPERVISOR_SHA256 = 'a432cb808d9e56a58e87684e8899b00667949c3acef68ed3a2d4beb65065291e'
TRANSPORT_SHA256 = 'bb81e88782aade42c8251a237ccd6c555d7c02452f718819f0c9c5d8229e4010'
INITIAL_WORKER_SHA256 = 'c29b97e7eb62e516cfbd270befb4f4af8592b38d2294a1b102447dc8a78484b3'
INITIAL_RUNTIME_VERSION = '1.0.1'
SUPERVISOR = '/usr/local/lib/yachad-updater/updater-supervisor.py'
TRANSPORT = '/usr/local/lib/yachad-updater/updater-transport.py'
SOURCE_POLICY = '/etc/yachad/updater-source.json'
KEY = '/etc/yachad/updater-signing.pub'
APP_KEY = '/etc/yachad/release-signing.pub'
LAUNCHER = b'''#!/usr/bin/env python3
"""Owner-enrolled GitHub HTTPS runtime launcher; trust is fixed outside releases."""
import os
import sys
os.execv('/usr/bin/python3', ['/usr/bin/python3', '-I', '-B', '/usr/local/lib/yachad-updater/updater-supervisor.py', *sys.argv[1:]])
'''
LAUNCHER_SHA256 = hashlib.sha256(LAUNCHER).hexdigest()
READ_ONLY = (SOURCE_POLICY, SUPERVISOR, TRANSPORT, '/usr/local/lib/yachad-updater/updater.py',
             '/usr/local/lib/yachad-updater/data-lock.py', '/usr/local/lib/yachad-updater/export-dependencies.py')
BOUNDARY = ('[Service]\nReadOnlyPaths=' + ' '.join(READ_ONLY) + '\n').encode()
CONFIRMATION = 'ENABLE GITHUB UPDATER'


def reviewed_module(path, digest):
    """No search-path imports; execute only exact bounded, reviewed sibling bytes."""
    path = pathlib.Path(path).absolute()
    if path.resolve(strict=True) != path:
        raise ValueError('Reviewed bootstrap source may not use symlinks')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        before = os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= 1024 * 1024:
            raise ValueError('Invalid reviewed source')
        with os.fdopen(os.dup(fd), 'rb') as stream:
            raw = stream.read(1024 * 1024 + 1)
        after = os.fstat(fd)
        if ((before.st_size, before.st_mtime_ns, before.st_ctime_ns) !=
                (after.st_size, after.st_mtime_ns, after.st_ctime_ns) or
                len(raw) != before.st_size or hashlib.sha256(raw).hexdigest() != digest):
            raise ValueError('Reviewed bootstrap source hash differs')
    finally:
        os.close(fd)
    module = types.ModuleType('reviewed_' + path.stem.replace('-', '_'))
    module.__file__ = str(path)
    exec(compile(raw, str(path), 'exec', dont_inherit=True), module.__dict__)
    return raw, module


_, base = reviewed_module(pathlib.Path(__file__).with_name('upgrade-worker-manual.py'), BASE_SHA256)
UpgradeError = base.UpgradeError
TARGET, CONFIG, STATE = base.TARGET, base.CONFIG, base.STATE
SCHEDULERS, WORKERS, RECOVERY_UNIT = base.SCHEDULERS, base.WORKERS, base.RECOVERY_UNIT
OLD_SHA256, DIRECT_WORKER_SHA256 = base.OLD_SHA256, base.DIRECT_WORKER_SHA256
HELPER_SHA256, MAX_SOURCE = base.HELPER_SHA256, base.MAX_SOURCE
RUNTIME_FILES = {'updater.py', 'manifest.json', 'payload.zip'}


class Bootstrap(base.Bootstrap):
    def __init__(self, root=pathlib.Path('/'), source=None, *, runtime_dir=None,
                 inspect_units=base.unit_states, scheduler=base.scheduler_action,
                 ask=input, apply_boundary=None):
        source = pathlib.Path(source) if source is not None else pathlib.Path(__file__).absolute().with_name('updater-supervisor.py')
        super().__init__(root, source, inspect_units, scheduler, ask)
        self.runtime_dir = pathlib.Path(runtime_dir) if runtime_dir is not None else source.parent
        self.apply_boundary = apply_boundary or self.reload_boundary

    def inputs(self):
        source, supervisor = reviewed_module(self.source, SUPERVISOR_SHA256)
        transport = self.external_file(self.source.with_name('updater-transport.py'), MAX_SOURCE)
        if hashlib.sha256(transport).hexdigest() != TRANSPORT_SHA256:
            raise UpgradeError('Immutable transport differs from reviewed bytes; nothing changed')
        raw = self.external_file(self.runtime_dir / 'yachad-updater.json', 8192)
        archive = self.external_file(self.runtime_dir / 'yachad-updater.zip', 2 * 1024 * 1024)
        if os.path.lexists(self.runtime_dir / 'yachad-updater.sig'):
            raise UpgradeError('This bootstrap accepts only the explicit no-signature initial runtime package')
        manifest, worker = supervisor.verify_https_bundle(raw, archive)
        if (manifest['version'] != INITIAL_RUNTIME_VERSION or
                hashlib.sha256(worker).hexdigest() != INITIAL_WORKER_SHA256):
            raise UpgradeError('Initial runtime is not the exact locally reviewed version')
        policy = dict(format=2, trust='github-https', enabled=True,
                      repository='NehoraiHadad/yahad', channel='stable', checkHours=1 / 12)
        return dict(supervisor=source, module=supervisor, transport=transport, raw=raw,
                    archive=archive, manifest=manifest, worker=worker,
                    source_policy=json.dumps(policy, separators=(',', ':')).encode())

    def helpers(self):
        result = {}
        for name, digests in HELPER_SHA256.items():
            raw, _ = self.read_file(self.path(TARGET).with_name(name), limit=MAX_SOURCE)
            if hashlib.sha256(raw).hexdigest() not in digests:
                raise UpgradeError('Installed helper is not exact reviewed bytes; preserved: ' + name)
            result[name] = raw
        return result

    def no_signed_trust(self):
        for value in (KEY, APP_KEY):
            if os.path.lexists(self.path(value)):
                raise UpgradeError('Existing signature pin is preserved; HTTPS enrollment refused')
        path = self.path(SOURCE_POLICY)
        if os.path.lexists(path):
            value = json.loads(self.read_file(path, private=True)[0])
            if (not isinstance(value, dict) or
                    set(value) != {'format', 'trust', 'enabled', 'repository', 'channel', 'checkHours'} or
                    type(value['format']) is not int or value['format'] != 2 or value['trust'] != 'github-https' or
                    type(value['enabled']) is not bool or value['repository'] != 'NehoraiHadad/yahad' or
                    value['channel'] != 'stable' or type(value['checkHours']) not in (int, float) or not 1 / 12 <= value['checkHours'] <= 168):
                raise UpgradeError('Signed, unknown or conflicting source policy is preserved; enrollment refused')

    def state(self):
        config, _ = self.read_file(self.path(CONFIG), private=True)
        status, _ = self.read_file(self.path(STATE) / 'status.json', limit=65536)
        try:
            settings, journal = json.loads(config), json.loads(status)
            if (not isinstance(settings, dict) or settings.get('stateDirectory') != STATE or
                    settings.get('service') != 'yachad.service' or settings.get('appUser') != 'yachad' or
                    settings.get('builderUser') != 'yachad-update' or settings.get('repository') != 'NehoraiHadad/yahad' or
                    settings.get('releaseTrust') != 'github-https' or settings.get('publicKeyFile') is not None or
                    settings.get('tokenFile') is not None or type(settings.get('enabled')) is not bool or
                    settings.get('mode') not in ('auto', 'notify') or type(settings.get('allowUiModeControl', False)) is not bool):
                raise ValueError()
            operator = settings.get('operator')
            if not isinstance(operator, dict) or set(operator) != {'familyId', 'userId'}:
                raise ValueError()
            for value in operator.values():
                if not isinstance(value, str) or str(uuid.UUID(value)) != value:
                    raise ValueError()
            if (not isinstance(journal, dict) or journal.get('operator') != operator or
                    journal.get('releaseTrust') != 'github-https' or journal.get('phase') not in base.STABLE_PHASES or
                    journal.get('modeChangePending') is not None or
                    isinstance(journal.get('lastModeRequest'), dict) and journal['lastModeRequest'].get('state') == 'pending' or
                    isinstance(journal.get('lastRequest'), dict) and journal['lastRequest'].get('state') == 'running'):
                raise ValueError()
        except (ValueError, TypeError, AttributeError):
            raise UpgradeError('App trust/configuration/journal is incompatible or has unfinished work; preserved') from None
        self.no_signed_trust()
        return config, status

    @staticmethod
    def sync(path):
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)

    def directory(self, path, mode=0o700):
        if os.path.lexists(path):
            self.ancestors(path)
            return
        self.ancestors(path.parent)
        path.mkdir(mode=mode)
        os.chown(path, 0, 0)
        os.chmod(path, mode)
        self.sync(path.parent)

    def exact_file(self, path, content, mode=0o600):
        if os.path.lexists(path):
            actual, _ = self.read_file(path, limit=max(len(content), 8192))
            if actual != content:
                raise UpgradeError('Existing bootstrap file differs; preserved: ' + str(path.relative_to(self.root)))
            return
        self.ancestors(path.parent)
        fd, temporary = tempfile.mkstemp(prefix='.github-bootstrap-', dir=path.parent)
        try:
            with os.fdopen(fd, 'wb') as stream:
                os.fchown(stream.fileno(), 0, 0); os.fchmod(stream.fileno(), mode)
                stream.write(content); stream.flush(); os.fsync(stream.fileno())
            if os.path.lexists(path):
                raise UpgradeError('Bootstrap target appeared concurrently; preserved')
            os.replace(temporary, path)
            self.sync(path.parent)
        finally:
            pathlib.Path(temporary).unlink(missing_ok=True)

    def boundary_paths(self):
        return [self.path('/etc/systemd/system/' + unit + '.d/60-updater-supervisor.conf')
                for unit in (*WORKERS, RECOVERY_UNIT)]

    def plan(self, inputs):
        reference = dict(version=inputs['manifest']['version'], sha256=inputs['manifest']['sha256'],
                         manifestSha256=hashlib.sha256(inputs['raw']).hexdigest())
        runtime = self.path(STATE) / 'runtime'
        destination = runtime / 'versions' / ('v' + reference['version'])
        journal = dict(format=1, phase='stable', active=reference, previous=None,
                       candidate=None, maxAcceptedVersion=reference['version'])
        planned = [(self.path(SUPERVISOR), inputs['supervisor'], 0o644),
                   (self.path(TRANSPORT), inputs['transport'], 0o644),
                   (self.path(SOURCE_POLICY), inputs['source_policy'], 0o600),
                   (destination / 'updater.py', inputs['worker'], 0o600),
                   (destination / 'manifest.json', inputs['raw'], 0o600),
                   (destination / 'payload.zip', inputs['archive'], 0o600),
                   (self.path(STATE) / 'updater-runtime.json', json.dumps(journal, separators=(',', ':')).encode(), 0o600),
                   *((path, BOUNDARY, 0o644) for path in self.boundary_paths())]
        return reference, runtime, destination, planned

    def preflight_new(self, inputs):
        reference, runtime, destination, planned = self.plan(inputs)
        for path, content, _ in planned:
            parent = path.parent
            while not os.path.lexists(parent): parent = parent.parent
            self.ancestors(parent)
            if os.path.lexists(path):
                actual, _ = self.read_file(path, limit=max(len(content), 8192))
                if actual != content:
                    raise UpgradeError('Conflicting or signed partial enrollment preserved: ' + str(path.relative_to(self.root)))
        current = runtime / 'current'
        if os.path.lexists(current):
            info = current.lstat()
            if not stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or os.readlink(current) != 'versions/v' + reference['version']:
                raise UpgradeError('Existing runtime pointer differs; preserved')
        if destination.exists() and {p.name for p in destination.iterdir()} - RUNTIME_FILES:
            raise UpgradeError('Initial runtime has unexpected files; preserved')
        if runtime.exists() and {p.name for p in runtime.iterdir()} - {'versions', 'current'}:
            raise UpgradeError('Unknown partial runtime enrollment is preserved')
        versions = runtime / 'versions'
        if versions.exists() and {p.name for p in versions.iterdir()} - {destination.name}:
            raise UpgradeError('Unknown prior runtime versions are preserved; enrollment refused')

    def reload_boundary(self):
        try:
            result = subprocess.run(['/usr/bin/systemctl', 'daemon-reload'], capture_output=True, timeout=30)
            if result.returncode: raise UpgradeError('Unit reload failed; launcher remains unactivated')
            for unit in (*WORKERS, RECOVERY_UNIT):
                result = subprocess.run(['/usr/bin/systemctl', 'show', unit, '--no-pager',
                                         '--property=ReadOnlyPaths,NeedDaemonReload'], capture_output=True, timeout=10)
                values = dict(line.split('=', 1) for line in result.stdout.decode('utf8').splitlines())
                if result.returncode or values.get('NeedDaemonReload') != 'no' or not set(READ_ONLY).issubset(set(values.get('ReadOnlyPaths', '').split())):
                    raise UpgradeError('Read-only immutable boundary was not confirmed; launcher remains unactivated')
        except (OSError, ValueError, UnicodeError, subprocess.TimeoutExpired):
            raise UpgradeError('Unit boundary verification is unconfirmed; launcher remains unactivated') from None

    def provision(self, inputs, lock):
        reference, runtime, destination, planned = self.plan(inputs)
        # Only exact absent/resumable files are provisioned. App config is never written.
        self.directory(runtime); self.directory(runtime / 'versions'); self.directory(destination)
        for path in self.boundary_paths(): self.directory(path.parent, 0o755)
        for path, content, mode in planned: self.exact_file(path, content, mode)
        if {p.name for p in destination.iterdir()} != RUNTIME_FILES:
            raise UpgradeError('Initial runtime has unexpected files')
        current, target = runtime / 'current', 'versions/v' + reference['version']
        if not os.path.lexists(current):
            current.symlink_to(target); self.sync(runtime)
        self.apply_boundary()
        supervisor = inputs['module'].Supervisor(self.root)
        supervisor.lock_fd = lock
        supervisor.verify_runtime(reference)
        supervisor.probe(reference)

    def verify_provisioned(self, inputs):
        for path, expected in [(self.path(SUPERVISOR), inputs['supervisor']),
                               (self.path(TRANSPORT), inputs['transport']),
                               *((path, BOUNDARY) for path in self.boundary_paths())]:
            if self.read_file(path, limit=max(len(expected), 8192))[0] != expected:
                raise UpgradeError('Installed immutable boundary differs; preserved')
        self.no_signed_trust()
        supervisor = inputs['module'].Supervisor(self.root)
        journal = supervisor.journal()
        if journal['phase'] != 'stable' or supervisor.pointer() != 'versions/v' + journal['active']['version']:
            raise UpgradeError('Runtime work/recovery is pending; preserve the journal')
        supervisor.verify_runtime(journal['active'])
        # Read-only idempotence: no candidate execution, policy reset or scheduler calls.

    def run(self, check_only=False):
        lock_path = self.path(STATE) / 'worker.lock'
        self.ancestors(lock_path.parent)
        lock = os.open(lock_path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            info = os.fstat(lock)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o600:
                raise UpgradeError('Unsafe updater lock; nothing changed')
            try: fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError: raise UpgradeError('Updater is busy; nothing changed') from None
            inputs = self.inputs()
            state, helpers = self.state(), self.helpers()
            old, previous = self.read_file(self.path(TARGET), limit=MAX_SOURCE)
            digest = hashlib.sha256(old).hexdigest()
            if digest not in (OLD_SHA256, DIRECT_WORKER_SHA256, INITIAL_WORKER_SHA256, LAUNCHER_SHA256):
                raise UpgradeError('Worker is not an exact reviewed direct worker or this HTTPS launcher; preserved')
            schedulers = self.quiet(allow_schedulers=True)
            if digest == LAUNCHER_SHA256:
                self.verify_provisioned(inputs)
                return 'ALREADY_CURRENT: HTTPS enrollment and active runtime verified; no files or services changed'
            if json.loads(state[1]).get('runtimeDigest') is not None:
                raise UpgradeError('Direct worker has a runtime-bound app journal; preserve it for local inspection')
            self.preflight_new(inputs)
            print('Trust fixed NehoraiHadad/yahad stable Releases over certificate-verified HTTPS for future privileged updater code.')
            print('No signatures/keys: GitHub repository control is publisher authority; hashes check bytes, not independent publisher identity.')
            print('Updater runtime updates become enabled; existing app auto/notify, operator, version pin and paths remain unchanged.')
            print('Immutable supervisor/transport/helpers/unit permissions cannot update themselves through Releases.')
            print('This bootstrap does not install an app or migrate the DB. Restored schedulers may immediately run existing authorized updates.')
            print('Prior scheduler runtime states: ' + ', '.join(unit + '=' + ('active' if active else 'inactive') for unit, active in schedulers.items()))
            print('Save these states. Forced termination/power loss or uncertain durability may leave schedulers paused for local inspection.')
            if check_only: return 'CHECK_OK: explicit HTTPS enrollment is eligible; no files or services changed'
            if self.ask('Type ' + CONFIRMATION + ' to authorize and install this boundary: ').strip() != CONFIRMATION:
                return 'CANCELED: no files or services changed'
            if self.quiet(allow_schedulers=True) != schedulers or self.state() != state or self.helpers() != helpers:
                raise UpgradeError('Installation changed during confirmation; nothing changed')
            self.unchanged_target(old, previous)
            self.preflight_new(inputs)
            temporary, committed, verified = None, False, False
            try:
                for unit, active in schedulers.items():
                    if active: self.scheduler('stop', unit)
                self.quiet()
                if self.state() != state or self.helpers() != helpers:
                    raise UpgradeError('Installation changed during quiescence; launcher remains unactivated')
                self.unchanged_target(old, previous)
                self.preflight_new(inputs)
                self.provision(inputs, lock)
                self.quiet()
                if self.state() != state or self.helpers() != helpers:
                    raise UpgradeError('Runtime probe changed protected state; launcher remains unactivated')
                self.unchanged_target(old, previous)
                fd, temporary = tempfile.mkstemp(prefix='.github-launcher-', dir=self.path(TARGET).parent)
                with os.fdopen(fd, 'wb') as stream:
                    os.fchown(stream.fileno(), previous.st_uid, previous.st_gid)
                    os.fchmod(stream.fileno(), stat.S_IMODE(previous.st_mode))
                    stream.write(LAUNCHER); stream.flush(); os.fsync(stream.fileno())
                committed = True
                os.replace(temporary, self.path(TARGET))
                self.sync(self.path(TARGET).parent)
                actual, actual_info = self.read_file(self.path(TARGET), limit=MAX_SOURCE)
                if actual != LAUNCHER or self.metadata(actual_info) != self.metadata(previous):
                    raise UpgradeError('Launcher replacement verification failed')
                verified = True
            finally:
                cleanup_failed = False
                if temporary is not None:
                    try: pathlib.Path(temporary).unlink(missing_ok=True)
                    except OSError: cleanup_failed = True
                try:
                    unchanged = self.state() == state and self.helpers() == helpers
                    if not committed or not verified: self.unchanged_target(old, previous)
                except Exception: unchanged = False
                if not unchanged:
                    raise UpgradeError('Installation/replacement is unconfirmed; schedulers remain paused for local inspection. No app/database/config rollback attempted') from None
                self.restore_schedulers(schedulers)
                if cleanup_failed: raise UpgradeError('Known state restored; inspect incomplete staging cleanup locally')
            return 'GITHUB_UPDATER_ENROLLED: reviewed launcher/runtime and fixed HTTPS source provisioned; app/data/config preserved; prior schedulers restored'
        finally:
            os.close(lock)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check-only', action='store_true', help='Read-only eligibility/installed-boundary check')
    parser.add_argument('--runtime-dir', help='Local reviewed initial-runtime directory; defaults beside the script')
    args = parser.parse_args()
    base.owner_guard(args.check_only)
    def interrupted(_signal, _frame): raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupted); signal.signal(signal.SIGHUP, interrupted)
    print(Bootstrap(runtime_dir=args.runtime_dir).run(check_only=args.check_only))


if __name__ == '__main__':
    try: main()
    except KeyboardInterrupt:
        print('GITHUB_ENROLLMENT_INTERRUPTED: inspect --check-only and prior scheduler states before retrying', file=sys.stderr)
        sys.exit(1)
    except Exception as error:
        print(str(error) if isinstance(error, UpgradeError) else 'GITHUB_ENROLLMENT_STOPPED: inspect local reviewed files/permissions; no private output logged', file=sys.stderr)
        sys.exit(1)
