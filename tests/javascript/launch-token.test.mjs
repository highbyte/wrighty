import assert from "node:assert/strict";
import test from "node:test";

import {
  clearLaunchToken,
  launchTokenStorageKey,
  loadLaunchToken
} from "../../src/Highbyte.Wrighty.Web/Assets/launch-token.mjs";

function memoryStorage(token = null, calls = []) {
  const values = new Map();
  if (token) values.set(launchTokenStorageKey, token);
  return {
    getItem(key) {
      calls.push(`get:${key}`);
      return values.get(key) ?? null;
    },
    setItem(key, value) {
      calls.push(`set:${key}`);
      values.set(key, value);
    },
    removeItem(key) {
      calls.push(`remove:${key}`);
      values.delete(key);
    }
  };
}

function browser({
  hash = "",
  pathname = "/",
  search = "",
  storage = memoryStorage(),
  calls = []
} = {}) {
  return {
    location: { hash, pathname, search },
    history: {
      replaceState(state, title, url) {
        calls.push(`replace:${url}`);
        assert.equal(state, null);
        assert.equal(title, "");
      }
    },
    sessionStorage: storage,
    get localStorage() {
      assert.fail("localStorage must not be used for launch tokens");
    }
  };
}

test("fragment token is stored before the fragment is removed", () => {
  const calls = [];
  const storage = memoryStorage("older-token", calls);
  const target = browser({
    hash: "#token=fresh-token",
    pathname: "/dashboard",
    search: "?scope=active",
    storage,
    calls
  });

  assert.equal(loadLaunchToken(target), "fresh-token");
  assert.deepEqual(calls, [
    `set:${launchTokenStorageKey}`,
    "replace:/dashboard?scope=active"
  ]);
  assert.equal(storage.getItem(launchTokenStorageKey), "fresh-token");
});

test("refresh recovers the origin-scoped session token", () => {
  const firstOrigin = memoryStorage("first-origin-token");
  const secondOrigin = memoryStorage("second-origin-token");

  assert.equal(loadLaunchToken(browser({ storage: firstOrigin })), "first-origin-token");
  assert.equal(loadLaunchToken(browser({ storage: secondOrigin })), "second-origin-token");
});

test("an empty fragment token does not replace a stored token", () => {
  const storage = memoryStorage("stored-token");

  assert.equal(
    loadLaunchToken(browser({ hash: "#token=", storage })),
    "stored-token");
});

test("storage getter failure retains one-load fragment behavior", () => {
  const calls = [];
  const target = browser({ hash: "#token=one-load-token", calls });
  Object.defineProperty(target, "sessionStorage", {
    get() {
      throw new Error("storage blocked");
    }
  });

  assert.equal(loadLaunchToken(target), "one-load-token");
  assert.deepEqual(calls, ["replace:/"]);
});

test("storage operation failures do not break fragment capture or missing-token handling", () => {
  const throwingStorage = {
    setItem() {
      throw new Error("write blocked");
    },
    getItem() {
      throw new Error("read blocked");
    }
  };

  assert.equal(
    loadLaunchToken(browser({
      hash: "#token=one-load-token",
      storage: throwingStorage
    })),
    "one-load-token");
  assert.equal(
    loadLaunchToken(browser({ storage: throwingStorage })),
    null);
});

test("authentication failure clears the stored token and tolerates blocked storage", () => {
  const calls = [];
  const storage = memoryStorage("stale-token", calls);
  clearLaunchToken(browser({ storage }));

  assert.deepEqual(calls, [`remove:${launchTokenStorageKey}`]);
  assert.equal(storage.getItem(launchTokenStorageKey), null);

  assert.doesNotThrow(() => clearLaunchToken(browser({
    storage: {
      removeItem() {
        throw new Error("remove blocked");
      }
    }
  })));

  const blocked = browser();
  Object.defineProperty(blocked, "sessionStorage", {
    get() {
      throw new Error("storage blocked");
    }
  });
  assert.doesNotThrow(() => clearLaunchToken(blocked));
});
