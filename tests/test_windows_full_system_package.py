"""Synthetic packaging contracts; fixture executables are never invoked."""
import copy
import hashlib
import json
from pathlib import Path
import stat
import struct
import tempfile
import types
import unittest
from unittest.mock import patch
import zipfile

from installer.windows import full_system_package as full


VERSION, SOURCE = '0.6.0', 'a'*40


def image(imported='kernel32.dll'):
    data=bytearray(2048);data[:2]=b'MZ';struct.pack_into('<I',data,0x3c,0x80);data[0x80:0x84]=b'PE\0\0'
    struct.pack_into('<HH',data,0x84,0x8664,1);struct.pack_into('<H',data,0x94,240)
    optional=0x98;struct.pack_into('<H',data,optional,0x20b);struct.pack_into('<I',data,optional+60,512);struct.pack_into('<I',data,optional+108,16)
    struct.pack_into('<IIII',data,optional+240+8,1536,0x1000,1536,512)
    struct.pack_into('<II',data,optional+120,0x1000,40);struct.pack_into('<I',data,512+12,0x1000+900-512)
    value=imported.encode('ascii')+b'\0';data[900:900+len(value)]=value
    return bytes(data)


class FullSystemPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory(prefix='yahad-full-package-test-');self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.repo=self.root/'repo';self.prepared=self.root/'payload';self.repo.mkdir();self.prepared.mkdir()
        self.tracked=['package.json','package-lock.json','src/database-schema.ts','src/native-migrate-v1.ts','src/db.ts','src/main.ts','src/native-supervisor.ts']
        source={
            'package.json':json.dumps({'version':VERSION}),
            'package-lock.json':json.dumps({'version':VERSION,'packages':{'':{'version':VERSION},'node_modules/fastify':{'version':'5.12.7'},'node_modules/vite':{'version':'7.3.7','dev':True}}}),
            'src/database-schema.ts':'export const DATABASE_SCHEMA = 6;\nexport const DATABASE_MIGRATIONS_FROM = [1, 2] as const;\n',
            'src/native-migrate-v1.ts':"import { DATABASE_SCHEMA } from './database-schema.js';\nimport { migrate } from './db.js';\n",
            'src/db.ts':"import type { DatabaseSync } from 'node:sqlite';\nexport function migrate() {}\n",
            'src/main.ts':"import { migrate } from './db.js';\n",
            'src/native-supervisor.ts':"import { fork } from 'node:child_process';\n",
        }
        for name,value in source.items():self.put(self.repo,name,value)
        self.put(self.prepared,'app/package.json',source['package.json'])
        for name in full.bundle.SERVER:self.put(self.prepared,name,'// bundled '+name+'\n')
        self.server=dict(format='yahad-server-bundle',schema=1,sources={name:hashlib.sha256(source[name].encode()).hexdigest() for name in source if name.startswith('src/')},
            packages=[dict(path='node_modules/fastify',name='fastify',version='5.12.7',noticeFiles=[])],outputs=[
            self.output('main',['src/main.ts','src/db.ts','src/database-schema.ts'],['node_modules/fastify']),
            self.output('native-migrate-v1',['src/native-migrate-v1.ts','src/db.ts','src/database-schema.ts'],[]),
            self.output('native-supervisor',['src/native-supervisor.ts'],[])])
        self.build=dict(appSourceCommit=SOURCE,helperSourceCommit=SOURCE,packagerSourceCommit=SOURCE,databaseSchema=6,databaseMigrationsFrom=[1,2])
        self.save_build();self.manifest=dict(schema=1,kind='windows-runtime-candidate',profile='full',target='win-x64',version=VERSION,authority='application-updater',files=[])
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

    def native(self,artifact='Yahad.Authority'):
        root=self.root/artifact;root.mkdir()
        for name in (artifact+'.exe',artifact+'.dll','coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll'):self.put(root,name,image())
        frameworks=[dict(name='Microsoft.NETCore.App',version=full.RUNTIME_VERSION)]
        if artifact=='Yahad.Owner':frameworks.append(dict(name='Microsoft.WindowsDesktop.App',version=full.RUNTIME_VERSION))
        self.put(root,artifact+'.runtimeconfig.json',json.dumps(dict(runtimeOptions=dict(includedFrameworks=frameworks))))
        libraries={'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/'+full.RUNTIME_VERSION:dict(runtime={artifact+'.dll':{},'System.Private.CoreLib.dll':{}},native={'coreclr.dll':{},'hostfxr.dll':{},'hostpolicy.dll':{}})}
        self.put(root,artifact+'.deps.json',json.dumps(dict(runtimeTarget=dict(name='net10.0/win-x64'),targets={'net10.0/win-x64':libraries})))
        return root

    def audit(self,artifact):return dict(artifact=artifact,applicationVersion=VERSION,sourceCommit=SOURCE,version='1.0.0+'+SOURCE)

    def components(self):
        values=[];entries={}
        for kind,(_,entry,_) in full.COMPONENTS.items():
            descriptor,members=full._component(kind,{entry:image()},VERSION,SOURCE,node=dict(version='24.22.2',sourceUrl='https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip'))
            values.append(descriptor);entries.update(members)
        return values,entries

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
        for field in ('appSourceCommit','helperSourceCommit','packagerSourceCommit'):
            with self.subTest(field=field):
                before=self.build[field];self.build[field]='b'*40;self.save_build()
                with self.assertRaisesRegex(full.PackageError,'Mixed source'):self.source()
                self.build[field]=before
        self.save_build()

    def test_schema_cannot_be_relabelled_or_coerced_from_boolean(self):
        for value in (7,True):
            self.build['databaseSchema']=value;self.save_build()
            with self.assertRaisesRegex(full.PackageError,'schema'):self.source()

    def test_shipped_migration_bytes_are_pinned_by_the_bundle_record(self):
        self.put(self.prepared,'app/server/native-migrate-v1.mjs','// replaced\n');self.refresh_records()
        with self.assertRaisesRegex(full.PackageError,'Server bundle record differs'):self.source()

    def test_migration_bundle_needs_schema_source_and_no_package(self):
        migration=self.server['outputs'][1]
        for change in (dict(sources=['src/native-migrate-v1.ts','src/db.ts']),dict(packages=['node_modules/fastify']),dict(packages=None)):
            with self.subTest(change=change):
                original=dict(migration);migration.update(change);self.save_build();self.refresh_records()
                with self.assertRaisesRegex(full.PackageError,'Migration bundle closure'):self.source()
                migration.clear();migration.update(original)

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

    def test_native_audit_is_source_bound_and_never_executed_by_tests(self):
        root=self.native();description=self.audit('Yahad.Authority')
        with patch.object(full,'describe_build_artifact',return_value=description) as audit:
            files=full.validate_native(root,'Yahad.Authority',VERSION,SOURCE,self.repo)
            self.assertIn('coreclr.dll',files);audit.assert_called_once_with(root/'Yahad.Authority.exe','--describe',self.repo)
        with patch.object(full,'describe_build_artifact',return_value=dict(description,sourceCommit='b'*40)),self.assertRaises(ValueError):full.validate_native(root,'Yahad.Authority',VERSION,SOURCE,self.repo)

    def test_owner_needs_private_desktop_runtime_and_exact_runtime_version(self):
        root=self.native('Yahad.Owner');path=root/'Yahad.Owner.runtimeconfig.json';valid=json.loads(path.read_text())
        with patch.object(full,'describe_build_artifact',return_value=self.audit('Yahad.Owner')):full.validate_native(root,'Yahad.Owner',VERSION,SOURCE,self.repo)
        for frameworks in ([dict(name='Microsoft.NETCore.App',version=full.RUNTIME_VERSION)], [dict(name='Microsoft.NETCore.App',version=full.RUNTIME_VERSION),dict(name='Microsoft.WindowsDesktop.App',version='10.0.13')]):
            path.write_text(json.dumps(dict(runtimeOptions=dict(includedFrameworks=frameworks))))
            with self.subTest(frameworks=frameworks),self.assertRaisesRegex(full.PackageError,'framework closure'):full.validate_native(root,'Yahad.Owner',VERSION,SOURCE,self.repo)
        path.write_text(json.dumps(valid))

    def test_missing_dependency_and_nonprivate_native_import_fail_before_audit(self):
        root=self.native();(root/'coreclr.dll').unlink()
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'Incomplete private'):full.validate_native(root,'Yahad.Authority',VERSION,SOURCE,self.repo)
        audit.assert_not_called();self.put(root,'coreclr.dll',image());self.put(root,'Yahad.Authority.exe',image('private-missing.dll'))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'private native dependency'):full.validate_native(root,'Yahad.Authority',VERSION,SOURCE,self.repo)
        audit.assert_not_called()

    def test_every_declared_private_runtime_asset_must_be_present(self):
        root=self.native();path=root/'Yahad.Authority.deps.json';deps=json.loads(path.read_text())
        next(iter(deps['targets']['net10.0/win-x64'].values()))['runtime']['missing.dll']={};path.write_text(json.dumps(deps))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'Missing private .NET dependency'):full.validate_native(root,'Yahad.Authority',VERSION,SOURCE,self.repo)
        audit.assert_not_called()

    def test_probe_is_exact_verified_application_node_and_license_not_a_url_claim(self):
        node=self.root/'node';node.mkdir();binary=image();license=b'synthetic notice'
        self.put(node,'node.exe',binary);self.put(node,'LICENSE',license)
        self.put(self.prepared,'runtime/node/node.exe',binary);self.put(self.prepared,'legal/Node-LICENSE.txt',license)
        pin=dict(path='runtime/node/node.exe',version='24.22.2',sourceUrl='https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip',sha256=hashlib.sha256(binary).hexdigest())
        self.assertEqual(set(full.validate_probe(node,self.prepared,pin)),{'node/node.exe','Node-LICENSE.txt'})
        for changed in (dict(pin,sourceUrl='https://example.com/node.zip'),dict(pin,sha256='0'*64)):
            with self.subTest(pin=changed),self.assertRaisesRegex(full.PackageError,'Node pin'):full.validate_probe(node,self.prepared,changed)
        self.put(node,'LICENSE',b'wrong notice')
        with self.assertRaisesRegex(full.PackageError,'Node pin'):full.validate_probe(node,self.prepared,pin)

    def test_native_input_change_during_audit_is_not_blessed_by_source_description(self):
        root=self.native()
        def changed(*args):self.put(root,'coreclr.dll',image()+b'changed');return self.audit('Yahad.Authority')
        with patch.object(full,'describe_build_artifact',side_effect=changed),self.assertRaisesRegex(full.PackageError,'closure changed'):full.validate_native(root,'Yahad.Authority',VERSION,SOURCE,self.repo)

    def test_system_manifest_matches_native_full_profile_and_source_binding(self):
        components,entries=self.components();application=full._encode(self.manifest)
        manifest=json.loads(full.system_manifest(VERSION,SOURCE,6,[1,2],application,components))
        self.assertEqual(manifest['format'],2);self.assertEqual(manifest['minimumWindowsBuild'],19045)
        self.assertEqual(manifest['sourceCommit'],SOURCE);self.assertEqual(manifest['applicationManifestSha256'],hashlib.sha256(application).hexdigest())
        self.assertEqual(manifest['protocols']['policy'],dict(minimumReader=2,maximumReader=2,writer=2))
        self.assertEqual({v['writer'] for k,v in manifest['protocols'].items() if k!='policy'},{1})
        for component in components:
            identity=json.loads(entries[component['prefix']+'/build-identity.json'])
            self.assertEqual(identity['sourceCommit'],SOURCE);self.assertEqual(identity['version'],VERSION);self.assertEqual(identity['component'],component['kind'])
        changed=copy.deepcopy(components);changed[0]['prefix']='bootstrap/arbitrary'
        with self.assertRaisesRegex(full.PackageError,'namespace'):full.system_manifest(VERSION,SOURCE,6,[1,2],application,changed)
        with self.assertRaisesRegex(full.PackageError,'Application manifest binding'):full.system_manifest('0.6.1',SOURCE,6,[1,2],application,components)

    def test_generated_identity_cannot_be_injected_into_caller_closure(self):
        root=self.native();self.put(root,'build-identity.json','{}')
        with self.assertRaisesRegex(full.PackageError,'must not be supplied'):full._inventory(root)

    def test_archive_namespace_bytes_and_records_are_exact_and_deterministic(self):
        components,entries=self.components();application=full._encode(self.manifest)
        entries.update({'payload/bundle.json':application,'system-manifest.json':full.system_manifest(VERSION,SOURCE,6,[1,2],application,components)})
        records=full._write_archive(self.root/'a.zip',entries);full._write_archive(self.root/'b.zip',entries)
        self.assertEqual((self.root/'a.zip').read_bytes(),(self.root/'b.zip').read_bytes())
        with zipfile.ZipFile(self.root/'a.zip') as archive:
            self.assertEqual(set(archive.namelist()),{r['Path'] for r in records})
            for record in records:self.assertEqual(hashlib.sha256(archive.read(record['Path'])).hexdigest(),record['Sha256'])
        with self.assertRaisesRegex(full.PackageError,'namespace'):full._write_archive(self.root/'bad.zip',{'unapproved/code.exe':b'not executed'})
        self.assertFalse((self.root/'bad.zip').exists())
        with self.assertRaisesRegex(full.PackageError,'namespace'):full._write_archive(self.root/'duplicate-host.zip',{'host/Yahad.ServiceHost.exe':b'no duplicate namespace'})
        self.assertFalse((self.root/'duplicate-host.zip').exists())

    def test_archive_refuses_changed_verified_bytes_and_preserves_existing_output(self):
        file=self.put(self.root,'one.dll',b'original');entries={'bootstrap/authority/one.dll':file}
        expected={name:full._record(name,value) for name,value in entries.items()};file.write_bytes(b'changed')
        with self.assertRaisesRegex(full.PackageError,'input changed'):full._write_archive(self.root/'changed.zip',entries,expected)
        output=self.put(self.root,'existing.zip',b'preserve')
        with self.assertRaises(FileExistsError):full._write_archive(output,entries)
        self.assertEqual(output.read_bytes(),b'preserve')


if __name__=='__main__':unittest.main(verbosity=2)
