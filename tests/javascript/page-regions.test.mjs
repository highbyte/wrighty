import assert from "node:assert/strict";
import test from "node:test";

import {
  readyPageRegions,
  readyRegionSelectors
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
    "#provider-capacity-region": region("capacity", events),
    "#operations-content": region("operations", events),
    "#settings-content": region("settings", events)
  });

  readyPageRegions(doc, recordingHtmx(processed));

  assert.deepEqual(processed, ["board", "capacity", "operations", "settings"]);
  assert.deepEqual(events, [
    "board:wrighty:ready",
    "capacity:wrighty:ready",
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
    "#provider-capacity-region": region("capacity", events),
    "#operations-content": region("operations", events)
  });

  readyPageRegions(doc, recordingHtmx(processed));

  assert.deepEqual(processed, ["capacity", "operations"]);
  assert.deepEqual(events, ["capacity:wrighty:ready", "operations:wrighty:ready"]);
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
    "#provider-capacity-region",
    "#operations-content",
    "#settings-content"
  ]);
});
