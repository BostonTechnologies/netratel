using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Logging;

namespace NetRatel.Client.Service.Auth;

public interface IInjectedEnrollmentBootstrap
{
    Task<(string AgentId, string RefreshToken)?> TryEnrollAsync(
        ClientOptions options,
        IAgentEnrollmentService enrollmentService,
        IAgentCredentialStore credentialStore,
        CancellationToken ct);
}

public sealed class InjectedEnrollmentBootstrap : IInjectedEnrollmentBootstrap
{
    private const string FileName = "netratel.enroll.json";
    private readonly IInjectedEnrollmentFileSystem _fileSystem;
    private readonly Func<string> _baseDirectoryProvider;
    private readonly Action<string> _diagnostic;

    public InjectedEnrollmentBootstrap(
        IInjectedEnrollmentFileSystem? fileSystem = null,
        Func<string>? baseDirectoryProvider = null,
        Action<string>? diagnostic = null)
    {
        _fileSystem = fileSystem ?? new InjectedEnrollmentFileSystem();
        _baseDirectoryProvider = baseDirectoryProvider ?? ResolveExecutableDirectory;
        _diagnostic = diagnostic ?? LogManager.WriteLog;
    }

    public async Task<(string AgentId, string RefreshToken)?> TryEnrollAsync(
        ClientOptions options,
        IAgentEnrollmentService enrollmentService,
        IAgentCredentialStore credentialStore,
        CancellationToken ct)
    {
        var baseDir = _baseDirectoryProvider();
        var enrollPath = Path.Combine(baseDir, FileName);
        if (!_fileSystem.Exists(enrollPath))
        {
            return null;
        }

        try
        {
            var json = await _fileSystem.ReadAllTextAsync(enrollPath, ct);
            var payload = JsonSerializer.Deserialize<InjectedEnrollmentPayload>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (payload is null)
            {
                _diagnostic("[Auth] Ignoring netratel.enroll.json: payload is empty.");
                return null;
            }

            var invalidReason = GetInvalidPayloadReason(options, payload);
            if (invalidReason is not null)
            {
                _diagnostic($"[Auth] Ignoring netratel.enroll.json: {invalidReason}");
                return null;
            }

            var creds = await enrollmentService.EnrollAsync(payload.EnrollmentCode.Trim(), ct);
            await credentialStore.SaveAsync(creds.AgentId, creds.RefreshToken);
            _fileSystem.Delete(enrollPath);
            return creds;
        }
        catch (JsonException)
        {
            _diagnostic("[Auth] Ignoring netratel.enroll.json: malformed JSON.");
            return null;
        }
        catch (IOException ex)
        {
            _diagnostic($"[Auth] Ignoring netratel.enroll.json: unable to read enrollment file ({ex.Message}).");
            return null;
        }
    }

    private static string? GetInvalidPayloadReason(ClientOptions options, InjectedEnrollmentPayload payload)
    {
        if (!string.Equals(payload.Schema, "netratel.enroll.v1", StringComparison.OrdinalIgnoreCase))
        {
            return "schema must be netratel.enroll.v1.";
        }

        if (payload.TenantId <= 0 || string.IsNullOrWhiteSpace(payload.EnrollmentCode))
        {
            return "tenantId and enrollmentCode are required.";
        }

        if (payload.ValidToUtc <= DateTime.UtcNow)
        {
            return "enrollment payload is expired or missing validToUtc.";
        }

        if (!string.IsNullOrWhiteSpace(payload.Issuer))
        {
            var configured = NormalizeOrigin(options.ApiBaseUrl);
            var issuer = NormalizeOrigin(payload.Issuer);
            if (!string.Equals(configured, issuer, StringComparison.OrdinalIgnoreCase))
            {
                return "issuer does not match the configured API base URL.";
            }
        }

        return null;
    }

    private static string NormalizeOrigin(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Authority}";
        }

        return value.Trim().TrimEnd('/');
    }

    private static string ResolveExecutableDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                return dir;
            }
        }

        return AppContext.BaseDirectory;
    }

    public sealed record InjectedEnrollmentPayload(
        string Schema,
        int TenantId,
        string EnrollmentCode,
        string? Issuer,
        DateTime CreatedAtUtc,
        DateTime ValidToUtc);
}

public interface IInjectedEnrollmentFileSystem
{
    bool Exists(string path);
    Task<string> ReadAllTextAsync(string path, CancellationToken ct);
    void Delete(string path);
}

public sealed class InjectedEnrollmentFileSystem : IInjectedEnrollmentFileSystem
{
    public bool Exists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(path, ct);

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
