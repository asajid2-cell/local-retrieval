// Minimal ntfy transport for the relay. Owned by the value prong (batch r.2 child 2).
// This leaf builds the pinned API so the ops-alerts leaf (r.1.17) can wire against it.
//
//   createNotifier({url, fetchImpl}) -> notifier.push({title, body, tags, priority, click}) -> {ok,...}
//
// push() never throws and never logs the URL (the topic IS the credential).

"use strict";

function createNotifier(options = {}) {
  const url = String(options.url || "").trim();
  if (!url) throw new TypeError("createNotifier requires a url");
  const fetchFn = typeof options.fetchImpl === "function" ? options.fetchImpl : fetch;

  async function push(message = {}) {
    if (!url) return { ok: false, error: "no ntfy URL configured" };
    if (!message || typeof message !== "object") return { ok: false, error: "push requires a message object" };
    try {
      const res = await fetchFn(url, {
        method: "POST",
        body: String(message.body || message.title || ""),
        headers: {
          "Title": String(message.title || ""),
          "Tags": String((message.tags || []).join(",")),
          "Priority": String(message.priority || "default"),
          "Click": String(message.click || ""),
        },
      });
      return { ok: res.ok, status: res.status };
    } catch (error) {
      return { ok: false, error: String((error && error.message) || error) };
    }
  }

  return { push };
}

module.exports = { createNotifier };
