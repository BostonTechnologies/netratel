namespace NetRatel.Application.Artifacts;

public sealed record DeploymentScriptTemplateRequest(
    int TenantId,
    string RuntimeId,
    string EnrollmentCode,
    string ApiBaseUrl,
    DateTimeOffset ValidToUtc,
    bool InstallAsService,
    bool SilentInstall,
    string? ArtifactVersion = null,
    string? ArtifactSha256 = null,
    string? GatewayEndpoint = null,
    bool IsUpdateSeed = false);

public interface IScriptTemplateService
{
    string Build(DeploymentScriptTemplateRequest request);
    string GetFileExtension(string runtimeId);
}
