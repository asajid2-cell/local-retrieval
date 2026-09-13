(function installMuxIntentJournal(global) {
  'use strict';

  const JOURNAL_KEY = 'mux.intent-journal.v1';
  const MAX_UNRESOLVED = 256;

  function canonical(value) {
    if (Array.isArray(value)) return value.map(canonical);
    if (value && typeof value === 'object') {
      const result = {};
      for (const key of Object.keys(value).sort()) result[key] = canonical(value[key]);
      return result;
    }
    return value;
  }

  function signature(method, url, payload) {
    return String(method) + ' ' + String(url) + '\n' + JSON.stringify(canonical(payload || {}));
  }

  function newIntent(prefix) {
    const suffix = global.crypto && global.crypto.randomUUID
      ? global.crypto.randomUUID().replaceAll('-', '')
      : Date.now().toString(36) + Math.random().toString(36).slice(2);
    return String(prefix || 'web') + '-' + suffix;
  }

  function stores() {
    return [global.localStorage, global.sessionStorage].filter(Boolean);
  }

  function read(storage) {
    const parsed = JSON.parse(storage.getItem(JOURNAL_KEY) || '{"records":[]}');
    if (!parsed || !Array.isArray(parsed.records)) throw new Error('invalid intent journal');
    return parsed;
  }

  function save(storage, journal) {
    storage.setItem(JOURNAL_KEY, JSON.stringify(journal));
  }

  function acquire(method, url, payload, prefix) {
    const operation = signature(method, url, payload);
    let lastError = null;
    for (const storage of stores()) {
      try {
        const journal = read(storage);
        const existing = journal.records.find(record => record.signature === operation);
        if (existing && /^[A-Za-z0-9._-]{1,128}$/.test(existing.intentId)) {
          return { storage, intentId: existing.intentId, signature: operation, url: existing.url || url, existing: true };
        }
        if (journal.records.length >= MAX_UNRESOLVED)
          throw new Error('too many unresolved remote operations');
        const intentId = newIntent(prefix);
        journal.records.push({ intentId, signature: operation, url: String(url || ''), createdAt: Date.now() });
        save(storage, journal);
        return { storage, intentId, signature: operation, url: String(url || '') };
      } catch (error) {
        lastError = error;
      }
    }
    throw new Error('could not durably record remote operation intent: ' + (lastError && lastError.message || 'browser storage unavailable'));
  }

  function clear(acquired) {
    if (!acquired) return;
    try {
      const journal = read(acquired.storage);
      journal.records = journal.records.filter(record =>
        record.intentId !== acquired.intentId || record.signature !== acquired.signature);
      save(acquired.storage, journal);
    } catch (error) {
      console.error('[intent-journal] could not clear completed intent', error);
    }
  }

  function recordFor(intentId) {
    const id = String(intentId || '');
    for (const storage of stores()) {
      try {
        const record = read(storage).records.find(item => item && item.intentId === id);
        if (record) return { storage, record };
      } catch (error) { /* another store may still be usable */ }
    }
    return null;
  }

  function records() {
    const seen = new Set();
    const result = [];
    for (const storage of stores()) {
      try {
        for (const record of read(storage).records) {
          if (!record || !record.intentId || seen.has(record.intentId)) continue;
          seen.add(record.intentId);
          result.push(record);
        }
      } catch (error) { /* another store may still be usable */ }
    }
    return result;
  }

  function update(acquired, patch) {
    if (!acquired) return;
    try {
      const journal = read(acquired.storage);
      const index = journal.records.findIndex(record =>
        record.intentId === acquired.intentId && record.signature === acquired.signature);
      if (index < 0) return;
      journal.records[index] = { ...journal.records[index], ...patch };
      save(acquired.storage, journal);
    } catch (error) {
      console.error('[intent-journal] could not update accepted intent', error);
    }
  }

  // A terminal status releases the journal entry — but an explicitly uncertain outcome is NOT a
  // settled one: the browser must keep it so a later explicit retry can reconcile the same command.
  function completeIntent(intentId, status, uncertain) {
    if ((status !== 'done' && status !== 'failed') || uncertain === true) return;
    const found = recordFor(intentId);
    if (found) clear({ storage: found.storage, intentId: found.record.intentId, signature: found.record.signature });
  }

  function queuedCommandUrl(intentId, base) {
    const id = String(intentId || '');
    if (!/^[A-Za-z0-9._-]{1,128}$/.test(id)) return '';
    const found = recordFor(id);
    const fingerprint = found && String(found.record.fingerprint || '');
    const query = /^[a-f0-9]{64}$/.test(fingerprint)
      ? '?fingerprint=' + encodeURIComponent(fingerprint) : '';
    return String(base || '') + '/api/app-commands/by-intent/' + encodeURIComponent(id) + query;
  }

  async function lookupIntent(intentId, base, fetchFn) {
    const url = queuedCommandUrl(intentId, base);
    if (!url) return null;
    const response = await (fetchFn || global.fetch)(url, { headers: { Accept: 'application/json' } });
    if (!response || !response.ok) return null;
    const result = await response.json();
    const found = recordFor(intentId);
    if (found && found.record.fingerprint && result
        && result.fingerprint !== found.record.fingerprint)
      throw new Error('intent fingerprint mismatch');
    if (found && result && /^[a-f0-9]{64}$/.test(String(result.fingerprint || ''))
        && (!found.record.fingerprint || found.record.fingerprint === result.fingerprint)) {
      update({ storage: found.storage, intentId: found.record.intentId, signature: found.record.signature }, {
        commandId: String(result.id || ''), fingerprint: String(result.fingerprint),
      });
    }
    if (result && (result.status === 'done' || result.status === 'failed'))
      completeIntent(intentId, result.status, result.uncertain);
    return result;
  }

  async function pollIntent(intentId, options) {
    const opts = options || {};
    const timeoutMs = Math.max(0, Number(opts.timeoutMs) || 20000);
    const intervalMs = Math.max(1, Number(opts.intervalMs) || 1200);
    const now = typeof opts.now === 'function' ? opts.now : Date.now;
    const sleep = typeof opts.sleep === 'function'
      ? opts.sleep : ms => new Promise(resolve => setTimeout(resolve, ms));
    const deadline = now() + timeoutMs;
    const attempts = Math.max(1, Math.ceil(timeoutMs / intervalMs) + 1);
    let latest = { intentId: String(intentId || ''), status: 'pending', detail: '' };
    for (let attempt = 0; attempt < attempts; attempt++) {
      try {
        const result = await lookupIntent(intentId, opts.base || '', opts.fetch);
        if (result) latest = result;
        if (result && (result.status === 'done' || result.status === 'failed')) return result;
      } catch (error) {
        if (String((error && error.message) || error) === 'intent fingerprint mismatch') throw error;
      }
      const remaining = deadline - now();
      if (remaining <= 0 || attempt === attempts - 1) break;
      await sleep(Math.min(intervalMs, remaining));
    }
    return { ...latest, status: 'pending', timedOut: true };
  }

  async function recoverPending(options) {
    const opts = options || {};
    const result = [];
    for (const record of records()) {
      if (!/^[A-Za-z0-9._-]{1,128}$/.test(String(record.intentId || ''))) continue;
      try {
        const status = await lookupIntent(record.intentId, opts.base || '', opts.fetch);
        if (status) result.push(status);
      } catch (error) { /* transient recovery miss remains journaled */ }
    }
    return result;
  }

  async function acceptedCommand(response, acquired) {
    if (!acquired || !response) return;
    const capture = body => {
      if (!body || !body.intentId || !body.id || !/^[a-f0-9]{64}$/.test(String(body.fingerprint || ''))) return body;
      update(acquired, { commandId: String(body.id), fingerprint: String(body.fingerprint) });
      if (body.status === 'done' || body.status === 'failed')
        completeIntent(acquired.intentId, body.status, body.uncertain);
      return body;
    };
    if (typeof response.clone === 'function') {
      try { capture(await response.clone().json()); } catch (error) { /* caller still receives body */ }
      return;
    }
    if (typeof response.json === 'function' && !response.json.__muxIntentCapture) {
      const original = response.json.bind(response);
      const wrapped = async () => capture(await original());
      wrapped.__muxIntentCapture = true;
      response.json = wrapped;
    }
  }

  function retryableStatus(status) {
    return status === 408 || status === 425 || status === 429 || status >= 500;
  }

  // Every mutating browser request goes through here, whatever its verb: DELETE/PATCH are exactly as
  // replay-prone as POST once a phone drops the response, and the relay dedupes them all on intentId.
  async function sendIntent(method, url, payload, prefix) {
    const verb = String(method || 'POST').toUpperCase();
    const explicitIntent = payload && payload.intentId;
    if (explicitIntent !== undefined && !/^[A-Za-z0-9._-]{1,128}$/.test(explicitIntent))
      throw new Error('invalid explicit operation intent');
    const acquired = explicitIntent ? null : acquire(verb, url, payload, prefix);
    const body = { ...(payload || {}), intentId: explicitIntent || acquired.intentId };
    const appCommand = /(?:^|\/)api\/app-commands\/?(?:\?|$)/.test(String(url || ''));

    // A startchat retry that finds a prior explicitly-uncertain outcome must reconcile that SAME
    // command rather than enqueue a second one; only an owner-authenticated by-intent lookup can
    // prove that, so this runs before the retry loop.
    if (acquired && acquired.existing && payload && payload.type === 'startchat') {
      const base = String(url).replace(/\/api\/app-commands\/?(?:\?.*)?$/, '');
      const prior = await lookupIntent(acquired.intentId, base);
      if (prior && prior.status === 'failed' && prior.uncertain === true) {
        const reconcile = await global.fetch(base + '/api/app-commands/' + encodeURIComponent(prior.id) + '/reconcile', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ fingerprint: prior.fingerprint }),
        });
        if (!reconcile.ok) return reconcile;
        return { ok: true, status: 200, json: async () => ({
          id: prior.id, intentId: acquired.intentId, fingerprint: prior.fingerprint,
          status: 'pending', reconciled: true,
        }) };
      }
      if (prior) return { ok: true, status: 200, json: async () => prior };
    }

    let lastError = null;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const response = await global.fetch(url, {
          method: verb,
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
        if (response.status >= 200 && response.status < 300) await acceptedCommand(response, acquired);
        if (!retryableStatus(response.status) && !appCommand) clear(acquired);
        // An enqueue error is not proof that a previously accepted intent never ran.
        // Only an authoritative terminal lookup may release an app-command journal entry.
        if (appCommand && acquired && response.status >= 400 && !retryableStatus(response.status)) {
          const base = String(url).replace(/\/api\/app-commands\/?(?:\?.*)?$/, '');
          await lookupIntent(acquired.intentId, base).catch(() => null);
        }
        if (!retryableStatus(response.status) || attempt === 2) return response;
      } catch (error) {
        lastError = error;
        if (attempt === 2) throw error;
      }
      await new Promise(resolve => setTimeout(resolve, attempt === 0 ? 250 : 750));
    }
    throw lastError || new Error('remote operation failed');
  }

  global.sendIntent = sendIntent;
  global.postIntent = (url, payload, prefix) => sendIntent('POST', url, payload, prefix);
  global.completeIntent = completeIntent;
  global.intentRecords = records;
  global.recoverPendingIntents = recoverPending;
  global.intentCommandUrl = queuedCommandUrl;
  global.lookupIntent = lookupIntent;
  global.pollIntent = pollIntent;
})(globalThis);
