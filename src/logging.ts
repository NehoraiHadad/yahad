/** Some transitive Signal code prints entire private session objects via console.warn.
 * This dedicated server logs only explicit operational strings written to stdio by main.ts.
 * Do not add HTTP bodies, provider errors, QR codes, cookies, or protocol frames to logs.
 */
export function silencePayloadConsoles() {
  for (const method of ['log', 'warn', 'error', 'debug', 'info', 'trace', 'dir', 'table'] as const)
    console[method] = () => {};
}
