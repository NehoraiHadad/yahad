import { silencePayloadConsoles } from './logging.js';
import { loadEnvFile } from 'node:process';
import { loadConfig } from './config.js';
import { createApp } from './server.js';
import { acquireDataLock } from './lock.js';
import { readBootstrap, readFirstOwnerIdentity } from './bootstrap.js';
import { platformFiles } from './platform-files.js';
import { installNativeControl } from './native-control.js';
import { installRuntimeShutdown } from './runtime-shutdown.js';
import { createWindowsUpdateTransport } from './windows-update-transport.js';
try {
  loadEnvFile('.env');
} catch {}
// Third-party Signal code can console.warn private session objects. Only whitelisted operational strings may reach stdio.
silencePayloadConsoles();
process.on('uncaughtException', () => {
  process.stderr.write('Unexpected runtime failure. Restarting without logging payload.\n');
  process.exit(1);
});
process.on('unhandledRejection', () => {
  process.stderr.write('Unhandled operation failed. Restarting without logging payload.\n');
  process.exit(1);
});
// The native host keeps only the exit code, never output, so a refused startup
// exits with the fixed code of the step that refused it.
const startupExit = { config: 10, dataLock: 11, app: 12, listen: 13, control: 14 } as const;
let stage: keyof typeof startupExit = 'config';
try {
  const config = loadConfig();
  stage = 'dataLock';
  const release = await acquireDataLock(config.dataDir);
  stage = 'app';
  const windowsUpdates = process.platform === 'win32' && process.env.YAHAD_NATIVE_PIPE ? createWindowsUpdateTransport() : undefined;
  const { app } = await createApp(config, { windowsUpdates,
    firstOwner: readFirstOwnerIdentity(process.env.SETUP_CAPABILITY_FILE || platformFiles.setupCapabilityPath()),
    bootstrap: readBootstrap(process.env.SETUP_CAPABILITY_FILE || platformFiles.setupCapabilityPath()) });
  stage = 'listen';
  await app.listen({ host: config.host, port: config.port });
  process.stdout.write(`Yachad listening (${config.connector}) on port ${config.port}\n`);
  stage = 'control';
  const stop = installRuntimeShutdown(() => app.close(), release);
  installNativeControl(stop);
} catch {
  process.stderr.write(
    'Startup failed. Check local configuration, file permissions, and port availability. No secrets logged.\n',
  );
  process.exit(startupExit[stage]);
}
