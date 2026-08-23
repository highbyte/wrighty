import assert from "node:assert/strict";
import test from "node:test";

import {
  captureSettingsScrollAnchor,
  restoreSettingsScrollAnchor
} from "../../src/Highbyte.Wrighty.Web/Assets/settings-scroll.mjs";

function settings(forms = []) {
  return {
    id: "settings-content",
    querySelectorAll: selector => selector === "form" ? forms : [],
    getBoundingClientRect: () => ({ top: 150 })
  };
}

function sourceFor(formElement) {
  return { closest: selector => selector === "form" ? formElement : null };
}

test("capture records a settings form's stable identity and viewport position", () => {
  const first = { id: "first" };
  const submitted = { id: "worker", getBoundingClientRect: () => ({ top: 420 }) };
  const target = settings([first, submitted]);

  const anchor = captureSettingsScrollAnchor(sourceFor(submitted), target, { scrollY: 340 });

  assert.deepEqual(anchor, {
    formId: "worker",
    formIndex: 1,
    top: 420,
    scrollY: 340
  });
});

test("capture ignores other regions and the initial settings loader request", () => {
  const source = sourceFor({ id: "worker", getBoundingClientRect: () => ({ top: 0 }) });
  assert.equal(captureSettingsScrollAnchor(source, { id: "board-content" }, { scrollY: 0 }), null);

  const target = settings();
  assert.equal(captureSettingsScrollAnchor(target, target, { scrollY: 0 }), null);
});

test("restore returns the identified form to its previous viewport position", () => {
  const replacement = { getBoundingClientRect: () => ({ top: -40 }) };
  const calls = [];
  const anchor = { formId: "worker", formIndex: 1, top: 320, scrollY: 340 };

  const restored = restoreSettingsScrollAnchor(
    anchor,
    settings([]),
    { getElementById: id => id === "worker" ? replacement : null },
    { scrollBy: (...args) => calls.push(["by", ...args]) }
  );

  assert.equal(restored, true);
  assert.deepEqual(calls, [["by", 0, -360]]);
});

test("restore uses the form index when a repeated form has no id", () => {
  const replacement = { getBoundingClientRect: () => ({ top: 275 }) };
  const calls = [];
  const anchor = { formId: null, formIndex: 1, top: 250, scrollY: 500 };

  restoreSettingsScrollAnchor(
    anchor,
    settings([{}, replacement]),
    { getElementById: () => null },
    { scrollBy: (...args) => calls.push(args) }
  );

  assert.deepEqual(calls, [[0, 25]]);
});

test("restore falls back to the original scroll offset when the form was removed", () => {
  const calls = [];
  const anchor = { formId: "removed", formIndex: 4, top: 250, scrollY: 500 };

  const restored = restoreSettingsScrollAnchor(
    anchor,
    settings([]),
    { getElementById: () => null },
    { scrollTo: (...args) => calls.push(args) }
  );

  assert.equal(restored, true);
  assert.deepEqual(calls, [[0, 500]]);
});

test("restore ignores swaps outside Settings", () => {
  const restored = restoreSettingsScrollAnchor(
    { formId: null, formIndex: -1, top: 0, scrollY: 0 },
    { id: "board-content" },
    {},
    {}
  );

  assert.equal(restored, false);
});
