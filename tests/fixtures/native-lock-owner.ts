import { createPlatformFiles } from '../../src/platform-files.js';
const release = await createPlatformFiles().acquireDataLock(process.argv[2]);
process.send?.({ ready: true });
process.on('message', () => { void release().then(() => process.exit(0)); });
