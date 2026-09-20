/**
 * Voice lab: the same two lines rendered with different ElevenLabs settings / voices, so a human can A/B the goose's tone
 * before the bank is regenerated. Cheap (~500 characters). Output: ./voice-lab/<variant>_<m|f>.wav + README.txt
 *   npx tsx scripts/voice-lab.ts
 */
import { writeFileSync, mkdirSync, existsSync, readFileSync } from "node:fs";

const key = process.env.ELEVENLABS_API_KEY ?? readDevVar("ELEVENLABS_API_KEY");
if (!key) { console.error("ELEVENLABS_API_KEY missing"); process.exit(1); }
const MODEL = "eleven_v3_conversational";
const VOICES = {
  callum: "N2lVS1w4EtoT3dr4eOWO", laura: "FGY2WhTYpPnrIDTdsKH5",
  harry: "SOYHLrjzK2X1ezoPC6cr", lily: "pFZP5JQG7iQjIQuC4Bku",
  charlie: "IKne3meq5aSn9XLyUdCD", jessica: "cgSgspJ2msm6clMCkdW9",
};
const LINE_M = "TEN SECONDS. I'VE SEEN BREAD LAST LONGER.";
const LINE_F = "[sighs] YOU DROPPED IT. YOU DROPPED IT.";
const sentence = (s: string) => s.replace(/\[[^\]]*\]\s*/g, (t) => t).split(/(\[[^\]]*\])/).map((part) =>
  part.startsWith("[") ? part : part.toLowerCase().replace(/(^|[.!?]\s+)([a-z])/g, (_m, a, b) => a + b.toUpperCase()).replace(/\bi\b/g, "I").replace(/\bi've\b/g, "I've")).join("");

interface Variant { name: string; note: string; m: string; f: string; text: (s: string) => string; settings: Record<string, number | boolean>; }
const V: Variant[] = [
  { name: "A_current", note: "what the bank has now: ALL CAPS text, stability 0.45, style 0.45, speed 1.05 (Callum / Laura)", m: VOICES.callum, f: VOICES.laura, text: (s) => s, settings: { stability: 0.45, similarity_boost: 0.75, style: 0.45, use_speaker_boost: true, speed: 1.05 } },
  { name: "B_theatrical", note: "sentence case (caps read as shouting in v3), creative stability 0.2, style 0.7, speed 1.1: more acted, more petty", m: VOICES.callum, f: VOICES.laura, text: sentence, settings: { stability: 0.2, similarity_boost: 0.7, style: 0.7, use_speaker_boost: true, speed: 1.1 } },
  { name: "C_snappy_mischief", note: "sentence case with a [mischievously] tag up front, stability 0.3, style 0.55, speed 1.18: quicker, scheming", m: VOICES.callum, f: VOICES.laura, text: (s) => "[mischievously] " + sentence(s).replace(/^\[[^\]]*\]\s*/, ""), settings: { stability: 0.3, similarity_boost: 0.7, style: 0.55, use_speaker_boost: true, speed: 1.18 } },
  { name: "D_deadpan", note: "sentence case, stability 0.7, style 0.2, speed 1.0: dry, bored, superior", m: VOICES.callum, f: VOICES.laura, text: sentence, settings: { stability: 0.7, similarity_boost: 0.75, style: 0.2, use_speaker_boost: true, speed: 1.0 } },
  { name: "E_alt_harry_lily", note: "B settings on other stock voices: Harry (Fierce Warrior, rough) / Lily (Velvety Actress, British)", m: VOICES.harry, f: VOICES.lily, text: sentence, settings: { stability: 0.2, similarity_boost: 0.7, style: 0.7, use_speaker_boost: true, speed: 1.1 } },
  { name: "F_alt_charlie_jessica", note: "B settings on Charlie (Deep, Energetic, Australian) / Jessica (Playful, Bright)", m: VOICES.charlie, f: VOICES.jessica, text: sentence, settings: { stability: 0.2, similarity_boost: 0.7, style: 0.7, use_speaker_boost: true, speed: 1.1 } },
];

async function tts(voice: string, text: string, settings: Record<string, number | boolean>): Promise<Buffer> {
  const res = await fetch(`https://api.elevenlabs.io/v1/text-to-speech/${voice}?output_format=wav_22050`, {
    method: "POST", headers: { "xi-api-key": key!, "Content-Type": "application/json" },
    body: JSON.stringify({ text, model_id: MODEL, voice_settings: settings }),
  });
  if (!res.ok) throw new Error(`${res.status} ${(await res.text()).slice(0, 160)}`);
  return Buffer.from(await res.arrayBuffer());
}

async function main() {
  mkdirSync("voice-lab", { recursive: true });
  const readme: string[] = ["GOOSED. voice lab: listen, pick a letter (and optionally 'goosified'), tell Claude.", ""];
  for (const v of V) {
    for (const [sex, voice, line] of [["m", v.m, LINE_M], ["f", v.f, LINE_F]] as const) {
      const text = v.text(line);
      const file = `voice-lab/${v.name}_${sex}.wav`;
      if (!existsSync(file)) {
        process.stdout.write(`${file}: ${text} ... `);
        try { writeFileSync(file, await tts(voice, text, v.settings)); console.log("ok"); }
        catch (e) { console.log("FAILED " + e); }
      }
    }
    readme.push(`${v.name}: ${v.note}`);
  }
  readme.push("", "*_goosified.wav: the same clip with the runtime effect the phone can apply (pitch x1.1, high-pass 220 Hz, light saturation).");
  writeFileSync("voice-lab/README.txt", readme.join("\n") + "\n");
  console.log("done -> voice-lab/");
}

function readDevVar(name: string): string | undefined {
  if (!existsSync(".dev.vars")) return undefined;
  const m = readFileSync(".dev.vars", "utf8").match(new RegExp(`^${name}=(.*)$`, "m"));
  return m?.[1]?.trim() || undefined;
}
main().catch((e) => { console.error(e); process.exit(1); });
