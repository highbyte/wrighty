import assert from "node:assert/strict";
import test from "node:test";

import {
  createTokenPickerState,
  installTokenPickers,
  normalizeToken,
  parseTokenValues,
  rememberTokenPickerInitialValues,
  validateProfileName
} from "../../src/Highbyte.Wrighty.Web/Assets/token-picker.mjs";

test("token values are canonical, distinct, and retain priority order", () => {
  assert.equal(normalizeToken("  Docs-Only "), "docs-only");
  assert.deepEqual(
    parseTokenValues(" Copilot, codex, COPILOT, , claude "),
    ["copilot", "codex", "claude"]);
});

test("created profile names use the repository vocabulary rules", () => {
  assert.equal(validateProfileName("docs-only"), null);
  assert.equal(validateProfileName(""), "Enter a profile name.");
  assert.match(validateProfileName("Docs only"), /lowercase words/);
  assert.match(validateProfileName("best"), /reserved/);
});

test("picker state adds, removes, and exposes only remaining known names", () => {
  const state = createTokenPickerState("codex", ["claude", "codex", "copilot"]);

  assert.deepEqual(state.values, ["codex"]);
  assert.deepEqual(state.remaining, ["claude", "copilot"]);
  assert.equal(state.add(" COPILOT "), true);
  assert.equal(state.add("copilot"), false);
  assert.deepEqual(state.values, ["codex", "copilot"]);
  assert.equal(state.remove("codex"), true);
  assert.equal(state.remove("missing"), false);
  assert.deepEqual(state.values, ["copilot"]);
  assert.deepEqual(state.remaining, ["claude", "codex"]);
});

test("status picker state preserves canonical casing while matching case-insensitively", () => {
  const state = createTokenPickerState(
    "done, CANCELLED",
    ["Todo", "Done", "Cancelled"],
    { preserveCase: true });

  assert.deepEqual(state.values, ["Done", "Cancelled"]);
  assert.deepEqual(state.remaining, ["Todo"]);
  assert.equal(state.add("TODO"), true);
  assert.equal(state.add("todo"), false);
  assert.deepEqual(state.values, ["Done", "Cancelled", "Todo"]);
  assert.equal(state.remove("DONE"), true);
  assert.deepEqual(state.values, ["Cancelled", "Todo"]);
});

test("a created name joins both the selected and known collections", () => {
  const state = createTokenPickerState("economy", ["economy", "balanced"]);

  assert.equal(state.add("docs-only"), true);
  assert.deepEqual(state.values, ["economy", "docs-only"]);
  assert.deepEqual(state.known, ["economy", "balanced", "docs-only"]);
});

test("priority swap is available only for a complete two-agent order", () => {
  const state = createTokenPickerState("codex, copilot", ["codex", "copilot"]);

  assert.equal(state.swap(), true);
  assert.deepEqual(state.values, ["copilot", "codex"]);
  state.remove("codex");
  assert.equal(state.swap(), false);
});

test("installing into a fragment without pickers is a no-op", () => {
  assert.deepEqual(installTokenPickers({ querySelectorAll: () => [] }), []);
});

test("picker initialization remembers both its list and dependent selection", () => {
  const source = { value: "economy, balanced", dataset: {} };
  const dependent = { value: "balanced", dataset: {} };

  rememberTokenPickerInitialValues(source, dependent);

  assert.equal(source.dataset.settingsInitialValue, "economy, balanced");
  assert.equal(dependent.dataset.settingsInitialValue, "balanced");

  source.value = "economy";
  dependent.value = "economy";
  assert.equal(source.dataset.settingsInitialValue, "economy, balanced");
  assert.equal(dependent.dataset.settingsInitialValue, "balanced");
});
