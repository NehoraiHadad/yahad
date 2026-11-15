// Opus facts shared by the browser recorder and the server validator.
// Sources: RFC 6716 §3.1 (TOC byte) and RFC 7845 §5.1 (ID header).

/** Voice-message bounds, enforced by the browser before upload and by the server on receipt. */
export const VOICE_MAX_SECONDS = 120;
export const VOICE_MAX_BYTES = 4 * 1024 ** 2;
/** Ogg Opus granule positions always count 48 kHz samples. */
export const OPUS_RATE = 48_000;
export const VOICE_MAX_SAMPLES = VOICE_MAX_SECONDS * OPUS_RATE;

export interface OpusHead {
  channels: number;
  preSkip: number;
  inputRate: number;
  mappingFamily: number;
  bytes: Uint8Array;
}

const ascii = (bytes: Uint8Array, at: number, text: string) =>
  bytes.length >= at + text.length && [...text].every((c, i) => bytes[at + i] === c.charCodeAt(0));
export const isOpusTags = (packet: Uint8Array) => ascii(packet, 0, 'OpusTags');

/** Parse an OpusHead identification header; undefined when it is not one. */
export function parseOpusHead(packet: Uint8Array): OpusHead | undefined {
  if (packet.length < 19 || !ascii(packet, 0, 'OpusHead')) return;
  // Major version 0 (RFC 7845 §5.1: the upper four bits); this app knows no other.
  if (packet[8] >> 4 !== 0) return;
  const view = new DataView(packet.buffer, packet.byteOffset, packet.byteLength);
  const mappingFamily = packet[18];
  if (mappingFamily === 0 && packet.length !== 19) return;
  return { channels: packet[9], preSkip: view.getUint16(10, true), inputRate: view.getUint32(12, true), mappingFamily, bytes: packet };
}

// Samples per frame at 48 kHz, by TOC configuration number (RFC 6716 Table 2).
const FRAME_SAMPLES = [
  480, 960, 1920, 2880, 480, 960, 1920, 2880, 480, 960, 1920, 2880, // SILK NB/MB/WB
  480, 960, 480, 960, // Hybrid SWB/FB
  120, 240, 480, 960, 120, 240, 480, 960, 120, 240, 480, 960, 120, 240, 480, 960, // CELT
];

export interface OpusPacketInfo { samples: number; stereo: boolean }

/** Decoded length of one Opus packet from its TOC byte; undefined when malformed. */
export function opusPacketInfo(packet: Uint8Array): OpusPacketInfo | undefined {
  if (!packet.length) return;
  const toc = packet[0], code = toc & 3;
  let frames: number;
  if (code === 0) frames = 1;
  else if (code === 1 || code === 2) frames = 2;
  else {
    if (packet.length < 2) return;
    frames = packet[1] & 0x3f;
  }
  const samples = frames * FRAME_SAMPLES[toc >> 3];
  // RFC 6716 §3.2.5: a packet never exceeds 120 ms.
  if (!frames || samples > 5760) return;
  return { samples, stereo: (toc & 4) !== 0 };
}
