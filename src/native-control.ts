import { connect } from 'node:net';
import type { UpdateIdleFence } from './update-idle.js';
import type { NativeEffectsGate } from './native-probation.js';

/** Direct app-root control. The host authenticates the connecting root PID. */
export function installNativeControl(stop: () => Promise<void>, updateIdle?: UpdateIdleFence,
  probation?: { gate: NativeEffectsGate; start: (signal: AbortSignal) => Promise<void> }) {
  const pipe = process.env.YAHAD_NATIVE_PIPE;
  if (!pipe) return;
  if (process.platform !== 'win32' || !/^\\\\\.\\pipe\\Yahad\.[a-f0-9]{32}$/.test(pipe) ||
      !process.env.YAHAD_NATIVE_LEASE_FILE || !process.env.YAHAD_NATIVE_LEASE_NONCE) throw new Error('Native control refused');
  const channel = connect(pipe); let pending = '', stopping = false, queued = 0;
  let handling = Promise.resolve();
  const close = () => { if (!stopping) { stopping = true; probation?.gate.abort(); void stop(); } };
  channel.setEncoding('utf8');
  channel.once('connect', () => channel.write(JSON.stringify(probation?.gate.ready() ?? { type: 'ready' }) + '\n'));
  channel.on('data', data => {
    pending += data;
    if (pending.length > 1024) return close();
    let end: number;
    try {
      while ((end = pending.indexOf('\n')) >= 0) {
        const frame = JSON.parse(pending.slice(0, end)); pending = pending.slice(end + 1);
        if (frame.type === 'stop' && Object.keys(frame).length === 1) { close(); return; }
        if (++queued > 4) throw new Error();
        handling = handling.then(async () => {
          try {
            if (stopping) return;
            if (frame.type === 'release-effects' && probation) {
              const reply = await probation.gate.release(frame, probation.start);
              if (!stopping) channel.write(JSON.stringify(reply) + '\n');
              return;
            }
            if (!/^[a-f0-9]{32}$/.test(frame.id) || Object.keys(frame).length !== 2) throw new Error();
            if (frame.type === 'prepare-update') {
              const idle = (!probation || probation.gate.released) && updateIdle?.prepare(frame.id) === true;
              channel.write(JSON.stringify({ type: idle ? 'update-idle' : 'update-busy', id: frame.id }) + '\n');
            } else if (frame.type === 'cancel-update') {
              channel.write(JSON.stringify({ type: updateIdle?.cancel(frame.id) === true ? 'update-cancelled' : 'update-cancel-refused', id: frame.id }) + '\n');
            } else throw new Error();
          } finally { queued--; }
        }).catch(close);
      }
    }
    catch { close(); }
  });
  channel.on('error', close); channel.on('close', close);
}
