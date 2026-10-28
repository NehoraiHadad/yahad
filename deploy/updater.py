#!/usr/bin/env python3
"""Root-owned, stdlib-only release worker. Never imports application code as root.

All package code and npm run as an isolated builder account. Release trust,
operator and paths are root configuration, never supplied by a web request.
"""
import fcntl
import hashlib
import io
import json
import os
import pathlib
import pwd
import re
import shutil
import signal
import sqlite3
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
import uuid
import math

CONFIG = pathlib.Path('/etc/yachad/updates.json')
ASSETS = ('yachad-release.json', 'yachad-release.sig', 'yachad-app.zip')
PUBLIC_REPOSITORY = 'NehoraiHadad/yahad'
PHASES = {'stopping', 'backing_up', 'activating', 'checking_health', 'rolling_back', 'recovery_required'}
HOSTS = {'api.github.com', 'github.com', 'objects.githubusercontent.com', 'release-assets.githubusercontent.com'}
UPDATER_PROTOCOL = 3
MAX_SCHEMA_ID = 1000000
RELEASE_METADATA = '.release-manifest.json'

# Linux UAPI values; unavailable kernel support still fails closed in fcntl.
F_ADD_SEALS = getattr(fcntl, 'F_ADD_SEALS', 1033)
F_GET_SEALS = getattr(fcntl, 'F_GET_SEALS', 1034)
SELECTION_SEALS = 0x0001 | 0x0002 | 0x0004 | 0x0008


class UpdateError(Exception):
    def __init__(self, code):
        self.code = code


def release_target(release, repository):
    """Opaque selected-object identity, not publisher authentication."""
    names = {'yachad-release.json', 'yachad-release.sig', 'yachad-app.zip',
             'yachad-updater.json', 'yachad-updater.sig', 'yachad-updater.zip'}
    if (not isinstance(repository, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}', repository) or not isinstance(release, dict) or
            type(release.get('id')) is not int or not 1 <= release['id'] <= 2**63 - 1 or
            release.get('draft') is not False or release.get('prerelease') is not False or
            not isinstance(release.get('tag_name'), str) or
            not re.fullmatch(r'v?(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})', release['tag_name']) or
            not isinstance(release.get('assets'), list) or len(release['assets']) > 1000):
        return None
    assets, seen = [], set()
    for item in release['assets']:
        if not isinstance(item, dict) or not isinstance(item.get('name'), str) or item['name'] in seen:
            return None
        seen.add(item['name'])
        if item['name'] not in names: continue
        url = item.get('url')
        if not isinstance(url, str) or not re.fullmatch(r'https://api\.github\.com/repos/' + re.escape(repository) + r'/releases/assets/[0-9]+', url):
            return None
        assets.append([item['name'], url])
    if not {'yachad-release.json', 'yachad-app.zip'}.issubset(seen): return None
    identity = dict(repository=repository, releaseId=release['id'], tag=release['tag_name'], assets=sorted(assets))
    raw = json.dumps(identity, sort_keys=True, separators=(',', ':')).encode()
    return dict(version=release['tag_name'].lstrip('v'), fingerprint=hashlib.sha256(raw).hexdigest())


def valid_target(value):
    return (isinstance(value, dict) and set(value) == {'version', 'fingerprint'} and
            isinstance(value['version'], str) and re.fullmatch(r'(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})', value['version']) is not None and
            isinstance(value['fingerprint'], str) and re.fullmatch('[0-9a-f]{64}', value['fingerprint']) is not None)


def schema_id(value):
    if type(value) is not int or not 1 <= value <= MAX_SCHEMA_ID:
        raise UpdateError('incompatible')
    return value


def manifest_contract(manifest):
    target = schema_id(manifest.get('databaseSchema'))
    if type(manifest.get('format')) is not int:
        raise UpdateError('incompatible')
    if manifest['format'] == 1:
        # Existing releases have only the historical schema-1 contract.
        if target != 1:
            raise UpdateError('incompatible')
        return target, []
    if manifest['format'] != 2:
        raise UpdateError('incompatible')
    minimum = manifest.get('minimumUpdaterProtocol')
    if type(minimum) is not int or not 1 <= minimum <= UPDATER_PROTOCOL:
        raise UpdateError('incompatible')
    sources = manifest.get('databaseMigrationsFrom')
    if not isinstance(sources, list) or len(sources) > 32:
        raise UpdateError('incompatible')
    for source in sources:
        if schema_id(source) >= target:
            raise UpdateError('incompatible')
    if len(set(sources)) != len(sources):
        raise UpdateError('incompatible')
    return target, sources


def schema_transition(previous, target, sources=()):
    # Migration code runs only as the app user. Authenticated metadata declares
    # the supported sources; no speculative migration, downgrade or root hook.
    schema_id(previous)
    schema_id(target)
    if previous != target and (previous >= target or previous not in sources):
        raise UpdateError('incompatible')


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r'v?\d{1,5}\.\d{1,5}\.\d{1,5}', value):
        raise UpdateError('incompatible')
    return tuple(int(x) for x in value.lstrip('v').split('.'))


def private_file(path):
    p = pathlib.Path(path)
    st = p.lstat()
    if not stat.S_ISREG(st.st_mode) or st.st_uid != 0 or st.st_mode & 0o077 or st.st_size > 8192:
        raise UpdateError('configuration')
    return p.read_bytes()


def atomic_json(path, data, group=None):
    path = pathlib.Path(path)
    temporary = path.with_name(path.name + '.tmp')
    # Directory is root-owned and non-writable by the application/builder.
    with temporary.open('w', encoding='utf8') as output:
        json.dump(data, output, separators=(',', ':'))
        output.flush()
        os.fsync(output.fileno())
    os.chmod(temporary, 0o640 if group is not None else 0o600)
    if group is not None:
        os.chown(temporary, 0, group)
    temporary.replace(path)
    sync_directory(path.parent)


def sync_directory(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def safe_url(url):
    p = urllib.parse.urlsplit(url)
    if p.scheme != 'https' or p.hostname not in HOSTS or p.port not in (None, 443) or p.username or p.password:
        raise UpdateError('network')


class Redirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        safe_url(newurl)
        redirect = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirect:
            redirect.remove_header('Authorization')
        return redirect


def download(url, maximum, token=None, accept='application/octet-stream'):
    safe_url(url)
    headers = {'User-Agent': 'Yachad-private-updater', 'Accept': accept, 'X-GitHub-Api-Version': '2022-11-28'}
    if token and urllib.parse.urlsplit(url).hostname == 'api.github.com':
        headers['Authorization'] = 'Bearer ' + token
    request = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.build_opener(Redirect()).open(request, timeout=30) as response:
            chunks, size = [], 0
            deadline = time.monotonic() + 300
            while True:
                b = response.read(min(1024 * 1024, maximum + 1 - size))
                if not b:
                    return b''.join(chunks)
                chunks.append(b)
                size += len(b)
                if size > maximum or time.monotonic() > deadline:
                    raise UpdateError('network')
    except (OSError, urllib.error.URLError):
        raise UpdateError('network') from None


def verify_manifest(manifest_bytes, archive):
    if len(manifest_bytes) > 8192:
        raise UpdateError('archive')
    try:
        manifest = json.loads(manifest_bytes)
        version(manifest['version'])
        manifest_contract(manifest)
        if manifest.get('archive') != ASSETS[2]:
            raise UpdateError('incompatible')
        if type(manifest.get('bytes')) is not int or not 1 <= manifest['bytes'] <= 64 * 1024 * 1024:
            raise UpdateError('archive')
        if len(archive) != manifest['bytes'] or hashlib.sha256(archive).hexdigest() != manifest.get('sha256'):
            raise UpdateError('archive')
        return manifest
    except (KeyError, ValueError, TypeError, AttributeError):
        raise UpdateError('archive') from None


def release_policy(config, status=None):
    # Missing policy means the existing signature contract, never an implicit downgrade.
    trust = config.get('releaseTrust', 'signature')
    if trust not in ('signature', 'github-https'):
        raise UpdateError('configuration')
    if trust == 'github-https':
        if (config.get('repository') != PUBLIC_REPOSITORY or config.get('tokenFile') or config.get('publicKeyFile') or
                (pathlib.Path('/etc/yachad/release-signing.pub').exists() or pathlib.Path('/etc/yachad/release-signing.pub').is_symlink()) or
                (status and status.get('releaseTrust', 'signature') != trust)):
            raise UpdateError('configuration')
    return trust


def release_assets(config, status=None):
    return ASSETS if release_policy(config, status) == 'signature' else (ASSETS[0], ASSETS[2])


def verify_release(manifest_bytes, signature, archive, public_key, directory):
    if len(manifest_bytes) > 8192 or len(signature) != 64:
        raise UpdateError('signature')
    m, sig = directory / 'manifest', directory / 'signature'
    m.write_bytes(manifest_bytes)
    sig.write_bytes(signature)
    check = subprocess.run(['/usr/bin/openssl', 'pkeyutl', '-verify', '-pubin', '-inkey', str(public_key),
                            '-rawin', '-in', str(m), '-sigfile', str(sig)], capture_output=True, timeout=15)
    if check.returncode:
        raise UpdateError('signature')
    return verify_manifest(manifest_bytes, archive)


def extract(archive, target):
    try:
        with zipfile.ZipFile(archive) as z:
            files = z.infolist()
            if len(files) > 2000 or sum(x.file_size for x in files) > 128 * 1024 * 1024:
                raise UpdateError('archive')
            seen = set()
            for info in files:
                name = pathlib.PurePosixPath(info.filename)
                mode = info.external_attr >> 16
                if (info.orig_filename != info.filename or name.is_absolute() or '..' in name.parts or '\\' in info.filename or
                        ':' in info.filename or info.filename in seen or
                        any(part.startswith('.') and part != '.env.example' for part in name.parts) or
                        stat.S_ISLNK(mode) or (stat.S_IFMT(mode) and not stat.S_ISREG(mode) and not stat.S_ISDIR(mode))):
                    raise UpdateError('archive')
                seen.add(info.filename)
                path = target.joinpath(*name.parts)
                if info.is_dir():
                    path.mkdir(parents=True, exist_ok=True)
                else:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with path.open('xb') as output:
                        shutil.copyfileobj(z.open(info), output)
            if not all((target / name).is_file() for name in ['package.json', 'package-lock.json', 'dist/index.html', 'src/main.ts', 'scripts/update-smoke.ts', 'scripts/update-backup.ts']):
                raise UpdateError('archive')
    except (zipfile.BadZipFile, OSError, RuntimeError):
        raise UpdateError('archive') from None


def extract_dependencies(content, target):
    if len(content) > 128 * 1024 * 1024:
        raise UpdateError('build')
    try:
        with zipfile.ZipFile(io.BytesIO(content)) as archive:
            files = archive.infolist()
            if not files or len(files) > 30000 or sum(x.file_size for x in files) > 512 * 1024 * 1024:
                raise UpdateError('build')
            seen = set()
            for info in files:
                name = pathlib.PurePosixPath(info.filename)
                mode = info.external_attr >> 16
                if (info.orig_filename != info.filename or name.is_absolute() or '..' in name.parts or
                        len(name.parts) < 2 or name.parts[0] != 'node_modules' or
                        '\\' in info.filename or ':' in info.filename or info.filename in seen or
                        not stat.S_ISREG(mode)):
                    raise UpdateError('build')
                seen.add(info.filename)
                path = target.joinpath(*name.parts)
                path.parent.mkdir(parents=True, exist_ok=True)
                with path.open('xb') as output, archive.open(info) as source:
                    shutil.copyfileobj(source, output)
                os.chmod(path, 0o750 if mode & 0o111 else 0o640)
    except (zipfile.BadZipFile, OSError, RuntimeError):
        raise UpdateError('build') from None


class Worker:
    def __init__(self, config):
        self.c = config
        self.state_dir = pathlib.Path(config['stateDirectory'])
        self.status_path = self.state_dir / 'status.json'
        self.status = self.load_status()
        self.group = pwd.getpwnam(config['appUser']).pw_gid

    def load_status(self):
        try:
            return json.loads(self.status_path.read_bytes())
        except FileNotFoundError:
            return {}
        except (OSError, ValueError):
            raise UpdateError('configuration') from None

    def save(self, **changes):
        following = dict(self.status)
        following.update(changes)
        following.setdefault('releaseTrust', self.c.get('releaseTrust', 'signature') if not self.status else 'signature')
        following.update(enabled=self.c['enabled'], mode=self.c['mode'], operator=self.c['operator'], updatedAt=int(time.time() * 1000))
        pending = following.get('modeChangePending')
        if isinstance(pending, dict) and pending.get('previousMode') in ('auto', 'notify'):
            following['mode'] = pending['previousMode']
        following['modeControlAllowed'] = self.c['enabled'] and self.c.get('allowUiModeControl') is True
        for field in ['currentVersion', 'latestVersion', 'checkedAt', 'error']:
            following.setdefault(field, None)
        following.setdefault('phase', 'idle')
        atomic_json(self.status_path, following, self.group)
        self.status = following

    def command(self, args, *, user=None, cwd=None, input=None, timeout=300, code='internal'):
        if user:
            args = ['/usr/sbin/runuser', '-u', user, '--'] + args
        home = pathlib.Path(self.c['buildDirectory']) / 'home'
        env = {'PATH': '/usr/local/bin:/usr/bin:/bin', 'HOME': str(home),
               'NODE_ENV': 'production', 'npm_config_cache': str(home / 'npm-cache')}
        try:
            child = subprocess.Popen(args, cwd=cwd, stdin=subprocess.PIPE if input is not None else subprocess.DEVNULL,
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, start_new_session=True)
            try:
                stdout, _stderr = child.communicate(input=input, timeout=timeout)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.communicate()
                raise UpdateError(code) from None
            if child.returncode:
                raise UpdateError(code)
            return stdout
        except (subprocess.TimeoutExpired, OSError):
            raise UpdateError(code) from None

    def current(self):
        link = pathlib.Path(self.c['currentLink'])
        path = link.resolve(strict=True)
        root = pathlib.Path(self.c['releasesDirectory']).resolve(strict=True)
        if not path.is_relative_to(root) or path == root or not link.is_symlink():
            raise UpdateError('configuration')
        return path

    def schema(self):
        path = pathlib.Path(self.c['dataDirectory']) / 'app.sqlite'
        try:
            with sqlite3.connect(path.as_uri() + '?mode=ro', uri=True) as db:
                rows = db.execute('SELECT version FROM schema_version').fetchall()
                if len(rows) != 1:
                    raise UpdateError('incompatible')
                return schema_id(rows[0][0])
        except (sqlite3.Error, TypeError):
            raise UpdateError('incompatible') from None

    def app_schema(self, release):
        # Written by root only after authenticating the release; archive
        # extraction rejects this dotfile, so package code cannot supply it.
        path = pathlib.Path(release) / RELEASE_METADATA
        try:
            info = path.lstat()
        except FileNotFoundError:
            return 1  # Narrow bridge for installations predating metadata.
        if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022 or info.st_size > 8192:
            raise UpdateError('configuration')
        try:
            manifest = json.loads(path.read_bytes())
            package = json.loads((path.parent / 'package.json').read_bytes())
            if manifest['version'] != package['version']:
                raise UpdateError('incompatible')
            return manifest_contract(manifest)[0]
        except (KeyError, ValueError, TypeError, AttributeError):
            raise UpdateError('incompatible') from None

    def backup_inventory(self):
        # Record names/metadata only, never read application-controlled backup
        # contents as root. A successful backup must identify one new private
        # archive so local recovery can locate it without guessing "latest".
        directory = pathlib.Path(self.c['backupDirectory'])
        try:
            if not stat.S_ISDIR(directory.lstat().st_mode):
                raise UpdateError('backup')
        except FileNotFoundError:
            return set()
        found = set()
        for index, path in enumerate(directory.iterdir()):
            if index >= 1000:
                raise UpdateError('backup')
            if not re.fullmatch(r'yachad-[0-9]{1,16}\.backup', path.name):
                continue
            info = path.lstat()
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != pwd.getpwnam(self.c['appUser']).pw_uid or
                    info.st_mode & 0o077 or info.st_size < 1):
                raise UpdateError('backup')
            found.add(path.name)
        return found

    def switch(self, target):
        root = pathlib.Path(self.c['releasesDirectory']).resolve(strict=True)
        target = pathlib.Path(target).resolve(strict=True)
        if not target.is_relative_to(root) or target == root or target.stat().st_uid != 0:
            raise UpdateError('configuration')
        link = pathlib.Path(self.c['currentLink'])
        temporary = link.with_name('.current-update')
        temporary.unlink(missing_ok=True)
        temporary.symlink_to(target, target_is_directory=True)
        temporary.replace(link)
        sync_directory(link.parent)

    def health(self, expected=None, expected_schema=None):
        if expected_schema is None:
            expected_schema = self.schema()
        schema_id(expected_schema)
        deadline = time.monotonic() + self.c.get('healthTimeoutSeconds', 90)
        url = self.c['healthUrl']
        if not re.fullmatch(r'http://127\.0\.0\.1:\d{1,5}/health/ready', url):
            raise UpdateError('configuration')
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        while time.monotonic() < deadline:
            try:
                self.command(['/usr/bin/systemctl', 'is-active', '--quiet', self.c['service']], timeout=5, code='health')
                with opener.open(url, timeout=3) as response:
                    body = json.loads(response.read(1024))
                if (body.get('ok') is True and type(body.get('databaseSchema')) is int and
                        body['databaseSchema'] == expected_schema and self.schema() == expected_schema and
                        (expected is None or body.get('version') == expected)):
                    return
            except (OSError, ValueError, UpdateError):
                pass
            time.sleep(2)
        raise UpdateError('health')

    def recover(self, boot=False):
        if self.status.get('phase') not in PHASES:
            return True
        stopped = False
        try:
            if self.status.get('runtimeDigest') != getattr(self, 'runtime_digest', None):
                raise UpdateError('incompatible')
            previous_schema = schema_id(self.status.get('previousDatabaseSchema', 1))
            # A crash after positive readiness but before the healthy journal
            # commit is not evidence that the migrated target failed. Adopt only
            # an exact journalled code/manifest/schema/runtime pairing. Never
            # retry a candidate whose actual health failure was already recorded.
            if self.status.get('phase') == 'checking_health' and self.status.get('error') is None:
                try:
                    self.recovery_target()
                    if boot:
                        # The app is ordered AFTER this boot unit. Do not start it
                        # here (dependency cycle), nor claim readiness prematurely.
                        # Preserve checking_health; next worker validates live health.
                        return True
                    self.health(self.status['targetVersion'], expected_schema=self.status['targetDatabaseSchema'])
                except (UpdateError, OSError, ValueError, TypeError, KeyError):
                    # Historical/incomplete metadata cannot authorize adoption.
                    # The normal same-schema rollback gate still applies below.
                    pass
                else:
                    self.save(phase='healthy', currentVersion=self.status['targetVersion'], failedVersion=None, error=None)
                    self.finish_adopted_request()
                    return True
            # Stop before checking schema: incompatible code must not continue writing.
            # At boot, systemd orders this unit before the application starts.
            if not boot:
                self.command(['/usr/bin/systemctl', 'stop', self.c['service']])
                stopped = True
            # Old worker journals describe schema 1. Never infer the previous
            # app's schema from the now-migrated DB or restore a stale backup.
            if self.schema() != previous_schema:
                raise UpdateError('incompatible')
            previous = pathlib.Path(self.status['previousPath'])
            if self.app_schema(previous) != previous_schema:
                raise UpdateError('incompatible')
            previous_version = json.loads((previous / 'package.json').read_bytes())['version']
            if previous_version != self.status.get('rollbackVersion', self.status.get('currentVersion')):
                raise UpdateError('rollback')
            self.save(phase='rolling_back', failedVersion=self.status.get('latestVersion'))
            self.switch(self.status['previousPath'])
            if boot:
                self.save(phase='rollback_pending_health', error=self.status.get('error') or 'health')
                return True
            self.command(['/usr/bin/systemctl', 'start', self.c['service']])
            self.health(previous_version, expected_schema=previous_schema)
            self.save(phase='rolled_back', currentVersion=previous_version, error=self.status.get('error') or 'health')
            return True
        except Exception:
            if not boot and not stopped:
                # A mismatched or unhealthy adoption candidate must not be left
                # running. Never switch to old code against a migrated schema.
                try: self.command(['/usr/bin/systemctl', 'stop', self.c['service']])
                except Exception: pass
            self.save(phase='recovery_required', error='rollback')
            return False

    def recovery_target(self):
        target = self.status.get('targetPath')
        expected = self.status.get('targetVersion')
        digest = self.status.get('targetManifestSha256')
        schema = schema_id(self.status.get('targetDatabaseSchema'))
        if (not isinstance(target, str) or not isinstance(expected, str) or
                not isinstance(digest, str) or not re.fullmatch('[0-9a-f]{64}', digest)):
            raise UpdateError('incompatible')
        version(expected)
        current = self.current()
        if (current != pathlib.Path(target) or current.name != 'v' + expected or
                self.schema() != schema or self.app_schema(current) != schema or
                hashlib.sha256((current / RELEASE_METADATA).read_bytes()).hexdigest() != digest or
                json.loads((current / 'package.json').read_bytes()).get('version') != expected):
            raise UpdateError('incompatible')

    def finish_adopted_request(self):
        # Recovery may have marked an interrupted request internal/failed before
        # discovering exact healthy activation. Only the request bound BEFORE
        # downtime can be completed; never acknowledge a newer queued action.
        request = self.status.get('lastRequest')
        if (isinstance(request, dict) and request.get('action') == 'install' and
                request.get('id') == self.status.get('activationRequestId') and
                valid_target(request.get('target')) and request['target'] == self.status.get('activationRequestTarget') and
                request['target']['version'] == self.status.get('targetVersion') and
                (request.get('state') == 'running' or request.get('state') == 'failed' and request.get('error') == 'internal')):
            self.save(lastRequest=dict(request, state='succeeded', error=None, finishedAt=int(time.time()*1000)))

    def read_request(self):
        path = pathlib.Path(self.c['dataDirectory']) / 'update-request.json'
        fd = None
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            st = os.fstat(fd)
            if not stat.S_ISREG(st.st_mode) or st.st_uid != pwd.getpwnam(self.c['appUser']).pw_uid or st.st_mode & 0o077 or not 0 < st.st_size <= 512:
                raise ValueError()
            data = json.loads(os.read(fd, 513))
            return data if isinstance(data, dict) else None
        except (OSError, ValueError, TypeError):
            return None
        finally:
            if fd is not None: os.close(fd)

    def request(self):
        path = pathlib.Path(self.c['dataDirectory']) / 'update-request.json'
        data = self.read_request()
        consume = True
        try:
            if not data: return None
            if (data.get('action') not in ('check', 'install', 'set_mode') or
                    data.get('familyId') != self.c['operator']['familyId'] or data.get('userId') != self.c['operator']['userId'] or
                    type(data.get('createdAt')) not in (int, float) or not math.isfinite(data['createdAt'])):
                return None
            if data['action'] == 'set_mode':
                try:
                    if str(uuid.UUID(data.get('id', ''))) != data['id'] or data.get('mode') not in ('auto', 'notify'):
                        return None
                except (ValueError, TypeError, AttributeError): return None
                if set(data) != {'id', 'action', 'mode', 'familyId', 'userId', 'createdAt'}: return None
                consume = False
                seen = self.mode_seen()
                error = ('replayed' if any(x['id'] == data['id'] for x in seen) else
                         'expired' if abs(time.time() * 1000 - data['createdAt']) > 600000 else
                         'busy' if len(seen) >= 64 else None)
                if error:
                    self.finish_mode_request(data, 'rejected', error)
                else:
                    # Journal before removing the app-owned queue file. Root config
                    # and the acknowledgement can then be reconciled after a crash.
                    self.save(modeChangePending={'id':data['id'], 'mode':data['mode'], 'previousMode':self.c['mode'], 'operator':dict(self.c['operator'])},
                              lastModeRequest={'id':data['id'], 'mode':data['mode'], 'state':'pending', 'error':None})
                consume = True
                return 'set_mode'
            # Older local request writers did not always include a UUID. Keep
            # their command contract, but correlate modern UI work durably.
            try:
                correlated = str(uuid.UUID(data.get('id', ''))) == data.get('id')
            except (ValueError, TypeError, AttributeError): correlated = False
            if abs(time.time() * 1000 - data['createdAt']) > 600000:
                if correlated:
                    consume = False
                    now = int(time.time()*1000)
                    self.save(lastRequest={'id':data['id'], 'action':data['action'], 'state':'failed',
                              'startedAt':now, 'finishedAt':now, 'error':'expired',
                              **({'target':data['target']} if valid_target(data.get('target')) else {})})
                    consume = True
                    return 'expired'
                return None
            if correlated:
                consume = False
                self.save(lastRequest={'id':data['id'], 'action':data['action'], 'state':'running',
                          'startedAt':int(time.time()*1000), 'finishedAt':None, 'error':None,
                          **({'target':data['target']} if valid_target(data.get('target')) else {})})
                consume = True
            if data['action'] == 'install':
                self.requested_install_target = data.get('target')
                self.saved_install_target = self.status.get('installTarget')
                self.saved_install_phase = self.status.get('phase')
            return data['action']
        finally:
            if consume: path.unlink(missing_ok=True)

    def mode_seen(self):
        now = time.time() * 1000
        return [x for x in self.status.get('modeRequestSeen', []) if isinstance(x, dict) and
                isinstance(x.get('id'), str) and type(x.get('at')) in (int, float) and math.isfinite(x['at']) and abs(now-x['at']) <= 1200000]

    def finish_request(self, state, error=None):
        request = self.status.get('lastRequest')
        if isinstance(request, dict) and request.get('state') == 'running':
            self.save(lastRequest=dict(request, state=state, finishedAt=int(time.time()*1000), error=error))

    def finish_mode_request(self, request, state, error=None):
        seen = [x for x in self.mode_seen() if x['id'] != request['id']]
        if len(seen) < 64: seen.append({'id':request['id'], 'at':int(time.time()*1000)})
        self.save(modeChangePending=None, modeRequestSeen=seen,
                  lastModeRequest={'id':request['id'], 'mode':request['mode'], 'state':state, 'error':error})

    def apply_mode_request(self):
        request = self.status.get('modeChangePending')
        if not request: return
        if (not isinstance(request, dict) or request.get('mode') not in ('auto', 'notify') or
                not isinstance(request.get('id'), str) or len(request['id']) != 36):
            raise UpdateError('configuration')
        try:
            if str(uuid.UUID(request['id'])) != request['id']: raise ValueError()
        except ValueError: raise UpdateError('configuration') from None
        queued = self.read_request()
        if queued and queued.get('id') == request['id']:
            (pathlib.Path(self.c['dataDirectory']) / 'update-request.json').unlink(missing_ok=True)
        if not self.c['enabled'] or self.c.get('allowUiModeControl') is not True:
            self.finish_mode_request(request, 'rejected', 'local_setup')
            return
        if request.get('operator') != self.c['operator']:
            self.finish_mode_request(request, 'rejected', 'operator_changed')
            return
        try:
            fresh = json.loads(private_file(CONFIG))
            if {k:v for k,v in fresh.items() if k != 'mode'} != {k:v for k,v in self.c.items() if k != 'mode'}:
                raise UpdateError('configuration')
            if fresh['mode'] != request['mode']:
                fresh['mode'] = request['mode']
                atomic_json(CONFIG, fresh)
            self.c = fresh
            self.finish_mode_request(request, 'applied')
        except (OSError, ValueError, UpdateError):
            # Keep root intent pending; a write may have committed before an fsync
            # failure. Never claim rejection/applied until private config is read.
            self.save(modeChangePending=request, lastModeRequest={'id':request['id'], 'mode':request['mode'], 'state':'pending', 'error':'configuration'})

    def release(self, token):
        required = release_assets(self.c, self.status)
        repo = self.c['repository']
        if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
            raise UpdateError('configuration')
        selected = getattr(self, 'release_selection', None)
        raw = (json.dumps(selected['release']).encode() if selected is not None else
               download(f'https://api.github.com/repos/{repo}/releases/latest', 1024 * 1024, token, 'application/vnd.github+json'))
        try:
            release = json.loads(raw)
            if release.get('draft') is not False or release.get('prerelease') is not False:
                raise UpdateError('incompatible')
            version(release['tag_name'])
            assets = {x['name']: x for x in release['assets']}
            if len(assets) != len(release['assets']) or not all(name in assets for name in required):
                raise UpdateError('archive')
            if self.c.get('releaseTrust') == 'github-https':
                for name in required:
                    if not re.fullmatch(r'https://api\.github\.com/repos/NehoraiHadad/yahad/releases/assets/[0-9]+', assets[name]['url']):
                        raise UpdateError('network')
            return release, assets
        except (KeyError, ValueError, TypeError):
            raise UpdateError('network') from None

    def install(self, release, assets, token, current):
        work = pathlib.Path(tempfile.mkdtemp(prefix='release-', dir=self.state_dir))
        build_root = pathlib.Path(self.c['buildDirectory'])
        info = build_root.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
            raise UpdateError('configuration')
        build = pathlib.Path(tempfile.mkdtemp(prefix='build-', dir=build_root))
        destination = None
        activated = False
        try:
            trust = release_policy(self.c, self.status)
            required = release_assets(self.c, self.status)
            limits = {ASSETS[0]:8192, ASSETS[1]:64, ASSETS[2]:64 * 1024 * 1024}
            self.save(phase='downloading')
            content = {name:download(assets[name]['url'], limits[name], token) for name in required}
            selection = getattr(self, 'release_selection', None)
            if selection is not None and hashlib.sha256(content[ASSETS[0]]).hexdigest() != selection.get('appManifestSha256'):
                raise UpdateError('target_changed')
            self.save(phase='verifying')
            if trust == 'signature':
                key = pathlib.Path(self.c['publicKeyFile'])
                if not key.is_file() or key.is_symlink() or key.stat().st_uid != 0 or key.stat().st_mode & 0o022:
                    raise UpdateError('configuration')
                manifest = verify_release(content[ASSETS[0]], content[ASSETS[1]], content[ASSETS[2]], key, work)
            else:
                manifest = verify_manifest(content[ASSETS[0]], content[ASSETS[2]])
            if version(manifest['version']) != version(release['tag_name']):
                raise UpdateError('incompatible')
            previous_schema = self.schema()
            if self.app_schema(current) != previous_schema:
                raise UpdateError('incompatible')
            target_schema, migration_sources = manifest_contract(manifest)
            schema_transition(previous_schema, target_schema, migration_sources)
            free = shutil.disk_usage(self.c['releasesDirectory']).free
            data_bytes = sum(p.stat().st_size for p in pathlib.Path(self.c['dataDirectory']).rglob('*') if p.is_file() and not p.is_symlink())
            if free < self.c.get('minimumFreeMiB', 512) * 1048576 + data_bytes:
                raise UpdateError('storage')
            archive = work / ASSETS[2]
            archive.write_bytes(content[ASSETS[2]])
            source = work / 'source'
            source.mkdir(mode=0o700)
            extract(archive, source)
            if (source / 'node_modules').exists():
                raise UpdateError('archive')
            package = json.loads((source / 'package.json').read_bytes())
            if package.get('version') != manifest['version']:
                raise UpdateError('archive')
            user = pwd.getpwnam(self.c['builderUser'])
            shutil.copytree(source, build, dirs_exist_ok=True)
            # The root-owned parent cannot be renamed by the builder. Hand off
            # the directory LAST, so metadata changes never race builder writes.
            for p in build.rglob('*'):
                os.chown(p, user.pw_uid, user.pw_gid, follow_symlinks=False)
                os.chmod(p, 0o700 if p.is_dir() else 0o600)
            os.chown(build, user.pw_uid, user.pw_gid)
            self.save(phase='building')
            self.command([self.c['npm'], 'ci', '--omit=dev', '--ignore-scripts', '--no-audit', '--no-fund'],
                         user=self.c['builderUser'], cwd=build, timeout=1200, code='build')
            smoke = [self.c['node'], '--import', 'tsx', 'scripts/update-smoke.ts']
            if manifest['format'] == 2:
                smoke += ['--database-schema', str(target_schema), '--database-migrations-from', json.dumps(migration_sources, separators=(',', ':'))]
            self.command(smoke, user=self.c['builderUser'], cwd=build, timeout=90, code='build')
            destination = pathlib.Path(self.c['releasesDirectory']) / ('v' + manifest['version'])
            if destination.exists():
                # A crashed preparation may leave an unpublished release. Preserve
                # the current tree and every journalled rollback tree.
                if (destination == self.current() or str(destination) == self.status.get('previousPath') or
                        destination.is_symlink() or destination.stat().st_uid != 0 or not destination.is_dir()):
                    raise UpdateError('configuration')
                shutil.rmtree(destination)
            # Export as builder: privileged code never traverses or opens files
            # controlled by that account. Preserve the authenticated source.
            dependencies = self.command(['/usr/bin/python3', '/usr/local/lib/yachad-updater/export-dependencies.py', str(build)],
                                        user=self.c['builderUser'], timeout=300, code='build')
            shutil.copytree(source, destination)
            (destination / RELEASE_METADATA).write_bytes(content[ASSETS[0]])
            extract_dependencies(dependencies, destination)
            for p in [destination, *destination.rglob('*')]:
                if p.is_symlink():
                    resolved = p.resolve(strict=True)
                    if not resolved.is_relative_to(destination):
                        raise UpdateError('archive')
                    os.lchown(p, 0, self.group)
                else:
                    os.chown(p, 0, self.group)
                    os.chmod(p, 0o750 if p.is_dir() or p.stat().st_mode & 0o111 else 0o640)
            # Require a locally provisioned secret before any downtime. It never reaches the app UI.
            passphrase = private_file(self.c['backupPassphraseFile']).strip()
            if not 18 <= len(passphrase) <= 1024:
                raise UpdateError('configuration')
            # Persist the complete new tree before publishing the intent to switch.
            self.command(['/usr/bin/sync', '-f', str(destination)], timeout=120, code='storage')
            request = self.status.get('lastRequest')
            correlated = (isinstance(request, dict) and request.get('state') == 'running' and
                          request.get('action') == 'install' and valid_target(request.get('target')) and
                          request['target']['version'] == manifest['version'])
            self.save(phase='stopping', previousPath=str(current), rollbackVersion=self.status['currentVersion'],
                      previousDatabaseSchema=previous_schema, targetDatabaseSchema=manifest['databaseSchema'],
                      targetPath=str(destination), targetVersion=manifest['version'],
                      targetManifestSha256=hashlib.sha256(content[ASSETS[0]]).hexdigest(),
                      activationRequestId=request['id'] if correlated else None,
                      activationRequestTarget=request['target'] if correlated else None,
                      runtimeDigest=getattr(self, 'runtime_digest', None), backupId=None, error=None)
            self.command(['/usr/bin/systemctl', 'stop', self.c['service']])
            if self.schema() != previous_schema:
                raise UpdateError('incompatible')
            self.save(phase='backing_up')
            backups_before = self.backup_inventory()
            self.command(['/usr/bin/python3', '/usr/local/lib/yachad-updater/data-lock.py', self.c['dataDirectory'],
                          self.c['node'], '--import', 'tsx', 'scripts/update-backup.ts', self.c['dataDirectory'],
                          self.c['masterKeyFile'], self.c['backupDirectory']], user=self.c['appUser'],
                         cwd=current, input=passphrase, timeout=900, code='backup')
            self.command(['/usr/bin/sync', '-f', self.c['backupDirectory']], timeout=120, code='backup')
            created_backups = self.backup_inventory() - backups_before
            if len(created_backups) != 1:
                raise UpdateError('backup')
            if self.schema() != previous_schema:
                raise UpdateError('incompatible')
            self.save(phase='activating', backupId=created_backups.pop())
            self.switch(destination)
            self.command(['/usr/bin/systemctl', 'start', self.c['service']])
            self.save(phase='checking_health')
            self.health(manifest['version'], expected_schema=manifest['databaseSchema'])
            self.save(phase='healthy', currentVersion=manifest['version'], failedVersion=None, error=None)
            activated = True
            self.prune_releases(destination, current)
        finally:
            shutil.rmtree(work, ignore_errors=True)
            shutil.rmtree(build, ignore_errors=True)
            if destination and not activated and destination.exists() and self.current() != destination:
                shutil.rmtree(destination, ignore_errors=True)

    def prune_releases(self, active, previous):
        root = pathlib.Path(self.c['releasesDirectory'])
        for p in root.iterdir():
            if p not in (active, previous) and not p.is_symlink() and p.is_dir() and re.fullmatch(r'v\d+\.\d+\.\d+', p.name):
                shutil.rmtree(p)

    def run(self, request_only=False):
        # PathChanged also observes removal. A stale event must never turn into
        # a scheduled release check (which could install in auto mode).
        if request_only and not os.path.lexists(pathlib.Path(self.c['dataDirectory']) / 'update-request.json'):
            return
        # Acquiring the same flock means a previous running request is orphaned,
        # not evidence that it succeeded. Recovery still has priority below.
        self.finish_request('failed', 'internal')
        was_recovering = self.status.get('phase') in PHASES
        if not self.recover():
            return
        if was_recovering:
            return
        if self.status.get('phase') == 'rollback_pending_health':
            try:
                if self.status.get('runtimeDigest') != getattr(self, 'runtime_digest', None):
                    self.command(['/usr/bin/systemctl', 'stop', self.c['service']])
                    raise UpdateError('incompatible')
                self.health(self.status.get('rollbackVersion', self.status.get('currentVersion')),
                            expected_schema=self.status.get('previousDatabaseSchema', 1))
                self.save(phase='rolled_back')
            except Exception:
                self.save(phase='recovery_required', error='rollback')
            return
        if self.status.get('modeChangePending'):
            self.apply_mode_request()
            return
        if self.status.get('phase') in ('checking', 'preparing', 'downloading', 'verifying', 'building'):
            # A previous worker stopped before touching the active service.
            self.save(phase='failed', error='build', failedVersion=self.status.get('latestVersion'))
        if not self.c['enabled']:
            self.save(phase='idle', error=None)
            return
        action = self.request()
        if action == 'expired':
            # Explicit stale intent is terminal, never an implicit scheduled
            # auto-install in the same timer invocation that consumes it.
            return
        if action == 'set_mode':
            self.apply_mode_request()
            return
        if request_only and not action:
            return
        selected = getattr(self, 'release_selection', None)
        usable_selection = isinstance(selected, dict) and 'release' in selected
        if (not action and not usable_selection and self.status.get('checkedAt', 0) and
                time.time() * 1000 - self.status['checkedAt'] < self.c.get('checkHours', 6) * 3600000):
            self.save()
            return
        try:
            if action == 'install' and (getattr(self, 'require_release_selection', False) or getattr(self, 'requested_install_target', None) is not None):
                if (not valid_target(getattr(self, 'requested_install_target', None)) or
                        self.requested_install_target != getattr(self, 'saved_install_target', None) or
                        getattr(self, 'saved_install_phase', None) != 'available'):
                    raise UpdateError('target_changed')
            if ((isinstance(selected, dict) and 'error' in selected) or
                    getattr(self, 'require_release_selection', False) and not usable_selection and
                    (self.c['mode'] == 'auto' or action == 'install')):
                # No joined root transaction means no app attempt. In particular
                # do not advance its cache or poison failedVersion because the
                # root schedule is cached/deferred/offline. A selected release
                # overrides the independent legacy app cache above.
                if action in ('check', 'install'):
                    self.save(phase='failed', error=selected.get('error', 'network') if isinstance(selected, dict) else 'network')
                return
            current = self.current()
            current_version = json.loads((current / 'package.json').read_bytes())['version']
            version(current_version)
            token = private_file(self.c['tokenFile']).decode().strip() if self.c.get('tokenFile') else None
            # Bind the active runtime before the first nonterminal phase, not
            # only before downtime: interrupted downloads/builds also dispatch
            # through the supervisor after a restart.
            self.save(phase='checking', runtimeDigest=getattr(self, 'runtime_digest', None), error=None)
            release, assets = self.release(token)
            newest = release['tag_name'].lstrip('v')
            target = release_target(release, self.c.get('repository'))
            if action == 'install' and getattr(self, 'requested_install_target', None) is not None and target != self.requested_install_target:
                raise UpdateError('target_changed')
            self.save(currentVersion=current_version, latestVersion=newest, checkedAt=int(time.time() * 1000), error=None,
                      installTarget=target,
                      phase='available' if version(newest) > version(current_version) else 'idle')
            if version(newest) > version(current_version) and (action == 'install' or self.c['mode'] == 'auto'):
                # A failed/deferred HTTPS root-runtime transaction must not turn
                # into an unrelated second latest-release install. Checks remain
                # readable; boot/recovery and disabled root updates are offline.
                if self.c.get('installVersionPin') is not None and version(newest) != version(self.c['installVersionPin']):
                    raise UpdateError('incompatible')
                # A failed version is not retried automatically forever; explicit install retries it.
                if (self.status.get('failedVersion') == newest and action != 'install' and
                        self.status.get('failedRuntimeDigest') == getattr(self, 'runtime_digest', None)):
                    self.save(phase='failed', error='health')
                    return
                self.save(phase='preparing')
                self.install(release, assets, token, current)
        except UpdateError as e:
            failure = {} if e.code == 'target_changed' else dict(failedVersion=self.status.get('latestVersion'),
                                                               failedRuntimeDigest=getattr(self, 'runtime_digest', None))
            self.save(error=e.code, **failure)
            if self.status.get('phase') in PHASES:
                self.recover()
            else:
                self.save(phase='incompatible' if e.code == 'incompatible' else 'failed')
        except Exception:
            self.save(error='internal')
            if self.status.get('phase') in PHASES:
                self.recover()
            else:
                self.save(phase='failed')
        finally:
            if action in ('check', 'install'):
                error = self.status.get('error')
                self.finish_request('failed' if error else 'succeeded', error)


def load_config():
    config = json.loads(private_file(CONFIG))
    if config.get('mode') not in ('notify', 'auto') or type(config.get('enabled')) is not bool:
        raise UpdateError('configuration')
    if type(config.get('allowUiModeControl', False)) is not bool:
        raise UpdateError('configuration')
    if config.get('installVersionPin') is not None:
        version(config['installVersionPin'])
    return config


def queued_stamp(config):
    try:
        info = (pathlib.Path(config['dataDirectory']) / 'update-request.json').lstat()
        return info.st_dev, info.st_ino, info.st_mtime_ns, info.st_size
    except FileNotFoundError:
        return None


def run_requests(state, runtime_digest=None, release_selection=None, require_release_selection=False):
    # Drain a follow-on click arriving while this service is running. Reload
    # policy after each serialized action, especially a confirmed mode change.
    # No progress means recovery/configuration needs attention, not a busy loop.
    for _ in range(16):
        config = load_config()
        if pathlib.Path(config['stateDirectory']) != state:
            raise UpdateError('configuration')
        before = queued_stamp(config)
        if before is None:
            return
        worker = Worker(config)
        worker.runtime_digest = runtime_digest
        worker.release_selection = release_selection
        worker.require_release_selection = require_release_selection
        pending = worker.status.get('modeChangePending')
        worker.run(request_only=True)
        if queued_stamp(config) == before and worker.status.get('modeChangePending') == pending:
            return


def parse_command(arguments):
    args = list(arguments)
    command = args.pop(0) if args else 'run'
    if command not in ('run', 'requests', 'recover', 'boot-recover', 'self-check'):
        raise UpdateError('configuration')
    if not args:
        return command, None, None, None
    selection_fd = None
    if len(args) == 6:
        if (command not in ('run', 'requests') or args[4] != '--release-selection-fd' or
                not re.fullmatch(r'[0-9]{1,9}', args[5])):
            raise UpdateError('configuration')
        selection_fd = int(args[5])
        args = args[:4]
    if (len(args) != 4 or args[0] != '--supervisor-lock-fd' or not re.fullmatch(r'[0-9]{1,9}', args[1]) or
            args[2] != '--runtime-digest' or not re.fullmatch(r'[0-9a-f]{64}', args[3])):
        raise UpdateError('configuration')
    if selection_fd == int(args[1]): raise UpdateError('configuration')
    return command, int(args[1]), args[3], selection_fd


def read_release_selection(fd, config):
    """Validate the supervisor's sealed, bounded fixed-repository input.

    This capability is never exposed through the app API. No arbitrary paths,
    source URLs, shell commands or trust choices are accepted in the descriptor.
    """
    if config.get('repository') != PUBLIC_REPOSITORY or config.get('releaseTrust') != 'github-https':
        raise UpdateError('configuration')
    info = os.fstat(fd)
    seals = SELECTION_SEALS
    if (not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or not 1 <= info.st_size <= 8192 or
            fcntl.fcntl(fd, F_GET_SEALS) & seals != seals):
        raise UpdateError('configuration')
    raw = os.pread(fd, 8193, 0)
    def pairs(items):
        value = {}
        for key, item in items:
            if key in value: raise UpdateError('configuration')
            value[key] = item
        return value
    try: value = json.loads(raw, object_pairs_hook=pairs)
    except (ValueError, UnicodeError): raise UpdateError('configuration') from None
    if isinstance(value, dict) and set(value) == {'format', 'repository', 'runtimeSha256', 'error'}:
        if (len(raw) != info.st_size or type(value['format']) is not int or value['format'] != 1 or
                value['repository'] != PUBLIC_REPOSITORY or value['error'] not in
                ('target_changed', 'network', 'archive', 'configuration', 'incompatible') or
                not isinstance(value['runtimeSha256'], str) or not re.fullmatch('[0-9a-f]{64}', value['runtimeSha256'])):
            raise UpdateError('configuration')
        return value
    if (len(raw) != info.st_size or not isinstance(value, dict) or not {'format', 'repository', 'release'}.issubset(value) or
            set(value) - {'format', 'repository', 'release', 'appManifestSha256', 'runtimeSha256'} or
            type(value['format']) is not int or value['format'] != 1 or value['repository'] != PUBLIC_REPOSITORY):
        raise UpdateError('configuration')
    for key in ('appManifestSha256', 'runtimeSha256'):
        if key in value and (not isinstance(value[key], str) or not re.fullmatch('[0-9a-f]{64}', value[key])):
            raise UpdateError('configuration')
    release = value['release']
    if (not isinstance(release, dict) or set(release) != {'id', 'tag_name', 'draft', 'prerelease', 'assets'} or
            type(release['id']) is not int or not 1 <= release['id'] <= 2**63 - 1 or
            release['draft'] is not False or release['prerelease'] is not False or
            not isinstance(release['assets'], list) or not 2 <= len(release['assets']) <= 6):
        raise UpdateError('configuration')
    version(release['tag_name'])
    names = set()
    for asset in release['assets']:
        if (not isinstance(asset, dict) or set(asset) != {'name', 'url'} or asset['name'] not in
                (*ASSETS, 'yachad-updater.json', 'yachad-updater.sig', 'yachad-updater.zip') or
                asset['name'] in names or not isinstance(asset['url'], str) or
                not re.fullmatch(r'https://api\.github\.com/repos/NehoraiHadad/yahad/releases/assets/[0-9]+', asset['url'])):
            raise UpdateError('configuration')
        names.add(asset['name'])
    if not {ASSETS[0], ASSETS[2]}.issubset(names) or release_target(release, PUBLIC_REPOSITORY) is None:
        raise UpdateError('configuration')
    return value


def requires_release_selection(inherited_fd):
    if inherited_fd is None: return False
    path = pathlib.Path('/etc/yachad/updater-source.json')
    try: policy = json.loads(private_file(path))
    except FileNotFoundError: return False  # Historical signed enrollment.
    return policy.get('format') == 2 and policy.get('trust') == 'github-https' and policy.get('enabled') is True


def supervisor_lock(fd, state, digest, command):
    # Root supervisor retains the existing worker flock across exec. Verify the
    # descriptor identifies that root-owned lock, never an arbitrary inherited fd.
    path = state / 'worker.lock'
    info, expected = os.fstat(fd), path.lstat()
    if (not stat.S_ISREG(info.st_mode) or not stat.S_ISREG(expected.st_mode) or
            info.st_uid != 0 or expected.st_uid != 0 or info.st_mode & 0o077 or
            info.st_nlink != 1 or (info.st_dev, info.st_ino) != (expected.st_dev, expected.st_ino)):
        raise UpdateError('configuration')
    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    runtime = json.loads(private_file(state / 'updater-runtime.json'))
    records = ('active', 'candidate') if command == 'self-check' else ('active',)
    if not any(isinstance(runtime.get(name), dict) and runtime[name].get('sha256') == digest for name in records):
        raise UpdateError('configuration')
    if command != 'self-check' and runtime.get('phase') != 'stable':
        raise UpdateError('configuration')


def self_check(worker):
    # Explicit, read-only capability probe. Never use run(), save(), request(),
    # package imports, service commands or release/network checks here.
    schema = worker.schema()
    if worker.app_schema(worker.current()) != schema:
        raise UpdateError('incompatible')
    return dict(supervisorProtocol=1, workerProtocol=UPDATER_PROTOCOL, helperProtocol=1,
                configFormat=1, stateFormat=1, appManifestFormats=[1, 2], databaseSchema=schema)


def main():
    command, inherited_fd, runtime_digest, selection_fd = parse_command(sys.argv[1:])
    if os.geteuid() != 0:
        raise UpdateError('configuration')
    # Installing the boot guard before configuring updates must leave a normal
    # non-updating installation usable. Missing config is permitted only at boot.
    boot = command == 'boot-recover'
    request_only = command == 'requests'
    if boot and not CONFIG.exists():
        return
    config = load_config()
    state = pathlib.Path(config['stateDirectory'])
    st = state.lstat()
    if not stat.S_ISDIR(st.st_mode) or st.st_uid != 0 or st.st_mode & 0o022:
        raise UpdateError('configuration')
    if command == 'self-check' and inherited_fd is None:
        print(json.dumps(self_check(Worker(config)), separators=(',', ':')))
        return
    if inherited_fd is not None:
        supervisor_lock(inherited_fd, state, runtime_digest, command)
    selection = read_release_selection(selection_fd, config) if selection_fd is not None else None
    if selection is not None and selection.get('runtimeSha256') != runtime_digest:
        raise UpdateError('configuration')
    require_selection = requires_release_selection(inherited_fd)
    with (os.fdopen(inherited_fd, 'a') if inherited_fd is not None else (state / 'worker.lock').open('a')) as lock:
        try:
            # Keep an event wake-up queued behind an already-running worker.
            # The service timeout bounds waiting; timer remains a fallback.
            fcntl.flock(lock, fcntl.LOCK_EX | (0 if request_only else fcntl.LOCK_NB))
        except BlockingIOError:
            if boot:
                raise UpdateError('configuration') from None
            return
        if request_only:
            run_requests(state, runtime_digest, selection, require_selection)
            return
        config = load_config()
        if pathlib.Path(config['stateDirectory']) != state:
            raise UpdateError('configuration')
        worker = Worker(config)
        worker.runtime_digest = runtime_digest
        worker.release_selection = selection
        worker.require_release_selection = require_selection
        if command == 'self-check':
            print(json.dumps(self_check(worker), separators=(',', ':')))
        elif command in ('recover', 'boot-recover'):
            worker.finish_request('failed', 'internal')
            if not worker.recover(boot=boot):
                raise UpdateError('rollback')
        else:
            worker.run()


if __name__ == '__main__':
    try:
        main()
    except Exception:
        # Never log exception bodies, subprocess output, remote response bodies or credentials.
        print('YACHAD_UPDATE_CONFIGURATION_OR_RUNTIME_FAILURE', file=sys.stderr)
        sys.exit(1)
