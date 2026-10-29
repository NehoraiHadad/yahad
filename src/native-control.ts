import { connect } from 'node:net';

/** Direct app-root control. The host authenticates the connecting root PID. */
export function installNativeControl(stop: () => Promise<void>) {
  const pipe = process.env.YAHAD_NATIVE_PIPE;
  if (!pipe) return;
  if (process.platform !== 'win32' || !/^\\\\\.\\pipe\\Yahad\.[a-f0-9]{32}$/.test(pipe) ||
      !process.env.YAHAD_NATIVE_LEASE_FILE || !process.env.YAHAD_NATIVE_LEASE_NONCE) throw new Error('Native control refused');
  const channel = connect(pipe); let pending = '', stopping = false;
  const close = () => { if (!stopping) { stopping = true; void stop(); } };
  channel.setEncoding('utf8');
  channel.once('connect', () => channel.write('{"type":"ready"}\n'));
  channel.on('data', data => {
    pending += data;
    if (pending.length > 1024) return close();
    const end = pending.indexOf('\n'); if (end < 0) return;
    try { if (end !== pending.length - 1 || JSON.parse(pending.slice(0, end))?.type !== 'stop') throw new Error(); close(); }
    catch { close(); }
  });
  channel.on('error', close); channel.on('close', close);
}
