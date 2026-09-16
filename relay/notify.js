'use strict';
// Shared ntfy push transport. Two independent event sources use it: session-attention episodes
// (value lane) and ops-health alerts (adversarial lane). It is pure transport on purpose — no
// dedupe, no episode state, no retry. Each event source already knows when it wants to notify;
// pushing that decision down here would let one source's flap suppress the other's real alert.
const http = require('http');
const https = require('https');
const { URL } = require('url');

const NOTIFY_TIMEOUT_MS = 5000;

// ntfy carries title/tags/priority/click as HTTP headers. undici throws synchronously on header
// values holding CR/LF or non-latin1 bytes, so a session named with an emoji would otherwise break
// the never-throws contract. Sanitize to printable ASCII; the message body stays full UTF-8.
function headerSafe(value) {
  return String(value)
    .replace(/[\r\n\t]+/g, ' ')
    .replace(/[^\x20-\x7E]/g, '')
    .trim()
    .slice(0, 512);
}

function buildHeaders(message) {
  const headers = { 'Content-Type': 'text/plain; charset=utf-8' };
  const title = headerSafe(message.title == null ? '' : message.title);
  if (title) headers.Title = title;
  const tags = Array.isArray(message.tags) ? message.tags : (message.tags == null ? [] : [message.tags]);
  const tagList = tags.map((t) => headerSafe(t)).filter(Boolean).join(',');
  if (tagList) headers.Tags = tagList;
  if (message.priority != null && headerSafe(message.priority)) headers.Priority = headerSafe(message.priority);
  const click = headerSafe(message.click == null ? '' : message.click);
  if (click) headers.Click = click;
  return headers;
}

// Fallback transport for runtimes without global fetch, and the path that keeps the 5s deadline
// honest end to end (a hung server that accepts the socket but never answers still aborts).
function nodeRequest(url, options, timeoutMs) {
  return new Promise((resolve, reject) => {
    let target;
    try { target = new URL(url); } catch (err) { reject(err); return; }
    const agent = target.protocol === 'https:' ? https : http;
    const req = agent.request(target, { method: options.method, headers: options.headers }, (res) => {
      res.resume();  // drain: we only care about the status line
      res.on('end', () => resolve({ status: res.statusCode, ok: res.statusCode >= 200 && res.statusCode < 300 }));
      res.on('error', reject);
    });
    req.setTimeout(timeoutMs, () => req.destroy(Object.assign(new Error('timeout'), { name: 'TimeoutError' })));
    req.on('error', reject);
    req.end(options.body);
  });
}

/**
 * createNotifier({url, fetchImpl, timeoutMs}) -> { push, enabled, url }
 * `url` defaults to process.env.MUX_NTFY_URL (a full ntfy topic URL). With no URL configured the
 * notifier is a no-op: push() resolves {ok:false, disabled:true} without touching the network. A
 * caller that must remain observable with no URL wants createJournalNotifier() below instead.
 * push({title, body, tags, priority, click}) resolves {ok, status} or {ok, error} and NEVER throws.
 */
function createNotifier(options) {
  const opts = options || {};
  const configured = opts.url === undefined ? process.env.MUX_NTFY_URL : opts.url;
  const url = typeof configured === 'string' ? configured.trim() : '';
  const timeoutMs = Number(opts.timeoutMs) > 0 ? Number(opts.timeoutMs) : NOTIFY_TIMEOUT_MS;
  const fetchImpl = typeof opts.fetchImpl === 'function' ? opts.fetchImpl : null;

  async function push(message) {
    const msg = message || {};
    if (!url) return { ok: false, disabled: true };
    const headers = buildHeaders(msg);
    const body = msg.body == null ? '' : String(msg.body);

    if (!fetchImpl) {
      try {
        const res = await nodeRequest(url, { method: 'POST', headers, body }, timeoutMs);
        return res.ok
          ? { ok: true, status: res.status }
          : { ok: false, status: res.status, error: 'ntfy responded ' + res.status };
      } catch (err) {
        return { ok: false, error: describe(err, timeoutMs) };
      }
    }

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    if (typeof timer.unref === 'function') timer.unref();
    try {
      const res = await fetchImpl(url, { method: 'POST', headers, body, signal: controller.signal });
      const status = res && typeof res.status === 'number' ? res.status : 0;
      const ok = res && typeof res.ok === 'boolean' ? res.ok : status >= 200 && status < 300;
      return ok
        ? { ok: true, status }
        : { ok: false, status, error: 'ntfy responded ' + status };
    } catch (err) {
      const timedOut = controller.signal.aborted;
      return { ok: false, error: timedOut ? 'ntfy push timed out after ' + timeoutMs + 'ms' : describe(err, timeoutMs) };
    } finally {
      clearTimeout(timer);
    }
  }

  return { push, enabled: Boolean(url), url };
}

// createJournalNotifier() -> { push, enabled, url } — the same interface with a different sink.
//
// Why this exists: createNotifier() with no URL is a NO-OP, and a no-op is indistinguishable from a
// quiet night. That is exactly how the ops-alert lane sat dead for weeks — no MUX_ALERT_NTFY_URL meant
// push() resolved {ok:false, disabled:true} and never touched the network, so "no operator was ever
// paged" and "nothing was ever wrong" produced identical output. A lane that can go dark must say so.
//
// So when no topic is configured the caller injects THIS instead: alerts still fire, still dedupe, and
// still print — into the journal, where they are readable with `journalctl -u multiplex-app`. `enabled`
// is false so the absence of a real push stays greppable, and push() reports ok:true because the write
// it was asked to do did happen.
function createJournalNotifier(options) {
  const opts = options || {};
  const log = typeof opts.log === 'function' ? opts.log : (line) => console.warn(line);
  const label = opts.label ? String(opts.label) : 'ops-alert';
  async function push(message) {
    const msg = message || {};
    const priority = headerSafe(msg.priority == null ? '' : msg.priority) || 'default';
    const head = '[' + label + '][journal] ' + priority + ' ' + headerSafe(msg.title == null ? '' : msg.title);
    const body = msg.body == null ? '' : String(msg.body);
    const lines = [head].concat(body ? body.split('\n').map((l) => '  ' + l) : []);
    const url = headerSafe(msg.click == null ? '' : msg.click);
    if (url) lines.push('  ' + url);
    try { log(lines.join('\n')); } catch { /* a broken logger must not break the never-throws contract */ }
    return { ok: true, journal: true };
  }
  return { push, enabled: false, url: '' };
}

function describe(err, timeoutMs) {
  if (err && (err.name === 'TimeoutError' || err.name === 'AbortError' || err.code === 'ETIMEDOUT'))
    return 'ntfy push timed out after ' + timeoutMs + 'ms';
  return String((err && err.message) || err || 'unknown ntfy push failure');
}

module.exports = { createNotifier, createJournalNotifier, NOTIFY_TIMEOUT_MS };
