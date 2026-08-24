import { installConfirmationDialog } from "./confirmation-dialog.mjs";
import {
  createContextPanelController,
  installContextStateUpdates
} from "./context-panel.mjs";
import {
  readyPageRegions,
  refreshVisibleOperations,
  revealWorkerProcesses
} from "./page-regions.mjs";
import {
  captureHostedLogViews,
  consumeHostedLogRestore,
  restoreHostedLogViews,
  revealHostedLogTail
} from "./hosted-log.mjs";
import {
  buildLaunchUrl,
  clearLaunchToken,
  loadLaunchToken
} from "./launch-token.mjs";
import {
  captureBoardControlFocus,
  restoreBoardControlFocus,
  captureOperationsControlFocus,
  restoreOperationsControlFocus,
  syncSortDirectionButton,
  toggleSortDirection,
  dismissBoardFilterMenu,
  syncBoardFilterIndicator,
  clearBoardFilters,
  resetBoardView,
  clearOperationsFilters,
  applyOperationsSort
} from "./board-controls.mjs";
import { localizeRelativeTimes } from "./relative-time.mjs";
import {
  closeTokenPickerPopovers,
  installTokenPickers
} from "./token-picker.mjs";
import {
  captureSettingsScrollAnchor,
  restoreSettingsScrollAnchor
} from "./settings-scroll.mjs";
import {
  createSettingsNavigationGuard,
  dismissWorkspaceModeHelp,
  initializeSettingsSaveButtons,
  refreshSettingsDirtyState,
  revealFirstDirtySettingsForm,
  updateSettingsDirtyIndicator
} from "./settings-dirty.mjs";

const tokenAuthenticationRequired =
  document.querySelector('meta[name="wrighty-auth"]')?.content !== "none";
let token = tokenAuthenticationRequired ? loadLaunchToken() : null;

const connectionStatus = document.querySelector("#connection-status");
const copyAccessLinkButton = document.querySelector("#copy-access-link");
const copyAccessLinkFeedback = document.querySelector("#copy-access-link-feedback");
const boardSearch = document.querySelector("#board-search");
const filterStatus = document.querySelector("#filter-status");
const boardFilters = document.querySelector("#board-filters");
const boardFilterMenu = document.querySelector("#board-filter-menu");
let boardRevision = null;
let providerRevision = null;
let workerSummaryRevision = null;
let lastOpenedItem = null;
let authenticationReadyDispatched = false;
let boardControlFocus = null;
let operationsControlFocus = null;
let settingsScrollAnchor = null;
let hostedLogViews = [];
let workerProcessesRevealPending = false;

syncBoardFilterIndicator(boardFilters, boardFilterMenu);

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

function refreshWorkerSummary() {
  const workerSummary = document.querySelector("#worker-summary-region");
  if (workerSummary && document.visibilityState === "visible" &&
      !workerSummary.matches(".htmx-request")) {
    workerSummary.dispatchEvent(new CustomEvent("wrighty:refresh"));
  }
}

function refreshDashboard() {
  refreshBoard();
  refreshWorkerSummary();
  refreshProviderCapacity();
  refreshVisibleOperations(document);
}

function syncSortDirectionButtons(root = document) {
  root.querySelectorAll?.("[data-sort-direction-for]").forEach(button => {
    const select = document.getElementById(button.dataset.sortDirectionFor);
    if (select) syncSortDirectionButton(select, button);
  });
}

// A card gesture that ended in the panel closes it: the operator has finished deciding, and
// leaving the panel open would make them dismiss a view they never chose to open.
document.addEventListener("wrighty:close-panel", () => closePanel());

document.addEventListener("wrighty:refresh", () => {
  boardRevision = null;
  providerRevision = null;
  workerSummaryRevision = null;
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

  const structured = document.querySelector("#board-content")?.dataset.structuredFilterCount;
  const itemLabel = `${visible} work item${visible === 1 ? "" : "s"}`;
  if (query.length === 0) {
    filterStatus.textContent = structured === undefined ? "" : `${itemLabel} match the active filters.`;
  } else {
    filterStatus.textContent = `${itemLabel} match “${boardSearch.value.trim()}”.`;
  }
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
  const tablist = tab.closest("[role=tablist]");
  tablist.querySelectorAll("[role=tab]").forEach(value => {
    const selected = value === tab;
    value.classList.toggle("active", selected);
    value.setAttribute("aria-selected", String(selected));
    value.tabIndex = selected ? 0 : -1;
    const panel = document.getElementById(value.getAttribute("aria-controls"));
    if (panel) panel.hidden = !selected;
  });
  // The page tabs remember themselves in the fragment, so a reload or a shared URL reopens the
  // same section. replaceState rather than assignment: switching tabs is not a history entry.
  if (tablist.id === "page-tabs" && tab.dataset.section) {
    history.replaceState(null, "", `#${tab.dataset.section}`);
  }
}

function revealPendingWorkerProcesses() {
  if (!workerProcessesRevealPending || !revealWorkerProcesses(document)) return false;
  workerProcessesRevealPending = false;
  return true;
}

function openWorkerProcesses() {
  const operationsTab = document.querySelector("#tab-operations");
  if (!operationsTab) return;
  selectTabWithSettingsGuard(operationsTab, {
    afterSelect() {
      workerProcessesRevealPending = true;
      requestAnimationFrame(revealPendingWorkerProcesses);
    }
  });
}

const selectTabWithSettingsGuard = createSettingsNavigationGuard({
  doc: document,
  requestConfirmation: confirmationUi.requestConfirmation,
  selectTab,
  discardSettings() {
    // Refresh from storage while the user views the destination tab. The request starts
    // synchronously; clearing its captured scroll anchor prevents the hidden refresh from
    // changing the destination tab's viewport when it completes. The dirty marker remains
    // until a successful swap, so a failed refresh cannot make the draft look discarded.
    document.querySelector("#refresh-settings")?.click();
    settingsScrollAnchor = null;
  }
});

function restorePageTabFromHash() {
  const section = location.hash.slice(1);
  if (!section) return;
  const tab = document.querySelector(
    `#page-tabs [role=tab][data-section="${CSS.escape(section)}"]`);
  // An unknown fragment (a stale token, a section this backend does not render) is ignored.
  if (tab) selectTab(tab);
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
      ? "Bearer access link copied. Share it only with an intended web console user."
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
  if (workerSummaryRevision && url.includes("handler=WorkerSummary")) {
    event.detail.headers["If-None-Match"] = `"${workerSummaryRevision}"`;
  }
});

document.addEventListener("htmx:beforeRequest", event => {
  const card = event.target.closest?.(".card");
  if (card) lastOpenedItem = card.dataset.itemId;
  const scrollAnchor = captureSettingsScrollAnchor(
    event.detail.elt || event.target,
    event.detail.target
  );
  if (scrollAnchor) settingsScrollAnchor = scrollAnchor;
  contextPanel.beforeRequest(event);
});

document.addEventListener("htmx:beforeSwap", event => {
  if (contextPanel.beforeSwap(event)) return;
  const swapTarget = event.detail.target;
  if (swapTarget?.classList?.contains("hosted-worker-log") ||
      swapTarget?.id === "operations-content") {
    hostedLogViews = captureHostedLogViews(swapTarget);
  }
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
    localizeRelativeTimes(board);
    restoreBoardControlFocus(document, boardControlFocus);
    boardControlFocus = null;
  }
  const providerCapacity =
    event.detail.target.closest?.("#provider-capacity-region") ||
    document.querySelector("#provider-capacity-region");
  if (providerCapacity?.dataset.revision) {
    providerRevision = providerCapacity.dataset.revision;
  }
  const workerSummary =
    event.detail.target.closest?.("#worker-summary-region") ||
    document.querySelector("#worker-summary-region");
  if (workerSummary?.dataset.revision) {
    workerSummaryRevision = workerSummary.dataset.revision;
  }
  if (event.detail.target.closest?.("#operations-content")) {
    restoreOperationsControlFocus(document, operationsControlFocus);
    operationsControlFocus = null;
    if (workerProcessesRevealPending) requestAnimationFrame(revealPendingWorkerProcesses);
  }
  if (event.detail.target.classList?.contains("hosted-worker-log") ||
      event.detail.target.id === "operations-content") {
    // outerHTML swaps leave detail.target pointing at the detached old node. Resolve against the
    // live document so the captured view lands on the replacement disclosure and log viewport.
    restoreHostedLogViews(document, hostedLogViews);
    hostedLogViews = [];
  }

  const heading = event.detail.target.querySelector?.(".detail h2");
  if (heading) heading.focus();
  highlightFrontmatter(event.detail.target);
  refreshExpandableValues(event.detail.target);
  refreshAttentionBadge();
  syncSortDirectionButtons(event.detail.target);
  installTokenPickers(event.detail.target);
  initializeSettingsSaveButtons(document);
  updateSettingsDirtyIndicator(document);
  const swappedSettings = event.target.closest?.("#settings-content");
  // Correct the outerHTML swap immediately so the browser never paints its temporary jump.
  // Keep the anchor until afterSettle because the settings grid can still move by a fraction.
  restoreSettingsScrollAnchor(settingsScrollAnchor, swappedSettings);
});

document.addEventListener("htmx:afterSettle", event => {
  const settledSettings = event.target.closest?.("#settings-content");
  if (restoreSettingsScrollAnchor(settingsScrollAnchor, settledSettings)) {
    settingsScrollAnchor = null;
  }
});

// The needs-attention count on the tab label, so items needing a human are noticed from any tab.
// The board fragment carries the count where a board exists; the operations fragment carries it
// for the GitHub view. Neither attribute present (archived scope) leaves the badge as it was.
function refreshAttentionBadge() {
  const badge = document.querySelector("#tab-attention-badge");
  if (!badge) return;
  const count = document.querySelector("#board-content")?.dataset.attentionCount ??
    document.querySelector("#operations-content")?.dataset.attentionCount;
  if (count === undefined) return;
  badge.textContent = count;
  badge.hidden = count === "0";
}

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
document.addEventListener("htmx:load", event => localizeRelativeTimes(event.detail.elt || document));
document.addEventListener("htmx:load", event => syncSortDirectionButtons(event.detail.elt || document));
document.addEventListener("htmx:load", event => installTokenPickers(event.detail.elt || document));
document.addEventListener("htmx:load", () => initializeSettingsSaveButtons(document));

document.addEventListener("input", event => {
  refreshSettingsDirtyState(event.target, document);
  if (event.target.closest(".edit-form, .create-form")) {
    event.target.closest(".edit-form, .create-form").dataset.dirty = "true";
  }
  if (event.target === boardSearch) applyClientFilter();
});

document.addEventListener("change", event => {
  refreshSettingsDirtyState(event.target, document);
  if (event.target.matches("select[data-sort-select]")) {
    const button = document.querySelector(
      `[data-sort-direction-for="${CSS.escape(event.target.id)}"]`);
    if (button) syncSortDirectionButton(event.target, button);
  }
  if (event.target.matches("#board-filters select:not([name=scope]), #board-filters input[name], [data-board-column-sort-index]")) {
    syncBoardFilterIndicator(boardFilters, boardFilterMenu);
    boardControlFocus = captureBoardControlFocus(document);
    boardRevision = null;
    boardFilters.requestSubmit();
  } else if (event.target.matches("#board-filters select[name=scope]")) {
    boardRevision = null;
    boardFilters.requestSubmit();
  } else if (event.target.matches("#operations-filters select, #operations-filters input")) {
    operationsControlFocus = captureOperationsControlFocus(document);
    event.target.form.requestSubmit();
  }
});

document.addEventListener("submit", event => {
  if (event.target === boardFilters) boardRevision = null;
  // Choosing a mode closes the chooser. The board refresh that follows replaces the card and its
  // dialog anyway; closing here means the choice does not sit open behind an in-flight request.
  if (event.target.closest(".launch-dialog")) event.target.closest("dialog")?.close();
});

function handleBoardSortClick(target) {
  const direction = target.closest("[data-sort-direction-for]");
  if (!direction) return false;

  const select = document.getElementById(direction.dataset.sortDirectionFor);
  if (select && toggleSortDirection(select, direction)) {
    boardControlFocus = select.dataset.boardColumnSortIndex || null;
    boardRevision = null;
    boardFilters.requestSubmit();
  }
  return true;
}

function handleBoardFilterClearClick(target) {
  const clearAll = target.closest("[data-clear-board-filters]");
  if (clearAll) {
    boardRevision = null;
    if (clearBoardFilters(boardFilters)) syncBoardFilterIndicator(boardFilters, boardFilterMenu);
    return true;
  }

  const clearFilter = target.closest("[data-clear-board-filter]");
  if (!clearFilter) return false;

  const name = clearFilter.dataset.clearBoardFilter;
  const value = clearFilter.dataset.clearBoardValue;
  boardFilters.querySelectorAll(`[name="${CSS.escape(name)}"]`).forEach(control => {
    if (value !== undefined && control.value.toLocaleLowerCase() !== value.toLocaleLowerCase()) return;
    if (control.matches("input[type=checkbox], input[type=radio]")) control.checked = false;
    else control.value = "";
  });
  syncBoardFilterIndicator(boardFilters, boardFilterMenu);
  boardRevision = null;
  boardFilters.requestSubmit();
  return true;
}

function handleBoardResetClick(target) {
  if (!target.closest("[data-reset-board-view]")) return false;
  boardRevision = null;
  if (resetBoardView(boardFilters)) {
    syncBoardFilterIndicator(boardFilters, boardFilterMenu);
    syncSortDirectionButtons(document);
    applyClientFilter();
  }
  return true;
}

function handleOperationsFilterClick(target) {
  const clearFilter = target.closest("[data-clear-operations-filter]");
  if (clearFilter) {
    const form = document.querySelector("#operations-filters");
    const control = form?.elements.namedItem(clearFilter.dataset.clearOperationsFilter);
    if (control) control.value = "";
    form?.requestSubmit();
    return true;
  }
  if (!target.closest("[data-clear-operations-filters]")) return false;
  clearOperationsFilters(document.querySelector("#operations-filters"));
  return true;
}

function handleOperationsSortClick(target) {
  const sort = target.closest("[data-operations-sort]");
  if (!sort) return false;
  operationsControlFocus = `sort:${sort.dataset.operationsSortField}`;
  applyOperationsSort(document.querySelector("#operations-filters"), sort.dataset.operationsSort);
  return true;
}

function handleGeneralClick(target) {
  if (target.closest("#refresh-board")) {
    boardRevision = null;
    providerRevision = null;
    refreshDashboard();
  }

  // A card action that offers modes opens its own dialog. showModal gives focus containment and
  // Escape without us reimplementing either.
  const opener = target.closest("[data-open-dialog]");
  if (opener) {
    const dialog = document.getElementById(opener.dataset.openDialog);
    if (dialog && !dialog.open) dialog.showModal();
  }

  const dialogCancel = target.closest(".launch-dialog-cancel");
  if (dialogCancel) dialogCancel.closest("dialog")?.close();

  if (target.closest("[data-open-worker-processes]")) {
    openWorkerProcesses();
    return;
  }

  const tab = target.closest("[role=tab]");
  if (tab) {
    if (tab.id === "tab-settings" && tab.getAttribute("aria-selected") === "true" &&
        revealFirstDirtySettingsForm(document)) return;
    selectTabWithSettingsGuard(tab);
    return;
  }

  const copyButton = target.closest(".copy-button[data-copy-target]");
  if (copyButton) void copyValue(copyButton);

  const accessLinkButton = target.closest("#copy-access-link");
  if (accessLinkButton) void copyAccessLink(accessLinkButton);

  const expandButton = target.closest(".expand-value-button[data-expand-target]");
  if (expandButton) toggleExpandableValue(expandButton);
}

document.addEventListener("click", event => {
  closeTokenPickerPopovers(document, event.target);
  dismissWorkspaceModeHelp(document, event.target);
  const filterClose = event.target.closest?.("[data-close-board-filters]");
  if (dismissBoardFilterMenu(boardFilterMenu, event.target) && filterClose) return;
  if (handleBoardSortClick(event.target)) return;
  if (handleBoardFilterClearClick(event.target)) return;
  if (handleBoardResetClick(event.target)) return;
  if (handleOperationsFilterClick(event.target)) return;
  if (handleOperationsSortClick(event.target)) return;
  handleGeneralClick(event.target);
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
  selectTabWithSettingsGuard(tabs[next], { focus: true });
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

window.addEventListener("beforeunload", event => {
  if (!updateSettingsDirtyIndicator(document)) return;
  event.preventDefault();
  event.returnValue = "";
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

// The disclosure is a reader, not disposable card decoration. Opening starts at the newest
// event; later Operations swaps preserve an intentional scroll-back instead of pulling the reader
// down. One immediate log request avoids waiting for the next normal Operations refresh.
document.addEventListener("toggle", event => {
  const panel = event.target.closest?.("[data-hosted-worker-log-panel]");
  // Restoring an open disclosure after an Operations outerHTML swap emits a browser toggle event
  // too. It is not a reader request to jump to the tail; the captured viewport was already
  // restored and must win.
  if (consumeHostedLogRestore(panel)) return;
  if (!panel?.open) return;
  revealHostedLogTail(panel);
  const log = panel.querySelector(".hosted-worker-log");
  if (log) htmx.trigger(log, "wrighty:hosted-worker-log");
}, true);

restorePageTabFromHash();

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
function storedControlValue(control) {
  if (control.tagName !== "SELECT") return control.defaultValue;
  const stored = [...control.options].find(option => option.defaultSelected) ?? control.options[0];
  return stored ? stored.value : "";
}

function refreshAddRow(form) {
  // A mapping needs a model or an effort; with neither, Add would save nothing.
  const add = form.querySelector("button[type=submit]");
  if (!add) return;
  add.disabled = !form.querySelector("[name=model]")?.value &&
                 !form.querySelector("[name=effort]")?.value;
}

function refreshMappingRow(form) {
  if (!form) return;
  if (form.id === "mapping-add-form") {
    refreshAddRow(form);
    return;
  }
  const update = form.querySelector("button[type=submit]:not([name=remove])");
  if (!update) return;
  const controls = [...form.querySelectorAll("select, input:not([type=hidden])")];
  update.disabled = controls.every(control => control.value === storedControlValue(control));
}

function refreshMappingRows() {
  document.querySelectorAll(".mapping-row").forEach(refreshMappingRow);
}

document.addEventListener("change", event => refreshMappingRow(event.target.closest?.(".mapping-row")));
document.addEventListener("input", event => refreshMappingRow(event.target.closest?.(".mapping-row")));
document.addEventListener("htmx:afterSwap", refreshMappingRows);
refreshMappingRows();
