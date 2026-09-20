import { describe, expect, it } from "vitest";
import { cleanPlayerName, cleanGooseName, clampSeconds, outcomeOf, rankOf } from "../src/board-util";

describe("goose board", () => {
  it("cleans player names", () => {
    expect(cleanPlayerName("mo rashid!")).toBe("MO RASHID");
    expect(cleanPlayerName("")).toBe("SOMEONE");
    expect(cleanPlayerName("   ")).toBe("SOMEONE");
    expect(cleanPlayerName("abcdefghijklmnop")).toBe("ABCDEFGHIJKL");
    expect(cleanPlayerName("R2-D2")).toBe("R2D2");
    expect(cleanPlayerName("I will kill")).toBe("SOMEONE");
    expect(cleanGooseName("kevin")).toBe("KEVIN");
    expect(cleanGooseName("")).toBe("GOOSE");
  });
  it("clamps times and outcomes", () => {
    expect(clampSeconds(23.456)).toBe(23.5);
    expect(clampSeconds(-3)).toBe(0);
    expect(clampSeconds("nope")).toBe(0);
    expect(clampSeconds(99999)).toBe(600);
    expect(outcomeOf("outlasted")).toBe("OUTLASTED");
    expect(outcomeOf("whatever")).toBe("GOOSED");
  });
  it("ranks with shared ties", () => {
    expect(rankOf(10, [])).toBe(1);
    expect(rankOf(10, [30, 20, 5])).toBe(3);
    expect(rankOf(20, [30, 20, 5])).toBe(2);
    expect(rankOf(30, [30, 20, 5])).toBe(1);
  });
});
