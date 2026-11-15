// How an uploaded attachment is sent, decided from its sniffed type (file-type).
// The browser and the server run the same rule on the same bytes, so the browser
// knows exactly when the server will require a preview.

export type AttachmentKind = 'image' | 'video' | 'document';
export const IMAGE_MIMES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif'] as const;

/** `detected` is the sniffed MIME without parameters, or undefined when unknown. */
export function attachmentKind(detected: string | undefined): AttachmentKind {
  if ((IMAGE_MIMES as readonly string[]).includes(detected || '')) return 'image';
  if (detected === 'video/mp4') return 'video';
  return 'document';
}

/** Images and videos travel with a browser-made JPEG preview; WhatsApp shows it before download. */
export const needsPreview = (kind: string) => kind === 'image' || kind === 'video';
