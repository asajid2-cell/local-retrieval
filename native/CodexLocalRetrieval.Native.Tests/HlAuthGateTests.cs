using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The optional hl-auth (SSO) gate decision. It must fail CLOSED — an unreachable or confusing auth
// service never grants access — and honor the per-page canOpen flag from /auth/api/access.
[TestClass]
public sealed class HlAuthGateTests
{
    private static string Access(bool authed, params (string id, bool canOpen)[] pages)
    {
        var ps = string.Join(",", pages.Select(p => $"{{\"id\":\"{p.id}\",\"path_prefix\":\"/{p.id}\",\"access\":\"restricted\",\"canOpen\":{(p.canOpen ? "true" : "false")}}}"));
        return $"{{\"authenticated\":{(authed ? "true" : "false")},\"pages\":[{ps}]}}";
    }

    [TestMethod]
    public void Evaluate_AuthedAndCanOpen_Allows()
        => Assert.AreEqual(GateOutcome.Allow, HlAuthGate.Evaluate(Access(true, ("remote", true)), "remote"));

    [TestMethod]
    public void Evaluate_AuthedButCannotOpen_Forbids()
        => Assert.AreEqual(GateOutcome.Forbid, HlAuthGate.Evaluate(Access(true, ("remote", false)), "remote"));

    [TestMethod]
    public void Evaluate_NotAuthenticated_SendsToLogin()
        => Assert.AreEqual(GateOutcome.Login, HlAuthGate.Evaluate(Access(false, ("remote", false)), "remote"));

    [TestMethod]
    public void Evaluate_NoRequiredPage_JustNeedsSession()
        => Assert.AreEqual(GateOutcome.Allow, HlAuthGate.Evaluate(Access(true), null));

    [TestMethod]
    public void Evaluate_PageNotRegistered_Forbids()
        => Assert.AreEqual(GateOutcome.Forbid, HlAuthGate.Evaluate(Access(true, ("other", true)), "remote"));

    [TestMethod]
    public void Evaluate_MalformedJson_FailsClosed()
        => Assert.AreEqual(GateOutcome.Forbid, HlAuthGate.Evaluate("not json", "remote"));

    [TestMethod]
    public async Task Check_NoCookie_SendsToLogin()
    {
        var gate = new HlAuthGate((_, _) => Task.FromResult<string?>(Access(true, ("remote", true))), "remote");
        Assert.AreEqual(GateOutcome.Login, await gate.CheckAsync(""));
    }

    [TestMethod]
    public async Task Check_AuthServiceUnreachable_FailsClosed()
    {
        var nullFetch = new HlAuthGate((_, _) => Task.FromResult<string?>(null), "remote");
        Assert.AreEqual(GateOutcome.Forbid, await nullFetch.CheckAsync("hl_session=abc"));

        var throwFetch = new HlAuthGate((_, _) => throw new HttpRequestException("down"), "remote");
        Assert.AreEqual(GateOutcome.Forbid, await throwFetch.CheckAsync("hl_session=abc"));
    }

    [TestMethod]
    public async Task Check_ValidSessionWithAccess_Allows()
    {
        var gate = new HlAuthGate((cookie, _) =>
        {
            Assert.AreEqual("hl_session=abc", cookie); // the cookie is forwarded to the access call
            return Task.FromResult<string?>(Access(true, ("remote", true)));
        }, "remote");
        Assert.AreEqual(GateOutcome.Allow, await gate.CheckAsync("hl_session=abc"));
    }

    [TestMethod]
    public void OutcomeCache_ExpiresEntriesAndBoundsCardinality()
    {
        var now = DateTimeOffset.Parse("2026-07-10T00:00:00Z");
        var cache = new HlAuthOutcomeCache(3, TimeSpan.FromSeconds(30), () => now);

        cache.Set("a", GateOutcome.Allow);
        cache.Set("b", GateOutcome.Login);
        cache.Set("c", GateOutcome.Forbid);
        cache.Set("d", GateOutcome.Allow);

        Assert.AreEqual(3, cache.Count);
        Assert.IsFalse(cache.TryGet("a", out _));
        Assert.IsTrue(cache.TryGet("d", out var outcome));
        Assert.AreEqual(GateOutcome.Allow, outcome);

        now = now.AddSeconds(30);
        Assert.IsFalse(cache.TryGet("d", out _));
        Assert.AreEqual(0, cache.Count);
    }

    [TestMethod]
    public void OutcomeCache_ConcurrentWritersNeverExceedCapacity()
    {
        const int capacity = 64;
        var cache = new HlAuthOutcomeCache(capacity, TimeSpan.FromMinutes(1));

        Parallel.For(0, 10_000, i =>
        {
            cache.Set("cookie-" + i, (GateOutcome)(i % 3));
            cache.TryGet("cookie-" + Math.Max(0, i - 1), out _);
        });

        Assert.IsLessThanOrEqualTo(capacity, cache.Count);
    }
}
