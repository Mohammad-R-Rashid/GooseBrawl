// scripts/elastic-setup.ts: one-time (idempotent) setup of the Elastic context layer.
//   npx tsx scripts/elastic-setup.ts            create/update indices + the telemetry data stream, pick inference endpoints
//   npx tsx scripts/elastic-setup.ts --wipe     delete everything goosed-* first (the demo data is re-seeded by elastic-seed.ts)
// Needs ELASTIC_URL (wrangler.jsonc) and ELASTIC_API_KEY (.dev.vars).
import { INDICES, TELEMETRY, es, linesMapping, loadDevVars, playersMapping, runsMapping, shoutsMapping, show, telemetryTemplate } from "./elastic-client";

interface InferenceList {
  endpoints?: { inference_id: string; task_type: string; service: string; service_settings?: { model_id?: string } }[];
}

async function main() {
  loadDevVars();
  const wipe = process.argv.includes("--wipe");

  const root = await es<{ version?: { number?: string }; tagline?: string }>("/");
  show("GET /", root);
  if (!root.ok) process.exit(1);
  console.log(`   ${root.body.tagline ?? ""} ${root.body.version?.number ?? ""}`);

  // Which embedding and rerank endpoints does this project have? Jina dense vectors preferred (the prize text names them).
  const inf = await es<InferenceList>("/_inference/_all");
  show("GET /_inference/_all", inf);
  const endpoints = inf.body.endpoints ?? [];
  for (const e of endpoints) console.log(`   ${e.task_type.padEnd(16)} ${e.inference_id}  (${e.service}${e.service_settings?.model_id ? ", " + e.service_settings.model_id : ""})`);
  const embeds = endpoints.filter((e) => e.task_type === "text_embedding");
  const embed = embeds.find((e) => /jina.*v5/i.test(e.inference_id + (e.service_settings?.model_id ?? ""))) ?? embeds.find((e) => /jina/i.test(e.inference_id + (e.service_settings?.model_id ?? ""))) ?? embeds.find((e) => e.service === "elastic");
  const reranks = endpoints.filter((e) => e.task_type === "rerank");
  const rerank = reranks.find((e) => /jina/i.test(e.inference_id)) ?? reranks.find((e) => e.service === "elastic") ?? reranks[0];
  console.log(`   embeddings for semantic_text: ${embed ? embed.inference_id : "(default endpoint)"}`);
  console.log(`   rerank endpoint: ${rerank ? rerank.inference_id : "(none: RRF only)"}  -> put it in wrangler.jsonc ELASTIC_RERANK_ID`);

  if (wipe) {
    for (const idx of INDICES) show(`DELETE /${idx}`, await es(`/${idx}`, "DELETE"));
    show(`DELETE /_data_stream/${TELEMETRY}`, await es(`/_data_stream/${TELEMETRY}`, "DELETE"));
  }

  // Plain indices: create, or update the mapping when they exist (adding fields is allowed; changing types is not).
  const mappings: Record<string, unknown> = {
    "goosed-shouts": shoutsMapping(embed?.inference_id),
    "goosed-lines": linesMapping(embed?.inference_id),
    "goosed-runs": runsMapping,
    "goosed-players": playersMapping,
  };
  for (const idx of INDICES) {
    const r = await es(`/${idx}`, "PUT", { mappings: mappings[idx] });
    if (r.ok) show(`PUT /${idx}`, r);
    else if (r.text.includes("resource_already_exists_exception")) show(`PUT /${idx}/_mapping`, await es(`/${idx}/_mapping`, "PUT", mappings[idx]));
    else show(`PUT /${idx}`, r);
  }

  // The sensor stream: a time-series data stream, falling back to a plain data stream if this project refuses TSDS settings.
  let t = await es(`/_index_template/${TELEMETRY}`, "PUT", telemetryTemplate("tsds"));
  if (t.ok) show(`PUT /_index_template/${TELEMETRY} (time_series)`, t);
  else {
    show(`PUT /_index_template/${TELEMETRY} (time_series)`, t);
    t = await es(`/_index_template/${TELEMETRY}`, "PUT", telemetryTemplate("plain"));
    show(`PUT /_index_template/${TELEMETRY} (plain data stream)`, t);
  }
  const ds = await es(`/_data_stream/${TELEMETRY}`, "PUT");
  if (ds.ok || ds.text.includes("resource_already_exists_exception")) console.log(`ok  ${ds.status} PUT /_data_stream/${TELEMETRY}`);
  else show(`PUT /_data_stream/${TELEMETRY}`, ds);

  const stats = await es<{ indices?: Record<string, { total?: { docs?: { count?: number } } }> }>("/goosed-*/_stats/docs");
  if (stats.ok) for (const [name, s] of Object.entries(stats.body.indices ?? {})) console.log(`   ${name}: ${s.total?.docs?.count ?? 0} docs`);
  console.log("done");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
