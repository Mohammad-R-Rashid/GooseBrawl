import { Agent } from "agents";
import * as Sentry from "@sentry/cloudflare";
import type { Env, Beat, EventPayload } from "./env";
import { BEATS } from "./env";
import { namePersona } from "./tools/openai";
import { writeAnyLine, transcribeAny } from "./writer";
import { EMPTY_INTEL, fastIntel, findEcho, deepIntel, intelForPrompt, type Intel } from "./intel";
import { elasticOn, indexQuietly, shoutDoc, lineDoc, runDoc } from "./tools/elastic";
import { speak, voiceFor, delivery } from "./tools/elevenlabs";
import { sha256Hex, audioKey, getCachedWav, putWav } from "./tools/r2";
import { bankLine, personaVoice } from "./bank";

/** What the goose remembers about this player (Durable Object state, persisted in SQLite, survives app launches). */
export interface GooseMemory {
  name: string;
  title: string;
  voice: "male" | "female";  // fixed once named: the same goose never changes voice
  grudge: number;      // rounds the goose has won against this player
  rounds: number;      // rounds started, ever
  outlasted: number;   // rounds the player won
  bestTime: number;    // player's best survival, seconds
  lastTime: number;    // last round's survival, seconds
  lastShout: string;   // the last thing the player yelled (transcript)
  breads: number;
  dodges: number;
  lastLine: string;
  created: number;
  lastSeen: number;
  /** How many times each beat fired this round: the k-th dodge gets the next bank line. */
  counts: Record<string, number>;
  /** The Elastic case file (crowd stats, movement profile, shouts, agent notes); null until the first session with Elastic on. */
  intel?: Intel | null;
}

interface SessionBody {
  roundsThisSession?: number;
  gamesPlayed?: number;
  bestTime?: number;
}

interface EventBody {
  kind: Beat;
  payload?: EventPayload;
}

const TOTAL_BUDGET_MS = 4200;
/** How long session start waits for the fast Elastic tier before writing the intro without it. */
const INTEL_BUDGET_MS = 900;
/** The phone stops waiting for a beat after this (GooseVoice.SayBeat deadline: 2.5 s, yell 3.5 s) and plays its bank line instead. */
const PHONE_DEADLINE_MS: Partial<Record<Beat, number>> = { yell: 3300, taunt10: 2300, bread: 2300, dodge: 2300, rage: 2300, caught: 2300, outlasted: 2300 };
/** What a live TTS round-trip costs on top of the line; when it would push the answer past the phone's deadline, the words go alone. */
const TTS_COST_MS = 1100;

export class GooseBrainBase extends Agent<Env, GooseMemory> {
  initialState: GooseMemory = {
    name: "", title: "", voice: "male", grudge: 0, rounds: 0, outlasted: 0, bestTime: 0, lastTime: 0, lastShout: "",
    breads: 0, dodges: 0, lastLine: "", created: Date.now(), lastSeen: Date.now(), counts: {},
  };

  async onStart() {
    this.sql`CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, payload TEXT, line TEXT, source TEXT, at INTEGER NOT NULL)`;
  }

  async onRequest(request: Request): Promise<Response> {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, "");
    try { Sentry.setConversationId(this.name); } catch { /* optional */ }
    try {
      if (request.method === "GET" && path.endsWith("/memory")) return this.json({ deviceId: this.name, memory: this.state, events: this.recentEvents() });
      if (request.method !== "POST") return new Response("Method not allowed", { status: 405 });
      if (path.endsWith("/session")) return await this.session(request, url);
      if (path.endsWith("/event")) return await this.event(request, url);
      if (path.endsWith("/reset")) { this.setState({ ...this.initialState, created: Date.now() }); return this.json({ ok: true }); }
      return new Response("Not found", { status: 404 });
    } catch (e) {
      Sentry.captureException(e);
      Sentry.logger.error("goose-brain request failed", { path, error: String(e) });
      return this.json({ error: String(e) }, 500);
    }
  }

  /** A goose keeps the voice its name owns: memories written before the voice field existed get it back. */
  private repairVoice(memory: GooseMemory): GooseMemory {
    const owned = personaVoice(memory.name);
    const voice: "male" | "female" = owned ?? (memory.voice === "female" ? "female" : "male");
    if (voice === memory.voice) return memory;
    const fixed = { ...memory, voice };
    this.setState(fixed);
    Sentry.logger.info("goose voice repaired", { name: memory.name, from: memory.voice ?? "none", to: voice });
    return fixed;
  }

  // ------------------------------------------------------------------ workflow: session start
  /** Name the goose (once per player), remember the visit, and pre-write + pre-voice the intro line. */
  private async session(request: Request, url: URL): Promise<Response> {
    const body = (await request.json().catch(() => ({}))) as SessionBody;
    const started = Date.now();
    let memory = this.repairVoice(this.state);
    // Elastic fast tier, in parallel with naming: crowd stats, this player's history and movement profile, recent shouts.
    const intelP: Promise<Intel | null> = elasticOn(this.env) ? fastIntel(this.env, this.name, Math.max(memory.bestTime, body.bestTime ?? 0), memory.intel) : Promise.resolve(null);
    if (!memory.name) {
      const p = await namePersona(this.env, body.gamesPlayed ?? 0);
      memory = { ...memory, name: p.name, title: p.title, voice: p.voice, created: Date.now() };
      Sentry.logger.info("goose named", { name: p.name, title: p.title, voice: p.voice, source: p.source });
    }
    memory = { ...memory, rounds: memory.rounds + 1, lastSeen: Date.now(), bestTime: Math.max(memory.bestTime, body.bestTime ?? 0), counts: {} };
    this.setState(memory);
    this.ctx.waitUntil(this.touchBoard()); // the booth page's brain panel follows whoever just started a round
    const intel = await Promise.race([intelP, new Promise<null>((resolve) => setTimeout(() => resolve(null), INTEL_BUDGET_MS))]);
    if (intel) {
      memory = { ...memory, intel };
      this.setState(memory);
    } else if (elasticOn(this.env)) {
      Sentry.logger.warn("intel fast tier missed the budget", { budgetMs: INTEL_BUDGET_MS });
      this.ctx.waitUntil(intelP.then((late) => { if (late) this.setState({ ...this.state, intel: late }); }));
    }
    // Deep tier: the Agent Builder agent takes seconds, so it runs after the response and lands in memory for the next beat.
    if (this.env.ELASTIC_AGENT_ID && elasticOn(this.env)) this.ctx.waitUntil(this.refreshDeepIntel(memory.name));

    const payload: EventPayload = { roundsThisSession: body.roundsThisSession ?? 0 };
    const intro = await this.produce(memory.rounds > 1 ? "intro_again" : "intro", payload, memory, started);
    this.record("session", body, intro.text, intro.source);
    this.ctx.waitUntil(indexQuietly(this.env, [lineDoc({ deviceId: this.name, goose: memory.name, beat: memory.rounds > 1 ? "intro_again" : "intro", line: intro.text, mood: intro.mood, source: intro.source, round: memory.rounds })]));
    return this.json({
      deviceId: this.name,
      name: memory.name,
      title: memory.title,
      voice: memory.voice,
      grudge: memory.grudge,
      rounds: memory.rounds,
      returning: memory.rounds > 1,
      intro,
      ms: Date.now() - started,
    });
  }

  // ------------------------------------------------------------------ workflow: game event -> line -> voice
  private async event(request: Request, url: URL): Promise<Response> {
    const started = Date.now();
    let body: EventBody;
    let audio: ArrayBuffer | null = null;
    const ctype = request.headers.get("content-type") ?? "";
    if (ctype.includes("multipart/form-data")) {
      const form = await request.formData();
      body = JSON.parse(String(form.get("json") ?? "{}")) as EventBody;
      const file = form.get("audio");
      if (file instanceof File) audio = await file.arrayBuffer();
    } else {
      body = (await request.json()) as EventBody;
    }
    const kind = body.kind;
    if (!BEATS.includes(kind)) return this.json({ error: "unknown beat " + kind }, 400);
    const payload: EventPayload = body.payload ?? {};
    let memory = this.repairVoice(this.state);

    let transcript = payload.transcript ?? "";
    let lang: string | undefined;
    if (kind === "yell" && audio && audio.byteLength > 1000) {
      const heard = await transcribeAny(this.env, audio, memory.name || "the goose");
      transcript = heard.text;
      lang = heard.lang;
      Sentry.logger.info("player shouted", { transcript, lang: lang ?? "und", bytes: audio.byteLength });
    }
    if (kind === "yell" && transcript) {
      payload.transcript = transcript;
      memory = { ...memory, lastShout: transcript };
      // Elastic: has anyone shouted something like this (hybrid BM25 + Jina search, any language)? Then remember it for everyone after.
      const echo = await findEcho(this.env, transcript, this.name);
      memory = { ...memory, intel: { ...(memory.intel ?? EMPTY_INTEL), echo } };
      this.ctx.waitUntil(indexQuietly(this.env, [shoutDoc({ deviceId: this.name, goose: memory.name, transcript, lang, survival: payload.survival, tier: payload.tier, round: memory.rounds })]));
    }

    // Remember first, then write, so the line can reference the updated memory.
    switch (kind) {
      case "bread": memory = { ...memory, breads: memory.breads + 1 }; break;
      case "dodge": memory = { ...memory, dodges: memory.dodges + 1 }; break;
      case "caught": memory = { ...memory, grudge: memory.grudge + 1, lastTime: payload.survival ?? memory.lastTime, bestTime: Math.max(memory.bestTime, payload.survival ?? 0) }; break;
      case "outlasted": memory = { ...memory, outlasted: memory.outlasted + 1, lastTime: payload.survival ?? memory.lastTime, bestTime: Math.max(memory.bestTime, payload.survival ?? 0) }; break;
    }
    this.setState(memory);
    if (kind === "caught" || kind === "outlasted") {
      this.ctx.waitUntil(indexQuietly(this.env, [runDoc({ deviceId: this.name, goose: memory.name, outcome: kind === "caught" ? "GOOSED" : "OUTLASTED", survival: payload.survival ?? 0, honks: payload.honks, dodges: payload.dodges, breads: payload.breads, tier: payload.tier, round: memory.rounds, grudge: memory.grudge, lastShout: memory.lastShout })]));
    }

    const line = await this.produce(kind, payload, memory, started);
    this.setState({ ...this.state, lastLine: line.text });
    this.record(kind, payload, line.text, line.source);
    this.ctx.waitUntil(indexQuietly(this.env, [lineDoc({ deviceId: this.name, goose: memory.name, beat: kind, line: line.text, mood: line.mood, source: line.source, survival: payload.survival, tier: payload.tier, round: memory.rounds })]));
    return this.json({ ...line, transcript, ms: Date.now() - started });
  }

  /** Write the line (OpenAI, bank fallback) and voice it (ElevenLabs -> R2), inside the total time budget. */
  private async produce(beat: Beat, payload: EventPayload, memory: GooseMemory, started: number) {
    const counts = { ...(memory.counts ?? {}) };
    const repeat = counts[beat] ?? 0;
    counts[beat] = repeat + 1;
    this.setState({ ...this.state, counts });
    const idx = memory.rounds * 3 + BEATS.indexOf(beat) + repeat;
    const written = await writeAnyLine(this.env, beat, payload, this.memoryForPrompt(memory), idx);
    const text = written.line;
    let audioUrl: string | null = null;
    let cached = false;
    const remaining = TOTAL_BUDGET_MS - (Date.now() - started);
    const phoneDeadline = PHONE_DEADLINE_MS[beat];
    // A written line the phone would never hear (it stops waiting and plays the bank) is better delivered as words alone:
    // GooseVoice shows it as a subtitle with a honk. Cached audio is still checked: it costs one HEAD.
    const ttsFits = !phoneDeadline || Date.now() - started + TTS_COST_MS < phoneDeadline;
    if (remaining > 800) {
      try {
        const voiceTag = voiceFor(this.env, memory.voice);
        const hash = await sha256Hex(`${voiceTag}|${this.env.ELEVENLABS_MODEL}|${delivery(this.env).signature}|${text}`);
        const key = audioKey(hash);
        cached = await getCachedWav(this.env, key);
        if (!cached && !ttsFits) {
          Sentry.logger.info("tts skipped: past the phone's deadline, words only", { beat, elapsed: Date.now() - started, phoneDeadline });
        } else if (!cached) {
          const result = await Promise.race([
            speak(this.env, text, voiceTag),
            new Promise<null>((resolve) => setTimeout(() => resolve(null), Math.max(500, remaining - 200))),
          ]);
          if (result) await putWav(this.env, key, result.wav);
          else Sentry.logger.warn("tts timed out, returning text only", { beat, remaining });
          if (result) audioUrl = `/audio/${hash}`;
        } else {
          audioUrl = `/audio/${hash}`;
        }
      } catch (e) {
        Sentry.captureException(e);
        Sentry.logger.error("voice failed, returning text only", { beat, error: String(e) });
      }
    } else {
      Sentry.logger.warn("no time budget left for tts", { beat, remaining });
    }
    Sentry.logger.info("goose line", { beat, text, source: written.source, mood: written.mood, cached, audio: audioUrl !== null, ms: Date.now() - started });
    return { text, mood: written.mood, source: written.source, audioUrl, cached, bankFallback: bankLine(beat, idx) };
  }

  /** Tell the global Goose Board this device is the one playing now (fire-and-forget, never on the request path). */
  private async touchBoard() {
    try {
      const stub = this.env.GooseBoard.get(this.env.GooseBoard.idFromName("global"));
      await stub.fetch(new Request("https://goose-board/touch", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ deviceId: this.name }) }));
    } catch (e) {
      Sentry.logger.warn("board touch failed", { error: String(e) });
    }
  }

  private memoryForPrompt(m: GooseMemory) {
    return {
      gooseName: m.name, gooseTitle: m.title, roundsPlayed: m.rounds, timesGooseWon: m.grudge, timesPlayerWon: m.outlasted,
      playerBestSeconds: Math.round(m.bestTime), playerLastSeconds: Math.round(m.lastTime), lastShout: m.lastShout,
      breadsThrown: m.breads, lungesDodged: m.dodges, lastLine: m.lastLine,
      intel: intelForPrompt(m.intel),
    };
  }

  /** Deep tier: the Agent Builder agent reads the indices itself; its notes land in memory for the next beat. */
  private async refreshDeepIntel(gooseName: string) {
    const deep = await deepIntel(this.env, this.name, gooseName);
    if (!deep) return;
    this.setState({ ...this.state, intel: { ...(this.state.intel ?? EMPTY_INTEL), deep } });
  }

  private record(kind: string, payload: unknown, line: string, source: string) {
    try {
      this.sql`INSERT INTO events (kind, payload, line, source, at) VALUES (${kind}, ${JSON.stringify(payload ?? {})}, ${line}, ${source}, ${Date.now()})`;
    } catch (e) {
      Sentry.logger.warn("event insert failed", { error: String(e) });
    }
  }

  private recentEvents() {
    try {
      return this.sql`SELECT kind, line, source, at FROM events ORDER BY id DESC LIMIT 20`;
    } catch {
      return [];
    }
  }

  private json(data: unknown, status = 200): Response {
    return new Response(JSON.stringify(data), { status, headers: { "content-type": "application/json", "access-control-allow-origin": "*" } });
  }
}
