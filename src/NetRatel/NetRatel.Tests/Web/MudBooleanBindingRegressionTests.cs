using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class MudBooleanBindingRegressionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void Job_run_boolean_switch_uses_the_supported_value_binding_contract()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/Pages/Jobs/RunJobDialog.razor"));

        source.Should().Contain("<MudSwitch T=\"bool\"")
            .And.Contain("Value=\"@GetBoolValue(p.Name)\"")
            .And.Contain("ValueChanged=\"@((bool value) => SetBoolValue(p.Name, value))\"")
            .And.NotContain("CheckedChanged=");
    }

    [Fact]
    public void Notification_selection_uses_supported_value_bindings_and_stops_row_navigation()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/Pages/Notifications.razor"));

        source.Should().Contain("Value=\"AreAllUnreadSelected\"")
            .And.Contain("ValueChanged=\"ToggleSelectAllUnread\"")
            .And.Contain("Value=\"@selectedIds.Contains(context.Id)\"")
            .And.Contain("ValueChanged=\"@((bool value) => ToggleSelection(context.Id, value))\"")
            .And.Contain("@onclick:stopPropagation=\"true\"")
            .And.NotContain("CheckedChanged=");
    }
}
