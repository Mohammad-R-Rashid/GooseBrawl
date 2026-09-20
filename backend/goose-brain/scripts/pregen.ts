/**
 * Pre-generate the offline fallback voice bank: every line in scripts/lines.json -> ElevenLabs -> WAV (22.05 kHz mono)
 * in ../../Assets/Resources/GooseVoice/<beat>_<n>.wav. Skips files that already exist (delete one to regenerate).
 *   ELEVENLABS_API_KEY=... ELEVENLABS_VOICE_ID=... npx tsx scripts/pregen.ts [--force] [--mock]
 * --mock writes synthesized goose-babble instead (no key needed) so Unity has something to play.
 */
import { writeFileSync, mkdirSync, existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import lines from "./lines.json";
import { synthBabbleWav } from "../src/tools/r2";

const force = process.argv.includes("--force");
const mock = process.argv.includes("--mock");
const key = process.env.ELEVENLABS_API_KEY ?? readDevVar("ELEVENLABS_API_KEY");
const voices: Record<string, string | undefined> = {
  m: process.env.ELEVENLABS_VOICE_ID ?? readDevVar("ELEVENLABS_VOICE_ID") ?? readWranglerVar("ELEVENLABS_VOICE_ID"),
  f: process.env.ELEVENLABS_VOICE_ID_FEMALE ?? readDevVar("ELEVENLABS_VOICE_ID_FEMALE") ?? readWranglerVar("ELEVENLABS_VOICE_ID_FEMALE"),
};
const model = process.env.ELEVENLABS_MODEL ?? readWranglerVar("ELEVENLABS_MODEL") ?? "eleven_v3_conversational";
const stability = Number(process.env.ELEVENLABS_STABILITY ?? readWranglerVar("ELEVENLABS_STABILITY") ?? 0.45);
const style = Number(process.env.ELEVENLABS_STYLE ?? readWranglerVar("ELEVENLABS_STYLE") ?? 0.45);
const speed = Number(process.env.ELEVENLABS_SPEED ?? readWranglerVar("ELEVENLABS_SPEED") ?? 1.05);
const textCase = process.env.ELEVENLABS_TEXT_CASE ?? readWranglerVar("ELEVENLABS_TEXT_CASE") ?? "caps";
const shape = (t: string) => textCase !== "sentence" ? t : t.split(/(\[[^\]]*\])/).map((part) => part.startsWith("[") ? part : part.toLowerCase().replace(/(^|[.!?]\s+)([a-z])/g, (_m, a: string, b: string) => a + b.toUpperCase()).replace(/\bi\b/g, "I").replace(/\bi'/g, "I'")).join("");
const outDir = resolve(process.cwd(), "../../Assets/Resources/GooseVoice");

async function tts(text: string, voice: string | undefined): Promise<Buffer> {
  if (mock) return Buffer.from(synthBabbleWav(text));
  if (!key) throw new Error("ELEVENLABS_API_KEY missing (env or .dev.vars); or use --mock");
  let v = voice;
  if (!v) {
    const r = await fetch("https://api.elevenlabs.io/v2/voices?page_size=5", { headers: { "xi-api-key": key } });
    const d = (await r.json()) as { voices?: { voice_id: string; name: string }[] };
    v = d.voices?.[0]?.voice_id;
    if (!v) throw new Error("no voices on the account: run npm run design-voice");
    console.log("using first account voice", d.voices?.[0]?.name, v);
  }
  for (const m of [model, "eleven_flash_v2_5"]) {
    const t = m === model ? shape(text) : shape(text).replace(/\[[^\]]*\]/g, "").trim();
    const res = await fetch(`https://api.elevenlabs.io/v1/text-to-speech/${v}?output_format=wav_22050`, {
      method: "POST",
      headers: { "xi-api-key": key, "Content-Type": "application/json" },
      body: JSON.stringify({ text: t, model_id: m, voice_settings: { stability, similarity_boost: 0.75, style, use_speaker_boost: true, speed } }),
    });
    if (res.ok) return Buffer.from(await res.arrayBuffer());
    console.warn(`  ${m} failed: ${res.status} ${(await res.text()).slice(0, 120)}`);
  }
  throw new Error("tts failed");
}

async function main() {
  let made = 0, skipped = 0;
  for (const [folder, voice] of Object.entries(voices)) {
  const dir = resolve(outDir, folder);
  mkdirSync(dir, { recursive: true });
  for (const [beat, arr] of Object.entries(lines.beats as Record<string, string[]>)) {
    for (let i = 0; i < arr.length; i++) {
      const file = resolve(dir, `${beat}_${i}.wav`);
      if (existsSync(file) && !force) { skipped++; continue; }
      process.stdout.write(`${folder}/${beat}_${i}: ${arr[i]} ... `);
      const wav = await tts(arr[i], voice);
      writeFileSync(file, wav);
      console.log(`${(wav.length / 1024).toFixed(0)} KB`);
      made++;
    }
  }
  }
  console.log(`done: ${made} generated, ${skipped} kept, in ${outDir}${mock ? " (MOCK babble)" : ""}`);
}

function readDevVar(name: string): string | undefined {
  if (!existsSync(".dev.vars")) return undefined;
  const m = readFileSync(".dev.vars", "utf8").match(new RegExp(`^${name}=(.*)$`, "m"));
  return m?.[1]?.trim() || undefined;
}
function readWranglerVar(name: string): string | undefined {
  const m = readFileSync("wrangler.jsonc", "utf8").match(new RegExp(`"${name}":\\s*"([^"]*)"`));
  return m?.[1] || undefined;
}

main().catch((e) => { console.error(e); process.exit(1); });
