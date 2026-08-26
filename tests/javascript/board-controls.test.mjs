import test from "node:test";
import assert from "node:assert/strict";
import {
  captureBoardControlFocus,
  restoreBoardControlFocus,
  captureOperationsControlFocus,
  restoreOperationsControlFocus,
  syncSortDirectionButton,
  toggleSortDirection,
  dismissBoardFilterMenu,
  dismissHeaderPopovers,
  syncBoardFilterIndicator,
  clearBoardFilters,
  resetBoardView,
  clearOperationsFilters,
  applyOperationsSort
} from "../../src/Highbyte.Wrighty.Web/Assets/board-controls.mjs";

function control(index) {
  return {
    dataset: { boardColumnSortIndex: index },
    focused: false,
    matches: selector => selector === "[data-board-column-sort-index]",
    focus() { this.focused = true; }
  };
}

test("captures only a focused per-column sort control", () => {
  assert.equal(captureBoardControlFocus({ activeElement: control("2") }), "sort:2");
  assert.equal(captureBoardControlFocus({ activeElement: { matches: () => false } }), null);
  assert.equal(captureBoardControlFocus({ activeElement: null }), null);
});

test("captures and restores a bulk action by semantic form id", () => {
  const button = {
    matches: () => false,
    closest: selector => selector === "[data-board-bulk-action]"
      ? { id: "board-bulk-resume-column-2" }
      : null
  };
  const replacement = { focused: false, focus() { this.focused = true; } };
  const doc = {
    activeElement: button,
    getElementById: id => id === "board-bulk-resume-column-2"
      ? { querySelector: () => replacement }
      : null,
    querySelector: () => null,
    querySelectorAll: () => []
  };

  const key = captureBoardControlFocus(doc);
  assert.equal(key, "bulk:board-bulk-resume-column-2");
  assert.equal(restoreBoardControlFocus(doc, key), true);
  assert.equal(replacement.focused, true);
});

test("bulk focus falls back to the originating column when its action disappears", () => {
  const heading = { focused: false, focus() { this.focused = true; } };
  const doc = {
    getElementById: () => null,
    querySelector: selector => selector === '[data-board-column-index="2"] h2'
      ? heading
      : null,
    querySelectorAll: () => []
  };

  assert.equal(
    restoreBoardControlFocus(doc, "bulk:board-bulk-resume-column-2"),
    true);
  assert.equal(heading.focused, true);
});

test("direction buttons announce state and select the opposite direction", () => {
  const attributes = {};
  const button = {
    setAttribute: (name, value) => { attributes[name] = value; },
    textContent: "",
    disabled: false
  };
  const select = {
    value: "updated:desc",
    options: [{ value: "updated:desc" }, { value: "updated:asc" }]
  };

  assert.equal(syncSortDirectionButton(select, button), true);
  assert.equal(attributes["aria-pressed"], "true");
  assert.equal(attributes["aria-label"], "Sorted descending");
  assert.equal(toggleSortDirection(select, button), true);
  assert.equal(select.value, "updated:asc");
  assert.equal(attributes["aria-label"], "Sorted ascending");

  select.value = "default";
  assert.equal(syncSortDirectionButton(select, button), false);
  assert.equal(button.disabled, true);
  assert.equal(toggleSortDirection(select, button), false);
});

test("captures and restores an Operations control by stable field name", () => {
  const search = control("unused");
  search.name = "search";
  search.matches = selector => selector === "#operations-filters [name]";
  const replacement = control("unused");
  replacement.name = "search";
  const doc = {
    activeElement: search,
    querySelectorAll: () => [replacement]
  };

  assert.equal(captureOperationsControlFocus(doc), "search");
  assert.equal(restoreOperationsControlFocus(doc, "search"), true);
  assert.equal(replacement.focused, true);
  assert.equal(restoreOperationsControlFocus(doc, "missing"), false);
  assert.equal(restoreOperationsControlFocus(doc, null), false);
  assert.equal(captureOperationsControlFocus({ activeElement: null }), null);
});

test("captures and restores an Operations sort heading by field", () => {
  const heading = {
    dataset: { operationsSortField: "updated" },
    matches: selector => selector === "[data-operations-sort-field]"
  };
  const replacement = {
    dataset: { operationsSortField: "updated" },
    focused: false,
    focus() { this.focused = true; }
  };
  const doc = {
    activeElement: heading,
    querySelectorAll(selector) {
      assert.equal(selector, "[data-operations-sort-field]");
      return [replacement];
    }
  };

  assert.equal(captureOperationsControlFocus(doc), "sort:updated");
  assert.equal(restoreOperationsControlFocus(doc, "sort:updated"), true);
  assert.equal(replacement.focused, true);
});

test("restores the matching replacement control", () => {
  const first = control("1");
  const replacement = control("2");
  const doc = { querySelectorAll: () => [first, replacement] };

  assert.equal(restoreBoardControlFocus(doc, "2"), true);
  assert.equal(replacement.focused, true);
  assert.equal(first.focused, false);
  assert.equal(restoreBoardControlFocus(doc, "3"), false);
  assert.equal(restoreBoardControlFocus(doc, null), false);
});

test("filter menu closes from its close button and restores summary focus", () => {
  const summary = { focused: false, focus() { this.focused = true; } };
  const closeButton = {};
  const target = {
    closest: selector => selector === "[data-close-board-filters]" ? closeButton : null
  };
  const menu = {
    open: true,
    contains: value => value === target,
    querySelector: selector => selector === "summary" ? summary : null
  };

  assert.equal(dismissBoardFilterMenu(menu, target), true);
  assert.equal(menu.open, false);
  assert.equal(summary.focused, true);
});

test("filter menu closes on an outside click but remains open for its fields", () => {
  const inside = { closest: () => null };
  const outside = { closest: () => null };
  const menu = {
    open: true,
    contains: target => target === inside,
    querySelector: () => null
  };

  assert.equal(dismissBoardFilterMenu(menu, inside), false);
  assert.equal(menu.open, true);
  assert.equal(dismissBoardFilterMenu(menu, outside), true);
  assert.equal(menu.open, false);
});

test("Agents popover closes outside while retaining a click within it", () => {
  const agentsTarget = {};
  const outside = {};
  const agents = {
    open: true,
    contains: target => target === agentsTarget
  };
  const doc = {
    querySelectorAll: selector => {
      assert.equal(selector, ".agents-menu[open]");
      return [agents].filter(menu => menu.open);
    }
  };

  assert.equal(dismissHeaderPopovers(doc, agentsTarget), 0);
  assert.equal(agents.open, true);
  assert.equal(dismissHeaderPopovers(doc, outside), 1);
  assert.equal(agents.open, false);
  assert.equal(dismissHeaderPopovers(doc, outside), 0);
  assert.equal(dismissHeaderPopovers(null, outside), 0);
  assert.equal(dismissHeaderPopovers(doc, null), 0);
});

test("confirmation dialog clicks retain the underlying header popover", () => {
  const target = {
    closest: selector => selector === "dialog" ? {} : null
  };
  const agents = {
    open: true,
    contains: () => false
  };
  const doc = {
    querySelectorAll: () => [agents]
  };

  assert.equal(dismissHeaderPopovers(doc, target), 0);
  assert.equal(agents.open, true);
});

test("a click from a replaced header menu does not close its replacement", () => {
  const sourceAgents = {
    classList: { contains: name => name === "agents-menu" }
  };
  const target = {
    closest: selector => selector.includes(".agents-menu") ? sourceAgents : null
  };
  const replacementAgents = {
    classList: { contains: name => name === "agents-menu" },
    open: true,
    contains: () => false
  };
  const doc = {
    querySelectorAll: () => [replacementAgents]
  };

  assert.equal(dismissHeaderPopovers(doc, target), 0);
  assert.equal(replacementAgents.open, true);
});

test("Board clear all empties only structured filters", () => {
  const controls = [
    { id: "board-search", name: "", value: "worker" },
    { id: "", name: "scope", value: "archived" },
    { id: "", name: "sort", value: "updated:desc" },
    { id: "", name: "columnSort", value: "2:title:asc" },
    { id: "", name: "agent", value: "codex", matches: () => false },
    { id: "", name: "priority", value: "P1", matches: () => false }
  ];
  const form = {
    elements: controls,
    submitted: false,
    requestSubmit() { this.submitted = true; }
  };

  assert.equal(clearBoardFilters(form), true);
  assert.deepEqual(controls.map(control => control.value), [
    "worker", "archived", "updated:desc", "2:title:asc", "", ""
  ]);
  assert.equal(form.submitted, true);
  assert.equal(clearBoardFilters(null), false);
});

test("Board reset view restores every Board control", () => {
  const controls = [
    { id: "board-search", name: "", value: "worker" },
    { id: "", name: "scope", value: "archived" },
    { id: "", name: "sort", value: "updated:desc" },
    { id: "", name: "columnSort", value: "2:title:asc", matches: () => false },
    { id: "", name: "claimKind", value: "agent", matches: () => false },
    { id: "", name: "updatedWithin", value: "7d", matches: () => false }
  ];
  const form = {
    elements: controls,
    submitted: false,
    requestSubmit() { this.submitted = true; }
  };

  assert.equal(resetBoardView(form), true);
  assert.deepEqual(controls.map(control => control.value), ["", "active", "default", "", "", ""]);
  assert.equal(form.submitted, true);
  assert.equal(resetBoardView(null), false);
});

test("Board filter indicator counts only active structured filters", () => {
  const controls = [
    { name: "sort", value: "updated:desc", matches: () => false },
    { name: "claimKind", value: "agent", matches: () => false },
    { name: "agent", value: "", matches: () => false },
    { name: "priority", value: " P1 ", matches: () => false },
    { name: "claimState", value: "", matches: () => false },
    { name: "updatedWithin", value: "", matches: () => false }
  ];
  const badge = { hidden: true, textContent: "" };
  const summary = { label: null, setAttribute: (_name, value) => { summary.label = value; } };
  const menu = {
    active: false,
    classList: { toggle: (_name, value) => { menu.active = value; } },
    querySelector: selector => selector === "summary" ? summary : badge
  };
  const form = { querySelectorAll: () => controls };

  assert.equal(syncBoardFilterIndicator(form, menu), 2);
  assert.equal(menu.active, true);
  assert.equal(badge.hidden, false);
  assert.equal(badge.textContent, "2");
  assert.equal(summary.label, "Filters, 2 active");

  controls.forEach(control => { control.value = ""; });
  assert.equal(syncBoardFilterIndicator(form, menu), 0);
  assert.equal(menu.active, false);
  assert.equal(badge.hidden, true);
  assert.equal(summary.label, "Filters");
});

test("Operations clear empties filters, preserves sort, and submits", () => {
  const controls = [
    { name: "search", value: "worker", matches: () => false },
    { name: "agent", value: "codex", matches: () => false },
    { name: "claimKind", checked: true, matches: selector => selector.includes("checkbox") }
  ];
  const form = {
    sort: "updated:desc",
    submitted: false,
    querySelectorAll(selector) {
      assert.equal(selector, "[name]:not([name=sort])");
      return controls;
    },
    requestSubmit() { this.submitted = true; }
  };

  assert.equal(clearOperationsFilters(form), true);
  assert.deepEqual(controls.map(control => control.value), ["", "", undefined]);
  assert.equal(controls[2].checked, false);
  assert.equal(form.sort, "updated:desc");
  assert.equal(form.submitted, true);
  assert.equal(clearOperationsFilters(null), false);
});

test("Operations heading sort updates the hidden query and submits", () => {
  const sort = { value: "default" };
  const form = {
    submitted: false,
    querySelector: selector => selector === "[name=sort]" ? sort : null,
    requestSubmit() { this.submitted = true; }
  };

  assert.equal(applyOperationsSort(form, "updated:desc"), true);
  assert.equal(sort.value, "updated:desc");
  assert.equal(form.submitted, true);
  assert.equal(applyOperationsSort(form, ""), false);
  assert.equal(applyOperationsSort(null, "title:asc"), false);
});
