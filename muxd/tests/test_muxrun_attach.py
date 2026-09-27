import asyncio
import ctypes
import os
import subprocess
import sys
import time
import unittest
from unittest import mock
from argparse import Namespace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import muxrun


class OwnerRedrawTests(unittest.IsolatedAsyncioTestCase):
    async def test_requested_redraw_repaints_a_unchanged_screen(self):
        class Socket:
            def __init__(self):
                self.sent = []

            async def send(self, data):
                self.sent.append(__import__('json').loads(data))

        ws = Socket()
        stop = asyncio.Event()
        redraw = asyncio.Event()
        with mock.patch.object(muxrun, 'read_visible_screen', return_value=(80, 24, 'fixed frame')):
            task = asyncio.create_task(muxrun.screen_pump(ws, stop, redraw))
            try:
                await asyncio.sleep(0.15)
                self.assertEqual(len([m for m in ws.sent if m['t'] == 'o']), 1)
                redraw.set()
                await asyncio.sleep(0.15)
                self.assertEqual(len([m for m in ws.sent if m['t'] == 'o']), 2)
            finally:
                stop.set()
                await task

    async def test_failed_console_read_does_not_erase_the_browser_mirror(self):
        class Socket:
            def __init__(self):
                self.sent = []

            async def send(self, data):
                self.sent.append(__import__('json').loads(data))

        ws = Socket()
        stop = asyncio.Event()
        redraw = asyncio.Event()
        samples = [(80, 24, 'VISIBLE FOOTER'), None, (80, 24, 'VISIBLE FOOTER')]

        def read():
            value = samples.pop(0)
            if value is None:
                redraw.set()
            if not samples:
                stop.set()
            return value

        with mock.patch.object(muxrun, 'read_visible_screen', side_effect=read):
            await muxrun.screen_pump(ws, stop, redraw)
        self.assertEqual([m['t'] for m in ws.sent], ['size', 'o', 'o'])
        self.assertFalse(redraw.is_set())
        self.assertEqual(ws.sent[1]['d'], ws.sent[2]['d'])

    async def test_size_change_repaints_even_when_screen_text_is_unchanged(self):
        class Socket:
            def __init__(self):
                self.sent = []

            async def send(self, data):
                self.sent.append(__import__('json').loads(data))

        ws = Socket()
        stop = asyncio.Event()
        with mock.patch.object(muxrun, 'read_visible_screen', side_effect=[
            (80, 24, 'same frame'), (100, 24, 'same frame'), (100, 24, 'same frame')
        ]):
            task = asyncio.create_task(muxrun.screen_pump(ws, stop))
            try:
                await asyncio.sleep(0.2)
            finally:
                stop.set()
                await task
        self.assertEqual([m['t'] for m in ws.sent], ['size', 'o', 'size', 'o'])

    def test_owner_snapshot_addresses_rows_instead_of_wrapping_full_width_lines(self):
        frame = muxrun.repaint_frame(5, 3, 'AAAAA\r\nBBBBB\r\nFOOT!')
        self.assertIn(b'\x1b[1;1HAAAAA\x1b[2;1HBBBBB\x1b[3;1HFOOT!', frame)
        self.assertNotIn(b'AAAAA\r\nBBBBB', frame)
        self.assertIn(b'\x1b[3;1HFOOT!', muxrun.repaint_frame(5, 3, 'A\r\n\r\nFOOT!'))

    async def test_owner_redraw_request_does_not_write_console_input(self):
        class Socket:
            def __init__(self):
                self.messages = [__import__('json').dumps({'t': 'redraw'})]

            def __aiter__(self):
                return self

            async def __anext__(self):
                if self.messages:
                    return self.messages.pop(0)
                raise StopAsyncIteration

        redraw = asyncio.Event()
        with mock.patch.object(muxrun, 'write_console_input') as write:
            await muxrun.listen_remote(Socket(), object(), redraw)
        self.assertTrue(redraw.is_set())
        write.assert_not_called()


class AttachedChildTests(unittest.TestCase):
    def test_attaching_to_new_console_retries_only_while_it_is_starting(self):
        class Kernel:
            def __init__(self, failures):
                self.failures = failures
                self.calls = []

            def FreeConsole(self):
                self.calls.append("free")

            def AttachConsole(self, pid):
                self.calls.append(pid)
                if self.failures:
                    self.failures -= 1
                    return False
                return True

        kernel = Kernel(2)
        error = OSError(6, "invalid handle")
        error.winerror = 6
        with mock.patch.object(muxrun.ctypes, "windll", type("Windll", (), {"kernel32": kernel})()), \
             mock.patch.object(muxrun.ctypes, "WinError", return_value=error), \
             mock.patch.object(muxrun.time, "sleep"):
            muxrun.attach_existing_console(4242)
        self.assertEqual(kernel.calls, ["free", 4242, 4242, 4242])

    def test_console_attach_does_not_retry_unrelated_errors(self):
        class Kernel:
            calls = 0

            def FreeConsole(self):
                pass

            def AttachConsole(self, pid):
                self.calls += 1
                return False

        kernel = Kernel()
        error = OSError(5, "access denied")
        error.winerror = 5
        with mock.patch.object(muxrun.ctypes, "windll", type("Windll", (), {"kernel32": kernel})()), \
             mock.patch.object(muxrun.ctypes, "WinError", return_value=error), \
             mock.patch.object(muxrun.time, "sleep") as sleep:
            with self.assertRaises(OSError):
                muxrun.attach_existing_console(4242)
        self.assertEqual(kernel.calls, 1)
        sleep.assert_not_called()

    def test_console_attach_opens_exact_process_handle_first(self):
        events = []
        original_child = muxrun.AttachedChild
        original_attach = muxrun.attach_existing_console

        class FakeChild:
            def __init__(self, pid):
                self.pid = pid
                events.append(("handle", pid))

            def close(self):
                events.append(("close", self.pid))

        def fail_attach(pid):
            events.append(("attach", pid))
            raise RuntimeError("attach probe")

        muxrun.AttachedChild = FakeChild
        muxrun.attach_existing_console = fail_attach
        try:
            with self.assertRaisesRegex(RuntimeError, "attach probe"):
                muxrun.open_attached_console(4242)
        finally:
            muxrun.AttachedChild = original_child
            muxrun.attach_existing_console = original_attach

        self.assertEqual(
            events,
            [("handle", 4242), ("attach", 4242), ("close", 4242)],
        )

    def test_current_process_is_reported_alive(self):
        child = muxrun.AttachedChild(os.getpid())
        try:
            self.assertIsNone(child.poll())
        finally:
            child.close()

    def test_exited_process_is_reported_finished(self):
        proc = subprocess.Popen(
            [sys.executable, "-c", "pass"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        proc.wait(timeout=10)
        child = muxrun.AttachedChild(proc.pid)

        deadline = time.time() + 5
        result = child.poll()
        while result is None and time.time() < deadline:
            time.sleep(0.05)
            result = child.poll()

        self.assertIsNotNone(result)

    def test_exact_handle_can_terminate_the_attached_target(self):
        proc = subprocess.Popen(
            [sys.executable, "-c", "import time; time.sleep(60)"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        child = muxrun.AttachedChild(proc.pid)
        try:
            child.terminate()
            child.wait(timeout=10)
            self.assertIsNotNone(child.poll())
            proc.wait(timeout=10)
        finally:
            child.close()
            if proc.poll() is None:
                proc.kill()
                proc.wait(timeout=5)

    def test_connect_failure_closes_attached_process_handle(self):
        closed = []
        mode_changes = []
        ctrl_ignored = []
        original_ensure = muxrun.ensure_muxd_started
        original_open = muxrun.open_attached_console
        original_modes = muxrun.enable_console_modes
        original_ctrl = muxrun.set_console_ctrl_ignored
        original_connect = muxrun.connect_owner

        class FakeChild:
            pid = 4242

            def close(self):
                closed.append(self.pid)

        async def fail_connect(*_args, **_kwargs):
            raise RuntimeError("muxd unavailable")

        muxrun.ensure_muxd_started = lambda: None
        muxrun.open_attached_console = lambda _pid: FakeChild()
        muxrun.enable_console_modes = lambda: mode_changes.append(True)
        muxrun.set_console_ctrl_ignored = lambda ignored: ctrl_ignored.append(ignored)
        muxrun.connect_owner = fail_connect
        args = Namespace(
            attach_pid=4242,
            cmd="codex resume session-1",
            cmd_b64="",
            cwd=os.getcwd(),
            name="mirror-test",
            session_id="session-1",
            alias=[],
        )
        try:
            result = asyncio.run(muxrun.main_async(args))
        finally:
            muxrun.ensure_muxd_started = original_ensure
            muxrun.open_attached_console = original_open
            muxrun.enable_console_modes = original_modes
            muxrun.set_console_ctrl_ignored = original_ctrl
            muxrun.connect_owner = original_connect

        self.assertEqual(2, result)
        self.assertEqual([4242], closed)
        self.assertEqual([], mode_changes)
        self.assertEqual([True, False], ctrl_ignored)

    def test_superseded_attached_owner_fails_without_retrying_forever(self):
        calls = []
        original_ensure = muxrun.ensure_muxd_started
        original_register = muxrun.register_owner
        original_sleep = muxrun.asyncio.sleep

        async def reject(*_args, **_kwargs):
            calls.append("register")
            raise RuntimeError("session already has a visible local owner: mirror-test")

        async def unexpected_sleep(_delay):
            calls.append("sleep")

        muxrun.ensure_muxd_started = lambda: None
        muxrun.register_owner = reject
        muxrun.asyncio.sleep = unexpected_sleep
        args = Namespace(attach_pid=4242)
        try:
            with self.assertRaisesRegex(muxrun.OwnerSupersededError, "visible local owner"):
                asyncio.run(
                    muxrun.connect_owner(
                        args,
                        "codex resume session-1",
                        os.getcwd(),
                        child_started=True,
                        owner_key="k" * 32,
                        child_pid=4242,
                    )
                )
        finally:
            muxrun.ensure_muxd_started = original_ensure
            muxrun.register_owner = original_register
            muxrun.asyncio.sleep = original_sleep

        self.assertEqual(["register"], calls)

    def test_initial_superseded_attached_owner_exits_cleanly(self):
        original_ensure = muxrun.ensure_muxd_started
        original_open = muxrun.open_attached_console
        original_connect = muxrun.connect_owner
        closed = []

        class FakeChild:
            pid = 4242

            def close(self):
                closed.append(self.pid)

        async def superseded(*_args, **_kwargs):
            raise muxrun.OwnerSupersededError("session already has a visible local owner")

        muxrun.ensure_muxd_started = lambda: None
        muxrun.open_attached_console = lambda _pid: FakeChild()
        muxrun.connect_owner = superseded
        args = Namespace(
            attach_pid=4242,
            cmd="codex resume session-1",
            cmd_b64="",
            cwd=os.getcwd(),
            name="mirror-test",
            session_id="session-1",
            alias=[],
        )
        try:
            self.assertEqual(0, asyncio.run(muxrun.main_async(args)))
        finally:
            muxrun.ensure_muxd_started = original_ensure
            muxrun.open_attached_console = original_open
            muxrun.connect_owner = original_connect

        self.assertEqual([4242], closed)

    def test_console_key_events_translate_ctrl_navigation_and_alt(self):
        self.assertEqual(
            [(0x43, "\x03", muxrun.LEFT_CTRL_PRESSED)],
            muxrun.console_key_events(b"\x03"),
        )
        self.assertEqual(
            [(muxrun.VK_UP, "", 0), (muxrun.VK_DELETE, "", 0)],
            muxrun.console_key_events(b"\x1b[A\x1b[3~"),
        )
        self.assertEqual(
            [(0x58, "x", muxrun.LEFT_ALT_PRESSED)],
            muxrun.console_key_events(b"\x1bx"),
        )
        self.assertEqual(
            [(muxrun.VK_BACK, "\b", 0)],
            muxrun.console_key_events(b"\x7f"),
        )
        self.assertEqual(
            [(muxrun.VK_UP, "", 0), (muxrun.VK_END, "", 0)],
            muxrun.console_key_events(b"\x1bOA\x1bOF"),
        )

    def test_web_mouse_reports_reach_the_owner_as_console_mouse_events(self):
        up = muxrun.input_records(b"\x1b[<64;93;24M")
        down = muxrun.input_records(b"\x1b[<65;93;24M")
        self.assertEqual(len(up), 1)
        self.assertEqual(len(down), 1)
        self.assertEqual(up[0].EventType, muxrun.MOUSE_EVENT_TYPE)
        self.assertEqual(up[0].Event.MouseEvent.dwEventFlags, muxrun.MOUSE_WHEELED)
        self.assertEqual(up[0].Event.MouseEvent.dwButtonState >> 16, 120)
        self.assertEqual(down[0].Event.MouseEvent.dwButtonState >> 16, (-120) & 0xffff)
        left = muxrun.input_records(b"\x1b[<66;93;24M")[0].Event.MouseEvent
        right = muxrun.input_records(b"\x1b[<67;93;24M")[0].Event.MouseEvent
        self.assertEqual((left.dwEventFlags, right.dwEventFlags), (muxrun.MOUSE_HWHEELED,) * 2)
        self.assertEqual((left.dwButtonState >> 16, right.dwButtonState >> 16),
                         ((-120) & 0xffff, 120))
        self.assertEqual((up[0].Event.MouseEvent.dwMousePosition.X,
                          up[0].Event.MouseEvent.dwMousePosition.Y), (92, 23))
        click = muxrun.input_records(b"\x1b[<0;93;24M\x1b[<0;93;24m")
        self.assertEqual(len(click), 2, 'app-owned clicks must reach its console without typing text')
        self.assertEqual([rec.EventType for rec in click], [muxrun.MOUSE_EVENT_TYPE] * 2)
        self.assertEqual([rec.Event.MouseEvent.dwButtonState for rec in click], [1, 0])
        self.assertEqual([rec.Event.MouseEvent.dwEventFlags for rec in click], [0, 0])
        self.assertEqual(muxrun.input_records(b"\x1b[<1;93;24M")[0].Event.MouseEvent.dwButtonState,
                         4, 'SGR button 1 is the middle console button')
        self.assertEqual(muxrun.input_records(b"\x1b[<2;93;24M")[0].Event.MouseEvent.dwButtonState,
                         2, 'SGR button 2 is the right console button')
        self.assertEqual(muxrun.input_records(b"\x1b[<32;93;24M"), [], 'motion remains dropped')
        self.assertEqual(muxrun.input_records(b"\x1b[<3;93;24M"), [], 'unowned release is dropped')
        self.assertEqual(muxrun.input_records(b"\x1b[<64;93;24Mhello")[-2].EventType, muxrun.KEY_EVENT)
        self.assertEqual(muxrun.input_records(b"hello\x1b[<64;93;24M")[-1].EventType, muxrun.MOUSE_EVENT_TYPE)
        self.assertEqual(muxrun.input_records(b"\x1b[<68;93;24M")[0].Event.MouseEvent.dwControlKeyState,
                         muxrun.SHIFT_PRESSED)
        self.assertEqual(muxrun.console_key_events(b"hello"),
                         [muxrun.vk_for_char(c) for c in 'hello'])

    def test_verified_descendants_reject_reused_parent_chain(self):
        rows = [
            (20, 10, 200),
            (30, 20, 300),
            (40, 10, 90),
            (50, 40, 500),
        ]
        self.assertEqual(
            [20, 30],
            muxrun.verified_descendant_pids(10, 100, rows),
        )

    def test_mode_restore_changes_only_bits_muxrun_changed(self):
        original = muxrun.ENABLE_QUICK_EDIT_MODE
        changed = muxrun.ENABLE_QUICK_EDIT_MODE | muxrun.ENABLE_EXTENDED_FLAGS
        current = muxrun.ENABLE_PROCESSED_INPUT | muxrun.ENABLE_EXTENDED_FLAGS
        restored = muxrun.restore_mode_bits(current, original, changed)
        self.assertTrue(restored & muxrun.ENABLE_QUICK_EDIT_MODE)
        self.assertFalse(restored & muxrun.ENABLE_EXTENDED_FLAGS)
        self.assertTrue(restored & muxrun.ENABLE_PROCESSED_INPUT)

    def test_cooked_ctrl_c_uses_console_control_event(self):
        self.assertTrue(muxrun.should_generate_ctrl_c(b"\x03", muxrun.ENABLE_PROCESSED_INPUT))
        self.assertFalse(muxrun.should_generate_ctrl_c(b"\x03", 0))
        self.assertFalse(muxrun.should_generate_ctrl_c(b"x", muxrun.ENABLE_PROCESSED_INPUT))

    def test_input_records_emit_balanced_key_down_and_up(self):
        records = muxrun.input_records(b"\x03\x1b[A")
        self.assertEqual(4, len(records))
        self.assertEqual(
            [True, False, True, False],
            [bool(record.Event.KeyEvent.bKeyDown) for record in records],
        )
        self.assertEqual(0x43, records[0].Event.KeyEvent.wVirtualKeyCode)
        self.assertEqual(muxrun.LEFT_CTRL_PRESSED, records[0].Event.KeyEvent.dwControlKeyState)
        self.assertEqual(muxrun.VK_UP, records[2].Event.KeyEvent.wVirtualKeyCode)

    def test_attached_stop_pins_descendants_before_terminating(self):
        events = []
        original_pin = muxrun.pin_verified_descendants

        class FakeAttached(muxrun.AttachedChild):
            def __init__(self, pid):
                self.pid = pid
                self.returncode = None
                self._handle = pid

            def poll(self):
                return self.returncode

            def terminate(self):
                events.append(("terminate", self.pid))
                self.returncode = 1

            def wait(self, timeout=None):
                return self.returncode

            def close(self):
                events.append(("close", self.pid))

        root = FakeAttached(10)
        descendants = [FakeAttached(20), FakeAttached(30)]

        def pin(_root):
            events.append(("pin", _root.pid))
            return descendants

        muxrun.pin_verified_descendants = pin
        try:
            self.assertTrue(muxrun.terminate_child_tree(root))
        finally:
            muxrun.pin_verified_descendants = original_pin

        self.assertEqual(
            [("pin", 10), ("terminate", 10), ("terminate", 20), ("terminate", 30)],
            events[:4],
        )

    def test_attached_stop_fails_when_a_descendant_does_not_exit(self):
        original_pin = muxrun.pin_verified_descendants

        class FakeAttached(muxrun.AttachedChild):
            def __init__(self, pid, exits=True):
                self.pid = pid
                self.returncode = None
                self._handle = pid
                self.exits = exits

            def poll(self):
                return self.returncode

            def terminate(self):
                if self.exits:
                    self.returncode = 1

            def wait(self, timeout=None):
                if self.returncode is None:
                    raise subprocess.TimeoutExpired(str(self.pid), timeout)
                return self.returncode

            def close(self):
                pass

        root = FakeAttached(10)
        stuck = FakeAttached(20, exits=False)
        muxrun.pin_verified_descendants = lambda _root: [stuck]
        try:
            self.assertFalse(muxrun.terminate_child_tree(root))
        finally:
            muxrun.pin_verified_descendants = original_pin
        self.assertTrue(root.pending_descendants_alive())
        self.assertFalse(root.tree_exit_confirmed())

        stuck.exits = True
        self.assertTrue(muxrun.terminate_child_tree(root))
        self.assertFalse(root.pending_descendants_alive())
        self.assertTrue(root.tree_exit_confirmed())

    def test_descendant_pin_failure_is_not_silently_ignored(self):
        class Root:
            pid = 10

            @staticmethod
            def creation_time():
                return 100

        denied = OSError("access denied")
        denied.winerror = 5
        with mock.patch.object(muxrun, "process_snapshot_edges", return_value=[(20, 10)]), \
             mock.patch.object(muxrun, "AttachedChild", side_effect=denied):
            with self.assertRaisesRegex(RuntimeError, "could not pin descendant pid 20"):
                muxrun.pin_verified_descendants(Root())

    def test_console_host_descendants_are_not_waited_before_sidecar_detach(self):
        class Root:
            pid = 10

            @staticmethod
            def creation_time():
                return 100

        with mock.patch.object(
            muxrun,
            "process_snapshot_edges",
            return_value=[(20, 10, "conhost.exe"), (30, 10, "agent.exe")],
        ), mock.patch.object(muxrun, "AttachedChild") as attached:
            agent = mock.Mock()
            agent.pid = 30
            agent.poll.return_value = None
            agent.creation_time.return_value = 110
            attached.return_value = agent

            pinned = muxrun.pin_verified_descendants(Root())

        self.assertEqual([agent], pinned)
        attached.assert_called_once_with(30)

    def test_failed_attached_kill_reports_error_without_dead_ack(self):
        class FakeChild:
            @staticmethod
            def poll():
                return None

        class FakeWs:
            def __init__(self):
                self.sent = []
                self.first = True

            def __aiter__(self):
                return self

            async def __anext__(self):
                if self.first:
                    self.first = False
                    return '{"t":"kill"}'
                await asyncio.Future()

            async def send(self, value):
                self.sent.append(value)

        async def run():
            ws = FakeWs()
            with mock.patch.object(muxrun, "terminate_child_tree", return_value=False):
                task = asyncio.create_task(muxrun.listen_remote(ws, FakeChild()))
                deadline = time.monotonic() + 2
                while not ws.sent and time.monotonic() < deadline:
                    await asyncio.sleep(0.01)
                task.cancel()
                with self.assertRaises(asyncio.CancelledError):
                    await task
            self.assertEqual(1, len(ws.sent))
            self.assertIn('"t": "killResult"', ws.sent[0])
            self.assertIn('"ok": false', ws.sent[0])
            self.assertNotIn('"t": "dead"', ws.sent[0])

        asyncio.run(run())

    def test_owner_reconnect_stops_when_the_visible_child_exits(self):
        original_ensure = muxrun.ensure_muxd_started
        original_register = muxrun.register_owner
        original_sleep = muxrun.asyncio.sleep
        calls = []

        class Child:
            def __init__(self):
                self.exited = False

            def poll(self):
                return 0 if self.exited else None

        child = Child()

        async def unavailable(*_args, **_kwargs):
            calls.append("register")
            child.exited = True
            raise OSError("muxd unavailable")

        async def no_sleep(_delay):
            calls.append("sleep")

        muxrun.ensure_muxd_started = lambda: None
        muxrun.register_owner = unavailable
        muxrun.asyncio.sleep = no_sleep
        try:
            with self.assertRaisesRegex(RuntimeError, "child exited"):
                asyncio.run(
                    muxrun.connect_owner(
                        Namespace(attach_pid=4242),
                        "codex resume session-1",
                        os.getcwd(),
                        child_started=True,
                        owner_key="k" * 32,
                        child_pid=4242,
                        child=child,
                    )
                )
        finally:
            muxrun.ensure_muxd_started = original_ensure
            muxrun.register_owner = original_register
            muxrun.asyncio.sleep = original_sleep

        self.assertEqual(["register"], calls)

    @unittest.skipUnless(os.name == "nt", "Windows console mode test")
    def test_console_mode_state_restores_exact_modes(self):
        k = ctypes.windll.kernel32
        hin = k.GetStdHandle(muxrun.STD_INPUT_HANDLE)
        hout = k.GetStdHandle(muxrun.STD_OUTPUT_HANDLE)
        before_in = ctypes.c_uint()
        before_out = ctypes.c_uint()
        if not k.GetConsoleMode(hin, ctypes.byref(before_in)):
            self.skipTest("stdin is not a console")
        if not k.GetConsoleMode(hout, ctypes.byref(before_out)):
            self.skipTest("stdout is not a console")

        state = muxrun.enable_console_modes()
        state.restore()
        after_in = ctypes.c_uint()
        after_out = ctypes.c_uint()
        self.assertTrue(k.GetConsoleMode(hin, ctypes.byref(after_in)))
        self.assertTrue(k.GetConsoleMode(hout, ctypes.byref(after_out)))
        self.assertEqual(before_in.value, after_in.value)
        self.assertEqual(before_out.value, after_out.value)

    @unittest.skipUnless(os.name == "nt", "Windows process tree test")
    def test_attached_stop_terminates_existing_descendants(self):
        parent = subprocess.Popen(
            [
                sys.executable,
                "-c",
                (
                    "import subprocess,sys,time;"
                    "p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);"
                    "print(p.pid,flush=True);time.sleep(60)"
                ),
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
        )
        descendant_pid = int(parent.stdout.readline().strip())
        root = muxrun.AttachedChild(parent.pid)
        descendant = muxrun.AttachedChild(descendant_pid)
        try:
            self.assertTrue(muxrun.terminate_child_tree(root))
            parent.wait(timeout=10)
            deadline = time.time() + 10
            while descendant.poll() is None and time.time() < deadline:
                time.sleep(0.05)
            self.assertIsNotNone(descendant.poll())
        finally:
            root.close()
            descendant.close()
            parent.stdout.close()
            if parent.poll() is None:
                parent.kill()
                parent.wait(timeout=5)


class ContainedChildTests(unittest.TestCase):
    @unittest.skipUnless(os.name == "nt", "Windows console flag test")
    def test_contained_child_spawns_without_a_visible_console(self):
        popen = mock.Mock(side_effect=RuntimeError("stop after recording the call"))
        with mock.patch.object(muxrun, "ChildJob", return_value=mock.Mock()), \
             mock.patch.object(muxrun.subprocess, "Popen", popen):
            with self.assertRaises(RuntimeError):
                muxrun.launch_contained_child("Write-Output probe", os.getcwd())

        _, kwargs = popen.call_args
        self.assertNotEqual(0, muxrun.CREATE_NO_WINDOW)
        self.assertEqual(muxrun.CREATE_NO_WINDOW, kwargs.get("creationflags"))


if __name__ == "__main__":
    unittest.main()
