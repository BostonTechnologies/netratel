using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using NetRatel.Web.Services;

namespace NetRatel.Web.Services.Authentication;

/// <summary>
/// Builds normal named clients over the framework's pooled, stateless handler pipelines,
/// adding caller credentials only in the current request/circuit scope.
/// </summary>
public sealed class OperatorApiHttpClientFactory(
    IHttpMessageHandlerFactory messageHandlers,
    IOptionsMonitor<HttpClientFactoryOptions> clientOptions,
    IServiceProvider scopedServices) : IHttpClientFactory
{
    private static readonly string DownloadClientName = typeof(IWebClientDownloadService).Name;

    public HttpClient CreateClient(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var pooledPipeline = messageHandlers.CreateHandler(name);
        HttpMessageHandler pipeline = new PooledMessageHandlerLease(pooledPipeline);
        if (RequiresOperatorCredential(name))
        {
            var credentials = scopedServices.GetRequiredService<OperatorApiCredentialProvider>();
            pipeline = new OperatorApiCredentialForwardingHandler(credentials)
            {
                InnerHandler = pipeline
            };
        }

        var client = new HttpClient(pipeline, disposeHandler: true);
        foreach (var configureClient in clientOptions.Get(name).HttpClientActions)
        {
            configureClient(client);
        }

        return client;
    }

    private static bool RequiresOperatorCredential(string name) =>
        name is "OrchestratorApi" or "OrchestratorApiStreaming" or NetRatel.Web.Services.Flows.FlowApiService.ClientName ||
        string.Equals(name, DownloadClientName, StringComparison.Ordinal);

    private sealed class PooledMessageHandlerLease(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override void Dispose(bool disposing)
        {
            // IHttpMessageHandlerFactory owns the pooled pipeline and its lifetime.
        }
    }
}
