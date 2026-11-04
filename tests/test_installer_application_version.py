"""Pure build identity tests: no installation, services or temporary ACL fixtures."""
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from installer.windows.application_version import package_release_version, source_release_version, require_source_release_version, require_native_build_identity

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
        for artifact in ('Yahad.ServiceHost','Yahad.Platform','Yahad.Updater'):
            description=dict(artifact=artifact,applicationVersion='0.6.0',sourceCommit=source,version='1.0.0+'+source)
            self.assertEqual(require_native_build_identity(description,artifact,'0.6.0',source),description)
            for field,value in (('applicationVersion','0.5.3'),('sourceCommit','b'*40),('version','1.0.0+'+'b'*40),('artifact','foreign')):
                mixed=dict(description);mixed[field]=value
                with self.subTest(artifact=artifact,field=field),self.assertRaises(ValueError):require_native_build_identity(mixed,artifact,'0.6.0',source)



if __name__ == '__main__': unittest.main()
