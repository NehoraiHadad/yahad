"""Synthetic archive/provenance tests; never executes fixture binaries."""
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile
from installer.windows import dependencies as dep


class DependencyTests(unittest.TestCase):
    def setUp(self):
        self.root=Path(tempfile.mkdtemp(prefix='yahad-vendor-test-')); self.archives={}; self.checks={}
        self.spec={'schema':1,'target':'win-x64','sources':{}}
        for name in ('caddy',):
            path=self.root/(name+'.zip'); checksum=self.root/(name+'.checksums'); data=(name+'-fixture').encode(); legal=(name+'-license-fixture').encode()
            members=[(name+'.exe',f'runtime/{name}/{name}.exe',data),('LICENSE',f'legal/{name}-license.txt',legal)]
            with zipfile.ZipFile(path,'w') as archive:
                for source,target,content in members: archive.writestr(source,content)
            algorithm='sha512'
            checksum.write_text(dep.digest(path,algorithm)+'  '+path.name+'\n',encoding='utf-8')
            self.spec['sources'][name]={'publisher':name+'-fixture','origin':'fixture','version':'1.0','license':'fixture',
                'archive':{'name':path.name,'url':'https://example.invalid/'+path.name,'sha256':dep.digest(path)},
                'checksums':{'sha256':dep.digest(checksum),'algorithm':algorithm},
                'files':[{'archivePath':source,'path':target,'size':len(content),'sha256':hashlib.sha256(content).hexdigest()} for source,target,content in members]}
            self.archives[name]=path; self.checks[name]=checksum
        self.output=self.root/'new-output'

    def stage(self): return dep.stage(self.archives,self.checks,self.output,self.spec)
    def files(self): return {p.relative_to(self.output).as_posix():p for p in self.output.rglob('*') if p.is_file()}
    def repin_archive(self,name):
        source=self.spec['sources'][name]; source['archive']['sha256']=dep.digest(self.archives[name])
        self.checks[name].write_text(dep.digest(self.archives[name],source['checksums']['algorithm'])+'  '+self.archives[name].name+'\n')
        source['checksums']['sha256']=dep.digest(self.checks[name])

    def test_offline_stage_exact_files_and_provenance(self):
        result=self.stage(); self.assertFalse(result['familyInstallationReady'])
        components=dep.validate_staged(self.files(),self.spec)
        self.assertEqual(set(components),{'caddy'})
        self.assertEqual(components['caddy']['sha256'],dep.digest(self.output/'runtime/caddy/caddy.exe'))

    def test_shipped_pins_are_caddy_only(self):
        self.assertEqual(set(dep.pins()['sources']),{'caddy'})

    def test_corrupt_archive_refused_before_any_output(self):
        with self.archives['caddy'].open('ab') as stream: stream.write(b'changed')
        with self.assertRaisesRegex(dep.DependencyError,'ARCHIVE_CHECKSUM'): self.stage()
        self.assertFalse(self.output.exists())

    def test_corrupt_checksum_file_refused_before_any_output(self):
        self.checks['caddy'].write_text('changed')
        with self.assertRaisesRegex(dep.DependencyError,'CHECKSUM_FILE_CHECKSUM'): self.stage()
        self.assertFalse(self.output.exists())

    def test_publisher_checksum_record_independently_required(self):
        self.checks['caddy'].write_text('0'*128+'  caddy.zip\n')
        self.spec['sources']['caddy']['checksums']['sha256']=dep.digest(self.checks['caddy'])
        with self.assertRaisesRegex(dep.DependencyError,'PUBLISHER_CHECKSUM_RECORD'): self.stage()

    def test_member_pin_not_just_archive_name(self):
        self.spec['sources']['caddy']['files'][0]['sha256']='0'*64
        with self.assertRaisesRegex(dep.DependencyError,'PINNED_MEMBER_CHECKSUM'): self.stage()
        self.assertFalse(self.output.exists())

    def test_unsafe_extra_archive_path_refused(self):
        with zipfile.ZipFile(self.archives['caddy'],'a') as archive: archive.writestr('../escape',b'fixture')
        self.repin_archive('caddy')
        with self.assertRaisesRegex(dep.DependencyError,'ARCHIVE_PATH'): self.stage()

    def test_archive_link_refused_even_if_not_selected(self):
        with zipfile.ZipFile(self.archives['caddy'],'a') as archive:
            info=zipfile.ZipInfo('linked'); info.create_system=3; info.external_attr=0o120777<<16; archive.writestr(info,b'target')
        self.repin_archive('caddy')
        with self.assertRaisesRegex(dep.DependencyError,'ARCHIVE_LINK'): self.stage()

    def test_staged_executable_corruption_refused(self):
        self.stage(); (self.output/'runtime/caddy/caddy.exe').write_bytes(b'caddy-changed')
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_FILE_CHECKSUM'): dep.validate_staged(self.files(),self.spec)

    def test_unpinned_runtime_dll_refused(self):
        self.stage(); (self.output/'runtime/caddy/unpinned.dll').write_bytes(b'fixture')
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_FILE_SET'): dep.validate_staged(self.files(),self.spec)

    def test_license_bytes_are_pinned_too(self):
        self.stage(); (self.output/'legal/caddy-license.txt').write_text('changed')
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_FILE_CHECKSUM'): dep.validate_staged(self.files(),self.spec)

    def test_changed_publisher_proof_refused(self):
        self.stage(); path=self.output/'legal/dependency-proof.json'; value=json.loads(path.read_text()); value['sources']['caddy']['publisher']='pretend-upstream'; path.write_text(json.dumps(value))
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_PROVENANCE'): dep.validate_staged(self.files(),self.spec)

    def test_existing_output_preserved(self):
        self.output.mkdir(); marker=self.output/'preserve'; marker.write_text('fixture')
        with self.assertRaisesRegex(dep.DependencyError,'NEW_OUTPUT_REQUIRED'): self.stage()
        self.assertEqual(marker.read_text(),'fixture')

    def test_shipped_node_pin(self):
        node=dep.pins()['node']
        self.assertEqual(node['version'],'24.22.2'); self.assertEqual(node['origin'],'official-vendor')
        self.assertTrue(node['archive']['url'].startswith('https://nodejs.org/dist/v24.22.2/'))
        self.assertEqual({f['directoryPath']:f['path'] for f in node['files']},{'node.exe':'runtime/node/node.exe','LICENSE':'legal/Node-LICENSE.txt'})
        self.assertEqual(set(dep.pins()['sources']),{'caddy'})

    def test_proof_covers_only_the_vendor_closure(self):
        spec=copy.deepcopy(self.spec); spec['node']={'version':'1'}; base=dep.proof(spec)['sourceSpecSha256']
        spec['node']={'version':'2'}; self.assertEqual(dep.proof(spec)['sourceSpecSha256'],base)
        spec['sources']['caddy']['version']='2.0'; self.assertNotEqual(dep.proof(spec)['sourceSpecSha256'],base)

    def node_fixture(self):
        directory=self.root/'node-dist'; directory.mkdir(); files=[]
        for source,target,content in (('node.exe','runtime/node/node.exe',b'node-fixture'),('LICENSE','legal/Node-LICENSE.txt',b'node-license')):
            (directory/source).write_bytes(content); files.append({'directoryPath':source,'path':target,'size':len(content),'sha256':hashlib.sha256(content).hexdigest()})
        spec=copy.deepcopy(self.spec)
        spec['node']={'publisher':'node-fixture','origin':'fixture','version':'24.0.0','license':'MIT','archive':{'url':'https://example.invalid/node.zip','sha256':'a'*64},'files':files}
        return directory,spec

    def test_validate_node_returns_pinned_files(self):
        directory,spec=self.node_fixture()
        self.assertEqual(dep.validate_node(directory,spec),{'runtime/node/node.exe':directory/'node.exe','legal/Node-LICENSE.txt':directory/'LICENSE'})

    def test_validate_node_refuses_changed_wrong_size_or_missing(self):
        directory,spec=self.node_fixture()
        (directory/'node.exe').write_bytes(b'node-fixturf')
        with self.assertRaisesRegex(dep.DependencyError,'NODE_PIN'): dep.validate_node(directory,spec)
        (directory/'node.exe').write_bytes(b'node-fixture-longer')
        with self.assertRaisesRegex(dep.DependencyError,'NODE_PIN'): dep.validate_node(directory,spec)
        (directory/'node.exe').unlink()
        with self.assertRaisesRegex(dep.DependencyError,'NODE_PIN'): dep.validate_node(directory,spec)

    def test_node_component_fields(self):
        _,spec=self.node_fixture(); executable=spec['node']['files'][0]
        self.assertEqual(dep.node_component(spec),{'id':'node','path':'runtime/node/node.exe','sha256':executable['sha256'],'version':'24.0.0','origin':'fixture',
            'publisher':'node-fixture','sourceUrl':'https://example.invalid/node.zip','archiveSha256':'a'*64,'license':'MIT'})

    def test_stage_writes_caddy_notices_and_lf_proof(self):
        self.stage()
        self.assertEqual((self.output/'legal/Caddy-NOTICES.txt').read_bytes(),dep.CADDY_NOTICES)
        self.assertNotIn(b'\r',(self.output/'legal/dependency-proof.json').read_bytes())

    def test_verify_node_cli_refuses_missing_directory(self):
        worktree=Path(__file__).resolve().parent.parent
        result=subprocess.run([sys.executable,'-m','installer.windows.dependencies','verify-node','--directory',str(self.root/'missing')],cwd=worktree,capture_output=True,text=True)
        self.assertNotEqual(result.returncode,0); self.assertIn('Dependency refused',result.stderr)


if __name__=='__main__': unittest.main(verbosity=2)
