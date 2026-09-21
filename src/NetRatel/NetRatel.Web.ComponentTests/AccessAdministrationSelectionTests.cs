using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Web.Components.Pages.Settings;
using NetRatel.Web.Services.Access;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class AccessAdministrationSelectionTests : AsyncBunitContext
{
    [Fact]
    public void Creating_a_local_user_reveals_only_its_activation_handoff_after_success()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        var access = new DelayedAccessAdministrationApiService();
        Services.AddSingleton<IAccessAdministrationApiService>(access);

        var cut = Render<AccessAdministration>();
        cut.WaitForAssertion(() => cut.FindAll("input").Should().HaveCount(2));
        var inputs = cut.FindAll("input");
        inputs[0].Change("Scoped operator");
        inputs[1].Change("scoped@example.test");
        cut.FindAll("button").Single(button => button.TextContent.Contains("Create activation handoff", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
        {
            access.Created.Should().Be(("Scoped operator", "scoped@example.test"));
            cut.Find("[data-testid='local-user-activation']").TextContent.Should().Contain("activation-token");
        });
    }

    [Fact]
    public void Slow_previous_selection_cannot_replace_the_current_users_assignments()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        var access = new DelayedAccessAdministrationApiService();
        Services.AddSingleton<IAccessAdministrationApiService>(access);

        var cut = Render<AccessAdministration>();
        cut.WaitForAssertion(() => cut.FindAll(".mud-list-item").Should().HaveCount(2));

        var users = cut.FindAll(".mud-list-item");
        users[0].Click();
        users[1].Click();

        access.Complete("principal-b", "B assignment");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("B assignment"));

        access.Complete("principal-a", "A assignment");
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("B assignment");
            cut.Markup.Should().NotContain("A assignment");
        });
    }

    private sealed class DelayedAccessAdministrationApiService : IAccessAdministrationApiService
    {
        private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<RoleAssignmentDto>>> _assignments =
            new(StringComparer.Ordinal)
            {
                ["principal-a"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
                ["principal-b"] = new(TaskCreationOptions.RunContinuationsAsynchronously)
            };

        public (string DisplayName, string Email)? Created { get; private set; }

        public Task<IReadOnlyList<AccessRoleDto>> GetRolesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccessRoleDto>>([]);

        public Task<EffectiveAccessSummaryDto> GetSelfAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSummaryDto(null, false, []));

        public Task<IReadOnlyList<LocalUserAccessDto>> GetUsersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalUserAccessDto>>(
            [
                new("user-a", "principal-a", "a@example.test", "User A", true, false),
                new("user-b", "principal-b", "b@example.test", "User B", true, false)
            ]);

        public Task<LocalAccountActivationDto> CreateLocalUserAsync(string displayName, string email, CancellationToken cancellationToken = default)
        {
            Created = (displayName, email);
            return Task.FromResult(new LocalAccountActivationDto("created-user", email, "activation-token"));
        }

        public Task<IReadOnlyList<RoleAssignmentDto>> GetAssignmentsAsync(string principalId, CancellationToken cancellationToken = default) =>
            _assignments[principalId].Task;

        public Task AssignAsync(string principalId, string roleId, int? tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAssignmentAsync(string principalId, string assignmentId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Complete(string principalId, string roleName) =>
            _assignments[principalId].SetResult(
            [new RoleAssignmentDto($"assignment-{principalId}", principalId, "role", roleName, null, DateTimeOffset.UtcNow)]);
    }
}
