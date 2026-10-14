using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetRatel.AgentClient;
using R3;
using Termina.Clipboard;
using Termina.Extensions;
using Termina.Hosting;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;

namespace NetRatel.Setup;

public static class HostingSetupTerminal
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.ClearProviders();
        var model = new HostingSetupViewModel();
        builder.Services.AddTermina("/hosting", t => t.RegisterRoute<HostingSetupPage, HostingSetupViewModel>("/hosting", sp => new HostingSetupPage(sp.GetRequiredService<IClipboardService>()), sp => model));
        using var host = builder.Build();
        await host.RunAsync(ct); return model.Result is not null ? 0 : 130;
    }
}
public sealed class HostingSetupViewModel : ReactiveViewModel
{
    private readonly NetRatelHttpHostingSetupService _service = new();
    private HttpHostingSetupRequest? _reviewedRequest;
    public ReactiveProperty<int> Revision { get; } = new(0);
    public ReactiveProperty<string> Error { get; } = new("");
    public string OutputDirectory { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string OidcAuthority { get; set; } = "";
    public string ApiBackend { get; set; } = "http://api:9222/";
    public string McpBackend { get; set; } = "http://mcp-http:9224/";
    public string AuthMode { get; set; } = "Local credential";
    public string Instance { get; set; } = "prod";
    public string RequiredGroups { get; set; } = "";
    public string RequiredScopes { get; set; } = "";
    public string Resource { get; set; } = "";
    public string ExistingOutbound { get; set; } = "";
    public string DelegationPrincipal { get; set; } = "";
    public string ExistingDelegationKeyFile { get; set; } = "";
    public string DelegationKeyId { get; set; } = "";
    public string DelegationIssuer { get; set; } = "";
    public string DelegationAudience { get; set; } = "";
    public string ExistingM2MCallers { get; set; } = "";
    public string AllowedOrigins { get; set; } = "";
    public string Port { get; set; } = "9224";
    public bool Advanced { get; set; }
    public bool ConfirmReplace { get; private set; }
    public HttpHostingSetupPreview? Preview { get; private set; }
    public HttpHostingSetupResult? Result { get; private set; }
    private static string[] Values(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private HttpHostingSetupRequest Request() => new(OutputDirectory, PublicOrigin, ApiBaseUrl, Instance, AuthMode == "Local credential")
    {
        OidcAuthority = string.IsNullOrWhiteSpace(OidcAuthority) ? null : OidcAuthority,
        RequiredGroups = Values(RequiredGroups), RequiredScopes = Values(RequiredScopes),
        WebApiBackendUrl = ApiBackend, WebMcpBackendUrl = McpBackend,
        PublicResourceUri = string.IsNullOrWhiteSpace(Resource) ? null : Resource,
        ExistingOutboundConfigurationPath = string.IsNullOrWhiteSpace(ExistingOutbound) ? null : ExistingOutbound,
        DelegationServicePrincipal = string.IsNullOrWhiteSpace(DelegationPrincipal) ? null : DelegationPrincipal,
        ExistingDelegationKeyFile = string.IsNullOrWhiteSpace(ExistingDelegationKeyFile) ? null : ExistingDelegationKeyFile,
        DelegationKeyId = string.IsNullOrWhiteSpace(DelegationKeyId) ? null : DelegationKeyId,
        DelegationIssuer = string.IsNullOrWhiteSpace(DelegationIssuer) ? null : DelegationIssuer,
        DelegationAudience = string.IsNullOrWhiteSpace(DelegationAudience) ? null : DelegationAudience,
        ExistingM2MCallerClientIds = string.IsNullOrWhiteSpace(ExistingM2MCallers) ? null : Values(ExistingM2MCallers), AllowedOrigins = string.IsNullOrWhiteSpace(AllowedOrigins) ? null : Values(AllowedOrigins),
        ListeningPort = int.TryParse(Port, out var port) ? port : throw new AgentClientValidationException("Listening port must be an integer."),
        ComposeOutputDirectory = Environment.GetEnvironmentVariable("NETRATEL_SETUP_HOST_OUTPUT_DIRECTORY")
    };
    public void Next(bool replace = false)
    {
        try
        {
            Error.Value = "";
            if (Result is not null) { RequestShutdown(); return; }
            if (Preview is null) { _reviewedRequest = Request(); Preview = _service.Preview(_reviewedRequest); }
            else if (Preview.ExistingConfiguration && !replace) ConfirmReplace = true;
            else { Result = _service.Apply(_reviewedRequest!, replace); ConfirmReplace = false; }
            Revision.Value++;
        }
        catch (AgentClientValidationException ex) { Error.Value = TerminalText.Safe(ex.Message); }
        catch { Error.Value = "Hosting setup could not complete. Check destination permissions and review the configuration."; }
    }
    public void Back() { ConfirmReplace = false; Preview = null; _reviewedRequest = null; Error.Value = ""; Revision.Value++; }
    public override void Dispose() { Revision.Dispose(); Error.Dispose(); base.Dispose(); }
}
public sealed class HostingSetupPage(IClipboardService clipboard) : ReactivePage<HostingSetupViewModel>
{
    private readonly List<IFocusable> _controls = [];
    private KeyboardScroll? _scroll;
    private readonly CompositeDisposable _contentSubscriptions = new();
    public override void OnNavigatedTo()
    {
        base.OnNavigatedTo();
        ViewModel.Revision.Skip(1).Subscribe(_ => InvalidateLayout()).DisposeWith(Subscriptions);
        KeyBindings.Register(ConsoleKey.F2, () => ViewModel.Next());
        KeyBindings.Register(ConsoleKey.Enter, () => { if (ViewModel.ConfirmReplace) ViewModel.Next(true); else if (Focus.CurrentFocus is SelectionListNode<string> choice) choice.HandleInput(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)); else if (Focus.CurrentFocus is CopyableTextNode copy) copy.TryCopy(); else ViewModel.Next(); });
        KeyBindings.Register(ConsoleKey.LeftArrow, ConsoleModifiers.Alt, ViewModel.Back);
        KeyBindings.Register(ConsoleKey.F6, () => { ViewModel.Advanced = !ViewModel.Advanced; ViewModel.Revision.Value++; });
        KeyBindings.Register(ConsoleKey.Tab, () => CycleFocus(false));
        KeyBindings.Register(ConsoleKey.Tab, ConsoleModifiers.Shift, () => CycleFocus(true));
        KeyBindings.Register(ConsoleKey.PageDown, () => _scroll?.Page(true));
        KeyBindings.Register(ConsoleKey.PageUp, () => _scroll?.Page(false));
        KeyBindings.Register(ConsoleKey.Home, ConsoleModifiers.Control, () => _scroll?.Home(false));
        KeyBindings.Register(ConsoleKey.End, ConsoleModifiers.Control, () => _scroll?.Home(true));
        KeyBindings.Register(ConsoleKey.Escape, () => { if (ViewModel.ConfirmReplace) ViewModel.Back(); else ViewModel.RequestShutdown(); });
        KeyBindings.Register(ConsoleKey.C, ConsoleModifiers.Control, ViewModel.RequestShutdown);
    }
    public override ILayoutNode BuildLayout()
    {
        var previousFocus = Math.Max(0, _controls.IndexOf(Focus.CurrentFocus!));
        var previousOffset = _scroll?.Offset ?? 0;
        _controls.Clear(); _contentSubscriptions.Clear();
        var form = Layouts.Vertical();
        if (ViewModel.Result is { } result)
        {
            foreach (var instruction in result.Instructions) form.WithChild(new TextNode(TerminalText.Safe(instruction)).HeightAuto());
            foreach (var path in result.Files) { var copy = new CopyableTextNode(clipboard, TerminalText.Safe(path)); _controls.Add(copy); form.WithChild(copy.Height(2)); }
        }
        else if (ViewModel.Preview is { } preview)
        {
            form.WithChild(new TextNode("Review deployment administrator configuration before applying.").Height(2));
            foreach (var change in preview.Changes) form.WithChild(new TextNode(TerminalText.Safe(change)).HeightAuto());
            foreach (var path in preview.Files) form.WithChild(new TextNode(TerminalText.Safe(path)).Height(1));
            form.WithChild(new TextNode("F2 writes persistent Web/API/MCP configuration and the isolated outbound identity. Apply it using the displayed instructions.").Height(3));
        }
        else
        {
            form.WithChild(new TextNode("Deployment administrators only · ingress auth and outbound identity are configured separately from ordinary client linking.").Height(3));
            form.WithChild(Input("Persistent output directory", ViewModel.OutputDirectory, v => ViewModel.OutputDirectory = v));
            form.WithChild(Input("Public HTTPS origin / MCP resource", ViewModel.PublicOrigin, v => ViewModel.PublicOrigin = v));
            form.WithChild(Input("Selected API HTTPS origin", ViewModel.ApiBaseUrl, v => ViewModel.ApiBaseUrl = v));
            var auth = new SelectionListNode<string>(["Local credential", "OIDC"], x => x).WithVisibleRows(2).WithHighlightedIndex(ViewModel.AuthMode == "OIDC" ? 1 : 0);
            auth.SelectionConfirmed.Subscribe(items => { ViewModel.AuthMode = items.First(); ViewModel.Revision.Value++; }).DisposeWith(_contentSubscriptions); _controls.Add(auth); form.WithChild(auth);
            if (ViewModel.AuthMode == "OIDC")
            {
                form.WithChild(Input("OIDC authority", ViewModel.OidcAuthority, v => ViewModel.OidcAuthority = v));
                form.WithChild(Input("Required groups (comma-separated)", ViewModel.RequiredGroups, v => ViewModel.RequiredGroups = v));
                form.WithChild(Input("Required scopes (comma-separated)", ViewModel.RequiredScopes, v => ViewModel.RequiredScopes = v));
            }
            if (ViewModel.Advanced)
            {
                form.WithChild(Input("Instance (dev or prod)", ViewModel.Instance, v => ViewModel.Instance = v));
                form.WithChild(Input("Canonical MCP resource (optional)", ViewModel.Resource, v => ViewModel.Resource = v));
                form.WithChild(Input("Listening port", ViewModel.Port, v => ViewModel.Port = v));
                form.WithChild(Input("Allowed browser origins (comma-separated)", ViewModel.AllowedOrigins, v => ViewModel.AllowedOrigins = v));
                form.WithChild(Input("Existing outbound protected JSON (optional)", ViewModel.ExistingOutbound, v => ViewModel.ExistingOutbound = v));
                form.WithChild(Input("Existing OIDC delegation principal (optional)", ViewModel.DelegationPrincipal, v => ViewModel.DelegationPrincipal = v));
                form.WithChild(Input("Existing delegation key protected file (optional)", ViewModel.ExistingDelegationKeyFile, v => ViewModel.ExistingDelegationKeyFile = v));
                form.WithChild(Input("Existing delegation key ID", ViewModel.DelegationKeyId, v => ViewModel.DelegationKeyId = v));
                form.WithChild(Input("Existing delegation issuer", ViewModel.DelegationIssuer, v => ViewModel.DelegationIssuer = v));
                form.WithChild(Input("Existing delegation audience", ViewModel.DelegationAudience, v => ViewModel.DelegationAudience = v));
                form.WithChild(Input("Existing API M2M caller IDs (comma-separated)", ViewModel.ExistingM2MCallers, v => ViewModel.ExistingM2MCallers = v));
                form.WithChild(Input("Web API backend URL", ViewModel.ApiBackend, v => ViewModel.ApiBackend = v));
                form.WithChild(Input("Web MCP backend URL", ViewModel.McpBackend, v => ViewModel.McpBackend = v));
            }
        }
        var scroller = new ScrollableContainerNode().WithContent(form).WithAutoScroll(AutoScrollPolicy.None);
        _scroll = new KeyboardScroll(scroller, form, 75);
        var root = Layouts.Vertical().WithChild(new TextNode("NetRatel · Configure HTTP MCP hosting").Bold().Height(1))
            .WithChild(new PanelNode().WithTitle("Hosting setup").WithContent(scroller).Fill())
            .WithChild(ViewModel.Error.Select<string, ILayoutNode>(e => new TextNode(e)).AsLayout(RenderFrameProvider).Height(2))
            .WithChild(new TextNode("Alt+Left Back  F2 Apply  Esc Cancel  Tab Focus  PgUp/Dn Scroll  F6 Advanced").NoWrap().Height(1));
        if (ViewModel.ConfirmReplace)
        {
            var modal = new ModalNode().WithTitle("Replace existing hosting configuration?").WithContent(new TextNode("Review the displayed output paths. Enter explicitly replaces existing configuration.")).WithFooter("Enter Replace · Escape Back"); _controls.Add(modal); Focus.SetFocus(modal); return new StackLayout([root, modal]);
        }
        scroller.Measure(new Size(75, 17)); _scroll.Restore(previousOffset);
        if (_controls.Count > 0) { Focus.SetFocus(_controls[Math.Min(previousFocus, _controls.Count - 1)]); _scroll.EnsureVisible(Focus.CurrentFocus); } else Focus.ClearFocus();
        return root;
    }
    private void CycleFocus(bool reverse) { Focus.CycleFocus(_controls, reverse); _scroll?.EnsureVisible(Focus.CurrentFocus); }
    private ILayoutNode Input(string label, string value, Action<string> change)
    {
        var node = new TextInputNode { Text = value, MaxLength = 1024 }; _controls.Add(node);
        node.TextChanged.Subscribe(change).DisposeWith(_contentSubscriptions);
        return Layouts.Vertical().WithChild(new TextNode(label).Height(1)).WithChild(node.Height(1)).Height(2);
    }
    public override void Dispose() { _contentSubscriptions.Dispose(); base.Dispose(); }
}
