using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Bootstrap;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class BootstrapStateStoreTests
{
    [Fact]
    public async Task Operator_status_does_not_create_an_installation_or_disclose_a_code()
    {
        await using var fixture = await BootstrapFixture.CreateAsync();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var missing = await BootstrapOperatorCommand.RunAsync(
            BootstrapOperatorCommand.Status, fixture.Options, output, error);
        missing.Should().Be(2);
        Directory.Exists(fixture.Options.StateDirectory).Should().BeFalse();

        await fixture.Store.LoadOrCreateAsync();
        var code = await File.ReadAllTextAsync(fixture.SetupProofPath);
        output.GetStringBuilder().Clear();
        (await BootstrapOperatorCommand.RunAsync(BootstrapOperatorCommand.Status, fixture.Options, output, error)).Should().Be(0);
        output.ToString().Should().Contain("Installation: Unconfigured").And.NotContain(code);
    }

    [Fact]
    public async Task Operator_rotation_invalidates_only_an_unclaimed_generated_code()
    {
        await using var fixture = await BootstrapFixture.CreateAsync();
        await fixture.Store.LoadOrCreateAsync();
        var original = await File.ReadAllTextAsync(fixture.SetupProofPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        (await BootstrapOperatorCommand.RunAsync(BootstrapOperatorCommand.ShowCode, fixture.Options, output, error)).Should().Be(0);
        output.ToString().Trim().Should().Be(original);
        output.GetStringBuilder().Clear();
        (await BootstrapOperatorCommand.RunAsync(BootstrapOperatorCommand.RotateCode, fixture.Options, output, error)).Should().Be(0);
        var replacement = await File.ReadAllTextAsync(fixture.SetupProofPath);
        replacement.Should().NotBe(original);
        (await fixture.Store.VerifySetupProofAsync(original)).Should().BeFalse();
        (await fixture.Store.VerifySetupProofAsync(replacement)).Should().BeTrue();

        (await fixture.Store.ClaimSetupAsync(replacement, "PostgreSQL", "ConnectionStrings:NetRatelDb")).Succeeded.Should().BeTrue();
        output.GetStringBuilder().Clear();
        (await BootstrapOperatorCommand.RunAsync(BootstrapOperatorCommand.RotateCode, fixture.Options, output, error)).Should().Be(4);
        (await BootstrapOperatorCommand.RunAsync(BootstrapOperatorCommand.ShowCode, fixture.Options, output, error)).Should().Be(4);
        output.ToString().Should().NotContain(replacement);
    }

    [Fact]
    public async Task Operator_rotation_never_overwrites_a_deployment_owned_proof()
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-operator-proof-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var proofPath = Path.Combine(root, "deployment-proof");
        const string proof = "deployment-owned-proof";
        await File.WriteAllTextAsync(proofPath, proof);
        try
        {
            var options = new BootstrapOptions { StateDirectory = Path.Combine(root, "bootstrap"), SetupProofPath = proofPath };
            await new BootstrapStateStore(options).LoadOrCreateAsync();
            using var output = new StringWriter();
            using var error = new StringWriter();

            (await BootstrapOperatorCommand.RunAsync(BootstrapOperatorCommand.RotateCode, options, output, error)).Should().Be(2);
            (await File.ReadAllTextAsync(proofPath)).Should().Be(proof);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Claim_is_single_use_across_concurrent_store_instances()
    {
        await using var fixture = await BootstrapFixture.CreateAsync();
        var initial = await fixture.Store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(fixture.SetupProofPath);

        (await File.ReadAllBytesAsync(fixture.SetupProofPath)).Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => new BootstrapStateStore(fixture.Options).ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb")));

        claims.Count(result => result.Succeeded).Should().Be(1);
        var descriptor = await fixture.Store.LoadOrCreateAsync();
        descriptor.State.Should().Be(BootstrapState.Configuring);
        descriptor.SetupProofHash.Should().BeEmpty();
        descriptor.OperationId.Should().NotBeNull();
        descriptor.SelectedProvider.Should().Be("PostgreSQL");
        (await fixture.Store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb")).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_key_material_enters_recovery_instead_of_reinitializing()
    {
        await using var fixture = await BootstrapFixture.CreateAsync();
        await fixture.Store.LoadOrCreateAsync();
        File.Delete(Path.Combine(fixture.Options.StateDirectory, "key-material-proof"));

        var descriptor = await fixture.Store.LoadOrCreateAsync();

        descriptor.State.Should().Be(BootstrapState.RecoveryRequired);
        File.Exists(Path.Combine(fixture.Options.StateDirectory, "descriptor.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Missing_descriptor_with_existing_journal_enters_recovery()
    {
        await using var fixture = await BootstrapFixture.CreateAsync();
        await fixture.Store.LoadOrCreateAsync();
        File.Delete(Path.Combine(fixture.Options.StateDirectory, "descriptor.json"));

        var descriptor = await fixture.Store.LoadOrCreateAsync();

        descriptor.State.Should().Be(BootstrapState.RecoveryRequired);
    }

    [Fact]
    public async Task Malformed_descriptor_with_retained_key_material_enters_recovery()
    {
        await using var fixture = await BootstrapFixture.CreateAsync();
        await fixture.Store.LoadOrCreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.StateDirectory, "descriptor.json"), "{");

        var descriptor = await fixture.Store.LoadOrCreateAsync();

        descriptor.State.Should().Be(BootstrapState.RecoveryRequired);
    }

    [Fact]
    public async Task Configured_but_unreachable_postgresql_enters_recovery()
    {
        await using var fixture = await BootstrapFixture.CreateAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:NetRatelDb"] = "Host=127.0.0.1;Port=1;Database=netratel;Username=netratel;Password=netratel;Timeout=1"
            })
            .Build();

        var descriptor = await new BootstrapLifecycleService(fixture.Store, configuration).InitializeAsync();

        descriptor.State.Should().Be(BootstrapState.RecoveryRequired);
    }

    [Fact]
    public async Task Expired_generated_proof_is_replaced_only_during_a_subsequent_startup()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await BootstrapFixture.CreateAsync(time);
        await fixture.Store.LoadOrCreateAsync();
        var originalProof = await File.ReadAllTextAsync(fixture.SetupProofPath);
        time.Advance(TimeSpan.FromHours(2));

        var descriptor = await fixture.Store.LoadOrCreateAsync();
        var renewedProof = await File.ReadAllTextAsync(fixture.SetupProofPath);

        descriptor.State.Should().Be(BootstrapState.Unconfigured);
        renewedProof.Should().NotBe(originalProof);
        descriptor.SetupProofExpiresAtUtc.Should().BeAfter(time.GetUtcNow());
    }

    [Fact]
    public async Task Expired_configuration_lease_enters_recovery_on_restart()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var fixture = await BootstrapFixture.CreateAsync(time);
        await fixture.Store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(fixture.SetupProofPath);
        (await fixture.Store.ClaimSetupAsync(proof, null, null)).Succeeded.Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(11));

        var descriptor = await fixture.Store.LoadOrCreateAsync();

        descriptor.State.Should().Be(BootstrapState.RecoveryRequired);
        descriptor.RecoveryReason.Should().Be("configuration-lease-expired");
    }

    private sealed class BootstrapFixture : IAsyncDisposable
    {
        private BootstrapFixture(string directory, BootstrapOptions options, BootstrapStateStore store)
        {
            Directory = directory;
            Options = options;
            Store = store;
        }

        public string Directory { get; }
        public BootstrapOptions Options { get; }
        public BootstrapStateStore Store { get; }
        public string SetupProofPath => Path.Combine(Options.StateDirectory, "setup-proof");

        public static Task<BootstrapFixture> CreateAsync(TimeProvider? timeProvider = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-tests", Guid.NewGuid().ToString("N"));
            var options = new BootstrapOptions { StateDirectory = directory, SetupProofLifetime = TimeSpan.FromHours(1) };
            return Task.FromResult(new BootstrapFixture(directory, options, new BootstrapStateStore(options, timeProvider)));
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
