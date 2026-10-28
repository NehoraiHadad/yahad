type Stream = Pick<EventSource, 'readyState' | 'close'> & Pick<EventTarget, 'addEventListener'> & { onerror: ((event: Event) => void) | null };
/** Native EventSource retries CONNECTING streams, but CLOSED streams need replacement.
 * Only the current stream can deliver events; dispose/expiry cancel all recovery.
 */
export function resilientEventStream(create: () => Stream, retryMs = 2000) {
  let source: Stream, stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
  const listeners = new Map<string, EventListener[]>();
  const result = {
    onerror: null as ((event: Event) => void) | null,
    addEventListener(type: string, callback: EventListener) {
      const callbacks = listeners.get(type) || [];
      callbacks.push(callback); listeners.set(type, callbacks);
      bind(source, type, callback);
    },
    close() { stopped = true; clearTimeout(timer); source.close(); },
  };
  function bind(stream: Stream, type: string, callback: EventListener) {
    stream.addEventListener(type, event => {
      if (stopped || stream !== source) return;
      // Expiry is terminal even if the server closes before React unmounts.
      if (type === 'expired') result.close();
      callback(event);
    });
  }
  function connect() {
    if (stopped) return;
    const stream = source = create();
    for (const [type, callbacks] of listeners) for (const callback of callbacks) bind(stream, type, callback);
    stream.onerror = event => {
      if (stopped || stream !== source) return;
      result.onerror?.(event);
      if (stream.readyState === 2 && timer === undefined) {
        stream.close();
        timer = setTimeout(() => { timer = undefined; connect(); }, retryMs);
      }
    };
  }
  connect();
  return result;
}
