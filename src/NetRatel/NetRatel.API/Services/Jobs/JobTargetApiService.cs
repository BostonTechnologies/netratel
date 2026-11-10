using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

/// <summary>Permitted read-only selector surfaces; execution authority is independently checked at admission.</summary>
public sealed class JobTargetApiService(IJobNodeDirectory directory, IJobTargetResolver resolver, IEffectiveAccessService access)
{
    public Task<JobNodePageDto> SearchAsync(int tenantId, string? search, int page, int pageSize,
        ClaimsPrincipal principal, CancellationToken ct) => SearchAsync(tenantId, search, page, pageSize, principal, ct, null);
    public async Task<JobNodePageDto> SearchAsync(int tenantId, string? search, int page, int pageSize,
        ClaimsPrincipal principal, CancellationToken ct, IReadOnlySet<Guid>? authorizedAgentIds)
    {
        await RequireReadAsync(tenantId, principal, ct).ConfigureAwait(false);
        ExecutionGroupService.RequirePage(page, pageSize);
        if (search?.Length > 256) throw new JobTargetValidationException("target_search_too_large", "Node search is limited to 256 characters.");
        var all = (await directory.ReadAsync(tenantId, authorizedAgentIds, false, ct).ConfigureAwait(false)).Where(x => JobNodeFilterEvaluator.MatchesSearch(x, search)).ToArray();
        return new(all.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), page, pageSize, all.Length, (long)page * pageSize < all.Length ? page + 1 : null);
    }
    public Task<IReadOnlyList<JobNodeInventoryDto>> IdentitiesAsync(int tenantId, IReadOnlyList<Guid> requested,
        ClaimsPrincipal principal, CancellationToken ct) => IdentitiesAsync(tenantId, requested, principal, ct, null);
    public async Task<IReadOnlyList<JobNodeInventoryDto>> IdentitiesAsync(int tenantId, IReadOnlyList<Guid> requested,
        ClaimsPrincipal principal, CancellationToken ct, IReadOnlySet<Guid>? authorizedAgentIds)
    {
        await RequireReadAsync(tenantId, principal, ct).ConfigureAwait(false);
        if (requested is null || requested.Count > 1000 || requested.Any(id => id == Guid.Empty))
            throw new JobTargetValidationException("target_identity_batch_invalid", "Read 0–1000 stable client identities per request.");
        var ids = requested.ToHashSet();
        if (authorizedAgentIds is not null && !ids.IsSubsetOf(authorizedAgentIds))
            throw new JobTargetValidationException("target_node_unavailable", "The requested identities are not all available in this tenant.");
        var found = await directory.ReadAsync(tenantId, ids, true, ct).ConfigureAwait(false);
        if (found.Count != ids.Count) throw new JobTargetValidationException("target_node_unavailable", "The requested identities are not all available in this tenant.");
        return found;
    }
    public Task<JobNodePreviewDto> PreviewAsync(int tenantId, JobNodePreviewRequest request,
        ClaimsPrincipal principal, CancellationToken ct) => PreviewAsync(tenantId, request, principal, ct, null);
    public async Task<JobNodePreviewDto> PreviewAsync(int tenantId, JobNodePreviewRequest request,
        ClaimsPrincipal principal, CancellationToken ct, IReadOnlySet<Guid>? authorizedAgentIds)
    {
        var result = await ResolvePreviewAsync(tenantId, new(new() { Source = JobTargetSource.NodeFilter, Filter = request.Filter },
            request.Environment, Parameters: request.Parameters, Page: request.Page, PageSize: request.PageSize), principal, ct, authorizedAgentIds).ConfigureAwait(false);
        return new(result.Items, result.Page, result.PageSize, result.TotalCount, result.NextPage, result.NormalizedFilter!, result.Digest);
    }
    public Task<JobTargetPreviewDto> ResolvePreviewAsync(int tenantId, JobTargetPreviewRequest request,
        ClaimsPrincipal principal, CancellationToken ct) => ResolvePreviewAsync(tenantId, request, principal, ct, null);
    public async Task<JobTargetPreviewDto> ResolvePreviewAsync(int tenantId, JobTargetPreviewRequest request,
        ClaimsPrincipal principal, CancellationToken ct, IReadOnlySet<Guid>? authorizedAgentIds)
    {
        await RequireReadAsync(tenantId, principal, ct).ConfigureAwait(false);
        ExecutionGroupService.RequirePage(request.Page, request.PageSize);
        var result = await resolver.ResolveAsync(tenantId, request.Target, request.Environment, request.Parameters,
            request.RuntimeTarget, authorizedAgentIds, ct).ConfigureAwait(false);
        // This directory-only digest is useful for read previews. Execution reviews separately bind the definition and policies.
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { tenantId, request.Target, request.Environment, result.GroupRevision, result.NormalizedFilter, result.Bindings, result.Nodes })));
        return new(result.Nodes.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).Select(ToInventory).ToArray(),
            request.Page, request.PageSize, result.Nodes.Count, (long)request.Page * request.PageSize < result.Nodes.Count ? request.Page + 1 : null,
            request.Target, result.GroupRevision, result.NormalizedFilter, digest);
    }
    public async Task<JobNodeFilterValidationDto> ValidateAsync(int tenantId, JobNodePreviewRequest request,
        ClaimsPrincipal principal, CancellationToken ct)
    {
        await RequireReadAsync(tenantId, principal, ct).ConfigureAwait(false);
        try
        {
            var expression = JobNodeFilterParser.Parse(request.Filter);
            if (request.Parameters is not null) _ = JobNodeFilterParser.Bind(expression, request.Parameters);
            return new(true, JobNodeFilterParser.Format(expression));
        }
        catch (JobTargetValidationException error) { return new(false, null, error.Code, error.Message); }
    }
    public async Task<IReadOnlyList<JobNodeFilterPropertyDto>> PropertiesAsync(int tenantId, ClaimsPrincipal principal, CancellationToken ct)
    { await RequireReadAsync(tenantId, principal, ct).ConfigureAwait(false); return JobNodeFilterParser.Properties; }
    private async Task RequireReadAsync(int tenantId, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (tenantId <= 0 || !(await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, tenantId, ct).ConfigureAwait(false) ||
            await access.AuthorizeAsync(principal, NetRatelPermissions.TenantAdministration, tenantId, ct).ConfigureAwait(false)))
            throw new JobTargetValidationException("target_read_denied", "Tenant target discovery permission is required.", 403);
    }
    private static JobNodeInventoryDto ToInventory(JobTargetNode node) => new(node.AgentId, node.Hostname,
        node.DisplayName ?? node.Hostname, node.Name, node.Tags, node.OperatingSystem, node.Architecture,
        node.Environment, node.IsOnline, node.IsEnabled, null);
}
