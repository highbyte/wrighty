import assert from "node:assert/strict";
import test from "node:test";

import {
  captureHostedLogView,
  consumeHostedLogRestore,
  restoreHostedLogView,
  revealHostedLogTail
} from "../../src/Highbyte.Wrighty.Web/Assets/hosted-log.mjs";

function fixture({ runId = "run-1", open = true, scrollTop = 0 } = {}) {
  const list = { scrollHeight: 1000, clientHeight: 200, scrollTop };
  const panel = {
    open,
    dataset: { workerRunId: runId },
    matches: selector => selector === "[data-hosted-worker-log-panel]",
    querySelector(selector) {
      if (selector === ".hosted-worker-log ol") return list;
      return null;
    }
  };
  return { panel, list };
}

test("a reader at the tail follows new log entries", () => {
  const current = fixture({ scrollTop: 790 });
  const replacement = fixture({ open: false });
  const view = captureHostedLogView(current.panel);
  replacement.list.scrollHeight = 1200;

  assert.equal(restoreHostedLogView(replacement.panel, view), true);
  assert.equal(replacement.panel.open, true);
  assert.equal(replacement.list.scrollTop, 1000);
  assert.equal(consumeHostedLogRestore(replacement.panel), true);
  assert.equal(consumeHostedLogRestore(replacement.panel), false);
});

test("scrolling back is preserved rather than pulled to the tail", () => {
  const { panel, list } = fixture({ scrollTop: 240 });
  const view = captureHostedLogView(panel);
  list.scrollHeight = 1200;

  restoreHostedLogView(panel, view);

  assert.equal(list.scrollTop, 240);
});

test("view state never transfers to a different worker run", () => {
  const first = fixture({ runId: "first" });
  const second = fixture({ runId: "second", open: false });
  const view = captureHostedLogView(first.panel);

  assert.equal(restoreHostedLogView(second.panel, view), false);
  assert.equal(second.panel.open, false);
});

test("opening reveals the newest entry", () => {
  const { panel, list } = fixture();

  assert.equal(revealHostedLogTail(panel), true);
  assert.equal(list.scrollTop, 800);

  panel.open = false;
  assert.equal(revealHostedLogTail(panel), false);
});
