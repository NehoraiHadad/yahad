// Voice capture contract: the browser records Opus itself (MediaRecorder WebM/Opus,
// Safari/iPadOS 18.4+, Chrome, Edge, Firefox) and sends an Ogg Opus remux of it.
// There is no other path: a browser that cannot record WebM/Opus records nothing.
import { RemuxError, voiceOggFromWebm, type RemuxFailure } from './webmOpus';

export const VOICE_RECORDING_MIME = 'audio/webm;codecs=opus';
/** Mono at capture: a remux cannot downmix, and the server accepts only one channel. */
export const VOICE_CONSTRAINTS: MediaStreamConstraints = { audio: { channelCount: 1 } };
export const VOICE_RECORDER_OPTIONS: MediaRecorderOptions = { mimeType: VOICE_RECORDING_MIME, audioBitsPerSecond: 32_000 };

export type VoiceSupport = 'supported' | 'insecure' | 'unsupported';
export interface VoiceEnvironment {
  isSecureContext: boolean;
  navigator: { mediaDevices?: { getUserMedia?: unknown } };
  MediaRecorder?: { isTypeSupported(type: string): boolean };
}

export function voiceSupport(env: VoiceEnvironment): VoiceSupport {
  if (!env.isSecureContext || typeof env.navigator.mediaDevices?.getUserMedia !== 'function') return 'insecure';
  if (typeof env.MediaRecorder?.isTypeSupported !== 'function' || !env.MediaRecorder.isTypeSupported(VOICE_RECORDING_MIME))
    return 'unsupported';
  return 'supported';
}

export const VOICE_SUPPORT_MESSAGE: Record<Exclude<VoiceSupport, 'supported'>, string> = {
  insecure: 'להקלטה צריך כתובת HTTPS שהמכשיר סומך עליה, או localhost.',
  unsupported: 'הודעות קוליות צריכות מכשיר או דפדפן חדשים יותר: אייפד או אייפון עם iPadOS/iOS 18.4 ומעלה, או Chrome, Edge או Firefox עדכניים. בדפדפן הזה לא מקליטים.',
};

const REMUX_MESSAGE: Record<RemuxFailure, string> = {
  'not-webm': 'ההקלטה לא נשמרה כראוי בדפדפן הזה. אפשר לנסות שוב.',
  'not-opus': 'הדפדפן הקליט בפורמט שאי אפשר לשלוח כהודעה קולית.',
  'not-mono': 'המיקרופון הקליט בסטריאו, ואפשר לשלוח רק הקלטה בערוץ אחד. נסו מיקרופון אחר.',
  empty: 'ההקלטה ריקה. אפשר לנסות שוב.',
  'too-large': 'ההקלטה הגיעה למגבלת הגודל.',
};

/** The Ogg Opus message for a finished recording, or a message explaining why there is none. */
export async function finishVoice(recording: Blob): Promise<{ ogg: Blob } | { error: string }> {
  try {
    const serial = crypto.getRandomValues(new Uint32Array(1))[0];
    const ogg = voiceOggFromWebm(new Uint8Array(await recording.arrayBuffer()), serial);
    return { ogg: new Blob([ogg as BlobPart], { type: 'audio/ogg; codecs=opus' }) };
  } catch (error) {
    if (error instanceof RemuxError) return { error: REMUX_MESSAGE[error.reason] };
    throw error;
  }
}
