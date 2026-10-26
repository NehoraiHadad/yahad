import { spawn } from 'node:child_process';
/** Pipe through ffmpeg: no decrypted temporary audio files; one process per app at a time. */
let busy = false;
export async function voiceToOpus(bytes: Buffer): Promise<Buffer> {
  if (busy) throw new Error('Audio encoder busy');
  busy = true;
  try {
    return await new Promise((resolve, reject) => {
      const child = spawn(
        process.env.FFMPEG_PATH || 'ffmpeg',
        [
          '-hide_banner',
          '-loglevel',
          'error',
          '-nostdin',
          '-protocol_whitelist',
          'pipe,crypto,data',
          '-i',
          'pipe:0',
          '-vn',
          '-t',
          '120',
          '-c:a',
          'libopus',
          '-b:a',
          '32k',
          '-ac',
          '1',
          '-f',
          'ogg',
          'pipe:1',
        ],
        { stdio: ['pipe', 'pipe', 'ignore'] },
      );
      let size = 0;
      const chunks: Buffer[] = [];
      const timer = setTimeout(() => {
        child.kill('SIGKILL');
        reject(new Error('Audio timeout'));
      }, 20_000);
      child.on('error', () => {
        clearTimeout(timer);
        reject(new Error('Audio encoder unavailable'));
      });
      child.stdin.on('error', () => {});
      child.stdout.on('data', (x: Buffer) => {
        size += x.length;
        if (size > 4 * 1024 ** 2) {
          child.kill();
          return;
        }
        chunks.push(x);
      });
      child.on('close', (code) => {
        clearTimeout(timer);
        code === 0 && size > 0 && size <= 4 * 1024 ** 2
          ? resolve(Buffer.concat(chunks))
          : reject(new Error('Audio encoding failed'));
      });
      child.stdin.end(bytes);
    });
  } finally {
    busy = false;
  }
}

/** Generate a real JPEG preview without third-party plaintext temp files. */
export async function thumbnail(bytes: Buffer): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const child = spawn(
      process.env.FFMPEG_PATH || 'ffmpeg',
      [
        '-hide_banner',
        '-loglevel',
        'error',
        '-nostdin',
        '-protocol_whitelist',
        'pipe,crypto,data',
        '-i',
        'pipe:0',
        '-vf',
        'scale=160:160:force_original_aspect_ratio=decrease',
        '-frames:v',
        '1',
        '-f',
        'image2pipe',
        '-vcodec',
        'mjpeg',
        'pipe:1',
      ],
      { stdio: ['pipe', 'pipe', 'ignore'] },
    );
    const parts: Buffer[] = [];
    let size = 0;
    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      reject(new Error('Thumbnail timeout'));
    }, 15000);
    child.on('error', () => {
      clearTimeout(timer);
      reject(new Error('Preview unavailable'));
    });
    child.stdin.on('error', () => {});
    child.stdout.on('data', (b: Buffer) => {
      size += b.length;
      if (size > 100000) child.kill('SIGKILL');
      else parts.push(b);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0 && size > 0 && size < 100000) resolve(Buffer.concat(parts));
      else reject(new Error('Preview unavailable'));
    });
    child.stdin.end(bytes);
  });
}
