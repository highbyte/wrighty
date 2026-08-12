export const launchTokenStorageKey = "wrighty.web.launch-token.v1";

export function buildLaunchUrl(token, browser = globalThis) {
  const url = new URL("/", browser.location.origin);
  if (token) url.hash = new URLSearchParams({ token }).toString();
  return url.href;
}

export function loadLaunchToken(browser = globalThis) {
  const fragment = new URLSearchParams(browser.location.hash.slice(1));
  const fragmentToken = fragment.get("token") || null;
  const storage = sessionStorage(browser);

  if (fragmentToken) {
    try {
      storage?.setItem(launchTokenStorageKey, fragmentToken);
    } catch {
      // Storage can be disabled by browser policy. Keep the captured token in memory.
    }

    // Strip only the fragment that carried the token. Any other fragment is page state (the
    // selected tab) and must survive the load.
    browser.history.replaceState(
      null,
      "",
      `${browser.location.pathname}${browser.location.search}`);
    return fragmentToken;
  }

  try {
    return storage?.getItem(launchTokenStorageKey) || null;
  } catch {
    return null;
  }
}

export function clearLaunchToken(browser = globalThis) {
  try {
    sessionStorage(browser)?.removeItem(launchTokenStorageKey);
  } catch {
    // Authentication failure still needs to be presented when storage is unavailable.
  }
}

function sessionStorage(browser) {
  try {
    return browser.sessionStorage;
  } catch {
    return null;
  }
}
