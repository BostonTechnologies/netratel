using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Application.Operations;
using NetRatel.Shared.Connectivity;
using NetRatel.Web.Services.OperatorPolicy;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class OperatorPolicyDialogFocusTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private readonly PolicyFixture _api = new();
    private ClientsManagementFixtureHost? _host;

    [Theory]
    [InlineData(1366, 681)]
    [InlineData(2124, 1093)]
    public async Task Inspect_restores_its_exact_later_page_trigger_after_Escape_or_Close(int width, int height)
    {
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var response = await page.GotoAsync(_host!.BaseAddress + "/admin/operator-policies", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(response);
        Assert.Equal(200, response.Status);
        var search = page.GetByRole(AriaRole.Textbox, new() { Name = "Search policies", Exact = true });
        await search.FillAsync("Reviewed grant");
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Lifecycle", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Active", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Next page", Exact = true }).ClickAsync();
        var policy = _api.Policies[13];
        var trigger = page.GetByTestId($"policy-inspect-{policy.PolicyId:D}");
        var dialog = page.GetByRole(AriaRole.Dialog);

        foreach (var escape in new[] { true, false })
        {
            // Hold the real component's detail read so its launcher is disabled
            // during dialog mounting, as in the owner-observed failure.
            var detail = _api.PauseNextDetail();
            await trigger.ClickAsync();
            await Assertions.Expect(page.GetByTestId("policy-dialog-loading")).ToBeVisibleAsync();
            await Assertions.Expect(trigger).ToBeDisabledAsync();
            detail.SetResult();
            await Assertions.Expect(page.GetByTestId("selected-policy")).ToContainTextAsync(policy.Name);
            await Assertions.Expect(page.GetByTestId("policy-dialog-close")).ToBeEnabledAsync();
            if (escape)
            {
                await page.Locator(".policy-dialog-body").FocusAsync();
                await page.Keyboard.PressAsync("Escape");
            }
            else await page.GetByTestId("policy-dialog-close").ClickAsync();

            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(trigger).ToBeFocusedAsync();
            await Assertions.Expect(search).ToHaveValueAsync("Reviewed grant");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Previous page", Exact = true })).ToBeEnabledAsync();
        }

        Assert.Equal(0, _api.Writes);
        // An accepted lifecycle change can remove the exact row from its active
        // filter. The search field is then the useful keyboard continuation.
        await trigger.ClickAsync();
        await page.GetByTestId("policy-disable").ClickAsync();
        await page.GetByRole(AriaRole.Checkbox, new() { Name = "I reviewed the exact principal, target, bounds and expiry above.", Exact = true }).CheckAsync();
        await page.GetByTestId("policy-confirm").ClickAsync();
        await Assertions.Expect(page.GetByTestId("policy-success")).ToContainTextAsync("readback and retained change audit match");
        await Assertions.Expect(trigger).ToHaveCountAsync(0);
        await page.GetByTestId("policy-dialog-close").ClickAsync();
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(search).ToBeFocusedAsync();
        Assert.Equal(1, _api.Writes);

        var create = page.GetByTestId("policy-create");
        await create.ClickAsync();
        await Assertions.Expect(page.GetByTestId("policy-editor")).ToBeVisibleAsync();
        await page.Locator(".policy-dialog-body").FocusAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(dialog).ToHaveCountAsync(0);
        await Assertions.Expect(create).ToBeFocusedAsync();
        Assert.Empty(errors);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    public async ValueTask InitializeAsync() =>
        _host = await ClientsManagementFixtureHost.StartAsync(services => services.AddSingleton<IMcpOperatorPolicyApiService>(_api));

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private sealed class PolicyFixture : IMcpOperatorPolicyApiService
    {
        public List<McpOperatorPolicy> Policies { get; } = Enumerable.Range(0, 25).Select(index => new McpOperatorPolicy(
            Guid.NewGuid(), $"Reviewed grant {index:D2}", McpOperatorEnvironment.Production, McpOperatorPolicyEffect.Allow,
            10, new(McpOperatorPrincipalSelectorKind.OAuthSubject, "separate-test-operator"),
            new(McpOperatorTargetSelectorKind.ControlPlane, 42), null, McpOperatorOperationFamily.JobExecution, null,
            new(MaxCommandDurationSeconds: 60), DateTimeOffset.UtcNow, "fixture-administrator",
            DateTimeOffset.UtcNow.AddHours(1), null, null, null, 1, "synthetic-dialog-test")).ToList();
        public int Writes { get; private set; }
        private TaskCompletionSource? _nextDetail;

        public TaskCompletionSource PauseNextDetail() => _nextDetail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PolicyAccountAuthority> GetAuthorityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolicyAccountAuthority("fixture-administrator", McpOperatorEnvironment.Production));
        public Task<IReadOnlyList<PolicyCredentialOption>> GetOwnedCredentialsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyCredentialOption>>([]);
        public Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment environment, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new McpOperatorPolicyPage(Policies.ToArray()));
        public async Task<McpOperatorPolicy> GetAsync(Guid policyId, CancellationToken cancellationToken = default)
        {
            var detail = _nextDetail;
            _nextDetail = null;
            if (detail is not null) await detail.Task.WaitAsync(cancellationToken);
            return Policies.Single(policy => policy.PolicyId == policyId);
        }
        public Task<McpOperatorTargetProfile?> GetTargetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpOperatorTargetProfile?>(null);
        public Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> GetAuditsAsync(Guid policyId, CancellationToken cancellationToken = default)
        {
            var policy = Policies.Single(item => item.PolicyId == policyId);
            return Task.FromResult<IReadOnlyList<McpOperatorPolicyChangeAudit>>([
                new(Guid.NewGuid(), policy.Version == 1 ? "policy_created" : "policy_disabled", policyId, null,
                    42, "fixture-administrator", policy.Version, DateTimeOffset.UtcNow)]);
        }
        public Task<CredentialMcpAdmission> CheckAdmissionAsync(string credentialId, ReviewedMcpAccessTarget target, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The focus fixture exposes no owned credentials.");
        public Task<PolicyMutationPreview> PreviewAsync(PreparedPolicyChange change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PolicyMutationPreview("synthetic-plan", "synthetic-idempotency", DateTimeOffset.UtcNow.AddMinutes(5),
                McpOperatorConfirmationClass.PolicyAdministration, 42, null, "synthetic-focus-correlation"));
        public Task<PolicyMutationResult> ConfirmAsync(PreparedPolicyChange change, PolicyMutationPreview preview, CancellationToken cancellationToken = default)
        {
            if (change.Action != PolicyChangeAction.Disable) throw new InvalidOperationException("Only the reviewed disable belongs to this fixture.");
            var index = Policies.FindIndex(policy => policy.PolicyId == change.PolicyId);
            Policies[index] = Policies[index] with
            {
                Version = Policies[index].Version + 1,
                LifecycleState = McpOperatorPolicyLifecycleState.Disabled,
                DisabledAtUtc = DateTimeOffset.UtcNow
            };
            Writes++;
            return Task.FromResult(new PolicyMutationResult(Policies[index], false, "synthetic-focus-correlation"));
        }
    }
}
