"""Pure build identity tests: no installation, services or temporary ACL fixtures."""
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from installer.windows.application_version import (Infrastructure, package_release_version, package_infrastructure_contract, source_release_version,
    require_source_release_version, source_infrastructure_identity, infrastructure_identity_from_props, require_native_build_identity,
    package_minimum_from)

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


PROPS = ('<Project>\n  <PropertyGroup>\n    <YahadInfrastructureVersion>{version}</YahadInfrastructureVersion>\n'
         '    <YahadInfrastructureContractMin>{low}</YahadInfrastructureContractMin>\n'
         '    <YahadInfrastructureContractMax>{high}</YahadInfrastructureContractMax>\n'
         '    <Version>$(YahadInfrastructureVersion)</Version>\n  </PropertyGroup>\n</Project>\n')


def props(version='1.0.0', low='1', high='1'): return PROPS.format(version=version, low=low, high=high).encode()


class InfrastructureIdentityTests(unittest.TestCase):
    def test_current_props_and_package_contract_agree(self):
        identity = source_infrastructure_identity(ROOT)
        self.assertIsInstance(identity, Infrastructure)
        self.assertLessEqual(identity.contract_min, identity.contract_max)
        contract = json.loads((ROOT/'package.json').read_bytes())['yahadInfrastructureContract']
        self.assertEqual(package_infrastructure_contract((ROOT/'package.json').read_bytes(), identity), contract)

    def test_props_identity_is_literal_canonical_and_defined_once(self):
        self.assertEqual(infrastructure_identity_from_props(props('2.10.0', '3', '1024')), Infrastructure('2.10.0', 3, 1024))
        bad = [props(version) for version in ('01.0.0', '1.0', '1.0.0-beta', '$(Other)', ' 1.0.0', '')]
        bad += [props(low=low) for low in ('0', '01', '+1', '1025', '١')] + [props(low='2', high='1'), props(high='1025')]
        bad += [b'\xef\xbb\xbf'+props(), props().replace(b'<Project>', b'<Other>').replace(b'</Project>', b'</Other>'),
                props().replace(b'</PropertyGroup>', b'<YahadInfrastructureVersion>1.0.0</YahadInfrastructureVersion></PropertyGroup>'),
                props().replace(b'<YahadInfrastructureContractMax>1</YahadInfrastructureContractMax>', b''),
                props().replace(b'<YahadInfrastructureContractMin>', b'<YahadInfrastructureContractMin Condition="x">'), b' '*65537]
        for data in bad:
            with self.subTest(data=data[:200]), self.assertRaises(ValueError): infrastructure_identity_from_props(data)

    def test_app_contract_is_an_exact_integer_in_the_infrastructure_range(self):
        infrastructure = Infrastructure('1.0.0', 2, 3)
        for value in (2, 3): self.assertEqual(package_infrastructure_contract(json.dumps(dict(version='0.6.0', yahadInfrastructureContract=value)), infrastructure), value)
        for value in (1, 4, 0, True, '2', 2.0, None, [2]):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, 'infrastructure contract'):
                package_infrastructure_contract(json.dumps(dict(version='0.6.0', yahadInfrastructureContract=value)), infrastructure)
        with self.assertRaisesRegex(ValueError, 'infrastructure contract'): package_infrastructure_contract('{"version":"0.6.0"}', infrastructure)


class MinimumFromTests(unittest.TestCase):
    def declare(self, value): return json.dumps(dict(version='0.6.0', yahadWindowsMinimumFrom=value))

    def test_checked_in_declaration_is_read_against_both_tracks(self):
        data = (ROOT/'package.json').read_bytes()
        minimum = package_minimum_from(data, source_release_version(ROOT), source_infrastructure_identity(ROOT).version)
        self.assertEqual(sorted(minimum), ['app', 'infrastructure'])

    def test_declaration_is_exact_canonical_and_never_newer_than_the_release(self):
        self.assertEqual(package_minimum_from(self.declare(dict(app='0.5.0', infrastructure='1.0.0')), '0.6.0', '1.0.0'),
                         dict(app='0.5.0', infrastructure='1.0.0'))
        self.assertEqual(package_minimum_from(self.declare(dict(app='0.6.0', infrastructure='0.9.0')), '0.6.0', '0.10.0'),
                         dict(app='0.6.0', infrastructure='0.9.0'))
        for value in (None, {}, dict(app='0.5.0'), dict(app='0.5.0', infrastructure='1.0.0', extra='1.0.0'), dict(app='05.0.0', infrastructure='1.0.0'),
                      dict(app='0.5', infrastructure='1.0.0'), dict(app=5, infrastructure='1.0.0'), dict(app='0.5.0', infrastructure='1.0.0-rc')):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, 'minimumFrom declaration'):
                package_minimum_from(self.declare(value), '0.6.0', '1.0.0')
        with self.assertRaisesRegex(ValueError, 'minimumFrom declaration'): package_minimum_from('{"version":"0.6.0"}', '0.6.0', '1.0.0')
        for value in (dict(app='0.6.1', infrastructure='1.0.0'), dict(app='0.6.0', infrastructure='1.0.1')):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, 'newer than the release'):
                package_minimum_from(self.declare(value), '0.6.0', '1.0.0')


class NativeBuildIdentityTests(unittest.TestCase):
    def test_native_images_carry_the_infrastructure_identity_and_never_the_app_version(self):
        source = 'a'*40; infrastructure = Infrastructure('1.2.3', 1, 2)
        for artifact in ('Yahad.ServiceHost', 'Yahad.Platform', 'Yahad.Updater', 'Yahad.Setup'):
            description = dict(artifact=artifact, infrastructureVersion='1.2.3', contractMin=1, contractMax=2, sourceCommit=source, version='1.2.3+'+source)
            self.assertEqual(require_native_build_identity(description, artifact, infrastructure, source), description)
            for field, value in (('infrastructureVersion', '1.2.4'), ('sourceCommit', 'b'*40), ('version', '1.2.3+'+'b'*40), ('version', '1.2.4+'+source),
                                 ('version', source), ('artifact', 'foreign'), ('contractMin', 2), ('contractMax', 3), ('contractMin', True),
                                 ('contractMax', '2'), ('applicationVersion', '0.6.0')):
                mixed = dict(description); mixed[field] = value
                with self.subTest(artifact=artifact, field=field, value=value), self.assertRaises(ValueError):
                    require_native_build_identity(mixed, artifact, infrastructure, source)
            for field in ('infrastructureVersion', 'contractMin', 'contractMax', 'sourceCommit', 'version'):
                missing = {k: v for k, v in description.items() if k != field}
                with self.subTest(artifact=artifact, missing=field), self.assertRaises(ValueError):
                    require_native_build_identity(missing, artifact, infrastructure, source)


if __name__ == '__main__': unittest.main()
