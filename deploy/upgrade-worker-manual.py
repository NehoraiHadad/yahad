#!/usr/bin/env python3
"""Manual, local-only transition from the exact legacy worker to protocol 2.

No signature enrollment, downloads, app upgrade or database access. Only the
root-owned direct updater.py is atomically replaced; helpers are verified, not
replaced. Existing configuration, operator, release trust, data, keys and unit
enablement are preserved. This does NOT enable updater self-updates.
Use a quiet maintenance window: no concurrent installer/manual updater commands.
Invoke as the ordinary owner: sudo python3 -I -B upgrade-worker-manual.py
"""
import sys
if __name__ == '__main__' and not sys.flags.isolated:
    raise SystemExit('Use isolated Python: sudo python3 -I -B upgrade-worker-manual.py [--check-only]')

import argparse
import fcntl
import hashlib
import json
import os
import pathlib
import stat
import subprocess
import tempfile
import pwd
import uuid
import signal

OLD_SHA256 = '6b1523a207ecff1ad7bff19c80d85809f4e453125bf5b5ecece64463465a6cad'
DIRECT_WORKER_SHA256 = '22cd73dc810d08d90e1ec6af4e4b482bc60a0f42b44e1bf874c78682375264cd'
# Exact canonical LF and historical 0.3.2 CRLF aliases; no normalization.
HELPER_SHA256 = {
    'data-lock.py': ('6fa8a14c774a2f1474f341c26cbbedd81bf93e619b738c1262ebe613bc0f751b',
                     'a7f4db80f61f48ed478f28244ff0ce54dc212cb8dab163f42ce9443e45803439'),
    'export-dependencies.py': ('7bf1c19ecb9fd2843b346a4c04caf2d3bed279e1e4c436ad103d2b06dd1eacc8',
                               '64b75b505935408d56a1989392c631daa41b832d318ca7a3b1758500a4ac10ac'),
}
TARGET = '/usr/local/lib/yachad-updater/updater.py'
CONFIG = '/etc/yachad/updates.json'
STATE = '/var/lib/yachad-updates'
SCHEDULERS = ('yachad-update.timer', 'yachad-update-request.path')
WORKERS = ('yachad-update.service', 'yachad-update-request.service')
QUIET_UNITS = SCHEDULERS + WORKERS
RECOVERY_UNIT = 'yachad-update-recover.service'
STABLE_PHASES = {'idle', 'healthy', 'available', 'incompatible', 'failed', 'rolled_back'}
MAX_SOURCE = 1024 * 1024
SIGNED_FOOTPRINT = (
    '/etc/yachad/updater-signing.pub', '/etc/yachad/updater-source.json',
    '/usr/local/lib/yachad-updater/updater-supervisor.py',
    '/usr/local/lib/yachad-updater/updater-transport.py',
    STATE + '/updater-runtime.json', STATE + '/runtime',
    *('/etc/systemd/system/' + unit + '.d/60-updater-supervisor.conf'
      for unit in (*WORKERS, RECOVERY_UNIT)),
)


class UpgradeError(Exception):
    pass


def unit_states():
    """Read systemd state only; never stop/start/enable/disable any unit."""
    states = {}
    for unit in (*QUIET_UNITS, RECOVERY_UNIT):
        properties = ['LoadState', 'ActiveState', 'SubState', 'Job', 'NeedDaemonReload']
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
                 scheduler=scheduler_action, ask=input):
        self.root = pathlib.Path(root).absolute()
        self.source = pathlib.Path(source) if source is not None else pathlib.Path(__file__).absolute().with_name('updater.py')
        self.inspect_units = inspect_units
        self.scheduler = scheduler
        self.ask = ask

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

    def quiet(self, allow_schedulers=False):
        states = self.inspect_units()
        for unit in (*QUIET_UNITS, RECOVERY_UNIT):
            value = states.get(unit, {})
            if (value.get('LoadState') != 'loaded' or value.get('MainPID') != '0' or value.get('ControlPID') != '0' or
                    value.get('Job') not in ('', '0') or value.get('NeedDaemonReload') != 'no'):
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

    def source_bytes(self):
        content = self.external_file(self.source, MAX_SOURCE)
        if hashlib.sha256(content).hexdigest() != DIRECT_WORKER_SHA256:
            raise UpgradeError('Direct-worker source does not match reviewed SHA-256; nothing changed')
        # Compile, do not import or run: no app code, SQLite/WAL access or network.
        compile(content, '<reviewed-protocol-2-worker>', 'exec', dont_inherit=True)
        return content

    def boundaries(self):
        for value in SIGNED_FOOTPRINT:
            path = self.path(value)
            if path.exists() or path.is_symlink():
                raise UpgradeError('Signed updater enrollment or partial enrollment exists; manual direct-worker transition refused')
        helpers = {}
        for name, digests in HELPER_SHA256.items():
            content, _ = self.read_file(self.path(TARGET).with_name(name), limit=MAX_SOURCE)
            if hashlib.sha256(content).hexdigest() not in digests:
                raise UpgradeError('Installed helper is not exact reviewed bytes; nothing changed: ' + name)
            helpers[name] = content
        return helpers

    def state(self):
        config, _ = self.read_file(self.path(CONFIG), private=True)
        status, _ = self.read_file(self.path(STATE) / 'status.json', limit=16384)
        try:
            settings = json.loads(config)
            journal = json.loads(status)
            if (not isinstance(settings, dict) or settings.get('stateDirectory') != STATE or
                    settings.get('service') != 'yachad.service' or
                    settings.get('appUser') != 'yachad' or settings.get('builderUser') != 'yachad-update' or
                    settings.get('repository') != 'NehoraiHadad/yahad' or
                    settings.get('releaseTrust', 'signature') not in ('signature', 'github-https') or
                    type(settings.get('enabled')) is not bool or settings.get('mode') not in ('auto', 'notify') or
                    type(settings.get('allowUiModeControl', False)) is not bool):
                raise ValueError()
            operator = settings.get('operator')
            if not isinstance(operator, dict) or set(operator) != {'familyId', 'userId'}:
                raise ValueError()
            for value in operator.values():
                if not isinstance(value, str) or str(uuid.UUID(value)) != value:
                    raise ValueError()
            if (not isinstance(journal, dict) or journal.get('operator') != operator or
                    journal.get('releaseTrust', 'signature') != settings.get('releaseTrust', 'signature')):
                raise ValueError()
            if (journal.get('phase') not in STABLE_PHASES or journal.get('runtimeDigest') is not None or
                    journal.get('modeChangePending') is not None or
                    isinstance(journal.get('lastModeRequest'), dict) and journal['lastModeRequest'].get('state') == 'pending' or
                    isinstance(journal.get('lastRequest'), dict) and journal['lastRequest'].get('state') == 'running'):
                raise UpgradeError('Updater has unfinished work or recovery; preserve its journal and resolve that work first')
        except (ValueError, TypeError, AttributeError):
            raise UpgradeError('Unexpected updater configuration, operator or journal; nothing changed') from None
        return config, status

    @staticmethod
    def metadata(info):
        return info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode)

    def unchanged_target(self, old, previous):
        current, info = self.read_file(self.path(TARGET), limit=MAX_SOURCE)
        if (current != old or (info.st_dev, info.st_ino) != (previous.st_dev, previous.st_ino) or
                self.metadata(info) != self.metadata(previous)):
            raise UpgradeError('Installed worker changed concurrently; it was preserved')

    def run(self, check_only=False):
        # Existing lock only; read-only eligibility checks create no files.
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
            source = self.source_bytes()
            helpers = self.boundaries()
            target = self.path(TARGET)
            old, previous = self.read_file(target, limit=MAX_SOURCE)
            current_hash = hashlib.sha256(old).hexdigest()
            if current_hash not in (OLD_SHA256, DIRECT_WORKER_SHA256):
                raise UpgradeError('Installed worker is not an exact reviewed legacy/current direct worker; nothing changed')
            state = self.state()
            schedulers = self.quiet(allow_schedulers=True)
            if current_hash == DIRECT_WORKER_SHA256:
                return 'ALREADY_CURRENT: exact protocol-2 direct worker and helpers verified; no files or services changed; updater self-updates remain unavailable'
            print('Manual one-time replacement of the exact legacy worker with reviewed protocol-2 direct worker.')
            print('No signature/key enrollment, root-code downloads, database access, app installation or updater self-updates.')
            print('Existing app data, auth, env, configuration, operator, trust, policy and unit enablement are preserved.')
            print('Prior scheduler runtime states: ' + ', '.join(unit + '=' + ('active' if active else 'inactive') for unit, active in schedulers.items()))
            print('Active timer/path are paused then restored; existing auto policy or queued requests may install the app immediately afterward.')
            print('Save these prior states. Power loss/forced termination or unconfirmed replacement may leave schedulers paused for local inspection.')
            if check_only:
                return 'CHECK_OK: exact direct-worker replacement is eligible; no files or services changed'
            if self.ask('Replace only this privileged direct worker, without signatures or self-updates? [y/N] ').strip().lower() != 'y':
                return 'CANCELED: no files or services changed'
            if self.quiet(allow_schedulers=True) != schedulers or self.state() != state or self.boundaries() != helpers:
                raise UpgradeError('Installation changed during confirmation; nothing changed')
            self.unchanged_target(old, previous)
            temporary = None
            committed = verified = False
            try:
                for unit, active in schedulers.items():
                    if active:
                        self.scheduler('stop', unit)
                # Refuse even a just-activated old worker waiting on our lock.
                self.quiet()
                if self.state() != state or self.boundaries() != helpers:
                    raise UpgradeError('Installation changed during quiescence; worker was not replaced')
                self.unchanged_target(old, previous)
                fd, temporary = tempfile.mkstemp(prefix='.updater-manual-', dir=target.parent)
                with os.fdopen(fd, 'wb') as output:
                    os.fchown(output.fileno(), previous.st_uid, previous.st_gid)
                    os.fchmod(output.fileno(), stat.S_IMODE(previous.st_mode))
                    output.write(source)
                    output.flush()
                    os.fsync(output.fileno())
                self.quiet()
                if self.state() != state or self.boundaries() != helpers:
                    raise UpgradeError('Installation changed before replacement; worker was not replaced')
                self.unchanged_target(old, previous)
                # Mark ambiguity BEFORE the syscall: even interruption immediately
                # after rename must not restore schedulers on an unverified target.
                committed = True
                os.replace(temporary, target)
                directory = os.open(target.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    os.fsync(directory)
                finally:
                    os.close(directory)
                actual, actual_info = self.read_file(target, limit=MAX_SOURCE)
                if actual != source or self.metadata(actual_info) != self.metadata(previous):
                    raise UpgradeError('Installed worker verification failed')
                verified = True
            finally:
                cleanup_failed = False
                if temporary is not None:
                    try:
                        pathlib.Path(temporary).unlink(missing_ok=True)
                    except OSError:
                        cleanup_failed = True
                try:
                    unchanged = self.state() == state and self.boundaries() == helpers
                    if not committed:
                        self.unchanged_target(old, previous)
                    elif not verified:
                        # An atomic rename failure may have left the known old file.
                        # Only its exact original inode/metadata permits restoration.
                        self.unchanged_target(old, previous)
                except Exception:
                    unchanged = False
                if not unchanged:
                    raise UpgradeError('Worker replacement or installation state is unconfirmed; updater schedulers remain paused for local inspection. No app/database/configuration rollback was attempted') from None
                self.restore_schedulers(schedulers)
                if cleanup_failed:
                    raise UpgradeError('Reviewed staging cleanup is incomplete; known worker/configuration verified and prior scheduler states restored; inspect local staging files')
            return 'WORKER_UPGRADED: exact protocol-2 direct worker installed; app/database/configuration untouched; prior scheduler states restored; updater self-updates NOT enabled'
        finally:
            os.close(lock)


def owner_guard(check_only):
    if not hasattr(os, 'geteuid') or os.geteuid() != 0:
        raise UpgradeError('Run this reviewed tool using sudo in the local owner terminal')
    if check_only:
        return
    try:
        uid = int(os.environ.get('SUDO_UID', '0'))
        owner = pwd.getpwuid(uid)
        if uid < 1000 or owner.pw_name in ('yachad', 'yachad-update'):
            raise ValueError()
    except (ValueError, KeyError):
        raise UpgradeError('Run from the ordinary owner account using sudo; nothing changed') from None
    if not sys.stdin.isatty() or not sys.stderr.isatty():
        raise UpgradeError('Interactive local owner confirmation is required; nothing changed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check-only', action='store_true', help='Read-only eligibility check; no file/service changes')
    args = parser.parse_args()
    owner_guard(args.check_only)
    # Ordinary terminal termination follows the same cleanup path as Ctrl+C.
    def interrupted(_signum, _frame):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGHUP, interrupted)
    print(Bootstrap().run(check_only=args.check_only))


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        print('MANUAL_UPGRADE_INTERRUPTED: inspect with --check-only and check prior scheduler states before retrying', file=sys.stderr)
        sys.exit(1)
    except UpgradeError as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
    except Exception:
        print('MANUAL_UPGRADE_STOPPED: inspect with --check-only and check prior scheduler states; no private output logged', file=sys.stderr)
        sys.exit(1)
