import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { resolve } from 'node:path';
const helper = spawn(resolve('native/Yahad.Platform.exe'), ['lock-data', process.argv[2], String(process.pid)], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
helper.stderr.resume(); helper.stdin.on('error', () => {});
let output = '';
helper.stdout.on('data', chunk => {
  output += chunk.toString();
  if (output.includes('\n')) {
    const reply = JSON.parse(output.trim());
    if (reply.locked !== true) process.exit(1);
    process.send?.({ ready: true });
  }
});
let requested = false;
process.on('message', async () => {
  if (requested) return; requested = true;
  const exited = once(helper, 'exit'); helper.kill(); await exited;
  process.send?.({ helperExited: true });
  // Stay alive so the parent test can prove the duplicate, rather than the dead
  // helper, continues to hold the exclusive kernel file object.
});
