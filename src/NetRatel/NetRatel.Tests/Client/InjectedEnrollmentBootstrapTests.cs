using FluentAssertions;
using System.Text.Json;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class InjectedEnrollmentBootstrapTests
{
    private const string DiagnosticCanary = "synthetic-diagnostic-value-canary";

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

    [Fact]
    public async Task TryEnrollAsync_WithMissingIssuer_LogsReasonAndDoesNotEnroll()
    {
        var fs = new FakeFileSystem();
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        fs.Files[Path.Combine(baseDir, "netratel.enroll.json")] = """
            {"schema":"netratel.enroll.v1","tenantId":42,"enrollmentCode":"ENR-ABC123","validToUtc":"2099-02-27T00:00:00Z"}
            """;
        var diagnostics = new List<string>();
        var bootstrap = new InjectedEnrollmentBootstrap(fs, () => baseDir, diagnostics.Add);

        var result = await bootstrap.TryEnrollAsync(
            new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid/tenant" },
            new FakeEnrollmentService(),
            new FakeCredentialStore(),
            CancellationToken.None);

        result.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain("issuer is required");
    }

    [Fact]
    public async Task TryEnrollAsync_WithMatchingApiSuffixIssuer_Enrolls()
    {
        var fs = new FakeFileSystem();
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(baseDir, "netratel.enroll.json");
        fs.Files[path] = """
            {
              "schema":"netratel.enroll.v1",
              "tenantId":42,
              "enrollmentCode":"ENR-ABC123",
              "issuer":"https://netratel.example.invalid/api/",
              "validToUtc":"2099-02-27T00:00:00Z"
            }
            """;

        var enrollment = new FakeEnrollmentService();
        var store = new FakeCredentialStore();
        var bootstrap = new InjectedEnrollmentBootstrap(fs, () => baseDir);

        var result = await bootstrap.TryEnrollAsync(
            new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid/api" },
            enrollment,
            store,
            CancellationToken.None);

        result.Should().NotBeNull();
        enrollment.LastEnrollmentCode.Should().Be("ENR-ABC123");
        store.Saved.Should().NotBeNull();
        fs.Deleted.Should().Contain(path);
    }

    [Fact]
    public async Task TryEnrollAsync_WithUnrecognizedApiPathBase_DoesNotEnroll()
    {
        var fs = new FakeFileSystem();
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        fs.Files[Path.Combine(baseDir, "netratel.enroll.json")] = """
            {
              "schema":"netratel.enroll.v1",
              "tenantId":42,
              "enrollmentCode":"ENR-ABC123",
              "issuer":"https://netratel.example.invalid/tenant",
              "validToUtc":"2099-02-27T00:00:00Z"
            }
            """;
        var diagnostics = new List<string>();
        var bootstrap = new InjectedEnrollmentBootstrap(fs, () => baseDir, diagnostics.Add);

        var result = await bootstrap.TryEnrollAsync(
            new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid" },
            new FakeEnrollmentService(),
            new FakeCredentialStore(),
            CancellationToken.None);

        result.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain("origin, optionally followed by /api");
    }

    [Theory]
    [InlineData("\"42\"", "field=tenantId; expected=Int32; actual=String")]
    [InlineData("\"synthetic-diagnostic-value-canary\"", "field=tenantId; expected=Int32; actual=String")]
    [InlineData("2147483648", "field=tenantId; expected=Int32; actual=Number")]
    [InlineData("-2147483649", "field=tenantId; expected=Int32; actual=Number")]
    [InlineData("42.5", "field=tenantId; expected=Int32; actual=Number")]
    [InlineData("null", "field=tenantId; expected=Int32; actual=Null")]
    [InlineData("true", "field=tenantId; expected=Int32; actual=True")]
    [InlineData("{}", "field=tenantId; expected=Int32; actual=Object")]
    [InlineData("[]", "field=tenantId; expected=Int32; actual=Array")]
    [InlineData(null, "tenantId and enrollmentCode are required")]
    [InlineData("0", "tenantId and enrollmentCode are required")]
    [InlineData("-1", "tenantId and enrollmentCode are required")]
    public async Task TryEnrollAsync_RejectsNonPositiveOrNonInt32TenantWithoutLeakingValues(string? tenantJson, string reason)
    {
        var tenantProperty = tenantJson is null ? "" : $"\"tenantId\":{tenantJson},";
        await AssertRejected($$"""
            {"schema":"netratel.enroll.v1",{{tenantProperty}}"enrollmentCode":"{{DiagnosticCanary}}","issuer":"https://netratel.example.invalid","validToUtc":"2099-02-27T00:00:00Z"}
            """, reason);
    }

    [Theory]
    [InlineData("schema", "7", "String", "Number")]
    [InlineData("enrollmentCode", "{\"secret\":\"synthetic-diagnostic-value-canary\"}", "String", "Object")]
    [InlineData("issuer", "[\"synthetic-diagnostic-value-canary\"]", "String", "Array")]
    [InlineData("createdAtUtc", "\"synthetic-diagnostic-value-canary\"", "DateTime", "String")]
    [InlineData("validToUtc", "\"synthetic-diagnostic-value-canary\"", "DateTime", "String")]
    [InlineData("TENANTID", "\"synthetic-diagnostic-value-canary\"", "Int32", "String")]
    public async Task TryEnrollAsync_ReportsOnlyAllowlistedFieldsAndTypes(string field, string value, string expected, string actual)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["schema"] = "\"netratel.enroll.v1\"",
            ["tenantId"] = "42",
            ["enrollmentCode"] = "\"" + DiagnosticCanary + "\"",
            ["issuer"] = "\"https://netratel.example.invalid\"",
            ["createdAtUtc"] = "\"2026-02-26T00:00:00Z\"",
            ["validToUtc"] = "\"2099-02-27T00:00:00Z\""
        };
        properties.Remove(field);
        properties.Add(field, value);
        var json = "{" + string.Join(",", properties.Select(property => $"\"{property.Key}\":{property.Value}")) + "}";
        var diagnosticField = field.ToLowerInvariant() switch
        {
            "tenantid" => "tenantId",
            "enrollmentcode" => "enrollmentCode",
            "createdatutc" => "createdAtUtc",
            "validtoutc" => "validToUtc",
            _ => field
        };

        await AssertRejected(json, $"invalid field/type (field={diagnosticField}; expected={expected}; actual={actual}).");
    }

    [Fact]
    public async Task TryEnrollAsync_ReportsMalformedSyntaxWithoutLeakingExceptionPayload()
    {
        await AssertRejected("{\"enrollmentCode\":\"" + DiagnosticCanary + "\",\"tenantId\":}", "malformed JSON syntax");
    }

    [Theory]
    [InlineData("[\"synthetic-diagnostic-value-canary\"]", "Array")]
    [InlineData("\"synthetic-diagnostic-value-canary\"", "String")]
    [InlineData("17", "Number")]
    public async Task TryEnrollAsync_RejectsNonObjectPayloadWithoutLeakingValues(string json, string actual)
    {
        await AssertRejected(json, $"invalid payload type (expected=Object; actual={actual})");
    }

    [Theory]
    [InlineData("\"issuer\":\"https://different.example.invalid\"", "issuer does not match")]
    [InlineData("\"validToUtc\":\"2000-02-27T00:00:00Z\"", "expired or missing validToUtc")]
    [InlineData("\"schema\":\"synthetic-diagnostic-value-canary\"", "schema must be netratel.enroll.v1")]
    public async Task TryEnrollAsync_RetainsStrictSemanticValidation(string replacement, string reason)
    {
        var json = $$"""
            {"schema":"netratel.enroll.v1","tenantId":42,"enrollmentCode":"{{DiagnosticCanary}}","issuer":"https://netratel.example.invalid","validToUtc":"2099-02-27T00:00:00Z"}
            """;
        var field = replacement.Split(':', 2)[0];
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        properties.Remove(JsonSerializer.Deserialize<string>(field)!);
        var remaining = JsonSerializer.Serialize(properties);

        await AssertRejected(remaining[..^1] + "," + replacement + "}", reason);
    }

    [Fact]
    public async Task TryEnrollAsync_ReadFailureDoesNotLeakExceptionMessage()
    {
        var fs = new FakeFileSystem { ReadFailure = new IOException(DiagnosticCanary) };
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(baseDir, "netratel.enroll.json");
        fs.Files[path] = "unused";
        var diagnostics = new List<string>();
        var enrollment = new FakeEnrollmentService();
        var store = new FakeCredentialStore();
        var bootstrap = new InjectedEnrollmentBootstrap(fs, () => baseDir, diagnostics.Add);

        var result = await bootstrap.TryEnrollAsync(
            new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid" }, enrollment, store, CancellationToken.None);

        result.Should().BeNull();
        diagnostics.Should().Equal("[Auth] Ignoring netratel.enroll.json: unable to read enrollment file (I/O failure).");
        string.Join("\n", diagnostics).Should().NotContain(DiagnosticCanary);
        enrollment.LastEnrollmentCode.Should().BeNull();
        store.Saved.Should().BeNull();
        fs.Files.Should().ContainKey(path);
        fs.Deleted.Should().BeEmpty();
    }

    private static async Task AssertRejected(string json, string reason)
    {
        var fs = new FakeFileSystem();
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(baseDir, "netratel.enroll.json");
        fs.Files[path] = json;
        var diagnostics = new List<string>();
        var enrollment = new FakeEnrollmentService();
        var store = new FakeCredentialStore();
        var bootstrap = new InjectedEnrollmentBootstrap(fs, () => baseDir, diagnostics.Add);

        var result = await bootstrap.TryEnrollAsync(
            new ClientOptions { ApiBaseUrl = "https://netratel.example.invalid" }, enrollment, store, CancellationToken.None);

        result.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Contain(reason);
        string.Join("\n", diagnostics).Should().NotContain(DiagnosticCanary);
        enrollment.LastEnrollmentCode.Should().BeNull();
        store.Saved.Should().BeNull();
        fs.Files.Should().ContainKey(path);
        fs.Deleted.Should().BeEmpty();
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
        public IOException? ReadFailure { get; init; }

        public bool Exists(string path) => Files.ContainsKey(path);

        public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => ReadFailure is null
            ? Task.FromResult(Files[path]) : Task.FromException<string>(ReadFailure);

        public void Delete(string path)
        {
            Deleted.Add(path);
            Files.Remove(path);
        }
    }
}
