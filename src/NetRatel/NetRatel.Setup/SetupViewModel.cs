using System.Diagnostics;
using NetRatel.AgentClient;
using R3;
using Termina.Input;
using Termina.Reactive;

namespace NetRatel.Setup;

public sealed class SetupViewModel(SetupSession session) : ReactiveViewModel
{
    private CancellationTokenSource _operation = new();
    private int _generation;
    private bool _disposed;
    public SetupSession Session { get; } = session;
    public ReactiveProperty<int> Revision { get; } = new(0);
    public ReactiveProperty<string> Status { get; } = new("");
    public SetupStep Step { get; private set; } = SetupStep.Instance;
    public string Profile { get; set; } = session.ProfileName;
    public string Destination { get; private set; } = session.Path;
    private bool _destinationEdited = session.HasExplicitDestination;
    public string Origin { get; set; } = Uri.TryCreate(session.Configuration.ApiBaseUrl, UriKind.Absolute, out var configuredOrigin) ? configuredOrigin.GetLeftPart(UriPartial.Authority) : "";
    public string ManualSecret { get; set; } = "";
    public string Authentication { get; set; } = session.HasConfiguredAuthentication ? "Use existing authentication" : "Browser approval";
    public string ExternalTokenUrl { get; set; } = session.Configuration.OidcTokenUrl ?? "";
    public string ExternalClientId { get; set; } = session.Configuration.OidcClientId ?? "";
    public string ExternalUsername { get; set; } = session.Configuration.OidcUsername ?? "";
    public string ExternalPassword { get; set; } = session.Configuration.OidcAppPassword ?? "";
    public bool ExternalProviderDiscovered { get; private set; }
    public string ExternalScope { get; set; } = session.Configuration.OidcScope ?? AgentClientConfiguration.DefaultScope;
    public ConfigurationSurface Surface { get; set; } = session.Surface;
    public bool Advanced { get; private set; }
    public bool ReplaceConfirmed { get; set; }
    public bool Cancelled { get; private set; }
    public bool Busy { get; private set; }
    public bool ConfirmReplace { get; private set; }
    public bool Hosting { get; set; }
    public string HostingDirectory { get; set; } = "";
    public string HostingPublicOrigin { get; set; } = "";
    public int Width { get; private set; } = 80;
    public IReadOnlyList<SetupCheck> Checks { get; private set; } = [];
    public override void OnActivated()
    {
        Input.OfType<IInputEvent, ResizeEvent>().Subscribe(size => { Width = size.Width; Refresh(); }).DisposeWith(Subscriptions);
    }
    public void Refresh() => Revision.Value++;
    public void SetProfile(string profile) { Profile = profile; if (!_destinationEdited) { try { Destination = NetRatelConfigurationService.ProfilePath(Surface, profile.Length == 0 ? "default" : profile); } catch (AgentClientValidationException) { } } }
    public void SetDestination(string path) { Destination = path; _destinationEdited = true; }
    public void SetSurface(ConfigurationSurface surface) { Surface = surface; if (!_destinationEdited) { try { Destination = NetRatelConfigurationService.ProfilePath(surface, Profile); } catch (AgentClientValidationException) { } } }
    public void ToggleAdvanced() { Advanced = !Advanced; Refresh(); }
    public Task NextAsync() => RunAsync(async ct =>
    {
        switch (Step)
        {
            case SetupStep.Profile:
                await Session.CancelPairingAsync();
                Session.SelectProfile(Surface, Profile, Destination);
                Destination = Session.Path;
                await ChangeStepAsync(SetupStep.Instance, ct);
                break;
            case SetupStep.Instance:
                if (Session.Surface != Surface || Session.ProfileName != Profile || Session.Path != Destination)
                    Session.SelectProfile(Surface, Profile, Destination);
                await Session.DiscoverAsync(Origin, ct);
                if (Surface == ConfigurationSurface.HttpMcpClient && !Session.HasConfiguredAuthentication)
                {
                    if (Session.Capabilities is { HttpMcpAuthenticationModeKnown: false })
                        throw new AgentClientValidationException("This instance does not advertise its HTTP MCP authentication mode. Upgrade its setup metadata or select an existing managed client profile in Advanced.");
                    if (Session.Capabilities?.HttpMcpAuthenticationMode == "oidc")
                    {
                        var provider = await Session.DiscoverExternalProviderAsync(ct);
                        ExternalTokenUrl = provider.TokenEndpoint; ExternalScope = provider.Scope; ExternalProviderDiscovered = true;
                        Authentication = "External authentication";
                    }
                }
                await ChangeStepAsync(SetupStep.Authentication, ct);
                break;
            case SetupStep.Authentication:
                if (Authentication == "Existing credential")
                {
                    Session.UseCredential(ManualSecret); ManualSecret = "";
                    await ChangeStepAsync(SetupStep.Review, ct);
                }
                else if (Authentication is "External authentication" or "Use existing authentication")
                {
                    if (Authentication == "Use existing authentication" || Session.HasConfiguredAuthentication) Session.UseExternalConfiguration();
                    else Session.UseGuidedExternalConfiguration(AgentClientConfiguration.Empty with
                    {
                        ApiBaseUrl = Session.CanonicalTarget, ProfileName = Profile, Instance = Session.Configuration.Instance ?? "prod", AuthenticationMode = "Oidc",
                        OidcTokenUrl = ExternalTokenUrl, OidcClientId = ExternalClientId, OidcUsername = ExternalUsername,
                        OidcAppPassword = ExternalPassword, OidcScope = ExternalScope
                    });
                    await ChangeStepAsync(SetupStep.Review, ct);
                }
                else
                {
                    await Session.BeginPairingAsync(ct);
                    await InvokeAsync(Refresh, ct);
                    var poll = await Session.PollAsync(ct);
                    await InvokeAsync(() => { Status.Value = $"Approval {TerminalText.Safe(poll.Status)}"; Refresh(); }, ct);
                    if (poll.Status == "issued") await CompleteApprovalAsync(ct);
                    else if (poll.Status is "pending" or "slow_down")
                    {
                        while (Session.Pairing is { } pairing && DateTimeOffset.UtcNow < pairing.ExpiresAtUtc)
                        {
                            // Countdown and status updates run on Termina's render loop.
                            for (var second = 0; second < Math.Clamp(poll.IntervalSeconds, 5, 60); second++)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                                await InvokeAsync(() => { Status.Value = $"Approval {Session.PairingStatus}; {Math.Max(0, (int)(pairing.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds)} seconds remaining. F4 opens the trusted link."; }, ct);
                            }
                            poll = await Session.PollAsync(ct);
                            if (poll.Status == "issued") { await CompleteApprovalAsync(ct); break; }
                            if (poll.Status is not "pending" and not "slow_down") break;
                        }
                        await InvokeAsync(() => { Status.Value = $"Approval {TerminalText.Safe(Session.PairingStatus)}"; Refresh(); }, ct);
                    }
                }
                break;
            case SetupStep.Grants:
                await ChangeStepAsync(SetupStep.Review, ct);
                break;
            case SetupStep.Review:
                if (File.Exists(Session.Path) && !Session.ReusesExistingConfiguration && !ReplaceConfirmed)
                {
                    await InvokeAsync(() => { ConfirmReplace = true; Refresh(); }, ct);
                    return;
                }
                await Session.SaveAsync(ReplaceConfirmed, ct);
                var checks = await Session.DoctorAsync(ct);
                ct.ThrowIfCancellationRequested();
                await InvokeAsync(() => { Checks = checks; }, ct);
                await ChangeStepAsync(SetupStep.Finish, ct);
                break;
            case SetupStep.Finish:
                if (Checks.Count == 0 || Checks.Any(check => check.Status != "completed"))
                {
                    var retried = await Session.DoctorAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    await InvokeAsync(() => { Checks = retried; Refresh(); }, ct);
                }
                else await InvokeAsync(RequestShutdown, ct);
                break;
        }
    });
    private Task CompleteApprovalAsync(CancellationToken ct) => ChangeStepAsync(
        Session.Delivery?.Grants.Select(grant => grant.TenantId).Distinct().Count() > 1 ? SetupStep.Grants : SetupStep.Review, ct);
    public void EditProfile() { if (Busy || Session.Persisted) return; Step = SetupStep.Profile; Advanced = true; Refresh(); }
    private Task ChangeStepAsync(SetupStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return InvokeAsync(() => { if (step == SetupStep.Review) Session.Review(); Step = step; Status.Value = ""; Refresh(); }, ct);
    }
    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (Busy || _disposed) return;
        var generation = _generation;
        var ct = _operation.Token;
        Busy = true; Status.Value = "Working…"; Refresh();
        try { await operation(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Never display server response bodies or exceptions that may contain a secret.
            var message = ex is AgentClientValidationException ? TerminalText.Safe(ex.Message) : "The operation could not complete. Check instance availability or credential configuration and retry.";
            if (!_disposed && generation == _generation) await InvokeAsync(() => { Status.Value = message; Refresh(); });
        }
        finally
        {
            if (!_disposed && generation == _generation) await InvokeAsync(() => { Busy = false; Refresh(); });
        }
    }
    public async Task BackAsync()
    {
        if (Session.Persisted) return;
        var previousStep = Step;
        CancelOperation();
        if (previousStep is SetupStep.Authentication or SetupStep.Instance or SetupStep.Profile)
        {
            try { await Session.CancelPairingAsync(); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            Session.DiscardUnpersistedAuthentication();
            ManualSecret = "";
        }
        if (_disposed) return;
        await InvokeAsync(() =>
        {
            ConfirmReplace = false; ReplaceConfirmed = false;
            Step = previousStep switch
            {
                SetupStep.Instance or SetupStep.Profile or SetupStep.Authentication => SetupStep.Instance,
                SetupStep.Grants => SetupStep.Authentication,
                SetupStep.Review => Session.Delivery?.Grants.Select(grant => grant.TenantId).Distinct().Count() > 1 ? SetupStep.Grants : SetupStep.Authentication,
                _ => SetupStep.Review
            };
            Status.Value = ""; Refresh();
        });
    }
    public void ConfirmReplacement() { ConfirmReplace = false; ReplaceConfirmed = true; Refresh(); _ = NextAsync(); }
    public void DismissReplacement() { ConfirmReplace = false; Refresh(); }
    public async Task SetAuthenticationAsync(string mode)
    {
        if (mode == Authentication) return;
        CancelOperation();
        try { await Session.CancelPairingAsync(); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        if (!_disposed) await InvokeAsync(() => { Authentication = mode; Refresh(); });
    }
    public void SetOrigin(string value) { if (Busy) CancelOperation(); Origin = value; }
    public void OpenBrowser()
    {
        var link = Session.Pairing?.VerificationUri;
        if (link is null) { Status.Value = "Begin browser approval first; the link remains copyable."; return; }
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch { Status.Value = "Browser could not open. Copy the displayed HTTPS link and open it on another machine."; }
    }
    private void CancelOperation() { _generation++; _operation.Cancel(); _operation.Dispose(); _operation = new(); Busy = false; }
    public override void RequestShutdown() { Cancelled = true; CancelOperation(); base.RequestShutdown(); }
    public override void OnDeactivating() { CancelOperation(); base.OnDeactivating(); }
    public override void Dispose() { _disposed = true; ManualSecret = ""; ExternalPassword = ""; _operation.Cancel(); _operation.Dispose(); Revision.Dispose(); Status.Dispose(); base.Dispose(); }
}

public static class TerminalText
{
    public static string Safe(string? text) => new((text ?? "").Where(c => !char.IsControl(c) && c != '\u007f' && c is not '\u202a' and not '\u202b' and not '\u202c' and not '\u202d' and not '\u202e' and not '\u2066' and not '\u2067' and not '\u2068' and not '\u2069').Take(4096).ToArray());
}
