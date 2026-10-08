using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Components.Shared.ServiceLinks;
using NetRatel.Web.Services.ServiceLinks;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class HelpdeskConsentStateRegressionTests : AsyncBunitContext
{
    public HelpdeskConsentStateRegressionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddScoped<HelpdeskM2MApiClient>();
        ComponentFactories.AddStub<AntiforgeryToken>();
    }

    [Fact]
    public async Task Settings_completion_cannot_unlock_pending_issuance_or_its_reveal()
    {
        using var transport = RegisterTransport();
        var busy = new List<bool>();
        var panel = Render<HelpdeskM2MSetupPanel>(parameters => parameters
            .Add(component => component.BusyChanged, (bool value) => busy.Add(value)));
        await PrepareManualAsync(panel);
        var createButton = Button(panel, "Create and reveal service client");
        var create = panel.InvokeAsync(() => createButton.Instance.OnClick.InvokeAsync(new MouseEventArgs()));
        panel.WaitForAssertion(() => Assert.Equal(1, transport.CreateCalls));
        var settings = panel.FindComponent<ServicePublicSettingsPanel>();

        try
        {
            await panel.InvokeAsync(() => settings.Instance.BusyChanged.InvokeAsync(false));
            panel.WaitForAssertion(() =>
            {
                Assert.True(busy.Last());
                Assert.True(createButton.Instance.Disabled);
                Assert.True(settings.Instance.Disabled);
            });
            await panel.InvokeAsync(() => createButton.Instance.OnClick.InvokeAsync(new MouseEventArgs()));
            await settings.InvokeAsync(() => Button(settings, "Save service settings").Instance.OnClick.InvokeAsync(new MouseEventArgs()));
            Assert.Equal(1, transport.CreateCalls);
            Assert.Equal(0, transport.SettingsWrites);
        }
        finally
        {
            transport.CompleteCreate();
            await create;
        }

        panel.WaitForAssertion(() =>
        {
            Assert.Equal("synthetic-one-time-secret", panel.FindComponent<ServiceClientSecretReveal>().Instance.Reveal.ClientSecret);
            Assert.True(settings.Instance.Disabled);
            Assert.True(createButton.Instance.Disabled);
        });
        await settings.InvokeAsync(() => Button(settings, "Save service settings").Instance.OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.Equal(0, transport.SettingsWrites);
        await panel.InvokeAsync(() => panel.FindComponent<ServiceClientSecretReveal>().Instance.OnStored.InvokeAsync());
        panel.WaitForAssertion(() => Assert.False(settings.Instance.Disabled));
    }

    [Theory]
    [InlineData("Approved RatelDesk instance ID", "different-instance")]
    [InlineData("Approved RatelDesk organization ID", "different-organization")]
    public async Task Editing_an_exact_peer_boundary_requires_new_manual_consent(string label, string value)
    {
        using var transport = RegisterTransport();
        var panel = Render<HelpdeskM2MSetupPanel>();
        await PrepareManualAsync(panel);
        var create = Button(panel, "Create and reveal service client");
        Assert.False(create.Instance.Disabled);

        await panel.InvokeAsync(() => TextField(panel, label).Instance.ValueChanged.InvokeAsync(value));

        panel.WaitForAssertion(() =>
        {
            Assert.False(ManualConsent(panel).Instance.GetState(component => component.Value));
            Assert.True(create.Instance.Disabled);
        });
        await panel.InvokeAsync(() => create.Instance.OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.Equal(0, transport.CreateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retired_guided_task_callbacks_cannot_restore_business_permissions_to_an_incident_only_draft(bool checkbox)
    {
        using var transport = RegisterTransport();
        var panel = Render<HelpdeskM2MSetupPanel>();
        panel.WaitForAssertion(() => Assert.Equal(2, panel.FindComponents<ServiceGrantSelector>().Count));
        await panel.InvokeAsync(() => GuidedTasks(panel).Instance.ValueChanged.InvokeAsync(true));
        var tasks = GuidedSelector(panel);
        var tasksInstance = tasks.Instance;
        await panel.InvokeAsync(() => tasks.Instance.Changed.InvokeAsync(TaskSelection));
        var retiredSelection = tasks.Instance.Changed;
        var retiredCheckbox = GuidedTasks(panel).Instance.ValueChanged;
        await panel.InvokeAsync(() => retiredCheckbox.InvokeAsync(false));
        var incidentOnly = GuidedSelector(panel);
        Assert.NotSame(tasksInstance, incidentOnly.Instance);
        Assert.True(incidentOnly.Instance.IncidentOnly);
        await panel.InvokeAsync(() => incidentOnly.Instance.Changed.InvokeAsync(IncidentSelection));

        await panel.InvokeAsync(() => checkbox ? retiredCheckbox.InvokeAsync(true) : retiredSelection.InvokeAsync(TaskSelection));

        panel.WaitForAssertion(() =>
        {
            Assert.False(GuidedTasks(panel).Instance.GetState(component => component.Value));
            Assert.Same(incidentOnly.Instance, GuidedSelector(panel).Instance);
            Assert.Equal(IncidentSelection.Scopes.Order(StringComparer.Ordinal), panel.FindAll("input[name='inboundScopes']")
                .Select(input => input.GetAttribute("value")!).Order(StringComparer.Ordinal));
            Assert.Empty(panel.FindAll("input[name='resourceIds']"));
            Assert.Empty(panel.FindAll("input[name='requestDefinitionIds']"));
            Assert.Equal("7", panel.Find("input[name='localTenantId']").GetAttribute("value"));
            Assert.DoesNotContain(panel.FindAll("input[name='outboundScopes']"), input => input.GetAttribute("value") == "rateldesk.orchestration.callback");
        });
        Assert.Equal(0, transport.CreateCalls);
        Assert.Equal(0, transport.CredentialWrites);
    }

    [Fact]
    public async Task Editing_the_producer_GUID_requires_new_mapping_consent()
    {
        using var transport = RegisterTransport();
        var panel = Render<ServiceLinkSourceIdentity>();
        panel.Find(".mud-expand-panel-header").Click();
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<MudTextField<string>>()));
        var producer = TextField(panel, "Persisted incident producer GUID");
        var consent = panel.FindComponent<MudCheckBox<bool>>();
        await panel.InvokeAsync(() => producer.Instance.ValueChanged.InvokeAsync("00000000-0000-0000-0000-000000000001"));
        await panel.InvokeAsync(() => consent.Instance.ValueChanged.InvokeAsync(true));
        Assert.False(Button(panel, "Approve producer mapping").Instance.Disabled);

        await panel.InvokeAsync(() => producer.Instance.ValueChanged.InvokeAsync("00000000-0000-0000-0000-000000000002"));

        panel.WaitForAssertion(() =>
        {
            Assert.False(consent.Instance.GetState(component => component.Value));
            Assert.True(Button(panel, "Approve producer mapping").Instance.Disabled);
        });
        await panel.InvokeAsync(() => Button(panel, "Approve producer mapping").Instance.OnClick.InvokeAsync(new MouseEventArgs()));
        Assert.Equal(0, transport.ProducerWrites);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authorization_denial_clears_registry_rows_and_pending_rotation(HttpStatusCode denial)
    {
        using var transport = RegisterTransport();
        var panel = Render<ServiceClientRegistryPanel>();
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<MudExpansionPanel>()));
        panel.Find(".mud-expand-panel-header").Click();
        panel.WaitForAssertion(() => Assert.Contains("Fixture manual client", panel.Markup));
        var oldRotation = Button(panel, "Rotate manual client").Instance.OnClick;
        await panel.InvokeAsync(() => oldRotation.InvokeAsync(new MouseEventArgs()));
        Assert.Contains("Confirm rotation", panel.Markup);
        transport.RegistryDenial = denial;

        await panel.InvokeAsync(() => Button(panel, "Refresh service clients").Instance.OnClick.InvokeAsync(new MouseEventArgs()));

        panel.WaitForAssertion(() =>
        {
            Assert.Empty(panel.FindAll("[data-testid='helpdesk-service-client-row']"));
            Assert.DoesNotContain("Confirm rotation", panel.Markup);
            Assert.DoesNotContain("deployment-fixture", panel.Markup);
            Assert.Contains("Current service-client management permission is required", panel.Markup);
        });
        await panel.InvokeAsync(() => oldRotation.InvokeAsync(new MouseEventArgs()));
        Assert.DoesNotContain("Confirm rotation", panel.Markup);
        Assert.Equal(0, transport.CredentialWrites);
    }

    [Fact]
    public async Task Successful_filtered_refresh_clears_pending_rotation_and_ignores_its_queued_confirmation()
    {
        using var transport = RegisterTransport();
        var panel = Render<ServiceClientRegistryPanel>();
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<MudExpansionPanel>()));
        panel.Find(".mud-expand-panel-header").Click();
        panel.WaitForAssertion(() => Assert.Contains("Fixture manual client", panel.Markup));
        var oldRotation = Button(panel, "Rotate manual client").Instance.OnClick;
        await panel.InvokeAsync(() => oldRotation.InvokeAsync(new MouseEventArgs()));
        var oldConfirmation = Button(panel, "Confirm rotation").Instance.OnClick;
        transport.EmptyRegistry = true;

        await panel.InvokeAsync(() => Button(panel, "Refresh service clients").Instance.OnClick.InvokeAsync(new MouseEventArgs()));

        panel.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Fixture manual client", panel.Markup);
            Assert.DoesNotContain("Confirm rotation", panel.Markup);
            Assert.Contains("deployment-fixture", panel.Markup);
        });
        await panel.InvokeAsync(() => oldConfirmation.InvokeAsync(new MouseEventArgs()));
        await panel.InvokeAsync(() => oldRotation.InvokeAsync(new MouseEventArgs()));
        Assert.DoesNotContain("Confirm rotation", panel.Markup);
        Assert.Equal(0, transport.CredentialWrites);
    }

    [Fact]
    public void Current_route_API_timeout_renders_unavailable_consent_without_approving()
    {
        using var transport = RegisterTransport();
        transport.AttemptTimeout = true;
        ComponentFactories.AddStub<HelpdeskConnectionsPanel>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/account/integration-credentials/link/review/old");

        var page = Render<ServiceLinkConsent>(parameters => parameters.Add(component => component.AttemptId, "old"));

        page.WaitForAssertion(() =>
        {
            Assert.Contains("The protected grant summary is unavailable", page.Markup);
            Assert.Empty(page.FindAll("[data-testid='service-link-final-approval']"));
            Assert.Empty(page.FindAll("[data-testid='service-link-consent-details']"));
        });
        Assert.Equal(0, transport.CredentialWrites);
        Assert.Equal(1, transport.AttemptReads);
    }

    [Fact]
    public async Task Current_registry_API_timeout_removes_old_confirmation_and_renders_unavailable()
    {
        using var transport = RegisterTransport();
        var panel = Render<ServiceClientRegistryPanel>();
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<MudExpansionPanel>()));
        panel.Find(".mud-expand-panel-header").Click();
        panel.WaitForAssertion(() => Assert.Contains("Fixture manual client", panel.Markup));
        await panel.InvokeAsync(() => Button(panel, "Rotate manual client").Instance.OnClick.InvokeAsync(new MouseEventArgs()));
        transport.RegistryTimeout = true;

        await panel.InvokeAsync(() => Button(panel, "Refresh service clients").Instance.OnClick.InvokeAsync(new MouseEventArgs()));

        panel.WaitForAssertion(() =>
        {
            Assert.Contains("Current service-client metadata is unavailable", panel.Markup);
            Assert.Empty(panel.FindAll("[data-testid='helpdesk-service-client-row']"));
            Assert.DoesNotContain("Confirm rotation", panel.Markup);
        });
        Assert.Equal(0, transport.CredentialWrites);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("resource")]
    [InlineData("scope")]
    [InlineData("definition")]
    [InlineData("outbound")]
    public async Task Editing_any_displayed_directional_grant_requires_new_responder_confirmation(string boundary)
    {
        using var transport = RegisterTransport();
        transport.ResponderAttempts = true;
        ComponentFactories.AddStub<HelpdeskConnectionsPanel>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/account/integration-credentials/link/respond/current");
        var page = Render<ServiceLinkConsent>(parameters => parameters.Add(component => component.AttemptId, "current"));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='service-link-responder-approval']")));
        var selector = page.FindComponent<ServiceGrantSelector>();
        await page.InvokeAsync(() => selector.Instance.Changed.InvokeAsync(ValidSelection));
        await page.InvokeAsync(() => page.Find("input[name='confirmed']").Change(true));
        page.WaitForAssertion(() => Assert.True(page.Find("input[name='confirmed']").HasAttribute("checked")));
        var attemptReads = transport.AttemptReads;
        var descriptorHash = page.Find("input[name='descriptorHash']").GetAttribute("value");

        if (boundary == "outbound")
        {
            var scopes = page.FindComponents<MudSelect<string>>().Single(select => select.Instance.Label == "NetRatel → RatelDesk permissions");
            await page.InvokeAsync(() => scopes.Instance.SelectedValuesChanged.InvokeAsync(["rateldesk.incidents.create"]));
        }
        else
        {
            var changed = boundary switch
            {
                "tenant" => new HelpdeskGrantSelection(8, ["netratel.orchestration.read"], ["resource-8"], []),
                "resource" => ValidSelection with { ResourceIds = ["resource-7b"] },
                "scope" => ValidSelection with { Scopes = ["netratel.orchestration.read", "netratel.orchestration.invoke"], RequestDefinitionIds = ["definition-7"] },
                "definition" => ValidSelection with { RequestDefinitionIds = ["definition-7"] },
                _ => throw new ArgumentOutOfRangeException(nameof(boundary))
            };
            await page.InvokeAsync(() => selector.Instance.Changed.InvokeAsync(changed));
        }

        page.WaitForAssertion(() =>
        {
            var confirmation = page.Find("input[name='confirmed']");
            Assert.False(confirmation.HasAttribute("checked"));
            Assert.Equal("true", confirmation.GetAttribute("value"));
            Assert.True(confirmation.HasAttribute("required"));
            Assert.Equal("post", page.Find("[data-testid='service-link-responder-approval']").GetAttribute("method"));
            Assert.Equal(descriptorHash, page.Find("input[name='descriptorHash']").GetAttribute("value"));
            Assert.Equal(attemptReads, transport.AttemptReads);
        });
    }

    [Fact]
    public async Task Queued_cancel_and_revoke_actions_cannot_replace_a_pending_rotation_or_its_reveal()
    {
        using var transport = RegisterTransport();
        var panel = Render<ServiceClientRegistryPanel>();
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<MudExpansionPanel>()));
        panel.Find(".mud-expand-panel-header").Click();
        var rotate = Button(panel, "Rotate manual client").Instance.OnClick;
        var revoke = Button(panel, "Revoke manual client").Instance.OnClick;
        await panel.InvokeAsync(() => rotate.InvokeAsync(new MouseEventArgs()));
        var confirm = Button(panel, "Confirm rotation").Instance.OnClick;
        var cancel = Button(panel, "Cancel").Instance.OnClick;
        var response = transport.DelayRotation();
        var rotation = panel.InvokeAsync(() => confirm.InvokeAsync(new MouseEventArgs()));
        panel.WaitForAssertion(() => Assert.Equal(1, transport.CredentialWrites));
        try
        {
            await panel.InvokeAsync(() => cancel.InvokeAsync(new MouseEventArgs()));
            await panel.InvokeAsync(() => revoke.InvokeAsync(new MouseEventArgs()));
            Assert.Contains("Confirm rotation", panel.Markup);
            Assert.DoesNotContain("Confirm revocation", panel.Markup);
        }
        finally
        {
            response.TrySetResult(RotationResponse("synthetic-rotation-once"));
            await rotation;
        }

        var revealed = panel.FindComponent<ServiceClientSecretReveal>().Instance.Reveal;
        await panel.InvokeAsync(() => revoke.InvokeAsync(new MouseEventArgs()));
        await panel.InvokeAsync(() => confirm.InvokeAsync(new MouseEventArgs()));
        Assert.Equal(1, transport.CredentialWrites);
        Assert.Same(revealed, panel.FindComponent<ServiceClientSecretReveal>().Instance.Reveal);
        Assert.DoesNotContain("Confirm revocation", panel.Markup);
    }

    [Fact]
    public async Task A_previous_secret_acknowledgment_cannot_clear_a_later_rotation_reveal()
    {
        using var transport = RegisterTransport();
        var panel = Render<ServiceClientRegistryPanel>();
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<MudExpansionPanel>()));
        panel.Find(".mud-expand-panel-header").Click();
        await RotateAsync("synthetic-first-rotation");
        var previousAcknowledgment = panel.FindComponent<ServiceClientSecretReveal>().Instance.OnStored;
        await panel.InvokeAsync(() => previousAcknowledgment.InvokeAsync());
        panel.WaitForAssertion(() => Assert.Empty(panel.FindComponents<ServiceClientSecretReveal>()));
        await RotateAsync("synthetic-second-rotation");
        var current = panel.FindComponent<ServiceClientSecretReveal>().Instance.Reveal;

        await panel.InvokeAsync(() => previousAcknowledgment.InvokeAsync());

        Assert.Same(current, panel.FindComponent<ServiceClientSecretReveal>().Instance.Reveal);
        Assert.Equal(2, transport.CredentialWrites);

        async Task RotateAsync(string secret)
        {
            await panel.InvokeAsync(() => Button(panel, "Rotate manual client").Instance.OnClick.InvokeAsync(new MouseEventArgs()));
            var response = transport.DelayRotation();
            var rotation = panel.InvokeAsync(() => Button(panel, "Confirm rotation").Instance.OnClick.InvokeAsync(new MouseEventArgs()));
            response.TrySetResult(RotationResponse(secret));
            await rotation;
            panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<ServiceClientSecretReveal>()));
        }
    }

    [Fact]
    public async Task Late_attempt_response_cannot_replace_the_new_review_or_its_form()
    {
        using var transport = RegisterTransport();
        ComponentFactories.AddStub<HelpdeskConnectionsPanel>();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/account/integration-credentials/link/review/old");
        var old = transport.DelayAttempt("old");
        var page = Render<ServiceLinkConsent>();
        var oldLoad = page.InvokeAsync(() => SetAttemptAsync(page.Instance, "old"));
        page.WaitForAssertion(() => Assert.Equal(1, transport.AttemptReads));
        navigation.NavigateTo("/account/integration-credentials/link/review/new");
        await page.InvokeAsync(() => SetAttemptAsync(page.Instance, "new"));
        page.WaitForAssertion(() => Assert.Equal("hash-new", page.Find("input[name='grantHash']").GetAttribute("value")));

        old.TrySetResult(JsonResponse(Attempt("old")));
        await oldLoad;

        page.WaitForAssertion(() =>
        {
            Assert.Equal("new", page.Find("input[name='attemptId']").GetAttribute("value"));
            Assert.Equal("hash-new", page.Find("input[name='grantHash']").GetAttribute("value"));
            Assert.DoesNotContain("peer-old", page.Markup);
        });
    }

    [Fact]
    public async Task Result_route_clears_previous_review_even_when_route_parameter_is_retained()
    {
        using var transport = RegisterTransport();
        ComponentFactories.AddStub<HelpdeskConnectionsPanel>();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/account/integration-credentials/link/review/old");
        var page = Render<ServiceLinkConsent>(parameters => parameters.Add(component => component.AttemptId, "old"));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='service-link-final-approval']")));
        var reads = transport.AttemptReads;
        navigation.NavigateTo("/account/integration-credentials/link/result?status=not-authorized");

        await page.InvokeAsync(async () =>
        {
            // Emulate the query-value supplier; Status is a cascading query parameter.
            typeof(ServiceLinkConsent).GetProperty(nameof(ServiceLinkConsent.Status))!
                .SetValue(page.Instance, "not-authorized");
            await page.Instance.SetParametersAsync(ParameterView.Empty);
        });

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("[data-testid='service-link-final-approval']"));
            Assert.Empty(page.FindAll("[data-testid='service-link-consent-details']"));
            Assert.Contains("Current integration management authority is required", page.Markup);
            Assert.DoesNotContain("peer-old", page.Markup);
        });
        Assert.Equal(reads, transport.AttemptReads);
    }

    [Fact]
    public async Task Failed_new_attempt_clears_previous_grants_and_selection()
    {
        using var transport = RegisterTransport();
        ComponentFactories.AddStub<HelpdeskConnectionsPanel>();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/account/integration-credentials/link/respond/old");
        transport.ResponderAttempts = true;
        var page = Render<ServiceLinkConsent>(parameters => parameters.Add(component => component.AttemptId, "old"));
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='service-link-responder-approval']")));
        await page.InvokeAsync(() => page.FindComponent<ServiceGrantSelector>().Instance.Changed.InvokeAsync(ValidSelection));
        var oldSelector = page.FindComponent<ServiceGrantSelector>().Instance;
        transport.FailedAttempt = "denied";
        navigation.NavigateTo("/account/integration-credentials/link/respond/denied");

        await page.InvokeAsync(() => SetAttemptAsync(page.Instance, "denied"));

        page.WaitForAssertion(() =>
        {
            Assert.Empty(page.FindAll("[data-testid='service-link-responder-approval']"));
            Assert.DoesNotContain("peer-old", page.Markup);
            Assert.Contains("protected grant summary is unavailable", page.Markup);
        });
        navigation.NavigateTo("/account/integration-credentials/link/respond/new");
        await page.InvokeAsync(() => SetAttemptAsync(page.Instance, "new"));
        page.WaitForAssertion(() =>
        {
            Assert.NotSame(oldSelector, page.FindComponent<ServiceGrantSelector>().Instance);
            Assert.True(string.IsNullOrEmpty(page.Find("input[name='localTenantId']").GetAttribute("value")));
            Assert.True(Button(page, "Approve and return to RatelDesk").Instance.Disabled);
        });
    }

    [Fact]
    public async Task Response_for_a_route_already_left_cannot_expose_an_approval_form()
    {
        using var transport = RegisterTransport();
        ComponentFactories.AddStub<HelpdeskConnectionsPanel>();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/account/integration-credentials/link/review/old");
        var pending = transport.DelayAttempt("old");
        var page = Render<ServiceLinkConsent>();
        var load = page.InvokeAsync(() => SetAttemptAsync(page.Instance, "old"));
        page.WaitForAssertion(() => Assert.Equal(1, transport.AttemptReads));
        navigation.NavigateTo("/account/integration-credentials/link/result");

        pending.TrySetResult(JsonResponse(Attempt("old")));
        await load;

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("[data-testid='service-link-final-approval']")));
    }

    private StateTransport RegisterTransport()
    {
        var transport = new StateTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        return transport;
    }

    private static readonly HelpdeskGrantSelection ValidSelection = new(7, ["netratel.orchestration.read"], ["resource-7"], []);
    private static readonly HelpdeskGrantSelection TaskSelection = new(7, ["netratel.orchestration.read", "netratel.orchestration.invoke"], ["resource-7"], ["definition-7"]);
    private static readonly HelpdeskGrantSelection IncidentSelection = new(7, [ServiceLinkContract.ControlScope, ServiceLinkContract.VerifyScope], [], []);

    private static async Task PrepareManualAsync(IRenderedComponent<HelpdeskM2MSetupPanel> panel)
    {
        panel.WaitForAssertion(() => Assert.Single(panel.FindComponents<ServicePublicSettingsPanel>()));
        foreach (var header in panel.FindAll(".mud-expand-panel-header")) header.Click();
        var manualSelector = panel.FindComponents<ServiceGrantSelector>()
            .Single(selector => selector.Instance.TestIdPrefix == "manual-helpdesk");
        await panel.InvokeAsync(() => manualSelector.Instance.Changed.InvokeAsync(ValidSelection));
        await panel.InvokeAsync(() => TextField(panel, "Service name").Instance.ValueChanged.InvokeAsync("Fixture manual client"));
        await panel.InvokeAsync(() => TextField(panel, "Approved RatelDesk instance ID").Instance.ValueChanged.InvokeAsync("peer-fixture"));
        await panel.InvokeAsync(() => TextField(panel, "Approved RatelDesk organization ID").Instance.ValueChanged.InvokeAsync("organization-fixture"));
        await panel.InvokeAsync(() => ManualConsent(panel).Instance.ValueChanged.InvokeAsync(true));
    }

    private static IRenderedComponent<MudTextField<string>> TextField<T>(IRenderedComponent<T> panel, string label) where T : class, IComponent =>
        panel.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == label);
    private static IRenderedComponent<ServiceGrantSelector> GuidedSelector(IRenderedComponent<HelpdeskM2MSetupPanel> panel) =>
        panel.FindComponents<ServiceGrantSelector>().Single(selector => selector.Instance.TestIdPrefix == "helpdesk");
    private static IRenderedComponent<MudCheckBox<bool>> GuidedTasks(IRenderedComponent<HelpdeskM2MSetupPanel> panel) =>
        panel.FindComponents<MudCheckBox<bool>>().Single(checkbox => checkbox.Instance.Label == "Also run approved tasks (optional)");
    private static IRenderedComponent<MudCheckBox<bool>> ManualConsent<T>(IRenderedComponent<T> panel) where T : class, IComponent =>
        panel.FindComponents<MudCheckBox<bool>>().Single(field => field.Instance.Label == "I approve this exact peer, tenant and resource grant");
    private static IRenderedComponent<MudButton> Button<T>(IRenderedComponent<T> panel, string label) where T : class, IComponent =>
        panel.FindComponents<MudButton>().Single(button => button.Markup.Contains(label, StringComparison.Ordinal));
    private static Task SetAttemptAsync(ServiceLinkConsent page, string attempt) => page.SetParametersAsync(ParameterView.FromDictionary(
        new Dictionary<string, object?> { [nameof(ServiceLinkConsent.AttemptId)] = attempt }));
    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpResponseMessage RotationResponse(string secret) => JsonResponse(new ServiceClientReveal(StateTransport.Client, secret,
        "https://issuer.example.test", "https://issuer.example.test/token", "api-fixture", StateTransport.Client.Scopes));

    private static ServiceLinkAdminStatus Attempt(string id, bool responder = false) => new(id, "link-" + id, 1, "approved", "7", "peer-" + id,
        "organization-fixture", "undecided", null, "hash-" + id,
        new ServiceLinkRequestDescriptor
        {
            AttemptId = id, DescriptorHash = "descriptor-" + id,
            InitiatorEndpointSnapshot = new ServiceLinkMetadata { Product = "rateldesk", InstanceId = "peer-" + id, WebBaseUrl = "https://desk.example.test" },
            ResponderEndpointSnapshot = new ServiceLinkMetadata { Product = "netratel", InstanceId = "local", WebBaseUrl = "https://web.example.test" },
            RequestedGrants = [new ServiceLinkGrant { TargetProduct = "netratel", Scopes = ["netratel.orchestration.read", "netratel.orchestration.invoke"] },
                new ServiceLinkGrant { TargetProduct = "rateldesk", Scopes = ["rateldesk.incidents.create", "rateldesk.orchestration.callback"] }]
        }, responder ? null : new ServiceLinkGrantSummary { AttemptId = id }, false, false, false, false, false, null, false, []);

    private sealed class StateTransport : HttpMessageHandler, IHttpClientFactory
    {
        private const string ClientRoot = "/api/v2/account/service-clients";
        private readonly TaskCompletionSource<HttpResponseMessage> create = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<HttpResponseMessage>? rotation;
        private readonly Dictionary<string, TaskCompletionSource<HttpResponseMessage>> attempts = new(StringComparer.Ordinal);
        public int CreateCalls { get; private set; }
        public int SettingsWrites { get; private set; }
        public int ProducerWrites { get; private set; }
        public int CredentialWrites { get; private set; }
        public int AttemptReads { get; private set; }
        public HttpStatusCode? RegistryDenial { get; set; }
        public bool EmptyRegistry { get; set; }
        public bool RegistryTimeout { get; set; }
        public bool AttemptTimeout { get; set; }
        public string? FailedAttempt { get; set; }
        public bool ResponderAttempts { get; set; }
        public static ServiceClientMetadata Client => new(Guid.Parse("00000000-0000-0000-0000-000000000007"), "Fixture manual client", "manual-fixture", 7,
            "peer-fixture", "organization-fixture", ["netratel.orchestration.read"], "{}", "active", "web", false, 1, 1,
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2027-10-01T00:00:00Z"));
        private static ServicePublicSettingsResponse Settings => new(true, "https://web.example.test", "https://api.example.test", "https://issuer.example.test",
            "api-fixture", "local", null, 1, [], true);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test") };
        public void CompleteCreate() => create.TrySetResult(JsonResponse(new ServiceClientReveal(Client, "synthetic-one-time-secret",
            "https://issuer.example.test", "https://issuer.example.test/token", "api-fixture", Client.Scopes)));
        public TaskCompletionSource<HttpResponseMessage> DelayAttempt(string id) => attempts[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> DelayRotation() => rotation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == ClientRoot) { ++CreateCalls; return create.Task; }
            if (request.Method == HttpMethod.Put && path == ClientRoot + "/settings") { ++SettingsWrites; return Task.FromResult(JsonResponse(Settings)); }
            if (request.Method == HttpMethod.Post && path.EndsWith("/rotate", StringComparison.Ordinal))
            { ++CredentialWrites; return rotation?.Task ?? Task.FromResult(RotationResponse("synthetic-immediate-rotation")); }
            if (request.Method == HttpMethod.Post && path.EndsWith("/identity/source", StringComparison.Ordinal)) ++ProducerWrites;
            else if (request.Method == HttpMethod.Post) ++CredentialWrites;
            if (path == ClientRoot && RegistryTimeout)
                return Task.FromException<HttpResponseMessage>(new OperationCanceledException("Synthetic current API budget timeout.", cancellationToken));
            if (path == ClientRoot) return Task.FromResult(RegistryDenial is { } denial
                ? new HttpResponseMessage(denial) { Content = JsonContent.Create(new { code = "not-authorized" }) }
                : JsonResponse(EmptyRegistry ? Array.Empty<ServiceClientMetadata>() : new[] { Client }));
            if (path == ClientRoot + "/deployment") return Task.FromResult(JsonResponse(new[] { new ServiceClientDeploymentMetadata("deployment-fixture", "api-fixture", []) }));
            if (path == ClientRoot + "/settings") return Task.FromResult(JsonResponse(Settings));
            if (path == ClientRoot + "/authority") return Task.FromResult(JsonResponse(new ServiceClientManagementAuthority(true,
                [new ServiceClientTenantAuthority(7, "Tenant Seven", ["netratel.orchestration.read", "netratel.orchestration.invoke"],
                    [new("resource-7", "Resource Seven"), new("resource-7b", "Resource Seven B")], [new("definition-7", "Definition Seven", "resource-7")]),
                 new ServiceClientTenantAuthority(8, "Tenant Eight", ["netratel.orchestration.read"], [new("resource-8", "Resource Eight")], [])], true)));
            if (path.EndsWith("/identity", StringComparison.Ordinal)) return Task.FromResult(JsonResponse(new ServiceLinkIdentityDto("local", null, 1)));
            if (path.Contains("/attempts/", StringComparison.Ordinal))
            {
                ++AttemptReads;
                if (AttemptTimeout)
                    return Task.FromException<HttpResponseMessage>(new OperationCanceledException("Synthetic current API budget timeout.", cancellationToken));
                var id = path[(path.LastIndexOf('/') + 1)..];
                if (attempts.TryGetValue(id, out var delayed)) return delayed.Task;
                if (id == FailedAttempt) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { code = "not-authorized" }) });
                return Task.FromResult(JsonResponse(Attempt(id, ResponderAttempts)));
            }
            return Task.FromResult(JsonResponse(Array.Empty<object>()));
        }
    }
}
