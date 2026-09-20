export interface Env {
  GooseBrain: DurableObjectNamespace;
  /** The Goose Board (one global instance named "global"): today's runs + who is playing now. */
  GooseBoard: DurableObjectNamespace;
  BUCKET: R2Bucket;
  OPENAI_API_KEY?: string;
  ELEVENLABS_API_KEY?: string;
  SENTRY_DSN: string;
  SENTRY_ENVIRONMENT: string;
  ELEVENLABS_VOICE_ID: string;
  ELEVENLABS_VOICE_ID_FEMALE: string;
  ELEVENLABS_MODEL: string;
  ELEVENLABS_STABILITY?: string;
  ELEVENLABS_STYLE?: string;
  ELEVENLABS_SPEED?: string;
  /** "caps" = send the script as written (v3 reads capitals as shouting); "sentence" = sentence case, tags kept. */
  ELEVENLABS_TEXT_CASE?: string;
  /** Shared key for POST /cache/<hash> (pre-generated bank upload). */
  WARM_KEY?: string;
  OPENAI_MODEL: string;
  OPENAI_TRANSCRIBE_MODEL: string;
  MOCK_AI: string;
  /** Elasticsearch (Elastic Cloud Serverless) endpoint; empty = the Elastic context layer is off (everything still works). */
  ELASTIC_URL?: string;
  /** Kibana endpoint of the same project, for the Agent Builder converse API. */
  ELASTIC_KIBANA_URL?: string;
  /** Elastic API key (secret: wrangler secret put ELASTIC_API_KEY). */
  ELASTIC_API_KEY?: string;
  /** Agent Builder agent id (scripts/elastic-agent.ts); empty = no deep tier. */
  ELASTIC_AGENT_ID?: string;
  /** Rerank inference endpoint id for the shout search; empty = RRF only. */
  ELASTIC_RERANK_ID?: string;
  /** Workers AI binding: Whisper (ears) and Llama (writer) when there is no OpenAI key. */
  AI?: Ai;
}

export type Beat = "intro" | "taunt10" | "yell" | "bread" | "dodge" | "rage" | "caught" | "outlasted" | "intro_again";
export const BEATS: Beat[] = ["intro", "taunt10", "yell", "bread", "dodge", "rage", "caught", "outlasted", "intro_again"];

export interface EventPayload {
  survival?: number;
  honks?: number;
  dodges?: number;
  breads?: number;
  tier?: number;
  roundsThisSession?: number;
  transcript?: string;
}

/** No OpenAI key (or MOCK_AI=1): lines come from the bank, shouts are not transcribed. */
export function mockText(env: Env): boolean {
  return env.MOCK_AI === "1" || !env.OPENAI_API_KEY;
}

/** No ElevenLabs key (or MOCK_AI=1): synthesized goose-babble instead of the designed voice. */
export function mockVoice(env: Env): boolean {
  return env.MOCK_AI === "1" || !env.ELEVENLABS_API_KEY;
}

/** Kept for callers that only care whether anything is mocked. */
export function mockAI(env: Env): boolean {
  return mockText(env) || mockVoice(env);
}
