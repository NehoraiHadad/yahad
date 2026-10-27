import { fileTypeFromBuffer } from 'file-type';

export interface GroupPhoto { bytes: Buffer; mime: string }
export const MAX_GROUP_PHOTO_BYTES = 256 * 1024;
const MIME = new Set(['image/jpeg', 'image/png', 'image/webp']);

/** Only provider-returned WhatsApp CDN URLs are accepted; never client URLs. */
export function photoUrl(value: string): URL | undefined {
  try {
    const url = new URL(value);
    if (url.protocol !== 'https:' || url.username || url.password || url.port || url.hash ||
      !/^(?:[a-z0-9-]+\.)*(?:whatsapp\.net|whatsapp\.com)$/.test(url.hostname)) return;
    // Restrict to the CDN hosts, not arbitrary services under the WhatsApp domain.
    if (url.hostname !== 'pps.whatsapp.net' && !/^mmg(?:-[a-z0-9-]+)?\.whatsapp\.net$/.test(url.hostname) &&
      !/^pps(?:-[a-z0-9-]+)?\.whatsapp\.net$/.test(url.hostname)) return;
    return url;
  } catch { return; }
}

export async function fetchGroupPhoto(value: string, fetcher: typeof fetch = fetch): Promise<GroupPhoto | undefined> {
  const url = photoUrl(value);
  if (!url) return;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);
  let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  try {
    const response = await fetcher(url, { redirect: 'error', signal: controller.signal, credentials: 'omit' });
    if (!response.ok || !response.body) return;
    const length = Number(response.headers.get('content-length'));
    if (length > MAX_GROUP_PHOTO_BYTES) { await response.body.cancel(); return; }
    reader = response.body.getReader();
    const chunks: Buffer[] = []; let size = 0;
    while (true) {
      const part = await reader.read();
      if (part.done) break;
      size += part.value.length;
      if (size > MAX_GROUP_PHOTO_BYTES) return;
      chunks.push(Buffer.from(part.value));
    }
    const bytes = Buffer.concat(chunks);
    const type = await fileTypeFromBuffer(bytes);
    if (!type || !MIME.has(type.mime)) return;
    return { bytes, mime: type.mime };
  } catch { return; } finally {
    clearTimeout(timer);
    await reader?.cancel().catch(() => {});
    controller.abort();
  }
}

type Entry = { owner: object; expires: number; photo?: GroupPhoto; pending?: Promise<GroupPhoto | undefined> };
/** Ephemeral, bounded cache: no URLs, credentials, database rows or backup changes. */
export class GroupPhotoCache {
  private entries = new Map<string, Entry>();
  private active = 0;
  private waiting: (() => void)[] = [];
  constructor(private now: () => number = Date.now) {}
  clear(owner: object) {
    for (const [key, entry] of this.entries) if (entry.owner === owner) this.entries.delete(key);
  }
  async get(owner: object, key: string, load: () => Promise<GroupPhoto | undefined>) {
    const old = this.entries.get(key);
    if (old?.owner === owner && (old.pending || old.expires > this.now())) return old.pending || old.photo;
    if (this.waiting.length >= 64) return;
    // At most 128 × 256 KiB retained; bounded pending work and four downloads.
    if (this.entries.size >= 128) {
      const victim = [...this.entries].find(([, entry]) => !entry.pending);
      if (victim) this.entries.delete(victim[0]); else return;
    }
    const entry: Entry = { owner, expires: 0 };
    this.entries.set(key, entry);
    entry.pending = (async () => {
      if (this.active >= 4) await new Promise<void>(resolve => this.waiting.push(resolve));
      else this.active++;
      try {
        if (this.entries.get(key) !== entry) return;
        const photo = await load().catch(() => undefined);
        if (this.entries.get(key) !== entry) return;
        entry.photo = photo && photo.bytes.length <= MAX_GROUP_PHOTO_BYTES && MIME.has(photo.mime) ? photo : undefined;
        entry.expires = this.now() + (entry.photo ? 15 * 60_000 : 60_000);
        return entry.photo;
      } finally {
        entry.pending = undefined;
        const next = this.waiting.shift();
        if (next) next(); else this.active--;
      }
    })();
    return entry.pending;
  }
}
