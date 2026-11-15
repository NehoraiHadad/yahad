#!/usr/bin/env python3
"""Real Linux install and update acceptance, for a disposable root runner with systemd.

`package` builds the candidate from this checkout with the real release command, plus
a healthy higher release of the same schema and that release's root updater pair.
`run` installs the candidate through its own deploy/install.py, as a new machine does:
one run, no separate enrollment. It creates a family through the real API over the
managed HTTPS origin, then publishes the higher release and lets the real systemd timer
deliver it: the supervisor advances the root updater, and deploy/updater.py's own
verify, build, backup, activation and health logic installs the app, keeping the data.

The updater has fixed hosts and verifies TLS, and deploy/ is not edited. So the host
gets a throwaway CA and an /etc/hosts entry, and fake-github.py serves "latest" from
the packaged candidates. github.com and npm stay the real services.

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

ASSET_URL = 'https://api.github.com/repos/NehoraiHadad/yahad/releases/assets/'
LATEST_URL = 'https://api.github.com/repos/NehoraiHadad/yahad/releases/latest'
FAKE_HOSTS = ('api.github.com', 'release-assets.githubusercontent.com')

# A private address on loopback. deploy/install.py requires a private non-loopback
# IPv4 origin, and Caddy binds exactly this address.
LAN_IP = '192.168.77.10'
ORIGIN = 'https://' + LAN_IP

CONFIG = pathlib.Path('/etc/yachad/updates.json')
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

# A higher release that differs from the candidate only by this line, so there is a real one to update to.
HIGHER = ('src/main.ts', 'silencePayloadConsoles();\n', 'silencePayloadConsoles();\n// Acceptance: a healthy higher version.\n')


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
    """A patched copy of this checkout at another version, through the real release command."""
    source = work / ('source-' + version)
    shutil.copytree(ROOT, source, ignore=shutil.ignore_patterns('node_modules', '.git', '.local', 'installer', '__pycache__'))
    name, old, new = patch
    path = source / name
    text = path.read_text(encoding='utf8')
    if text.count(old) != 1:
        raise AcceptanceFailure(f'The candidate patch no longer matches {name}; update it')
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
    healthy_same_schema = f'{major}.{minor}.{patch + 1}'
    with recorder.step('Package a healthy higher candidate of the same schema'):
        package_variant(work, output / 'healthy-same-schema', healthy_same_schema, HIGHER)
    work.rmdir()
    with recorder.step('Package its root updater pair with scripts/release-updater.mjs package-github'):
        healthy_runtime = package_runtime(output / 'updater-healthy-same-schema', healthy_same_schema, env)
        recorder.check(version_tuple(healthy_runtime) > version_tuple(bootstrap_literal('INITIAL_RUNTIME_VERSION')),
                       'the runtime is above the initial one of the bootstrap', healthy_runtime)
    (output / 'build.json').write_text(json.dumps(dict(
        commit=arguments.commit, version=version, databaseSchema=manifest['databaseSchema'],
        manifestSha256=sha256(output / 'candidate/yachad-release.json'), archiveBytes=manifest['bytes'],
        healthySameSchemaVersion=healthy_same_schema, healthySameSchemaRuntimeVersion=healthy_runtime,
        steps=recorder.steps), indent=2))
    print(f'Candidate {version} and its higher release {healthy_same_schema} packaged in {output}', flush=True)


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

    def publish(self, directory, tag, updater=None):
        assets, files = [], {}
        sources = [(pathlib.Path(directory), name) for name in ('yachad-app.zip', 'yachad-release.json')]
        if updater:
            sources += [(pathlib.Path(updater), name) for name in ('yachad-updater.json', 'yachad-updater.zip')]
        for directory, name in sources:
            self.next_id += 1
            asset_id = self.next_id
            path = directory / name
            assets.append(dict(id=asset_id, name=name, url=ASSET_URL + str(asset_id), size=path.stat().st_size,
                               state='uploaded', content_type='application/octet-stream'))
            files[str(asset_id)] = str(path)
        self.next_id += 1
        value = dict(id=self.next_id, tag_name=tag, draft=False, prerelease=False, assets=assets)
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


# The application's own HTTP surface -----------------------------------------------

class Api:
    """A browser's view: the managed HTTPS origin, Origin header, secure cookie, CSRF."""

    def __init__(self):
        context = ssl.create_default_context(cafile=str(CADDY_ROOT))
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
        self.cookie = None
        self.csrf = None

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
            self.csrf = me['csrf']
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


def read_database():
    connection = sqlite3.connect(DATABASE.as_uri() + '?mode=ro', uri=True)
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
    """The data created before the update, and how to prove it is still there."""

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
    """What the parent's update panel reads, parsed by the running application's own schema."""
    view = api.require('GET', '/api/parent/updates')
    recorder.check(view.get('configured') is True, 'GET /api/parent/updates parses the updater status as configured', view)
    for name, value in expected.items():
        recorder.check(view.get(name) == value, f'the update panel reports {name}={value}', view)
    recorder.check(view.get('enabled') is True and view.get('stale') is False, 'the update panel is enabled and not stale', view)
    return view


# Steps ----------------------------------------------------------------------------

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
            raise AcceptanceFailure(f'deploy/install.py exited {self.process.returncode} before printing a setup link:\n' + self.tail())
        return match and match[1]

    def tail(self):
        with self.lock:
            return re.sub(r'#setup=[A-Za-z0-9_-]+', '#setup=REDACTED', self.output)[-3000:]


def unpacked(package):
    # Not under the runner's home: root-owned and reachable, as an owner's unpacked copy is.
    source = pathlib.Path(tempfile.mkdtemp(prefix='yahad-candidate-', dir='/var/tmp'))
    with zipfile.ZipFile(package / 'yachad-app.zip') as archive:
        archive.extractall(source)
    return source


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


def seed_family(recorder, family, version, schema):
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
    return database


def bootstrap_literal(name):
    """A constant of the reviewed bootstrap, read without running it."""
    for node in ast.parse((ROOT / 'deploy/upgrade-worker-github.py').read_bytes()).body:
        if isinstance(node, ast.Assign) and any(isinstance(target, ast.Name) and target.id == name for target in node.targets):
            return ast.literal_eval(node.value)
    raise AcceptanceFailure('The bootstrap no longer defines ' + name)


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
            if (current.get('phase') in ('failed', 'rolled_back', 'recovery_required', 'incompatible') and current.get('failedVersion') is not None or
                    transport_state().get('result') == 'failed'):
                raise AcceptanceFailure(f'The scheduled run stopped: {json.dumps(current)} {json.dumps(transport_state())}')
        elif state == 'failed':
            log = run(['journalctl', '-u', 'yachad-update.service', '-n', '40', '-o', 'cat', '--no-pager']).stdout.decode(errors='replace')
            raise AcceptanceFailure(f'yachad-update.service failed: {json.dumps(current)} {json.dumps(transport_state())}\n{log}')
        if time.monotonic() > deadline:
            raise AcceptanceFailure(f'Timed out after {timeout}s on the timer: {json.dumps(current)} {json.dumps(transport_state())}')
        time.sleep(1)


def install_candidate(recorder, candidates, build, family, work):
    """A new machine: this commit's own deploy/install.py, one run, no separate enrollment, nothing typed but its y."""
    version = build['version']
    schema = build['databaseSchema']
    initial = bootstrap_literal('INITIAL_RUNTIME_VERSION')
    launcher = hashlib.sha256(bootstrap_literal('LAUNCHER')).hexdigest()
    source = unpacked(candidates / 'candidate')

    with recorder.step(f'Install {version} through its own deploy/install.py and create the family'):
        installer = Installer(source, work / 'install.log')
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


def update_on_the_timer(recorder, github, candidates, build, family, database):
    """The higher release and its root updater pair arrive on the real timer, with no button pressed."""
    version = build['version']
    schema = build['databaseSchema']
    healthy = build['healthySameSchemaVersion']
    runtime = build['healthySameSchemaRuntimeVersion']
    package = candidates / 'healthy-same-schema'
    pair = candidates / 'updater-healthy-same-schema'
    launcher = hashlib.sha256(bootstrap_literal('LAUNCHER')).hexdigest()

    with recorder.step(f'{healthy} with runtime {runtime} arrives on the timer'):
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
    summary = dict(commit=arguments.commit, candidate=build)
    try:
        with recorder.step('Prepare the host'):
            summary['adjustedDirectories'] = prepare_host(recorder)
            summary['host'] = dict(machine=platform.machine(), kernel=platform.release(), python=platform.python_version(),
                                   node=run(['node', '--version']).stdout.decode().strip(),
                                   caddy=run(['caddy', 'version']).stdout.decode().strip())
        with recorder.step('Stand in for GitHub with a throwaway CA'):
            github.publish(candidates / 'candidate', 'v' + build['version'])
            github.start()
            github.verify_reachable(recorder, 'v' + build['version'])
        database = install_candidate(recorder, candidates, build, family, work)
        update_on_the_timer(recorder, github, candidates, build, family, database)
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
    build = commands.add_parser('package', help='Build the candidate and its higher release, as an ordinary user')
    build.add_argument('--output', required=True)
    build.add_argument('--commit', required=True)
    accept = commands.add_parser('run', help='Install the candidate and update it on the timer, as root on a disposable machine')
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
