const reservedProfileNames = new Set([
  "auto", "best", "cheapest", "default", "fastest", "latest", "none"
]);

export function normalizeToken(value) {
  return String(value ?? "").trim().toLocaleLowerCase("en-US");
}

function displayToken(value, preserveCase) {
  const trimmed = String(value ?? "").trim();
  return preserveCase ? trimmed : normalizeToken(trimmed);
}

function distinctTokens(values, preserveCase) {
  const seen = new Set();
  return values
    .map(value => displayToken(value, preserveCase))
    .filter(value => {
      const key = normalizeToken(value);
      if (!key || seen.has(key)) return false;
      seen.add(key);
      return true;
    });
}

export function parseTokenValues(value, preserveCase = false) {
  return distinctTokens(String(value ?? "").split(","), preserveCase);
}

export function validateProfileName(value) {
  const normalized = normalizeToken(value);
  if (!normalized) return "Enter a profile name.";
  if (!/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(normalized)) {
    return "Use lowercase words separated by single dashes.";
  }
  if (reservedProfileNames.has(normalized)) {
    return `“${normalized}” is reserved; choose an intent-based name.`;
  }
  return null;
}

export function createTokenPickerState(sourceValue, knownValues = [], options = {}) {
  const preserveCase = options.preserveCase === true;
  let known = distinctTokens(knownValues, preserveCase);
  let values = parseTokenValues(sourceValue, preserveCase)
    .map(value => known.find(candidate => normalizeToken(candidate) === normalizeToken(value)) ?? value);
  known = distinctTokens([...known, ...values], preserveCase);

  return {
    get values() { return [...values]; },
    get known() { return [...known]; },
    get remaining() {
      const selected = new Set(values.map(normalizeToken));
      return known.filter(value => !selected.has(normalizeToken(value)));
    },
    add(value) {
      const key = normalizeToken(value);
      if (!key || values.some(candidate => normalizeToken(candidate) === key)) return false;
      const displayed = known.find(candidate => normalizeToken(candidate) === key)
        ?? displayToken(value, preserveCase);
      values = [...values, displayed];
      if (!known.some(candidate => normalizeToken(candidate) === key)) known = [...known, displayed];
      return true;
    },
    remove(value) {
      const key = normalizeToken(value);
      const next = values.filter(candidate => normalizeToken(candidate) !== key);
      if (next.length === values.length) return false;
      values = next;
      return true;
    },
    swap() {
      if (values.length !== 2) return false;
      values = [values[1], values[0]];
      return true;
    }
  };
}

function element(doc, tag, className, text) {
  const value = doc.createElement(tag);
  if (className) value.className = className;
  if (text !== undefined) value.textContent = text;
  return value;
}

function button(doc, className, text, label) {
  const value = element(doc, "button", className, text);
  value.type = "button";
  if (label) value.setAttribute("aria-label", label);
  return value;
}

function knownValues(picker) {
  try {
    const parsed = JSON.parse(picker.dataset.knownValues || "[]");
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function dependentSelectFor(picker) {
  const id = picker.dataset.dependentSelect;
  return id ? picker.ownerDocument.getElementById(id) : null;
}

function syncDependentSelect(picker, values) {
  const select = dependentSelectFor(picker);
  if (!select) return;

  const selected = values.includes(select.value) ? select.value : "";
  select.replaceChildren();
  const empty = element(select.ownerDocument, "option", "", select.dataset.emptyLabel || "None");
  empty.value = "";
  select.append(empty);
  values.forEach(value => {
    const option = element(select.ownerDocument, "option", "", value);
    option.value = value;
    select.append(option);
  });
  select.value = selected;
}

export function rememberTokenPickerInitialValues(source, dependentSelect = null) {
  source.dataset.settingsInitialValue = source.value;
  if (dependentSelect) dependentSelect.dataset.settingsInitialValue = dependentSelect.value;
}

export function enhanceTokenPicker(picker) {
  if (!picker || picker.dataset.tokenPickerReady === "true") return null;
  const source = picker.querySelector("[data-token-source]");
  if (!source) return null;

  const doc = picker.ownerDocument;
  const tokenLabel = picker.dataset.tokenLabel || "value";
  picker.classList.add("token-picker");
  const state = createTokenPickerState(source.value, knownValues(picker), {
    preserveCase: picker.dataset.preserveCase === "true"
  });
  const ui = element(doc, "div", "token-picker-ui");
  const field = element(doc, "div", "token-picker-field");
  const chips = element(doc, "span", "token-picker-chips");
  const add = button(doc, "token-picker-add", "+ Add", `Add ${tokenLabel}`);
  const popover = element(doc, "div", "token-picker-popover");
  const options = element(doc, "div", "token-picker-options");
  const status = element(doc, "span", "token-picker-status");
  const popoverId = `${source.id}-choices`;

  popover.id = popoverId;
  popover.hidden = true;
  popover.setAttribute("role", "group");
  popover.setAttribute("aria-label", `Add ${tokenLabel}`);
  add.setAttribute("aria-controls", popoverId);
  add.setAttribute("aria-expanded", "false");
  status.setAttribute("role", "status");
  status.setAttribute("aria-live", "polite");
  field.append(chips, add);
  popover.append(options);

  let createInput = null;
  if (picker.dataset.allowCreate === "true") {
    const customValueMode = picker.dataset.createMode === "value";
    const createRow = element(doc, "div", "token-picker-create-row");
    createInput = element(doc, "input", "token-picker-create-input");
    const create = button(doc, "token-picker-create", customValueMode ? "Add" : "Create");
    createInput.type = "text";
    createInput.autocomplete = "off";
    createInput.placeholder = customValueMode ? "" : "docs-only";
    createInput.setAttribute("aria-label", customValueMode ? tokenLabel : "New profile name");
    createRow.append(createInput, create);
    popover.append(createRow, status);

    const createProfile = () => {
      let error = validateProfileName(createInput.value);
      if (customValueMode) {
        error = String(createInput.value).trim() ? null : `Enter ${tokenLabel}.`;
      }
      if (error) {
        status.textContent = error;
        createInput.focus();
        return;
      }
      const name = customValueMode
        ? String(createInput.value).trim()
        : normalizeToken(createInput.value);
      if (!state.add(name)) {
        status.textContent = `“${name}” is already selected.`;
        createInput.focus();
        return;
      }
      createInput.value = "";
      status.textContent = `${name} created and selected.`;
      update(true);
      close();
    };

    create.addEventListener("click", createProfile);
    createInput.addEventListener("keydown", event => {
      if (event.key === "Enter") {
        event.preventDefault();
        createProfile();
      }
    });
  }

  const close = () => {
    popover.hidden = true;
    add.setAttribute("aria-expanded", "false");
  };

  const update = (notify = false) => {
    const values = state.values;
    source.value = values.join(", ");
    chips.replaceChildren();
    if (values.length === 0) {
      chips.append(element(doc, "span", "token-picker-empty", "None selected"));
    } else {
      values.forEach((value, index) => {
        if (picker.dataset.ordered === "true" && index > 0) {
          chips.append(element(doc, "span", "token-picker-separator", "→"));
        }
        const chip = element(doc, "span", "token-picker-chip");
        chip.append(element(doc, "span", "", value));
        const remove = button(doc, "token-picker-remove", "×", `Remove ${value}`);
        remove.addEventListener("click", () => {
          state.remove(value);
          status.textContent = `${value} removed.`;
          update(true);
        });
        chip.append(remove);
        chips.append(chip);
      });
    }

    if (picker.dataset.ordered === "true" && values.length === 2) {
      const swap = button(doc, "token-picker-swap", "⇄", `Swap ${tokenLabel} priority`);
      swap.title = "Swap priority order";
      swap.addEventListener("click", () => {
        state.swap();
        status.textContent = "Priority order swapped.";
        update(true);
      });
      chips.append(swap);
    }

    options.replaceChildren();
    state.remaining.forEach(value => {
      const choice = button(doc, "token-picker-option", value);
      choice.addEventListener("click", () => {
        state.add(value);
        status.textContent = `${value} added.`;
        update(true);
        close();
      });
      options.append(choice);
    });
    if (state.remaining.length === 0) {
      options.append(element(doc, "span", "token-picker-empty-option", "All known names are selected."));
    }
    add.hidden = picker.dataset.allowCreate !== "true" && state.remaining.length === 0;
    if (add.hidden) close();

    syncDependentSelect(picker, values);
    if (notify) source.dispatchEvent(new Event("input", { bubbles: true }));
  };

  add.addEventListener("click", () => {
    const opening = popover.hidden;
    popover.hidden = !opening;
    add.setAttribute("aria-expanded", String(opening));
    status.textContent = "";
    if (opening) (options.querySelector("button") ?? createInput)?.focus();
  });
  picker.addEventListener("keydown", event => {
    if (event.key !== "Escape" || popover.hidden) return;
    event.preventDefault();
    close();
    add.focus();
  });

  source.type = "hidden";
  ui.append(field, popover);
  picker.append(ui);
  picker.dataset.tokenPickerReady = "true";
  update();
  rememberTokenPickerInitialValues(source, dependentSelectFor(picker));
  return { close, state, update };
}

export function installTokenPickers(root) {
  const pickers = root.querySelectorAll?.("[data-token-picker]") ?? [];
  return [...pickers].map(enhanceTokenPicker).filter(Boolean);
}

export function closeTokenPickerPopovers(root, target) {
  root.querySelectorAll?.("[data-token-picker-ready=true]").forEach(picker => {
    if (picker.contains(target)) return;
    const popover = picker.querySelector(".token-picker-popover");
    const add = picker.querySelector(".token-picker-add");
    if (popover) popover.hidden = true;
    if (add) add.setAttribute("aria-expanded", "false");
  });
}
