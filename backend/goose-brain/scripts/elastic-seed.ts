// scripts/elastic-seed.ts: a day of hackathon traffic for the demo, so the goose's case file has substance before the
// first real player: players, rounds, multilingual shouts, the goose's lines and 5 Hz telemetry (with a few rounds whose
// frame rate collapses near the goose, for the Workflow to find). Everything carries seed: true.
//   npx tsx scripts/elastic-seed.ts                  seed (idempotent-ish: run --clean first to start over)
//   npx tsx scripts/elastic-seed.ts --clean          delete the seeded documents only (real players stay)
//   npx tsx scripts/elastic-seed.ts --players 80 --hours 20
import { es, loadDevVars, show } from "./elastic-client";
import { PERSONAS, BEAT_LINES } from "../src/bank";
import { lineDoc, runDoc, shoutDoc, IDX, type BulkDoc } from "../src/tools/elastic";
import { telemetryDocs } from "../src/telemetry";

const SHOUTS: { text: string; lang: string }[] = [
  { text: "go away", lang: "en" }, { text: "shoo", lang: "en" }, { text: "leave me alone", lang: "en" }, { text: "stop it", lang: "en" },
  { text: "bad goose", lang: "en" }, { text: "get out of my house", lang: "en" }, { text: "no no no no", lang: "en" }, { text: "I'm sorry okay", lang: "en" },
  { text: "please stop", lang: "en" }, { text: "you're not even real", lang: "en" }, { text: "get off my rug", lang: "en" }, { text: "honk at me again I dare you", lang: "en" },
  { text: "somebody help", lang: "en" }, { text: "not the egg", lang: "en" }, { text: "back off bird", lang: "en" },
  { text: "va-t'en", lang: "fr" }, { text: "arrête", lang: "fr" }, { text: "laisse-moi tranquille", lang: "fr" }, { text: "dégage sale oie", lang: "fr" },
  { text: "vete", lang: "es" }, { text: "déjame en paz", lang: "es" }, { text: "basta ya", lang: "es" }, { text: "fuera de aquí", lang: "es" },
  { text: "जाओ यहाँ से", lang: "hi" }, { text: "बस करो", lang: "hi" }, { text: "मुझे अकेला छोड़ दो", lang: "hi" },
  { text: "ਚਲਾ ਜਾ", lang: "pa" }, { text: "ਬੱਸ ਕਰ", lang: "pa" },
  { text: "走开", lang: "zh" }, { text: "别追我", lang: "zh" }, { text: "停下", lang: "zh" },
  { text: "اذهب بعيدا", lang: "ar" }, { text: "توقف", lang: "ar" },
  { text: "vai embora", lang: "pt" }, { text: "me deixa em paz", lang: "pt" },
  { text: "hau ab", lang: "de" }, { text: "lass mich in Ruhe", lang: "de" },
  { text: "저리 가", lang: "ko" }, { text: "그만해", lang: "ko" },
  { text: "போய்விடு", lang: "ta" }, { text: "چلے جاؤ", lang: "ur" }, { text: "あっち行け", lang: "ja" }, { text: "ходи отсюда", lang: "ru" },
];

// A deterministic PRNG so two seed runs produce the same day (and the demo script can quote it).
let seed = 20260919;
const rnd = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296);
const pick = <T>(a: readonly T[]): T => a[Math.floor(rnd() * a.length)];
const between = (lo: number, hi: number) => lo + rnd() * (hi - lo);

function arg(name: string, dflt: number): number {
  const i = process.argv.indexOf(name);
  return i >= 0 && process.argv[i + 1] ? Number(process.argv[i + 1]) : dflt;
}

async function flush(docs: BulkDoc[]) {
  for (let i = 0; i < docs.length; i += 500) {
    const chunk = docs.slice(i, i + 500);
    const lines: string[] = [];
    for (const d of chunk) {
      lines.push(JSON.stringify({ create: { _index: d.index } }));
      lines.push(JSON.stringify(d.doc));
    }
    const r = await es<{ errors: boolean; items: { create: { status: number; error?: unknown } }[] }>("/_bulk", "POST", lines.join("\n") + "\n", 120000);
    const bad = r.ok ? r.body.items.filter((it) => it.create.status >= 300) : [];
    if (!r.ok || bad.length) console.log(`   bulk ${chunk.length}: ${r.status}${bad.length ? ` ${bad.length} failed: ${JSON.stringify(bad[0].create.error).slice(0, 300)}` : ""}`);
  }
}

/** One round's telemetry: the player wanders a 4 x 4 m room, the goose closes in; some rounds drop frames when it is close. */
function telemetryFor(device: string, goose: string, round: number, survival: number, at: number, laggy: boolean): BulkDoc[] {
  const samples: number[][] = [];
  let px = between(-1.5, 1.5), pz = between(-1.5, 1.5), gx = px + between(2.4, 3.2) * (rnd() < 0.5 ? 1 : -1), gz = pz + between(-1, 1);
  let heading = between(0, Math.PI * 2);
  const dt = 0.2;
  const state = (t: number, dist: number) => (t < 1.5 ? 0 : dist < 0.9 && rnd() < 0.15 ? 4 : t > 30 ? 2 : 1);
  for (let t = 0; t <= survival; t += dt) {
    const tier = t < 10 ? 0 : t < 20 ? 1 : t < 30 ? 2 : 3;
    heading += between(-0.6, 0.6);
    const pspeed = rnd() < 0.2 ? 0 : between(0.3, 1.3);
    px = Math.max(-2, Math.min(2, px + Math.cos(heading) * pspeed * dt));
    pz = Math.max(-2, Math.min(2, pz + Math.sin(heading) * pspeed * dt));
    const dx = px - gx, dz = pz - gz;
    const dist = Math.hypot(dx, dz);
    const gspeed = Math.min(1.6, 0.9 + tier * 0.2 + between(-0.1, 0.1));
    if (dist > 0.6) {
      gx += (dx / dist) * gspeed * dt;
      gz += (dz / dist) * gspeed * dt;
    }
    const near = dist < 1.2;
    const frameMs = laggy && near ? between(34, 52) : between(15.5, 17.5) + (near ? between(0, 3) : 0);
    samples.push([t, px, 1.4, pz, gx, 0, gz, dist, pspeed, dist > 0.6 ? gspeed : 0, tier, tier + (rnd() < 0.3 ? 1 : 0), 1000 / frameMs, frameMs, frameMs * 0.55, laggy ? 1 : 0, state(t, dist)]);
  }
  return telemetryDocs({ device, goose, round, dur: survival, samples }, at + survival * 1000).map((d) => ({ ...d, doc: { ...d.doc, seed: true } }));
}

async function main() {
  loadDevVars();
  if (process.argv.includes("--clean")) {
    for (const idx of [IDX.shouts, IDX.lines, IDX.runs, IDX.telemetry]) show(`DELETE seed docs in ${idx}`, await es(`/${idx}/_delete_by_query?conflicts=proceed&refresh=true`, "POST", { query: { term: { seed: true } } }, 120000));
    return;
  }
  const players = arg("--players", 60);
  const hours = arg("--hours", 20);
  const now = Date.now();
  const docs: BulkDoc[] = [];
  let runs = 0, shouts = 0, lines = 0, samples = 0, laggyRounds = 0;
  for (let p = 0; p < players; p++) {
    const device = `seed${String(p).padStart(4, "0")}${Math.floor(rnd() * 1e8).toString(16).padStart(8, "0")}`;
    const persona = PERSONAS[p % PERSONAS.length];
    const nRuns = 1 + Math.floor(rnd() * rnd() * 6);
    let grudge = 0;
    let at = now - between(0.2, hours) * 3600 * 1000;
    for (let r = 1; r <= nRuns; r++) {
      at += between(40, 400) * 1000;
      if (at > now - 60000) at = now - between(60, 600) * 1000;
      // Survival: most humans last 8-35 s; a few outlast the goose (45 s); a handful barely make 5.
      const survival = rnd() < 0.12 ? between(45, 58) : rnd() < 0.15 ? between(3, 8) : between(8, 35);
      const outcome = survival >= 45 ? "OUTLASTED" : "GOOSED";
      if (outcome === "GOOSED") grudge++;
      const tier = survival < 10 ? 0 : survival < 20 ? 1 : survival < 30 ? 2 : 3;
      const breads = rnd() < 0.5 ? 1 : 0;
      const dodges = Math.floor(rnd() * (survival / 9));
      const honks = Math.floor(survival / 2.5 + rnd() * 6);
      const shoutHere = rnd() < 0.6 ? [pick(SHOUTS)] : rnd() < 0.3 ? [pick(SHOUTS), pick(SHOUTS)] : [];
      docs.push(runDoc({ deviceId: device, goose: persona.name, outcome, survival: Math.round(survival * 10) / 10, honks, dodges, breads, tier, round: r, grudge, lastShout: shoutHere[0]?.text, at: at + survival * 1000, seed: true }));
      runs++;
      docs.push(lineDoc({ deviceId: device, goose: persona.name, beat: r > 1 ? "intro_again" : "intro", line: pick(BEAT_LINES[r > 1 ? "intro_again" : "intro"]), mood: "smug", source: "bank", round: r, at, seed: true }));
      lines++;
      let ts = at + 3000;
      for (const s of shoutHere) {
        ts += between(3, 12) * 1000;
        docs.push(shoutDoc({ deviceId: device, goose: persona.name, transcript: s.text, lang: s.lang, survival: (ts - at) / 1000, tier, round: r, at: ts, seed: true }));
        docs.push(lineDoc({ deviceId: device, goose: persona.name, beat: "yell", line: pick(BEAT_LINES.yell), mood: "angry", source: "bank", survival: (ts - at) / 1000, tier, round: r, at: ts + 1500, seed: true }));
        shouts++;
        lines++;
      }
      const endBeat = outcome === "GOOSED" ? "caught" : "outlasted";
      docs.push(lineDoc({ deviceId: device, goose: persona.name, beat: endBeat, line: pick(BEAT_LINES[endBeat]), mood: outcome === "GOOSED" ? "gleeful" : "sore", source: "bank", survival, tier, round: r, at: at + survival * 1000, seed: true }));
      lines++;
      if (rnd() < 0.55) {
        const laggy = rnd() < 0.15;
        if (laggy) laggyRounds++;
        const t = telemetryFor(device, persona.name, r, survival, at, laggy);
        samples += t.length;
        docs.push(...t);
      }
    }
  }
  console.log(`seeding ${players} players: ${runs} runs, ${shouts} shouts, ${lines} lines, ${samples} telemetry samples (${laggyRounds} laggy rounds) = ${docs.length} docs`);
  await flush(docs);
  show("refresh", await es("/goosed-*/_refresh", "POST"));
  const stats = await es<{ indices?: Record<string, { total?: { docs?: { count?: number } } }> }>("/goosed-*/_stats/docs");
  if (stats.ok) for (const [name, s] of Object.entries(stats.body.indices ?? {})) console.log(`   ${name}: ${s.total?.docs?.count ?? 0} docs`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
