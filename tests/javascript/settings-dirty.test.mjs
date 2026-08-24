import assert from "node:assert/strict";
import test from "node:test";

import {
  createSettingsNavigationGuard,
  dismissWorkspaceModeHelp,
  initializeSettingsSaveButtons,
  refreshSettingsDirtyState,
  revealFirstDirtySettingsForm,
  settingsFormIsDirty,
  tabLeavesUnsavedSettings,
  updateSettingsDirtyIndicator
} from "../../src/Highbyte.Wrighty.Web/Assets/settings-dirty.mjs";

const settle = () => new Promise(resolve => setImmediate(resolve));

function input(value, defaultValue = value, overrides = {}) {
  return { name: "setting", type: "text", value, defaultValue, disabled: false, ...overrides };
}

function form(controls, saveButtons = []) {
  return {
    elements: controls,
    dataset: {},
    querySelectorAll: selector => selector === "[data-settings-save]" ? saveButtons : []
  };
}

function documentState(dirtyForms = []) {
  const indicator = { hidden: true };
  const currentDirtyForms = () => dirtyForms.filter(
    candidate => candidate.dataset.settingsDirty === "true");
  return {
    indicator,
    querySelector(selector) {
      if (selector === "#tab-settings-unsaved") return indicator;
      if (selector.includes("data-settings-dirty")) return currentDirtyForms()[0] ?? null;
      return null;
    },
    querySelectorAll(selector) {
      return selector.includes("data-settings-dirty") ? currentDirtyForms() : [];
    },
    getElementById() { return null; }
  };
}

test("form dirtiness compares text, checkbox, and select controls with loaded values", () => {
  const unchangedSelect = {
    name: "agent",
    type: "select-one",
    tagName: "SELECT",
    value: "claude",
    disabled: false,
    multiple: false,
    options: [
      { value: "claude", defaultSelected: true, selected: true },
      { value: "codex", defaultSelected: false, selected: false }
    ]
  };
  const unchangedCheckbox = input("true", "true", {
    type: "checkbox",
    checked: true,
    defaultChecked: true
  });

  assert.equal(settingsFormIsDirty(form([
    input("stored"),
    unchangedCheckbox,
    unchangedSelect,
    input("ignored", "different", { disabled: true }),
    input("ignored", "different", { type: "submit" })
  ])), false);

  assert.equal(settingsFormIsDirty(form([input("draft", "stored")])), true);
  assert.equal(settingsFormIsDirty(form([{
    ...unchangedCheckbox,
    checked: false
  }])), true);
  assert.equal(settingsFormIsDirty(form([{
    ...unchangedSelect,
    value: "codex"
  }])), true);
});

test("single selects use their first option when no explicit default exists", () => {
  const select = {
    name: "profile",
    type: "select-one",
    tagName: "SELECT",
    value: "economy",
    disabled: false,
    multiple: false,
    options: [
      { value: "economy", defaultSelected: false, selected: true },
      { value: "deep", defaultSelected: false, selected: false }
    ]
  };

  assert.equal(settingsFormIsDirty(form([select])), false);
  select.value = "deep";
  assert.equal(settingsFormIsDirty(form([select])), true);
});

test("configuration choice help closes only when clicking outside it", () => {
  const inside = {};
  const help = {
    open: true,
    contains: target => target === inside
  };
  const root = {
    querySelectorAll: () => help.open ? [help] : []
  };

  assert.equal(dismissWorkspaceModeHelp(root, inside), false);
  assert.equal(help.open, true);
  assert.equal(dismissWorkspaceModeHelp(root, {}), true);
  assert.equal(help.open, false);
  assert.equal(dismissWorkspaceModeHelp(root, {}), false);
});

test("multiple selects compare every selected option", () => {
  const select = {
    name: "statuses",
    type: "select-multiple",
    tagName: "SELECT",
    value: "Done",
    disabled: false,
    multiple: true,
    options: [
      { value: "Done", defaultSelected: true, selected: true },
      { value: "Cancelled", defaultSelected: false, selected: false }
    ]
  };

  assert.equal(settingsFormIsDirty(form([select])), false);
  select.options[1].selected = true;
  assert.equal(settingsFormIsDirty(form([select])), true);
});

test("token picker sources compare against their explicit canonical baseline", () => {
  const source = input("codex, copilot", "codex, copilot", {
    type: "hidden",
    dataset: { settingsInitialValue: "codex, copilot" }
  });

  assert.equal(settingsFormIsDirty(form([source])), false);

  source.value = "copilot, codex";
  source.defaultValue = source.value;
  assert.equal(settingsFormIsDirty(form([source])), true);

  source.value = "codex, copilot";
  source.defaultValue = source.value;
  assert.equal(settingsFormIsDirty(form([source])), false);
});

test("editing and reverting a named settings control updates the form and tab marker", () => {
  const field = input("draft", "stored");
  const save = { disabled: true };
  const editedForm = form([field], [save]);
  field.matches = selector => selector === "[name]";
  field.closest = selector => selector === "#settings-content form" ? editedForm : null;
  const doc = documentState([editedForm]);

  assert.equal(refreshSettingsDirtyState(field, doc), true);
  assert.equal(editedForm.dataset.settingsDirty, "true");
  assert.equal(doc.indicator.hidden, false);
  assert.equal(save.disabled, false);

  field.value = "stored";
  assert.equal(refreshSettingsDirtyState(field, doc), true);
  assert.equal(editedForm.dataset.settingsDirty, undefined);
  assert.equal(save.disabled, true);
  doc.querySelector = selector => selector === "#tab-settings-unsaved" ? doc.indicator : null;
  updateSettingsDirtyIndicator(doc);
  assert.equal(doc.indicator.hidden, true);
});

test("save buttons initialize from clean and server-retained dirty forms", () => {
  const cleanSave = { disabled: false };
  const dirtySave = { disabled: true };
  const cleanForm = form([input("stored")], [cleanSave]);
  const dirtyForm = form([input("rejected draft")], [dirtySave]);
  dirtyForm.dataset.settingsDirty = "true";

  initializeSettingsSaveButtons({
    querySelectorAll: selector => selector === "#settings-content form"
      ? [cleanForm, dirtyForm]
      : []
  });

  assert.equal(cleanSave.disabled, true);
  assert.equal(dirtySave.disabled, false);
});

test("the unsaved indicator reveals the first dirty form and its Save action", () => {
  const actions = [];
  const save = { focus: options => actions.push(["focus", options]) };
  const dirtyForm = {
    scrollIntoView: options => actions.push(["scroll", options]),
    querySelector: selector => selector.startsWith("[data-settings-save]") ? save : null
  };
  const doc = {
    querySelector: selector => selector.includes("data-settings-dirty") ? dirtyForm : null
  };

  assert.equal(revealFirstDirtySettingsForm(doc), true);
  assert.deepEqual(actions, [
    ["scroll", { behavior: "smooth", block: "center" }],
    ["focus", { preventScroll: true }]
  ]);

  const field = { focus: options => actions.push(["field", options]) };
  doc.querySelector = () => ({
    querySelector: selector => selector.startsWith("[data-settings-save]") ? null : field
  });
  assert.equal(revealFirstDirtySettingsForm(doc), true);
  assert.deepEqual(actions.at(-1), ["field", { preventScroll: true }]);
  assert.equal(revealFirstDirtySettingsForm({ querySelector: () => null }), false);
});

test("unrelated controls do not change settings dirty state", () => {
  const doc = documentState();
  assert.equal(refreshSettingsDirtyState({ matches: () => false }, doc), false);
  assert.equal(refreshSettingsDirtyState({
    matches: () => true,
    closest: () => null
  }, doc), false);
});

test("leaving a panel containing dirty forms requires confirmation", () => {
  const dirtyForm = form([]);
  dirtyForm.dataset.settingsDirty = "true";
  const destination = { contains: value => value === dirtyForm };
  const doc = documentState([dirtyForm]);
  doc.getElementById = id => id === "settings-repository-panel" ? destination : null;
  const staying = { getAttribute: () => "settings-repository-panel" };
  const leaving = { getAttribute: () => "board-panel" };

  assert.equal(tabLeavesUnsavedSettings(staying, doc), false);
  assert.equal(tabLeavesUnsavedSettings(leaving, doc), true);
  assert.equal(tabLeavesUnsavedSettings(null, doc), true);
  assert.equal(tabLeavesUnsavedSettings(staying, documentState()), false);
});

test("navigation is immediate when it does not leave unsaved settings", () => {
  const selected = [];
  const guard = createSettingsNavigationGuard({
    doc: documentState(),
    requestConfirmation: () => Promise.resolve(false),
    selectTab: tab => selected.push(tab),
    discardSettings: () => assert.fail("clean settings must not be discarded")
  });
  const tab = { focus: () => { tab.focused = true; } };

  assert.equal(guard(tab, { focus: true }), false);
  assert.deepEqual(selected, [tab]);
  assert.equal(tab.focused, true);
});

test("cancelled navigation retains the current tab and draft", async () => {
  const dirtyForm = form([]);
  dirtyForm.dataset.settingsDirty = "true";
  const doc = documentState([dirtyForm]);
  const current = {};
  const destination = { contains: () => false };
  doc.getElementById = () => destination;
  const next = {
    getAttribute: () => "board-panel",
    closest: () => ({ querySelector: () => current })
  };
  const confirmations = [];
  const guard = createSettingsNavigationGuard({
    doc,
    requestConfirmation: (details, trigger) => {
      confirmations.push({ details, trigger });
      return Promise.resolve(false);
    },
    selectTab: () => assert.fail("cancelled navigation must not select the destination"),
    discardSettings: () => assert.fail("cancelled navigation must retain the draft")
  });

  assert.equal(guard(next), true);
  await settle();
  assert.equal(confirmations[0].details.title, "Discard unsaved settings?");
  assert.equal(confirmations[0].details.action, "Discard changes");
  assert.equal(confirmations[0].trigger, current);
});

test("confirmed navigation discards, selects, and focuses the destination", async () => {
  const dirtyForm = form([]);
  dirtyForm.dataset.settingsDirty = "true";
  const doc = documentState([dirtyForm]);
  doc.getElementById = () => ({ contains: () => false });
  const actions = [];
  const next = {
    getAttribute: () => "user-panel",
    closest: () => ({ querySelector: () => ({}) }),
    focus: () => actions.push("focus")
  };
  const guard = createSettingsNavigationGuard({
    doc,
    requestConfirmation: () => Promise.resolve(true),
    selectTab: tab => actions.push(tab === next ? "select" : "wrong"),
    discardSettings: () => actions.push("discard")
  });

  assert.equal(guard(next, { focus: true }), true);
  await settle();
  assert.deepEqual(actions, ["discard", "select", "focus"]);
});

test("navigation runs its destination callback only after selection is allowed", async () => {
  const dirtyForm = form([]);
  dirtyForm.dataset.settingsDirty = "true";
  const doc = documentState([dirtyForm]);
  doc.getElementById = () => ({ contains: () => false });
  const actions = [];
  const next = {
    getAttribute: () => "operations-panel",
    closest: () => ({ querySelector: () => ({}) })
  };
  const guard = createSettingsNavigationGuard({
    doc,
    requestConfirmation: () => Promise.resolve(true),
    selectTab: () => actions.push("select"),
    discardSettings: () => actions.push("discard")
  });

  guard(next, { afterSelect: () => actions.push("after") });
  assert.deepEqual(actions, []);
  await settle();
  assert.deepEqual(actions, ["discard", "select", "after"]);
});
