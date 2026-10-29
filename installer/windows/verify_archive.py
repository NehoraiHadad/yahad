"""Developer candidate extraction/checksums, not release-signature verification."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import zipfile
from . import bundle


def verify(archive_path, output, *, retain_manifest=False):
    archive_path, output = Path(archive_path).absolute(), Path(output).absolute()
    if output.exists(): raise bundle.BundleError('New extraction output required')
    for ancestor in output.parents:
        if ancestor.exists() and (ancestor.is_symlink() or getattr(ancestor.lstat(),'st_file_attributes',0)&0x400):
            raise bundle.BundleError('Linked output ancestor')
    with zipfile.ZipFile(archive_path) as archive:
        infos=archive.infolist()
        if len(infos)>bundle.MAX_FILES+1: raise bundle.BundleError('Archive file count')
        lookup={i.filename:i for i in infos}
        if len(lookup)!=len(infos) or 'bundle.json' not in lookup or lookup['bundle.json'].file_size>8*1024**2:
            raise bundle.BundleError('Archive manifest')
        manifest_bytes=archive.read('bundle.json');manifest=json.loads(manifest_bytes)
        if not isinstance(manifest,dict) or manifest.get('kind')!='windows-runtime-candidate' or manifest.get('schema')!=1 or manifest.get('target')!='win-x64' or manifest.get('familyInstallationReady') is not False or manifest.get('profile') not in ('full','core-fixture'):
            raise bundle.BundleError('Candidate contract')
        records=manifest.get('files')
        if not isinstance(records,list) or len(records)>bundle.MAX_FILES: raise bundle.BundleError('File records')
        declared={};seen=set();total=0
        for record in records:
            if not isinstance(record,dict): raise bundle.BundleError('File record')
            name=record.get('path'); size=record.get('size'); digest=record.get('sha256')
            if not isinstance(name,str) or len(name)>240 or not name.startswith(('app/','runtime/','legal/')) or '\\' in name or any(p in ('','.','..') or ':' in p or p.endswith(('.', ' ')) or re.fullmatch(r'(?i)(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\..*)?',p) for p in name.split('/')):
                raise bundle.BundleError('Archive path')
            if name.casefold() in seen or type(size) is not int or size<0 or size>512*1024**2 or not isinstance(digest,str) or not re.fullmatch('[a-f0-9]{64}',digest): raise bundle.BundleError('Archive file record')
            seen.add(name.casefold());declared[name]=record;total+=size
        if total>bundle.MAX_BYTES or set(lookup)!=set(declared)|{'bundle.json'}: raise bundle.BundleError('Archive member set')
        for name,record in declared.items():
            info=lookup[name]
            if info.is_dir() or info.file_size!=record['size'] or stat.S_IFMT(info.external_attr>>16) not in (0,stat.S_IFREG):
                raise bundle.BundleError('Archive member type/size')
        output.mkdir(parents=True,exist_ok=False)
        for name,record in declared.items():
            target=output/name;target.parent.mkdir(parents=True,exist_ok=True)
            hashed=hashlib.sha256();count=0
            with archive.open(name) as source,target.open('xb') as destination:
                while chunk:=source.read(1024*1024):
                    count+=len(chunk)
                    if count>record['size']: raise bundle.BundleError('Archive expansion')
                    hashed.update(chunk);destination.write(chunk)
            if count!=record['size'] or hashed.hexdigest()!=record['sha256']: raise bundle.BundleError('Archive checksum')
    verified=bundle.validate(output,manifest['profile'])
    if verified!=manifest: raise bundle.BundleError('Extracted candidate differs from manifest')
    if retain_manifest:
        with (output/'bundle.json').open('xb') as metadata:
            metadata.write(manifest_bytes);metadata.flush();os.fsync(metadata.fileno())
    return {'kind':manifest['kind'],'profile':manifest['profile'],'target':'win-x64',
        'fileCount':len(records),'peCount':len(verified['peModules']),'payloadBytes':total,
        'archiveBytes':archive_path.stat().st_size,'archiveSha256':bundle.digest(archive_path),
        'allExtractedFilesVerified':True,'familyInstallationReady':False,
        'releaseSignatureVerified':False,'retainedInstalledManifest':retain_manifest,
        'manifestSha256':hashlib.sha256(manifest_bytes).hexdigest(),'output':str(output)}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--archive',required=True,type=Path);parser.add_argument('--output',required=True,type=Path);parser.add_argument('--report',type=Path);parser.add_argument('--retain-manifest',action='store_true');args=parser.parse_args()
    result=verify(args.archive,args.output,retain_manifest=args.retain_manifest)
    if args.report:
        with args.report.open('x',encoding='utf-8') as destination:json.dump(result,destination,indent=2)
    print(json.dumps(result))
