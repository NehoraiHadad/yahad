import { closeSync, constants, fstatSync, mkdirSync, chmodSync, openSync, readFileSync } from 'node:fs';
import { execFileSync, spawn } from 'node:child_process';
import { dirname, join, resolve, win32 } from 'node:path';
import { fileURLToPath } from 'node:url';
import lockfile from 'proper-lockfile';

export interface PlatformFiles {
  readMasterKey(path: string): Buffer;
  readSetupCapability(path: string): Buffer | undefined;
  acquireDataLock(directory: string): Promise<() => Promise<void>>;
  setupCapabilityPath(): string;
}

function readPosix(path: string, bootstrap: boolean): Buffer | undefined {
  let fd: number | undefined;
  try {
    fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
    const st = fstatSync(fd);
    if (!st.isFile() || st.nlink !== 1 || st.size < 1 || st.size > (bootstrap ? 1024 : 4096) ||
      st.mode & (bootstrap ? 0o027 : 0o077) || bootstrap && st.uid !== 0)
      throw new Error('Private file permissions or type refused');
    return readFileSync(fd);
  } catch (error: any) {
    if (bootstrap && error.code === 'ENOENT') return undefined;
    throw error;
  } finally { if (fd !== undefined) closeSync(fd); }
}

function helperEnvironment(): NodeJS.ProcessEnv {
  // Do not inherit DOTNET_STARTUP_HOOKS, profiler variables or arbitrary PATH entries.
  const systemRoot = process.env.SystemRoot || 'C:\\Windows';
  return { SystemRoot: systemRoot, PATH: win32.join(systemRoot, 'System32'), TEMP: process.env.TEMP,
    TMP: process.env.TMP, LOCALAPPDATA: process.env.LOCALAPPDATA, DOTNET_EnableDiagnostics: '0' };
}

function decodeReply(output: string): any {
  try {
    if (output.length > 8192) throw new Error();
    const reply = JSON.parse(output);
    if (reply.schema !== 1 || reply.ok !== true) throw new Error();
    return reply;
  } catch { throw new Error('Native file boundary refused'); }
}

export function createPlatformFiles(target: NodeJS.Platform = process.platform,
  helper = resolve(dirname(fileURLToPath(import.meta.url)), '../native/Yahad.Platform.exe')): PlatformFiles {
  if (target !== 'win32') return {
    readMasterKey: path => readPosix(path, false)!,
    readSetupCapability: path => readPosix(path, true),
    setupCapabilityPath: () => '/etc/yachad/setup-capability.json',
    async acquireDataLock(directory) {
      mkdirSync(directory, { recursive: true, mode: 0o700 }); chmodSync(directory, 0o700);
      return lockfile.lock(directory, { lockfilePath: join(directory, 'process.lock'), realpath: true,
        stale: 10000, update: 3000, retries: 0 });
    },
  };
  if (!win32.isAbsolute(helper) || !helper.toLowerCase().endsWith('.exe')) throw new Error('Native helper path required');
  const read = (path: string, bootstrap: boolean): Buffer | undefined => {
    let reply: any;
    try {
      reply = decodeReply(execFileSync(helper, ['read-secret', bootstrap ? 'setup-capability' : 'master-key', path],
        { encoding: 'utf8', timeout: 10000, maxBuffer: 8192, windowsHide: true, env: helperEnvironment(), stdio: ['ignore', 'pipe', 'pipe'] }));
    } catch { throw new Error('Native file boundary refused'); }
    if (bootstrap && reply.exists === false) return undefined;
    if (reply.exists !== true || typeof reply.bytes !== 'string' || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(reply.bytes))
      throw new Error('Native file boundary refused');
    const bytes = Buffer.from(reply.bytes, 'base64');
    if (bytes.length < 1 || bytes.length > (bootstrap ? 1024 : 4096)) throw new Error('Native file boundary refused');
    return bytes;
  };
  return {
    readMasterKey: path => read(path, false)!,
    readSetupCapability: path => read(path, true),
    setupCapabilityPath: () => win32.join(process.env.ProgramData || 'C:\\ProgramData', 'Yahad', 'Secrets', 'setup-capability.json'),
    acquireDataLock(directory) {
      const descriptor = process.env.YAHAD_NATIVE_LEASE_FILE, nonce = process.env.YAHAD_NATIVE_LEASE_NONCE;
      if (descriptor || nonce) {
        if (!descriptor || !win32.isAbsolute(descriptor) || !nonce || !/^[a-f0-9]{64}$/.test(nonce)) throw new Error('Native host lease refused');
        const reply = decodeReply(execFileSync(helper, ['adopt-host-lease', descriptor, directory, String(process.pid), nonce],
          { encoding: 'utf8', timeout: 10000, maxBuffer: 8192, windowsHide: true, env: helperEnvironment(), stdio: ['ignore', 'pipe', 'pipe'] }));
        if (reply.locked !== true) throw new Error('Native host lease refused');
        // Keep the duplicated kernel gate alive until this process exits, after
        // Store.close(). Never release it early or reacquire through a gap.
        return Promise.resolve(async () => {});
      }
      return new Promise((accept, reject) => {
        const child = spawn(helper, ['lock-data', directory, String(process.pid)], { windowsHide: true, env: helperEnvironment(), stdio: ['pipe', 'pipe', 'pipe'] });
        let buffer = '', settled = false, acquired = false, released = false;
        const fail = () => {
          if (settled) return;
          settled = true; clearTimeout(timer); child.stdin.destroy(); child.kill();
          reject(new Error('Native data lock refused'));
        };
        const timer = setTimeout(fail, 10000);
        child.on('error', fail); child.stdin.on('error', () => {});
        // Stderr is drained but never retained; protocol errors cannot include private data.
        child.stderr.resume();
        child.once('exit', () => {
          if (!settled) fail();
          else if (acquired && !released) {
            process.stderr.write('Native data lock lost. Stopping before further writes.\n');
            process.exit(1);
          }
        });
        child.stdout.on('data', (chunk: Buffer) => {
          if (settled) return;
          buffer += chunk.toString('utf8');
          if (buffer.length > 8192) { fail(); return; }
          if (!buffer.includes('\n')) return;
          try {
            if (decodeReply(buffer.trim()).locked !== true) throw new Error();
            settled = true; acquired = true; clearTimeout(timer);
            accept(async () => {
              if (released) return; released = true;
              if (child.exitCode !== null || child.signalCode !== null) throw new Error('Native data lock lost');
              await new Promise<void>((done, refused) => {
                const deadline = setTimeout(() => { child.kill(); refused(new Error('Native lock release timed out')); }, 5000);
                child.once('exit', code => { clearTimeout(deadline); code === 0 ? done() : refused(new Error('Native lock release failed')); });
                child.stdin.end('release\n');
              });
            });
          } catch { fail(); }
        });
      });
    },
  };
}

export const platformFiles = createPlatformFiles();
