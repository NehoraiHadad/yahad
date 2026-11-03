"""Synthetic runtime-only (Format 1) packaging contracts; fixture executables are never invoked."""
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


VERSION, SOURCE = '0.6.0', 'a'*40


def image(imported='kernel32.dll'):
    data=bytearray(2048);data[:2]=b'MZ';struct.pack_into('<I',data,0x3c,0x80);data[0x80:0x84]=b'PE\0\0'
    struct.pack_into('<HH',data,0x84,0x8664,1);struct.pack_into('<H',data,0x94,240)
    optional=0x98;struct.pack_into('<H',data,optional,0x20b);struct.pack_into('<I',data,optional+60,512);struct.pack_into('<I',data,optional+108,16)
    struct.pack_into('<IIII',data,optional+240+8,1536,0x1000,1536,512)
    struct.pack_into('<II',data,optional+120,0x1000,40);struct.pack_into('<I',data,512+12,0x1000+900-512)
    value=imported.encode('ascii')+b'\0';data[900:900+len(value)]=value
    return bytes(data)


class SetupPackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory(prefix='yahad-full-package-test-');self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name);self.repo=self.root/'repo';self.prepared=self.root/'payload';self.repo.mkdir();self.prepared.mkdir()
        self.tracked=['package.json','package-lock.json','src/database-schema.ts','src/db.ts','src/main.ts']
        source={
            'package.json':json.dumps({'version':VERSION}),
            'package-lock.json':json.dumps({'version':VERSION,'packages':{'':{'version':VERSION},'node_modules/fastify':{'version':'5.12.7'},'node_modules/vite':{'version':'7.3.7','dev':True}}}),
            'src/database-schema.ts':'export const DATABASE_SCHEMA = 6;\nexport const DATABASE_MIGRATIONS_FROM = [1, 2] as const;\n',
            'src/db.ts':"import type { DatabaseSync } from 'node:sqlite';\nexport function migrate() {}\n",
            'src/main.ts':"import { migrate } from './db.js';\n",
        }
        for name,value in source.items():self.put(self.repo,name,value)
        self.put(self.prepared,'app/package.json',source['package.json'])
        for name in full.bundle.SERVER:self.put(self.prepared,name,'// bundled '+name+'\n')
        self.server=dict(format='yahad-server-bundle',schema=1,sources={name:hashlib.sha256(source[name].encode()).hexdigest() for name in source if name.startswith('src/')},
            packages=[dict(path='node_modules/fastify',name='fastify',version='5.12.7',noticeFiles=[])],outputs=[
            self.output('main',['src/main.ts','src/db.ts','src/database-schema.ts'],['node_modules/fastify'])])
        self.build=dict(appSourceCommit=SOURCE,helperSourceCommit=SOURCE,packagerSourceCommit=SOURCE,databaseSchema=6,databaseMigrationsFrom=[1,2])
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

    def audit(self,artifact):return dict(artifact=artifact,applicationVersion=VERSION,sourceCommit=SOURCE,version='1.0.0+'+SOURCE)

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
        root=self.native();description=self.audit('Yahad.ServiceHost')
        with patch.object(full,'describe_build_artifact',return_value=description) as audit:
            files=full.validate_native(root,'Yahad.ServiceHost',VERSION,SOURCE,self.repo)
            self.assertIn('coreclr.dll',files);audit.assert_called_once_with(root/'Yahad.ServiceHost.exe','--describe',self.repo)
        with patch.object(full,'describe_build_artifact',return_value=dict(description,sourceCommit='b'*40)),self.assertRaises(ValueError):full.validate_native(root,'Yahad.ServiceHost',VERSION,SOURCE,self.repo)

    def test_missing_dependency_and_nonprivate_native_import_fail_before_audit(self):
        root=self.native();(root/'coreclr.dll').unlink()
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'Incomplete private'):full.validate_native(root,'Yahad.ServiceHost',VERSION,SOURCE,self.repo)
        audit.assert_not_called();self.put(root,'coreclr.dll',image());self.put(root,'Yahad.ServiceHost.exe',image('private-missing.dll'))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'private native dependency'):full.validate_native(root,'Yahad.ServiceHost',VERSION,SOURCE,self.repo)
        audit.assert_not_called()

    def test_every_declared_private_runtime_asset_must_be_present(self):
        root=self.native();path=root/'Yahad.ServiceHost.deps.json';deps=json.loads(path.read_text())
        next(iter(deps['targets']['net10.0/win-x64'].values()))['runtime']['missing.dll']={};path.write_text(json.dumps(deps))
        with patch.object(full,'describe_build_artifact') as audit,self.assertRaisesRegex(full.PackageError,'Missing private .NET dependency'):full.validate_native(root,'Yahad.ServiceHost',VERSION,SOURCE,self.repo)
        audit.assert_not_called()

    def test_native_input_change_during_audit_is_not_blessed_by_source_description(self):
        root=self.native()
        def changed(*args):self.put(root,'coreclr.dll',image()+b'changed');return self.audit('Yahad.ServiceHost')
        with patch.object(full,'describe_build_artifact',side_effect=changed),self.assertRaisesRegex(full.PackageError,'closure changed'):full.validate_native(root,'Yahad.ServiceHost',VERSION,SOURCE,self.repo)

    def test_archive_namespace_bytes_and_records_are_exact_and_deterministic(self):
        entries={'payload/bundle.json':full._encode(self.manifest),'payload/app/server/main.mjs':b'bundle','host/Yahad.ServiceHost.exe':image(),'host/sub/Yahad.dll':image()}
        records=full._write_archive(self.root/'a.zip',entries);full._write_archive(self.root/'b.zip',entries)
        self.assertEqual((self.root/'a.zip').read_bytes(),(self.root/'b.zip').read_bytes())
        with zipfile.ZipFile(self.root/'a.zip') as archive:
            self.assertEqual(set(archive.namelist()),{r['Path'] for r in records})
            for record in records:self.assertEqual(hashlib.sha256(archive.read(record['Path'])).hexdigest(),record['Sha256'])
        with self.assertRaisesRegex(full.PackageError,'namespace'):full._write_archive(self.root/'bad.zip',{'unapproved/code.exe':b'not executed'})
        self.assertFalse((self.root/'bad.zip').exists())
        for name in ('bootstrap/authority/Yahad.Authority.exe','system-manifest.json','payload2/x','host/../escape'):
            with self.subTest(name=name),self.assertRaises(full.PackageError):full._write_archive(self.root/'foreign.zip',{name:b'refused'})
        self.assertFalse((self.root/'foreign.zip').exists())

    def test_archive_refuses_changed_verified_bytes_and_preserves_existing_output(self):
        file=self.put(self.root,'one.dll',b'original');entries={'host/one.dll':file}
        expected={name:full._record(name,value) for name,value in entries.items()};file.write_bytes(b'changed')
        with self.assertRaisesRegex(full.PackageError,'input changed'):full._write_archive(self.root/'changed.zip',entries,expected)
        output=self.put(self.root,'existing.zip',b'preserve')
        with self.assertRaises(FileExistsError):full._write_archive(output,entries)
        self.assertEqual(output.read_bytes(),b'preserve')


class RuntimeOnlyBuildTests(unittest.TestCase):
    """End-to-end build() with source identity and the PE/audit boundary stubbed."""
    def setUp(self):
        self.base = SetupPackageTests('test_bundle_sources_and_migration_declaration_are_taken_from_actual_bytes'); self.base.setUp(); self.addCleanup(self.base.tearDown)
        b = self.base; b.native('Yahad.Platform', b.prepared/'app/native')
        for name in ('app/dist/index.html', 'runtime/node/node.exe', 'runtime/ffmpeg/ffmpeg.exe', 'runtime/caddy/caddy.exe'):
            b.put(b.prepared, name, image() if name.endswith('.exe') else b'<html>')
        b.put(b.prepared, 'legal/runtime-lock.json', json.dumps(dict(components=[dict(id='platform', sourceCommit=SOURCE)])))
        b.refresh_records(); self.application = full._encode(b.manifest); b.put(b.prepared, 'bundle.json', self.application)
        self.host = b.native('Yahad.ServiceHost'); self.updater = b.native('Yahad.Updater'); self.output = b.root/'out'

    def build(self, describe=None):
        b = self.base
        def extract(archive, output, retain_manifest=False): shutil.copytree(b.prepared, output)
        def audit(path, flag, repo): return b.audit(Path(path).stem)
        with patch.object(full, '_source_identity', return_value=(VERSION, SOURCE, b.tracked)), patch.object(full.verify_archive, 'verify', side_effect=extract), \
             patch.object(full, 'describe_build_artifact', side_effect=describe or audit), patch.object(full, '_unlinked', side_effect=lambda p, file=False: Path(p).absolute()):
            return full.build(b.repo, b.root/'payload.zip', self.host, self.updater, self.output)

    def test_package_is_format_one_with_only_host_updater_and_payload_members(self):
        result = self.build(); metadata = json.loads((self.output/'install-manifest.json').read_bytes())
        self.assertEqual(sorted(metadata), ['Files', 'Format', 'PayloadDigest', 'SourceCommit', 'Version', 'ZipSha256'])
        self.assertEqual((metadata['Format'], metadata['Version'], metadata['SourceCommit']), (1, VERSION, SOURCE))
        self.assertEqual(metadata['PayloadDigest'], hashlib.sha256(self.application).hexdigest())
        self.assertEqual(metadata['ZipSha256'], hashlib.sha256((self.output/'install.zip').read_bytes()).hexdigest())
        self.assertEqual({p.name for p in self.output.iterdir()}, {'install.zip', 'install-manifest.json'})
        with zipfile.ZipFile(self.output/'install.zip') as archive:
            names = archive.namelist()
            self.assertEqual(set(names), {r['Path'] for r in metadata['Files']}); self.assertEqual(result['files'], len(names))
            self.assertTrue(all(n.startswith(('host/', 'updater/', 'payload/')) for n in names))
            self.assertEqual(archive.read('payload/bundle.json'), self.application)
            for record in metadata['Files']: self.assertEqual(hashlib.sha256(archive.read(record['Path'])).hexdigest(), record['Sha256'])
        self.assertTrue(full.REQUIRED_MEMBERS <= set(names))
        self.assertFalse(any('Authority' in n or n.startswith('bootstrap/') or n == 'system-manifest.json' for n in names))

    def test_stale_or_mismatched_service_host_build_identity_is_refused_before_manifest_is_written(self):
        b = self.base; exe = self.host/'Yahad.ServiceHost.exe'
        for changed in (dict(applicationVersion='0.5.3'), dict(sourceCommit='b'*40, version='1.0.0+'+'b'*40), dict(artifact='foreign')):
            with self.subTest(changed=changed):
                shutil.rmtree(self.output, ignore_errors=True)
                def describe(path, flag, repo, changed=changed):
                    value = b.audit(Path(path).stem); return dict(value, **changed) if Path(path) == exe else value
                with self.assertRaises(ValueError): self.build(describe)
                self.assertFalse((self.output/'install-manifest.json').exists())

    def test_platform_runtime_lock_must_name_the_same_source(self):
        b = self.base; b.put(b.prepared, 'legal/runtime-lock.json', json.dumps(dict(components=[dict(id='platform', sourceCommit='b'*40)])))
        with self.assertRaisesRegex(full.PackageError, 'runtime-lock'): self.build()
        self.assertFalse((self.output/'install-manifest.json').exists())

    def test_required_members_must_all_be_present(self):
        full.require_members(full.REQUIRED_MEMBERS)
        for name in sorted(full.REQUIRED_MEMBERS):
            with self.subTest(name=name), self.assertRaisesRegex(full.PackageError, 'Required package member'): full.require_members(full.REQUIRED_MEMBERS - {name})

    def test_output_must_be_new_and_outside_input_closures(self):
        self.output.mkdir()
        with self.assertRaisesRegex(full.PackageError, 'New output'): self.build()
        self.output = self.host/'nested-out'
        with self.assertRaisesRegex(full.PackageError, 'New output'): self.build()

    def test_host_change_after_validation_is_refused(self):
        b = self.base; original = full.validate_native
        def validated(*args):
            files = original(*args)
            if args[1] == 'Yahad.ServiceHost': b.put(self.host, 'coreclr.dll', image()+b'changed')
            return files
        with patch.object(full, 'validate_native', side_effect=validated), self.assertRaises(full.PackageError): self.build()
        self.assertFalse((self.output/'install-manifest.json').exists())


if __name__=='__main__':unittest.main(verbosity=2)
