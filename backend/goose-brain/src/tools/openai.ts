import OpenAI from "openai";
import * as Sentry from "@sentry/cloudflare";
import type { Env, Beat, EventPayload } from "../env";
import { mockText } from "../env";
import { CHARACTER_SHEET, LINE_SCHEMA, PERSONA_SHEET, PERSONA_SCHEMA, eventContext } from "../prompts";
import { bankLine, bankPersona } from "../bank";
import { sanitizeLine, sanitizeName } from "../guard";

function client(env: Env): OpenAI {
  const c = new OpenAI({ apiKey: env.OPENAI_API_KEY, timeout: 6000, maxRetries: 0 });
  return Sentry.instrumentOpenAiClient(c, { recordInputs: true, recordOutputs: true });
}

/** The Responses API message text, wherever it sits in the output list (reasoning items may precede it). */
function extractText(res: unknown): string {
  const r = res as { output_text?: string; output?: { type: string; content?: { type: string; text?: string }[] }[] };
  if (typeof r.output_text === "string" && r.output_text) return r.output_text;
  const msg = r.output?.find((o) => o.type === "message");
  const part = msg?.content?.find((c) => c.type === "output_text");
  return part?.text ?? "";
}

async function structured(env: Env, instructions: string, input: string, name: string, schema: object, maxTokens: number): Promise<Record<string, unknown>> {
  const c = client(env);
  const base = {
    model: env.OPENAI_MODEL || "gpt-5.6-luna",
    instructions,
    input,
    max_output_tokens: maxTokens,
    text: { format: { type: "json_schema", name, strict: true, schema } },
  };
  let res: unknown;
  try {
    res = await c.responses.create({ ...base, reasoning: { effort: "none" } } as never);
  } catch (e) {
    // Older / different models reject reasoning.effort=none: retry once without it.
    Sentry.logger.warn("openai responses retry without reasoning", { error: String(e) });
    res = await c.responses.create(base as never);
  }
  const text = extractText(res);
  try {
    return JSON.parse(text) as Record<string, unknown>;
  } catch {
    return { line: text };
  }
}

export async function writeLine(env: Env, beat: Beat, payload: EventPayload, memory: Record<string, unknown>, fallbackIndex: number): Promise<{ line: string; mood: string; source: string }> {
  const fallback = bankLine(beat, fallbackIndex);
  if (mockText(env)) return { line: fallback, mood: "smug", source: "bank" };
  try {
    const out = await structured(env, CHARACTER_SHEET, JSON.stringify(eventContext(beat, payload, memory)), "goose_line", LINE_SCHEMA, 120);
    const g = sanitizeLine(String(out.line ?? ""), fallback);
    if (!g.ok) Sentry.logger.warn("goose line rejected by guard", { beat, reason: g.reason, raw: String(out.line ?? "").slice(0, 120) });
    return { line: g.line, mood: String(out.mood ?? "smug"), source: g.ok ? "openai" : "bank" };
  } catch (e) {
    Sentry.captureException(e);
    Sentry.logger.error("openai writeLine failed, using bank", { beat, error: String(e) });
    return { line: fallback, mood: "smug", source: "bank" };
  }
}

export async function namePersona(env: Env, gamesPlayed: number): Promise<{ name: string; title: string; voice: "male" | "female"; source: string }> {
  const fb = bankPersona(gamesPlayed);
  if (mockText(env)) return { ...fb, source: "bank" };
  try {
    const out = await structured(env, PERSONA_SHEET, JSON.stringify({ gamesPlayed, avoid: ["KEVIN", "GARY"].slice(0, gamesPlayed % 3) }), "goose_persona", PERSONA_SCHEMA, 60);
    const name = sanitizeName(String(out.name ?? ""), fb.name);
    const title = sanitizeLine(String(out.title ?? ""), fb.title).line.toUpperCase().replace(/[.]+$/, "");
    const voice = out.voice === "female" ? "female" : "male";
    return { name, title: title.length > 40 ? fb.title : title, voice, source: "openai" };
  } catch (e) {
    Sentry.captureException(e);
    return { ...fb, source: "bank" };
  }
}

/** What did the player shout? WAV in, text out. */
export async function transcribe(env: Env, wav: ArrayBuffer, gooseName: string): Promise<string> {
  if (mockText(env)) return "";
  return Sentry.startSpan(
    { op: "gen_ai.execute_tool", name: "transcribe", attributes: { "gen_ai.tool.name": "openai.transcriptions", "gen_ai.request.model": env.OPENAI_TRANSCRIBE_MODEL, audio_bytes: wav.byteLength } },
    async (span) => {
      try {
        const c = client(env);
        const file = new File([wav], "yell.wav", { type: "audio/wav" });
        const model = env.OPENAI_TRANSCRIBE_MODEL || "gpt-transcribe";
        const params: Record<string, unknown> = {
          file,
          model,
          prompt: `A person shouting at a goose named ${gooseName}. Loud, short, informal. Words like: go away, shoo, stop, leave me alone, honk, ${gooseName}.`,
          response_format: "json",
        };
        if (model === "gpt-transcribe") params.languages = ["en"]; else params.language = "en";
        const r = (await c.audio.transcriptions.create(params as never)) as { text?: string };
        const text = (r.text ?? "").trim();
        span.setAttribute("transcript", text.slice(0, 120));
        return text;
      } catch (e) {
        Sentry.captureException(e);
        Sentry.logger.error("transcription failed", { error: String(e) });
        return "";
      }
    },
  );
}
