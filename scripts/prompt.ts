import { createInterface } from 'node:readline/promises';
import { stdin, stdout } from 'node:process';
/** Secrets are read from a TTY with echo disabled, or supplied locally by a file descriptor. */
export async function ask(label: string, secret = false) {
  if (secret && stdin.isTTY) {
    stdout.write(label);
    stdin.setRawMode(true);
    stdin.resume();
    let value = '';
    return await new Promise<string>((resolve, reject) => {
      const handler = (b: Buffer) => {
        for (const c of b.toString()) {
          if (c === '\u0003') {
            cleanup();
            reject(new Error('Cancelled'));
            return;
          }
          if (c === '\r' || c === '\n') {
            cleanup();
            stdout.write('\n');
            resolve(value);
            return;
          }
          if (c === '\u007f') value = value.slice(0, -1);
          else value += c;
        }
      };
      const cleanup = () => {
        stdin.off('data', handler);
        stdin.setRawMode(false);
        stdin.pause();
      };
      stdin.on('data', handler);
    });
  }
  const rl = createInterface({ input: stdin, output: stdout });
  try {
    return await rl.question(label);
  } finally {
    rl.close();
  }
}
