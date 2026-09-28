namespace NetRatel.Web.Services.Authentication;

/// <summary>Copies the caller's circuit-owned credential onto one outgoing message.</summary>
public sealed class OperatorApiCredentialForwardingHandler(OperatorApiCredentialProvider credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var credential = await credentials.GetCurrentCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is not null)
        {
            request.Options.Set(OperatorApiCredential.RequestOptionsKey, credential);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
