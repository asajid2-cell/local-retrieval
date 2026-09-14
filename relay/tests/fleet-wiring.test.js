const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "..", "public", "index.html"), "utf8");
const serverSource = fs.readFileSync(path.join(__dirname, "..", "server.js"), "utf8");

// The real fleet `state` vocabulary comes from relay/server.js, NOT from fleet.js: a browser row's state
// is attentionStatusForHosted()'s output. Parse it from the server so this suite fails if fleet.js ever
// drifts from the payload again. Every return path sets `state: '<name>'`, except when the function
// returns hostAgentStatus()'s result verbatim (the `provided` branch), whose state is a ternary mapping.
function readServerStates() {
  const states = new Set();
  for (const m of serverSource.matchAll(/\bstate:\s*'([a-z]+)'/g)) states.add(m[1]);
  const agentFn = serverSource.slice(
    serverSource.indexOf("function hostAgentStatus"),
    serverSource.indexOf("function attentionStatusForHosted"));
  for (const m of agentFn.matchAll(/\?\s*'([a-z]+)'/g)) states.add(m[1]);
  for (const m of agentFn.matchAll(/:\s*'([a-z]+)'\s*;/g)) states.add(m[1]);
  return states;
}

function indexOf(needle, from) {
  return source.indexOf(needle, from || 0);
}

function section(start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.notEqual(from, -1, `missing section start: ${start}`);
  assert.notEqual(to, -1, `missing section end: ${end}`);
  return source.slice(from, to);
}

test("MOBILE_VIEWS includes fleet", () => {
  assert.match(source, /const MOBILE_VIEWS = \[.*'fleet'.*\]/);
});

test("fleet.js script tag exists after intent-journal.js", () => {
  const intentIdx = indexOf('src="intent-journal.js"');
  const fleetIdx = indexOf('src="fleet.js"');
  assert.notEqual(intentIdx, -1, "intent-journal.js script not found");
  assert.notEqual(fleetIdx, -1, "fleet.js script not found");
  assert.ok(fleetIdx > intentIdx, "fleet.js must load after intent-journal.js");
});

test("fleet button exists in mobile nav with data-mobile-view=fleet", () => {
  const nav = section('<nav id="mobileNav"', "</nav>");
  assert.match(nav, /data-mobile-view="fleet"/);
  assert.match(nav, /aria-label="Fleet"/);
});

test("#fleetList div exists as child of #app", () => {
  const appIdx = source.indexOf('<div id="app">');
  const fleetIdx = source.indexOf('id="fleetList"');
  assert.ok(fleetIdx > appIdx, "fleetList must be after #app open");
  // It must appear before the copyview element which follows #app
  const copyviewIdx = source.indexOf('id="copyview"');
  assert.ok(fleetIdx < copyviewIdx, "fleetList must be before copyview (outside #app)");
  // Verify the fleetList is within the #app container by checking it appears after #app start
  // and before the status bar which is the last child before mobileNav
  const statusIdx = source.indexOf('id="status"', appIdx);
  assert.ok(fleetIdx < statusIdx, "fleetList must be before #status inside #app");
});

test("#fleetList has grid-area:term CSS", () => {
  assert.match(source, /#fleetList\s*\{[^}]*grid-area:\s*term/);
});

test("#fleetList defaults to display:none", () => {
  assert.match(source, /#fleetList\s*\{[^}]*display:\s*none/);
});

test("mobile-view-fleet CSS rule exists inside media query", () => {
  const mqStart = source.indexOf("@media (max-width:59.99rem)");
  const mqEnd = source.indexOf("@media (max-width:23rem)", mqStart);
  const mq = source.slice(mqStart, mqEnd);
  assert.match(mq, /#app\.mobile-view-fleet\s*\{/);
});

test("mobile-view-fleet shows #fleetList", () => {
  const mqStart = source.indexOf("@media (max-width:59.99rem)");
  const mqEnd = source.indexOf("@media (max-width:23rem)", mqStart);
  const mq = source.slice(mqStart, mqEnd);
  assert.match(mq, /#app\.mobile-view-fleet\s+#fleetList\s*\{\s*display:\s*flex/);
});

test("mobile-view-fleet hides term, keybar, and tabwrap", () => {
  const mqStart = source.indexOf("@media (max-width:59.99rem)");
  const mqEnd = source.indexOf("@media (max-width:23rem)", mqStart);
  const mq = source.slice(mqStart, mqEnd);
  assert.match(mq, /#app\.mobile-view-fleet\s+#term.*display:\s*none/);
  assert.match(mq, /#app\.mobile-view-fleet\s+#keybar.*display:\s*none/);
  assert.match(mq, /#app\.mobile-view-fleet\s+#tabwrap.*display:\s*none/);
});

test("nav grid uses repeat(5) to accommodate the fleet button", () => {
  assert.match(source, /grid-template-columns:\s*repeat\(5,\s*minmax\(0,\s*1fr\)\)/);
});

test("--info custom property exists in :root", () => {
  const root = section(":root {", "}");
  assert.match(root, /--info:\s*#60a5fa/);
});

test("fleet-state chip colour rules exist", () => {
  const css = section(":root {", "</style>");
  assert.match(css, /\.fleet-state-green\s+\.fleet-chip\s*\{[^}]*background:\s*var\(--live\)/);
  assert.match(css, /\.fleet-state-yellow\s+\.fleet-chip\s*\{[^}]*background:\s*var\(--warn\)/);
  assert.match(css, /\.fleet-state-red\s+\.fleet-chip\s*\{[^}]*background:\s*var\(--rose\)/);
  assert.match(css, /\.fleet-state-white\s+\.fleet-chip\s*\{[^}]*background:\s*var\(--txt\)/);
  assert.match(css, /\.fleet-state-detached\s+\.fleet-chip\s*\{[^}]*background:\s*var\(--info\)/);
  assert.match(css, /\.fleet-state-dormant\s+\.fleet-chip\s*\{[^}]*background:\s*var\(--muted\)/);
  assert.match(css, /\.fleet-state-unknown\s+\.fleet-chip\s*\{[^}]*background:\s*var\(--faint\)/);
  // The dead colour names must be gone, not merely unused.
  for (const dead of ["amber", "grey", "gray", "blue"]) {
    assert.equal(css.indexOf(".fleet-state-" + dead), -1, `stale .fleet-state-${dead} rule remains`);
  }
});

test("fleet-row CSS exists", () => {
  const css = section(":root {", "</style>");
  assert.match(css, /\.fleet-row\s*\{/);
  assert.match(css, /\.fleet-row\s*\{[^}]*min-height:\s*44px/);
});

test("no inline fleet-row markup in index.html", () => {
  assert.equal(source.indexOf('class="fleet-row'), -1, "index.html must not contain inline fleet-row class");
});

test("refreshFleet function exists and is guarded", () => {
  assert.match(source, /function refreshFleet\(\)\s*\{/);
  assert.match(source, /!window\.MuxFleet\s*\|\|\s*!\$\("#fleetList"\)/);
  assert.match(source, /\.catch\(\s*function\s*\(\s*\)\s*\{\s*\}\s*\)/);
});

test("initMobileViews wires MuxFleet.attachFleet", () => {
  const init = section("function initMobileViews", "  syncMobileView");
  assert.match(init, /MuxFleet\.attachFleet/);
  assert.match(init, /connect\(name,\s*false\)/);
});

test("setMobileView starts fleet polling on enter and stops on leave", () => {
  const smv = section("function setMobileView", "function syncMobileView");
  assert.match(smv, /view==='fleet'/);
  assert.match(smv, /setInterval/);
  assert.match(smv, /clearInterval\(fleetPollInterval\)/);
});

test("focus guard excludes fleet view", () => {
  const smv = section("function setMobileView", "function syncMobileView");
  assert.match(smv, /view!=='sessions'\s*&&\s*view!=='fleet'/);
  assert.match(smv, /term\.focus\(\)/);
});

// === BOOT + COUPLING PROOF ===
test("every fleet.js emitted class token has a CSS selector in index.html", () => {
  // Boot fleet.js in a bare vm context (same pattern as fleet-render.test.js)
  const fleetSource = fs.readFileSync(path.join(__dirname, "..", "public", "fleet.js"), "utf8");
  const stub = {};
  vm.runInNewContext(fleetSource, { globalThis: stub }, { filename: "fleet.js" });
  const MuxFleet = stub.MuxFleet;

  // Build fixture with at least two sessions, distinct names, different states, non-empty snippets
  const fixture = [
    { name: "codex-worker", state: "green", snippet: "build: SUCCESS\ntests: OK", autoheal: true, snippetSig: "abc123", snippetDegraded: false },
    { name: "shell-host", state: "yellow", snippet: "top -bn1\nload average: 2.3", autoheal: false, snippetSig: null, snippetDegraded: true },
  ];

  // Render and assert rowCount > 0
  const el = { innerHTML: "" };
  const rowCount = MuxFleet.renderFleet(el, { sessions: fixture });
  assert.ok(rowCount > 0, `expected rowCount > 0, got ${rowCount}`);

  // Every fixture name appears in rendered HTML
  for (const row of fixture) {
    assert.ok(el.innerHTML.indexOf(row.name) >= 0, `name "${row.name}" not found in rendered HTML`);
  }

  // Harvest every distinct class token that fleet.js emits
  const classTokens = new Set();

  // 1. From rendered fixture HTML
  const renderedClasses = [...el.innerHTML.matchAll(/class="([^"]+)"/g)];
  for (const m of renderedClasses) {
    for (const token of m[1].split(/\s+/)) {
      if (token.startsWith("fleet-")) classTokens.add(token);
    }
  }

  // 2. From empty-state: hostUp=false
  const el2 = { innerHTML: "" };
  MuxFleet.renderFleet(el2, { sessions: [], hostUp: false });
  const emptyOfflineClasses = [...el2.innerHTML.matchAll(/class="([^"]+)"/g)];
  for (const m of emptyOfflineClasses) {
    for (const token of m[1].split(/\s+/)) {
      if (token.startsWith("fleet-")) classTokens.add(token);
    }
  }

  // 3. From empty-state: hostUp=true
  const el3 = { innerHTML: "" };
  MuxFleet.renderFleet(el3, { sessions: [], hostUp: true });
  const emptyOnlineClasses = [...el3.innerHTML.matchAll(/class="([^"]+)"/g)];
  for (const m of emptyOnlineClasses) {
    for (const token of m[1].split(/\s+/)) {
      if (token.startsWith("fleet-")) classTokens.add(token);
    }
  }

  // 4. Every state the server can emit, plus one unrecognized token. The vocabulary is read from
  // relay/server.js (the payload's source of truth) rather than from fleet.js's own STATES list, so a
  // drifted fleet.js fails here instead of testing its drift against itself.
  const serverStates = readServerStates();
  for (const s of [...serverStates, "garbage"]) {
    const sc = MuxFleet.stateClass({ state: s });
    classTokens.add(sc);
  }
  assert.deepStrictEqual([...MuxFleet.STATES].sort(), [...serverStates].sort(),
    "fleet.js STATES drifted from relay/server.js attentionStatusForHosted()");

  // Now verify each token has a CSS selector in index.html
  const cssBlock = section(":root {", "</style>");
  const missing = [];
  for (const token of classTokens) {
    const selector = "." + token;
    if (cssBlock.indexOf(selector) < 0) {
      missing.push(token);
    }
  }

  assert.deepStrictEqual(missing, [], `CSS missing for fleet class tokens: ${missing.join(", ")}`);
  assert.ok(classTokens.size >= 15, `expected at least 15 fleet class tokens, got ${classTokens.size}: ${[...classTokens].sort().join(", ")}`);
});
