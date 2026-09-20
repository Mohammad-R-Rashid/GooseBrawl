import * as Sentry from "@sentry/cloudflare";
import type { Env } from "./env";
import { IDX, converse, elasticOn, esql, similarShouts, type ShoutHit } from "./tools/elastic";

/**
 * The goose's case file on a player, built from Elasticsearch: crowd statistics (every player who faced a goose today),
 * this player's history and movement profile (the AR sensor stream), what the crowd has been shouting, and, when the
 * Agent Builder agent has had time, its own written notes. Read at session start (fast tier, parallel ES|QL, under a
 * second) and refreshed in the background (deep tier, the agent, seconds); the beats only read the cached copy.
 */
export interface Intel {
  at: number;
  crowd: { runs: number; players: number; avgSeconds: number; bestSeconds: number; goosedPct: number } | null;
  you: { runs: number; avgSeconds: number; bestSeconds: number; ahead: number | null; avgSpeed: number | null; maxSpeed: number | null; stillPct: number | null; avgDistance: number | null } | null;
  crowdShouts: { text: string; lang: string }[];
  yourShouts: string[];
  /** Filled at yell time: what someone else shouted that resembles this one (hybrid search). */
  echo: { text: string; lang: string; goose: string } | null;
  /** The Agent Builder agent's notes, when it has answered. */
  deep: string | null;
  /** Written by the Elastic Workflow: the player has been crushed so often the goose is told to go easy on the words. */
  mercy: boolean;
}

export const EMPTY_INTEL: Intel = { at: 0, crowd: null, you: null, crowdShouts: [], yourShouts: [], echo: null, deep: null, mercy: false };

const num = (v: unknown): number | null => (typeof v === "number" && Number.isFinite(v) ? v : null);

async function quiet<T>(name: string, p: Promise<T>): Promise<T | null> {
  try {
    return await p;
  } catch (e) {
    Sentry.logger.warn("intel query failed", { query: name, error: String(e).slice(0, 300) });
    return null;
  }
}

/** Fast tier: five ES|QL queries in parallel, each one optional. Never throws. */
export async function fastIntel(env: Env, deviceId: string, myBestSeconds: number, previous?: Intel | null): Promise<Intel | null> {
  if (!elasticOn(env)) return null;
  return Sentry.startSpan({ op: "function", name: "fastIntel" }, async (span) => {
    const started = Date.now();
    const [crowd, you, rank, move, crowdShouts, yourShouts, flags] = await Promise.all([
      quiet("crowd", esql(env, `FROM ${IDX.runs} | WHERE @timestamp > NOW() - 24 hours | STATS runs = COUNT(*), players = COUNT_DISTINCT(device_id), avg = AVG(survival), best = MAX(survival), goosed = SUM(CASE(outcome == "GOOSED", 1, 0))`)),
      quiet("you", esql(env, `FROM ${IDX.runs} | WHERE device_id == ?device | STATS runs = COUNT(*), avg = AVG(survival), best = MAX(survival)`, { device: deviceId })),
      quiet("rank", esql(env, `FROM ${IDX.runs} | WHERE @timestamp > NOW() - 24 hours | STATS best = MAX(survival) BY device_id | WHERE best > ?mine | STATS ahead = COUNT(*)`, { mine: myBestSeconds })),
      quiet("move", esql(env, `FROM ${IDX.telemetry} | WHERE device_id == ?device AND @timestamp > NOW() - 7 days | STATS avgSpeed = AVG(player_speed), maxSpeed = MAX(player_speed), still = SUM(CASE(player_speed < 0.15, 1, 0)), n = COUNT(*), avgDist = AVG(dist)`, { device: deviceId })),
      quiet("crowdShouts", esql(env, `FROM ${IDX.shouts} | WHERE @timestamp > NOW() - 24 hours AND device_id != ?device | SORT @timestamp DESC | KEEP transcript, lang | LIMIT 3`, { device: deviceId })),
      quiet("yourShouts", esql(env, `FROM ${IDX.shouts} | WHERE device_id == ?device | SORT @timestamp DESC | KEEP transcript | LIMIT 3`, { device: deviceId })),
      quiet("flags", esql(env, `FROM ${IDX.players} | WHERE device_id == ?device | KEEP mercy | LIMIT 1`, { device: deviceId })),
    ]);
    const c = crowd?.[0];
    const y = you?.[0];
    const m = move?.[0];
    const n = num(m?.n) ?? 0;
    const intel: Intel = {
      at: Date.now(),
      crowd: c && (num(c.runs) ?? 0) > 0 ? { runs: num(c.runs) ?? 0, players: num(c.players) ?? 0, avgSeconds: num(c.avg) ?? 0, bestSeconds: num(c.best) ?? 0, goosedPct: Math.round(100 * ((num(c.goosed) ?? 0) / Math.max(1, num(c.runs) ?? 1))) } : null,
      you: y && (num(y.runs) ?? 0) > 0
        ? {
            runs: num(y.runs) ?? 0,
            avgSeconds: num(y.avg) ?? 0,
            bestSeconds: num(y.best) ?? 0,
            ahead: num(rank?.[0]?.ahead),
            avgSpeed: n > 0 ? num(m?.avgSpeed) : null,
            maxSpeed: n > 0 ? num(m?.maxSpeed) : null,
            stillPct: n > 0 ? Math.round(100 * ((num(m?.still) ?? 0) / n)) : null,
            avgDistance: n > 0 ? num(m?.avgDist) : null,
          }
        : null,
      crowdShouts: (crowdShouts ?? []).map((r) => ({ text: String(r.transcript ?? ""), lang: String(r.lang ?? "und") })).filter((s) => s.text),
      yourShouts: (yourShouts ?? []).map((r) => String(r.transcript ?? "")).filter(Boolean),
      echo: null,
      deep: previous?.deep ?? null,
      mercy: Boolean(flags?.[0]?.mercy),
    };
    span.setAttribute("ms", Date.now() - started);
    span.setAttribute("crowd_runs", intel.crowd?.runs ?? 0);
    Sentry.logger.info("intel fast", { ms: Date.now() - started, crowdRuns: intel.crowd?.runs ?? 0, yourRuns: intel.you?.runs ?? 0, crowdShouts: intel.crowdShouts.length, mercy: intel.mercy });
    return intel;
  });
}

/** Yell time: who else said something like this? One hybrid query, inside the beat's time budget. */
export async function findEcho(env: Env, transcript: string, deviceId: string, timeoutMs = 1800): Promise<Intel["echo"]> {
  if (!elasticOn(env) || transcript.trim().length < 2) return null;
  try {
    const hits: ShoutHit[] = await similarShouts(env, transcript, { excludeDevice: deviceId, k: 1, rerankId: env.ELASTIC_RERANK_ID || undefined, timeoutMs });
    const h = hits[0];
    if (!h) return null;
    Sentry.logger.info("intel echo", { transcript, echo: h.transcript, lang: h.lang ?? "und", goose: h.goose });
    return { text: h.transcript, lang: h.lang ?? "und", goose: h.goose };
  } catch (e) {
    Sentry.logger.warn("intel echo failed", { error: String(e).slice(0, 300) });
    return null;
  }
}

/** Deep tier: the Agent Builder agent reads the indices itself and writes three sentences. Background only. */
export async function deepIntel(env: Env, deviceId: string, gooseName: string): Promise<string | null> {
  if (!env.ELASTIC_AGENT_ID) return null;
  const started = Date.now();
  try {
    const r = await converse(env, `Build the case file for player device_id "${deviceId}" (the goose is ${gooseName || "KEVIN"}). Use the tools; do not guess.`);
    if (!r || !r.message) return null;
    const notes = r.message.replace(/\s+/g, " ").trim().slice(0, 600);
    Sentry.logger.info("intel deep", { ms: Date.now() - started, steps: r.steps, notes });
    return notes;
  } catch (e) {
    Sentry.logger.warn("intel deep failed", { ms: Date.now() - started, error: String(e).slice(0, 300) });
    return null;
  }
}

const s1 = (v: number | null | undefined) => (v === null || v === undefined ? undefined : Math.round(v * 10) / 10);

/** The compact, prose-friendly view the writer sees (numbers already rounded; empty parts omitted). */
export function intelForPrompt(intel: Intel | null | undefined): Record<string, unknown> | undefined {
  if (!intel || (!intel.crowd && !intel.you && intel.crowdShouts.length === 0 && !intel.echo && !intel.deep)) return undefined;
  const out: Record<string, unknown> = {};
  if (intel.crowd) out.crowdToday = { humansFaced: intel.crowd.players, rounds: intel.crowd.runs, averageSeconds: s1(intel.crowd.avgSeconds), bestSeconds: s1(intel.crowd.bestSeconds), percentGoosed: intel.crowd.goosedPct };
  if (intel.you) {
    out.thisPlayer = {
      roundsOnRecord: intel.you.runs,
      averageSeconds: s1(intel.you.avgSeconds),
      playersAheadOfThem: intel.you.ahead ?? undefined,
      averageSpeedMetersPerSecond: s1(intel.you.avgSpeed),
      topSpeedMetersPerSecond: s1(intel.you.maxSpeed),
      percentOfTimeStandingStill: intel.you.stillPct ?? undefined,
      averageDistanceKeptMeters: s1(intel.you.avgDistance),
    };
  }
  if (intel.yourShouts.length) out.thingsThisPlayerShoutedBefore = intel.yourShouts;
  if (intel.crowdShouts.length) out.thingsOtherPlayersShoutedToday = intel.crowdShouts.map((s) => (s.lang && s.lang !== "und" && s.lang !== "en" ? `${s.text} (${s.lang})` : s.text));
  if (intel.echo) out.someoneElseShoutedSomethingSimilar = { text: intel.echo.text, language: intel.echo.lang, toGoose: intel.echo.goose };
  if (intel.deep) out.caseFileNotes = intel.deep;
  if (intel.mercy) out.mercy = "This player keeps losing fast. Be smug, not cruel.";
  return out;
}
