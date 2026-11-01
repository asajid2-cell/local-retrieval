"""The relay's watch intent must outlive the owner link it was stated against.

A stream owner's row is POPPED the moment its socket drops (reconcile_owner_disconnect), so a muxtee
reconnect is a brand-new OwnerSession that starts unwatched (section 6.5). The relay only states its
interest on a transition - relay/server.js syncWatch early-returns when its own watchedHost flag already
matches - so nothing ever re-sends `watch` afterwards. The tab reconnects, the relay still believes it
is watched, and the screen never updates again.

That was the second half of the "local tab is frozen on the site" failure: the first half was the
oversized snapshot closing the link 1009 in the first place. These pin the re-arm that closes the loop.
"""
import asyncio
import unittest

import muxd


class RememberDesiredWatch(unittest.TestCase):
    def setUp(self):
        muxd.DESIRED_WATCH.clear()

    def tearDown(self):
        muxd.DESIRED_WATCH.clear()

    def test_a_statement_is_recorded_even_with_no_session_of_that_name(self):
        # The attach can land in the window where the row is gone; dropping the statement there is what
        # left a rebuilt owner permanently unwatched.
        muxd.remember_desired_watch("tab-1234", True)
        self.assertEqual(muxd.DESIRED_WATCH["tab-1234"][0], True)

    def test_a_later_statement_replaces_the_earlier_one(self):
        muxd.remember_desired_watch("tab-1234", True)
        muxd.remember_desired_watch("tab-1234", False)
        self.assertEqual(muxd.DESIRED_WATCH["tab-1234"][0], False)

    def test_pruning_only_drops_names_with_no_session_and_an_old_statement(self):
        muxd.DESIRED_WATCH["gone"] = (True, 0.0)              # no session, ancient
        muxd.remember_desired_watch("tab-1234", True)
        muxd.DESIRED_WATCH_MAX = 1
        try:
            muxd.remember_desired_watch("tab-5678", True)
        finally:
            muxd.DESIRED_WATCH_MAX = 512
        self.assertNotIn("gone", muxd.DESIRED_WATCH)
        self.assertIn("tab-1234", muxd.DESIRED_WATCH)
        self.assertIn("tab-5678", muxd.DESIRED_WATCH)


class ReconnectRearms(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        muxd.DESIRED_WATCH.clear()

    def tearDown(self):
        muxd.DESIRED_WATCH.clear()

    async def _register_and_close(self, granted):
        sessions = {}
        outq = muxd.RelayFanout()
        owner_sent = []

        async def save(source):
            return None

        async def register(first, ws):
            s = muxd.OwnerSession(first["s"], "", None, 120, 30, asyncio.get_running_loop(), outq,
                                  ws, owner_key="k" * 24, stream=True)
            s._send_owner = lambda frame: owner_sent.append(frame)
            sessions[first["s"]] = s
            return s, ""

        class Quiet:
            def __aiter__(self):
                return self

            async def __anext__(self):
                raise StopAsyncIteration

            async def send(self, frame):
                return None

        await muxd.serve_owner_connection(
            Quiet(), {"t": "owner", "s": "tab"}, sessions, outq, save, register
        )
        return owner_sent

    async def test_a_reconnecting_stream_owner_is_rewatched_from_the_last_statement(self):
        muxd.DESIRED_WATCH["tab"] = (True, __import__("time").time())
        # The handshake happens inside serve_owner_connection, and the reap later pops the row, so probe
        # what the owner was told during the connection instead of looking at the emptied dict.
        sent = []
        sessions = {}
        outq = muxd.RelayFanout()

        async def save(source):
            return None

        async def register(first, ws):
            s = muxd.OwnerSession(first["s"], "", None, 120, 30, asyncio.get_running_loop(), outq,
                                  ws, owner_key="k" * 24, stream=True)
            s._send_owner = lambda frame: sent.append(frame)
            sessions[first["s"]] = s
            return s, ""

        class Quiet:
            def __aiter__(self):
                return self

            async def __anext__(self):
                raise StopAsyncIteration

            async def send(self, frame):
                return None

        await muxd.serve_owner_connection(
            Quiet(), {"t": "owner", "s": "tab"}, sessions, outq, save, register
        )
        self.assertIn({"t": "watch"}, sent, "a re-registered owner the relay still wants must be re-armed")

    async def test_nothing_was_stated_so_nothing_is_sent(self):
        sent = await self._register_and_close(None)
        self.assertNotIn({"t": "watch"}, sent)

    async def test_an_explicit_unwatch_is_not_reversed_by_a_reconnect(self):
        muxd.DESIRED_WATCH["tab"] = (False, __import__("time").time())
        sent = await self._register_and_close(False)
        self.assertNotIn({"t": "watch"}, sent)


class LocalSocketMessageBudget(unittest.TestCase):
    """One frame carries a whole owner snapshot, so the server limit must clear muxtee's ring.

    websockets defaults max_size to 1 MiB. muxtee's ring is 2 MiB (TeeEngine.cs) and it re-sends the
    whole ring as a single 0x03 frame on every register and every watch. At the default the server
    answered 1009 and closed the link, the client reconnected and sent the same frame again, and the
    loop repeated about once a second forever - 1827 occurrences in a single morning, each one costing
    the tab its mirror.
    """

    MUXTEE_RING_BYTES = 2 * 1024 * 1024

    def test_the_attach_server_accepts_a_full_owner_snapshot(self):
        self.assertGreater(muxd.LOCAL_MAX_MESSAGE, self.MUXTEE_RING_BYTES)

    def test_the_default_is_not_the_websockets_default(self):
        self.assertNotEqual(muxd.LOCAL_MAX_MESSAGE, 1024 * 1024)


if __name__ == "__main__":
    unittest.main()
