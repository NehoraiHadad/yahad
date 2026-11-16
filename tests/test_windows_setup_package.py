"""Synthetic Format 2 install-package contracts; fixture executables are never invoked."""
import hashlib
import json
from pathlib import Path
import shutil
import stat
import struct
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile

from installer.windows import setup_package as full
from installer.windows.application_version import Infrastructure


VERSION, SOURCE = '0.6.0', 'a'*40
INFRA = Infrastructure('1.0.0', 1, 1)
PROPS = ('<Project>\n  <PropertyGroup>\n    <YahadInfrastructureVersion>1.0.0</YahadInfrastructureVersion>\n'
         '    <YahadInfrastructureContractMin>1</YahadInfrastructureContractMin>\n'
         '    <YahadInfrastructureContractMax>1</YahadInfrastructureContractMax>\n'
         '    <Version>$(YahadInfrastructureVersion)</Version>\n  </PropertyGroup>\n</Project>\n')


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


class SetupPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory(prefix='yahad-full-package-test-');self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.repo=self.root/'repo';self.prepared=self.root/'payload';self.repo.mkdir();self.prepared.mkdir()
        self.tracked=['package.json','package-lock.json','src/database-schema.ts','src/db.ts','src/main.ts']
        source={
            'package.json':json.dumps({'version':VERSION,'yahadInfrastructureContract':1}),
            'package-lock.json':json.dumps({'version':VERSION,'packages':{'':{'version':VERSION},'node_modules/fastify':{'version':'5.12.7'},'node_modules/vite':{'version':'7.3.7','dev':True}}}),
            'src/database-schema.ts':'export const DATABASE_SCHEMA = 6;\nexport const DATABASE_MIGRATIONS_FROM = [1, 2] as const;\n',
            'src/db.ts':"import type { DatabaseSync } from 'node:sqlite';\nexport function migrate() {}\n",
            'src/main.ts':"import { migrate } from './db.js';\n",
        }
        for name,value in source.items():self.put(self.repo,name,value)
        self.put(self.repo,'installer/Directory.Build.props',PROPS)
        self.put(self.prepared,'app/package.json',source['package.json'])
        for name in full.bundle.SERVER:self.put(self.prepared,name,'// bundled '+name+'\n')
        self.server=dict(format='yahad-server-bundle',schema=1,sources={name:hashlib.sha256(source[name].encode()).hexdigest() for name in source if name.startswith('src/')},
            packages=[dict(path='node_modules/fastify',name='fastify',version='5.12.7',noticeFiles=[])],outputs=[
            self.output('main',['src/main.ts','src/db.ts','src/database-schema.ts'],['node_modules/fastify'])])
        self.build=dict(appSourceCommit=SOURCE,packagerSourceCommit=SOURCE,databaseSchema=6,databaseMigrationsFrom=[1,2])
        self.save_build();self.manifest=dict(schema=1,kind='windows-runtime-candidate',profile='full',target='win-x64',version=VERSION,files=[])
        self.refresh_records()

    def put(self,root,name,value):
        path=root/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(value.encode() if isinstance(value,str) else value);return path

    def output(self,stem,sources,packages):
        bytes_=(self.prepared/'app/server'/(stem+'.mjs')).read_bytes()
        return dict(path='app/server/'+stem+'.mjs',entry='src/'+stem+'.ts',size=len(bytes_),sha256=hashlib.sha256(bytes_).hexdigest(),sources=sources,packages=packages)

    def save_build(self):
        self.put(self.prepared,'legal/build-inputs.json',json.dumps(self.build));self.put(self.prepared,'legal/server-bundle.json',json.dumps(self.server))

    def refresh_records(self):
        self.manifest['files']=[full._record(p.relative_to(self.prepared).as_posix(),p) for p in sorted(self.prepared.rglob('*')) if p.is_file()]

    def source(self):return full.validate_payload_source(self.repo,self.prepared,self.manifest,VERSION,SOURCE,self.tracked)

    def native(self,artifact='Yahad.ServiceHost',root=None):
        root=root or self.root/artifact;root.mkdir(parents=True)
        for name in (artifact+'.exe',artifact+'.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll'):self.put(root,name,image())
        frameworks=[dict(name='Microsoft.NETCore.App',version=full.RUNTIME_VERSION)]
        self.put(root,artifact+'.runtimeconfig.json',json.dumps(dict(runtimeOptions=dict(includedFrameworks=frameworks))))
        libraries={'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/'+full.RUNTIME_VERSION:dict(runtime={artifact+'.dll':{},'System.Private.CoreLib.dll':{}},native={'coreclr.dll':{},'hostfxr.dll':{},'hostpolicy.dll':{}})}
        self.put(root,artifact+'.deps.json',json.dumps(dict(runtimeTarget=dict(name='net10.0/win-x64'),targets={'net10.0/win-x64':libraries})))
        return root

    def engine(self):return self.put(self.root/'engine','Yahad.Setup.exe',image())

    def test_bundle_sources_and_migration_declaration_are_taken_from_actual_bytes(self):
        self.assertEqual(self.source(),(6,[1,2]))
        self.put(self.repo,'src/db.ts','changed bytes')
        with self.assertRaisesRegex(full.PackageError,'source bytes'):self.source()

    def test_migration_declaration_must_match_build_record(self):
        for value in ([1],[1,2,3],None,[1,True]):
            with self.subTest(value=value):
                self.build['databaseMigrationsFrom']=value;self.save_build()
                with self.assertRaisesRegex(full.PackageError,'schema'):self.source()

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
            with self.assertRaisesRegex(full.PackageError,'schema'):self.source()

    def test_shipped_server_bytes_are_pinned_by_the_bundle_record(self):
        self.put(self.prepared,'app/server/main.mjs','// replaced\n');self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'Server bundle record differs'):self.source()

    def test_bundle_entries_are_fixed(self):
        self.server['outputs'][0]['entry']='src/db.ts';self.save_build();self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'record differs'):self.source()
        self.server['outputs'].pop(0);self.save_build();self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'Server bundle record'):self.source()

    def test_bundled_packages_must_be_locked_production_packages(self):
        for package in (dict(path='node_modules/fastify',version='5.0.0'),dict(path='node_modules/vite',version='7.3.7'),dict(path='node_modules/absent',version='1.0.0')):
            with self.subTest(package=package):
                self.server['packages']=[dict(package,name=package['path'].rsplit('/',1)[1],noticeFiles=[])];self.save_build();self.refresh_records()
                with self.assertRaisesRegex(full.PackageError,'not locked'):self.source()

    def test_schema_sources_must_be_unique_sorted_and_strictly_older(self):
        for values in ('2,1','1,1','0,1','1,6'):
            self.put(self.repo,'src/database-schema.ts','export const DATABASE_SCHEMA = 6;\nexport const DATABASE_MIGRATIONS_FROM = ['+values+'] as const;\n')
            with self.subTest(values=values),self.assertRaisesRegex(full.PackageError,'migration declaration'):full.source_schema(self.repo)

    def test_untracked_source_and_synthetic_cache_cannot_join_verified_payload(self):
        self.put(self.repo,'src/untracked.ts','extra');self.server['sources']['src/untracked.ts']=hashlib.sha256(b'extra').hexdigest();self.save_build();self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'source bytes'):self.source()
        del self.server['sources']['src/untracked.ts'];self.save_build()
        for name in ('app/src/main.ts','app/node_modules/fastify/package.json','app/package-lock.json'):
            with self.subTest(name=name):
                self.put(self.prepared,name,'extra');self.refresh_records()
                with self.assertRaisesRegex(full.PackageError,'source or dependency tree'):self.source()
                (self.prepared/name).unlink()
        self.put(self.prepared,'app/.local/synthetic.js','extra');self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'Private member'):self.source()

    def test_platform_helper_is_never_application_payload(self):
        # Platform is infrastructure (infra/platform/), never under the app.
        for name in ('app/native/Yahad.Platform.exe','app/Native/coreclr.dll'):
            with self.subTest(name=name):
                self.put(self.prepared,name,image());self.refresh_records()
                with self.assertRaisesRegex(full.PackageError,'Native helper in application payload'):self.source()
                (self.prepared/name).unlink();self.refresh_records()
        self.source()

    def test_release_identity_reads_both_tracks_and_the_app_contract(self):
        self.assertEqual(full.release_identity(self.repo),(VERSION,INFRA,1))
        for package in ({'version':VERSION},{'version':VERSION,'yahadInfrastructureContract':2},{'version':VERSION,'yahadInfrastructureContract':0},
                        {'version':VERSION,'yahadInfrastructureContract':True},{'version':VERSION,'yahadInfrastructureContract':'1'},
                        {'version':VERSION,'yahadInfrastructureContract':1.0}):
            with self.subTest(package=package):
                self.put(self.repo,'package.json',json.dumps(package))
                with self.assertRaisesRegex(ValueError,'infrastructure contract'):full.release_identity(self.repo)
        self.put(self.repo,'package.json',json.dumps({'version':VERSION,'yahadInfrastructureContract':1}))
        self.put(self.repo,'installer/Directory.Build.props',PROPS.replace('<YahadInfrastructureVersion>1.0.0','<YahadInfrastructureVersion>01.0.0'))
        with self.assertRaisesRegex(ValueError,'Infrastructure identity'):full.release_identity(self.repo)

    def test_duplicate_json_and_case_directory_collisions_are_refused(self):
        with self.assertRaisesRegex(full.PackageError,'Duplicate JSON'):full._json(b'{"sourceCommit":"a","sourceCommit":"b"}')
        for names in (['A.dll','a.dll'],['folder/one.dll','Folder/two.dll'],['folder','folder/file.dll']):
            with self.subTest(names=names),self.assertRaises(full.PackageError):full._closed_names(names,component=True)

    def test_private_component_names_are_refused_without_creating_links(self):
        for name in ('../escape','secrets/token.txt','db.sqlite','x.pdb','.env.production','.local/cache.txt','CON.txt','odd:stream','folder/backups/file','datasets/id/data'):
            with self.subTest(name=name),self.assertRaises(full.PackageError):full._closed_names([name],component=True)
        file=self.put(self.root,'ordinary.dll',b'x');original=Path.lstat
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
        audit.assert_not_called();self.put(root,'coreclr.dll',image());self.put(root,'Yahad.ServiceHost.exe',image('private-missing.dll'))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'private native dependency'):full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)
        audit.assert_not_called()

    def test_every_declared_private_runtime_asset_must_be_present(self):
        root=self.native();path=root/'Yahad.ServiceHost.deps.json';deps=json.loads(path.read_text())
        next(iter(deps['targets']['net10.0/win-x64'].values()))['runtime']['missing.dll']={};path.write_text(json.dumps(deps))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'Missing private .NET dependency'):full.validate_native(root,'Yahad.ServiceHost',INFRA,SOURCE,self.repo)
        audit.assert_not_called()

    def test_native_input_change_during_audit_is_not_blessed_by_source_description(self):
        root=self.native()
        def changed(*args):self.put(root,'coreclr.dll',image()+b'changed');return describe('Yahad.ServiceHost')
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
        with patch.object(full,'describe_build_artifact',return_value={k:v for k,v in describe('Yahad.Setup').items() if k!='embeddedPackage'}),self.assertRaisesRegex(full.PackageError,'payload-free'):
            full.validate_engine(engine,INFRA,SOURCE,self.repo)
        renamed=self.put(self.root/'other','Yahad.Setup-0.6.0.exe',image())
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'engine image'):full.validate_engine(renamed,INFRA,SOURCE,self.repo)
        audit.assert_not_called()
        # A single-file image has no siblings to load a private library from.
        self.put(self.root/'engine','Yahad.Setup.exe',image('hostfxr.dll'))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'private native dependency'):full.validate_engine(engine,INFRA,SOURCE,self.repo)
        audit.assert_not_called()

    def test_tree_record_is_ordinal_sorted_camel_case_and_bound(self):
        record=lambda data:dict(size=len(data),sha256=hashlib.sha256(data).hexdigest())
        # Ordinal is UTF-16 code units: a surrogate pair sorts before U+FF01, unlike code points.
        members={'app/b':record(b'b'),'app/B':record(b'B'),'app/！':record(b'x'),'app/\U0001f600':record(b'y'),'app/a/z':record(b'z')}
        data=full.tree_record(VERSION,SOURCE,members);value=json.loads(data)
        self.assertFalse(data.startswith(b'\xef\xbb\xbf'));self.assertEqual(sorted(value),['files','format','sourceCommit','version'])
        self.assertEqual((value['format'],value['version'],value['sourceCommit']),(1,VERSION,SOURCE))
        self.assertEqual([f['path'] for f in value['files']],['app/B','app/a/z','app/b','app/\U0001f600','app/！'])
        self.assertTrue(all(sorted(f)==['path','sha256','size'] for f in value['files']))
        with self.assertRaisesRegex(full.PackageError,'Tree record bound'):full.tree_record(VERSION,SOURCE,{})

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
                     'apps/app/Native/coreclr.dll','apps/runtime/node/node.exe','apps/infra-tree.json','infra/app-tree.json','install-manifest.json'):
            with self.subTest(name=name),self.assertRaises(full.PackageError):full._write_archive(self.root/'foreign.zip',{name:b'refused'})
        self.assertFalse((self.root/'foreign.zip').exists())

    def test_archive_refuses_changed_verified_bytes_and_preserves_existing_output(self):
        file=self.put(self.root,'one.dll',b'original');entries={'infra/host/one.dll':file}
        expected={name:full._record(name,value) for name,value in entries.items()};file.write_bytes(b'changed')
        with self.assertRaisesRegex(full.PackageError,'input changed'):full._write_archive(self.root/'changed.zip',entries,expected)
        output=self.put(self.root,'existing.zip',b'preserve')
        with self.assertRaises(FileExistsError):full._write_archive(output,entries)
        self.assertEqual(output.read_bytes(),b'preserve')


class InstallPackageBuildTests(unittest.TestCase):
    """End-to-end build() with the Git checks, archive extraction and the PE/audit boundary stubbed."""
    LEGAL = ('legal/SBOM.json','legal/runtime-lock.json','legal/Node-LICENSE.txt','legal/ThirdPartyNotices-server.txt')

    def setUp(self):
        self.base = SetupPackageTests('test_bundle_sources_and_migration_declaration_are_taken_from_actual_bytes'); self.base.setUp(); self.addCleanup(self.base.tearDown)
        b = self.base
        for name in ('app/dist/index.html', 'app/deploy/lan-capabilities.json', 'app/deploy/lan-help.html', 'runtime/node/node.exe', 'runtime/caddy/caddy.exe', *self.LEGAL):
            b.put(b.prepared, name, image() if name.endswith('.exe') else b'{}')
        b.refresh_records(); b.put(b.prepared, 'bundle.json', full._encode(b.manifest))
        self.host = b.native('Yahad.ServiceHost'); self.updater = b.native('Yahad.Updater'); self.platform = b.native('Yahad.Platform')
        self.engine = b.engine(); self.output = b.root/'out'

    def build(self, audit=None):
        b = self.base
        def extract(archive, output, retain_manifest=False): shutil.copytree(b.prepared, output)
        with patch.object(full, '_committed_source', return_value=(SOURCE, b.tracked)), patch.object(full.verify_archive, 'verify', side_effect=extract), \
             patch.object(full, 'describe_build_artifact', side_effect=audit or (lambda path, flag, repo: describe(Path(path).stem))), \
             patch.object(full, '_unlinked', side_effect=lambda p, file=False: Path(p).absolute()):
            return full.build(b.repo, b.root/'payload.zip', self.host, self.updater, self.platform, self.engine, self.output)

    def written(self):
        manifest = json.loads((self.output/'install-manifest.json').read_bytes())
        with zipfile.ZipFile(self.output/'install.zip') as archive: members = {name: archive.read(name) for name in archive.namelist()}
        return manifest, members

    def test_manifest_is_format_two_with_exact_keys_and_both_identities(self):
        result = self.build(); manifest, members = self.written(); data = (self.output/'install-manifest.json').read_bytes()
        self.assertFalse(data.startswith(b'\xef\xbb\xbf'))
        self.assertEqual(sorted(manifest), ['App', 'Files', 'Format', 'Infrastructure', 'ZipSha256'])
        self.assertIs(type(manifest['Format']), int); self.assertEqual(manifest['Format'], 2)
        self.assertEqual(manifest['Infrastructure'], dict(Version='1.0.0', ContractMin=1, ContractMax=1, SourceCommit=SOURCE,
            TreeSha256=hashlib.sha256(members['infra/infra-tree.json']).hexdigest()))
        self.assertEqual(manifest['App'], dict(Version=VERSION, SourceCommit=SOURCE, RequiresContract=1,
            TreeSha256=hashlib.sha256(members['apps/app-tree.json']).hexdigest(), DatabaseSchema=6, DatabaseMigrationsFrom=[1, 2]))
        self.assertEqual(manifest['ZipSha256'], hashlib.sha256((self.output/'install.zip').read_bytes()).hexdigest())
        self.assertEqual({p.name for p in self.output.iterdir()}, {'install.zip', 'install-manifest.json'})
        self.assertEqual([r['Path'] for r in manifest['Files']], list(members)); self.assertEqual(result['files'], len(members))
        for record in manifest['Files']:
            self.assertEqual(sorted(record), ['Path', 'Sha256', 'Size'])
            self.assertEqual((record['Size'], record['Sha256']), (len(members[record['Path']]), hashlib.sha256(members[record['Path']]).hexdigest()))
        self.assertEqual((result['infrastructureVersion'], result['appVersion'], result['sourceCommit']), ('1.0.0', VERSION, SOURCE))

    def test_bundle_manifest_and_payload_digest_leave_the_install_package(self):
        self.build(); manifest, members = self.written()
        self.assertNotIn('PayloadDigest', manifest)
        self.assertFalse(any(name.rsplit('/', 1)[-1] == 'bundle.json' or name.startswith('payload/') for name in members))

    def test_tree_records_list_every_other_member_of_their_tree_exactly_once(self):
        self.build(); manifest, members = self.written()
        for record_name, root, version, prefixes in (('infra/infra-tree.json', 'infra/', '1.0.0', full.INFRA_PREFIXES), ('apps/app-tree.json', 'apps/', VERSION, ('app/',))):
            with self.subTest(tree=record_name):
                data = members[record_name]; self.assertFalse(data.startswith(b'\xef\xbb\xbf')); value = json.loads(data)
                self.assertEqual(sorted(value), ['files', 'format', 'sourceCommit', 'version'])
                self.assertEqual((value['format'], value['version'], value['sourceCommit']), (1, version, SOURCE))
                paths = [f['path'] for f in value['files']]
                self.assertEqual(paths, sorted(paths, key=lambda p: p.encode('utf-16-be'))); self.assertEqual(len(paths), len({p.casefold() for p in paths}))
                self.assertEqual(set(paths), {n[len(root):] for n in members if n.startswith(root) and n != record_name})
                self.assertNotIn(record_name[len(root):], paths)
                self.assertTrue(all(p.startswith(prefixes) for p in paths))
                for f in value['files']: self.assertEqual((f['size'], f['sha256']), (len(members[root+f['path']]), hashlib.sha256(members[root+f['path']]).hexdigest()))
        self.assertTrue(all(n.startswith(('infra/', 'apps/')) for n in members))
        self.assertFalse(any(n.casefold().startswith('apps/app/native/') for n in members))

    def test_components_payload_and_engine_land_in_their_trees(self):
        self.build(); _, members = self.written()
        for prefix, root in (('infra/host/', self.host), ('infra/updater/', self.updater), ('infra/platform/', self.platform)):
            self.assertEqual({n for n in members if n.startswith(prefix)}, {prefix+p.name for p in root.iterdir()})
        self.assertEqual({n for n in members if n.startswith('infra/setup/')}, {'infra/setup/Yahad.Setup.exe'})
        self.assertEqual(members['infra/setup/Yahad.Setup.exe'], self.engine.read_bytes())
        self.assertEqual({n for n in members if n.startswith('infra/runtime/')}, {'infra/runtime/node/node.exe', 'infra/runtime/caddy/caddy.exe'})
        self.assertTrue(full.REQUIRED_MEMBERS <= set(members))

    def test_legal_records_split_between_app_and_infrastructure(self):
        self.build(); _, members = self.written()
        self.assertEqual({n for n in members if n.startswith('apps/app/legal/')},
                         {'apps/app/legal/server-bundle.json', 'apps/app/legal/ThirdPartyNotices-server.txt', 'apps/app/legal/build-inputs.json'})
        self.assertEqual({n for n in members if n.startswith('infra/legal/')}, {'infra/legal/SBOM.json', 'infra/legal/runtime-lock.json', 'infra/legal/Node-LICENSE.txt'})
        self.assertEqual(members['apps/app/legal/build-inputs.json'], (self.base.prepared/'legal/build-inputs.json').read_bytes())

    def test_app_contract_must_be_declared_inside_the_infrastructure_range(self):
        for package in ({'version': VERSION}, {'version': VERSION, 'yahadInfrastructureContract': 2}, {'version': VERSION, 'yahadInfrastructureContract': True}):
            with self.subTest(package=package):
                self.base.put(self.base.repo, 'package.json', json.dumps(package))
                with self.assertRaisesRegex(ValueError, 'infrastructure contract'): self.build()
                self.assertFalse(self.output.exists())

    def test_engine_is_a_required_input(self):
        self.engine.unlink()
        with self.assertRaises(OSError): self.build()
        self.assertFalse((self.output/'install-manifest.json').exists())

    def test_mismatched_describe_identity_is_refused_before_manifest_is_written(self):
        for artifact in ('Yahad.ServiceHost', 'Yahad.Updater', 'Yahad.Platform', 'Yahad.Setup'):
            for changed in (dict(infrastructureVersion='1.0.1', version='1.0.1+'+SOURCE), dict(sourceCommit='b'*40, version='1.0.0+'+'b'*40),
                            dict(applicationVersion=VERSION), dict(artifact='foreign')):
                with self.subTest(artifact=artifact, changed=changed):
                    shutil.rmtree(self.output, ignore_errors=True)
                    def audit(path, flag, repo, artifact=artifact, changed=changed):
                        stem = Path(path).stem; return describe(stem, **changed) if stem == artifact else describe(stem)
                    with self.assertRaises(ValueError): self.build(audit)
                    self.assertFalse((self.output/'install-manifest.json').exists())

    def test_platform_closure_must_be_a_complete_private_runtime(self):
        (self.platform/'coreclr.dll').unlink()
        with self.assertRaisesRegex(full.PackageError, 'Incomplete private'): self.build()
        self.assertFalse((self.output/'install-manifest.json').exists())

    def test_required_members_must_all_be_present(self):
        full.require_members(full.REQUIRED_MEMBERS)
        for name in sorted(full.REQUIRED_MEMBERS):
            with self.subTest(name=name), self.assertRaisesRegex(full.PackageError, 'Required package member'): full.require_members(full.REQUIRED_MEMBERS - {name})

    def test_payload_outside_the_declared_namespaces_is_refused(self):
        b = self.base; b.put(b.prepared, 'extra/file.txt', b'x'); b.refresh_records(); (b.prepared/'bundle.json').write_bytes(full._encode(b.manifest))
        with self.assertRaisesRegex(full.PackageError, 'payload namespace'): self.build()

    def test_output_must_be_new_and_outside_input_closures(self):
        self.output.mkdir()
        with self.assertRaisesRegex(full.PackageError, 'New output'): self.build()
        for closure in (self.host, self.updater, self.platform, self.engine.parent):
            self.output = closure/'nested-out'
            with self.subTest(closure=closure.name), self.assertRaisesRegex(full.PackageError, 'New output'): self.build()

    def test_host_change_after_validation_is_refused(self):
        b = self.base; original = full.validate_native
        def validated(*args):
            files = original(*args)
            if args[1] == 'Yahad.ServiceHost': b.put(self.host, 'coreclr.dll', image()+b'changed')
            return files
        with patch.object(full, 'validate_native', side_effect=validated), self.assertRaises(full.PackageError): self.build()
        self.assertFalse((self.output/'install-manifest.json').exists())

    def test_engine_change_after_validation_is_refused(self):
        original = full.validate_engine
        def validated(*args):
            files = original(*args); self.engine.write_bytes(image()+b'changed'); return files
        with patch.object(full, 'validate_engine', side_effect=validated), self.assertRaises(full.PackageError): self.build()
        self.assertFalse((self.output/'install-manifest.json').exists())


if __name__=='__main__':unittest.main(verbosity=2)
