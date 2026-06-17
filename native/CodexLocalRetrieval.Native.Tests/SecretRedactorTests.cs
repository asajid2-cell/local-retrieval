using System.Text.Json;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The co-pilot sends archive snippets to a third-party model, so any credential that lives in an old
// chat must be masked before it leaves the machine. These prove the redactor masks the common secret
// shapes, leaves ordinary text alone, and is actually wired into the tool output (read_chat).
[TestClass]
public sealed class SecretRedactorTests
{
    // NOTE: every token below is synthetic ("FAKEexample…") and the provider prefix is split across a
    // `+` so no contiguous real-credential pattern exists in this source file — that keeps GitHub push
    // protection (and any scanner) from flagging the test fixtures, while the strings still assemble at
    // runtime to exercise the redactor's regexes.
    [DataTestMethod]
    [DataRow("here is my key " + "sk-" + "FAKEexampleKEYnotreal0000000 ok")]
    [DataRow("anthropic " + "sk-ant-" + "api03FAKEexampleKEYnotreal00 use it")]
    [DataRow("token " + "ghp_" + "FAKEexampleTOKENnotreal000000000000 use")]
    [DataRow("aws " + "AKIA" + "EXAMPLEFAKEKEY12 creds")]
    [DataRow("google " + "AIza" + "FAKEexampleGoogleKEYnotreal000000000000 key")]
    [DataRow("slack " + "xox" + "b-000000000000-FAKEexampletoken token")]
    [DataRow("header Authorization: Bearer " + "FAKEexampleBEARERtoken000000 done")]
    [DataRow("stripe " + "sk_" + "live_FAKEexampleSTRIPE0000000 use it")]
    [DataRow("jwt " + "eyJ" + "hbGciOiFAKE.eyJzdWIiOjEyMzQ1.SflKxwRJSMeKKF2QT4 token")]
    [DataRow("npm " + "npm_" + "FAKEexampleNPMtokennotreal0000000000 token")]
    public void Scrub_MasksKnownSecretShapes(string text)
    {
        var scrubbed = SecretRedactor.Scrub(text);
        StringAssert.Contains(scrubbed, SecretRedactor.Mask, $"expected a mask in: {scrubbed}");
        // the raw secret token must be gone
        var secret = text.Split(' ').First(w => w.Length > 20 || w.StartsWith("sk-") || w.StartsWith("AKIA") || w.StartsWith("xox") || w.StartsWith("ghp_"));
        StringAssert.DoesNotMatch(scrubbed, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(secret)));
    }

    [TestMethod]
    public void Scrub_MasksKeyValueSecretsButKeepsFieldName()
    {
        var scrubbed = SecretRedactor.Scrub("config: api_key = \"hunter2supersecret\" and password: p@ssw0rd-longenough");
        StringAssert.Contains(scrubbed, "api_key");          // field name preserved for context
        StringAssert.Contains(scrubbed, SecretRedactor.Mask); // value masked
        StringAssert.DoesNotMatch(scrubbed, new System.Text.RegularExpressions.Regex("hunter2supersecret"));
        StringAssert.DoesNotMatch(scrubbed, new System.Text.RegularExpressions.Regex("p@ssw0rd-longenough"));
    }

    // The common AWS secret-key form has the keyword buried in a snake_case compound — underscores
    // are NOT regex word boundaries, so a naive \bsecret\b rule would miss it.
    [TestMethod]
    public void Scrub_MasksAwsSecretAccessKeyCompound()
    {
        var scrubbed = SecretRedactor.Scrub("aws_secret_access_key = " + "FAKEexampleAWSsecretKEYnotreal000000");
        StringAssert.Contains(scrubbed, "aws_secret_access_key");
        StringAssert.Contains(scrubbed, SecretRedactor.Mask);
        StringAssert.DoesNotMatch(scrubbed, new System.Text.RegularExpressions.Regex("FAKEexampleAWSsecretKEYnotreal000000"));
    }

    // ...but an ordinary word that merely CONTAINS a keyword substring must not be redacted —
    // including with a colon after it (the path where the key=value rule actually engages).
    [TestMethod]
    public void Scrub_DoesNotRedact_InnocentWordContainingKeyword()
    {
        const string sentence = "Memo to the secretary: Johnson12 will file the tokenizer results today.";
        Assert.AreEqual(sentence, SecretRedactor.Scrub(sentence));
    }

    // A quoted secret can contain spaces; the value rule must consume through the closing quote
    // rather than stopping at the first space (which would leak the rest).
    [TestMethod]
    public void Scrub_MasksQuotedMultiWordSecret()
    {
        var scrubbed = SecretRedactor.Scrub("api_key = \"ab cd ef gh ij\"");
        StringAssert.Contains(scrubbed, "api_key");
        StringAssert.Contains(scrubbed, SecretRedactor.Mask);
        StringAssert.DoesNotMatch(scrubbed, new System.Text.RegularExpressions.Regex("ab cd ef gh ij"));
    }

    // The password inside a connection string / URL userinfo is masked, structure preserved.
    [TestMethod]
    public void Scrub_MasksConnectionStringPassword()
    {
        var scrubbed = SecretRedactor.Scrub("DATABASE_URL=postgres://dbuser:s3cr3tP4ss@db.internal:5432/app");
        StringAssert.Contains(scrubbed, SecretRedactor.Mask);
        StringAssert.DoesNotMatch(scrubbed, new System.Text.RegularExpressions.Regex("s3cr3tP4ss"));
        StringAssert.Contains(scrubbed, "db.internal"); // host kept, structure intact
    }

    [TestMethod]
    public void Scrub_MasksPemPrivateKeyBlock()
    {
        var pem = "before\n-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAA\nAAAAB\n-----END OPENSSH PRIVATE KEY-----\nafter";
        var scrubbed = SecretRedactor.Scrub(pem);
        StringAssert.Contains(scrubbed, SecretRedactor.Mask);
        StringAssert.Contains(scrubbed, "before");
        StringAssert.Contains(scrubbed, "after");
        StringAssert.DoesNotMatch(scrubbed, new System.Text.RegularExpressions.Regex("b3BlbnNzaC1rZXktdjEAAAA"));
    }

    [TestMethod]
    public void Scrub_LeavesOrdinaryTextUnchanged()
    {
        const string ordinary = "Let's refactor the renderer and fix the VENPOD walk benchmark by Tuesday.";
        Assert.AreEqual(ordinary, SecretRedactor.Scrub(ordinary));
    }

    [TestMethod]
    public void Scrub_HandlesNullAndEmpty()
    {
        Assert.AreEqual("", SecretRedactor.Scrub(null));
        Assert.AreEqual("", SecretRedactor.Scrub(""));
    }

    // Defence-in-depth, end to end: a secret pasted into a chat message must not appear in read_chat's
    // model-facing output. This is the surface that actually ships content to DeepSeek.
    [TestMethod]
    public async Task ReadChat_RedactsSecretsInModelOutput()
    {
        var store = Path.Combine(Path.GetTempPath(), "clr-redact-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var fakeKey = "sk-" + "FAKEexampleKEYnotreal0000000"; // synthetic, not a real key
            var svc = new ArchiveService(storePath: store);
            var session = new ArchiveSession { Id = "leaky", Title = "Deploy notes" };
            session.Messages.Add(new ArchiveMessage { Role = "user", Text = $"the prod key is {fakeKey}, don't lose it" });
            svc.Store.Sessions["leaky"] = session;

            var readChat = new ArchiveToolService(svc).Tools().First(t => t.Name == "read_chat");
            var args = JsonSerializer.Deserialize<JsonElement>("{\"id\":\"leaky\"}");
            var json = JsonSerializer.Serialize(await readChat.Execute(args, default));

            StringAssert.Contains(json, SecretRedactor.Mask);
            StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(fakeKey)));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }
}
