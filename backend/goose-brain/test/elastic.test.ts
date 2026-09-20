import { describe, expect, it } from "vitest";
import { telemetryDocs } from "../src/telemetry";
import { intelForPrompt, EMPTY_INTEL, type Intel } from "../src/intel";
import { shoutDoc, runDoc, IDX } from "../src/tools/elastic";

describe("telemetry ingest", () => {
  const sample = (t: number) => [t, 0.5, 1.4, -0.25, 2.0, 0, 0.1, 1.55, 0.8, 1.1, 2, 3, 59.4, 16.8, 9.2, 1, 1];

  it("turns compact rows into documents with worker-side timestamps", () => {
    const now = Date.parse("2026-09-20T03:00:00Z");
    const docs = telemetryDocs({ device: "abc123", goose: "KEVIN", round: 2, dur: 3, samples: [sample(0), sample(1.5), sample(3)] }, now);
    expect(docs).toHaveLength(3);
    expect(docs[0].index).toBe(IDX.telemetry);
    expect(docs[0].doc["@timestamp"]).toBe(new Date(now - 3000).toISOString());
    expect(docs[2].doc["@timestamp"]).toBe(new Date(now).toISOString());
    expect(docs[0].doc).toMatchObject({ device_id: "abc123", round_id: "abc123-2", goose: "KEVIN", state: "walk", tier: 2, anger: 3, dist: 1.55, player_speed: 0.8, goose_speed: 1.1, fps: 59.4, frame_ms: 16.8, gpu_ms: 9.2, thermal: 1 });
    expect(docs[0].doc.player).toEqual({ x: 0.5, y: 1.4, z: -0.25 });
    expect(docs[0].doc.player_pt).toEqual({ x: 0.5, y: -0.25 });
  });

  it("drops junk and sanitises the device id", () => {
    expect(telemetryDocs({ device: "", samples: [sample(0)] })).toEqual([]);
    expect(telemetryDocs({ device: "a/b c!", samples: [sample(0), [1, 2] as number[], "x" as unknown as number[]] })[0].doc.device_id).toBe("abc");
    expect(telemetryDocs({ device: "d", samples: [[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 99]] })[0].doc.state).toBe("unknown");
  });

  it("caps a batch", () => {
    const many = Array.from({ length: 1000 }, (_, i) => sample(i * 0.2));
    expect(telemetryDocs({ device: "d", samples: many }).length).toBe(400);
  });
});

describe("intel for the writer", () => {
  it("is absent when there is nothing to say", () => {
    expect(intelForPrompt(null)).toBeUndefined();
    expect(intelForPrompt(EMPTY_INTEL)).toBeUndefined();
  });

  it("rounds numbers and keeps only the parts that exist", () => {
    const intel: Intel = {
      ...EMPTY_INTEL,
      at: 1,
      crowd: { runs: 212, players: 61, avgSeconds: 19.4567, bestSeconds: 57.91, goosedPct: 88 },
      you: { runs: 3, avgSeconds: 11.26, bestSeconds: 14.0, ahead: 40, avgSpeed: 0.612, maxSpeed: 1.31, stillPct: 27, avgDistance: 1.94 },
      crowdShouts: [{ text: "va-t'en", lang: "fr" }, { text: "go away", lang: "en" }],
      echo: { text: "走开", lang: "zh", goose: "BRENDA" },
      mercy: true,
    };
    const out = intelForPrompt(intel) as Record<string, unknown>;
    expect(out.crowdToday).toEqual({ humansFaced: 61, rounds: 212, averageSeconds: 19.5, bestSeconds: 57.9, percentGoosed: 88 });
    expect((out.thisPlayer as Record<string, unknown>).averageSpeedMetersPerSecond).toBe(0.6);
    expect((out.thisPlayer as Record<string, unknown>).playersAheadOfThem).toBe(40);
    expect(out.thingsOtherPlayersShoutedToday).toEqual(["va-t'en (fr)", "go away"]);
    expect(out.someoneElseShoutedSomethingSimilar).toEqual({ text: "走开", language: "zh", toGoose: "BRENDA" });
    expect(String(out.mercy)).toContain("not cruel");
    expect(out.caseFileNotes).toBeUndefined();
    expect(out.thingsThisPlayerShoutedBefore).toBeUndefined();
  });
});

describe("documents", () => {
  it("writes the transcript into both the keyword and the semantic field", () => {
    const d = shoutDoc({ deviceId: "d", goose: "KEVIN", transcript: "laisse-moi tranquille", lang: "fr", round: 1, at: 0 });
    expect(d.index).toBe(IDX.shouts);
    expect(d.doc).toMatchObject({ transcript: "laisse-moi tranquille", transcript_semantic: "laisse-moi tranquille", lang: "fr", "@timestamp": "1970-01-01T00:00:00.000Z", seed: false });
  });
  it("records a finished round", () => {
    const d = runDoc({ deviceId: "d", goose: "GARY", outcome: "OUTLASTED", survival: 46.2, round: 4, grudge: 2 });
    expect(d.doc).toMatchObject({ outcome: "OUTLASTED", survival: 46.2, grudge: 2, honks: 0, last_shout: "" });
  });
});
