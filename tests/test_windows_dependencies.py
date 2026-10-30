"""Synthetic archive/provenance tests; never executes fixture binaries."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from installer.windows import dependencies as dep


class DependencyTests(unittest.TestCase):
    def setUp(self):
        self.root=Path(tempfile.mkdtemp(prefix='yahad-vendor-test-')); self.archives={}; self.checks={}
        self.spec={'schema':1,'target':'win-x64','sources':{}}
        for name in ('ffmpeg','caddy'):
            path=self.root/(name+'.zip'); checksum=self.root/(name+'.checksums'); data=(name+'-fixture').encode(); legal=(name+'-license-fixture').encode()
            members=[(name+'.exe',f'runtime/{name}/{name}.exe',data),('LICENSE',f'legal/{name}-license.txt',legal)]
            if name=='ffmpeg': members.append(('private.dll','runtime/ffmpeg/private.dll',b'dll-fixture'))
            with zipfile.ZipFile(path,'w') as archive:
                for source,target,content in members: archive.writestr(source,content)
            algorithm='sha256' if name=='ffmpeg' else 'sha512'
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
        self.assertEqual(set(components),{'ffmpeg','caddy'})
        self.assertEqual(components['ffmpeg']['sha256'],dep.digest(self.output/'runtime/ffmpeg/ffmpeg.exe'))

    def test_corrupt_archive_refused_before_any_output(self):
        with self.archives['ffmpeg'].open('ab') as stream: stream.write(b'changed')
        with self.assertRaisesRegex(dep.DependencyError,'ARCHIVE_CHECKSUM'): self.stage()
        self.assertFalse(self.output.exists())

    def test_corrupt_checksum_file_refused_before_any_output(self):
        self.checks['caddy'].write_text('changed')
        with self.assertRaisesRegex(dep.DependencyError,'CHECKSUM_FILE_CHECKSUM'): self.stage()
        self.assertFalse(self.output.exists())

    def test_publisher_checksum_record_independently_required(self):
        self.checks['ffmpeg'].write_text('0'*64+'  ffmpeg.zip\n')
        self.spec['sources']['ffmpeg']['checksums']['sha256']=dep.digest(self.checks['ffmpeg'])
        with self.assertRaisesRegex(dep.DependencyError,'PUBLISHER_CHECKSUM_RECORD'): self.stage()

    def test_member_pin_not_just_archive_name(self):
        self.spec['sources']['ffmpeg']['files'][0]['sha256']='0'*64
        with self.assertRaisesRegex(dep.DependencyError,'PINNED_MEMBER_CHECKSUM'): self.stage()
        self.assertFalse(self.output.exists())

    def test_unsafe_extra_archive_path_refused(self):
        with zipfile.ZipFile(self.archives['ffmpeg'],'a') as archive: archive.writestr('../escape',b'fixture')
        self.repin_archive('ffmpeg')
        with self.assertRaisesRegex(dep.DependencyError,'ARCHIVE_PATH'): self.stage()

    def test_archive_link_refused_even_if_not_selected(self):
        with zipfile.ZipFile(self.archives['ffmpeg'],'a') as archive:
            info=zipfile.ZipInfo('linked'); info.create_system=3; info.external_attr=0o120777<<16; archive.writestr(info,b'target')
        self.repin_archive('ffmpeg')
        with self.assertRaisesRegex(dep.DependencyError,'ARCHIVE_LINK'): self.stage()

    def test_staged_dll_corruption_refused(self):
        self.stage(); (self.output/'runtime/ffmpeg/private.dll').write_bytes(b'dll-changed')
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_FILE_CHECKSUM'): dep.validate_staged(self.files(),self.spec)

    def test_unpinned_runtime_dll_refused(self):
        self.stage(); (self.output/'runtime/caddy/unpinned.dll').write_bytes(b'fixture')
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_FILE_SET'): dep.validate_staged(self.files(),self.spec)

    def test_license_bytes_are_pinned_too(self):
        self.stage(); (self.output/'legal/ffmpeg-license.txt').write_text('changed')
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_FILE_CHECKSUM'): dep.validate_staged(self.files(),self.spec)

    def test_changed_publisher_proof_refused(self):
        self.stage(); path=self.output/'legal/dependency-proof.json'; value=json.loads(path.read_text()); value['sources']['ffmpeg']['publisher']='pretend-upstream'; path.write_text(json.dumps(value))
        with self.assertRaisesRegex(dep.DependencyError,'DEPENDENCY_PROVENANCE'): dep.validate_staged(self.files(),self.spec)

    def test_existing_output_preserved(self):
        self.output.mkdir(); marker=self.output/'preserve'; marker.write_text('fixture')
        with self.assertRaisesRegex(dep.DependencyError,'NEW_OUTPUT_REQUIRED'): self.stage()
        self.assertEqual(marker.read_text(),'fixture')


if __name__=='__main__': unittest.main(verbosity=2)
