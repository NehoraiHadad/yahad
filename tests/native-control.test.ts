import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { createServer, type Socket } from 'node:net';
import { once } from 'node:events';
import { installNativeControl } from '../src/native-control.js';

// The ServiceHost's child-control channel (installer/Yahad.ServiceHost/ServiceProcess.cs):
// node writes `{"type":"ready"}\n` once listening; the host's only command is `{"type":"stop"}\n`.
const keys = ['YAHAD_NATIVE_PIPE', 'YAHAD_NATIVE_LEASE_FILE', 'YAHAD_NATIVE_LEASE_NONCE'] as const;
async function withHost(run: (host: { peer: Promise<Socket>; stops: () => number }) => Promise<void>) {
  const saved = keys.map(key => process.env[key]);
  const pipe = '\\\\.\\pipe\\Yahad.' + randomBytes(16).toString('hex');
  const server = createServer(); server.listen(pipe); await once(server, 'listening');
  const peer = once(server, 'connection').then(([socket]) => socket as Socket);
  Object.assign(process.env, { YAHAD_NATIVE_PIPE: pipe, YAHAD_NATIVE_LEASE_FILE: 'synthetic-lease', YAHAD_NATIVE_LEASE_NONCE: 'synthetic-nonce' });
  let stopped = 0;
  try {
    installNativeControl(async () => { stopped++; });
    await run({ peer, stops: () => stopped });
  } finally {
    keys.forEach((key, i) => { if (saved[i] === undefined) delete process.env[key]; else process.env[key] = saved[i]; });
    (await peer).destroy(); server.close();
  }
}
const firstLine = async (socket: Socket) => { socket.setEncoding('utf8'); let text = '';
  while (!text.includes('\n')) text += (await once(socket, 'data'))[0]; return text; };
const settle = () => new Promise(resolve => setTimeout(resolve, 50));
// The host pipe exists only on Windows; elsewhere a well-formed pipe is refused.
function refusedOffWindows() {
  const saved = keys.map(key => process.env[key]);
  try {
    Object.assign(process.env, { YAHAD_NATIVE_PIPE: '\\\\.\\pipe\\Yahad.' + '0'.repeat(32), YAHAD_NATIVE_LEASE_FILE: 'synthetic-lease', YAHAD_NATIVE_LEASE_NONCE: 'synthetic-nonce' });
    assert.throws(() => installNativeControl(async () => {}), /Native control refused/);
  } finally { keys.forEach((key, i) => { if (saved[i] === undefined) delete process.env[key]; else process.env[key] = saved[i]; }); }
}

test('without the host pipe there is no control channel; a malformed pipe or missing lease is refused', () => {
  const saved = keys.map(key => process.env[key]);
  try {
    for (const key of keys) delete process.env[key];
    assert.equal(installNativeControl(async () => {}), undefined);
    process.env.YAHAD_NATIVE_PIPE = '\\\\.\\pipe\\Other.' + '0'.repeat(32);
    process.env.YAHAD_NATIVE_LEASE_FILE = 'synthetic-lease'; process.env.YAHAD_NATIVE_LEASE_NONCE = 'synthetic-nonce';
    assert.throws(() => installNativeControl(async () => {}), /Native control refused/);
    process.env.YAHAD_NATIVE_PIPE = '\\\\.\\pipe\\Yahad.' + '0'.repeat(32); delete process.env.YAHAD_NATIVE_LEASE_NONCE;
    assert.throws(() => installNativeControl(async () => {}), /Native control refused/);
  } finally { keys.forEach((key, i) => { if (saved[i] === undefined) delete process.env[key]; else process.env[key] = saved[i]; }); }
});

test('on the Windows host pipe node reports ready, then stops exactly once on stop', async () => {
  if (process.platform !== 'win32') return refusedOffWindows();
  await withHost(async ({ peer, stops }) => {
    const socket = await peer;
    assert.equal(await firstLine(socket), '{"type":"ready"}\n');
    await settle(); assert.equal(stops(), 0);
    socket.write('{"type":"stop"}\n'); await settle(); assert.equal(stops(), 1);
    socket.end(); await settle(); assert.equal(stops(), 1, 'the channel closing after stop does not stop again');
  });
});

test('any other host frame, an oversized frame or a lost channel also stops the runtime', async () => {
  if (process.platform !== 'win32') return refusedOffWindows();
  for (const frame of ['{"type":"prepare-update","id":"' + '1'.repeat(32) + '"}\n', '{"type":"stop","extra":1}\n', 'x'.repeat(1025), null]) {
    await withHost(async ({ peer, stops }) => {
      const socket = await peer; await firstLine(socket);
      if (frame === null) socket.destroy(); else socket.write(frame);
      await settle(); assert.equal(stops(), 1);
    });
  }
});
