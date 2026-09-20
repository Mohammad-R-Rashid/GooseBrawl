export interface Env {
  GooseBrain: DurableObjectNamespace;
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
