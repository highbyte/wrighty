const dirtyFormSelector = "#settings-content form[data-settings-dirty=true]";

function controlIsDirty(control) {
  if (!control.name || control.disabled || ["button", "reset", "submit"].includes(control.type)) {
    return false;
  }
  if (control.dataset?.settingsInitialValue !== undefined) {
    return control.value !== control.dataset.settingsInitialValue;
  }
  if (control.type === "checkbox" || control.type === "radio") {
    return control.checked !== control.defaultChecked;
  }
  if (control.tagName === "SELECT") {
    const options = [...control.options];
    if (control.multiple) {
      return options.some(option => option.selected !== option.defaultSelected);
    }
    const stored = options.find(option => option.defaultSelected) ?? options[0];
    return control.value !== (stored?.value ?? "");
  }
  return control.value !== control.defaultValue;
}

export function settingsFormIsDirty(form) {
  return [...form.elements].some(controlIsDirty);
}

function updateSettingsFormSaveButton(form, dirty) {
  for (const button of form.querySelectorAll?.("[data-settings-save]") ?? []) {
    button.disabled = !dirty;
  }
}

export function initializeSettingsSaveButtons(root = document) {
  for (const form of root.querySelectorAll?.("#settings-content form") ?? []) {
    const dirty = form.dataset.settingsDirty === "true" || settingsFormIsDirty(form);
    updateSettingsFormSaveButton(form, dirty);
  }
}

export function updateSettingsDirtyIndicator(doc = document) {
  const dirty = Boolean(doc.querySelector(dirtyFormSelector));
  const indicator = doc.querySelector("#tab-settings-unsaved");
  if (indicator) indicator.hidden = !dirty;
  return dirty;
}

export function revealFirstDirtySettingsForm(doc = document) {
  const form = doc.querySelector(dirtyFormSelector);
  if (!form) return false;

  form.scrollIntoView?.({ behavior: "smooth", block: "center" });
  const focusTarget = form.querySelector?.("[data-settings-save]:not(:disabled)") ??
    form.querySelector?.(
      "input:not([type=hidden]):not(:disabled), select:not(:disabled), textarea:not(:disabled)");
  focusTarget?.focus?.({ preventScroll: true });
  return true;
}

export function refreshSettingsDirtyState(target, doc = document) {
  if (!target?.matches?.("[name]")) return false;
  const form = target.closest?.("#settings-content form");
  if (!form) return false;

  const dirty = settingsFormIsDirty(form);
  if (dirty) form.dataset.settingsDirty = "true";
  else delete form.dataset.settingsDirty;
  updateSettingsFormSaveButton(form, dirty);
  updateSettingsDirtyIndicator(doc);
  return true;
}

export function tabLeavesUnsavedSettings(tab, doc = document) {
  const dirtyForms = [...doc.querySelectorAll(dirtyFormSelector)];
  if (dirtyForms.length === 0) return false;

  const panelId = tab?.getAttribute?.("aria-controls");
  const destination = panelId ? doc.getElementById(panelId) : null;
  return !destination || dirtyForms.some(form => !destination.contains(form));
}

export function createSettingsNavigationGuard({
  doc = document,
  requestConfirmation,
  selectTab,
  discardSettings
}) {
  return function selectTabWithSettingsGuard(tab, { focus = false, afterSelect = null } = {}) {
    const finishSelection = () => {
      selectTab(tab);
      if (focus) tab.focus();
      afterSelect?.();
    };
    if (!tabLeavesUnsavedSettings(tab, doc)) {
      finishSelection();
      return false;
    }

    const currentTab = tab.closest("[role=tablist]")?.querySelector(
      "[role=tab][aria-selected=true]");
    void requestConfirmation({
      title: "Discard unsaved settings?",
      message: "Your unsaved settings will be lost.",
      action: "Discard changes",
      tone: "danger"
    }, currentTab).then(confirmed => {
      if (!confirmed) return;
      discardSettings();
      finishSelection();
    });
    return true;
  };
}
