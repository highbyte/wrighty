import assert from "node:assert/strict";
import test from "node:test";

import {
  createContextPanelController,
  installContextStateUpdates
} from "../../src/Highbyte.Wrighty.Web/Assets/context-panel.mjs";

class FakeElement {
  constructor(tagName = "div") {
    this.tagName = tagName;
    this.attributes = new Map();
    this.children = [];
    this.dataset = {};
    this.isConnected = true;
    this.focused = false;
    this.textContent = "";
  }

  setAttribute(name, value) {
    this.attributes.set(name, value);
  }

  removeAttribute(name) {
    this.attributes.delete(name);
    if (name === "title") this.title = "";
  }

  append(...children) {
    this.children.push(...children);
  }

  replaceChildren(...children) {
    this.children = children;
  }

  focus() {
    this.focused = true;
  }

  querySelector(selector) {
    const matches = element => selector === ".panel-loading h2"
      ? element.tagName === "h2"
      : selector === ".panel-loading-status" && element.className === "panel-loading-status";
    const pending = [...this.children];
    while (pending.length > 0) {
      const element = pending.shift();
      if (matches(element)) return element;
      pending.push(...element.children);
    }
    return null;
  }
}

class FakeDocument {
  constructor() {
    this.listeners = new Map();
    this.elements = new Map();
    this.panel = new FakeElement();
    this.panel.id = "item-panel";
  }

  addEventListener(name, listener) {
    this.listeners.set(name, listener);
  }

  dispatch(name, detail) {
    this.listeners.get(name)?.({ detail });
  }

  getElementById(id) {
    return this.elements.get(id) ?? null;
  }

  querySelector(selector) {
    return selector === "#item-panel" ? this.panel : null;
  }

  createElement(tagName) {
    return new FakeElement(tagName);
  }
}

function trigger(id, loadingLabel = "Loading context approval…") {
  const element = new FakeElement("button");
  element.id = id;
  element.dataset.panelLoadingLabel = loadingLabel;
  element.dataset.panelLoadingDetail = "Reading current diagnostics…";
  element.closest = selector => selector === "[data-panel-loading-label]" ? element : null;
  return element;
}

test("context state updates validate event details and replace the projected badge", () => {
  const doc = new FakeDocument();
  const state = new FakeElement("span");
  state.title = "projection";
  doc.elements.set("context-approval-state-github-owner-repo-1", state);
  installContextStateUpdates(doc);

  for (const detail of [
    undefined,
    {},
    { automationKey: "", label: "Approved", appearance: "approved" },
    { automationKey: "github-owner-repo-1", label: "", appearance: "approved" },
    { automationKey: "github-owner-repo-1", label: "Approved", appearance: "unsafe" },
    { automationKey: "missing", label: "Approved", appearance: "approved" }
  ]) doc.dispatch("wrighty:context-state", detail);

  assert.equal(state.textContent, "");
  doc.dispatch("wrighty:context-state", {
    automationKey: "github-owner-repo-1",
    label: "Needs review",
    appearance: "needs-review",
    title: "Inspect found stale content."
  });
  assert.equal(state.className, "state-pill context-approval-needs-review");
  assert.equal(state.textContent, "Needs review");
  assert.equal(state.title, "Inspect found stale content.");

  doc.dispatch("wrighty:context-state", {
    automationKey: "github-owner-repo-1",
    label: "Approved",
    appearance: "approved"
  });
  assert.equal(state.title, "");
});

test("panel controller shows progress, suppresses failed swaps, and renders failure", () => {
  const doc = new FakeDocument();
  const source = trigger("inspect-1");
  const request = { readyState: 1, status: 0 };
  const controller = createContextPanelController({ doc });

  controller.beforeRequest({
    target: source,
    detail: { target: { id: "item-panel" }, xhr: request }
  });
  assert.equal(doc.panel.attributes.get("aria-busy"), "true");
  const detail = doc.panel.children[0];
  const heading = detail.children[0].children[0];
  const status = detail.children[1];
  assert.equal(heading.textContent, "Loading context approval…");
  assert.equal(status.children[1].textContent, "Reading current diagnostics…");
  assert.equal(heading.focused, true);

  request.status = 500;
  const swap = { detail: { xhr: request, shouldSwap: true } };
  assert.equal(controller.beforeSwap(swap), true);
  assert.equal(swap.detail.shouldSwap, false);
  assert.equal(controller.afterRequest({ detail: { xhr: request } }), 500);
  assert.equal(heading.textContent, "Unable to load details");
  assert.equal(status.className, "error");
  assert.equal(status.attributes.get("role"), "alert");
  assert.match(status.textContent, /request failed/i);

  doc.panel.setAttribute("aria-busy", "true");
  controller.afterSwap({ detail: { target: { id: "other", removeAttribute() {} } } });
  assert.equal(doc.panel.attributes.get("aria-busy"), "true");
  controller.afterSwap({ detail: { target: doc.panel } });
  assert.equal(doc.panel.attributes.has("aria-busy"), false);
});

test("panel close cancels a pending response and restores focus safely", () => {
  const doc = new FakeDocument();
  const original = trigger("inspect-2");
  const replacement = new FakeElement("button");
  doc.elements.set(original.id, replacement);
  const fallback = new FakeElement("input");
  let closed = 0;
  const controller = createContextPanelController({
    doc,
    focusFallback: () => fallback,
    onClose: () => { closed += 1; }
  });
  const request = { readyState: 1, status: 200 };
  controller.beforeRequest({
    target: original,
    detail: { target: { id: "item-panel" }, xhr: request }
  });

  controller.close();
  assert.equal(replacement.focused, true);
  assert.equal(fallback.focused, false);
  assert.equal(closed, 1);
  assert.deepEqual(doc.panel.children, []);
  const lateSwap = { detail: { xhr: request, shouldSwap: true } };
  assert.equal(controller.beforeSwap(lateSwap), true);
  assert.equal(lateSwap.detail.shouldSwap, false);
  assert.equal(controller.afterRequest({ detail: { xhr: request } }), null);
  assert.equal(controller.afterRequest({ detail: { xhr: request } }), 200);

  const connected = trigger("");
  controller.beforeRequest({
    target: connected,
    detail: { target: { id: "item-panel" }, xhr: { readyState: 4, status: 200 } }
  });
  controller.close();
  assert.equal(connected.focused, true);

  connected.isConnected = false;
  controller.beforeRequest({
    target: connected,
    detail: { target: { id: "item-panel" }, xhr: { readyState: 4, status: 200 } }
  });
  controller.close();
  assert.equal(fallback.focused, true);
});

test("non-panel and non-loading requests are ignored", () => {
  const doc = new FakeDocument();
  const controller = createContextPanelController({ doc });
  const plain = new FakeElement("button");
  plain.closest = () => null;
  controller.beforeRequest({
    target: plain,
    detail: { target: { id: "elsewhere" }, xhr: { status: 200 } }
  });
  controller.beforeRequest({
    target: plain,
    detail: { target: { id: "item-panel" }, xhr: { status: 200 } }
  });
  assert.deepEqual(doc.panel.children, []);
  assert.equal(controller.beforeSwap({ detail: { xhr: { status: 200 } } }), false);
});
