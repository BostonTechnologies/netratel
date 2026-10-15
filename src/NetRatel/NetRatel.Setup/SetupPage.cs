using NetRatel.AgentClient;
using R3;
using Termina.Clipboard;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Terminal;
using Termina.Rendering;

namespace NetRatel.Setup;

public sealed class SetupPage(IClipboardService clipboard) : ReactivePage<SetupViewModel>
{
    private readonly List<IFocusable> _controls = [];
    private KeyboardScroll? _scroll;
    private SetupStep _previousStep;
    private int _layoutGeneration;
    private readonly CompositeDisposable _contentSubscriptions = new();
    private readonly bool _monochrome = Environment.GetEnvironmentVariable("NO_COLOR") is not null || Environment.GetEnvironmentVariable("TERM") == "dumb";
    public SetupPage() : this(new TextOnlyClipboard()) { }
    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        ViewModel.Revision.Skip(1).Subscribe(_ => InvalidateLayout()).DisposeWith(Subscriptions);
        KeyBindings.Register(ConsoleKey.F2, () => _ = ViewModel.NextAsync());
        KeyBindings.Register(ConsoleKey.LeftArrow, ConsoleModifiers.Alt, () => _ = ViewModel.BackAsync());
        KeyBindings.Register(ConsoleKey.Escape, () => { if (ViewModel.ConfirmReplace) ViewModel.DismissReplacement(); else ViewModel.RequestShutdown(); });
        KeyBindings.Register(ConsoleKey.C, ConsoleModifiers.Control, ViewModel.RequestShutdown);
        KeyBindings.Register(ConsoleKey.Tab, () => CycleFocus(false));
        KeyBindings.Register(ConsoleKey.Tab, ConsoleModifiers.Shift, () => CycleFocus(true));
        KeyBindings.Register(ConsoleKey.PageDown, () => _scroll?.Page(true));
        KeyBindings.Register(ConsoleKey.PageUp, () => _scroll?.Page(false));
        KeyBindings.Register(ConsoleKey.Home, ConsoleModifiers.Control, () => _scroll?.Home(false));
        KeyBindings.Register(ConsoleKey.End, ConsoleModifiers.Control, () => _scroll?.Home(true));
        KeyBindings.Register(ConsoleKey.F4, ViewModel.OpenBrowser);
        KeyBindings.Register(ConsoleKey.F6, ViewModel.ToggleAdvanced);
        KeyBindings.Register(ConsoleKey.Enter, () => { if (ViewModel.ConfirmReplace) ViewModel.ConfirmReplacement(); else if (Focus.CurrentFocus is SelectionListNode<string> choice) choice.HandleInput(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)); else if (Focus.CurrentFocus is SelectionListNode<int?> tenant) tenant.HandleInput(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)); else if (Focus.CurrentFocus is CopyableTextNode copy) copy.TryCopy(); else _ = ViewModel.NextAsync(); });
    }
    public override ILayoutNode BuildLayout()
    {
        var layoutGeneration = ++_layoutGeneration;
        var priorFocus = _previousStep == ViewModel.Step ? Math.Max(0, _controls.IndexOf(Focus.CurrentFocus!)) : 0;
        var priorScroll = _previousStep == ViewModel.Step ? _scroll?.Offset ?? 0 : 0;
        _previousStep = ViewModel.Step;
        _contentSubscriptions.Clear(); _controls.Clear();
        var activeForm = (VerticalLayout)BuildStep(ViewModel.Step);
        var formWidth = Math.Max(20, ViewModel.Width - (ViewModel.Width >= 100 ? 24 : 5));
        var formHeight = Math.Max(1, activeForm.Measure(new Size(formWidth, int.MaxValue / 2)).Height);
        var wizard = new WizardNode<SetupStep>().WithProgressStyle(WizardProgressStyle.None);
        foreach (var step in Enum.GetValues<SetupStep>()) wizard.WithStep(step, step.ToString(), () => step == ViewModel.Step ? activeForm : BuildStep(step));
        wizard.GoToStep(ViewModel.Step);
        wizard.Height(formHeight);
        var central = new ScrollableContainerNode().WithContent(wizard).WithAutoScroll(AutoScrollPolicy.None);
        central.Fill();
        _scroll = new KeyboardScroll(central, activeForm, formWidth);
        ILayoutNode body = central;
        if (ViewModel.Width >= 100)
        {
            var stepper = new TextNode(string.Join("\n\n", Enum.GetValues<SetupStep>().Select(s => (s == ViewModel.Step ? "> " : "  ") + s))).Width(19).Fill();
            body = Layouts.Horizontal().WithChild(stepper).WithChild(central).Fill();
        }
        var content = Layouts.Vertical()
            .WithChild(new TextNode($"NetRatel  ·  {TerminalText.Safe(ViewModel.Profile)}  ·  {TerminalText.Safe(ViewModel.Origin)}").Bold().NoWrap().Height(1))
            .WithChild(new TextNode(ViewModel.Step == SetupStep.Instance ? "Connect your client" : ViewModel.Step.ToString()).Height(1))
            .WithChild(new PanelNode().WithTitle(ViewModel.Step.ToString()).WithBorder(_monochrome ? BorderStyle.Ascii : BorderStyle.Single).WithContent(body).Fill())
            .WithChild(ViewModel.Status.Select<string, ILayoutNode>(s => new TextNode(TerminalText.Safe(s)).Height(2)).AsLayout(RenderFrameProvider).Height(2))
            .WithChild(new TextNode("Enter Next  Alt+Left Back  Esc Cancel  Tab Focus  PgUp/Dn Scroll").NoWrap().Height(1));
        if (ViewModel.Busy) content.WithChild(new SpinnerNode().Height(1));
        if (ViewModel.ConfirmReplace)
        {
            var modal = new ModalNode().WithTitle("Replace existing profile?").WithBorder(_monochrome ? BorderStyle.Ascii : BorderStyle.Single).WithContent(new TextNode("Replace only the reviewed profile at " + TerminalText.Safe(ViewModel.Session.Path))).WithFooter("Enter confirms replacement · Escape keeps the existing profile");
            _controls.Add(modal); Focus.SetFocus(modal);
            return new StackLayout([content, modal]);
        }
        central.Measure(new Size(formWidth, 16));
        _scroll.Restore(priorScroll);
        if (_controls.Count > 0)
        {
            var selected = _controls[Math.Min(priorFocus, _controls.Count - 1)];
            // Wizard content is attached lazily during rendering. Dispatch focus
            // after the framework swaps the tree so reconciliation retains it.
            Post(() => { if (layoutGeneration == _layoutGeneration) { Focus.SetFocus(selected); _scroll?.EnsureVisible(selected); } });
        }
        else Focus.ClearFocus();
        return content;
    }
    private void CycleFocus(bool reverse) { Focus.CycleFocus(_controls, reverse); _scroll?.EnsureVisible(Focus.CurrentFocus); }
    private ILayoutNode BuildStep(SetupStep step)
    {
        var form = Layouts.Vertical();
        switch (step)
        {
            case SetupStep.Profile:
                form.WithChild(new TextNode("Connect a client · select a transport and named profile").Height(1));
                var transports = new SelectionListNode<string>(["CLI", "stdio MCP", "HTTP MCP client"], x => x).WithVisibleRows(3).WithHighlightedIndex(ViewModel.Surface == ConfigurationSurface.StdioMcp ? 1 : ViewModel.Surface == ConfigurationSurface.HttpMcpClient ? 2 : 0);
                transports.SelectionConfirmed.Subscribe(items => { ViewModel.SetSurface(items.First() switch { "stdio MCP" => ConfigurationSurface.StdioMcp, "HTTP MCP client" => ConfigurationSurface.HttpMcpClient, _ => ConfigurationSurface.Cli }); ViewModel.Status.Value = "Transport selected. Tab to profile; F2 continues."; }).DisposeWith(_contentSubscriptions);
                _controls.Add(transports); form.WithChild(transports);
                form.WithChild(Input("Profile", ViewModel.Profile, ViewModel.SetProfile, externallyManaged: ViewModel.Session.IsExternallyManaged("ProfileName"), source: ViewModel.Session.SourceName("ProfileName")));
                if (ViewModel.Advanced) form.WithChild(Input("Destination profile path", ViewModel.Destination, ViewModel.SetDestination));
                form.WithChild(new TextNode("Hosting administrators: setup --mode host --help configures ingress and outbound identities separately.").Height(2));
                break;
            case SetupStep.Instance:
                form.WithChild(Input("Instance Web URL", ViewModel.Origin, ViewModel.SetOrigin, externallyManaged: ViewModel.Surface != ConfigurationSurface.HttpMcpClient && ViewModel.Session.IsExternallyManaged("apiBaseUrl"), source: ViewModel.Session.SourceName("apiBaseUrl")));
                if (ViewModel.Surface == ConfigurationSurface.HttpMcpClient && ViewModel.Session.IsExternallyManaged("apiBaseUrl")) form.WithChild(new TextNode("Canonical MCP resource is read-only: " + TerminalText.Safe(ViewModel.Session.Configuration.ApiBaseUrl)).Height(2));
                form.WithChild(new TextNode("Paste the instance Web URL, with or without /mcp, then press Enter.").Height(2));
                if (ViewModel.Advanced)
                {
                    var profile = new SelectionListNode<string>(["Change profile / transport"], x => x).WithVisibleRows(1);
                    profile.SelectionConfirmed.Subscribe(_ => ViewModel.EditProfile()).DisposeWith(_contentSubscriptions);
                    _controls.Add(profile); form.WithChild(profile);
                }
                break;
            case SetupStep.Authentication:
                if (ViewModel.Session.Capabilities is null) form.WithChild(new TextNode("Pairing unavailable · instance compatibility is unconfirmed. Import only a credential you intend to bind to this selected instance.").Height(3));
                form.WithChild(new TextNode(ViewModel.Authentication).Bold().Height(1));
                if (ViewModel.Advanced)
                {
                    var modes = ViewModel.Session.HasConfiguredAuthentication
                        ? new[] { "Use existing authentication", "Browser approval", "Existing credential", "External authentication" }
                        : new[] { "Browser approval", "Existing credential", "External authentication" };
                    var auth = new SelectionListNode<string>(modes, x => x).WithVisibleRows(modes.Length).WithHighlightedIndex(Math.Max(0, Array.IndexOf(modes, ViewModel.Authentication)));
                    auth.SelectionConfirmed.Subscribe(items => { _ = ViewModel.SetAuthenticationAsync(items.First()); }).DisposeWith(_contentSubscriptions);
                    _controls.Add(auth); form.WithChild(auth);
                }
                if (ViewModel.Authentication == "Existing credential") form.WithChild(Input("Existing credential (masked; never copied)", ViewModel.ManualSecret, value => ViewModel.ManualSecret = value, password: true, externallyManaged: ViewModel.Session.IsExternallyManaged("integrationCredential"), source: ViewModel.Session.SourceName("integrationCredential")));
                if (ViewModel.Authentication == "External authentication")
                {
                    if (ViewModel.ExternalProviderDiscovered) form.WithChild(new TextNode("Existing OIDC provider: " + TerminalText.Safe(ViewModel.Session.Capabilities?.HttpMcpOidcAuthority)).Height(2));
                    if (ViewModel.ExternalProviderDiscovered && !ViewModel.Advanced) form.WithChild(new TextNode("Provider HTTPS token endpoint: " + TerminalText.Safe(ViewModel.ExternalTokenUrl)).Height(3));
                    if (!ViewModel.ExternalProviderDiscovered || ViewModel.Advanced) form.WithChild(Input("Provider HTTPS token endpoint", ViewModel.ExternalTokenUrl, value => ViewModel.ExternalTokenUrl = value, externallyManaged: ViewModel.Session.IsExternallyManaged("OidcTokenUrl"), source: ViewModel.Session.SourceName("OidcTokenUrl")));
                    form.WithChild(Input("Provider application client ID", ViewModel.ExternalClientId, value => ViewModel.ExternalClientId = value, externallyManaged: ViewModel.Session.IsExternallyManaged("OidcClientId"), source: ViewModel.Session.SourceName("OidcClientId")));
                    form.WithChild(Input("Application account username", ViewModel.ExternalUsername, value => ViewModel.ExternalUsername = value, externallyManaged: ViewModel.Session.IsExternallyManaged("OidcUsername"), source: ViewModel.Session.SourceName("OidcUsername")));
                    form.WithChild(Input("Application password", ViewModel.ExternalPassword, value => ViewModel.ExternalPassword = value, password: true, externallyManaged: ViewModel.Session.IsExternallyManaged("OidcAppPassword"), source: ViewModel.Session.SourceName("OidcAppPassword")));
                    form.WithChild(Input("Provider scopes", ViewModel.ExternalScope, value => ViewModel.ExternalScope = value, externallyManaged: ViewModel.Session.IsExternallyManaged("OidcScope"), source: ViewModel.Session.SourceName("OidcScope")));
                }
                if (ViewModel.Session.Pairing is { } pairing)
                {
                    var link = new CopyableTextNode(clipboard, TerminalText.Safe(pairing.VerificationUri)).WithHint("Enter copies link; F4 explicitly opens browser");
                    _controls.Add(link); form.WithChild(link.Height(2));
                    var code = new CopyableTextNode(clipboard, TerminalText.Safe(pairing.UserCode)).WithHint("Enter copies user code");
                    _controls.Add(code); form.WithChild(code.Height(2));
                    form.WithChild(new TextNode("Target: " + TerminalText.Safe(ViewModel.Session.CanonicalTarget)).Height(2));
                    form.WithChild(new TextNode("Tenant and permission approval happens in your signed-in browser.").Height(2));
                }
                else form.WithChild(new TextNode(ViewModel.Authentication == "External authentication" ? "Next validates the selected external provider configuration. Existing managed values retain their source." : ViewModel.Authentication == "Use existing authentication" ? "Next reuses the selected protected configuration and verifies its actual connection." : "Next begins browser approval. Open the displayed link here or on another device.").Height(3));
                break;
            case SetupStep.Grants:
                var delivery = ViewModel.Session.Delivery;
                form.WithChild(new TextNode("Approved grants: " + string.Join(", ", delivery?.Grants.Select(g => $"{TerminalText.Safe(g.TenantName ?? $"Tenant {g.TenantId}")}: {TerminalText.Safe(g.PermissionLabel ?? g.Permission)}") ?? [])).Height(3));
                form.WithChild(new TextNode("Instance permissions: " + string.Join(", ", delivery?.InstancePermissions.Select(TerminalText.Safe) ?? [])).Height(2));
                if (ViewModel.Session.IsExternallyManaged("DefaultTenantId"))
                {
                    form.WithChild(new TextNode("Default tenant: " + TerminalText.Safe(ViewModel.Session.Configuration.DefaultTenantId) + " [read-only: " + TerminalText.Safe(ViewModel.Session.SourceName("DefaultTenantId")) + "]").Height(2));
                    break;
                }
                var tenants = new int?[] { null }.Concat(delivery?.Grants.Select(g => (int?)g.TenantId).Distinct() ?? []).ToArray();
                var selectedTenant = Array.FindIndex(tenants, tenantId => tenantId?.ToString(System.Globalization.CultureInfo.InvariantCulture) == ViewModel.Session.Configuration.DefaultTenantId);
                var tenant = new SelectionListNode<int?>(tenants, t => t is null ? "No default tenant" : TerminalText.Safe(delivery?.Grants.FirstOrDefault(grant => grant.TenantId == t)?.TenantName ?? $"Tenant {t}")).WithVisibleRows(5).WithHighlightedIndex(Math.Max(0, selectedTenant));
                tenant.SelectionConfirmed.Subscribe(items => ViewModel.Session.SelectTenant(items.First())).DisposeWith(_contentSubscriptions);
                _controls.Add(tenant); form.WithChild(tenant);
                break;
            case SetupStep.Review:
                form.WithChild(new TextNode($"Profile: {TerminalText.Safe(ViewModel.Profile)}\nInstance: {TerminalText.Safe(ViewModel.Origin)}\nTransport: {ViewModel.Surface}\nTarget: {TerminalText.Safe(ViewModel.Session.CanonicalTarget)}\nDestination: {TerminalText.Safe(ViewModel.Session.Path)}\nSecret storage: separate owner-only secret file\nDefault tenant: {ViewModel.Session.Configuration.DefaultTenantId ?? "none"}").Height(8));
                form.WithChild(new TextNode(ViewModel.Session.ReusesExistingConfiguration ? "Next verifies the selected existing configuration. Its managed source and protected files are reused without rewriting credentials." : "Next saves atomically. Actual profile replacements require confirmation; managed values retain their source.").Height(3));
                foreach (var source in ViewModel.Session.Sources.Where(s => s.Value.ExternallyManaged)) form.WithChild(new TextNode(TerminalText.Safe($"{source.Key}: read-only ({source.Value.Source})")).Height(1));
                break;
            case SetupStep.Finish:
                foreach (var check in ViewModel.Checks) form.WithChild(new TextNode($"{check.Name}: {check.Status} · {check.Detail}").Height(2));
                var snippet = new CopyableTextNode(clipboard, ViewModel.Session.LaunchSnippet()).WithHint("Enter copies launch snippet (contains only a secret reference)");
                _controls.Add(snippet); form.WithChild(snippet.Height(5));
                var manage = new CopyableTextNode(clipboard, ViewModel.Session.CredentialManagementUrl).WithHint("Credential management and revocation");
                _controls.Add(manage); form.WithChild(manage.Height(2));
                break;
        }
        if (step == ViewModel.Step)
        {
            var choices = step == SetupStep.Authentication && ViewModel.Session.Pairing is not null
                ? new[] { "Next / retry", "Open browser", "Advanced", "Back", "Cancel" }
                : step == SetupStep.Instance ? new[] { "Next", "Advanced", "Cancel" } : new[] { step == SetupStep.Finish ? ViewModel.Checks.Count > 0 && ViewModel.Checks.All(check => check.Status == "completed") ? "Finish" : "Retry verification" : "Next", "Advanced", "Back", "Cancel" };
            var actions = new SelectionListNode<string>(choices, x => "[ " + x + " ]").WithVisibleRows(choices.Length);
            actions.SelectionConfirmed.Subscribe(items =>
            {
                var choice = items.First();
                if (choice == "Advanced") ViewModel.ToggleAdvanced();
                else if (choice == "Open browser") ViewModel.OpenBrowser();
                else if (choice == "Back") _ = ViewModel.BackAsync();
                else if (choice == "Cancel") ViewModel.RequestShutdown();
                else _ = ViewModel.NextAsync();
            }).DisposeWith(_contentSubscriptions);
            _controls.Add(actions); form.WithChild(actions);
        }
        return form;
    }
    private ILayoutNode Input(string label, string value, Action<string> changed, bool password = false, bool externallyManaged = false, string? source = null)
    {
        if (externallyManaged) return new TextNode(label + ": " + (password ? "••••••" : TerminalText.Safe(value)) + " [read-only: " + TerminalText.Safe(source ?? "managed source") + "]").Height(2);
        var input = new TextInputNode { Text = value, IsPassword = password, MaxLength = password ? 4096 : 1024 };
        input.TextChanged.Subscribe(changed).DisposeWith(_contentSubscriptions);
        input.Submitted.Subscribe(text => { _ = ViewModel.NextAsync(); }).DisposeWith(_contentSubscriptions);
        _controls.Add(input);
        return Layouts.Vertical().WithChild(new TextNode(label).Height(1)).WithChild(input.Height(1)).Height(2);
    }
    public override void Dispose() { _layoutGeneration++; _contentSubscriptions.Dispose(); base.Dispose(); }
    private sealed class TextOnlyClipboard : IClipboardService { public bool Copy(string text) => false; }
}
