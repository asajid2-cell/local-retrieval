"""Durable input-intent authorization for the muxd relay host link.

The relay is an untrusted conduit. It may observe, drop, reorder, and replay frames, so a
`t:"i"` frame arriving over the host link proves only that *the relay* asked for a PTY write.
This module turns that frame into a principal-authorized `input.durable` operation under the
`principalAuthV1` contract: a bounded schema check, then the exact `bodyB64` bytes hashed
before anything parses them, then expiry/session/ACL/lease checks, and only last the P-256
signature over the canonical transcript.

Two things this module deliberately does NOT do:

* It is not the authority. muxd's principal endpoint (`PrincipalEndpoint`) owns the registry,
  the ACL revision, and the lease epoch; this module only asks it. An endpoint with no
  principals refuses everything, which is the correct posture before pairing has happened.
* It does not treat `replayPolicy`, `leaseToken`, or `intentId` as authorization. Those fields
  describe operation semantics and work distribution. A correct `replayPolicy` says what the
  operation means if it runs twice; it says nothing about who was allowed to run it.

Refusals never reach the PTY: the caller gets a refusal object instead of bytes, so a rejected
frame performs zero writes.
"""

import base64
import binascii
import hashlib
import json
import os
import re

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec, utils as asym_utils

TRANSCRIPT_VERSION = "mux-authz-v1"
OP_INPUT_DURABLE = "input.durable"

# Refusal codes. `principal-proof-required` is reserved for "no usable proof was presented at
# all" so a client can tell "you must sign this" apart from "your proof was rejected".
PROOF_REQUIRED = "principal-proof-required"
PROOF_INVALID = "principal-proof-invalid"
PROOF_EXPIRED = "principal-proof-expired"
SESSION_MISMATCH = "principal-session-mismatch"
NOT_AUTHORIZED = "principal-not-authorized"
ACL_STALE = "principal-acl-stale"
LEASE_STALE = "principal-lease-stale"
LEASE_HELD = "principal-lease-held"

# Normal interactive expiry is 5s; 15s is the hard protocol maximum. A longer window is not a
# generous client, it is a replay window, so it is refused rather than clamped.
MAX_PROOF_LIFETIME_MS = 15_000
MAX_CLOCK_SKEW_MS = 2_000
MAX_BODY_BYTES = 64 * 1024

_ID_RE = re.compile(r"\A[A-Za-z0-9._-]{1,128}\Z")
_HEX32_RE = re.compile(r"\A[0-9a-f]{64}\Z")

# Staged rollout knob owned by the trust migration leaf. This module's endpoint always refuses a
# proofless frame; the mode only decides whether muxd's call site is still allowed to fall back
# to the legacy unsigned write while relays are being migrated.
def authz_mode(env=None):
    source = os.environ if env is None else env
    mode = str(source.get("MUX_AUTHZ_MODE", "audit") or "audit").strip().lower()
    return mode if mode in ("audit", "enforce") else "audit"


class PrincipalRefusal:
    """A refusal carries a stable machine code plus a human detail. It is never an exception:
    refusing input is ordinary operation, not an error path."""

    __slots__ = ("code", "detail", "intent_id")

    def __init__(self, code, detail, intent_id=""):
        self.code = code
        self.detail = detail
        self.intent_id = intent_id

    def frame(self, session_name=""):
        out = {"t": "err", "code": self.code, "m": self.detail}
        if session_name:
            out["s"] = session_name
        if self.intent_id:
            out["intentId"] = self.intent_id
        return out

    def __repr__(self):
        return "PrincipalRefusal(%r, %r)" % (self.code, self.detail)


class VerifiedPrincipal:
    """The identity a verified `input.durable` envelope proves. Replaces `scope="relay"` when it
    reaches `execute_input_intent`: the intent namespace and the replay fingerprint both derive
    from the principal, so two principals cannot collide on an intent id and a replay is only
    "the same operation" when the same key authorized the same bytes for the same session."""

    __slots__ = (
        "principal_id", "key_id", "session_uuid", "channel_id", "intent_id",
        "seq", "acl_revision", "lease_epoch", "issued_at_ms", "expires_at_ms",
        "body_sha256", "roles",
    )

    def __init__(self, **fields):
        for name in self.__slots__:
            setattr(self, name, fields.get(name))

    @property
    def intent_scope(self):
        return "principal:" + str(self.principal_id)

    @property
    def intent_fingerprint(self):
        """Binds (principalId, keyId, sessionUuid, intentId, bodySha256) — the tuple the contract
        says the execution endpoint persists. Deliberately NOT the whole frame: re-signing after
        a reconnect changes issuedAtMs, and that must not read as a different payload."""
        material = "\x1f".join([
            TRANSCRIPT_VERSION, OP_INPUT_DURABLE,
            str(self.principal_id), str(self.key_id), str(self.session_uuid),
            str(self.intent_id), str(self.body_sha256),
        ])
        return hashlib.sha256(material.encode("utf-8")).hexdigest()

    def journal_record(self, outcome=""):
        return {
            "principalId": self.principal_id,
            "keyId": self.key_id,
            "sessionUuid": self.session_uuid,
            "intentId": self.intent_id,
            "bodySha256": self.body_sha256,
            "outcome": outcome,
        }


class PrincipalEndpoint:
    """muxd-side principal registry. The trust prong's registry leaf replaces the storage; the
    lookup shape is the seam. Empty means "nobody is provisioned", which refuses everything."""

    def __init__(self, instance_id="", principals=None):
        self.instance_id = str(instance_id or "")
        self._principals = dict(principals or {})

    def register(self, principal_id, key_id, public_key, *, session_uuid, roles=("drive",),
                 acl_revision=1, lease_epoch=1, lease_holder="", status="active"):
        self._principals[(principal_id, key_id)] = {
            "publicKey": public_key,
            "status": status,
            "sessions": {
                session_uuid: {
                    "roles": set(roles),
                    "aclRevision": int(acl_revision),
                    "leaseEpoch": int(lease_epoch),
                    "leaseHolder": str(lease_holder or ""),
                }
            },
        }

    def lookup(self, principal_id, key_id, session_uuid):
        record = self._principals.get((principal_id, key_id))
        if record is None:
            return None
        grant = (record.get("sessions") or {}).get(session_uuid)
        return {
            "publicKey": record.get("publicKey"),
            "status": record.get("status", "active"),
            "grant": dict(grant) if grant else None,
        }


def canonical_transcript_bytes(op, instance_id, principal_id, key_id, session_uuid, channel_id,
                               seq, lease_epoch, acl_revision, issued_at_ms, expires_at_ms,
                               intent_id, body_sha256):
    """The exact bytes both runtimes sign. A fixed positional array of bounded ASCII ids and
    integers — never a re-serialized arbitrary object, so the relay cannot change the meaning by
    reordering keys or re-encoding numbers."""
    array = [
        TRANSCRIPT_VERSION, op, instance_id, principal_id, key_id, session_uuid, channel_id,
        int(seq), int(lease_epoch), int(acl_revision), int(issued_at_ms), int(expires_at_ms),
        intent_id, body_sha256,
    ]
    return json.dumps(array, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def _bounded_id(value):
    text = str(value or "")
    return text if _ID_RE.match(text) else ""


def _bounded_int(value):
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return value


def verify_signature(public_key, signed_bytes, signature):
    """P-256 / SHA-256 with a pinned 64-byte r||s wire encoding. DER is not accepted: one
    accepted encoding means one signature per (key, message), which keeps replay detection exact."""
    if public_key is None or len(signature) != 64:
        return False
    r = int.from_bytes(signature[:32], "big")
    s = int.from_bytes(signature[32:], "big")
    if r == 0 or s == 0:
        return False
    try:
        public_key.verify(
            asym_utils.encode_dss_signature(r, s),
            signed_bytes,
            ec.ECDSA(hashes.SHA256()),
        )
    except (InvalidSignature, ValueError, TypeError):
        return False
    return True


# Durable-replay dispositions. The execution endpoint persists (principal, fingerprint, outcome)
# and this decides what a second arrival of the same intent id means.
REPLAY_FRESH = "fresh"
REPLAY_CACHED = "cached"
REPLAY_CONFLICT = "conflict"
REPLAY_UNCERTAIN = "uncertain"


def replay_decision(record, fingerprint):
    """Decide what to do with an intent id that already has a persisted record.

    Returns `(disposition, result_frame)`; `result_frame` is None only for REPLAY_FRESH, meaning
    the caller must actually perform the write. The uncertain case is the important one: if the
    outcome was never durably observed, muxd refuses rather than guessing. Re-writing bytes that
    may already have reached the PTY is worse than an honest refusal — the client can re-key the
    intent, but nobody can un-type a command.
    """
    if not record:
        return REPLAY_FRESH, None
    if record.get("fingerprint") != fingerprint:
        return REPLAY_CONFLICT, {"t": "err", "m": "intent id is already bound to a different payload"}
    if record.get("status") in ("completed", "failed"):
        return REPLAY_CACHED, dict(record.get("result") or {})
    return REPLAY_UNCERTAIN, {
        "t": "err",
        "m": "input outcome is uncertain; the PTY write was not replayed",
    }


def session_uuid_of(session):
    """The contract's sessionUuid is muxd-generated and immutable. `session_id` is the agent
    transcript/resume identity and is explicitly NOT it — binding proofs to that would let a
    resumed agent inherit another session's authorization."""
    return str(getattr(session, "session_uuid", "") or "")


def verify_host_input_frame(frame, *, endpoint, session, now_ms):
    """Verify one relay host-link `t:"i"` frame as an `input.durable` operation.

    Returns `(VerifiedPrincipal, body_bytes, None)` on success or `(None, None, refusal)`.
    Checks run cheapest-and-most-bounded first so a hostile relay cannot make muxd do
    signature math over unbounded junk, and expiry is settled before any crypto.
    """
    if not isinstance(frame, dict):
        return None, None, PrincipalRefusal(PROOF_REQUIRED, "input frame is not an object")

    auth = frame.get("auth")
    if not isinstance(auth, dict):
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED,
            "input requires a signed input.durable proof; the relay is not an authority",
        )

    if str(auth.get("op", "")) != OP_INPUT_DURABLE:
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED, "unsupported input proof operation: " + str(auth.get("op", ""))[:64]
        )

    intent_id = _bounded_id(auth.get("intentId"))
    fields = {}
    for name in ("principalId", "keyId", "sessionUuid", "channelId", "instanceId"):
        value = _bounded_id(auth.get(name))
        if not value:
            return None, None, PrincipalRefusal(
                PROOF_REQUIRED, "input proof field is missing or malformed: " + name, intent_id
            )
        fields[name] = value
    if not intent_id:
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED, "input proof field is missing or malformed: intentId"
        )
    fields["intentId"] = intent_id

    for name in ("seq", "aclRevision", "leaseEpoch", "issuedAtMs", "expiresAtMs"):
        number = _bounded_int(auth.get(name))
        if number is None or number < 0:
            return None, None, PrincipalRefusal(
                PROOF_REQUIRED, "input proof field is not a non-negative integer: " + name,
                intent_id,
            )
        fields[name] = number

    body_sha256 = str(auth.get("bodySha256", "") or "")
    if not _HEX32_RE.match(body_sha256):
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED, "input proof body digest is not a sha256 hex digest", intent_id
        )

    try:
        signature = base64.b64decode(str(auth.get("sig", "") or ""), validate=True)
    except (binascii.Error, ValueError):
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED, "input proof signature is not valid base64", intent_id
        )
    if len(signature) != 64:
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED, "input proof signature is not a 64-byte r||s value", intent_id
        )

    # Exact transported bytes. `bodyB64` is required even though the legacy frame carried `d`:
    # the signature covers the digest of these bytes, so accepting a second body field would
    # reintroduce exactly the ambiguity the digest exists to remove.
    raw_body = auth.get("bodyB64")
    if raw_body is None:
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED, "signed input must carry its payload as bodyB64", intent_id
        )
    try:
        body = base64.b64decode(str(raw_body), validate=True)
    except (binascii.Error, ValueError):
        return None, None, PrincipalRefusal(PROOF_INVALID, "input body is not valid base64", intent_id)
    if not body:
        return None, None, PrincipalRefusal(PROOF_INVALID, "input body is empty", intent_id)
    if len(body) > MAX_BODY_BYTES:
        return None, None, PrincipalRefusal(PROOF_INVALID, "input body exceeds the frame limit", intent_id)

    # Hash the bytes before anything parses them.
    if hashlib.sha256(body).hexdigest() != body_sha256:
        return None, None, PrincipalRefusal(
            PROOF_INVALID, "signed body digest does not match the transmitted bytes", intent_id
        )

    issued_at = fields["issuedAtMs"]
    expires_at = fields["expiresAtMs"]
    if expires_at <= issued_at:
        return None, None, PrincipalRefusal(PROOF_EXPIRED, "input proof expiry precedes its capture time", intent_id)
    if expires_at - issued_at > MAX_PROOF_LIFETIME_MS:
        return None, None, PrincipalRefusal(
            PROOF_EXPIRED, "input proof lifetime exceeds the 15s protocol maximum", intent_id
        )
    if now_ms >= expires_at:
        return None, None, PrincipalRefusal(PROOF_EXPIRED, "input proof has expired", intent_id)
    if issued_at > now_ms + MAX_CLOCK_SKEW_MS:
        return None, None, PrincipalRefusal(PROOF_EXPIRED, "input proof is dated in the future", intent_id)

    bound_uuid = session_uuid_of(session)
    if not bound_uuid:
        return None, None, PrincipalRefusal(
            PROOF_REQUIRED, "session has no durable uuid; principal binding is unavailable", intent_id
        )
    if fields["sessionUuid"] != bound_uuid:
        return None, None, PrincipalRefusal(
            SESSION_MISMATCH, "input proof was signed for a different session", intent_id
        )

    if endpoint is None or fields["instanceId"] != str(getattr(endpoint, "instance_id", "") or ""):
        return None, None, PrincipalRefusal(
            PROOF_INVALID, "input proof names a different muxd instance", intent_id
        )

    record = endpoint.lookup(fields["principalId"], fields["keyId"], bound_uuid)
    if not record:
        return None, None, PrincipalRefusal(PROOF_INVALID, "unknown principal or key", intent_id)
    if str(record.get("status", "")) != "active":
        return None, None, PrincipalRefusal(PROOF_INVALID, "principal key is not active", intent_id)

    grant = record.get("grant")
    if not grant or "drive" not in set(grant.get("roles") or ()):
        return None, None, PrincipalRefusal(
            NOT_AUTHORIZED, "principal does not hold drive on this session", intent_id
        )
    if fields["aclRevision"] != int(grant.get("aclRevision", -1)):
        return None, None, PrincipalRefusal(ACL_STALE, "input proof cites a stale acl revision", intent_id)
    if fields["leaseEpoch"] != int(grant.get("leaseEpoch", -1)):
        return None, None, PrincipalRefusal(LEASE_STALE, "input proof cites a stale lease epoch", intent_id)
    holder = str(grant.get("leaseHolder") or "")
    if holder and holder != fields["channelId"]:
        return None, None, PrincipalRefusal(LEASE_HELD, "another channel holds the write lease", intent_id)

    signed = canonical_transcript_bytes(
        OP_INPUT_DURABLE, fields["instanceId"], fields["principalId"], fields["keyId"],
        bound_uuid, fields["channelId"], fields["seq"], fields["leaseEpoch"],
        fields["aclRevision"], issued_at, expires_at, intent_id, body_sha256,
    )
    if not verify_signature(record.get("publicKey"), signed, signature):
        return None, None, PrincipalRefusal(PROOF_INVALID, "input proof signature did not verify", intent_id)

    principal = VerifiedPrincipal(
        principal_id=fields["principalId"], key_id=fields["keyId"], session_uuid=bound_uuid,
        channel_id=fields["channelId"], intent_id=intent_id, seq=fields["seq"],
        acl_revision=fields["aclRevision"], lease_epoch=fields["leaseEpoch"],
        issued_at_ms=issued_at, expires_at_ms=expires_at, body_sha256=body_sha256,
        roles=frozenset(grant.get("roles") or ()),
    )
    return principal, body, None
