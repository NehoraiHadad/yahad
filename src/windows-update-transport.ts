import { execFile } from 'node:child_process';
import { resolve, dirname, win32 } from 'node:path';
import { fileURLToPath } from 'node:url';
import { z } from 'zod';
import type { UpdateTarget } from './updates.js';

export type NativeUpdateCommand = { id: string; action: 'check'|'install'|'set_mode'; target?: UpdateTarget; mode?: 'auto'|'notify'; familyId: string; userId: string };
/** Trusted in-process transport, never constructed from an HTTP body or environment flag. */
export interface WindowsUpdateTransport {
  status(): Promise<unknown>;
  submit(command: NativeUpdateCommand): Promise<unknown>;
}

/** Only `{schema:1, ok:true, result}` carries a result. The Platform helper of
 * this release answers every request with a refusal, so updates are disabled. */
export function windowsUpdateResult(stdout: string): unknown {
  const reply = JSON.parse(stdout);
  if (reply?.schema !== 1 || reply.ok !== true || !reply.result) throw new Error('WINDOWS_UPDATES_REPLY');
  return reply.result;
}

/** The host-selected immutable Platform helper answers `windows-updates`.
 * HTTP never supplies paths, service names or source URLs. */
export function createWindowsUpdateTransport(helper = resolve(dirname(fileURLToPath(import.meta.url)), '../native/Yahad.Platform.exe')): WindowsUpdateTransport {
  if (process.platform !== 'win32' || !win32.isAbsolute(helper) || !helper.toLowerCase().endsWith('.exe')) throw new Error('WINDOWS_UPDATES_UNCONFIGURED');
  const call = (input: unknown) => new Promise<unknown>((accept, reject) => {
    const systemRoot = process.env.SystemRoot || 'C:\Windows';
    const child = execFile(helper, ['windows-updates'], { encoding: 'utf8', timeout: 16000, maxBuffer: 32768, windowsHide: true,
      env: { SystemRoot: systemRoot, PATH: win32.join(systemRoot, 'System32'), TEMP: process.env.TEMP, TMP: process.env.TMP,
        LOCALAPPDATA: process.env.LOCALAPPDATA, DOTNET_EnableDiagnostics: '0' } }, (error, stdout) => {
      if (error) { reject(new Error('WINDOWS_UPDATES_UNAVAILABLE')); return; }
      try { accept(windowsUpdateResult(stdout)); } catch { reject(new Error('WINDOWS_UPDATES_REPLY')); }
    });
    // No retry: a missing reply may follow durable native acceptance.
    child.stdin?.on('error', () => {});
    child.stdin?.end(JSON.stringify(input));
  });
  return { status: () => call({ kind: 'status' }), submit: (command: NativeUpdateCommand) => call({ kind: 'submit', command }) };
}

const submitted = z.object({ state: z.enum(['queued','pending']) }).strict();
export async function submitWindowsUpdate(transport: WindowsUpdateTransport, command: NativeUpdateCommand) {
  // A lost reply may follow durable acceptance. Do not retry it here.
  return submitted.parse(await transport.submit(command)).state;
}
