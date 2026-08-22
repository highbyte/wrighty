export function captureBoardControlFocus(doc) {
  const active = doc.activeElement;
  if (!active?.matches?.("[data-board-column-sort-index]")) return null;
  return active.dataset.boardColumnSortIndex || null;
}

export function restoreBoardControlFocus(doc, columnIndex) {
  if (columnIndex === null) return false;
  const control = [...doc.querySelectorAll("[data-board-column-sort-index]")]
    .find(value => value.dataset.boardColumnSortIndex === columnIndex);
  if (!control) return false;
  control.focus();
  return true;
}

export function captureOperationsControlFocus(doc) {
  const active = doc.activeElement;
  if (active?.matches?.("#operations-filters [name]")) return active.name || null;
  return active?.matches?.("[data-operations-sort-field]")
    ? `sort:${active.dataset.operationsSortField}`
    : null;
}

export function restoreOperationsControlFocus(doc, name) {
  if (name === null) return false;
  const sortField = name.startsWith("sort:") ? name.slice("sort:".length) : null;
  const selector = sortField === null
    ? "#operations-filters [name]"
    : "[data-operations-sort-field]";
  const control = [...doc.querySelectorAll(selector)]
    .find(value => sortField === null
      ? value.name === name
      : value.dataset.operationsSortField === sortField);
  if (!control) return false;
  control.focus();
  return true;
}

export function syncSortDirectionButton(select, button) {
  const parts = select.value.split(":");
  const available = parts.length >= 2 && (parts.at(-1) === "asc" || parts.at(-1) === "desc");
  button.disabled = !available;
  if (!available) {
    button.setAttribute("aria-pressed", "false");
    button.setAttribute("aria-label", "Default ordering has no direction toggle");
    button.textContent = "↕";
    return false;
  }
  const descending = parts.at(-1) === "desc";
  button.setAttribute("aria-pressed", String(descending));
  button.setAttribute("aria-label", descending ? "Sorted descending" : "Sorted ascending");
  button.textContent = descending ? "↓" : "↑";
  return true;
}

export function toggleSortDirection(select, button) {
  const parts = select.value.split(":");
  if (parts.length < 2) return false;
  parts[parts.length - 1] = parts.at(-1) === "desc" ? "asc" : "desc";
  const next = parts.join(":");
  if (![...select.options].some(option => option.value === next)) return false;
  select.value = next;
  syncSortDirectionButton(select, button);
  return true;
}

export function dismissBoardFilterMenu(menu, target) {
  if (!menu?.open || !target) return false;
  const closeButton = target.closest?.("[data-close-board-filters]");
  if (!closeButton && menu.contains(target)) return false;

  menu.open = false;
  if (closeButton) menu.querySelector("summary")?.focus();
  return true;
}

const structuredBoardFilterNames = new Set([
  "claimKind", "agent", "priority", "claimState", "updatedWithin"
]);

export function syncBoardFilterIndicator(form, menu) {
  const active = [...(form?.querySelectorAll?.("[name]") || [])]
    .filter(control => structuredBoardFilterNames.has(control.name))
    .filter(control => control.matches?.("input[type=checkbox], input[type=radio]")
      ? control.checked
      : String(control.value || "").trim().length > 0)
    .length;
  menu?.classList?.toggle("has-active-filters", active > 0);
  const badge = menu?.querySelector?.("[data-board-filter-count]");
  if (badge) {
    badge.hidden = active === 0;
    badge.textContent = active === 0 ? "" : String(active);
  }
  const summary = menu?.querySelector?.("summary");
  summary?.setAttribute?.("aria-label", active === 0 ? "Filters" : `Filters, ${active} active`);
  return active;
}

export function clearBoardFilters(form) {
  if (!form?.elements || !form?.requestSubmit) return false;
  [...form.elements]
    .filter(control => structuredBoardFilterNames.has(control.name))
    .forEach(control => {
      if (control.matches?.("input[type=checkbox], input[type=radio]")) control.checked = false;
      else control.value = "";
    });
  form.requestSubmit();
  return true;
}

export function resetBoardView(form) {
  if (!form?.elements || !form?.requestSubmit) return false;
  [...form.elements].forEach(control => {
    if (control.id === "board-search") control.value = "";
    else if (control.name === "scope") control.value = "active";
    else if (control.name === "sort") control.value = "default";
    else if (control.name === "columnSort" || structuredBoardFilterNames.has(control.name)) {
      if (control.matches?.("input[type=checkbox], input[type=radio]")) control.checked = false;
      else control.value = "";
    }
  });
  form.requestSubmit();
  return true;
}

export function clearOperationsFilters(form) {
  if (!form?.querySelectorAll || !form?.requestSubmit) return false;
  form.querySelectorAll("[name]:not([name=sort])").forEach(control => {
    if (control.matches("input[type=checkbox], input[type=radio]")) control.checked = false;
    else control.value = "";
  });
  form.requestSubmit();
  return true;
}

export function applyOperationsSort(form, value) {
  const control = form?.querySelector?.("[name=sort]");
  if (!control || !value || !form?.requestSubmit) return false;
  control.value = value;
  form.requestSubmit();
  return true;
}
