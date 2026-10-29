"""Build the real first-install payload using the existing verified dependencies.

Build-machine tool only; never runs the installer or modifies a Windows service.
"""
import argparse
import hashlib
import json
import shutil
import stat
import subprocess
import zipfile
from pathlib import Path
from . import bundle,dependencies,pe
from .prepare_core_fixture import prepare


def payload(repo,core,vendors,full,node,dotnet,npm_cache,verified_vendor_source,archive):
    repo,core,vendors,full,node,dotnet,npm_cache,verified_vendor_source,archive=(Path(p).resolve() for p in (repo,core,vendors,full,node,dotnet,npm_cache,verified_vendor_source,archive))
    if any(p.exists() for p in (core,vendors,full,archive)):raise ValueError('New build outputs required')
    prepare(repo,core,node,dotnet,npm_cache)
    vendors.mkdir(parents=True)
    spec=dependencies.pins()
    for source in spec['sources'].values():
        for record in source['files']:
            original=verified_vendor_source/record['path']
            if original.stat().st_size!=record['size'] or dependencies.digest(original)!=record['sha256']:raise ValueError('Pinned vendor input differs')
            target=vendors/record['path'];target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(original,target)
    for name in ['legal/dependency-proof.json','legal/ffmpeg-build.json','legal/Caddy-NOTICES.txt']:
        shutil.copyfile(verified_vendor_source/name,vendors/name)
    dependencies.add_license_texts(vendors)
    dependencies.validate_staged(bundle.inventory(vendors),spec)
    dependencies.augment(core,vendors,full)
    result=bundle.build(full,archive,'full')
    if result['version']!='0.5.3':raise ValueError('Published 0.5.3 metadata required')
    return dict(prepared=str(full),archive=str(archive),files=len(result['files']),version=result['version'],sha256=dependencies.digest(archive))


def setup_inputs(repo,payload_zip,host,output):
    repo,payload_zip,host,output=(Path(p).resolve() for p in (repo,payload_zip,host,output))
    if output.exists():raise ValueError('New output required')
    if subprocess.check_output(['git','-C',str(repo),'status','--porcelain','--untracked-files=no']).strip():raise ValueError('Clean committed source required')
    source=subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD']).decode().strip()
    runtime=json.loads((host/'Yahad.ServiceHost.runtimeconfig.json').read_text())['runtimeOptions']
    if runtime.get('includedFrameworks')!=[dict(name='Microsoft.NETCore.App',version='10.0.14')]:raise ValueError('Private .NET runtime required')
    hostfiles={p.relative_to(host).as_posix():p for p in host.rglob('*') if p.is_file()}
    modules={name:pe.inspect(p) for name,p in hostfiles.items() if p.suffix.lower() in ('.exe','.dll')}
    folded={name.casefold() for name in hostfiles}
    for name,module in modules.items():
        for imported in module['imports']:
            if not pe.is_system(imported) and (Path(name).parent/imported).as_posix().casefold() not in folded:raise ValueError('Missing host native dependency')
    output.mkdir(parents=True);records=[];archive=output/'install.zip'
    with zipfile.ZipFile(payload_zip) as payload_source:
        payload_manifest=payload_source.read('bundle.json')
        parsed=json.loads(payload_manifest)
        if parsed['version']!='0.5.3' or parsed['profile']!='full' or parsed['target']!='win-x64':raise ValueError('Wrong app payload')
        build=json.loads(payload_source.read('legal/build-inputs.json'))
        if build['appSourceCommit']!=source or build['helperSourceCommit']!=source or build['databaseSchema']!=5:raise ValueError('Mixed source payload')
        expected={'bundle.json'}|{record['path'] for record in parsed['files']}
        if set(payload_source.namelist())!=expected or len(payload_source.infolist())!=len(expected):raise ValueError('Unexpected payload member')
        with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as target:
            def put(name,bytes_):
                if len(bytes_)>536870912:raise ValueError('Oversize member')
                info=zipfile.ZipInfo(name,(2026,10,29,0,0,0));info.create_system=3;info.external_attr=(stat.S_IFREG|0o644)<<16;info.compress_type=zipfile.ZIP_DEFLATED
                target.writestr(info,bytes_);records.append(dict(Path=name,Size=len(bytes_),Sha256=hashlib.sha256(bytes_).hexdigest()))
            put('payload/bundle.json',payload_manifest)
            for record in parsed['files']:
                bytes_=payload_source.read(record['path'])
                if len(bytes_)!=record['size'] or hashlib.sha256(bytes_).hexdigest()!=record['sha256']:raise ValueError('Payload bytes differ')
                put('payload/'+record['path'],bytes_)
            for name,path in sorted(hostfiles.items()):
                if path.is_symlink() or path.stat().st_nlink!=1 or path.suffix.lower() in ('.pdb','.key','.pem','.sqlite'):raise ValueError('Invalid host input')
                put('host/'+name,path.read_bytes())
    manifest=dict(Format=1,Version='0.5.3',SourceCommit=source,ZipSha256=dependencies.digest(archive),PayloadDigest=hashlib.sha256(payload_manifest).hexdigest(),Files=records)
    (output/'install-manifest.json').write_text(json.dumps(manifest,separators=(',',':'))+'\n',encoding='utf-8')
    with zipfile.ZipFile(archive) as check:
        if check.testzip() is not None:raise ValueError('Installer ZIP corrupt')
        for record in records:
            b=check.read(record['Path'])
            if len(b)!=record['Size'] or hashlib.sha256(b).hexdigest()!=record['Sha256']:raise ValueError('Installer ZIP differs')
    return dict(inputs=str(output),files=len(records),zipSha256=manifest['ZipSha256'],payload=manifest['PayloadDigest'],source=source)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);sub=parser.add_subparsers(dest='action',required=True)
    app=sub.add_parser('payload')
    for name in ['repo','core','vendors','full','node','dotnet','npm-cache','verified-vendor-source','archive']:app.add_argument('--'+name,required=True,type=Path)
    installer=sub.add_parser('setup-inputs')
    for name in ['repo','payload-zip','host','output']:installer.add_argument('--'+name,required=True,type=Path)
    args=vars(parser.parse_args());action=args.pop('action');args={k.replace('-','_'):v for k,v in args.items()}
    result=payload(**args) if action=='payload' else setup_inputs(**args);print(json.dumps(result))
