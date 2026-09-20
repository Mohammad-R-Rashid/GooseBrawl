# OpenAI: API Prizes

**Claim:** OpenAI is the goose's writer and its ears. Every spoken line is produced by a writer with the goose's memory
in front of it (name, grudge, what you shouted, how long you lasted, what you threw), through the Responses API with a
strict JSON schema, and the player's shout is turned into text by the transcription API so the reply answers what was
actually said. The 84-line script is the offline product of the same character sheet and is the fallback for anything
slow or rejected.

## Where it lives

| File | Role |
|---|---|
| `backend/goose-brain/src/tools/openai.ts` | `writeLine`, `namePersona`, `transcribe`: the three OpenAI tools of the agent |
| `backend/goose-brain/src/prompts.ts` | the character sheet, the beat hints, the JSON schemas (`goose_line`, `goose_persona`) |
| `backend/goose-brain/src/guard.ts` | content guard on model output (length, one audio tag, banned words) |
| `backend/goose-brain/src/agent.ts` | the workflow that feeds memory + event to the writer and stores the result |
| `backend/goose-brain/scripts/lines.json`, `Assets/Scripts/Core/GooseLines.cs` | the script bank: 84 lines across 9 beats, 12 personas |
| `Assets/Scripts/Core/YellDetector.cs` | captures the 2-second shout window as 16 kHz WAV for transcription |

## The writer

```
event -> memoryForPrompt(): gooseName, gooseTitle, roundsPlayed, timesGooseWon, timesPlayerWon, playerBestSeconds,
                             playerLastSeconds, lastShout, breadsThrown, lungesDodged, lastLine
      + BEAT_HINTS[beat]   (what just happened and how to react)
      + payload            (survival, honks, dodges, breads, tier, transcript)
POST /v1/responses  { model: "gpt-5.6-luna", reasoning: { effort: "none" }, instructions: CHARACTER_SHEET,
                      input: JSON, max_output_tokens: 120,
                      text: { format: { type: "json_schema", name: "goose_line", strict: true,
                              schema: { line: string, mood: smug|angry|hurt|gleeful|sore } } } }
-> sanitizeLine(line) -> speak(line) (ElevenLabs) -> phone
```

The character sheet (`CHARACTER_SHEET`): a petty, theatrical, pompous Canada goose, family-friendly, 3 to 14 words,
at most one ElevenLabs audio tag from a fixed list, reacting to the memory (quote the shout, mention last time, be a sore
loser after a dodge, gloat after a catch, pretend to have let the player win after being outlasted). Beats: `intro`,
`intro_again`, `taunt10`, `yell`, `bread`, `dodge`, `rage`, `caught`, `outlasted`.

The output is consumed by code, not by a person: the line is voiced, shown as a subtitle, stored in memory (`lastLine`)
and logged, so the schema is strict and every failure path (timeout, parse error, guard rejection) resolves to the script
line for that beat. `reasoning.effort: "none"` keeps the round trip short; the code retries once without the reasoning
parameter for models that reject it.

## The name

On first contact the agent asks for `{ name, title, voice }` (`goose_persona` schema): a plain first name or a mock-noble
one, an ALL CAPS self-important title, and which stock voice fits. The result is stored in the Durable Object memory and
never changes for that player, so the goose the judges meet is the goose they meet again.

## The ears

The phone's yell detector captures 0.3 s before and 1.7 s after the trigger (16 kHz mono WAV, about 64 KB) and uploads it
with the `yell` event as multipart. The Worker calls `POST /v1/audio/transcriptions` (`gpt-transcribe`, `languages: ["en"]`,
`response_format: "json"`, a vocabulary prompt with the goose's name and the words people shout at geese), stores the
transcript as `lastShout`, hands it to the writer, and the reply comes back quoting the player. The phone flinches the
goose locally the instant the shout is detected, so the API latency is hidden behind the animation.

## Guard rails

- strict JSON schemas on every call (no free-text parsing);
- `sanitizeLine`: 140 characters max, one audio tag from the allowed list, banned-word list, empty/too-short rejection;
- `sanitizeName`: letters only, 2 to 22 characters;
- a 4.2 s budget per event; text always answers, audio only if there is time;
- `Sentry.instrumentOpenAiClient`: every call is a `gen_ai.chat` span with token usage in Sentry's AI Agents view.

## Verify it yourself

`cd backend/goose-brain && npm run dev`, then `POST /agents/goose-brain/<id>/session` and `.../event` (see
[CLOUDFLARE.md](CLOUDFLARE.md)); the response field `source` is `"openai"` for a written line and `"bank"` for a script
line. `npm test` covers the guard and the deterministic bank.

## Codex (to be filled in by the team)

Record here how Codex was used during the build and one concrete way it improved the outcome (for example the Worker's
guard tests in `test/guard.test.ts`, the upload script, a review of `agent.ts`), with links or screenshots of the
sessions.

## FAQ (questions we expect from the OpenAI judges)

**What does the API power in the experience?**
The goose's words: every spoken beat is written from its memory of you, its name is invented on first contact, and the
reply to a shout is written from the transcript of what you said.

**Why structured outputs?**
The line and its mood are consumed by code (voice, subtitle, memory, logs); strict schemas remove parsing failures and
make the content guard simple.

**How do you keep a model-written goose safe on stage?**
A character sheet that forbids comments on people, strict schemas, a guard on length, tags and banned words, and the
script line as the fallback for anything rejected or slow.

**Why `gpt-5.6-luna` with reasoning off?**
A petty 3-14 word line needs no reasoning; the cost-optimized model with `reasoning.effort: "none"` gives the shortest
round trip and the lowest cost, and the same call shape works on the larger models if wanted.

**Why `gpt-transcribe` for a two-second clip?**
It is the recommended transcription model, accepts WAV, takes a vocabulary prompt (the goose's name, "go away", "shoo")
and a language hint, and the clip is small enough that the reply lands while the goose is still recovering from the
flinch.

**What happens when the writer is slow or offline?**
The phone plays the script line for that beat (same voice, deterministic pick), and the event is still remembered by the
agent; the trace shows `voice.deadline` or `brain.request_failed` so the fallback is visible.
