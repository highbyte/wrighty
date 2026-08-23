const settingsRegionId = "settings-content";

/**
 * Records the submitted settings form's viewport position before htmx replaces the full region.
 * The form index is a fallback for repeated forms that do not have an id, such as profile rows.
 */
export function captureSettingsScrollAnchor(source, target, viewport = window) {
  if (target?.id !== settingsRegionId || source === target) return null;

  const form = source?.closest?.("form");
  const anchor = form || target;
  const forms = form ? [...target.querySelectorAll("form")] : [];

  return {
    formId: form?.id || null,
    formIndex: form ? forms.indexOf(form) : -1,
    top: anchor.getBoundingClientRect().top,
    scrollY: viewport.scrollY
  };
}

/**
 * Keeps the same form at the same viewport position after the settings region is replaced.
 * Restoring the raw scroll offset is the fallback when a mutation removes the submitted form.
 */
export function restoreSettingsScrollAnchor(anchor, settings, doc = document, viewport = window) {
  if (!anchor || settings?.id !== settingsRegionId) return false;

  let element = anchor.formId ? doc.getElementById(anchor.formId) : null;
  if (!element && anchor.formIndex >= 0) {
    element = settings.querySelectorAll("form")[anchor.formIndex] ?? null;
  }

  if (!element) {
    viewport.scrollTo(0, anchor.scrollY);
    return true;
  }

  viewport.scrollBy(0, element.getBoundingClientRect().top - anchor.top);
  return true;
}
