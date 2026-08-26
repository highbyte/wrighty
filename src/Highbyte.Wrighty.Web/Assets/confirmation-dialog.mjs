export function installConfirmationDialog({ document, closePanel }) {
  const dialog = document.querySelector("#confirmation-dialog");
  const title = document.querySelector("#confirmation-dialog-title");
  const message = document.querySelector("#confirmation-dialog-message");
  const cancel = document.querySelector("#confirmation-dialog-cancel");
  const accept = document.querySelector("#confirmation-dialog-accept");
  let protectedTrigger = null;

  // Complete the dialog explicitly instead of depending on form[method=dialog] to copy the
  // submit button's value into dialog.returnValue. The confirmed HTMX request resumes from the
  // close event, so a browser that closes with an empty return value silently cancels the action.
  accept.addEventListener("click", event => {
    event.preventDefault();
    dialog.close("confirm");
  });

  function requestConfirmation(
    { title: heading = "Confirm action", message: detail, action = "Continue", tone = "" },
    trigger = document.activeElement) {
    if (dialog.open) {
      cancel.focus();
      return Promise.resolve(false);
    }

    title.textContent = heading;
    message.textContent = detail;
    accept.textContent = action;
    dialog.returnValue = "";
    dialog.dataset.tone = tone === "danger" ? "danger" : "default";
    const restoreFocus = typeof trigger?.focus === "function" ? trigger : null;
    protectedTrigger = trigger?.isConnected ? trigger : null;

    return new Promise(resolve => {
      dialog.addEventListener("close", () => {
        const confirmed = dialog.returnValue === "confirm";
        protectedTrigger = null;
        delete dialog.dataset.tone;
        if (restoreFocus?.isConnected) restoreFocus.focus();
        resolve(confirmed);
      }, { once: true });
      dialog.showModal();
      cancel.focus();
    });
  }

  document.addEventListener("click", event => {
    const panelClose = event.target.closest?.(".close-panel, .cancel-edit");
    if (!panelClose) return;

    const dirtyForm = document.querySelector(
      ".edit-form[data-dirty=true], .create-form[data-dirty=true]");
    if (!dirtyForm) {
      closePanel();
      return;
    }

    event.preventDefault();
    void requestConfirmation({
      title: "Discard unsaved changes?",
      message: "Your changes will be lost.",
      action: "Discard changes",
      tone: "danger"
    }, panelClose).then(confirmed => {
      if (confirmed) closePanel();
    });
  });

  document.addEventListener("htmx:confirm", event => {
    const submitter = event.detail.triggeringEvent?.submitter;
    // Both halves test the *value*, not the attribute's presence. Card actions render the
    // data-confirm-* attributes unconditionally, so an action with nothing to confirm still
    // carries an empty data-confirm-message; the presence selector this replaces matched it, and
    // every such action opened the dialog with nothing in it. The template cannot be relied on to
    // omit them — Razor emits the attribute for a null-valued conditional expression too — so the
    // check belongs here.
    const carrier = event.target.closest?.("[data-confirm-message]");
    const explicitConfirmation =
      (submitter?.dataset.confirmMessage ? submitter : null) ||
      (carrier?.dataset.confirmMessage ? carrier : null);
    if (explicitConfirmation) {
      event.preventDefault();
      const issueRequest = event.detail.issueRequest;
      void requestConfirmation({
        title: explicitConfirmation.dataset.confirmTitle || "Confirm action",
        message: explicitConfirmation.dataset.confirmMessage,
        action:
          explicitConfirmation.dataset.confirmAction ||
          submitter?.textContent?.trim() ||
          "Continue",
        tone: explicitConfirmation.dataset.confirmTone
      }, submitter || explicitConfirmation).then(confirmed => {
        if (confirmed) issueRequest(true);
      });
      return;
    }

    const dirtyForm = document.querySelector(
      ".edit-form[data-dirty=true], .create-form[data-dirty=true]");
    const opensAnotherItem = event.target.closest?.(".card");
    const releasesDraft = submitter?.value === "release";
    if (!dirtyForm || (!opensAnotherItem && !releasesDraft)) return;

    event.preventDefault();
    const issueRequest = event.detail.issueRequest;
    void requestConfirmation(releasesDraft
      ? {
          title: "Discard this draft and release the claim?",
          message: "Your unsaved changes will be lost and the claim will be released.",
          action: "Discard and release",
          tone: "danger"
        }
      : {
          title: "Discard this draft and open another item?",
          message: "Your unsaved changes will be lost.",
          action: "Discard and open",
          tone: "danger"
        }, submitter || event.target).then(confirmed => {
          if (confirmed) issueRequest(true);
        });
  });

  function handleKeydown(event) {
    if (!dialog.open) return false;
    if (event.key === "Escape") {
      event.preventDefault();
      dialog.close("cancel");
    }
    return true;
  }

  function wouldReplaceTrigger(target) {
    return Boolean(
      protectedTrigger?.isConnected &&
      typeof target?.contains === "function" &&
      target.contains(protectedTrigger));
  }

  return { handleKeydown, requestConfirmation, wouldReplaceTrigger };
}
