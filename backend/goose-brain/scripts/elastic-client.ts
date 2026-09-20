// Shared bits for the Elastic scripts: env loading (.dev.vars), a tiny REST client for Elasticsearch and Kibana, and the
// index / data-stream definitions (kept next to the document shapes in src/tools/elastic.ts).
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

export function loadDevVars(): void {
  const p = resolve(process.cwd(), ".dev.vars");
  if (!existsSync(p)) return;
  for (const raw of readFileSync(p, "utf8").split("\n")) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const i = line.indexOf("=");
    if (i < 0) continue;
    const k = line.slice(0, i).trim();
    const v = line.slice(i + 1).trim();
    if (!process.env[k]) process.env[k] = v;
  }
  // Non-secret vars live in wrangler.jsonc; read them too so the scripts need no duplication.
  const w = resolve(process.cwd(), "wrangler.jsonc");
  if (existsSync(w)) {
    const text = readFileSync(w, "utf8");
    for (const k of ["ELASTIC_URL", "ELASTIC_KIBANA_URL", "ELASTIC_AGENT_ID", "ELASTIC_RERANK_ID", "WARM_KEY"]) {
      const m = text.match(new RegExp(`"${k}"\\s*:\\s*"([^"]*)"`));
      if (m && !process.env[k]) process.env[k] = m[1];
    }
  }
}

export function need(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`${name} is not set (put it in .dev.vars or the environment)`);
  return v;
}

export interface Reply<T = unknown> {
  status: number;
  ok: boolean;
  body: T;
  text: string;
}

async function call<T>(base: string, path: string, method: string, body: unknown, headers: Record<string, string>, timeoutMs: number): Promise<Reply<T>> {
  const ndjson = typeof body === "string";
  const res = await fetch(base.replace(/\/+$/, "") + path, {
    method,
    headers: { authorization: `ApiKey ${need("ELASTIC_API_KEY")}`, "content-type": ndjson ? "application/x-ndjson" : "application/json", accept: "application/json", ...headers },
    body: body === undefined ? undefined : ndjson ? (body as string) : JSON.stringify(body),
    signal: AbortSignal.timeout(timeoutMs),
  });
  const text = await res.text();
  let parsed: unknown = text;
  try {
    parsed = text ? JSON.parse(text) : {};
  } catch {
    /* keep text */
  }
  return { status: res.status, ok: res.ok, body: parsed as T, text };
}

export const es = <T = unknown>(path: string, method = "GET", body?: unknown, timeoutMs = 30000) => call<T>(need("ELASTIC_URL"), path, method, body, {}, timeoutMs);
export const kb = <T = unknown>(path: string, method = "GET", body?: unknown, timeoutMs = 60000) => call<T>(need("ELASTIC_KIBANA_URL"), path, method, body, { "kbn-xsrf": "true" }, timeoutMs);

export function show(label: string, r: Reply): void {
  const brief = r.text.length > 400 ? r.text.slice(0, 400) + "…" : r.text;
  console.log(`${r.ok ? "ok " : "ERR"} ${r.status} ${label}${r.ok ? "" : ": " + brief}`);
}

// ------------------------------------------------------------------ index definitions

const keyword = { type: "keyword" };
const textK = { type: "text", fields: { keyword: { type: "keyword", ignore_above: 256 } } };

export function shoutsMapping(embedId?: string) {
  return {
    properties: {
      "@timestamp": { type: "date" },
      device_id: keyword,
      goose: keyword,
      transcript: textK,
      transcript_semantic: embedId ? { type: "semantic_text", inference_id: embedId } : { type: "semantic_text" },
      lang: keyword,
      survival_at: { type: "float" },
      tier: { type: "integer" },
      round: { type: "integer" },
      reply: { type: "text" },
      source: keyword,
      seed: { type: "boolean" },
    },
  };
}

export function linesMapping(embedId?: string) {
  return {
    properties: {
      "@timestamp": { type: "date" },
      device_id: keyword,
      goose: keyword,
      beat: keyword,
      line: textK,
      line_semantic: embedId ? { type: "semantic_text", inference_id: embedId } : { type: "semantic_text" },
      mood: keyword,
      source: keyword,
      survival_at: { type: "float" },
      tier: { type: "integer" },
      round: { type: "integer" },
      seed: { type: "boolean" },
    },
  };
}

export const runsMapping = {
  properties: {
    "@timestamp": { type: "date" },
    device_id: keyword,
    goose: keyword,
    outcome: keyword,
    survival: { type: "float" },
    honks: { type: "integer" },
    dodges: { type: "integer" },
    breads: { type: "integer" },
    tier_max: { type: "integer" },
    round: { type: "integer" },
    grudge: { type: "integer" },
    last_shout: textK,
    seed: { type: "boolean" },
  },
};

export const playersMapping = {
  properties: {
    device_id: keyword,
    mercy: { type: "boolean" },
    reason: keyword,
    note: { type: "text" },
    fast_losses: { type: "integer" },
    updated: { type: "date" },
  },
};

const xyz = { properties: { x: { type: "float" }, y: { type: "float" }, z: { type: "float" } } };

/** The AR sensor stream: a time-series data stream (dimensions: device + round; gauges: everything measured). */
export function telemetryTemplate(mode: "tsds" | "plain") {
  const dim = mode === "tsds" ? { type: "keyword", time_series_dimension: true } : keyword;
  const gauge = (type: string) => (mode === "tsds" ? { type, time_series_metric: "gauge" } : { type });
  return {
    index_patterns: ["goosed-telemetry*"],
    data_stream: {},
    priority: 500,
    template: {
      settings: mode === "tsds" ? { "index.mode": "time_series", "index.routing_path": ["device_id", "round_id"] } : {},
      mappings: {
        properties: {
          "@timestamp": { type: "date" },
          device_id: dim,
          round_id: dim,
          goose: keyword,
          state: keyword,
          t: { type: "float" },
          player: xyz,
          goose_pos: xyz,
          player_pt: { type: "point" },
          goose_pt: { type: "point" },
          dist: gauge("double"),
          player_speed: gauge("double"),
          goose_speed: gauge("double"),
          tier: gauge("integer"),
          anger: gauge("integer"),
          fps: gauge("double"),
          frame_ms: gauge("double"),
          gpu_ms: gauge("double"),
          thermal: gauge("integer"),
        },
      },
    },
  };
}

export const INDICES = ["goosed-shouts", "goosed-lines", "goosed-runs", "goosed-players"] as const;
export const TELEMETRY = "goosed-telemetry";
