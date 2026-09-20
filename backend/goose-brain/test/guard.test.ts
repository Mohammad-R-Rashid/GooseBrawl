import { describe, expect, it } from "vitest";
import { sanitizeLine, sanitizeName } from "../src/guard";
import { bankLine, bankPersona } from "../src/bank";

describe("guard", () => {
  it("keeps a clean line", () => {
    expect(sanitizeLine("[laughs] GOT YOU. TELL YOUR FRIENDS.", "x")).toEqual({ line: "[laughs] GOT YOU. TELL YOUR FRIENDS.", ok: true });
  });
  it("drops unknown tags and keeps one", () => {
    expect(sanitizeLine("[angry] [laughs] [sighs] FINE.", "x").line).toBe("[laughs] FINE.");
  });
  it("falls back on banned words and empties", () => {
    expect(sanitizeLine("I will kill you", "fallback").line).toBe("fallback");
    expect(sanitizeLine("", "fallback").ok).toBe(false);
  });
  it("caps length", () => {
    expect(sanitizeLine("A".repeat(300), "x").line.length).toBeLessThanOrEqual(141);
  });
  it("names", () => {
    expect(sanitizeName("kevin", "X")).toBe("KEVIN");
    expect(sanitizeName("", "X")).toBe("X");
  });
  it("bank is deterministic", () => {
    expect(bankLine("intro", 0)).toBe(bankLine("intro", 10));
    expect(bankLine("dodge", 1)).not.toBe(bankLine("dodge", 2));
    expect(bankPersona(0).name).toBe("KEVIN");
  });
});
