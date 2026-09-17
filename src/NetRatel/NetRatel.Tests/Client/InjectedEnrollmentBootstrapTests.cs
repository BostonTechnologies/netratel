using FluentAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class InjectedEnrollmentBootstrapTests
{
    [Fact]
    public async Task TryEnrollAsync_WithValidFile_EnrollsAndDeletesFile()
    {
        var fs = new FakeFileSystem();
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(baseDir, "netratel.enroll.json");
        fs.Files[path] = """
            {
              "schema":"netratel.enroll.v1",
              "tenantId":42,
              "enrollmentCode":"ENR-ABC123",
              "issuer":"https://netratel.example.invalid",
              "createdAtUtc":"2026-02-26T00:00:00Z",
              "validToUtc":"2099-02-27T00:00:00Z"
            }
            """;

        var enrollment = new FakeEnrollmentService();
        var store = new FakeCredentialStore();
        var bootstrap = new InjectedEnrollmentBootstrap(fs, () => baseDir);
        var options = new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid" };

        var result = await bootstrap.TryEnrollAsync(options, enrollment, store, CancellationToken.None);

        result.Should().NotBeNull();
        enrollment.LastEnrollmentCode.Should().Be("ENR-ABC123");
        store.Saved.Should().Be(("agent-1", "refresh-1"));
        fs.Deleted.Should().Contain(path);
    }

    [Fact]
    public async Task TryEnrollAsync_WithExpiredFile_DoesNotEnroll()
    {
        var fs = new FakeFileSystem();
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(baseDir, "netratel.enroll.json");
        fs.Files[path] = """
            {
              "schema":"netratel.enroll.v1",
              "tenantId":42,
              "enrollmentCode":"ENR-ABC123",
              "issuer":"https://netratel.example.invalid",
              "createdAtUtc":"2026-02-26T00:00:00Z",
              "validToUtc":"2000-02-27T00:00:00Z"
            }
            """;

        var enrollment = new FakeEnrollmentService();
        var store = new FakeCredentialStore();
        var bootstrap = new InjectedEnrollmentBootstrap(fs, () => baseDir);
        var options = new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid" };

        var result = await bootstrap.TryEnrollAsync(options, enrollment, store, CancellationToken.None);

        result.Should().BeNull();
        enrollment.LastEnrollmentCode.Should().BeNull();
        store.Saved.Should().BeNull();
        fs.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task TryEnrollAsync_WithMissingExpiry_LogsReasonAndDoesNotEnroll()
    {
        var fs = new FakeFileSystem();
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(baseDir, "netratel.enroll.json");
        fs.Files[path] = """
            {"schema":"netratel.enroll.v1","tenantId":42,"enrollmentCode":"ENR-ABC123","issuer":"https://netratel.example.invalid"}
            """;
        var diagnostics = new List<string>();
        var bootstrap = new InjectedEnrollmentBootstrap(
            fs,
            () => baseDir,
            diagnostics.Add);

        var result = await bootstrap.TryEnrollAsync(
            new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid" },
            new FakeEnrollmentService(),
            new FakeCredentialStore(),
            CancellationToken.None);

        result.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain("missing validToUtc");
    }

    private sealed class FakeEnrollmentService : IAgentEnrollmentService
    {
        public string? LastEnrollmentCode { get; private set; }

        public Task<(string AgentId, string RefreshToken)> EnrollAsync(string enrollmentCode, CancellationToken ct)
        {
            LastEnrollmentCode = enrollmentCode;
            return Task.FromResult(("agent-1", "refresh-1"));
        }
    }

    private sealed class FakeCredentialStore : IAgentCredentialStore
    {
        public (string AgentId, string RefreshToken)? Saved { get; private set; }

        public Task SaveAsync(string agentId, string refreshToken)
        {
            Saved = (agentId, refreshToken);
            return Task.CompletedTask;
        }

        public Task<(string AgentId, string RefreshToken)?> LoadAsync()
            => Task.FromResult<(string AgentId, string RefreshToken)?>(null);

        public Task ClearRefreshCredentialsAsync() => Task.CompletedTask;

        public Task ResetInstallationIdentityAsync() => Task.CompletedTask;

        public Task ClearAsync() => ClearRefreshCredentialsAsync();
    }

    private sealed class FakeFileSystem : IInjectedEnrollmentFileSystem
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Deleted { get; } = new();

        public bool Exists(string path) => Files.ContainsKey(path);

        public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => Task.FromResult(Files[path]);

        public void Delete(string path)
        {
            Deleted.Add(path);
            Files.Remove(path);
        }
    }
}
