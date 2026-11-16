"""Staged app tree validation (installer/windows/bundle.py) and bounded PE inspection.
Synthetic fixtures only; never executes fixture binaries."""
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
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


PACKAGES=[{'path':'node_modules/fastify','name':'fastify','version':'5.12.7','noticeFiles':[]}]


class StagedAppTreeTests(unittest.TestCase):
    def setUp(self):
        temporary=tempfile.TemporaryDirectory(prefix='yahad-windows-bundle-'); self.addCleanup(temporary.cleanup)
        self.root=Path(temporary.name); self.prepared=self.root/'prepared'; self.prepared.mkdir()
        for name in bundle.REQUIRED: self.put(name,'fixture '+name)
        self.package()
        self.server()
        self.sbom()

    def put(self,name,value):
        path=self.prepared/name; path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(value.encode() if isinstance(value,str) else value)

    def remove(self,name):
        path=self.prepared/name; path.unlink()
        parent=path.parent
        while parent!=self.prepared and not any(parent.iterdir()): parent.rmdir(); parent=parent.parent

    def package(self,version='0.6.0',node='>=24.0.0 <25'):
        self.put('app/package.json',json.dumps({'version':version,'engines':{'node':node}}))

    def server(self,**changes):
        outputs=[{'path':name,'size':(self.prepared/name).stat().st_size,'sha256':hashlib.sha256((self.prepared/name).read_bytes()).hexdigest()} for name in bundle.SERVER]
        record={'format':'yahad-server-bundle','schema':1,'notices':'app/legal/ThirdPartyNotices-server.txt','outputs':outputs,'packages':PACKAGES}
        record.update(changes); self.put('app/legal/server-bundle.json',json.dumps(record))

    def sbom(self,**changes):
        record={'format':'yahad-app-inventory','serverBundleDependencies':[{'path':p['path']} for p in PACKAGES]}
        record.update(changes); self.put('app/legal/SBOM.json',json.dumps(record))

    def test_valid_tree_returns_version_and_sorted_verified_records(self):
        result=bundle.validate(self.prepared)
        self.assertEqual(set(result),{'version','files'}); self.assertEqual(result['version'],'0.6.0')
        paths=[record['path'] for record in result['files']]
        self.assertEqual(paths,sorted(paths)); self.assertEqual(set(paths),set(bundle.REQUIRED))
        for record in result['files']:
            data=(self.prepared/record['path']).read_bytes()
            self.assertEqual(record['size'],len(data)); self.assertEqual(record['sha256'],hashlib.sha256(data).hexdigest())

    def test_each_required_member_is_required(self):
        for name in bundle.REQUIRED:
            with self.subTest(name=name):
                original=(self.prepared/name).read_bytes(); self.remove(name)
                with self.assertRaisesRegex(bundle.BundleError,'MISSING_PAYLOAD'): bundle.validate(self.prepared)
                self.put(name,original)
        bundle.validate(self.prepared)

    def test_forbidden_members_are_refused(self):
        for name in ('app/src/x.ts','app/node_modules/a/index.js','app/native/Yahad.Platform.exe','app/Native/coreclr.dll','app/package-lock.json',
                     'app/dist/addon.node','app/dist/tool.exe','app/dist/lib.dll','app/dist/run.ps1','app/dist/run.cmd','app/dist/run.bat','app/dist/tool.CMD'):
            with self.subTest(name=name):
                self.put(name,'fixture')
                with self.assertRaises(bundle.BundleError): bundle.validate(self.prepared)
                self.remove(name)
        bundle.validate(self.prepared)

    def test_platform_and_source_are_outside_the_app_namespace(self):
        for name in ('app/src/x.ts','app/native/Yahad.Platform.exe','app/Native/coreclr.dll','app/package-lock.json','app/node_modules/a/index.js'):
            with self.subTest(name=name):
                self.put(name,'fixture')
                with self.assertRaisesRegex(bundle.BundleError,'Undeclared app namespace'): bundle.validate(self.prepared)
                self.remove(name)

    def test_executable_or_script_names_are_refused_inside_app_roots(self):
        for name in ('app/dist/addon.node','app/dist/tool.exe','app/dist/lib.dll','app/dist/run.ps1','app/dist/run.cmd','app/dist/run.bat','app/dist/tool.CMD'):
            with self.subTest(name=name):
                self.put(name,'fixture')
                with self.assertRaisesRegex(bundle.BundleError,'Executable or script member name'): bundle.validate(self.prepared)
                self.remove(name)

    def test_pe_image_under_innocent_name_is_refused(self):
        self.put('app/dist/logo.bin',b'MZ'+b'\0'*30)
        with self.assertRaisesRegex(bundle.BundleError,'PE image member'): bundle.validate(self.prepared)

    def test_refused_member_unit(self):
        self.assertIsNone(bundle.refused_member('app/dist/index.html',b'<!'))
        self.assertIsNone(bundle.refused_member('app/dist/empty.txt',b''))
        self.assertIsNotNone(bundle.refused_member('app/dist/x.bin',b'MZ'))
        self.assertIsNotNone(bundle.refused_member('app/dist/x.DLL',b'ab'))
        self.assertIsNotNone(bundle.refused_member('APP/Node_Modules/x.js',b'ab'))
        self.assertIsNotNone(bundle.refused_member('app/Package-Lock.json',b'{}'))

    def test_nothing_outside_app_is_packaged(self):
        for name in ('legal/x','runtime/node/node.exe'):
            with self.subTest(name=name):
                self.put(name,'fixture')
                with self.assertRaisesRegex(bundle.BundleError,'Undeclared app namespace'): bundle.validate(self.prepared)
                self.remove(name)

    def test_server_directory_holds_only_the_bundle(self):
        self.put('app/server/extra.mjs','fixture')
        with self.assertRaisesRegex(bundle.BundleError,'Undeclared app namespace'): bundle.validate(self.prepared)

    def test_unknown_app_root_refused(self):
        self.put('app/other-root/file','fixture')
        with self.assertRaisesRegex(bundle.BundleError,'Undeclared app namespace'): bundle.validate(self.prepared)

    def test_private_paths_refused(self):
        for name in ('app/data/x','app/.env','app/dist/secrets/x'):
            with self.subTest(name=name):
                self.put(name,'fixture')
                with self.assertRaisesRegex(bundle.BundleError,'Private or unsafe path'): bundle.validate(self.prepared)
                self.remove(name)
        for name in ('app/dist/x.sqlite','app/dist/key.pem'):
            with self.subTest(name=name):
                self.put(name,'fixture')
                with self.assertRaisesRegex(bundle.BundleError,'Unsafe package input'): bundle.validate(self.prepared)
                self.remove(name)

    def test_server_bundle_is_pinned_by_its_record(self):
        with (self.prepared/'app/server/main.mjs').open('ab') as stream: stream.write(b'changed')
        with self.assertRaisesRegex(bundle.BundleError,'Server bundle checksum'): bundle.validate(self.prepared)

    def test_server_bundle_record_contract(self):
        for name,changes in (('notices',{'notices':'app/legal/other.txt'}),('format',{'format':'something-else'}),('schema',{'schema':2}),
                             ('escaping package path',{'packages':[{**PACKAGES[0],'path':'../escape'}]})):
            with self.subTest(name=name):
                self.server(**changes)
                with self.assertRaisesRegex(bundle.BundleError,'Server bundle contract'): bundle.validate(self.prepared)
        self.server(); bundle.validate(self.prepared)

    def test_sbom_must_match_the_server_bundle(self):
        self.sbom(format='wrong')
        with self.assertRaisesRegex(bundle.BundleError,'App SBOM contract'): bundle.validate(self.prepared)
        self.sbom(serverBundleDependencies=[{'path':'node_modules/other'}])
        with self.assertRaisesRegex(bundle.BundleError,'App SBOM differs from the server bundle'): bundle.validate(self.prepared)
        self.sbom(); bundle.validate(self.prepared)

    def test_app_package_identity(self):
        self.package(node='>=22')
        with self.assertRaisesRegex(bundle.BundleError,'App package identity'): bundle.validate(self.prepared)
        self.package(version='0.6')
        with self.assertRaisesRegex(bundle.BundleError,'App package identity'): bundle.validate(self.prepared)


class PETests(unittest.TestCase):
    def setUp(self):
        temporary=tempfile.TemporaryDirectory(prefix='yahad-windows-pe-'); self.addCleanup(temporary.cleanup); self.root=Path(temporary.name)

    def test_pe_bounds_and_rva_reject_malformed_files(self):
        path=self.root/'bad.exe'; path.write_bytes(b'MZ')
        with self.assertRaises(pe.PEError): pe.inspect(path)
        broken=bytearray(image(('kernel32.dll',))); struct.pack_into('<I',broken,512+12,0xf0000000); path.write_bytes(broken)
        with self.assertRaises(pe.PEError): pe.inspect(path)

    def test_valid_x64_image_reports_its_imports(self):
        path=self.root/'good.exe'; path.write_bytes(image(('kernel32.dll',)))
        result=pe.inspect(path)
        self.assertEqual(result['machine'],'x64'); self.assertFalse(result['managed']); self.assertIn('kernel32.dll',result['imports'])

    def test_other_architecture_is_not_inspected_as_x64(self):
        path=self.root/'arm64.exe'; path.write_bytes(image(machine=0xaa64))
        with self.assertRaisesRegex(pe.PEError,'not x64'): pe.inspect(path)


if __name__=='__main__': unittest.main(verbosity=2)
