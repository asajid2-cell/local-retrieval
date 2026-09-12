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
        if (existing && /^[A-Za-z0-9._-]{1,128}$/.test(existing.intentId))
          return { storage, intentId: existing.intentId, signature: operation };
        if (journal.records.length >= MAX_UNRESOLVED)
          throw new Error('too many unresolved remote operations');
        const intentId = newIntent(prefix);
        journal.records.push({ intentId, signature: operation, createdAt: Date.now() });
        save(storage, journal);
        return { storage, intentId, signature: operation };
      } catch (error) {
        lastError = error;
      }
    }
    throw new Error('could not durably record remote operation intent: ' + (lastError && lastError.message || 'browser storage unavailable'));
  }

  function clear(acquired) {
    try {
      const journal = read(acquired.storage);
      journal.records = journal.records.filter(record =>
        record.intentId !== acquired.intentId || record.signature !== acquired.signature);
      save(acquired.storage, journal);
    } catch (error) {
      console.error('[intent-journal] could not clear completed intent', error);
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
    let lastError = null;
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        const response = await fetch(url, {
          method: verb,
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(body),
        });
        if (acquired && !retryableStatus(response.status)) clear(acquired);
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
})(globalThis);
