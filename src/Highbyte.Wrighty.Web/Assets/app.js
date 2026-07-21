const fragment = new URLSearchParams(location.hash.slice(1));
const token = fragment.get("token");
history.replaceState(null, "", `${location.pathname}${location.search}`);

const connectionStatus = document.querySelector("#connection-status");
const boardSearch = document.querySelector("#board-search");
const filterStatus = document.querySelector("#filter-status");
const boardFilters = document.querySelector("#board-filters");
let boardRevision = null;
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

document.addEventListener("wrighty:refresh", () => {
  boardRevision = null;
  refreshBoard();
});

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
  if (authenticationReadyDispatched || !token) return;
  authenticationReadyDispatched = true;
  const board = document.querySelector("#board-content");
  globalThis.htmx?.process(board);
  board?.dispatchEvent(new CustomEvent("wrighty:ready"));
}

function closePanel() {
  const panel = document.querySelector("#item-panel");
  panel.replaceChildren();
  const card = lastOpenedItem
    ? document.querySelector(`.card[data-item-id="${CSS.escape(lastOpenedItem)}"]:not([hidden])`)
    : null;
  (card || boardSearch).focus();
}

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
});

document.addEventListener("htmx:beforeRequest", event => {
  const card = event.target.closest?.(".card");
  if (card) lastOpenedItem = card.dataset.itemId;
});

document.addEventListener("htmx:beforeSwap", event => {
  if (event.detail.xhr.status >= 400 && event.detail.xhr.status < 500) {
    event.detail.shouldSwap = true;
    event.detail.isError = false;
  }
});

document.addEventListener("htmx:afterSwap", event => {
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

  const heading = event.detail.target.querySelector?.(".detail h2");
  if (heading) heading.focus();
  highlightFrontmatter(event.detail.target);
  refreshExpandableValues(event.detail.target);
});

document.addEventListener("htmx:afterRequest", event => {
  const responseStatus = event.detail.xhr.status;
  if (responseStatus >= 200 && responseStatus < 400) {
    setConnection("Connected", "connected");
  } else if (responseStatus === 401) {
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
});

document.addEventListener("click", event => {
  if (event.target.closest("#refresh-board")) {
    boardRevision = null;
    refreshBoard();
  }

  const tab = event.target.closest("[role=tab]");
  if (tab) selectTab(tab);

  const copyButton = event.target.closest(".copy-button[data-copy-target]");
  if (copyButton) void copyValue(copyButton);

  const expandButton = event.target.closest(".expand-value-button[data-expand-target]");
  if (expandButton) toggleExpandableValue(expandButton);

  if (event.target.closest(".close-panel") || event.target.closest(".cancel-edit")) {
    const form = document.querySelector(".edit-form[data-dirty=true], .create-form[data-dirty=true]");
    if (!form || confirm("Discard your unsaved changes?")) closePanel();
  }
});

window.addEventListener("resize", () => refreshExpandableValues());

document.addEventListener("htmx:confirm", event => {
  const submitter = event.detail.triggeringEvent?.submitter;
  const explicitConfirmation =
    submitter?.dataset.confirmMessage ||
    event.target.closest?.("[data-confirm-message]")?.dataset.confirmMessage;
  if (explicitConfirmation) {
    event.preventDefault();
    if (confirm(explicitConfirmation)) event.detail.issueRequest(true);
    return;
  }

  const dirtyForm = document.querySelector(".edit-form[data-dirty=true], .create-form[data-dirty=true]");
  const opensAnotherItem = event.target.closest?.(".card");
  const releasesDraft = submitter?.value === "release";
  if (!dirtyForm || (!opensAnotherItem && !releasesDraft)) return;

  event.preventDefault();
  const question = releasesDraft
    ? "Discard this draft and release the claim?"
    : "Discard this draft and open another work item?";
  if (confirm(question)) event.detail.issueRequest(true);
});

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
  if (handleSearchKeydown(event)) return;
  if (handlePanelKeydown(event)) return;
  if (handleTabKeydown(event)) return;
  handleCardKeydown(event);
});

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") refreshBoard();
});

setInterval(refreshBoard, 2000);

if (!token) {
  setConnection("Launch token missing — reopen Wrighty from the terminal", "error");
} else {
  setConnection("Connecting…");
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", dispatchAuthenticationReady, { once: true });
  } else {
    queueMicrotask(dispatchAuthenticationReady);
  }
}
