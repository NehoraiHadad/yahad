import type { PickerSticker } from './EmojiStickerPicker';

/** Reuses only the application's authorized media route; never follows redirects or stores media URLs. */
export async function loadStickerFile(sticker: PickerSticker, maxBytes: number, signal: AbortSignal): Promise<File> {
  if (!/^\/api\/groups\/[^/?#]+\/media\/[^/?#]+$/.test(sticker.url) || !Number.isSafeInteger(maxBytes) || maxBytes < 12)
    throw new Error('כתובת המדבקה אינה תקינה.');
  const requestSignal = AbortSignal.any([signal, AbortSignal.timeout(30_000)]);
  const response = await fetch(sticker.url, { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: requestSignal });
  if (!response.ok) throw new Error('המדבקה אינה זמינה עוד או שאין הרשאה לשיחה.');
  const mime = response.headers.get('content-type')?.split(';')[0].trim().toLowerCase();
  const length = Number(response.headers.get('content-length'));
  if (mime !== 'image/webp' || length > maxBytes) {
    await response.body?.cancel();
    throw new Error(mime !== 'image/webp' ? 'אפשר לבחור רק מדבקה מסוג WebP.' : 'המדבקה גדולה מהמותר.');
  }
  const reader = response.body?.getReader();
  if (!reader) throw new Error('לא ניתן לקרוא את המדבקה.');
  const chunks: Uint8Array<ArrayBuffer>[] = [];
  let size = 0;
  try {
    for (;;) {
      requestSignal.throwIfAborted();
      const { value, done } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maxBytes) { await reader.cancel(); throw new Error('המדבקה גדולה מהמותר.'); }
      chunks.push(new Uint8Array(value));
    }
  } catch (error) {
    await reader.cancel().catch(() => {});
    throw error;
  } finally { reader.releaseLock(); }
  requestSignal.throwIfAborted();
  const blob = new Blob(chunks, { type: 'image/webp' });
  const header = new Uint8Array(await blob.slice(0, 12).arrayBuffer());
  const ascii = (from: number, to: number) => String.fromCharCode(...header.slice(from, to));
  if (header.length < 12 || ascii(0, 4) !== 'RIFF' || ascii(8, 12) !== 'WEBP') throw new Error('קובץ המדבקה אינו WebP תקין.');
  return new File([blob], 'sticker.webp', { type: 'image/webp' });
}
