"""A durable input intent has to survive muxd's own manifest save/load.

The record `execute_durable_input_intent` persists is not free-form: `valid_manifest` validates
the WHOLE manifest as one unit, so a single intent record that trips `valid_intent_records`
discards every persisted session too. These tests build the exact terminal record that endpoint
writes for a principal-bound intent and push it through the round trip muxd actually performs —
validate, JSON-encode, compact, reload, replay.
"""

import importlib
import json
import os
import shutil
import tempfile
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


def reserve_record(principal, created=NOW, updated=NOW):
    """Exactly what execute_durable_input_intent persists on the RESERVE path (muxd.py:2712-2724).

    This is the record actually on disk if muxd dies between reserving an intent and settling it —
    status "dispatching", empty result. execute_durable_input_intent never writes the literal
    "uncertain"; "dispatching" is the real crash-window record the durability story hangs on.
    """
    record = {
        "kind": "input",
        "session": "work",
        "fingerprint": principal.intent_fingerprint,
        "status": "dispatching",
        "result": {},
        "createdAt": created,
        "updatedAt": updated,
    }
    record["principal"] = principal.journal_record("dispatching")
    return record


def keyed(principal):
    return muxd.intent_key(principal.intent_scope, "input", principal.intent_id)


class DurableInputIntentPersistence(unittest.TestCase):
    def setUp(self):
        self.original_manifest = muxd.MANIFEST
        self.original_intent_records = dict(muxd.intent_records)
        self.original_sessions = dict(muxd.sessions)
        self.temp_dir = tempfile.mkdtemp()
        muxd.MANIFEST = os.path.join(self.temp_dir, "sessions.json")
        muxd.intent_records.clear()
        muxd.sessions.clear()

    def tearDown(self):
        muxd.MANIFEST = self.original_manifest
        muxd.intent_records.clear()
        muxd.intent_records.update(self.original_intent_records)
        muxd.sessions.clear()
        muxd.sessions.update(self.original_sessions)
        shutil.rmtree(self.temp_dir)

    def write_manifest(self, payload):
        with open(muxd.MANIFEST, "w", encoding="utf-8") as stream:
            stream.write(json.dumps(payload))

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

    def test_manifest_payload_keeps_wide_terminal_record(self):
        principal = make_principal(principal_id=WIDE, intent_id=WIDE)
        key = keyed(principal)
        now = muxd.time.time()
        expected = {key: terminal_record(principal, created=now, updated=now)}
        muxd.intent_records.update(expected)

        payload = muxd.manifest_payload()

        self.assertTrue(muxd.valid_manifest(payload))
        self.assertIn(key, payload["intents"])
        self.assertEqual(payload["intents"], expected)

    def test_manifest_round_trip_restores_wide_terminal_record_and_replay(self):
        principal = make_principal(principal_id=WIDE, intent_id=WIDE)
        key = keyed(principal)
        now = muxd.time.time()
        expected = {key: terminal_record(principal, created=now, updated=now)}
        muxd.intent_records.update(expected)
        payload = muxd.manifest_payload()
        self.write_manifest(payload)
        muxd.intent_records.clear()

        muxd.manifest_load()

        self.assertEqual(muxd.intent_records, expected)
        self.assertEqual(
            host_input_intent.replay_decision(
                muxd.intent_records[key], principal.intent_fingerprint
            ),
            (host_input_intent.REPLAY_CACHED, {"t": "input-ok", "s": "work"}),
        )

    def test_manifest_round_trip_restores_wide_reserve_record_and_replay(self):
        principal = make_principal(principal_id=WIDE, intent_id=WIDE)
        key = keyed(principal)
        expected = {key: reserve_record(principal)}
        expected_bytes = json.dumps(
            expected[key], sort_keys=True, separators=(",", ":")
        ).encode("utf-8")
        muxd.intent_records.update(expected)
        payload = muxd.manifest_payload()
        self.write_manifest(payload)
        muxd.intent_records.clear()

        muxd.manifest_load()

        self.assertEqual(muxd.intent_records, expected)
        self.assertEqual(
            json.dumps(
                muxd.intent_records[key], sort_keys=True, separators=(",", ":")
            ).encode("utf-8"),
            expected_bytes,
        )
        self.assertEqual(
            host_input_intent.replay_decision(
                muxd.intent_records[key], principal.intent_fingerprint
            )[0],
            host_input_intent.REPLAY_UNCERTAIN,
        )

    def test_manifest_load_rejects_invalid_intent_status(self):
        principal = make_principal()
        invalid_record = terminal_record(principal, status="donezo")
        payload = {
            "version": 2,
            "sessions": {
                "work": {
                    "cmd": "bash",
                    "cwd": "/tmp",
                    "cols": 80,
                    "rows": 24,
                    "heal": True,
                }
            },
            "intents": {keyed(principal): invalid_record},
        }
        self.write_manifest(payload)

        with self.assertRaisesRegex(OSError, "invalid persisted state shape"):
            muxd.manifest_load()

    def test_reserve_record_passes_manifest_validation(self):
        for principal in (make_principal(), make_principal(principal_id=WIDE, intent_id=WIDE)):
            self.assertTrue(muxd.valid_intent_records({keyed(principal): reserve_record(principal)}))

    def test_reserve_record_survives_the_whole_manifest(self):
        """One bad intent record rejects the sessions too — assert the dispatching one does not."""
        for principal in (make_principal(), make_principal(principal_id=WIDE, intent_id=WIDE)):
            manifest = {
                "version": 2,
                "sessions": {"work": {"cmd": "bash", "cwd": "/tmp", "cols": 80, "rows": 24, "heal": True}},
                "intents": {keyed(principal): reserve_record(principal)},
            }
            self.assertTrue(muxd.valid_manifest(manifest))

    def test_reserve_record_is_plain_json(self):
        """The principal sub-object from journal_record() must survive JSON or persistence throws."""
        for principal in (make_principal(), make_principal(principal_id=WIDE, intent_id=WIDE)):
            record = reserve_record(principal)
            self.assertEqual(
                record,
                {
                    "kind": "input",
                    "session": "work",
                    "fingerprint": principal.intent_fingerprint,
                    "status": "dispatching",
                    "result": {},
                    "createdAt": NOW,
                    "updatedAt": NOW,
                    "principal": principal.journal_record("dispatching"),
                },
            )
            self.assertEqual(json.loads(json.dumps(record)), record)

    def test_compaction_never_ages_out_a_reserve_record(self):
        """Forgetting an unsettled outcome silently converts an honest refusal into re-typing."""
        for principal in (make_principal(), make_principal(principal_id=WIDE, intent_id=WIDE)):
            record = reserve_record(principal)
            ancient = NOW + muxd.INTENT_TERMINAL_RETENTION_SECONDS * 10
            kept = muxd.compact_intent_records({keyed(principal): record}, now=ancient)
            self.assertEqual(kept, {keyed(principal): record})

    def test_reloaded_reserve_record_is_uncertain_and_conflict_aware(self):
        for principal in (make_principal(), make_principal(principal_id=WIDE, intent_id=WIDE)):
            reloaded = json.loads(json.dumps(reserve_record(principal)))
            disposition, frame = host_input_intent.replay_decision(reloaded, principal.intent_fingerprint)
            self.assertEqual(disposition, host_input_intent.REPLAY_UNCERTAIN)
            self.assertEqual(frame["t"], "err")
            self.assertEqual(
                host_input_intent.replay_decision(reloaded, "f" * 64)[0],
                host_input_intent.REPLAY_CONFLICT,
            )


if __name__ == "__main__":
    unittest.main()
