'use strict';
/* FAIL CLOSED on the wrong pane.
 *
 * The page treats `?s=<name>` as a suggestion: an unknown name clears the URL and attaches to the first
 * session in the list. A probe that does not check therefore types into whatever session happens to sort
 * first - which is how a measurement run polluted a live agent tab on 2026-11-11. Every probe that can
 * send input or click the size chip must call this after attach and ABORT unless the loaded session is the
 * one it asked for.
 */
async function assertSession(page, want) {
  const got = await page.evaluate(() => {
    try { return { current: typeof current !== 'undefined' ? current : null, names: (window._sessions || []).map(s => s.name) }; }
    catch (e) { return { current: null, names: [], error: String(e) }; }
  });
  if (got.current !== want) {
    throw new Error(
      `REFUSING TO PROBE: asked for session ${JSON.stringify(want)} but the page attached to ${JSON.stringify(got.current)}. `
      + `Sessions present: ${JSON.stringify(got.names)}. `
      + `The page falls back to the first session when ?s= names nothing, so this probe would have typed into `
      + `a pane it does not own. Create the fixture session first (see memory mux-live-test-session-credential).`,
    );
  }
  if (!got.names.includes(want)) {
    throw new Error(`REFUSING TO PROBE: ${JSON.stringify(want)} is not in the session list ${JSON.stringify(got.names)}`);
  }
  return got;
}
module.exports = { assertSession };
