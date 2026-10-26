#!/usr/bin/env python3
"""Local administrator recovery; never reads or prints the current password or WA keys."""
import argparse
import getpass
import hashlib
import os
import pathlib
import sqlite3
import subprocess
import sys


def reset(path, username, password):
    if not 8 <= len(password) <= 256:
        raise ValueError('Use 8 to 256 characters')
    salt = os.urandom(16)
    derived = hashlib.scrypt(password.encode('utf8'), salt=salt, n=16384, r=8, p=1, dklen=64, maxmem=64*1024*1024)
    stored = salt.hex() + ':' + derived.hex()
    db = sqlite3.connect(pathlib.Path(path).resolve().as_uri() + '?mode=rw', uri=True, timeout=5)
    db.execute('PRAGMA foreign_keys=ON')
    try:
        with db:
            row = db.execute('SELECT family_id,id FROM users WHERE username=?', (username.strip().lower(),)).fetchone()
            if row is None:
                raise ValueError('Account not found; check the username')
            db.execute('UPDATE users SET password=? WHERE family_id=? AND id=?', (stored, *row))
            db.execute('DELETE FROM sessions WHERE family_id=? AND user_id=?', row)
            if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='paired_devices'").fetchone():
                db.execute('DELETE FROM paired_devices WHERE family_id=? AND user_id=?', row)
    finally:
        db.close()


def main():
    p = argparse.ArgumentParser(description='Reset one local account without changing conversations or WhatsApp keys')
    p.add_argument('username')
    p.add_argument('--data-dir', default='/var/lib/yachad')
    p.add_argument('--no-restart', action='store_true')
    args = p.parse_args()
    path = pathlib.Path(args.data_dir) / 'app.sqlite'
    # Check account existence before asking for a password; do not create a database on a typo.
    db = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)
    try:
        if db.execute('SELECT 1 FROM users WHERE username=?', (args.username.strip().lower(),)).fetchone() is None:
            raise ValueError('Account not found; check the username')
    finally:
        db.close()
    def secret(label):
        if sys.stdin.isatty():
            return getpass.getpass(label)
        return sys.stdin.readline().rstrip('\r\n')
    password = secret('New password (8+ characters, not displayed): ')
    confirmation = secret('Confirm new password: ')
    if password != confirmation:
        raise ValueError('Passwords do not match; nothing changed')
    reset(path, args.username, password)
    print('Password updated. Existing sessions for this account were revoked. Conversations and WhatsApp keys were preserved.')
    if not args.no_restart:
        r = subprocess.run(['/usr/bin/systemctl', 'restart', 'yachad.service'], capture_output=True)
        if r.returncode:
            print('Password was saved. Restart yachad.service locally to clear any sign-in cooldown.', file=sys.stderr)
            return 1
        print('Service restarted. You can sign in with the new password.')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except ValueError as e:
        print(str(e), file=sys.stderr)
        sys.exit(1)
    except Exception:
        print('Recovery failed. Check local account name, database path and permissions. No credentials logged.', file=sys.stderr)
        sys.exit(1)
