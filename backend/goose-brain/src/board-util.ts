import { BANNED } from "./guard";

export const DAY_MS = 86_400_000;
export const NAME_FALLBACK = "SOMEONE";

/** Player names on the board: A-Z, 0-9 and single spaces, at most 12 characters, nothing a judge would mind reading out loud. */
export function cleanPlayerName(raw: unknown, fallback = NAME_FALLBACK): string {
  const s = String(raw ?? "").toUpperCase().replace(/[^A-Z0-9 ]/g, "").replace(/\s+/g, " ").trim().slice(0, 12).trim();
  if (!s) return fallback;
  for (const re of BANNED) if (re.test(s)) return fallback;
  return s;
}

export function cleanGooseName(raw: unknown): string {
  const s = String(raw ?? "").toUpperCase().replace(/[^A-Z0-9 .'\-]/g, "").replace(/\s+/g, " ").trim().slice(0, 22);
  return s || "GOOSE";
}

export function clampSeconds(v: unknown): number {
  const n = Number(v);
  if (!Number.isFinite(n) || n < 0) return 0;
  return Math.round(Math.min(n, 600) * 10) / 10;
}

export function clampCount(v: unknown): number {
  const n = Math.floor(Number(v));
  return Number.isFinite(n) && n > 0 ? Math.min(n, 9999) : 0;
}

export type Outcome = "GOOSED" | "OUTLASTED";
export function outcomeOf(v: unknown): Outcome {
  return String(v ?? "").toUpperCase() === "OUTLASTED" ? "OUTLASTED" : "GOOSED";
}

/** 1 + the number of runs that beat this one (ties share a rank). */
export function rankOf(seconds: number, others: number[]): number {
  let better = 0;
  for (const o of others) if (o > seconds) better++;
  return 1 + better;
}
