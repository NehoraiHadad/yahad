import { silencePayloadConsoles } from './logging.js';
import { loadEnvFile } from 'node:process';
import { loadConfig } from './config.js';
import { createApp } from './server.js';
import { acquireDataLock } from './lock.js';
import { readBootstrap, readFirstOwnerIdentity } from './bootstrap.js';
import { platformFiles } from './platform-files.js';
import { installNativeControl } from './native-control.js';
import { installRuntimeShutdown } from './runtime-shutdown.js';
import { UpdateIdleFence } from './update-idle.js';
import { createWindowsUpdateTransport } from './windows-update-transport.js';
import { NativeEffectsGate, nativeProbationBinding } from './native-probation.js';
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
try {
  const config = loadConfig();
  const release = await acquireDataLock(config.dataDir);
  const updateIdle = process.platform === 'win32' && process.env.YAHAD_NATIVE_PIPE ? new UpdateIdleFence(config.dataDir) : undefined;
  const windowsUpdates = updateIdle ? createWindowsUpdateTransport() : undefined;
  const binding = nativeProbationBinding(process.env), probation = binding ? new NativeEffectsGate(binding) : undefined;
  const { app, service } = await createApp(config, { updateIdle, windowsUpdates, ...(probation ? { boot: false, effectsReady: () => probation.released } : {}),
    firstOwner: readFirstOwnerIdentity(process.env.SETUP_CAPABILITY_FILE || platformFiles.setupCapabilityPath()),
    bootstrap: readBootstrap(process.env.SETUP_CAPABILITY_FILE || platformFiles.setupCapabilityPath()) });
  if (!probation) {
    await app.listen({ host: config.host, port: config.port });
    process.stdout.write(`Yachad listening (${config.connector}) on port ${config.port}\n`);
  } else {
    await app.ready(); const health = await app.inject({ url: '/health/ready' });
    if (health.statusCode !== 200) throw new Error('NATIVE_PROBATION_HEALTH_REFUSED');
  }
  const stop = installRuntimeShutdown(() => app.close(), release);
  installNativeControl(stop, updateIdle, probation ? { gate: probation, start: async signal => {
    signal.throwIfAborted(); await app.listen({ host: config.host, port: config.port }); signal.throwIfAborted();
    // Enabling effects is distinct from waiting for a remote provider to be
    // online. The local app remains usable while normal reconnect logic retries.
    void service.boot().catch(() => process.stderr.write('Provider startup deferred. Local application remains available.\n'));
    signal.throwIfAborted();
  } } : undefined);
  if (!probation && process.connected && process.send) process.send({ type: 'yahad.ready' }, undefined, undefined, () => {});
} catch {
  process.stderr.write(
    'Startup failed. Check local configuration, file permissions, and port availability. No secrets logged.\n',
  );
  process.exit(1);
}
