import { execFile } from 'node:child_process';
import { resolve, dirname, win32 } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { NativeUpdateCommand, WindowsUpdateTransport } from './windows-update-client.js';

/** The host-selected immutable Platform helper authenticates the Authority
 * service and pipe. HTTP never supplies paths, service names or source URLs. */
export function createWindowsUpdateTransport(helper = resolve(dirname(fileURLToPath(import.meta.url)), '../native/Yahad.Platform.exe')): WindowsUpdateTransport {
  if (process.platform !== 'win32' || !win32.isAbsolute(helper) || !helper.toLowerCase().endsWith('.exe')) throw new Error('WINDOWS_AUTHORITY_UNCONFIGURED');
  const call = (input: unknown) => new Promise<unknown>((accept, reject) => {
    const systemRoot = process.env.SystemRoot || 'C:\\Windows';
    const child = execFile(helper, ['windows-updates'], { encoding: 'utf8', timeout: 16000, maxBuffer: 32768, windowsHide: true,
      env: { SystemRoot: systemRoot, PATH: win32.join(systemRoot, 'System32'), TEMP: process.env.TEMP, TMP: process.env.TMP,
        LOCALAPPDATA: process.env.LOCALAPPDATA, DOTNET_EnableDiagnostics: '0' } }, (error, stdout) => {
      if (error) { reject(new Error('WINDOWS_AUTHORITY_UNAVAILABLE')); return; }
      try {
        const reply = JSON.parse(stdout);
        if (reply.schema !== 1 || reply.ok !== true || !reply.result) throw new Error();
        accept(reply.result);
      } catch { reject(new Error('WINDOWS_AUTHORITY_REPLY')); }
    });
    // No retry: a missing reply may follow durable native acceptance.
    child.stdin?.on('error', () => {});
    child.stdin?.end(JSON.stringify(input));
  });
  return { status: () => call({ kind: 'status' }), submit: (command: NativeUpdateCommand) => call({ kind: 'submit', command }),
    defer: command => call({ kind: 'defer', command }) };
}
