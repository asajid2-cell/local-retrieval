// Ops alerts on health EDGE transitions.
//
// The relay already computes everything an operator needs (/api/health, server.js:1165) but nobody is
// watching it at 3am. This module turns that poll-only signal into a push — and only on the edges that
// actually mean something. Three rules keep it from becoming noise you learn to ignore:
//
//   1. EDGE ONLY. A condition alerts when it becomes true, not on every sample it stays true.
//   2. SUSTAIN. Slow conditions must hold for a dwell window first, so a 30s reconnect blip is silent.
//   3. DEDUPE. One push per condition per 30min. If it is still broken after that window, you get one
//      more nag — silence would read as "it fixed itself".
//
// Every cleared condition that alerted gets a recovery push, so the last message you see is the truth.
//
// Transport is NOT built here. A notifier is injected (relay/notify.js, owned by the value prong):
//   createNotifier({ url, fetchImpl }) -> notifier.push({ title, body, tags, priority, click }) -> { ok, ... }
// push() never throws. Injection is what lets the tests drive this with a mock and no network.
//
// The ntfy topic URL is a secret — the topic IS the credential. The caller (server.js) takes it from
// the environment and passes it in; on the box it lives in /etc/multiplex-app.env (read by the systemd
// unit's EnvironmentFile) as:
//
//   MUX_ALERT_NTFY_URL=https://ntfy.sh/<unguessable-topic>
//
// This key is not the attention lane's: that one is created bare (server.js) and falls back to
// notify.js's own default, MUX_NTFY_URL. Setting either one does nothing for the other lane.
//
// The key being absent must not switch this module off. It used to: server.js wired the lane only when
// the variable was set, so a missing key meant no alerts, no errors, and no log — which is byte-for-byte
// what a quiet, healthy fleet looks like. The lane therefore always starts, and the caller picks the
// sink: notify.js's createNotifier when the key is present, else its createJournalNotifier, which runs
// the identical conditions through the identical dedupe and prints the result to the journal. This
// module is unchanged by that choice — it only ever holds an already-constructed notifier.
//
// This module never reads env and never logs the URL. Nothing here is committed with a real topic in it.

'use strict';

const DEFAULTS = {
  dedupeMs: 30 * 60 * 1000,      // one push per condition per 30 minutes
  degradedSustainMs: 2 * 60 * 1000,   // healthy -> degraded must hold >2min
  hostDownSustainMs: 5 * 60 * 1000,   // host link down must hold >5min
};

function bool(value) {
  return value === true;
}

// Why the whole health blob is degraded, in the operator's words. Mirrors the disjunction at
// server.js:1175-1177 — if a term is added there, add it here so the push says which one tripped.
function degradedReasons(health) {
  const out = [];
  const host = health.host || {};
  const pc = health.pc || {};
  const projects = health.projects || {};
  const persistence = health.persistence || {};
  if (host.connected === false) out.push('host link down');
  if (host.protocolOk === false) out.push('host protocol mismatch');
  if (pc.reachable === false) out.push(`PC unreachable (${pc.host || 'unknown'})`);
  if (Number(health.legacySessions) > 0) out.push(`${health.legacySessions} legacy tmux session(s)`);
  if (projects.bridgeLive === false) out.push('projects bridge down');
  if (persistence.blocked) out.push(`persistence blocked: ${persistence.detail || 'unknown'}`);
  else if (persistence.ok === false) out.push(`persistence failing: ${persistence.detail || 'unknown'}`);
  if (Number(health.pendingRenameIntents) > 0) out.push(`${health.pendingRenameIntents} pending rename intent(s)`);
  const warnings = Array.isArray(health.uploadRecoveryWarnings) ? health.uploadRecoveryWarnings.length : 0;
  if (warnings > 0) out.push(`${warnings} upload recovery warning(s)`);
  if (Number(health.gaveUp) > 0) out.push(`${health.gaveUp} session(s) gave up healing`);
  if (Number(health.hostedArmedDown) > 0) out.push(`${health.hostedArmedDown} armed session(s) hosted-and-unreachable`);
  return out;
}

function minutes(ms) {
  return Math.round(ms / 60000);
}

// The watched conditions. `sustainMs: 0` means fire on the first sample that sees it — used for states
// that are already an operator escalation by the time they appear once.
const CONDITIONS = [
  {
    key: 'degraded',
    sustainKey: 'degradedSustainMs',
    detect: (health) => bool(health.degraded),
    title: () => 'Relay degraded',
    body: (health, heldMs) => {
      const why = degradedReasons(health);
      const head = `Health has been degraded for ${minutes(heldMs)}m.`;
      return why.length ? `${head}\n- ${why.join('\n- ')}` : `${head}\nNo specific reason reported.`;
    },
    recoveryTitle: () => 'Relay recovered',
    recoveryBody: (health, heldMs) => `Health is green again after ${minutes(heldMs)}m degraded.`,
    tags: ['warning'],
    priority: 'high',
  },
  {
    key: 'host-link-down',
    sustainKey: 'hostDownSustainMs',
    // Explicit false only: a missing/unknown host block is not evidence of a down link.
    detect: (health) => (health.host || {}).connected === false,
    title: () => 'Host link down',
    body: (health, heldMs) => {
      const host = health.host || {};
      return `muxd host link has been down for ${minutes(heldMs)}m`
        + `${host.name ? ` (${host.name})` : ''}. Sessions cannot be created, healed, or driven.`;
    },
    recoveryTitle: () => 'Host link restored',
    recoveryBody: (health, heldMs) => `muxd reconnected after ${minutes(heldMs)}m down.`,
    tags: ['electric_plug'],
    priority: 'high',
  },
  {
    key: 'persistence-blocked',
    sustainMs: 0,
    // Blocked persistence does not self-heal — it is latched until an operator clears it
    // (server.js:20/:37/:2034), so there is nothing to wait out. Alert on sight.
    detect: (health) => !!(health.persistence || {}).blocked,
    title: () => 'Persistence BLOCKED',
    body: (health) => {
      const detail = (health.persistence || {}).detail;
      return 'State writes are blocked pending operator recovery.'
        + `\n${detail ? String(detail) : 'No detail reported.'}`;
    },
    recoveryTitle: () => 'Persistence unblocked',
    recoveryBody: (health, heldMs) => `State writes are working again after ${minutes(heldMs)}m blocked.`,
    tags: ['rotating_light'],
    priority: 'urgent',
  },
  {
    key: 'heal-gave-up',
    sustainMs: 0,
    // TODO(muxd signal): WIRED BUT INERT. /api/health.gaveUp is a hardcoded 0 at server.js:1170 and
    // muxd does not report heal-give-up over the host link, so this condition can never trip today.
    // It is kept here deliberately: the moment muxd reports a real give-up count and server.js stops
    // stubbing the field, this alert starts firing with no change to this module. Do not delete it as
    // dead code — the health contract is what is missing, not the alert.
    detect: (health) => Number(health.gaveUp) > 0,
    title: () => 'Self-heal gave up',
    body: (health) => `${Number(health.gaveUp)} session(s) exhausted their heal attempts and will not come back on their own.`,
    recoveryTitle: () => 'Self-heal clear',
    recoveryBody: () => 'No sessions are in the gave-up state.',
    tags: ['skull'],
    priority: 'urgent',
  },
];

/**
 * @param {object} options
 * @param {{push: Function}} options.notifier  injected transport (relay/notify.js). Required.
 * @param {number} [options.dedupeMs]
 * @param {number} [options.degradedSustainMs]
 * @param {number} [options.hostDownSustainMs]
 * @param {string} [options.click]   URL the push opens (the relay dashboard).
 * @param {string} [options.label]   prefix for push titles, e.g. the host name.
 */
function createHealthAlerts(options = {}) {
  const notifier = options.notifier;
  if (!notifier || typeof notifier.push !== 'function') {
    throw new TypeError('createHealthAlerts requires a notifier with a push(message) method');
  }
  const config = {
    dedupeMs: Number(options.dedupeMs) >= 0 ? Number(options.dedupeMs) : DEFAULTS.dedupeMs,
    degradedSustainMs: Number(options.degradedSustainMs) >= 0 ? Number(options.degradedSustainMs) : DEFAULTS.degradedSustainMs,
    hostDownSustainMs: Number(options.hostDownSustainMs) >= 0 ? Number(options.hostDownSustainMs) : DEFAULTS.hostDownSustainMs,
  };
  const click = options.click || undefined;
  const label = options.label ? String(options.label) : '';

  // activeSince:   when this episode of the condition began (null = clear).
  // alertedAt:     when we last pushed for THIS episode (null = never pushed, so no recovery is owed).
  // episodeAlerts: pushes within THIS episode — resets on clear, so the first alert of a new episode
  //                reads as a fresh edge rather than a nag. count is the lifetime total.
  const state = new Map(CONDITIONS.map(c => [c.key, {
    activeSince: null, alertedAt: null, count: 0, episodeAlerts: 0,
  }]));

  function sustainFor(condition) {
    if (condition.sustainKey) return config[condition.sustainKey];
    return Number(condition.sustainMs) || 0;
  }

  function title(text) {
    return label ? `[${label}] ${text}` : text;
  }

  async function send(message) {
    // push() is contracted never to throw; belt-and-braces so a broken transport can never take the
    // relay's health loop down with it.
    try {
      const result = await notifier.push(click ? { ...message, click } : message);
      return result || { ok: false };
    } catch (error) {
      return { ok: false, error: String((error && error.message) || error) };
    }
  }

  /**
   * Feed one health sample. Returns the alerts pushed for THIS sample (usually empty).
   * `now` is explicit so callers — and tests — own the clock.
   */
  async function observe(health, now = Date.now()) {
    const sample = health && typeof health === 'object' ? health : {};
    const fired = [];

    for (const condition of CONDITIONS) {
      const entry = state.get(condition.key);
      let on = false;
      try {
        on = !!condition.detect(sample);
      } catch {
        on = false; // a malformed health blob must not wedge the watcher
      }

      if (on) {
        if (entry.activeSince === null) entry.activeSince = now;
        const heldMs = now - entry.activeSince;
        if (heldMs < sustainFor(condition)) continue;               // rule 2: not sustained yet
        if (entry.alertedAt !== null && now - entry.alertedAt < config.dedupeMs) continue; // rule 3
        const message = {
          title: title(condition.title(sample)),
          body: condition.body(sample, heldMs),
          tags: condition.tags,
          priority: condition.priority,
        };
        entry.alertedAt = now;
        entry.count += 1;
        entry.episodeAlerts += 1;
        const result = await send(message);
        fired.push({ key: condition.key, kind: 'alert', repeat: entry.episodeAlerts > 1, heldMs, message, result });
        continue;
      }

      // Cleared. Only owed a recovery push if this episode actually alerted — a transient blip that
      // never crossed its sustain window is a non-event and stays silent in both directions.
      const owed = entry.alertedAt !== null;
      const heldMs = entry.activeSince === null ? 0 : now - entry.activeSince;
      entry.activeSince = null;
      entry.alertedAt = null;
      entry.episodeAlerts = 0;
      if (!owed) continue;
      const message = {
        title: title(condition.recoveryTitle(sample)),
        body: condition.recoveryBody(sample, heldMs),
        tags: ['white_check_mark'],
        priority: 'default',
      };
      const result = await send(message);
      fired.push({ key: condition.key, kind: 'recovery', heldMs, message, result });
    }

    return fired;
  }

  function snapshot() {
    const out = {};
    for (const [key, entry] of state) out[key] = { ...entry };
    return out;
  }

  return { observe, snapshot, config, conditions: CONDITIONS.map(c => c.key) };
}

/**
 * Optional wiring seam for server.js: poll a health producer on an interval.
 * The timer is unref'd so it can never hold the process open (tests, CLI runs).
 *
 *   const { createNotifier, createJournalNotifier } = require('./notify');
 *   const { startHealthAlerts } = require('./health-alerts');
 *   // Always start. The secret only selects the sink, so an unset key is a logged lane, not a dead one.
 *   const notifier = process.env.MUX_ALERT_NTFY_URL  // secret from /etc/multiplex-app.env
 *     ? createNotifier({ url: process.env.MUX_ALERT_NTFY_URL })
 *     : createJournalNotifier({ label: 'ops-alert' });
 *   startHealthAlerts({ notifier, getHealth: () => healthSnapshot() });
 */
function startHealthAlerts(options = {}) {
  const getHealth = options.getHealth;
  if (typeof getHealth !== 'function') throw new TypeError('startHealthAlerts requires getHealth()');
  const intervalMs = Number(options.intervalMs) > 0 ? Number(options.intervalMs) : 30000;
  const alerts = createHealthAlerts(options);
  let busy = false;
  const tick = async () => {
    if (busy) return;         // a slow push must not stack up overlapping observations
    busy = true;
    try {
      await alerts.observe(await getHealth());
    } catch {
      // never let the watcher kill the relay
    } finally {
      busy = false;
    }
  };
  const timer = setInterval(tick, intervalMs);
  if (typeof timer.unref === 'function') timer.unref();
  return { alerts, stop: () => clearInterval(timer), tick };
}

module.exports = { createHealthAlerts, startHealthAlerts, degradedReasons, DEFAULTS };
