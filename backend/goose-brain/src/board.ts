import { Agent } from "agents";
import * as Sentry from "@sentry/cloudflare";
import type { Env } from "./env";
import { DAY_MS, cleanPlayerName, cleanGooseName, clampSeconds, clampCount, outcomeOf, rankOf } from "./board-util";

export interface BoardState { latestDevice: string; latestAt: number }

export interface RunInput { deviceId?: string; name?: string; seconds?: number; outcome?: string; gooseName?: string; honks?: number; dodges?: number }
export interface RunRow { rank: number; name: string; seconds: number; outcome: string; goose: string; honks: number; dodges: number; at: number }
type RunSql = { name: string; seconds: number; outcome: string; goose: string | null; honks: number | null; dodges: number | null; at: number };

/**
 * The Goose Board: one global Agent (Durable Object + SQLite) holding every run posted in the last 24 hours and the
 * device that is playing right now (so the booth page's brain panel follows whoever holds the phone).
 */
export class GooseBoardBase extends Agent<Env, BoardState> {
  initialState: BoardState = { latestDevice: "", latestAt: 0 };

  async onStart() {
    this.ensureTables();
  }

  private ensureTables() {
    this.sql`CREATE TABLE IF NOT EXISTS runs (id INTEGER PRIMARY KEY AUTOINCREMENT, device TEXT, name TEXT NOT NULL, seconds REAL NOT NULL, outcome TEXT NOT NULL, goose TEXT, honks INTEGER, dodges INTEGER, at INTEGER NOT NULL)`;
    this.sql`CREATE INDEX IF NOT EXISTS runs_at ON runs(at)`;
  }

  /** Same surface over plain fetch, for callers without RPC typing: POST /run, GET /top?n=, POST /touch. */
  async onRequest(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "");
    try {
      if (request.method === "POST" && path.endsWith("/run")) return Response.json(await this.addRun((await request.json().catch(() => ({}))) as RunInput));
      if (request.method === "GET" && path.endsWith("/top")) return Response.json(await this.top(Number(url.searchParams.get("n") ?? 10)));
      if (request.method === "POST" && path.endsWith("/touch")) { const b = (await request.json().catch(() => ({}))) as { deviceId?: string }; this.touch(String(b.deviceId ?? "")); return Response.json({ ok: true }); }
      return new Response("Not found", { status: 404 });
    } catch (e) {
      Sentry.captureException(e);
      return Response.json({ error: String(e) }, { status: 500 });
    }
  }

  /** A finished round. Returns the rank among today's runs (ties share a rank). */
  async addRun(input: RunInput): Promise<{ rank: number; total: number; name: string }> {
    this.ensureTables();
    const name = cleanPlayerName(input?.name);
    const seconds = clampSeconds(input?.seconds);
    const outcome = outcomeOf(input?.outcome);
    const goose = cleanGooseName(input?.gooseName);
    const honks = clampCount(input?.honks);
    const dodges = clampCount(input?.dodges);
    const device = String(input?.deviceId ?? "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 64);
    const now = Date.now();
    this.sql`INSERT INTO runs (device, name, seconds, outcome, goose, honks, dodges, at) VALUES (${device}, ${name}, ${seconds}, ${outcome}, ${goose}, ${honks}, ${dodges}, ${now})`;
    this.touch(device);
    const since = now - DAY_MS;
    const rows = this.sql<{ seconds: number }>`SELECT seconds FROM runs WHERE at > ${since}`;
    const others = rows.map((r) => Number(r.seconds));
    const result = { rank: rankOf(seconds, others), total: others.length, name };
    Sentry.logger.info("board run", { ...result, seconds, outcome, goose, device });
    return result;
  }

  /** Today's top n plus the device that most recently played. */
  async top(n: number): Promise<{ runs: RunRow[]; total: number; latestDevice: string }> {
    this.ensureTables();
    const limit = Math.min(50, Math.max(1, Math.floor(n) || 10));
    const since = Date.now() - DAY_MS;
    const rows = this.sql<RunSql>`SELECT name, seconds, outcome, goose, honks, dodges, at FROM runs WHERE at > ${since} ORDER BY seconds DESC, id ASC LIMIT ${limit}`;
    const count = this.sql<{ n: number }>`SELECT COUNT(*) AS n FROM runs WHERE at > ${since}`;
    const runs: RunRow[] = rows.map((r, i) => ({
      rank: i + 1, name: String(r.name), seconds: Number(r.seconds), outcome: String(r.outcome), goose: String(r.goose ?? ""),
      honks: Number(r.honks ?? 0), dodges: Number(r.dodges ?? 0), at: Number(r.at),
    }));
    return { runs, total: Number(count[0]?.n ?? 0), latestDevice: this.state.latestDevice };
  }

  /** Someone started (or finished) a round on this device: the brain panel follows them. */
  touch(deviceId: string): void {
    if (!deviceId) return;
    this.setState({ latestDevice: deviceId, latestAt: Date.now() });
  }
}
