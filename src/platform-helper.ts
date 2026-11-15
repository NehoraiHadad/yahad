import { win32 } from 'node:path';

/** The Windows Platform helper belongs to the installed infrastructure, not to
 * the app. The service host names its own image in `YAHAD_PLATFORM_HELPER`; the
 * app never derives a path of its own. Only a normalized, drive-qualified
 * `.exe` path is admitted. */
export function platformHelper(env: NodeJS.ProcessEnv = process.env): string {
  const helper = env.YAHAD_PLATFORM_HELPER;
  if (typeof helper !== 'string' || !/^[A-Za-z]:\\/.test(helper) || win32.normalize(helper) !== helper ||
    !helper.toLowerCase().endsWith('.exe')) throw new Error('YAHAD_PLATFORM_HELPER refused');
  return helper;
}
