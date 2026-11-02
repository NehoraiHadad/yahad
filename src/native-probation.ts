export type NativeProbationBinding = Readonly<{ nonce: string; activationReceiptDigest: string; operationId: string }>;
const id = /^[a-f0-9]{32}$/;
export function nativeProbationBinding(env: NodeJS.ProcessEnv): NativeProbationBinding | undefined {
  const nonce = env.YAHAD_NATIVE_PROBATION_NONCE, activationReceiptDigest = env.YAHAD_NATIVE_PROBATION_ACTIVATION,
    operationId = env.YAHAD_NATIVE_PROBATION_OPERATION;
  if (!nonce && !activationReceiptDigest && !operationId) return;
  if (!nonce || !id.test(nonce) || !operationId || !id.test(operationId) || !activationReceiptDigest || !/^[a-f0-9]{64}$/.test(activationReceiptDigest) ||
      !env.YAHAD_NATIVE_PIPE || !env.YAHAD_NATIVE_LEASE_FILE || !env.YAHAD_NATIVE_LEASE_NONCE) throw new Error('NATIVE_PROBATION_REFUSED');
  return Object.freeze({ nonce, activationReceiptDigest, operationId });
}

/** A verified application cooperation barrier. Migration's OS network isolation
 * is separate; this barrier prevents normal provider boot and HTTP mutations. */
export class NativeEffectsGate {
  private phase: 'holding' | 'releasing' | 'released' | 'failed' = 'holding';
  private readonly cancellation = new AbortController();
  constructor(readonly binding: NativeProbationBinding) {}
  get released() { return this.phase === 'released'; }
  ready() { return { type: 'probation-ready', ...this.binding }; }
  abort() { this.cancellation.abort(); this.phase = 'failed'; }
  async release(frame: unknown, start: (signal: AbortSignal) => Promise<void>) {
    const f = frame as Record<string, unknown>;
    if (!f || typeof f !== 'object' || Object.keys(f).length !== 5 || f.type !== 'release-effects' || typeof f.id !== 'string' || !id.test(f.id) ||
        f.nonce !== this.binding.nonce || f.operationId !== this.binding.operationId || f.activationReceiptDigest !== this.binding.activationReceiptDigest ||
        this.phase !== 'holding') throw new Error('NATIVE_PROBATION_RELEASE_REFUSED');
    this.phase = 'releasing';
    try {
      this.cancellation.signal.throwIfAborted(); await start(this.cancellation.signal); this.cancellation.signal.throwIfAborted();
      this.phase = 'released'; return { type: 'effects-released', id: f.id, ...this.binding };
    } catch { this.phase = 'failed'; throw new Error('NATIVE_PROBATION_EFFECTS_UNKNOWN'); }
  }
}
