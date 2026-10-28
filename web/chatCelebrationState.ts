/** Small, local-only decoration: never reads or changes message content/status. */
export const CELEBRATION_DURATION_MS = 950;
export const CELEBRATION_COOLDOWN_MS = 2400;
export const CELEBRATION_PARTICLES = 6;
export type CelebrationFlair = 'flower' | 'heart' | 'sun';
export type CelebrationBurst = { id: number; flair: CelebrationFlair };

export function celebrationFlair(value: string): CelebrationFlair {
  return value === 'heart' || value === 'sun' ? value : 'flower';
}

/** Injectable clock/timer keeps bounds and teardown verifiable without a DOM. */
export function createCelebrationController(options: {
  allowed: () => boolean;
  flair: CelebrationFlair;
  onChange: (burst: CelebrationBurst | null) => void;
  now?: () => number;
  schedule?: (callback: () => void, delay: number) => ReturnType<typeof setTimeout>;
  cancel?: (timer: ReturnType<typeof setTimeout>) => void;
}) {
  const now = options.now ?? (() => performance.now());
  const schedule = options.schedule ?? setTimeout;
  const cancel = options.cancel ?? clearTimeout;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let lastStarted = -Infinity;
  let sequence = 0;
  let disposed = false;
  function clear() {
    if (timer !== undefined) cancel(timer);
    timer = undefined;
    if (!disposed) options.onChange(null);
  }
  return {
    celebrate() {
      const timestamp = now();
      if (disposed || !options.allowed() || timestamp - lastStarted < CELEBRATION_COOLDOWN_MS) return;
      clear();
      lastStarted = timestamp;
      options.onChange({ id: ++sequence, flair: options.flair });
      timer = schedule(() => {
        timer = undefined;
        if (!disposed) options.onChange(null);
      }, CELEBRATION_DURATION_MS);
    },
    clear,
    dispose() {
      disposed = true;
      if (timer !== undefined) cancel(timer);
      timer = undefined;
    },
  };
}
