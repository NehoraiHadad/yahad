import {
  createReadStream,
  openSync,
  writeSync,
  closeSync,
  readFileSync,
  statSync,
  readdirSync,
  mkdirSync,
  existsSync,
  rmSync,
  chmodSync,
  lstatSync,
  writeFileSync,
} from 'node:fs';
import { join, resolve } from 'node:path';
import { createCipheriv, createDecipheriv, randomBytes, scryptSync } from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import { acquireDataLock } from '../src/lock.js';
const magic = Buffer.from('YACHAD01');
const derive = (pass: string, salt: Buffer) =>
  scryptSync(pass, salt, 32, { N: 32768, r: 8, p: 1, maxmem: 64 * 1024 ** 2 });
function password(pass: string) {
  if (pass.length < 18) throw new Error('Backup passphrase needs 18+ characters');
}
export async function createBackup(
  dataDir: string,
  keyPath: string,
  outDir: string,
  pass: string,
  now = Date.now(),
) {
  password(pass);
  if (resolve(outDir) === resolve(dataDir) || resolve(outDir).startsWith(resolve(dataDir) + '/'))
    throw new Error('Backups must be outside the live data directory');
  mkdirSync(outDir, { recursive: true, mode: 0o700 });
  const directory = lstatSync(outDir);
  if (!directory.isDirectory() || directory.isSymbolicLink() || directory.mode & 0o077)
    throw new Error('Use a dedicated private backup directory with mode 0700');
  const name = `yachad-${now}.backup`,
    path = join(outDir, name),
    salt = randomBytes(16),
    iv = randomBytes(12),
    head = Buffer.concat([magic, salt, iv]),
    cipher = createCipheriv('aes-256-gcm', derive(pass, salt), iv);
  cipher.setAAD(head);
  let fd: number | undefined;
  const release = await acquireDataLock(dataDir);
  try {
    const db = new DatabaseSync(join(dataDir, 'app.sqlite'));
    db.exec('PRAGMA wal_checkpoint(TRUNCATE)');
    db.close();
    fd = openSync(path, 'wx', 0o600);
    writeSync(fd, head);
    const add = (b: Buffer | string) => writeSync(fd!, cipher.update(b));
    add(
      JSON.stringify({
        version: 1,
        created: now,
        masterKey: readFileSync(keyPath, 'utf8').trim(),
      }) + '\n',
    );
    const files = ['app.sqlite', ...readdirSync(join(dataDir, 'media')).map((n) => 'media/' + n)];
    for (const file of files) {
      if (file !== 'app.sqlite' && !/^media\/[0-9a-f]{64}$/.test(file))
        throw new Error('Unexpected media filename');
      const p = join(dataDir, file);
      add(file + '\t' + statSync(p).size + '\n');
      for await (const chunk of createReadStream(p)) add(chunk as Buffer);
      add('\n');
    }
    add('END\n');
    writeSync(fd, cipher.final());
    writeSync(fd, cipher.getAuthTag());
    closeSync(fd);
    fd = undefined;
    // Seven daily copies at most and nothing older than seven days. Run backup daily.
    const copies = readdirSync(outDir)
      .filter((x) => /^yachad-\d+\.backup$/.test(x))
      .sort((a, b) => Number(b.match(/\d+/)![0]) - Number(a.match(/\d+/)![0]));
    for (let i = 0; i < copies.length; i++)
      if (i >= 7 || Number(copies[i].match(/\d+/)![0]) < now - 7 * 86400000)
        rmSync(join(outDir, copies[i]));
    return path;
  } catch (e) {
    if (fd !== undefined) closeSync(fd);
    if (existsSync(path)) rmSync(path);
    throw e;
  } finally {
    await release();
  }
}
async function decryptPass(path: string, pass: string, consume: (chunk: Buffer) => void) {
  const size = statSync(path).size;
  if (size < 64) throw new Error('Invalid backup');
  const fd = openSync(path, 'r');
  const { readSync } = await import('node:fs');
  const header = Buffer.alloc(36),
    tag = Buffer.alloc(16);
  readSync(fd, header, 0, 36, 0);
  readSync(fd, tag, 0, 16, size - 16);
  closeSync(fd);
  if (!header.subarray(0, 8).equals(magic)) throw new Error('Invalid format');
  const d = createDecipheriv(
    'aes-256-gcm',
    derive(pass, header.subarray(8, 24)),
    header.subarray(24, 36),
  );
  d.setAAD(header);
  d.setAuthTag(tag);
  for await (const chunk of createReadStream(path, { start: 36, end: size - 17 }))
    consume(d.update(chunk as Buffer));
  consume(d.final());
}
export async function restoreBackup(path: string, target: string, pass: string) {
  password(pass);
  if (existsSync(target)) throw new Error('Restore target must not exist');
  // First authenticate the ENTIRE archive before writing any output. No unverified tar extraction.
  await decryptPass(path, pass, () => {});
  mkdirSync(target, { recursive: true, mode: 0o700 });
  let buffer = Buffer.alloc(0),
    metadata: any,
    remaining = -1,
    currentFd: number | undefined,
    expectNewline = false,
    done = false;
  const seen = new Set<string>();
  try {
    await decryptPass(path, pass, (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length) {
        if (remaining > 0) {
          const length = Math.min(remaining, buffer.length);
          writeSync(currentFd!, buffer.subarray(0, length));
          buffer = buffer.subarray(length);
          remaining -= length;
          if (remaining === 0) {
            closeSync(currentFd!);
            currentFd = undefined;
            expectNewline = true;
          }
          continue;
        }
        if (expectNewline) {
          if (buffer[0] !== 10) throw new Error('Invalid framing');
          buffer = buffer.subarray(1);
          expectNewline = false;
          continue;
        }
        const nl = buffer.indexOf(10);
        if (nl < 0) {
          if (buffer.length > 8192) throw new Error('Invalid header');
          break;
        }
        const line = buffer.subarray(0, nl).toString();
        buffer = buffer.subarray(nl + 1);
        if (!metadata) {
          metadata = JSON.parse(line);
          if (metadata.version !== 1 || Buffer.from(metadata.masterKey, 'base64').length !== 32)
            throw new Error('Invalid metadata');
          continue;
        }
        if (line === 'END') {
          done = true;
          if (buffer.length) throw new Error('Trailing data');
          break;
        }
        if (done) throw new Error('Trailing data');
        const [file, size] = line.split('\t');
        if (file !== 'app.sqlite' && !/^media\/[0-9a-f]{64}$/.test(file))
          throw new Error('Invalid path');
        if (seen.has(file)) throw new Error('Duplicate file');
        seen.add(file);
        remaining = Number(size);
        if (!Number.isSafeInteger(remaining) || remaining < 1 || remaining > 8 * 1024 ** 3)
          throw new Error('Invalid size');
        if (file.startsWith('media/'))
          mkdirSync(join(target, 'media'), { recursive: true, mode: 0o700 });
        currentFd = openSync(join(target, file), 'wx', 0o600);
      }
    });
    if (!done || remaining > 0 || currentFd !== undefined || !seen.has('app.sqlite'))
      throw new Error('Incomplete backup');
    mkdirSync(join(target, 'media'), { recursive: true, mode: 0o700 });
    writeFileSync(join(target, 'master.key'), metadata.masterKey, { mode: 0o600, flag: 'wx' });
    const db = new DatabaseSync(join(target, 'app.sqlite'));
    try {
      if (
        (db.prepare('PRAGMA integrity_check').get() as any).integrity_check !== 'ok' ||
        db.prepare('PRAGMA foreign_key_check').all().length
      )
        throw new Error('Invalid restored database');
      db.exec('DELETE FROM sessions; PRAGMA wal_checkpoint(TRUNCATE);');
    } finally {
      db.close();
    }
    return { created: metadata.created, dataDir: target, keyPath: join(target, 'master.key') };
  } catch (e) {
    if (currentFd !== undefined) closeSync(currentFd);
    rmSync(target, { recursive: true, force: true });
    throw e;
  }
}
