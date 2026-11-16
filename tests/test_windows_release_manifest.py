import copy
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
import zipfile

from installer.windows import release_manifest as rm
from installer.windows.setup_package import APP_TREE_NAME

ROOT = Path(__file__).resolve().parents[1]
CSHARP = (ROOT/'installer'/'Yahad.Install.Core'/'ReleaseManifest.cs').read_text(encoding='utf-8')
CONTRACT_CS = (ROOT/'installer'/'Yahad.Install.Core'/'InfrastructureRelease.cs').read_text(encoding='utf-8')

KEY_ID = '0123456789abcdef'
APP_COMMIT, INFRA_COMMIT = 'a'*40, 'b'*40
INFRA_TREE = 'c'*64
SETUP_BYTES = b'MZ fake setup image'
APP_TREE_BYTES = b'{"tree":"app"}\n'


def csharp_records():
    """{record name: [camelCased parameter names]} from the `record X(...)` declarations of ReleaseManifest.cs."""
    records = {}
    for name, parameters in re.findall(r'internal sealed record (\w+)\((.*?)\)\s*(?:;|\r?\n\s*\{)', CSHARP, re.S):
        names = [parameter.split()[-1] for parameter in parameters.split(',')]
        records[name] = [part[0].lower()+part[1:] for part in names]
    return records


def csharp_constant(name):
    match = re.search(r'\b'+name+r'=([0-9]+|"[^"]*")', CSHARP)
    assert match, name
    text = match.group(1)
    return text.strip('"') if text.startswith('"') else int(text)


def csharp_name_template(function):
    """'prefix{v}suffix' from `static string Name(string version)=>"prefix"+version+"suffix";`."""
    match = re.search(function+r'\(string version\)=>"([^"]*)"\+version\+"([^"]*)"', CSHARP)
    assert match, function
    return match.group(1)+'{v}'+match.group(2)


def encode(value):
    return json.dumps(value, separators=(',', ':')).encode('ascii')


def sample(version='0.6.0'):
    return {
        'format': 2, 'product': 'yahad-windows-release', 'version': version, 'sourceCommit': APP_COMMIT, 'keyId': KEY_ID,
        'createdUnix': 1790000000,
        'infrastructure': {'version': '0.5.0', 'contractMin': 1, 'contractMax': 2, 'sourceCommit': INFRA_COMMIT, 'treeSha256': INFRA_TREE,
                           'setup': {'name': 'Yahad.Setup-'+version+'.exe', 'sha256': 'd'*64, 'bytes': 1234}},
        'app': {'archive': {'name': 'yahad-windows-app-'+version+'.zip', 'sha256': 'e'*64, 'bytes': 5678}, 'treeSha256': 'f'*64,
                'requiresContract': 1, 'databaseSchema': 5, 'databaseMigrationsFrom': [3, 4]},
        'minimumFrom': {'app': '0.5.0', 'infrastructure': '0.4.0'},
    }


class ContractTests(unittest.TestCase):
    def test_record_fields_equal_the_validated_key_lists_in_order(self):
        records = csharp_records()
        self.assertEqual(set(records), {'ReleaseFile', 'ReleaseInfrastructure', 'ReleaseApp', 'ReleaseMinimumFrom', 'ReleaseManifest'})
        value = sample()
        self.assertEqual(list(value), records['ReleaseManifest'])
        self.assertEqual(list(value['infrastructure']), records['ReleaseInfrastructure'])
        self.assertEqual(list(value['infrastructure']['setup']), records['ReleaseFile'])
        self.assertEqual(list(value['app']), records['ReleaseApp'])
        self.assertEqual(list(value['app']['archive']), records['ReleaseFile'])
        self.assertEqual(list(value['minimumFrom']), records['ReleaseMinimumFrom'])
        # validate accepts exactly the C# shape...
        self.assertEqual(rm.validate(encode(value)), value)
        # ...and refuses each record with a field dropped, added or reordered (the key lists validate requires).
        def rebuilt(path, change):
            changed = copy.deepcopy(value)
            target = changed
            for step in path: target = target[step]
            change(target)
            return encode(changed)
        for path, keys in (((), records['ReleaseManifest']), (('infrastructure',), records['ReleaseInfrastructure']),
                           (('infrastructure', 'setup'), records['ReleaseFile']), (('app',), records['ReleaseApp']),
                           (('app', 'archive'), records['ReleaseFile']), (('minimumFrom',), records['ReleaseMinimumFrom'])):
            for key in keys:
                with self.subTest(path=path, missing=key), self.assertRaises(ValueError):
                    rm.validate(rebuilt(path, lambda target: target.pop(key)))
            with self.subTest(path=path, extra=True), self.assertRaises(ValueError):
                rm.validate(rebuilt(path, lambda target: target.update(extra=1)))
            with self.subTest(path=path, reordered=True), self.assertRaises(ValueError):
                rm.validate(rebuilt(path, lambda target: target.update({keys[0]: target.pop(keys[0])})))

    def test_constants_equal_the_csharp_constants(self):
        self.assertEqual(rm.FORMAT, csharp_constant('CurrentFormat'))
        self.assertEqual(rm.PRODUCT, csharp_constant('ProductName'))
        self.assertEqual(rm.MAXIMUM_BYTES, csharp_constant('MaximumBytes'))
        self.assertEqual(rm.MAXIMUM_SETUP, csharp_constant('MaximumSetupBytes'))
        self.assertEqual(rm.MAXIMUM_APP_ARCHIVE, csharp_constant('MaximumAppArchiveBytes'))
        self.assertEqual(rm.MAXIMUM_SOURCES, csharp_constant('MaximumMigrationSources'))

    def test_file_names_follow_the_csharp_patterns(self):
        for version in ('0.6.0', '12.0.3'):
            self.assertEqual(rm.manifest_name(version), csharp_name_template('ManifestName').format(v=version))
            self.assertEqual(rm.setup_name(version), csharp_name_template('SetupName').format(v=version))
            self.assertEqual(rm.app_archive_name(version), csharp_name_template('AppArchiveName').format(v=version))

    def test_bounds_equal_the_csharp_bounds(self):
        self.assertIn('IsContract(int value)=>value is >=1 and <=1024', CONTRACT_CS)
        self.assertIn('a.DatabaseSchema is <1 or >1024', CSHARP)
        self.assertIn('value.Length<=40', CSHARP)

    def test_exact_bounds_are_accepted_and_one_past_is_refused(self):
        def check(change, ok):
            value = sample()
            change(value)
            if ok: rm.validate(encode(value))
            else:
                with self.assertRaises(ValueError): rm.validate(encode(value))
        for field, bound in (('setup', rm.MAXIMUM_SETUP), ):
            check(lambda v: v['infrastructure'][field].update(bytes=bound), True)
            check(lambda v: v['infrastructure'][field].update(bytes=bound+1), False)
        check(lambda v: v['app']['archive'].update(bytes=rm.MAXIMUM_APP_ARCHIVE), True)
        check(lambda v: v['app']['archive'].update(bytes=rm.MAXIMUM_APP_ARCHIVE+1), False)
        check(lambda v: v['infrastructure'].update(contractMin=1024, contractMax=1024), True)
        check(lambda v: v['infrastructure'].update(contractMax=1025), False)
        check(lambda v: v['infrastructure'].update(contractMin=0), False)
        check(lambda v: v['app'].update(databaseSchema=1024), True)
        check(lambda v: v['app'].update(databaseSchema=1025), False)


class ValidateTests(unittest.TestCase):
    def refused(self, change):
        value = sample()
        change(value)
        with self.assertRaises(ValueError): rm.validate(encode(value))

    def test_sample_is_valid(self):
        self.assertEqual(rm.validate(encode(sample())), sample())

    def test_identity_fields(self):
        self.refused(lambda v: v.update(format=1))
        self.refused(lambda v: v.update(format=2.0))
        self.refused(lambda v: v.update(product='other'))
        self.refused(lambda v: v.update(version='0.06.0'))
        self.refused(lambda v: v.update(version='1.2'))
        self.refused(lambda v: v.update(version='1.'*20+'1'))
        self.refused(lambda v: v.update(sourceCommit='A'*40))
        self.refused(lambda v: v.update(sourceCommit='a'*39))
        for key in ('', '0123456789abcde', '0123456789abcdef0', '0123456789ABCDEF', '0123456789abcdeg', None, 5):
            with self.subTest(key=key): self.refused(lambda v: v.update(keyId=key))

    def test_created_unix(self):
        self.refused(lambda v: v.update(createdUnix=0))
        self.refused(lambda v: v.update(createdUnix=-5))
        self.refused(lambda v: v.update(createdUnix=True))
        self.refused(lambda v: v.update(createdUnix='5'))
        value = sample()
        value['createdUnix'] = 1
        rm.validate(encode(value))

    def test_booleans_and_strings_are_not_integers(self):
        self.refused(lambda v: v['infrastructure'].update(contractMin=True))
        self.refused(lambda v: v['infrastructure'].update(contractMax=True))
        self.refused(lambda v: v['infrastructure']['setup'].update(bytes=True))
        self.refused(lambda v: v['app'].update(requiresContract=True))
        self.refused(lambda v: v['app'].update(databaseSchema=True))
        self.refused(lambda v: v['app']['databaseMigrationsFrom'].__setitem__(0, True))
        self.refused(lambda v: v['app'].update(databaseSchema='5'))
        self.refused(lambda v: v['app']['archive'].update(bytes=1.0))

    def test_contract_range(self):
        self.refused(lambda v: v['infrastructure'].update(contractMin=3, contractMax=2))
        self.refused(lambda v: v['app'].update(requiresContract=0))
        self.refused(lambda v: v['app'].update(requiresContract=1025))
        value = sample()
        value['infrastructure'].update(contractMin=3, contractMax=3)
        rm.validate(encode(value))
        # Whether the app fits the release's own infrastructure is selection's question, not a parse failure.
        value['app']['requiresContract'] = 9
        rm.validate(encode(value))

    def test_schema_and_migrations(self):
        self.refused(lambda v: v['app'].update(databaseSchema=0))
        self.refused(lambda v: v['app'].update(databaseSchema=1025))
        self.refused(lambda v: v['app'].update(databaseMigrationsFrom=[3, 3]))
        self.refused(lambda v: v['app'].update(databaseMigrationsFrom=[5]))
        self.refused(lambda v: v['app'].update(databaseMigrationsFrom=[6]))
        self.refused(lambda v: v['app'].update(databaseMigrationsFrom=[0]))
        self.refused(lambda v: v['app'].update(databaseMigrationsFrom=[-1]))
        self.refused(lambda v: v['app'].update(databaseMigrationsFrom=['3']))
        self.refused(lambda v: v['app'].update(databaseMigrationsFrom=3))
        self.refused(lambda v: v['app'].update(databaseSchema=1024, databaseMigrationsFrom=list(range(1, rm.MAXIMUM_SOURCES+2))))
        for migrations, schema in (([], 1), ([4, 3], 5), (list(range(1, rm.MAXIMUM_SOURCES+1)), 1024)):
            value = sample()
            value['app'].update(databaseSchema=schema, databaseMigrationsFrom=migrations)
            rm.validate(encode(value))

    def test_file_descriptors(self):
        self.refused(lambda v: v['infrastructure']['setup'].update(name='Yahad.Setup-0.6.1.exe'))
        self.refused(lambda v: v['infrastructure']['setup'].update(name='yahad.setup-0.6.0.exe'))
        self.refused(lambda v: v['app']['archive'].update(name='yahad-windows-app-0.5.0.zip'))
        self.refused(lambda v: v['app']['archive'].update(name='Yahad.Setup-0.6.0.exe'))
        self.refused(lambda v: v['infrastructure']['setup'].update(sha256='D'*64))
        self.refused(lambda v: v['infrastructure']['setup'].update(sha256='d'*63))
        self.refused(lambda v: v['app']['archive'].update(sha256='e'*65))
        self.refused(lambda v: v['infrastructure']['setup'].update(bytes=0))
        self.refused(lambda v: v['app']['archive'].update(bytes=0))
        self.refused(lambda v: v['app']['archive'].update(bytes=-1))
        self.refused(lambda v: v['infrastructure'].update(treeSha256='c'*63))
        self.refused(lambda v: v['app'].update(treeSha256='F'*64))
        self.refused(lambda v: v['infrastructure'].update(sourceCommit='b'*64))

    def test_minimum_from(self):
        self.refused(lambda v: v['minimumFrom'].update(app='0.6.1'))
        self.refused(lambda v: v['minimumFrom'].update(app='1.0.0'))
        self.refused(lambda v: v['minimumFrom'].update(infrastructure='0.5.1'))
        self.refused(lambda v: v['minimumFrom'].update(infrastructure='0.10.0'))
        self.refused(lambda v: v['minimumFrom'].update(app='bad'))
        self.refused(lambda v: v['minimumFrom'].update(infrastructure='01.0.0'))
        value = sample()
        value['minimumFrom'] = {'app': '0.6.0', 'infrastructure': '0.5.0'}
        rm.validate(encode(value))
        # Components compare as numbers, not text: 0.9.0 is older than 0.10.0 although it sorts after it.
        value = sample('0.10.0')
        value['minimumFrom'] = {'app': '0.9.0', 'infrastructure': '0.4.0'}
        rm.validate(encode(value))
        value['minimumFrom']['app'] = '0.11.0'
        with self.assertRaises(ValueError): rm.validate(encode(value))

    def test_byte_level_refusals(self):
        good = encode(sample())
        with self.assertRaises(ValueError): rm.validate(b'\xef\xbb\xbf'+good)
        with self.assertRaises(ValueError): rm.validate(b'')
        with self.assertRaises(ValueError): rm.validate(b'not json')
        with self.assertRaises(ValueError): rm.validate(b'[]')
        with self.assertRaises(ValueError): rm.validate(b'null')
        with self.assertRaises(ValueError): rm.validate(good.replace(b'"format":2', b'"format":2,"format":2'))
        with self.assertRaises(ValueError): rm.validate(good.replace(b'"bytes":1234', b'"bytes":1234,"bytes":1234'))
        rm.validate(good+b' '*(rm.MAXIMUM_BYTES-len(good)))
        with self.assertRaises(ValueError): rm.validate(good+b' '*(rm.MAXIMUM_BYTES-len(good)+1))
        with self.assertRaises(ValueError): rm.validate(b' '*5000+good)


class ComposeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='yahad-release-manifest-', dir=os.environ.get('YAHAD_TEST_TEMP'))
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.version = '0.6.0'
        self.install = {
            'Format': 2,
            'Infrastructure': {'Version': '0.5.0', 'ContractMin': 1, 'ContractMax': 2, 'SourceCommit': INFRA_COMMIT, 'TreeSha256': INFRA_TREE},
            'App': {'Version': self.version, 'SourceCommit': APP_COMMIT, 'RequiresContract': 2, 'TreeSha256': hashlib.sha256(APP_TREE_BYTES).hexdigest(),
                    'DatabaseSchema': 7, 'DatabaseMigrationsFrom': [5, 6]},
            'MinimumFrom': {'App': '0.5.0', 'Infrastructure': '0.4.0'},
            'ZipSha256': '9'*64, 'Files': [],
        }
        self.manifest = self.root/'install-manifest.json'
        self.setup = self.root/'Yahad.Setup-0.6.0.exe'
        self.archive = self.root/'yahad-windows-app-0.6.0.zip'
        self.write_inputs()

    def write_inputs(self):
        self.manifest.write_bytes(json.dumps(self.install).encode('utf-8'))
        self.setup.write_bytes(SETUP_BYTES)
        with zipfile.ZipFile(self.archive, 'w') as archive:
            archive.writestr(APP_TREE_NAME, APP_TREE_BYTES)
            archive.writestr('app/server.js', b'x'*100)

    def compose(self, key_id=KEY_ID, created=1790000000, setup=None, archive=None):
        return rm.compose(self.manifest, setup or self.setup, archive or self.archive, key_id, created)

    def test_output_is_compact_ascii_json_in_contract_order(self):
        data = self.compose()
        self.assertEqual(data, data.decode('ascii').encode('ascii'))
        self.assertFalse(data.startswith(b'\xef\xbb\xbf'))
        self.assertFalse(data.endswith(b'\n'))
        self.assertNotIn(b' ', data)
        self.assertNotIn(b'\n', data)
        self.assertLessEqual(len(data), rm.MAXIMUM_BYTES)
        records = csharp_records()
        value = json.loads(data)
        self.assertEqual(list(value), records['ReleaseManifest'])
        self.assertEqual(list(value['infrastructure']), records['ReleaseInfrastructure'])
        self.assertEqual(list(value['infrastructure']['setup']), records['ReleaseFile'])
        self.assertEqual(list(value['app']), records['ReleaseApp'])
        self.assertEqual(list(value['app']['archive']), records['ReleaseFile'])
        self.assertEqual(list(value['minimumFrom']), records['ReleaseMinimumFrom'])
        self.assertEqual(rm.validate(data), value)

    def test_values_come_from_the_install_manifest_and_files(self):
        value = json.loads(self.compose())
        self.assertEqual(value, {
            'format': 2, 'product': 'yahad-windows-release', 'version': '0.6.0', 'sourceCommit': APP_COMMIT, 'keyId': KEY_ID, 'createdUnix': 1790000000,
            'infrastructure': {'version': '0.5.0', 'contractMin': 1, 'contractMax': 2, 'sourceCommit': INFRA_COMMIT, 'treeSha256': INFRA_TREE,
                               'setup': {'name': 'Yahad.Setup-0.6.0.exe', 'sha256': hashlib.sha256(SETUP_BYTES).hexdigest(), 'bytes': len(SETUP_BYTES)}},
            'app': {'archive': {'name': 'yahad-windows-app-0.6.0.zip', 'sha256': hashlib.sha256(self.archive.read_bytes()).hexdigest(), 'bytes': self.archive.stat().st_size},
                    'treeSha256': hashlib.sha256(APP_TREE_BYTES).hexdigest(), 'requiresContract': 2, 'databaseSchema': 7, 'databaseMigrationsFrom': [5, 6]},
            'minimumFrom': {'app': '0.5.0', 'infrastructure': '0.4.0'},
        })

    def test_compose_is_deterministic(self):
        self.assertEqual(self.compose(), self.compose())

    def test_app_archive_tree_must_equal_the_install_package_tree(self):
        with zipfile.ZipFile(self.archive, 'w') as archive: archive.writestr(APP_TREE_NAME, b'{"tree":"other"}\n')
        with self.assertRaisesRegex(rm.ReleaseError, 'tree differs'): self.compose()

    def test_app_archive_without_a_tree_record_is_refused(self):
        with zipfile.ZipFile(self.archive, 'w') as archive: archive.writestr('app/server.js', b'x')
        with self.assertRaises(KeyError): self.compose()

    def test_wrong_file_names_are_refused(self):
        wrong_setup = self.root/'Setup.exe'
        wrong_setup.write_bytes(SETUP_BYTES)
        with self.assertRaisesRegex(rm.ReleaseError, 'Release file name'): self.compose(setup=wrong_setup)
        other_version = self.root/'Yahad.Setup-0.6.1.exe'
        other_version.write_bytes(SETUP_BYTES)
        with self.assertRaisesRegex(rm.ReleaseError, 'Release file name'): self.compose(setup=other_version)
        wrong_archive = self.root/'app.zip'
        wrong_archive.write_bytes(self.archive.read_bytes())
        with self.assertRaisesRegex(rm.ReleaseError, 'Release file name'): self.compose(archive=wrong_archive)

    def test_key_id_and_created_time_are_validated(self):
        for key in ('', 'abc', '0123456789ABCDEF', '0123456789abcdef0', 'zzzzzzzzzzzzzzzz'):
            with self.subTest(key=key), self.assertRaises(rm.ReleaseError): self.compose(key_id=key)
        with self.assertRaises(rm.ReleaseError): self.compose(created=0)
        with self.assertRaises(rm.ReleaseError): self.compose(created=-1)
        with self.assertRaises(rm.ReleaseError): self.compose(created=True)

    def test_install_manifest_inconsistencies_are_refused(self):
        def refused(change):
            self.write_inputs()
            package = json.loads(self.manifest.read_bytes())
            change(package)
            self.manifest.write_bytes(json.dumps(package).encode('utf-8'))
            with self.assertRaises(ValueError): self.compose()
        refused(lambda p: p['MinimumFrom'].update(App='0.6.1'))
        refused(lambda p: p['MinimumFrom'].update(Infrastructure='0.5.1'))
        refused(lambda p: p['App'].update(DatabaseSchema=0))
        refused(lambda p: p['App'].update(DatabaseSchema=1025))
        refused(lambda p: p['App'].update(DatabaseMigrationsFrom=[5, 5]))
        refused(lambda p: p['App'].update(DatabaseMigrationsFrom=[7]))
        refused(lambda p: p['App'].update(DatabaseSchema=1024, DatabaseMigrationsFrom=list(range(1, 34))))
        refused(lambda p: p['Infrastructure'].update(ContractMin=3, ContractMax=2))
        refused(lambda p: p['Infrastructure'].update(ContractMin=True))
        refused(lambda p: p['Infrastructure'].update(SourceCommit='short'))
        with self.assertRaises(KeyError):
            package = copy.deepcopy(self.install)
            del package['App']['RequiresContract']
            self.manifest.write_bytes(json.dumps(package).encode('utf-8'))
            self.compose()

    def test_write_creates_the_named_file_and_returns_the_value(self):
        output = self.root/'yahad-windows-release-0.6.0.json'
        value = rm.write(self.manifest, self.setup, self.archive, KEY_ID, 1790000000, output)
        self.assertEqual(output.read_bytes(), self.compose())
        self.assertEqual(value, json.loads(output.read_bytes()))

    def test_write_refuses_another_output_name_and_creates_nothing(self):
        for name in ('release.json', 'yahad-windows-release-0.6.1.json', 'yahad-windows-release-0.6.0.json.sig'):
            output = self.root/name
            with self.subTest(name=name), self.assertRaisesRegex(rm.ReleaseError, 'Release manifest name'):
                rm.write(self.manifest, self.setup, self.archive, KEY_ID, 1790000000, output)
            self.assertFalse(output.exists())

    def test_write_never_overwrites(self):
        output = self.root/'yahad-windows-release-0.6.0.json'
        output.write_bytes(b'existing')
        with self.assertRaises(FileExistsError): rm.write(self.manifest, self.setup, self.archive, KEY_ID, 1790000000, output)
        self.assertEqual(output.read_bytes(), b'existing')

    def run_cli(self, *extra, output):
        return subprocess.run([sys.executable, '-m', 'installer.windows.release_manifest', '--install-manifest', str(self.manifest), '--setup', str(self.setup),
                               '--app-archive', str(self.archive), '--output', str(output), *extra], cwd=ROOT, capture_output=True, text=True)

    def test_cli_writes_and_refuses(self):
        output = self.root/'yahad-windows-release-0.6.0.json'
        result = self.run_cli('--key-id', KEY_ID, '--created-unix', '1790000000', output=output)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(result.stdout), json.loads(output.read_bytes()))
        again = self.run_cli('--key-id', KEY_ID, '--created-unix', '1790000000', output=output)
        self.assertNotEqual(again.returncode, 0)
        self.assertIn('Release manifest refused', again.stderr)
        bad = self.run_cli('--key-id', 'nope', '--created-unix', '1790000000', output=self.root/'other.json')
        self.assertNotEqual(bad.returncode, 0)
        self.assertIn('Release manifest refused', bad.stderr)


if __name__ == '__main__': unittest.main()
