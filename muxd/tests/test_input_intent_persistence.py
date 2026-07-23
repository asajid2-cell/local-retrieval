"""A durable input intent has to survive muxd's own manifest save/load.

The record `execute_durable_input_intent` persists is not free-form: `valid_manifest` validates
the WHOLE manifest as one unit, so a single intent record that trips `valid_intent_records`
discards every persisted session too. These tests build the exact terminal record that endpoint
writes for a principal-bound intent and push it through the round trip muxd actually performs —
validate, JSON-encode, compact, reload, replay.
"""

import importlib
import json
import unittest

host_input_intent = importlib.import_module("host_input_intent")
muxd = importlib.import_module("muxd")

SESSION_UUID = "6f1c2d3e-aaaa-4bbb-8ccc-000000000001"
PRINCIPAL = "principal-alice"
KEY_ID = "key-1"
INTENT_ID = "intent-1"
BODY_SHA256 = "e" * 64
NOW = 1_700_000_000.0

# The widest key `principal:<pid>:input:<intentId>` can get: both halves at 128 characters of the
# charset a principal id and an intent id are allowed to use.
WIDE = ("aZ9._-" * 22)[:128]


def make_principal(principal_id=PRINCIPAL, intent_id=INTENT_ID, body_sha256=BODY_SHA256):
    return host_input_intent.VerifiedPrincipal(
        principal_id=principal_id,
        key_id=KEY_ID,
        session_uuid=SESSION_UUID,
        channel_id="chan-7",
        intent_id=intent_id,
        seq=1,
        acl_revision=1,
        lease_epoch=1,
        issued_at_ms=1_700_000_000_000,
        expires_at_ms=1_700_000_005_000,
        body_sha256=body_sha256,
        roles=("drive",),
    )


def terminal_record(principal, status="completed", result=None, created=NOW, updated=NOW):
    """Exactly what execute_durable_input_intent persists on the settle path (muxd.py:2731-2747)."""
    record = {
        "kind": "input",
        "session": "work",
        "fingerprint": principal.intent_fingerprint,
        "status": status,
        "result": {"t": "input-ok", "s": "work"} if result is None else result,
        "createdAt": created,
        "updatedAt": updated,
    }
    record["principal"] = principal.journal_record(status)
    return record


def keyed(principal):
    return muxd.intent_key(principal.intent_scope, "input", principal.intent_id)


class DurableInputIntentPersistence(unittest.TestCase):
    def test_terminal_record_passes_manifest_validation(self):
        principal = make_principal()
        key = keyed(principal)
        self.assertEqual(key, "principal:%s:input:%s" % (PRINCIPAL, INTENT_ID))
        self.assertTrue(muxd.valid_intent_records({key: terminal_record(principal)}))

    def test_widest_possible_key_passes_manifest_validation(self):
        self.assertEqual(len(WIDE), 128)
        principal = make_principal(principal_id=WIDE, intent_id=WIDE)
        key = keyed(principal)
        self.assertEqual(key, "principal:%s:input:%s" % (WIDE, WIDE))
        self.assertTrue(muxd.valid_intent_records({key: terminal_record(principal)}))

    def test_whole_manifest_survives_the_intent_record(self):
        """A bad intent record rejects the sessions too — assert the good one does not."""
        principal = make_principal(principal_id=WIDE, intent_id=WIDE)
        manifest = {
            "version": 2,
            "sessions": {"work": {"cmd": "bash", "cwd": "/tmp", "cols": 80, "rows": 24, "heal": True}},
            "intents": {keyed(principal): terminal_record(principal)},
        }
        self.assertTrue(muxd.valid_manifest(manifest))

    def test_record_is_plain_json(self):
        principal = make_principal(principal_id=WIDE, intent_id=WIDE)
        record = terminal_record(principal)
        self.assertEqual(json.loads(json.dumps(record)), record)
        self.assertEqual(json.loads(json.dumps(record))["principal"], principal.journal_record("completed"))

    def test_compaction_keeps_a_freshly_settled_completed_record(self):
        principal = make_principal()
        kept = muxd.compact_intent_records(
            {keyed(principal): terminal_record(principal)}, now=NOW + 1.0
        )
        self.assertIn(keyed(principal), kept)
        self.assertEqual(kept[keyed(principal)]["status"], "completed")

    def test_compaction_never_forgets_an_uncertain_record(self):
        """Forgetting an uncertain outcome silently converts 'refuse' into 'write it again'."""
        principal = make_principal()
        record = terminal_record(principal, status="uncertain", result={}, updated=NOW)
        ancient = NOW + muxd.INTENT_TERMINAL_RETENTION_SECONDS * 1000 + 86_400
        kept = muxd.compact_intent_records({keyed(principal): record}, now=ancient)
        self.assertEqual(kept, {keyed(principal): record})

    def test_replay_decision_after_a_json_round_trip(self):
        principal = make_principal(principal_id=WIDE, intent_id=WIDE)
        fingerprint = principal.intent_fingerprint
        record = terminal_record(principal)
        reloaded = json.loads(json.dumps({keyed(principal): record}))[keyed(principal)]

        disposition, result = host_input_intent.replay_decision(reloaded, fingerprint)
        self.assertEqual(disposition, host_input_intent.REPLAY_CACHED)
        self.assertEqual(result, {"t": "input-ok", "s": "work"})

        conflicted, _ = host_input_intent.replay_decision(reloaded, "f" * 64)
        self.assertEqual(conflicted, host_input_intent.REPLAY_CONFLICT)

    def test_reloaded_uncertain_record_still_refuses(self):
        principal = make_principal()
        record = terminal_record(principal, status="uncertain", result={})
        reloaded = json.loads(json.dumps(record))
        self.assertTrue(muxd.valid_intent_records({keyed(principal): reloaded}))
        disposition, refusal = host_input_intent.replay_decision(reloaded, principal.intent_fingerprint)
        self.assertEqual(disposition, host_input_intent.REPLAY_UNCERTAIN)
        self.assertEqual(refusal["t"], "err")


if __name__ == "__main__":
    unittest.main()
