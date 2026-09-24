using System.Text;
using NetRatel.API.Models;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;

namespace NetRatel.API.Services;

public interface IClientScriptService
{
    Task<ClientScriptResult> GenerateAsync(ClientScriptRequest request, CancellationToken ct);
}

public sealed record ClientScriptResult(
    byte[] Content,
    string ContentType,
    string FileName,
    string? EnrollmentCode = null,
    DateTimeOffset? EnrollmentExpiresUtc = null);

public sealed class ClientScriptService : IClientScriptService
{
    private readonly ITenantLookupService _tenantLookupService;
    private readonly IEnrollmentCodeIssueService _enrollmentCodeIssueService;
    private readonly IScriptTemplateService _templateService;
    private readonly IClientArtifactsService _artifactsService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientScriptService> _logger;

    public ClientScriptService(
        ITenantLookupService tenantLookupService,
        IEnrollmentCodeIssueService enrollmentCodeIssueService,
        IScriptTemplateService templateService,
        IClientArtifactsService artifactsService,
        IConfiguration configuration,
        ILogger<ClientScriptService> logger)
    {
        _tenantLookupService = tenantLookupService;
        _enrollmentCodeIssueService = enrollmentCodeIssueService;
        _templateService = templateService;
        _artifactsService = artifactsService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ClientScriptResult> GenerateAsync(ClientScriptRequest request, CancellationToken ct)
    {
        if (!await _tenantLookupService.TenantExistsAsync(request.TenantId, ct))
        {
            throw new RequestValidationException("tenantId", $"Tenant '{request.TenantId}' does not exist.");
        }

        var apiBase = ResolveApiBaseUrl();
        var artifact = string.IsNullOrWhiteSpace(request.ArtifactVersion)
            ? await _artifactsService.GetLatestAsync(request.RuntimeId, ct)
            : await _artifactsService.GetMetadataAsync(request.RuntimeId, request.ArtifactVersion, ct);
        if (artifact is null)
        {
            var requestedVersion = string.IsNullOrWhiteSpace(request.ArtifactVersion) ? "latest" : request.ArtifactVersion;
            throw new FileNotFoundException($"No stored artifact '{requestedVersion}' found for RID '{request.RuntimeId}'.");
        }

        // Reject an unavailable artifact before creating a redeemable credential.
        var issue = await _enrollmentCodeIssueService.IssueAsync(
            new EnrollmentCodeIssueRequest(
                request.TenantId,
                request.ValidForMinutes,
                request.MaxUses ?? 1,
                CreatedBy: null,
                Notes: "script-generated"),
            ct);

        var script = _templateService.Build(
            new DeploymentScriptTemplateRequest(
                request.TenantId,
                request.RuntimeId,
                issue.Code,
                apiBase,
                issue.ValidToUtc,
                request.InstallAsService,
                request.SilentInstall,
                artifact.Version,
                artifact.Sha256));

        _logger.LogInformation(
            "Deployment script generated. tenantId={TenantId}, runtimeId={RuntimeId}, version={Version}, enrollmentCodeId={EnrollmentCodeId}",
            request.TenantId,
            request.RuntimeId,
            artifact.Version,
            issue.EnrollmentCodeId);

        var ext = _templateService.GetFileExtension(request.RuntimeId);
        var fileName = $"netratel-install-{request.TenantId}.{ext}";
        return new ClientScriptResult(
            Encoding.UTF8.GetBytes(script),
            "text/plain",
            fileName,
            issue.Code,
            issue.ValidToUtc);
    }

    private string ResolveApiBaseUrl()
    {
        var apiBase = _configuration["AgentAuth:Issuer"]
                      ?? _configuration["ApiBaseUrl"]
                      ?? _configuration["ClientDefaults:Dev:ApiBaseUrl"]
                      ?? "https://localhost:5005";
        return apiBase.TrimEnd('/');
    }
}
