"""Seal a locally built SCM host; never installs/starts a service or opens a listener."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import zipfile
from . import pe


def digest(path, algorithm='sha256'):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def build(published, runtime_package, source_commit, repo, output, verification, manifest_output, proposal_output):
    published, runtime_package, repo, output, verification, manifest_output, proposal_output = (
        Path(p).resolve() for p in (published, runtime_package, repo, output, verification, manifest_output, proposal_output))
    if output.exists() or verification.exists() or manifest_output.exists() or proposal_output.exists():
        raise ValueError('New output paths required')
    if published.parent != repo/'installer/artifacts' or published.name != 'Yahad.ServiceHost-'+source_commit[:7]:
        raise ValueError('Explicit source-bound local publication required')
    runtime = json.loads((published/'Yahad.ServiceHost.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions']
    if runtime.get('framework') or runtime.get('frameworks') or runtime.get('includedFrameworks') != [{'name':'Microsoft.NETCore.App','version':'10.0.14'}]:
        raise ValueError('Private self-contained runtime required')
    package = runtime_package/'microsoft.netcore.app.runtime.win-x64.10.0.14.nupkg'
    observed_sha512 = base64.b64encode(bytes.fromhex(digest(package,'sha512'))).decode()
    if observed_sha512 != (runtime_package/(package.name+'.sha512')).read_text().strip():
        raise ValueError('Cached runtime package checksum differs')
    legal=published/'legal';legal.mkdir(exist_ok=False)
    for source,name in [(repo/'LICENSE','Yahad-LICENSE.txt'),(runtime_package/'LICENSE.TXT','DotNet-Runtime-LICENSE.txt'),
                        (runtime_package/'THIRD-PARTY-NOTICES.TXT','DotNet-Runtime-ThirdPartyNotices.txt')]:
        shutil.copyfile(source,legal/name)
    records=[];modules={}
    for path in sorted(published.rglob('*')):
        if path.is_symlink() or getattr(path.lstat(),'st_file_attributes',0)&0x400:raise ValueError('Linked publication')
        if path.is_dir():continue
        name=path.relative_to(published).as_posix()
        if path.stat().st_nlink!=1 or len(name)>200 or path.suffix.lower() in ('.sqlite','.key','.pem','.backup','.pdb'):
            raise ValueError('Unexpected/private publication member')
        records.append(dict(path=name,bytes=path.stat().st_size,sha256=digest(path)))
        if path.suffix.lower() in ('.exe','.dll'):modules[name]=pe.inspect(path)
    names={r['path'].casefold() for r in records}
    for name,module in modules.items():
        for imported in module['imports']:
            sibling=(str(Path(name).parent/imported).replace('\\','/')).removeprefix('./').casefold()
            if not pe.is_system(imported) and sibling not in names:raise ValueError('Missing private native import')
    deps=json.loads((published/'Yahad.ServiceHost.deps.json').read_text())
    target=deps['runtimeTarget']['name']
    if not target.endswith('/win-x64'):raise ValueError('Runtime architecture')
    for library in deps['targets'][target].values():
        for kind in ('runtime','native'):
            for name in library.get(kind,{}):
                if name.rsplit('/',1)[-1].casefold() not in names:raise ValueError('Missing CLR asset')
    executable=next(r for r in records if r['path']=='Yahad.ServiceHost.exe')
    artifact=dict(schema=1,kind='windows-scm-host-development',sourceCommit=source_commit,target='win-x64',
        familyInstallationReady=False,releaseReady=False,releaseSignatureVerified=False,serviceInstalled=False,
        activationAuthorityConfigured=False,entryArguments=['--service'],executable=executable,files=records,
        privateRuntime='Microsoft.NETCore.App 10.0.14',runtimeInputSha512=observed_sha512,peCount=len(modules))
    metadata=json.dumps(artifact,indent=2).encode()
    manifest_output.write_bytes(metadata)
    with zipfile.ZipFile(output,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        archive.writestr('artifact-manifest.json',metadata)
        for record in records:archive.write(published/record['path'],'package/'+record['path'])
    # Re-read and byte-check exact archive membership into a NEW development directory.
    with zipfile.ZipFile(output) as archive:
        expected={'artifact-manifest.json'}|{'package/'+r['path'] for r in records}
        if len(archive.infolist())!=len(expected) or set(archive.namelist())!=expected:raise ValueError('Archive membership')
        verification.mkdir()
        for record in records:
            target=verification/record['path'];target.parent.mkdir(parents=True,exist_ok=True)
            target.write_bytes(archive.read('package/'+record['path']))
            if target.stat().st_size!=record['bytes'] or digest(target)!=record['sha256']:raise ValueError('Extracted artifact differs')
    temp=repo/'installer/.scratch/service-describe-private';temp.mkdir(parents=True,exist_ok=True)
    environment={'SystemRoot':os.environ['SystemRoot'],'WINDIR':os.environ['SystemRoot'],'PATH':'',
        'TEMP':str(temp),'TMP':str(temp),'APPDATA':str(temp),'LOCALAPPDATA':str(temp),'DOTNET_EnableDiagnostics':'0'}
    description=json.loads(subprocess.check_output([str(verification/'Yahad.ServiceHost.exe'),'--describe'],
        env=environment,cwd=temp,text=True,timeout=15))
    if not description['version'].endswith(source_commit) or description['activationAuthorityConfigured'] or description['familyInstallationReady']:
        raise ValueError('Artifact source/readiness description')
    bootstrap=r'C:\Program Files\Yahad.Acceptance\bootstrap'
    proposal=dict(schema=1,kind='proposed-disposable-vm-service-test',authorized=False,
        requiresOwnerApproval=True,requiresIdentifiedDisposableVm=True,vmId=None,vmSnapshotId=None,
        artifact=dict(sourceCommit=source_commit,archive=output.name,archiveBytes=output.stat().st_size,
            archiveSha256=digest(output),fileManifestSha256=digest(manifest_output),executable=executable),
        purpose='SCM entry, dedicated identity and fail-closed unprovisioned startup only; no app startup',
        target=dict(os='supported Windows 11 x64',domainJoined=False,preinstalledRuntimesRequired=False),
        service=dict(name='Yahad.Acceptance',account=r'NT SERVICE\Yahad.Acceptance',password='OS-managed virtual account; no supplied password',
            type='SERVICE_WIN32_OWN_PROCESS',imagePath='"'+bootstrap+r'\Yahad.ServiceHost.exe" --service',
            startType='SERVICE_DEMAND_START',delayedAutostart=False,failureActions=[],
            requiredPrivileges=['SeChangeNotifyPrivilege'],serviceSidType='SERVICE_SID_TYPE_UNRESTRICTED',
            serviceObjectDacl='D:P(A;;GA;;;SY)(A;;GA;;;BA)'),
        privileges=dict(installer='administrator in the identified VM only',runtime='non-administrator; service-logon group; exact required-privilege set verified by host'),
        files=dict(copyOnlyVerifiedPackageTo=bootstrap,owner='BUILTIN\\Administrators',protectedAcl=True,
            allowedAccess={'SYSTEM':'FullControl','BUILTIN\\Administrators':'FullControl',r'NT SERVICE\Yahad.Acceptance':'ReadAndExecute'},
            readButDoNotCreate=r'C:\ProgramData\Yahad.Acceptance\bootstrap.json',
            programDataWritesAllowed=[],syntheticStateCreation=False),
        persistentChanges=['SCM registration under HKLM\\SYSTEM\\CurrentControlSet\\Services\\Yahad.Acceptance',
            'only the new verified Program Files test subtree and its private ACL'],
        preconditions=['owner names VM/console/snapshot and authorizes this exact manifest',
            'service and both Yahad.Acceptance machine-directory roots absent; abort on any collision',
            'verify archive hash and every extracted bootstrap/runtime file; executable hash alone is insufficient',
            'private snapshot/revert path available; no family data or domain membership'],
        procedure=['copy verified self-contained folder, apply scoped ACL and create exact demand-start service',
            'configure and read back dedicated account, required privileges, SID type, DACL and empty failure actions',
            'start once through SCM; record status/exit codes; do not create a bootstrap descriptor to bypass refusal'],
        expected=dict(state='SERVICE_STOPPED',win32ExitCode=1066,serviceSpecificExitCode=1002,
            meaning='identity checks passed; absent admin-owned bootstrap refused',applicationChildren=0,applicationListeners=0),
        failConditions=['identity denial 1001 or any unexpected service-specific code','RUNNING or app child/listener',
            'machine data/credential creation','modified existing service/file/configuration','uncertain owned process-tree cleanup'],
        rollback=['confirm exact test service/owned process exit; never report recovery if quiescence is uncertain',
            'delete only the test SCM registration and receipt-owned Program Files test files',
            'preserve any unexpected data and stop for reconciliation; do not delete it as cleanup',
            'owner-approved VM snapshot reversion is the final rollback if normal removal cannot be verified'],
        notIncluded=['autostart or reboot','positive authenticated app launch','real family/WhatsApp setup','firewall/CA/trust/DNS/tunnel changes',
            'machine-global runtime/PATH changes','production credentials/signing enrollment','release or website publication'],
        limitations=['native SCM, service-account/token and service-object ACL acceptance remain unverified',
            'no application listener is expected; runtime OS diagnostics are not an authenticated app-control endpoint',
            'no existing disposable VM is identified through current access'])
    proposal_output.write_text(json.dumps(proposal,indent=2),encoding='utf-8')
    return dict(archive=str(output),archiveBytes=output.stat().st_size,archiveSha256=digest(output),
        executableSha256=executable['sha256'],files=len(records),peCount=len(modules),
        extractedFilesVerified=True,descriptionVerifiedWithEmptyPath=True,sourceCommit=source_commit,
        serviceInstalled=False,nativeScmInvoked=False,activationAuthorityConfigured=False)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('published','runtime-package','repo','output','verification','manifest-output','proposal-output'):parser.add_argument('--'+name,required=True,type=Path)
    parser.add_argument('--source-commit',required=True)
    args=parser.parse_args()
    print(json.dumps(build(args.published,args.runtime_package,args.source_commit,args.repo,args.output,args.verification,args.manifest_output,args.proposal_output)))
