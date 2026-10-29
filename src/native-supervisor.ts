import { fork } from 'node:child_process';
import { connect } from 'node:net';

// Private native-host IPC only. No HTTP control endpoint or user setup flow.
const pipe = process.env.YAHAD_NATIVE_PIPE;
if (!pipe || !/^\\\\\.\\pipe\\Yahad\.[a-f0-9]{32}$/.test(pipe)) process.exit(2);
const environment = { ...process.env };
delete environment.YAHAD_NATIVE_PIPE;
const channel = connect(pipe);
let stopping = false;
let deadline: NodeJS.Timeout | undefined;
const child = fork(new URL('./main.ts', import.meta.url), {
  env: environment,
  execPath: process.execPath,
  execArgv: process.execArgv,
  stdio: ['ignore', 'pipe', 'pipe', 'ipc'],
});
child.stdout?.resume(); child.stderr?.resume();
function stop() {
  if (stopping) return;
  stopping = true;
  if (child.connected) child.send({ type: 'yahad.shutdown' }, () => {});
  else child.kill();
  deadline = setTimeout(() => child.kill('SIGKILL'), 12000);
}
let pending = '';
channel.setEncoding('utf8');
channel.on('data', data => {
  pending += data;
  if (pending.length > 1024) return stop();
  const end = pending.indexOf('\n');
  if (end < 0) return;
  const frame = pending.slice(0, end); pending = pending.slice(end + 1);
  try { if (JSON.parse(frame)?.type !== 'stop' || pending.length) throw new Error(); stop(); }
  catch { stop(); }
});
channel.on('error', stop); channel.on('close', stop);
child.on('message', message => {
  if (stopping || !message || typeof message !== 'object' || (message as {type?:string}).type !== 'yahad.ready') return;
  const send = () => channel.write('{"type":"ready"}\n');
  if (channel.connecting) channel.once('connect', send); else send();
});
child.on('error', () => { channel.destroy(); process.exitCode = 1; });
child.on('exit', code => {
  if (deadline) clearTimeout(deadline);
  const clean = stopping && code === 0;
  stopping = true; channel.end(); process.exitCode = clean ? 0 : 1;
});
