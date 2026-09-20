import type { Env } from "../env";

export async function sha256Hex(text: string): Promise<string> {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return Array.from(new Uint8Array(buf)).map((b) => b.toString(16).padStart(2, "0")).join("");
}

export function audioKey(hash: string): string {
  return `audio/${hash}.wav`;
}

export async function getCachedWav(env: Env, key: string): Promise<boolean> {
  try {
    const head = await env.BUCKET.head(key);
    return head !== null;
  } catch {
    return false;
  }
}

export async function putWav(env: Env, key: string, bytes: ArrayBuffer): Promise<void> {
  await env.BUCKET.put(key, bytes, {
    httpMetadata: { contentType: "audio/wav", cacheControl: "public, max-age=31536000, immutable" },
  });
}

/** Serve a cached WAV through the Worker (one origin, immutable caching, no r2.dev rate limits). */
export async function serveWav(env: Env, key: string): Promise<Response> {
  const object = await env.BUCKET.get(key);
  if (!object) return new Response("Not found", { status: 404 });
  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set("etag", object.httpEtag);
  headers.set("content-type", "audio/wav");
  headers.set("cache-control", "public, max-age=31536000, immutable");
  headers.set("access-control-allow-origin", "*");
  return new Response(object.body, { headers });
}

/**
 * Mock voice for local development without an ElevenLabs key: goose-babble, one syllable per word, so the whole
 * pipeline (cache, R2, Unity playback) runs. 22.05 kHz mono 16-bit PCM WAV.
 */
export function synthBabbleWav(text: string): ArrayBuffer {
  const sr = 22050;
  const words = text.replace(/\[[^\]]*\]/g, "").split(/\s+/).filter(Boolean);
  const syllables = Math.max(2, Math.min(14, words.reduce((n, w) => n + Math.max(1, Math.round(w.length / 3)), 0)));
  const sylLen = Math.round(sr * 0.13);
  const gap = Math.round(sr * 0.05);
  const total = syllables * (sylLen + gap) + Math.round(sr * 0.1);
  const pcm = new Int16Array(total);
  let seed = 7;
  const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; };
  let pos = 0;
  for (let s = 0; s < syllables; s++) {
    const f0 = 330 + rnd() * 110;
    const rasp = 0.35 + rnd() * 0.3;
    for (let i = 0; i < sylLen; i++) {
      const t = i / sr;
      const k = i / sylLen;
      const env = Math.sin(Math.PI * Math.min(1, k * 1.15)) * (1 - 0.3 * k);
      const pitch = f0 * (1 + 0.08 * Math.sin(k * Math.PI));
      const saw = 2 * ((t * pitch) % 1) - 1;
      const h2 = 0.5 * Math.sin(2 * Math.PI * pitch * 2 * t);
      const noise = (rnd() * 2 - 1) * rasp * 0.4;
      pcm[pos + i] = Math.max(-32767, Math.min(32767, Math.round((saw * 0.5 + h2 * 0.3 + noise) * env * 12000)));
    }
    pos += sylLen + gap;
  }
  return pcmToWav(pcm, sr);
}

export function pcmToWav(pcm: Int16Array, sampleRate: number): ArrayBuffer {
  const bytes = pcm.length * 2;
  const buf = new ArrayBuffer(44 + bytes);
  const v = new DataView(buf);
  const str = (o: number, s: string) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
  str(0, "RIFF"); v.setUint32(4, 36 + bytes, true); str(8, "WAVE");
  str(12, "fmt "); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, sampleRate, true); v.setUint32(28, sampleRate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  str(36, "data"); v.setUint32(40, bytes, true);
  new Int16Array(buf, 44).set(pcm);
  return buf;
}
