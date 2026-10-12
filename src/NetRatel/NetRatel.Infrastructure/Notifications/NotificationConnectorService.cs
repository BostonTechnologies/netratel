using System.Security.Claims;
using System.Security.Cryptography;
using NetRatel.Application.Notifications;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Infrastructure.Notifications;

public sealed class NotificationConnectorService(INotificationConnectorStore store, IRatelDeskConnectorAuthorization authorization,
    INotificationCredentialProtector protector, INotificationWebhookTransport transport, TimeProvider clock) : INotificationConnectorService
{
    public async Task<IReadOnlyList<NotificationConnectorDto>> ListAsync(int tenantId, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await RequireManageAsync(actor, tenantId, ct).ConfigureAwait(false);
        return (await store.ListAsync(tenantId, ct).ConfigureAwait(false)).Select(ToDto).ToArray();
    }

    public async Task<NotificationConnectorDto> SaveAsync(int tenantId, Guid id, SaveNotificationConnectorRequest request,
        ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await RequireManageAsync(actor, tenantId, ct).ConfigureAwait(false);
        var principalId = actor.FindFirst("netratel_principal_id")?.Value ?? throw new UnauthorizedAccessException();
        if (!await authorization.CanExecuteAsync(principalId, actor.FindFirst("netratel_integration_credential_id")?.Value, tenantId, ct).ConfigureAwait(false))
            throw new UnauthorizedAccessException();
        if (id == Guid.Empty || request.ExpectedRevision < 0 || request.Configuration is null)
            throw new ArgumentException("invalid-notification-connector-request");
        var configuration = Validate(request.Configuration);
        var current = await store.GetAsync(tenantId, id, ct).ConfigureAwait(false);
        if (current?.DeletedAtUtc is not null || (current?.Revision ?? 0) != request.ExpectedRevision)
            throw new InvalidOperationException("notification-connector-conflict");
        string protectedUrl;
        if (!string.IsNullOrWhiteSpace(request.WebhookUrl))
        {
            var address = NotificationWebhookAddress.Validate(configuration.Provider, request.WebhookUrl);
            protectedUrl = protector.Protect(tenantId, id, address.AbsoluteUri);
        }
        else if (current is not null && current.Configuration.Provider == configuration.Provider)
            protectedUrl = current.ProtectedWebhookUrl;
        else throw new ArgumentException("notification-webhook-required");
        var state = new NotificationConnectorState(id, tenantId, checked(request.ExpectedRevision + 1), principalId, configuration, protectedUrl);
        if (!await store.SaveAsync(state, request.ExpectedRevision, ct).ConfigureAwait(false))
            throw new InvalidOperationException("notification-connector-conflict");
        return ToDto(state);
    }

    public async Task DeleteAsync(int tenantId, Guid id, long expectedRevision, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await RequireManageAsync(actor, tenantId, ct).ConfigureAwait(false);
        var state = await store.GetAsync(tenantId, id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (state.Revision != expectedRevision || state.DeletedAtUtc is not null)
            throw new InvalidOperationException("notification-connector-conflict");
        var deleted = state with { Revision = checked(expectedRevision + 1), DeletedAtUtc = clock.GetUtcNow(),
            ProtectedWebhookUrl = string.Empty, Configuration = state.Configuration with { Enabled = false } };
        if (!await store.SaveAsync(deleted, expectedRevision, ct).ConfigureAwait(false))
            throw new InvalidOperationException("notification-connector-conflict");
    }

    public async Task<NotificationDeliveryResult> SendTestAsync(int tenantId, Guid id, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await RequireManageAsync(actor, tenantId, ct).ConfigureAwait(false);
        var actorId = actor.FindFirst("netratel_principal_id")?.Value ?? throw new UnauthorizedAccessException();
        if (!await authorization.CanExecuteAsync(actorId, actor.FindFirst("netratel_integration_credential_id")?.Value, tenantId, ct).ConfigureAwait(false))
            throw new UnauthorizedAccessException();
        var state = await store.GetAsync(tenantId, id, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (state.DeletedAtUtc is not null || !state.Configuration.Enabled ||
            !await authorization.CanExecuteAsync(state.OwnerPrincipalId, null, tenantId, ct).ConfigureAwait(false))
            return new(NotificationDeliveryStatus.Unavailable, "notification-connector-disabled-or-owner-denied");
        try
        {
            return await transport.SendAsync(state.Configuration.Provider, protector.Unprotect(tenantId, id, state.ProtectedWebhookUrl),
                $"NetRatel test notification\n{state.Configuration.Name}\nTenant {tenantId}\n{clock.GetUtcNow():yyyy-MM-dd HH:mm:ss} UTC",
                state.Configuration.DiscordUsername, state.Configuration.DiscordThreadId, ct).ConfigureAwait(false);
        }
        catch (CryptographicException) { return new(NotificationDeliveryStatus.Unavailable, "notification-credential-unavailable"); }
    }

    private async Task RequireManageAsync(ClaimsPrincipal actor, int tenantId, CancellationToken ct)
    {
        if (tenantId <= 0 || !await authorization.CanManageAsync(actor, tenantId, ct).ConfigureAwait(false))
            throw new UnauthorizedAccessException();
    }

    private static NotificationConnectorConfiguration Validate(NotificationConnectorConfiguration configuration)
    {
        var name = configuration.Name?.Trim();
        if (name is not { Length: > 0 and <= NotificationConnectorLimits.MaximumNameLength } || name.Any(char.IsControl) ||
            !Enum.IsDefined(configuration.Provider)) throw new ArgumentException("invalid-notification-configuration");
        var username = string.IsNullOrWhiteSpace(configuration.DiscordUsername) ? null : configuration.DiscordUsername.Trim();
        var thread = string.IsNullOrWhiteSpace(configuration.DiscordThreadId) ? null : configuration.DiscordThreadId.Trim();
        if (username is not null && (username.Length > 80 || username.Any(char.IsControl)) ||
            thread is not null && (thread.Length > 20 || thread.Any(c => !char.IsAsciiDigit(c)) || !ulong.TryParse(thread, out var threadId) || threadId == 0) ||
            configuration.Provider == NotificationProvider.Slack && (username is not null || thread is not null))
            throw new ArgumentException("invalid-notification-provider-options");
        return configuration with { Name = name, DiscordUsername = username, DiscordThreadId = thread };
    }

    public static NotificationConnectorDto ToDto(NotificationConnectorState state) => new(state.Id, state.TenantId, state.Revision,
        state.Configuration, state.DeletedAtUtc is null && !string.IsNullOrEmpty(state.ProtectedWebhookUrl));
}
