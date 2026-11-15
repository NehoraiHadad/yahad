// WebM → Ogg remux for MediaRecorder's `audio/webm;codecs=opus` output.
// The Opus packets are copied unchanged: no decoding, no re-encoding, no downmix.
// Only the layout MediaRecorder writes is accepted: one Opus audio track, its OpusHead
// in CodecPrivate, unlaced (Simple)Blocks. Anything else is refused with a reason.
import { VOICE_MAX_BYTES, VOICE_MAX_SAMPLES, opusPacketInfo, parseOpusHead } from '../src/opus';
import { writeOggOpus } from '../src/ogg-opus';

export type RemuxFailure = 'not-webm' | 'not-opus' | 'not-mono' | 'empty' | 'too-large';
export class RemuxError extends Error {
  constructor(readonly reason: RemuxFailure) { super(reason); }
}

const EBML = 0x1a45dfa3, DOC_TYPE = 0x4282, SEGMENT = 0x18538067, TRACKS = 0x1654ae6b, TRACK_ENTRY = 0xae,
  TRACK_NUMBER = 0xd7, TRACK_TYPE = 0x83, CODEC_ID = 0x86, CODEC_PRIVATE = 0x63a2, CLUSTER = 0x1f43b675,
  BLOCK_GROUP = 0xa0, BLOCK = 0xa1, SIMPLE_BLOCK = 0xa3;
// Masters whose children are read in place. Only Segment and Cluster may have unknown
// size (live MediaRecorder output); their extent ends where the stream does.
const DESCEND = new Set([EBML, SEGMENT, TRACKS, TRACK_ENTRY, CLUSTER, BLOCK_GROUP]);
const UNKNOWN_SIZE_ALLOWED = new Set([SEGMENT, CLUSTER]);

interface Track { number?: number; type?: number; codec?: string; head?: Uint8Array }

/** Extract the OpusHead and the audio packets of a MediaRecorder WebM recording. */
export function webmOpusPackets(bytes: Uint8Array): { head: Uint8Array; packets: Uint8Array[] } {
  const fail = (reason: RemuxFailure): never => { throw new RemuxError(reason); };
  // EBML variable-length integers (RFC 8794 §4). IDs keep their marker bit; sizes drop it.
  const vint = (from: Uint8Array, pos: number, keepMarker: boolean) => {
    const first = from[pos];
    if (first === undefined || first === 0) return fail('not-webm');
    const length = Math.clz32(first) - 23;
    if (pos + length > from.length) fail('not-webm');
    let value = keepMarker ? first : first & (0xff >> length), allOnes = value === 0xff >> length;
    for (let i = 1; i < length; i++) { value = value * 256 + from[pos + i]; allOnes &&= from[pos + i] === 0xff; }
    return { value, length, unknown: !keepMarker && allOnes };
  };
  let at = 0;
  const tracks: Track[] = [], blocks: { track: number; frame: Uint8Array }[] = [];
  let docType = '';
  if (bytes.length < 4 || new DataView(bytes.buffer, bytes.byteOffset, 4).getUint32(0) !== EBML) fail('not-webm');
  while (at < bytes.length) {
    const id = vint(bytes, at, true);
    if (id.length > 4) fail('not-webm');
    const size = vint(bytes, at + id.length, false);
    at += id.length + size.length;
    if (DESCEND.has(id.value)) {
      if (size.unknown && !UNKNOWN_SIZE_ALLOWED.has(id.value)) fail('not-webm');
      if (id.value === TRACK_ENTRY) tracks.push({});
      continue;
    }
    if (size.unknown || at + size.value > bytes.length) fail('not-webm');
    const body = bytes.subarray(at, at + size.value), track = tracks[tracks.length - 1];
    const uint = () => body.reduce((n, b) => n * 256 + b, 0), text = () => new TextDecoder().decode(body);
    switch (id.value) {
      case DOC_TYPE: docType = text(); break;
      case TRACK_NUMBER: if (track) track.number = uint(); break;
      case TRACK_TYPE: if (track) track.type = uint(); break;
      case CODEC_ID: if (track) track.codec = text(); break;
      case CODEC_PRIVATE: if (track) track.head = body; break;
      case SIMPLE_BLOCK:
      case BLOCK: {
        // Block header: track number, int16 timecode, flags (lacing in bits 1–2).
        const number = vint(body, 0, false), header = number.length + 3;
        if (number.unknown || header > body.length) fail('not-webm');
        if ((body[header - 1] >> 1) & 3) fail('not-webm');
        blocks.push({ track: number.value, frame: body.subarray(header) });
        break;
      }
    }
    at += size.value;
  }
  if (docType !== 'webm' || tracks.length !== 1) fail('not-webm');
  const [track] = tracks;
  if (track.type !== 2 || track.codec !== 'A_OPUS' || !track.head) fail('not-opus');
  const head = parseOpusHead(track.head!);
  if (!head || head.mappingFamily !== 0) fail('not-opus');
  if (head!.channels !== 1) fail('not-mono');
  const packets = blocks.filter(b => b.track === track.number).map(b => b.frame);
  if (blocks.length !== packets.length) fail('not-webm');
  if (!packets.length) fail('empty');
  return { head: head!.bytes, packets };
}

/**
 * The Ogg Opus voice message for a recording: the recorder's own packets, cut at
 * VOICE_MAX_SAMPLES of playable audio (the recorder stops on a timer, so its last
 * packet can overrun by a fraction of a second).
 */
export function voiceOggFromWebm(bytes: Uint8Array, serial: number): Uint8Array {
  const { head, packets } = webmOpusPackets(bytes);
  const preSkip = parseOpusHead(head)!.preSkip, kept: Uint8Array[] = [];
  let decoded = 0;
  for (const packet of packets) {
    const info = opusPacketInfo(packet);
    if (!info) throw new RemuxError('not-opus');
    if (info.stereo) throw new RemuxError('not-mono');
    if (decoded + info.samples - preSkip > VOICE_MAX_SAMPLES) break;
    decoded += info.samples;
    kept.push(packet);
  }
  if (decoded <= preSkip) throw new RemuxError('empty');
  const ogg = writeOggOpus(head, kept, serial);
  if (ogg.length > VOICE_MAX_BYTES) throw new RemuxError('too-large');
  return ogg;
}
