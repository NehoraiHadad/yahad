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
import contextlib


def reset(path, username, password):
    if not 14 <= len(password.encode('utf-16-le')) // 2 <= 256:
        raise ValueError('Use 14 to 256 characters; spaces and memorable passphrases are supported')
    salt = os.urandom(16)
    derived = hashlib.scrypt(password.encode('utf8'), salt=salt, n=16384, r=8, p=1, dklen=64, maxmem=64*1024*1024)
    stored = salt.hex() + ':' + derived.hex()
    db = sqlite3.connect(pathlib.Path(path).resolve().as_uri() + '?mode=rw', uri=True, timeout=5)
    db.execute('PRAGMA foreign_keys=ON')
    try:
        with db:
            row = db.execute("SELECT family_id,id FROM users WHERE username=? AND role='parent'", (username.strip().lower(),)).fetchone()
            if row is None:
                raise ValueError('Account not found; check the username')
            db.execute('UPDATE users SET password=? WHERE family_id=? AND id=?', (stored, *row))
            db.execute('DELETE FROM sessions WHERE family_id=? AND user_id=?', row)
            if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='paired_devices'").fetchone():
                db.execute('DELETE FROM paired_devices WHERE family_id=? AND user_id=?', row)
    finally:
        db.close()


def main():
    if not hasattr(os, 'geteuid') or os.geteuid() != 0:
        raise ValueError('Owner-only local recovery requires sudo on the Pi')
    p = argparse.ArgumentParser(description='Reset one local account without changing conversations or WhatsApp keys')
    p.add_argument('username')
    p.add_argument('--data-dir', default='/var/lib/yachad')
    p.add_argument('--no-restart', action='store_true')
    args = p.parse_args()
    path = pathlib.Path(args.data_dir) / 'app.sqlite'
    # Check account existence before asking for a password; do not create a database on a typo.
    db = sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True)
    try:
        if db.execute("SELECT 1 FROM users WHERE username=? AND role='parent'", (args.username.strip().lower(),)).fetchone() is None:
            raise ValueError('Parent account not found; use the parent UI for child passwords')
    finally:
        db.close()
    def secret(label):
        if sys.stdin.isatty():
            return getpass.getpass(label)
        return sys.stdin.readline().rstrip('\r\n')
    password = secret('New passphrase (14+ characters, spaces allowed, not displayed): ')
    confirmation = secret('Confirm new password: ')
    if password != confirmation:
        raise ValueError('Passwords do not match; nothing changed')
    if not 14 <= len(password.encode('utf-16-le')) // 2 <= 256:
        raise ValueError('Use 14 to 256 characters; nothing changed')
    # Do not contend with a running service, backup, setup, or upgrade. This
    # kernel lock is also held by the production service and offline helpers.
    import fcntl
    import stat
    # Acquire the updater lock BEFORE stopping or restarting its application.
    # Otherwise a refused recovery could restart the app in the middle of backup.
    with contextlib.ExitStack() as stack:
        worker = pathlib.Path('/var/lib/yachad-updates/worker.lock')
        if worker.parent.exists():
            directory = worker.parent.lstat()
            if not stat.S_ISDIR(directory.st_mode) or directory.st_uid != 0 or directory.st_mode & 0o022:
                raise ValueError('Unsafe updater directory')
            fd = os.open(worker, os.O_RDONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600)
            stack.callback(os.close, fd)
            if not stat.S_ISREG(os.fstat(fd).st_mode): raise ValueError('Unsafe updater lock')
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        active = subprocess.run(['/usr/bin/systemctl', 'is-active', '--quiet', 'yachad.service'], capture_output=True).returncode == 0
        if active and args.no_restart:
            raise ValueError('Stop yachad.service before using --no-restart')
        if active:
            subprocess.run(['/usr/bin/systemctl', 'stop', 'yachad.service'], check=True, capture_output=True)
        saved = False
        try:
            guard = pathlib.Path(args.data_dir) / 'process.guard'
            if guard.exists():
                guard_fd = os.open(guard, os.O_RDONLY | os.O_NOFOLLOW)
                stack.callback(os.close, guard_fd)
                if not stat.S_ISREG(os.fstat(guard_fd).st_mode): raise ValueError('Unsafe data lock')
                fcntl.flock(guard_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            reset(path, args.username, password)
            saved = True
        finally:
            # Release data guard before starting Node, while retaining worker lock.
            if 'guard_fd' in locals(): fcntl.flock(guard_fd, fcntl.LOCK_UN)
            if active:
                result = subprocess.run(['/usr/bin/systemctl', 'start', 'yachad.service'], capture_output=True)
                if result.returncode:
                    raise ValueError('Password saved and sessions revoked; service restart failed. Start yachad.service locally.'
                                     if saved else 'No password reset completed; restoring the previously running service failed.')
    print('Password updated. Existing sessions for this account were revoked. Conversations and WhatsApp keys were preserved.')
    print('Service restarted.' if active else 'Service was already stopped and remains stopped.')
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
