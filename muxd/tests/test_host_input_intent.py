"""Durable input-intent authorization on the muxd relay host link.

Every test here asks the same question from a different angle: can a frame reach the PTY without
a principal having signed for these exact bytes, on this exact session, inside the expiry window?
The answer has to be no even when the relay is the one asking.
"""

import base64
import asyncio
import hashlib
import importlib
import json
import os
import tempfile
import time
import unittest
from unittest import mock

from cryptography.hazmat.primitives.asymmetric import ec

host_input_intent = importlib.import_module("host_input_intent")
muxd = importlib.import_module("muxd")

INSTANCE = "muxd-instance-1"
SESSION_UUID = "6f1c2d3e-aaaa-4bbb-8ccc-000000000001"
PRINCIPAL = "principal-alice"
KEY_ID = "key-1"
CHANNEL = "chan-7"
NOW_MS = 1_700_000_000_000


class RecordingSession:
    """A session that refuses to be written to quietly: every write is recorded so a test can
    assert a refused frame performed exactly zero of them."""

    def __init__(self, session_uuid=SESSION_UUID):
        self.name = "work"
        self.session_uuid = session_uuid
        self.writes = []

    def write(self, data):
        self.writes.append(data)

    def alive(self):
        return True


def make_endpoint(session_uuid=SESSION_UUID, **grant):
    private = ec.generate_private_key(ec.SECP256R1())
    endpoint = host_input_intent.PrincipalEndpoint(instance_id=INSTANCE)
    endpoint.register(
        PRINCIPAL, KEY_ID, private.public_key(), session_uuid=session_uuid, **grant
    )
    return endpoint, private


def sign(private, transcript):
    from cryptography.hazmat.primitives import hashes
    from cryptography.hazmat.primitives.asymmetric import utils as asym_utils

    der = private.sign(transcript, ec.ECDSA(hashes.SHA256()))
    r, s = asym_utils.decode_dss_signature(der)
    return base64.b64encode(r.to_bytes(32, "big") + s.to_bytes(32, "big")).decode()


def signed_frame(private, body=b"ls -la\r", *, intent_id="intent-1", session_uuid=SESSION_UUID,
                 issued_at=NOW_MS, expires_at=None, instance_id=INSTANCE, channel_id=CHANNEL,
                 seq=1, acl_revision=1, lease_epoch=1, body_sha256=None, body_b64=None):
    expires_at = issued_at + 5_000 if expires_at is None else expires_at
    digest = hashlib.sha256(body).hexdigest() if body_sha256 is None else body_sha256
    transcript = host_input_intent.canonical_transcript_bytes(
        host_input_intent.OP_INPUT_DURABLE, instance_id, PRINCIPAL, KEY_ID, session_uuid,
        channel_id, seq, lease_epoch, acl_revision, issued_at, expires_at, intent_id, digest,
    )
    return {
        "t": "i",
        "s": "work",
        "auth": {
            "op": host_input_intent.OP_INPUT_DURABLE,
            "instanceId": instance_id,
            "principalId": PRINCIPAL,
            "keyId": KEY_ID,
            "sessionUuid": session_uuid,
            "channelId": channel_id,
            "intentId": intent_id,
            "seq": seq,
            "aclRevision": acl_revision,
            "leaseEpoch": lease_epoch,
            "issuedAtMs": issued_at,
            "expiresAtMs": expires_at,
            "bodySha256": digest,
            "bodyB64": base64.b64encode(body).decode() if body_b64 is None else body_b64,
            "sig": sign(private, transcript),
        },
    }


class HostInputProofTests(unittest.TestCase):
    def verify(self, frame, endpoint, session=None, now_ms=NOW_MS):
        return host_input_intent.verify_host_input_frame(
            frame, endpoint=endpoint, session=session or RecordingSession(), now_ms=now_ms
        )

    def test_signed_frame_yields_the_principal_and_the_exact_bytes(self):
        endpoint, private = make_endpoint()
        principal, body, refusal = self.verify(signed_frame(private, b"echo hi\r"), endpoint)

        self.assertIsNone(refusal)
        self.assertEqual(body, b"echo hi\r")
        self.assertEqual(principal.principal_id, PRINCIPAL)
        self.assertEqual(principal.session_uuid, SESSION_UUID)
        # The identity, not the transport, names the intent namespace.
        self.assertEqual(principal.intent_scope, "principal:" + PRINCIPAL)
        self.assertNotIn("relay", principal.intent_scope)

    def test_proofless_frame_is_refused_with_principal_proof_required(self):
        endpoint, _ = make_endpoint()
        session = RecordingSession()

        principal, body, refusal = self.verify(
            {"t": "i", "s": "work", "d": base64.b64encode(b"rm -rf /\r").decode()},
            endpoint,
            session=session,
        )

        self.assertIsNone(principal)
        self.assertIsNone(body)
        self.assertEqual(refusal.code, "principal-proof-required")
        self.assertEqual(session.writes, [], "a proofless frame must perform zero PTY writes")

    def test_legacy_d_payload_is_never_a_substitute_for_bodyb64(self):
        endpoint, private = make_endpoint()
        frame = signed_frame(private)
        frame["d"] = base64.b64encode(b"different\r").decode()
        frame["auth"].pop("bodyB64")

        _, _, refusal = self.verify(frame, endpoint)

        self.assertEqual(refusal.code, "principal-proof-required")

    def test_body_that_does_not_match_the_signed_digest_is_rejected(self):
        endpoint, private = make_endpoint()
        frame = signed_frame(private, b"echo safe\r")
        # The relay swaps the payload but keeps the signature and digest it was handed.
        frame["auth"]["bodyB64"] = base64.b64encode(b"echo pwned\r").decode()

        principal, body, refusal = self.verify(frame, endpoint)

        self.assertIsNone(principal)
        self.assertIsNone(body)
        self.assertEqual(refusal.code, "principal-proof-invalid")

    def test_digest_swapped_to_match_a_forged_body_still_fails_the_signature(self):
        endpoint, private = make_endpoint()
        frame = signed_frame(private, b"echo safe\r")
        forged = b"echo pwned\r"
        frame["auth"]["bodyB64"] = base64.b64encode(forged).decode()
        frame["auth"]["bodySha256"] = hashlib.sha256(forged).hexdigest()

        _, _, refusal = self.verify(frame, endpoint)

        self.assertEqual(refusal.code, "principal-proof-invalid")

    def test_expired_proof_is_refused(self):
        endpoint, private = make_endpoint()
        frame = signed_frame(private, issued_at=NOW_MS - 9_000, expires_at=NOW_MS - 4_000)

        _, _, refusal = self.verify(frame, endpoint)

        self.assertEqual(refusal.code, "principal-proof-expired")

    def test_lifetime_beyond_the_fifteen_second_maximum_is_refused_not_clamped(self):
        endpoint, private = make_endpoint()
        frame = signed_frame(private, expires_at=NOW_MS + 60_000)

        _, _, refusal = self.verify(frame, endpoint)

        self.assertEqual(refusal.code, "principal-proof-expired")

    def test_proof_signed_for_another_session_is_refused(self):
        endpoint, private = make_endpoint()
        other = "6f1c2d3e-aaaa-4bbb-8ccc-000000000002"
        # Correctly signed — for the wrong session. The signature is not the whole story.
        endpoint.register(
            PRINCIPAL, KEY_ID, endpoint.lookup(PRINCIPAL, KEY_ID, SESSION_UUID)["publicKey"],
            session_uuid=other,
        )
        frame = signed_frame(private, session_uuid=other)

        principal, body, refusal = self.verify(frame, endpoint, session=RecordingSession())

        self.assertIsNone(principal)
        self.assertIsNone(body)
        self.assertEqual(refusal.code, "principal-session-mismatch")

    def test_session_without_a_durable_uuid_fails_closed(self):
        endpoint, private = make_endpoint()
        session = RecordingSession(session_uuid="")

        _, _, refusal = self.verify(signed_frame(private), endpoint, session=session)

        self.assertEqual(refusal.code, "principal-proof-required")
        self.assertEqual(session.writes, [])

    def test_unknown_principal_is_refused_by_an_empty_endpoint(self):
        _, private = make_endpoint()
        empty = host_input_intent.PrincipalEndpoint(instance_id=INSTANCE)

        _, _, refusal = self.verify(signed_frame(private), empty)

        self.assertEqual(refusal.code, "principal-proof-invalid")

    def test_stale_acl_revision_and_lease_epoch_are_refused(self):
        endpoint, private = make_endpoint(acl_revision=9, lease_epoch=4)

        _, _, stale_acl = self.verify(
            signed_frame(private, acl_revision=8, lease_epoch=4), endpoint
        )
        _, _, stale_lease = self.verify(
            signed_frame(private, acl_revision=9, lease_epoch=3), endpoint
        )

        self.assertEqual(stale_acl.code, "principal-acl-stale")
        self.assertEqual(stale_lease.code, "principal-lease-stale")

    def test_replay_policy_is_not_authorization(self):
        endpoint, _ = make_endpoint()
        frame = {"t": "i", "s": "work", "replayPolicy": "exactly-once", "leaseToken": "tok",
                 "d": base64.b64encode(b"x").decode()}

        _, _, refusal = self.verify(frame, endpoint)

        self.assertEqual(refusal.code, "principal-proof-required")

    def test_refusal_frame_carries_a_machine_code_and_the_session(self):
        refusal = host_input_intent.PrincipalRefusal(
            host_input_intent.PROOF_REQUIRED, "no proof", "intent-1"
        )
        frame = refusal.frame("work")

        self.assertEqual(frame["t"], "err")
        self.assertEqual(frame["code"], "principal-proof-required")
        self.assertEqual(frame["s"], "work")
        self.assertEqual(frame["intentId"], "intent-1")


class IntentFingerprintTests(unittest.TestCase):
    def test_resigning_the_same_operation_keeps_one_fingerprint(self):
        endpoint, private = make_endpoint()
        first, _, _ = host_input_intent.verify_host_input_frame(
            signed_frame(private, b"same\r", issued_at=NOW_MS),
            endpoint=endpoint, session=RecordingSession(), now_ms=NOW_MS,
        )
        # A reconnect re-signs with a fresh issuedAtMs; that is the same operation, not a new one.
        second, _, _ = host_input_intent.verify_host_input_frame(
            signed_frame(private, b"same\r", issued_at=NOW_MS + 1_500),
            endpoint=endpoint, session=RecordingSession(), now_ms=NOW_MS + 1_500,
        )

        self.assertEqual(first.intent_fingerprint, second.intent_fingerprint)

    def test_different_bytes_under_one_intent_id_are_a_different_fingerprint(self):
        endpoint, private = make_endpoint()
        first, _, _ = host_input_intent.verify_host_input_frame(
            signed_frame(private, b"echo a\r"), endpoint=endpoint,
            session=RecordingSession(), now_ms=NOW_MS,
        )
        second, _, _ = host_input_intent.verify_host_input_frame(
            signed_frame(private, b"echo b\r"), endpoint=endpoint,
            session=RecordingSession(), now_ms=NOW_MS,
        )

        self.assertNotEqual(first.intent_fingerprint, second.intent_fingerprint)

    def test_journal_record_persists_the_authorizing_identity_with_the_outcome(self):
        endpoint, private = make_endpoint()
        principal, _, _ = host_input_intent.verify_host_input_frame(
            signed_frame(private), endpoint=endpoint, session=RecordingSession(), now_ms=NOW_MS,
        )

        record = principal.journal_record("completed")

        self.assertEqual(record["principalId"], PRINCIPAL)
        self.assertEqual(record["keyId"], KEY_ID)
        self.assertEqual(record["intentId"], "intent-1")
        self.assertEqual(record["sessionUuid"], SESSION_UUID)
        self.assertEqual(record["outcome"], "completed")


class DurableReplayTests(unittest.TestCase):
    """The replay rules muxd's durable input path executes, exercised directly."""

    def test_exact_replay_returns_the_cached_result_without_rewriting(self):
        record = {"fingerprint": "fp", "status": "completed",
                  "result": {"t": "input-ok", "s": "work"}}

        disposition, result = host_input_intent.replay_decision(record, "fp")

        self.assertEqual(disposition, host_input_intent.REPLAY_CACHED)
        self.assertEqual(result, {"t": "input-ok", "s": "work"})

    def test_a_failed_outcome_is_also_settled_and_replayable(self):
        record = {"fingerprint": "fp", "status": "failed", "result": {"t": "err", "m": "dead"}}

        disposition, result = host_input_intent.replay_decision(record, "fp")

        self.assertEqual(disposition, host_input_intent.REPLAY_CACHED)
        self.assertEqual(result["t"], "err")

    def test_a_different_body_under_the_same_intent_id_is_rejected(self):
        record = {"fingerprint": "fp", "status": "completed", "result": {"t": "input-ok"}}

        disposition, result = host_input_intent.replay_decision(record, "other-fp")

        self.assertEqual(disposition, host_input_intent.REPLAY_CONFLICT)
        self.assertEqual(result["t"], "err")
        self.assertIn("different payload", result["m"])

    def test_an_uncertain_outcome_is_refused_and_never_auto_replayed(self):
        record = {"fingerprint": "fp", "status": "dispatching", "result": {}}

        disposition, result = host_input_intent.replay_decision(record, "fp")

        self.assertEqual(disposition, host_input_intent.REPLAY_UNCERTAIN)
        self.assertEqual(result["t"], "err")
        self.assertIn("uncertain", result["m"])
        self.assertNotIn("input-ok", json.dumps(result))

    def test_no_record_means_the_write_still_has_to_happen(self):
        disposition, result = host_input_intent.replay_decision(None, "fp")

        self.assertEqual(disposition, host_input_intent.REPLAY_FRESH)
        self.assertIsNone(result)


class MuxdWiringTests(unittest.TestCase):
    """muxd's own host-link seam, not a re-implementation of it."""

    def test_relay_host_link_refuses_a_proofless_frame_with_zero_writes(self):
        session = RecordingSession()

        with mock.patch.dict(os.environ, {"MUX_AUTHZ_MODE": "enforce"}):
            principal, body, refusal = muxd.authorize_relay_input(
                {"t": "i", "s": "work", "d": base64.b64encode(b"whoami\r").decode()},
                session,
                endpoint=host_input_intent.PrincipalEndpoint(instance_id=INSTANCE),
                now_ms=NOW_MS,
            )

        self.assertIsNone(principal)
        self.assertIsNone(body)
        self.assertEqual(refusal.code, "principal-proof-required")
        self.assertEqual(refusal.frame("work")["code"], "principal-proof-required")
        self.assertEqual(session.writes, [])

    def test_runtime_config_enforces_when_process_environment_is_unset(self):
        session = RecordingSession()

        with mock.patch.object(muxd, "ENV", {"MUX_AUTHZ_MODE": "enforce"}):
            with mock.patch.dict(os.environ, {}):
                os.environ.pop("MUX_AUTHZ_MODE", None)
                principal, body, refusal = muxd.authorize_relay_input(
                    {"t": "i", "s": "work", "d": base64.b64encode(b"whoami\r").decode()},
                    session,
                    endpoint=host_input_intent.PrincipalEndpoint(instance_id=INSTANCE),
                    now_ms=NOW_MS,
                )

        self.assertIsNone(principal)
        self.assertIsNone(body)
        self.assertEqual(refusal.code, "principal-proof-required")
        self.assertEqual(session.writes, [])

    def test_process_environment_overrides_runtime_authz_config(self):
        legacy = {"t": "i", "s": "work", "d": base64.b64encode(b"legacy\r").decode()}

        with mock.patch.object(muxd, "ENV", {"MUX_AUTHZ_MODE": "enforce"}):
            with mock.patch.dict(os.environ, {"MUX_AUTHZ_MODE": "audit"}):
                principal, body, refusal = muxd.authorize_relay_input(
                    legacy,
                    RecordingSession(),
                    endpoint=host_input_intent.PrincipalEndpoint(instance_id=INSTANCE),
                    now_ms=NOW_MS,
                )

        self.assertIsNone(principal)
        self.assertEqual(body, b"legacy\r")
        self.assertIsNone(refusal)

    def test_audit_mode_admits_only_a_proofless_legacy_body(self):
        endpoint, private = make_endpoint()
        legacy = {"t": "i", "s": "work", "d": base64.b64encode(b"legacy\r").decode()}
        with mock.patch.dict(os.environ, {"MUX_AUTHZ_MODE": "audit"}):
            principal, body, refusal = muxd.authorize_relay_input(
                legacy, RecordingSession(), endpoint=endpoint, now_ms=NOW_MS
            )
            self.assertIsNone(principal)
            self.assertEqual(body, b"legacy\r")
            self.assertIsNone(refusal)

            forged = signed_frame(private)
            forged["auth"]["sig"] = base64.b64encode(b"x" * 64).decode()
            principal, body, refusal = muxd.authorize_relay_input(
                forged, RecordingSession(), endpoint=endpoint, now_ms=NOW_MS
            )
            self.assertIsNone(principal)
            self.assertIsNone(body)
            self.assertEqual(refusal.code, "principal-proof-invalid")

    def test_relay_host_link_admits_a_verified_frame(self):
        endpoint, private = make_endpoint()

        principal, body, refusal = muxd.authorize_relay_input(
            signed_frame(private, b"pwd\r"), RecordingSession(), endpoint=endpoint, now_ms=NOW_MS
        )

        self.assertIsNone(refusal)
        self.assertEqual(body, b"pwd\r")
        self.assertEqual(principal.intent_scope, "principal:" + PRINCIPAL)

    def test_default_endpoint_has_no_principals_so_it_refuses_everything(self):
        _, private = make_endpoint()

        _, _, refusal = muxd.authorize_relay_input(
            signed_frame(private), RecordingSession(), now_ms=NOW_MS
        )

        self.assertIsNotNone(refusal)

    def test_input_durable_is_advertised_in_the_protocol_4_registry(self):
        self.assertEqual(muxd.PROTOCOL, 4)
        self.assertIn("inputDurable", muxd.CAPS)
        self.assertIn("input", muxd.CAPS)


@unittest.skipIf(os.name != "nt", "DPAPI registry requires Windows")
class PrincipalRegistryTests(unittest.TestCase):
    def test_dpapi_registry_round_trip_preserves_instance_and_grant(self):
        endpoint, _ = make_endpoint()
        with tempfile.TemporaryDirectory() as root:
            path = os.path.join(root, "principals.dpapi")
            host_input_intent.save_principal_endpoint(endpoint, path)
            with open(path, "rb") as stream:
                raw = stream.read()
            self.assertNotIn(PRINCIPAL.encode(), raw)
            loaded = host_input_intent.load_principal_endpoint(path)
            self.assertEqual(loaded.instance_id, INSTANCE)
            self.assertTrue(loaded.provisioned())
            self.assertIsNotNone(loaded.lookup(PRINCIPAL, KEY_ID, SESSION_UUID))


@unittest.skipIf(os.name != "nt", "real PTY test requires Windows ConPTY")
class SignedInputRuntimeTests(unittest.TestCase):
    def test_verified_input_reaches_a_real_conpty(self):
        marker = "SIGNED_INPUT_PTY_%d" % int(time.time() * 1000)
        loop = asyncio.new_event_loop()
        session = muxd.Session(
            "signed-input-runtime", "", tempfile.gettempdir(), 100, 30,
            loop, asyncio.Queue(), session_uuid=SESSION_UUID,
        )
        endpoint, private = make_endpoint()
        try:
            principal, body, refusal = muxd.authorize_relay_input(
                signed_frame(private, ("Write-Output '%s'\r" % marker).encode("utf-8")),
                session, endpoint=endpoint, now_ms=NOW_MS,
            )
            self.assertIsNone(refusal)
            self.assertIsNotNone(principal)
            ok, detail = session.write_confirmed(body)
            self.assertTrue(ok, detail)
            deadline = time.time() + 10
            while time.time() < deadline and marker not in session.tail_text():
                time.sleep(0.1)
            self.assertIn(marker, session.tail_text())
        finally:
            session.kill()
            loop.close()

    def test_session_uuid_survives_manifest_restore(self):
        original = muxd.Session(
            "uuid-runtime", "", tempfile.gettempdir(), 80, 24,
            None, None, spawn_now=False,
        )
        payload = muxd.session_records_payload({"uuid-runtime": original})
        restored = {}
        muxd.restore_manifest_sessions(payload, restored, None, None)
        self.assertEqual(restored["uuid-runtime"].session_uuid, original.session_uuid)
        self.assertNotEqual(restored["uuid-runtime"].session_uuid, original.session_id)


if __name__ == "__main__":
    unittest.main()
