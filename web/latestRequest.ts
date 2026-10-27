/** A response is usable only while its request is newest and has not been invalidated.
 * Invalidating on event-stream loss also rejects reads that finish after disconnect. */
export class LatestRequest {
  private generation = 0;
  begin(): number { return ++this.generation; }
  invalidate(): void { this.generation++; }
  isCurrent(request: number): boolean { return request === this.generation; }
}

/** Server refresh events with a group are message/history updates only.
 * Permission, session, reset and connection invalidations carry no group. */
export function isGroupDataRefresh(data: string): boolean {
  try {
    const value: unknown = JSON.parse(data);
    return !!value && typeof value === 'object' && 'group' in value &&
      typeof value.group === 'string' && value.group.length > 0;
  } catch { return false; }
}
