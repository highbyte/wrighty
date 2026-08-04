const appearances = new Set(["approved", "needs-review", "unknown"]);

/** Installs the row update raised after authoritative context inspection. */
export function installContextStateUpdates(doc) {
  doc.addEventListener("wrighty:context-state", event => {
    const automationKey = event.detail?.automationKey;
    const label = event.detail?.label;
    const appearance = event.detail?.appearance;
    if (typeof automationKey !== "string" || automationKey.length === 0) return;
    if (typeof label !== "string" || label.length === 0) return;
    if (!appearances.has(appearance)) return;
    const state = doc.getElementById(`context-approval-state-${automationKey}`);
    if (!state) return;
    state.className = `state-pill context-approval-${appearance}`;
    state.textContent = label;
    if (typeof event.detail?.title === "string" && event.detail.title.length > 0) {
      state.title = event.detail.title;
    } else {
      state.removeAttribute("title");
    }
  });
}

/** Owns the loading, cancellation, failure, and focus lifecycle of the details drawer. */
export function createContextPanelController({ doc, focusFallback, onClose }) {
  let lastTrigger = null;
  let lastTriggerId = null;
  let activeRequest = null;
  const cancelledRequests = new WeakSet();

  function panel() {
    return doc.querySelector("#item-panel");
  }

  function showLoading(
    message,
    detailMessage = "Fetching the latest approval diagnostics from GitHub…") {
    const target = panel();
    const detail = doc.createElement("article");
    detail.className = "detail panel-loading";

    const header = doc.createElement("header");
    header.className = "detail-header";
    const heading = doc.createElement("h2");
    heading.tabIndex = -1;
    heading.textContent = message;
    const close = doc.createElement("button");
    close.type = "button";
    close.className = "close-panel";
    close.setAttribute("aria-label", "Close details");
    close.textContent = "×";
    header.append(heading, close);

    const status = doc.createElement("p");
    status.className = "panel-loading-status";
    status.setAttribute("role", "status");
    const spinner = doc.createElement("span");
    spinner.className = "panel-loading-spinner";
    spinner.setAttribute("aria-hidden", "true");
    const statusText = doc.createElement("span");
    statusText.textContent = detailMessage;
    status.append(spinner, statusText);

    detail.append(header, status);
    target.setAttribute("aria-busy", "true");
    target.replaceChildren(detail);
    heading.focus();
  }

  function showLoadFailure() {
    const target = panel();
    target.removeAttribute("aria-busy");
    const heading = target.querySelector(".panel-loading h2");
    if (heading) heading.textContent = "Unable to load details";
    const status = target.querySelector(".panel-loading-status");
    if (!status) return;
    status.className = "error";
    status.setAttribute("role", "alert");
    status.textContent = "The request failed. Close this drawer and choose Inspect to try again.";
  }

  return {
    close() {
      const target = panel();
      if (activeRequest && activeRequest.readyState < 4) {
        cancelledRequests.add(activeRequest);
      }
      activeRequest = null;
      target.removeAttribute("aria-busy");
      target.replaceChildren();
      const trigger =
        (lastTriggerId ? doc.getElementById(lastTriggerId) : null) ||
        (lastTrigger?.isConnected ? lastTrigger : null);
      (trigger || focusFallback?.())?.focus();
      lastTrigger = null;
      lastTriggerId = null;
      onClose?.();
    },

    beforeRequest(event) {
      if (event.detail.target?.id !== "item-panel") return;
      if (!event.target.closest?.("#item-panel")) {
        lastTrigger = event.target;
        lastTriggerId = event.target.id || null;
      }
      const source = event.target.closest?.("[data-panel-loading-label]");
      if (!source) return;
      activeRequest = event.detail.xhr;
      showLoading(source.dataset.panelLoadingLabel, source.dataset.panelLoadingDetail);
    },

    beforeSwap(event) {
      if (cancelledRequests.has(event.detail.xhr)) {
        event.detail.shouldSwap = false;
        return true;
      }
      if (event.detail.xhr === activeRequest && event.detail.xhr.status >= 400) {
        event.detail.shouldSwap = false;
        return true;
      }
      return false;
    },

    afterSwap(event) {
      if (event.detail.target.id === "item-panel") {
        event.detail.target.removeAttribute("aria-busy");
      }
    },

    afterRequest(event) {
      if (cancelledRequests.has(event.detail.xhr)) {
        cancelledRequests.delete(event.detail.xhr);
        return null;
      }
      const responseStatus = event.detail.xhr.status;
      if (event.detail.xhr === activeRequest) {
        activeRequest = null;
        if (responseStatus < 200 || responseStatus >= 400) showLoadFailure();
      }
      return responseStatus;
    }
  };
}
