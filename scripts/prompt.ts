import { createInterface } from 'node:readline';
import { Writable, type Readable } from 'node:stream';
import { stdin, stdout } from 'node:process';
// Readline owns terminal editing/restoration, including Unicode and pasted input.
// Keep editing output muted even at non-secret prompts: a multiline terminal
// paste may contain later passwords before the next ask() has resumed.
export function createPrompter(input: Readable, display: Writable, terminal: boolean) {
  const output = new Writable({ write(_chunk, _encoding, done) { done(); } });
  const reader = createInterface({ input, output, terminal });
  const lines = reader[Symbol.asyncIterator]();
  reader.on('SIGINT', () => reader.close());
  return {
    async ask(label: string, _secret = false) {
      display.write(label);
      const result = await lines.next();
      if (terminal) display.write('\n');
      if (result.done) throw new Error('Input closed');
      return result.value;
    },
    close() { reader.close(); },
  };
}
let prompt: ReturnType<typeof createPrompter> | undefined;
export function ask(label: string, secret = false) {
  return (prompt ||= createPrompter(stdin, stdout, !!stdin.isTTY)).ask(label, secret);
}
export function closePrompt() { prompt?.close(); }
