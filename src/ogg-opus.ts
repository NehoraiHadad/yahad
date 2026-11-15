// Ogg Opus (RFC 3533 framing, RFC 7845 mapping): the one voice format the app sends.
// The browser writes it from its recorder's own Opus packets; the server only
// validates it. Neither side decodes or re-encodes audio.
import { OPUS_RATE, VOICE_MAX_BYTES, VOICE_MAX_SAMPLES, isOpusTags, opusPacketInfo, parseOpusHead } from './opus.js';

const CRC = (() => {
  const table = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let r = i << 24;
    for (let k = 0; k < 8; k++) r = r & 0x80000000 ? (r << 1) ^ 0x04c11db7 : r << 1;
    table[i] = r >>> 0;
  }
  return table;
})();
/** The Ogg page checksum: polynomial 0x04c11db7, zero initial value, no reflection. */
function oggCrc(bytes: Uint8Array) {
  let crc = 0;
  for (const b of bytes) crc = ((crc << 8) ^ CRC[((crc >>> 24) ^ b) & 0xff]) >>> 0;
  return crc;
}

const FLAG_CONTINUED = 1, FLAG_BOS = 2, FLAG_EOS = 4, NO_GRANULE = -1n;
// Audio pages stay near this size; packets are never split across pages.
const PAGE_TARGET = 4096;

function page(flags: number, granule: bigint, serial: number, sequence: number, packets: Uint8Array[]) {
  const lacing: number[] = [];
  for (const p of packets) {
    for (let left = p.length; ; left -= 255) {
      lacing.push(Math.min(left, 255));
      if (left < 255) break;
    }
  }
  if (lacing.length > 255) throw new Error('Ogg page overflow');
  const body = packets.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(27 + lacing.length + body), view = new DataView(out.buffer);
  out.set([0x4f, 0x67, 0x67, 0x53, 0, flags]);
  view.setBigInt64(6, granule, true);
  view.setUint32(14, serial, true);
  view.setUint32(18, sequence, true);
  out[26] = lacing.length;
  out.set(lacing, 27);
  let at = 27 + lacing.length;
  for (const p of packets) { out.set(p, at); at += p.length; }
  view.setUint32(22, oggCrc(out), true);
  return out;
}

const segments = (packet: Uint8Array) => Math.floor(packet.length / 255) + 1;

/** OpusTags with this app as vendor and no user comments. */
export function opusTags() {
  const vendor = new TextEncoder().encode('yahad');
  const out = new Uint8Array(8 + 4 + vendor.length + 4), view = new DataView(out.buffer);
  out.set(new TextEncoder().encode('OpusTags'));
  view.setUint32(8, vendor.length, true);
  out.set(vendor, 12);
  return out;
}

/** Write one Ogg Opus logical stream from an OpusHead and its audio packets. */
export function writeOggOpus(head: Uint8Array, packets: Uint8Array[], serial: number): Uint8Array {
  if (!parseOpusHead(head)) throw new Error('Not an OpusHead');
  if (!packets.length) throw new Error('No audio');
  const pages = [page(FLAG_BOS, 0n, serial, 0, [head]), page(0, 0n, serial, 1, [opusTags()])];
  let granule = 0n, batch: Uint8Array[] = [], lacing = 0, bytes = 0;
  const flush = (last: boolean) => {
    pages.push(page(last ? FLAG_EOS : 0, granule, serial, pages.length, batch));
    batch = []; lacing = 0; bytes = 0;
  };
  for (const packet of packets) {
    const info = opusPacketInfo(packet);
    if (!info) throw new Error('Malformed Opus packet');
    if (batch.length && (lacing + segments(packet) > 255 || bytes + packet.length > PAGE_TARGET)) flush(false);
    batch.push(packet); lacing += segments(packet); bytes += packet.length;
    granule += BigInt(info.samples);
  }
  flush(true);
  const out = new Uint8Array(pages.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of pages) { out.set(p, at); at += p.length; }
  return out;
}

export type VoiceRejectionCode =
  | 'AUDIO_TOO_LARGE'
  | 'AUDIO_NOT_OGG'
  | 'AUDIO_NOT_OPUS'
  | 'AUDIO_NOT_MONO'
  | 'AUDIO_TOO_LONG'
  | 'AUDIO_EMPTY';
export class VoiceRejection extends Error {
  constructor(readonly code: VoiceRejectionCode) { super(code); }
}

interface Page { flags: number; granule: bigint; completed: number }

/**
 * Accept only a single, complete Ogg Opus stream that is mono, mapping family 0,
 * at most VOICE_MAX_SECONDS long and VOICE_MAX_BYTES big. Returns its playable length.
 */
export function validateVoiceOgg(bytes: Uint8Array): { seconds: number } {
  const reject = (code: VoiceRejectionCode): never => { throw new VoiceRejection(code); };
  if (bytes.length > VOICE_MAX_BYTES) reject('AUDIO_TOO_LARGE');
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const pages: Page[] = [], packets: Uint8Array[] = [];
  let at = 0, serial = 0, partial: Uint8Array[] = [];
  while (at < bytes.length) {
    if (bytes.length - at < 27 || view.getUint32(at) !== 0x4f676753 || bytes[at + 4] !== 0) reject('AUDIO_NOT_OGG');
    const flags = bytes[at + 5], count = bytes[at + 26], tableEnd = at + 27 + count;
    if (flags & ~7 || tableEnd > bytes.length) reject('AUDIO_NOT_OGG');
    const lacing = bytes.subarray(at + 27, tableEnd), end = tableEnd + lacing.reduce((n, v) => n + v, 0);
    if (end > bytes.length) reject('AUDIO_NOT_OGG');
    const pageBytes = new Uint8Array(bytes.subarray(at, end)); // a copy: the checksum field is zeroed
    pageBytes.fill(0, 22, 26);
    if (oggCrc(pageBytes) !== view.getUint32(at + 22, true)) reject('AUDIO_NOT_OGG');
    if (!pages.length) serial = view.getUint32(at + 14, true);
    else if (view.getUint32(at + 14, true) !== serial) reject('AUDIO_NOT_OGG');
    if (view.getUint32(at + 18, true) !== pages.length) reject('AUDIO_NOT_OGG');
    if (!!(flags & FLAG_CONTINUED) !== partial.length > 0) reject('AUDIO_NOT_OGG');
    if (!!(flags & FLAG_BOS) !== (pages.length === 0)) reject('AUDIO_NOT_OGG');
    if (pages.length && pages[pages.length - 1].flags & FLAG_EOS) reject('AUDIO_NOT_OGG');
    let body = tableEnd, completed = 0;
    for (const size of lacing) {
      partial.push(bytes.subarray(body, body + size));
      body += size;
      if (size < 255) {
        const length = partial.reduce((n, p) => n + p.length, 0), packet = new Uint8Array(length);
        let o = 0;
        for (const p of partial) { packet.set(p, o); o += p.length; }
        packets.push(packet); partial = []; completed++;
      }
    }
    pages.push({ flags, granule: view.getBigInt64(at + 6, true), completed });
    at = end;
  }
  const last = pages[pages.length - 1];
  if (!last || !(last.flags & FLAG_EOS) || partial.length) reject('AUDIO_NOT_OGG');
  // RFC 7845 §3: the ID header alone on the first page, the comment header
  // finishing its own page, audio starting on a fresh page.
  if (pages[0].completed !== 1 || pages[0].granule !== 0n) reject('AUDIO_NOT_OGG');
  const head = parseOpusHead(packets[0]);
  if (!head || head.mappingFamily !== 0 || !packets[1] || !isOpusTags(packets[1])) reject('AUDIO_NOT_OPUS');
  if (head!.channels !== 1) reject('AUDIO_NOT_MONO');
  let seen = 1, tagsPage = 1;
  while (tagsPage < pages.length && seen + pages[tagsPage].completed < 2) seen += pages[tagsPage++].completed;
  if (tagsPage >= pages.length || seen + pages[tagsPage].completed !== 2) reject('AUDIO_NOT_OGG');
  for (let i = 1; i <= tagsPage; i++) if (pages[i].granule !== 0n && pages[i].granule !== NO_GRANULE) reject('AUDIO_NOT_OGG');
  const audio = packets.slice(2);
  if (!audio.length) reject('AUDIO_EMPTY');
  let decoded = 0n, packet = 2, previous = 0n;
  for (let i = tagsPage + 1; i < pages.length; i++) {
    const p = pages[i];
    for (let k = 0; k < p.completed; k++) {
      const info = opusPacketInfo(packets[packet++]);
      if (!info) reject('AUDIO_NOT_OPUS');
      if (info!.stereo) reject('AUDIO_NOT_MONO');
      decoded += BigInt(info!.samples);
      if (decoded - BigInt(head!.preSkip) > BigInt(VOICE_MAX_SAMPLES)) reject('AUDIO_TOO_LONG');
    }
    if (!p.completed) { if (p.granule !== NO_GRANULE) reject('AUDIO_NOT_OGG'); continue; }
    // Interior pages carry the exact decoded count; the final page may trim the end.
    if (p === last ? p.granule <= previous || p.granule > decoded : p.granule !== decoded) reject('AUDIO_NOT_OGG');
    previous = p.granule;
  }
  const playable = last.granule - BigInt(head!.preSkip);
  if (playable <= 0n) reject('AUDIO_EMPTY');
  return { seconds: Number(playable) / OPUS_RATE };
}
