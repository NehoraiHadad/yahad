// The preview that accompanies an image or video: a small baseline or progressive
// JPEG made by the browser. Checked structurally, marker by marker, never decoded.

export const PREVIEW_EDGE = 160;
/** Exclusive upper bound, the size WhatsApp previews have always been kept under here. */
export const PREVIEW_MAX_BYTES = 100_000;

// SOF markers that carry frame dimensions (DHT C4, JPG C8 and DAC CC are not frames).
const FRAME = new Set([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf]);

/** True only for one complete JPEG of at most PREVIEW_EDGE pixels per side and under PREVIEW_MAX_BYTES. */
export function validJpegPreview(bytes: Uint8Array): boolean {
  const n = bytes.length;
  if (n < 4 || n >= PREVIEW_MAX_BYTES || bytes[0] !== 0xff || bytes[1] !== 0xd8) return false;
  let at = 2, frame = false, scan = false;
  while (at < n) {
    if (bytes[at] !== 0xff) return false;
    while (at < n && bytes[at] === 0xff) at++; // fill bytes
    if (at >= n) return false;
    const marker = bytes[at++];
    if (marker === 0xd9) return frame && scan && at === n; // EOI ends the file exactly
    if (marker === 0x00 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd8)) return false;
    if (at + 2 > n) return false;
    const length = (bytes[at] << 8) | bytes[at + 1];
    if (length < 2 || at + length > n) return false;
    if (FRAME.has(marker)) {
      if (frame || length < 8) return false;
      const height = (bytes[at + 3] << 8) | bytes[at + 4], width = (bytes[at + 5] << 8) | bytes[at + 6];
      if (bytes[at + 2] !== 8 || !width || !height || width > PREVIEW_EDGE || height > PREVIEW_EDGE) return false;
      frame = true;
    }
    at += length;
    if (marker !== 0xda) continue;
    if (!frame) return false;
    scan = true;
    // Entropy-coded data: skip stuffed 0xFF00 and restart markers up to the next real marker.
    while (at < n) {
      if (bytes[at] !== 0xff) { at++; continue; }
      const next = bytes[at + 1];
      if (next === 0x00 || (next >= 0xd0 && next <= 0xd7)) { at += 2; continue; }
      if (next === 0xff) { at++; continue; }
      break;
    }
  }
  return false;
}
