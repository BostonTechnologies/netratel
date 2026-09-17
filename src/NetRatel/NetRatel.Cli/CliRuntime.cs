using System.Net.Http.Headers;
using System.Text.Json;
using NetRatel.AgentClient;

internal sealed class CliRuntime
{
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private string? _accessToken;

    public CliRuntime(Func<HttpMessageHandler>? handlerFactory = null)
    {
        _handlerFactory = handlerFactory;
    }

    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;

    public HttpClient CreateHttpClient(Uri baseAddress)
    {
        var client = _handlerFactory is null ? new HttpClient() : new HttpClient(_handlerFactory(), disposeHandler: true);
        client.BaseAddress = baseAddress;
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    public async Task<string> GetAccessTokenAsync(ResolvedCliConfig config, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken))
        {
            return _accessToken;
        }

        try
        {
            var agentClient = new NetRatelAgentClient(new AgentClientConfiguration(
                config.ApiBaseUrl.ToString(), config.OidcTokenUrl, config.OidcClientId,
                config.OidcUsername, config.OidcAppPassword, config.OidcScope), _handlerFactory);
            return _accessToken = await agentClient.GetAccessTokenAsync(ct).ConfigureAwait(false);
        }
        catch (AgentClientRemoteException ex)
        {
            throw new CliRemoteException(ex.Code, ex.Message, ex.StatusCode, ex.ResponseBody);
        }
        catch (AgentClientValidationException ex)
        {
            throw new CliValidationException(ex.Message);
        }
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(ResolvedCliConfig config, CancellationToken ct = default)
    {
        var client = CreateHttpClient(config.ApiBaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(config, ct).ConfigureAwait(false));
        return client;
    }
}
