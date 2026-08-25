/**
 * The htmx-driven regions of the page, in the order they are readied.
 *
 * Not every page variant renders all of them: the GitHub backend's limited view has no item
 * board, and a variant that omits any of these must still bring up the rest.
 */
export const readyRegionSelectors = [
  "#board-content",
  "#worker-summary-region",
  "#agents-region",
  "#operations-content",
  "#settings-content"
];

/**
 * Processes and readies every region the page actually rendered, and returns them.
 *
 * Each region is independent on purpose. Readying them as one fixed sequence meant an absent
 * region ended initialization for every region after it — htmx.process throws on a null element,
 * so a page without the board never readied its operations panel, which then sat at its loading
 * placeholder with its request never sent. Anything missing here is a page variant, not an error.
 */
export function readyPageRegions(doc, htmx) {
  const regions = readyRegionSelectors
    .map((selector) => doc.querySelector(selector))
    .filter((region) => region !== null && region !== undefined);
  for (const region of regions) {
    htmx?.process(region);
    region.dispatchEvent(new CustomEvent("wrighty:ready"));
  }
  return regions;
}

/**
 * Polls Operations only while its page tab and the browser page are visible.
 *
 * Operations can perform materially more work than the board, especially for GitHub-backed
 * trackers, so the dashboard timer must not refresh it in the background from another tab.
 */
export function refreshVisibleOperations(doc) {
  const operations = doc.querySelector("#operations-content");
  const panel = operations?.closest?.('[role="tabpanel"]');
  const requestInFlight = operations?.matches?.(".htmx-request") ||
    operations?.querySelector?.(".htmx-request");
  if (!operations || doc.visibilityState !== "visible" || panel?.hidden ||
      doc.querySelector("dialog[open]") || requestInFlight) return false;

  operations.dispatchEvent(new CustomEvent("wrighty:operations-refresh"));
  return true;
}

/**
 * Polls the Agents inventory while capacity probes run, but not while a short mutation is replacing
 * the same controls. Probe requests intentionally remain visible through read-only polling so the
 * operator sees each acquired probe and its warning state before the vendor call completes.
 */
export function refreshAgentsInventory(doc) {
  const agents = doc.querySelector("#agents-region");
  const blockingRequest = agents?.matches?.(".htmx-request") ||
    agents?.querySelector?.(".htmx-request:not([data-agent-probe-request])");
  if (!agents || doc.visibilityState !== "visible" || blockingRequest) return false;

  agents.dispatchEvent(new CustomEvent("wrighty:refresh"));
  return true;
}

/**
 * Reveals the worker controls without leaving focus inside the polled Operations fragment.
 *
 * Operations is replaced on every poll. Focusing #worker-processes made htmx restore that focus
 * after each replacement, which also pulled the viewport back after the user had scrolled away.
 * The page tab is stable across swaps and remains the appropriate keyboard focus destination.
 */
export function revealWorkerProcesses(doc) {
  doc.querySelector("#tab-operations")?.focus?.({ preventScroll: true });
  const workerProcesses = doc.querySelector("#worker-processes");
  if (!workerProcesses) return false;
  workerProcesses.scrollIntoView?.({ block: "start", behavior: "auto" });
  return true;
}
