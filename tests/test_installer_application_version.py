"""Pure build identity tests: no installation, services or temporary ACL fixtures."""
import hashlib
import io
import json
from pathlib import Path
import types
import unittest
from unittest.mock import patch
from installer.windows.application_version import package_release_version, source_release_version, require_source_release_version, require_native_build_identity
from installer.bootstrap.payload_identity import validate_payload_pin, require_installer_description
from installer.windows.first_install_package import setup_inputs

ROOT = Path(__file__).resolve().parents[1]


class ApplicationVersionTests(unittest.TestCase):
    def test_current_source_and_both_lock_versions_match(self):
        version = json.loads((ROOT/'package.json').read_bytes())['version']
        self.assertEqual(source_release_version(ROOT), version)
        self.assertEqual(require_source_release_version(version, ROOT), version)
        with self.assertRaises(ValueError): require_source_release_version('0.5.3', ROOT)

    def test_canonical_future_versions_need_no_installer_constant_change(self):
        for version in ('0.0.0', '0.6.0', '12.34.56'):
            self.assertEqual(package_release_version(json.dumps(dict(version=version))), version)

    def test_bad_version_identity_is_rejected(self):
        for version in (None, True, 6, '01.2.3', '0.6.0-beta', '0.6.0+build', ' 0.6.0', '0.6.0\n', '0.6', '١.٢.٣', '1'*41+'.0.0'):
            with self.subTest(version=version), self.assertRaises(ValueError): package_release_version(json.dumps(dict(version=version)))
        for data in ('{}', '[]', 'null', '{"version":"0.6.0","version":"0.5.3"}', ' '*65537):
            with self.subTest(data=data[:80]), self.assertRaises(ValueError): package_release_version(data)

    def test_both_lock_identity_slots_must_match_the_package(self):
        package=b'{"version":"0.6.0"}'
        for lock in ({'version':'0.5.3','packages':{'':{'version':'0.6.0'}}}, {'version':'0.6.0','packages':{'':{'version':'0.5.3'}}}, [], {'version':'0.6.0','packages':None}):
            with self.subTest(lock=lock), patch.object(Path, 'read_bytes', side_effect=[package,json.dumps(lock).encode()]), self.assertRaises(ValueError): source_release_version(ROOT)


class NativeBuildIdentityTests(unittest.TestCase):
    def test_mixed_native_core_host_or_helper_release_and_source_is_refused(self):
        source='a'*40
        for artifact in ('Yahad.ServiceHost','Yahad.Platform'):
            description=dict(artifact=artifact,applicationVersion='0.6.0',sourceCommit=source,version='1.0.0+'+source)
            self.assertEqual(require_native_build_identity(description,artifact,'0.6.0',source),description)
            for field,value in (('applicationVersion','0.5.3'),('sourceCommit','b'*40),('version','1.0.0+'+'b'*40),('artifact','foreign')):
                mixed=dict(description);mixed[field]=value
                with self.subTest(artifact=artifact,field=field),self.assertRaises(ValueError):require_native_build_identity(mixed,artifact,'0.6.0',source)

    def test_setup_inputs_refuses_a_retained_old_service_host_before_any_staging(self):
        source='a'*40
        stale=dict(artifact='Yahad.ServiceHost',applicationVersion='0.5.3',sourceCommit='b'*40,version='1.0.0+'+'b'*40)
        with patch('installer.windows.first_install_package.source_release_version',return_value='0.6.0'), \
             patch('installer.windows.first_install_package.subprocess.check_output',side_effect=[b'',source.encode()]), \
             patch('installer.windows.first_install_package.describe_build_artifact',return_value=stale) as describe, \
             patch.object(Path,'exists',return_value=False),patch.object(Path,'mkdir') as mkdir, \
             patch.object(Path,'read_text') as read, self.assertRaises(ValueError):
            setup_inputs(ROOT,ROOT/'synthetic-payload.zip',ROOT/'synthetic-host',ROOT/'synthetic-new-output')
        describe.assert_called_once();mkdir.assert_not_called();read.assert_not_called()


class BootstrapIdentityTests(unittest.TestCase):
    source='a'*40
    data=b'synthetic full installer, never executed'
    def pin(self, **changed):
        return dict(version='0.6.0',sourceCommit=self.source,payloadSourceCommit=self.source,bytes=len(self.data),sha256=hashlib.sha256(self.data).hexdigest(),**changed)

    def test_stale_preserved_pin_is_refused_without_relabeling_its_bytes(self):
        pin=json.loads((ROOT/'installer/bootstrap/payload-pin.json').read_bytes())
        with self.assertRaises(ValueError): validate_payload_pin(pin,source_release_version(ROOT),self.source)

    def test_wrong_version_source_size_or_digest_metadata_cannot_configure_bootstrap(self):
        for changed in ({'version':'0.5.3'}, {'sourceCommit':'b'*40}, {'payloadSourceCommit':'b'*40}, {'bytes':True}, {'bytes':0}, {'sha256':'NOT_A_DIGEST'}):
            pin=self.pin();pin.update(changed)
            with self.subTest(changed=changed), self.assertRaises(ValueError): validate_payload_pin(pin,'0.6.0',self.source)

    def test_retained_artifact_bytes_are_checked_before_configuring_distribution(self):
        path=types.SimpleNamespace(is_symlink=lambda:False,is_file=lambda:True,stat=lambda:types.SimpleNamespace(st_nlink=1,st_size=len(self.data)),open=lambda _:io.BytesIO(self.data))
        with patch('installer.bootstrap.payload_identity.Path',return_value=path):
            self.assertEqual(validate_payload_pin(self.pin(),'0.6.0',self.source,'synthetic.exe'),self.pin())
            corrupt=self.pin();corrupt['sha256']='0'*64
            with self.assertRaises(ValueError):validate_payload_pin(corrupt,'0.6.0',self.source,'synthetic.exe')


    def test_new_pin_cannot_relabel_an_old_installers_embedded_identity(self):
        validate_payload_pin(self.pin(),'0.6.0',self.source)
        old=dict(artifact='Yahad.Setup',version='0.5.3',sourceCommit='b'*40,systemOperationsInvoked=False,machineAcceptance=False)
        with self.assertRaises(ValueError):require_installer_description(old,'0.6.0',self.source)
        current=dict(old,version='0.6.0',sourceCommit=self.source,setupSourceCommit=self.source,coreSourceCommit=self.source)
        self.assertEqual(require_installer_description(current,'0.6.0',self.source),current)
        for field,value in (('setupSourceCommit','b'*40),('coreSourceCommit','b'*40),('systemOperationsInvoked',True),('machineAcceptance',True)):
            mixed=dict(current);mixed[field]=value
            with self.subTest(field=field),self.assertRaises(ValueError):require_installer_description(mixed,'0.6.0',self.source)


if __name__ == '__main__': unittest.main()
