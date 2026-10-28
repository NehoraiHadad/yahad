#!/usr/bin/env python3
"""Immutable launcher for independently signed, versioned updater runtimes.

This file is provisioned only by an owner-reviewed bootstrap. It never installs
itself, keys, helpers or units, and never trusts an application release to supply
root code. Transport is deliberately separate: stage() accepts authenticated
bundle bytes, not URLs/commands from an app manifest or web request.
"""
import contextlib
import fcntl
import hashlib
import io
import json
import os
import pathlib
import re
import selectors
import signal
import stat
import subprocess
import sys
import tempfile
import time
import types
import zipfile

CONFIG = '/etc/yachad/updates.json'
KEY = '/etc/yachad/updater-signing.pub'
TRANSPORT = '/usr/local/lib/yachad-updater/updater-transport.py'
COMMANDS = {'run', 'requests', 'recover', 'boot-recover'}
CAPABILITIES = dict(supervisorProtocol=1, workerProtocol=2, helperProtocol=1,
                    configFormat=1, stateFormat=1, appManifestFormats=[1, 2])
STABLE_APP_PHASES = {'idle', 'healthy', 'available', 'incompatible', 'failed', 'rolled_back'}
APP_PHASES = STABLE_APP_PHASES | {'checking', 'preparing', 'downloading', 'verifying', 'building',
                                'stopping', 'backing_up', 'activating', 'checking_health',
                                'rolling_back', 'rollback_pending_health', 'recovery_required'}
MAX_ARCHIVE = 2 * 1024 * 1024
MAX_SOURCE = 1024 * 1024
MAX_SCHEMA_ID = 1_000_000
RUNTIME_FILES = {'updater.py', 'manifest.json', 'manifest.sig', 'payload.zip'}
ENV = {'PATH': '/usr/local/bin:/usr/bin:/bin', 'HOME': '/root', 'LANG': 'C.UTF-8'}


class SupervisorError(Exception):
    """Only fixed operational codes; never print signed code, config or output."""


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r'(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})', value):
        raise SupervisorError('version')
    return tuple(map(int, value.split('.')))


def strict_json(raw):
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise SupervisorError('duplicate_field')
            result[key] = value
        return result
    try:
        return json.loads(raw, object_pairs_hook=pairs,
                          parse_constant=lambda _: (_ for _ in ()).throw(SupervisorError('json')))
    except (ValueError, UnicodeError):
        raise SupervisorError('json') from None


def fixed_process(args, *, cwd, pass_fds=(), timeout=30):
    """Fixed argv/no shell, clean environment, bounded output and process lifetime."""
    process = subprocess.Popen(args, cwd=cwd, env=ENV, pass_fds=pass_fds,
                               stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.DEVNULL, start_new_session=True)
    output = bytearray()
    deadline = time.monotonic() + timeout
    try:
        with selectors.DefaultSelector() as poll:
            poll.register(process.stdout, selectors.EVENT_READ)
            while poll.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise SupervisorError('timeout')
                for key, _ in poll.select(min(remaining, 1)):
                    chunk = os.read(key.fd, 4096)
                    if not chunk:
                        poll.unregister(key.fileobj)
                    output.extend(chunk)
                    if len(output) > 8192:
                        raise SupervisorError('output_limit')
            if process.wait(timeout=max(0.01, deadline - time.monotonic())):
                raise SupervisorError('runtime')
        return bytes(output)
    except BaseException:
        # A signed but broken runtime must not leave its subprocesses running.
        try: os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError: pass
        process.wait()
        raise
    finally:
        process.stdout.close()


def public_key_der(public_key_bytes, work_directory):
    """Canonical Ed25519 DER for a separately authenticated bootstrap pin."""
    if not isinstance(public_key_bytes, bytes) or not 1 <= len(public_key_bytes) <= 8192:
        raise SupervisorError('key')
    with tempfile.TemporaryDirectory(prefix='.key-', dir=work_directory) as temporary:
        directory = pathlib.Path(temporary)
        key = directory / 'key'
        key.write_bytes(public_key_bytes)
        der = fixed_process(['/usr/bin/openssl', 'pkey', '-pubin', '-in', str(key), '-outform', 'DER'], cwd=directory)
        if len(der) != 44 or der[:12] != bytes.fromhex('302a300506032b6570032100'):
            raise SupervisorError('key')
        return der


def verify_bundle(raw, signature, archive, public_key_bytes, work_directory):
    if not 1 <= len(raw) <= 8192 or len(signature) != 64 or not 1 <= len(archive) <= MAX_ARCHIVE:
        raise SupervisorError('bundle')
    # The caller must pin this key independently of the bundle/app transport.
    if not isinstance(public_key_bytes, bytes) or not 1 <= len(public_key_bytes) <= 8192:
        raise SupervisorError('key')
    with tempfile.TemporaryDirectory(prefix='.verify-', dir=work_directory) as temporary:
        directory = pathlib.Path(temporary)
        (directory / 'key').write_bytes(public_key_bytes)
        (directory / 'manifest').write_bytes(raw)
        (directory / 'signature').write_bytes(signature)
        try:
            der = fixed_process(['/usr/bin/openssl', 'pkey', '-pubin', '-in', str(directory / 'key'), '-outform', 'DER'], cwd=directory)
            if len(der) != 44 or der[:12] != bytes.fromhex('302a300506032b6570032100'):
                raise SupervisorError('key')
            fixed_process(['/usr/bin/openssl', 'pkeyutl', '-verify', '-pubin', '-inkey', str(directory / 'key'),
                           '-rawin', '-in', str(directory / 'manifest'), '-sigfile', str(directory / 'signature')], cwd=directory)
        except (OSError, subprocess.SubprocessError, SupervisorError):
            raise SupervisorError('signature') from None
    manifest = strict_json(raw)
    fields = {'format', 'kind', 'version', 'archive', 'bytes', 'sha256', *CAPABILITIES}
    if (not isinstance(manifest, dict) or set(manifest) != fields or type(manifest['format']) is not int or
            manifest['format'] != 1 or manifest['kind'] != 'yachad-updater' or manifest['archive'] != 'yachad-updater.zip'):
        raise SupervisorError('manifest')
    version(manifest['version'])
    for key in ('supervisorProtocol', 'helperProtocol', 'configFormat', 'stateFormat'):
        if type(manifest[key]) is not int or manifest[key] != CAPABILITIES[key]:
            raise SupervisorError('protocol')
    formats = manifest['appManifestFormats']
    if (type(manifest['workerProtocol']) is not int or not 2 <= manifest['workerProtocol'] <= MAX_SCHEMA_ID or
            not isinstance(formats, list) or not 2 <= len(formats) <= 32 or
            any(type(n) is not int or not 1 <= n <= MAX_SCHEMA_ID for n in formats) or
            len(set(formats)) != len(formats) or not {1, 2}.issubset(formats)):
        raise SupervisorError('protocol')
    if (type(manifest['bytes']) is not int or manifest['bytes'] != len(archive) or
            manifest['sha256'] != hashlib.sha256(archive).hexdigest()):
        raise SupervisorError('archive')
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            entries = bundle.infolist()
            if len(entries) != 1 or entries[0].filename != 'updater.py':
                raise SupervisorError('allowlist')
            item = entries[0]
            mode = item.external_attr >> 16
            if (item.orig_filename != item.filename or not stat.S_ISREG(mode) or mode & 0o7022 or
                    item.flag_bits & 1 or not 1 <= item.file_size <= MAX_SOURCE or
                    item.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED)):
                raise SupervisorError('archive')
            with bundle.open(item) as stream: source = stream.read(MAX_SOURCE + 1)
            if len(source) != item.file_size: raise SupervisorError('archive')
            # Parsing is not execution/import and happens only after signature checks.
            compile(source, 'signed-updater.py', 'exec', dont_inherit=True)
    except (zipfile.BadZipFile, RuntimeError, ValueError, SyntaxError):
        raise SupervisorError('archive') from None
    return manifest, source



class Supervisor:
    def __init__(self, root=pathlib.Path('/'), *, owner_uid=0, runner=fixed_process):
        # root/owner_uid are test injection only; CLI never accepts either option.
        self.root = pathlib.Path(root).absolute()
        self.owner_uid, self.runner, self.lock_fd = owner_uid, runner, None
        self.config_path = self.path(CONFIG)
        config = strict_json(self.read(self.config_path, 8192, private=True))
        state = config.get('stateDirectory') if isinstance(config, dict) else None
        if not isinstance(state, str) or not state.startswith('/') or '..' in pathlib.PurePosixPath(state).parts:
            raise SupervisorError('configuration')
        self.state = self.path(state)
        self.directory(self.state)
        self.runtime = self.state / 'runtime'
        self.versions = self.runtime / 'versions'
        self.current = self.runtime / 'current'
        self.journal_path = self.state / 'updater-runtime.json'
        self.key = self.path(KEY)

    def path(self, absolute):
        return self.root / str(absolute).lstrip('/')

    def directory(self, path):
        path = pathlib.Path(path)
        if not path.is_relative_to(self.root):
            raise SupervisorError('path')
        for item in (path, *path.parents):
            if not item.is_relative_to(self.root): break
            info = item.lstat()
            if (not stat.S_ISDIR(info.st_mode) or info.st_uid != self.owner_uid or
                    info.st_mode & 0o022 or info.st_mode & 0o7000):
                raise SupervisorError('directory')

    def read(self, path, limit, *, private=False, empty=False):
        self.directory(path.parent)
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            before = os.fstat(fd)
            if (not stat.S_ISREG(before.st_mode) or before.st_uid != self.owner_uid or
                    before.st_nlink != 1 or before.st_mode & (0o077 if private else 0o022) or
                    before.st_mode & 0o7000 or not (0 if empty else 1) <= before.st_size <= limit):
                raise SupervisorError('file')
            raw = bytearray()
            while len(raw) <= limit:
                chunk = os.read(fd, min(65536, limit + 1 - len(raw)))
                if not chunk: break
                raw.extend(chunk)
            after = os.fstat(fd)
            if len(raw) != before.st_size or (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_size, after.st_mtime_ns, after.st_ctime_ns):
                raise SupervisorError('changed_file')
            return bytes(raw)
        finally: os.close(fd)

    @staticmethod
    def sync(path):
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)

    def atomic(self, path, raw):
        self.directory(path.parent)
        # Unique O_EXCL files, never follow or overwrite an existing temp symlink.
        fd, temporary = tempfile.mkstemp(prefix='.' + path.name + '-', dir=path.parent)
        try:
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, 'wb') as stream:
                stream.write(raw); stream.flush(); os.fsync(stream.fileno())
            os.replace(temporary, path)
            self.sync(path.parent)
        finally:
            if os.path.lexists(temporary): os.unlink(temporary)

    @contextlib.contextmanager
    def locked(self, *, wait=False):
        if self.lock_fd is not None:
            raise SupervisorError('nested_lock')
        self.directory(self.state)
        fd = os.open(self.state / 'worker.lock', os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
        try:
            info = os.fstat(fd)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != self.owner_uid or
                    info.st_nlink != 1 or info.st_mode & 0o077):
                raise SupervisorError('lock')
            try: fcntl.flock(fd, fcntl.LOCK_EX | (0 if wait else fcntl.LOCK_NB))
            except BlockingIOError: raise SupervisorError('busy') from None
            self.lock_fd = fd
            yield
        finally:
            self.lock_fd = None
            # Closing our fd after the child exits releases the shared lock.
            # Neither supervisor nor worker explicitly LOCK_UN an inherited fd.
            os.close(fd)

    def ref(self, value):
        if not isinstance(value, dict) or set(value) != {'version', 'sha256'}:
            raise SupervisorError('journal')
        version(value['version'])
        if not isinstance(value['sha256'], str) or not re.fullmatch('[0-9a-f]{64}', value['sha256']):
            raise SupervisorError('journal')
        return value

    def journal(self):
        value = strict_json(self.read(self.journal_path, 8192, private=True))
        if (not isinstance(value, dict) or set(value) != {'format', 'phase', 'active', 'previous', 'candidate', 'maxAcceptedVersion'} or
                type(value['format']) is not int or value['format'] != 1 or
                value['phase'] not in {'stable', 'prepared', 'probing', 'recovery_required'}):
            raise SupervisorError('journal')
        self.ref(value['active'])
        for key in ('previous', 'candidate'):
            if value[key] is not None: self.ref(value[key])
        if version(value['maxAcceptedVersion']) != version(value['active']['version']):
            raise SupervisorError('journal')
        if value['previous'] and version(value['previous']['version']) >= version(value['active']['version']):
            raise SupervisorError('journal')
        if ((value['phase'] == 'stable') != (value['candidate'] is None) or
                (value['candidate'] and version(value['candidate']['version']) <= version(value['maxAcceptedVersion']))):
            raise SupervisorError('journal')
        return value

    def save(self, value):
        self.atomic(self.journal_path, json.dumps(value, separators=(',', ':')).encode())

    def verify(self, raw, signature, archive):
        self.directory(self.runtime)
        return verify_bundle(raw, signature, archive, self.read(self.key, 8192), self.runtime)

    def runtime_path(self, reference):
        return self.versions / ('v' + self.ref(reference)['version'])

    def verify_runtime(self, reference):
        directory = self.runtime_path(reference)
        self.directory(directory)
        if {item.name for item in directory.iterdir()} != RUNTIME_FILES:
            raise SupervisorError('runtime_files')
        manifest, source = self.verify(self.read(directory / 'manifest.json', 8192),
                                       self.read(directory / 'manifest.sig', 64),
                                       self.read(directory / 'payload.zip', MAX_ARCHIVE))
        if ({'version': manifest['version'], 'sha256': manifest['sha256']} != reference or
                self.read(directory / 'updater.py', MAX_SOURCE) != source):
            raise SupervisorError('runtime_digest')
        return directory

    def pointer(self):
        self.directory(self.runtime)
        info = self.current.lstat()
        if not stat.S_ISLNK(info.st_mode) or info.st_uid != self.owner_uid:
            raise SupervisorError('pointer')
        return os.readlink(self.current)

    def switch(self, reference):
        self.directory(self.runtime)
        target = 'versions/v' + self.ref(reference)['version']
        # Validate existing pointer before replacing it, including crash recovery.
        old = self.pointer()
        if not re.fullmatch(r'versions/v[0-9]+\.[0-9]+\.[0-9]+', old):
            raise SupervisorError('pointer')
        temporary = self.runtime / ('.current-' + os.urandom(12).hex())
        try:
            os.symlink(target, temporary)
            os.replace(temporary, self.current)
            self.sync(self.runtime)
        finally:
            if os.path.lexists(temporary): temporary.unlink()

    def app_status(self):
        try: value = strict_json(self.read(self.state / 'status.json', 65536))
        except FileNotFoundError: return {}
        if not isinstance(value, dict): raise SupervisorError('app_journal')
        return value

    def require_quiet_app(self):
        value = self.app_status()
        phase = value.get('phase', 'idle')
        if not isinstance(phase, str) or phase not in STABLE_APP_PHASES or value.get('modeChangePending') is not None:
            raise SupervisorError('app_busy')
        for key, terminal in (('lastRequest', {'succeeded', 'failed'}), ('lastModeRequest', {'applied', 'rejected'})):
            request = value.get(key)
            if request is not None and (not isinstance(request, dict) or not isinstance(request.get('state'), str) or
                                        request['state'] not in terminal):
                raise SupervisorError('app_busy')
        return value

    def snapshot(self):
        try: status = self.read(self.state / 'status.json', 65536)
        except FileNotFoundError: status = None
        return self.read(self.config_path, 8192, private=True), status

    def probe(self, reference):
        directory = self.verify_runtime(reference)
        manifest = strict_json(self.read(directory / 'manifest.json', 8192))
        before = self.snapshot()
        try:
            raw = self.runner(['/usr/bin/python3', '-I', '-B', str(directory / 'updater.py'), 'self-check',
                               '--supervisor-lock-fd', str(self.lock_fd), '--runtime-digest', reference['sha256']],
                              cwd=directory, pass_fds=(self.lock_fd,), timeout=30)
        finally:
            if self.snapshot() != before: raise SupervisorError('probe_mutation')
        if not isinstance(raw, bytes) or len(raw) > 8192: raise SupervisorError('probe')
        result = strict_json(raw)
        if not isinstance(result, dict) or set(result) != {*CAPABILITIES, 'databaseSchema'}:
            raise SupervisorError('probe')
        for key in CAPABILITIES:
            expected = manifest[key]
            if (result[key] != expected or type(result[key]) is not type(expected) or
                    (isinstance(result[key], list) and any(type(n) is not int for n in result[key]))):
                raise SupervisorError('probe')
        if type(result['databaseSchema']) is not int or not 1 <= result['databaseSchema'] <= MAX_SCHEMA_ID:
            raise SupervisorError('probe')

    def stage(self, manifest_bytes, signature_bytes, archive_bytes):
        with self.locked(): return self.stage_locked(manifest_bytes, signature_bytes, archive_bytes)

    def stage_locked(self, manifest_bytes, signature_bytes, archive_bytes):
        if self.lock_fd is None: raise SupervisorError('lock_required')
        journal = self.journal()
        if journal['phase'] != 'stable': raise SupervisorError('pending_runtime')
        self.require_quiet_app()
        self.verify_runtime(journal['active'])
        if self.pointer() != 'versions/v' + journal['active']['version']:
            raise SupervisorError('pointer')
        manifest, source = self.verify(manifest_bytes, signature_bytes, archive_bytes)
        candidate = {'version': manifest['version'], 'sha256': manifest['sha256']}
        if version(candidate['version']) <= version(journal['maxAcceptedVersion']):
            if candidate == journal['active']: return candidate
            raise SupervisorError('downgrade')
        destination = self.runtime_path(candidate)
        if os.path.lexists(destination):
            self.verify_runtime(candidate)  # no replacement of an immutable version
        else:
            self.directory(self.versions)
            temporary = pathlib.Path(tempfile.mkdtemp(prefix='.candidate-', dir=self.versions))
            try:
                for name, content in {'updater.py': source, 'manifest.json': manifest_bytes,
                                      'manifest.sig': signature_bytes, 'payload.zip': archive_bytes}.items():
                    self.atomic(temporary / name, content)
                self.sync(temporary)
                os.rename(temporary, destination)
                self.sync(self.versions)
            finally:
                # Never remove installed/referenced runtime trees, even on failure.
                if temporary.exists():
                    for item in temporary.iterdir(): item.unlink()
                    temporary.rmdir()
        self.save(dict(journal, phase='prepared', candidate=candidate))
        return candidate

    def recover_locked(self):
        journal = self.journal()
        if journal['phase'] == 'recovery_required': raise SupervisorError('recovery_required')
        self.verify_runtime(journal['active'])
        expected = 'versions/v' + journal['active']['version']
        if journal['phase'] == 'stable':
            if self.pointer() != expected: raise SupervisorError('pointer')
            return journal
        # Only pre-commit activation is reversible. Never undo a committed runtime
        # in response to a later application/database failure.
        try:
            self.require_quiet_app()
            allowed = {expected, 'versions/v' + journal['candidate']['version']}
            if self.pointer() not in allowed: raise SupervisorError('pointer')
            self.probe(journal['active'])
            if self.pointer() != expected: self.switch(journal['active'])
            following = dict(journal, phase='stable', candidate=None)
            self.save(following)
            return following
        except Exception:
            self.save(dict(journal, phase='recovery_required'))
            raise

    def activate(self):
        with self.locked(): return self.activate_locked()

    def activate_locked(self, guard=None):
        if self.lock_fd is None: raise SupervisorError('lock_required')
        journal = self.journal()
        if journal['phase'] == 'stable': return journal['active']
        if journal['phase'] != 'prepared': raise SupervisorError('pending_runtime')
        self.require_quiet_app()
        if self.pointer() != 'versions/v' + journal['active']['version']:
            raise SupervisorError('pointer')
        try:
            self.probe(journal['candidate'])
            if guard is not None and guard() is False:
                raise SupervisorError('authorization_changed')
            # Detect forbidden preflight mutations before pointer activation.
            self.require_quiet_app()
            self.save(dict(journal, phase='probing'))
            self.switch(journal['candidate'])
            self.probe(journal['candidate'])
            if guard is not None and guard() is False:
                raise SupervisorError('authorization_changed')
            self.require_quiet_app()
            self.save(dict(journal, phase='stable', active=journal['candidate'], previous=journal['active'],
                           candidate=None, maxAcceptedVersion=journal['candidate']['version']))
            return journal['candidate']
        except Exception as error:
            # A crash after stable-journal rename is already committed; this
            # observes disk and must not blindly revert to the prior runtime.
            if isinstance(error, SupervisorError) and str(error) == 'probe_mutation':
                self.save(dict(self.journal(), phase='recovery_required'))
                raise
            self.recover_locked()
            raise

    def install_bundle(self, manifest_bytes, signature_bytes, archive_bytes, *, guard=None):
        # Transport cannot leave a gap for ordinary dispatch to recover a merely
        # prepared candidate. All staging/verification/activation uses one lock.
        with self.locked():
            # Only the immutable transport supplies this closure. Revalidate its
            # root policy and still-pending owner intent after network I/O, while
            # holding the same lock used for staging and activation.
            if guard is not None and guard() is False:
                raise SupervisorError('authorization_changed')
            self.stage_locked(manifest_bytes, signature_bytes, archive_bytes)
            return self.activate_locked(guard=guard)

    def recover(self):
        with self.locked(): return self.recover_locked()

    def dispatch(self, command):
        if command not in COMMANDS: raise SupervisorError('command')
        with self.locked(wait=command == 'requests'):
            journal = self.recover_locked()
            reference = journal['active']
            status = self.app_status()
            if not isinstance(status.get('phase', 'idle'), str) or status.get('phase', 'idle') not in APP_PHASES:
                raise SupervisorError('app_journal')
            if status.get('phase', 'idle') not in STABLE_APP_PHASES and status.get('runtimeDigest') != reference['sha256']:
                raise SupervisorError('app_runtime_mismatch')
            directory = self.verify_runtime(reference)
            # Do not catch a failed app run by restoring the previous runtime:
            # the database or pending recovery format may already have changed.
            return self.runner(['/usr/bin/python3', '-I', '-B', str(directory / 'updater.py'), command,
                                '--supervisor-lock-fd', str(self.lock_fd), '--runtime-digest', reference['sha256']],
                               cwd=directory, pass_fds=(self.lock_fd,),
                               timeout=3900 if command == 'requests' else 1800 if command == 'run' else 180)


def refresh_runtime(supervisor, command='run'):
    # This separately bootstrapped immutable sibling is not part of an app or
    # runtime bundle. Read checked bytes once: no sys.path lookup or app imports.
    if command not in {'run', 'requests'}: raise SupervisorError('command')
    path = supervisor.path(TRANSPORT)
    source = supervisor.read(path, MAX_SOURCE)
    module = types.ModuleType('yachad_updater_transport')
    module.__file__ = str(path)
    exec(compile(source, str(path), 'exec', dont_inherit=True), module.__dict__)
    return module.refresh(supervisor, command=command)


def main():
    if os.geteuid() != 0 or len(sys.argv) != 2 or sys.argv[1] not in COMMANDS:
        raise SupervisorError('command')
    supervisor = Supervisor()
    if sys.argv[1] in {'run', 'requests'}:
        try: refresh_runtime(supervisor, sys.argv[1])
        except Exception:
            # Optional remote discovery never replaces a missing signature with
            # app HTTPS trust or prevents verified local runtime recovery.
            print('YACHAD_UPDATER_RUNTIME_CHECK_FAILED', file=sys.stderr)
    supervisor.dispatch(sys.argv[1])


if __name__ == '__main__':
    try: main()
    except Exception:
        print('YACHAD_UPDATER_SUPERVISOR_FAILURE', file=sys.stderr)
        sys.exit(1)
