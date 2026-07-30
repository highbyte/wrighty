/**
 * The htmx-driven regions of the page, in the order they are readied.
 *
 * Not every page variant renders all of them: the GitHub backend's limited view has no item
 * board, and a variant that omits any of these must still bring up the rest.
 */
export const readyRegionSelectors = [
  "#board-content",
  "#provider-capacity-region",
  "#operations-content"
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
