#!/usr/bin/env python3
"""Owner-operated installation of the existing GitHub HTTPS Yachad updater.
Only run on the owner terminal. No signing key or GitHub credentials required.
"""
import base64, contextlib, getpass, hashlib, importlib.util, json, os, pathlib, re
import secrets, shutil, sqlite3, stat, subprocess, sys, tempfile, time, types, urllib.request, zlib

STAGE_SOURCE = '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'
STAGE_SHA = '8ddf1931e745e7feef8c17c4bbe456928ca6c24c3f5165c64d3f9779eddd9bda'
source = zlib.decompress(base64.b64decode(STAGE_SOURCE))
if hashlib.sha256(source).hexdigest() != STAGE_SHA:
    raise RuntimeError('Embedded verifier mismatch')
verifier = types.ModuleType('embedded_verified_stager')
exec(compile(source, '<embedded-stager>', 'exec'), verifier.__dict__)

UNITS = ('yachad-update.service', 'yachad-update.timer', 'yachad-update-recover.service',
         'yachad-update-backup-expiry.service', 'yachad-update-backup-expiry.timer')
OLD_EXEC = '/usr/bin/env node --import tsx src/main.ts'
STATE = '/var/lib/yachad-updates'
DATA = '/var/lib/yachad/live'
KEY = '/etc/yachad/whatsapp.key'
CONFIG = '/etc/yachad/updates.json'
SECRET = '/etc/yachad/update-backup-passphrase'
BACKUPS = '/var/backups/yachad-updates'
BUILD = '/var/lib/yachad-update-build'
HELPERS = '/usr/local/lib/yachad-updater'
RECEIPT = STATE + '/owner-setup-https-0.3.2.json'
DROPIN = '/etc/systemd/system/yachad.service.d/40-yachad-updater.conf'
MODE_CONTROL_RELEASE_READY = False  # Confirm stable v0.3.2 publication/read-back before delivery.


def regular_bytes(path, maximum):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= maximum:
            raise ValueError('Expected bounded regular input file')
        with os.fdopen(fd, 'rb', closefd=False) as incoming:
            value = incoming.read(maximum + 1)
        if len(value) > maximum: raise ValueError('Input grew beyond bound')
        return value
    finally: os.close(fd)


class Files:
    def ancestors(self, path):
        for parent in (path, *path.parents):
            if parent.exists() or parent.is_symlink():
                info=self.info(parent)
                if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0 or info.st_mode & 0o022:
                    raise ValueError('Unsafe privileged ancestor: '+str(parent))
    def info(self, path): return path.lstat()
    def own(self, path, uid, gid): os.chown(path, uid, gid, follow_symlinks=False)
    def check(self, path, uid, gid, mode, directory=False):
        info = self.info(path)
        if (not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)) or
                info.st_uid != uid or info.st_gid != gid or stat.S_IMODE(info.st_mode) != mode):
            raise ValueError('Unexpected ownership/type/mode: ' + str(path))
    def directory(self, path, uid, gid, mode):
        self.ancestors(path.parent)
        if path.exists() or path.is_symlink(): self.check(path, uid, gid, mode, True)
        else:
            path.mkdir(mode=mode)
            self.own(path, uid, gid)
            path.chmod(mode)
    def same_or_absent(self, path, value, uid, gid, mode):
        if path.exists() or path.is_symlink():
            self.check(path, uid, gid, mode)
            if regular_bytes(path, max(len(value), 8192)) != value:
                raise ValueError('Existing file differs; no overwrite: ' + str(path))
    def install(self, path, value, uid, gid, mode):
        self.ancestors(path.parent)
        self.same_or_absent(path, value, uid, gid, mode)
        if path.exists(): return
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
        try:
            os.fchown(fd, uid, gid)
            os.fchmod(fd, mode)
            with os.fdopen(fd, 'wb', closefd=False) as output:
                output.write(value); output.flush(); os.fsync(fd)
        finally: os.close(fd)
    def replace_owned(self, path, value):
        self.check(path, 0, 0, 0o600)
        temporary = path.with_name(path.name + '.owner-setup.tmp')
        self.install(temporary, value, 0, 0, 0o600)
        temporary.replace(path)
        fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try: os.fsync(fd)
        finally: os.close(fd)


class Linux:
    def __init__(self):
        import pwd
        if os.geteuid() != 0 or not sys.stdin.isatty() or not sys.stderr.isatty():
            raise ValueError('Run with sudo in the local owner terminal, without piping input')
        uid = int(os.environ.get('SUDO_UID', '0'))
        if uid < 1000: raise ValueError('Run from the ordinary owner account using sudo')
        self.owner = pwd.getpwuid(uid)
        self.app = pwd.getpwnam('yachad')
        self.files = Files()
        self.home = pathlib.Path(self.owner.pw_dir)
    def run(self, args, *, user=None, cwd=None, input=None, stdin_file=None, stdout_file=None, timeout=1200):
        if user: args = ['/usr/sbin/runuser', '-u', user, '--', *args]
        try:
            result = subprocess.run(args, cwd=cwd, input=input, stdin=stdin_file, stdout=stdout_file or subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout,
                                    env={'PATH':'/usr/local/bin:/usr/bin:/bin','HOME':'/root','LANG':'C.UTF-8'})
        except (OSError, subprocess.TimeoutExpired): raise ValueError('Command unavailable or timed out: ' + pathlib.Path(args[0]).name) from None
        if result.returncode: raise ValueError('Command failed; no command output logged: ' + pathlib.Path(args[0]).name)
        return result.stdout or b''
    def health(self, version):
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs): return None
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        # Explicit loopback GET; no proxy and no provider access.
        with opener.open('http://127.0.0.1:3000/health/ready', timeout=10) as response:
            body = json.loads(response.read(1024))
        if body.get('ok') is not True or body.get('version') != version or body.get('databaseSchema') != 1:
            raise ValueError('Expected application readiness/version/schema not confirmed')
    def builder(self, create=False):
        import pwd, grp
        try: account = pwd.getpwnam('yachad-update')
        except KeyError:
            if not create: return None
            self.run(['/usr/sbin/useradd','--system','--home',BUILD+'/home','--shell','/usr/sbin/nologin','yachad-update'])
            account = pwd.getpwnam('yachad-update')
        groups = os.getgrouplist(account.pw_name, account.pw_gid)
        if (account.pw_uid == 0 or account.pw_uid == self.app.pw_uid or self.app.pw_gid in groups or 0 in groups or
                account.pw_dir != BUILD+'/home' or account.pw_shell != '/usr/sbin/nologin'):
            raise ValueError('Existing builder account differs from the isolated layout; inspect it locally')
        return account


class Guide:
    def __init__(self, system, root=pathlib.Path('/'), ask=input, hidden=getpass.getpass):
        self.s, self.f, self.root, self.ask, self.hidden = system, system.files, root, ask, hidden
        self.receipt = {}
    def p(self, absolute): return self.root / absolute.lstrip('/')
    def note(self, **updates):
        self.receipt.update(updates)
        value = (json.dumps(self.receipt, indent=2)+'\n').encode()
        path = self.p(RECEIPT)
        if path.exists(): self.f.replace_owned(path, value)
        else: self.f.install(path, value, 0, 0, 0o600)
    def load_receipt(self):
        path = self.p(RECEIPT)
        if path.exists() or path.is_symlink():
            self.f.check(path, 0, 0, 0o600)
            self.receipt = json.loads(regular_bytes(path, 8192))
            if self.receipt.get('release') != verifier.RELEASE_ID: raise ValueError('Setup receipt belongs to another release')
    def verified_release(self, work):
        # Fetch public stable assets into a root-private snapshot over verified TLS.
        # No owner-writable downloaded code is imported or executed as root.
        return verifier.fetch_release(work), None
    def environment(self):
        data = regular_bytes(self.p('/etc/yachad/yachad.env'), 65536).decode()
        found = {}
        for line in data.splitlines():
            match = re.fullmatch(r'\s*(DATA_DIR|MASTER_KEY_FILE)\s*=\s*(.*?)\s*', line)
            if match:
                import shlex
                values = shlex.split(match[2], comments=False)
                if len(values) != 1: raise ValueError('Nonstandard live environment; review locally')
                found[match[1]] = values[0]
        if found != {'DATA_DIR':DATA,'MASTER_KEY_FILE':KEY}: raise ValueError('Live data/key paths differ; no environment changes made')
    def operator(self):
        with sqlite3.connect(self.p(DATA+'/app.sqlite').as_uri()+'?mode=ro', uri=True) as db:
            if db.execute('SELECT version FROM schema_version').fetchone() != (1,): raise ValueError('Schema must be 1')
            rows = db.execute("SELECT family_id,id,username FROM users WHERE role='parent' ORDER BY username").fetchall()
        if not rows: raise ValueError('No parent account found in the live store')
        if self.receipt.get('operator'):
            old = self.receipt['operator']
            if any((f,u)==(old['familyId'],old['userId']) for f,u,_ in rows): return old
            raise ValueError('Previously designated parent is no longer present')
        if len(rows)==1: choice=0
        else:
            print('Choose the parent who controls installation-wide updates:')
            for index,(_,_,username) in enumerate(rows,1): print(str(index)+'. '+username)
            choice=int(self.ask('Parent number: '))-1
            if not 0 <= choice < len(rows): raise ValueError('Invalid parent choice')
        return {'familyId':rows[choice][0], 'userId':rows[choice][1]}
    def config(self, release, operator):
        value=json.loads((release/'deploy/updates.json.example').read_bytes())
        value.update(enabled=False, mode='notify', operator=operator, dataDirectory=DATA, masterKeyFile=KEY,
                     node='/usr/local/bin/node', npm='/usr/local/bin/npm', tokenFile=None, publicKeyFile=None,
                     releaseTrust='github-https', repository='NehoraiHadad/yahad', allowUiModeControl=False, installVersionPin=verifier.VERSION)
        return value
    def preflight(self, release, public):
        self.environment()
        current=self.p('/opt/yachad/current')
        active_name=current.resolve().name
        expected=verifier.VERSION if self.receipt.get('manualHealthy') or (self.receipt.get('manualStarting') and active_name=='v'+verifier.VERSION) else '0.2.0'
        if not current.is_symlink() or current.resolve()!=self.p('/opt/yachad/releases/v'+expected).resolve():
            raise ValueError('Current release differs; inspect installation state before proceeding')
        for directory in ('/opt/yachad','/opt/yachad/releases',str(current.resolve())):
            path = pathlib.Path(directory) if str(directory).startswith(str(self.root)) and self.root!=pathlib.Path('/') else self.p(directory)
            info=self.f.info(path)
            if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0 or info.st_mode & 0o022: raise ValueError('Release ancestor must be root-owned/non-writable')
        self.f.check(self.p(DATA), self.s.app.pw_uid,self.s.app.pw_gid,0o700,True)
        self.f.check(self.p(KEY), self.s.app.pw_uid,self.s.app.pw_gid,0o600)
        self.s.health(expected)
        properties=self.s.run(['/usr/bin/systemctl','show','yachad.service','--property=User,Group,WorkingDirectory,ExecStart,EnvironmentFiles','--no-pager']).decode()
        values=dict(line.split('=',1) for line in properties.splitlines() if '=' in line)
        if values.get('User')!='yachad' or values.get('Group')!='yachad' or values.get('WorkingDirectory')!='/opt/yachad/current': raise ValueError('Nonstandard service identity/cwd; preserve and review')
        argv=re.search(r'argv\[\]=(.*?)\s*;',values.get('ExecStart',''))
        guard=next(x.removeprefix('ExecStart=') for x in (release/'deploy/yachad.service').read_text().splitlines() if x.startswith('ExecStart='))
        if not argv or argv[1].strip() not in (OLD_EXEC,guard): raise ValueError('Nonstandard app command; preserve and review')
        if values.get('EnvironmentFiles')!='/etc/yachad/yachad.env (ignore_errors=no)': raise ValueError('Nonstandard EnvironmentFile set; no changes made')
        for unit in ('yachad-update.service','yachad-update.timer','yachad-update-backup-expiry.timer'):
            state=self.s.run(['/usr/bin/systemctl','show',unit,'--property=LoadState,ActiveState,UnitFileState','--no-pager']).decode()
            if ('ActiveState=active' in state or 'UnitFileState=enabled' in state) and not (self.receipt.get('autoEnabled') or self.receipt.get('autoStarting')):
                raise ValueError('An updater unit is already active/enabled; inspect before changing it')
        for name in UNITS:
            self.f.same_or_absent(self.p('/etc/systemd/system/'+name),(release/'deploy'/name).read_bytes(),0,0,0o644)
        for name in ('updater.py','data-lock.py','export-dependencies.py'):
            self.f.same_or_absent(self.p(HELPERS+'/'+name),(release/'deploy'/name).read_bytes(),0,0,0o644)
        pin=self.p('/etc/yachad/release-signing.pub')
        if pin.exists() or pin.is_symlink():raise ValueError('Signature-pinned installation preserved; HTTPS setup will not migrate it')
        if self.p(CONFIG).exists() and not self.receipt: raise ValueError('Existing config has no setup receipt; preserve and inspect')
        self.s.builder(False)
        return guard
    def passphrase(self):
        path=self.p(SECRET)
        if path.exists() or path.is_symlink():
            self.f.check(path,0,0,0o600)
            value=regular_bytes(path,1024).strip()
            if self.receipt.get('secretHash'):
                if hashlib.sha256(value).hexdigest()!=self.receipt['secretHash']: raise ValueError('Backup secret changed; preserve and inspect')
                return value
            print('An existing backup secret will be preserved. Retrieve its off-device recovery copy.')
            entered=self.hidden('Existing backup passphrase (hidden): ').encode()
            if not secrets.compare_digest(entered,value): raise ValueError('Recovery copy does not match')
        else:
            print('Save a separate backup passphrase in your password manager/off-device recovery storage first.')
            print('It protects backups containing chats, linked-device auth and the data master key; losing it prevents restore.')
            first=self.hidden('Backup passphrase, 18+ characters (hidden): ')
            if len(first)<18 or first!=first.strip() or len(first.encode())>1024 or any(ord(c)<32 for c in first): raise ValueError('Use 18+ characters, no leading/trailing whitespace or controls')
            if first!=self.hidden('Confirm from your saved recovery copy (hidden): '): raise ValueError('Passphrases differ')
            value=first.encode()
        if not 18<=len(value)<=1024: raise ValueError('Existing backup secret length is invalid')
        if self.ask('Type SAVED after saving the recovery copy outside this Pi: ')!='SAVED': raise ValueError('Off-device secret recovery has not been confirmed')
        return value
    def prepare(self, release, public):
        guard=self.preflight(release,public)
        operator=self.operator()
        config=self.config(release,operator)
        if self.p(CONFIG).exists():
            self.f.check(self.p(CONFIG),0,0,0o600)
            existing=json.loads(regular_bytes(self.p(CONFIG),8192))
            allowed=[config]
            if self.receipt.get('checkingEnabled') or self.receipt.get('checkingStarting'): allowed.append(dict(config,enabled=True))
            if self.receipt.get('autoEnabled') or self.receipt.get('autoStarting'): allowed.append(dict(config,enabled=True,mode='auto',allowUiModeControl=True,installVersionPin=None))
            if existing not in allowed: raise ValueError('Existing config differs; no overwrite')
        secret=self.passphrase()
        for path,uid,gid,mode in ((STATE,0,self.s.app.pw_gid,0o750),(BUILD,0,0,0o755),(HELPERS,0,0,0o755),(BACKUPS,self.s.app.pw_uid,self.s.app.pw_gid,0o700)):
            self.f.directory(self.p(path),uid,gid,mode)
        # Persist intent before creating config/secret, so an interrupted setup can resume safely.
        if not self.receipt: self.note(release=verifier.RELEASE_ID,operator=operator,prepared=False)
        builder=self.s.builder(True)
        self.f.directory(self.p(BUILD+'/home'),builder.pw_uid,builder.pw_gid,0o700)
        for name in ('updater.py','data-lock.py','export-dependencies.py'):
            self.f.install(self.p(HELPERS+'/'+name),(release/'deploy'/name).read_bytes(),0,0,0o644)
        for name in UNITS:
            self.f.install(self.p('/etc/systemd/system/'+name),(release/'deploy'/name).read_bytes(),0,0,0o644)
        self.f.install(self.p(SECRET),secret,0,0,0o600)
        if self.p(CONFIG).exists():
            self.f.check(self.p(CONFIG),0,0,0o600)
            existing=json.loads(regular_bytes(self.p(CONFIG),8192))
            if existing not in allowed: raise ValueError('Existing config differs; no overwrite')
        else: self.f.install(self.p(CONFIG),(json.dumps(config,indent=2)+'\n').encode(),0,0,0o600)
        pending=(release/'deploy/update-recovery.conf').read_text()+'\n[Service]\nExecStart=\nExecStart='+guard+'\n'
        self.f.install(self.p(STATE+'/pending-app-integration.conf'),pending.encode(),0,0,0o644)
        self.note(prepared=True,secretHash=hashlib.sha256(secret).hexdigest())
        if self.receipt.get('checkingEnabled') or self.receipt.get('autoStarting') or self.receipt.get('autoEnabled'):
            print('RESUME_PREPARED: existing verified configuration retained. Preparation performed no daemon reload/start/restart/enable; activation state will be checked next.')
        else:
            print('PREPARED: updater prerequisites installed; app/env/live key/current release unchanged. Updates disabled; no daemon reload/start/restart/enable was run.')
    def set_mode(self, enabled, mode):
        if mode=='auto' and not all(self.receipt.get(k) for k in ('backupVerified','offDeviceBackup','manualHealthy','ownerAcceptance')):
            raise ValueError('Automatic activation gates are incomplete')
        config=json.loads(regular_bytes(self.p(CONFIG),8192));config.update(enabled=enabled,mode=mode)
        if mode=='auto' and all(self.receipt.get(k) for k in ('backupVerified','offDeviceBackup','manualHealthy','ownerAcceptance')):
            config['allowUiModeControl']=True
            config['installVersionPin']=None
        self.f.replace_owned(self.p(CONFIG),(json.dumps(config,indent=2)+'\n').encode())
    def continue_guided(self):
        if self.receipt.get('autoEnabled'):
            self.s.health('0.3.2')
            for unit in ('yachad-update.timer','yachad-update-backup-expiry.timer'):
                self.s.run(['/usr/bin/systemctl','is-enabled','--quiet',unit]);self.s.run(['/usr/bin/systemctl','is-active','--quiet',unit])
            print('ALREADY_COMPLETE: automatic mode and timers verified; no changes.');return
        print('Next: approved maintenance window, backup/isolated restore drill, app integration/restarts, first stable 0.3.2 install.')
        print('After health + off-device backup + your acceptance checks, this guide will enable automatic updates as requested.')
        print('Retention: at most 7 encrypted update backups, maximum 7 days, with daily expiry. Keep your off-device copy separately.')
        if self.ask('Type MAINTENANCE to continue now; Enter stops safely before service-affecting actions: ')!='MAINTENANCE':
            print('PAUSED_BEFORE_MAINTENANCE. Run the same command later.');return
        if not self.receipt.get('backupVerified'): self.backup_drill()
        if not self.receipt.get('offDeviceBackup'):
            print('Copy the encrypted recovery backup off the Pi:',self.receipt['exportPath'])
            if self.ask('Type COPIED only after that backup and its passphrase are available off-device: ')!='COPIED':
                print('PAUSED_BEFORE_INSTALL. The original app was restarted.');return
            self.note(offDeviceBackup=True)
        if not self.receipt.get('manualHealthy'): self.first_install()
        self.s.health('0.3.2')
        print('Check parent/child sign-in, allowed groups, existing WhatsApp link and normal message/media behavior. Do not relink or rerun setup.')
        if self.ask('Type ACCEPTED only after those checks pass; Enter keeps automatic mode off: ')!='ACCEPTED':
            print('PAUSED_BEFORE_AUTO. Healthy manual install retained; timers remain disabled.');return
        self.note(ownerAcceptance=True)
        if not all(self.receipt.get(k) for k in ('backupVerified','offDeviceBackup','manualHealthy','ownerAcceptance')): raise ValueError('Acceptance gate incomplete')
        self.s.health('0.3.2')
        self.note(autoStarting=True)
        self.set_mode(True,'auto')
        self.s.run(['/usr/bin/systemctl','enable','--now','yachad-update.timer','yachad-update-backup-expiry.timer'])
        for unit in ('yachad-update.timer','yachad-update-backup-expiry.timer'):
            self.s.run(['/usr/bin/systemctl','is-enabled','--quiet',unit]);self.s.run(['/usr/bin/systemctl','is-active','--quiet',unit])
        self.note(autoEnabled=True,autoStarting=False)
        print('AUTO_READY: 0.3.2 healthy; automatic stable-release updates and backup expiry enabled. Designated parent can check/install and choose auto/notify in the UI.')
    def backup_drill(self):
        needed=sum(path.lstat().st_size for path in self.p(DATA).rglob('*') if path.is_file() and not path.is_symlink())
        if shutil.disk_usage(self.p(BACKUPS)).free < 4*needed+512*1024**2:
            raise ValueError('Insufficient free space for backup, immutable snapshot, restore drill and off-device export staging')
        secret=regular_bytes(self.p(SECRET),1024).strip()
        before=set(self.p(BACKUPS).glob('yachad-*.backup'))
        stop_attempted=False
        snapshot_parent=None
        try:
            stop_attempted=True
            self.s.run(['/usr/bin/systemctl','stop','yachad.service'])
            command=['/usr/bin/python3',HELPERS+'/data-lock.py',DATA,'/usr/local/bin/node','--import','tsx','scripts/update-backup.ts',DATA,KEY,BACKUPS]
            if self.s.run(command,user='yachad',cwd='/opt/yachad/current',input=secret,timeout=900).strip()!=b'BACKUP_OK': raise ValueError('Backup was not confirmed')
            self.s.run(['/usr/bin/sync','-f',BACKUPS])
            created=set(self.p(BACKUPS).glob('yachad-*.backup'))-before
            if len(created)!=1: raise ValueError('Cannot identify the new backup safely')
            archive=created.pop()
            # Snapshot through the app user's reader, so root never follows a path
            # controlled by the app. The root-owned immutable snapshot is shared
            # read-only with yachad for authenticated restore, then streamed out.
            snapshot_parent=pathlib.Path(tempfile.mkdtemp(prefix='drill-input-',dir=self.p(STATE)))
            self.f.own(snapshot_parent,0,self.s.app.pw_gid);snapshot_parent.chmod(0o750)
            snapshot=snapshot_parent/'archive.backup'
            reader='import os,stat,sys;fd=os.open(sys.argv[1],os.O_RDONLY|os.O_NOFOLLOW|os.O_NONBLOCK);s=os.fstat(fd);assert stat.S_ISREG(s.st_mode) and s.st_uid==os.geteuid() and not s.st_mode&0o077 and 0<s.st_size<=16*1024**3;f=os.fdopen(fd,"rb");left=s.st_size\nwhile left:\n b=f.read(min(left,65536));assert b;sys.stdout.buffer.write(b);left-=len(b)\nassert not f.read(1)'
            with snapshot.open('xb') as outgoing:
                self.f.own(snapshot,0,self.s.app.pw_gid);snapshot.chmod(0o640)
                self.s.run(['/usr/bin/python3','-c',reader,str(archive)],user='yachad',stdout_file=outgoing,timeout=900)
                outgoing.flush();os.fsync(outgoing.fileno())
            parent=self.s.run(['/usr/bin/python3','-c','import tempfile;print(tempfile.mkdtemp(prefix="restore-drill-",dir="'+BACKUPS+'"))'],user='yachad').decode().strip()
            if pathlib.PurePosixPath(parent).parent!=pathlib.PurePosixPath(BACKUPS) or not pathlib.PurePosixPath(parent).name.startswith('restore-drill-'): raise ValueError('Unexpected restore location')
            code="import {restoreBackup} from './scripts/backup-lib.ts';const chunks=[];for await(const b of process.stdin)chunks.push(b);await restoreBackup(process.argv[1],process.argv[2],Buffer.concat(chunks).toString().trim());console.log('RESTORE_OK');"
            try:
                result=self.s.run(['/usr/local/bin/node','--import','tsx','--input-type=module','-e',code,str(snapshot),parent+'/restored'],user='yachad',cwd='/opt/yachad/current',input=secret,timeout=900)
                if result.strip()!=b'RESTORE_OK': raise ValueError('Authenticated restore was not confirmed')
            finally:
                self.s.run(['/usr/bin/python3','-c','import shutil,sys;shutil.rmtree(sys.argv[1])',parent],user='yachad')
            # Export only the encrypted backup as the ordinary owner; privileged code never writes through owner-controlled paths.
            destination=str(self.s.home/('yachad-recovery-'+archive.name))
            export='import os,sys;fd=os.open(sys.argv[1],os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600);f=os.fdopen(fd,"wb");import shutil;shutil.copyfileobj(sys.stdin.buffer,f);f.flush();os.fsync(f.fileno());f.close()'
            with snapshot.open('rb') as incoming:
                self.s.run(['/usr/bin/python3','-c',export,destination],user=self.s.owner.pw_name,stdin_file=incoming,timeout=900)
            self.note(backupVerified=True,backupPath=str(archive),exportPath=destination)
        finally:
            try:
                if snapshot_parent: shutil.rmtree(snapshot_parent)
            finally:
                if stop_attempted:
                    self.s.run(['/usr/bin/systemctl','start','yachad.service']);self.s.health('0.2.0')
    def first_install(self):
        self.note(integrationStarting=True)
        folder=self.p('/etc/systemd/system/yachad.service.d')
        if not folder.exists(): self.f.directory(folder,0,0,0o755)
        else:
            info=self.f.info(folder)
            if not stat.S_ISDIR(info.st_mode) or info.st_uid!=0 or info.st_mode & 0o022: raise ValueError('Unsafe app drop-in directory')
        self.f.install(self.p(DROPIN),regular_bytes(self.p(STATE+'/pending-app-integration.conf'),8192),0,0,0o644)
        self.s.run(['/usr/bin/systemctl','daemon-reload'])
        expected=regular_bytes(self.p(STATE+'/pending-app-integration.conf'),8192).decode().split('ExecStart=')[-1].strip()
        effective=self.s.run(['/usr/bin/systemctl','show','yachad.service','--property=ExecStart,Requires,After','--no-pager']).decode()
        values=dict(line.split('=',1) for line in effective.splitlines() if '=' in line)
        actual=re.search(r'argv\[\]=(.*?)\s*;',values.get('ExecStart',''))
        if not actual or actual[1].strip()!=expected or 'yachad-update-recover.service' not in values.get('Requires','').split() or 'yachad-update-recover.service' not in values.get('After','').split():
            raise ValueError('Effective service guard/recovery differs; no restart/update was requested')
        self.s.run(['/usr/bin/systemctl','start','yachad-update-recover.service'])
        active_version=json.loads(regular_bytes(self.p('/opt/yachad/current/package.json'),8192))['version']
        if active_version=='0.2.0':
            self.s.run(['/usr/bin/systemctl','restart','yachad.service']);self.s.health('0.2.0')
            self.note(checkingStarting=True)
            self.set_mode(True,'notify');self.note(checkingEnabled=True,checkingStarting=False)
            if self.p(DATA+'/update-request.json').exists(): raise ValueError('Existing request must be inspected; it was not overwritten')
            self.s.run(['/usr/bin/systemctl','start','yachad-update.service'],timeout=2100)
            status=json.loads(regular_bytes(self.p(STATE+'/status.json'),8192))
            if status.get('phase')!='available' or status.get('latestVersion')!=verifier.VERSION: raise ValueError('Expected stable target is not available; no install request sent')
            request=json.dumps({'action':'install','familyId':self.receipt['operator']['familyId'],'userId':self.receipt['operator']['userId'],'createdAt':int(time.time()*1000)})
            writer='import os,sys;fd=os.open(sys.argv[1],os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600);os.write(fd,sys.argv[2].encode());os.fsync(fd);os.close(fd)'
            self.note(manualStarting=True)
            self.s.run(['/usr/bin/python3','-c',writer,DATA+'/update-request.json',request],user='yachad')
            # Existing service supervises worker termination and ExecStopPost recovery.
            self.s.run(['/usr/bin/systemctl','start','yachad-update.service'],timeout=2100)
        status=json.loads(regular_bytes(self.p(STATE+'/status.json'),8192))
        if status.get('phase')!='healthy' or status.get('currentVersion')!=verifier.VERSION: raise ValueError('Manual update did not commit expected healthy version; automatic mode remains off')
        self.s.health(verifier.VERSION)
        self.s.run(['/usr/bin/systemctl','restart','yachad.service']);self.s.health(verifier.VERSION)
        self.note(manualHealthy=True)


def main():
    if not MODE_CONTROL_RELEASE_READY:
        print('DRAFT_ONLY: stable v0.3.2 publication/read-back is pending. No changes were made.')
        return
    system=Linux();os.umask(0o077)
    print('This installs the existing updater component. It preserves the app/env/live data/key/Caddy and pauses before maintenance.')
    guide=Guide(system)
    # Root-only setup lock: never follow an existing link or trust a shared-file owner.
    import fcntl
    fd=os.open('/run/lock/yachad-updater-setup.lock',os.O_CREAT|os.O_RDWR|os.O_NOFOLLOW,0o600)
    try:
        info=os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid!=0 or info.st_mode & 0o077: raise ValueError('Unsafe setup lock')
        fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        guide.load_receipt()
        with tempfile.TemporaryDirectory(prefix='yachad-owner-verified-') as directory:
            guide.snapshot=pathlib.Path(directory)
            release,public=guide.verified_release(guide.snapshot)
            guide.prepare(release,public)
            guide.continue_guided()
    finally: os.close(fd)


if __name__=='__main__':
    try: main()
    except (Exception, KeyboardInterrupt):
        print('SETUP_STOPPED: no exception/command bodies or secrets logged. Preserve files and inspect the private setup receipt/updater status. If activation started, automatic mode/timers may already be partially configured; no success is claimed.',file=sys.stderr)
        sys.exit(1)
