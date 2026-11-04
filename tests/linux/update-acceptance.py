#!/usr/bin/env python3
"""Real Linux upgrade acceptance, for a disposable root runner with systemd.

`package` builds the candidate from this checkout with the real release command,
plus deliberately broken variants. `run` then installs the published v0.5.3 through
its own deploy/install.py, creates a family through the real API over the managed
HTTPS origin, and drives upgrades with the parent update button: the POST writes the
request file, the systemd path unit wakes deploy/updater.py, and the updater's own
verify, build, backup, activation, health and rollback logic does the rest. The
`fresh` scenario instead installs the candidate through its own deploy/install.py,
as a new machine does: there is no older release and no separate enrollment.

The updater has fixed hosts and verifies TLS, and deploy/ is not edited. So the host
gets a throwaway CA and an /etc/hosts entry, and fake-github.py serves "latest". The
v0.5.3 assets are the real published bytes, pinned by SHA-256, served there too, so a
later release cannot change the baseline. github.com and npm stay the real services.

Never run this on a machine you care about: it edits /etc/hosts and the CA store and
installs the app. It refuses to start without --disposable or over an existing install.
"""
import argparse
import ast
import contextlib
import hashlib
import json
import os
import pathlib
import platform
import re
import secrets
import select
import shutil
import signal
import sqlite3
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
FAKE_GITHUB = pathlib.Path(__file__).resolve().with_name('fake-github.py')

# The published baseline. Read-only: downloaded by these exact bytes, never changed.
BASE_VERSION = '0.5.3'
BASE_SCHEMA = 5
BASE_RELEASE_ID = 427600759
BASE_ASSETS = {
    'yachad-app.zip': (687835743, '3e6daba7210d2528f352fb416072d774b362804ac2fd0986e236bd1cb4a986f1'),
    'yachad-release.json': (687835744, '82620aa7bcd3ac247c433b9ee16199af33e5d77af459fbaba3569551becb026d'),
}
RELEASE_DOWNLOAD = 'https://github.com/NehoraiHadad/yahad/releases/download/'
BASE = dict(version=BASE_VERSION, schema=BASE_SCHEMA, release_id=BASE_RELEASE_ID, assets=BASE_ASSETS, directory='base-assets')
# The Pi runs app 0.5.3 under the root updater of 0.5.0, because an app release never replaces that
# file: it has no supervisor, is in auto mode and writes no installTarget. Read from the Pi on 2026-11-04.
PI_INSTALLED = dict(version='0.5.0', schema=2, release_id=426336825, directory='pi-assets', assets={
    'yachad-app.zip': (684096682, '30f3ed5e9c6e75e5ed5aa4225f277c9431463f27bcd3fcb8e3cf8c762cca4a5c'),
    'yachad-release.json': (684096681, '49afc04f2c0f1075935a10139adff4731eaf0d0dc0042413c1cd6a021a9b7d7d'),
})
PI_UPDATER_SHA256 = '22cd73dc810d08d90e1ec6af4e4b482bc60a0f42b44e1bf874c78682375264cd'
ASSET_URL = 'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/'
LATEST_URL = 'https://api.github.com/repos/NehoraiHadad/yahad/releases/latest'
FAKE_HOSTS = ('api.github.com', 'release-assets.githubusercontent.com')

# A private address on loopback. deploy/install.py requires a private non-loopback
# IPv4 origin, and Caddy binds exactly this address.
LAN_IP = '192.168.77.10'
ORIGIN = 'https://' + LAN_IP

CONFIG = pathlib.Path('/etc/yachad/updates.json')
PASSPHRASE = pathlib.Path('/etc/yachad/update-backup-passphrase')
STATUS = pathlib.Path('/var/lib/yachad-updates/status.json')
DATA = pathlib.Path('/var/lib/yachad/store')
DATABASE = DATA / 'app.sqlite'
CURRENT = pathlib.Path('/opt/yachad/current')
RELEASES = pathlib.Path('/opt/yachad/releases')
BACKUPS = pathlib.Path('/var/backups/yachad-updates')
WORKER = pathlib.Path('/usr/local/lib/yachad-updater/updater.py')
SOURCE_POLICY = pathlib.Path('/etc/yachad/updater-source.json')
RUNTIME_JOURNAL = pathlib.Path('/var/lib/yachad-updates/updater-runtime.json')
RUNTIME_POINTER = pathlib.Path('/var/lib/yachad-updates/runtime/current')
TRANSPORT_STATE = pathlib.Path('/var/lib/yachad-updates/updater-transport.json')
CADDY_ROOT = pathlib.Path('/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt')
HEALTH = 'http://127.0.0.1:3000/health/ready'
HOSTS_FILE = pathlib.Path('/etc/hosts')
TRUST_FILE = pathlib.Path('/usr/local/share/ca-certificates/yahad-acceptance.crt')
ENV = {'PATH': '/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin', 'LANG': 'C.UTF-8', 'HOME': '/root'}
UNITS = ('yachad.service', 'yachad-web.service', 'yachad-update.service', 'yachad-update.timer',
         'yachad-update-request.path', 'yachad-update-request.service', 'yachad-update-recover.service')

GROUP = '120363001@g.us'  # The mock connector's first group.
# Tables whose rows must survive. Sessions, throttles and usage leases legitimately change.
KEPT_TABLES = ('families', 'users', 'groups', 'grants', 'group_keys', 'messages', 'media', 'auth', 'identity_labels')
# Compared byte for byte: credentials, permissions and the keys that decrypt history.
EXACT_QUERIES = {
    'users': 'SELECT id,family_id,name,name_key,role,password FROM users ORDER BY family_id,id',
    'grants': 'SELECT family_id,user_id,group_id FROM grants ORDER BY family_id,user_id,group_id',
    'group_keys': 'SELECT family_id,group_id,key FROM group_keys ORDER BY family_id,group_id',
}

CRASH_AT_START = ('src/main.ts', 'silencePayloadConsoles();\n',
                  "silencePayloadConsoles();\nprocess.stderr.write('Acceptance: deliberately broken candidate\\n');\nprocess.exit(12);\n")
# Only the real service fails: scripts/update-smoke.ts must still pass, as the updater
# runs it before any downtime. The app has already opened (and migrated) the database.
HEALTHY = ('src/main.ts', 'silencePayloadConsoles();\n', 'silencePayloadConsoles();\n// Acceptance: a healthy higher version.\n')
READY_503 = ('src/server.ts', "app.get('/health/ready', async (_req, reply) => {\n",
             "app.get('/health/ready', async (_req, reply) => {\n    if (process.argv[1]?.endsWith('/src/main.ts')) return reply.code(503).send({ ok: false });\n")


class AcceptanceFailure(Exception):
    pass


class Recorder:
    """Everything summary.json reports: timings, each assertion, the phases seen."""

    def __init__(self):
        self.started = time.monotonic()
        self.steps, self.assertions, self.phases = [], [], []
        self.current = None

    @contextlib.contextmanager
    def step(self, name):
        print(f'::group::{name}', flush=True)
        begun, previous, self.current = time.monotonic(), self.current, name
        entry = dict(name=name, seconds=None, ok=False)
        self.steps.append(entry)
        try:
            yield
            entry['ok'] = True
        finally:
            entry['seconds'] = round(time.monotonic() - begun, 1)
            self.current = previous
            print(f"{'ok' if entry['ok'] else 'FAILED'}: {name} ({entry['seconds']}s)", flush=True)
            print('::endgroup::', flush=True)

    def check(self, condition, name, detail=None):
        self.assertions.append(dict(step=self.current, name=name, ok=bool(condition)))
        if not condition:
            raise AcceptanceFailure(name + ('' if detail is None else ': ' + json.dumps(detail, ensure_ascii=False, default=str)))
        print(f'  pass: {name}', flush=True)

    def phase(self, value, attempt):
        self.phases.append(dict(at=round(time.monotonic() - self.started, 1), attempt=attempt, phase=value))
        print(f'  phase[{attempt}]: {value}', flush=True)


def wait_until(probe, timeout, what, interval=1.0):
    deadline = time.monotonic() + timeout
    while True:
        value = probe()
        if value:
            return value
        if time.monotonic() > deadline:
            raise AcceptanceFailure('Timed out after ' + str(timeout) + 's waiting for ' + what)
        time.sleep(interval)


def run(args, *, check=True, input=None, timeout=300, cwd=None, env=None):
    result = subprocess.run(args, input=input, cwd=cwd, env=env or ENV, timeout=timeout,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if check and result.returncode:
        raise AcceptanceFailure(f'Command failed ({result.returncode}): {args[0]} {args[1:4]}\n'
                                + result.stdout.decode(errors='replace')[-2000:] + result.stderr.decode(errors='replace')[-2000:])
    return result


def sha256(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


# Candidates ---------------------------------------------------------------------

def version_tuple(value):
    return tuple(int(part) for part in value.split('.'))


def package_variant(work, output, version, patch):
    """A real candidate that only the service fails on, through the real release command."""
    source = work / ('source-' + version)
    shutil.copytree(ROOT, source, ignore=shutil.ignore_patterns('node_modules', '.git', '.local', 'installer', '__pycache__'))
    name, old, new = patch
    path = source / name
    text = path.read_text(encoding='utf8')
    if text.count(old) != 1:
        raise AcceptanceFailure(f'The broken-candidate patch no longer matches {name}; update it')
    path.write_text(text.replace(old, new), encoding='utf8')
    for manifest in ('package.json', 'package-lock.json'):
        value = json.loads((source / manifest).read_text(encoding='utf8'))
        value['version'] = version
        if manifest == 'package-lock.json':
            value['packages']['']['version'] = version
        (source / manifest).write_text(json.dumps(value, indent=2) + '\n', encoding='utf8')
    run(['node', 'scripts/release.mjs', 'package-https', str(output)], cwd=source, env=dict(os.environ))
    shutil.rmtree(source)


def package_runtime(output, app_version, env):
    """The root updater pair of one release, at the version release:publish gives it, by the real packager."""
    rule = "import {runtimeVersionFor} from './scripts/release-windows.mjs';console.log(runtimeVersionFor(process.argv[1]))"
    version = run(['node', '--input-type=module', '-e', rule, app_version], cwd=ROOT, env=env).stdout.decode().strip()
    run(['node', 'scripts/release-updater.mjs', 'package-github', version, str(output)], cwd=ROOT, env=env)
    return version


def package(arguments):
    output = pathlib.Path(arguments.output).resolve()
    if output.exists():
        raise AcceptanceFailure('Choose a new candidates directory')
    output.mkdir(parents=True)
    work = output / 'work'
    work.mkdir()
    env = dict(os.environ, PATH='/usr/local/bin:' + os.environ['PATH'])
    os.environ['PATH'] = env['PATH']
    recorder = Recorder()
    with recorder.step('Install locked npm dependencies'):
        run(['npm', 'ci', '--no-audit', '--no-fund'], cwd=ROOT, env=env, timeout=900)
    with recorder.step('Build the web client'):
        run(['npm', 'run', 'build'], cwd=ROOT, env=env, timeout=600)
    with recorder.step('Package the candidate with scripts/release.mjs package-https'):
        run(['node', 'scripts/release.mjs', 'package-https', str(output / 'candidate')], cwd=ROOT, env=env)
    manifest = json.loads((output / 'candidate/yachad-release.json').read_bytes())
    version = manifest['version']
    major, minor, patch = version_tuple(version)
    recorder.check(version_tuple(version) > version_tuple('0.5.91'), 'candidate version is above the broken 0.5.x fixtures', version)
    recorder.check(BASE_SCHEMA in manifest['databaseMigrationsFrom'], 'candidate declares a migration from the baseline schema', manifest)
    broken_same_schema = f'{major}.{minor}.{patch + 1}'
    healthy_same_schema = f'{major}.{minor}.{patch + 2}'
    with recorder.step('Package the deliberately broken candidates'):
        package_variant(work, output / 'broken-crash', '0.5.90', CRASH_AT_START)
        package_variant(work, output / 'broken-migrated', '0.5.91', READY_503)
        package_variant(work, output / 'broken-same-schema', broken_same_schema, READY_503)
    with recorder.step('Package a healthy higher candidate of the same schema'):
        package_variant(work, output / 'healthy-same-schema', healthy_same_schema, HEALTHY)
    work.rmdir()
    with recorder.step('Package the root updater pair of the candidates with scripts/release-updater.mjs package-github'):
        runtime = package_runtime(output / 'updater-candidate', version, env)
        broken_runtime = package_runtime(output / 'updater-broken-same-schema', broken_same_schema, env)
        healthy_runtime = package_runtime(output / 'updater-healthy-same-schema', healthy_same_schema, env)
        recorder.check(version_tuple(runtime) > version_tuple(bootstrap_literal('INITIAL_RUNTIME_VERSION')), 'the runtime is above the initial one of the bootstrap', runtime)
    (output / 'build.json').write_text(json.dumps(dict(
        commit=arguments.commit, version=version, databaseSchema=manifest['databaseSchema'],
        databaseMigrationsFrom=manifest['databaseMigrationsFrom'], manifestSha256=sha256(output / 'candidate/yachad-release.json'),
        archiveBytes=manifest['bytes'], brokenSameSchemaVersion=broken_same_schema, runtimeVersion=runtime,
        brokenSameSchemaRuntimeVersion=broken_runtime, healthySameSchemaVersion=healthy_same_schema,
        healthySameSchemaRuntimeVersion=healthy_runtime, steps=recorder.steps), indent=2))
    print(f'Candidate {version} and broken variants packaged in {output}', flush=True)


# The stand-in for GitHub --------------------------------------------------------

class GitHub:
    """Trust, name resolution and the server. Everything is undone by close()."""

    def __init__(self, directory):
        self.directory = directory
        self.state = directory / 'state.json'
        self.log = directory / 'requests.jsonl'
        self.hosts = None
        self.process = None
        self.next_id = 900000000

    def issue_certificate(self):
        d = self.directory
        (d / 'ca.cnf').write_text('[req]\ndistinguished_name=dn\nx509_extensions=ca\nprompt=no\n[dn]\nCN=Yahad acceptance throwaway CA\n'
                                  '[ca]\nbasicConstraints=critical,CA:TRUE\nkeyUsage=critical,keyCertSign,cRLSign\nsubjectKeyIdentifier=hash\n')
        (d / 'leaf.cnf').write_text('[leaf]\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\n'
                                    'extendedKeyUsage=serverAuth\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid\n'
                                    'subjectAltName=' + ','.join('DNS:' + host for host in FAKE_HOSTS) + '\n')
        run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2', '-config', str(d / 'ca.cnf'),
             '-keyout', str(d / 'ca.key'), '-out', str(d / 'ca.crt')])
        run(['openssl', 'req', '-new', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=' + FAKE_HOSTS[0],
             '-keyout', str(d / 'leaf.key'), '-out', str(d / 'leaf.csr')])
        run(['openssl', 'x509', '-req', '-days', '2', '-in', str(d / 'leaf.csr'), '-CA', str(d / 'ca.crt'), '-CAkey', str(d / 'ca.key'),
             '-CAcreateserial', '-extfile', str(d / 'leaf.cnf'), '-extensions', 'leaf', '-out', str(d / 'leaf.crt')])
        (d / 'ca.key').unlink()  # It only had to sign this one leaf.

    def publish(self, directory, tag, release_id=None, asset_ids=None, updater=None):
        assets, files = [], {}
        sources = [(pathlib.Path(directory), name) for name in ('yachad-app.zip', 'yachad-release.json')]
        if updater:
            sources += [(pathlib.Path(updater), name) for name in ('yachad-updater.json', 'yachad-updater.zip')]
        for directory, name in sources:
            if asset_ids:
                asset_id = asset_ids[name]
            else:
                self.next_id += 1
                asset_id = self.next_id
            path = directory / name
            assets.append(dict(id=asset_id, name=name, url=ASSET_URL + str(asset_id), size=path.stat().st_size,
                               state='uploaded', content_type='application/octet-stream'))
            files[str(asset_id)] = str(path)
        if release_id is None:
            self.next_id += 1
            release_id = self.next_id
        value = dict(id=release_id, tag_name=tag, draft=False, prerelease=False, assets=assets)
        temporary = self.state.with_name('state.tmp')
        temporary.write_text(json.dumps(dict(release=value, files=files)))
        temporary.replace(self.state)

    def start(self):
        self.issue_certificate()
        shutil.copyfile(self.directory / 'ca.crt', TRUST_FILE)
        run(['update-ca-certificates'])
        self.hosts = HOSTS_FILE.read_bytes()
        HOSTS_FILE.write_bytes(self.hosts + ('\n127.0.0.1 ' + ' '.join(FAKE_HOSTS) + '\n').encode())
        self.process = subprocess.Popen(['/usr/bin/python3', '-I', str(FAKE_GITHUB), '--state', str(self.state), '--log', str(self.log),
                                         '--cert', str(self.directory / 'leaf.crt'), '--key', str(self.directory / 'leaf.key')],
                                        env=ENV, stdout=subprocess.DEVNULL, stderr=(self.directory / 'server.log').open('w'))

    def verify_reachable(self, recorder, expected_tag):
        """Through the same default TLS verification the updater uses, with nothing bypassed."""
        context = ssl.create_default_context()

        def probe():
            try:
                request = urllib.request.Request(LATEST_URL, headers={'Accept': 'application/vnd.github+json'})
                with urllib.request.urlopen(request, timeout=5, context=context) as response:
                    return json.loads(response.read())
            except (OSError, ValueError):
                return None
        value = wait_until(probe, 30, 'the stand-in GitHub to answer')
        recorder.check(value.get('tag_name') == expected_tag, 'the host resolves and trusts the stand-in, with TLS verification on', value)

    def requests(self):
        if not self.log.exists():
            return []
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def close(self):
        if self.process:
            self.process.terminate()
            with contextlib.suppress(subprocess.TimeoutExpired):
                self.process.wait(10)
        if self.hosts is not None:
            HOSTS_FILE.write_bytes(self.hosts)
        if TRUST_FILE.exists():
            TRUST_FILE.unlink()
            run(['update-ca-certificates', '--fresh'], check=False)
        for name in ('ca.crt', 'leaf.crt', 'leaf.key', 'leaf.csr', 'ca.srl', 'ca.cnf', 'leaf.cnf'):
            (self.directory / name).unlink(missing_ok=True)


def download_release(work, release):
    """Done before the host is redirected, so these are the real published assets."""
    directory = work / release['directory']
    directory.mkdir(parents=True)
    for name, (_, digest) in release['assets'].items():
        with urllib.request.urlopen(f"{RELEASE_DOWNLOAD}v{release['version']}/{name}", timeout=120) as response:
            raw = response.read()
        if hashlib.sha256(raw).hexdigest() != digest:
            raise AcceptanceFailure(f"Published {name} of v{release['version']} differs from the pinned SHA-256")
        (directory / name).write_bytes(raw)


# The application's own HTTP surface -----------------------------------------------

class Api:
    """A browser's view: the managed HTTPS origin, Origin header, secure cookie, CSRF."""

    def __init__(self):
        context = ssl.create_default_context(cafile=str(CADDY_ROOT))
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
        self.cookie = None
        self.csrf = None
        self.user = None

    def raw(self, method, path, body=None, headers=None):
        request = urllib.request.Request(ORIGIN + path, method=method, data=None if body is None else json.dumps(body).encode())
        request.add_header('Origin', ORIGIN)
        if body is not None:
            request.add_header('Content-Type', 'application/json')
        if self.cookie:
            request.add_header('Cookie', 'sid=' + self.cookie)
        if self.csrf and method not in ('GET', 'HEAD'):
            request.add_header('x-csrf-token', self.csrf)
        for name, value in (headers or {}).items():
            request.add_header(name, value)
        try:
            with self.opener.open(request, timeout=60) as response:
                return response.status, response.read(), response.headers
        except urllib.error.HTTPError as error:
            return error.code, error.read(), error.headers

    def json(self, method, path, body=None, headers=None):
        status, raw, response_headers = self.raw(method, path, body, headers)
        for value in response_headers.get_all('Set-Cookie') or []:
            match = re.match(r'sid=([^;]*)', value)
            if match:
                self.cookie = match[1]
        try:
            return status, json.loads(raw) if raw else None
        except ValueError:
            return status, raw.decode(errors='replace')[:500]

    def login(self, name, password):
        status, value = self.json('POST', '/api/login', dict(name=name, password=password))
        if status != 200:
            return status, value
        status, me = self.json('GET', '/api/me')
        if status == 200:
            self.user, self.csrf = me['user'], me['csrf']
        return status, me

    def require(self, method, path, body=None):
        status, value = self.json(method, path, body)
        if status != 200:
            raise AcceptanceFailure(f'{method} {path} returned {status}: {value}')
        return value


def messages_of(api):
    return api.require('GET', '/api/groups/' + urllib.parse.quote(GROUP, safe='') + '/messages')['messages']


def post_message(recorder, api, text):
    api.require('POST', '/api/groups/' + urllib.parse.quote(GROUP, safe='') + '/messages', dict(clientId=str(uuid.uuid4()), text=text))
    wait_until(lambda: any(m.get('text') == text for m in messages_of(api)), 60, 'the sent message to be stored')
    recorder.check(True, 'a new message is written and read back: ' + text)


# Direct evidence ------------------------------------------------------------------

def show(unit, name):
    return run(['/usr/bin/systemctl', 'show', unit, '--property=' + name, '--value']).stdout.decode().strip()


def status():
    return json.loads(STATUS.read_bytes())


def health():
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(HEALTH, timeout=3) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, None
    except (OSError, ValueError):
        return None, None


def wait_healthy(version):
    return wait_until(lambda: (lambda h: h[1] if h[0] == 200 and h[1].get('version') == version else None)(health()),
                      90, f'/health/ready to report {version}')


def read_database(path=DATABASE):
    connection = sqlite3.connect(pathlib.Path(path).as_uri() + '?mode=ro', uri=True)
    try:
        rows = connection.execute('SELECT version FROM schema_version').fetchall()
        tables = {row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        return dict(schema=rows[0][0] if len(rows) == 1 else None,
                    counts={name: connection.execute(f'SELECT count(*) FROM {name}').fetchone()[0] for name in KEPT_TABLES if name in tables},
                    exact={name: hashlib.sha256(repr(connection.execute(query).fetchall()).encode()).hexdigest() for name, query in EXACT_QUERIES.items()},
                    messageIds=sorted(row[0] for row in connection.execute('SELECT id FROM messages')))
    finally:
        connection.close()


def assert_rows_kept(recorder, before, after):
    recorder.check(after['exact'] == before['exact'], 'accounts, passwords, grants and group keys are byte-identical',
                   dict(before=before['exact'], after=after['exact']))
    for name, count in before['counts'].items():
        recorder.check(after['counts'].get(name, -1) >= count, f'table {name} kept its {count} rows', after['counts'])
    recorder.check(set(before['messageIds']) <= set(after['messageIds']), f"all {len(before['messageIds'])} stored messages are still present")


def assert_running(recorder, version, schema):
    expected = RELEASES / ('v' + version)
    value = wait_healthy(version)
    recorder.check(pathlib.Path(os.path.realpath(CURRENT)) == expected, f'/opt/yachad/current points at v{version}', os.path.realpath(CURRENT))
    recorder.check(show('yachad.service', 'ActiveState') == 'active', 'yachad.service is active')
    pid = show('yachad.service', 'MainPID')
    recorder.check(os.readlink(f'/proc/{pid}/cwd') == str(expected), f'the service process runs from {expected}', os.readlink(f'/proc/{pid}/cwd'))
    command = pathlib.Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')
    recorder.check(b'src/main.ts' in command, 'the service process is the application entry point', command[:6])
    recorder.check(value.get('ok') is True and value.get('databaseSchema') == schema, f'/health/ready reports {version} with schema {schema}', value)
    recorder.check(read_database()['schema'] == schema, f'schema_version in SQLite is {schema}')


class Family:
    """The data created before the upgrade, and how to prove it is still there."""

    def __init__(self):
        self.parent = dict(name='acceptance-parent', password=secrets.token_urlsafe(18))
        self.child = dict(name='acceptance-child', password=secrets.token_urlsafe(18))
        self.family_name = 'משפחת בדיקה'
        self.family_id = None
        self.child_id = None
        self.messages = {}
        self.media = {}

    def signed_in_parent(self, recorder):
        api = Api()
        code, me = api.login(**self.parent)
        recorder.check(code == 200 and me['user']['role'] == 'parent', 'the same parent signs in with the same password', me)
        recorder.check(me['user']['familyId'] == self.family_id, 'the parent is in the same family', me['user'])
        return api

    def capture(self, api):
        current = messages_of(api)
        self.messages = {m['id']: m.get('text') for m in current}
        self.media = {}
        for message in current:
            if message.get('mediaId'):
                self.media[message['id']] = (message['mediaId'], self.media_digest(api, message['mediaId']))

    @staticmethod
    def media_digest(api, media_id):
        code, raw, _ = api.raw('GET', '/api/groups/' + urllib.parse.quote(GROUP, safe='') + '/media/' + urllib.parse.quote(media_id, safe=''))
        if code != 200:
            raise AcceptanceFailure(f'Media {media_id} returned {code}')
        return hashlib.sha256(raw).hexdigest()

    def verify(self, recorder):
        api = self.signed_in_parent(recorder)
        current = {m['id']: m.get('text') for m in messages_of(api)}
        missing = {key: value for key, value in self.messages.items() if current.get(key) != value}
        recorder.check(not missing, f'all {len(self.messages)} messages decrypt to the same text through the API', missing)
        for message_id, (media_id, digest) in self.media.items():
            recorder.check(self.media_digest(api, media_id) == digest, f'media of {message_id} decrypts to the same bytes')
        overview = api.require('GET', '/api/parent/overview')
        recorder.check(any(child['id'] == self.child_id for child in overview['children']), 'the child account is listed', overview['children'])
        recorder.check(any(item['user_id'] == self.child_id and item['group_id'] == GROUP for item in overview['grants']),
                       'the child still has the approved group', overview['grants'])
        child = Api()
        code, me = child.login(**self.child)
        recorder.check(code == 200 and me['user']['role'] == 'child', 'the child signs in with the same password', me)
        groups = child.require('GET', '/api/groups')
        recorder.check([group['id'] for group in groups] == [GROUP], 'the child sees exactly the approved group', groups)
        return api


def assert_update_panel(recorder, api, **expected):
    """What the parent's update panel reads, parsed by the running application's own schema.

    The real timer keeps running every few minutes and legitimately moves the phase on
    (healthy to idle, rolled_back to failed/health), so a phase may be a tuple of states.
    A timer run in progress is transient, hence the short wait.
    """
    phases = expected.pop('phase', None)
    phases = (phases,) if isinstance(phases, str) else phases

    def settled():
        current = api.require('GET', '/api/parent/updates')
        return current if phases is None or current.get('phase') in phases else None
    with contextlib.suppress(AcceptanceFailure):
        wait_until(settled, 30, 'the update panel phase')
    view = api.require('GET', '/api/parent/updates')
    recorder.check(view.get('configured') is True, 'GET /api/parent/updates parses the updater status as configured', view)
    if phases is not None:
        recorder.check(view.get('phase') in phases, f'the update panel reports phase in {phases}', view)
    for name, value in expected.items():
        recorder.check(view.get(name) == value, f'the update panel reports {name}={value}', view)
    recorder.check(view.get('enabled') is True and view.get('stale') is False, 'the update panel is enabled and not stale', view)
    return view


# Steps ----------------------------------------------------------------------------

class InstallStopped(AcceptanceFailure):
    pass


class Installer:
    """deploy/install.py insists on a terminal, so it gets one, and it is answered like an owner."""

    def __init__(self, source, log):
        self.log = log
        self.output = ''
        self.answered = False
        self.lock = threading.Lock()
        self.master, slave = os.openpty()
        self.process = subprocess.Popen(['/usr/bin/python3', 'deploy/install.py', '--origin', ORIGIN], cwd=source, env=ENV,
                                        stdin=slave, stdout=slave, stderr=slave, start_new_session=True, close_fds=True)
        os.close(slave)
        self.pump = threading.Thread(target=self.read, daemon=True)
        self.pump.start()
        self.database_at_exit = None
        self.waiter = threading.Thread(target=self.note_exit, daemon=True)
        self.waiter.start()

    def note_exit(self):
        # Observed the moment the process ends: the app creates the database about a second later.
        self.process.wait()
        self.database_at_exit = DATABASE.exists()

    def stopped_before_database(self):
        self.waiter.join()
        return self.process.returncode == 1 and self.database_at_exit is False and 'INSTALL_STOPPED' in self.tail()

    def read(self):
        with self.log.open('w', encoding='utf8') as log:
            while True:
                try:
                    raw = os.read(self.master, 65536)
                except OSError:
                    return
                if not raw:
                    return
                text = raw.decode(errors='replace')
                # The token is consumed by setup anyway, but it has no place in a public artifact.
                log.write(re.sub(r'#setup=[A-Za-z0-9_-]+', '#setup=REDACTED', text))
                log.flush()
                with self.lock:
                    self.output += text
                    if not self.answered and 'Continue? [y/N]' in self.output:
                        self.answered = True
                        os.write(self.master, b'y\n')

    def setup_token(self):
        with self.lock:
            match = re.search(r'#setup=([A-Za-z0-9_-]{43})', self.output)
        if not match and self.process.poll() is not None:
            time.sleep(1)
            raise InstallStopped(f'deploy/install.py exited {self.process.returncode} before printing a setup link:\n' + self.tail())
        return match and match[1]

    def tail(self):
        with self.lock:
            return re.sub(r'#setup=[A-Za-z0-9_-]+', '#setup=REDACTED', self.output)[-3000:]


def publish_release(github, work, release):
    github.publish(work / release['directory'], 'v' + release['version'], release['release_id'],
                   {name: value[0] for name, value in release['assets'].items()})


def unpacked(package):
    # Not under the runner's home: root-owned and reachable, as an owner's unpacked copy is.
    source = pathlib.Path(tempfile.mkdtemp(prefix='yahad-base-', dir='/var/tmp'))
    with zipfile.ZipFile(package / 'yachad-app.zip') as archive:
        archive.extractall(source)
    return source


def install_base(recorder, github, work, family, release):
    source = unpacked(work / release['directory'])
    installer = Installer(source, work / 'install.log')
    try:
        token = wait_until(installer.setup_token, 1200, 'deploy/install.py to print the setup link', 2)
    except InstallStopped:
        # deploy/install.py restarts the app and calls health(), which reads the schema from a database
        # the app has not created yet (deploy/updater.py:428). A published release cannot change, so
        # the test does what its owner would: run the same command again, which resumes from its receipt.
        if not installer.stopped_before_database():
            raise
        recorder.check(True, 'the first run stopped because health() read the database before the app created it')
        wait_healthy(release['version'])
        installer = Installer(source, work / 'install-rerun.log')
        token = wait_until(installer.setup_token, 1200, 'the rerun of deploy/install.py to print the setup link', 2)
    create_family(recorder, installer, token, family, release['version'], release['schema'])


def create_family(recorder, installer, token, family, version, schema):
    api = Api()
    code, value = api.json('GET', '/api/setup/status')
    recorder.check(code == 200 and value == {'state': 'ready'}, 'setup is open through the managed HTTPS origin', value)
    code, value = api.json('POST', '/api/setup', dict(familyName=family.family_name, name=family.parent['name'],
                                                      password=family.parent['password'], confirmation=family.parent['password']),
                           {'x-setup-token': token})
    recorder.check(code == 200, 'POST /api/setup creates the family', value)
    try:
        code = installer.process.wait(600)
    except subprocess.TimeoutExpired:
        installer.process.kill()
        raise AcceptanceFailure('deploy/install.py did not finish after setup:\n' + installer.tail()) from None
    installer.pump.join(5)
    recorder.check(code == 0 and 'COMPLETE' in installer.output, 'deploy/install.py completes after its backup and restore check', installer.tail())
    assert_running(recorder, version, schema)
    recorder.check(CONFIG.stat().st_mode & 0o077 == 0, 'the updater configuration is private')
    config = json.loads(CONFIG.read_bytes())
    recorder.check(config.get('releaseTrust') == 'github-https' and config.get('enabled') is True, 'the updater is enabled in GitHub-HTTPS trust mode', config)
    for unit in ('yachad-update-request.path', 'yachad-update.timer'):
        recorder.check(show(unit, 'ActiveState') == 'active', f'{unit} is active')


def seed_family(recorder, family, version=BASE_VERSION, schema=BASE_SCHEMA):
    api = Api()
    code, me = api.login(**family.parent)
    recorder.check(code == 200 and me['user']['role'] == 'parent', 'the parent signs in to v' + version, me)
    family.family_id = me['user']['familyId']
    api.require('POST', '/api/parent/connect')
    api.require('POST', '/api/parent/sync-groups')
    wait_until(lambda: any(group['id'] == GROUP for group in api.require('GET', '/api/parent/overview')['groups']), 60, 'the mock groups to appear')
    created = api.require('POST', '/api/parent/children', dict(name=family.child['name'], password=family.child['password'], confirmation=family.child['password']))
    family.child_id = created['id']
    api.require('PUT', '/api/parent/grant', dict(childId=family.child_id, groupId=GROUP, allowed=True))

    def settled():
        first = len(messages_of(api))
        time.sleep(3)
        return first >= 6 and first == len(messages_of(api))
    wait_until(settled, 120, 'the sample history to arrive')
    post_message(recorder, api, 'written on v' + version + ' ' + uuid.uuid4().hex)
    family.capture(api)
    recorder.check(len(family.messages) >= 7, 'the family has message history', len(family.messages))
    recorder.check(family.media, 'the family has encrypted media')
    database = read_database()
    recorder.check(database['schema'] == schema, f'the data is in schema {schema}', database['schema'])
    for name in ('families', 'users', 'groups', 'grants', 'group_keys', 'messages', 'media'):
        recorder.check(database['counts'].get(name, 0) > 0, f'table {name} has rows', database['counts'])
    return api, database


def update_action(recorder, api, body, attempt, timeout=1800):
    """Press the button: POST, and watch the root journal to the request's terminal state."""
    previous = (status().get('lastRequest') or {}).get('id')
    since = int(time.time())
    code, value = api.json('POST', '/api/parent/updates', body)
    recorder.check(code == 200 and value == {'state': 'queued'}, f"POST /api/parent/updates {body['action']} is queued", value)
    seen = None
    deadline = time.monotonic() + timeout
    while True:
        current = status()
        if current.get('phase') != seen:
            seen = current.get('phase')
            recorder.phase(seen, attempt)
        request = current.get('lastRequest') or {}
        if request.get('id') != previous and request.get('state') in ('succeeded', 'failed'):
            break
        if time.monotonic() > deadline:
            raise AcceptanceFailure(f"{body['action']} did not finish; status: {json.dumps(current)}")
        time.sleep(0.5)

    def woke():
        lines = run(['journalctl', '-u', 'yachad-update-request.service', '--since', f'@{since}', '-o', 'cat', '--no-pager']).stdout
        return lines.strip()
    wait_until(woke, 30, 'the journal to show the request service')
    recorder.check(True, 'the path unit woke yachad-update-request.service')
    return request, current


def discover(recorder, api, version, attempt):
    request, current = update_action(recorder, api, dict(action='check'), attempt)
    recorder.check(request['state'] == 'succeeded', 'the check succeeds', current)
    view = assert_update_panel(recorder, api, phase='available', latestVersion=version)
    target = view.get('installTarget') or {}
    recorder.check(target.get('version') == version and re.fullmatch('[0-9a-f]{64}', target.get('fingerprint', '')), 'the panel offers a bound install target', view)
    return target


def set_notify(recorder, api):
    """The fresh default is auto, where a check also installs. Pressing Install needs notify."""
    assert_update_panel(recorder, api, mode='auto', modeControlAllowed=True, currentVersion=BASE_VERSION)
    set_mode(recorder, api, 'notify')


def set_mode(recorder, api, mode):
    code, value = api.json('POST', '/api/parent/updates', dict(action='set_mode', mode=mode))
    recorder.check(code == 200 and value == {'state': 'queued'}, f'the parent asks for {mode} mode', value)
    wait_until(lambda: (status().get('lastModeRequest') or {}).get('state') == 'applied' and json.loads(CONFIG.read_bytes())['mode'] == mode,
               120, 'the mode change to apply')
    recorder.check(True, f'the root configuration now says {mode}')
    assert_update_panel(recorder, api, mode=mode)


def as_the_timer_does(recorder, attempt):
    """What six hours passing does on the Pi: the timer starts `updater.py run`, which finds its last check old."""
    # A run the real timer began owns the status file and would write over the aged check.
    wait_until(lambda: show('yachad-update.service', 'ActiveState') != 'activating', 1800, 'the running updater to finish')
    current = status()
    info = STATUS.stat()
    temporary = STATUS.with_name('status.acceptance')
    temporary.write_text(json.dumps(dict(current, checkedAt=1)))
    os.chown(temporary, info.st_uid, info.st_gid)
    os.chmod(temporary, info.st_mode & 0o7777)
    temporary.replace(STATUS)
    run(['/usr/bin/systemctl', 'start', 'yachad-update.service'], check=False, timeout=1800)
    wait_until(lambda: status().get('checkedAt') != 1 and show('yachad-update.service', 'ActiveState') in ('inactive', 'failed'),
               1800, 'the scheduled updater run to finish')
    current = status()
    recorder.phase(current.get('phase'), attempt)
    return current


def assert_rollback(recorder, family, request, current, *, attempt_version, back_to, schema, before):
    recorder.check(request['state'] == 'failed' and request['error'] == 'health', 'the request fails with the health error', request)
    recorder.check(current['phase'] == 'rolled_back', 'the updater reports rolled_back', current)
    recorder.check(current['currentVersion'] == back_to and current['failedVersion'] == attempt_version, 'the journal records the failed version', current)
    assert_running(recorder, back_to, schema)
    assert_rows_kept(recorder, before, read_database())
    api = family.verify(recorder)
    assert_update_panel(recorder, api, phase=('rolled_back', 'failed'), currentVersion=back_to, error='health')
    return api


def let_start_limit_window_pass():
    # yachad.service allows 12 starts in 120s, and the updater does not reset that count. A crash loop takes up to 9
    # during its 90s health wait and the rollback one more, so this script's quick restarts would use up the rest:
    # the updater's next start is then refused and it stops at recovery_required, seen in runs 41803772941 and 41804602065.
    started = int(show('yachad.service', 'ActiveEnterTimestampMonotonic')) / 1e6
    time.sleep(max(0, started + 120 - time.monotonic()))


def scenario_upgrade(recorder, github, candidates, build, family, api, database):
    version = build['version']
    schema = build['databaseSchema']
    broken_same = build['brokenSameSchemaVersion']
    worker_digest = sha256(WORKER)

    with recorder.step('Broken candidate that dies before migrating: rollback from the baseline'):
        let_start_limit_window_pass()
        github.publish(candidates / 'broken-crash', 'v0.5.90')
        target = discover(recorder, api, '0.5.90', 'crash')
        backups = len(list(BACKUPS.glob('yachad-*.backup')))
        request, current = update_action(recorder, api, dict(action='install', target=target), 'crash')
        recorder.check(len(list(BACKUPS.glob('yachad-*.backup'))) == backups + 1, 'a backup was taken before the downtime')
        recorder.check(read_database()['schema'] == BASE_SCHEMA, 'the crashed candidate left the schema alone')
        api = assert_rollback(recorder, family, request, current, attempt_version='0.5.90', back_to=BASE_VERSION, schema=BASE_SCHEMA, before=database)

    with recorder.step(f'Upgrade {BASE_VERSION} to {version} with the parent update button'):
        github.publish(candidates / 'candidate', 'v' + version)
        target = discover(recorder, api, version, 'upgrade')
        backups = len(list(BACKUPS.glob('yachad-*.backup')))
        begun = time.monotonic()
        request, current = update_action(recorder, api, dict(action='install', target=target), 'upgrade')
        recorder.upgrade_seconds = round(time.monotonic() - begun, 1)
        recorder.check(request['state'] == 'succeeded' and request['error'] is None, 'the install request succeeds', current)
        recorder.check(current['phase'] == 'healthy' and current['currentVersion'] == version, 'the updater commits the healthy version', current)
        recorder.check(len(list(BACKUPS.glob('yachad-*.backup'))) == backups + 1, 'a backup was taken before the downtime')
        served = [entry['path'] for entry in github.requests() if entry['status'] == 200]
        recorder.check(any(path.endswith('/releases/latest') for path in served), 'the updater read /releases/latest from the stand-in')

    with recorder.step('The service runs the new release, and the family is intact'):
        assert_running(recorder, version, schema)
        recorder.check(sha256(WORKER) == worker_digest, 'the root worker was not replaced by the app release')
        recorder.check((RELEASES / ('v' + BASE_VERSION)).is_dir(), 'the previous release is kept')
        recorder.check(sha256(CURRENT / '.release-manifest.json') == build['manifestSha256'], 'the installed release carries the exact packaged manifest')
        after = read_database()
        recorder.check(after['schema'] == schema, f'the database migrated to schema {schema}', after['schema'])
        assert_rows_kept(recorder, database, after)
        api = family.verify(recorder)
        recorder.check(not (RELEASES / 'v0.5.90').exists(), 'the successful install pruned the failed v0.5.90 tree')
        assert_update_panel(recorder, api, phase=('healthy', 'idle'), currentVersion=version, latestVersion=version, mode='notify',
                            modeControlAllowed=True, error=None)

    with recorder.step('The new release keeps working after a restart, and its own button works'):
        run(['/usr/bin/systemctl', 'restart', 'yachad.service'])
        assert_running(recorder, version, schema)
        api = family.verify(recorder)
        api.require('POST', '/api/parent/connect')
        post_message(recorder, api, f'written on v{version} ' + uuid.uuid4().hex)
        family.capture(api)
        request, current = update_action(recorder, api, dict(action='check'), 'recheck')
        recorder.check(request['state'] == 'succeeded' and current['phase'] == 'idle', 'a check from the new app finds nothing newer', current)
        assert_update_panel(recorder, api, phase='idle', currentVersion=version)

    with recorder.step(f'Candidate failing /health/ready at the same schema: rollback to {version}'):
        database = read_database()
        let_start_limit_window_pass()
        github.publish(candidates / 'broken-same-schema', 'v' + broken_same)
        target = discover(recorder, api, broken_same, 'same-schema')
        request, current = update_action(recorder, api, dict(action='install', target=target), 'same-schema')
        assert_rollback(recorder, family, request, current, attempt_version=broken_same, back_to=version, schema=schema, before=database)
        recorder.check((RELEASES / ('v' + BASE_VERSION)).is_dir(), 'the failed attempt pruned nothing')


def restore_backup(archive, directory):
    """As the app user with the release's own code, the way an owner would recover."""
    code = ("import {restoreBackup} from './scripts/backup-lib.ts';const chunks=[];for await(const b of process.stdin)chunks.push(b);"
            "await restoreBackup(process.argv[1],process.argv[2],Buffer.concat(chunks).toString().trim());console.log('RESTORE_OK');")
    result = run(['/usr/sbin/runuser', '-u', 'yachad', '--', '/usr/local/bin/node', '--import', 'tsx', '--input-type=module', '-e', code, str(archive), str(directory)],
                 cwd=RELEASES / ('v' + BASE_VERSION), input=PASSPHRASE.read_bytes().strip(), timeout=300)
    return result.stdout.strip() == b'RESTORE_OK'


def scenario_migrated_failure(recorder, github, candidates, build, family, api, database):
    """The updater never restores data by itself, so this one must stop rather than roll back."""
    schema = build['databaseSchema']
    with recorder.step('Candidate failing /health/ready after migrating: the updater stops the app'):
        github.publish(candidates / 'broken-migrated', 'v0.5.91')
        target = discover(recorder, api, '0.5.91', 'migrated')
        request, current = update_action(recorder, api, dict(action='install', target=target), 'migrated')
        wait_until(lambda: show('yachad-update-request.service', 'ActiveState') in ('inactive', 'failed'), 240, 'the request service to finish')
        current = status()
        recorder.check(request['state'] == 'failed', 'the install request fails', request)
        recorder.check(current['phase'] == 'recovery_required' and current['error'] == 'rollback', 'the updater reports recovery_required', current)
        recorder.check(show('yachad.service', 'ActiveState') in ('inactive', 'failed'), 'the app is stopped, not left running on the failed release')
        code, _ = Api().json('GET', '/api/setup/status')
        recorder.check(code != 200, 'the app does not answer through the HTTPS origin', code)
        recorder.check((RELEASES / ('v' + BASE_VERSION)).is_dir(), 'the previous release is kept')

    with recorder.step('The data survives in the migrated database and in the pre-update backup'):
        after = read_database()
        recorder.check(after['schema'] == schema, f'the database is in the migrated schema {schema}', after['schema'])
        assert_rows_kept(recorder, database, after)
        backup = BACKUPS / current['backupId']
        recorder.check(backup.is_file(), 'the journal names the pre-update backup', current.get('backupId'))
        parent = run(['/usr/sbin/runuser', '-u', 'yachad', '--', '/usr/bin/python3', '-c',
                      f"import tempfile;print(tempfile.mkdtemp(prefix='restore-check-',dir='{BACKUPS}'))"], cwd='/').stdout.decode().strip()
        restored = pathlib.Path(parent) / 'restored'
        try:
            recorder.check(restore_backup(backup, restored), 'the pre-update backup authenticates and restores with the local passphrase')
            copies = list(restored.rglob('app.sqlite'))
            recorder.check(len(copies) == 1, 'the backup contains the database', [str(path) for path in copies])
            old = read_database(copies[0])
            recorder.check(old['schema'] == BASE_SCHEMA, f'the backup holds schema {BASE_SCHEMA}', old['schema'])
            assert_rows_kept(recorder, database, old)
        finally:
            shutil.rmtree(parent, ignore_errors=True)


def reach_the_pi_state(recorder, github, work):
    """The Pi's own path: app 0.5.3 installed by the 0.5.0 updater, on its schedule."""
    recorder.check(sha256(WORKER) == PI_UPDATER_SHA256, 'the root updater is the exact file the Pi runs')
    recorder.check(json.loads(CONFIG.read_bytes())['mode'] == 'auto', 'the updater is in auto mode, as on the Pi')
    publish_release(github, work, BASE)
    let_start_limit_window_pass()
    # A fresh install has never checked, so the real timer may install this during the wait above. The run below then
    # finds nothing new and reports idle, which is what the Pi itself reports.
    current = as_the_timer_does(recorder, 'to-pi')
    recorder.check(current['phase'] in ('healthy', 'idle') and current['currentVersion'] == BASE_VERSION and current['error'] is None, f'the updater installed {BASE_VERSION} by itself', current)
    recorder.check('installTarget' not in current, 'this updater writes no installTarget, as on the Pi', current)
    assert_running(recorder, BASE_VERSION, BASE_SCHEMA)


def assert_scheduled_rollback(recorder, family, current, *, attempt_version, back_to, schema, before):
    recorder.check(current['phase'] == 'rolled_back' and current['error'] == 'health', 'the updater reports rolled_back with the health error', current)
    recorder.check(current['currentVersion'] == back_to and current['failedVersion'] == attempt_version, 'the journal records the failed version', current)
    assert_running(recorder, back_to, schema)
    assert_rows_kept(recorder, before, read_database())
    return family.verify(recorder)


def scenario_pi(recorder, github, candidates, build, family, api, database):
    version = build['version']
    schema = build['databaseSchema']
    broken_same = build['brokenSameSchemaVersion']

    with recorder.step('Pi state, a crashing release arrives by itself: rollback, and no retry'):
        let_start_limit_window_pass()
        github.publish(candidates / 'broken-crash', 'v0.5.90')
        backups = len(list(BACKUPS.glob('yachad-*.backup')))
        current = as_the_timer_does(recorder, 'crash')
        recorder.check(len(list(BACKUPS.glob('yachad-*.backup'))) == backups + 1, 'a backup was taken before the downtime')
        api = assert_scheduled_rollback(recorder, family, current, attempt_version='0.5.90', back_to=BASE_VERSION, schema=BASE_SCHEMA, before=database)
        current = as_the_timer_does(recorder, 'crash-again')
        recorder.check(current['phase'] == 'failed' and len(list(BACKUPS.glob('yachad-*.backup'))) == backups + 1, 'the next scheduled run does not retry it', current)
        assert_running(recorder, BASE_VERSION, BASE_SCHEMA)

    with recorder.step(f'Pi state, {version} arrives by itself: the real upgrade'):
        let_start_limit_window_pass()
        github.publish(candidates / 'candidate', 'v' + version)
        begun = time.monotonic()
        current = as_the_timer_does(recorder, 'upgrade')
        recorder.upgrade_seconds = round(time.monotonic() - begun, 1)
        recorder.check(current['phase'] == 'healthy' and current['currentVersion'] == version, 'the updater commits the healthy version', current)
        assert_running(recorder, version, schema)
        recorder.check(sha256(WORKER) == PI_UPDATER_SHA256, 'the 0.5.0 root updater is still the one running')
        recorder.check(sha256(CURRENT / '.release-manifest.json') == build['manifestSha256'], 'the installed release carries the exact packaged manifest')
        recorder.check((RELEASES / ('v' + BASE_VERSION)).is_dir(), 'the previous release is kept')
        after = read_database()
        recorder.check(after['schema'] == schema, f'the database migrated to schema {schema}', after['schema'])
        assert_rows_kept(recorder, database, after)
        api = family.verify(recorder)
        assert_update_panel(recorder, api, currentVersion=version, mode='auto', modeControlAllowed=True, error=None)

    with recorder.step('Pi state, the new release keeps working after a restart'):
        run(['/usr/bin/systemctl', 'restart', 'yachad.service'])
        assert_running(recorder, version, schema)
        api = family.verify(recorder)
        api.require('POST', '/api/parent/connect')
        post_message(recorder, api, f'written on v{version} ' + uuid.uuid4().hex)
        family.capture(api)
        request, current = update_action(recorder, api, dict(action='check'), 'recheck')
        recorder.check(request['state'] == 'succeeded' and current['phase'] == 'idle', 'a check from the new app finds nothing newer', current)

    with recorder.step('Pi state, the install button cannot work without an installTarget'):
        set_mode(recorder, api, 'notify')
        github.publish(candidates / 'broken-same-schema', 'v' + broken_same)
        request, current = update_action(recorder, api, dict(action='check'), 'button')
        recorder.check(request['state'] == 'succeeded' and current['phase'] == 'available', 'the check finds the new release', current)
        view = assert_update_panel(recorder, api, phase='available', latestVersion=broken_same)
        recorder.check(not view.get('installTarget'), 'the panel offers no install target', view)
        code, value = api.json('POST', '/api/parent/updates', dict(action='install', target=dict(version=broken_same, fingerprint='0' * 64)))
        recorder.check(not (code == 200 and value == {'state': 'queued'}), 'the app refuses the button', value)
        recorder.phase(f'button refused: {code} {value}', 'button')
        set_mode(recorder, api, 'auto')

    with recorder.step(f'Pi state, a broken release at the same schema arrives by itself: rollback to {version}'):
        database = read_database()
        let_start_limit_window_pass()
        current = as_the_timer_does(recorder, 'same-schema')
        assert_scheduled_rollback(recorder, family, current, attempt_version=broken_same, back_to=version, schema=schema, before=database)


def bootstrap_literal(name):
    """A constant of the reviewed bootstrap, read without running it."""
    for node in ast.parse((ROOT / 'deploy/upgrade-worker-github.py').read_bytes()).body:
        if isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == name for target in node.targets):
            return ast.literal_eval(node.value)
    raise AcceptanceFailure('The bootstrap no longer defines ' + name)


def in_a_terminal(args, cwd, env, prompt=None, answer=None, timeout=300):
    """The bootstrap insists on a terminal for its confirmation, so it gets one, answered at the exact prompt."""
    master, slave = os.openpty()
    process = subprocess.Popen(args, cwd=cwd, env=env, stdin=slave, stdout=slave, stderr=slave, start_new_session=True, close_fds=True)
    os.close(slave)
    output, answered = '', False
    deadline = time.monotonic() + timeout
    try:
        while True:
            if time.monotonic() > deadline:
                process.kill()
                raise AcceptanceFailure('The bootstrap did not finish:\n' + output[-3000:])
            if not select.select([master], [], [], 1)[0]:
                continue
            try:
                raw = os.read(master, 65536)
            except OSError:  # The terminal's other end closed: the process is done.
                break
            if not raw:
                break
            output += raw.decode(errors='replace')
            if prompt and not answered and prompt in output:
                answered = True
                os.write(master, answer.encode() + b'\n')
    finally:
        os.close(master)
    return process.wait(30), output


def as_the_owner(source, *options, prompt=None, answer=None):
    """`sudo python3 -I -B upgrade-worker-github.py`: root, and SUDO_UID naming the ordinary account that owns the checkout."""
    env = dict(ENV, SUDO_UID=str(ROOT.stat().st_uid))
    # The real timer keeps running. These refusals change nothing, and an owner just runs the command again.
    busy = ('Updater is busy', 'Updater units are missing or running', 'Updater workers must be inactive')

    def attempt():
        code, output = in_a_terminal(['/usr/bin/python3', '-I', '-B', 'upgrade-worker-github.py', *options], source, env, prompt, answer)
        print(output, flush=True)
        return None if code and any(text in output for text in busy) else (code, output)
    return wait_until(attempt, 120, 'the updater units to be quiet for the bootstrap', 5)


def delivered_bootstrap(recorder):
    """Packaged by the real script, and checked and unpacked the way an owner handles the delivered archive."""
    # Not under the runner's home: root-owned and reachable, as in install_base.
    directory = pathlib.Path(tempfile.mkdtemp(prefix='yahad-bootstrap-', dir='/var/tmp'))
    run(['/usr/bin/python3', '-I', '-B', str(ROOT / 'scripts/package-github-upgrade.py'), str(directory / 'artifact')], cwd=ROOT)
    digest, name = (directory / 'artifact/SHA256SUMS').read_text(encoding='ascii').split()
    recorder.check(sha256(directory / 'artifact' / name) == digest, 'the delivered archive matches its checksum', name)
    with zipfile.ZipFile(directory / 'artifact' / name) as archive:
        archive.extractall(directory / 'extracted')
    return directory / 'extracted'


def assert_runtime(recorder, version, *, previous, package=None):
    journal = json.loads(RUNTIME_JOURNAL.read_bytes())
    recorder.check(journal['phase'] == 'stable' and journal['candidate'] is None, 'the runtime journal is stable', journal)
    recorder.check(journal['active']['version'] == version and journal['maxAcceptedVersion'] == version, f'the active runtime is {version}', journal)
    recorder.check((journal['previous'] or {}).get('version') == previous, f'the previous runtime is {previous}', journal)
    recorder.check(os.readlink(RUNTIME_POINTER) == 'versions/v' + version, 'the runtime pointer follows the journal', os.readlink(RUNTIME_POINTER))
    worker = RUNTIME_POINTER / 'updater.py'
    recorder.check(worker.read_bytes() == (ROOT / 'deploy/updater.py').read_bytes(), 'the active worker is the updater of this checkout')
    if package:
        recorder.check(journal['active']['manifestSha256'] == sha256(package / 'yachad-updater.json') and
                       journal['active']['sha256'] == sha256(package / 'yachad-updater.zip'), 'the active runtime is the published pair', journal)


def transport_state():
    with contextlib.suppress(FileNotFoundError):
        return json.loads(TRANSPORT_STATE.read_bytes())
    return {}


def on_the_real_timer(recorder, attempt, reached, timeout):
    """Nothing is started or aged here. The status file must not be touched either: the supervisor fails closed
    if it changes during a probe (deploy/updater-supervisor.py:430). The timer fires every 5 minutes on an enrolled machine."""
    seen = None
    deadline = time.monotonic() + timeout
    while True:
        current = status()
        if current.get('phase') != seen:
            seen = current.get('phase')
            recorder.phase(seen, attempt)
        state = show('yachad-update.service', 'ActiveState')
        if state == 'inactive':
            if reached(current):
                return current
            if current.get('phase') in ('failed', 'rolled_back', 'recovery_required', 'incompatible') or transport_state().get('result') == 'failed':
                raise AcceptanceFailure(f'The scheduled run stopped: {json.dumps(current)} {json.dumps(transport_state())}')
        elif state == 'failed':
            log = run(['journalctl', '-u', 'yachad-update.service', '-n', '40', '-o', 'cat', '--no-pager']).stdout.decode(errors='replace')
            raise AcceptanceFailure(f'yachad-update.service failed: {json.dumps(current)} {json.dumps(transport_state())}\n{log}')
        if time.monotonic() > deadline:
            raise AcceptanceFailure(f'Timed out after {timeout}s on the timer: {json.dumps(current)} {json.dumps(transport_state())}')
        time.sleep(1)


def scenario_pi_enrolled(recorder, github, candidates, build, family, api, database):
    version = build['version']
    schema = build['databaseSchema']
    broken_same = build['brokenSameSchemaVersion']
    runtime = build['runtimeVersion']
    broken_runtime = build['brokenSameSchemaRuntimeVersion']
    initial = bootstrap_literal('INITIAL_RUNTIME_VERSION')
    launcher = hashlib.sha256(bootstrap_literal('LAUNCHER')).hexdigest()
    settings = CONFIG.read_bytes()

    with recorder.step('Pi state, the owner enrolls with the real one-time bootstrap'):
        source = delivered_bootstrap(recorder)
        code, output = as_the_owner(source, '--check-only')
        recorder.check(code == 0 and 'CHECK_OK' in output, 'the check-only run finds the Pi eligible', output[-2000:])
        recorder.check(sha256(WORKER) == PI_UPDATER_SHA256 and not SOURCE_POLICY.exists(), 'the check-only run changed nothing')
        code, output = as_the_owner(source, prompt='Type ENABLE GITHUB UPDATER to authorize', answer='ENABLE GITHUB UPDATER')
        recorder.check(code == 0 and 'GITHUB_UPDATER_ENROLLED' in output, 'the bootstrap enrolls after the exact confirmation', output[-2000:])
        recorder.check(sha256(WORKER) == launcher, 'the launcher replaced the 0.5.0 root updater')
        for name in ('updater-supervisor.py', 'updater-transport.py'):
            recorder.check(WORKER.with_name(name).read_bytes() == (ROOT / 'deploy' / name).read_bytes(), f'{name} is installed')
        policy = json.loads(SOURCE_POLICY.read_bytes())
        recorder.check(policy['format'] == 2 and policy['trust'] == 'github-https' and policy['enabled'] is True, 'the source policy is the enabled GitHub-HTTPS profile', policy)
        assert_runtime(recorder, initial, previous=None)
        recorder.check(CONFIG.read_bytes() == settings and json.loads(settings)['mode'] == 'auto', 'the app configuration is byte-identical and still in auto mode')
        for unit in ('yachad-update-request.path', 'yachad-update.timer'):
            recorder.check(show(unit, 'ActiveState') == 'active', f'{unit} was restored')
        assert_running(recorder, BASE_VERSION, BASE_SCHEMA)
        assert_rows_kept(recorder, database, read_database())

    with recorder.step('Pi state, the first scheduled run under the supervisor keeps the app and issues a target'):
        current = on_the_real_timer(recorder, 'enrolled', lambda value: (value.get('installTarget') or {}).get('version') == BASE_VERSION, 420)
        recorder.check(current['phase'] in ('healthy', 'idle') and current['currentVersion'] == BASE_VERSION, f'the app is still {BASE_VERSION}', current)
        recorder.check(transport_state().get('result') == 'unavailable', 'an app-only release keeps the initial runtime', transport_state())
        assert_runtime(recorder, initial, previous=None)
        assert_running(recorder, BASE_VERSION, BASE_SCHEMA)
        assert_rows_kept(recorder, database, read_database())
        api = family.verify(recorder)
        assert_update_panel(recorder, api, currentVersion=BASE_VERSION, mode='auto', modeControlAllowed=True, error=None)
        code, output = as_the_owner(source, '--check-only')
        recorder.check(code == 0 and 'ALREADY_CURRENT' in output, 'rerunning the bootstrap reports the completed enrollment', output[-2000:])

    with recorder.step(f'Enrolled, {version} with runtime {runtime} arrives on the timer'):
        let_start_limit_window_pass()
        backups = len(list(BACKUPS.glob('yachad-*.backup')))
        github.publish(candidates / 'candidate', 'v' + version, updater=candidates / 'updater-candidate')
        begun = time.monotonic()
        # At most one timer interval and its jitter, then at most the service's 30 minutes.
        current = on_the_real_timer(recorder, 'upgrade', lambda value: value.get('currentVersion') == version and value.get('phase') in ('healthy', 'idle'), 2220)
        recorder.upgrade_seconds = round(time.monotonic() - begun, 1)
        assert_runtime(recorder, runtime, previous=initial, package=candidates / 'updater-candidate')
        recorder.check(sha256(WORKER) == launcher, 'the launcher itself is not replaced by a release')
        recorder.check(transport_state().get('version') == runtime and transport_state().get('result') in ('updated', 'unchanged'), 'the transport reports the new runtime', transport_state())
        assert_running(recorder, version, schema)
        recorder.check(len(list(BACKUPS.glob('yachad-*.backup'))) == backups + 1, 'a backup was taken before the downtime')
        recorder.check(sha256(CURRENT / '.release-manifest.json') == build['manifestSha256'], 'the installed release carries the exact packaged manifest')
        recorder.check((RELEASES / ('v' + BASE_VERSION)).is_dir(), 'the previous release is kept')
        after = read_database()
        recorder.check(after['schema'] == schema, f'the database migrated to schema {schema}', after['schema'])
        assert_rows_kept(recorder, database, after)
        api = family.verify(recorder)
        view = assert_update_panel(recorder, api, currentVersion=version, mode='auto', modeControlAllowed=True, error=None)
        target = view.get('installTarget') or {}
        recorder.check(target.get('version') == version and re.fullmatch('[0-9a-f]{64}', target.get('fingerprint', '')), 'the panel now offers a bound install target', view)

    with recorder.step(f'Enrolled in notify mode, the install button takes a broken release: rollback to {version} under the new runtime'):
        set_mode(recorder, api, 'notify')
        database = read_database()
        let_start_limit_window_pass()
        github.publish(candidates / 'broken-same-schema', 'v' + broken_same, updater=candidates / 'updater-broken-same-schema')
        target = discover(recorder, api, broken_same, 'same-schema')
        request, current = update_action(recorder, api, dict(action='install', target=target), 'same-schema')
        assert_rollback(recorder, family, request, current, attempt_version=broken_same, back_to=version, schema=schema, before=database)
        # A committed worker stays: the failure was the app's (docs/GITHUB-RUNTIME-UPDATES.md, "Failure and verification contract").
        assert_runtime(recorder, broken_runtime, previous=runtime, package=candidates / 'updater-broken-same-schema')
        recorder.check((RELEASES / ('v' + BASE_VERSION)).is_dir(), 'the failed attempt pruned nothing')


def install_candidate(recorder, candidates, build, family, work):
    """A new machine: this commit's own deploy/install.py, one run, no separate enrollment, nothing typed but its y."""
    version = build['version']
    schema = build['databaseSchema']
    initial = bootstrap_literal('INITIAL_RUNTIME_VERSION')
    launcher = hashlib.sha256(bootstrap_literal('LAUNCHER')).hexdigest()
    source = unpacked(candidates / 'candidate')

    with recorder.step(f'Install {version} through its own deploy/install.py and create the family'):
        installer = Installer(source, work / 'install.log')
        # InstallStopped is the failure here: a first install must not need a rerun.
        token = wait_until(installer.setup_token, 1200, 'deploy/install.py to print the setup link', 2)
        create_family(recorder, installer, token, family, version, schema)
        recorder.check('NehoraiHadad/yahad' in installer.output and 'certificate-verified HTTPS' in installer.output, 'the install discloses whom it trusts for root updater code')
        recorder.check('ENABLE GITHUB UPDATER' not in installer.output, 'the install asks for no typed phrase')

    with recorder.step('The one run also enrolled the root updater'):
        recorder.check(sha256(WORKER) == launcher, 'the launcher is the installed root updater')
        for name in ('updater-supervisor.py', 'updater-transport.py'):
            recorder.check(WORKER.with_name(name).read_bytes() == (ROOT / 'deploy' / name).read_bytes(), f'{name} is installed')
        policy = json.loads(SOURCE_POLICY.read_bytes())
        recorder.check(policy['format'] == 2 and policy['trust'] == 'github-https' and policy['enabled'] is True, 'the source policy is the enabled GitHub-HTTPS profile', policy)
        assert_runtime(recorder, initial, previous=None)
        config = json.loads(CONFIG.read_bytes())
        recorder.check(config['mode'] == 'auto', 'the install leaves automatic updates on', config)

    with recorder.step('The first scheduled run under the supervisor keeps the app and issues a target'):
        current = on_the_real_timer(recorder, 'fresh', lambda value: (value.get('installTarget') or {}).get('version') == version, 420)
        recorder.check(current['phase'] in ('healthy', 'idle') and current['currentVersion'] == version, f'the app is still {version}', current)
        recorder.check(transport_state().get('result') == 'unavailable', 'an app-only release keeps the initial runtime', transport_state())
        assert_runtime(recorder, initial, previous=None)

    with recorder.step('Rerunning the same command on the enrolled machine succeeds and changes nothing'):
        # Just after a timer run, so the next one is minutes away and finds the lock free.
        watched = (WORKER, CONFIG, SOURCE_POLICY, RUNTIME_JOURNAL, pathlib.Path('/var/lib/yachad-updates/install.json'), STATUS)
        before = [path.read_bytes() for path in watched]
        pointer = os.readlink(RUNTIME_POINTER)
        rerun = Installer(source, work / 'install-rerun.log')
        try:
            code = rerun.process.wait(600)
        except subprocess.TimeoutExpired:
            rerun.process.kill()
            raise AcceptanceFailure('The rerun of deploy/install.py did not finish:\n' + rerun.tail()) from None
        rerun.pump.join(5)
        recorder.check(code == 0 and 'ALREADY_COMPLETE' in rerun.output, 'the rerun reports the completed install', rerun.tail())
        recorder.check([path.read_bytes() for path in watched] == before and os.readlink(RUNTIME_POINTER) == pointer, 'the rerun changed nothing')

    with recorder.step('Create real data through the API'):
        return seed_family(recorder, family, version, schema)


def scenario_fresh(recorder, github, candidates, build, family, api, database):
    version = build['version']
    schema = build['databaseSchema']
    healthy = build['healthySameSchemaVersion']
    runtime = build['healthySameSchemaRuntimeVersion']
    package = candidates / 'healthy-same-schema'
    pair = candidates / 'updater-healthy-same-schema'
    launcher = hashlib.sha256(bootstrap_literal('LAUNCHER')).hexdigest()

    with recorder.step(f'{healthy} with runtime {runtime} arrives on the timer'):
        let_start_limit_window_pass()
        backups = len(list(BACKUPS.glob('yachad-*.backup')))
        github.publish(package, 'v' + healthy, updater=pair)
        begun = time.monotonic()
        # At most one timer interval and its jitter, then at most the service's 30 minutes.
        on_the_real_timer(recorder, 'upgrade', lambda value: value.get('currentVersion') == healthy and value.get('phase') in ('healthy', 'idle'), 2220)
        recorder.upgrade_seconds = round(time.monotonic() - begun, 1)
        assert_runtime(recorder, runtime, previous=bootstrap_literal('INITIAL_RUNTIME_VERSION'), package=pair)
        recorder.check(sha256(WORKER) == launcher, 'the launcher itself is not replaced by a release')
        assert_running(recorder, healthy, schema)
        recorder.check(len(list(BACKUPS.glob('yachad-*.backup'))) == backups + 1, 'a backup was taken before the downtime')
        recorder.check(sha256(CURRENT / '.release-manifest.json') == sha256(package / 'yachad-release.json'), 'the installed release carries the exact packaged manifest')
        recorder.check((RELEASES / ('v' + version)).is_dir(), 'the previous release is kept')
        assert_rows_kept(recorder, database, read_database())
        api = family.verify(recorder)
        view = assert_update_panel(recorder, api, currentVersion=healthy, mode='auto', modeControlAllowed=True, error=None)
        target = view.get('installTarget') or {}
        recorder.check(target.get('version') == healthy and re.fullmatch('[0-9a-f]{64}', target.get('fingerprint', '')), 'the panel offers a bound install target', view)


SCENARIOS = {'upgrade': scenario_upgrade, 'migrated-failure': scenario_migrated_failure, 'pi': scenario_pi, 'pi-enrolled': scenario_pi_enrolled, 'fresh': scenario_fresh}


def prepare_host(recorder):
    recorder.check(pathlib.Path('/proc/1/comm').read_text().strip() == 'systemd', 'PID 1 is systemd')
    recorder.check(platform.machine() == 'aarch64', 'the host is arm64, like the Pi', platform.machine())
    for path in ('/opt/yachad', '/etc/yachad', '/var/lib/yachad'):
        if os.path.lexists(path):
            raise AcceptanceFailure('Refusing to run over an existing installation: ' + path)
    # A hosted image relaxes some system directories that a Pi keeps root-only, and
    # the installer rightly refuses those. This puts the host in the Pi's shape.
    adjusted = []
    for name in ('/', '/opt', '/usr', '/usr/local', '/usr/local/lib', '/usr/local/bin', '/var', '/var/lib', '/var/backups', '/etc', '/etc/systemd', '/etc/systemd/system'):
        info = os.lstat(name)
        if info.st_uid != 0 or info.st_mode & 0o022:
            os.chown(name, 0, info.st_gid)
            os.chmod(name, info.st_mode & 0o7755)
            adjusted.append(name)
    run(['ip', 'address', 'add', LAN_IP + '/32', 'dev', 'lo'])
    return adjusted


def collect_diagnostics(output):
    diagnostics = output / 'diagnostics'
    diagnostics.mkdir(exist_ok=True)
    for unit in UNITS:
        log = run(['journalctl', '-u', unit, '-o', 'short-iso', '--no-pager'], check=False, timeout=60).stdout
        (diagnostics / (unit + '.log')).write_bytes(log)
        (diagnostics / (unit + '.status')).write_bytes(run(['/usr/bin/systemctl', 'status', '--no-pager', '--full', unit], check=False, timeout=60).stdout)
    for path in (STATUS, CONFIG, SOURCE_POLICY, RUNTIME_JOURNAL, TRANSPORT_STATE):  # None holds a secret; the operator ids are throwaway.
        if path.exists():
            shutil.copyfile(path, diagnostics / path.name)
    listing = run(['ls', '-la', '/opt/yachad', '/opt/yachad/releases', str(BACKUPS), '/var/lib/yachad-updates', '/var/lib/yachad-updates/runtime/versions'], check=False).stdout
    (diagnostics / 'layout.txt').write_bytes(listing)


def install_published(recorder, github, work, family, scenario, summary):
    first = PI_INSTALLED if scenario in ('pi', 'pi-enrolled') else BASE
    with recorder.step('Download the published assets'):
        for release in {id(item): item for item in (first, BASE)}.values():
            download_release(work, release)
    with recorder.step('Stand in for GitHub with a throwaway CA'):
        publish_release(github, work, first)
        github.start()
        github.verify_reachable(recorder, 'v' + first['version'])
    with recorder.step(f"Install v{first['version']} through its own deploy/install.py and create the family"):
        install_base(recorder, github, work, family, first)
        summary['updaterSha256'] = sha256(WORKER)
        summary['updaterIdenticalToCheckout'] = sha256(WORKER) == sha256(ROOT / 'deploy/updater.py')
    if first is PI_INSTALLED:
        with recorder.step(f'Reach the Pi state: {BASE_VERSION} under the 0.5.0 updater'):
            reach_the_pi_state(recorder, github, work)
    with recorder.step('Create real data through the API'):
        api, database = seed_family(recorder, family)
    if first is not PI_INSTALLED:
        with recorder.step('Parent switches to notify, so that the install is the button'):
            set_notify(recorder, api)
    return api, database


def execute(arguments):
    if not arguments.disposable or os.geteuid() != 0:
        raise AcceptanceFailure('Run as root with --disposable, on a throwaway machine only')
    output = pathlib.Path(arguments.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    candidates = pathlib.Path(arguments.candidates).resolve()
    build = json.loads((candidates / 'build.json').read_bytes())
    work = output / 'work'
    work.mkdir()
    fake = output / 'github'
    fake.mkdir()
    recorder = Recorder()
    github = GitHub(fake)
    family = Family()
    failure = None
    summary = dict(scenario=arguments.scenario, commit=arguments.commit, baseRelease=BASE_VERSION, candidate=build)
    try:
        with recorder.step('Prepare the host'):
            summary['adjustedDirectories'] = prepare_host(recorder)
            summary['host'] = dict(machine=platform.machine(), kernel=platform.release(), python=platform.python_version(),
                                   node=run(['node', '--version']).stdout.decode().strip(),
                                   caddy=run(['caddy', 'version']).stdout.decode().strip())
        if arguments.scenario == 'fresh':
            summary['baseRelease'] = None
            with recorder.step('Stand in for GitHub with a throwaway CA'):
                github.publish(candidates / 'candidate', 'v' + build['version'])
                github.start()
                github.verify_reachable(recorder, 'v' + build['version'])
            api, database = install_candidate(recorder, candidates, build, family, work)
        else:
            api, database = install_published(recorder, github, work, family, arguments.scenario, summary)
        SCENARIOS[arguments.scenario](recorder, github, candidates, build, family, api, database)
    except Exception as error:  # Reported in summary.json, then the job fails.
        failure = dict(type=type(error).__name__, message=str(error)[:4000], trace=traceback.format_exc()[-4000:])
        print('::error::' + str(error).replace('\n', '%0A')[:2000], flush=True)
    finally:
        with contextlib.suppress(Exception):
            collect_diagnostics(output)
        requests = github.requests()
        github.close()
        for log in ('install.log', 'install-rerun.log'):
            (work / log).exists() and shutil.copyfile(work / log, output / log)
        shutil.rmtree(work, ignore_errors=True)
        summary.update(result='failed' if failure else 'passed', failure=failure, totalSeconds=round(time.monotonic() - recorder.started, 1),
                       upgradeSeconds=getattr(recorder, 'upgrade_seconds', None), steps=recorder.steps, phases=recorder.phases,
                       assertionsPassed=sum(1 for item in recorder.assertions if item['ok']), assertions=recorder.assertions,
                       githubRequests=len(requests))
        (output / 'summary.json').write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding='utf8')
        run(['chmod', '-R', 'a+rX', str(output)], check=False)
    return 1 if failure else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest='command', required=True)
    build = commands.add_parser('package', help='Build the candidate and the broken variants, as an ordinary user')
    build.add_argument('--output', required=True)
    build.add_argument('--commit', required=True)
    accept = commands.add_parser('run', help='Run one scenario, as root on a disposable machine')
    accept.add_argument('--scenario', required=True, choices=sorted(SCENARIOS))
    accept.add_argument('--candidates', required=True)
    accept.add_argument('--output', required=True)
    accept.add_argument('--commit', required=True)
    accept.add_argument('--disposable', action='store_true')
    arguments = parser.parse_args()
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    if arguments.command == 'package':
        package(arguments)
        return 0
    return execute(arguments)


if __name__ == '__main__':
    try:
        sys.exit(main())
    except AcceptanceFailure as error:
        print('::error::' + str(error).replace('\n', '%0A')[:2000], flush=True)
        sys.exit(1)
