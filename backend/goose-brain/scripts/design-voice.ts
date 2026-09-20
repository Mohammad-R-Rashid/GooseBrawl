/**
 * Design the goose's voice with the ElevenLabs Voice Design API and save it to the account.
 *   ELEVENLABS_API_KEY=... npx tsx scripts/design-voice.ts
 * Prints the voice_id to put into wrangler.jsonc vars.ELEVENLABS_VOICE_ID (and .dev.vars). Previews are written to
 * ./voice-previews/*.mp3 so you can pick by ear (pass --pick N to save preview N instead of the first).
 */
import { writeFileSync, mkdirSync, existsSync, readFileSync } from "node:fs";

const key = process.env.ELEVENLABS_API_KEY ?? readDevVar("ELEVENLABS_API_KEY");
if (!key) { console.error("ELEVENLABS_API_KEY missing (env or .dev.vars)"); process.exit(1); }
const pick = Number(process.argv[process.argv.indexOf("--pick") + 1] || 0) || 0;

const description = "A grumpy, pompous Canada goose villain: raspy, nasal honking edge, mid-pitched, theatrical and petty, fast talker with dramatic pauses, cartoonish but not childish.";
const sample = "MY EGG. MY FLOOR. YOUR PROBLEM. You dropped it. You DROPPED it. Ten seconds? I have seen bread last longer. Fine. Keep your stupid legs. This is not over. I know where you live. It is here.";

async function main() {
  const res = await fetch("https://api.elevenlabs.io/v1/text-to-voice/design", {
    method: "POST",
    headers: { "xi-api-key": key!, "Content-Type": "application/json" },
    body: JSON.stringify({ voice_description: description, model_id: "eleven_ttv_v3", text: sample, loudness: 0.5, guidance_scale: 5 }),
  });
  if (!res.ok) { console.error("design failed", res.status, await res.text()); process.exit(1); }
  const data = (await res.json()) as { previews: { generated_voice_id: string; audio_base_64: string; media_type: string }[] };
  mkdirSync("voice-previews", { recursive: true });
  data.previews.forEach((p, i) => {
    const ext = p.media_type.includes("mpeg") ? "mp3" : "wav";
    writeFileSync(`voice-previews/preview_${i}.${ext}`, Buffer.from(p.audio_base_64, "base64"));
    console.log(`preview ${i}: voice-previews/preview_${i}.${ext} (${p.generated_voice_id})`);
  });
  const chosen = data.previews[Math.min(pick, data.previews.length - 1)];
  const save = await fetch("https://api.elevenlabs.io/v1/text-to-voice", {
    method: "POST",
    headers: { "xi-api-key": key!, "Content-Type": "application/json" },
    body: JSON.stringify({ voice_name: "GOOSED goose", voice_description: description, generated_voice_id: chosen.generated_voice_id, labels: { game: "goosed" } }),
  });
  if (!save.ok) { console.error("save failed", save.status, await save.text()); process.exit(1); }
  const saved = (await save.json()) as { voice_id: string };
  console.log("\nELEVENLABS_VOICE_ID=" + saved.voice_id);
  console.log("Put it in wrangler.jsonc vars and .dev.vars, then run: npm run pregen");
}

function readDevVar(name: string): string | undefined {
  if (!existsSync(".dev.vars")) return undefined;
  const m = readFileSync(".dev.vars", "utf8").match(new RegExp(`^${name}=(.*)$`, "m"));
  return m?.[1]?.trim();
}

main().catch((e) => { console.error(e); process.exit(1); });
