#!/usr/bin/env python3
"""Fixed stable-release transport for independently signed root runtimes.

Provisioned once with the immutable supervisor. The app manifest/HTTP API cannot
choose a root-code URL, key, command or trust policy. All I/O is bounded; only the
supervisor's pinned-signature verifier may install a downloaded candidate.
"""
import json
import math
import os
import pathlib
import hashlib
import pwd
import stat
import uuid
import re
import time
import urllib.error
import urllib.parse
import urllib.request

POLICY = '/etc/yachad/updater-source.json'
ASSETS = ('yachad-updater.json', 'yachad-updater.sig', 'yachad-updater.zip')
HOSTS = {'api.github.com', 'github.com', 'objects.githubusercontent.com', 'release-assets.githubusercontent.com'}
LIMITS = (8192, 64, 2 * 1024 * 1024)
RESULTS = {'checking', 'updated', 'unchanged', 'unavailable', 'failed', 'deferred'}
ERRORS = {'network', 'invalid_release', 'signature', 'protocol', 'downgrade', 'activation', 'configuration', 'busy'}


class TransportError(Exception): pass


def strict_json(raw):
    def pairs(values):
        output = {}
        for key, value in values:
            if key in output: raise TransportError('invalid_release')
            output[key] = value
        return output
    try:
        return json.loads(raw, object_pairs_hook=pairs,
                          parse_constant=lambda _: (_ for _ in ()).throw(TransportError('invalid_release')))
    except (ValueError, UnicodeError):
        raise TransportError('invalid_release') from None


def safe_url(url):
    try:
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme != 'https' or parsed.hostname not in HOSTS or parsed.port not in (None, 443) or
                parsed.username or parsed.password or parsed.fragment):
            raise TransportError('network')
    except (TypeError, ValueError):
        raise TransportError('network') from None


class Redirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        safe_url(newurl)
        following = super().redirect_request(request, fp, code, message, headers, newurl)
        if following: following.remove_header('Authorization')
        return following


def download(url, maximum, accept='application/octet-stream'):
    safe_url(url)
    request = urllib.request.Request(url, headers={'User-Agent': 'Yachad-root-runtime-updater',
                'Accept': accept, 'X-GitHub-Api-Version': '2022-11-28'})
    try:
        with urllib.request.build_opener(urllib.request.ProxyHandler({}), Redirect()).open(request, timeout=30) as response:
            chunks, total = [], 0
            deadline = time.monotonic() + 120
            while True:
                chunk = response.read(min(65536, maximum + 1 - total))
                if not chunk: return b''.join(chunks)
                chunks.append(chunk); total += len(chunk)
                if total > maximum or time.monotonic() > deadline: raise TransportError('network')
    except (OSError, urllib.error.URLError):
        raise TransportError('network') from None


def policy(supervisor):
    value = strict_json(supervisor.read(supervisor.path(POLICY), 8192, private=True))
    if (not isinstance(value, dict) or set(value) != {'format', 'enabled', 'repository', 'channel', 'checkHours'} or
            type(value['format']) is not int or value['format'] != 1 or type(value['enabled']) is not bool or
            not isinstance(value['repository'], str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}', value['repository']) or
            value['channel'] != 'stable' or type(value['checkHours']) is not int or not 1 <= value['checkHours'] <= 168):
        raise TransportError('configuration')
    return value


def app_policy(supervisor):
    value = strict_json(supervisor.read(supervisor.config_path, 8192, private=True))
    if (not isinstance(value, dict) or type(value.get('enabled')) is not bool or
            value.get('mode') not in ('auto', 'notify')):
        raise TransportError('configuration')
    return value


def request_pending(supervisor):
    # The supervisor lock serializes workers, not app request writers. Recheck
    # before staging; a newly queued request is left untouched for the current
    # or newly authenticated compatible worker to consume.
    config = app_policy(supervisor)
    data = config.get('dataDirectory') if isinstance(config, dict) else None
    if not isinstance(data, str) or not data.startswith('/') or '..' in pathlib.PurePosixPath(data).parts:
        raise TransportError('configuration')
    return any(os.path.lexists(supervisor.path(data) / name)
               for name in ('update-request.json', 'update-request.wake'))


def saved_status(supervisor):
    try: value = strict_json(supervisor.read(supervisor.state / 'updater-transport.json', 8192, private=True))
    except FileNotFoundError: return None
    if (not isinstance(value, dict) or set(value) != {'format', 'checkedAt', 'attempt', 'result', 'error', 'version'} or
            type(value['format']) is not int or value['format'] != 1 or
            type(value['checkedAt']) not in (int, float) or not math.isfinite(value['checkedAt']) or value['checkedAt'] < 0 or
            not isinstance(value['attempt'], str) or not re.fullmatch('[0-9a-f]{32}', value['attempt']) or
            value['result'] not in RESULTS or value['error'] is not None and value['error'] not in ERRORS or
            value['version'] is not None and (not isinstance(value['version'], str) or not re.fullmatch(r'\d{1,5}\.\d{1,5}\.\d{1,5}', value['version']))):
        raise TransportError('configuration')
    return value


def save(supervisor, checked_at, attempt, result, error=None, version=None):
    value = dict(format=1, checkedAt=checked_at, attempt=attempt, result=result, error=error, version=version)
    supervisor.atomic(supervisor.state / 'updater-transport.json', json.dumps(value, separators=(',', ':')).encode())
    return value


def release_assets(raw, repository):
    value = strict_json(raw)
    if (not isinstance(value, dict) or value.get('draft') is not False or value.get('prerelease') is not False or
            not isinstance(value.get('tag_name'), str) or not re.fullmatch(r'v?\d{1,5}\.\d{1,5}\.\d{1,5}', value['tag_name']) or
            not isinstance(value.get('assets'), list) or len(value['assets']) > 1000):
        raise TransportError('invalid_release')
    assets = {}
    for item in value['assets']:
        if not isinstance(item, dict) or not isinstance(item.get('name'), str) or item['name'] in assets:
            raise TransportError('invalid_release')
        assets[item['name']] = item
    present = [name for name in ASSETS if name in assets]
    if not present: return None  # Older app-only releases remain usable.
    if len(present) != len(ASSETS): raise TransportError('invalid_release')
    urls = []
    for name in ASSETS:
        url = assets[name].get('url')
        if not isinstance(url, str) or not re.fullmatch(r'https://api\.github\.com/repos/' + re.escape(repository) + r'/releases/assets/[0-9]+', url):
            raise TransportError('invalid_release')
        urls.append(url)
    return urls


def install_intent(supervisor, config, now_ms):
    """Read the same narrow local operator contract as Worker.request, not a URL.

    The independent root policy and pinned signature remain mandatory. This
    trigger never consumes/replaces the queue or acknowledges application work.
    """
    path = supervisor.path(config['dataDirectory']) / 'update-request.json'
    fd = None
    try:
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        before = os.fstat(fd)
        if (not stat.S_ISREG(before.st_mode) or before.st_uid != pwd.getpwnam(config['appUser']).pw_uid or
                before.st_mode & 0o077 or before.st_nlink != 1 or not 0 < before.st_size <= 512):
            return None
        raw = os.read(fd, 513)
        after = os.fstat(fd)
        if len(raw) != before.st_size or (before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns):
            return None
        value = strict_json(raw)
        if (not isinstance(value, dict) or set(value) != {'id', 'action', 'familyId', 'userId', 'createdAt'} or
                value['action'] != 'install' or str(uuid.UUID(value['id'])) != value['id'] or
                value['familyId'] != config['operator']['familyId'] or value['userId'] != config['operator']['userId'] or
                type(value['createdAt']) not in (int, float) or not math.isfinite(value['createdAt']) or
                abs(now_ms-value['createdAt']) > 600000):
            return None
        status = supervisor.app_status()
        if (any(isinstance(status.get(key), dict) and status[key].get('id') == value['id']
                for key in ('lastRequest', 'lastModeRequest')) or
                any(isinstance(item, dict) and item.get('id') == value['id'] for item in status.get('modeRequestSeen', []))):
            return None
        return hashlib.sha256(raw).hexdigest()
    except (OSError, ValueError, KeyError, TypeError, AttributeError, TransportError):
        return None
    finally:
        if fd is not None: os.close(fd)


def eligible(supervisor, command, selected, now_ms, expected_intent=None):
    current = policy(supervisor)
    app = app_policy(supervisor)
    if current != selected or not current['enabled'] or not app['enabled']:
        raise TransportError('busy')
    if command == 'requests':
        intent = install_intent(supervisor, app, now_ms)
        if intent is None or expected_intent is not None and intent != expected_intent:
            raise TransportError('busy')
        return intent
    if app['mode'] != 'auto' or request_pending(supervisor):
        raise TransportError('busy')
    return None


def finish(supervisor, checked_at, attempt, result, error=None, version=None):
    # A later serialized check owns the outcome. Never overwrite it after a slow
    # network response from an older attempt, even with equal wall-clock time.
    with supervisor.locked():
        previous = saved_status(supervisor)
        if previous is None or previous['attempt'] != attempt:
            return {'result': 'superseded'}
        return save(supervisor, checked_at, attempt, result, error, version)


def refresh(supervisor, command='run', *, fetch=download, now=time.time):
    """Authenticate a runtime before app work; no network on recovery paths.

    Normal automatic runs honor both independent root enablement and existing
    application auto/notify. A validated explicit local install may also update
    the runtime in notify mode; check-only/foreign/raw URL requests never may.
    """
    if command not in ('run', 'requests'): return {'result': 'skipped'}
    checked_at = now() * 1000
    attempt = None
    try: selected = policy(supervisor)
    except FileNotFoundError: return {'result': 'not_configured'}
    except Exception: return {'result': 'failed', 'error': 'configuration'}
    try:
        if not selected['enabled']: return {'result': 'disabled'}
        app = app_policy(supervisor)
        if not app['enabled']: return {'result': 'app_disabled'}
        if command == 'run' and app['mode'] != 'auto': return {'result': 'notify'}
        if command == 'requests' and install_intent(supervisor, app, checked_at) is None:
            return {'result': 'skipped'}
        with supervisor.locked():
            if supervisor.journal()['phase'] != 'stable': return {'result': 'deferred'}
            supervisor.require_quiet_app()
            intent = eligible(supervisor, command, selected, checked_at)
            previous = saved_status(supervisor)
            if command == 'run' and previous and 0 <= checked_at - previous['checkedAt'] < selected['checkHours'] * 3600000:
                return {'result': 'cached'}
            before = supervisor.journal()['active']
            attempt = uuid.uuid4().hex
            save(supervisor, checked_at, attempt, 'checking')
        repository = selected['repository']
        raw = fetch('https://api.github.com/repos/' + repository + '/releases/latest', 1024 * 1024, 'application/vnd.github+json')
        urls = release_assets(raw, repository)
        if urls is None: return finish(supervisor, checked_at, attempt, 'unavailable')
        payload = [fetch(url, maximum) for url, maximum in zip(urls, LIMITS)]
        def guard():
            eligible(supervisor, command, selected, now()*1000, intent)
        # The guard rechecks root policy + bounded intent after acquiring the
        # SAME lock retained throughout authentication, stage, probe and commit.
        installed = supervisor.install_bundle(*payload, guard=guard)
        return finish(supervisor, checked_at, attempt, 'unchanged' if installed == before else 'updated', version=installed['version'])
    except Exception as error:
        code = ('busy' if str(error) in ('app_busy', 'pending_runtime', 'busy') else
                'configuration' if isinstance(error, FileNotFoundError) else
                str(error) if str(error) in ERRORS else 'activation')
        result = 'deferred' if code == 'busy' else 'failed'
        if attempt is None: return {'result': result, 'error': code}
        try: return finish(supervisor, checked_at, attempt, result, code)
        except Exception: return {'result': 'failed', 'error': 'configuration'}
