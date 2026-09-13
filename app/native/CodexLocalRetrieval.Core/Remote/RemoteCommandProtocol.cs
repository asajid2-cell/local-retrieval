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
    // What each native consumer advertises it can execute. The relay routes a queued command only to a
    // leased consumer whose commandTypes advertisement covers it, so these lists are a wire contract: a
    // type omitted here is never delivered. They mirror the real dispatcher switches (MainPage.Remote.cs
    // for GUI, RemoteBridge.cs for headless) — do not advertise a type the switch cannot execute.
    public static readonly IReadOnlyList<string> GuiCommandTypes =
    [
        "kill", "transcript", "transcriptfetch", "fetchfile", "rename", "setapptitle",
        "addtocollection", "startmux", "mirrorlocal", "startchat", "reclaim", "branchcreate",
        "captureworkspace", "checkpointcreate", "checkpointspawn", "checkpointrename",
        "checkpointdelete", "archive", "removefromcollection", "setfavorite", "setphrases",
        "settag", "collectioncreate", "collectionrename", "collectiondelete", "collectionmove",
        "collectionsettag", "collectionreorder", "collectionrecover", "collectionpurge",
        "collectionempty", "deckcreate", "deckrename", "deckdelete", "deckreorder",
        "cleartabhistory", "settabcolor",
    ];

    // The headless bridge owns an ArchiveService too, so it can run the same store-backed operations the
    // GUI can except the two tab-presentation setters, which only exist on an open desktop tab.
    public static readonly IReadOnlyList<string> HeadlessCommandTypes =
    [
        "kill", "transcript", "transcriptfetch", "fetchfile", "rename", "setapptitle",
        "addtocollection", "startmux", "mirrorlocal", "startchat", "reclaim", "branchcreate",
        "captureworkspace", "checkpointcreate", "checkpointspawn", "checkpointrename",
        "checkpointdelete", "archive", "removefromcollection", "setfavorite", "setphrases",
        "settag", "collectioncreate", "collectionrename", "collectiondelete", "collectionmove",
        "collectionsettag", "collectionreorder", "collectionrecover", "collectionpurge",
        "collectionempty", "deckcreate", "deckrename", "deckdelete", "deckreorder",
    ];

    public static string LeaseOwner(string role, string? principalInstanceId = null)
    {
        var clean = new StringBuilder();
        foreach (var ch in principalInstanceId ?? Environment.MachineName)
            clean.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-');
        return $"{role}-{clean}-{Environment.ProcessId}";
    }

    // A mux "create" response is the only place the relay learns whether a durable mux session exists.
    // Anything that is not an explicit "created" (or an explicit, non-uncertain "err") is UNKNOWN, and
    // an unknown create must be reconciled against muxd before a retry — never retried blind.
    public static (bool ok, string detail) ParseMuxCreateResponse(string response)
    {
        const string uncertain = "mux start outcome uncertain: authoritative reconciliation is required before retry";
        try
        {
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var type)) return (false, uncertain);
            if (type.GetString() == "created") return (true, "mux session created");
            if (type.GetString() != "err") return (false, uncertain);
            if (root.TryGetProperty("uncertain", out var uncertainty) && uncertainty.ValueKind == JsonValueKind.True)
                return (false, uncertain);
            return (false, root.TryGetProperty("m", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? "muxd error" : "muxd error");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return (false, uncertain);
        }
    }

    // The launch crossed its durable boundary (the mux session is created) but the authoritative filing of
    // the resulting chat is not yet confirmed. This is NOT a failure: the relay must retry it, and the
    // consumer reconciles rather than re-launching.
    public const string StartChatFilingPending = "startchat launch confirmed; authoritative filing is pending";

    public static bool IsPendingStartChatFiling(string? type, (bool ok, string detail) result)
        => type == "startchat" && !result.ok && result.detail == StartChatFilingPending;

    public static bool IsUncertainOutcome((bool ok, string detail) result)
        => !result.ok
           && result.detail.Contains("outcome uncertain:", StringComparison.OrdinalIgnoreCase)
           && result.detail.Contains("authoritative reconciliation is required", StringComparison.OrdinalIgnoreCase);

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
