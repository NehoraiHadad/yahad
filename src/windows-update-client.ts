import { z } from 'zod';
import type { UpdateTarget } from './updates.js';

export type NativeUpdateCommand = { id: string; action: 'check'|'install'|'set_mode'; target?: UpdateTarget; mode?: 'auto'|'notify'; familyId: string; userId: string };
/** Trusted in-process transport, never constructed from an HTTP body or environment flag.
 * The actual authenticated Windows endpoint is intentionally unconfigured in production.
 */
export interface WindowsUpdateTransport {
  status(): Promise<unknown>;
  submit(command: NativeUpdateCommand): Promise<unknown>;
}
const result = z.object({ state: z.enum(['queued','pending']) }).strict();
export async function submitWindowsUpdate(transport: WindowsUpdateTransport, command: NativeUpdateCommand) {
  // A lost reply may follow durable acceptance. Do not retry it here.
  return result.parse(await transport.submit(command)).state;
}
export class UnconfiguredWindowsUpdateTransport implements WindowsUpdateTransport {
  async status(): Promise<unknown> { throw new Error('WINDOWS_AUTHORITY_UNCONFIGURED'); }
  async submit(_command: NativeUpdateCommand): Promise<unknown> { throw new Error('WINDOWS_AUTHORITY_UNCONFIGURED'); }
}
