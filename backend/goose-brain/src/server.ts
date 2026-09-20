import * as Sentry from "@sentry/cloudflare";
import { routeAgentRequest } from "agents";
import type { Env } from "./env";
import { GooseBrainBase } from "./agent";
import { GooseBoardBase } from "./board";
import { BOARD_HTML } from "./board-page";
import { serveWav, sha256Hex, audioKey, getCachedWav, putWav } from "./tools/r2";
import { speak, delivery } from "./tools/elevenlabs";
import { BEAT_LINES } from "./bank";
import { elasticOn } from "./tools/elastic";
import { ingestTelemetry } from "./telemetry";
import { elasticAlert } from "./alerts";
import { writerName } from "./writer";

const VERSION = "goose-brain@1.0.0";

const sentryOptions = (env: Env) => ({
  dsn: env.SENTRY_DSN || undefined,
  environment: env.SENTRY_ENVIRONMENT || "hackathon",
  release: VERSION,
  tracesSampleRate: 1.0,
  enableLogs: true,
  sendDefaultPii: false,
  dataCollection: { genAI: { inputs: true, outputs: true } },
});

/** The goose brain, wrapped so every request, @callable and OpenAI call becomes a span in one trace with the phone. */
export const GooseBrain = Sentry.instrumentAgentWithSentry(sentryOptions, GooseBrainBase);
/** The Goose Board: one global Durable Object (today's runs + who is playing now) behind /board. */
export const GooseBoard = Sentry.instrumentAgentWithSentry(sentryOptions, GooseBoardBase);

const NO_STORE = { "cache-control": "no-store", "access-control-allow-origin": "*" };

/** /board (page), /board/run, /board/top, /board/data. Returns null when the path is not the board's. */
async function boardRoutes(request: Request, env: Env, url: URL): Promise<Response | null> {
  if (url.pathname !== "/board" && !url.pathname.startsWith("/board/")) return null;
  const board = env.GooseBoard.get(env.GooseBoard.idFromName("global"));
  if (url.pathname === "/board") return new Response(BOARD_HTML, { headers: { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" } });
  if (url.pathname === "/board/run" && request.method === "POST") {
    const r = await board.fetch(new Request("https://goose-board/run", { method: "POST", headers: { "content-type": "application/json" }, body: await request.text() }));
    return new Response(await r.text(), { status: r.status, headers: { "content-type": "application/json", ...NO_STORE } });
  }
  if (url.pathname === "/board/top") {
    const n = Math.min(10, Math.max(1, Number(url.searchParams.get("n") ?? 3) || 3));
    const r = await board.fetch(new Request(`https://goose-board/top?n=${n}`));
    return new Response(await r.text(), { status: r.status, headers: { "content-type": "application/json", ...NO_STORE } });
  }
  if (url.pathname === "/board/data") {
    const snap = (await (await board.fetch(new Request("https://goose-board/top?n=10"))).json()) as { runs: unknown[]; total: number; latestDevice: string };
    const device = (url.searchParams.get("device") ?? "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 64) || snap.latestDevice;
    let brain: unknown = null;
    if (device) {
      try {
        const stub = env.GooseBrain.get(env.GooseBrain.idFromName(device));
        const r = await stub.fetch(new Request(`${url.origin}/agents/goose-brain/${device}/memory`));
        if (r.ok) {
          const m = (await r.json()) as { events?: unknown[] };
          m.events = (m.events ?? []).slice(0, 12);
          brain = m;
        }
      } catch (e) {
        Sentry.captureException(e);
      }
    }
    return Response.json({ ...snap, latestDevice: device, brain, at: Date.now() }, { headers: NO_STORE });
  }
  return new Response("Not found", { status: 404 });
}

async function warmCache(env: Env): Promise<Response> {
  const voices = [env.ELEVENLABS_VOICE_ID || "auto", env.ELEVENLABS_VOICE_ID_FEMALE].filter((v, i, a) => v && a.indexOf(v) === i);
  let cached = 0, generated = 0, failed = 0;
  const started = Date.now();
  outer: for (const voiceTag of voices) for (const lines of Object.values(BEAT_LINES)) {
    for (const text of lines) {
      const key = audioKey(await sha256Hex(`${voiceTag}|${env.ELEVENLABS_MODEL}|${delivery(env).signature}|${text}`));
      if (await getCachedWav(env, key)) { cached++; continue; }
      try {
        const result = await speak(env, text, voiceTag);
        await putWav(env, key, result.wav);
        generated++;
      } catch (e) {
        failed++;
        Sentry.captureException(e);
      }
      if (Date.now() - started > 25000) break outer; // stay well inside the request time limit; call again to continue
    }
  }
  Sentry.logger.info("warm cache", { cached, generated, failed, ms: Date.now() - started });
  return Response.json({ cached, generated, failed, ms: Date.now() - started, done: failed === 0 && generated + cached >= voices.length * Object.values(BEAT_LINES).reduce((n, l) => n + l.length, 0) });
}

const handler = {
  async fetch(request: Request, env: Env, _ctx: ExecutionContext): Promise<Response> {
    const url = new URL(request.url);
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: { "access-control-allow-origin": "*", "access-control-allow-methods": "GET,POST,OPTIONS", "access-control-allow-headers": "content-type,sentry-trace,baggage" } });
    }
    if (url.pathname === "/health") {
      return Response.json({ ok: true, version: VERSION, delivery: delivery(env).signature, mock: env.MOCK_AI === "1" || !env.OPENAI_API_KEY, mockText: env.MOCK_AI === "1" || !env.OPENAI_API_KEY, mockVoice: env.MOCK_AI === "1" || !env.ELEVENLABS_API_KEY, writer: writerName(env), elastic: elasticOn(env), at: Date.now() });
    }
    if (url.pathname.startsWith("/audio/")) {
      const hash = url.pathname.slice("/audio/".length).replace(/[^a-f0-9]/g, "");
      if (hash.length !== 64) return new Response("Bad key", { status: 400 });
      return serveWav(env, `audio/${hash}.wav`);
    }
    // PUT /cache/<sha256>: upload a pre-generated WAV into the cache (the pregen bank), so a tone change costs no second TTS pass.
    if (url.pathname.startsWith("/cache/") && request.method === "PUT") {
      if (!env.WARM_KEY || request.headers.get("x-warm-key") !== env.WARM_KEY) return new Response("Forbidden", { status: 403 });
      const hash = url.pathname.slice("/cache/".length).replace(/[^a-f0-9]/g, "");
      if (hash.length !== 64) return new Response("Bad key", { status: 400 });
      const body = await request.arrayBuffer();
      if (body.byteLength < 64 || body.byteLength > 2_000_000 || new TextDecoder().decode(body.slice(0, 4)) !== "RIFF") return new Response("Not a WAV", { status: 400 });
      await putWav(env, `audio/${hash}.wav`, body);
      return Response.json({ ok: true, bytes: body.byteLength });
    }
    // POST /warm: voice every bank line into the R2 cache once, so the demo never waits on the first TTS of a line.
    if (url.pathname === "/warm" && request.method === "POST") {
      return warmCache(env);
    }
    // The Goose Board: the booth page, today's runs and the live brain of whoever is playing.
    const board = await boardRoutes(request, env, url);
    if (board) return board;
    // POST /telemetry: the phone's 5 Hz AR sensor stream -> Elasticsearch (answered 202 before it is indexed).
    if (url.pathname === "/telemetry" && request.method === "POST") return ingestTelemetry(request, env, _ctx);
    // POST /elastic/alert: the Elastic Workflow closes its loop here (a perf regression it found -> a Sentry issue).
    if (url.pathname === "/elastic/alert" && request.method === "POST") return elasticAlert(request, env);
    // /agents/goose-brain/<deviceId>/session | /event | /memory | /reset
    const routed = await routeAgentRequest(request, env);
    if (routed) return routed;
    return new Response("GOOSED. goose-brain. Try /health", { status: 404 });
  },
} satisfies ExportedHandler<Env>;

export default Sentry.withSentry(sentryOptions, handler);
