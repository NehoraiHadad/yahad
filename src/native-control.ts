import { connect } from 'node:net';

/** Direct app-root control. The host authenticates the connecting root PID.
 * Node sends one `{"type":"ready"}` frame once it is listening; the host's only
 * command is `{"type":"stop"}`; any other frame, or a lost channel, also stops. */
export function installNativeControl(stop: () => Promise<void>) {
  const pipe = process.env.YAHAD_NATIVE_PIPE;
  if (!pipe) return;
  if (process.platform !== 'win32' || !/^\\\\\.\\pipe\\Yahad\.[a-f0-9]{32}$/.test(pipe) ||
      !process.env.YAHAD_NATIVE_LEASE_FILE || !process.env.YAHAD_NATIVE_LEASE_NONCE) throw new Error('Native control refused');
  const channel = connect(pipe); let pending = '', stopping = false;
  const close = () => { if (!stopping) { stopping = true; void stop(); } };
  channel.setEncoding('utf8');
  channel.once('connect', () => channel.write(JSON.stringify({ type: 'ready' }) + '\n'));
  channel.on('data', data => {
    // `stop` is the only command, and anything else is a protocol failure that
    // also stops: so the first complete (or oversized) frame always stops.
    pending += data;
    if (pending.length > 1024 || pending.includes('\n')) close();
  });
  channel.on('error', close); channel.on('close', close);
}
