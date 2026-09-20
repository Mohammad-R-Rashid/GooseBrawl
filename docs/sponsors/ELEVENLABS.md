# MLH: Best Use of ElevenLabs

**Claim:** the goose talks, and ElevenLabs is its voice. Every persona owns one stock voice for life, the delivery was
chosen by ear from an A/B lab, and the whole script is pre-voiced so the demo never waits on the API.

## Where it lives

| File | Role |
|---|---|
| `backend/goose-brain/src/tools/elevenlabs.ts` | `speak(env, text, voiceId)`: TTS to WAV 22.05 kHz, delivery settings, model fallback, `voiceFor(persona)` |
| `backend/goose-brain/src/agent.ts` | picks the voice from the goose's memory (`voice: "male" | "female"`), caches by voice + delivery + text |
| `backend/goose-brain/scripts/voice-lab.ts` | the A/B lab: one line in six deliveries and three voice pairs (`voice-lab/*.wav`) |
| `backend/goose-brain/scripts/pregen.ts` | voices the script for both voices into `Assets/Resources/GooseVoice/{m,f}/<beat>_<n>.wav` |
| `backend/goose-brain/scripts/upload-bank.ts` | pushes those WAVs into the Worker's R2 cache under the same keys (no second TTS pass) |
| `backend/goose-brain/scripts/design-voice.ts` | Voice Design API flow (needs a paid plan; kept for when that changes) |
| `Assets/Scripts/Goose/GooseVoice.cs` | plays the clip on the goose's 3D voice source, beak bob, subtitle, honk suppression, goose EQ |
| `Assets/Scripts/Core/GoosePersona.cs`, `GooseLines.cs` | persona -> voice mapping, offline script |

## Voices and delivery

| Personas | Voice | Id |
|---|---|---|
| KEVIN, GARY, DR. HONK, LORD FEATHERINGTON, STEVE, CHAD, DUKE, BARRY | Callum, "Husky Trickster" | `N2lVS1w4EtoT3dr4eOWO` |
| BRENDA, MARGARET, AGNES, PAMELA | Laura, "Enthusiast, Quirky Attitude" | `FGY2WhTYpPnrIDTdsKH5` |

Model `eleven_v3_conversational` (expressive, audio tags), fallback `eleven_flash_v2_5` with tags stripped. Delivery
"theatrical" (chosen in the lab): the script is sent in **sentence case** (v3 reads capitals as shouting; the HUD still
shows caps), stability 0.2, style 0.7, speed 1.1. Lines carry at most one tag from `[laughs] [sighs] [whispers]
[sarcastic] [excited] [mischievously]`. The content guard limits lines to 140 characters and one tag.

The voice is decided once, when the goose is named, and stored in the Durable Object memory and mirrored in PlayerPrefs:
the same goose never changes voice, and a female-named goose always speaks with the female voice.

## The pipeline

```
line text ──► shapeText (sentence case, tags kept) ──► POST /v1/text-to-speech/{voice}?output_format=wav_22050
                                                        { text, model_id, voice_settings: { stability, similarity_boost, style, use_speaker_boost, speed } }
          ◄── WAV bytes ──► R2 key sha256(voiceId | model | "s0.2-y0.7-p1.1-sentence" | text) ──► GET /audio/<key>
Unity: UnityWebRequestMultimedia.GetAudioClip(url, AudioType.WAV) ──► goose voiceSource (3D, behind-you low-pass, slow-mo pitch)
       + goose character: pitch x1.1, high-pass 220 Hz, distortion 0.1 (AudioManager.voicePitch / voiceHighPassHz / voiceDistortion)
```

Three tiers of availability, all the same voice:
1. **Live**: the Worker returns a URL to the cached WAV (all 168 script lines are pre-uploaded, so this is a cache hit;
   a novel line would be voiced on the fly within the 4.2 s budget).
2. **Offline bank**: `Assets/Resources/GooseVoice/{m,f}/` holds the same 168 clips (Vorbis 0.7 in the build).
3. **Last resort**: synthesized goose-babble (`GooseVoice.Babble`) with the subtitle carrying the words.

## Budget

Free tier, 10k characters per month. The script is 3,159 characters; two voices plus the lab and the first live cache
cost about 8.8k this month. Because every line is cached (R2 + on-device), the demo itself costs nothing. Tone changes
are voiced once locally (`npm run pregen -- --force`) and uploaded (`npx tsx scripts/upload-bank.ts`), never twice.

## FAQ (questions we expect from the ElevenLabs judges)

**Is the voice generated live or canned?**
Both, on purpose. The Worker voices any line through the API (that path is exercised by `/warm` and by any new line),
and the script lines are cached so a 45-second chase never stalls. The demo plays cached API output.

**Why stock voices instead of a designed one?**
Voice Design is paid-plan only (`design-voice.ts` returned 403 `feature_not_available` on the free tier). We ran an A/B lab
on six deliveries and three voice pairs (`scripts/voice-lab.ts`, output in `voice-lab/`) and picked Callum and Laura with
the theatrical delivery by ear.

**Why sentence case if the game shows all caps?**
v3 treats capitalization as emphasis; an all-caps script came out as monotone shouting. Sentence case with one tag per
line gave the petty, acted delivery the character needs. The subtitle keeps the caps for the audience.

**What makes it sound like a goose rather than a narrator?**
Delivery (creative stability, high style, snappier speed), the tags, and a runtime character pass on the phone: pitch up
10%, a 220 Hz high-pass and light distortion on the goose's spatial voice source, which also carries the "behind you"
low-pass and the slow-motion pitch drop. All three knobs are live in the Inspector.

**How is the voice tied to the character?**
`GooseLines.Personas` maps each name to a voice; the Worker stores it in the goose's memory when it names the goose; the
phone mirrors it. Female names get the female voice, and a goose keeps its voice across rounds and app launches.

**Does it use audio tags / expressiveness?**
Yes: `[sighs]`, `[laughs]`, `[whispers]`, `[sarcastic]`, `[excited]`, `[mischievously]`, at most one per line, on the
v3 conversational model; the Flash fallback strips them.

**What would you do with a paid plan?**
Design a proper goose voice (`design-voice.ts` is ready), and let the OpenAI writer produce fresh lines live, which the
same pipeline would voice within the budget.
