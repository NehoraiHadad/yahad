/** Some transitive Signal code prints entire private session objects via console.warn.
 * This dedicated server logs only explicit operational strings written to stdio by main.ts.
 * Do not add HTTP bodies, provider errors, QR codes, cookies, or protocol frames to logs.
 */
export function silencePayloadConsoles() {
  for (const method of ['log', 'warn', 'error', 'debug', 'info', 'trace', 'dir', 'table'] as const)
    console[method] = () => {};
}
/** One stderr line for a request the server failed to handle (HTTP 5xx). Each field is
 * a closed token (code, route pattern, reference, exception class, storage kind), so the
 * line cannot carry a message body, chat content, a token or a name. */
export function logServerFailure(fields: Record<string, string | number | null | undefined>,
  write: (line: string) => void = line => { process.stderr.write(line); }) {
  const parts = Object.entries(fields).map(([key, value]) =>
    `${key}=${value === null || value === undefined ? '-' : String(value).replace(/[^A-Za-z0-9_:/.*-]/g, '?').slice(0, 120)}`);
  write(`Request failed ${parts.join(' ')}\n`);
}
