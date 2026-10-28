#!/usr/bin/env python3
"""Explicit local app-install request through the existing direct updater.

This never installs root code or changes updater policy. The existing worker
selects the latest stable app release; EXPECTED_VERSION is an outcome assertion,
not a new release pin. Run only after the reviewed manual worker upgrade.
"""
import sys
if __name__ == '__main__' and not sys.flags.isolated:
    raise SystemExit('Use isolated Python: sudo python3 -I -B deploy/request-update-manual.py')

import argparse
import fcntl
import hashlib
import json
import os
import pathlib
import pwd
import re
import stat
import subprocess
import time
import urllib.request
import uuid

CONFIG = '/etc/yachad/updates.json'
STATE = '/var/lib/yachad-updates'
WORKER = '/usr/local/lib/yachad-updater/updater.py'
WORKER_SHA256 = '22cd73dc810d08d90e1ec6af4e4b482bc60a0f42b44e1bf874c78682375264cd'
SERVICE = 'yachad-update-request.service'
EXPECTED_VERSION = '0.5.0'
STABLE = {'idle', 'healthy', 'available', 'incompatible', 'failed', 'rolled_back'}
SIGNED_FOOTPRINT = (
    '/etc/yachad/updater-signing.pub', '/etc/yachad/updater-source.json',
    '/usr/local/lib/yachad-updater/updater-supervisor.py',
    '/usr/local/lib/yachad-updater/updater-transport.py',
    STATE + '/updater-runtime.json', STATE + '/runtime',
    *('/etc/systemd/system/' + unit + '.d/60-updater-supervisor.conf' for unit in
      ('yachad-update.service', SERVICE, 'yachad-update-recover.service')),
)

# This fixed program runs only AFTER runuser drops privileges. Root never opens
# or follows app-controlled queue paths. A hard link publishes complete JSON
# without overwriting an existing request; only our temporary name is removed.
WRITER = r'''
import json, os, stat, sys, time, uuid
if os.geteuid() == 0: raise SystemExit(10)
payload = sys.stdin.buffer.read(513)
if not 0 < len(payload) <= 512: raise SystemExit(11)
value = json.loads(payload)
if value['action'] != 'install' or str(uuid.UUID(value['id'])) != value['id']: raise SystemExit(11)
if abs(time.time()*1000-value['createdAt']) > 10000: raise SystemExit(11)
directory = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
name = '.manual-update-' + uuid.uuid4().hex
created = False
published = False
try:
    info = os.fstat(directory)
    if info.st_uid != os.geteuid() or info.st_mode & 0o022: raise SystemExit(12)
    fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
    created = True
    with os.fdopen(fd, 'wb') as output:
        os.fchmod(output.fileno(), 0o600)
        output.write(payload); output.flush(); os.fsync(output.fileno())
    try:
        os.link(name, 'update-request.json', src_dir_fd=directory, dst_dir_fd=directory, follow_symlinks=False)
    except FileExistsError:
        raise SystemExit(20)
    published = True
    os.fsync(directory)
    try:
        os.link(name, 'update-request.wake', src_dir_fd=directory, dst_dir_fd=directory, follow_symlinks=False)
        os.fsync(directory)
    except OSError:
        pass  # The authoritative queue exists; explicit service start/timer remain.
except Exception:
    raise SystemExit(21 if published else 22)
finally:
    if created:
        try: os.unlink(name, dir_fd=directory)
        except OSError: pass
    os.close(directory)
'''


class RequestError(Exception):
    pass


def service_start():
    result = subprocess.run(['/usr/bin/systemctl', 'start', '--no-block', SERVICE],
                            capture_output=True, timeout=30,
                            env={'PATH': '/usr/bin:/bin', 'LANG': 'C', 'HOME': '/root'})
    if result.returncode:
        raise RequestError('REQUEST_WAKE_UNCONFIRMED: inspect the request ID; do not queue a duplicate')


def publish(config, payload):
    result = subprocess.run(['/usr/sbin/runuser', '-u', config['appUser'], '--',
                             '/usr/bin/python3', '-I', '-B', '-c', WRITER, config['dataDirectory']],
                            input=payload, capture_output=True, timeout=30,
                            env={'PATH': '/usr/bin:/bin', 'LANG': 'C', 'HOME': '/'})
    if result.returncode == 20:
        raise RequestError('REQUEST_EXISTS: the existing queue was preserved; inspect it before retrying')
    if result.returncode:
        raise RequestError('REQUEST_PUBLICATION_UNCONFIRMED: inspect the request ID before any retry')


def ready(url):
    if not isinstance(url, str) or not re.fullmatch(r'http://127\.0\.0\.1:[0-9]{1,5}/health/ready', url):
        return False
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(url, timeout=5) as response:
            raw = response.read(1025)
            if len(raw) > 1024:
                return False
            body = json.loads(raw)
        return (body.get('ok') is True and body.get('version') == EXPECTED_VERSION and
                type(body.get('databaseSchema')) is int and body['databaseSchema'] == 2)
    except Exception:
        return False


class ManualRequest:
    def __init__(self, root=pathlib.Path('/'), writer=publish, start=service_start,
                 health=ready, now=time.time, monotonic=time.monotonic, sleep=time.sleep):
        self.root = pathlib.Path(root).absolute()
        self.writer, self.start, self.health = writer, start, health
        self.now, self.monotonic, self.sleep = now, monotonic, sleep

    def path(self, value):
        return self.root / value.lstrip('/')

    def ancestors(self, directory):
        for path in (directory, *directory.parents):
            if not path.is_relative_to(self.root):
                break
            info = path.lstat()
            if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
                raise RequestError('UNSAFE_ROOT_PATH: no request was authorized')

    def read(self, value, maximum=16384, private=False):
        path = self.path(value)
        self.ancestors(path.parent)
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            info = os.fstat(fd)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or
                    info.st_mode & (0o077 if private else 0o022) or info.st_mode & 0o7000 or
                    not 0 < info.st_size <= maximum):
                raise RequestError('UNSAFE_ROOT_FILE: no request was authorized')
            data = os.read(fd, maximum + 1)
            if len(data) != info.st_size:
                raise RequestError('ROOT_FILE_CHANGED: inspect before retrying')
            return data
        finally:
            os.close(fd)

    def config(self, require_enabled=True):
        config = json.loads(self.read(CONFIG, 8192, private=True))
        if (not isinstance(config, dict) or type(config.get('enabled')) is not bool or
                require_enabled and config.get('enabled') is not True or
                config.get('mode') not in ('auto', 'notify') or config.get('stateDirectory') != STATE or
                config.get('service') != 'yachad.service' or
                config.get('repository') != 'NehoraiHadad/yahad' or
                config.get('releaseTrust', 'signature') not in ('signature', 'github-https')):
            raise RequestError('CONFIGURATION_BLOCKED: existing policy was preserved; updates must already be enabled')
        if require_enabled and config.get('installVersionPin') not in (None, EXPECTED_VERSION, 'v' + EXPECTED_VERSION):
            raise RequestError('VERSION_PIN_BLOCKED: existing version pin was preserved')
        user = config.get('appUser')
        if not isinstance(user, str) or not re.fullmatch(r'[a-z_][a-z0-9_-]{0,31}', user) or pwd.getpwnam(user).pw_uid == 0:
            raise RequestError('INVALID_APP_USER: no request was authorized')
        data = config.get('dataDirectory')
        if (not isinstance(data, str) or not re.fullmatch(r'/var/lib/yachad(?:/[A-Za-z0-9_.-]+)*', data) or
                any(part in ('.', '..') for part in pathlib.PurePosixPath(data).parts)):
            raise RequestError('INVALID_DATA_DIRECTORY: no request was authorized')
        operator = config.get('operator')
        if not isinstance(operator, dict) or set(operator) != {'familyId', 'userId'}:
            raise RequestError('INVALID_OPERATOR: no request was authorized')
        try:
            if any(not isinstance(value, str) or str(uuid.UUID(value)) != value for value in operator.values()):
                raise ValueError()
        except (ValueError, TypeError, AttributeError):
            raise RequestError('INVALID_OPERATOR: no request was authorized') from None
        if not isinstance(config.get('healthUrl'), str) or not re.fullmatch(r'http://127\.0\.0\.1:[0-9]{1,5}/health/ready', config['healthUrl']):
            raise RequestError('INVALID_HEALTH_URL: no request was authorized')
        return config

    def status(self):
        value = json.loads(self.read(STATE + '/status.json'))
        if not isinstance(value, dict):
            raise RequestError('INVALID_STATUS: inspect locally')
        return value

    def queue(self):
        lock_path = self.path(STATE + '/worker.lock')
        self.ancestors(lock_path.parent)
        fd = os.open(lock_path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        request_id = str(uuid.uuid4())
        try:
            info = os.fstat(fd)
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o600):
                raise RequestError('UNSAFE_WORKER_LOCK: nothing queued')
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise RequestError('UPDATER_BUSY: nothing queued; allow existing work to finish') from None
            if hashlib.sha256(self.read(WORKER, 1024 * 1024)).hexdigest() != WORKER_SHA256:
                raise RequestError('DIRECT_WORKER_REQUIRED: use the reviewed worker upgrade first')
            for value in SIGNED_FOOTPRINT:
                path = self.path(value)
                if path.exists() or path.is_symlink():
                    raise RequestError('SIGNED_ENROLLMENT_PRESENT: direct-only request helper refused')
            config, status = self.config(), self.status()
            if (status.get('operator') != config['operator'] or
                    status.get('releaseTrust', 'signature') != config.get('releaseTrust', 'signature')):
                raise RequestError('STATUS_POLICY_MISMATCH: existing configuration and journal were preserved')
            if (status.get('phase') not in STABLE or status.get('runtimeDigest') is not None or
                    status.get('modeChangePending') is not None or
                    isinstance(status.get('lastModeRequest'), dict) and status['lastModeRequest'].get('state') == 'pending' or
                    isinstance(status.get('lastRequest'), dict) and status['lastRequest'].get('state') == 'running'):
                raise RequestError('UNFINISHED_WORK: journal and request were preserved; inspect locally')
            payload = json.dumps(dict(id=request_id, action='install', familyId=config['operator']['familyId'],
                                     userId=config['operator']['userId'], createdAt=int(self.now() * 1000)),
                                 separators=(',', ':')).encode()
            if len(payload) > 512:
                raise RequestError('REQUEST_TOO_LARGE: nothing queued')
            # Print the correlation ID before the publication attempt, so even
            # interrupted/uncertain publication is inspectable without requeueing.
            print('REQUEST_ID=' + request_id, flush=True)
            self.writer(config, payload)
        finally:
            os.close(fd)
        # Never wait for systemd while retaining worker.lock: the service needs it.
        self.start()
        return request_id, config

    def wait(self, request_id, config, seconds):
        deadline = self.monotonic() + seconds
        while True:
            status = self.status()
            request = status.get('lastRequest')
            if isinstance(request, dict) and request.get('id') == request_id and request.get('action') == 'install':
                if request.get('state') == 'failed':
                    raise RequestError('INSTALL_FAILED: matching request failed; inspect local updater status; do not reset recovery or restore old data')
                if request.get('state') == 'succeeded':
                    if (status.get('currentVersion') != EXPECTED_VERSION or status.get('phase') not in ('healthy', 'idle') or
                            status.get('error') is not None or not self.health(config['healthUrl'])):
                        raise RequestError('EXPECTED_HEALTH_UNCONFIRMED: request completed but expected 0.5.0/schema-2 readiness was not verified; no rollback attempted')
                    return 'INSTALL_HEALTHY: matching request completed; 0.5.0/schema-2 local readiness verified'
            if self.monotonic() >= deadline:
                return 'INSTALL_PENDING_OR_UNCONFIRMED: no matching terminal result; work was not canceled. Inspect with --status ' + request_id
            self.sleep(min(2, max(0, deadline - self.monotonic())))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--status', help='Read-only follow-up of a previously printed request UUID; never queues or starts services')
    parser.add_argument('--wait-seconds', type=int, default=4200, help='Observe for 0..4200 seconds; timeout does not cancel work')
    args = parser.parse_args()
    if os.geteuid() != 0 or not 0 <= args.wait_seconds <= 4200:
        raise RequestError('Run locally with sudo; wait must be 0..4200 seconds')
    if not args.status:
        try:
            uid = int(os.environ.get('SUDO_UID', '0'))
            owner = pwd.getpwuid(uid)
            if uid < 1000 or owner.pw_name in ('yachad', 'yachad-update'):
                raise ValueError()
        except (ValueError, KeyError):
            raise RequestError('Run from the ordinary owner account using sudo; nothing queued') from None
        if not sys.stdin.isatty() or not sys.stderr.isatty():
            raise RequestError('Interactive local owner terminal required; nothing queued')
    helper = ManualRequest()
    if args.status:
        if str(uuid.UUID(args.status)) != args.status:
            raise RequestError('INVALID_REQUEST_ID')
        request_id, config = args.status, helper.config(require_enabled=False)
    else:
        request_id, config = helper.queue()
    print(helper.wait(request_id, config, args.wait_seconds))


if __name__ == '__main__':
    try:
        main()
    except RequestError as error:
        print(str(error), file=sys.stderr); sys.exit(1)
    except (Exception, KeyboardInterrupt):
        print('MANUAL_REQUEST_UNCONFIRMED: inspect the printed request ID before retrying; no rollback or cancellation was attempted', file=sys.stderr)
        sys.exit(1)
