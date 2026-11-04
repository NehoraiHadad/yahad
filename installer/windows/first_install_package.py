"""Build the real first-install payload using the existing verified dependencies.

Build-machine tool only; never runs the installer or modifies a Windows service.
"""
import argparse
import json
import re
import shutil
from pathlib import Path
from . import bundle,dependencies
from .prepare_core_fixture import prepare
from .application_version import source_release_version, require_source_release_version


def source_database_schema(repo):
    text=(Path(repo)/'src'/'database-schema.ts').read_text(encoding='utf-8')
    matches=re.findall(r'^export const DATABASE_SCHEMA\s*=\s*([0-9]+);',text,re.MULTILINE)
    if len(matches)!=1 or not 1<=int(matches[0])<=64:raise ValueError('Source database schema is unavailable')
    return int(matches[0])


def require_source_database_schema(build,repo):
    expected=source_database_schema(repo)
    if type(build.get('databaseSchema')) is not int or build['databaseSchema']!=expected:
        raise ValueError('Mixed source database schema')
    return expected


def payload(repo,core,vendors,full,node,dotnet,npm_cache,verified_vendor_source,archive):
    repo,core,vendors,full,node,dotnet,npm_cache,verified_vendor_source,archive=(Path(p).resolve() for p in (repo,core,vendors,full,node,dotnet,npm_cache,verified_vendor_source,archive))
    source_release_version(repo)
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
    require_source_release_version(result['version'],repo)
    return dict(prepared=str(full),archive=str(archive),files=len(result['files']),version=result['version'],sha256=dependencies.digest(archive))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);sub=parser.add_subparsers(dest='action',required=True)
    app=sub.add_parser('payload')
    for name in ['repo','core','vendors','full','node','dotnet','npm-cache','verified-vendor-source','archive']:app.add_argument('--'+name,required=True,type=Path)
    args=vars(parser.parse_args());action=args.pop('action');args={k.replace('-','_'):v for k,v in args.items()}
    print(json.dumps(payload(**args)))
