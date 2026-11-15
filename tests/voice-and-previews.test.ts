import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { VoiceRejection, validateVoiceOgg, writeOggOpus, opusTags } from '../src/ogg-opus.ts';
import { VOICE_MAX_BYTES, VOICE_MAX_SAMPLES, parseOpusHead } from '../src/opus.ts';
import { RemuxError, voiceOggFromWebm, webmOpusPackets } from '../web/webmOpus.ts';
import { validJpegPreview, PREVIEW_MAX_BYTES } from '../src/jpeg-preview.ts';
import { attachmentKind, needsPreview } from '../src/media-kind.ts';
import { voiceSupport, VOICE_RECORDING_MIME } from '../web/voiceRecording.ts';

// An independent Ogg page writer (RFC 3533), so refusals can be built from raw pages.
const crcTable = Array.from({ length: 256 }, (_, i) => {
  let r = i << 24;
  for (let k = 0; k < 8; k++) r = r & 0x80000000 ? (r << 1) ^ 0x04c11db7 : r << 1;
  return r >>> 0;
});
const crc = (b: Uint8Array) => b.reduce((c, x) => ((c << 8) ^ crcTable[((c >>> 24) ^ x) & 0xff]) >>> 0, 0);
function page(flags: number, granule: bigint, sequence: number, packets: Uint8Array[], serial = 7) {
  const lacing = packets.flatMap(p => [...Array(Math.floor(p.length / 255)).fill(255), p.length % 255]);
  const out = Buffer.alloc(27 + lacing.length + packets.reduce((n, p) => n + p.length, 0));
  out.write('OggS', 0, 'latin1'); out[5] = flags; out.writeBigInt64LE(granule, 6);
  out.writeUInt32LE(serial, 14); out.writeUInt32LE(sequence, 18); out[26] = lacing.length;
  Buffer.from(lacing).copy(out, 27);
  let at = 27 + lacing.length;
  for (const p of packets) { Buffer.from(p).copy(out, at); at += p.length; }
  out.writeUInt32LE(crc(out), 22);
  return out;
}
const head = (channels = 1, preSkip = 312) => {
  const h = Buffer.alloc(19); h.write('OpusHead', 0, 'latin1'); h[8] = 1; h[9] = channels;
  h.writeUInt16LE(preSkip, 10); h.writeUInt32LE(48000, 12); return h;
};
const celt20 = (stereo = false) => Uint8Array.from([0xf8 | (stereo ? 4 : 0), 1, 2, 3]); // CELT FB 20 ms: 960 samples
const packets = (count: number, stereo = false) => Array.from({ length: count }, () => celt20(stereo));
const code = (bytes: Uint8Array) => {
  try { validateVoiceOgg(bytes); return 'accepted'; } catch (e) { if (e instanceof VoiceRejection) return e.code; throw e; }
};
const chrome = readFileSync('fixtures/voice-chrome-mono.webm');

test('a real FFmpeg-made and a real Chrome-recorded Ogg Opus voice message are accepted with their length', () => {
  assert.equal(validateVoiceOgg(readFileSync('fixtures/audio.ogg')).seconds, 3);
  const ogg = voiceOggFromWebm(chrome, 1234);
  assert.ok(Math.abs(validateVoiceOgg(ogg).seconds - 2.28) < 0.001);
  assert.equal(Buffer.from(ogg.subarray(0, 4)).toString('latin1'), 'OggS');
});

test('the remux copies the recorder Opus packets byte for byte: nothing is decoded or re-encoded', () => {
  const { head: opusHead, packets: recorded } = webmOpusPackets(chrome);
  assert.equal(parseOpusHead(opusHead)!.channels, 1);
  const ogg = voiceOggFromWebm(chrome, 99), body = Buffer.from(ogg);
  let at = 0;
  for (const p of recorded) { at = body.indexOf(Buffer.from(p), at); assert.ok(at > 0); at += p.length; }
});

test('the server refuses wrong containers, wrong codecs, stereo, overlong and oversized voice with a clear code', () => {
  const sequence = (audio: Uint8Array[], h = head(), tags: Uint8Array = opusTags()) =>
    Buffer.concat([page(2, 0n, 0, [h]), page(0, 0n, 1, [tags]), page(4, BigInt(audio.length * 960), 2, audio)]);
  assert.equal(code(sequence(packets(10))), 'accepted');
  // Container
  assert.equal(code(chrome), 'AUDIO_NOT_OGG');
  assert.equal(code(readFileSync('fixtures/garden.mp4')), 'AUDIO_NOT_OGG');
  const corrupt = sequence(packets(10)); corrupt[corrupt.length - 1] ^= 1;
  assert.equal(code(corrupt), 'AUDIO_NOT_OGG');
  assert.equal(code(sequence(packets(10)).subarray(0, 120)), 'AUDIO_NOT_OGG');
  assert.equal(code(Buffer.concat([page(2, 0n, 0, [head()]), page(0, 0n, 1, [opusTags()]), page(0, 9600n, 2, packets(10))])), 'AUDIO_NOT_OGG'); // no EOS
  assert.equal(code(Buffer.concat([page(2, 0n, 0, [head()]), page(0, 0n, 2, [opusTags()]), page(4, 9600n, 3, packets(10))])), 'AUDIO_NOT_OGG'); // gap
  assert.equal(code(Buffer.concat([page(2, 0n, 0, [head()]), page(0, 0n, 1, [opusTags()], 8), page(4, 9600n, 2, packets(10))])), 'AUDIO_NOT_OGG'); // two streams
  assert.equal(code(Buffer.concat([page(2, 0n, 0, [head(), opusTags()]), page(4, 9600n, 1, packets(10))])), 'AUDIO_NOT_OGG'); // header not alone
  assert.equal(code(Buffer.concat([page(2, 0n, 0, [head()]), page(0, 0n, 1, [opusTags()]), page(4, 99999n, 2, packets(10))])), 'AUDIO_NOT_OGG'); // lying granule
  // Codec
  const vorbis = Buffer.from('\x01vorbis' + '\0'.repeat(23), 'latin1');
  assert.equal(code(Buffer.concat([page(2, 0n, 0, [vorbis]), page(0, 0n, 1, [opusTags()]), page(4, 9600n, 2, packets(10))])), 'AUDIO_NOT_OPUS');
  assert.equal(code(sequence(packets(10), head(), Buffer.from('NotTags!'))), 'AUDIO_NOT_OPUS');
  const familyOne = Buffer.concat([head(1), Buffer.from([1, 0, 0])]); familyOne[18] = 1;
  assert.equal(code(sequence(packets(10), familyOne)), 'AUDIO_NOT_OPUS');
  assert.equal(code(sequence([Uint8Array.from([0xfb])])), 'AUDIO_NOT_OPUS'); // code-3 packet without its frame count
  // Channels
  assert.equal(code(sequence(packets(10), head(2))), 'AUDIO_NOT_MONO');
  assert.equal(code(sequence(packets(10, true))), 'AUDIO_NOT_MONO');
  // Length: exactly the cap is fine, one packet beyond is refused.
  const fit = (VOICE_MAX_SAMPLES + 312) / 960;
  assert.equal(code(writeOggOpus(head(), packets(Math.floor(fit)), 1)), 'accepted');
  assert.equal(code(writeOggOpus(head(), packets(Math.floor(fit) + 1), 1)), 'AUDIO_TOO_LONG');
  // Size and emptiness
  assert.equal(code(Buffer.alloc(VOICE_MAX_BYTES + 1)), 'AUDIO_TOO_LARGE');
  assert.equal(code(Buffer.concat([page(2, 0n, 0, [head()]), page(4, 0n, 1, [opusTags()])])), 'AUDIO_EMPTY');
});

test('the browser remux refuses what the server would: not WebM, not Opus, stereo, empty', () => {
  const reason = (bytes: Uint8Array) => { try { voiceOggFromWebm(bytes, 1); return 'accepted'; } catch (e) { return (e as RemuxError).reason; } };
  assert.equal(reason(chrome), 'accepted');
  assert.equal(reason(readFileSync('fixtures/audio.ogg')), 'not-webm');
  assert.equal(reason(chrome.subarray(0, chrome.length - 7)), 'not-webm');
  const stereo = Buffer.from(chrome); stereo[stereo.indexOf('OpusHead') + 9] = 2;
  assert.equal(reason(stereo), 'not-mono');
  const vorbis = Buffer.from(chrome); vorbis.write('A_VORB', vorbis.indexOf('A_OPUS'), 'latin1');
  assert.equal(reason(vorbis), 'not-opus');
  const headerOnly = chrome.subarray(0, chrome.indexOf(Buffer.from([0x1f, 0x43, 0xb6, 0x75])));
  assert.equal(reason(headerOnly), 'empty');
});

test('only a complete, small JPEG passes as a preview', () => {
  assert.equal(validJpegPreview(readFileSync('fixtures/garden-preview.jpg')), true);
  assert.equal(validJpegPreview(readFileSync('fixtures/garden-video-preview.jpg')), true);
  assert.equal(validJpegPreview(readFileSync('fixtures/garden.jpg')), false, 'larger than 160 px');
  assert.equal(validJpegPreview(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0, 4, 1, 2, 0xff, 0xd9])), false, 'no frame or scan');
  assert.equal(validJpegPreview(Buffer.concat([Buffer.from([0xff, 0xd8, 0xff]), Buffer.from('<script>evil()</script>'), Buffer.from([0xff, 0xd9])])), false);
  assert.equal(validJpegPreview(readFileSync('fixtures/flower.webp')), false);
  const preview = readFileSync('fixtures/garden-preview.jpg');
  assert.equal(validJpegPreview(preview.subarray(0, preview.length - 2)), false, 'truncated');
  assert.equal(validJpegPreview(Buffer.concat([preview, Buffer.from('trailing')])), false);
  const padded = Buffer.concat([preview.subarray(0, 2), Buffer.from([0xff, 0xfe, 0xff, 0xff]), Buffer.alloc(PREVIEW_MAX_BYTES), preview.subarray(2)]);
  assert.equal(validJpegPreview(padded), false, 'too big');
});

test('the browser and the server classify attachments with one rule', () => {
  for (const mime of ['image/png', 'image/jpeg', 'image/webp', 'image/gif']) assert.equal(attachmentKind(mime), 'image');
  assert.equal(attachmentKind('video/mp4'), 'video');
  for (const mime of ['video/quicktime', 'image/heic', 'application/pdf', undefined]) assert.equal(attachmentKind(mime), 'document');
  assert.deepEqual(['image', 'video', 'document', 'audio', 'sticker'].filter(needsPreview), ['image', 'video']);
});

test('voice recording is offered only where WebM/Opus recording exists', () => {
  const env = (secure: boolean, media: boolean, recorder?: (type: string) => boolean) => ({
    isSecureContext: secure,
    navigator: { mediaDevices: media ? { getUserMedia: () => {} } : undefined },
    MediaRecorder: recorder ? { isTypeSupported: recorder } : undefined,
  });
  const modern = (type: string) => type === VOICE_RECORDING_MIME;
  assert.equal(voiceSupport(env(true, true, modern)), 'supported');
  assert.equal(voiceSupport(env(false, false, modern)), 'insecure');
  assert.equal(voiceSupport(env(true, true)), 'unsupported', 'no MediaRecorder (old iPadOS)');
  assert.equal(voiceSupport(env(true, true, type => type === 'audio/mp4')), 'unsupported', 'iPadOS before 18.4 records only MP4/AAC');
});
