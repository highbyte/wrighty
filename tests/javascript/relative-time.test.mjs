import test from "node:test";
import assert from "node:assert/strict";
import { localizeRelativeTimes, relativeTimeLabel } from "../../src/Highbyte.Wrighty.Web/Assets/relative-time.mjs";

const now = Date.parse("2026-08-21T12:00:00Z");

test("relative labels cover past, future, and invalid timestamps", () => {
  assert.equal(relativeTimeLabel(now - 10_000, now), "just now");
  assert.equal(relativeTimeLabel(now - 3_600_000, now), "1h ago");
  assert.equal(relativeTimeLabel(now - 3 * 86_400_000, now), "3d ago");
  assert.equal(relativeTimeLabel(now + 120_000, now), "in 2m");
  assert.equal(relativeTimeLabel("invalid", now), null);
});

test("rendering retains the field label and ignores invalid values", () => {
  const valid = {
    dateTime: "2026-08-21T10:00:00Z",
    dataset: { timeLabel: "Created" },
    textContent: "absolute",
    getAttribute: () => null
  };
  const invalid = {
    dateTime: "invalid",
    dataset: {},
    textContent: "unchanged",
    getAttribute: () => null
  };
  localizeRelativeTimes({ querySelectorAll: () => [valid, invalid] }, now);
  assert.equal(valid.textContent, "Created 2h ago");
  assert.equal(invalid.textContent, "unchanged");
  localizeRelativeTimes({}, now);
});
