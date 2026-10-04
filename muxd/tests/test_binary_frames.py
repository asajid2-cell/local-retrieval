"""Lever 4: the binary host-link frame codec, on the muxd side.

muxd and the relay must agree byte-for-byte on the four-byte header and on the rule for what counts as a
valid frame. These pin the contract muxd encodes and the relay decodes -- version, kind, the big-endian
slot, and a payload that is passed through untouched -- plus the two refusals that matter: a slot past the
current hello, and a kind that is not one of the three this protocol defines.
"""
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import muxd  # noqa: E402


def test_o_frame_round_trips_with_the_raw_payload():
    payload = b"\x1b[31mhello\x1b[0m\r\n"
    frame = muxd.encode_host_binary("o", 3, 1, payload)
    assert frame[:4] == bytes((muxd.HOST_BIN_VERSION, muxd.HOST_BIN_KIND["o"], 0, 1))
    kind, name, raw = muxd.decode_host_binary(frame, 3, ["a", "b", "c"])
    assert (kind, name) == ("o", "b")
    assert raw == payload


def test_i_frame_carries_input_bytes_and_the_slot_decodes_to_the_name():
    frame = muxd.encode_host_binary("i", 2, 0, b"ls -la\r")
    kind, name, raw = muxd.decode_host_binary(frame, 2, ["work", "scratch"])
    assert (kind, name) == ("i", "work")
    assert raw == b"ls -la\r"


def test_a_slot_past_the_current_hello_is_refused():
    # muxd must not encode an index it cannot address, and decode must reject one the relay sends back.
    assert muxd.encode_host_binary("o", 1, 1, b"x") is None
    frame = bytes((muxd.HOST_BIN_VERSION, muxd.HOST_BIN_KIND["o"], 0, 5)) + b"x"
    assert muxd.decode_host_binary(frame, 1, ["only"]) is None


def test_an_unknown_version_kind_or_truncated_header_is_refused():
    payload = b"data"
    good = muxd.encode_host_binary("o", 1, 0, payload)
    assert muxd.decode_host_binary(b"\x09" + good[1:], 1, ["s"]) is None            # bad version
    assert muxd.decode_host_binary(good[:1] + b"\x07" + good[2:], 1, ["s"]) is None  # bad kind
    assert muxd.decode_host_binary(b"\x01\x02", 1, ["s"]) is None                    # truncated
    assert muxd.decode_host_binary(b"not bytes", 1, ["s"]) is None                    # not bytes


def test_redraw_is_an_empty_payload_on_the_same_header():
    frame = muxd.encode_host_binary("r", 1, 0, b"")
    assert frame == bytes((muxd.HOST_BIN_VERSION, muxd.HOST_BIN_KIND["r"], 0, 0))
    assert muxd.decode_host_binary(frame, 1, ["s"]) == ("r", "s", b"")


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
