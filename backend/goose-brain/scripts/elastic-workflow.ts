// scripts/elastic-workflow.ts: the Elastic Workflow that closes the loop without a human. Every 5 minutes it runs ES|QL
// over the sensor stream and the runs, then ACTS: rounds whose frame rate collapsed near the goose are filed with the
// goose-brain Worker (they become Sentry issues with the numbers attached), and players who keep getting goosed in
// under 8 s get a mercy flag in goosed-players, which the goose's case file reads (the intro goes easy on them).
//   npx tsx scripts/elastic-workflow.ts             create or update the workflow
//   npx tsx scripts/elastic-workflow.ts --run       also run it once now and print the run
import { kb, loadDevVars, need, show } from "./elastic-client";

const NAME = "goosed-watch";

function yaml(worker: string, warmKey: string): string {
  return `version: "1"
name: ${NAME}
description: "GOOSED.: every 5 minutes, find rounds whose frame rate collapsed while the goose was close and file them with the goose-brain Worker (Sentry issues); flag players who keep getting goosed in under 8 s so the goose goes easy on the words (mercy)."
enabled: true
tags: [goosed, hack-the-north]
triggers:
  - type: scheduled
    with:
      every: "5m"
  - type: manual
consts:
  worker: "${worker}"
  warm_key: "${warmKey}"
steps:
  - name: slow_rounds
    type: elasticsearch.esql.query
    with:
      format: json
      query: |
        FROM goosed-telemetry
        | WHERE @timestamp > NOW() - 15 minutes AND dist < 1.2
        | STATS median_fps = MEDIAN(fps), samples = COUNT(*), median_frame_ms = MEDIAN(frame_ms), thermal = MAX(thermal) BY device_id, round_id
        | WHERE median_fps < 30 AND samples >= 10
        | SORT median_fps ASC
        | LIMIT 5
  - name: report_slow_rounds
    type: foreach
    foreach: "\${{ steps.slow_rounds.output.values }}"
    steps:
      - name: file_with_worker
        type: http
        with:
          url: "{{ consts.worker }}/elastic/alert"
          method: POST
          headers:
            content-type: application/json
            x-warm-key: "{{ consts.warm_key }}"
          body:
            kind: "perf.fps_collapse_near_goose"
            message: "round {{ foreach.item[5] }}: median {{ foreach.item[0] }} fps within 1.2 m of the goose ({{ foreach.item[1] }} samples, {{ foreach.item[2] }} ms frames, thermal {{ foreach.item[3] }})"
            device_id: "{{ foreach.item[4] }}"
            data:
              median_fps: "{{ foreach.item[0] }}"
              samples: "{{ foreach.item[1] }}"
              median_frame_ms: "{{ foreach.item[2] }}"
              thermal: "{{ foreach.item[3] }}"
              round_id: "{{ foreach.item[5] }}"
  - name: fast_losers
    type: elasticsearch.esql.query
    with:
      format: json
      query: |
        FROM goosed-runs
        | WHERE @timestamp > NOW() - 2 hours AND outcome == "GOOSED" AND survival < 8
        | STATS fast_losses = COUNT(*) BY device_id
        | WHERE fast_losses >= 3
        | LIMIT 20
  - name: flag_mercy
    type: foreach
    foreach: "\${{ steps.fast_losers.output.values }}"
    steps:
      - name: write_flag
        type: elasticsearch.request
        with:
          method: PUT
          path: "/goosed-players/_doc/{{ foreach.item[1] }}"
          body:
            device_id: "{{ foreach.item[1] }}"
            mercy: true
            reason: "fast_losses"
            fast_losses: "{{ foreach.item[0] }}"
            updated: "{{ execution.startedAt }}"
`;
}

interface WorkflowRow {
  id: string;
  name: string;
}

async function main() {
  loadDevVars();
  need("ELASTIC_KIBANA_URL");
  const worker = process.env.WORKER_URL || "https://goose-brain.mohammad-rashid7337.workers.dev";
  const body = yaml(worker, need("WARM_KEY"));

  // Find an existing workflow with this name (update in place), else create.
  const list = await kb<{ results?: WorkflowRow[]; workflows?: WorkflowRow[] }>(`/api/workflows?limit=100`);
  show("GET /api/workflows", list);
  const rows = list.ok ? (list.body.results ?? list.body.workflows ?? []) : [];
  const existing = rows.find((w) => w.name === NAME);
  let id = existing?.id;
  if (existing) {
    show(`PUT /api/workflows/${existing.id}`, await kb(`/api/workflows/${existing.id}`, "PUT", { yaml: body, enabled: true }));
  } else {
    const r = await kb<{ id?: string }>(`/api/workflows`, "POST", { yaml: body });
    show("POST /api/workflows", r);
    id = r.ok ? r.body.id : undefined;
  }
  if (!id) {
    console.log("   (if Workflows is off in this project: Kibana > Stack Management > Advanced settings > enable Workflows, then rerun)");
    return;
  }
  console.log(`   workflow ${NAME} = ${id}`);
  if (process.argv.includes("--run")) {
    const run = await kb<{ workflowExecutionId?: string; id?: string }>(`/api/workflows/${id}/run`, "POST", { inputs: {} }, 120000);
    show(`POST /api/workflows/${id}/run`, run);
    const exec = run.ok ? run.body.workflowExecutionId ?? run.body.id : undefined;
    if (exec) {
      await new Promise((r) => setTimeout(r, 6000));
      const status = await kb<{ status?: string; stepExecutions?: { stepId?: string; status?: string; error?: string }[] }>(`/api/workflowExecutions/${exec}`);
      show(`GET /api/workflowExecutions/${exec}`, status);
      if (status.ok) {
        console.log(`   status: ${status.body.status}`);
        for (const s of status.body.stepExecutions ?? []) console.log(`   ${s.status?.padEnd(10)} ${s.stepId}${s.error ? " " + String(s.error).slice(0, 200) : ""}`);
      }
    }
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
