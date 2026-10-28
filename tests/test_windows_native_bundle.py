"""Synthetic PE/package checks. Never executes fixture binaries."""
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile
from installer.windows import bundle, pe

def image(imports=(), machine=0x8664, managed=False):
    data=bytearray(2048); data[:2]=b'MZ'; struct.pack_into('<I',data,0x3c,0x80); data[0x80:0x84]=b'PE\0\0'
    struct.pack_into('<HH',data,0x84,machine,1); struct.pack_into('<H',data,0x94,240)
    opt=0x98; struct.pack_into('<H',data,opt,0x20b); struct.pack_into('<I',data,opt+60,512); struct.pack_into('<I',data,opt+108,16)
    section=opt+240; struct.pack_into('<IIII',data,section+8,1536,0x1000,1536,512)
    if managed:
        struct.pack_into('<II',data,opt+112+14*8,0x1000,72); struct.pack_into('<I',data,512+16,1)
    elif imports:
        struct.pack_into('<II',data,opt+120,0x1000,(len(imports)+1)*20)
        for i,name in enumerate(imports):
            location=900+i*120; encoded=name.encode()+b'\0'; data[location:location+len(encoded)]=encoded
            struct.pack_into('<I',data,512+i*20+12,0x1000+location-512)
    return bytes(data)

class WindowsBundleTests(unittest.TestCase):
    def setUp(self):
        self.root=Path(tempfile.mkdtemp(prefix='yahad-windows-bundle-')); self.prepared=self.root/'prepared'; self.prepared.mkdir()
        for name in (*bundle.BASE_REQUIRED,*bundle.FULL_REQUIRED): self.put(name,'fixture')
        package={'version':'0.5.2','engines':{'node':'>=24.0.0 <25'},'dependencies':{'tsx':'4.20.6'}}
        self.put('app/package.json',json.dumps(package)); self.put('app/package-lock.json',json.dumps({'packages':{'':package}}))
        self.put('app/native/Yahad.Platform.runtimeconfig.json',json.dumps({'runtimeOptions':{'includedFrameworks':[{'name':'Microsoft.NETCore.App','version':'10.0.14'}]}}))
        self.put('app/native/Yahad.Platform.deps.json',json.dumps({'runtimeTarget':{'name':'net10.0/win-x64'},'targets':{'net10.0/win-x64':{'runtimepack/10.0.14':{'runtime':{'System.Private.CoreLib.dll':{}},'native':{'coreclr.dll':{},'hostfxr.dll':{},'hostpolicy.dll':{}}}}}}))
        self.put('legal/SBOM.json','{"fixture":true}')
        for path in self.prepared.rglob('*'):
            if path.suffix in ('.exe','.dll','.node'): path.write_bytes(image(('kernel32.dll',)))
        self.lock()

    def put(self,name,value):
        path=self.prepared/name; path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(value.encode() if isinstance(value,str) else value)

    def lock(self,profile='full'):
        components=[{'id':'node','path':'runtime/node/node.exe','version':'24.22.2','sourceUrl':'https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip'},
            {'id':'platform','path':'app/native/Yahad.Platform.exe','origin':'project-built','sourceCommit':'a'*40,'sourceUrl':'https://github.com/NehoraiHadad/yahad'}]
        if profile=='full': components += [{'id':'ffmpeg','path':'runtime/ffmpeg/ffmpeg.exe','origin':'project-built-from-official-source','sourceUrl':'https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz'},
            {'id':'caddy','path':'runtime/caddy/caddy.exe','sourceUrl':'https://github.com/caddyserver/caddy/releases/download/v2.11.7/caddy_2.11.7_windows_amd64.zip'}]
        for component in components: component['sha256']=hashlib.sha256((self.prepared/component['path']).read_bytes()).hexdigest()
        self.put('legal/runtime-lock.json',json.dumps({'schema':1,'target':'win-x64','components':components}))
        self.put('legal/ffmpeg-build.json',json.dumps({'sourceSha256':'0'*64,'buildRecipeSha256':'1'*64,'configure':['--disable-gpl','--disable-nonfree','--enable-libopus']}))

    def test_full_candidate_content_and_no_install_claim(self):
        result=bundle.build(self.prepared,self.root/'full.zip')
        self.assertFalse(result['familyInstallationReady']); self.assertEqual(result['profile'],'full')
        with zipfile.ZipFile(self.root/'full.zip') as archive:
            saved=json.loads(archive.read('bundle.json')); self.assertEqual(saved['kind'],'windows-runtime-candidate')
            for record in saved['files']: self.assertEqual(hashlib.sha256(archive.read(record['path'])).hexdigest(),record['sha256'])

    def test_deterministic_offline_build(self):
        bundle.build(self.prepared,self.root/'a.zip'); bundle.build(self.prepared,self.root/'b.zip')
        self.assertEqual((self.root/'a.zip').read_bytes(),(self.root/'b.zip').read_bytes())

    def test_missing_node_blocks_full_candidate(self):
        (self.prepared/'runtime/node/node.exe').unlink()
        with self.assertRaisesRegex(bundle.BundleError,'MISSING_PAYLOAD'): bundle.validate(self.prepared)

    def test_corrupt_runtime_checksum_refused(self):
        with (self.prepared/'runtime/node/node.exe').open('ab') as stream: stream.write(b'changed')
        with self.assertRaisesRegex(bundle.BundleError,'checksum'): bundle.validate(self.prepared)

    def test_unbundled_vc_runtime_refused(self):
        self.put('runtime/node/node.exe',image(('VCRUNTIME140.dll',))); self.lock()
        with self.assertRaisesRegex(bundle.BundleError,'MISSING_PRIVATE_DLL'): bundle.validate(self.prepared)

    def test_sibling_private_dll_resolves_without_path(self):
        self.put('runtime/node/node.exe',image(('private.dll',))); self.put('runtime/node/private.dll',image(('kernel32.dll',))); self.lock()
        bundle.validate(self.prepared)

    def test_unrelated_folder_dll_cannot_mask_dependency(self):
        self.put('runtime/node/node.exe',image(('private.dll',))); self.put('legal/elsewhere/private.dll',image()); self.lock()
        with self.assertRaisesRegex(bundle.BundleError,'MISSING_PRIVATE_DLL'): bundle.validate(self.prepared)

    def test_wrong_architecture_refused(self):
        self.put('runtime/node/node.exe',image(machine=0xaa64)); self.lock()
        with self.assertRaisesRegex(bundle.BundleError,'PE inspection'): bundle.validate(self.prepared)

    def test_framework_dependent_dotnet_refused(self):
        self.put('app/native/Yahad.Platform.runtimeconfig.json','{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.14"}}}')
        with self.assertRaisesRegex(bundle.BundleError,'self-contained'): bundle.validate(self.prepared)

    def test_officially_linked_ffmpeg_not_silently_vendor_official(self):
        path=self.prepared/'legal/runtime-lock.json'; value=json.loads(path.read_text()); item=next(c for c in value['components'] if c['id']=='ffmpeg'); item['sourceUrl']='https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/example.zip'; item['origin']='community-build'; path.write_text(json.dumps(value))
        with self.assertRaisesRegex(bundle.BundleError,'Official-source'): bundle.validate(self.prepared)

    def test_private_state_not_packaged(self):
        self.put('app/.env','fixture only')
        with self.assertRaises(bundle.BundleError): bundle.validate(self.prepared)

    def test_existing_output_never_replaced(self):
        path=self.root/'existing.zip'; path.write_bytes(b'preserve')
        with self.assertRaises(bundle.BundleError): bundle.build(self.prepared,path)
        self.assertEqual(path.read_bytes(),b'preserve')

    def test_core_fixture_cannot_pass_as_full_package(self):
        (self.prepared/'runtime/ffmpeg/ffmpeg.exe').unlink(); (self.prepared/'runtime/caddy/caddy.exe').unlink(); self.lock('core-fixture')
        result=bundle.validate(self.prepared,'core-fixture'); self.assertFalse(result['familyInstallationReady'])
        with self.assertRaisesRegex(bundle.BundleError,'MISSING_PAYLOAD'): bundle.validate(self.prepared,'full')

    def test_undeclared_runtime_namespace_refused(self):
        self.put('runtime/unknown/tool.exe',image())
        with self.assertRaisesRegex(bundle.BundleError,'Undeclared runtime'): bundle.validate(self.prepared)

    def test_missing_private_dotnet_asset_refused(self):
        self.put('app/native/Yahad.Platform.deps.json', json.dumps({'runtimeTarget':{'name':'net10.0/win-x64'},'targets':{'net10.0/win-x64':{'app/1':{'runtime':{'missing.dll':{}}}}}}))
        with self.assertRaisesRegex(bundle.BundleError,'Missing private .NET'): bundle.validate(self.prepared)

    def test_addon_node_host_import_resolves_only_for_native_addon(self):
        self.put('app/node_modules/addon/addon.node',image(('node.exe',))); bundle.validate(self.prepared)
        self.put('app/node_modules/addon/rogue.dll',image(('node.exe',)))
        with self.assertRaisesRegex(bundle.BundleError,'MISSING_PRIVATE_DLL'): bundle.validate(self.prepared)

    def test_dependency_data_folder_does_not_allow_live_app_data(self):
        self.put('app/node_modules/example/tests/data/fixture.txt','fixture'); bundle.validate(self.prepared)
        self.put('app/Data/family.txt','synthetic state')
        with self.assertRaisesRegex(bundle.BundleError,'Private or unsafe'): bundle.validate(self.prepared)

    def test_pe_bounds_and_rva_reject_malformed_files(self):
        path=self.root/'bad.exe'; path.write_bytes(b'MZ')
        with self.assertRaises(pe.PEError): pe.inspect(path)
        broken=bytearray(image(('kernel32.dll',))); struct.pack_into('<I',broken,512+12,0xf0000000); path.write_bytes(broken)
        with self.assertRaises(pe.PEError): pe.inspect(path)

if __name__=='__main__': unittest.main(verbosity=2)
