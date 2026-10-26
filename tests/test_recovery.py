import importlib.util
import pathlib
import sqlite3
import tempfile
import unittest
from contextlib import closing

spec = importlib.util.spec_from_file_location('recovery', pathlib.Path(__file__).parents[1] / 'scripts/recover-account.py')
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)

class RecoveryTests(unittest.TestCase):
    def test_parent_only_reset_revokes_sessions_and_preserves_other_data(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = pathlib.Path(tmp) / 'app.sqlite'
            with closing(sqlite3.connect(path)) as db:
                db.executescript("""CREATE TABLE users(family_id TEXT,id TEXT,username TEXT,role TEXT,password TEXT);
                CREATE TABLE sessions(family_id TEXT,user_id TEXT);
                CREATE TABLE auth(value TEXT); INSERT INTO auth VALUES('encrypted fixture');
                INSERT INTO users VALUES('f','p','parent','parent','old'),('f','c','child','child','child-old');
                INSERT INTO sessions VALUES('f','p'),('f','c');""")
            with self.assertRaises(ValueError): recovery.reset(path, 'child', 'a random long phrase')
            with self.assertRaises(ValueError): recovery.reset(path, 'parent', '12345678')
            with self.assertRaises(ValueError): recovery.reset(path, 'parent', '\U0001f600' * 200)
            recovery.reset(path, 'parent', 'a random long phrase')
            with closing(sqlite3.connect(path)) as db:
                self.assertEqual(db.execute('SELECT * FROM sessions').fetchall(), [('f','c')])
                self.assertEqual(db.execute('SELECT value FROM auth').fetchone()[0], 'encrypted fixture')
                self.assertEqual(db.execute("SELECT password FROM users WHERE id='c'").fetchone()[0], 'child-old')
                self.assertNotIn('a random long phrase', db.execute("SELECT password FROM users WHERE id='p'").fetchone()[0])

if __name__ == '__main__': unittest.main()
