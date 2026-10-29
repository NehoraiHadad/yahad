/** Provider edit time is separate from the original timestamp used by history.
 * Only timestamp metadata is corrected; no earlier message content is restored. */
export function rawAtOriginalTime(raw: string | undefined,time: number,origin?: string): string | undefined {
  if(!raw)return;
  try {
    const original=JSON.parse(raw);
    if(!original || typeof original!=='object')return;
    if(origin){const metadata=JSON.parse(origin);if(metadata?.key)original.key=metadata.key;}
    original.messageTimestamp=Math.floor(time/1000);
    return JSON.stringify(original);
  } catch { return; }
}
export function boundedEditTime(value: number | undefined) {
  return Number.isFinite(value) ? Math.max(0,Math.min(value!,Date.now()+300000)) : Date.now();
}
