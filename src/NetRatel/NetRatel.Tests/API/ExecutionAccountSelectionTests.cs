using NetRatel.API.Services.Secrets;
using NetRatel.Shared.Contracts.Secrets;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ExecutionAccountSelectionTests
{
    [Fact]
    public void Job_selection_uses_each_clients_default_unless_the_job_overrides_it()
    {
        var job = Guid.NewGuid();
        var client = Guid.NewGuid();
        var requested = new ExecutionIdentitySetting(ExecutionIdentityMode.WindowsPassword, job, OverrideClientDefault: false);

        Assert.Null(ExecutionAccountService.ChooseProfile(null, null));
        Assert.Equal(client, ExecutionAccountService.ChooseProfile(null, client));
        Assert.Equal(client, ExecutionAccountService.ChooseProfile(requested, client));
        Assert.Equal(job, ExecutionAccountService.ChooseProfile(requested, null));
        Assert.Equal(job, ExecutionAccountService.ChooseProfile(requested with { OverrideClientDefault = true }, client));
    }
}
