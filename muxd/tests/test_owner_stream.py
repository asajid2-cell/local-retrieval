"""P2: the owner stream link (spec sections 6.1-6.5).

A teed PC tab registers with muxd as a STREAM owner: it sends raw VT as binary frames dispatched on the
first byte (6.1), muxd reports it as kind "local-tab" and never auto-heals it because there is no command
to relaunch (6.2), the registration claims no writer slot (6.3), remote input comes back as a binary 0x02
frame (6.4), and output is only forwarded while a relay viewer is watching (6.5).

These pin the muxd-side contract against a constructed OwnerSession and the pure helpers -- no live
socket, no PTY -- so a regression in the framing or the watch gate fails here.
"""
import asyncio
import base64
import json
import time
import unittest

import muxd


def make_stream_session(name="tab", loop=None, outq=None):
    # The 8th positional arg is owner_ws. A stream owner registers with cmd="" so session_has_command is
    # False and kind resolves to local-tab.
    outq = outq if outq is not None else muxd.RelayFanout()
    return muxd.OwnerSession(name, "", None, 120, 30, loop, outq, None,
                             owner_key="k" * 24, stream=True)


def make_regular_session(name="shell", loop=None, outq=None):
    return muxd.OwnerSession(name, "", None, 120, 30, loop, outq, None,
                             owner_key="k" * 24, stream=False)


class OwnerBinaryFrames(unittest.IsolatedAsyncioTestCase):
    """6.1: the three-byte-kind binary path into the ring."""

    async def test_ingest_appends_to_the_ring_and_parks_the_bytes(self):
        s = make_stream_session()
        await muxd.handle_owner_binary(s, bytes((muxd.OWNER_FRAME_INGEST,)) + b"hello")
        self.assertEqual(s.drain(), b"hello")
        self.assertEqual(b"".join(s._ring_snapshot()), b"hello")

    async def test_resync_replaces_the_ring_and_asks_the_relay_to_repaint(self):
        s = make_stream_session()
        await muxd.handle_owner_binary(s, bytes((muxd.OWNER_FRAME_INGEST,)) + b"first")
        await muxd.handle_owner_binary(s, bytes((muxd.OWNER_FRAME_RESYNC,)) + b"second")
        # replace_history clears the ring and ingests the snapshot; the pending buffer is dropped and the
        # relay is told to repaint, because a resync is delivered through the scrollback path, not `o`.
        self.assertEqual(b"".join(s._ring_snapshot()), b"second")
        self.assertIsNone(s.drain())
        frame = s.outq.get_nowait()
        self.assertEqual(frame[0], "resync")

    def test_the_kinds_are_the_three_the_ws_loop_routes(self):
        self.assertEqual(
            {muxd.OWNER_FRAME_INGEST, muxd.OWNER_FRAME_RESYNC, muxd.OWNER_FRAME_INPUT},
            {1, 3, 2},
        )


class OwnerStreamKind(unittest.TestCase):
    """6.2/6.5: kind, no auto-heal, and the tail omission for a bare tab."""

    def test_a_stream_owner_reports_local_tab_and_is_never_healed(self):
        s = make_stream_session()
        s.alive = lambda: True
        payload = muxd.session_payload("tab", s)
        self.assertEqual(payload["kind"], "local-tab")
        self.assertFalse(payload["heal"])

    def test_a_regular_owner_keeps_the_existing_ladder(self):
        s = make_regular_session()
        s.alive = lambda: True
        payload = muxd.session_payload("shell", s)
        self.assertNotEqual(payload["kind"], "local-tab")

    def test_local_tab_without_a_bound_agent_omits_the_tail(self):
        s = make_stream_session()
        s.alive = lambda: True
        s.tail_text = lambda *a, **k: "PS C:\\Users\\me> "
        payload = muxd.session_payload("tab", s)
        self.assertEqual(payload["kind"], "local-tab")
        self.assertEqual(payload["tail"], "", "a bare PC tab carries no useful tail")


class OwnerWatchGate(unittest.TestCase):
    """6.5: output is only forwarded while a relay viewer is watching."""

    def test_a_stream_owner_starts_unwatched_and_a_regular_owner_starts_watched(self):
        self.assertFalse(make_stream_session().watched)
        self.assertTrue(make_regular_session().watched)

    def test_set_watched_is_idempotent_and_tells_the_owner(self):
        s = make_stream_session()
        sent = []
        s._send_owner = lambda frame: sent.append(frame)
        s.set_watched(True)
        s.set_watched(True)          # a second viewer must not re-fire
        self.assertEqual(sent, [{"t": "watch"}])
        self.assertTrue(s.watched)
        s.set_watched(False)
        s.set_watched(False)
        self.assertEqual(sent, [{"t": "watch"}, {"t": "unwatch"}])

    def test_registration_record_carries_the_stream_flag(self):
        # A restored record must remember it is a stream owner, or a reconnect would heal it.
        records = muxd.session_records_payload({"tab": make_stream_session()})
        self.assertTrue(records["tab"]["stream"])


class OwnerNoLaunchClaim(unittest.TestCase):
    """6.3: a stream owner claims no writer slot.

    A teed PC tab registers with an empty command and no identity, so it must not reserve a launch
    claim. CLAIM_ROOT exists to stop two muxd-driven writers racing into one agent session; a bare tab
    has no muxd-side writer to race (muxtee is the tab's only child, and the tab is the only writer), and
    taking a claim would also block a later agent binding inside the same tab. This pins the property the
    stream branch rides on: an empty command yields no candidate ids, so acquire_launch_claim refuses to
    write a claim file. Drop that guard and the duplicate-writer path starts claiming bare tabs.
    """

    def test_empty_command_yields_no_claim_candidate(self):
        self.assertEqual(muxd.launch_candidate_ids("", []), [], "a bare tab has no identity to claim")

    def test_acquire_launch_claim_for_an_empty_command_takes_no_claim(self):
        claim, detail = muxd.acquire_launch_claim("", [])
        self.assertIsNone(claim, "an empty-command tab must not acquire a launch claim")
        self.assertEqual(detail, "")


class OwnerTabLifecycle(unittest.IsolatedAsyncioTestCase):
    """S2: a teed tab's row follows its socket, and one bad command never takes the relay link down.

    A WT tab closing leaves muxtee's socket gone. On the old path a kill of that row fell through to
    the PTY branch, called stop_input_writer on an OwnerSession (only Session defines it), and threw -
    out of the relay dispatch and off the whole link. These pin the three behaviours the fix rests on:
    a gone tab is dropped at once, a live tab is the user's to close, and a closed stream socket
    removes its row instead of parking it.
    """

    async def test_a_kill_of_a_tab_with_no_live_socket_removes_it_without_waiting(self):
        s = make_stream_session()
        s.dead = True          # the socket is gone...
        s.owner = False        # ...and reconcile_finalize cleared the owner claim
        s.lifecycle = "dormant"
        started = time.perf_counter()
        ok, detail = await muxd.terminate_session_off_loop(s, by_user=True)
        self.assertTrue(ok, detail)
        self.assertLess(time.perf_counter() - started, 1.0, "a gone tab must not be waited on")
        self.assertIn("mirror", detail)

    async def test_a_kill_of_a_live_stream_tab_is_refused(self):
        s = make_stream_session()   # dead defaults to False, so the tab reads as live
        ok, detail = await muxd.terminate_session_off_loop(s, by_user=True)
        self.assertFalse(ok)
        self.assertIn("local terminal owns", detail)

    async def test_a_closed_stream_socket_removes_the_row_instead_of_parking_it(self):
        s = make_stream_session()
        sessions = {"tab": s}
        saved = []

        async def save(source):
            saved.append(dict(source))

        await muxd.reconcile_owner_disconnect(sessions, "tab", s, save)
        self.assertNotIn("tab", sessions, "a closed stream socket must remove the row, not park it")
        self.assertTrue(saved, "the removal must be persisted")

    async def test_a_closed_regular_owner_socket_keeps_its_row_dormant(self):
        # A non-stream owner is muxrun/adopted: its row is a resumable mirror, so a dropped link parks
        # it dormant rather than deleting it. Only a stream tab is dropped outright.
        s = make_regular_session()
        sessions = {"shell": s}

        async def save(source):
            return None

        await muxd.reconcile_owner_disconnect(sessions, "shell", s, save)
        self.assertIn("shell", sessions)
        self.assertEqual(s.lifecycle, "dormant")


class RecordingWs:
    def __init__(self, owner):
        self.owner = owner
        self.frames = []

    async def send(self, frame):
        self.frames.append(frame)
        # The owner acknowledges a confirmed write by resolving the waiter for its rid, which is exactly
        # what the real muxtee does once it has written the bytes into the visible terminal.
        rid = json.loads(frame).get("rid")
        waiter = self.owner.input_waiters.get(rid)
        if waiter is not None and not waiter.done():
            waiter.set_result(True)


class OwnerBinaryInput(unittest.IsolatedAsyncioTestCase):
    """6.4: remote input reaches the owner as a confirmed write."""

    async def test_input_reaches_the_owner_as_a_confirmed_write(self):
        s = make_stream_session(loop=asyncio.get_running_loop())
        ws = RecordingWs(s)
        s.owner_ws = ws

        ok = await s.write_confirmed(b"ls\r")
        self.assertTrue(ok)
        sent = json.loads(ws.frames[-1])
        self.assertEqual(sent["t"], "i")
        self.assertEqual(base64.b64decode(sent["d"]), b"ls\r")


class HandshakeDies:
    """A socket that dies the instant muxd tries to acknowledge registration.

    The leak this stands in for: `coordinate_owner_registration` has already committed the row to the
    manifest, and then the `owner-ok` reply raises on the closed socket. On the old handler the reap
    only wrapped the read loop, so the row was left behind forever - a local tab no path could clear.
    """

    def __aiter__(self):
        return self

    async def __anext__(self):
        await asyncio.Future()      # no frames; the socket is gone before the loop is ever entered

    async def send(self, frame):
        if '"owner-ok"' in frame:
            raise ConnectionError("socket closed before the acknowledgement")


class OwnerConnectionReap(unittest.IsolatedAsyncioTestCase):
    """#20: a local-owned row is reaped on EVERY exit path, not just the read loop."""

    async def test_a_socket_that_dies_on_the_owner_ok_reply_still_reaps_the_row(self):
        sessions = {}
        outq = muxd.RelayFanout()
        saved = []

        async def save(source):
            saved.append(dict(source))

        async def register(first, ws):
            s = make_stream_session(name=first["s"], loop=asyncio.get_running_loop(), outq=outq)
            s.owner_ws = ws
            sessions[first["s"]] = s
            return s, ""

        with self.assertRaises(ConnectionError):
            await muxd.serve_owner_connection(
                HandshakeDies(), {"t": "owner", "s": "tab"}, sessions, outq, save, register
            )
        self.assertNotIn("tab", sessions, "a socket that dies before the read loop must still reap its row")
        self.assertTrue(saved, "the reap must be persisted, not just held in memory")

    async def test_owner_connected_is_true_only_while_the_socket_is_live(self):
        sessions = {}
        outq = muxd.RelayFanout()
        observed = {}

        async def save(source):
            return None

        async def register(first, ws):
            s = make_regular_session(name=first["s"], loop=asyncio.get_running_loop(), outq=outq)
            s.owner_ws = ws
            sessions[first["s"]] = s
            return s, ""

        class Probe:
            def __aiter__(self):
                return self

            async def __anext__(self):
                raise StopAsyncIteration

            async def send(self, frame):
                observed["connected_at_ack"] = sessions["shell"].owner_connected

        await muxd.serve_owner_connection(
            Probe(), {"t": "owner", "s": "shell"}, sessions, outq, save, register
        )
        self.assertTrue(observed["connected_at_ack"], "ownerConnected must be true once acknowledged")
        self.assertFalse(sessions["shell"].owner_connected, "and cleared once the connection ends")

    def test_owner_connected_is_reported_in_the_ls_row(self):
        s = make_stream_session()
        s.alive = lambda: True
        self.assertFalse(muxd.session_payload("tab", s)["ownerConnected"])
        s.owner_connected = True
        self.assertTrue(muxd.session_payload("tab", s)["ownerConnected"])


if __name__ == "__main__":
    unittest.main()
