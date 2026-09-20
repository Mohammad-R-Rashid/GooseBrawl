import * as Sentry from "@sentry/cloudflare";
import type { Env } from "./env";

/**
 * POST /elastic/alert: where the Elastic Workflow closes its loop. Every few minutes it runs ES|QL over the telemetry
 * stream; when it finds rounds whose frame rate collapsed while the goose was close, it posts here and the finding
 * becomes a Sentry issue with the numbers attached (the mercy flag it writes goes straight into goosed-players).
 */
export async function elasticAlert(request: Request, env: Env): Promise<Response> {
  if (!env.WARM_KEY || request.headers.get("x-warm-key") !== env.WARM_KEY) return new Response("Forbidden", { status: 403 });
  const body = (await request.json().catch(() => ({}))) as { kind?: string; message?: string; device_id?: string; data?: unknown };
  const kind = String(body.kind ?? "alert").replace(/[^a-z0-9_.-]/gi, "").slice(0, 40) || "alert";
  const message = String(body.message ?? "").replace(/\s+/g, " ").trim().slice(0, 500);
  const device = String(body.device_id ?? "").slice(0, 64);
  Sentry.logger.warn("elastic workflow alert", { kind, message, device, data: JSON.stringify(body.data ?? {}).slice(0, 1000) });
  Sentry.captureMessage(`Elastic workflow: ${kind}: ${message || "(no message)"}`, {
    level: "warning",
    tags: { source: "elastic-workflow", kind },
    extra: { device_id: device, data: body.data },
  });
  return Response.json({ ok: true, kind });
}
