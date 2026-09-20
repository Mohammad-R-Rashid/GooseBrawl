import * as Sentry from "@sentry/cloudflare";
import type { Env } from "../env";
import { mockVoice } from "../env";
import { synthBabbleWav } from "./r2";

const FLASH = "eleven_flash_v2_5";

/** Delivery settings (wrangler vars) and their signature for the cache key, so a tone change never serves stale audio. */
export function delivery(env: Env) {
  const stability = Number(env.ELEVENLABS_STABILITY ?? 0.45);
  const style = Number(env.ELEVENLABS_STYLE ?? 0.45);
  const speed = Number(env.ELEVENLABS_SPEED ?? 1.05);
  const textCase = env.ELEVENLABS_TEXT_CASE === "sentence" ? "sentence" : "caps";
  return { stability, style, speed, textCase, signature: `s${stability}-y${style}-p${speed}-${textCase}` };
}

/** Sentence case, ElevenLabs tags untouched. */
export function shapeText(text: string, textCase: string): string {
  if (textCase !== "sentence") return text;
  return text.split(/(\[[^\]]*\])/).map((part) => part.startsWith("[") ? part
    : part.toLowerCase().replace(/(^|[.!?]\s+)([a-z])/g, (_m, a: string, b: string) => a + b.toUpperCase()).replace(/\bi\b/g, "I").replace(/\bi'/g, "I'")).join("");
}

export interface SpeakResult {
  wav: ArrayBuffer;
  model: string;
  voice: string;
  mocked: boolean;
}

let cachedVoice: string | null = null;

export function voiceFor(env: Env, voice: "male" | "female" | undefined): string {
  return (voice === "female" ? env.ELEVENLABS_VOICE_ID_FEMALE : env.ELEVENLABS_VOICE_ID) || env.ELEVENLABS_VOICE_ID || "auto";
}

async function resolveVoice(env: Env, voiceId?: string): Promise<string> {
  if (voiceId && voiceId !== "auto") return voiceId;
  if (env.ELEVENLABS_VOICE_ID) return env.ELEVENLABS_VOICE_ID;
  if (cachedVoice) return cachedVoice;
  // New accounts have no stock voices via the API unless designed/added: take whatever the account has.
  const res = await fetch("https://api.elevenlabs.io/v2/voices?page_size=5", {
    headers: { "xi-api-key": env.ELEVENLABS_API_KEY ?? "" },
    signal: AbortSignal.timeout(4000),
  });
  if (!res.ok) throw new Error(`elevenlabs voices ${res.status}`);
  const data = (await res.json()) as { voices?: { voice_id: string; name: string }[] };
  const v = data.voices?.[0];
  if (!v) throw new Error("elevenlabs: no voices on this account (run scripts/design-voice.ts)");
  cachedVoice = v.voice_id;
  return cachedVoice;
}

/** ElevenLabs TTS -> 22.05 kHz WAV. Expressive model first (audio tags), Flash as the fallback. */
export async function speak(env: Env, text: string, voiceId?: string): Promise<SpeakResult> {
  return Sentry.startSpan(
    { op: "gen_ai.execute_tool", name: "speak", attributes: { "gen_ai.tool.name": "elevenlabs.tts", "gen_ai.tool.input": text, "gen_ai.system": "elevenlabs" } },
    async (span) => {
      if (mockVoice(env)) {
        span.setAttribute("mocked", true);
        return { wav: synthBabbleWav(text), model: "mock", voice: "mock", mocked: true };
      }
      const voice = await resolveVoice(env, voiceId);
      span.setAttribute("voice_id", voice);
      const model = env.ELEVENLABS_MODEL || "eleven_v3_conversational";
      const d = delivery(env);
      const shaped = shapeText(text, d.textCase);
      try {
        const wav = await convert(env, voice, model, shaped);
        span.setAttribute("gen_ai.request.model", model);
        span.setAttribute("audio_bytes", wav.byteLength);
        return { wav, model, voice, mocked: false };
      } catch (e) {
        Sentry.logger.warn("elevenlabs primary model failed, falling back to flash", { model, error: String(e) });
        const plain = shaped.replace(/\[[^\]]*\]/g, "").replace(/\s+/g, " ").trim();
        const wav = await convert(env, voice, FLASH, plain);
        span.setAttribute("gen_ai.request.model", FLASH);
        span.setAttribute("fallback_model", true);
        return { wav, model: FLASH, voice, mocked: false };
      }
    },
  );
}

async function convert(env: Env, voice: string, model: string, text: string): Promise<ArrayBuffer> {
  const res = await fetch(`https://api.elevenlabs.io/v1/text-to-speech/${voice}?output_format=wav_22050`, {
    method: "POST",
    headers: { "xi-api-key": env.ELEVENLABS_API_KEY ?? "", "Content-Type": "application/json", Accept: "audio/wav" },
    body: JSON.stringify({
      text,
      model_id: model,
      voice_settings: { stability: delivery(env).stability, similarity_boost: 0.75, style: delivery(env).style, use_speaker_boost: true, speed: delivery(env).speed },
    }),
    signal: AbortSignal.timeout(7000),
  });
  if (!res.ok) throw new Error(`elevenlabs tts ${res.status}: ${(await res.text()).slice(0, 200)}`);
  return res.arrayBuffer();
}
