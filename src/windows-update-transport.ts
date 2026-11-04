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

/** Why updates are unavailable: a fixed stage and at most a Windows error number. */
export class WindowsUpdateRefusal extends Error {
  constructor(readonly reason: string, message = 'WINDOWS_UPDATES_REPLY') { super(message); }
}
const refusalReason = /^[A-Z][A-Z0-9_]{0,47}(:\d{1,10})?$/;
/** Only `{schema:1, ok:true, result}` carries a result. The Platform helper refuses
 * with `ok:false` whenever the Updater service cannot be reached and verified. */
export function windowsUpdateResult(stdout: string): unknown {
  let reply;
  try { reply = JSON.parse(stdout); } catch { throw new WindowsUpdateRefusal('PLATFORM_REPLY'); }
  if (reply?.schema !== 1 || reply.ok !== true || !reply.result)
    throw new WindowsUpdateRefusal(typeof reply?.reason === 'string' && refusalReason.test(reply.reason) ? reply.reason : 'PLATFORM_REPLY');
  return reply.result;
}

/** The host-selected immutable Platform helper answers `windows-updates`.
 * HTTP never supplies paths, service names or source URLs. */
export function createWindowsUpdateTransport(helper = resolve(dirname(fileURLToPath(import.meta.url)), '../native/Yahad.Platform.exe')): WindowsUpdateTransport {
  if (process.platform !== 'win32' || !win32.isAbsolute(helper) || !helper.toLowerCase().endsWith('.exe')) throw new Error('WINDOWS_UPDATES_UNCONFIGURED');
  const call = (input: unknown) => new Promise<unknown>((accept, reject) => {
    const systemRoot = process.env.SystemRoot || 'C:\\Windows';
    const child = execFile(helper, ['windows-updates'], { encoding: 'utf8', timeout: 16000, maxBuffer: 32768, windowsHide: true,
      env: { SystemRoot: systemRoot, SystemDrive: win32.parse(systemRoot).root.slice(0, 2), PATH: win32.join(systemRoot, 'System32'), TEMP: process.env.TEMP, TMP: process.env.TMP,
        LOCALAPPDATA: process.env.LOCALAPPDATA, DOTNET_EnableDiagnostics: '0' } }, (error, stdout) => {
      if (error) { reject(new WindowsUpdateRefusal('PLATFORM_HELPER', 'WINDOWS_UPDATES_UNAVAILABLE')); return; }
      try { accept(windowsUpdateResult(stdout)); } catch (refusal) { reject(refusal); }
    });
    // No retry: a missing reply may follow durable native acceptance.
    child.stdin?.on('error', () => {});
    child.stdin?.end(JSON.stringify(input));
  });
  return { status: () => call({ kind: 'status' }), submit: (command: NativeUpdateCommand) => call({ kind: 'submit', command }) };
}

const submitted = z.object({ state: z.enum(['queued','pending','target_changed']) }).strict();
export async function submitWindowsUpdate(transport: WindowsUpdateTransport, command: NativeUpdateCommand) {
  // A lost reply may follow durable acceptance. Do not retry it here.
  return submitted.parse(await transport.submit(command)).state;
}
