#!/usr/bin/env python3
"""Root-owned, stdlib-only release worker. Never imports application code as root.

All package code and npm run as an isolated builder account. Repository, signing
key, operator and paths are root configuration, never supplied by a web request.
"""
import fcntl
import hashlib
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

CONFIG = pathlib.Path('/etc/yachad/updates.json')
ASSETS = ('yachad-release.json', 'yachad-release.sig', 'yachad-app.zip')
PHASES = {'stopping', 'backing_up', 'activating', 'checking_health'}
HOSTS = {'api.github.com', 'github.com', 'objects.githubusercontent.com', 'release-assets.githubusercontent.com'}


class UpdateError(Exception):
    def __init__(self, code):
        self.code = code


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
    fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
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
    try:
        manifest = json.loads(manifest_bytes)
        version(manifest['version'])
        if manifest.get('format') != 1 or manifest.get('databaseSchema') != 1 or manifest.get('archive') != ASSETS[2]:
            raise UpdateError('incompatible')
        if not isinstance(manifest.get('bytes'), int) or not 1 <= manifest['bytes'] <= 64 * 1024 * 1024:
            raise UpdateError('archive')
        if len(archive) != manifest['bytes'] or hashlib.sha256(archive).hexdigest() != manifest.get('sha256'):
            raise UpdateError('archive')
        return manifest
    except (KeyError, ValueError, TypeError):
        raise UpdateError('archive') from None


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
                if (name.is_absolute() or '..' in name.parts or '\\' in info.filename or
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
        except (OSError, ValueError):
            return {}

    def save(self, **changes):
        self.status.update(changes)
        self.status.update(enabled=self.c['enabled'], mode=self.c['mode'], operator=self.c['operator'], updatedAt=int(time.time() * 1000))
        for field in ['currentVersion', 'latestVersion', 'checkedAt', 'error']:
            self.status.setdefault(field, None)
        self.status.setdefault('phase', 'idle')
        atomic_json(self.status_path, self.status, self.group)

    def command(self, args, *, user=None, cwd=None, input=None, timeout=300, code='internal'):
        if user:
            args = ['/usr/sbin/runuser', '-u', user, '--'] + args
        env = {'PATH': '/usr/local/bin:/usr/bin:/bin', 'HOME': self.c['buildDirectory'],
               'NODE_ENV': 'production', 'npm_config_cache': str(pathlib.Path(self.c['buildDirectory']) / 'npm-cache')}
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
                return db.execute('SELECT version FROM schema_version').fetchone()[0]
        except (sqlite3.Error, TypeError):
            raise UpdateError('incompatible') from None

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

    def health(self, expected=None):
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
                if body.get('ok') is True and body.get('databaseSchema') == 1 and (expected is None or body.get('version') == expected):
                    return
            except (OSError, ValueError, UpdateError):
                pass
            time.sleep(2)
        raise UpdateError('health')

    def recover(self):
        if self.status.get('phase') not in PHASES:
            return
        try:
            if self.schema() != 1:
                raise UpdateError('incompatible')
            self.command(['/usr/bin/systemctl', 'stop', self.c['service']])
            self.switch(self.status['previousPath'])
            self.command(['/usr/bin/systemctl', 'start', self.c['service']])
            self.health(self.status.get('currentVersion'))
            self.save(phase='rolled_back', error=self.status.get('error') or 'health')
        except Exception:
            self.save(phase='failed', error='rollback')

    def request(self):
        path = pathlib.Path(self.c['dataDirectory']) / 'update-request.json'
        try:
            st = path.lstat()
            if not stat.S_ISREG(st.st_mode) or st.st_size > 512:
                path.unlink(missing_ok=True)
                return None
            data = json.loads(path.read_bytes())
            path.unlink(missing_ok=True)
            if (data.get('action') not in ('check', 'install') or
                    data.get('familyId') != self.c['operator']['familyId'] or data.get('userId') != self.c['operator']['userId'] or
                    not isinstance(data.get('createdAt'), (int, float)) or abs(time.time() * 1000 - data['createdAt']) > 600000):
                return None
            return data['action']
        except (OSError, ValueError, TypeError):
            return None

    def release(self, token):
        repo = self.c['repository']
        if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo):
            raise UpdateError('configuration')
        raw = download(f'https://api.github.com/repos/{repo}/releases/latest', 1024 * 1024, token, 'application/vnd.github+json')
        try:
            release = json.loads(raw)
            if release.get('draft') or release.get('prerelease'):
                raise UpdateError('incompatible')
            version(release['tag_name'])
            assets = {x['name']: x for x in release['assets']}
            if not all(name in assets for name in ASSETS):
                raise UpdateError('archive')
            return release, assets
        except (KeyError, ValueError, TypeError):
            raise UpdateError('network') from None

    def install(self, release, assets, token, current):
        work = pathlib.Path(tempfile.mkdtemp(prefix='release-', dir=self.state_dir))
        build = pathlib.Path(self.c['buildDirectory']) / ('build-' + str(int(time.time())))
        destination = None
        activated = False
        try:
            content = [download(assets[name]['url'], limit, token) for name, limit in zip(ASSETS, [8192, 64, 64 * 1024 * 1024])]
            key = pathlib.Path(self.c['publicKeyFile'])
            if not key.is_file() or key.is_symlink() or key.stat().st_uid != 0 or key.stat().st_mode & 0o022:
                raise UpdateError('configuration')
            manifest = verify_release(*content, key, work)
            if version(manifest['version']) != version(release['tag_name']):
                raise UpdateError('signature')
            if self.schema() != manifest['databaseSchema']:
                raise UpdateError('incompatible')
            free = shutil.disk_usage(self.c['releasesDirectory']).free
            data_bytes = sum(p.stat().st_size for p in pathlib.Path(self.c['dataDirectory']).rglob('*') if p.is_file() and not p.is_symlink())
            if free < self.c.get('minimumFreeMiB', 512) * 1048576 + data_bytes:
                raise UpdateError('storage')
            archive = work / ASSETS[2]
            archive.write_bytes(content[2])
            build.mkdir(mode=0o700)
            extract(archive, build)
            package = json.loads((build / 'package.json').read_bytes())
            if package.get('version') != manifest['version']:
                raise UpdateError('archive')
            user = pwd.getpwnam(self.c['builderUser'])
            for p in [build, *build.rglob('*')]:
                os.chown(p, user.pw_uid, user.pw_gid)
                os.chmod(p, 0o700 if p.is_dir() else 0o600)
            self.command([self.c['npm'], 'ci', '--omit=dev', '--ignore-scripts', '--no-audit', '--no-fund'],
                         user=self.c['builderUser'], cwd=build, timeout=1200, code='build')
            self.command([self.c['node'], '--import', 'tsx', 'scripts/update-smoke.ts'], user=self.c['builderUser'], cwd=build, timeout=90, code='build')
            destination = pathlib.Path(self.c['releasesDirectory']) / ('v' + manifest['version'])
            if destination.exists():
                raise UpdateError('configuration')
            # Copy rather than move: a builder must not retain an FD to the root-owned release tree.
            shutil.copytree(build, destination, symlinks=True)
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
            self.save(phase='stopping', previousPath=str(current), error=None)
            self.command(['/usr/bin/systemctl', 'stop', self.c['service']])
            self.save(phase='backing_up')
            self.command([self.c['node'], '--import', 'tsx', 'scripts/update-backup.ts', self.c['dataDirectory'],
                          self.c['masterKeyFile'], self.c['backupDirectory']], user=self.c['appUser'],
                         cwd=current, input=passphrase, timeout=900, code='backup')
            self.save(phase='activating')
            self.switch(destination)
            self.command(['/usr/bin/systemctl', 'start', self.c['service']])
            self.save(phase='checking_health')
            self.health(manifest['version'])
            self.save(phase='healthy', currentVersion=manifest['version'], error=None)
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

    def run(self):
        self.recover()
        if self.status.get('phase') == 'preparing':
            # A previous worker stopped before touching the active service.
            self.save(phase='failed', error='build', failedVersion=self.status.get('latestVersion'))
        if not self.c['enabled']:
            self.save(phase='idle', error=None)
            return
        action = self.request()
        if not action and self.status.get('checkedAt', 0) and time.time() * 1000 - self.status['checkedAt'] < self.c.get('checkHours', 6) * 3600000:
            self.save()
            return
        try:
            current = self.current()
            current_version = json.loads((current / 'package.json').read_bytes())['version']
            version(current_version)
            token = private_file(self.c['tokenFile']).decode().strip() if self.c.get('tokenFile') else None
            release, assets = self.release(token)
            newest = release['tag_name'].lstrip('v')
            self.save(currentVersion=current_version, latestVersion=newest, checkedAt=int(time.time() * 1000), error=None,
                      phase='available' if version(newest) > version(current_version) else 'idle')
            if version(newest) > version(current_version) and (action == 'install' or self.c['mode'] == 'auto'):
                # A failed version is not retried automatically forever; explicit install retries it.
                if self.status.get('failedVersion') == newest and action != 'install':
                    self.save(phase='failed', error='health')
                    return
                self.save(phase='preparing')
                self.install(release, assets, token, current)
        except UpdateError as e:
            self.save(error=e.code, failedVersion=self.status.get('latestVersion'))
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


def main():
    if os.geteuid() != 0:
        raise UpdateError('configuration')
    config = json.loads(private_file(CONFIG))
    if config.get('mode') not in ('notify', 'auto') or type(config.get('enabled')) is not bool:
        raise UpdateError('configuration')
    state = pathlib.Path(config['stateDirectory'])
    st = state.lstat()
    if not stat.S_ISDIR(st.st_mode) or st.st_uid != 0 or st.st_mode & 0o022:
        raise UpdateError('configuration')
    with (state / 'worker.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return
        worker = Worker(config)
        if sys.argv[1:] == ['recover']:
            worker.recover()
        else:
            worker.run()


if __name__ == '__main__':
    try:
        main()
    except Exception:
        # Never log exception bodies, subprocess output, remote response bodies or credentials.
        print('YACHAD_UPDATE_CONFIGURATION_OR_RUNTIME_FAILURE', file=sys.stderr)
        sys.exit(1)
