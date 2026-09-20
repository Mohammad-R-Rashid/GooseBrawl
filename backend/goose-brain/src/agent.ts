import { Agent } from "agents";
import * as Sentry from "@sentry/cloudflare";
import type { Env, Beat, EventPayload } from "./env";
import { BEATS } from "./env";
import { writeLine, namePersona, transcribe } from "./tools/openai";
import { speak, voiceFor, delivery } from "./tools/elevenlabs";
import { sha256Hex, audioKey, getCachedWav, putWav } from "./tools/r2";
import { bankLine } from "./bank";

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

  // ------------------------------------------------------------------ workflow: session start
  /** Name the goose (once per player), remember the visit, and pre-write + pre-voice the intro line. */
  private async session(request: Request, url: URL): Promise<Response> {
    const body = (await request.json().catch(() => ({}))) as SessionBody;
    const started = Date.now();
    let memory = this.state;
    if (!memory.name) {
      const p = await namePersona(this.env, body.gamesPlayed ?? 0);
      memory = { ...memory, name: p.name, title: p.title, voice: p.voice, created: Date.now() };
      Sentry.logger.info("goose named", { name: p.name, title: p.title, voice: p.voice, source: p.source });
    }
    memory = { ...memory, rounds: memory.rounds + 1, lastSeen: Date.now(), bestTime: Math.max(memory.bestTime, body.bestTime ?? 0), counts: {} };
    this.setState(memory);

    const payload: EventPayload = { roundsThisSession: body.roundsThisSession ?? 0 };
    const intro = await this.produce(memory.rounds > 1 ? "intro_again" : "intro", payload, memory, started);
    this.record("session", body, intro.text, intro.source);
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
    let memory = this.state;

    let transcript = payload.transcript ?? "";
    if (kind === "yell" && audio && audio.byteLength > 1000) {
      transcript = await transcribe(this.env, audio, memory.name || "the goose");
      Sentry.logger.info("player shouted", { transcript, bytes: audio.byteLength });
    }
    if (kind === "yell" && transcript) { payload.transcript = transcript; memory = { ...memory, lastShout: transcript }; }

    // Remember first, then write, so the line can reference the updated memory.
    switch (kind) {
      case "bread": memory = { ...memory, breads: memory.breads + 1 }; break;
      case "dodge": memory = { ...memory, dodges: memory.dodges + 1 }; break;
      case "caught": memory = { ...memory, grudge: memory.grudge + 1, lastTime: payload.survival ?? memory.lastTime, bestTime: Math.max(memory.bestTime, payload.survival ?? 0) }; break;
      case "outlasted": memory = { ...memory, outlasted: memory.outlasted + 1, lastTime: payload.survival ?? memory.lastTime, bestTime: Math.max(memory.bestTime, payload.survival ?? 0) }; break;
    }
    this.setState(memory);

    const line = await this.produce(kind, payload, memory, started);
    this.setState({ ...this.state, lastLine: line.text });
    this.record(kind, payload, line.text, line.source);
    return this.json({ ...line, transcript, ms: Date.now() - started });
  }

  /** Write the line (OpenAI, bank fallback) and voice it (ElevenLabs -> R2), inside the total time budget. */
  private async produce(beat: Beat, payload: EventPayload, memory: GooseMemory, started: number) {
    const counts = { ...(memory.counts ?? {}) };
    const repeat = counts[beat] ?? 0;
    counts[beat] = repeat + 1;
    this.setState({ ...this.state, counts });
    const idx = memory.rounds * 3 + BEATS.indexOf(beat) + repeat;
    const written = await writeLine(this.env, beat, payload, this.memoryForPrompt(memory), idx);
    const text = written.line;
    let audioUrl: string | null = null;
    let cached = false;
    const remaining = TOTAL_BUDGET_MS - (Date.now() - started);
    if (remaining > 800) {
      try {
        const voiceTag = voiceFor(this.env, memory.voice);
        const hash = await sha256Hex(`${voiceTag}|${this.env.ELEVENLABS_MODEL}|${delivery(this.env).signature}|${text}`);
        const key = audioKey(hash);
        cached = await getCachedWav(this.env, key);
        if (!cached) {
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

  private memoryForPrompt(m: GooseMemory) {
    return {
      gooseName: m.name, gooseTitle: m.title, roundsPlayed: m.rounds, timesGooseWon: m.grudge, timesPlayerWon: m.outlasted,
      playerBestSeconds: Math.round(m.bestTime), playerLastSeconds: Math.round(m.lastTime), lastShout: m.lastShout,
      breadsThrown: m.breads, lungesDodged: m.dodges, lastLine: m.lastLine,
    };
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
