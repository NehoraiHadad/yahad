// The preview WhatsApp shows before an image or video downloads, made here in the
// browser: one frame drawn to a canvas, at most 160 px per side, as JPEG.
import { attachmentKind, needsPreview } from '../src/media-kind';
import { PREVIEW_EDGE, validJpegPreview } from '../src/jpeg-preview';

export class PreviewError extends Error {}
const UNAVAILABLE = 'הדפדפן לא הצליח להכין תצוגה מקדימה לקובץ הזה, ולכן אי אפשר לשלוח אותו כתמונה או כסרטון.';
const TIMEOUT_MS = 15_000;

/** Same sniffing as the server (file-type), so both sides agree on when a preview is needed. */
export async function attachmentPreviewKind(file: Blob) {
  const { fileTypeFromBlob } = await import('file-type');
  const kind = attachmentKind((await fileTypeFromBlob(file))?.mime.split(';')[0]);
  return needsPreview(kind) ? (kind as 'image' | 'video') : undefined;
}

const once = (target: EventTarget, event: string) =>
  new Promise<void>((resolve, reject) => {
    const timer = setTimeout(() => done(new PreviewError(UNAVAILABLE)), TIMEOUT_MS);
    const done = (error?: Error) => {
      clearTimeout(timer);
      target.removeEventListener(event, ok);
      target.removeEventListener('error', bad);
      error ? reject(error) : resolve();
    };
    const ok = () => done(), bad = () => done(new PreviewError(UNAVAILABLE));
    target.addEventListener(event, ok);
    target.addEventListener('error', bad);
  });

async function frame(kind: 'image' | 'video', url: string): Promise<{ source: CanvasImageSource; width: number; height: number }> {
  if (kind === 'image') {
    const image = new Image();
    image.src = url;
    await image.decode().catch(() => { throw new PreviewError(UNAVAILABLE); });
    return { source: image, width: image.naturalWidth, height: image.naturalHeight };
  }
  // Muted and inline so iPadOS loads it without a gesture; draw only after `seeked`,
  // since WebKit can paint a black or stale frame before the seek completes.
  const video = document.createElement('video');
  video.muted = true;
  video.playsInline = true;
  video.setAttribute('playsinline', '');
  video.preload = 'auto';
  const metadata = once(video, 'loadedmetadata');
  video.src = url;
  await metadata;
  const seeked = once(video, 'seeked');
  video.currentTime = Math.min(0.001, video.duration || 0);
  await seeked;
  if (video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA) await once(video, 'loadeddata');
  return { source: video, width: video.videoWidth, height: video.videoHeight };
}

/** A JPEG preview for an image or the first frame of a video; throws PreviewError with a user message. */
export async function makePreview(file: Blob, kind: 'image' | 'video'): Promise<Blob> {
  const url = URL.createObjectURL(file);
  try {
    const { source, width, height } = await frame(kind, url);
    if (!width || !height) throw new PreviewError(UNAVAILABLE);
    const scale = Math.min(1, PREVIEW_EDGE / width, PREVIEW_EDGE / height);
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(width * scale));
    canvas.height = Math.max(1, Math.round(height * scale));
    const context = canvas.getContext('2d');
    if (!context) throw new PreviewError(UNAVAILABLE);
    context.fillStyle = '#fff'; // JPEG has no alpha; transparent pixels become white, not black.
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.drawImage(source, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.8));
    if (!blob || !validJpegPreview(new Uint8Array(await blob.arrayBuffer()))) throw new PreviewError(UNAVAILABLE);
    return blob;
  } finally {
    URL.revokeObjectURL(url);
  }
}
