import * as Sentry from "@sentry/cloudflare";
import type { Env } from "../env";

/**
 * Elasticsearch is the goose's context layer: every shout, every line, every finished round and a 5 Hz AR sensor
 * stream land here (write path, always off the critical path), and the brain reads it back as ES|QL aggregations
 * and hybrid (BM25 + Jina dense + rerank) search when it writes a line. Indices are created by scripts/elastic-setup.ts.
 */
export const IDX = {
  shouts: "goosed-shouts", // what players yelled: text (BM25) + semantic_text (Jina, multilingual)
  lines: "goosed-lines", // what the goose said, per beat
  runs: "goosed-runs", // one document per finished round
  players: "goosed-players", // flags written by the Elastic Workflow (mercy, notes)
  telemetry: "goosed-telemetry", // the AR sensor stream (time-series data stream)
} as const;

export function elasticOn(env: Env): boolean {
  return Boolean(env.ELASTIC_URL && env.ELASTIC_API_KEY);
}

export class ElasticError extends Error {
  constructor(public status: number, public body: string, path: string) {
    super(`elasticsearch ${status} on ${path}: ${body.slice(0, 300)}`);
  }
}

function base(url: string): string {
  return url.replace(/\/+$/, "");
}

/** One Elasticsearch REST call as a Sentry span. A string body is sent as NDJSON (the bulk API). */
export async function es<T = Record<string, unknown>>(env: Env, method: string, path: string, body?: unknown, timeoutMs = 2500): Promise<T> {
  return Sentry.startSpan(
    { op: "db.query", name: `${method} ${path.split("?")[0]}`, attributes: { "db.system.name": "elasticsearch", "server.address": env.ELASTIC_URL ?? "" } },
    async (span) => {
      const ndjson = typeof body === "string";
      const res = await fetch(base(env.ELASTIC_URL ?? "") + path, {
        method,
        headers: {
          authorization: `ApiKey ${env.ELASTIC_API_KEY ?? ""}`,
          "content-type": ndjson ? "application/x-ndjson" : "application/json",
          accept: "application/json",
        },
        body: body === undefined ? undefined : ndjson ? (body as string) : JSON.stringify(body),
        signal: AbortSignal.timeout(timeoutMs),
      });
      span.setAttribute("http.response.status_code", res.status);
      const text = await res.text();
      if (!res.ok) throw new ElasticError(res.status, text, path);
      return (text ? JSON.parse(text) : {}) as T;
    },
  );
}

export interface BulkDoc {
  index: string;
  id?: string;
  doc: Record<string, unknown>;
}

interface BulkResponse {
  errors: boolean;
  items: { create?: { status: number; error?: unknown } }[];
}

/** Bulk-create documents (works for plain indices and data streams). Never throws on per-document errors. */
export async function bulk(env: Env, docs: BulkDoc[], timeoutMs = 5000): Promise<{ indexed: number; errors: number }> {
  if (docs.length === 0) return { indexed: 0, errors: 0 };
  const lines: string[] = [];
  for (const d of docs) {
    lines.push(JSON.stringify({ create: d.id ? { _index: d.index, _id: d.id } : { _index: d.index } }));
    lines.push(JSON.stringify(d.doc));
  }
  const res = await es<BulkResponse>(env, "POST", "/_bulk", lines.join("\n") + "\n", timeoutMs);
  const failed = res.items.filter((i) => (i.create?.status ?? 500) >= 300);
  if (failed.length) {
    Sentry.logger.warn("elastic bulk had errors", { errors: failed.length, sample: JSON.stringify(failed[0].create?.error ?? "").slice(0, 300) });
  }
  return { indexed: docs.length - failed.length, errors: failed.length };
}

/** Fire-and-forget indexing for the request path: logs, never throws, never awaited by the caller. */
export async function indexQuietly(env: Env, docs: BulkDoc[]): Promise<void> {
  if (!elasticOn(env) || docs.length === 0) return;
  try {
    const r = await bulk(env, docs);
    Sentry.logger.info("elastic indexed", { indexed: r.indexed, errors: r.errors, index: docs[0].index });
  } catch (e) {
    Sentry.logger.warn("elastic index failed", { error: String(e), index: docs[0].index });
  }
}

interface EsqlResponse {
  columns: { name: string; type: string }[];
  values: unknown[][];
}

/** ES|QL with named parameters (`?device`), rows as objects. */
export async function esql<T extends Record<string, unknown> = Record<string, unknown>>(env: Env, query: string, params?: Record<string, unknown>, timeoutMs = 1500): Promise<T[]> {
  const body: Record<string, unknown> = { query };
  if (params) body.params = Object.entries(params).map(([k, v]) => ({ [k]: v }));
  const res = await es<EsqlResponse>(env, "POST", "/_query?format=json", body, timeoutMs);
  return res.values.map((row) => Object.fromEntries(row.map((v, i) => [res.columns[i].name, v])) as T);
}

export interface ShoutHit {
  transcript: string;
  lang?: string;
  device_id: string;
  goose: string;
  at: string;
}

interface SearchResponse {
  hits: { hits: { _source: { transcript: string; lang?: string; device_id: string; goose: string; "@timestamp": string } }[] };
}

/**
 * Hybrid search over what everyone has shouted: BM25 on the transcript + Jina dense vectors on the semantic field, fused
 * with RRF, optionally reranked. Multilingual on purpose: "va-t'en" and "go away" land in the same neighbourhood.
 */
export async function similarShouts(env: Env, text: string, opts: { excludeDevice?: string; k?: number; rerankId?: string; timeoutMs?: number } = {}): Promise<ShoutHit[]> {
  const k = opts.k ?? 3;
  const filter = opts.excludeDevice ? [{ bool: { must_not: { term: { device_id: opts.excludeDevice } } } }] : [];
  const rrf = {
    rrf: {
      retrievers: [
        { standard: { query: { match: { transcript: { query: text } } }, filter } },
        { standard: { query: { semantic: { field: "transcript_semantic", query: text } }, filter } },
      ],
      rank_window_size: 20,
      rank_constant: 60,
    },
  };
  const reranked = opts.rerankId
    ? { text_similarity_reranker: { retriever: rrf, field: "transcript", inference_id: opts.rerankId, inference_text: text, rank_window_size: 10 } }
    : rrf;
  const run = (retriever: unknown) =>
    es<SearchResponse>(env, "POST", `/${IDX.shouts}/_search`, { size: k, _source: ["transcript", "lang", "device_id", "goose", "@timestamp"], retriever }, opts.timeoutMs ?? 2000);
  let res: SearchResponse;
  try {
    res = await run(reranked);
  } catch (e) {
    if (!opts.rerankId) throw e;
    Sentry.logger.warn("rerank failed, rrf only", { error: String(e) });
    res = await run(rrf);
  }
  return res.hits.hits.map((h) => ({ transcript: h._source.transcript, lang: h._source.lang, device_id: h._source.device_id, goose: h._source.goose, at: h._source["@timestamp"] }));
}

/**
 * Ask the Elastic Agent Builder agent (the goose's intelligence officer) for its own read of the data. It decides which
 * tools to call (ES|QL stats, shout search, line search); this takes seconds, so callers run it in the background.
 */
export async function converse(env: Env, input: string, timeoutMs = 25000): Promise<{ message: string; conversationId?: string; steps: number } | null> {
  if (!env.ELASTIC_KIBANA_URL || !env.ELASTIC_API_KEY || !env.ELASTIC_AGENT_ID) return null;
  return Sentry.startSpan(
    { op: "gen_ai.invoke_agent", name: "elastic agent builder", attributes: { "gen_ai.agent.name": env.ELASTIC_AGENT_ID, "gen_ai.system": "elastic", "gen_ai.request.messages": input.slice(0, 500) } },
    async (span) => {
      const res = await fetch(base(env.ELASTIC_KIBANA_URL ?? "") + "/api/agent_builder/converse", {
        method: "POST",
        headers: { authorization: `ApiKey ${env.ELASTIC_API_KEY ?? ""}`, "kbn-xsrf": "true", "content-type": "application/json" },
        body: JSON.stringify({ agent_id: env.ELASTIC_AGENT_ID, input }),
        signal: AbortSignal.timeout(timeoutMs),
      });
      const text = await res.text();
      if (!res.ok) throw new ElasticError(res.status, text, "/api/agent_builder/converse");
      const data = JSON.parse(text) as { conversation_id?: string; steps?: unknown[]; response?: { message?: string } };
      const steps = data.steps?.length ?? 0;
      span.setAttribute("steps", steps);
      span.setAttribute("gen_ai.response.text", (data.response?.message ?? "").slice(0, 500));
      return { message: data.response?.message ?? "", conversationId: data.conversation_id, steps };
    },
  );
}

// ------------------------------------------------------------------ document shapes (one place, so the seed script matches)

export function shoutDoc(p: { deviceId: string; goose: string; transcript: string; lang?: string; survival?: number; tier?: number; round: number; reply?: string; source?: string; at?: number; seed?: boolean }): BulkDoc {
  return {
    index: IDX.shouts,
    doc: {
      "@timestamp": new Date(p.at ?? Date.now()).toISOString(),
      device_id: p.deviceId,
      goose: p.goose,
      transcript: p.transcript,
      transcript_semantic: p.transcript,
      lang: p.lang ?? "und",
      survival_at: p.survival ?? 0,
      tier: p.tier ?? 0,
      round: p.round,
      reply: p.reply ?? "",
      source: p.source ?? "phone",
      seed: p.seed ?? false,
    },
  };
}

export function lineDoc(p: { deviceId: string; goose: string; beat: string; line: string; mood: string; source: string; survival?: number; tier?: number; round: number; at?: number; seed?: boolean }): BulkDoc {
  return {
    index: IDX.lines,
    doc: {
      "@timestamp": new Date(p.at ?? Date.now()).toISOString(),
      device_id: p.deviceId,
      goose: p.goose,
      beat: p.beat,
      line: p.line,
      line_semantic: p.line,
      mood: p.mood,
      source: p.source,
      survival_at: p.survival ?? 0,
      tier: p.tier ?? 0,
      round: p.round,
      seed: p.seed ?? false,
    },
  };
}

export function runDoc(p: { deviceId: string; goose: string; outcome: "GOOSED" | "OUTLASTED"; survival: number; honks?: number; dodges?: number; breads?: number; tier?: number; round: number; grudge: number; lastShout?: string; at?: number; seed?: boolean }): BulkDoc {
  return {
    index: IDX.runs,
    doc: {
      "@timestamp": new Date(p.at ?? Date.now()).toISOString(),
      device_id: p.deviceId,
      goose: p.goose,
      outcome: p.outcome,
      survival: p.survival,
      honks: p.honks ?? 0,
      dodges: p.dodges ?? 0,
      breads: p.breads ?? 0,
      tier_max: p.tier ?? 0,
      round: p.round,
      grudge: p.grudge,
      last_shout: p.lastShout ?? "",
      seed: p.seed ?? false,
    },
  };
}
