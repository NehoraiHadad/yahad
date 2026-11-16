"""Synthetic Format 2 install-package and app-asset contracts; fixture executables are never invoked."""
import hashlib
import json
from pathlib import Path
import re
import stat
import struct
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile

from installer.windows import bundle, setup_package as full, infra_package
from installer.windows.application_version import Infrastructure


ROOT = Path(__file__).resolve().parents[1]
VERSION, SOURCE, INFRA_SOURCE = '0.6.0', 'a'*40, 'c'*40
INFRA = Infrastructure('1.0.0', 1, 1)
PROPS = ('<Project>\n  <PropertyGroup>\n    <YahadInfrastructureVersion>1.0.0</YahadInfrastructureVersion>\n'
         '    <YahadInfrastructureContractMin>1</YahadInfrastructureContractMin>\n'
         '    <YahadInfrastructureContractMax>1</YahadInfrastructureContractMax>\n'
         '    <Version>$(YahadInfrastructureVersion)</Version>\n  </PropertyGroup>\n</Project>\n')
PACKAGE = json.dumps({'version':VERSION, 'engines':{'node':'>=24.0.0 <25'}, 'yahadInfrastructureContract':1,
                      'yahadWindowsMinimumFrom':{'app':'0.5.0', 'infrastructure':'1.0.0'}})


def image(imported='kernel32.dll'):
    data=bytearray(2048);data[:2]=b'MZ';struct.pack_into('<I',data,0x3c,0x80);data[0x80:0x84]=b'PE\0\0'
    struct.pack_into('<HH',data,0x84,0x8664,1);struct.pack_into('<H',data,0x94,240)
    optional=0x98;struct.pack_into('<H',data,optional,0x20b);struct.pack_into('<I',data,optional+60,512);struct.pack_into('<I',data,optional+108,16)
    struct.pack_into('<IIII',data,optional+240+8,1536,0x1000,1536,512)
    struct.pack_into('<II',data,optional+120,0x1000,40);struct.pack_into('<I',data,512+12,0x1000+900-512)
    value=imported.encode('ascii')+b'\0';data[900:900+len(value)]=value
    return bytes(data)


def describe(artifact, /, **changed):
    value=dict(artifact=artifact,infrastructureVersion=INFRA.version,contractMin=INFRA.contract_min,contractMax=INFRA.contract_max,
               sourceCommit=SOURCE,version=INFRA.version+'+'+SOURCE)
    if artifact=='Yahad.Setup': value['embeddedPackage']=False
    return dict(value,**changed)


def put(root, name, value):
    path=Path(root)/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(value.encode() if isinstance(value,str) else value);return path


def stage_app(root, repo_sources):
    """A complete staged app tree (prepare_core_fixture.py's output shape)."""
    put(root,'app/package.json',PACKAGE)
    for name in full.bundle.SERVER: put(root,name,'// bundled '+name+'\n')
    for name in ('app/dist/index.html','app/deploy/lan-capabilities.json','app/deploy/lan-help.html','app/legal/ThirdPartyNotices-server.txt','app/legal/Yahad-LICENSE.txt'):
        put(root,name,'fixture '+name)
    main=(Path(root)/'app/server/main.mjs').read_bytes()
    server=dict(format='yahad-server-bundle',schema=1,notices='app/legal/ThirdPartyNotices-server.txt',
        sources={name:hashlib.sha256(repo_sources[name].encode()).hexdigest() for name in repo_sources if name.startswith('src/')},
        packages=[dict(path='node_modules/fastify',name='fastify',version='5.12.7',noticeFiles=[])],
        outputs=[dict(path='app/server/main.mjs',entry='src/main.ts',size=len(main),sha256=hashlib.sha256(main).hexdigest(),
                      sources=['src/main.ts','src/db.ts','src/database-schema.ts'],packages=['node_modules/fastify'])])
    put(root,'app/legal/server-bundle.json',json.dumps(server))
    put(root,'app/legal/SBOM.json',json.dumps(dict(format='yahad-app-inventory',schema=1,serverBundleDependencies=[dict(path='node_modules/fastify',version='5.12.7')])))
    return server


class SetupPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory(prefix='yahad-full-package-test-');self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.repo=self.root/'repo';self.prepared=self.root/'payload';self.repo.mkdir();self.prepared.mkdir()
        self.tracked=['package.json','package-lock.json','src/database-schema.ts','src/db.ts','src/main.ts']
        self.sources={
            'package.json':PACKAGE,
            'package-lock.json':json.dumps({'version':VERSION,'packages':{'':{'version':VERSION},'node_modules/fastify':{'version':'5.12.7'},'node_modules/vite':{'version':'7.3.7','dev':True}}}),
            'src/database-schema.ts':'export const DATABASE_SCHEMA = 6;\nexport const DATABASE_MIGRATIONS_FROM = [1, 2] as const;\n',
            'src/db.ts':"import type { DatabaseSync } from 'node:sqlite';\nexport function migrate() {}\n",
            'src/main.ts':"import { migrate } from './db.js';\n",
        }
        for name,value in self.sources.items():put(self.repo,name,value)
        put(self.repo,'installer/Directory.Build.props',PROPS)
        self.server=stage_app(self.prepared,self.sources)
        self.build=dict(appSourceCommit=SOURCE,packagerSourceCommit=SOURCE,databaseSchema=6,databaseMigrationsFrom=[1,2])
        self.save_build()

    def save_build(self):
        put(self.prepared,'app/legal/build-inputs.json',json.dumps(self.build));put(self.prepared,'app/legal/server-bundle.json',json.dumps(self.server))
        self.refresh_records()

    def refresh_records(self):
        self.records=[full._record(p.relative_to(self.prepared).as_posix(),p) for p in sorted(self.prepared.rglob('*')) if p.is_file()]

    def source(self):return full.validate_payload_source(self.repo,self.prepared,self.records,VERSION,SOURCE,self.tracked)

    def native(self,artifact='Yahad.ServiceHost',root=None):
        root=root or self.root/artifact;root.mkdir(parents=True)
        for name in (artifact+'.exe',artifact+'.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll'):put(root,name,image())
        frameworks=[dict(name='Microsoft.NETCore.App',version=full.RUNTIME_VERSION)]
        put(root,artifact+'.runtimeconfig.json',json.dumps(dict(runtimeOptions=dict(includedFrameworks=frameworks))))
        libraries={'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/'+full.RUNTIME_VERSION:dict(runtime={artifact+'.dll':{},'System.Private.CoreLib.dll':{}},native={'coreclr.dll':{},'hostfxr.dll':{},'hostpolicy.dll':{}})}
        put(root,artifact+'.deps.json',json.dumps(dict(runtimeTarget=dict(name='net10.0/win-x64'),targets={'net10.0/win-x64':libraries})))
        return root

    def engine(self):return put(self.root/'engine','Yahad.Setup.exe',image())

    def test_staged_app_validates_as_the_app_tree(self):
        key=lambda r:r['path'];self.assertEqual(sorted(full.bundle.validate(self.prepared)['files'],key=key),sorted(self.records,key=key))

    def test_bundle_sources_and_migration_declaration_are_taken_from_actual_bytes(self):
        self.assertEqual(self.source(),(6,[1,2]))
        put(self.repo,'src/db.ts','changed bytes')
        with self.assertRaisesRegex(full.PackageError,'source bytes'):self.source()

    def test_migration_declaration_must_match_build_record(self):
        for value in ([1],[1,2,3],None,[1,True]):
            with self.subTest(value=value):
                self.build['databaseMigrationsFrom']=value;self.save_build()
                with self.assertRaisesRegex(ValueError,'schema'):self.source()

    def test_every_build_input_source_claim_must_match(self):
        for field in ('appSourceCommit','packagerSourceCommit'):
            with self.subTest(field=field):
                before=self.build[field];self.build[field]='b'*40;self.save_build()
                with self.assertRaisesRegex(full.PackageError,'Mixed source'):self.source()
                self.build[field]=before
        self.save_build()

    def test_schema_cannot_be_relabelled_or_coerced_from_boolean(self):
        for value in (7,True):
            self.build['databaseSchema']=value;self.save_build()
            with self.assertRaisesRegex(ValueError,'schema'):self.source()

    def test_app_package_bytes_must_be_the_checkouts(self):
        put(self.prepared,'app/package.json',PACKAGE+' ');self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'package bytes'):self.source()

    def test_shipped_server_bytes_are_pinned_by_the_bundle_record(self):
        put(self.prepared,'app/server/main.mjs','// replaced\n');self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'Server bundle record differs'):self.source()

    def test_bundle_entries_are_fixed(self):
        self.server['outputs'][0]['entry']='src/db.ts';self.save_build()
        with self.assertRaisesRegex(full.PackageError,'record differs'):self.source()
        self.server['outputs'].pop(0);self.save_build()
        with self.assertRaisesRegex(full.PackageError,'Server bundle record'):self.source()

    def test_bundled_packages_must_be_locked_production_packages(self):
        for package in (dict(path='node_modules/fastify',version='5.0.0'),dict(path='node_modules/vite',version='7.3.7'),dict(path='node_modules/absent',version='1.0.0')):
            with self.subTest(package=package):
                self.server['packages']=[dict(package,name=package['path'].rsplit('/',1)[1],noticeFiles=[])];self.save_build()
                with self.assertRaisesRegex(full.PackageError,'not locked'):self.source()

    def test_untracked_source_cannot_join_verified_payload(self):
        put(self.repo,'src/untracked.ts','extra');self.server['sources']['src/untracked.ts']=hashlib.sha256(b'extra').hexdigest();self.save_build()
        with self.assertRaisesRegex(full.PackageError,'source bytes'):self.source()

    def test_app_identity_reads_version_contract_and_minimum_from_against_the_infrastructure(self):
        self.assertEqual(full.app_identity(self.repo,INFRA),(VERSION,1,dict(app='0.5.0',infrastructure='1.0.0')))
        with self.assertRaisesRegex(full.PackageError,'Infrastructure identity differs'):full.app_identity(self.repo,Infrastructure('1.0.1',1,1))
        base=json.loads(PACKAGE)
        for changed,message in (({'yahadInfrastructureContract':2},'infrastructure contract'),({'yahadInfrastructureContract':True},'infrastructure contract'),
                                ({'yahadWindowsMinimumFrom':None},'minimumFrom'),({'yahadWindowsMinimumFrom':{'app':'0.7.0','infrastructure':'1.0.0'}},'minimumFrom'),
                                ({'yahadWindowsMinimumFrom':{'app':'0.5.0','infrastructure':'1.0.1'}},'minimumFrom')):
            with self.subTest(changed=changed):
                put(self.repo,'package.json',json.dumps(dict(base,**changed)))
                with self.assertRaisesRegex(ValueError,message):full.app_identity(self.repo,INFRA)
        put(self.repo,'package.json',PACKAGE)
        put(self.repo,'installer/Directory.Build.props',PROPS.replace('<YahadInfrastructureVersion>1.0.0','<YahadInfrastructureVersion>01.0.0'))
        with self.assertRaisesRegex(ValueError,'Infrastructure identity'):full.app_identity(self.repo,INFRA)

    def test_duplicate_json_and_case_directory_collisions_are_refused(self):
        with self.assertRaisesRegex(full.PackageError,'Duplicate JSON'):full._json(b'{"sourceCommit":"a","sourceCommit":"b"}')
        for names in (['A.dll','a.dll'],['folder/one.dll','Folder/two.dll'],['folder','folder/file.dll']):
            with self.subTest(names=names),self.assertRaises(full.PackageError):full._closed_names(names,component=True)

    def test_private_component_names_are_refused_without_creating_links(self):
        for name in ('../escape','secrets/token.txt','db.sqlite','x.pdb','.env.production','.local/cache.txt','CON.txt','odd:stream','folder/backups/file','datasets/id/data'):
            with self.subTest(name=name),self.assertRaises(full.PackageError):full._closed_names([name],component=True)
        file=put(self.root,'ordinary.dll',b'x');original=Path.lstat
        for attributes,links in ((0,2),(0x400,1)):
            def metadata(path,*args,**kwargs):
                if path==file:return types.SimpleNamespace(st_mode=stat.S_IFREG|0o600,st_nlink=links,st_size=1,st_file_attributes=attributes)
                return original(path,*args,**kwargs)
            with self.subTest(attributes=attributes,links=links),patch.object(Path,'lstat',metadata),self.assertRaises(full.PackageError):full._unlinked(file,file=True)

    def test_native_audit_is_infrastructure_and_source_bound_and_never_executed_by_tests(self):
        root=self.native();description=describe('Yahad.ServiceHost')
        with patch.object(full,'describe_build_artifact',return_value=description) as audit:
            files=full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)
            self.assertIn('coreclr.dll',files);audit.assert_called_once_with(root/'Yahad.ServiceHost.exe','--describe',self.repo)
        for changed in (dict(sourceCommit='b'*40),dict(infrastructureVersion='1.0.1'),dict(version='1.0.1+'+SOURCE),dict(contractMax=2),
                        dict(contractMin=True),dict(applicationVersion=VERSION),dict(artifact='Yahad.Updater')):
            with self.subTest(changed=changed),patch.object(full,'describe_build_artifact',return_value=dict(description,**changed)),self.assertRaises(ValueError):
                full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)

    def test_missing_dependency_and_nonprivate_native_import_fail_before_audit(self):
        root=self.native();(root/'coreclr.dll').unlink()
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'Incomplete private'):full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)
        audit.assert_not_called();put(root,'coreclr.dll',image());put(root,'Yahad.ServiceHost.exe',image('private-missing.dll'))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'private native dependency'):full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)
        audit.assert_not_called()

    def test_every_declared_private_runtime_asset_must_be_present(self):
        root=self.native();path=root/'Yahad.ServiceHost.deps.json';deps=json.loads(path.read_text())
        next(iter(deps['targets']['net10.0/win-x64'].values()))['runtime']['missing.dll']={};path.write_text(json.dumps(deps))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'Missing private .NET dependency'):full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)
        audit.assert_not_called()

    def test_native_input_change_during_audit_is_not_blessed_by_source_description(self):
        root=self.native()
        def changed(*args):put(root,'coreclr.dll',image()+b'changed');return describe('Yahad.ServiceHost')
        with patch.object(full,'describe_build_artifact',side_effect=changed),self.assertRaisesRegex(full.PackageError,'closure changed'):full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)

    def test_engine_is_one_payload_free_infrastructure_image(self):
        engine=self.engine()
        with patch.object(full,'describe_build_artifact',return_value=describe('Yahad.Setup')) as audit:
            self.assertEqual(set(full.validate_engine(engine,INFRA,SOURCE,self.repo)),{'Yahad.Setup.exe'})
            audit.assert_called_once_with(engine,'--describe',self.repo)
        for changed in (dict(embeddedPackage=True),dict(embeddedPackage=None),dict(sourceCommit='b'*40,version='1.0.0+'+'b'*40),
                        dict(infrastructureVersion='1.0.1',version='1.0.1+'+SOURCE),dict(artifact='Yahad.ServiceHost')):
            with self.subTest(changed=changed),patch.object(full,'describe_build_artifact',return_value=describe('Yahad.Setup',**changed)),self.assertRaises(ValueError):
                full.validate_engine(engine,INFRA,SOURCE,self.repo)
        renamed=put(self.root/'other','Yahad.Setup-0.6.0.exe',image())
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'engine image'):full.validate_engine(renamed,INFRA,SOURCE,self.repo)
        audit.assert_not_called()
        # A single-file image has no siblings to load a private library from.
        put(self.root/'engine','Yahad.Setup.exe',image('hostfxr.dll'))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'private native dependency'):full.validate_engine(engine,INFRA,SOURCE,self.repo)
        audit.assert_not_called()

    def test_tree_record_is_ordinal_sorted_camel_case_and_bound(self):
        record=lambda data:dict(size=len(data),sha256=hashlib.sha256(data).hexdigest())
        # Ordinal is UTF-16 code units: a surrogate pair sorts before U+FF01, unlike code points.
        members={'app/c':record(b'c'),'app/B':record(b'B'),'app/！':record(b'x'),'app/\U0001f600':record(b'y'),'app/a/z':record(b'z')}
        data=full.tree_record(VERSION,SOURCE,members,full.APP_PREFIXES);value=json.loads(data)
        self.assertFalse(data.startswith(b'\xef\xbb\xbf'));self.assertEqual(sorted(value),['files','format','sourceCommit','version'])
        self.assertEqual((value['format'],value['version'],value['sourceCommit']),(1,VERSION,SOURCE))
        self.assertEqual([f['path'] for f in value['files']],['app/B','app/a/z','app/c','app/\U0001f600','app/！'])
        self.assertTrue(all(sorted(f)==['path','sha256','size'] for f in value['files']))
        self.assertEqual(full.parse_tree_record(data,full.APP_PREFIXES),value)
        with self.assertRaisesRegex(full.PackageError,'Tree record bound'):full.tree_record(VERSION,SOURCE,{},full.APP_PREFIXES)
        # The writer refuses what the reader would: case collisions and foreign prefixes.
        for changed in ({'app/C':record(b'C')},{'host/x':record(b'x')}):
            with self.subTest(changed=changed),self.assertRaises(full.PackageError):full.tree_record(VERSION,SOURCE,dict(members,**changed),full.APP_PREFIXES)

    def test_tree_record_parser_is_as_strict_as_the_reader(self):
        good=dict(format=1,version=VERSION,sourceCommit=SOURCE,files=[dict(path='app/a',size=1,sha256='0'*64),dict(path='app/b',size=1,sha256='0'*64)])
        encode=lambda value:json.dumps(value).encode()
        full.parse_tree_record(encode(good),full.APP_PREFIXES)
        bad=[b'\xef\xbb\xbf'+encode(good),encode(dict(good,format=True)),encode(dict(good,format=2)),encode(dict(good,extra=1)),
             encode(dict(good,version='01.0.0')),encode(dict(good,sourceCommit='A'*40)),encode(dict(good,files=[])),
             encode(dict(good,files=list(reversed(good['files'])))),encode(dict(good,files=[good['files'][0],dict(good['files'][0],path='app/A')])),
             encode(dict(good,files=[dict(path='host/a',size=1,sha256='0'*64)])),encode(dict(good,files=[dict(path='app/a',size=True,sha256='0'*64)])),
             encode(dict(good,files=[dict(path='app/a',size=1,sha256='0'*64,extra=1)])),b'{"format":1,"format":1}']
        for data in bad:
            with self.subTest(data=data[:80]),self.assertRaises(full.PackageError):full.parse_tree_record(data,full.APP_PREFIXES)

    def test_archive_namespace_bytes_and_records_are_exact_and_deterministic(self):
        entries={'infra/infra-tree.json':b'{}','apps/app-tree.json':b'{}','apps/app/server/main.mjs':b'bundle','apps/app/legal/build-inputs.json':b'{}',
                 'infra/host/Yahad.ServiceHost.exe':image(),'infra/host/sub/Yahad.dll':image(),'infra/platform/Yahad.Platform.exe':image(),
                 'infra/runtime/node/node.exe':image(),'infra/legal/SBOM.json':b'{}','infra/setup/Yahad.Setup.exe':image(),'infra/updater/Yahad.Updater.exe':image()}
        records=full._write_archive(self.root/'a.zip',entries);full._write_archive(self.root/'b.zip',entries)
        self.assertEqual((self.root/'a.zip').read_bytes(),(self.root/'b.zip').read_bytes())
        self.assertEqual([r['Path'] for r in records],sorted(entries))
        with zipfile.ZipFile(self.root/'a.zip') as archive:
            self.assertEqual(archive.namelist(),[r['Path'] for r in records])
            for record in records:self.assertEqual(hashlib.sha256(archive.read(record['Path'])).hexdigest(),record['Sha256'])
        with self.assertRaisesRegex(full.PackageError,'namespace'):full._write_archive(self.root/'bad.zip',{'unapproved/code.exe':b'not executed'})
        self.assertFalse((self.root/'bad.zip').exists())
        for name in ('payload/bundle.json','bundle.json','host/Yahad.ServiceHost.exe','platform/Yahad.Platform.exe','infra/native/Yahad.Platform.exe',
                     'infra/app/package.json','infra/host/../escape','infra/host/','infra/apps/app-tree.json','apps/app/native/Yahad.Platform.exe',
                     'apps/app/Native/coreclr.dll','apps/runtime/node/node.exe','apps/infra-tree.json','infra/app-tree.json','install-manifest.json',
                     'apps/app/src/main.ts','apps/app/node_modules/x/index.js','apps/app/package-lock.json','apps/app/dist/tool.exe','apps/app/dist/run.ps1'):
            with self.subTest(name=name),self.assertRaises(full.PackageError):full._write_archive(self.root/'foreign.zip',{name:b'refused'})
        self.assertFalse((self.root/'foreign.zip').exists())

    def test_app_archive_holds_exactly_the_app_tree_record_and_app_members(self):
        entries={'app-tree.json':b'{}','app/server/main.mjs':b'bundle','app/dist/index.html':b'<!doctype html>'}
        full._write_archive(self.root/'app.zip',entries,member=full._app_archive_member)
        with zipfile.ZipFile(self.root/'app.zip') as archive:self.assertEqual(archive.namelist(),['app-tree.json','app/dist/index.html','app/server/main.mjs'])
        for name in ('apps/app-tree.json','apps/app/server/main.mjs','infra/host/x.dll','install-manifest.json','infra-tree.json','app/src/main.ts',
                     'app/node_modules/x/index.js','app/native/Yahad.Platform.exe','app/package-lock.json','app/dist/a.dll','app/dist/a.node',
                     'app/dist/a.cmd','app/dist/a.BAT','app/../escape','app/.env'):
            with self.subTest(name=name),self.assertRaises(full.PackageError):full._write_archive(self.root/'refused.zip',{name:b'refused'},member=full._app_archive_member)
        self.assertFalse((self.root/'refused.zip').exists())
        self.assertEqual(full.app_archive_name('1.2.3'),'yahad-windows-app-1.2.3.zip')

    def test_archive_refuses_changed_verified_bytes_and_preserves_existing_output(self):
        file=put(self.root,'one.dll',b'original');entries={'infra/host/one.dll':file}
        expected={name:full._record(name,value) for name,value in entries.items()};file.write_bytes(b'changed')
        with self.assertRaisesRegex(full.PackageError,'input changed'):full._write_archive(self.root/'changed.zip',entries,expected)
        output=put(self.root,'existing.zip',b'preserve')
        with self.assertRaises(FileExistsError):full._write_archive(output,entries)
        self.assertEqual(output.read_bytes(),b'preserve')


def infra_entry(cache, release_key_id=None, source=INFRA_SOURCE):
    """A cache entry as infra_package.build writes it: tree/ with every required
    member and infra-tree.json, and infra-build.json."""
    entry=Path(cache)/INFRA.version;tree=entry/'tree'
    for name in sorted(full.INFRA_REQUIRED):put(tree,name,image() if name.endswith('.exe') else '{"fixture":"'+name+'"}\n')
    files=full._VerifiedFiles(full._inventory(tree))
    data=full.tree_record(INFRA.version,source,files.records,full.INFRA_PREFIXES);put(tree,full.INFRA_TREE_NAME,data)
    record=dict(format=1,infrastructureVersion=INFRA.version,contractMin=1,contractMax=1,sourceCommit=source,inputDigest='d'*64,
                treeSha256=hashlib.sha256(data).hexdigest(),releaseKeyId=release_key_id)
    put(entry,'infra-build.json',json.dumps(record,indent=2,sort_keys=True)+'\n')
    return entry


class InstallPackageBuildTests(unittest.TestCase):
    """build() from a cached infrastructure entry and a staged app, with the Git checks stubbed."""
    def setUp(self):
        self.base=SetupPackageTests('test_bundle_sources_and_migration_declaration_are_taken_from_actual_bytes');self.base.setUp();self.addCleanup(self.base.tearDown)
        b=self.base;self.entry=infra_entry(b.root/'cache');self.output=b.root/'out';self.app_archive=b.root/'assets'/full.app_archive_name(VERSION)
        self.app_archive.parent.mkdir()

    def build(self):
        b=self.base
        with patch.object(full,'_committed_source',return_value=(SOURCE,b.tracked)):
            return full.build(b.repo,b.prepared,self.entry,self.output,self.app_archive)

    def written(self):
        manifest=json.loads((self.output/'install-manifest.json').read_bytes())
        with zipfile.ZipFile(self.output/'install.zip') as archive:members={name:archive.read(name) for name in archive.namelist()}
        return manifest,members

    def test_manifest_is_format_two_with_exact_keys_and_each_tracks_identity(self):
        result=self.build();manifest,members=self.written();data=(self.output/'install-manifest.json').read_bytes()
        self.assertFalse(data.startswith(b'\xef\xbb\xbf'))
        self.assertEqual(list(manifest),['App','Files','Format','Infrastructure','MinimumFrom','ZipSha256'])
        self.assertIs(type(manifest['Format']),int);self.assertEqual(manifest['Format'],2)
        record=json.loads((self.entry/'infra-build.json').read_bytes())
        self.assertEqual(manifest['Infrastructure'],dict(ContractMax=1,ContractMin=1,SourceCommit=INFRA_SOURCE,
            TreeSha256=record['treeSha256'],Version='1.0.0'))
        self.assertEqual(hashlib.sha256(members['infra/infra-tree.json']).hexdigest(),record['treeSha256'])
        self.assertEqual(manifest['App'],dict(DatabaseMigrationsFrom=[1,2],DatabaseSchema=6,RequiresContract=1,SourceCommit=SOURCE,
            TreeSha256=hashlib.sha256(members['apps/app-tree.json']).hexdigest(),Version=VERSION))
        self.assertEqual(manifest['MinimumFrom'],dict(App='0.5.0',Infrastructure='1.0.0'))
        self.assertEqual(manifest['ZipSha256'],hashlib.sha256((self.output/'install.zip').read_bytes()).hexdigest())
        self.assertEqual({p.name for p in self.output.iterdir()},{'install.zip','install-manifest.json'})
        self.assertEqual([r['Path'] for r in manifest['Files']],list(members));self.assertEqual(result['files'],len(members))
        for item in manifest['Files']:
            self.assertEqual(sorted(item),['Path','Sha256','Size'])
            self.assertEqual((item['Size'],item['Sha256']),(len(members[item['Path']]),hashlib.sha256(members[item['Path']]).hexdigest()))
        self.assertEqual((result['infrastructureVersion'],result['infraSourceCommit'],result['appVersion'],result['sourceCommit'],result['appTreeSha256']),
                         ('1.0.0',INFRA_SOURCE,VERSION,SOURCE,manifest['App']['TreeSha256']))

    def test_manifest_fields_are_the_setup_readers_records(self):
        # installer/Yahad.Setup/InstallPackage.cs reads this file with unmapped members disallowed.
        source=(ROOT/'installer/Yahad.Setup/InstallPackage.cs').read_text(encoding='utf-8')
        records={m.group(1):[p.strip().split()[-1] for p in m.group(2).split(',')] for m in re.finditer(r'record (\w+)\(([^)]*)\)',source)}
        if 'MinimumFrom' not in records['InstallPackageManifest']:
            self.skipTest('InstallPackage.cs has no MinimumFrom yet (owner A adds InstallPackageManifest.MinimumFrom)')
        self.build();manifest,_=self.written()
        self.assertEqual(sorted(manifest),sorted(records['InstallPackageManifest']))
        self.assertEqual(sorted(manifest['Infrastructure']),sorted(records['PackageInfrastructure']))
        self.assertEqual(sorted(manifest['App']),sorted(records['PackageApp']))
        self.assertEqual(sorted(manifest['Files'][0]),sorted(records['InstallMember']))

    def test_app_member_policy_is_the_builders(self):
        # installer/Yahad.Setup/AppMemberPolicy.cs is the one reader rule for app members
        # in the app archive and in the install package's apps/ namespace (5.2).
        source=(ROOT/'installer/Yahad.Setup/AppMemberPolicy.cs').read_text(encoding='utf-8')
        def strings(name):
            return re.findall(r'"([^"]*)"',re.search(name+r'\s*=\s*\[(.*?)\];',source,re.S).group(1))
        self.assertEqual(strings('Required'),list(bundle.REQUIRED))
        self.assertEqual(strings('ForbiddenDirectories'),list(bundle.REFUSED_DIRECTORIES))
        self.assertEqual(strings('ForbiddenExtensions'),list(bundle.REFUSED_SUFFIXES))
        self.assertEqual([re.search(r'ForbiddenFile="([^"]*)"',source).group(1)],list(bundle.REFUSED_FILES))
        self.assertEqual(full.APP_REQUIRED,frozenset(bundle.REQUIRED))

    def test_app_archive_is_the_app_tree_with_identical_record_bytes(self):
        self.build();manifest,members=self.written()
        with zipfile.ZipFile(self.app_archive) as archive:asset={name:archive.read(name) for name in archive.namelist()}
        self.assertEqual(asset['app-tree.json'],members['apps/app-tree.json'])
        self.assertEqual(hashlib.sha256(asset['app-tree.json']).hexdigest(),manifest['App']['TreeSha256'])
        self.assertEqual(asset,{name[len('apps/'):]:value for name,value in members.items() if name.startswith('apps/')})
        self.assertTrue(all(name=='app-tree.json' or name.startswith('app/') for name in asset))

    def test_outputs_are_deterministic(self):
        self.build();first=(self.output/'install.zip').read_bytes(),self.app_archive.read_bytes(),(self.output/'install-manifest.json').read_bytes()
        self.output=self.base.root/'again';self.app_archive=self.base.root/'again-assets'/full.app_archive_name(VERSION);self.app_archive.parent.mkdir()
        self.build();self.assertEqual(first,((self.output/'install.zip').read_bytes(),self.app_archive.read_bytes(),(self.output/'install-manifest.json').read_bytes()))

    def test_tree_records_list_every_other_member_of_their_tree_exactly_once(self):
        self.build();manifest,members=self.written()
        for record_name,root,version,source,prefixes in (('infra/infra-tree.json','infra/','1.0.0',INFRA_SOURCE,full.INFRA_PREFIXES),('apps/app-tree.json','apps/',VERSION,SOURCE,('app/',))):
            with self.subTest(tree=record_name):
                data=members[record_name];value=full.parse_tree_record(data,prefixes)
                self.assertEqual((value['version'],value['sourceCommit']),(version,source))
                paths=[f['path'] for f in value['files']]
                self.assertEqual(set(paths),{n[len(root):] for n in members if n.startswith(root) and n!=record_name})
                for f in value['files']:self.assertEqual((f['size'],f['sha256']),(len(members[root+f['path']]),hashlib.sha256(members[root+f['path']]).hexdigest()))
        self.assertTrue(all(n.startswith(('infra/','apps/')) for n in members))
        self.assertTrue(full.REQUIRED_MEMBERS<=set(members))

    def test_infrastructure_members_are_the_cached_bytes(self):
        self.build();_,members=self.written()
        tree=self.entry/'tree'
        self.assertEqual({n[len('infra/'):]:v for n,v in members.items() if n.startswith('infra/')},
                         {p.relative_to(tree).as_posix():p.read_bytes() for p in tree.rglob('*') if p.is_file()})

    def test_legal_records_split_between_app_and_infrastructure(self):
        self.build();_,members=self.written()
        self.assertEqual({n for n in members if n.startswith('apps/app/legal/')},{'apps/'+n for n in full.bundle.APP_LEGAL})
        self.assertTrue({'infra/'+n for n in full.INFRA_LEGAL}<={n for n in members if n.startswith('infra/legal/')})
        self.assertFalse(any(n.startswith(('apps/legal/','apps/app/runtime/','infra/app/')) for n in members))

    def test_corrupt_or_foreign_cache_entry_is_refused_before_any_output(self):
        (self.entry/'tree/host/Yahad.ServiceHost.exe').write_bytes(image()+b'changed')
        with self.assertRaisesRegex(infra_package.InfraError,'INFRA_CACHE_CORRUPT'):self.build()
        self.assertFalse(self.output.exists());self.assertFalse(self.app_archive.exists())

    def test_checkout_must_declare_the_cached_infrastructure(self):
        put(self.base.repo,'installer/Directory.Build.props',PROPS.replace('>1.0.0<','>1.0.1<'))
        with self.assertRaisesRegex(full.PackageError,'Infrastructure identity differs'):self.build()
        self.assertFalse(self.output.exists())

    def test_app_contract_must_be_declared_inside_the_infrastructure_range(self):
        for contract in (None,2,True):
            with self.subTest(contract=contract):
                package=json.loads(PACKAGE);package['yahadInfrastructureContract']=contract;put(self.base.repo,'package.json',json.dumps(package))
                with self.assertRaisesRegex(ValueError,'infrastructure contract'):self.build()
                self.assertFalse(self.output.exists())

    def test_forbidden_app_members_never_reach_either_archive(self):
        b=self.base
        for name in ('app/dist/helper.exe','app/dist/addon.node','app/native/x.txt','app/dist/innocent.js'):
            with self.subTest(name=name):
                put(b.prepared,name,image() if name.endswith('innocent.js') else 'x')
                with self.assertRaises(ValueError):self.build()
                (b.prepared/name).unlink();self.assertFalse(self.output.exists());self.assertFalse(self.app_archive.exists())

    def test_app_archive_name_and_outputs_must_be_new_and_outside_input_closures(self):
        self.app_archive=self.app_archive.with_name('yahad-windows-app-0.6.1.zip')
        with self.assertRaisesRegex(full.PackageError,'App archive name'):self.build()
        self.app_archive=self.app_archive.with_name(full.app_archive_name(VERSION))
        self.output.mkdir()
        with self.assertRaisesRegex(full.PackageError,'New output'):self.build()
        for closure in (self.base.prepared,self.entry):
            self.output=closure/'nested-out'
            with self.subTest(closure=closure.name),self.assertRaisesRegex(full.PackageError,'New output'):self.build()

    def test_required_members_must_all_be_present(self):
        full.require_members(full.REQUIRED_MEMBERS)
        for name in sorted(full.REQUIRED_MEMBERS):
            with self.subTest(name=name),self.assertRaisesRegex(full.PackageError,'Required package member'):full.require_members(full.REQUIRED_MEMBERS-{name})


if __name__=='__main__':unittest.main(verbosity=2)
