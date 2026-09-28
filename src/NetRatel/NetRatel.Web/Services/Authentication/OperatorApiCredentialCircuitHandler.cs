using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace NetRatel.Web.Services.Authentication;

/// <summary>Captures caller credentials into server-side state owned by one Blazor circuit.</summary>
public sealed class OperatorApiCredentialCircuitHandler : CircuitHandler, IDisposable
{
    private readonly OperatorApiCredentialState _state;
    private readonly OperatorApiCredentialProvider _credentials;
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly ILogger<OperatorApiCredentialCircuitHandler> _logger;
    private readonly object _authenticationStateGate = new();
    private readonly Dictionary<Task<AuthenticationState>, Task> _pendingAuthenticationChanges = [];
    private long _authenticationStateVersion;

    public OperatorApiCredentialCircuitHandler(
        OperatorApiCredentialState state,
        OperatorApiCredentialProvider credentials,
        AuthenticationStateProvider authenticationStateProvider,
        ILogger<OperatorApiCredentialCircuitHandler> logger)
    {
        _state = state;
        _credentials = credentials;
        _authenticationStateProvider = authenticationStateProvider;
        _logger = logger;
        _authenticationStateProvider.AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        RefreshCredentialAsync(cancellationToken);

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) =>
        RefreshCredentialAsync(cancellationToken);

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        lock (_authenticationStateGate)
        {
            _authenticationStateVersion++;
            _state.Clear();
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _authenticationStateProvider.AuthenticationStateChanged -= OnAuthenticationStateChanged;

    internal Task PendingAuthenticationChangesAsync()
    {
        lock (_authenticationStateGate)
        {
            return Task.WhenAll(_pendingAuthenticationChanges.Values.ToArray());
        }
    }

    internal Task AuthenticationStateChangeAsync(Task<AuthenticationState> authenticationStateTask)
    {
        lock (_authenticationStateGate)
        {
            return _pendingAuthenticationChanges.TryGetValue(authenticationStateTask, out var pending)
                ? pending
                : Task.CompletedTask;
        }
    }

    private async Task RefreshCredentialAsync(CancellationToken cancellationToken)
    {
        long version;
        lock (_authenticationStateGate)
        {
            version = ++_authenticationStateVersion;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var authenticationState = await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await BindAndCaptureIfCurrentAsync(authenticationState.User, version, cancellationToken).ConfigureAwait(false);
    }

    private void OnAuthenticationStateChanged(Task<AuthenticationState> authenticationStateTask)
    {
        long version;
        lock (_authenticationStateGate)
        {
            version = ++_authenticationStateVersion;
            // Fail closed immediately. An older token acquisition completing in
            // the background cannot revive credentials after sign-out or a
            // different user becoming current.
            _state.Clear();
        }

        var refreshTask = RefreshCredentialAfterAuthenticationChangeAsync(authenticationStateTask, version);
        lock (_authenticationStateGate)
        {
            _pendingAuthenticationChanges[authenticationStateTask] = refreshTask;
        }

        _ = RemoveCompletedAuthenticationChangeAsync(authenticationStateTask, refreshTask);
    }

    private async Task RefreshCredentialAfterAuthenticationChangeAsync(Task<AuthenticationState> authenticationStateTask, long version)
    {
        try
        {
            var authenticationState = await authenticationStateTask.ConfigureAwait(false);
            await BindAndCaptureIfCurrentAsync(authenticationState.User, version, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not refresh the current circuit's operator API credential after an authentication state change.");
        }
    }

    private async Task BindAndCaptureIfCurrentAsync(ClaimsPrincipal principal, long version, CancellationToken cancellationToken)
    {
        long generation;
        lock (_authenticationStateGate)
        {
            if (version != _authenticationStateVersion)
            {
                return;
            }

            generation = _state.BindPrincipal(principal);
        }

        await _credentials.CaptureForCircuitAsync(principal, generation, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveCompletedAuthenticationChangeAsync(Task<AuthenticationState> authenticationStateTask, Task refreshTask)
    {
        try
        {
            await refreshTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_authenticationStateGate)
            {
                if (_pendingAuthenticationChanges.TryGetValue(authenticationStateTask, out var current)
                    && ReferenceEquals(current, refreshTask))
                {
                    _pendingAuthenticationChanges.Remove(authenticationStateTask);
                }
            }
        }
    }
}
