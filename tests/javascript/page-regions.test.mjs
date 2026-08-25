import assert from "node:assert/strict";
import test from "node:test";

import {
  readyPageRegions,
  readyRegionSelectors,
  refreshVisibleOperations,
  revealWorkerProcesses
} from "../../src/Highbyte.Wrighty.Web/Assets/page-regions.mjs";

function region(name, events = []) {
  return {
    name,
    dispatchEvent(event) {
      events.push(`${name}:${event.type}`);
      return true;
    }
  };
}

function documentWith(regions) {
  return {
    querySelector(selector) {
      return regions[selector] ?? null;
    }
  };
}

function recordingHtmx(processed) {
  return { process: (value) => processed.push(value.name) };
}

test("every rendered region is processed and readied", () => {
  const events = [];
  const processed = [];
  const doc = documentWith({
    "#board-content": region("board", events),
    "#worker-summary-region": region("workers", events),
    "#provider-capacity-region": region("capacity", events),
    "#skill-status-region": region("skills", events),
    "#operations-content": region("operations", events),
    "#settings-content": region("settings", events)
  });

  readyPageRegions(doc, recordingHtmx(processed));

  assert.deepEqual(processed, ["board", "workers", "capacity", "skills", "operations", "settings"]);
  assert.deepEqual(events, [
    "board:wrighty:ready",
    "workers:wrighty:ready",
    "capacity:wrighty:ready",
    "skills:wrighty:ready",
    "operations:wrighty:ready",
    "settings:wrighty:ready"
  ]);
});

test("a page without a board still readies the regions it does render", () => {
  // The GitHub backend's limited view. Readying the regions as one fixed sequence meant the
  // absent board ended initialization before the operations panel, which then sat at its loading
  // placeholder with its request never sent.
  const events = [];
  const processed = [];
  const doc = documentWith({
    "#worker-summary-region": region("workers", events),
    "#provider-capacity-region": region("capacity", events),
    "#skill-status-region": region("skills", events),
    "#operations-content": region("operations", events)
  });

  readyPageRegions(doc, recordingHtmx(processed));

  assert.deepEqual(processed, ["workers", "capacity", "skills", "operations"]);
  assert.deepEqual(events, [
    "workers:wrighty:ready",
    "capacity:wrighty:ready",
    "skills:wrighty:ready",
    "operations:wrighty:ready"
  ]);
});

test("a null region is never handed to htmx", () => {
  // htmx.process throws on null, which is what turned one absent region into a dead page.
  const doc = documentWith({ "#operations-content": region("operations") });
  const htmx = {
    process(value) {
      assert.ok(value, "htmx.process must never receive a null region");
    }
  };

  readyPageRegions(doc, htmx);
});

test("a page rendering no known region does nothing rather than failing", () => {
  const processed = [];

  const readied = readyPageRegions(documentWith({}), recordingHtmx(processed));

  assert.deepEqual(readied, []);
  assert.deepEqual(processed, []);
});

test("regions are readied without htmx present", () => {
  // The ready event is what triggers each region's own request; it must not depend on htmx
  // having loaded, which the optional call already allowed for.
  const events = [];
  const doc = documentWith({ "#operations-content": region("operations", events) });

  readyPageRegions(doc, undefined);

  assert.deepEqual(events, ["operations:wrighty:ready"]);
});

test("the selector list is the documented region order", () => {
  assert.deepEqual(readyRegionSelectors, [
    "#board-content",
    "#worker-summary-region",
    "#provider-capacity-region",
    "#skill-status-region",
    "#operations-content",
    "#settings-content"
  ]);
});

test("visible Operations dispatches its polling event", () => {
  const events = [];
  const panel = { hidden: false };
  const operations = region("operations", events);
  operations.closest = selector => selector === '[role="tabpanel"]' ? panel : null;
  const doc = documentWith({ "#operations-content": operations });
  doc.visibilityState = "visible";

  assert.equal(refreshVisibleOperations(doc), true);
  assert.deepEqual(events, ["operations:wrighty:operations-refresh"]);
});

test("worker navigation focuses the stable tab and scrolls the worker controls", () => {
  const actions = [];
  const operationsTab = {
    focus(options) { actions.push(["focus-tab", options]); }
  };
  const workerProcesses = {
    focus() { throw new Error("the polled worker fragment must not receive focus"); },
    scrollIntoView(options) { actions.push(["scroll-workers", options]); }
  };
  const doc = documentWith({
    "#tab-operations": operationsTab,
    "#worker-processes": workerProcesses
  });

  assert.equal(revealWorkerProcesses(doc), true);
  assert.deepEqual(actions, [
    ["focus-tab", { preventScroll: true }],
    ["scroll-workers", { block: "start", behavior: "auto" }]
  ]);
});

test("worker navigation remains pending until worker controls have loaded", () => {
  const actions = [];
  const doc = documentWith({
    "#tab-operations": {
      focus(options) { actions.push(["focus-tab", options]); }
    }
  });

  assert.equal(revealWorkerProcesses(doc), false);
  assert.deepEqual(actions, [["focus-tab", { preventScroll: true }]]);
});

test("Operations polling pauses off-tab and while the page is hidden", () => {
  const events = [];
  const panel = { hidden: true };
  const operations = region("operations", events);
  operations.closest = () => panel;
  const doc = documentWith({ "#operations-content": operations });
  doc.visibilityState = "visible";

  assert.equal(refreshVisibleOperations(doc), false);
  panel.hidden = false;
  doc.visibilityState = "hidden";
  assert.equal(refreshVisibleOperations(doc), false);
  assert.deepEqual(events, []);
});

test("Operations polling does not replace a form behind a dialog or active request", () => {
  const events = [];
  const operations = region("operations", events);
  operations.closest = () => ({ hidden: false });
  operations.matches = () => false;
  operations.querySelector = () => null;
  const openDialog = {};
  const doc = documentWith({
    "#operations-content": operations,
    "dialog[open]": openDialog
  });
  doc.visibilityState = "visible";

  assert.equal(refreshVisibleOperations(doc), false);
  doc.querySelector = selector => selector === "#operations-content" ? operations : null;
  operations.matches = selector => selector === ".htmx-request";
  assert.equal(refreshVisibleOperations(doc), false);
  assert.deepEqual(events, []);
});

test("Operations polling continues while the hosted log reader is open", () => {
  const events = [];
  const operations = region("operations", events);
  operations.closest = () => ({ hidden: false });
  operations.matches = () => false;
  operations.querySelector = () => null;
  const doc = documentWith({
    "#operations-content": operations,
    "[data-hosted-worker-log-panel][open]": {}
  });
  doc.visibilityState = "visible";

  assert.equal(refreshVisibleOperations(doc), true);
  assert.deepEqual(events, ["operations:wrighty:operations-refresh"]);
});
