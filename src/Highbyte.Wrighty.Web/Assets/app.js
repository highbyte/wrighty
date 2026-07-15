const fragment = new URLSearchParams(location.hash.slice(1));
const token = fragment.get("token");
history.replaceState(null, "", `${location.pathname}${location.search}`);

const status = document.querySelector("#connection-status");
let boardRevision = null;
let lastOpenedItem = null;

function setConnection(message, state = "") {
  status.textContent = message;
  status.dataset.state = state;
}

function refreshBoard() {
  const board = document.querySelector("#board-content");
  if (board && document.visibilityState === "visible") {
    board.dispatchEvent(new CustomEvent("wrighty:refresh"));
  }
}

document.addEventListener("htmx:configRequest", event => {
  if (token) event.detail.headers["X-Wrighty-Token"] = token;
  const url = String(event.detail.path || "");
  if (boardRevision && url.includes("handler=Board")) {
    event.detail.headers["If-None-Match"] = `"${boardRevision}"`;
  }
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
  }
  setConnection("Connected", "connected");
  const heading = event.detail.target.querySelector?.(".detail h2");
  if (heading) heading.focus?.();
});

document.addEventListener("htmx:responseError", () => setConnection("Disconnected — keeping last snapshot", "error"));
document.addEventListener("htmx:sendError", () => setConnection("Disconnected — keeping last snapshot", "error"));

document.addEventListener("input", event => {
  if (event.target.closest(".edit-form")) event.target.closest(".edit-form").dataset.dirty = "true";
  if (event.target.closest("#board-filters")) boardRevision = null;
});

document.addEventListener("click", event => {
  const openedCard = event.target.closest(".card");
  if (openedCard) lastOpenedItem = openedCard.dataset.itemId;

  if (event.target.closest("#refresh-board")) {
    boardRevision = null;
    refreshBoard();
  }

  const tab = event.target.closest("[data-tab]");
  if (tab) {
    const detail = tab.closest(".detail");
    detail.querySelectorAll("[data-tab]").forEach(value => value.classList.toggle("active", value === tab));
    detail.querySelectorAll("[data-tab-panel]").forEach(value => value.classList.toggle("hidden", value.dataset.tabPanel !== tab.dataset.tab));
  }

  if (event.target.closest(".close-panel") || event.target.closest(".cancel-edit")) {
    const form = document.querySelector(".edit-form[data-dirty=true]");
    if (!form || confirm("Discard your unsaved changes?")) {
      document.querySelector("#item-panel").replaceChildren();
      if (lastOpenedItem) document.querySelector(`.card[data-item-id="${CSS.escape(lastOpenedItem)}"]`)?.focus();
    }
  }
});

document.addEventListener("htmx:confirm", event => {
  const submitter = event.detail.triggeringEvent?.submitter;
  if (submitter?.value === "release" && document.querySelector(".edit-form[data-dirty=true]")) {
    event.preventDefault();
    if (confirm("Discard this draft and release the claim?")) event.detail.issueRequest(true);
  }
});

document.addEventListener("keydown", event => {
  if (event.key === "Escape") document.querySelector(".close-panel")?.click();
});

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") refreshBoard();
});

setInterval(refreshBoard, 2000);

if (!token) {
  setConnection("Launch token missing — reopen Wrighty from the terminal", "error");
} else {
  setConnection("Connecting…");
  document.querySelector("#board-content").dispatchEvent(new CustomEvent("wrighty:ready"));
}
