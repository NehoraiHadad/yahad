import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from installer.windows import infra_inputs

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT/'installer'/'windows'/'infra_inputs.py'
OUTSIDE = ('src/app.ts', 'README.md', 'installer/Yahad.Install.Tests/x.cs', 'installer/build.ps1')


def git(repo, *args, data=None):
    return subprocess.run(['git', '-C', str(repo), *args], input=data, check=True, capture_output=True).stdout.decode('utf-8').strip()


def input_path(name):
    return name+'file.txt' if name.endswith('/') else name


class InfraInputsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='yahad-infra-inputs-', dir=os.environ.get('YAHAD_TEST_TEMP'))
        self.addCleanup(self.temporary.cleanup)
        self.repo = Path(self.temporary.name)/'repo'
        self.repo.mkdir()
        git(self.repo, 'init', '-q')
        for key, value in (('user.name', 'Test'), ('user.email', 'test@example.invalid'), ('core.autocrlf', 'false'), ('commit.gpgsign', 'false')):
            git(self.repo, 'config', key, value)
        for name in (*(input_path(n) for n in infra_inputs.INPUTS), *OUTSIDE): self.write(name, 'initial '+name)
        self.commit('initial')

    def write(self, name, text):
        path = self.repo/name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(text.encode('utf-8'))

    def commit(self, message):
        git(self.repo, 'add', '-A')
        git(self.repo, 'commit', '-q', '-m', message)
        return self.head()

    def head(self): return git(self.repo, 'rev-parse', 'HEAD')

    def test_digest_is_stable_hex_and_commit_form_agrees(self):
        value = infra_inputs.digest(self.repo)
        self.assertRegex(value, r'\A[a-f0-9]{64}\Z')
        self.assertEqual(value, infra_inputs.digest(self.repo))
        self.assertEqual(value, infra_inputs.digest(self.repo, commit=self.head()))

    def test_every_input_changes_the_digest_and_nothing_else_does(self):
        previous = infra_inputs.digest(self.repo)
        for name in infra_inputs.INPUTS:
            with self.subTest(input=name):
                self.write(input_path(name), 'changed '+name)
                self.commit('change '+name)
                current = infra_inputs.digest(self.repo)
                self.assertNotEqual(current, previous)
                previous = current
        for name in OUTSIDE:
            with self.subTest(outside=name):
                self.write(name, 'changed '+name)
                self.commit('change '+name)
                self.assertEqual(infra_inputs.digest(self.repo), previous)

    def test_new_file_in_an_input_directory_changes_the_digest(self):
        before = infra_inputs.digest(self.repo)
        self.write('installer/windows/nested/extra.json', '{}')
        self.commit('add')
        self.assertNotEqual(infra_inputs.digest(self.repo), before)

    def test_test_project_and_sibling_directories_are_not_inputs(self):
        before = infra_inputs.digest(self.repo)
        self.write('installer/Yahad.Setup.Tests/y.cs', 'x')
        self.write('installer/Yahad.Setup2/y.cs', 'x')
        self.commit('add')
        self.assertEqual(infra_inputs.digest(self.repo), before)

    def test_file_mode_matters(self):
        before = infra_inputs.digest(self.repo)
        git(self.repo, 'update-index', '--chmod=+x', 'installer/package.ps1')
        git(self.repo, 'commit', '-q', '-m', 'mode')
        self.assertNotEqual(infra_inputs.digest(self.repo, commit=self.head()), before)

    def test_missing_input_is_refused(self):
        git(self.repo, 'rm', '-q', 'installer/release-key.spki')
        git(self.repo, 'commit', '-q', '-m', 'remove key')
        with self.assertRaisesRegex(infra_inputs.InputError, 'Missing infrastructure input: installer/release-key.spki'):
            infra_inputs.digest(self.repo)

    def test_empty_input_directory_is_refused(self):
        git(self.repo, 'rm', '-q', '-r', 'installer/Yahad.Updater')
        git(self.repo, 'commit', '-q', '-m', 'remove updater')
        with self.assertRaisesRegex(infra_inputs.InputError, 'Missing infrastructure input: installer/Yahad.Updater/'):
            infra_inputs.digest(self.repo)

    def test_modified_tracked_input_refuses_head_but_not_an_explicit_commit(self):
        head = self.head()
        expected = infra_inputs.digest(self.repo)
        self.write('installer/NuGet.Config', 'dirty')
        with self.assertRaisesRegex(infra_inputs.InputError, 'Uncommitted'): infra_inputs.digest(self.repo)
        self.assertEqual(infra_inputs.digest(self.repo, commit=head), expected)

    def test_untracked_file_in_an_input_directory_refuses_head_but_not_an_explicit_commit(self):
        head = self.head()
        expected = infra_inputs.digest(self.repo)
        self.write('installer/Yahad.Platform/new.cs', 'new')
        with self.assertRaisesRegex(infra_inputs.InputError, 'Uncommitted'): infra_inputs.digest(self.repo)
        self.assertEqual(infra_inputs.digest(self.repo, commit=head), expected)

    def test_changes_outside_the_inputs_do_not_refuse_head(self):
        expected = infra_inputs.digest(self.repo)
        self.write('src/app.ts', 'dirty')
        self.write('untracked.txt', 'new')
        self.assertEqual(infra_inputs.digest(self.repo), expected)

    def test_commit_must_be_a_full_hex_commit_id(self):
        head = self.head()
        for bad in (head[:7], 'HEAD', head.upper(), head+'0', ''):
            with self.subTest(commit=bad), self.assertRaises(infra_inputs.InputError): infra_inputs.digest(self.repo, commit=bad)

    def test_commit_must_name_a_commit_object(self):
        tree = git(self.repo, 'rev-parse', 'HEAD^{tree}')
        with self.assertRaisesRegex(infra_inputs.InputError, 'Not a commit'): infra_inputs.digest(self.repo, commit=tree)

    def test_link_entry_is_refused(self):
        blob = git(self.repo, 'hash-object', '-w', '--stdin', data=b'target')
        git(self.repo, 'update-index', '--add', '--cacheinfo', '120000,'+blob+',installer/windows/link')
        git(self.repo, 'commit', '-q', '-m', 'link')
        with self.assertRaisesRegex(infra_inputs.InputError, 'Unsupported infrastructure input entry'):
            infra_inputs.digest(self.repo, commit=self.head())

    def test_digest_equals_hand_computation(self):
        self.write('installer/windows/Zeta.json', 'Z')
        self.write('installer/windows/alpha.json', 'a')
        self.write('installer/windows/é.json', 'e')
        git(self.repo, 'update-index', '--chmod=+x', 'installer/package.ps1')
        self.commit('more')
        head = self.head()
        listing = git(self.repo, '-c', 'core.quotepath=off', 'ls-tree', '-r', '--full-tree', head, '--', *infra_inputs.INPUTS)
        found = []
        for line in listing.splitlines():
            meta, path = line.split('\t', 1)
            mode, kind, oid = meta.split(' ')
            self.assertEqual(kind, 'blob')
            found.append((path, mode, oid))
        self.assertGreater(len(found), len(infra_inputs.INPUTS))
        found.sort(key=lambda entry: entry[0].encode('utf-16-be'))
        text = infra_inputs.PREFIX+''.join(f'{mode} {oid} {path}\n' for path, mode, oid in found).encode('utf-8')
        self.assertEqual(infra_inputs.PREFIX, b'yahad-infrastructure-inputs-v1\n')
        self.assertEqual(infra_inputs.digest(self.repo), hashlib.sha256(text).hexdigest())

    def run_cli(self, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *args], cwd=ROOT, capture_output=True, text=True)

    def test_cli_digests_the_checked_in_repo(self):
        head = git(ROOT, 'rev-parse', 'HEAD')
        result = self.run_cli('digest', '--commit', head)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertRegex(result.stdout.strip(), r'\A[a-f0-9]{64}\Z')
        self.assertEqual(result.stdout.strip(), infra_inputs.digest(ROOT, commit=head))

    def test_cli_digests_a_clean_temp_repo(self):
        result = self.run_cli('digest', '--repo', str(self.repo))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), infra_inputs.digest(self.repo))

    def test_cli_refuses_a_dirty_repo(self):
        self.write('installer/package.ps1', 'dirty')
        result = self.run_cli('digest', '--repo', str(self.repo))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('INFRA_INPUTS_REFUSED', result.stderr)
        self.assertEqual(result.stdout, '')

    def test_cli_refuses_a_short_commit(self):
        result = self.run_cli('digest', '--repo', str(self.repo), '--commit', self.head()[:8])
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('INFRA_INPUTS_REFUSED', result.stderr)


if __name__ == '__main__': unittest.main()
