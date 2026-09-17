using System;
using NuGet.Versioning;

namespace NetRatel.API.Models;

public static class ClientDownloadValidation
{
    public const int MinValidityMinutes = 5;
    public const int MaxValidityMinutes = 72460;

    public static (int tenantId, string normalizedRid, string normalizedVersion) Validate(ClientDownloadRequest req, bool isWindows)
    {
        if (req.TenantId <= 0)
        {
            throw new RequestValidationException("tenantId", "TenantId must be a positive integer.");
        }

        var rid = NormalizeRid(req.RuntimeId, isWindows);
        if (string.IsNullOrWhiteSpace(rid))
        {
            throw new RequestValidationException(
                "runtimeId",
                "RuntimeId must be a valid RID (e.g., 'win-x64', 'linux-x64'). You can also send 'x64' and it will be normalized.");
        }

        var version = NormalizeVersion(req.Version);
        ValidateEnrollmentOptions(req);

        return (req.TenantId, rid, version);
    }

    public static string NormalizeRid(string rid, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(rid)) return string.Empty;
        rid = rid.Trim().ToLowerInvariant();

        if (rid == "x64")
        {
            return isWindows ? "win-x64" : "linux-x64";
        }

        if (rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase) ||
            rid.StartsWith("linux-", StringComparison.OrdinalIgnoreCase) ||
            rid.StartsWith("osx-", StringComparison.OrdinalIgnoreCase))
        {
            return rid;
        }

        if (rid is "windows-x64" or "win64")
        {
            return "win-x64";
        }

        if (rid is "linux64")
        {
            return "linux-x64";
        }

        return string.Empty;
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
        {
            return "latest";
        }

        var trimmed = version.Trim();
        if (!SemanticVersion.TryParse(trimmed, out _))
        {
            throw new RequestValidationException("version", "Version must be a valid semantic version or 'latest'.");
        }

        return trimmed;
    }

    private static void ValidateEnrollmentOptions(ClientDownloadRequest req)
    {
        if (!req.InjectEnrollment)
        {
            return;
        }

        if (!req.EnrollmentCodeId.HasValue && !req.ValidForMinutes.HasValue)
        {
            throw new RequestValidationException("validForMinutes", "ValidForMinutes is required when issuing an injected enrollment code.");
        }

        if (req.ValidForMinutes.HasValue &&
            (req.ValidForMinutes.Value < MinValidityMinutes || req.ValidForMinutes.Value > MaxValidityMinutes))
        {
            throw new RequestValidationException("validForMinutes", $"ValidForMinutes must be between {MinValidityMinutes} and {MaxValidityMinutes}.");
        }

        if (req.MaxUses.HasValue && req.MaxUses.Value <= 0)
        {
            throw new RequestValidationException("maxUses", "MaxUses must be greater than zero.");
        }
    }
}

public sealed class RequestValidationException : Exception
{
    public string Field { get; }

    public RequestValidationException(string field, string message) : base(message)
    {
        Field = field;
    }
}
