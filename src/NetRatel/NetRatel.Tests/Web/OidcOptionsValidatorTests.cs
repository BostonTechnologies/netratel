using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NetRatel.Web.Configuration;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class OidcOptionsValidatorTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public void Http_Endpoints_Are_Rejected_Without_Development_Opt_Out(
        string environmentName,
        bool requireHttpsMetadata)
    {
        var options = ValidOptions();
        options.Authority = "HTTP://issuer.example.invalid";
        options.TokenEndpoint = "http://issuer.example.invalid/token";
        options.RequireHttpsMetadata = requireHttpsMetadata;

        var result = CreateValidator(environmentName).Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("Authentication:Oidc:Authority", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("Authentication:Oidc:TokenEndpoint", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_Rejects_Metadata_Opt_Out_Even_With_Https_Endpoints()
    {
        var options = ValidOptions();
        options.RequireHttpsMetadata = false;

        var result = CreateValidator(Environments.Production).Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("RequireHttpsMetadata=false is allowed only in the Development environment", result.FailureMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Development_Opt_Out_Allows_Disposable_Http_Provider()
    {
        var options = ValidOptions();
        options.Authority = "http://issuer.example.invalid";
        options.TokenEndpoint = "http://issuer.example.invalid/token";
        options.RequireHttpsMetadata = false;

        var result = CreateValidator(Environments.Development).Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Production_Allows_Https_Provider()
    {
        var result = CreateValidator(Environments.Production).Validate(null, ValidOptions());

        Assert.True(result.Succeeded);
    }

    private static OidcOptionsValidator CreateValidator(string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Oidc",
                ["OIDC_CLIENT_SECRET"] = "test-secret"
            })
            .Build();
        return new OidcOptionsValidator(configuration, new TestHostEnvironment(environmentName));
    }

    private static OidcOptions ValidOptions() => new()
    {
        Authority = "https://issuer.example.invalid",
        TokenEndpoint = "https://issuer.example.invalid/token",
        ClientId = "test-client",
        ApiScope = "netratel.api"
    };

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(OidcOptionsValidatorTests);
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
