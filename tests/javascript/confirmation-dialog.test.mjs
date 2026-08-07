import assert from "node:assert/strict";
import test from "node:test";

import {
  installConfirmationDialog
} from "../../src/Highbyte.Wrighty.Web/Assets/confirmation-dialog.mjs";

class FakeElement {
  constructor(document) {
    this.document = document;
    this.dataset = {};
    this.isConnected = true;
    this.listeners = new Map();
    this.open = false;
    this.returnValue = "";
    this.textContent = "";
    this.value = "";
    this.focusCount = 0;
  }

  addEventListener(type, listener, options = {}) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push({ listener, once: options.once === true });
    this.listeners.set(type, listeners);
  }

  dispatch(type) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.forEach(({ listener }) => listener());
    this.listeners.set(type, listeners.filter(({ once }) => !once));
  }

  focus() {
    this.focusCount += 1;
    this.document.activeElement = this;
  }

  showModal() {
    this.open = true;
  }

  close(returnValue = "") {
    this.returnValue = returnValue;
    this.open = false;
    this.dispatch("close");
  }
}

class FakeDocument {
  constructor() {
    this.activeElement = null;
    this.dirtyForm = null;
    this.listeners = new Map();
    this.elements = new Map([
      ["#confirmation-dialog", new FakeElement(this)],
      ["#confirmation-dialog-title", new FakeElement(this)],
      ["#confirmation-dialog-message", new FakeElement(this)],
      ["#confirmation-dialog-cancel", new FakeElement(this)],
      ["#confirmation-dialog-accept", new FakeElement(this)]
    ]);
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  emit(type, event) {
    (this.listeners.get(type) ?? []).forEach(listener => listener(event));
  }

  querySelector(selector) {
    if (selector === ".edit-form[data-dirty=true], .create-form[data-dirty=true]") {
      return this.dirtyForm;
    }
    return this.elements.get(selector) ?? null;
  }
}

function createHarness() {
  const document = new FakeDocument();
  let closePanelCount = 0;
  const controller = installConfirmationDialog({
    document,
    closePanel: () => {
      closePanelCount += 1;
    }
  });
  return {
    controller,
    document,
    element: selector => document.querySelector(selector),
    closePanelCount: () => closePanelCount
  };
}

function eventTarget(matches = {}) {
  return {
    closest: selector => matches[selector] ?? null
  };
}

function cancellableEvent(properties = {}) {
  return {
    ...properties,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    }
  };
}

async function settle() {
  await Promise.resolve();
  await Promise.resolve();
}

test("confirmation presents the requested content and restores focus", async () => {
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  const trigger = new FakeElement(harness.document);

  const result = harness.controller.requestConfirmation({
    message: "Continue with this operation?"
  }, trigger);

  assert.equal(dialog.open, true);
  assert.equal(harness.element("#confirmation-dialog-title").textContent, "Confirm action");
  assert.equal(
    harness.element("#confirmation-dialog-message").textContent,
    "Continue with this operation?");
  assert.equal(harness.element("#confirmation-dialog-accept").textContent, "Continue");
  assert.equal(dialog.dataset.tone, "default");
  assert.equal(harness.element("#confirmation-dialog-cancel").focusCount, 1);

  dialog.close("confirm");

  assert.equal(await result, true);
  assert.equal(trigger.focusCount, 1);
  assert.equal("tone" in dialog.dataset, false);
});

test("an open confirmation rejects a second request", async () => {
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  const cancel = harness.element("#confirmation-dialog-cancel");
  const first = harness.controller.requestConfirmation({
    title: "First",
    message: "First request",
    action: "Proceed",
    tone: "danger"
  });

  const second = harness.controller.requestConfirmation({
    title: "Second",
    message: "Second request"
  });

  assert.equal(await second, false);
  assert.equal(harness.element("#confirmation-dialog-title").textContent, "First");
  assert.equal(dialog.dataset.tone, "danger");
  assert.equal(cancel.focusCount, 2);

  dialog.close("cancel");
  assert.equal(await first, false);
});

test("keyboard handling consumes modal keys and Escape cancels", async () => {
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  assert.equal(harness.controller.handleKeydown({ key: "Escape" }), false);

  const result = harness.controller.requestConfirmation({ message: "Unsaved changes" });
  assert.equal(harness.controller.handleKeydown({ key: "Tab" }), true);

  const escape = cancellableEvent({ key: "Escape" });
  assert.equal(harness.controller.handleKeydown(escape), true);
  assert.equal(escape.defaultPrevented, true);
  assert.equal(dialog.open, false);
  assert.equal(await result, false);
});

test("panel close is immediate when clean and confirmed when dirty", async () => {
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  const panelClose = new FakeElement(harness.document);
  const target = eventTarget({ ".close-panel, .cancel-edit": panelClose });

  harness.document.emit("click", cancellableEvent({ target }));
  assert.equal(harness.closePanelCount(), 1);

  harness.document.dirtyForm = {};
  const dirtyCancel = cancellableEvent({ target });
  harness.document.emit("click", dirtyCancel);
  assert.equal(dirtyCancel.defaultPrevented, true);
  assert.equal(dialog.open, true);
  assert.equal(harness.element("#confirmation-dialog-title").textContent, "Discard unsaved changes?");
  assert.equal(harness.element("#confirmation-dialog-accept").textContent, "Discard changes");

  dialog.close("cancel");
  await settle();
  assert.equal(harness.closePanelCount(), 1);

  harness.document.emit("click", cancellableEvent({ target }));
  dialog.close("confirm");
  await settle();
  assert.equal(harness.closePanelCount(), 2);
});

test("explicit HTMX confirmation uses action metadata and issues once", async () => {
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  const submitter = new FakeElement(harness.document);
  submitter.dataset = {
    confirmTitle: "Archive this item?",
    confirmMessage: "The recorded session is preserved.",
    confirmAction: "Archive",
    confirmTone: "danger"
  };
  const issued = [];
  const event = cancellableEvent({
    target: eventTarget(),
    detail: {
      triggeringEvent: { submitter },
      issueRequest: confirmed => issued.push(confirmed)
    }
  });

  harness.document.emit("htmx:confirm", event);

  assert.equal(event.defaultPrevented, true);
  assert.equal(harness.element("#confirmation-dialog-title").textContent, "Archive this item?");
  assert.equal(
    harness.element("#confirmation-dialog-message").textContent,
    "The recorded session is preserved.");
  assert.equal(harness.element("#confirmation-dialog-accept").textContent, "Archive");
  assert.equal(dialog.dataset.tone, "danger");

  dialog.close("confirm");
  await settle();
  assert.deepEqual(issued, [true]);
});

test("explicit target confirmation uses safe fallback labels", async () => {
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  const explicit = new FakeElement(harness.document);
  explicit.dataset.confirmMessage = "Proceed with the request?";
  const issued = [];
  const event = cancellableEvent({
    target: eventTarget({ "[data-confirm-message]": explicit }),
    detail: {
      issueRequest: confirmed => issued.push(confirmed)
    }
  });

  harness.document.emit("htmx:confirm", event);

  assert.equal(harness.element("#confirmation-dialog-title").textContent, "Confirm action");
  assert.equal(harness.element("#confirmation-dialog-accept").textContent, "Continue");
  dialog.close("cancel");
  await settle();
  assert.deepEqual(issued, []);
});

test("dirty release and item navigation get distinct confirmations", async () => {
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  harness.document.dirtyForm = {};

  const release = new FakeElement(harness.document);
  release.value = "release";
  const releaseIssued = [];
  const releaseEvent = cancellableEvent({
    target: eventTarget(),
    detail: {
      triggeringEvent: { submitter: release },
      issueRequest: confirmed => releaseIssued.push(confirmed)
    }
  });
  harness.document.emit("htmx:confirm", releaseEvent);

  assert.equal(
    harness.element("#confirmation-dialog-title").textContent,
    "Discard this draft and release the claim?");
  assert.equal(harness.element("#confirmation-dialog-accept").textContent, "Discard and release");
  dialog.close("confirm");
  await settle();
  assert.deepEqual(releaseIssued, [true]);

  const navigationIssued = [];
  const navigationEvent = cancellableEvent({
    target: eventTarget({ ".card": {} }),
    detail: {
      issueRequest: confirmed => navigationIssued.push(confirmed)
    }
  });
  harness.document.emit("htmx:confirm", navigationEvent);

  assert.equal(
    harness.element("#confirmation-dialog-title").textContent,
    "Discard this draft and open another item?");
  assert.equal(harness.element("#confirmation-dialog-accept").textContent, "Discard and open");
  dialog.close("cancel");
  await settle();
  assert.deepEqual(navigationIssued, []);
});

test("HTMX requests without a confirmation condition pass through", () => {
  const harness = createHarness();
  const event = cancellableEvent({
    target: eventTarget(),
    detail: { issueRequest: () => assert.fail("request should not be issued by the dialog") }
  });

  harness.document.emit("htmx:confirm", event);

  assert.equal(event.defaultPrevented, false);
  assert.equal(harness.element("#confirmation-dialog").open, false);
});

test("an empty confirmation message is not a confirmation", async () => {
  // A card action with nothing to confirm still renders the data-confirm-* attributes empty on
  // some templates. Selecting on the attribute's presence turned every such action into a
  // confirmation with a blank message — clicked, the operator saw an empty dialog.
  const harness = createHarness();
  const dialog = harness.element("#confirmation-dialog");
  const form = new FakeElement(harness.document);
  form.dataset = { confirmTitle: "", confirmMessage: "", confirmAction: "" };
  const submitter = new FakeElement(harness.document);
  submitter.dataset = {};
  const issued = [];
  const event = cancellableEvent({
    target: eventTarget({ "[data-confirm-message]": form }),
    detail: {
      triggeringEvent: { submitter },
      issueRequest: confirmed => issued.push(confirmed)
    }
  });

  harness.document.emit("htmx:confirm", event);
  await settle();

  // htmx proceeds untouched: no dialog, no interception.
  assert.equal(event.defaultPrevented, false);
  assert.equal(dialog.open, false);
  assert.deepEqual(issued, []);
});
