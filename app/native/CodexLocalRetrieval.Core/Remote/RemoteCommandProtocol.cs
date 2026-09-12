using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexLocalRetrieval.Core.Remote;

// What a polled command is allowed to do before anything touches this PC.
public enum RemoteCommandAdmission
{
    Execute,    // gate passed; run it, then Record the outcome
    Refused,    // policy/envelope violation — ack as failed, NO side effect
    Duplicate,  // this intent was already delivered — replay the recorded ack, NO side effect
    Busy,       // still executing — no terminal acknowledgement
}

public sealed class RemoteCommandUnconfirmedException(string detail) : Exception(detail);

public static class RemoteCommandProtocol
{
    public static string LeaseOwner(string role)
    {
        var clean = new StringBuilder();
        foreach (var ch in Environment.MachineName)
            clean.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-');
        return $"{role}-{clean}-{Environment.ProcessId}";
    }

    public static string NewIntent(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}";

    public static bool IsReplaySafe(string? type, string? replayPolicy)
    {
        var expected = (type ?? "").Trim().ToLowerInvariant() switch
        {
            "kill" => "refused",
            "transcript" => "read-only",
            "transcriptfetch" => "read-only",
            "fetchfile" => "intent-fenced",
            "rename" => "idempotent",
            "setapptitle" => "idempotent",
            "setfavorite" or "setapptitle" or "archive" or "setphrases" or "settag" => "idempotent",
            "addtocollection" or "removefromcollection" => "idempotent",
            "deckcreate" or "collectioncreate" or "deckrename" or "collectionrename" or "collectionmove" or "deckdelete" or "collectiondelete" or "collectionrecover" or "collectionpurge" or "collectionempty" or "collectionsettag" or "collectionreorder" or "deckreorder" or "captureworkspace" or "checkpointcreate" or "checkpointrename" or "checkpointdelete" or "checkpointspawn" or "branchcreate" => "intent-fenced",
            "startmux" or "startchat" or "reclaim" => "intent-fenced",
            "mirrorlocal" => "intent-fenced",
            "cleartabhistory" => "idempotent",
            "settabcolor" => "idempotent",
            _ => "",
        };
        return expected.Length > 0
            && string.Equals(expected, replayPolicy, StringComparison.Ordinal);
    }

    // A correct replayPolicy proves the OPERATION's semantics; it proves nothing about the delivery being
    // current. "intent-fenced" means at-most-once, so those types additionally need a live lease token (this
    // delivery still owns the command) and a stable intent id (redeliveries are recognisable). Neither proves
    // WHO authorised the operation — that is the principal-proof layer, not this one.
    public static bool RequiresIntentEnvelope(string? type)
        => (type ?? "").Trim().ToLowerInvariant() is "fetchfile" or "startmux" or "startchat" or "reclaim" or "mirrorlocal"
            or "deckcreate" or "collectioncreate" or "deckrename" or "collectionrename"
            or "collectionmove" or "deckdelete" or "collectiondelete" or "collectionrecover" or "collectionpurge" or "collectionempty" or "collectionsettag" or "collectionreorder" or "deckreorder" or "captureworkspace" or "checkpointcreate" or "checkpointrename" or "checkpointdelete" or "checkpointspawn" or "branchcreate";

    // The relay's own canonical form for both ids (relay/server.js commandIntentId): it rejects anything
    // outside this charset before queueing, so a value that fails here never came from an honest lease.
    private static readonly Regex EnvelopeToken = new(@"^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);

    public static bool IsWellFormedEnvelopeToken(string? value)
        => !string.IsNullOrWhiteSpace(value) && EnvelopeToken.IsMatch(value);

    public static bool ValidateEnvelope(string? intentId, string? leaseToken, out string detail)
    {
        if (string.IsNullOrWhiteSpace(leaseToken))
        {
            detail = "command refused: intent-fenced commands require a lease token";
            return false;
        }
        if (!IsWellFormedEnvelopeToken(leaseToken))
        {
            detail = "command refused: the lease token is malformed";
            return false;
        }
        if (string.IsNullOrWhiteSpace(intentId))
        {
            detail = "command refused: intent-fenced commands require a stable intent id";
            return false;
        }
        if (!IsWellFormedEnvelopeToken(intentId))
        {
            detail = "command refused: the intent id is malformed";
            return false;
        }
        detail = "";
        return true;
    }

    // The complete stateless gate: replay semantics THEN currency/dedup envelope.
    public static bool TryAdmit(string? type, string? replayPolicy, string? intentId, string? leaseToken, out string detail)
    {
        if (!IsReplaySafe(type, replayPolicy))
        {
            detail = "command replay policy is missing or invalid";
            return false;
        }
        if (RequiresIntentEnvelope(type)) return ValidateEnvelope(intentId, leaseToken, out detail);
        detail = "";
        return true;
    }

    // The whole per-command gate a poller runs BEFORE any side effect. Both the GUI poller
    // (MainPage.Remote.cs) and the headless bridge (RemoteBridge.cs) drive exactly this, so its behaviour
    // IS the pollers' admission behaviour. Bounded so a long-lived poller cannot grow without limit.
    public sealed class IntentLedger
    {
        private const string InFlight = "duplicate delivery ignored: the original intent is still executing";

        private readonly int _capacity;
        private readonly object _sync = new();
        private readonly Dictionary<string, (bool ok, string detail)?> _outcomes = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public IntentLedger(int capacity = 512) => _capacity = capacity < 1 ? 1 : capacity;

        public RemoteCommandAdmission Admit(
            string? type,
            string? replayPolicy,
            string? intentId,
            string? leaseToken,
            out (bool ok, string detail) outcome)
        {
            if (!TryAdmit(type, replayPolicy, intentId, leaseToken, out var refusal))
            {
                outcome = (false, refusal);
                return RemoteCommandAdmission.Refused;
            }
            if (!RequiresIntentEnvelope(type))
            {
                outcome = (false, "");
                return RemoteCommandAdmission.Execute;
            }
            var key = intentId!.Trim();
            lock (_sync)
            {
                if (_outcomes.TryGetValue(key, out var prior))
                {
                    outcome = prior ?? (false, InFlight);
                    return prior.HasValue ? RemoteCommandAdmission.Duplicate : RemoteCommandAdmission.Busy;
                }
                if (_outcomes.Count >= _capacity)
                {
                    var completed = _order.FirstOrDefault(candidate => _outcomes[candidate].HasValue);
                    if (completed is null)
                    {
                        outcome = (false, "intent capacity is busy; retry delivery");
                        return RemoteCommandAdmission.Busy;
                    }
                    _outcomes.Remove(completed);
                    var retained = _order.Where(candidate => candidate != completed).ToArray();
                    _order.Clear();
                    foreach (var candidate in retained) _order.Enqueue(candidate);
                }
                _outcomes[key] = null;   // claimed, not yet completed
                _order.Enqueue(key);
            }
            outcome = (false, "");
            return RemoteCommandAdmission.Execute;
        }

        public void Release(string? intentId)
        {
            var key = (intentId ?? "").Trim();
            lock (_sync)
            {
                if (!_outcomes.TryGetValue(key, out var outcome) || outcome.HasValue) return;
                _outcomes.Remove(key);
                var retained = _order.Where(candidate => candidate != key).ToArray();
                _order.Clear();
                foreach (var candidate in retained) _order.Enqueue(candidate);
            }
        }

        // Record the terminal outcome so a redelivery replays the same ack instead of re-executing.
        public void Record(string? intentId, bool ok, string detail)
        {
            var key = (intentId ?? "").Trim();
            if (key.Length == 0) return;
            lock (_sync)
            {
                if (!_outcomes.ContainsKey(key)) return;
                _outcomes[key] = (ok, detail);
            }
        }
    }

    public static bool AckSucceeded(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
