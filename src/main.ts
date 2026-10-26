import { silencePayloadConsoles } from './logging.js';
import { loadEnvFile } from 'node:process';
import { loadConfig } from './config.js';
import { createApp } from './server.js';
import { acquireDataLock } from './lock.js';
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
  const { app } = await createApp(config);
  await app.listen({ host: config.host, port: config.port });
  process.stdout.write(`Yachad listening (${config.connector}) on port ${config.port}\n`);
  for (const signal of ['SIGTERM', 'SIGINT'] as const)
    process.once(signal, () => {
      void app.close().then(async () => {
        await release();
        process.exit(0);
      });
    });
} catch {
  process.stderr.write(
    'Startup failed. Check local configuration, file permissions, and port availability. No secrets logged.\n',
  );
  process.exit(1);
}
