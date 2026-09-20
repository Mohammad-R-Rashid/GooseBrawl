import type { Env, Beat, EventPayload } from "./env";
import { mockText } from "./env";
import { writeLine, transcribe } from "./tools/openai";
import { llamaLine, whisper, workersAiOn } from "./tools/workersai";
import { bankLine } from "./bank";

/**
 * Beats the Workers AI writer takes on: the ones where the Elastic case file has something to say and the phone's
 * deadline has room (the intro is pre-voiced, yell waits 3.5 s, the round-end lines play under the slow-mo). The quick
 * mid-chase beats stay on the bank: pre-voiced, cached, back in well under the phone's 2.5 s.
 */
const WORKERS_AI_BEATS: Beat[] = ["intro", "intro_again", "yell", "caught", "outlasted"];

/** Who writes the goose's words: OpenAI when its key is set, else Llama on Workers AI, else the script bank. */
export function writerName(env: Env): "openai" | "workersai" | "bank" {
  if (!mockText(env)) return "openai";
  if (workersAiOn(env)) return "workersai";
  return "bank";
}

export async function writeAnyLine(env: Env, beat: Beat, payload: EventPayload, memory: Record<string, unknown>, fallbackIndex: number): Promise<{ line: string; mood: string; source: string }> {
  switch (writerName(env)) {
    case "openai":
      return writeLine(env, beat, payload, memory, fallbackIndex);
    case "workersai":
      if (!WORKERS_AI_BEATS.includes(beat)) return { line: bankLine(beat, fallbackIndex), mood: "smug", source: "bank" };
      return llamaLine(env, beat, payload, memory, fallbackIndex);
    default:
      return { line: bankLine(beat, fallbackIndex), mood: "smug", source: "bank" };
  }
}

/** The ears: OpenAI transcription with its key, Whisper on Workers AI without it (multilingual, language detected). */
export async function transcribeAny(env: Env, wav: ArrayBuffer, gooseName: string): Promise<{ text: string; lang?: string }> {
  switch (writerName(env)) {
    case "openai":
      return { text: await transcribe(env, wav, gooseName), lang: "en" };
    case "workersai":
      return whisper(env, wav, gooseName);
    default:
      return { text: "" };
  }
}
