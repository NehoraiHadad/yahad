#!/usr/bin/env python3
"""Reviewed, local-only supervisor bootstrap. Does not upgrade the app or database.

Run only after separate owner approval and a quiet maintenance window. The
updater timer/path are paused during replacement and normally restored afterward;
detected app configuration/journal mutation leaves them paused for local recovery.
The independent updater signing key grants privileged code authority. Existing
app configuration, release trust, account data and unit enablement are preserved.
New explicit read-only unit drop-ins and signed-runtime state are provisioned.
No concurrent manual updater/installer commands may run during this operation.
Invoke with: sudo python3 -I -B deploy/upgrade-worker.py [options]
"""
import sys
if __name__ == '__main__' and not sys.flags.isolated:
    raise SystemExit('Use isolated Python: sudo python3 -I -B deploy/upgrade-worker.py [options]')

import argparse
import fcntl
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import tempfile
import re
import types

OLD_SHA256 = '6b1523a207ecff1ad7bff19c80d85809f4e453125bf5b5ecece64463465a6cad'
# Both enrollment baselines are exact reviewed bytes, never a version string or
# whatever worker happens to be shipped alongside an untrusted application.
DIRECT_WORKER_SHA256 = '22cd73dc810d08d90e1ec6af4e4b482bc60a0f42b44e1bf874c78682375264cd'
SUPERVISOR_SHA256 = 'a432cb808d9e56a58e87684e8899b00667949c3acef68ed3a2d4beb65065291e'
SUPERVISOR = '/usr/local/lib/yachad-updater/updater-supervisor.py'
TRANSPORT = '/usr/local/lib/yachad-updater/updater-transport.py'
TRANSPORT_SHA256 = 'bb81e88782aade42c8251a237ccd6c555d7c02452f718819f0c9c5d8229e4010'
SOURCE_POLICY = '/etc/yachad/updater-source.json'
KEY = '/etc/yachad/updater-signing.pub'
LAUNCHER = b'''#!/usr/bin/env python3
"""Owner-provisioned stable entrypoint; runtime trust lives in the supervisor."""
import os
import sys
os.execv('/usr/bin/python3', ['/usr/bin/python3', '-I', '-B', '/usr/local/lib/yachad-updater/updater-supervisor.py', *sys.argv[1:]])
'''
LAUNCHER_SHA256 = hashlib.sha256(LAUNCHER).hexdigest()
READ_ONLY = (KEY, SOURCE_POLICY, SUPERVISOR, TRANSPORT, '/usr/local/lib/yachad-updater/updater.py')
BOUNDARY = ('[Service]\nReadOnlyPaths=' + ' '.join(READ_ONLY) + '\n').encode()
TARGET = '/usr/local/lib/yachad-updater/updater.py'
CONFIG = '/etc/yachad/updates.json'
STATE = '/var/lib/yachad-updates'
SCHEDULERS = ('yachad-update.timer', 'yachad-update-request.path')
WORKERS = ('yachad-update.service', 'yachad-update-request.service')
QUIET_UNITS = SCHEDULERS + WORKERS
RECOVERY_UNIT = 'yachad-update-recover.service'
STABLE_PHASES = {'idle', 'healthy', 'available', 'incompatible', 'failed', 'rolled_back'}
MAX_SOURCE = 1024 * 1024


class UpgradeError(Exception):
    pass


def unit_states():
    """Read systemd state only; never stop/start/enable/disable any unit."""
    states = {}
    for unit in (*QUIET_UNITS, RECOVERY_UNIT):
        properties = ['LoadState', 'ActiveState', 'SubState']
        if unit not in SCHEDULERS:
            properties += ['MainPID', 'ControlPID']
        try:
            result = subprocess.run(['/usr/bin/systemctl', 'show', unit, '--no-pager',
                                     '--property=' + ','.join(properties)],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=10,
                                    env={'PATH': '/usr/bin:/bin', 'LANG': 'C', 'HOME': '/root'})
            if result.returncode:
                raise UpgradeError('Cannot verify updater service state; nothing changed')
            values = {}
            for line in result.stdout.decode('ascii').splitlines():
                key, value = line.split('=', 1)
                if key in values:
                    raise ValueError()
                values[key] = value
            if set(values) != set(properties):
                raise ValueError()
            if unit in SCHEDULERS:
                values.update(MainPID='0', ControlPID='0')
            states[unit] = values
        except (OSError, ValueError, UnicodeError, subprocess.TimeoutExpired):
            raise UpgradeError('Cannot verify updater service state; nothing changed') from None
    return states


def scheduler_action(action, unit):
    if action not in ('stop', 'start') or unit not in SCHEDULERS:
        raise UpgradeError('Unsupported scheduler operation')
    try:
        result = subprocess.run(['/usr/bin/systemctl', action, unit], stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=30,
                                env={'PATH': '/usr/bin:/bin', 'LANG': 'C', 'HOME': '/root'})
        if result.returncode:
            raise UpgradeError('Scheduler operation failed')
    except (OSError, subprocess.TimeoutExpired):
        raise UpgradeError('Scheduler operation is unconfirmed') from None


class Bootstrap:
    def __init__(self, root=pathlib.Path('/'), source=None, inspect_units=unit_states,
                 scheduler=scheduler_action, ask=input, runtime_dir=None, public_key=None,
                 key_fingerprint=None, apply_boundary=None, enable_updater_updates=False):
        self.root = pathlib.Path(root).absolute()
        self.source = pathlib.Path(source) if source is not None else pathlib.Path(__file__).absolute().with_name('updater-supervisor.py')
        self.inspect_units = inspect_units
        self.scheduler = scheduler
        self.ask = ask
        self.runtime_dir = pathlib.Path(runtime_dir) if runtime_dir is not None else None
        self.public_key = pathlib.Path(public_key) if public_key is not None else None
        self.key_fingerprint = key_fingerprint
        self.enable_updater_updates = enable_updater_updates
        self.apply_boundary = apply_boundary or self.reload_boundary

    def path(self, value):
        return self.root / value.lstrip('/')

    def ancestors(self, path):
        if not path.is_relative_to(self.root):
            raise UpgradeError('Privileged path is outside the expected installation')
        for directory in (path, *path.parents):
            if not directory.is_relative_to(self.root):
                break
            info = directory.lstat()
            if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
                raise UpgradeError('Unsafe privileged directory; nothing changed')

    def read_file(self, path, *, private=False, limit=8192):
        self.ancestors(path.parent)
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            info = os.fstat(fd)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or
                    info.st_mode & (0o077 if private else 0o022) or info.st_mode & 0o7000 or
                    not 0 < info.st_size <= limit):
                raise UpgradeError('Unsafe privileged file; nothing changed')
            with os.fdopen(os.dup(fd), 'rb') as stream:
                content = stream.read(limit + 1)
            if len(content) != info.st_size or len(content) > limit:
                raise UpgradeError('Privileged file changed during inspection; nothing changed')
            return content, info
        finally:
            os.close(fd)

    @staticmethod
    def external_file(path, limit):
        path = pathlib.Path(path).absolute()
        if path.resolve(strict=True) != path:
            raise UpgradeError('Input must not use symlinks; no provisioning performed')
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            before = os.fstat(fd)
            if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= limit:
                raise UpgradeError('Invalid local input file')
            with os.fdopen(os.dup(fd), 'rb') as stream:
                content = stream.read(limit + 1)
            after = os.fstat(fd)
            if (len(content) != before.st_size or
                    (before.st_size, before.st_mtime_ns, before.st_ctime_ns) !=
                    (after.st_size, after.st_mtime_ns, after.st_ctime_ns)):
                raise UpgradeError('Input changed during verification')
            return content
        finally:
            os.close(fd)

    def reviewed_source(self):
        content = self.external_file(self.source, MAX_SOURCE)
        if hashlib.sha256(content).hexdigest() != SUPERVISOR_SHA256:
            raise UpgradeError('Supervisor source does not match the reviewed SHA-256; no provisioning performed')
        # Execute ONLY the reviewed, pinned privileged verifier, never app/runtime
        # code or another file selected by a package. Compile exactly these bytes.
        module = types.ModuleType('reviewed_bootstrap_supervisor')
        module.__file__ = str(self.source)
        exec(compile(content, str(self.source), 'exec', dont_inherit=True), module.__dict__)
        return content, module

    def inputs(self):
        if (self.runtime_dir is None or self.public_key is None or
                not isinstance(self.key_fingerprint, str) or
                not re.fullmatch('[0-9a-fA-F]{64}', self.key_fingerprint)):
            raise UpgradeError('Provide a signed runtime directory, public PEM, and independently verified DER SHA-256 fingerprint')
        source, supervisor = self.reviewed_source()
        transport = self.external_file(self.source.with_name('updater-transport.py'), MAX_SOURCE)
        if hashlib.sha256(transport).hexdigest() != TRANSPORT_SHA256:
            raise UpgradeError('Transport source does not match the reviewed SHA-256; no provisioning performed')
        transport_module = types.ModuleType('reviewed_bootstrap_transport')
        transport_module.__file__ = str(self.source.with_name('updater-transport.py'))
        exec(compile(transport, transport_module.__file__, 'exec', dont_inherit=True), transport_module.__dict__)
        if type(self.enable_updater_updates) is not bool:
            raise UpgradeError('Explicit updater source selection must be boolean')
        policy = dict(format=1, enabled=self.enable_updater_updates, repository='NehoraiHadad/yahad',
                      channel='stable', checkHours=6)
        key = self.external_file(self.public_key, 8192)
        raw = self.external_file(self.runtime_dir / 'yachad-updater.json', 8192)
        signature = self.external_file(self.runtime_dir / 'yachad-updater.sig', 64)
        archive = self.external_file(self.runtime_dir / 'yachad-updater.zip', 2 * 1024 * 1024)
        try:
            with tempfile.TemporaryDirectory(prefix='yachad-bootstrap-verify-') as temporary:
                directory = pathlib.Path(temporary)
                der = supervisor.public_key_der(key, directory)
                if hashlib.sha256(der).hexdigest() != self.key_fingerprint.lower():
                    raise UpgradeError('Independent public-key fingerprint does not match; no provisioning performed')
                manifest, worker = supervisor.verify_bundle(raw, signature, archive, key, directory)
        except UpgradeError:
            raise
        except Exception:
            raise UpgradeError('Initial updater signature or protocol validation failed; no provisioning performed') from None
        return dict(supervisor=source, module=supervisor, transport=transport, transport_module=transport_module,
                    source_policy=json.dumps(policy, separators=(',', ':')).encode(), key=key, manifest=manifest,
                    raw=raw, signature=signature, archive=archive, worker=worker)

    def directory(self, path, mode=0o700):
        if path.exists() or path.is_symlink():
            self.ancestors(path)
            return
        self.ancestors(path.parent)
        path.mkdir(mode=mode)
        os.chown(path, 0, 0)
        os.chmod(path, mode)
        self.sync(path.parent)

    @staticmethod
    def sync(path):
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)

    def exact_file(self, path, content, mode=0o600):
        if path.exists() or path.is_symlink():
            actual, _ = self.read_file(path, limit=max(len(content), 8192))
            if actual != content:
                raise UpgradeError('Existing bootstrap file differs; trust and files were preserved: ' + str(path.relative_to(self.root)))
            return
        self.ancestors(path.parent)
        fd, temporary = tempfile.mkstemp(prefix='.bootstrap-', dir=path.parent)
        try:
            with os.fdopen(fd, 'wb') as output:
                os.fchown(output.fileno(), 0, 0)
                os.fchmod(output.fileno(), mode)
                output.write(content); output.flush(); os.fsync(output.fileno())
            # All mutation is under worker.lock in root-only directories. Refuse
            # any unexpected concurrent administrator; never overwrite a key.
            if path.exists() or path.is_symlink():
                raise UpgradeError('Bootstrap target appeared concurrently; it was preserved')
            os.replace(temporary, path)
            self.sync(path.parent)
        finally:
            pathlib.Path(temporary).unlink(missing_ok=True)

    def boundary_paths(self):
        return [self.path('/etc/systemd/system/' + unit + '.d/60-updater-supervisor.conf')
                for unit in (*WORKERS, RECOVERY_UNIT)]

    def preflight_new(self, inputs):
        reference = dict(version=inputs['manifest']['version'], sha256=inputs['manifest']['sha256'])
        runtime = self.path(STATE) / 'runtime'
        destination = runtime / 'versions' / ('v' + reference['version'])
        journal = dict(format=1, phase='stable', active=reference, previous=None,
                       candidate=None, maxAcceptedVersion=reference['version'])
        planned = [(self.path(KEY), inputs['key']), (self.path(SUPERVISOR), inputs['supervisor']),
                   (self.path(TRANSPORT), inputs['transport']), (self.path(SOURCE_POLICY), inputs['source_policy']),
                   (destination / 'updater.py', inputs['worker']), (destination / 'manifest.json', inputs['raw']),
                   (destination / 'manifest.sig', inputs['signature']), (destination / 'payload.zip', inputs['archive']),
                   (self.path(STATE) / 'updater-runtime.json', json.dumps(journal, separators=(',', ':')).encode()),
                   *((path, BOUNDARY) for path in self.boundary_paths())]
        for path, content in planned:
            parent = path.parent
            while not parent.exists() and not parent.is_symlink():
                parent = parent.parent
            self.ancestors(parent)
            if path.exists() or path.is_symlink():
                actual, _ = self.read_file(path, limit=max(len(content), 8192))
                if actual != content:
                    raise UpgradeError('Existing bootstrap file differs; it was preserved: ' + str(path.relative_to(self.root)))
        current = runtime / 'current'
        if current.exists() or current.is_symlink():
            info = current.lstat()
            if (not stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or
                    os.readlink(current) != 'versions/v' + reference['version']):
                raise UpgradeError('Existing runtime pointer differs; preserved')
        if destination.exists() and {p.name for p in destination.iterdir()} - {'updater.py', 'manifest.json', 'manifest.sig', 'payload.zip'}:
            raise UpgradeError('Initial immutable runtime contains unexpected files')

    def reload_boundary(self):
        try:
            result = subprocess.run(['/usr/bin/systemctl', 'daemon-reload'], capture_output=True, timeout=30)
            if result.returncode:
                raise UpgradeError('Unit reload failed; launcher remains unactivated')
            for unit in (*WORKERS, RECOVERY_UNIT):
                result = subprocess.run(['/usr/bin/systemctl', 'show', unit, '--no-pager',
                                         '--property=ReadOnlyPaths,NeedDaemonReload'],
                                        capture_output=True, timeout=10)
                values = dict(line.split('=', 1) for line in result.stdout.decode('utf8').splitlines())
                if (result.returncode or values.get('NeedDaemonReload') != 'no' or
                        not set(READ_ONLY).issubset(set(values.get('ReadOnlyPaths', '').split()))):
                    raise UpgradeError('Explicit supervisor/key read-only unit boundary was not confirmed')
        except (OSError, ValueError, UnicodeError, subprocess.TimeoutExpired):
            raise UpgradeError('Unit boundary verification is unconfirmed; launcher remains unactivated') from None

    def provision(self, inputs, lock):
        # Root's app configuration, operator, release trust and policies are never
        # rewritten. New independent trust is explicit and cannot replace a pin.
        self.exact_file(self.path(KEY), inputs['key'], 0o644)
        self.exact_file(self.path(SUPERVISOR), inputs['supervisor'], 0o644)
        self.exact_file(self.path(TRANSPORT), inputs['transport'], 0o644)
        self.exact_file(self.path(SOURCE_POLICY), inputs['source_policy'])
        runtime = self.path(STATE) / 'runtime'
        versions = runtime / 'versions'
        self.directory(runtime)
        self.directory(versions)
        reference = dict(version=inputs['manifest']['version'], sha256=inputs['manifest']['sha256'])
        destination = versions / ('v' + reference['version'])
        self.directory(destination)
        for name, content in {'updater.py': inputs['worker'], 'manifest.json': inputs['raw'],
                              'manifest.sig': inputs['signature'], 'payload.zip': inputs['archive']}.items():
            self.exact_file(destination / name, content)
        if {p.name for p in destination.iterdir()} != {'updater.py', 'manifest.json', 'manifest.sig', 'payload.zip'}:
            raise UpgradeError('Initial immutable runtime contains unexpected files')
        current = runtime / 'current'
        target = 'versions/v' + reference['version']
        if current.exists() or current.is_symlink():
            info = current.lstat()
            if not stat.S_ISLNK(info.st_mode) or info.st_uid != 0 or os.readlink(current) != target:
                raise UpgradeError('Existing runtime pointer differs; preserved')
        else:
            current.symlink_to(target)
            self.sync(runtime)
        journal = dict(format=1, phase='stable', active=reference, previous=None,
                       candidate=None, maxAcceptedVersion=reference['version'])
        self.exact_file(self.path(STATE) / 'updater-runtime.json',
                        json.dumps(journal, separators=(',', ':')).encode())
        for path in self.boundary_paths():
            self.directory(path.parent, 0o755)
            self.exact_file(path, BOUNDARY, 0o644)
        self.apply_boundary()
        supervisor = inputs['module'].Supervisor(self.root)
        supervisor.lock_fd = lock
        supervisor.verify_runtime(reference)
        supervisor.probe(reference)

    def verify_provisioned(self, inputs):
        for path, expected in [(self.path(KEY), inputs['key']), (self.path(SUPERVISOR), inputs['supervisor']),
                               (self.path(TRANSPORT), inputs['transport']),
                               *((path, BOUNDARY) for path in self.boundary_paths())]:
            content, _ = self.read_file(path, limit=max(len(expected), 8192))
            if content != expected:
                raise UpgradeError('Installed supervisor, independent key, or unit boundary differs; preserved')
        supervisor = inputs['module'].Supervisor(self.root)
        inputs['transport_module'].policy(supervisor)  # Validate and preserve existing source policy; never reset it.
        journal = supervisor.journal()
        if journal['phase'] != 'stable' or supervisor.pointer() != 'versions/v' + journal['active']['version']:
            raise UpgradeError('Runtime update is pending or pointer is inconsistent; preserve recovery journal')
        supervisor.verify_runtime(journal['active'])
        # Do not execute a runtime or reset an already advanced journal during an
        # idempotent bootstrap/check-only call.

    def quiet(self, allow_schedulers=False):
        states = self.inspect_units()
        for unit in (*QUIET_UNITS, RECOVERY_UNIT):
            value = states.get(unit, {})
            if value.get('LoadState') != 'loaded' or value.get('MainPID') != '0' or value.get('ControlPID') != '0':
                raise UpgradeError('Updater units are missing or running; worker was not replaced')
            expected = [('inactive', 'dead')]
            if unit == RECOVERY_UNIT:
                expected.append(('active', 'exited'))
            if allow_schedulers and unit in SCHEDULERS:
                expected.append(('active', 'waiting'))
            if (value.get('ActiveState'), value.get('SubState')) not in expected:
                raise UpgradeError('Updater workers must be inactive before bootstrap; worker was not replaced')
        return {unit: states[unit]['ActiveState'] == 'active' for unit in SCHEDULERS}

    def restore_schedulers(self, original):
        for unit, active in original.items():
            if active:
                try:
                    self.scheduler('start', unit)
                except Exception:
                    # An uncertain systemctl result is checked against actual state.
                    pass
        try:
            states = self.inspect_units()
            restored = all(states.get(unit, {}).get('ActiveState') == ('active' if active else 'inactive')
                           for unit, active in original.items())
        except Exception:
            restored = False
        if not restored:
            raise UpgradeError('Scheduler restoration is unconfirmed; inspect worker with --check-only and restore the prior timer/path runtime states locally')

    def state(self):
        config, _ = self.read_file(self.path(CONFIG), private=True)
        status, _ = self.read_file(self.path(STATE) / 'status.json', limit=16384)
        try:
            settings = json.loads(config)
            journal = json.loads(status)
            if (not isinstance(settings, dict) or settings.get('stateDirectory') != STATE or
                    settings.get('service') != 'yachad.service' or
                    settings.get('releaseTrust', 'signature') not in ('signature', 'github-https')):
                raise ValueError()
            if (not isinstance(journal, dict) or journal.get('phase') not in STABLE_PHASES or
                    journal.get('modeChangePending') is not None or
                    isinstance(journal.get('lastModeRequest'), dict) and journal['lastModeRequest'].get('state') == 'pending' or
                    isinstance(journal.get('lastRequest'), dict) and journal['lastRequest'].get('state') == 'running'):
                raise UpgradeError('Updater has unfinished work or recovery; preserve its journal and resolve that work first')
        except (ValueError, TypeError):
            raise UpgradeError('Unexpected updater configuration or journal; nothing changed') from None
        return config, status

    def run(self, check_only=False):
        # Existing lock only: even a check must not create installation files.
        lock_path = self.path(STATE) / 'worker.lock'
        self.ancestors(lock_path.parent)
        lock = os.open(lock_path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            info = os.fstat(lock)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or
                    stat.S_IMODE(info.st_mode) != 0o600):
                raise UpgradeError('Unsafe updater lock; nothing changed')
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise UpgradeError('Updater is busy; nothing changed') from None
            inputs = self.inputs()
            target = self.path(TARGET)
            old, previous = self.read_file(target, limit=MAX_SOURCE)
            current_hash = hashlib.sha256(old).hexdigest()
            if current_hash not in (OLD_SHA256, DIRECT_WORKER_SHA256, LAUNCHER_SHA256):
                raise UpgradeError('Installed worker is not an exact reviewed version; nothing changed')
            state = self.state()
            schedulers = self.quiet(allow_schedulers=True)
            if current_hash == LAUNCHER_SHA256:
                self.verify_provisioned(inputs)
                return 'ALREADY_CURRENT: launcher, supervisor, key and signed active runtime verified; no files or services changed'
            self.preflight_new(inputs)
            print('Provision an independently signed updater runtime and immutable supervisor; activate the fixed launcher last.')
            print('Independent Ed25519 key DER SHA-256: ' + self.key_fingerprint.lower())
            print('This key authorizes future privileged updater code. Verify its fingerprint independently of this bundle.')
            print('Existing app DB, auth, configuration, release trust and policy are preserved.')
            print('Independent updater downloads: ' + ('ENABLED by your explicit option; automatic checks honor parent auto/notify and explicit install can update the runtime.' if self.enable_updater_updates else 'DISABLED by default; no root-update downloads are authorized.'))
            print('New explicit read-only supervisor/key unit drop-ins are installed; systemd reloads them. Unit enablement is unchanged.')
            print('Active updater timer/path are paused then restored; existing auto policy may run immediately afterward.')
            print('Power loss or forced termination may require locally restoring prior scheduler runtime states.')
            print('If preflight changes app configuration or its journal, schedulers stay paused for local recovery; data is never rolled back automatically.')
            if check_only:
                return 'CHECK_OK: reviewed replacement is eligible; no files or services changed'
            if self.ask('Provision this privileged signing trust, supervisor, runtime and launcher? [y/N] ').strip().lower() != 'y':
                return 'CANCELED: no files or services changed'
            # Confirmation is not a lock release: inspect every boundary again.
            if self.quiet(allow_schedulers=True) != schedulers:
                raise UpgradeError('Scheduler states changed during confirmation; nothing changed')
            if self.state() != state:
                raise UpgradeError('Configuration or journal changed during confirmation; nothing changed')
            current, current_info = self.read_file(target, limit=MAX_SOURCE)
            if current != old or (current_info.st_dev, current_info.st_ino) != (previous.st_dev, previous.st_ino):
                raise UpgradeError('Installed worker changed during confirmation; nothing changed')
            try:
                for unit, active in schedulers.items():
                    if active:
                        self.scheduler('stop', unit)
                # A timer/path activation just before stopping may have loaded the
                # old source and be waiting on our lock. Never replace in that case.
                self.quiet()
                if self.state() != state:
                    raise UpgradeError('Updater journal changed during quiescence; worker was not replaced')
                try:
                    self.provision(inputs, lock)
                except UpgradeError:
                    raise
                except Exception:
                    raise UpgradeError('Signed runtime preparation or self-check failed; launcher remains unactivated and verified staging files are preserved') from None
                self.quiet()
                if self.state() != state:
                    raise UpgradeError('App configuration or journal changed during runtime preflight; launcher remains unactivated')
                temporary = None
                committed = False
                try:
                    fd, temporary = tempfile.mkstemp(prefix='.updater-reviewed-', dir=target.parent)
                    with os.fdopen(fd, 'wb') as output:
                        os.fchown(output.fileno(), previous.st_uid, previous.st_gid)
                        os.fchmod(output.fileno(), stat.S_IMODE(previous.st_mode))
                        output.write(LAUNCHER)
                        output.flush()
                        os.fsync(output.fileno())
                    os.replace(temporary, target)
                    committed = True
                    directory = os.open(target.parent, os.O_RDONLY | os.O_DIRECTORY)
                    try:
                        os.fsync(directory)
                    finally:
                        os.close(directory)
                    actual, actual_info = self.read_file(target, limit=MAX_SOURCE)
                    if (actual != LAUNCHER or (actual_info.st_uid, actual_info.st_gid, stat.S_IMODE(actual_info.st_mode)) !=
                            (previous.st_uid, previous.st_gid, stat.S_IMODE(previous.st_mode))):
                        raise UpgradeError('Installed worker verification failed')
                except Exception:
                    if committed:
                        raise UpgradeError('Worker replacement occurred but durability/verification is unconfirmed; do not retry blindly, inspect with --check-only') from None
                    raise
                finally:
                    if temporary is not None:
                        pathlib.Path(temporary).unlink(missing_ok=True)
            finally:
                # No enable/disable/reset-failed calls. Already
                # inactive schedulers stay inactive, including after refusal.
                try:
                    unchanged = self.state() == state
                except Exception:
                    unchanged = False
                if not unchanged:
                    # A signed preflight is authorized root code, but its contract
                    # forbids changing the app's config/journal. Never let the old
                    # worker continue on uncertain state or silently restore data.
                    raise UpgradeError('App configuration or journal changed; updater schedulers remain paused for local recovery. No database/configuration rollback was attempted')
                self.restore_schedulers(schedulers)
            return 'WORKER_UPGRADED: trusted supervisor and signed runtime provisioned, launcher activated; app/database untouched; prior timer/path runtime states restored'
        finally:
            os.close(lock)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check-only', action='store_true', help='Read-only eligibility check; does not replace any file')
    parser.add_argument('--runtime-dir', required=True, help='Local independently signed yachad-updater bundle directory')
    parser.add_argument('--public-key', required=True, help='Local Ed25519 public PEM only; never a private key')
    parser.add_argument('--public-key-sha256', required=True, help='Independently verified SHA-256 of the public DER bytes')
    parser.add_argument('--enable-updater-updates', action='store_true', help='Explicitly opt in to signed root-runtime updates; default is disabled')
    args = parser.parse_args()
    if not hasattr(os, 'geteuid') or os.geteuid() != 0:
        raise UpgradeError('Run this reviewed tool using sudo in the local owner terminal')
    if not args.check_only and not sys.stdin.isatty():
        raise UpgradeError('Interactive local owner confirmation is required; nothing changed')
    print(Bootstrap(runtime_dir=args.runtime_dir, public_key=args.public_key,
                    key_fingerprint=args.public_key_sha256, enable_updater_updates=args.enable_updater_updates).run(check_only=args.check_only))


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        print('WORKER_UPGRADE_INTERRUPTED: inspect with --check-only before retrying; scheduler restoration was attempted', file=sys.stderr)
        sys.exit(1)
    except UpgradeError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except Exception:
        print('WORKER_UPGRADE_STOPPED: check local ownership, files and permissions; no private output logged', file=sys.stderr)
        sys.exit(1)
