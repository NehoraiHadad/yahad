"""Synthetic schema-2 account recovery; never controls a real service or account."""
import hashlib
import importlib.util
import io
import json
import pathlib
import shutil
import sqlite3
import subprocess
import tempfile
import unittest
from contextlib import closing
from unittest.mock import patch

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('recovery', ROOT / 'scripts/recover-account.py')
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


class RecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = pathlib.Path(self.temp.name) / 'app.sqlite'
        with closing(sqlite3.connect(self.path)) as db:
            db.executescript("""
                PRAGMA foreign_keys=ON;
                CREATE TABLE schema_version(version INTEGER NOT NULL);
                INSERT INTO schema_version VALUES(2);
                CREATE TABLE users(family_id TEXT,id TEXT,name TEXT,name_key TEXT UNIQUE,role TEXT,password TEXT,
                    PRIMARY KEY(family_id,id));
                CREATE TABLE sessions(family_id TEXT,user_id TEXT,
                    FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id));
                CREATE TABLE paired_devices(family_id TEXT,user_id TEXT,
                    FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id));
                CREATE TABLE auth(family_id TEXT,key TEXT,value BLOB);
                CREATE TABLE messages(family_id TEXT,body BLOB);
                CREATE TABLE grants(family_id TEXT,user_id TEXT,group_id TEXT,
                    FOREIGN KEY(family_id,user_id) REFERENCES users(family_id,id));
                INSERT INTO users VALUES('f','p','הורה Élan','הורה élan','parent','parent-old'),
                    ('f','c','ילד','ילד','child','child-old'),
                    ('other','p','Another Parent','another parent','parent','other-old');
                INSERT INTO sessions VALUES('f','p'),('f','c'),('other','p');
                INSERT INTO paired_devices VALUES('f','p'),('f','c'),('other','p');
                INSERT INTO auth VALUES('f','creds',X'0012ff3456'),('other','creds',X'21ab00ef');
                INSERT INTO messages VALUES('f',X'10203aff'),('other',X'99ab10');
                INSERT INTO grants VALUES('f','c','group');
            """)

    def rows(self, table):
        with closing(sqlite3.connect(self.path)) as db:
            return db.execute('SELECT * FROM ' + table + ' ORDER BY rowid').fetchall()

    def snapshot(self):
        return {table: self.rows(table) for table in
                ('schema_version', 'users', 'sessions', 'paired_devices', 'auth', 'messages', 'grants')}

    def test_parent_only_reset_revokes_sessions_and_preserves_other_data(self):
        before = self.snapshot()
        for name, password in [('ילד', 'a random long phrase'), ('missing', 'a random long phrase'),
                               ('הורה Élan', '12345678'), ('הורה Élan', '\U0001f600' * 200)]:
            with self.subTest(name=name, password_length=len(password)):
                with self.assertRaises(ValueError):
                    recovery.reset(self.path, name, password)
                self.assertEqual(self.snapshot(), before)
        recovery.reset(self.path, '  הורה   E\u0301LAN  ', 'a random long phrase')
        after = self.snapshot()
        self.assertEqual(after['sessions'], [('f', 'c'), ('other', 'p')])
        self.assertEqual(after['paired_devices'], [('f', 'c'), ('other', 'p')])
        for table in ('schema_version', 'auth', 'messages', 'grants'):
            self.assertEqual(after[table], before[table])
        self.assertEqual(after['users'][0][:-1], before['users'][0][:-1])
        self.assertEqual(after['users'][1:], before['users'][1:])
        salt, derived = after['users'][0][-1].split(':')
        actual = hashlib.scrypt(b'a random long phrase', salt=bytes.fromhex(salt),
                                n=16384, r=8, p=1, dklen=64, maxmem=64*1024*1024)
        self.assertEqual(actual.hex(), derived)
        with closing(sqlite3.connect(self.path)) as db:
            self.assertEqual(db.execute('PRAGMA foreign_key_check').fetchall(), [])

    def test_password_length_remains_utf16_not_python_codepoints(self):
        with self.assertRaises(ValueError):
            recovery.reset(self.path, 'הורה Élan', '\U0001f600' * 6)
        recovery.reset(self.path, 'הורה Élan', '\U0001f600' * 7)
        self.assertNotEqual(self.rows('users')[0][-1], 'parent-old')

    def test_failed_revocation_rolls_password_and_sessions_back_together(self):
        with closing(sqlite3.connect(self.path)) as db:
            db.executescript("""
                CREATE TRIGGER synthetic_revocation_failure BEFORE DELETE ON paired_devices
                BEGIN SELECT RAISE(ABORT, 'synthetic revocation failure'); END;
            """)
        before = self.snapshot()
        with self.assertRaises(sqlite3.Error):
            recovery.reset(self.path, 'הורה Élan', 'a random long phrase')
        self.assertEqual(self.snapshot(), before)

    def test_legacy_future_missing_or_ambiguous_schema_is_rejected_without_mutations(self):
        for versions in ([1], [3], [], [2, 2]):
            with self.subTest(versions=versions):
                with closing(sqlite3.connect(self.path)) as db:
                    db.execute('DELETE FROM schema_version')
                    db.executemany('INSERT INTO schema_version VALUES(?)', [(value,) for value in versions])
                    db.commit()
                before = self.snapshot()
                with self.assertRaisesRegex(ValueError, 'requires database schema 2'):
                    recovery.reset(self.path, 'הורה Élan', 'a random long phrase')
                self.assertEqual(self.snapshot(), before)
        with closing(sqlite3.connect(self.path)) as db:
            db.execute('DROP TABLE schema_version')
        with self.assertRaisesRegex(ValueError, 'requires database schema 2'):
            recovery.reset(self.path, 'הורה Élan', 'a random long phrase')

    def test_schema1_cli_rejects_before_secret_prompt_or_service_command(self):
        with closing(sqlite3.connect(self.path)) as db:
            db.execute('UPDATE schema_version SET version=1')
            db.commit()
        with patch.object(recovery.os, 'geteuid', return_value=0, create=True), \
             patch.object(recovery.sys, 'argv', ['recover-account.py', 'הורה Élan', '--data-dir', self.temp.name]), \
             patch.object(recovery.sys, 'stdin', io.StringIO('')), \
             patch.object(recovery.subprocess, 'run', side_effect=AssertionError('must not control service')), \
             patch.object(recovery.getpass, 'getpass', side_effect=AssertionError('must not ask for password')):
            with self.assertRaisesRegex(ValueError, 'requires database schema 2'):
                recovery.main()

    def test_typo_in_database_path_never_creates_a_database(self):
        missing = self.path.with_name('missing.sqlite')
        with self.assertRaises(sqlite3.Error):
            recovery.reset(missing, 'הורה Élan', 'a random long phrase')
        self.assertFalse(missing.exists())


class NameNormalizationTests(unittest.TestCase):
    valid = ['הורה', '  אבא   אמא  ', 'שָׁלוֹם', 'E\u0301LAN', 'Élan', 'İPEK', 'ΟΣ',
             'Straße', 'USER._-123', '\U00010400name', '\U00010400' * 60, 'Ⅳ', 'a' * 60]
    invalid = ['', '   ', '.', '._-', '\u0301', 'a' * 61, '\U00010400' * 61, 'a\tb',
               '\tparent', 'parent\n', 'a\u202eb', 'a\u200db', 'a\u00a0b', '\ufeffparent',
               'a\x00b', '😀', "a'b", 'a/b']

    def test_account_key_normalization_and_rejection(self):
        self.assertEqual(recovery.account_name_key('  אבא   אמא  '), 'אבא אמא')
        self.assertEqual(recovery.account_name_key('E\u0301LAN'), 'élan')
        self.assertEqual(recovery.account_name_key('İPEK'), 'i\u0307pek')
        self.assertEqual(recovery.account_name_key('ΟΣ'), 'ος')
        self.assertEqual(recovery.account_name_key('Straße'), 'straße')
        for name in self.valid:
            with self.subTest(name=name):
                self.assertTrue(recovery.account_name_key(name))
        for name in self.invalid:
            with self.subTest(name=repr(name)):
                with self.assertRaises(ValueError):
                    recovery.account_name_key(name)

    def test_unknown_unicode_is_rejected_without_guessing_an_account_key(self):
        with self.assertRaisesRegex(ValueError, 'unsupported by this Python Unicode database'):
            recovery.account_name_key('parent\u0378')

    @unittest.skipUnless(shutil.which('node'), 'Node required for shared normalizer parity')
    def test_python_matches_actual_typescript_normalizer_for_supported_unicode_fixtures(self):
        names = self.valid + self.invalid
        script = """
            import { accountNameKey } from './src/account-name.ts';
            let input = ''; for await (const chunk of process.stdin) input += chunk;
            console.log(JSON.stringify(JSON.parse(input).map(name => {
              try { return { key: accountNameKey(name) }; } catch { return { invalid: true }; }
            })));
        """
        result = subprocess.run(['node', '--import', 'tsx', '--input-type=module', '-e', script],
                                cwd=ROOT, input=json.dumps(names), capture_output=True, text=True,
                                encoding='utf-8', check=True)
        actual = []
        for name in names:
            try:
                actual.append({'key': recovery.account_name_key(name)})
            except ValueError:
                actual.append({'invalid': True})
        self.assertEqual(actual, json.loads(result.stdout))


if __name__ == '__main__':
    unittest.main()
