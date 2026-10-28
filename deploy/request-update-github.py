#!/usr/bin/env python3
"""Local target-bound check/confirm/install bridge for an enrolled HTTPS updater.

This is separate from bootstrap and never changes trust/policy. Explicit install
requests need fresh target confirmation; checks can install under an existing
authorized auto policy. Run locally as the ordinary owner
with sudo. --status UUID only observes an already printed request, without writes.
"""
import sys
if __name__ == '__main__' and not sys.flags.isolated:
    raise SystemExit('Use isolated Python: sudo python3 -I -B request-update-github.py')

import argparse
import contextlib
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
import types
import urllib.request
import uuid

BASE_SHA256 = '953e13f8d680804671d59fc399d748467cf378c7df2a45c4a366f5a74c3036ee'
SUPERVISOR_SHA256 = '31bb934e57a28b8a8aad9fc3e27f3eb19a2b9628164f3d5563d8885eaaeaf5ac'
TRANSPORT_SHA256 = '77965b53ec5cd7cba536d65d0ed9aede16537fe513596e76dac3dbbe1ff71f24'
LAUNCHER_SHA256 = '7f2739eaa71df3a295cb0e33c9e78cd3c9e20978a964e51a470ff7e84a5d05dd'
DIRECTORY = '/usr/local/lib/yachad-updater/'
POLICY = '/etc/yachad/updater-source.json'
KEY = '/etc/yachad/updater-signing.pub'
VERSION = r'(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})'


def load_base():
    path = pathlib.Path(__file__).absolute().with_name('request-update-manual.py')
    if path.resolve(strict=True) != path: raise ValueError('Reviewed request helper may not use symlinks')
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        before = os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= 1024 * 1024: raise ValueError('Invalid reviewed helper')
        with os.fdopen(os.dup(fd), 'rb') as stream: raw = stream.read(1024 * 1024 + 1)
        after = os.fstat(fd)
        if (len(raw) != before.st_size or hashlib.sha256(raw).hexdigest() != BASE_SHA256 or
                (before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_size, after.st_mtime_ns, after.st_ctime_ns)):
            raise ValueError('Reviewed request helper changed')
    finally: os.close(fd)
    module = types.ModuleType('reviewed_manual_request'); module.__file__ = str(path)
    exec(compile(raw, str(path), 'exec', dont_inherit=True), module.__dict__)
    return module


base = load_base()
RequestError = base.RequestError
CONFIG, STATE = base.CONFIG, base.STATE
# The reviewed publisher still writes only after dropping to the fixed configured
# app user. Add bounded target/check validation, not arbitrary commands/paths.
WRITER = base.WRITER.replace("import json, os, stat, sys, time, uuid", "import json, os, re, stat, sys, time, uuid")
WRITER = WRITER.replace("if value['action'] != 'install' or str(uuid.UUID(value['id'])) != value['id']: raise SystemExit(11)", r'''
if value.get('action') not in ('check','install') or str(uuid.UUID(value['id'])) != value['id']: raise SystemExit(11)
fields={'id','action','familyId','userId','createdAt'}
if value['action']=='install':
 fields.add('target')
 target=value.get('target')
 if not isinstance(target,dict) or set(target)!={'version','fingerprint'}: raise SystemExit(11)
 if not isinstance(target['version'],str) or not re.fullmatch(r'(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})',target['version']): raise SystemExit(11)
 if not isinstance(target['fingerprint'],str) or not re.fullmatch('[0-9a-f]{64}',target['fingerprint']): raise SystemExit(11)
if set(value)!=fields: raise SystemExit(11)
''')


def publish(config, payload):
    result = subprocess.run(['/usr/sbin/runuser', '-u', config['appUser'], '--', '/usr/bin/python3', '-I', '-B',
                             '-c', WRITER, config['dataDirectory']], input=payload, capture_output=True, timeout=30,
                            env={'PATH': '/usr/bin:/bin', 'LANG': 'C', 'HOME': '/'})
    if result.returncode == 20: raise RequestError('REQUEST_EXISTS: existing queue preserved; inspect before retrying')
    if result.returncode: raise RequestError('REQUEST_PUBLICATION_UNCONFIRMED: inspect printed UUID before retrying')


def target(value):
    if (not isinstance(value, dict) or set(value) != {'version', 'fingerprint'} or
            not isinstance(value['version'], str) or not re.fullmatch(VERSION, value['version']) or
            not isinstance(value['fingerprint'], str) or not re.fullmatch('[0-9a-f]{64}', value['fingerprint'])):
        raise RequestError('TARGET_UNAVAILABLE: a fresh root-worker check must expose a valid target')
    return dict(value)


def ready(url, version, schema):
    if not isinstance(url, str) or not re.fullmatch(r'http://127\.0\.0\.1:[0-9]{1,5}/health/ready', url): return False
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs): return None
    try:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        with opener.open(url, timeout=5) as response:
            raw = response.read(1025)
            if len(raw) > 1024: return False
            body = json.loads(raw)
        return (body.get('ok') is True and body.get('version') == version and
                type(body.get('databaseSchema')) is int and body['databaseSchema'] == schema)
    except Exception: return False


class GithubRequest(base.ManualRequest):
    def __init__(self, root=pathlib.Path('/'), *, writer=publish, start=base.service_start, health=ready,
                 now=time.time, monotonic=time.monotonic, sleep=time.sleep, ask=input):
        super().__init__(root, writer, start, health, now, monotonic, sleep)
        self.ask = ask

    def config(self, require_enabled=True):
        config = super().config(require_enabled=False)  # No historical fixed 0.5.0 pin assumption.
        if (require_enabled and config['enabled'] is not True or config.get('releaseTrust') != 'github-https' or
                config.get('publicKeyFile') is not None or config.get('tokenFile') is not None or config['appUser'] != 'yachad'):
            raise RequestError('CONFIGURATION_BLOCKED: existing HTTPS policy/operator must already be enabled; no changes made')
        pin = config.get('installVersionPin')
        if pin is not None and (not isinstance(pin, str) or not re.fullmatch('v?' + VERSION, pin)):
            raise RequestError('VERSION_PIN_BLOCKED: existing pin is invalid and was preserved')
        return config

    def status(self):
        value = json.loads(self.read(STATE + '/status.json', 65536))
        if not isinstance(value, dict): raise RequestError('INVALID_STATUS: inspect locally')
        return value

    def enrollment(self, require_enabled=True):
        for path in (KEY, '/etc/yachad/release-signing.pub'):
            if os.path.lexists(self.path(path)): raise RequestError('SIGNED_ENROLLMENT_PRESENT: HTTPS request refused')
        policy_raw = self.read(POLICY, 8192, private=True)
        policy = json.loads(policy_raw)
        if (not isinstance(policy, dict) or set(policy) != {'format', 'trust', 'enabled', 'repository', 'channel', 'checkHours'} or
                type(policy['format']) is not int or policy['format'] != 2 or policy['trust'] != 'github-https' or
                type(policy['enabled']) is not bool or require_enabled and not policy['enabled'] or
                policy['repository'] != 'NehoraiHadad/yahad' or policy['channel'] != 'stable' or
                type(policy['checkHours']) is not int or not 1 <= policy['checkHours'] <= 168):
            raise RequestError('HTTPS_ENROLLMENT_REQUIRED: fixed HTTPS source must already be enabled')
        pins = {'updater.py': LAUNCHER_SHA256, 'updater-supervisor.py': SUPERVISOR_SHA256, 'updater-transport.py': TRANSPORT_SHA256}
        for name, digest in pins.items():
            if hashlib.sha256(self.read(DIRECTORY + name, 1024 * 1024)).hexdigest() != digest:
                raise RequestError('IMMUTABLE_BOUNDARY_MISMATCH: use the reviewed enrollment first')
        runtime_raw = self.read(STATE + '/updater-runtime.json', 8192, private=True)
        runtime = json.loads(runtime_raw)
        if not isinstance(runtime, dict) or runtime.get('phase') != 'stable':
            raise RequestError('RUNTIME_RECOVERY_REQUIRED: existing runtime journal preserved')
        active = runtime.get('active')
        if (not isinstance(active, dict) or set(active) != {'version', 'sha256', 'manifestSha256'} or
                not isinstance(active['version'], str) or not re.fullmatch(VERSION, active['version']) or
                any(not isinstance(active[x], str) or not re.fullmatch('[0-9a-f]{64}', active[x]) for x in ('sha256', 'manifestSha256'))):
            raise RequestError('RUNTIME_JOURNAL_INVALID: inspect locally')
        return policy_raw, runtime_raw

    @contextlib.contextmanager
    def locked(self):
        path = self.path(STATE + '/worker.lock'); self.ancestors(path.parent)
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        try:
            info = os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_nlink != 1 or stat.S_IMODE(info.st_mode) != 0o600:
                raise RequestError('UNSAFE_WORKER_LOCK: nothing queued')
            try: fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError: raise RequestError('UPDATER_BUSY: nothing queued; allow existing work to finish') from None
            yield
        finally: os.close(fd)

    def quiet(self, config, status):
        if status.get('operator') != config['operator'] or status.get('releaseTrust') != 'github-https':
            raise RequestError('STATUS_POLICY_MISMATCH: existing configuration/status preserved')
        if (status.get('phase') not in base.STABLE or status.get('modeChangePending') is not None or
                isinstance(status.get('lastModeRequest'), dict) and status['lastModeRequest'].get('state') == 'pending' or
                isinstance(status.get('lastRequest'), dict) and status['lastRequest'].get('state') == 'running'):
            raise RequestError('UNFINISHED_WORK: existing recovery/request preserved')

    def queue(self, action, selected=None, expected=None):
        if action not in ('check', 'install'): raise RequestError('INVALID_ACTION')
        if action == 'install': selected = target(selected)
        elif selected is not None: raise RequestError('INVALID_ACTION')
        request_id = str(uuid.uuid4())
        with self.locked():
            config, status, boundary = self.config(), self.status(), self.enrollment()
            self.quiet(config, status)
            if expected is not None and (config, boundary) != expected:
                raise RequestError('TARGET_CHANGED: configuration/enrollment changed after confirmation; nothing queued')
            if selected is not None:
                if (status.get('phase') != 'available' or status.get('latestVersion') != selected['version'] or
                        target(status.get('installTarget')) != selected):
                    raise RequestError('TARGET_CHANGED: a different release is available; review it explicitly')
                if config.get('installVersionPin') not in (None, selected['version'], 'v' + selected['version']):
                    raise RequestError('VERSION_PIN_BLOCKED: existing target pin preserved')
            value = dict(id=request_id, action=action, familyId=config['operator']['familyId'],
                         userId=config['operator']['userId'], createdAt=int(self.now() * 1000))
            if selected is not None: value['target'] = selected
            payload = json.dumps(value, separators=(',', ':')).encode()
            if len(payload) > 512: raise RequestError('REQUEST_TOO_LARGE: nothing queued')
            print('REQUEST_ID=' + request_id + ' ACTION=' + action, flush=True)
            self.writer(config, payload)
        self.start()  # The worker must be able to acquire its lock before dispatch.
        return request_id, config

    def observe(self, request_id, config, seconds, action=None, selected=None):
        deadline = self.monotonic() + seconds
        while True:
            status = self.status(); request = status.get('lastRequest')
            if isinstance(request, dict) and request.get('id') == request_id and request.get('action') in ('check', 'install'):
                if action is not None and request['action'] != action:
                    raise RequestError('REQUEST_ID_MISMATCH: action differs; inspect locally')
                if request.get('state') == 'failed':
                    reason = 'TARGET_CHANGED' if request.get('error') == 'target_changed' else 'REQUEST_FAILED'
                    raise RequestError(reason + ': matching request failed; do not clear recovery or restore old data')
                if request.get('state') == 'succeeded':
                    if request['action'] == 'check': return status
                    retained = target(request.get('target'))
                    if selected is not None and retained != selected:
                        raise RequestError('TARGET_CHANGED: completion does not match the confirmed target')
                    schema = status.get('targetDatabaseSchema')
                    if (status.get('currentVersion') != retained['version'] or status.get('phase') not in ('healthy', 'idle') or
                            status.get('error') is not None or type(schema) is not int or not 1 <= schema <= 1000000 or
                            not self.health(config['healthUrl'], retained['version'], schema)):
                        raise RequestError('EXPECTED_HEALTH_UNCONFIRMED: exact version/schema readiness not verified; no rollback attempted')
                    return status
            if self.monotonic() >= deadline: return None
            self.sleep(min(2, max(0, deadline - self.monotonic())))

    @staticmethod
    def pending(request_id):
        return 'REQUEST_PENDING_OR_UNCONFIRMED: work was not canceled; inspect with --status ' + request_id

    def install(self, seconds):
        print('Checking the selected release. Existing auto mode may already install during this check; no policy is changed.')
        check_id, config = self.queue('check')
        checked = self.observe(check_id, config, seconds, action='check')
        if checked is None: return self.pending(check_id)
        with self.locked():
            config, status, boundary = self.config(), self.status(), self.enrollment()
            self.quiet(config, status)
            if status.get('lastRequest', {}).get('id') != check_id:
                raise RequestError('CHECK_SUPERSEDED: review a new check before installing')
            current = status.get('currentVersion')
            selected_value = status.get('installTarget')
            selected = target(selected_value) if selected_value is not None else None
            if (isinstance(current, str) and re.fullmatch(VERSION, current) and
                    status.get('latestVersion') == current and (selected is None or selected['version'] == current)):
                schema = status.get('targetDatabaseSchema')
                if (status.get('phase') in ('healthy', 'idle') and status.get('error') is None and
                        type(schema) is int and 1 <= schema <= 1000000 and
                        self.health(config['healthUrl'], current, schema)):
                    return 'CURRENT_HEALTHY: correlated check completed; ' + current + ' and its recorded DB schema are ready; no extra install queued'
                return 'CURRENT_VERSION_REPORTED_HEALTH_UNCONFIRMED: no extra install queued; inspect exact version/schema readiness locally'
            if selected is None:
                raise RequestError('TARGET_UNAVAILABLE: root check did not expose an installable target')
            expected = config, boundary
        print('Available version: ' + selected['version'])
        print('Release fingerprint: ' + selected['fingerprint'])
        print('This request may first update the privileged worker, then the app and its supported DB schema.')
        if self.ask('Type INSTALL ' + selected['version'] + ' to install exactly this release: ').strip() != 'INSTALL ' + selected['version']:
            return 'CANCELED: check completed; no install request was queued'
        request_id, config = self.queue('install', selected=selected, expected=expected)
        if self.observe(request_id, config, seconds, action='install', selected=selected) is None: return self.pending(request_id)
        return 'INSTALL_HEALTHY: matching request completed; ' + selected['version'] + ' and its recorded DB schema are ready'

    def follow(self, request_id, seconds):
        config = self.config(require_enabled=False)
        result = self.observe(request_id, config, seconds)
        if result is None: return self.pending(request_id)
        request = result['lastRequest']
        if request['action'] == 'check': return 'CHECK_COMPLETED: no install was authorized by this read-only status command'
        return 'INSTALL_HEALTHY: matching target-bound request completed with exact version/schema readiness'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--status', help='Read-only observation of a printed check/install UUID')
    parser.add_argument('--wait-seconds', type=int, default=4200, help='Observe each request for 0..4200 seconds; timeout does not cancel')
    args = parser.parse_args()
    if not hasattr(os, 'geteuid') or os.geteuid() != 0 or not 0 <= args.wait_seconds <= 4200:
        raise RequestError('Run locally with sudo; wait must be 0..4200 seconds')
    if not args.status:
        try:
            uid = int(os.environ.get('SUDO_UID', '0')); owner = pwd.getpwuid(uid)
            if uid < 1000 or owner.pw_name in ('yachad', 'yachad-update'): raise ValueError()
        except (ValueError, KeyError): raise RequestError('Use the ordinary owner account via sudo; nothing queued') from None
        if not sys.stdin.isatty() or not sys.stderr.isatty(): raise RequestError('Interactive local owner confirmation required; nothing queued')
    helper = GithubRequest()
    if args.status:
        if str(uuid.UUID(args.status)) != args.status: raise RequestError('INVALID_REQUEST_ID')
        print(helper.follow(args.status, args.wait_seconds))
    else: print(helper.install(args.wait_seconds))


if __name__ == '__main__':
    try: main()
    except RequestError as error:
        print(str(error), file=sys.stderr); sys.exit(1)
    except (Exception, KeyboardInterrupt):
        print('GITHUB_REQUEST_UNCONFIRMED: inspect printed UUID before retrying; no cancellation or rollback attempted', file=sys.stderr)
        sys.exit(1)
