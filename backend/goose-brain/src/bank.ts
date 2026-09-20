import lines from "../scripts/lines.json";
import type { Beat } from "./env";

/** Offline / fallback lines. Mirrors Assets/Scripts/Core/GooseLines.cs (exported by Goose Brawl > Export Voice Lines). */
export type Voice = "male" | "female";
export const PERSONAS: { name: string; title: string; voice: Voice }[] = (lines.personas as { name: string; title: string; voice?: string }[]).map((p) => ({ name: p.name, title: p.title, voice: p.voice === "female" ? "female" : "male" }));
export const BEAT_LINES: Record<Beat, string[]> = lines.beats as Record<Beat, string[]>;

export function bankLine(beat: Beat, index: number): string {
  const arr = BEAT_LINES[beat] ?? ["HONK."];
  return arr[((index % arr.length) + arr.length) % arr.length];
}

export function bankPersona(index: number): { name: string; title: string; voice: Voice } {
  return PERSONAS[((index % PERSONAS.length) + PERSONAS.length) % PERSONAS.length];
}

/** The voice a bank name owns (undefined for names the model invented). */
export function personaVoice(name: string | undefined): Voice | undefined {
  if (!name) return undefined;
  const n = name.trim().toUpperCase();
  return PERSONAS.find((p) => p.name.toUpperCase() === n)?.voice;
}
