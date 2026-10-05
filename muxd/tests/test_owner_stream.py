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


if __name__ == "__main__":
    unittest.main()
