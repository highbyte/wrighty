import { installConfirmationDialog } from "./confirmation-dialog.mjs";
import {
  createContextPanelController,
  installContextStateUpdates
} from "./context-panel.mjs";
import { readyPageRegions } from "./page-regions.mjs";
import {
  buildLaunchUrl,
  clearLaunchToken,
  loadLaunchToken
} from "./launch-token.mjs";

const tokenAuthenticationRequired =
  document.querySelector('meta[name="wrighty-auth"]')?.content !== "none";
let token = tokenAuthenticationRequired ? loadLaunchToken() : null;

const connectionStatus = document.querySelector("#connection-status");
const copyAccessLinkButton = document.querySelector("#copy-access-link");
const copyAccessLinkFeedback = document.querySelector("#copy-access-link-feedback");
const boardSearch = document.querySelector("#board-search");
const filterStatus = document.querySelector("#filter-status");
const boardFilters = document.querySelector("#board-filters");
let boardRevision = null;
let providerRevision = null;
let lastOpenedItem = null;
let authenticationReadyDispatched = false;

function setConnection(message, state = "") {
  connectionStatus.textContent = message;
  connectionStatus.dataset.state = state;
}

function refreshBoard() {
  const board = document.querySelector("#board-content");
  if (board && document.visibilityState === "visible") {
    board.dispatchEvent(new CustomEvent("wrighty:refresh"));
  }
}

function refreshProviderCapacity() {
  const providerCapacity = document.querySelector("#provider-capacity-region");
  if (providerCapacity && document.visibilityState === "visible") {
    providerCapacity.dispatchEvent(new CustomEvent("wrighty:refresh"));
  }
}

function refreshDashboard() {
  refreshBoard();
  refreshProviderCapacity();
}

// A card gesture that ended in the panel closes it: the operator has finished deciding, and
// leaving the panel open would make them dismiss a view they never chose to open.
document.addEventListener("wrighty:close-panel", () => closePanel());

document.addEventListener("wrighty:refresh", () => {
  boardRevision = null;
  providerRevision = null;
  refreshDashboard();
});

installContextStateUpdates(document);

function applyClientFilter() {
  const query = boardSearch.value.trim().toLocaleLowerCase();
  const cards = [...document.querySelectorAll("#board-content .card")];
  let visible = 0;

  cards.forEach(card => {
    const matches = query.length === 0 || card.dataset.filterText.toLocaleLowerCase().includes(query);
    card.hidden = !matches;
    if (matches) visible += 1;
  });

  document.querySelectorAll("#board-content .column, #board-content .archived-group").forEach(group => {
    const count = [...group.querySelectorAll(".card")].filter(card => !card.hidden).length;
    const countElement = group.querySelector("[data-visible-count]");
    if (countElement) updateVisibleCount(countElement, group, query, count);
  });

  filterStatus.textContent = query.length === 0
    ? ""
    : `${visible} work item${visible === 1 ? "" : "s"} match “${boardSearch.value.trim()}”.`;
}

function updateVisibleCount(countElement, group, query, count) {
  const total = Number(countElement.dataset.totalCount ?? count);
  const description = visibleCountDescription(
    count,
    total,
    group.matches(".archived-group"),
    query.length > 0);
  countElement.textContent = query.length === 0 ? String(count) : `${count} of ${total}`;
  countElement.dataset.tooltip = description;
  countElement.setAttribute("aria-label", description);
}

function visibleCountDescription(count, total, archived, filtered) {
  const visibleItems = `item${count === 1 ? "" : "s"}`;
  if (!filtered)
    return archived
      ? `${count} archived ${visibleItems} currently shown.`
      : `${count} ${visibleItems} currently shown in this column.`;
  const totalItems = `item${total === 1 ? "" : "s"}`;
  const matches = count === 1 ? "matches" : "match";
  return archived
    ? `${count} of ${total} archived ${totalItems} ${matches} the current search.`
    : `${count} of ${total} ${totalItems} in this column ${matches} the current search.`;
}

function dispatchAuthenticationReady() {
  if (authenticationReadyDispatched || (tokenAuthenticationRequired && !token)) return;
  authenticationReadyDispatched = true;
  readyPageRegions(document, globalThis.htmx);
}

const contextPanel = createContextPanelController({
  doc: document,
  focusFallback: () => {
    const card = lastOpenedItem
      ? document.querySelector(`.card[data-item-id="${CSS.escape(lastOpenedItem)}"]:not([hidden])`)
      : null;
    return card || boardSearch;
  },
  onClose: () => { lastOpenedItem = null; }
});

function closePanel() {
  contextPanel.close();
}

const confirmationUi = installConfirmationDialog({ document, closePanel });

function selectTab(tab) {
  const detail = tab.closest(".detail");
  detail.querySelectorAll("[role=tab]").forEach(value => {
    const selected = value === tab;
    value.classList.toggle("active", selected);
    value.setAttribute("aria-selected", String(selected));
    value.tabIndex = selected ? 0 : -1;
  });
  detail.querySelectorAll("[role=tabpanel]").forEach(value => {
    value.hidden = value.id !== tab.getAttribute("aria-controls");
  });
}

function highlightFrontmatter(root = document) {
  if (!globalThis.hljs) return;
  root.querySelectorAll?.(".frontmatter code.language-yaml:not([data-highlighted])")
    .forEach(code => globalThis.hljs.highlightElement(code));
}

async function writeClipboard(text) {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch {
      // Browsers can deny the asynchronous API even on localhost. Use the synchronous
      // selection fallback before reporting failure.
    }
  }

  const field = document.createElement("textarea");
  field.value = text;
  field.setAttribute("readonly", "");
  field.style.position = "fixed";
  field.style.opacity = "0";
  document.body.append(field);
  field.select();
  const copied = document.execCommand("copy");
  field.remove();
  if (!copied) throw new Error("Clipboard access is unavailable.");
}

async function copyValue(button) {
  const target = document.getElementById(button.dataset.copyTarget);
  const feedback = button.closest("[data-copy-scope]")?.querySelector(".copy-feedback");
  const originalLabel = button.dataset.originalLabel || button.textContent;
  button.dataset.originalLabel = originalLabel;
  if (!target) return;

  try {
    await writeClipboard(target.textContent);
    button.textContent = "Copied";
    if (feedback) feedback.textContent = `${button.dataset.copyName || "Value"} copied to clipboard.`;
    setTimeout(() => {
      if (!button.isConnected) return;
      button.textContent = originalLabel;
      if (feedback) feedback.textContent = "";
    }, 2000);
  } catch {
    button.textContent = "Copy failed";
    if (feedback) feedback.textContent = "Clipboard access was denied. Select and copy the text manually.";
  }
}

async function copyAccessLink(button) {
  const originalLabel = button.dataset.originalLabel || button.textContent;
  button.dataset.originalLabel = originalLabel;
  if (tokenAuthenticationRequired && !token) {
    button.disabled = true;
    copyAccessLinkFeedback.textContent =
      "The access link is unavailable because this browser is not authenticated.";
    return;
  }

  try {
    await writeClipboard(buildLaunchUrl(token));
    button.textContent = "Copied";
    copyAccessLinkFeedback.textContent = token
      ? "Bearer access link copied. Share it only with an intended dashboard user."
      : "Access link copied.";
    setTimeout(() => {
      if (!button.isConnected) return;
      button.textContent = originalLabel;
      copyAccessLinkFeedback.textContent = "";
    }, 4000);
  } catch {
    button.textContent = "Copy failed";
    copyAccessLinkFeedback.textContent =
      "Clipboard access was denied. Copy the Open URL from the Wrighty terminal.";
  }
}

function refreshExpandableValues(root = document) {
  root.querySelectorAll?.(".expand-value-button[data-expand-target]").forEach(button => {
    const target = document.getElementById(button.dataset.expandTarget);
    if (!target) return;
    const expanded = target.classList.contains("expanded");
    button.hidden = !expanded && target.scrollWidth <= target.clientWidth;
  });
}

function toggleExpandableValue(button) {
  const target = document.getElementById(button.dataset.expandTarget);
  if (!target) return;
  const expanded = target.classList.toggle("expanded");
  button.setAttribute("aria-expanded", String(expanded));
  button.textContent = expanded ? "Collapse" : "Show full";
  refreshExpandableValues(button.closest(".detail") || document);
}

document.addEventListener("htmx:configRequest", event => {
  if (token) event.detail.headers["X-Wrighty-Token"] = token;
  const url = String(event.detail.path || "");
  if (boardRevision && url.includes("handler=Board")) {
    event.detail.headers["If-None-Match"] = `"${boardRevision}"`;
  }
  if (providerRevision && url.includes("handler=ProviderCapacity")) {
    event.detail.headers["If-None-Match"] = `"${providerRevision}"`;
  }
});

document.addEventListener("htmx:beforeRequest", event => {
  const card = event.target.closest?.(".card");
  if (card) lastOpenedItem = card.dataset.itemId;
  contextPanel.beforeRequest(event);
});

document.addEventListener("htmx:beforeSwap", event => {
  if (contextPanel.beforeSwap(event)) return;
  if (event.detail.xhr.status >= 400 && event.detail.xhr.status < 500) {
    event.detail.shouldSwap = true;
    event.detail.isError = false;
  }
});

document.addEventListener("htmx:afterSwap", event => {
  contextPanel.afterSwap(event);
  const board = event.detail.target.closest?.("#board-content") || document.querySelector("#board-content");
  if (board?.dataset.revision) {
    const newRevision = board.dataset.revision;
    if (boardRevision && newRevision !== boardRevision && document.querySelector(".edit-form[data-dirty=true]")) {
      const notice = document.querySelector("#stale-edit-notice");
      if (notice) notice.hidden = false;
    }
    boardRevision = newRevision;
    applyClientFilter();
  }
  const providerCapacity =
    event.detail.target.closest?.("#provider-capacity-region") ||
    document.querySelector("#provider-capacity-region");
  if (providerCapacity?.dataset.revision) {
    providerRevision = providerCapacity.dataset.revision;
  }

  const heading = event.detail.target.querySelector?.(".detail h2");
  if (heading) heading.focus();
  highlightFrontmatter(event.detail.target);
  refreshExpandableValues(event.detail.target);
});

document.addEventListener("htmx:afterRequest", event => {
  const responseStatus = contextPanel.afterRequest(event);
  if (responseStatus === null) return;
  if (responseStatus >= 200 && responseStatus < 400) {
    setConnection("Connected", "connected");
  } else if (responseStatus === 401) {
    clearLaunchToken();
    token = null;
    copyAccessLinkButton.disabled = true;
    setConnection("Session expired — reopen Wrighty from the terminal", "error");
  } else {
    setConnection("Request failed — keeping last snapshot", "error");
  }
});

document.addEventListener("htmx:sendError", () => {
  setConnection("Disconnected — keeping last snapshot", "error");
});

document.addEventListener("htmx:timeout", () => {
  setConnection("Disconnected — keeping last snapshot", "error");
});

document.addEventListener("htmx:load", dispatchAuthenticationReady, { once: true });
document.addEventListener("htmx:load", event => highlightFrontmatter(event.detail.elt || document));

document.addEventListener("input", event => {
  if (event.target.closest(".edit-form, .create-form")) {
    event.target.closest(".edit-form, .create-form").dataset.dirty = "true";
  }
  if (event.target === boardSearch) applyClientFilter();
});

document.addEventListener("change", event => {
  if (event.target.matches("#board-filters select[name=scope]")) {
    boardRevision = null;
    boardFilters.requestSubmit();
  }
});

document.addEventListener("submit", event => {
  if (event.target === boardFilters) boardRevision = null;
  // Choosing a mode closes the chooser. The board refresh that follows replaces the card and its
  // dialog anyway; closing here means the choice does not sit open behind an in-flight request.
  if (event.target.closest(".launch-dialog")) event.target.closest("dialog")?.close();
});

document.addEventListener("click", event => {
  if (event.target.closest("#refresh-board")) {
    boardRevision = null;
    providerRevision = null;
    refreshDashboard();
  }

  // A card action that offers modes opens its own dialog. showModal gives focus containment and
  // Escape without us reimplementing either.
  const opener = event.target.closest("[data-open-dialog]");
  if (opener) {
    const dialog = document.getElementById(opener.dataset.openDialog);
    if (dialog && !dialog.open) dialog.showModal();
  }

  const dialogCancel = event.target.closest(".launch-dialog-cancel");
  if (dialogCancel) dialogCancel.closest("dialog")?.close();

  const tab = event.target.closest("[role=tab]");
  if (tab) selectTab(tab);

  const copyButton = event.target.closest(".copy-button[data-copy-target]");
  if (copyButton) void copyValue(copyButton);

  const accessLinkButton = event.target.closest("#copy-access-link");
  if (accessLinkButton) void copyAccessLink(accessLinkButton);

  const expandButton = event.target.closest(".expand-value-button[data-expand-target]");
  if (expandButton) toggleExpandableValue(expandButton);

});

window.addEventListener("resize", () => refreshExpandableValues());

function handleSearchKeydown(event) {
  if (event.target === boardSearch && event.key === "Enter") {
    event.preventDefault();
    applyClientFilter();
    return true;
  }
  return false;
}

function handlePanelKeydown(event) {
  if (event.key === "Escape" && document.querySelector("#item-panel:not(:empty)")) {
    event.preventDefault();
    document.querySelector(".close-panel")?.click();
    return true;
  }
  return false;
}

function handleTabKeydown(event) {
  const tab = event.target.closest?.("[role=tab]");
  if (!tab || !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return false;

  event.preventDefault();
  const tabs = [...tab.closest("[role=tablist]").querySelectorAll("[role=tab]")];
  const current = tabs.indexOf(tab);
  let next = (current - 1 + tabs.length) % tabs.length;
  if (event.key === "Home") next = 0;
  if (event.key === "End") next = tabs.length - 1;
  if (event.key === "ArrowRight") next = (current + 1) % tabs.length;
  selectTab(tabs[next]);
  tabs[next].focus();
  return true;
}

function handleCardKeydown(event) {
  const card = event.target.closest?.(".card");
  if (card && ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key)) {
    event.preventDefault();
    const cards = [...document.querySelectorAll("#board-content .card:not([hidden])")];
    const offset = ["ArrowUp", "ArrowLeft"].includes(event.key) ? -1 : 1;
    cards[(cards.indexOf(card) + offset + cards.length) % cards.length]?.focus();
  }
}

document.addEventListener("keydown", event => {
  if (confirmationUi.handleKeydown(event)) return;
  if (handleSearchKeydown(event)) return;
  if (handlePanelKeydown(event)) return;
  if (handleTabKeydown(event)) return;
  handleCardKeydown(event);
});

// Drag-and-drop status moves. The card buttons remain the accessible baseline: every operation
// drag performs is also reachable by pressing something, so nothing here is drag-only. A drop
// posts the same bundled move the buttons use, and the board refresh is what settles the card —
// a refused drop simply refreshes it back where it came from, so there is no snap-back to track.
let draggedItemId = null;

function dropStatusFor(target) {
  const zone = target?.closest?.("[data-drop-status]");
  return zone ? { zone, status: zone.dataset.dropStatus } : null;
}

function clearDropHighlight() {
  document.querySelectorAll(".drop-target").forEach(zone => {
    zone.classList.remove("drop-target");
  });
}

document.addEventListener("dragstart", event => {
  const card = event.target.closest?.("[data-drag-item]");
  if (!card) return;
  draggedItemId = card.dataset.dragItem;
  card.classList.add("dragging");
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    // Some browsers refuse a drag without data; the id is also what a drop reads back.
    event.dataTransfer.setData("text/plain", draggedItemId);
  }
});

document.addEventListener("dragend", () => {
  document.querySelectorAll(".card-wrap.dragging").forEach(card => {
    card.classList.remove("dragging");
  });
  clearDropHighlight();
  draggedItemId = null;
});

function mayDropOn(itemId, status) {
  const card = document.querySelector(`[data-drag-item="${CSS.escape(itemId)}"]`);
  // The server decides: a card carries the statuses it may move to, and an empty list means the
  // item is not the operator's to move in one gesture. The same rule is enforced server-side, so
  // this only spares the operator a refusal they cannot act on.
  return (card?.dataset.dragTargets || "").split("\u001f").filter(Boolean).includes(status);
}

document.addEventListener("dragover", event => {
  const drop = dropStatusFor(event.target);
  if (!drop || !draggedItemId) return;
  if (!mayDropOn(draggedItemId, drop.status)) return;
  event.preventDefault();
  if (event.dataTransfer) event.dataTransfer.dropEffect = "move";
  if (!drop.zone.classList.contains("drop-target")) {
    clearDropHighlight();
    drop.zone.classList.add("drop-target");
  }
});

document.addEventListener("dragleave", event => {
  const drop = dropStatusFor(event.target);
  if (drop && !drop.zone.contains(event.relatedTarget)) drop.zone.classList.remove("drop-target");
});

document.addEventListener("drop", event => {
  const drop = dropStatusFor(event.target);
  const itemId = draggedItemId || event.dataTransfer?.getData("text/plain");
  clearDropHighlight();
  if (!drop || !itemId || !mayDropOn(itemId, drop.status)) return;
  event.preventDefault();
  const verificationToken = document.querySelector(
    "#board-drag-token input[name='__RequestVerificationToken']")?.value;
  if (!verificationToken) return;
  htmx.ajax("POST", "/?handler=MoveItem", {
    target: "#item-panel",
    swap: "innerHTML",
    values: {
      id: itemId,
      status: drop.status,
      __RequestVerificationToken: verificationToken
    }
  });
});

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") refreshDashboard();
});

setInterval(refreshDashboard, 2000);

if (tokenAuthenticationRequired && !token) {
  copyAccessLinkButton.disabled = true;
  setConnection("Launch token missing — reopen Wrighty from the terminal", "error");
} else {
  setConnection("Connecting…");
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", dispatchAuthenticationReady, { once: true });
  } else {
    queueMicrotask(dispatchAuthenticationReady);
  }
}

// Mapping rows: the Update button is enabled only while the row differs from what is stored, so a
// pending edit is visible and an untouched row offers nothing to press. The stored state is the
// markup's own defaults (the option carrying `selected`, an input's defaultValue), so this needs no
// data model and survives htmx re-renders, which rebuild those defaults from the settings file.
function refreshMappingRow(form) {
  if (!form) return;
  if (form.id === "mapping-add-form") {
    // A mapping needs a model or an effort; with neither, Add would save nothing.
    const model = form.querySelector("[name=model]");
    const effort = form.querySelector("[name=effort]");
    const add = form.querySelector("button[type=submit]");
    if (add) add.disabled = !(model && model.value) && !(effort && effort.value);
    return;
  }
  const update = form.querySelector("button[type=submit]:not([name=remove])");
  if (!update) return;
  let dirty = false;
  for (const control of form.querySelectorAll("select, input:not([type=hidden])")) {
    if (control.tagName === "SELECT") {
      const stored = [...control.options].find(option => option.defaultSelected) ?? control.options[0];
      if (control.value !== (stored ? stored.value : "")) { dirty = true; break; }
    } else if (control.value !== control.defaultValue) { dirty = true; break; }
  }
  update.disabled = !dirty;
}

function refreshMappingRows() {
  document.querySelectorAll(".mapping-row").forEach(refreshMappingRow);
}

document.addEventListener("change", event => refreshMappingRow(event.target.closest?.(".mapping-row")));
document.addEventListener("input", event => refreshMappingRow(event.target.closest?.(".mapping-row")));
document.addEventListener("htmx:afterSwap", refreshMappingRows);
refreshMappingRows();
