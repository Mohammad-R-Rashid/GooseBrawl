// scripts/elastic-agent.ts: the goose's intelligence officer in Elastic Agent Builder: five tools (ES|QL with parameters,
// index search over the shouts and the lines) composed into the agent "goosed-intel", which the Worker asks for a case
// file in the background (src/intel.ts deepIntel). Idempotent: existing tools/agents are updated in place.
//   npx tsx scripts/elastic-agent.ts               create or update the tools and the agent
//   npx tsx scripts/elastic-agent.ts --ask <device_id>   one converse round-trip, printed with timing
import { kb, loadDevVars, need, show } from "./elastic-client";

const AGENT_ID = () => process.env.ELASTIC_AGENT_ID || "goosed-intel";

const deviceParam = { device_id: { type: "keyword", description: "The player's device id (16 hex characters, or seed0000... for the seeded day)" } };

const TOOLS = [
  {
    id: "goosed-player-stats",
    type: "esql",
    description: "Round history of one player (by device_id): rounds played, average and best survival in seconds, how many times the goose won, breads thrown, lunges dodged, when they were last seen.",
    configuration: {
      query: `FROM goosed-runs | WHERE device_id == ?device_id | STATS rounds = COUNT(*), avg_seconds = AVG(survival), best_seconds = MAX(survival), times_goosed = SUM(CASE(outcome == "GOOSED", 1, 0)), breads = SUM(breads), dodges = SUM(dodges), last_seen = MAX(@timestamp)`,
      params: deviceParam,
    },
  },
  {
    id: "goosed-crowd-today",
    type: "esql",
    description: "Everyone who faced a goose in the last 24 hours: rounds, distinct players, average and best survival in seconds, and the share of rounds the goose won.",
    configuration: {
      query: `FROM goosed-runs | WHERE @timestamp > NOW() - 24 hours | STATS rounds = COUNT(*), players = COUNT_DISTINCT(device_id), avg_seconds = AVG(survival), best_seconds = MAX(survival), goosed_pct = 100 * AVG(CASE(outcome == "GOOSED", 1.0, 0.0))`,
      params: {},
    },
  },
  {
    id: "goosed-movement-profile",
    type: "esql",
    description: "How one player (by device_id) moves, from the 5 Hz AR sensor stream: average and top speed in metres per second, percentage of samples standing still, average distance kept from the goose, median frame rate.",
    configuration: {
      query: `FROM goosed-telemetry | WHERE device_id == ?device_id | STATS samples = COUNT(*), avg_speed = AVG(player_speed), top_speed = MAX(player_speed), still_pct = 100 * AVG(CASE(player_speed < 0.15, 1.0, 0.0)), avg_distance = AVG(dist), median_fps = MEDIAN(fps)`,
      params: deviceParam,
    },
  },
  {
    id: "goosed-leaderboard",
    type: "esql",
    description: "The five longest survivals of the last 24 hours: device id, the goose they faced, seconds, outcome, time.",
    configuration: {
      query: `FROM goosed-runs | WHERE @timestamp > NOW() - 24 hours | SORT survival DESC | KEEP device_id, goose, survival, outcome, @timestamp | LIMIT 5`,
      params: {},
    },
  },
  {
    id: "goosed-shouts",
    type: "index_search",
    description: "Semantic and keyword search over everything players have shouted at the goose, in any language. Fields: transcript, lang (ISO code), device_id, goose, @timestamp, survival_at.",
    configuration: { pattern: "goosed-shouts" },
  },
  {
    id: "goosed-lines",
    type: "index_search",
    description: "Search over every line the goose has said. Fields: line, beat (intro, taunt10, yell, bread, dodge, rage, caught, outlasted), mood, device_id, goose, @timestamp.",
    configuration: { pattern: "goosed-lines" },
  },
];

const INSTRUCTIONS = `You are the intelligence officer of a petty, theatrical Canada goose villain in the AR game GOOSED. (the player stole its egg; it chases them around their own room). You are given a player's device_id. Use the tools, never guess: goosed-player-stats and goosed-movement-profile for this player, goosed-crowd-today and goosed-leaderboard for the crowd, and goosed-shouts to find what this player shouted and whether other players shouted something similar (any language).
Then write the case file the goose will taunt from: at most 4 short lines, each one concrete fact with a number or a quote the goose can weaponise (this player against the crowd; how slow they run or how much they stand still; what they shouted, in which language; whether someone else shouted the same thing). If a tool returns nothing, say the player is new. Family-friendly: nothing about anyone's body, identity or background. End with one line starting "Angle:" naming the taunt angle. Plain text, no markdown, no headings, under 90 words.`;

async function upsertTool(t: (typeof TOOLS)[number]) {
  const r = await kb(`/api/agent_builder/tools`, "POST", t);
  if (r.ok) return show(`POST tool ${t.id}`, r);
  if (r.status === 409 || /already exists|conflict/i.test(r.text)) return show(`PUT tool ${t.id}`, await kb(`/api/agent_builder/tools/${t.id}`, "PUT", { description: t.description, configuration: t.configuration }));
  show(`POST tool ${t.id}`, r);
}

async function upsertAgent() {
  const id = AGENT_ID();
  const body = {
    id,
    name: "GOOSED. intelligence officer",
    description: "Builds the goose's case file on a player from Elasticsearch: crowd stats, movement profile, shouts in any language.",
    labels: ["goosed", "hack-the-north"],
    avatar_color: "#FFB826",
    avatar_symbol: "G",
    configuration: { instructions: INSTRUCTIONS, tools: [{ tool_ids: TOOLS.map((t) => t.id) }] },
  };
  const r = await kb(`/api/agent_builder/agents`, "POST", body);
  if (r.ok) return show(`POST agent ${id}`, r);
  if (r.status === 409 || /already exists|conflict/i.test(r.text)) {
    const { id: _omit, ...rest } = body;
    return show(`PUT agent ${id}`, await kb(`/api/agent_builder/agents/${id}`, "PUT", rest));
  }
  show(`POST agent ${id}`, r);
}

async function ask(device: string) {
  const started = Date.now();
  const r = await kb<{ conversation_id?: string; steps?: { type?: string; tool_id?: string }[]; response?: { message?: string } }>(`/api/agent_builder/converse`, "POST", { agent_id: AGENT_ID(), input: `Build the case file for player device_id "${device}" (the goose is KEVIN). Use the tools; do not guess.` }, 120000);
  show("POST /api/agent_builder/converse", r);
  if (!r.ok) return;
  console.log(`   ${Date.now() - started} ms, ${r.body.steps?.length ?? 0} steps: ${(r.body.steps ?? []).map((s) => s.tool_id ?? s.type).join(", ")}`);
  console.log("   " + (r.body.response?.message ?? "").replace(/\n/g, "\n   "));
}

async function main() {
  loadDevVars();
  need("ELASTIC_KIBANA_URL");
  const i = process.argv.indexOf("--ask");
  if (i >= 0) return ask(process.argv[i + 1] || "seed0000");
  for (const t of TOOLS) await upsertTool(t);
  await upsertAgent();
  const list = await kb<{ results?: { id: string }[] }>(`/api/agent_builder/agents`);
  if (list.ok) console.log("   agents: " + (list.body.results ?? []).map((a) => a.id).join(", "));
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
