const panelSelector = "[data-hosted-worker-log-panel]";
const tailTolerance = 24;

function panelIn(root) {
  if (root?.matches?.(panelSelector)) return root;
  const ancestor = root?.closest?.(panelSelector);
  if (ancestor) return ancestor;
  return root?.querySelector?.(panelSelector) ?? null;
}

function panelsIn(root) {
  if (root?.matches?.(panelSelector)) return [root];
  const ancestor = root?.closest?.(panelSelector);
  if (ancestor) return [ancestor];
  return Array.from(root?.querySelectorAll?.(panelSelector) ?? []);
}

function listIn(panel) {
  return panel?.querySelector?.(".hosted-worker-log ol") ?? null;
}

export function captureHostedLogView(root) {
  const panel = panelIn(root);
  if (!panel) return null;
  const list = listIn(panel);
  const distanceFromTail = list
    ? list.scrollHeight - list.clientHeight - list.scrollTop
    : 0;
  return {
    runId: panel.dataset?.workerRunId ?? "",
    open: Boolean(panel.open),
    scrollTop: list?.scrollTop ?? 0,
    followTail: !list || distanceFromTail <= tailTolerance
  };
}

export function captureHostedLogViews(root) {
  return panelsIn(root).map(captureHostedLogView).filter(Boolean);
}

export function restoreHostedLogView(root, view) {
  if (!view) return false;
  const panel = panelIn(root);
  if (!panel || (panel.dataset?.workerRunId ?? "") !== view.runId) return false;
  if (view.open && !panel.open) panel.dataset.hostedLogRestored = "true";
  panel.open = view.open;
  const list = listIn(panel);
  if (view.open && list) {
    const maximum = Math.max(0, list.scrollHeight - list.clientHeight);
    list.scrollTop = view.followTail ? maximum : Math.min(view.scrollTop, maximum);
  }
  return true;
}

export function restoreHostedLogViews(root, views) {
  if (!views?.length) return 0;
  const panels = Array.from(root?.querySelectorAll?.(panelSelector) ?? []);
  let restored = 0;
  for (const view of views) {
    const panel = panels.find(value =>
      (value.dataset?.workerRunId ?? "") === view.runId);
    if (panel && restoreHostedLogView(panel, view)) restored++;
  }
  return restored;
}

export function consumeHostedLogRestore(panel) {
  if (panel?.dataset?.hostedLogRestored !== "true") return false;
  delete panel.dataset.hostedLogRestored;
  return true;
}

export function revealHostedLogTail(panel) {
  if (!panel?.open) return false;
  const list = listIn(panel);
  if (!list) return false;
  list.scrollTop = Math.max(0, list.scrollHeight - list.clientHeight);
  return true;
}
