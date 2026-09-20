/**
 * Upload the pre-generated bank (Assets/Resources/GooseVoice/{m,f}/*.wav) into the deployed Worker's R2 cache under the
 * same content-addressed keys the Worker computes, so a tone change is voiced once locally and never again server-side.
 *   npx tsx scripts/upload-bank.ts [https://goose-brain.<account>.workers.dev]
 */
import { readFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { createHash } from "node:crypto";
import lines from "./lines.json";

const base = process.argv[2] ?? readFileSync("../../Assets/Resources/GooseBrainUrl.txt", "utf8").trim();
const cfg = readFileSync("wrangler.jsonc", "utf8");
const v = (name: string) => cfg.match(new RegExp(`"${name}":\\s*"([^"]*)"`))?.[1] ?? "";
const model = v("ELEVENLABS_MODEL"), key = v("WARM_KEY");
const signature = `s${Number(v("ELEVENLABS_STABILITY") || 0.45)}-y${Number(v("ELEVENLABS_STYLE") || 0.45)}-p${Number(v("ELEVENLABS_SPEED") || 1.05)}-${v("ELEVENLABS_TEXT_CASE") === "sentence" ? "sentence" : "caps"}`;
const voices: Record<string, string> = { m: v("ELEVENLABS_VOICE_ID"), f: v("ELEVENLABS_VOICE_ID_FEMALE") };
const bank = resolve(process.cwd(), "../../Assets/Resources/GooseVoice");

async function main() {
  let up = 0, missing = 0, failed = 0;
  for (const [folder, voice] of Object.entries(voices)) {
    if (!voice) continue;
    for (const [beat, arr] of Object.entries(lines.beats as Record<string, string[]>)) {
      for (let i = 0; i < arr.length; i++) {
        const file = resolve(bank, folder, `${beat}_${i}.wav`);
        if (!existsSync(file)) { missing++; continue; }
        const hash = createHash("sha256").update(`${voice}|${model}|${signature}|${arr[i]}`).digest("hex");
        const res = await fetch(`${base}/cache/${hash}`, { method: "PUT", headers: { "x-warm-key": key, "content-type": "audio/wav" }, body: readFileSync(file) });
        if (res.ok) up++; else { failed++; console.log(`${folder}/${beat}_${i}: ${res.status} ${await res.text()}`); }
      }
    }
  }
  console.log(`uploaded ${up}, missing ${missing}, failed ${failed} -> ${base} (${signature})`);
}
main().catch((e) => { console.error(e); process.exit(1); });
