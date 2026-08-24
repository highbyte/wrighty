import assert from "node:assert/strict";
import test from "node:test";

import {
  captureHostedLogView,
  captureHostedLogViews,
  consumeHostedLogRestore,
  restoreHostedLogView,
  restoreHostedLogViews,
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

test("multiple worker log readers preserve their own disclosure and scroll state", () => {
  const first = fixture({ runId: "first", scrollTop: 120 });
  const second = fixture({ runId: "second", scrollTop: 790 });
  const firstReplacement = fixture({ runId: "first", open: false });
  const secondReplacement = fixture({ runId: "second", open: false });
  secondReplacement.list.scrollHeight = 1200;
  const currentRoot = {
    querySelectorAll: selector => selector === "[data-hosted-worker-log-panel]"
      ? [first.panel, second.panel]
      : []
  };
  const replacementRoot = {
    querySelectorAll: selector => selector === "[data-hosted-worker-log-panel]"
      ? [firstReplacement.panel, secondReplacement.panel]
      : []
  };

  const views = captureHostedLogViews(currentRoot);

  assert.equal(views.length, 2);
  assert.equal(restoreHostedLogViews(replacementRoot, views), 2);
  assert.equal(firstReplacement.panel.open, true);
  assert.equal(firstReplacement.list.scrollTop, 120);
  assert.equal(secondReplacement.panel.open, true);
  assert.equal(secondReplacement.list.scrollTop, 1000);
});

test("opening reveals the newest entry", () => {
  const { panel, list } = fixture();

  assert.equal(revealHostedLogTail(panel), true);
  assert.equal(list.scrollTop, 800);

  panel.open = false;
  assert.equal(revealHostedLogTail(panel), false);
});
