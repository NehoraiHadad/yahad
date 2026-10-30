import { platformFiles } from './platform-files.js';
export async function acquireDataLock(dir: string) {
  return platformFiles.acquireDataLock(dir);
}
