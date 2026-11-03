using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Bootstrap;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using Xunit;

public sealed class BootstrapHostDependencyValidationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Unconfigured_host_validates_without_operational_database_services(string environment)
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-validation", Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = new BootstrapValidationFactory(root, environment);
            using var client = factory.CreateClient();

            Assert.Equal(environment, factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            var status = await client.GetFromJsonAsync<BootstrapStatus>("/api/v2/setup/status");
            Assert.NotNull(status);
            Assert.Equal(BootstrapState.Unconfigured, status.State);
            Assert.True(status.SetupRequired);
            Assert.Null(factory.Services.GetService<OrchestratorDbContext>());
            Assert.Null(factory.Services.GetService<SoftwareInventoryService>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class BootstrapValidationFactory(string root, string environment) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // MVC.Testing supplies these as early entry-point arguments, before Program
            // reconciles bootstrap state. Keep this host independent of ambient ready-host settings.
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [HostDefaults.EnvironmentKey] = environment,
                    ["Bootstrap:StateDirectory"] = Path.Combine(root, "bootstrap"),
                    ["DataProtection:KeysDirectory"] = Path.Combine(root, "keys"),
                    ["Database:Provider"] = "PostgreSql",
                    ["ConnectionStrings:NetRatelDb"] = string.Empty,
                    ["ConnectionStrings:Default"] = string.Empty
                }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateOnBuild = true;
                options.ValidateScopes = true;
            });
        }
    }
}
