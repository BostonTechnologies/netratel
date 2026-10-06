using System.Text;
using Microsoft.Extensions.Options;
using NetRatel.API.Models;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Shared.Client;

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
    private readonly IOptionsMonitor<ClientInstallationEndpointOptions> _endpoints;
    private readonly IDeploymentBrandingService _branding;
    private readonly ILogger<ClientScriptService> _logger;

    public ClientScriptService(
        ITenantLookupService tenantLookupService,
        IEnrollmentCodeIssueService enrollmentCodeIssueService,
        IScriptTemplateService templateService,
        IClientArtifactsService artifactsService,
        IOptionsMonitor<ClientInstallationEndpointOptions> endpoints,
        IDeploymentBrandingService branding,
        ILogger<ClientScriptService> logger)
    {
        _tenantLookupService = tenantLookupService;
        _enrollmentCodeIssueService = enrollmentCodeIssueService;
        _templateService = templateService;
        _artifactsService = artifactsService;
        _endpoints = endpoints;
        _branding = branding;
        _logger = logger;
    }

    public async Task<ClientScriptResult> GenerateAsync(ClientScriptRequest request, CancellationToken ct)
    {
        if (!await _tenantLookupService.TenantExistsAsync(request.TenantId, ct))
        {
            throw new RequestValidationException("tenantId", $"Tenant '{request.TenantId}' does not exist.");
        }

        var endpoints = ClientInstallEndpointResolver.Resolve(_endpoints.CurrentValue,
            await _branding.GetEffectiveAsync(ct).ConfigureAwait(false));
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
                endpoints.PublicApiBaseUrl,
                issue.ValidToUtc,
                request.InstallAsService,
                request.SilentInstall,
                artifact.Version,
                artifact.Sha256,
                ClientInstallEndpointResolver.GatewayOverride(endpoints)));

        _logger.LogInformation(
            "Deployment script generated. tenantId={TenantId}, runtimeId={RuntimeId}, version={Version}, enrollmentCodeId={EnrollmentCodeId}, apiEndpointSource={ApiEndpointSource}, gatewayEndpointSource={GatewayEndpointSource}",
            request.TenantId,
            request.RuntimeId,
            artifact.Version,
            issue.EnrollmentCodeId,
            endpoints.PublicApiSource, endpoints.GatewaySource);

        var ext = _templateService.GetFileExtension(request.RuntimeId);
        var fileName = $"netratel-install-{request.TenantId}.{ext}";
        return new ClientScriptResult(
            Encoding.UTF8.GetBytes(script),
            "text/plain",
            fileName,
            issue.Code,
            issue.ValidToUtc);
    }

}
