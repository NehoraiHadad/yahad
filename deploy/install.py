#!/usr/bin/env python3
"""Guided owner installation. Linux runtime, also used by the Windows WSL adapter.
No application/package code runs as root. Run from an inspected release checkout.
"""
import argparse, base64, contextlib, fcntl, hashlib, importlib.util, ipaddress, json, os
import pathlib, pwd, re, secrets, shlex, shutil, sqlite3, ssl, stat, subprocess, sys, tempfile, time
import urllib.error, urllib.request
from urllib.parse import urlsplit

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('release_worker', HERE / 'updater.py')
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
UNITS = ('yachad-update.service','yachad-update.timer','yachad-update-recover.service',
         'yachad-update-request.service','yachad-update-request.path',
         'yachad-update-backup-expiry.service','yachad-update-backup-expiry.timer')
ENV = '/etc/yachad/yachad.env'
CONFIG = '/etc/yachad/updates.json'
RECEIPT = '/var/lib/yachad-updates/install.json'
# Exact aliases from reviewed 0.3.2 commit 3624743148c453f55c6b51ffb4051986f3e79cc0.
# Only identical current LF source may accept its known historical CRLF bytes.
# This is NOT a worker-version migration or general newline normalization.
HISTORICAL_CRLF = {
 'data-lock.py': ('6fa8a14c774a2f1474f341c26cbbedd81bf93e619b738c1262ebe613bc0f751b','a7f4db80f61f48ed478f28244ff0ce54dc212cb8dab163f42ce9443e45803439'),
 'export-dependencies.py': ('7bf1c19ecb9fd2843b346a4c04caf2d3bed279e1e4c436ad103d2b06dd1eacc8','64b75b505935408d56a1989392c631daa41b832d318ca7a3b1758500a4ac10ac'),
 'yachad-update.service': ('ec633bb8689c36ed105449cddd3697b78c8173014a82383d21693340346d88dc','72d3f0620771bb6ef5329d2bfca1af363e4142e688e038e88098d8afc2d45a89'),
 'yachad-update.timer': ('3abe4fe80608c994896b7c9468d49d6686fe4e7aef99ff7aeed3203614d3c07a','ea13aa9753dcc66bdee8e91e52fc5269d3db03f21e6dd50e9dc5f3f7f4b16f29'),
 'yachad-update-recover.service': ('94a3e56fb8ffa97d05f5bd5bee40bc15d0c47a910c42e2171402741e724703a0','cb9e829a0635d308c77b1bff009c32e65e536f221e69d9d5e07a7bcbc56ccb93'),
}

def same_managed_source(path, actual, expected):
    if actual == expected: return True
    target = pathlib.PurePosixPath(path)
    allowed_root = '/usr/local/lib/yachad-updater' if target.name.endswith('.py') else '/etc/systemd/system'
    aliases = HISTORICAL_CRLF.get(target.name)
    return (str(target.parent) == allowed_root and aliases is not None and
            hashlib.sha256(expected).hexdigest() == aliases[0] and hashlib.sha256(actual).hexdigest() == aliases[1])


class InstallError(Exception): pass
class InstallCanceled(InstallError): pass

def environment(content):
    result = {}
    for line in content.splitlines():
        if not line.strip() or line.lstrip().startswith('#'): continue
        match = re.fullmatch(r'([A-Z_]+)=(.*)', line)
        if not match or match[1] in result: raise InstallError('Nonstandard environment; existing file preserved')
        values = shlex.split(match[2], comments=False)
        if len(values) != 1: raise InstallError('Nonstandard environment; existing file preserved')
        result[match[1]] = values[0]
    return result

def origin(value):
    parsed = urlsplit(value)
    try: address = ipaddress.ip_address(parsed.hostname)
    except ValueError: raise InstallError('Use the private LAN IP shown by your router') from None
    if parsed.scheme != 'https' or not address.is_private or address.is_loopback or address.version != 4 or parsed.port not in (None,443) or parsed.path or parsed.query or parsed.fragment or parsed.username:
        raise InstallError('Expected https://PRIVATE_LAN_IPV4 with no path')
    return 'https://' + str(address)

def safe_paths(data, key):
    # Match the existing systemd writable boundary, without assuming a live/mock name.
    for value in (data,key):
        if not value.startswith('/') or '..' in pathlib.PurePosixPath(value).parts or not re.fullmatch(r'/[A-Za-z0-9_./-]+',value): raise InstallError('Unsupported local path')
    if not pathlib.PurePosixPath(data).is_relative_to('/var/lib/yachad') or data == '/var/lib/yachad':
        # Root store is supported for older installations too.
        if data != '/var/lib/yachad': raise InstallError('Data must stay inside the service writable root')
    if not pathlib.PurePosixPath(key).is_relative_to('/etc/yachad'): raise InstallError('Key must stay inside /etc/yachad')
    return data,key

class Linux:
    def run(self,args,**kwargs):
        try:
            result=subprocess.run(args,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=kwargs.pop('timeout',120),
                env={'PATH':'/usr/local/bin:/usr/bin:/bin','LANG':'C.UTF-8','HOME':'/root'},**kwargs)
        except (OSError,subprocess.TimeoutExpired): raise InstallError('Local command unavailable or timed out: '+pathlib.Path(args[0]).name) from None
        if result.returncode: raise InstallError('Local command failed: '+pathlib.Path(args[0]).name)
        return result.stdout
    def account(self,name):
        import grp
        try: user=pwd.getpwnam(name)
        except KeyError:
            self.run(['/usr/sbin/useradd','--system','--user-group','--no-create-home','--home-dir','/nonexistent','--shell','/usr/sbin/nologin',name])
            user=pwd.getpwnam(name)
        try: group=grp.getgrnam(name)
        except KeyError: raise InstallError('Existing service account needs its matching private group') from None
        groups=set(os.getgrouplist(name,user.pw_gid))
        if (user.pw_uid == 0 or user.pw_gid == 0 or user.pw_gid != group.gr_gid or groups != {user.pw_gid} or
                user.pw_shell not in ('/usr/sbin/nologin','/sbin/nologin','/bin/false')):
            raise InstallError('Existing service account is not isolated')
        return user

class Installer:
    def __init__(self,system,root=pathlib.Path('/'),ask=input,sleep=time.sleep):
        self.s=system; self.root=root; self.ask=ask; self.sleep=sleep; self.receipt={}
    def p(self,value): return self.root / value.lstrip('/')
    def secure(self,path,uid=0,mode=None,directory=False):
        info=path.lstat()
        if not (stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode)) or info.st_uid!=uid or (mode is not None and stat.S_IMODE(info.st_mode)!=mode):
            raise InstallError('Unexpected local file type/owner/mode')
        return info
    def secure_environment(self,path):
        info=self.secure(path)
        mode=stat.S_IMODE(info.st_mode)
        if mode==0o600:return
        # Historical installs used root:yachad 0640. Only that known service
        # group may retain read access; keys/backup secrets keep stricter checks.
        try: app_gid=pwd.getpwnam('yachad').pw_gid
        except KeyError: raise InstallError('Existing environment has no matching service account') from None
        if mode!=0o640 or info.st_gid!=app_gid or app_gid==0:
            raise InstallError('Existing environment must be root-only or root:yachad 0640; preserved')
    def ancestors(self,path):
        for item in (path,*path.parents):
            if item==self.root.parent and self.root!=pathlib.Path('/'): break
            if item.exists() or item.is_symlink():
                st=self.secure(item,directory=True)
                if st.st_mode & 0o022: raise InstallError('Writable privileged ancestor')
    def directory(self,value,uid=0,gid=0,mode=0o755):
        path=self.p(value); self.ancestors(path.parent)
        if path.exists() or path.is_symlink():
            self.secure(path,uid,mode,True)
        else: path.mkdir(mode=mode); os.chown(path,uid,gid); path.chmod(mode)
        return path
    def write(self,value,content,uid=0,gid=0,mode=0o644):
        path=self.p(value); self.ancestors(path.parent)
        if path.exists() or path.is_symlink():
            self.secure(path,uid,mode)
            if not same_managed_source(value,path.read_bytes(),content): raise InstallError('Existing managed file differs; reviewed helper migration is required, file preserved: '+value)
            return
        fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,mode)
        try:
            os.fchown(fd,uid,gid);os.fchmod(fd,mode);os.write(fd,content);os.fsync(fd)
        finally:os.close(fd)
    def note(self,**values):
        self.receipt.update(values); worker.atomic_json(self.p(RECEIPT),self.receipt)
    def load(self):
        if self.p(RECEIPT).exists():
            self.secure(self.p(RECEIPT),mode=0o600)
            self.receipt=json.loads(self.p(RECEIPT).read_bytes())
    def paths(self,new_origin=None):
        if self.p(ENV).exists():
            self.secure_environment(self.p(ENV))
            env=environment(self.p(ENV).read_text())
            safe_paths(env.get('DATA_DIR',''),env.get('MASTER_KEY_FILE',''))
            origin(env.get('APP_ORIGIN',''))
            return env,False
        if not new_origin: raise InstallError('Fresh installation needs --origin https://PRIVATE_LAN_IP')
        return dict(DATA_DIR='/var/lib/yachad/store',MASTER_KEY_FILE='/etc/yachad/master.key',APP_ORIGIN=origin(new_origin),
                    COOKIE_SECURE='true',CONNECTOR='mock',ALLOW_REAL_WHATSAPP='no',HOST='127.0.0.1',PORT='3000',
                    UPDATE_STATUS_FILE='/var/lib/yachad-updates/status.json',SETUP_CAPABILITY_FILE='/etc/yachad/setup-capability.json'),True
    def prerequisites(self):
        # Read-only, grouped diagnostics before downloads, confirmation or provisioning.
        missing=[]
        try:
            if self.s.run(['/usr/bin/cat','/proc/1/comm']).strip()!=b'systemd':
                missing.append('systemd is not running. In WSL, enable systemd in the selected distribution using docs/INSTALL.md; then reopen it.')
        except InstallError:
            missing.append('Cannot check systemd. Use a supported Debian/Ubuntu Linux terminal (Windows requires WSL2).')
        node=shutil.which('node'); npm=shutil.which('npm')
        if not node:
            missing.append('Node 24 is missing. Install Node 24 LTS inside this Linux/WSL distribution, available to system services.')
        else:
            try:
                if not self.s.run([node,'--version']).startswith(b'v24.'):
                    missing.append('Node has the wrong version. Use Node 24 LTS, not another major version.')
            except InstallError:missing.append('Node could not start. Repair the Node 24 installation inside this Linux/WSL distribution.')
        if not npm:missing.append('npm is missing. Install npm alongside Node 24 inside this Linux/WSL distribution.')
        requirements=[('ffmpeg','-version','ffmpeg'),('openssl','version','OpenSSL')]
        # Existing HTTPS arrangements are preserved, not replaced with Caddy.
        config_dir=self.p('/etc/yachad')
        # An ordinary owner cannot traverse the protected existing config directory.
        # Check accessibility before probing ENV; privileged run() still validates
        # its actual files/ownership through load() and paths().
        if (not config_dir.exists() or os.access(config_dir,os.R_OK|os.X_OK)) and not self.p(ENV).exists():
            requirements.append(('caddy','version','Caddy (fresh-install HTTPS)'))
        for command,argument,label in requirements:
            try:self.s.run([command,argument])
            except InstallError:missing.append(label+' is missing or cannot start. Install/repair it inside this Linux/WSL distribution.')
        if missing:
            raise InstallError('NOT READY — no installation changes made. Fix these prerequisites first:\n- '+'\n- '.join(missing)+'\nSee docs/INSTALL.md. This check does not install packages, enable WSL/systemd, or change security settings.')
        return node,npm
    def active(self,env):
        data=self.p(env['DATA_DIR']);key=self.p(env['MASTER_KEY_FILE'])
        self.secure(data,self.app.pw_uid,0o700,True);self.secure(key,self.app.pw_uid,0o600)
        link=self.p('/opt/yachad/current')
        if not link.is_symlink(): raise InstallError('Existing current release must be a symlink')
        target=link.resolve(strict=True)
        if not target.is_relative_to(self.p('/opt/yachad/releases').resolve()) or target==self.p('/opt/yachad/releases').resolve(): raise InstallError('Release path is outside managed releases')
        self.ancestors(target)
        version=json.loads((target/'package.json').read_bytes())['version'];worker.version(version)
        return version
    def parent(self,env):
        path=self.p(env['DATA_DIR'])/'app.sqlite'
        if not path.exists(): return None
        self.secure(path,self.app.pw_uid)
        with sqlite3.connect(path.as_uri()+'?mode=ro',uri=True) as db:
            rows=db.execute('SELECT version FROM schema_version').fetchall()
            if len(rows)!=1: raise InstallError('Database schema is not supported')
            worker.schema_id(rows[0][0])
            rows=db.execute("SELECT family_id,id FROM users WHERE role='parent' ORDER BY id").fetchall()
        if not rows:return None
        old=self.receipt.get('operator') or getattr(self,'config_value',{}).get('operator')
        if old=={'familyId':'REPLACE_WITH_FAMILY_UUID','userId':'REPLACE_WITH_PARENT_UUID'}:old=None
        if old:
            if (old['familyId'],old['userId']) not in rows:raise InstallError('Designated parent changed')
            return old
        if len(rows)!=1:raise InstallError('Multiple parents require explicit local operator selection; existing accounts preserved')
        return dict(familyId=rows[0][0],userId=rows[0][1])
    def config(self,env,node,npm):
        value=json.loads((HERE/'updates.json.example').read_bytes())
        value.update(dataDirectory=env['DATA_DIR'],masterKeyFile=env['MASTER_KEY_FILE'],node=node,npm=npm)
        path=self.p(CONFIG)
        if path.exists() or path.is_symlink():
            self.secure(path,mode=0o600);existing=json.loads(path.read_bytes())
            worker.release_policy(existing)
            if any(existing.get(k)!=value[k] for k in ('dataDirectory','masterKeyFile','currentLink','releasesDirectory','repository','appUser','stateDirectory','backupDirectory','buildDirectory','service')):
                raise InstallError('Existing updater paths differ; no configuration changed')
            # Keep signature trust, tokens, operator and all existing policy fields.
            return existing
        if self.p('/etc/yachad/release-signing.pub').exists():raise InstallError('Existing signature trust needs its matching updater configuration')
        return value
    def helper_files(self):
        files={'/usr/local/lib/yachad-updater/'+name:(HERE/name).read_bytes() for name in ('updater.py','data-lock.py','export-dependencies.py')}
        files.update({'/etc/systemd/system/'+name:(HERE/name).read_bytes() for name in UNITS})
        if any(b'\r' in value for value in files.values()):
            raise InstallError('Installer source is not canonical LF; use the verified release ZIP, not a converted Windows checkout')
        return files
    def verify_managed(self,files,allow_missing=False):
        for value,expected in files.items():
            path=self.p(value);self.ancestors(path.parent)
            if allow_missing and not path.exists() and not path.is_symlink():continue
            if not path.exists():raise InstallError('Managed update component missing; owner repair required: '+value)
            self.secure(path,mode=0o644)
            if not same_managed_source(value,path.read_bytes(),expected):
                raise InstallError('Existing managed file differs; reviewed helper migration is required, file preserved: '+value)
    def install_helpers(self):
        files=self.helper_files()
        self.verify_managed(files,allow_missing=True)
        self.directory('/usr/local/lib/yachad-updater')
        for path,content in files.items():self.write(path,content)
    def boundary_files(self,config,env):
        safe_paths(config['dataDirectory'],env['MASTER_KEY_FILE'])
        if config['dataDirectory']!=env['DATA_DIR']:raise InstallError('Request queue path differs from the app data directory')
        protected=[ENV,env['MASTER_KEY_FILE'],config['backupPassphraseFile']]
        protected += [config[k] for k in ('publicKeyFile','tokenFile') if config.get(k)]
        if any(not re.fullmatch(r'/[A-Za-z0-9_./-]+',p) or '..' in pathlib.PurePosixPath(p).parts for p in protected):
            raise InstallError('Nonstandard protected path; no service sandbox changes made')
        content='[Service]\nReadWritePaths=/etc/yachad\nReadOnlyPaths='+' '.join(protected)+'\n'
        files={'/etc/systemd/system/'+unit+'.d/40-mode-control.conf':content.encode() for unit in ('yachad-update.service','yachad-update-request.service')}
        watch='[Path]\nPathChanged=\nPathExists=\nPathChanged='+config['dataDirectory']+'/update-request.json\nPathExists='+config['dataDirectory']+'/update-request.wake\n'
        files['/etc/systemd/system/yachad-update-request.path.d/40-data-directory.conf']=watch.encode()
        latch='[Service]\nExecStartPre=\nExecStartPre=/usr/bin/rm -f -- '+config['dataDirectory']+'/update-request.wake\n'
        files['/etc/systemd/system/yachad-update-request.service.d/40-data-directory.conf']=latch.encode()
        return files
    def mode_control_boundary(self,config,env):
        files=self.boundary_files(config,env)
        for directory in sorted({str(pathlib.PurePosixPath(path).parent) for path in files}):self.directory(directory)
        for path,content in files.items():self.write(path,content)
    def verify_effective_updates(self,config,env,allow_missing=False):
        boundaries=self.boundary_files(config,env)
        for unit in UNITS:
            raw=self.s.run(['/usr/bin/systemctl','show',unit,'--property=LoadState,FragmentPath,DropInPaths,NeedDaemonReload','--no-pager']).decode()
            values=dict(line.split('=',1) for line in raw.splitlines() if '=' in line)
            if allow_missing and values.get('LoadState')=='not-found':continue
            expected={path for path in boundaries if '/'+unit+'.d/' in path}
            actual=set(values.get('DropInPaths','').split())
            # systemd resolves inherited/global/runtime/vendor overrides here too.
            if (values.get('LoadState')!='loaded' or values.get('FragmentPath')!='/etc/systemd/system/'+unit or
                    values.get('NeedDaemonReload')!='no' or (not actual<=expected if allow_missing else actual!=expected)):
                raise InstallError('Effective updater unit differs or needs reload; owner review required, preserved: '+unit)
            self.verify_managed({path:boundaries[path] for path in actual})
    def verify_update_state(self,config):
        state=self.p(config['stateDirectory'])/'status.json'
        if state.exists():
            self.secure(state)
            if json.loads(state.read_bytes()).get('phase') not in ('idle','healthy','available','incompatible','failed','rolled_back'):
                raise InstallError('Updater unfinished state must be recovered before installer changes')
    def verify_completed_updates(self,config,env):
        self.verify_managed({**self.helper_files(),**self.boundary_files(config,env)})
        self.verify_effective_updates(config,env)
        if not config.get('enabled'):
            return 'update policy disabled (preserved); component bytes verified'
        for unit in ('yachad-update-request.path','yachad-update.timer','yachad-update-backup-expiry.timer'):
            try:
                self.s.run(['/usr/bin/systemctl','is-active','--quiet',unit])
                self.s.run(['/usr/bin/systemctl','is-enabled','--quiet',unit])
            except InstallError:
                raise InstallError('Update component inactive or disabled; preference preserved, owner repair required: '+unit) from None
        return 'request path and timers active/enabled; mode='+config['mode']+' (preserved)'
    def ensure_boot_guard(self,config,env,version,fd):
        before=self.p(CONFIG).read_bytes() if self.p(CONFIG).exists() else None
        # The recovery unit acquires the same kernel lock. Start it before
        # starting the app, while temporarily yielding this installer's lock.
        fcntl.flock(fd,fcntl.LOCK_UN)
        try:self.s.run(['/usr/bin/systemctl','start','yachad-update-recover.service'])
        finally:fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
        after=self.p(CONFIG).read_bytes() if self.p(CONFIG).exists() else None
        if before!=after:raise InstallError('Updater policy changed during recovery-unit startup; preserved')
        self.s.run(['/usr/bin/systemctl','is-active','--quiet','yachad-update-recover.service'])
        if not self.receipt.get('newInstall') and self.active(env)!=version:raise InstallError('Active release changed during guard startup')
        state=self.p(config['stateDirectory'])/'status.json'
        if state.exists() and json.loads(state.read_bytes()).get('phase') in worker.PHASES:raise InstallError('Recovery is still pending')
    def verify_guard(self,env):
        raw=self.s.run(['/usr/bin/systemctl','show','yachad.service','--property=User,Group,WorkingDirectory,ExecStart,EnvironmentFiles,Requires,After','--no-pager']).decode()
        values=dict(line.split('=',1) for line in raw.splitlines() if '=' in line)
        expected='/usr/bin/python3 /usr/local/lib/yachad-updater/data-lock.py ${DATA_DIR} /usr/bin/env node --import tsx src/main.ts'
        command=re.search(r'argv\[\]=(.*?)\s*;',values.get('ExecStart',''))
        if not command or command[1].strip()!=expected or values.get('User')!='yachad' or values.get('Group')!='yachad' or values.get('WorkingDirectory')!='/opt/yachad/current' or values.get('EnvironmentFiles')!='/etc/yachad/yachad.env (ignore_errors=no)' or any('yachad-update-recover.service' not in values.get(k,'').split() for k in ('Requires','After')):
            raise InstallError('Effective service configuration differs; inspect local drop-ins before continuing')
    def fresh_release(self,config):
        w=worker.Worker(config)
        manifest,archive=self.release_input
        with tempfile.TemporaryDirectory(dir=self.p(config['stateDirectory'])) as temp:
            path=pathlib.Path(temp);(path/'app.zip').write_bytes(archive);source=path/'source';source.mkdir();worker.extract(path/'app.zip',source)
            if json.loads((source/'package.json').read_bytes()).get('version')!=manifest['version'] or not (source/'src/bootstrap.ts').is_file():raise InstallError('Release does not contain the expected first-run support')
            destination=self.p(config['releasesDirectory'])/('v'+manifest['version'])
            stamp=destination/'.installer-archive-sha256'
            if destination.exists():
                self.ancestors(destination)
                if not stamp.is_file() or stamp.read_text()!=manifest['sha256']:raise InstallError('Unfinished release preparation preserved; inspect before retry')
                link=self.p(config['currentLink'])
                if not link.exists() and not link.is_symlink():link.symlink_to(destination,target_is_directory=True)
                return manifest['version']
            build=pathlib.Path(tempfile.mkdtemp(prefix='install-',dir=self.p(config['buildDirectory'])))
            try:
                shutil.copytree(source,build,dirs_exist_ok=True)
                for item in [*build.rglob('*'),build]:
                    os.chown(item,self.builder.pw_uid,self.builder.pw_gid,follow_symlinks=False);os.chmod(item,0o700 if item.is_dir() else 0o600)
                w.command([config['npm'],'ci','--omit=dev','--ignore-scripts','--no-audit','--no-fund'],user=config['builderUser'],cwd=build,timeout=1200,code='build')
                w.command([config['node'],'--import','tsx','scripts/update-smoke.ts','--database-schema',str(manifest['databaseSchema']),
                           '--database-migrations-from',json.dumps(manifest['databaseMigrationsFrom'],separators=(',',':'))],user=config['builderUser'],cwd=build,timeout=90,code='build')
                deps=w.command(['/usr/bin/python3','/usr/local/lib/yachad-updater/export-dependencies.py',str(build)],user=config['builderUser'],code='build')
                shutil.copytree(source,destination);worker.extract_dependencies(deps,destination)
                (destination/worker.RELEASE_METADATA).write_bytes((json.dumps(manifest,separators=(',',':'))+'\n').encode())
                for item in [destination,*destination.rglob('*')]:
                    executable=bool(item.stat().st_mode & 0o111)
                    os.chown(item,0,self.app.pw_gid,follow_symlinks=False);os.chmod(item,0o750 if item.is_dir() or executable else 0o640)
                stamp.write_text(manifest['sha256']);stamp.chmod(0o640)
            finally:shutil.rmtree(build,ignore_errors=True)
        link=self.p(config['currentLink']);link.symlink_to(destination,target_is_directory=True);worker.sync_directory(link.parent)
        return manifest['version']
    def fetch_fresh(self):
        # Check the stable release BEFORE provisioning anything. Published 0.3.2
        # remains immutable and deliberately cannot claim this new capability.
        probe=worker.Worker.__new__(worker.Worker)
        probe.c={'repository':'NehoraiHadad/yahad','releaseTrust':'github-https','publicKeyFile':None,'tokenFile':None};probe.status={}
        release,assets=probe.release(None)
        raw=worker.download(assets['yachad-release.json']['url'],8192)
        archive=worker.download(assets['yachad-app.zip']['url'],64*1024*1024)
        manifest=worker.verify_manifest(raw,archive)
        if worker.version(manifest['version'])!=worker.version(release['tag_name']) or worker.version(manifest['version'])<(0,4,0) or manifest['format']!=2:
            raise InstallError('Publish the reviewed format-2 installer release first; no provisioning was performed')
        self.release_input=(manifest,archive)
    def bootstrap(self,env,version):
        if self.parent(env):return
        capability=dict(token=secrets.token_urlsafe(32),expiresAt=int(time.time()*1000)+15*60_000)
        path=self.p('/etc/yachad/setup-capability.json')
        if path.exists():
            self.secure(path,mode=0o640)
            path.unlink() # Expired ephemeral setup capability only; never an account credential.
        self.write('/etc/yachad/setup-capability.json',(json.dumps(capability)+'\n').encode(),gid=self.app.pw_gid,mode=0o640)
        self.s.run(['/usr/bin/systemctl','restart','yachad.service']);worker.Worker(self.config_value).health(version)
        if self.receipt.get('newInstall'):self.web_health(env,'ready')
        print('Open this private setup link in your browser (15 minutes; do not share it):')
        print(env['APP_ORIGIN']+'/#setup='+capability['token'])
        print('Create the parent in the browser. The application keeps running if this installer is interrupted.')
        deadline=time.monotonic()+15*60
        while time.monotonic()<deadline:
            operator=self.parent(env)
            if operator:
                path.unlink(missing_ok=True);return
            self.sleep(2)
        raise InstallError('Setup link expired. Run this same installer again for a fresh local link')
    def web(self,env):
        host=urlsplit(env['APP_ORIGIN']).hostname
        config=('{'+'\n admin off\n auto_https disable_redirects\n skip_install_trust\n}\n'+env['APP_ORIGIN']+' {\n bind '+host+'\n tls internal\n respond /health/* 404\n reverse_proxy 127.0.0.1:3000\n}\n').encode()
        self.s.run(['caddy','version'])
        self.write('/etc/caddy/yachad.Caddyfile',config,gid=pwd.getpwnam('caddy').pw_gid,mode=0o640)
        self.s.run(['caddy','validate','--config','/etc/caddy/yachad.Caddyfile','--adapter','caddyfile'])
        unit=b'[Unit]\nDescription=Yachad private LAN HTTPS\nAfter=network-online.target\n[Service]\nUser=caddy\nGroup=caddy\nExecStart=/usr/bin/caddy run --config /etc/caddy/yachad.Caddyfile --adapter caddyfile\nRestart=on-failure\nEnvironment=HOME=/var/lib/caddy\nAmbientCapabilities=CAP_NET_BIND_SERVICE\nNoNewPrivileges=true\nProtectSystem=strict\nReadWritePaths=/var/lib/caddy\n[Install]\nWantedBy=multi-user.target\n'
        self.write('/etc/systemd/system/yachad-web.service',unit)
    def web_health(self,env,expected):
        # Verify the same LAN origin the browser uses, anchored to this managed
        # Caddy instance's public CA. This never changes OS/browser trust stores.
        url=origin(env['APP_ORIGIN'])+'/api/setup/status'
        ca=self.p('/var/lib/caddy/.local/share/caddy/pki/authorities/local/root.crt')
        caddy_uid=pwd.getpwnam('caddy').pw_uid
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self,*args,**kwargs):return None
        deadline=time.monotonic()+90
        while time.monotonic()<deadline:
            try:
                self.s.run(['/usr/bin/systemctl','is-active','--quiet','yachad-web.service'])
                info=ca.lstat()
                if not stat.S_ISREG(info.st_mode) or info.st_uid not in (0,caddy_uid) or info.st_mode&0o022 or not 0<info.st_size<=65536:
                    raise InstallError('Unsafe local Caddy CA certificate')
                context=ssl.create_default_context(cafile=str(ca))
                opener=urllib.request.build_opener(urllib.request.ProxyHandler({}),NoRedirect(),urllib.request.HTTPSHandler(context=context))
                with opener.open(url,timeout=3) as response:
                    raw=response.read(1025)
                    if response.status==200 and len(raw)<=1024 and json.loads(raw)=={'state':expected}:return
            except (InstallError,OSError,ValueError,urllib.error.URLError):pass
            self.sleep(2)
        raise InstallError('HTTPS readiness failed; check the chosen LAN IP, yachad-web service and local Caddy CA. Certificate verification was not bypassed')
    def resume_backup(self,config,env,version):
        # Called only after run() owns the worker lock and verifies the recovery
        # guard. A killed installer cannot run finish()'s service-start finally.
        if not self.receipt.get('backupStarting'):return
        if self.active(env)!=version:raise InstallError('Active release changed during interrupted backup; preserved')
        state=self.s.run(['/usr/bin/systemctl','show','yachad.service','--property=ActiveState','--value']).strip()
        if state not in (b'active',b'inactive',b'failed'):
            raise InstallError('Application is transitioning; retry after it settles')
        if state!=b'active':self.s.run(['/usr/bin/systemctl','start','yachad.service'])
        worker.Worker(config).health(version)
        self.note(backupStarting=False)
    def finish(self,config,env,version):
        operator=self.parent(env)
        if not operator:raise InstallError('Create the first parent in the browser before finishing')
        old=config.get('operator')
        if old and old!={'familyId':'REPLACE_WITH_FAMILY_UUID','userId':'REPLACE_WITH_PARENT_UUID'} and old!=operator:raise InstallError('Existing operator differs')
        if self.receipt.get('configHash') and hashlib.sha256(self.p(CONFIG).read_bytes()).hexdigest()!=self.receipt['configHash']:
            raise InstallError('Updater configuration changed during setup; preserved')
        self.verify_update_state(config)
        self.verify_effective_updates(config,env)
        w=worker.Worker(config);w.health(version)
        if not self.receipt.get('backupVerified'):
            self.note(backupStarting=True)
            try:
                self.s.run(['/usr/bin/systemctl','stop','yachad.service'])
                result=w.command(['/usr/bin/python3','/usr/local/lib/yachad-updater/data-lock.py',env['DATA_DIR'],config['node'],'--import','tsx','scripts/update-backup.ts',env['DATA_DIR'],env['MASTER_KEY_FILE'],config['backupDirectory'],'--verify'],user='yachad',cwd=self.p('/opt/yachad/current'),input=worker.private_file(self.p(config['backupPassphraseFile'])).strip(),timeout=900,code='backup')
                if result.strip()!=b'BACKUP_OK':raise InstallError('Backup and isolated restore were not confirmed')
                self.note(backupVerified=True)
            finally:
                self.s.run(['/usr/bin/systemctl','start','yachad.service']);w.health(version)
                self.note(backupStarting=False)
        if self.receipt.get('newInstall'):self.web_health(env,'complete')
        config.update(operator=operator)
        if self.receipt.get('newInstall'):
            config.update(enabled=True,mode='auto',allowUiModeControl=True,installVersionPin=None)
        self.note(autoStarting=True,operator=operator)
        worker.atomic_json(self.p(CONFIG),config)
        self.note(configHash=hashlib.sha256(self.p(CONFIG).read_bytes()).hexdigest())
        w=worker.Worker(config);w.save(phase='healthy',currentVersion=version,error=None)
        units=('yachad.service','yachad-update-request.path','yachad-update.timer','yachad-update-backup-expiry.timer') if self.receipt.get('newInstall') else ()
        for unit in units:
            self.s.run(['/usr/bin/systemctl','enable','--now',unit]);self.s.run(['/usr/bin/systemctl','is-active','--quiet',unit]);self.s.run(['/usr/bin/systemctl','is-enabled','--quiet',unit])
        self.note(complete=True,autoStarting=False)
        detail='automatic updates and parent UI control are active' if self.receipt.get('newInstall') else 'existing update policy, version pin and service enablement preserved; no automatic activation performed'
        print('COMPLETE: healthy app; '+detail+'. Local encrypted backups: at most 7, kept at most 7 days. Device loss can also lose local backups. Keep an off-device copy if you want disaster recovery.')
    def run(self,new_origin=None):
        self.load()
        node,npm=self.prerequisites();env,fresh=self.paths(new_origin)
        fresh=fresh or bool(self.receipt.get('newInstall') and not self.receipt.get('complete'))
        if fresh and not self.p('/opt/yachad/current').is_symlink():self.fetch_fresh()
        print('This installs app/updater service components. Fresh installs enable automatic stable releases after health checks; existing update policy, version pin and service enablement are preserved. Data, keys, environment, Caddy CA and signature policy are preserved. A short service interruption may be needed.')
        if self.ask('Continue? [y/N] ').strip().lower()!='y':raise InstallCanceled('CANCELED: no changes made')
        self.app=self.s.account('yachad');self.builder=self.s.account('yachad-update')
        if self.app.pw_uid==self.builder.pw_uid or self.app.pw_gid==self.builder.pw_gid:
            raise InstallError('Application and builder accounts must have separate users and groups')
        if not fresh:
            version=self.active(env)
            if worker.version(version)<(0,4,0):raise InstallError('Upgrade the app to 0.4.0+ with the existing updater before using the new installer')
        for value,uid,gid,mode in (('/var/lib/yachad',0,self.app.pw_gid,0o755),('/etc/yachad',0,self.app.pw_gid,0o750),('/opt/yachad',0,self.app.pw_gid,0o750),('/opt/yachad/releases',0,self.app.pw_gid,0o750),('/var/lib/yachad-updates',0,self.app.pw_gid,0o750),('/var/lib/yachad-update-build',0,0,0o755),('/var/backups/yachad-updates',self.app.pw_uid,self.app.pw_gid,0o700)):
            # Existing shared data root may have a stricter mode; don't relax it.
            if value=='/var/lib/yachad' and self.p(value).exists():
                st=self.p(value).lstat()
                if not stat.S_ISDIR(st.st_mode) or st.st_uid not in (0,self.app.pw_uid) or st.st_mode&0o022:raise InstallError('Unsafe data root')
                self.ancestors(self.p(value).parent);continue
            self.directory(value,uid,gid,mode)
        lock=self.p('/var/lib/yachad-updates/worker.lock')
        fd=os.open(lock,os.O_RDWR|os.O_CREAT|os.O_NOFOLLOW|os.O_NONBLOCK,0o600)
        try:
            info=os.fstat(fd)
            if not stat.S_ISREG(info.st_mode) or info.st_uid!=0 or info.st_mode&0o077:raise InstallError('Unsafe updater lock')
            fcntl.flock(fd,fcntl.LOCK_EX|fcntl.LOCK_NB)
            config=self.config(env,node,npm);self.config_value=config
            self.verify_update_state(config)
            self.verify_effective_updates(config,env,allow_missing=True)
            if self.receipt.get('complete'):
                self.verify_guard(env);worker.Worker(config).health(self.active(env))
                if self.receipt.get('newInstall'):self.web_health(env,'complete')
                detail=self.verify_completed_updates(config,env)
                print('ALREADY_COMPLETE: healthy app; '+detail);return
            self.directory('/var/lib/yachad-update-build/home',self.builder.pw_uid,self.builder.pw_gid,0o700)
            self.install_helpers()
            self.mode_control_boundary(config,env)
            if fresh:
                self.note(newInstall=True)
                self.directory(env['DATA_DIR'],self.app.pw_uid,self.app.pw_gid,0o700)
                if self.p(env['MASTER_KEY_FILE']).exists():self.secure(self.p(env['MASTER_KEY_FILE']),self.app.pw_uid,0o600)
                else:self.write(env['MASTER_KEY_FILE'],base64.b64encode(secrets.token_bytes(32))+b'\n',self.app.pw_uid,self.app.pw_gid,0o600)
                self.write(ENV,(''.join(k+'='+v+'\n' for k,v in env.items())).encode(),mode=0o600)
                version=self.active(env) if self.p('/opt/yachad/current').is_symlink() else self.fresh_release(config)
                self.web(env)
                self.write('/etc/systemd/system/yachad.service',(HERE/'yachad.service').read_bytes())
                self.directory('/etc/systemd/system/yachad.service.d')
                self.write('/etc/systemd/system/yachad.service.d/update-recovery.conf',(HERE/'update-recovery.conf').read_bytes())
            self.directory('/etc/systemd/system/yachad.service.d')
            integration=(HERE/'update-recovery.conf').read_text()+'\n[Service]\nEnvironment=UPDATE_STATUS_FILE=/var/lib/yachad-updates/status.json\nExecStart=\nExecStart=/usr/bin/python3 /usr/local/lib/yachad-updater/data-lock.py ${DATA_DIR} /usr/bin/env node --import tsx src/main.ts\n'
            self.write('/etc/systemd/system/yachad.service.d/50-guided-install.conf',integration.encode())
            self.note(prepared=True,version=version)
            secret=self.p(config['backupPassphraseFile'])
            if secret.exists():self.secure(secret,mode=0o600)
            else:self.write(config['backupPassphraseFile'],secrets.token_urlsafe(48).encode()+b'\n',mode=0o600)
            self.s.run(['/usr/bin/systemctl','daemon-reload'])
            self.verify_effective_updates(config,env)
            self.ensure_boot_guard(config,env,version,fd)
            self.verify_guard(env)
            self.resume_backup(config,env,version)
            if fresh:self.s.run(['/usr/bin/systemctl','enable','--now','yachad-web.service'])
            if not self.receipt.get('integrationHealthy'):
                self.s.run(['/usr/bin/systemctl','restart','yachad.service']);worker.Worker(config).health(version)
                self.note(integrationHealthy=True)
            self.bootstrap(env,version)
            self.finish(config,env,version)
        except BlockingIOError:raise InstallError('Updater is busy; try again after it finishes') from None
        finally:os.close(fd)

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--origin')
    parser.add_argument('--check-only',action='store_true',help='Check local prerequisites without sudo, downloads or installation changes')
    args=parser.parse_args()
    if args.check_only:
        if args.origin:origin(args.origin)
        Installer(Linux()).prerequisites()
        print('PREREQUISITES_OK: local tools are available. No installation changes made. Network, existing-install safety, LAN access and certificate trust still require installation/owner checks.')
        return
    if os.geteuid()!=0 or not sys.stdin.isatty():raise InstallError('Run using sudo in the local owner terminal')
    Installer(Linux()).run(args.origin)
if __name__=='__main__':
    try:main()
    except InstallCanceled as error:print(str(error));sys.exit(2)
    except KeyboardInterrupt:print('PAUSED: app/setup state retained; rerun the same command to continue',file=sys.stderr);sys.exit(1)
    except Exception as error:
        print(str(error) if isinstance(error,InstallError) else 'INSTALL_STOPPED: inspect local service health; no secret or subprocess output logged',file=sys.stderr);sys.exit(1)
