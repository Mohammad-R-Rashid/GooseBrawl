import * as Sentry from "@sentry/cloudflare";
import type { Env } from "./env";
import { IDX, elasticOn, indexQuietly, type BulkDoc } from "./tools/elastic";

/**
 * POST /telemetry: the phone's 5 Hz AR sensor stream, batched every ~3 s by GooseTelemetryStream.cs. The Worker answers
 * 202 at once and bulk-indexes in the background, so the phone never waits on Elasticsearch. Timestamps are assigned
 * here from the batch's own clock (t = seconds since the batch started), so a wrong phone clock cannot land outside the
 * data stream's time window.
 *
 * Sample columns: [t, px, py, pz, gx, gy, gz, dist, pspeed, gspeed, tier, anger, fps, frame_ms, gpu_ms, thermal, state]
 */
export interface TelemetryBatch {
  device: string;
  goose?: string;
  round?: number;
  /** Seconds the batch spans (t of the last sample). */
  dur?: number;
  samples: number[][];
}

const MAX_SAMPLES = 400;
const STATES = ["idle", "walk", "run", "angryflap", "jumpattack", "dash", "glare", "stunned", "gameover", "distracted", "eating", "flinched", "namecalled"];

const f = (v: unknown, digits = 3): number => (typeof v === "number" && Number.isFinite(v) ? Math.round(v * 10 ** digits) / 10 ** digits : 0);

export function telemetryDocs(batch: TelemetryBatch, now = Date.now()): BulkDoc[] {
  const device = String(batch.device ?? "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 32);
  if (!device || !Array.isArray(batch.samples)) return [];
  const goose = String(batch.goose ?? "").slice(0, 32);
  const round = Math.max(0, Math.floor(f(batch.round, 0)));
  const dur = Math.max(0, f(batch.dur ?? batch.samples[batch.samples.length - 1]?.[0] ?? 0));
  const docs: BulkDoc[] = [];
  for (const s of batch.samples.slice(-MAX_SAMPLES)) {
    if (!Array.isArray(s) || s.length < 13) continue;
    const t = f(s[0]);
    const at = now - Math.max(0, dur - t) * 1000;
    const state = STATES[Math.floor(f(s[16], 0))] ?? "unknown";
    docs.push({
      index: IDX.telemetry,
      doc: {
        "@timestamp": new Date(at).toISOString(),
        device_id: device,
        round_id: `${device}-${round}`,
        goose,
        state,
        t,
        player: { x: f(s[1]), y: f(s[2]), z: f(s[3]) },
        goose_pos: { x: f(s[4]), y: f(s[5]), z: f(s[6]) },
        player_pt: { x: f(s[1]), y: f(s[3]) },
        goose_pt: { x: f(s[4]), y: f(s[6]) },
        dist: f(s[7]),
        player_speed: f(s[8]),
        goose_speed: f(s[9]),
        tier: Math.floor(f(s[10], 0)),
        anger: Math.floor(f(s[11], 0)),
        fps: f(s[12], 1),
        frame_ms: f(s[13], 2),
        gpu_ms: f(s[14], 2),
        thermal: Math.floor(f(s[15], 0)),
      },
    });
  }
  return docs;
}

export async function ingestTelemetry(request: Request, env: Env, ctx: ExecutionContext): Promise<Response> {
  if (!elasticOn(env)) return new Response(null, { status: 204 });
  let batch: TelemetryBatch;
  try {
    batch = (await request.json()) as TelemetryBatch;
  } catch {
    return Response.json({ error: "bad json" }, { status: 400 });
  }
  const docs = telemetryDocs(batch);
  if (docs.length === 0) return Response.json({ accepted: 0 }, { status: 202 });
  ctx.waitUntil(indexQuietly(env, docs));
  Sentry.logger.debug("telemetry batch", { device: batch.device, samples: docs.length, round: batch.round ?? 0 });
  return Response.json({ accepted: docs.length }, { status: 202, headers: { "access-control-allow-origin": "*" } });
}
