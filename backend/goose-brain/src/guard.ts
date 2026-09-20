/** Content guard for model output: short, one audio tag at most, nothing that would embarrass anyone on stage. */
export const ALLOWED_TAGS = ["laughs", "sighs", "whispers", "sarcastic", "excited", "curious", "mischievously", "exhales", "snorts"];
export const BANNED = [/\bkill\b/i, /\bdie\b/i, /\bstupid people\b/i, /\bfat\b/i, /\bugly\b/i, /\bsex/i, /\bdamn\b/i, /\bhell\b/i, /\bshit\b/i, /\bfuck/i, /\bnazi/i, /\bracis/i, /\bgun\b/i, /\bshoot\b/i, /\bsuicid/i];
const MAX_LEN = 140;

export function sanitizeLine(raw: string | undefined | null, fallback: string): { line: string; ok: boolean; reason?: string } {
  let line = (raw ?? "").replace(/\s+/g, " ").trim();
  if (!line) return { line: fallback, ok: false, reason: "empty" };
  // Keep at most one audio tag and only the documented ones.
  let tagsSeen = 0;
  line = line.replace(/\[([^\]]{1,24})\]/g, (_m, tag: string) => {
    const t = tag.trim().toLowerCase();
    if (tagsSeen >= 1 || !ALLOWED_TAGS.includes(t)) return "";
    tagsSeen++;
    return `[${t}]`;
  }).replace(/\s+/g, " ").trim();
  if (line.length > MAX_LEN) line = line.slice(0, MAX_LEN - 1).replace(/[,;:\s]+\S*$/, "") + ".";
  for (const re of BANNED) if (re.test(line)) return { line: fallback, ok: false, reason: "banned" };
  if (line.replace(/\[[^\]]*\]/g, "").trim().length < 2) return { line: fallback, ok: false, reason: "too_short" };
  return { line, ok: true };
}

export function sanitizeName(raw: string | undefined, fallback: string): string {
  let s = (raw ?? "").replace(/[^A-Za-z .'\-]/g, "").replace(/\s+/g, " ").trim().toUpperCase();
  if (s.length < 2 || s.length > 22) return fallback;
  for (const re of BANNED) if (re.test(s)) return fallback;
  return s;
}
