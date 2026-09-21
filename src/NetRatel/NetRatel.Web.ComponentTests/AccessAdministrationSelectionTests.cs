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

        public Task<IReadOnlyList<RoleAssignmentDto>> GetAssignmentsAsync(string principalId, CancellationToken cancellationToken = default) =>
            _assignments[principalId].Task;

        public Task AssignAsync(string principalId, string roleId, int? tenantId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAssignmentAsync(string principalId, string assignmentId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Complete(string principalId, string roleName) =>
            _assignments[principalId].SetResult(
            [new RoleAssignmentDto($"assignment-{principalId}", principalId, "role", roleName, null, DateTimeOffset.UtcNow)]);
    }
}
