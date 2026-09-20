import * as Sentry from "@sentry/cloudflare";
import { routeAgentRequest } from "agents";
import type { Env } from "./env";
import { GooseBrainBase } from "./agent";
import { serveWav, sha256Hex, audioKey, getCachedWav, putWav } from "./tools/r2";
import { speak, delivery } from "./tools/elevenlabs";
import { BEAT_LINES } from "./bank";

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
      return Response.json({ ok: true, version: VERSION, delivery: delivery(env).signature, mock: env.MOCK_AI === "1" || !env.OPENAI_API_KEY, mockText: env.MOCK_AI === "1" || !env.OPENAI_API_KEY, mockVoice: env.MOCK_AI === "1" || !env.ELEVENLABS_API_KEY, at: Date.now() });
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
    // /agents/goose-brain/<deviceId>/session | /event | /memory | /reset
    const routed = await routeAgentRequest(request, env);
    if (routed) return routed;
    return new Response("GOOSED. goose-brain. Try /health", { status: 404 });
  },
} satisfies ExportedHandler<Env>;

export default Sentry.withSentry(sentryOptions, handler);
