import assert from "node:assert/strict";
import test from "node:test";

import {
  closeTokenPickerPopovers,
  createTokenPickerState,
  enhanceTokenPicker,
  installTokenPickers,
  normalizeToken,
  parseTokenValues,
  rememberTokenPickerInitialValues,
  validateProfileName
} from "../../src/Highbyte.Wrighty.Web/Assets/token-picker.mjs";

class FakeElement {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = ownerDocument;
    this.dataset = {};
    this.children = [];
    this.listeners = new Map();
    this.attributes = new Map();
    this.className = "";
    this.hidden = false;
    this.value = "";
    this.classList = {
      add: (...names) => {
        this.className = [...new Set([...this.className.split(" ").filter(Boolean), ...names])]
          .join(" ");
      }
    };
  }

  append(...children) {
    children.forEach(child => { child.parentElement = this; });
    this.children.push(...children);
  }

  replaceChildren(...children) {
    this.children = [];
    this.append(...children);
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  dispatchEvent(event) {
    for (const listener of this.listeners.get(event.type) ?? []) listener(event);
    this.dispatchedEvent = event.type;
    return true;
  }

  click() {
    this.dispatchEvent({ type: "click", preventDefault() {} });
  }

  keydown(key) {
    this.dispatchEvent({ type: "keydown", key, preventDefault() { this.prevented = true; } });
  }

  focus() {
    this.ownerDocument.activeElement = this;
  }

  querySelector(selector) {
    return walk(this).find(element => matches(element, selector)) ?? null;
  }

  contains(target) {
    return target === this || walk(this).includes(target);
  }
}

class FakeDocument {
  constructor() {
    this.elementsById = new Map();
    this.activeElement = null;
  }

  createElement(tagName) {
    return new FakeElement(tagName, this);
  }

  getElementById(id) {
    return this.elementsById.get(id) ?? null;
  }
}

function walk(root) {
  return root.children.flatMap(child => [child, ...walk(child)]);
}

function matches(element, selector) {
  if (selector === "button") return element.tagName === "BUTTON";
  if (selector === "[data-token-source]") return element.dataset.tokenSource !== undefined;
  if (selector.startsWith(".")) return element.className.split(" ").includes(selector.slice(1));
  return false;
}

function pickerHarness(sourceValue, options = {}) {
  const doc = new FakeDocument();
  const picker = doc.createElement("div");
  picker.dataset.tokenLabel = options.tokenLabel ?? "agent";
  picker.dataset.knownValues = JSON.stringify(options.knownValues ?? []);
  if (options.ordered) picker.dataset.ordered = "true";
  if (options.allowCreate) picker.dataset.allowCreate = "true";
  if (options.createMode) picker.dataset.createMode = options.createMode;
  if (options.preserveCase) picker.dataset.preserveCase = "true";
  const source = doc.createElement("input");
  source.id = "picker-source";
  source.dataset.tokenSource = "";
  source.value = sourceValue;
  picker.append(source);
  return { doc, picker, source };
}

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

test("enhanced pickers add, remove, close, and notify through their rendered controls", () => {
  const { picker, source } = pickerHarness("codex", {
    knownValues: ["claude", "codex"]
  });

  const enhanced = enhanceTokenPicker(picker);

  assert.ok(enhanced);
  assert.equal(source.type, "hidden");
  assert.equal(source.dataset.settingsInitialValue, "codex");
  assert.equal(enhanceTokenPicker(picker), null);

  const add = picker.querySelector(".token-picker-add");
  const popover = picker.querySelector(".token-picker-popover");
  add.click();
  assert.equal(popover.hidden, false);
  assert.equal(add.getAttribute("aria-expanded"), "true");

  picker.querySelector(".token-picker-option").click();
  assert.equal(source.value, "codex, claude");
  assert.equal(source.dispatchedEvent, "input");
  assert.equal(add.hidden, true);

  const removeClaude = walk(picker).find(element =>
    element.getAttribute("aria-label") === "Remove claude");
  removeClaude.click();
  assert.equal(source.value, "codex");
  assert.equal(add.hidden, false);

  add.click();
  picker.keydown("Escape");
  assert.equal(popover.hidden, true);
  assert.equal(picker.ownerDocument.activeElement, add);

  add.click();
  closeTokenPickerPopovers({ querySelectorAll: () => [picker] }, {});
  assert.equal(popover.hidden, true);
  assert.equal(add.getAttribute("aria-expanded"), "false");
});

test("ordered pickers render priority swapping", () => {
  const { picker, source } = pickerHarness("codex, copilot", {
    knownValues: ["codex", "copilot"],
    ordered: true,
    tokenLabel: "fallback agent"
  });

  enhanceTokenPicker(picker);
  const swap = picker.querySelector(".token-picker-swap");
  assert.equal(swap.getAttribute("aria-label"), "Swap fallback agent priority");
  swap.click();
  assert.equal(source.value, "copilot, codex");
});

test("profile and free-value pickers validate and create names", () => {
  const profile = pickerHarness("economy", {
    knownValues: ["economy"],
    allowCreate: true,
    tokenLabel: "profile"
  });
  enhanceTokenPicker(profile.picker);
  const profileInput = profile.picker.querySelector(".token-picker-create-input");
  const profileCreate = profile.picker.querySelector(".token-picker-create");
  const profileStatus = profile.picker.querySelector(".token-picker-status");

  profileCreate.click();
  assert.equal(profileStatus.textContent, "Enter a profile name.");
  assert.equal(profile.doc.activeElement, profileInput);

  profileInput.value = "docs-only";
  profileCreate.click();
  assert.equal(profile.source.value, "economy, docs-only");
  assert.equal(profileStatus.textContent, "docs-only created and selected.");

  profileInput.value = "docs-only";
  profileCreate.click();
  assert.equal(profileStatus.textContent, "“docs-only” is already selected.");

  const status = pickerHarness("Done", {
    knownValues: ["Done"],
    allowCreate: true,
    createMode: "value",
    preserveCase: true,
    tokenLabel: "archive status"
  });
  enhanceTokenPicker(status.picker);
  const statusInput = status.picker.querySelector(".token-picker-create-input");
  const statusCreate = status.picker.querySelector(".token-picker-create");
  const statusMessage = status.picker.querySelector(".token-picker-status");

  statusCreate.click();
  assert.equal(statusMessage.textContent, "Enter archive status.");
  statusInput.value = "Cancelled";
  statusInput.keydown("Enter");
  assert.equal(status.source.value, "Done, Cancelled");
});

test("dependent profile selections retain their loaded baseline", () => {
  const { doc, picker, source } = pickerHarness("economy, balanced", {
    knownValues: ["economy", "balanced"]
  });
  const select = doc.createElement("select");
  select.value = "balanced";
  select.dataset.emptyLabel = "None";
  doc.elementsById.set("default-profile", select);
  picker.dataset.dependentSelect = "default-profile";

  const installed = installTokenPickers({ querySelectorAll: () => [picker] });

  assert.equal(installed.length, 1);
  assert.equal(select.value, "balanced");
  assert.equal(select.dataset.settingsInitialValue, "balanced");
  assert.equal(source.dataset.settingsInitialValue, "economy, balanced");
});
