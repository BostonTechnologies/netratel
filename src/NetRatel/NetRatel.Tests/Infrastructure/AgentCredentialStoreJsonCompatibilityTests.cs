using System.Reflection;
using System.Text;
using AwesomeAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class AgentCredentialStoreJsonCompatibilityTests
{
    [Fact]
    public async Task CredentialWriterPreservesReleasedBytesAndExplicitNulls()
    {
        using var fixture = new StoreFixture();
        var input = CredentialCurrent[..^1] + ",\"FutureCredentialField\":true}";
        var original = Protect(fixture.Store, input, "CurrentProtection");
        await File.WriteAllBytesAsync(fixture.Path, original, TestContext.Current.CancellationToken);

        (await fixture.Store.LoadCredentialsAsync(TestContext.Current.CancellationToken)).Should().Be((Agent, Parent));
        (await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).Should().Be(DeviceKey);
        (await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken)).Should().Equal(original);

        await fixture.Store.SaveAsync(Agent, Parent, TestContext.Current.CancellationToken);
        (await Plaintext(fixture.Store, fixture.Path, "CurrentProtection")).Should().Be(CredentialCurrent);
        await fixture.Store.ClearRefreshCredentialsAsync();
        (await Plaintext(fixture.Store, fixture.Path, "CurrentProtection")).Should().Be(CredentialKeyOnly);
        (await fixture.Store.LoadCredentialsAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).Should().Be(DeviceKey);
    }

    [Fact]
    public async Task FrozenLegacyProtectionAndLocationMigrateWithoutChangingPayload()
    {
        using var fixture = new StoreFixture();
        var legacyPath = System.IO.Path.Combine(fixture.Directory, "legacy.dat");
        var legacy = OperatingSystem.IsWindows()
            ? Protect(fixture.Store, CredentialCurrent, "LegacyStoProtection")
            : Convert.FromBase64String(LegacyProtectedBase64);
        await File.WriteAllBytesAsync(legacyPath, legacy, TestContext.Current.CancellationToken);
        var store = NewStore(fixture.Path, legacyPath);

        (await store.LoadCredentialsAsync(TestContext.Current.CancellationToken)).Should().Be((Agent, Parent));
        (await store.LoadAsync(TestContext.Current.CancellationToken)).Should().Be(DeviceKey);
        File.Exists(legacyPath).Should().BeFalse();
        (await Plaintext(store, fixture.Path, "CurrentProtection")).Should().Be(CredentialCurrent);
        var migrated = await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken);
        (await NewStore(fixture.Path).LoadCredentialsAsync(TestContext.Current.CancellationToken)).Should().Be((Agent, Parent));
        (await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken)).Should().Equal(migrated);
    }

    [Fact]
    public async Task JournalWriterPreservesReleasedBytesAndCommittedSuccessorRecovery()
    {
        using var fixture = new StoreFixture();
        var parentBytes = Protect(fixture.Store, CredentialCurrent, "CurrentProtection");
        await File.WriteAllBytesAsync(fixture.Path, parentBytes, TestContext.Current.CancellationToken);
        var exchange = NewExchange();
        await fixture.Store.BeginRefreshExchangeAsync(exchange, TestContext.Current.CancellationToken);
        var journalPath = fixture.Path + ".native-refresh";
        (await Plaintext(fixture.Store, journalPath, "ExchangeProtection")).Should().Be(JournalPending);

        var futureJournal = JournalPending.Replace("\"Exchange\":{",
            "\"FutureJournalField\":true,\"Exchange\":{\"FutureExchangeField\":true,", StringComparison.Ordinal);
        var originalJournal = Protect(fixture.Store, futureJournal, "ExchangeProtection");
        await File.WriteAllBytesAsync(journalPath, originalJournal, TestContext.Current.CancellationToken);
        (await NewStore(fixture.Path).LoadPendingExchangeAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo(exchange);
        (await File.ReadAllBytesAsync(journalPath, TestContext.Current.CancellationToken)).Should().Equal(originalJournal);

        await fixture.Store.CompleteRefreshExchangeAsync(exchange, Successor, TestContext.Current.CancellationToken);
        (await Plaintext(fixture.Store, journalPath, "ExchangeProtection")).Should().Be(JournalCompleted);
        (await Plaintext(fixture.Store, fixture.Path, "CurrentProtection")).Should().Be(CredentialSuccessor);
        await File.WriteAllBytesAsync(fixture.Path, parentBytes, TestContext.Current.CancellationToken);
        var restarted = NewStore(fixture.Path);
        (await restarted.LoadCredentialsAsync(TestContext.Current.CancellationToken)).Should().Be((Agent, Successor));
        (await restarted.LoadAsync(TestContext.Current.CancellationToken)).Should().Be(DeviceKey);
        (await restarted.LoadPendingExchangeAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await Plaintext(restarted, fixture.Path, "CurrentProtection")).Should().Be(CredentialSuccessor);
    }

    [Fact]
    public async Task CredentialReaderRetainsCaseSensitiveNamesAndPreservesUnrecognizedBytes()
    {
        using var fixture = new StoreFixture();
        var input = CredentialCurrent.Replace("AgentId", "agentId", StringComparison.Ordinal)
            .Replace("RefreshToken", "refreshToken", StringComparison.Ordinal)
            .Replace("PublicKey", "publicKey", StringComparison.Ordinal)
            .Replace("PrivateKey", "privateKey", StringComparison.Ordinal)
            .Replace("KeyAlgorithm", "keyAlgorithm", StringComparison.Ordinal);
        var original = Protect(fixture.Store, input, "CurrentProtection");
        await File.WriteAllBytesAsync(fixture.Path, original, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => fixture.Store.LoadCredentialsAsync(TestContext.Current.CancellationToken));

        (await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken)).Should().Equal(original);
    }

    [Theory]
    [InlineData("case")]
    [InlineData("quoted-number")]
    public async Task JournalReaderRetainsCaseSensitiveNamesAndStrictNumbers(string variation)
    {
        using var fixture = new StoreFixture();
        var credentials = Protect(fixture.Store, CredentialCurrent, "CurrentProtection");
        await File.WriteAllBytesAsync(fixture.Path, credentials, TestContext.Current.CancellationToken);
        var input = variation == "case"
            ? JournalPending.Replace("\"Version\":1", "\"version\":1", StringComparison.Ordinal)
            : JournalPending.Replace("\"Version\":1", "\"Version\":\"1\"", StringComparison.Ordinal);
        var original = Protect(fixture.Store, input, "ExchangeProtection");
        var journalPath = fixture.Path + ".native-refresh";
        await File.WriteAllBytesAsync(journalPath, original, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => fixture.Store.LoadPendingExchangeAsync(TestContext.Current.CancellationToken));

        (await File.ReadAllBytesAsync(journalPath, TestContext.Current.CancellationToken)).Should().Equal(original);
        (await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken)).Should().Equal(credentials);
    }

    private const string Agent = "61427545-a59f-41b1-b96f-152981400b27";
    private const string Parent = "parent <é>&\"\\\n";
    private const string Successor = "successor <β>&\"\\\n";
    private static readonly AgentDeviceKeyMaterial DeviceKey = new("synthetic-public+/==", "synthetic-private <β>", "ecdsa-p256");

    private static PendingAgentRefreshExchange NewExchange() => new(1, Guid.Parse("78ad9117-0943-48a2-af7d-3a44b2ba194f"),
        Agent, Parent, "/QF0WnVovB4rymVT6F2Q43T0DXgyI9oj/6lcvsv1k/4=", ["netratel:connect", "scope:β", "scope:<>&"]);

    private static AgentCredentialStore NewStore(string path, string? legacyPath = null) =>
        new(path, legacyPath, () => "credential-json-fixture", () => false);

    private static byte[] Protect(AgentCredentialStore store, string plaintext, string purpose) =>
        Crypto(store, "ProtectForPurpose", Encoding.UTF8.GetBytes(plaintext), purpose);

    private static async Task<string> Plaintext(AgentCredentialStore store, string path, string purpose) =>
        Encoding.UTF8.GetString(Crypto(store, "Unprotect", await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken), purpose));

    private static byte[] Crypto(AgentCredentialStore store, string method, byte[] bytes, string purpose)
    {
        var profile = typeof(AgentCredentialStore).GetField(purpose, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        return (byte[])typeof(AgentCredentialStore).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [bytes, profile])!;
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netratel-store-json-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "agent.dat");
        public AgentCredentialStore Store { get; }

        public StoreFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = NewStore(Path);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    // Captured from the original private JSON writers at ad9eee91 before this change.
    // Keep released PascalCase/order/default escaping and explicit null fields.
    private const string CredentialCurrent = """{"AgentId":"61427545-a59f-41b1-b96f-152981400b27","RefreshToken":"parent \u003C\u00E9\u003E\u0026\u0022\\\n","PublicKey":"synthetic-public\u002B/==","PrivateKey":"synthetic-private \u003C\u03B2\u003E","KeyAlgorithm":"ecdsa-p256"}""";
    private const string CredentialKeyOnly = """{"AgentId":null,"RefreshToken":null,"PublicKey":"synthetic-public\u002B/==","PrivateKey":"synthetic-private \u003C\u03B2\u003E","KeyAlgorithm":"ecdsa-p256"}""";
    private const string CredentialSuccessor = """{"AgentId":"61427545-a59f-41b1-b96f-152981400b27","RefreshToken":"successor \u003C\u03B2\u003E\u0026\u0022\\\n","PublicKey":"synthetic-public\u002B/==","PrivateKey":"synthetic-private \u003C\u03B2\u003E","KeyAlgorithm":"ecdsa-p256"}""";
    private const string JournalPending = """{"Version":1,"Exchange":{"Version":1,"ExchangeId":"78ad9117-0943-48a2-af7d-3a44b2ba194f","AgentId":"61427545-a59f-41b1-b96f-152981400b27","ParentRefreshToken":"parent \u003C\u00E9\u003E\u0026\u0022\\\n","DeviceKeyHash":"/QF0WnVovB4rymVT6F2Q43T0DXgyI9oj/6lcvsv1k/4=","RequestedScopes":["netratel:connect","scope:\u03B2","scope:\u003C\u003E\u0026"]},"SuccessorRefreshToken":null}""";
    private const string JournalCompleted = """{"Version":1,"Exchange":{"Version":1,"ExchangeId":"78ad9117-0943-48a2-af7d-3a44b2ba194f","AgentId":"61427545-a59f-41b1-b96f-152981400b27","ParentRefreshToken":"parent \u003C\u00E9\u003E\u0026\u0022\\\n","DeviceKeyHash":"/QF0WnVovB4rymVT6F2Q43T0DXgyI9oj/6lcvsv1k/4=","RequestedScopes":["netratel:connect","scope:\u03B2","scope:\u003C\u003E\u0026"]},"SuccessorRefreshToken":"successor \u003C\u03B2\u003E\u0026\u0022\\\n"}""";
    // Actual old Unix STO ciphertext for the fixed synthetic machine; Windows DPAPI is user-bound.
    private const string LegacyProtectedBase64 = "2NvWHXdX01wKySZ5zc6U27/3wk4JKPoxYpReJP35wzwbIFy0p7mjTcBM4ReTFpBCo22UfjHak2Vim60TkXqQhM3cc+sXYNwZcNmoRFZ6cntIMOHV8M+oKvadsrZLamL/qQ7XNafmecdCTAW9nPOoG1CtwyJTSjYo1/PsYRLFiUWGjhrj91/TGS1krt5/iJwsqS7aanrmVNn/GyXaP7ONhH65na7k8wUXXAOoCDaRqR6qEXeNBeOctc/L3xjIGR+6SIS79s/XGBopcRniXlHl52kp7KnN1/Jf6WlNFSnOg8xu0w+pF3FlTOwhpka1C5qiYbG/I4kx/EhMrhUhpt6bAA==";
}
