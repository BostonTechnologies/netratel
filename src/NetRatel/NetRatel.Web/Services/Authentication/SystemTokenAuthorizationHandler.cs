using System.Net.Http.Headers;

namespace NetRatel.Web.Services.Authentication;

public class SystemTokenAuthorizationHandler : DelegatingHandler
{
    private readonly ISystemTokenService _tokens;

    public SystemTokenAuthorizationHandler(ISystemTokenService tokens)
    {
        _tokens = tokens;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _tokens.GetTokenAsync();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
