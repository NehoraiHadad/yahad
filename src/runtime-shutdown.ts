/** One close sequence for signals and the private parent IPC channel.
 * IPC is inherited at fork; this does not open a public command endpoint.
 * The .NET service host still needs its reviewed IPC adapter before SCM use.
 */
export function installRuntimeShutdown(close: () => Promise<unknown>, release: () => Promise<void>) {
  let stopping: Promise<void> | undefined;
  const stop = () => stopping ??= (async () => {
    const deadline = setTimeout(() => { process.stderr.write('Graceful shutdown timed out. Recovery review required.\n'); process.exit(1); }, 10000);
    deadline.unref();
    try { await close(); await release(); clearTimeout(deadline); process.exit(0); }
    catch { process.stderr.write('Graceful shutdown failed. Recovery review required.\n'); process.exit(1); }
  })();
  const signal = () => { void stop(); };
  const message = (value: unknown) => {
    if (value && typeof value === 'object' && (value as { type?: unknown }).type === 'yahad.shutdown') void stop();
  };
  process.once('SIGTERM', signal); process.once('SIGINT', signal);
  if (process.connected) { process.on('message', message); process.once('disconnect', signal); }
  return stop;
}
