# muxd input→echo latency — measured baseline

First real measurement of `scripts/latency_probe.py` against a **live muxd**. Everything before
this was driven by a loopback fake, so no number in this repo had ever touched a ConPTY.

- **Measured:** 2026-07-23
- **Path:** local muxd control socket, `ws://127.0.0.1:7699` — the same path `muxctl` attaches on
- **Method:** throwaway `latprobe-*` session running a pure echo child (console echo and line
  input disabled, `msvcrt.getwch()` → write back), one single-byte marker at a time, strictly
  sequential, cycled through a 36-byte alphabet so a stale echo can never be misread as the
  current sample. RTT is byte-handed-to-transport → that exact byte back.
- **Samples:** 200 per run, 5 consecutive runs

## Local series (DIAGNOSTIC ONLY)

| run | p50 | p95 | p99 | min | max | mean |
|-----|------|------|------|------|------|------|
| 1 | 15.66 | 31.10 | 31.68 | 13.41 | 78.70 | 17.47 |
| 2 | 15.79 | 35.94 | 55.59 | 11.15 | 74.43 | 19.83 |
| 3 | 15.58 | 30.74 | 34.88 |  2.90 | 58.05 | 17.70 |
| 4 | 15.73 | 31.42 | 38.42 | 13.42 | 48.84 | 18.34 |
| 5 | 15.57 | 30.34 | 33.94 | 10.31 | 95.31 | 17.29 |

All values in milliseconds. Headline: **p50 ≈ 15.7 ms, p95 ≈ 30–36 ms, p99 ≈ 32–56 ms.**

The p50 is the interesting number. It lands on 15.57–15.79 ms across five independent runs — the
Windows default timer tick is 15.625 ms. A round trip that reproducibly quantizes to the
scheduler tick is not paying for I/O; it is waiting on a timer. p95 sitting near 2× p50 and p99
near 2–3.5× p50 is the same signal: whole extra ticks, not a long tail of jitter. One run recorded
a 2.90 ms minimum, which shows the transport itself can turn a byte around in well under a tick —
so 15.7 ms is not a floor imposed by ConPTY or the websocket.

p99 is the least stable column (31.68 → 55.59 across runs) because it is 2 samples out of 200 on
a host that was concurrently running an orchestration campaign. Do not build a budget on p99 from
this page; p50 and p95 are the reproducible ones.

That is a hypothesis about where the time goes, not a diagnosis. Confirming it means
instrumenting muxd's pump, which this node does not do.

## Caveats

**Machine-dependent.** These came off one Windows 11 host running a live orchestration campaign
(other muxd sessions resident, other agents working). Treat them as this machine's shape, not a
portable constant. Re-measure before comparing against any other box.

**The signed/enforced path could NOT be measured on this branch.** `--signed` exits 4
(`EXIT_SIGNED_UNAVAILABLE`): `principal-auth.js` does not exist anywhere under the repo root, so
no signed channel can be opened and no signed output can be verified. The enforced path is the
shipping path and it is the authoritative one — which makes **every number on this page
DIAGNOSTIC ONLY**. They are the unsigned local transport, and the signed path can only be slower.
Re-run `--signed` once the authorized-principal workstream (r.2) merges.

## What this does not authorize

**muxd pump tuning remains DEFERRED.** No buffering, timer, or pump change was made in this node,
and none is authorized by this page. Tuning is gated on these numbers, and the gate is not
"someone measured something" — it is a signed-path p95 to enforce a budget against. Until r.2
lands, the enforced budget has no measurement behind it and any tuning would be aimed at a
diagnostic proxy.

## Reproducing

```
python scripts/latency_probe.py --local --samples 200 --timeout 180 --json-out <path>
python -m pytest muxd/tests/test_latency_probe_live.py -q
```

Exit codes: `0` in budget · `1` p95 over budget · `2` usage/probe error · `3` muxd unreachable ·
`4` signed path requested but unavailable · `5` `--timeout` wall clamp tripped.
