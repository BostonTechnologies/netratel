using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using NetRatel.Application.Events;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.API.Endpoints.SystemPairing;
public static class PairingEndpoints
{
    public const string RateLimiter = "SystemPairingSensitive";
    public static IServiceCollection AddPairingApi(this IServiceCollection services)
    {
        services.AddSingleton<PairingFailureNotificationGate>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimiter, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                if (http.Request.Path.StartsWithSegments(PairingProtocol.Route) || http.Request.Path.StartsWithSegments(PairingProtocol.AdminRoute))
                {
                    http.Response.StatusCode = StatusCodes.Status429TooManyRequests; http.Response.Headers.CacheControl = "no-store";
                    await http.Response.WriteAsJsonAsync(new { code = "pairing-rate-limited", message = "Too many pairing attempts. Wait a minute, then retry.", correlationId = Guid.NewGuid().ToString("N") }, ct);
                }
            };
        });
        return services;
    }
    public static IEndpointRouteBuilder MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        var peer = app.MapGroup(PairingProtocol.Route).WithTags("System pairing").DisableAntiforgery();
        peer.MapGet("/metadata", (HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => Results.Ok(await pairing.MetadataProofAsync(http.Request.Headers["X-Pairing-Nonce"].ToString(), ct)))).AllowAnonymous();
        peer.MapPost("/exchange", (PairingExchangeRequest request, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => Results.Ok(await pairing.ExchangeAsync(request, ct)))).AllowAnonymous().RequireRateLimiting(RateLimiter);
        peer.MapGet("/directory", (HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => Results.Ok(await pairing.DirectoryAsync(await AuthenticateAsync(http, pairing, ct), ct)))).AllowAnonymous();
        peer.MapPut("/mappings/{id:guid}", (Guid id, PairingSaveRequest request, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => Results.Ok(await pairing.ReceiveSaveAsync(await AuthenticateAsync(http, pairing, ct), id, request, ct)))).AllowAnonymous();
        peer.MapPost("/mappings/{id:guid}/test", (Guid id, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => Results.Ok(await pairing.ReceiveTestAsync(await AuthenticateAsync(http, pairing, ct), id, ct)))).AllowAnonymous();
        peer.MapDelete("/mappings/{id:guid}", (Guid id, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => { await pairing.ReceiveDeleteAsync(await AuthenticateAsync(http, pairing, ct), id, ct); return Results.NoContent(); })).AllowAnonymous();
        peer.MapDelete("/pair", (HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => { await pairing.ReceiveDeleteAsync(await AuthenticateAsync(http, pairing, ct), null, ct); return Results.NoContent(); })).AllowAnonymous();
        var local = app.MapGroup(PairingProtocol.AdminRoute).RequireAuthorization("InteractiveAccount").WithTags("System connections");
        local.MapGet("", (HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => Results.Ok(await pairing.ListAsync(http.User, ct))));
        local.MapPost("/code", (HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, true, async () => Results.Ok(await pairing.GenerateAsync(http.User, ct)))).RequireRateLimiting(RateLimiter);
        local.MapPost("/pair", (PairingConnectRequest request, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, true, async () => Results.Ok(await pairing.ConnectAsync(request, http.User, ct)))).RequireRateLimiting(RateLimiter);
        local.MapGet("/{pairId}/directory", (string pairId, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, false, async () => Results.Ok(await pairing.DirectoriesAsync(pairId, http.User, ct))));
        local.MapPut("/{pairId}/mappings/{id:guid}", (string pairId, Guid id, PairingMapping mapping, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, true, async () => Results.Ok(await pairing.SaveAsync(pairId, id, mapping, http.User, ct))));
        local.MapPost("/{pairId}/mappings/{id:guid}/test", (string pairId, Guid id, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, true, async () => Results.Ok(await pairing.TestAsync(pairId, id, http.User, ct))));
        local.MapDelete("/{pairId}/mappings/{id:guid}", (string pairId, Guid id, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, true, async () => { await pairing.DeleteAsync(pairId, id, http.User, ct); return Results.NoContent(); }));
        local.MapDelete("/{pairId}", (string pairId, HttpContext http, PairingService pairing, CancellationToken ct) => ExecuteAsync(http, true, async () => { await pairing.DeleteAsync(pairId, null, http.User, ct); return Results.NoContent(); }));
        return app;
    }
    private static Task<SystemPairRecord> AuthenticateAsync(HttpContext http, PairingService pairing, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Pairing ", StringComparison.Ordinal) || header.Length > 128) throw new PairingException(401, "pairing-authentication-required", "Current system pairing authentication is required.");
        return pairing.AuthenticateSetupAsync(http.Request.Headers["X-Pairing-Peer"].ToString(), header[8..], ct, http.Request.Headers["X-Pairing-Caller"].ToString());
    }
    private static async Task<IResult> ExecuteAsync(HttpContext http, bool humanMutation, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (humanMutation && http.User.HasClaim("auth_mode", "local") && http.Request.Headers["X-NetRatel-Account-Request"] != "1") return Results.Forbid();
        try { return await action(); }
        catch (PairingException error) { return await FailureAsync(http, error.StatusCode, error.Code, error.Message, humanMutation); }
        catch (HttpRequestException error)
        {
            var blocked = false; for (Exception? current = error; current != null; current = current.InnerException) if (current.Data.Contains("Pairing.NetworkPolicyRejected")) blocked = true;
            return await FailureAsync(http, 502, blocked ? "address-blocked" : "peer-unreachable", blocked ? "The address resolves to a reserved or public HTTP target." : "The peer could not be reached. Check its address, port, certificate and API service.", humanMutation);
        }
        catch (IOException) { return await FailureAsync(http, 502, "peer-response-interrupted", "The peer response was interrupted. Retry the same operation to recover its recorded result.", humanMutation); }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested) { return await FailureAsync(http, 504, "peer-timeout", "The peer did not respond within the connection deadline. Retry this same operation.", humanMutation); }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException)
        { return await FailureAsync(http, 409, "connection-storage-unavailable", "The connection could not be persisted or its protected credentials could not be read. Check the safe reference in the server logs, then retry this same connection.", humanMutation, error.GetType().Name); }
    }
    private static async Task<IResult> FailureAsync(HttpContext http, int status, string code, string message, bool notify, string? failureType = null)
    {
        var reference = Guid.NewGuid().ToString("N");
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SystemPairing").LogWarning("Pairing operation failed Code={Code} Reference={Reference} FailureType={FailureType}", code, reference, failureType);
        // Mutating interactive failures notify only their current administrator. Peer/read traffic never emits notifications.
        var actor = PairingAuthority.ActorId(http.User);
        if (notify && !string.IsNullOrEmpty(actor) && (status >= 500 || code == "connection-storage-unavailable") && http.RequestServices.GetRequiredService<PairingFailureNotificationGate>().Reserve(actor, code))
        {
            try
            {
                await using var scope = http.RequestServices.CreateAsyncScope(); var events = scope.ServiceProvider.GetService<IEventRecorder>();
                if (events is not null) await events.RecordAsync(new DomainEvent { EventType = NetRatelNotificationAudience.PairingFailure, Source = "SystemPairing", CorrelationId = reference, EntityId = actor, Severity = "Warning", Message = message, Payload = new { Code = code, Reference = reference, Link = "/account/integration-credentials" } }, http.RequestAborted);
            }
            catch (Exception) when (!http.RequestAborted.IsCancellationRequested) { }
        }
        return Results.Json(new { code, message, correlationId = reference }, statusCode: status);
    }
}

/// <summary>Bound operational failure notifications while every failed request keeps its own inline reference.</summary>
public sealed class PairingFailureNotificationGate
{
    private readonly object _sync = new();
    private readonly Dictionary<(string Actor, string Code), DateTimeOffset> _recent = new();
    public bool Reserve(string actor, string code)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _recent.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) _recent.Remove(expired);
            var key = (actor, code); if (_recent.ContainsKey(key)) return false;
            if (_recent.Count >= 1024) _recent.Remove(_recent.MinBy(x => x.Value).Key);
            _recent[key] = now.AddMinutes(5); return true;
        }
    }
}
