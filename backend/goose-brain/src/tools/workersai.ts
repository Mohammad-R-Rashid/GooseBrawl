import * as Sentry from "@sentry/cloudflare";
import type { Env, Beat, EventPayload } from "../env";
import { CHARACTER_SHEET, LINE_SCHEMA, eventContext } from "../prompts";
import { bankLine } from "../bank";
import { sanitizeLine } from "../guard";

/**
 * Workers AI: the zero-key path. With no OpenAI key on the account, Whisper (multilingual) hears the shout and Llama
 * writes the line from the same character sheet and the same Elastic case file. Both live on the Cloudflare account the
 * brain already runs on, so nothing new has to be signed up for.
 */
const WHISPER = "@cf/openai/whisper-large-v3-turbo";
const LLAMA = "@cf/meta/llama-3.3-70b-instruct-fp8-fast";
const LINE_TIMEOUT_MS = 2600;

export function workersAiOn(env: Env): boolean {
  return Boolean(env.AI) && env.MOCK_AI !== "1";
}

function toBase64(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

/** What did the player shout, in whatever language? WAV in, text + detected language out. */
export async function whisper(env: Env, wav: ArrayBuffer, gooseName: string): Promise<{ text: string; lang?: string }> {
  if (!env.AI) return { text: "" };
  return Sentry.startSpan(
    { op: "gen_ai.execute_tool", name: "transcribe", attributes: { "gen_ai.tool.name": "workersai.whisper", "gen_ai.request.model": WHISPER, "gen_ai.system": "cloudflare", audio_bytes: wav.byteLength } },
    async (span) => {
      try {
        const out = (await env.AI!.run(WHISPER, {
          audio: toBase64(wav),
          task: "transcribe",
          vad_filter: true,
          condition_on_previous_text: false,
          initial_prompt: `Someone shouting at a goose named ${gooseName}. Short and loud. Any language.`,
        })) as { text?: string; transcription_info?: { language?: string; language_probability?: number } };
        const text = (out.text ?? "").replace(/\s+/g, " ").trim();
        const lang = out.transcription_info?.language;
        span.setAttribute("transcript", text.slice(0, 120));
        if (lang) span.setAttribute("language", lang);
        return { text, lang };
      } catch (e) {
        Sentry.captureException(e);
        Sentry.logger.error("whisper failed", { error: String(e) });
        return { text: "" };
      }
    },
  );
}

function safeParse(s: string): Record<string, unknown> {
  try {
    return JSON.parse(s) as Record<string, unknown>;
  } catch {
    const m = s.match(/\{[\s\S]*\}/);
    if (m) {
      try {
        return JSON.parse(m[0]) as Record<string, unknown>;
      } catch {
        /* fall through */
      }
    }
    return { line: s };
  }
}

/** One spoken line from Llama on Workers AI, through the content guard, inside a hard timeout; bank line otherwise. */
export async function llamaLine(env: Env, beat: Beat, payload: EventPayload, memory: Record<string, unknown>, fallbackIndex: number): Promise<{ line: string; mood: string; source: string }> {
  const fallback = bankLine(beat, fallbackIndex);
  if (!env.AI) return { line: fallback, mood: "smug", source: "bank" };
  return Sentry.startSpan(
    { op: "gen_ai.generate_text", name: "write line", attributes: { "gen_ai.request.model": LLAMA, "gen_ai.system": "cloudflare", beat } },
    async (span) => {
      try {
        const context = eventContext(beat, payload, memory);
        const out = await Promise.race([
          env.AI!.run(LLAMA, {
            messages: [
              { role: "system", content: CHARACTER_SHEET + "\nStyle for this writer: one or two punchy spoken sentences, 3 to 14 words in total. Never say your own title. Never quote the memory verbatim or list numbers; turn one detail into a jab." },
              { role: "user", content: JSON.stringify(context) },
            ],
            max_tokens: 60,
            temperature: 0.9,
            response_format: { type: "json_schema", json_schema: LINE_SCHEMA },
          }),
          new Promise<null>((resolve) => setTimeout(() => resolve(null), LINE_TIMEOUT_MS)),
        ]);
        if (!out) {
          span.setAttribute("timeout", true);
          Sentry.logger.warn("workers ai line timed out, using bank", { beat });
          return { line: fallback, mood: "smug", source: "bank" };
        }
        const raw = (out as { response?: unknown }).response;
        const obj = typeof raw === "string" ? safeParse(raw) : ((raw as Record<string, unknown> | undefined) ?? {});
        const g = sanitizeLine(String(obj.line ?? ""), fallback);
        span.setAttribute("gen_ai.response.text", g.line);
        if (!g.ok) Sentry.logger.warn("workers ai line rejected by guard", { beat, reason: g.reason });
        return { line: g.line, mood: String(obj.mood ?? "smug"), source: g.ok ? "workersai" : "bank" };
      } catch (e) {
        Sentry.captureException(e);
        Sentry.logger.error("workers ai line failed, using bank", { beat, error: String(e) });
        return { line: fallback, mood: "smug", source: "bank" };
      }
    },
  );
}
